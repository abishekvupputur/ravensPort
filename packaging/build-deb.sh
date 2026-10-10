#!/usr/bin/env bash
#
# Builds a .deb for Debian and Ubuntu, for the architecture of the machine it runs on: amd64, or
# arm64 (64-bit Raspberry Pi OS among them). Native rather than cross-compiled, because the
# 1Password native below needs cgo, and cgo for another architecture needs a cross C toolchain.
#
# Self-contained, like the Windows installer: the .NET runtime travels inside the package. That is
# the same reasoning as win-x64-selfcontained.pubxml — a user installing a proxy should not also be
# installing a framework — and it is why Depends below lists only the system libraries Avalonia and
# libsecret actually dlopen, not anything from .NET.
#
# The 1Password native is built here rather than assumed: it is the same Go source as the Windows
# DLL, and without it the package installs an app whose 1Password backend fails at the first call.
#
# Usage: packaging/build-deb.sh [version]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:-$(grep -oP '(?<=<Version>)[^<]+' "$REPO_ROOT/Directory.Build.props" | head -1)}"
ARCH="$(dpkg --print-architecture)"
case "$ARCH" in
    amd64) RID="linux-x64" ;;
    arm64) RID="linux-arm64" ;;
    *) echo "error: no self-contained .NET runtime for $ARCH; build on amd64 or arm64." >&2; exit 1 ;;
esac
STAGE="$REPO_ROOT/packaging/deb/ravensport_${VERSION}_${ARCH}"
OUT="$REPO_ROOT/packaging/ravensport_${VERSION}_${ARCH}.deb"

echo "==> RavensPort ${VERSION} (${ARCH})"

# --- keep the lock files as they were ------------------------------------------------------------
# A restore with a runtime identifier (the publish below) writes a linux-x64 or linux-arm64 section into every
# packages.lock.json it touches, and CI's locked-mode restore then rejects the files with NU1004:
# the projects have no runtime identifier, the lock files do. Building a package must not dirty the
# tree, so they are put back on the way out — including when the build fails.
# (RestorePackagesWithLockFile=false is not an option: NuGet refuses it while a lock file exists.)
LOCK_BACKUP="$(mktemp -d)"
while IFS= read -r -d '' lock; do
    mkdir -p "$LOCK_BACKUP/$(dirname "$lock")"
    cp -p "$lock" "$LOCK_BACKUP/$lock"
done < <(cd "$REPO_ROOT" && find src tests tools -name packages.lock.json -print0)
restore_locks() {
    ( cd "$LOCK_BACKUP" && find . -name packages.lock.json -print0 | while IFS= read -r -d '' lock; do
        cp -p "$lock" "$REPO_ROOT/$lock"
    done )
    rm -rf "$LOCK_BACKUP"
}
trap restore_locks EXIT

# --- the 1Password native ------------------------------------------------------------------------
# c-shared needs cgo, and cgo needs a C compiler. Said plainly here because the failure otherwise
# arrives as a linker error from inside the Go toolchain.
#
# SKIP_ONEPASSWORD=1 builds without it, for a machine with no Go toolchain. The package then has no
# 1Password backend — the app reports it unavailable and Proton Pass still works — so the result
# is for trying the app, not for releasing.
if [ "${SKIP_ONEPASSWORD:-}" = "1" ]; then
    echo "==> WARNING: SKIP_ONEPASSWORD=1 — building WITHOUT libonepassword.so; 1Password will be unavailable" >&2
    rm -f "$REPO_ROOT/src/OnePasswordNative/libonepassword.so"
else
    command -v go >/dev/null || { echo "error: 'go' not found. Install Go and a C compiler, or set SKIP_ONEPASSWORD=1 to build without 1Password." >&2; exit 1; }
    echo "==> Building libonepassword.so"
    ( cd "$REPO_ROOT/src/OnePasswordNative" && CGO_ENABLED=1 go build -buildmode=c-shared -o libonepassword.so main.go )
fi

