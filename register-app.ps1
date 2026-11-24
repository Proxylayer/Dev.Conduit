#Requires -Version 7.0
<#
.SYNOPSIS
    Provisions the Entra app registration and managed identity Dev.Conduit signs in with.

.DESCRIPTION
    Dev.Conduit authenticates without a client secret. The server runs on an Azure VM in the
    server's tenant (-ServerTenantId); a user-assigned managed identity on that VM is the
    app's credential, trusted through a federated identity credential. Users sign in from
    their own tenant, the users' tenant (-UserTenantId), so the app registration is
    multi-tenant and an admin of the users' tenant consents to it once.

    Entra imposes the shape: only a user-assigned identity can be a federated credential,
    and it must live in the same tenant as the app registration. Hence the app lives in
    the server's tenant, next to the identity, and is provisioned into the users' tenant by
    admin consent.

    The script converges, in the server's tenant:
      1. the user-assigned managed identity, created in the VM's resource group
      2. its attachment to the VM (existing identities on the VM are left alone)
      3. the multi-tenant app registration: redirect URIs, public client flows, the
         api://{appId} identifier, the delegated scope (-ScopeName, DevConduit.Forward by
         default) and required permissions
      4. the federated identity credential that trusts the managed identity
      5. the app's service principal
    then prints the users' tenant admin-consent link and the appsettings.json values.

    Every step is idempotent: a second run reports each object as unchanged. -WhatIf
    signs in and reads, but writes nothing.

    Uses the Azure CLI only. Roles needed in the server's tenant: Application Developer (or
    Cloud Application Administrator), plus Managed Identity Contributor and Virtual Machine
    Contributor on the resource group. Consent in the users' tenant needs a Cloud Application
    Administrator or Global Administrator there.

.PARAMETER UserTenantId
    The tenant users sign in from (the users' tenant). The app is provisioned into it by
    admin consent, and it becomes AzureAd:TenantId in the printed settings. Required.

.PARAMETER ServerTenantId
    The tenant that holds the server VM, its managed identity and the app registration (the
    server's tenant). The script signs in to it, and -SubscriptionId must belong to it. Required.

.EXAMPLE
    .\register-app.ps1 -UserTenantId <users-tenant-id> -ServerTenantId <server-tenant-id> -SubscriptionId <sub-id> -ResourceGroup rg-devconduit -VmName vm-devconduit -WhatIf

.EXAMPLE
    .\register-app.ps1 -UserTenantId <users-tenant-id> -ServerTenantId <server-tenant-id> -SubscriptionId <sub-id> -ResourceGroup rg-devconduit -VmName vm-devconduit

.EXAMPLE
    .\register-app.ps1 -UserTenantId <users-tenant-id> -ServerTenantId <server-tenant-id> -SubscriptionId <sub-id> -ResourceGroup rg-devconduit -VmName vm-devconduit -IncludeGroupClaims
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$SubscriptionId,

    [Parameter(Mandatory)]
    [string]$ResourceGroup,

    [Parameter(Mandatory)]
    [string]$VmName,

    [Parameter(Mandatory)]
    [string]$UserTenantId,

    [Parameter(Mandatory)]
    [string]$ServerTenantId,

    [string]$IdentityName = 'id-dev-conduit',

    [string]$DisplayName = 'Dev.Conduit',

    [string]$ServerBaseUrl = 'http://localhost:8091',

    [string]$ScopeName = 'DevConduit.Forward',

    # Name of the DevConduit:Remotes entry printed for a non-localhost -ServerBaseUrl.
    # Defaults to the first label of its host, for example vm-devconduit.
    [string]$RemoteName,

    [switch]$IncludeGroupClaims
)

$ErrorActionPreference = 'Stop'

$GraphAppId = '00000003-0000-0000-c000-000000000000'
$GraphUserReadScopeId = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'
$FederatedCredentialName = 'devconduit-vm-uami'
$TokenExchangeAudience = 'api://AzureADTokenExchange'
$PublicClientRedirectUri = 'http://localhost'
$GraphApplications = 'https://graph.microsoft.com/v1.0/applications'

# --- helpers ---------------------------------------------------------------------------

function Invoke-Az {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) { return $null }
        throw "az $($Arguments[0..1] -join ' ') failed (exit code $LASTEXITCODE). See the error above."
    }

    $text = ($output -join "`n").Trim()
    if (-not $text) { return $null }
    return $text | ConvertFrom-Json -Depth 50
}

