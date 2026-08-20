# The Store MSIX package

How the Microsoft Store package is built and where it is published. The EXE/MSI submission is
documented separately in [STORE-SUBMISSION.md](STORE-SUBMISSION.md); nothing about the installer,
`release.yml`, or the `dist/` blob changes because of any of this.

Live listing: **<https://apps.microsoft.com/detail/9PBNQH53L61D>**

## Why there is an MSIX at all

The EXE submission was rejected under policy 10.2.9:

> The binary and all of its Portable Executable (PE) files must be digitally signed with a code sign
> certificate that chains up to a certificate issued by a Certificate Authority (CA) that is part of
> the Microsoft Trusted Root Program.

That covers `RavensPort-Setup-<version>.exe` **and every PE inside it** - `RavensPort.exe` and the
bundled `onepassword.dll` - so signing the outer installer alone would not have satisfied it. The
three ways to comply:

| Route | Cost | What it means here |
|---|---|---|
| Azure Trusted Signing | ~$10/month plus identity validation | Keeps the EXE submission exactly as it is |
| OV certificate from a CA | ~$200-400/year, key on a hardware token or cloud HSM | Same, with more moving parts in CI |
| **Ship MSIX instead** | **free** | **Microsoft signs the package at ingestion** |

MSIX is what is implemented, and it is the route Microsoft's own rejection notice offers.

## The package is deliberately unsigned

Do not sign the one you upload. Partner Center accepts an unsigned `.msix` and signs it during
ingestion with a certificate chaining to the Microsoft Trusted Root Program - which is the entire
point. A package signed locally is *rejected*, because the certificate subject would not match the
publisher Partner Center has on file.