# --- the app -------------------------------------------------------------------------------------
# Loose files rather than single-file: a .deb is already an archive, and unpacking to /opt means the
# app does not extract itself to /tmp on every cold start.
#
# The framework is named explicitly. The project multi-targets, and publish refuses to guess
# (NETSDK1129) — the same reason the Windows workflows pass -p:TargetFramework beside their profile.
echo "==> Publishing $RID"
dotnet publish "$REPO_ROOT/src/RavensPort.App/RavensPort.App.csproj" \
    -f net10.0 -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$STAGE/opt/ravensport"

# The headless CLI, into the same directory. Both are self-contained against the same runtime, so
# the files they share are identical and the second publish only adds ravensport-cli's own.
echo "==> Publishing ravensport-cli $RID"
dotnet publish "$REPO_ROOT/src/RavensPort.Cli/RavensPort.Cli.csproj" \
    -f net10.0 -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$STAGE/opt/ravensport"

rm -rf "$STAGE/DEBIAN" "$STAGE/usr"
mkdir -p "$STAGE/DEBIAN" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
         "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/usr/share/doc/ravensport/examples"

INSTALLED_KB=$(du -sk "$STAGE/opt" | cut -f1)

# --- metadata ------------------------------------------------------------------------------------
# Depends is deliberately short. Everything .NET needs is inside the package; what is listed is what
# Avalonia's X11 backend and libsecret load at run time and cannot bring with them.
cat > "$STAGE/DEBIAN/control" <<EOF
Package: ravensport
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: ${ARCH}
Depends: libx11-6, libice6, libsm6, libfontconfig1, libsecret-1-0
Recommends: gnome-keyring
Installed-Size: ${INSTALLED_KB}
Maintainer: RavensPort
Description: Local OAuth2 reverse proxy and MCP funnel
 Gives each AI agent its own MCP endpoint, pooling the servers you choose and
 exposing only the tools you allow, with OAuth2 handled for you.
 .
 Credentials, tokens, routes and funnels live in a vault in 1Password or Proton
 Pass. On Linux the Proton Pass session key is kept in the system keyring, which
 encrypts it at rest but is unlocked for the whole login session — weaker than
 the Windows build, which binds it to a Windows Hello gesture.
 .
 Includes ravensport-cli, which runs the proxy headless on a server with no
 display and manages a running RavensPort from the command line.
EOF

# A wrapper rather than a symlink: the apphost resolves its runtime relative to its own directory,
# and a symlink on PATH would resolve to /usr/bin and find nothing there.
cat > "$STAGE/usr/bin/ravensport" <<'EOF'
#!/bin/sh
exec /opt/ravensport/RavensPort "$@"
EOF
chmod 0755 "$STAGE/usr/bin/ravensport"

cat > "$STAGE/usr/bin/ravensport-cli" <<'EOF'
#!/bin/sh
exec /opt/ravensport/ravensport-cli "$@"
EOF
chmod 0755 "$STAGE/usr/bin/ravensport-cli"

# Examples for running headless under systemd. Shipped, never enabled: each needs a service user
# and an encrypted token that only the administrator can provide.
cp "$REPO_ROOT"/packaging/systemd/*.service "$STAGE/usr/share/doc/ravensport/examples/"
chmod 0644 "$STAGE"/usr/share/doc/ravensport/examples/*.service

cat > "$STAGE/usr/share/applications/ravensport.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=RavensPort
Comment=Local OAuth2 reverse proxy and MCP funnel
Exec=/opt/ravensport/RavensPort
Icon=ravensport
Terminal=false
Categories=Utility;Network;
StartupWMClass=RavensPort
EOF

cp "$REPO_ROOT/src/RavensPort.App/Assets/logo.png" \
   "$STAGE/usr/share/icons/hicolor/256x256/apps/ravensport.png"

chmod 0755 "$STAGE/opt/ravensport/RavensPort" "$STAGE/opt/ravensport/ravensport-cli"

# --- build ---------------------------------------------------------------------------------------
# root:root ownership, because the files land under /opt and /usr. Without --root-owner-group they
# would carry the building user's uid, which lintian flags and which is wrong on the target machine.
echo "==> Packing"
dpkg-deb --build --root-owner-group "$STAGE" "$OUT" >/dev/null

echo "==> $OUT"
ls -lh "$OUT" | awk '{print "    " $5}'
