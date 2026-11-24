#Requires -Version 7.0
<#
.SYNOPSIS
    Builds Dev.Conduit.

.DESCRIPTION
    Simple (default) prints a pass/fail summary and any errors.
    -Detailed prints full MSBuild diagnostic output.
    -Target <name> builds a single project and filters output to it.

    After a successful build of the solution (or of Dev.Conduit), the app is
    published from the build output and zipped to
    artifacts\Dev.Conduit-<Configuration>.zip. The zip is a framework-dependent
    publish: unzip it on a Windows machine with the ASP.NET Core Runtime 10 and the
    .NET Desktop Runtime 10, and run Dev.Conduit.exe from the extracted folder.
    -NoPackage skips this step and the installer.

    The installer step (on by default for Release, off for Debug; -Installer turns it on,
    -NoInstaller off) publishes the app self-contained for win-x64 to
    artifacts\installer\publish, builds the WiX MSI from it to
    artifacts\Dev.Conduit-<version>.msi (its ProductVersion is the exe's file version,
    <version>.0), and zips the MSI with INSTALL.txt to
    artifacts\Dev.Conduit-<version>-installer.zip. The MSI needs no .NET runtimes on the
    target machine.

    The version is <Version> in src\Proxylayer.DevConduit\Proxylayer.DevConduit.csproj; -Version overrides it
    for this build (major.minor.build, as an MSI product version allows).

    Next to the MSI the installer step writes artifacts\Dev.Conduit-<version>.msi.sha256: the
    lower-case SHA-256 hex digest, two spaces and the file name, which the updater checks a
    downloaded MSI against.

    -Sign signs the installer's files with Authenticode: the published Dev.Conduit.exe and the
    custom action DLL before the WiX build packages them, and the MSI after it is built. Every
    signature is timestamped by a public RFC 3161 server (SIGN_TIMESTAMP_URL overrides it). The
    default, none, signs nothing and still produces every artifact.
      signtool         signtool.exe from PATH or the Windows SDK, with either SIGN_PFX_PATH and
                       SIGN_PFX_PASSWORD (a .pfx file) or SIGN_CERT_THUMBPRINT (a certificate in
                       the current user's or the machine's personal store). Timestamp default:
                       http://timestamp.digicert.com.
      trusted-signing  Azure Trusted Signing through the dotnet "sign" tool (installed into
                       artifacts\tools when missing), with AZURE_TRUSTED_SIGNING_ENDPOINT,
                       AZURE_TRUSTED_SIGNING_ACCOUNT and AZURE_TRUSTED_SIGNING_PROFILE. It signs in
                       as DefaultAzureCredential does: AZURE_TENANT_ID, AZURE_CLIENT_ID and
                       AZURE_CLIENT_SECRET, or an Azure CLI login. Timestamp default:
                       http://timestamp.acs.microsoft.com.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Detailed
    .\build.ps1 -Target Proxylayer.DevConduit
    .\build.ps1 -Configuration Release
    .\build.ps1 -Configuration Release -Version 1.2.0
    .\build.ps1 -Configuration Release -NoInstaller
    .\build.ps1 -Configuration Release -Version 1.2.0 -Sign signtool
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Target,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoRestore,
    [switch]$NoPackage,
    [string]$Version,
    [switch]$Installer,
    [switch]$NoInstaller,
    [ValidateSet('none', 'signtool', 'trusted-signing')]
    [string]$Sign = 'none'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$solution = Join-Path $root 'Proxylayer.DevConduit.sln'
$appProject = Join-Path $root 'src\Proxylayer.DevConduit\Proxylayer.DevConduit.csproj'
$artifacts = Join-Path $root 'artifacts'
$installerProject = Join-Path $root 'installer\Proxylayer.DevConduit.Installer.wixproj'

if ($Installer -and $NoInstaller) {
    throw '-Installer and -NoInstaller cannot be used together.'
}

# An MSI product version is major.minor.build, at most 255.255.65535.
function Test-ProductVersion {
    param([string]$Value)

    if ($Value -notmatch '^(\d+)\.(\d+)\.(\d+)$') { return $false }
    return [int]$Matches[1] -le 255 -and [int]$Matches[2] -le 255 -and [int]$Matches[3] -le 65535
}

