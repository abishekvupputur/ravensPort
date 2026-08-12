<#
.SYNOPSIS
    Compiles the RavensPort installer.

.DESCRIPTION
    Shared by the PR and release workflows so the two cannot drift apart. The PR build only needs
    to know that the script still compiles, so it passes -CompressionMode none and finishes in
    seconds; the release build takes the default and spends about 80 seconds on LZMA2 to get an
    installer small enough to commit (see the Compression note in RavensPort.iss).

.OUTPUTS
    The path of the installer it produced, relative to the repository root.
#>
[CmdletBinding()]
param(
    # Digits only. The release tag carries a leading "v" that Inno's VersionInfoVersion rejects.
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+(\.\d+){0,3}$')]
    [string] $Version,

    # The published single-file exe to wrap. Relative to the repository root, or absolute.
    [Parameter(Mandatory)]
    [string] $SourceExe,

    [string] $CompressionMode = 'lzma2/max'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'RavensPort.iss'

# Resolved to an absolute path before it reaches Inno. Paths in the script are otherwise relative
# to the .iss file rather than to the working directory, which is a difference nobody remembers
# until a build fails on a machine whose layout differs.
$payload = if ([System.IO.Path]::IsPathRooted($SourceExe)) { $SourceExe } else { Join-Path $repoRoot $SourceExe }
if (-not (Test-Path $payload)) {
    throw "Installer payload not found: $payload (publish must run first)"
}
$payload = (Resolve-Path $payload).Path

# The installer ships the full app -- Proton Pass, mTLS, certificate generation, all of it. Only
# the Microsoft Store package drops those, and only because certification rejected them. Wrapping a
# store payload here would quietly ship a cut-down EXE to everyone downloading from GitHub, which
# is the mirror image of the mistake packaging/build-msix.ps1 guards against. See BuildProfile.cs
# and Directory.Build.props.
$product = (Get-Item $payload).VersionInfo.ProductName
if ($product -like '*Store*') {
    throw "$payload was built with -p:StoreBuild=true (ProductName '$product'). The installer must " +
          'wrap the full build: publish again without that property.'
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    # Inno ships on the GitHub Windows runner images, but not always on PATH, and winget installs
    # it per-user rather than into Program Files.
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) was not found. Install it, or add it to PATH.' }

Write-Host "ISCC:        $iscc"
Write-Host "Payload:     $payload"
Write-Host "Compression: $CompressionMode"

& $iscc $script "/DAppVersion=$Version" "/DSourceExe=$payload" "/DCompressionMode=$CompressionMode"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$setup = Join-Path $repoRoot "dist\RavensPort-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "ISCC reported success but $setup is missing." }

$size = (Get-Item $setup).Length
Write-Host ("Installer:   {0} ({1:N1} MB)" -f $setup, ($size / 1MB))

# The Store submission needs this committed to dist/ and served from raw.githubusercontent.com,
# and GitHub refuses a file over 100 MB in a repository. Catching that here beats catching it at
# git push, when the release has already been cut. Only meaningful for a real release build.
if ($CompressionMode -ne 'none' -and $size -gt 100MB) {
    # ASCII only in this file. Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so a stray
    # em dash here becomes three bytes of mojibake and takes the parser down with it.
    throw ("Installer is {0:N1} MB, over GitHub's 100 MB file limit, so it could not be committed " -f ($size / 1MB)) +
          'to dist/ - which is how the Microsoft Store submission is hosted. See docs/STORE-SUBMISSION.md.'
}

"dist/RavensPort-Setup-$Version.exe"
