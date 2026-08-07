#!/usr/bin/env bash
#
# Builds a .deb for Debian and Ubuntu, x86_64.
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
ARCH="amd64"
STAGE="$REPO_ROOT/packaging/deb/ravensport_${VERSION}_${ARCH}"
OUT="$REPO_ROOT/packaging/ravensport_${VERSION}_${ARCH}.deb"

echo "==> RavensPort ${VERSION} (${ARCH})"

# --- the 1Password native ------------------------------------------------------------------------
# c-shared needs cgo, and cgo needs a C compiler. Said plainly here because the failure otherwise
# arrives as a linker error from inside the Go toolchain.
echo "==> Building libonepassword.so"
( cd "$REPO_ROOT/src/OnePasswordNative" && CGO_ENABLED=1 go build -buildmode=c-shared -o libonepassword.so main.go )

# --- the app -------------------------------------------------------------------------------------
# Loose files rather than single-file: a .deb is already an archive, and unpacking to /opt means the
# app does not extract itself to /tmp on every cold start.
echo "==> Publishing linux-x64"
dotnet publish "$REPO_ROOT/src/RavensPort.App/RavensPort.App.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -o "$STAGE/opt/ravensport"

rm -rf "$STAGE/DEBIAN" "$STAGE/usr"
mkdir -p "$STAGE/DEBIAN" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
         "$STAGE/usr/share/icons/hicolor/256x256/apps"

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
EOF

# A wrapper rather than a symlink: the apphost resolves its runtime relative to its own directory,
# and a symlink on PATH would resolve to /usr/bin and find nothing there.
cat > "$STAGE/usr/bin/ravensport" <<'EOF'
#!/bin/sh
exec /opt/ravensport/RavensPort "$@"
EOF
chmod 0755 "$STAGE/usr/bin/ravensport"

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

chmod 0755 "$STAGE/opt/ravensport/RavensPort"

# --- build ---------------------------------------------------------------------------------------
# root:root ownership, because the files land under /opt and /usr. Without --root-owner-group they
# would carry the building user's uid, which lintian flags and which is wrong on the target machine.
echo "==> Packing"
dpkg-deb --build --root-owner-group "$STAGE" "$OUT" >/dev/null

echo "==> $OUT"
ls -lh "$OUT" | awk '{print "    " $5}'