Windows, on the other hand, will not *install* an unsigned MSIX, so trying the package out on your
own machine needs a signed one. That is a second file, not a different build: see
[Two packages: one to upload, one to test](#two-packages-one-to-upload-one-to-test).

## One-time setup

Two things, in this order. The second is effectively irreversible. No tokens or secrets are
involved: the workflow publishes into this repository with the built-in `GITHUB_TOKEN`.

### 1. Partner Center

MSIX and EXE/MSI are different app types and cannot share a reserved name.

1. Delete the app name from the existing Win32 (EXE/MSI) app. Microsoft's notice calls this out
   explicitly: the name cannot be reserved twice.
2. Create a new app of the MSIX type and reserve `RavensPort` for it.

### 2. Package identity

Open **Product management -> "View app identity details"** in Partner Center and copy three values
into [../packaging/AppxManifest.xml](../packaging/AppxManifest.xml), replacing the `FILLMEIN` ones.
The values are case-sensitive, and spaces and punctuation must match too:

| Partner Center | Manifest |
|---|---|
| `Package/Identity/Name` | `<Identity Name="...">` |
| `Package/Identity/Publisher` | `<Identity Publisher="CN=...">` |
| `Package/Properties/PublisherDisplayName` | `<PublisherDisplayName>` |

All three must match exactly or the upload is rejected, and **the identity cannot be changed after
the first accepted submission**. `build-msix.ps1` checks the parsed values, not the file text, and
refuses to pack while any placeholder survives.

## Cutting a release

There is no separate tag and no separate workflow. The Store package is built by the ordinary
release pipeline, from the same tag as everything else:

```
git tag v4.4.0
git push origin v4.4.0
```

`release.yml` runs the test suite first and stops on failure, then publishes twice — once raw for
the installer, once loose-file for the package — builds both, attests both, and attaches both to
the release:

| Asset | For |
|---|---|
| `RavensPort-Setup-4.4.0.exe` | installing outside the Store |
| `RavensPort-4.4.0.msix` | uploading to Partner Center |

Download the `.msix` from the release and upload it in Partner Center.

The version comes off the tag, so `v4.4.0` produces a `4.4.0.0` package. MSIX wants four parts and
the Store reserves the fourth, so `build-msix.ps1` pads it rather than letting you set it.

Because both artifacts come off one tag, a Store resubmission means cutting a release — there is no
way to rebuild only the package. That is the trade for having one pipeline and one version number
that cannot drift.

### Provenance

Both assets are attested, so either can be checked against the build that produced it:

```bash
gh attestation verify RavensPort-4.4.0.msix --repo abishekvupputur/ravensPort
```

## Building locally

```powershell
dotnet publish src/RavensPort.App/RavensPort.App.csproj `
  -p:PublishProfile=win-x64-msix -p:StoreBuild=true -c Release

./packaging/build-msix.ps1 -Version 4.4.0 -Sign `
  -PublishDir 'src/RavensPort.App/bin/Release/net10.0-windows/publish/win-x64-msix'
```

`-p:StoreBuild=true` is not optional and cannot be moved into the publish profile — see
[the `StoreBuild` flag](#it-must-be-a-command-line-property) below. `build-msix.ps1` throws if the
payload was built without it.

### Two packages: one to upload, one to test

`-Sign` is what makes the local build testable, and it produces a *second* file rather than
changing the first:

| File | For | Signature |
|---|---|---|
| `RavensPort-<version>.msix` | Partner Center | none — Microsoft signs it at ingestion |
| `RavensPort-<version>-signed.msix` | installing on your machine | self-signed test certificate |
| `RavensPort-<version>-signed.cer` | trusting that certificate once | — |

Windows refuses to install an unsigned MSIX at all, so the upload copy cannot be the one you try.
Signing the upload copy instead is not an option either: Partner Center rejects a package carrying
a signature it did not apply. Hence two files, from one pack — the signed one is the same payload
with a signature appended, so what you test is what you upload.

Windows also checks the signing certificate's **subject against the manifest's `Publisher`** and
refuses the install if they differ by a character. `build-msix.ps1` reads the subject back out of
the manifest it just wrote and mints (or reuses) a self-signed code-signing certificate with
exactly that subject in `Cert:\CurrentUser\My`, valid a year. Pass
`-SigningCertificateThumbprint` to use one of your own; it is checked against the same rule.

Installing, from an **elevated** PowerShell:

```powershell
Import-Certificate -FilePath packaging/obj/RavensPort-4.4.0-signed.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path packaging/obj/RavensPort-4.4.0-signed.msix
```

The certificate import is once per machine. Uninstall with
`Get-AppxPackage *RavensPort* | Remove-AppxPackage`.

Two things to know before installing it. The test package carries the **same package identity as
the Store one**, so it occupies the slot a Store install would use — uninstall it before installing
from the Store. And an MSIX install redirects `%APPDATA%`/`%LOCALAPPDATA%` into the package's own
`LocalCache`, so it starts with fresh local state; see *What running under MSIX changes* below.

CI never passes `-Sign`. The release workflow wants exactly one artifact, unsigned, and
`build-msix.ps1` returns that path alone — the signed copy is reported on the console only.

Measured against Windows SDK 10.0.22621: a 249 MB layout of 644 files packs to a **99.3 MB** package
in about a minute. Output goes to `packaging/obj/`, which the existing blanket `obj/` ignore rule
already covers, so a local build never leaves an untracked blob in a tracked directory. `-SkipPack`
builds the layout and stops, and downgrades the placeholder-identity error to a warning - enough to
catch a broken manifest or a moved logo without a Partner Center identity or a minute of packing.

What the script does, and why:

- **Visual assets are generated, not committed.** All six logos are resized from the existing
  1080x1080 `src/RavensPort.App/Assets/logo.png`, so the tile, the taskbar icon and the Store
  listing cannot drift from the icon the app itself uses.
- **The version is injected** into a copy of the manifest in the layout. The committed file keeps
  `0.0.0.0`.
- **`resources.pri` is generated** by `makepri`, best effort. Not strictly required for a package
  with no MRT-qualified resources, but ingestion is happier with a package shaped like the ones it
  usually sees.

### Why this publish is not single-file

Every other publish here uses `PublishSingleFile`; the MSIX one deliberately does not. MSIX
compresses and block-dedupes its own payload, so a Store update downloads only the blocks that
changed - which relinking a single-file bundle defeats, because it rewrites all of them. It also
drops the extract-to-temp step single-file performs on every cold start. Self-contained is not
optional: there is no MSIX framework package for the .NET desktop runtime, so a framework-dependent
package would install and then fail to start with no way for the Store to satisfy the dependency.

## What running under MSIX changes

The package declares `EntryPoint="Windows.FullTrustApplication"` with the `runFullTrust` capability,
so the app runs outside AppContainer with the user's own rights. Everything that would otherwise be
a problem keeps working unchanged:

- Kestrel and `HttpListener` bind to `127.0.0.1` - loopback is only blocked for AppContainer apps.
- `pass-cli` still runs as a child process.
- The Go `onepassword.dll` still P/Invokes.
- The `RavensPort_SingleInstance` mutex still works.

One real difference: **the MSIX container redirects `%APPDATA%` and `%LOCALAPPDATA%` writes** into
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\`. A user moving from the Inno installer to
the Store build therefore starts with fresh local state - settings and activity log. Nothing
irreplaceable lives there, because the configuration itself is in the user's password manager, but
it belongs in the release notes.

## The `StoreBuild` flag: what the package does not have

Certification kept failing the package on features the EXE is entitled to have. The first round
(4.3.0) was answered by deleting them from the single Windows build; the second round was not
survivable that way, because what it asked for is the removal of Proton Pass and mTLS altogether -
and the EXE has no reason to give those up. So there are now two builds from one codebase.

### The findings

| Policy | What it said | What it pointed at |
|---|---|---|
| 10.1.5 Software Distribution | "The product promotes acquiring software outside the Store" | The setup page's Proton Pass card - originally its **Open download page** button, and on resubmission the card itself |
| 10.2.10 Security | "Your product must not compromise the security of users... - Certificate Installation" | Settings -> **Generate new certificate** |
| 10.2.10.1 Security | "An App or its metadata cannot initiate downloads of other apps or executables" | "Location of Download: Settings > Generate New Certificate" |

The certificate findings are worth reading carefully, because the app never installed a certificate
into a Windows store and never downloaded one. It minted a self-signed PFX for its own loopback
listener and offered to save it. That is not what the policy is aimed at, and it is also not an
argument worth having twice: mTLS on 127.0.0.1 sits behind a per-endpoint proxy key that is doing
the real work anyway, so the store build drops it and the EXE keeps it.

### What each build ships

| | EXE (installer, GitHub release) | MSIX (Microsoft Store) |
|---|---|---|
| 1Password backend | yes | yes |
| Single use (memory only) | yes | yes |
| Proton Pass backend | yes | **no** |
| mTLS listener | yes | **no** |
| Certificate generation and export | yes | **no** |
| Everything else | yes | yes |

Nothing else differs. Routes, funnels, OAuth2, proxy keys, the activity log, Windows Hello, the
vault integrity check - all identical.

### How it works

`-p:StoreBuild=true` defines `STORE_BUILD`, which drives
[`BuildProfile`](../src/RavensPort.Core/BuildProfile.cs). Two mechanisms, chosen per site:

- **`const` flags** where the code has to stay compiled (`BuildProfile.ProtonPassEnabled`,
  `BuildProfile.MtlsEnabled`). The compiler folds the branch, and XAML can bind to a view model
  property that reads one - which is how the Settings tab's whole "Client Certificate" card
  collapses.
- **`#if STORE_BUILD`** where the point is that the code is *not in the package*. Most of all
  `MtlsCertificateFactory.GenerateClientCertificatePfx`: the store build does not hide the Generate
  button, it does not carry the method behind it.

Proton Pass is removed at one place and one place only -
`VaultGateService.EvaluateAsync` does not probe it. Everything the user sees is downstream of what
that returns: the setup page's cards, the tie-break between two ready managers, the Settings tab's
"Sign out of Proton Pass" button. One switch, so nothing can fall out of step with it. The public
entry points (`ConnectAsync`, `SelectBackend`, `CreateVaultAsync`, `UseExistingVaultAsync`) throw
`NotSupportedException` for the backend as a backstop.

The Store listing copy is metadata under the same 10.1.5, so
[STORE-LISTING.md](STORE-LISTING.md) does not name Proton Pass either.

### It must be a command-line property

```powershell
dotnet publish src/RavensPort.App/RavensPort.App.csproj -p:PublishProfile=win-x64-msix -p:StoreBuild=true -c Release
```

Not in `win-x64-msix.pubxml`, and this is the trap. Publish-profile properties apply to the project
being published and **do not flow across a `ProjectReference`**, so a profile-set flag would build
`RavensPort.App` with `STORE_BUILD` and `RavensPort.Core` without it. The result compiles, looks
right, and still has the removed code in it. A command-line `-p:` is a global property and reaches
all three projects.

Because that failure is invisible by inspection, it is checked rather than trusted. Under
`StoreBuild=true` the PE product name becomes `RavensPort (Microsoft Store)`;
`packaging/build-msix.ps1` refuses to pack a payload without it, and `installer/build.ps1` refuses
to wrap one *with* it - so neither build can be shipped through the other's pipeline.

### What running `pass-cli` still looks like

Only in the EXE. There, `pass-cli` is located by `VaultProbe` wherever the user installed it and run
as a child process; a copy left by 4.3.0 or earlier is still found, last, so upgrades keep working.
The `ProtonPassInstaller` and `VaultLockGuidance.DownloadUrl` deleted for the first round are gone
from both builds and are not coming back - the app installs no software.

### A user moving between the two

Both builds read the same vault. Someone with the EXE on one machine and the Store package on
another has one configuration between them, so the store build has to cope with settings it cannot
honour:

- A vault whose `MtlsEnabled` is true: the store build binds `http://127.0.0.1` anyway and says so
  in the activity log. Refusing to start would strand the user; binding silently would let them
  believe the proxy demands a certificate when it does not. Per-endpoint proxy keys still apply.
- A vault last written by the Proton Pass backend: nothing reads it in the store build. The user
  connects 1Password, or installs the EXE.

`AppSettings` deliberately keeps its mTLS fields in both builds. Forking the schema would have one
build discard the other's settings on every write.

## What a reviewer sees

The findings that produced the installer (10.1.2.10, 10.2.7, 10.3.4) are satisfied by the package
itself rather than by anything RavensPort does:

1. Install and uninstall are handled by the Store.
2. **RavensPort** appears in Settings -> Apps -> Installed apps.
3. A **RavensPort** Start menu entry comes from the manifest's `<Application>`.
4. Launching shows the app window - not just a tray icon.
5. Tray -> Exit quits; launching again brings it straight back.
6. Launching while it is already running brings the existing window forward.

Points 4-6 are app behaviour, fixed alongside the installer, and still worth walking on a machine
that has never had RavensPort installed.

The listing still has no screenshot meeting Partner Center's 1366x768 minimum; see the Screenshots
section of [STORE-LISTING.md](STORE-LISTING.md). That blocks the listing independently of any of
this.
