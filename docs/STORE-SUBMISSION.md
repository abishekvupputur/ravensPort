# Microsoft Store submission (EXE/MSI app type)

What to put in Partner Center, and why the shape of the submission changed.

## What failed, and why it was one cause

The 08/03/2026 review (Product ID `1fab64a5-4d87-4cff-9df1-db83143fef01`) returned three findings.
All three came from submitting `RavensPort-<tag>.exe` — the **application** — where the Store
expected an **installer**.

| Policy | Finding | Cause |
|---|---|---|
| 10.3.4 | "failed to install through the Store" | The Store runs the submitted file with the declared silent switches and then looks for the product on the machine. Nothing installed, so nothing was found. |
| 10.2.7 | No Add or Remove Programs entry | Nothing wrote an uninstall key, because nothing installed. |
| 10.1.2.10 | "no accessible method of being launched" | No Start menu shortcut, and the app started hidden in the tray. After tray → Exit there was no way back short of finding the exe on disk. |

Fixed by `installer/RavensPort.iss`, which produces a real per-user installer, and by two app
changes: the main window is now shown on launch, and a second launch brings the running instance's
window to the front instead of showing a "look in the tray" message box.

## Submit this file

`RavensPort-Setup-<version>.exe`, built by the release workflow and attached to the GitHub release.
**Not** `RavensPort-<tag>.exe` — that is still published for people who want to run the app without
installing it, and it is still not an installer.

The Store requires a redirect-free download URL. GitHub *release* asset URLs redirect to
`objects.githubusercontent.com`, so the installer has to be committed as a blob in this repository
and served from `raw.githubusercontent.com`, exactly as `dist/RavensPort-v3.0.2.exe` was before:

```
https://raw.githubusercontent.com/abishekvupputur/ravensPort/main/dist/RavensPort-Setup-<version>.exe
```

Commit the new installer to `dist/` and delete the superseded one in the same commit. `dist/` now
holds `RavensPort-Setup-4.1.4.exe`, which is what the current submission points at; the bare
`RavensPort-v3.0.2.exe` it replaced is gone. Whatever is submitted must be the version the listing
claims, so a bump in `Directory.Build.props` means a new blob before the next submission.

Prefer a commit-pinned URL over `/main/`. The bytes at a SHA can never change, which is the
property a store listing wants:

```
https://raw.githubusercontent.com/abishekvupputur/ravensPort/<commit-sha>/dist/RavensPort-Setup-<version>.exe
```

## Partner Center answers

| Field | Value |
|---|---|
| Installer type | EXE |
| Silent install | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` |
| Silent uninstall | `"%LOCALAPPDATA%\Programs\RavensPort\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` |
| Successful install exit code | `0` |
| Requires elevation | No |
| Install scope | Per-user |
| Install location | `%LOCALAPPDATA%\Programs\RavensPort` |
| ARP display name | `RavensPort` |
| ARP registry key | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C47DF74F-150F-4AD3-9B12-46A8BF02BE9C}_is1` |
| Minimum OS | Windows 10 1809 (10.0.17763), x64 |

`PrivilegesRequired=lowest` is deliberate. A per-user install never raises UAC, and a silent
install that cannot raise UAC is a silent install that cannot fail on it. Nothing in RavensPort
needs machine scope: the configuration lives in the user's password manager and the session key is
already bound to the Windows account.

## What a reviewer will now see

1. Install runs unattended and returns 0.
2. **RavensPort** appears in Settings → Apps → Installed apps, with a working Uninstall.
3. A **RavensPort** shortcut appears in the Start menu.
4. Launching shows the app window — not just a tray icon.
5. Tray → Exit quits. Launching from the Start menu again brings it straight back.
6. Launching while it is already running brings the existing window forward.

## Why the payload is published uncompressed

The release workflow publishes with `EnableCompressionInSingleFile=false`, giving a 243 MB payload
that the installer compresses down to **67.9 MB**. That looks backwards until you measure it.

Single-file's own compression is what you want for an exe that is downloaded and run directly — it
was 99 MB that way, and that is what the release asset used to be, before the installer became the
only asset. The installer wants the opposite, because it compresses its payload itself and LZMA2 has
far more to work with when the input has not already been deflated. Measured, all three
combinations:

| Payload | Installer compression | Result |
|---|---|---|
| Compressed (99 MB) | `none` | **101.6 MB** — over GitHub's limit, unpushable |
| Compressed (99 MB) | `lzma2/max` | 94.0 MB — under, but only 6 MB of headroom |
| Raw (243 MB) | `lzma2/max` | **67.9 MB** — 32 MB of headroom |

The first was the original bug: the payload was already deflated, so `Compression=none` made the
installer the payload plus overhead, and the `dist/` blob could not be committed at all. The second
works but leaves almost no room for the app to grow. The third is what is configured, and it drops
the runtime self-extraction step as well.

`build.ps1` fails the build if a release installer exceeds 100 MB, so this is caught at build time
rather than at `git push`, when the release has already been cut.

## Architecture

`ArchitecturesAllowed=x64compatible`, not `x64`. The latter is deprecated and now resolves to
`x64os` — x64 hardware only — which **refuses to install on an ARM64 Windows device**, even though
the payload runs there fine under emulation. Recent Surface Laptops are ARM64, and a Surface Laptop
is what certification tested on, so this alone could have reproduced 10.3.4.

The script falls back to `x64` when compiled by Inno older than 6.3, where `x64compatible` is a
hard error. That narrows the audience rather than breaking the build, so check the CI log if the
installer ever stops working on ARM64.

## Re-verifying before resubmission

Verified locally against Inno Setup 6.7.3: compiles clean, exit code 0, 67.9 MB output. To repeat:

```powershell
dotnet publish src/RavensPort.App/RavensPort.App.csproj -p:PublishProfile=win-x64-selfcontained -p:TargetFramework=net8.0-windows10.0.19041.0 `
  -c Release -p:EnableCompressionInSingleFile=false `
  -p:PublishDir="bin\Release\net8.0-windows\publish\win-x64-raw\"

& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\RavensPort.iss /DAppVersion=4.1.5 `
  /DSourceExe=..\src\RavensPort.App\bin\Release\net8.0-windows\publish\win-x64-raw\RavensPort.exe
```

Compilation takes about 80 seconds — LZMA2 on a 243 MB payload, not a hang.

Then walk points 1–6 above on a machine that has never had RavensPort installed, **including an
ARM64 one if you can get hold of it**. Point 1 in particular cannot be checked by running the
installer interactively — use the silent switches.

## Not addressed here

The listing still has no screenshot meeting Partner Center's 1366x768 minimum; see the Screenshots
section of [STORE-LISTING.md](STORE-LISTING.md). That was not among the three findings, but it will
block the listing separately.
