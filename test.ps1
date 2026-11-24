#Requires -Version 7.0
<#
.SYNOPSIS
    Runs the Dev.Conduit test suites.

.DESCRIPTION
    Simple (default) prints a pass/fail summary and the names of failing tests.
    -Detailed prints the full runner output for every test.
    -Target <name> runs a single test project, or filters to matching tests when the
    name is not a project (class, method or namespace, wildcards allowed).

    The run is always headless and non-interactive, and exits non-zero on failure.

    -Trx also writes a TRX report per test project to TestResults\<project>.trx (or under
    -ResultsDirectory), for CI to upload.

.EXAMPLE
    .\test.ps1
    .\test.ps1 -Detailed
    .\test.ps1 -Target Proxylayer.DevConduit.Tests
    .\test.ps1 -Target RouteCookiePolicyTests -Detailed
    .\test.ps1 -Trx
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Target,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$Trx,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$testsRoot = Join-Path $root 'tests'

function Get-TestProject {
    if (-not (Test-Path $testsRoot)) {
        throw "No tests/ directory was found under $root."
    }

    return Get-ChildItem -Path $testsRoot -Recurse -Filter '*.csproj'
}

$projects = @(Get-TestProject)
if ($projects.Count -eq 0) {
    throw "No test projects were found under tests/."
}

# -Target names either a test project or a subset of tests inside every project.
$filter = ''
if (-not [string]::IsNullOrWhiteSpace($Target)) {
    $matched = $projects | Where-Object { $_.BaseName -eq $Target -or $_.Name -eq $Target }
    if ($matched) {
        $projects = @($matched)
    }
    else {
        $filter = $Target
    }
}

$totals = [ordered]@{ total = 0; passed = 0; failed = 0; skipped = 0 }
$failedNames = [System.Collections.Generic.List[string]]::new()
$exitCode = 0
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($project in $projects) {
    $label = $project.BaseName

    if (-not $NoBuild) {
        $buildOutput = & dotnet build $project.FullName --configuration $Configuration --nologo -v quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            $buildOutput | ForEach-Object { Write-Host $_.ToString() -ForegroundColor Red }
            Write-Host "TEST FAILED      $label [$Configuration]  the test project did not build." -ForegroundColor Red
            exit 1
        }
    }

    $assembly = Join-Path $project.DirectoryName "bin/$Configuration/net10.0-windows/$label.exe"
    if (-not (Test-Path $assembly)) {
        throw "The test runner '$assembly' was not found. Run without -NoBuild."
    }

    # Microsoft.Testing.Platform: headless by construction, no watch mode, no UI.
    $arguments = @('--no-ansi', '--progress', 'off')
    if ($Detailed) { $arguments += @('--output', 'Detailed') }
    if ($filter) { $arguments += @('--filter-display-name', "*$filter*", '--zero-tests-policy', 'allow-skipped') }
    if ($Trx) {
        $resultsPath = if ($ResultsDirectory) { [System.IO.Path]::GetFullPath($ResultsDirectory) } else { Join-Path $root 'TestResults' }
        $arguments += @('--results-directory', $resultsPath, '--report-xunit-trx', '--report-xunit-trx-filename', "$label.trx")
    }

    $output = & $assembly @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE }

    $lines = $output | ForEach-Object { $_.ToString() }

    if ($Detailed) {
        $lines | ForEach-Object { Write-Host $_ }
    }

    $section = ''
    foreach ($line in $lines) {
        switch -Regex ($line) {
            '^\s*total:\s*(\d+)\s*$'     { $totals.total += [int]$Matches[1]; continue }
            '^\s*failed:\s*(\d+)\s*$'    { $totals.failed += [int]$Matches[1]; continue }
            '^\s*succeeded:\s*(\d+)\s*$' { $totals.passed += [int]$Matches[1]; continue }
            '^\s*skipped:\s*(\d+)\s*$'   { $totals.skipped += [int]$Matches[1]; continue }
            '^\s*failed\s+(.+)$'         { $section = $Matches[1].Trim(); $failedNames.Add("$label :: $section"); continue }
        }
    }

    # A crash or a filter that matched nothing leaves no summary to parse.
    if ($LASTEXITCODE -ne 0 -and $failedNames.Count -eq 0 -and -not $Detailed) {
        $lines | Where-Object { $_ -match 'error|Exception|Unhandled' } | ForEach-Object {
            Write-Host $_ -ForegroundColor Red
        }
    }
}

$stopwatch.Stop()

foreach ($name in $failedNames) {
    Write-Host "FAILED  $name" -ForegroundColor Red
}

$elapsed = '{0:N1}s' -f $stopwatch.Elapsed.TotalSeconds
$scope = if ($filter) { "$($projects.BaseName -join ', ') (filter: $filter)" } else { $projects.BaseName -join ', ' }
$summary = "total: $($totals.total)  passed: $($totals.passed)  failed: $($totals.failed)  skipped: $($totals.skipped)"

if ($exitCode -eq 0 -and $totals.failed -eq 0) {
    Write-Host "TESTS PASSED     $scope [$Configuration]  $summary  ($elapsed)" -ForegroundColor Green
}
else {
    Write-Host "TESTS FAILED     $scope [$Configuration]  $summary  ($elapsed)" -ForegroundColor Red
    if (-not $Detailed) {
        Write-Host "Re-run with -Detailed, or -Target <test> -Detailed, for the full runner output." -ForegroundColor DarkGray
    }

    if ($exitCode -eq 0) { $exitCode = 1 }
}

exit $exitCode
