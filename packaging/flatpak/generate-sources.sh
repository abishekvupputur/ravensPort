#!/usr/bin/env bash
#
# Writes the three source lists the Flatpak manifest builds from offline, next to the manifest:
#
#   nuget-sources.json           every NuGet package the App restores, for x86_64 and aarch64
#   go-sources.json              the 1Password native's Go modules, unpacked as a vendor directory
#   pass-cli-cargo-sources.json  the Proton Pass CLI's crates, from crates.io, Proton's own registry
#                                and git
#
# Re-run it whenever a NuGet package, a Go module or the pass-cli version changes, and copy the
# results into the Flathub repository beside the manifest. They are not committed here: each is a
# few thousand lines that only the Flathub build reads.
#
# Needs Linux with flatpak (the .NET restore runs inside the SDK), go, python3, curl, and the pip
# packages aiohttp, tomlkit and PyYAML. build.sh runs it first and lists the apt packages.
#
# Usage: packaging/flatpak/generate-sources.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$REPO_ROOT/packaging/flatpak"
MANIFEST="$HERE/io.github.abishekvupputur.RavensPort.yml"
WORK="$(mktemp -d)"

# Pinned, because these generate what the build trusts.
TOOLS_COMMIT=74697c75b630d7330e77250fc13cb5ea688d9479   # flatpak/flatpak-builder-tools
GO_MOD_VERSION=v0.1.0                                   # github.com/dennwc/flatpak-go-mod

FREEDESKTOP="$(grep -oP "(?<=^runtime-version: ')[^']+" "$MANIFEST")"

# --- keep the lock files as they were ------------------------------------------------------------
# A restore with a runtime identifier writes a RID section into every packages.lock.json, which CI's
# locked-mode restore then rejects (NU1004). Put back on the way out, as build-deb.sh does.
while IFS= read -r -d '' lock; do
    mkdir -p "$WORK/locks/$(dirname "$lock")"
    cp -p "$lock" "$WORK/locks/$lock"
done < <(cd "$REPO_ROOT" && find src tests tools -name packages.lock.json -print0)
cleanup() {
    local status=$?
    ( cd "$WORK/locks" && find . -name packages.lock.json -print0 | while IFS= read -r -d '' lock; do
        cp -p "$lock" "$REPO_ROOT/$lock"
    done )
    rm -rf "$WORK"
    exit $status
}
trap cleanup EXIT

echo "==> flatpak-builder-tools @ ${TOOLS_COMMIT:0:12}"
for tool in dotnet/flatpak-dotnet-generator.py cargo/flatpak-cargo-generator.py; do
    curl -fsSL -o "$WORK/$(basename "$tool")" \
        "https://raw.githubusercontent.com/flatpak/flatpak-builder-tools/$TOOLS_COMMIT/$tool"
done

# --- NuGet ---------------------------------------------------------------------------------------
# One runtime per run: the generator restores its runtimes concurrently into the same obj/, which
# races. The two lists are then merged, since most packages are the same for both architectures.
# The generator ignores a failed restore, so a short list is checked for below rather than trusted.
for rid in linux-x64 linux-arm64; do
    echo "==> NuGet ($rid)"
    python3 "$WORK/flatpak-dotnet-generator.py" "$WORK/nuget-$rid.json" \
        "$REPO_ROOT/src/RavensPort.App/RavensPort.App.csproj" \
        --runtime "$rid" --freedesktop "$FREEDESKTOP" --dotnet 10 \
        --dotnet-args -p:TargetFramework=net10.0 -p:SelfContained=true \
                      -p:PublishTrimmed=false -p:PublishSingleFile=false
done

# A package only one architecture's restore wanted (the runtime packs and apphost, mostly) is
# marked only-arches, so each architecture's build downloads its own and not the other's.
python3 - "$WORK/nuget-linux-x64.json" "$WORK/nuget-linux-arm64.json" "$HERE/nuget-sources.json" <<'EOF'
import json, sys
x64, arm64, output = sys.argv[1:]
lists = {"x86_64": json.load(open(x64)), "aarch64": json.load(open(arm64))}
wanted_by = {}
merged = {}
for arch, sources in lists.items():
    for source in sources:
        merged[source["dest-filename"]] = source
        wanted_by.setdefault(source["dest-filename"], set()).add(arch)
for name, arches in wanted_by.items():
    if len(arches) == 1:
        merged[name]["only-arches"] = sorted(arches)
for runtime in ("linux-x64", "linux-arm64"):
    if not any(f"runtime.{runtime}" in name.lower() for name in merged):
        sys.exit(f"no {runtime} runtime pack in the restore; it probably failed (see above)")
