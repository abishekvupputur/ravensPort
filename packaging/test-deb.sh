#!/usr/bin/env bash
#
# Installs the built .deb, proves the installed copy works, and removes it again: the Linux
# counterpart of installer/test-install.ps1. A GitHub runner is ephemeral, so it is already the
# clean machine this wants.
#
# "Works" is checked two ways, because neither covers the other:
#
#   The installed app is launched for real, under Xvfb, and has to map a window and still be running
#   after 15 seconds. That is the whole binary -- apphost, bundled runtime, native libraries, the
#   desktop session it starts in -- and nothing else exercises that combination.
#
#   The single-use UI suite is run against the installed assemblies. The suite drives the views
#   headlessly from "Start in single use", in-process, so it cannot press buttons in the window
#   above; instead the RavensPort assemblies it was built against are swapped for the ones the
#   package put in /opt/ravensport and it runs again with --no-build. Every view it drives is then
#   the code that ships, not the code that happened to be in bin/.
#
# Needs the UI suite already built: `dotnet test` (or build) of RavensPort.UI.Tests with
# -f net10.0 -c Release, which pr.yml and deb.yml both do before packaging.
#
# Usage: packaging/test-deb.sh <path-to-.deb> <version>
set -euo pipefail

[ $# -eq 2 ] || { echo "usage: $0 <path-to-.deb> <version>" >&2; exit 2; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEB="$(realpath "$1")"
VERSION="$2"
UI_TESTS_BIN="$REPO_ROOT/tests/RavensPort.UI.Tests/bin/Release/net10.0"
# The screenshot and the app's own output, kept for a workflow to upload whether or not this passed.
ARTIFACTS="${TEST_DEB_ARTIFACTS:-$REPO_ROOT/packaging/test-deb}"

ok() { echo "  ok: $*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

[ -f "$DEB" ] || fail "package not found: $DEB"
[ -f "$UI_TESTS_BIN/RavensPort.UI.Tests.dll" ] || fail "the UI suite is not built at $UI_TESTS_BIN"
mkdir -p "$ARTIFACTS"

# A leftover install would make every assertion below meaningless -- the files would be there
# whether or not this package put them there.
if dpkg-query -W ravensport >/dev/null 2>&1; then
    fail "ravensport is already installed on this machine. This test needs a clean one."
fi

XVFB_PID=""
cleanup() {
    pkill -f /opt/ravensport/RavensPort 2>/dev/null || true
    [ -n "$XVFB_PID" ] && kill "$XVFB_PID" 2>/dev/null || true
}
trap cleanup EXIT

# --- install ------------------------------------------------------------------------------------
# apt rather than dpkg -i, as a user's `apt install ./file.deb` is: Depends are resolved from the
# archive, so a Depends naming a package that does not exist fails here rather than on their machine.
echo "==> Installing $DEB"
sudo apt-get install -y --no-install-recommends "$DEB"

installed="$(dpkg-query -W -f='${Version}' ravensport)"
[ "$installed" = "$VERSION" ] || fail "dpkg reports version '$installed', expected '$VERSION'"
ok "dpkg reports ravensport $VERSION"

for path in /opt/ravensport/RavensPort \
            /opt/ravensport/libonepassword.so \
            /usr/bin/ravensport \
            /usr/share/applications/ravensport.desktop \
            /usr/share/icons/hicolor/256x256/apps/ravensport.png; do
    [ -e "$path" ] || fail "$path was not installed"
    ok "$path"
done
[ "$(command -v ravensport)" = /usr/bin/ravensport ] || fail "ravensport is not the one on PATH"
ok "ravensport on PATH is /usr/bin/ravensport"

# --- launch -------------------------------------------------------------------------------------
# Through the PATH wrapper, as the desktop entry and a terminal both reach it. dbus-run-session
# because a desktop session has a session bus and the runner does not: the keyring probe talks to
# it, and an app that falls over without one is a CI artefact, not a finding.
echo "==> Launching the installed app"
sudo apt-get install -y --no-install-recommends xvfb xdotool imagemagick dbus >/dev/null
export DISPLAY=:99
Xvfb "$DISPLAY" -screen 0 1280x800x24 -nolisten tcp &
XVFB_PID=$!
sleep 2

dbus-run-session -- ravensport > "$ARTIFACTS/app-output.log" 2>&1 &
started=$SECONDS

timeout 30 xdotool search --sync --onlyvisible --name RavensPort >/dev/null \
    || fail "no RavensPort window appeared within 30 seconds (see $ARTIFACTS/app-output.log)"
ok "a RavensPort window is mapped"

sleep $(( 15 - (SECONDS - started) > 0 ? 15 - (SECONDS - started) : 0 ))
pgrep -f /opt/ravensport/RavensPort >/dev/null \
    || fail "RavensPort exited on its own within 15 seconds of launch (see $ARTIFACTS/app-output.log)"
ok "still running after 15s"

import -window root "$ARTIFACTS/installed-app.png"
ok "screenshot saved to $ARTIFACTS/installed-app.png"

pkill -f /opt/ravensport/RavensPort
for _ in $(seq 1 20); do pgrep -f /opt/ravensport/RavensPort >/dev/null || break; sleep 0.5; done
kill "$XVFB_PID" 2>/dev/null || true
XVFB_PID=""

# --- the single-use UI suite, on the installed assemblies ---------------------------------------
# The pdbs go too: a pdb that does not match its dll is ignored, but deleting it says plainly which
# build these are.
echo "==> Single-use UI suite against /opt/ravensport"
for assembly in RavensPort RavensPort.UI RavensPort.Core; do
    cp /opt/ravensport/$assembly.dll "$UI_TESTS_BIN/$assembly.dll"
    rm -f "$UI_TESTS_BIN/$assembly.pdb"
    ok "$assembly.dll taken from the package"
done

DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test "$REPO_ROOT/tests/RavensPort.UI.Tests/RavensPort.UI.Tests.csproj" \
    -f net10.0 -c Release --no-build --verbosity normal

# --- uninstall ----------------------------------------------------------------------------------
# purge rather than remove, so a conffile left behind would count as a failure to clean up.
echo "==> Uninstalling"
sudo apt-get purge -y ravensport

if dpkg-query -W ravensport >/dev/null 2>&1 && [ "$(dpkg-query -W -f='${db:Status-Status}' ravensport)" != "not-installed" ]; then
    fail "dpkg still lists ravensport after purge"
fi
ok "dpkg no longer lists ravensport"
for path in /opt/ravensport /usr/bin/ravensport /usr/share/applications/ravensport.desktop; do
    [ ! -e "$path" ] || fail "$path is still there after purge"
    ok "$path was removed"
done

echo
echo "Install, launch, single-use UI suite and uninstall all behaved."