function Invoke-GraphPatch {
    param(
        [Parameter(Mandatory)][string]$ObjectId,
        [Parameter(Mandatory)][hashtable]$Body
    )

    $file = Join-Path ([System.IO.Path]::GetTempPath()) "devconduit-app-$([guid]::NewGuid()).json"
    try {
        $Body | ConvertTo-Json -Depth 20 | Set-Content -Path $file -Encoding utf8NoBOM
        Invoke-Az @('rest', '--method', 'PATCH', '--url', "$GraphApplications/$ObjectId",
            '--headers', 'Content-Type=application/json', '--body', "@$file") | Out-Null
    }
    finally {
        Remove-Item -Path $file -ErrorAction SilentlyContinue
    }
}

function Write-Step {
    param(
        [Parameter(Mandatory)][ValidateSet('created', 'updated', 'unchanged', 'would create', 'would update', 'skipped')]
        [string]$Status,
        [Parameter(Mandatory)][string]$Subject,
        [string]$Detail
    )

    $colour = switch ($Status) {
        'created' { 'Green' }
        'updated' { 'Yellow' }
        'unchanged' { 'DarkGray' }
        default { 'Cyan' }
    }
    $line = '  {0,-13} {1}' -f "[$Status]", $Subject
    if ($Detail) { $line += " - $Detail" }
    Write-Host $line -ForegroundColor $colour
}

function Merge-List {
    param([object[]]$Existing, [string[]]$Required)

    $result = [System.Collections.Generic.List[string]]::new()
    foreach ($item in @($Existing)) { if ($item) { $result.Add([string]$item) } }
    foreach ($item in $Required) {
        if (-not ($result | Where-Object { $_ -ieq $item })) { $result.Add($item) }
    }
    return , $result.ToArray()
}

function Test-ListContains {
    param([object[]]$List, [string]$Value)
    return [bool](@($List) | Where-Object { $_ -ieq $Value })
}

# --- preflight -------------------------------------------------------------------------

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'The Azure CLI (az) was not found on PATH. Install it from https://aka.ms/installazurecli and re-run.'
}

$baseUri = $null
if (-not [Uri]::TryCreate($ServerBaseUrl.TrimEnd('/'), [UriKind]::Absolute, [ref]$baseUri)) {
    throw "-ServerBaseUrl '$ServerBaseUrl' is not an absolute URL."
}
if ($baseUri.Scheme -eq 'http' -and $baseUri.Host -ne 'localhost') {
    throw ("-ServerBaseUrl '$ServerBaseUrl' uses http on a host other than localhost. Entra accepts " +
        'plain-http redirect URIs only for localhost; use https for any other host.')
}
$serverBase = $baseUri.GetLeftPart([UriPartial]::Authority) + $baseUri.AbsolutePath.TrimEnd('/')
$webRedirectUri = "$serverBase/signin-oidc"
$logoutUrl = "$serverBase/signout-callback-oidc"

Write-Host "Provisioning Dev.Conduit in tenant $ServerTenantId" -ForegroundColor Cyan
if ($WhatIfPreference) {
    Write-Host '  -WhatIf: reading only, nothing will be written.' -ForegroundColor Cyan
}

$account = Invoke-Az @('account', 'show') -AllowFailure
if (-not $account -or $account.tenantId -ne $ServerTenantId) {
    Write-Host "  Signing in to tenant $ServerTenantId..." -ForegroundColor Cyan
    & az login --tenant $ServerTenantId --only-show-errors --output none
    if ($LASTEXITCODE -ne 0) { throw "az login to tenant $ServerTenantId failed." }
}

