<#
.SYNOPSIS
    Runs the test suites with coverage collection and merges the results into one report.

.DESCRIPTION
    RavensPort.Core is exercised from two directions. RavensPort.Core.Tests drives it against an
    InMemoryVault and a temp directory; RavensPort.SystemTests drives the same assembly against a
    real 1Password account. Neither number is interesting on its own -- the unit suite never reaches
    the code paths behind the native vault client, and the system suite deliberately does not
    re-prove the branch coverage the unit suite already has. The union is the figure worth looking
    at, and producing it is what this script exists for.

    Both runs share coverlet.runsettings, so the two Cobertura reports cover the same set of files
    under the same rules and can legitimately be merged. ReportGenerator (pinned in
    .config/dotnet-tools.json) does the merging: a line hit by either suite is covered once.

.PARAMETER SkipSystemTests
    Run the unit suite only. The report is then labelled as partial, because a merged number that
    silently lost half its input is worse than an obviously incomplete one.

.PARAMETER NoBuild
    Reuse the existing build output. Saves about a minute when iterating on tests alone.

.PARAMETER Open
    Open the HTML report in the default browser when it is finished.

.PARAMETER OutputDirectory
    Where the raw .cobertura.xml files and the merged report go. Defaults to ./TestResults, which
    .gitignore already covers.

.EXAMPLE
    ./coverage.ps1 -SkipSystemTests -Open

    The usual local loop: unit coverage only, no 1Password account needed, report opens when done.

.EXAMPLE
    $env:OP_SERVICE_ACCOUNT_TOKEN = '<token for a throwaway account>'
    $env:RAVENSPORT_SYSTEM_TEST_ACK = 'i-understand-this-erases-the-ravensport-vault'
    ./coverage.ps1

    The full figure. Read tests/RavensPort.SystemTests/README.md before doing this: the system
    suite erases every RavensPort item in the vault the token reaches.
#>
[CmdletBinding()]
param(
    [switch]$SkipSystemTests,
    [switch]$NoBuild,
    [switch]$Open,
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'TestResults')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot    = $PSScriptRoot
$runSettings = Join-Path $repoRoot 'coverlet.runsettings'
$rawDir      = Join-Path $OutputDirectory 'coverage-raw'
$reportDir   = Join-Path $OutputDirectory 'coverage-report'

# A stale .cobertura.xml from a previous run would be picked up by the merge glob below and reported
# as though it came from this one. Coverage that is quietly a week old is the failure mode this
# whole script exists to avoid, so the raw directory starts empty every time.
if (Test-Path $rawDir)    { Remove-Item $rawDir -Recurse -Force }
if (Test-Path $reportDir) { Remove-Item $reportDir -Recurse -Force }
New-Item -ItemType Directory -Path $rawDir -Force | Out-Null

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with exit code $LASTEXITCODE." }

# Whether each suite actually ran, tracked separately from whether it passed. A suite that was
# skipped contributes no lines, and the summary at the end has to say so rather than presenting a
# unit-only figure as the combined one.
$ranUnit   = $false
$ranSystem = $false

function Invoke-CoveredTestRun {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Label
    )

    # Each suite gets its own results directory. The collector names every file
    # coverage.cobertura.xml inside a fresh GUID folder, so two runs sharing one directory would be
    # distinguishable only by that GUID -- and when the merged number later looks wrong, "which
    # suite produced this file" is the first question. The directory name answers it.
    $resultsDir = Join-Path $rawDir $Label

    $testArgs = @(
        'test', $Project,
        '-c', $Configuration,
        '--settings', $runSettings,
        '--collect:XPlat Code Coverage',
        '--results-directory', $resultsDir,
        '--verbosity', 'minimal'
    )
    if ($NoBuild) { $testArgs += '--no-build' }

    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan

    # Out-Host, not a bare call. A native command's stdout lands on the function's output stream,
    # so without this the caller receives every line dotnet printed with the exit code appended to
    # it, and the `-ne 0` check at the bottom then compares an array against a number and is always
    # true. Sending the output straight to the host leaves the exit code as the only return value.
    & dotnet @testArgs | Out-Host
    return $LASTEXITCODE
}