if ($Version -and -not (Test-ProductVersion $Version)) {
    throw "-Version must be major.minor.build, at most 255.255.65535, for example 1.2.0. '$Version' is not."
}

$versionArgs = if ($Version) { @("-p:Version=$Version") } else { @() }

# Authenticode signing for -Sign signtool and -Sign trusted-signing; a no-op for none.
function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { [version]($_.Directory.Parent.Name -replace '[^\d.]', '') } -Descending -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $found) { throw 'signtool.exe was found neither on PATH nor in the Windows 10/11 SDK.' }
    return $found.FullName
}

function Find-SignCli {
    $onPath = Get-Command sign -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $toolPath = Join-Path $artifacts 'tools'
    $local = Join-Path $toolPath 'sign.exe'
    if (-not (Test-Path $local)) {
        & dotnet tool install sign --tool-path $toolPath --prerelease | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The dotnet "sign" tool could not be installed.' }
    }
    return $local
}

function Invoke-Sign {
    param([string[]]$Files, [string]$What)

    if ($Sign -eq 'none') { return }

    foreach ($file in $Files) {
        if (-not (Test-Path $file)) { throw "Nothing to sign at $file." }
    }

    if ($Sign -eq 'signtool') {
        $tool = Find-SignTool
        $timestamp = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
        $signArgs = @('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256')
        if ($env:SIGN_PFX_PATH) {
            $signArgs += @('/f', $env:SIGN_PFX_PATH)
            if ($env:SIGN_PFX_PASSWORD) { $signArgs += @('/p', $env:SIGN_PFX_PASSWORD) }
        }
        elseif ($env:SIGN_CERT_THUMBPRINT) {
            $signArgs += @('/sha1', $env:SIGN_CERT_THUMBPRINT)
        }
        else {
            throw '-Sign signtool needs SIGN_PFX_PATH (and SIGN_PFX_PASSWORD) or SIGN_CERT_THUMBPRINT.'
        }

        $output = & $tool @signArgs @Files 2>&1
    }
    else {
        foreach ($name in 'AZURE_TRUSTED_SIGNING_ENDPOINT', 'AZURE_TRUSTED_SIGNING_ACCOUNT', 'AZURE_TRUSTED_SIGNING_PROFILE') {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "-Sign trusted-signing needs $name." }
        }

        $tool = Find-SignCli
        $timestamp = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.acs.microsoft.com' }
        $output = & $tool code trusted-signing @Files `
            --trusted-signing-endpoint $env:AZURE_TRUSTED_SIGNING_ENDPOINT `
            --trusted-signing-account $env:AZURE_TRUSTED_SIGNING_ACCOUNT `
            --trusted-signing-certificate-profile $env:AZURE_TRUSTED_SIGNING_PROFILE `
            --timestamp-url $timestamp `
            --verbosity warning 2>&1
    }

    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_.ToString() -ForegroundColor Red }
        throw "Signing $What failed ($Sign)."
    }

    if ($Detailed) { $output | ForEach-Object { Write-Host $_.ToString() } }
    Write-Host "SIGNED           $What ($Sign)" -ForegroundColor Green
}

function Resolve-Target {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return $solution
    }

    $candidate = Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter '*.csproj' |
        Where-Object { $_.BaseName -eq $Name -or $_.Name -eq $Name }

    if (-not $candidate) {
        throw "No project named '$Name' was found under src/. Known projects: " +
            ((Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter '*.csproj' | ForEach-Object BaseName) -join ', ')
    }

    return $candidate[0].FullName
}

$projectPath = Resolve-Target -Name $Target
$label = Split-Path $projectPath -Leaf

$arguments = @($projectPath, '--configuration', $Configuration, '--nologo') + $versionArgs
if ($NoRestore) { $arguments += '--no-restore' }
if ($Detailed) { $arguments += @('-v', 'detailed') } else { $arguments += @('-v', 'minimal') }

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$output = & dotnet build @arguments 2>&1
$exitCode = $LASTEXITCODE
$stopwatch.Stop()

$lines = $output | ForEach-Object { $_.ToString() }

if ($Detailed) {
    $lines | ForEach-Object { Write-Host $_ }
}
else {
    $interesting = $lines | Where-Object { $_ -match ': (error|warning) ' }

    if ($Target) {
        $interesting = $interesting | Where-Object { $_ -match [regex]::Escape($label) -or $_ -match 'error' }
    }

    foreach ($line in $interesting) {
        if ($line -match ': error') { Write-Host $line -ForegroundColor Red }
        else { Write-Host $line -ForegroundColor Yellow }
    }
}

$errorCount = ($lines | Where-Object { $_ -match ': error ' }).Count
$warningCount = ($lines | Where-Object { $_ -match ': warning ' }).Count
$elapsed = '{0:N1}s' -f $stopwatch.Elapsed.TotalSeconds

if ($exitCode -eq 0) {
    Write-Host "BUILD SUCCEEDED  $label [$Configuration]  warnings: $warningCount  ($elapsed)" -ForegroundColor Green
}
else {
    Write-Host "BUILD FAILED     $label [$Configuration]  errors: $errorCount  warnings: $warningCount  ($elapsed)" -ForegroundColor Red
    if (-not $Detailed) {
        Write-Host "Re-run with -Detailed for the full MSBuild log." -ForegroundColor DarkGray
    }
    exit $exitCode
}

# Package: publish the app from the build output just produced and zip it.
$buildsApp = $projectPath -eq $solution -or $projectPath -eq (Resolve-Path $appProject).Path
if ($NoPackage -or -not $buildsApp) {
    exit 0
}

$staging = Join-Path $artifacts "publish\$Configuration"
$zipPath = Join-Path $artifacts "Dev.Conduit-$Configuration.zip"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$publishArgs = @($appProject, '--configuration', $Configuration, '--output', $staging, '--no-build', '--nologo') + $versionArgs
$publishArgs += if ($Detailed) { @('-v', 'detailed') } else { @('-v', 'quiet') }

$publishOutput = & dotnet publish @publishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    $publishOutput | ForEach-Object { Write-Host $_.ToString() }
    Write-Host "PACKAGE FAILED   dotnet publish exited with $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}
if ($Detailed) { $publishOutput | ForEach-Object { Write-Host $_.ToString() } }

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = '{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "PACKAGE CREATED  $zipPath  ($zipSize)" -ForegroundColor Green

# Installer: a self-contained publish, the MSI built from it, and a zip of the MSI with INSTALL.txt.
$buildInstaller = if ($NoInstaller) { $false } elseif ($Installer) { $true } else { $Configuration -eq 'Release' }
if (-not $buildInstaller) {
    exit 0
}

$stopwatch.Restart()

if (-not $Version) {
    $Version = "$(& dotnet msbuild $appProject -getProperty:Version -nologo 2>&1)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-ProductVersion $Version)) {
        Write-Host "INSTALLER FAILED the project's <Version>, '$Version', is not major.minor.build." -ForegroundColor Red
        exit 1
    }
}

$installerRoot = Join-Path $artifacts 'installer'
$payload = Join-Path $installerRoot 'publish'
$zipStaging = Join-Path $installerRoot 'zip'
$msiPath = Join-Path $artifacts "Dev.Conduit-$Version.msi"
$installerZip = Join-Path $artifacts "Dev.Conduit-$Version-installer.zip"

foreach ($folder in @($payload, $zipStaging)) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}

$selfContainedArgs = @(
    $appProject, '--configuration', $Configuration, '--runtime', 'win-x64', '--self-contained', 'true',
    '--output', $payload, '--nologo', "-p:Version=$Version"
)
$selfContainedArgs += if ($Detailed) { @('-v', 'detailed') } else { @('-v', 'quiet') }

$publishOutput = & dotnet publish @selfContainedArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    $publishOutput | ForEach-Object { Write-Host $_.ToString() }
    Write-Host "INSTALLER FAILED self-contained dotnet publish exited with $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}
if ($Detailed) { $publishOutput | ForEach-Object { Write-Host $_.ToString() } }

# The custom actions are built here rather than by the WiX build, so the DLL it packages can be
# signed first; the WiX build then takes it as it is (BuildProjectReferences=false).
$customActionsProject = Join-Path $root 'installer\CustomActions\Proxylayer.DevConduit.Installer.CustomActions.csproj'
$caArgs = @($customActionsProject, '--configuration', $Configuration, '--nologo')
$caArgs += if ($Detailed) { @('-v', 'detailed') } else { @('-v', 'quiet') }
$caOutput = & dotnet build @caArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    $caOutput | ForEach-Object { Write-Host $_.ToString() }
    Write-Host "INSTALLER FAILED the custom actions did not build" -ForegroundColor Red
    exit $LASTEXITCODE
}
$customActionsDll = Join-Path $root "installer\CustomActions\bin\x64\$Configuration\net472\Proxylayer.DevConduit.Installer.CustomActions.CA.dll"

try {
    Invoke-Sign -Files @((Join-Path $payload 'Dev.Conduit.exe'), $customActionsDll) -What 'Dev.Conduit.exe and the custom actions'
}
catch {
    Write-Host "INSTALLER FAILED $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

$wixArgs = @($installerProject, '--configuration', $Configuration, '--nologo', "-p:PayloadDir=$payload\", '-p:BuildProjectReferences=false')
$wixArgs += if ($Detailed) { @('-v', 'detailed') } else { @('-v', 'minimal') }

$wixOutput = & dotnet build @wixArgs 2>&1
$wixExit = $LASTEXITCODE
$wixLines = $wixOutput | ForEach-Object { $_.ToString() }

# MSBuild repeats every diagnostic in its closing summary; count each once.
$wixDiagnostics = @($wixLines | Where-Object { $_ -match ': (error|warning) ' } | Select-Object -Unique)

if ($Detailed) {
    $wixLines | ForEach-Object { Write-Host $_ }
}
else {
    foreach ($line in $wixDiagnostics) {
        if ($line -match ': error') { Write-Host $line -ForegroundColor Red }
        else { Write-Host $line -ForegroundColor Yellow }
    }
}

$wixWarnings = @($wixDiagnostics | Where-Object { $_ -match ': warning ' }).Count
if ($wixExit -ne 0) {
    Write-Host "INSTALLER FAILED the MSI did not build  warnings: $wixWarnings" -ForegroundColor Red
    exit $wixExit
}

$builtMsi = Join-Path $root "installer\bin\$Configuration\Dev.Conduit.msi"
if (-not (Test-Path $builtMsi)) {
    Write-Host "INSTALLER FAILED no MSI at $builtMsi" -ForegroundColor Red
    exit 1
}

Copy-Item $builtMsi $msiPath -Force
try {
    Invoke-Sign -Files @($msiPath) -What (Split-Path $msiPath -Leaf)
}
catch {
    Write-Host "INSTALLER FAILED $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# The checksum the updater verifies a downloaded MSI against, taken after signing.
$checksumPath = "$msiPath.sha256"
$digest = (Get-FileHash -Path $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksumPath -Value "$digest  $(Split-Path $msiPath -Leaf)`n" -NoNewline -Encoding utf8NoBOM

Copy-Item $msiPath $zipStaging
# CRLF, so older Notepad on a server shows the lines.
(Get-Content (Join-Path $root 'installer\INSTALL.txt') -Raw).Replace('{{Version}}', $Version) -replace "`r?`n", "`r`n" |
    Set-Content (Join-Path $zipStaging 'INSTALL.txt') -NoNewline

if (Test-Path $installerZip) { Remove-Item $installerZip -Force }
Compress-Archive -Path (Join-Path $zipStaging '*') -DestinationPath $installerZip -CompressionLevel Optimal

$msiSize = '{0:N1} MB' -f ((Get-Item $msiPath).Length / 1MB)
$elapsed = '{0:N1}s' -f $stopwatch.Elapsed.TotalSeconds
Write-Host "INSTALLER CREATED $msiPath  ($msiSize)  warnings: $wixWarnings  ($elapsed)" -ForegroundColor Green
Write-Host "INSTALLER ZIPPED  $installerZip" -ForegroundColor Green
Write-Host "CHECKSUM WRITTEN $checksumPath" -ForegroundColor Green

exit 0