Invoke-Az @('account', 'set', '--subscription', $SubscriptionId) | Out-Null
$account = Invoke-Az @('account', 'show')
if ($account.tenantId -ne $ServerTenantId) {
    throw "Subscription $SubscriptionId belongs to tenant $($account.tenantId), not $ServerTenantId."
}
Write-Host "  Subscription: $($account.name) ($SubscriptionId)" -ForegroundColor DarkGray

# --- 1. user-assigned managed identity -------------------------------------------------

$vm = Invoke-Az @('vm', 'show', '--resource-group', $ResourceGroup, '--name', $VmName)

$identity = @(Invoke-Az @('identity', 'list', '--resource-group', $ResourceGroup)) |
    Where-Object { $_.name -ieq $IdentityName } |
    Select-Object -First 1

if ($identity) {
    Write-Step 'unchanged' "managed identity $IdentityName" "client id $($identity.clientId)"
}
elseif ($PSCmdlet.ShouldProcess("$ResourceGroup/$IdentityName", 'Create user-assigned managed identity')) {
    $identity = Invoke-Az @('identity', 'create', '--resource-group', $ResourceGroup,
        '--name', $IdentityName, '--location', $vm.location)
    Write-Step 'created' "managed identity $IdentityName" "client id $($identity.clientId)"
}
else {
    Write-Step 'would create' "managed identity $IdentityName" "in $ResourceGroup, $($vm.location)"
}

# --- 2. attach it to the VM ------------------------------------------------------------

if ($identity) {
    $attached = @()
    if ($vm.identity -and $vm.identity.userAssignedIdentities) {
        $attached = @($vm.identity.userAssignedIdentities.PSObject.Properties.Name)
    }

    if (Test-ListContains $attached $identity.id) {
        Write-Step 'unchanged' "VM $VmName" "identity $IdentityName attached"
    }
    elseif ($PSCmdlet.ShouldProcess("$ResourceGroup/$VmName", "Attach managed identity $IdentityName")) {
        Invoke-Az @('vm', 'identity', 'assign', '--resource-group', $ResourceGroup,
            '--name', $VmName, '--identities', $identity.id) | Out-Null
        Write-Step 'updated' "VM $VmName" "identity $IdentityName attached"
    }
    else {
        Write-Step 'would update' "VM $VmName" "attach identity $IdentityName"
    }
}
else {
    Write-Step 'would update' "VM $VmName" "attach identity $IdentityName"
}

# --- 3. app registration ---------------------------------------------------------------

$found = @(@(Invoke-Az @('ad', 'app', 'list', '--display-name', $DisplayName)) |
    Where-Object { $_.displayName -ceq $DisplayName })

if ($found.Count -gt 1) {
    $list = ($found | ForEach-Object { "    $($_.appId)  (object $($_.id))" }) -join "`n"
    throw "More than one app registration is named '$DisplayName':`n$list`nRename or delete the extras and re-run."
}

$app = $null
if ($found.Count -eq 1) {
    $app = Invoke-Az @('rest', '--method', 'GET', '--url', "$GraphApplications/$($found[0].id)")
    Write-Step 'unchanged' "app registration $DisplayName" "app id $($app.appId)"
}
elseif ($PSCmdlet.ShouldProcess($DisplayName, 'Create multi-tenant app registration')) {
    $created = Invoke-Az @('ad', 'app', 'create', '--display-name', $DisplayName,
        '--sign-in-audience', 'AzureADMultipleOrgs')
    $app = Invoke-Az @('rest', '--method', 'GET', '--url', "$GraphApplications/$($created.id)")
    Write-Step 'created' "app registration $DisplayName" "app id $($app.appId)"
}
else {
    Write-Step 'would create' "app registration $DisplayName" 'multi-tenant, then configure it'
}