$unitExit = Invoke-CoveredTestRun `
    -Project (Join-Path $repoRoot 'tests/RavensPort.Core.Tests/RavensPort.Core.Tests.csproj') `
    -Label 'unit'
$ranUnit = $true

# The system suite does not run unless the environment says it may. Both variables are checked here
# and not just the token, mirroring SystemTestEnvironment: the token decides which account is
# reached, the acknowledgement is the switch that permits erasing its vault. Without them every test
# in that project reports Skipped, which produces a Cobertura report of zeroes -- and merging that
# in would drag the combined figure down for a reason that has nothing to do with the code.
$systemExit = 0
$token = $env:OP_SERVICE_ACCOUNT_TOKEN
$ack   = $env:RAVENSPORT_SYSTEM_TEST_ACK

if ($SkipSystemTests) {
    Write-Host ""
    Write-Host "System suite skipped: -SkipSystemTests was passed." -ForegroundColor Yellow
}
elseif ([string]::IsNullOrWhiteSpace($token) -or [string]::IsNullOrWhiteSpace($ack)) {
    Write-Host ""
    Write-Host "System suite skipped: OP_SERVICE_ACCOUNT_TOKEN and RAVENSPORT_SYSTEM_TEST_ACK are not both set." -ForegroundColor Yellow
    Write-Host "  See tests/RavensPort.SystemTests/README.md. Pass -SkipSystemTests to silence this." -ForegroundColor Yellow
}
else {
    $systemExit = Invoke-CoveredTestRun `
        -Project (Join-Path $repoRoot 'tests/RavensPort.SystemTests/RavensPort.SystemTests.csproj') `
        -Label 'system'
    $ranSystem = $true
}

$reports = @(Get-ChildItem -Path $rawDir -Recurse -Filter '*.cobertura.xml' -ErrorAction SilentlyContinue)
if ($reports.Count -eq 0) {
    throw "No coverage files were produced under $rawDir. The test run itself may have failed to start; check the output above."
}

Write-Host ""
Write-Host "Merging $($reports.Count) coverage file(s)." -ForegroundColor Cyan

# Cobertura is emitted alongside the human-readable output so the merged file can be fed to anything
# downstream -- another tool, a diff against the previous run -- without re-running the suites.
# TextSummary is what gets printed below; MarkdownSummaryGithub is what CI puts in the job summary.
dotnet reportgenerator `
    "-reports:$(($reports.FullName) -join ';')" `
    "-targetdir:$reportDir" `
    "-reporttypes:Html;Cobertura;TextSummary;MarkdownSummaryGithub" `
    "-title:RavensPort combined coverage" `
    "-verbosity:Warning"
if ($LASTEXITCODE -ne 0) { throw "reportgenerator failed with exit code $LASTEXITCODE." }

$summaryFile = Join-Path $reportDir 'Summary.txt'
if (Test-Path $summaryFile) {
    Write-Host ""
    Get-Content $summaryFile | Select-Object -First 20 | ForEach-Object { Write-Host $_ }
}

Write-Host ""
Write-Host "Sources in this figure:" -ForegroundColor Cyan
Write-Host ("  unit suite   : {0}" -f $(if ($ranUnit)   { 'included' } else { 'MISSING' }))
Write-Host ("  system suite : {0}" -f $(if ($ranSystem) { 'included' } else { 'MISSING - this is not the combined figure' }))
Write-Host ""
Write-Host "HTML report: $(Join-Path $reportDir 'index.html')"

if ($Open) { Start-Process (Join-Path $reportDir 'index.html') }

# A failing test still produces a usable coverage report -- the lines it executed before failing were
# genuinely executed -- but the script must not exit 0, or a caller that only checks the exit code
# would read a red suite as a passing coverage run.
if ($unitExit -ne 0)   { throw "The unit suite failed with exit code $unitExit. The coverage report above is still valid, but the build is not." }
if ($systemExit -ne 0) { throw "The system suite failed with exit code $systemExit. The coverage report above is still valid, but the build is not." }