json.dump(sorted(merged.values(), key=lambda s: s["dest-filename"]), open(output, "w"), indent=4)
print(f"    {len(merged)} packages")
EOF

# --- Go ------------------------------------------------------------------------------------------
# flatpak-go-mod lays the modules out as a vendor/ directory, plus the modules.txt that `go build
# -mod=vendor` checks against go.mod. That file is folded into the list as an inline source, so
# the Flathub repository carries one file for Go rather than two.
#
# It runs on a copy of the module in $WORK: the tool moves modules.txt out of the vendor/ it makes
# with a plain rename, which fails across filesystems (a checkout under /mnt/c in WSL, $WORK in
# /tmp), and the copy also keeps that vendor/ out of the checkout.
echo "==> Go modules"
GOBIN="$WORK/bin" go install "github.com/dennwc/flatpak-go-mod@$GO_MOD_VERSION"
mkdir "$WORK/gomod"
cp "$REPO_ROOT"/src/OnePasswordNative/{go.mod,go.sum,*.go} "$WORK/gomod/"
"$WORK/bin/flatpak-go-mod" -json -out "$WORK" -dest-pref "src/OnePasswordNative/" "$WORK/gomod"

python3 - "$WORK/go.mod.json" "$WORK/modules.txt" "$HERE/go-sources.json" <<'EOF'
import json, sys
sources_path, modules_txt, output = sys.argv[1:]
sources = []
for source in json.load(open(sources_path)):
    if source.get("type") == "file" and source.get("path", "").endswith("modules.txt"):
        source = {"type": "inline", "contents": open(modules_txt).read(),
                  "dest": source["dest"], "dest-filename": "modules.txt"}
    sources.append(source)
json.dump(sources, open(output, "w"), indent=4)
print(f"    {len(sources) - 1} modules")
EOF

# --- Cargo (the Proton Pass CLI) -----------------------------------------------------------------
# The generator only knows crates.io and git. pass-cli also pulls a dozen crates from Proton's own
# sparse registry, which the generator would look for on crates.io and not find, so those entries
# are pointed at Proton's download URL, and the cargo config it writes gets a source replacement
# for that registry as well.
PASS_CLI_URL="$(grep -oP '(?<=url: )https://github.com/protonpass/pass-cli/archive/\S+' "$MANIFEST")"
PASS_CLI_SHA="$(grep -A1 -F "$PASS_CLI_URL" "$MANIFEST" | grep -oP '(?<=sha256: )\w+')"
echo "==> pass-cli crates ($(basename "$PASS_CLI_URL"))"
curl -fsSL -o "$WORK/pass-cli.tar.gz" "$PASS_CLI_URL"
echo "$PASS_CLI_SHA  $WORK/pass-cli.tar.gz" | sha256sum -c --quiet
mkdir "$WORK/pass-cli"
tar -xzf "$WORK/pass-cli.tar.gz" -C "$WORK/pass-cli" --strip-components=1
python3 "$WORK/flatpak-cargo-generator.py" "$WORK/pass-cli/Cargo.lock" -o "$WORK/cargo.json"

python3 - "$WORK/pass-cli/Cargo.lock" "$WORK/cargo.json" "$HERE/pass-cli-cargo-sources.json" <<'EOF'
import json, sys, tomllib
lock_path, sources_path, output = sys.argv[1:]
REGISTRY = "sparse+https://rust-registry.proton.me/index/"
DOWNLOAD = "https://rust-registry.proton.me/downloads/{name}@{version}.crate"

proton = {(p["name"], p["version"]) for p in tomllib.load(open(lock_path, "rb"))["package"]
          if p.get("source") == REGISTRY}
sources = json.load(open(sources_path))
rewritten = 0
for source in sources:
    url = source.get("url", "")
    if url.startswith("https://static.crates.io/crates/"):
        name, crate = url.split("/")[-2:]
        version = crate[len(name) + 1:-len(".crate")]
        if (name, version) in proton:
            source["url"] = DOWNLOAD.format(name=name, version=version)
            rewritten += 1
    if source.get("dest-filename") == "config" and source.get("type") == "inline":
        source["contents"] += (
            '\n[source.proton-registry]\n'
            f'registry = "{REGISTRY}"\n'
            'replace-with = "vendored-sources"\n')
if rewritten != len(proton):
    sys.exit(f"pointed {rewritten} crates at Proton's registry, expected {len(proton)}")
json.dump(sources, open(output, "w"), indent=4)
print(f"    {sum(1 for s in sources if s.get('type') == 'archive')} crates and git sources, "
      f"{rewritten} from Proton's registry")
EOF

echo "==> $HERE/{nuget-sources,go-sources,pass-cli-cargo-sources}.json"