if ($app) {
    $appId = $app.appId
    $identifierUri = "api://$appId"

    # Pass 1: everything but the self-referencing permission, which needs the scope to exist.
    $patch = @{}
    $changes = [System.Collections.Generic.List[string]]::new()

    if ($app.signInAudience -ne 'AzureADMultipleOrgs') {
        $patch.signInAudience = 'AzureADMultipleOrgs'
        $changes.Add('multi-tenant')
    }

    $web = $app.web
    if (-not (Test-ListContains $web.redirectUris $webRedirectUri) -or $web.logoutUrl -ne $logoutUrl) {
        $patch.web = @{
            homePageUrl           = $web.homePageUrl
            implicitGrantSettings = $web.implicitGrantSettings
            logoutUrl             = $logoutUrl
            redirectUris          = Merge-List $web.redirectUris @($webRedirectUri)
        }
        $changes.Add('web redirect and logout URLs')
    }

    if (-not (Test-ListContains $app.publicClient.redirectUris $PublicClientRedirectUri)) {
        $patch.publicClient = @{ redirectUris = Merge-List $app.publicClient.redirectUris @($PublicClientRedirectUri) }
        $changes.Add('public client redirect URI')
    }

    if ($app.isFallbackPublicClient -ne $true) {
        $patch.isFallbackPublicClient = $true
        $changes.Add('public client flows')
    }

    if (-not (Test-ListContains $app.identifierUris $identifierUri)) {
        $patch.identifierUris = Merge-List $app.identifierUris @($identifierUri)
        $changes.Add($identifierUri)
    }

    $scopes = @($app.api.oauth2PermissionScopes | Where-Object { $_ })
    $scope = $scopes | Where-Object { $_.value -ceq $ScopeName } | Select-Object -First 1
    if (-not $scope -or $scope.isEnabled -ne $true) {
        if ($scope) {
            $scope.isEnabled = $true
        }
        else {
            $scope = [pscustomobject]@{
                id                      = [guid]::NewGuid().ToString()
                value                   = $ScopeName
                type                    = 'User'
                isEnabled               = $true
                adminConsentDisplayName = 'Forward requests through Dev.Conduit'
                adminConsentDescription = 'Allows the app to forward HTTP requests through Dev.Conduit on behalf of the signed-in user.'
                userConsentDisplayName  = 'Forward requests through Dev.Conduit'
                userConsentDescription  = 'Allows the app to forward HTTP requests through Dev.Conduit on your behalf.'
            }
            $scopes += $scope
        }

        $patch.api = @{
            acceptMappedClaims          = $app.api.acceptMappedClaims
            knownClientApplications     = @($app.api.knownClientApplications | Where-Object { $_ })
            preAuthorizedApplications   = @($app.api.preAuthorizedApplications | Where-Object { $_ })
            requestedAccessTokenVersion = $app.api.requestedAccessTokenVersion
            oauth2PermissionScopes      = $scopes
        }
        $changes.Add("scope $ScopeName")
    }

    if ($IncludeGroupClaims -and $app.groupMembershipClaims -ne 'SecurityGroup') {
        $patch.groupMembershipClaims = 'SecurityGroup'
        $changes.Add('group claims')
    }

    if ($changes.Count -eq 0) {
        Write-Step 'unchanged' 'app configuration'
    }
    elseif ($PSCmdlet.ShouldProcess($DisplayName, "Configure: $($changes -join ', ')")) {
        Invoke-GraphPatch -ObjectId $app.id -Body $patch
        Write-Step 'updated' 'app configuration' ($changes -join ', ')
    }
    else {
        Write-Step 'would update' 'app configuration' ($changes -join ', ')
    }

    # Pass 2: required permissions - Graph User.Read for sign-in, and the app's own scope.
    $required = @(
        @{ resourceAppId = $GraphAppId; id = $GraphUserReadScopeId; label = 'Graph User.Read' },
        @{ resourceAppId = $appId; id = $scope.id; label = $ScopeName }
    )

    $access = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in @($app.requiredResourceAccess | Where-Object { $_ })) {
        $access.Add(@{
                resourceAppId  = $entry.resourceAppId
                resourceAccess = [System.Collections.Generic.List[object]]@(
                    $entry.resourceAccess | ForEach-Object { @{ id = $_.id; type = $_.type } })
            })
    }

    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($need in $required) {
        $resource = $access | Where-Object { $_.resourceAppId -eq $need.resourceAppId } | Select-Object -First 1
        if (-not $resource) {
            $resource = @{ resourceAppId = $need.resourceAppId; resourceAccess = [System.Collections.Generic.List[object]]::new() }
            $access.Add($resource)
        }
        if (-not ($resource.resourceAccess | Where-Object { $_.id -eq $need.id -and $_.type -eq 'Scope' })) {
            $resource.resourceAccess.Add(@{ id = $need.id; type = 'Scope' })
            $missing.Add($need.label)
        }
    }

    if ($missing.Count -eq 0) {
        Write-Step 'unchanged' 'required permissions'
    }
    elseif ($PSCmdlet.ShouldProcess($DisplayName, "Add required permissions: $($missing -join ', ')")) {
        Invoke-GraphPatch -ObjectId $app.id -Body @{ requiredResourceAccess = $access.ToArray() }
        Write-Step 'updated' 'required permissions' ($missing -join ', ')
    }
    else {
        Write-Step 'would update' 'required permissions' ($missing -join ', ')
    }

    # --- 4. federated identity credential ----------------------------------------------

    if ($identity) {
        $issuer = "https://login.microsoftonline.com/$ServerTenantId/v2.0"
        $desired = [ordered]@{
            name        = $FederatedCredentialName
            issuer      = $issuer
            subject     = $identity.principalId
            audiences   = @($TokenExchangeAudience)
            description = "Managed identity $IdentityName on VM $VmName"
        }

        $credentials = @(Invoke-Az @('ad', 'app', 'federated-credential', 'list', '--id', $appId))
        $named = $credentials | Where-Object { $_.name -eq $FederatedCredentialName } | Select-Object -First 1
        $equivalent = $credentials | Where-Object { $_.issuer -eq $issuer -and $_.subject -eq $identity.principalId } |
            Select-Object -First 1

        $file = Join-Path ([System.IO.Path]::GetTempPath()) "devconduit-fic-$([guid]::NewGuid()).json"
        try {
            $desired | ConvertTo-Json -Depth 5 | Set-Content -Path $file -Encoding utf8NoBOM

            if ($named -and $named.issuer -eq $issuer -and $named.subject -eq $identity.principalId -and
                (Test-ListContains $named.audiences $TokenExchangeAudience)) {
                Write-Step 'unchanged' "federated credential $FederatedCredentialName" "subject $($identity.principalId)"
            }
            elseif (-not $named -and $equivalent) {
                # Entra allows one credential per issuer and subject; one under another name already trusts the identity.
                Write-Step 'unchanged' "federated credential $($equivalent.name)" "already trusts $IdentityName"
            }
            elseif ($named) {
                if ($PSCmdlet.ShouldProcess($FederatedCredentialName, 'Update federated credential')) {
                    Invoke-Az @('ad', 'app', 'federated-credential', 'update', '--id', $appId,
                        '--federated-credential-id', $named.id, '--parameters', "@$file") | Out-Null
                    Write-Step 'updated' "federated credential $FederatedCredentialName" "subject $($identity.principalId)"
                }
                else {
                    Write-Step 'would update' "federated credential $FederatedCredentialName" "subject $($identity.principalId)"
                }
            }
            elseif ($PSCmdlet.ShouldProcess($FederatedCredentialName, 'Create federated credential')) {
                Invoke-Az @('ad', 'app', 'federated-credential', 'create', '--id', $appId,
                    '--parameters', "@$file") | Out-Null
                Write-Step 'created' "federated credential $FederatedCredentialName" "subject $($identity.principalId)"
            }
            else {
                Write-Step 'would create' "federated credential $FederatedCredentialName" "subject $($identity.principalId)"
            }
        }
        finally {
            Remove-Item -Path $file -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Step 'would create' "federated credential $FederatedCredentialName" 'once the managed identity exists'
    }

    # --- 5. service principal ----------------------------------------------------------

    $principal = @(Invoke-Az @('ad', 'sp', 'list', '--filter', "appId eq '$appId'")) | Select-Object -First 1
    if ($principal) {
        Write-Step 'unchanged' 'service principal' "object $($principal.id)"
    }
    elseif ($PSCmdlet.ShouldProcess($appId, 'Create service principal')) {
        $principal = Invoke-Az @('ad', 'sp', 'create', '--id', $appId)
        Write-Step 'created' 'service principal' "object $($principal.id)"
    }
    else {
        Write-Step 'would create' 'service principal'
    }
}
else {
    Write-Step 'would update' 'app configuration' 'after the app is created'
    Write-Step 'would create' "federated credential $FederatedCredentialName" 'after the app is created'
    Write-Step 'would create' 'service principal' 'after the app is created'
}

