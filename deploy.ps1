#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes Dev.Conduit to a folder you can run from.

.DESCRIPTION
    Framework-dependent publish: the machine that runs it needs both the ASP.NET Core
    Runtime 10 and the .NET Desktop Runtime 10 (the tray app uses Windows Forms). The
    output runs as a console app, a tray app or a Windows service; there is no IIS site
    and no remote copy step.

    To install on a machine without the runtimes, use the MSI instead:
    .\build.ps1 -Configuration Release builds artifacts\Dev.Conduit-<version>.msi, a
    self-contained package that installs the Workstation or Server role (README section 05.3).

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -OutputPath D:\tools\devconduit -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'publish'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Detailed
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Proxylayer.DevConduit\Proxylayer.DevConduit.csproj'

if (-not (Test-Path $project)) {
    throw "Project not found at $project."
}

Write-Host "Publishing Dev.Conduit [$Configuration] to $OutputPath" -ForegroundColor Cyan

$arguments = @(
    $project,
    '--configuration', $Configuration,
    '--output', $OutputPath,
    '--self-contained', 'false',
    '--nologo'
)
$arguments += if ($Detailed) { @('-v', 'detailed') } else { @('-v', 'minimal') }

& dotnet publish @arguments
if ($LASTEXITCODE -ne 0) {
    Write-Host 'PUBLISH FAILED' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host 'PUBLISH SUCCEEDED' -ForegroundColor Green
Write-Host ''
Write-Host 'Requirements on the machine that runs it (framework-dependent, Windows x64):' -ForegroundColor Cyan
Write-Host '    - ASP.NET Core Runtime 10'
Write-Host '    - .NET Desktop Runtime 10   (new: the tray app uses Windows Forms; needed even if the tray is never used)'
Write-Host '    Both from https://dotnet.microsoft.com/download/dotnet/10.0'
Write-Host '    Or install from the MSI, which needs neither: .\build.ps1 -Configuration Release builds'
Write-Host '    artifacts\Dev.Conduit-<version>.msi (README section 05.3).'
Write-Host ''
Write-Host 'Run it with (start from the publish folder so appsettings.json and wwwroot resolve):' -ForegroundColor Cyan
Write-Host "    Set-Location '$OutputPath'"
Write-Host "    .\Dev.Conduit.exe"
Write-Host ''
Write-Host 'Then open http://localhost:8091 in a browser.'
Write-Host ''
Write-Host 'The same folder runs the workstation half. On a developer machine:' -ForegroundColor Cyan
Write-Host "    .\Dev.Conduit.exe --mode local"
Write-Host '    (loopback only, console on http://127.0.0.1:8092, forward proxy on :8093)'
Write-Host '    Name this instance under DevConduit:Remotes first; see docs\LOCAL-MODE.md.'
Write-Host ''
Write-Host 'Or run the tray app, which hosts local mode on a workstation and manages the service on the server:' -ForegroundColor Cyan
Write-Host "    .\Dev.Conduit.exe tray"
Write-Host '    Its setup window saves to %ProgramData%\Proxylayer\Dev.Conduit\settings.json (machine) and'
Write-Host '    %LOCALAPPDATA%\Proxylayer\Dev.Conduit\settings.json (user), which survive an update; see README section 05.2.'
Write-Host ''
Write-Host 'On the server it can run as a Windows service instead (server mode only). From an elevated prompt:' -ForegroundColor Cyan
Write-Host "    Set-Location '$OutputPath'"
Write-Host '    .\Dev.Conduit.exe install'
Write-Host '    .\Dev.Conduit.exe start'
Write-Host '    (stop / uninstall the same way; logs go to the Application event log, source Dev.Conduit;'
Write-Host '     see README section 05.1)'
Write-Host ''
Write-Host 'Before the first run, configure Entra ID:' -ForegroundColor Cyan
Write-Host '    1. Run .\register-app.ps1 once, with -UserTenantId (the tenant users sign in from) and'
Write-Host '       -ServerTenantId (the tenant that holds the VM). It provisions the app registration and the'
Write-Host '       managed identity on the server VM, and prints the users'' tenant admin-consent link.'
Write-Host "    2. AzureAd:TenantId, AzureAd:ClientId and AzureAd:ClientCredentials in"
Write-Host "       '$([System.IO.Path]::Combine($OutputPath, 'appsettings.json'))' must hold the values it printed."
Write-Host '       No client secret is needed: on the VM the managed identity is the credential.'
Write-Host '    3. Off the VM there is no managed identity. To run server mode elsewhere, supply a secret instead:'
Write-Host '           $env:AzureAd__ClientSecret = "<secret>"'
Write-Host '       User secrets are NOT read by a published instance (Development environment only).'
Write-Host ''
Write-Host 'History and header sets are written to %LOCALAPPDATA%\Proxylayer\Dev.Conduit unless DevConduit:DataRoot says otherwise.'
