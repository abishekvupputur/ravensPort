#!/usr/bin/env bash
#
# Builds the Flatpak from this checkout, runs Flathub's linter over it, and writes an installable
# bundle, so a build can be tried on a desktop before the manifest goes to Flathub:
#
#   flatpak install --user packaging/flatpak/ravensport.flatpak
#
# Generates the three source lists first (generate-sources.sh), which are also what a Flathub
# submission carries beside the manifest.
#
# Needs Linux with flatpak, go, python3 (with aiohttp, tomlkit and PyYAML) and curl. On Debian or
# Ubuntu, including under WSL:
#
#   sudo apt-get install flatpak golang python3-aiohttp python3-tomlkit python3-yaml curl
#
# Usage: packaging/flatpak/build.sh [--skip-sources]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$REPO_ROOT/packaging/flatpak"
APP_ID=io.github.abishekvupputur.RavensPort
MANIFEST="$HERE/$APP_ID.yml"
# Outside the checkout: the manifest copies the whole checkout in as the app's source, and would
# copy its own build directories with it.
OUT="${FLATPAK_BUILD_DIR:-$HOME/.cache/ravensport-flatpak}"
VERSION="$(grep -oP "(?<=^runtime-version: ')[^']+" "$MANIFEST")"

echo "==> Flathub remote, freedesktop $VERSION SDK, .NET extension and org.flatpak.Builder"
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user -y --noninteractive flathub org.flatpak.Builder \
    "org.freedesktop.Sdk//$VERSION" "org.freedesktop.Sdk.Extension.dotnet10//$VERSION"

if [ "${1:-}" != "--skip-sources" ]; then
    "$HERE/generate-sources.sh"
fi

# The manifest names a release tag, which is what Flathub builds. Here the working tree is built
# instead, from a copy of the manifest beside the original so its relative paths still resolve.
#
# Not the checkout itself but what git would commit from it: tracked files plus new ones that
# are not ignored, so uncommitted changes are built and bin/ and obj/ are not. A Windows build's
# obj/ holds assets files naming C:\Users\...\.nuget\packages, and publish trusts those over the
# restore that ran here.
LOCAL="$HERE/.local-build.yml"
trap 'status=$?; rm -f "$LOCAL"; exit $status' EXIT
mkdir -p "$OUT"
rm -rf "$OUT/source"
mkdir "$OUT/source"
( cd "$REPO_ROOT" && git ls-files -z --cached --others --exclude-standard \
    | xargs -0 cp --parents -t "$OUT/source" 2>/dev/null || true )
test -f "$OUT/source/src/RavensPort.App/RavensPort.App.csproj" \
    || { echo "error: could not copy the working tree to $OUT/source" >&2; exit 1; }

python3 - "$MANIFEST" "$LOCAL" "$OUT/source" <<'EOF'
import sys, yaml
source, target, tree = sys.argv[1:]
manifest = yaml.safe_load(open(source))
module = next(m for m in manifest["modules"] if isinstance(m, dict) and m["name"] == "ravensport")
module["sources"][0] = {"type": "dir", "path": tree}
yaml.safe_dump(manifest, open(target, "w"), sort_keys=False)
EOF

cd "$OUT"

# The screenshot mirror is what Flathub's own build uses, and what the repo lint expects.
# rofiles-fuse guards the build cache against a build writing into it; WSL refuses its FUSE
# mount, and the result is the same without it.
echo "==> Building into $OUT"
flatpak run org.flatpak.Builder --user --install-deps-from=flathub --force-clean \
    --disable-rofiles-fuse \
    --repo=repo --default-branch=test \
    --mirror-screenshots-url=https://dl.flathub.org/media/ --compose-url-policy=full \
    build-dir "$LOCAL"

# A build that finishes is not one that works: the pieces the app needs at run time have to be
# where it looks for them, and pass-cli has to start.
echo "==> Checking the contents"
for path in bin/ravensport bin/pass-cli \
            lib/ravensport/RavensPort lib/ravensport/libonepassword.so \
            share/licenses/$APP_ID/pass-cli/LICENSE \
            share/applications/$APP_ID.desktop \
            share/metainfo/$APP_ID.metainfo.xml \
            share/icons/hicolor/512x512/apps/$APP_ID.png; do
    test -e "build-dir/files/$path" || { echo "missing from the Flatpak: $path" >&2; exit 1; }
done
# Installed from the repo just built and run as a user would run it: on the Platform runtime
# rather than the SDK, inside the sandbox the manifest's permissions describe. (flatpak-builder
# --run would use the SDK, and needs the FUSE mount WSL refuses.) This is also the install to try
# the app from: `flatpak run io.github.abishekvupputur.RavensPort`.
flatpak remote-add --user --no-gpg-verify --if-not-exists ravensport-local "file://$OUT/repo"
flatpak install --user -y --noninteractive --reinstall ravensport-local "$APP_ID//test"
flatpak run --command=pass-cli "$APP_ID//test" --version

# libsecret and the X11 libraries come from the runtime and are loaded with dlopen, which ldd
# cannot see. A missing one shows up here rather than at first launch.
flatpak run --command=sh "$APP_ID//test" -c '
    for lib in libsecret-1.so.0 libX11.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1; do
        ls /usr/lib/*/"$lib" /usr/lib/"$lib" 2>/dev/null | grep -q . \
            || { echo "not in the runtime: $lib" >&2; exit 1; }
    done'

# The screenshots and remote icon the build fetched for the mirror go into the repo on a branch
# of their own, as Flathub's build does; the repo lint looks for them there.
flatpak run --command=ostree org.flatpak.Builder commit --repo=repo --canonical-permissions --branch="screenshots/$(uname -m)" \
    build-dir/files/share/app-info/media >/dev/null

echo "==> Flathub linter"
flatpak run --command=flatpak-builder-lint org.flatpak.Builder manifest "$MANIFEST"
flatpak run --command=flatpak-builder-lint org.flatpak.Builder repo repo

echo "==> Bundling"
flatpak build-bundle repo "$HERE/ravensport.flatpak" "$APP_ID" test \
    --runtime-repo=https://dl.flathub.org/repo/flathub.flatpakrepo
echo "==> $HERE/ravensport.flatpak"