# --- summary ---------------------------------------------------------------------------

Write-Host ''
if (-not $app -or -not $identity) {
    Write-Host 'Run again without -WhatIf to create the missing objects; the IDs below are filled in then.' -ForegroundColor Cyan
}

$clientId = if ($app) { $app.appId } else { '<app id>' }
$identityClientId = if ($identity) { $identity.clientId } else { '<managed identity client id>' }

Write-Host "Scope: $ScopeName (clients request api://$clientId/$ScopeName)" -ForegroundColor Cyan
Write-Host '  The server must accept the same scope: Scope on its setup window''s Server page (AzureAd:Scopes),'
Write-Host '  blank meaning DevConduit.Forward. Each .conduit it exports then carries the scope to clients.'
Write-Host ''

Write-Host 'Next: consent in the users'' tenant.' -ForegroundColor Cyan
Write-Host '  A Cloud Application Administrator or Global Administrator of the users'' tenant opens:'
Write-Host "    https://login.microsoftonline.com/$UserTenantId/adminconsent?client_id=$clientId"
Write-Host '  After approving, the browser is sent back to localhost and that page may not load.'
Write-Host '  Consent is granted regardless.'
Write-Host ''
Write-Host 'Then: put these values in src\Proxylayer.DevConduit\appsettings.json.' -ForegroundColor Cyan
$settings = [ordered]@{
    AzureAd  = [ordered]@{
        TenantId          = $UserTenantId
        ClientId          = $clientId
        ClientCredentials = @(
            [ordered]@{
                SourceType              = 'SignedAssertionFromManagedIdentity'
                ManagedIdentityClientId = $identityClientId
            }
        )
    }
}
if ($ScopeName -ne 'DevConduit.Forward') {
    $settings.AzureAd.Scopes = @($ScopeName)
}
# Local mode inherits TenantId and ClientId from AzureAd; it only needs to know where the server is.
$serverHost = ([Uri]$ServerBaseUrl).Host
if ($serverHost -ne 'localhost') {
    $remote = if ($RemoteName) { $RemoteName }
              elseif ([System.Net.IPAddress]::TryParse($serverHost, [ref]$null)) { 'remote' }
              else { $serverHost.Split('.')[0] }
    $settings.DevConduit = [ordered]@{
        Remotes = [ordered]@{ $remote = $ServerBaseUrl.TrimEnd('/') }
    }
}
$settings | ConvertTo-Json -Depth 6 | Write-Host
Write-Host ''
Write-Host '  AzureAd:ClientSecret is no longer needed; remove it wherever it is set.' -ForegroundColor DarkGray
Write-Host '  Server mode on the VM uses the managed identity. Off the VM there is no identity, so' -ForegroundColor DarkGray
Write-Host '  server-mode sign-in fails there; local mode is unaffected.' -ForegroundColor DarkGray
