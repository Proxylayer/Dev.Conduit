# Dev.Conduit

Published by Proxylayer. Previously named POC.DevProxy; nothing is migrated from it (section 05.3).

A developer tool that runs on a machine with the network access you need, signs you in with
Microsoft Entra ID, and serves a request console at `http://localhost:8091`. You compose an
HTTP request in the browser; the server executes the outbound call from that machine and
returns the complete response — status, headers, timing, and body. It also relays existing
traffic transparently through `/route`, so code that already calls an address keeps calling
it and simply travels through Dev.Conduit on the way. Request history, routed traffic, and
reusable header sets are stored per user as JSON files on disk.

Use it when the endpoint you need is reachable only from a particular machine — a corporate
network, a VPN-joined box, an environment behind an allowlist — but you want to drive it from
your own workstation.

Closest previous-technology analogy: a desktop REST client such as Postman, except the
outbound call originates on the host running the service rather than on the workstation
holding the browser. The analogy is approximate: there is no collection format, no
environment substitution, and no scripting.

The same executable runs in two modes. Section 01 explains which one you want.

| Document | Covers |
| --- | --- |
| This file | Setup, configuration, the console, security posture |
| [docs/ROUTING.md](docs/ROUTING.md) | The `/route` forwarding contract: URLs, headers, streaming, capture |
| [docs/LOCAL-MODE.md](docs/LOCAL-MODE.md) | Running on the workstation: both ingress styles, `HTTP_PROXY`, Angular, sign-in |

---

## 01 The two modes

Dev.Conduit is one binary in two roles, chosen by `DevConduit:Mode` or the `--mode` argument.

| | Server mode | Local mode |
| --- | --- | --- |
| Runs on | The privileged machine | The developer workstation |
| Binds | `:8091` | Loopback only, `:8092` and `:8093` |
| Entra ID | **Validates** bearer tokens and console sessions | **Acquires** tokens with MSAL |
| Forwards to | The real upstream | The remote Dev.Conduit instance |
| Console UI | Served, behind sign-in | Served, no sign-in |
| Routed capture | On | Off by default |

```powershell
dotnet run --project src\Proxylayer.DevConduit                      # server, the default
dotnet run --project src\Proxylayer.DevConduit -- --mode local      # local
```

`--mode` overrides `DevConduit:Mode`; `DevConduit:Mode` overrides the `Server` default. Startup
prints a banner naming the mode, the bind address, and — in local mode — the remote endpoint
it will relay to.

**Server mode alone is enough.** Point a client at `http://<host>:8091/route/...` with a
bearer token and it works. Local mode exists to remove that requirement from the client: the
application talks plain HTTP to loopback with no token, no headers and no Dev.Conduit reference,
and the workstation-side process holds the Entra credential. That is what makes it work for
an Angular dev server, `curl`, a Java client, and a legacy executable alike.

> **Caution.** Local mode performs no inbound authentication, by design. It refuses to start
> bound to anything but a loopback address, and ignores any `Kestrel:Endpoints` in
> configuration so nothing else can bind. See [docs/LOCAL-MODE.md](docs/LOCAL-MODE.md).

---

## 02 Requirements

| Requirement | Detail |
| --- | --- |
| SDK | .NET SDK 10.0, target framework `net10.0-windows` |
| Runtime | Windows x64. The **MSI** (section 05.3) is self-contained and needs no .NET runtime. The **zip** is framework-dependent and needs both the **ASP.NET Core Runtime 10** and the **.NET Desktop Runtime 10** (x64); the desktop runtime is for the tray app's Windows Forms UI, and is needed even where the tray is never started |
| Directory | Two Entra ID tenants: the users' tenant and the server's tenant. Section 03 explains the split |
| Host | An Azure VM in the server's tenant for server mode, with a user-assigned managed identity attached |
| Provisioning | Azure CLI, for `register-app.ps1` |
| Front end | None to install. The console is plain HTML/CSS/JS served from `wwwroot` |

There is no Node.js, no npm, and no build step for the browser assets.

---

## 03 Register the application in Microsoft Entra ID

Two tenants are involved, and each holds a different half of the arrangement.

| Tenant | Example id | Holds |
| --- | --- | --- |
| The users' tenant | `00000000-0000-0000-0000-000000000001` | The user accounts. Users sign in from their own tenant, so it is the issuer of every token. |
| The server's tenant | `00000000-0000-0000-0000-000000000002` | The Azure VM that runs server mode, its user-assigned managed identity, and the app registration. |

The two may be the same tenant; the rest of this section assumes they are not.

The app registration has **no client secret**. A user-assigned managed identity (UAMI) in
the server's tenant is attached to the VM, and a federated identity credential on the app trusts it. At
sign-in, server mode asks the VM for a managed identity token and presents it to Entra as a
signed assertion in place of a secret. There is nothing to rotate and nothing to leak.

> **Concept.** Entra's rules fix the shape. Only a *user-assigned* managed identity can back a
> federated identity credential; a system-assigned one cannot. The identity and the app
> registration must live in the same tenant, so the registration lives in the server's tenant
> beside the VM. Serving users from a different tenant then requires a **multi-tenant** registration
> (`AzureADMultipleOrgs`) and admin consent in the users' tenant.

### 03.1 Provision with the script

`register-app.ps1` at the repository root performs the whole registration. It uses the Azure
CLI only, is idempotent, and supports `-WhatIf`. If the CLI is not already signed in to the
server's tenant, the script runs `az login --tenant` for you.

```powershell
.\register-app.ps1 -UserTenantId <users-tenant-id> -ServerTenantId <server-tenant-id> `
    -SubscriptionId <id> -ResourceGroup <rg> -VmName <vm> -WhatIf
.\register-app.ps1 -UserTenantId <users-tenant-id> -ServerTenantId <server-tenant-id> `
    -SubscriptionId <id> -ResourceGroup <rg> -VmName <vm>
```

| Parameter | Default | Meaning |
| --- | --- | --- |
| `-UserTenantId` | mandatory | The users' tenant: the tenant users sign in from, and the one the admin consent link points at. |
| `-ServerTenantId` | mandatory | The server's tenant: it holds the VM, the identity and the registration. `-SubscriptionId` must belong to it. |
| `-SubscriptionId`, `-ResourceGroup`, `-VmName` | mandatory | The VM in the server's tenant that runs server mode. |
| `-IdentityName` | `id-dev-conduit` | Name of the user-assigned managed identity. |
| `-DisplayName` | `Dev.Conduit` | Display name of the app registration. |
| `-ServerBaseUrl` | `http://localhost:8091` | Base of the Web redirect and logout URLs. `http` is refused for any host but localhost. |
| `-ScopeName` | `DevConduit.Forward` | The delegated scope `/route` requires. It must match the server's Scope (below). The summary prints it. |
| `-IncludeGroupClaims` | off | Emit group claims, for `AllowedGroupIds`. |

What it does, in order:

1. Creates the UAMI, or reuses it, and attaches it to the VM.
2. Finds or creates the multi-tenant app registration and converges it to the settings in
   section 03.2.
3. Creates or updates the federated credential `devconduit-vm-uami`, and ensures the service
   principal exists.
4. Prints the admin consent link and the `AzureAd` values the server needs.

**The scope.** The scope name the registration exposes and the scope the server accepts must be
the same. The server accepts `AzureAd:Scopes`, `DevConduit.Forward` unless set, entered as
Scope on the setup window's Server page. Clients do not configure it: every `.conduit` the
server exports carries the server's scope, and the import makes the workstation request
`api://<client-id>/<scope>`, so a client always asks for what the server accepts. A
registration made before the rename exposes `DevProxy.Forward`, and clients asking it for
`DevConduit.Forward` fail with `AADSTS65005`. To keep such a registration working, set Scope to
`DevProxy.Forward` on the Server page, save, restart the service, then export a new `.conduit` and
import it on each workstation. To move to the new name instead, add `DevConduit.Forward` to the
registration beside the old scope (running `register-app.ps1` again with
`-DisplayName <the registration's name>` adds it), have consent granted again, clear Scope, and
export again.

| Tenant | Roles the person running it needs |
| --- | --- |
| The server's tenant | Application Developer; Managed Identity Contributor and Virtual Machine Contributor on the resource group |
| The users' tenant | None to run it. An admin grants consent afterwards |

Then:

1. **Consent.** A Cloud Application Administrator or Global Administrator of the users' tenant
   opens the printed link once (the users' tenant id here is an example):

   ```text
   https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/adminconsent?client_id=<appId>
   ```

2. **Configure.** Enter the printed tenant id, client id and managed identity client id on the
   server's setup window (tray, Settings..., Server page), which writes them to the machine settings
   file. Section 04 shows their shape. Workstations get the ids from the server's `.conduit`
   (section 05.4) and need no configuring by hand.
3. **Sign in.** Users sign in with their own accounts in the users' tenant. No guest accounts
   are created in the server's tenant.

> **Caution.** The consent page redirects back to localhost afterwards, and that page may fail
> to load. Consent is granted regardless; the failed page is not a failed consent.

### 03.2 Manual reference

What the script converges the registration to, for reading or for repair by hand:

| Blade | Setting |
| --- | --- |
| Overview | Lives in the server's tenant. Supported account types: **Accounts in any organizational directory** (multi-tenant). |
| Authentication → Web | Redirect URI `http://localhost:8091/signin-oidc`; front-channel logout `http://localhost:8091/signout-callback-oidc`. |
| Authentication → Mobile and desktop applications | Redirect URI `http://localhost`, for local mode's interactive sign-in. |
| Authentication → Advanced | **Allow public client flows** = Yes (`isFallbackPublicClient`). Local mode's device-code sign-in needs it. |
| Certificates & secrets → Federated credentials | `devconduit-vm-uami`: issuer `https://login.microsoftonline.com/{ServerTenantId}/v2.0`, subject the UAMI's principal (object) id, audience `api://AzureADTokenExchange`. |
| Expose an API | Application ID URI `api://<client-id>`, scope `DevConduit.Forward` (or `-ScopeName`). |
| API permissions | Microsoft Graph `User.Read`, and `DevConduit.Forward` under **My APIs**. |
| Token configuration | Groups claim, only with `-IncludeGroupClaims`. |

Under *Implicit grant and hybrid flows*, ticking **ID tokens** is harmless but not required.

> **Concept.** The authorization code flow with `response_mode=query` is deliberate. It keeps
> the OIDC correlation and nonce cookies at `SameSite=Lax`, which is what makes sign-in work
> over plain `http` on localhost. That flow requires a client credential, which here is the
> managed identity assertion rather than a secret.

> **Caution.** The Web redirect is `http://localhost:8091/signin-oidc`, so browser sign-in to
> the server console works only on the VM itself. Workstations use local mode
> ([docs/LOCAL-MODE.md](docs/LOCAL-MODE.md)).

[docs/ROUTING.md](docs/ROUTING.md), section 5, covers what `/route` needs from the
registration.

---

## 04 Configure the application

A fresh install is blank. `src/Proxylayer.DevConduit/appsettings.json` carries logging levels and nothing
else: no Entra ids, no credential, no remotes and no routes. A server gets its values from the
setup window, which writes them to the machine settings file (section 04.4); a workstation gets
them by importing the server's `.conduit` (section 05.4). The `AzureAd` values have this
shape, wherever they are set:

```json
"AzureAd": {
  "TenantId": "00000000-0000-0000-0000-000000000001",
  "ClientId": "<the app registration's client id>",
  "ClientCredentials": [
    {
      "SourceType": "SignedAssertionFromManagedIdentity",
      "ManagedIdentityClientId": "<the UAMI's client id>"
    }
  ]
}
```

| Key | Value |
| --- | --- |
| `TenantId` | The users' tenant, and so the issuer of every token the server validates. The id above is an example. |
| `ClientId` | The multi-tenant app registration in the server's tenant. |
| `ClientCredentials` | The federated credential: a signed assertion from the VM's user-assigned managed identity. `ManagedIdentityClientId` is the identity's *client* id, not its principal id. |

Everything else in the section is optional and defaults to the public-cloud values, so set a
key only to change it:

| Optional key | Default |
| --- | --- |
| `Instance` | `https://login.microsoftonline.com/`, supplied by Dev.Conduit when absent. |
| `CallbackPath` | `/signin-oidc`. Must match the Web redirect URI on the registration exactly. |
| `SignedOutCallbackPath` | `/signout-callback-oidc`. |
| `Scopes` | `DevConduit.Forward`, the delegated scope a bearer token must carry to use `/route`. A list of plain scope names, never `api://` URIs; the first is written into every `.conduit` (section 05.4). The Server page's Scope field. |
| `ClientCredentials[].TokenExchangeUrl` | `api://AzureADTokenExchange`, the public-cloud token exchange audience. |

`register-app.ps1` prints these values with the real ids filled in. A directory id and an
application id are public identifiers rather than secrets — they travel in the query string of
every sign-in redirect — but they belong to one deployment, so none is committed. Neither is
usable without a credential, and the managed identity credential works only on the VM the
identity is attached to.

Startup logs which credential is in use, as `Entra client credential: …`. When none is
configured it logs a warning naming both options instead.

.NET reads these sources in order, each winning over the ones before it:

1. `appsettings.json`
2. `appsettings.{Environment}.json`, for example `appsettings.Development.json` in the `Development` environment
3. Machine settings, `%ProgramData%\Proxylayer\Dev.Conduit\settings.json`, read by every launch, the service included
4. User settings, `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\settings.json`, read by console and tray launches, never by the service
5. User secrets, in the `Development` environment only
6. Environment variables — `AzureAd__TenantId` and `AzureAd__ClientId`, double underscore
7. Command-line arguments — `--AzureAd:TenantId <id> --AzureAd:ClientId <id>`

The two settings files exist so that machine and personal settings survive an update, which
replaces every `appsettings*.json`. Section 04.4 describes them.

**Client secret fallback.** `AzureAd:ClientSecret` remains supported for running server mode
somewhere with no managed identity — a laptop, or any machine but the VM — where the managed
identity credential cannot work. Create a client secret on the registration. A usable
`ClientCredentials` entry takes precedence over `ClientSecret`, so the managed identity entry
must not be present where the secret is meant to be used. Never put the secret in
`appsettings.json`. In development, store it in user secrets:

```powershell
cd src\Proxylayer.DevConduit
dotnet user-secrets set "AzureAd:ClientSecret" "<the secret value>"
```

> **Caution.** This applies to the client secret fallback only; the managed identity
> credential needs no secret anywhere. User secrets load only in the `Development` environment.
> A published instance started without `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT` set to
> `Development` reads no secret at all, and sign-in then fails with an invalid-client error
> that says nothing about configuration. In production supply the secret as the environment
> variable `AzureAd__ClientSecret`, or from a key vault.

> **Caution.** While `TenantId` or `ClientId` look like placeholders — empty, starting with
> `<`, or containing `YOUR-` — the app logs a loud warning at startup and every request except
> `/health` answers `503` with the same instructions. It does not crash, so you get a readable
> page in the browser instead of a failed sign-in.

**Remotes and routes.** The settings files name the remote Dev.Conduit instances a workstation
relays to and the named routes both modes understand. A `.conduit` import (section 05.4)
writes both for a workstation; the server's own routes come from its setup window. The setup
window never edits remotes; it shows the imported ones and removes one on request. With one
remote, the result looks like this:

```json
"DevConduit": {
  "Remotes": { "uat": "https://devconduit.example.internal:8091" },
  "Routes": { "orders": "http://10.0.0.5:8080" }
}
```

`DevConduit:Remotes` maps a name to a remote's endpoint; local mode signs in for it with the
`AzureAd` identity. `DevConduit:Routes` maps a name to an upstream origin, reachable as
`/route/orders/...` in either mode. With one remote, every route goes to it. Several remotes are
written the same way; an entry that needs more than an endpoint becomes an object:

```json
"DevConduit": {
  "Remotes": {
    "uat":  "https://devconduit.example.internal:8091",
    "prod": {
      "Endpoint": "https://devconduit-prod.example.internal:8091",
      "ClientId": "<the prod app registration's client id>",
      "Targets": [ "*.prod.internal" ]
    }
  },
  "DefaultRemote": "uat",
  "Routes": {
    "orders":      "http://10.0.0.5:8080",
    "orders-prod": { "Target": "http://10.50.1.10:8080", "Remote": "prod" }
  }
}
```

Server mode reads only `DevConduit:Routes`, as its alias table, and ignores which remote a route
names. Sections 04.2 and 04.3 list every key.

### 04.1 `DevConduit` settings

| Setting | Default | Effect |
| --- | --- | --- |
| `DataRoot` | `%LOCALAPPDATA%\Proxylayer\Dev.Conduit`; on a server, `%ProgramData%\Proxylayer\Dev.Conduit` | Root folder for the JSON store, the Data Protection keys and the server's certificates. Environment variables are expanded. The server default applies in server mode when running as the service, or while the `Dev.Conduit` service is installed, so the service and a console `export` or `certs` on the same machine share one folder. An explicit value always wins. |
| `MaxHistoryEntries` | `500` | Entries retained per user. The oldest are pruned, and their files deleted, on write. |
| `MaxStoredBodyBytes` | `1048576` | Request and response bodies larger than this are truncated in storage and flagged. |
| `AllowInsecureTlsOptIn` | `true` | When `false`, the per-request *allow invalid certificate* option is refused. |
| `SecretHeaderNames` | see below | Header names always treated as secrets. |
| `BlockedEgressNetworks` | `169.254.0.0/16`, `168.63.129.16/32`, `fe80::/10` | CIDR networks outbound calls may never connect to. Configured entries are added to the defaults, never replace them. See section 08.5. |
| `AllowedGroupIds` | `[]` | Entra group object ids allowed in. Empty means any authenticated tenant user. |
| `AllowedUserOids` | `[]` | Entra user object ids allowed in. Empty means any authenticated tenant user. |
| `Server:Hostname` | `""` | Server mode. The name clients reach this server by. When set, the server serves HTTPS on every interface with a certificate it creates and rotates itself, and can export `.conduit` files. Blank on a server run as the service, or beside the installed service, means this computer's DNS name. Section 05.4. |
| `Server:Port` | `8091` | The HTTPS port for `Server:Hostname`. |
| `Server:ClientAddress` | `""` | The address an export tells clients to map `Server:Hostname` to: an IP address for their hosts file, or a name DNS resolves. `export --address` overrides it. |
| `Server:TrustStore` | `LocalMachine\Root` | Where the server trusts its own certificates (section 05.4): `LocalMachine\Root`, `None` to leave Windows trust alone, or another `Location\Name` store for testing. `CurrentUser\Root` is refused. |
| `Mode` | `Server` | `Server` or `Local`. The `--mode` argument overrides it. |
| `MaxRoutedHistoryEntries` | `5000` | Routed-traffic entries retained per user, kept apart from console history. |
| `MaxRoutedStoredBodyBytes` | `2097152` | Bytes of each routed body retained. |

Default secret header names: `Authorization`, `Cookie`, `Set-Cookie`, `Proxy-Authorization`,
`X-Api-Key`, `api-key`, `Ocp-Apim-Subscription-Key`, `X-DevConduit-Upstream-Authorization`,
`X-DevConduit-Local-Secret`.

`AllowedGroupIds` works only if the app registration emits group claims
(Token configuration → Add groups claim). `AllowedUserOids` needs no extra configuration.

With `DevConduit:Server:Hostname` set, the server listens on `https://0.0.0.0:{Port}` with its
app-managed certificate (section 05.4). A server run as the service, or a console run on a
machine where the service is installed, uses this computer's DNS name when no host name is set
(the rule the data root follows), so it serves HTTPS without configuration. An explicit `Kestrel:Endpoints` still wins over that:
the server then uses it as it is, logs a warning that the managed certificate is not used, and
does not serve `/cert`, and the computer's name is not used. Without a host name the listen address comes from Kestrel configuration
alone: `Kestrel:Endpoints`, or `urls` / `ASPNETCORE_URLS`. With none of them set, the server
listens on `http://localhost:8091`. A plain `http` listener on anything but loopback logs a prominent
warning, because tokens and proxied traffic would cross the network unencrypted. Local mode
ignores all of this and binds its own loopback listener. The `DevConduit` section no longer
has listen-address settings of its own; startup warns about any it still finds and ignores
them.

### 04.2 `DevConduit:Routes` and `DevConduit:Route` settings

These govern the `/route` forwarding endpoint in both modes.
[docs/ROUTING.md](docs/ROUTING.md) is the full reference.

`DevConduit:Routes` maps a route name to its upstream, either as a string or as an object:

| Form | Example | Effect |
| --- | --- | --- |
| String | `"orders": "http://10.0.0.5:8080"` | The name is the `/route/{name}/...` alias; the value is the upstream origin. |
| Object | `"orders-prod": { "Target": "http://10.50.1.10:8080", "Remote": "prod" }` | `Target` is the origin. `Remote` names the entry under `DevConduit:Remotes` that carries it in local mode; without it the default remote does. Server mode ignores `Remote`. |

Names are unique and case-insensitive. An object with `"Enabled": false` is left out entirely,
as if absent; that is how a settings file switches off a route `appsettings.json` defines
(section 04.4).

`DevConduit:Route` holds the endpoint's switches:

| Setting | Default | Effect |
| --- | --- | --- |
| `AliasesOnly` | `false` | `true` refuses `X-DevConduit-Target` and accepts route names only. |
| `ForwardCookies` | `false` | `true` lets non-Dev.Conduit cookies reach the upstream. Dev.Conduit's own cookies never do. |
| `TimeoutSeconds` | `100` | Per-request upstream timeout, clamped to `600`. |
| `AllowedTargets` | `[]` | Server mode. The hosts the server may call: host names, `*.` wildcards, IP addresses and CIDR networks, each with an optional `:port`. Every route's target is allowed besides. Empty allows any host not blocked. Section 08.9. |

### 04.3 `DevConduit:Remotes` and `DevConduit:Local` settings

Read only in local mode, and ignored entirely in server mode.
[docs/LOCAL-MODE.md](docs/LOCAL-MODE.md) is the full reference.

`DevConduit:Remotes` maps a remote name (case-insensitive) to the Dev.Conduit instance local mode
relays to. At least one is required. The value is the endpoint itself, or an object:

| Key | Default | Effect |
| --- | --- | --- |
| `Endpoint` | required | Base address of the remote Dev.Conduit instance, an absolute `http` or `https` URI. In the string form, the string is the endpoint. |
| `TenantId`, `ClientId` | `AzureAd:TenantId`, `AzureAd:ClientId` | The app registration this machine signs in against for this remote. Set them only to differ from the server's. |
| `Scopes` | `api://{ClientId}/DevConduit.Forward` | Scopes requested for this remote, from its effective client id. An import sets it from the `.conduit`'s scope when that is not the default (section 05.4). |
| `Targets` | `[]` | Host rules the forward proxy relays to this remote rather than contacting directly. Every route's origin is added as a `host:port` rule for that route's remote, so routed hosts need no entry here. |
| `Enabled` | `true` | `false` leaves the remote out entirely, as if absent. A settings file uses it to switch off a remote a lower file defines (section 04.4). |

`DevConduit:DefaultRemote` names the remote used when nothing more specific applies. It defaults
to the only remote when exactly one is configured. With several remotes and no default, every
route must name its `Remote`, and a header-targeted request that matches no route and no
`Targets` rule is answered `400` with `errorKind` `no_remote`. The setup window does not set a
default: its Routes page sends a route that names no server to the only one, and with several
it asks each route to name its server.

`DevConduit:Local` holds the workstation's own settings:

| Setting | Default | Effect |
| --- | --- | --- |
| `Port` | `8092` | Reverse proxy and console. Differs from `8091` so both modes can run on one machine. |
| `BindAddress` | `127.0.0.1` | Must be a loopback address; anything else is a fatal startup error. |
| `SharedSecret` | `""` | When set, proxy ingress requires `X-DevConduit-Local-Secret`. Set it in a settings file; the setup window does not show it. |
| `ForwardProxyEnabled` | `true` | Whether the raw `HTTP_PROXY` listener runs. |
| `ForwardProxyPort` | `8093` | Port for that listener. |
| `CaptureEnabled` | `false` | Whether local mode also records what it relays. |
| `CacheDirectory` | `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\msal` | Where the encrypted token cache lives, shared by every remote's sign-in. |
| `InteractiveFallback` | `true` | When `false`, sign-in skips the browser and goes straight to device code. |

`DevConduit:Route:Aliases`, `DevConduit:Local:RemoteEndpoint`, `DevConduit:Local:Targets` and
`DevConduit:Local:TenantId`, `ClientId` and `Scopes` have been removed. Startup warns about each
one it still finds and names its replacement; nothing is migrated automatically.

Local mode uses three ports in total: `8092` for the reverse proxy and console, `8093` for
the forward proxy, and `8091` on the remote machine it relays to.

### 04.4 Settings that survive an update

An update extracts a new zip over the folder, which replaces `appsettings.json` and every
`appsettings.{Environment}.json`. Anything set for one machine or one person therefore goes in
one of two optional files layered above them, which no update touches:

| File | Read by | Written by |
| --- | --- | --- |
| `%ProgramData%\Proxylayer\Dev.Conduit\settings.json` (machine) | every launch: console, tray and the service | the tray's setup window, through an elevated helper (section 05.2) |
| `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\settings.json` (user) | console and tray launches; never the service | the tray's setup window, directly |

Both use the `appsettings.json` shape and sit above every settings file in the application
folder (`appsettings.json`, `appsettings.{Environment}.json` and .NET's
`Dev.Conduit.settings[.{Environment}].json`), machine below user, so user secrets, environment variables and the command line still win over
both. A file that is absent or empty adds nothing. A file that is not a JSON object stops
startup with an error naming the file.

> **Caution.** The service reads the machine file as LocalSystem, and an ordinary user can
> create files in a folder under `%ProgramData%`. Every launch therefore refuses a machine file
> owned by anyone but Administrators, SYSTEM or TrustedInstaller, or that anyone else may write,
> with an error naming the file and the account. The setup window writes the file with its own
> access list: Administrators and SYSTEM full control, Users read.

Configuration merges files key by key, so a file can change or add a key but never delete one
a lower file defines. Three consequences, all handled by the setup window:

- `DevConduit:Remotes` and `DevConduit:Routes` are written as whole sections holding only the
  entries that differ from the lower files. An entry `appsettings.json` defines cannot be
  deleted from above; it is switched off with `"Enabled": false` instead.
- A list such as `Targets` or `AllowedUserOids` merges by index, so a shorter list written over
  a longer one is padded with empty entries, which every reader of those lists ignores.
- A value equal to what the lower files already give is removed from the settings file rather
  than written, so a later `appsettings.json` change still reaches it.

The window never edits the `appsettings*.json` files. Comments in a settings file are not
kept when the window saves it.

---

## 05 Build and run

```powershell
.\build.ps1                       # quiet build, pass/fail summary
.\build.ps1 -Detailed             # full MSBuild output when something is wrong
.\build.ps1 -Target Proxylayer.DevConduit  # build one project
.\build.ps1 -Configuration Release # build, zip a Release publish, build the MSI (section 05.3)
.\build.ps1 -Configuration Release -Version 1.2.0  # the same, as version 1.2.0
.\build.ps1 -Configuration Release -NoInstaller    # the zip without the MSI
.\build.ps1 -NoPackage            # build only, skip the zip and the MSI

dotnet run --project src\Proxylayer.DevConduit                  # server mode, the default
dotnet run --project src\Proxylayer.DevConduit -- --mode local  # local mode
```

A successful build of the solution also publishes the app from that build output and zips it to
`artifacts\Dev.Conduit-<Configuration>.zip` (Debug by default). The zip is framework-dependent:
extract it on a Windows machine with the ASP.NET Core Runtime 10 and the .NET Desktop Runtime 10
(section 02) and run `Dev.Conduit.exe` from the extracted folder. Its `appsettings.json` is
blank; settings live in the settings files of section 04.4. Building a single project with `-Target` other than
`Proxylayer.DevConduit` skips the zip.

**The UAT server.** The VM's own values live in the machine settings file, entered on the
setup window's Server and Routes pages: the Entra ids and managed identity credential, the
routes, `DevConduit:Server:Hostname` (`devconduit.example.internal`; left blank it would be the
VM's own DNS name) and `ClientAddress`. The server creates and rotates its HTTPS certificate
itself and adds it to the VM's own `LocalMachine\Root` (section 05.4). Its data root is `%ProgramData%\Proxylayer\Dev.Conduit` without any setting, because
the service is installed there (section 04.1), so a console `export` or `certs` run on the VM
finds the service's certificates. No environment name is needed. Because the host name is set,
the server's `http://localhost:8091` default does not apply on the VM. Local mode never reads
`DevConduit:Server` and binds loopback only.

In server mode, browse to `http://localhost:8091`. The first request redirects to Entra ID;
after sign-in you land on the console.

In local mode, browse to `http://127.0.0.1:8092`. There is no sign-in: the console is
loopback-only and the Entra credential is used outbound, not inbound. A sign-in prompt may
appear at startup instead, once per machine, to fill the token cache.

To publish a runnable folder:

```powershell
.\deploy.ps1                        # -> .\publish, framework-dependent
.\deploy.ps1 -OutputPath D:\tools\devconduit
```

`deploy.ps1` prints the exact run instructions when it finishes.

### 05.1 Run as a Windows service

On the server the published folder can run as a Windows service, `Dev.Conduit` (display
name *Dev.Conduit*), so it starts with the machine and restarts on failure. From an elevated
prompt in the extracted folder:

```powershell
.\Dev.Conduit.exe install                    # register the service (Production environment)
.\Dev.Conduit.exe start
.\Dev.Conduit.exe stop
.\Dev.Conduit.exe uninstall
.\Dev.Conduit.exe help                       # every install option
```

| Concern | Behaviour |
| --- | --- |
| Mode | Server mode only. Local mode needs an interactive sign-in, which a service cannot show, so a service whose configuration says `local` logs an error and stops. |
| Environment | Optional. `-environment:<name>` stores `DOTNET_ENVIRONMENT=<name>` in the service's own registry entry, which picks `appsettings.<name>.json`; without it the service runs as Production. Nothing in the package needs another environment. |
| Configuration | `appsettings.json` and `appsettings.<Environment>.json` from the exe's folder, the machine settings file (section 04.4), then environment variables. Never the user settings file. The service passes no command-line settings to the app. |
| Account | LocalSystem by default. Topshelf's own `-username:` / `-password:`, `--localservice` or `--networkservice` install options choose another; that account then needs full control of `{DataRoot}\certs`, where the app-managed certificate lives (section 05.4). |
| Data | `%ProgramData%\Proxylayer\Dev.Conduit` by default, the server default of section 04.1; `DevConduit:DataRoot` overrides it. |
| Start | Automatic, delayed. Restarted one minute after a failure; the failure count resets daily. |
| Logs | The Application event log, source `Dev.Conduit`: this application's messages from Information, the framework's from Warning. `install` registers the source. |

To update, stop the service, extract the new zip over the folder, and start it again. The
service and its environment stay registered; the zip's `appsettings*.json` replace the
folder's, as for any update, while the machine settings file is left as it is.

While the MSI of section 05.3 is installed, in either role, `install` refuses with a message and
a non-zero exit code before touching the service control manager; `uninstall` refuses the same
way only while the MSI is installed in its Server role. `start`, `stop` and `help` still work.

Every other command line, `.\Dev.Conduit.exe` and `.\Dev.Conduit.exe --mode local --Some:Key
value` included, runs in the console exactly as before.

### 05.2 Tray app and setup window

The same exe runs as a notification-area app:

```powershell
.\Dev.Conduit.exe tray
```

One tray runs per user; starting it again opens the running tray's setup window. Started from
a terminal it detaches and hands the prompt back. It plays one of two roles, decided every few
seconds and shown at the top of its menu:

| Role | When | What the tray does |
| --- | --- | --- |
| Server | the `Dev.Conduit` service is installed | Shows the service's state, polled every 3 seconds; Start, Stop and Restart it; Open console (`https://<Server:Hostname>:<Server:Port>/`, with this computer's name when no host name is set, unless an explicit HTTPS endpoint overrides it); Open Event Log, filtered to source `Dev.Conduit`; Export client configuration... (as on the Server page, below); Uninstall service..., greyed while the MSI installed the service (below). Exit leaves the service running. |
| Workstation | no service is installed | Runs local mode inside the tray process: Start and Stop local proxy, Open local console (`http://127.0.0.1:8092`), Copy proxy address (`http://127.0.0.1:8093`), View log (the last 2,000 lines, with copy and clear), Start with Windows, Start proxy automatically. Each start rebuilds local mode, so saved settings apply. When an imported server's new certificate is pinned but not yet in `LocalMachine\Root`, or this computer's client certificate for it is due for renewal, a notification says so once and the menu shows Update Dev.Conduit certificates (needs admin)..., which runs `service-admin trust` (UAC) for each server that needs it. Without the MSI, Install service... registers the service, which turns the tray into the Server role. Exit stops it cleanly. |

The icon is drawn in the status colour: green running, grey stopped, amber starting, signing in
or needing attention (local mode that has logged an error since it started), red failed. The
tooltip repeats the role and status. Local mode's sign-in works as it does from a console,
because the tray runs in the signed-in user's session. When local mode refuses to start, a
notification gives the first problem and opens the setup window when clicked; the full list
is in the log.

**Start with Windows** writes `"<exe>" tray` to the `Dev.Conduit` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The exe is a console program, so a
console window flashes briefly as the tray starts at sign-in. A Workstation install from the
MSI (section 05.3) writes the same value under `HKLM` for every user; the item is then shown
ticked and greyed, and the tray removes its own `HKCU` value when it starts the same exe, so
the tray is not started twice.

**Start proxy automatically**, ticked by default, starts the local proxy without a click: when the
tray starts, and when an import, a removal or a Routes or Local proxy save leaves it stopped (or
failed). It does so only when the configuration is ready: at least one server imported, nothing
that would stop local mode starting (the Routes page lists those problems), and Entra ids for at
least one server to sign in with. A running proxy is offered a restart after a save, as before. An
automatic start that fails, for example because a port is in use, shows the failed status and one
notification, and is not retried until the next save or tray start. Unticking it writes
`DevConduit:Tray:AutoStart: false` to the user settings file; ticking it removes the key again.
The Server role never starts anything itself.

**Settings...** opens the setup window. It shows one set of pages per role, saves to the
settings files of section 04.4 and never to `appsettings*.json`, and has nothing about
certificates: the server manages its own, and a workstation gets the server's from its
`.conduit`. A workstation never sees server settings.

Each page saves only its own keys (`ManagedSettingKeys`), and after any save, import or removal
every page reads the files again. The page that saved shows the files as they now are; every
other page keeps what was typed into it and not yet saved, over the files as they now are, so
saving the Routes page never clears unsaved edits on the Server page, and the reverse. Each save
also merges into the file as it is at that moment rather than as it was when the window opened.
Revert, on the Routes and Local proxy pages, drops a page's unsaved edits.

| Role | Page | Shows and edits | Saves to |
| --- | --- | --- | --- |
| Workstation | Servers | One row per imported server: name, host name, address (from the hosts file, else the import), the server certificate's status in words (*Trusted until 2027-11-20*, *Update ready (needs admin)*, *Not trusted*), the local proxy's last sync, and how many routes go to it. With none, it says to import a `.conduit` from your Dev.Conduit server. Buttons: Import .conduit... (the file is checked here, then imported by `service-admin import`; afterwards the tray offers to restart the local proxy, or starts it if it is stopped and Start proxy automatically is ticked), Remove (`service-admin forget <hostname>`: the remote, its routes, its client folder, its hosts line and the certificates this app added to Windows trust for it; your own pins and renewed certificate for it, and your own routes for it, are then deleted too), and Update certificates, shown only when Windows does not yet trust a pinned certificate or this computer's client certificate is due for renewal (`service-admin trust <hostname>`, which asks the server itself). *Update ready (needs admin)* stands for either. | machine settings, through the elevated helper |
| Workstation | Routes | Name, target and server (a drop-down of the imported servers). Routes from an import are grey until changed; a change is saved to your own file as the difference only, and deleting one switches it off (`"Enabled": false`). With one server a route naming none goes to it; with several each route names one. Every edit is checked with the parser a launch uses. | user settings |
| Workstation | Local proxy | Console port, forward proxy on or off and its port, capture. | user settings |
| Server | Server | Host name, port (8091), client address (what an export offers clients), tenant ID, client ID, managed identity client ID, scope (blank is `DevConduit.Forward`; it must match the app registration, and an export hands it to clients, section 03.1), allowed users and groups (one per line), allowed targets (one per line, section 08.9). Blank text fields inherit the value shown greyed in them. A line in orange warns while the settings as typed leave the server open: *Every user in the tenant can use this server. Add allowed users or groups to restrict.* with no allowed users or groups, and *Any reachable host is allowed. Add allowed targets to restrict.* with no allowed targets. A blank host name shows this computer's name greyed, *(this computer's name — saved only if you change it)*: it is what the service uses, and it is written to the file only once typed in. Buttons: Save (administrator), Export client configuration... (a save dialog for `<hostname>.conduit` in Documents, an address prompt filled from the client address, then `service-admin export`; the file holds a client certificate, so share it only with people who should use the server), Copy settings from .conduit... (another server's `.conduit`, checked as strictly as an import; it shows the tenant, client, scope and route count it would write and asks first, then `service-admin seed`; section 05.4), Restart service. | machine settings |
| Server | Routes | Name and target: the routes the server forwards and an export hands to clients. | machine settings |
| Both | About | The role, the version, the install folder, the two settings files and whether each exists, and the data folder. | |

Everything else (`DevConduit:Route` switches, `DataRoot`, `Kestrel` endpoints, a remote's
`Targets` or scopes, `SharedSecret`) stays configurable in the settings files by hand.

**Administrator actions.** The tray runs unelevated. Service control, install, uninstall,
saving machine settings, seeding a server's settings from a `.conduit`, and importing, removing
and trusting servers start the exe again elevated (`service-admin <action>`), which shows a UAC prompt; declining it does nothing. To
save machine settings the tray writes the change to a new file with a random name in its temp
folder; the elevated helper accepts only a file named that way, checks that it touches only keys
the setup window manages (`ManagedSettingKeys.Machine`), applies it to
`%ProgramData%\Proxylayer\Dev.Conduit\settings.json` with the access list of section 04.4, and deletes
it. Its targets are fixed: the service is always `Dev.Conduit` and the file always the machine
settings file; `forget` and `trust` take a lower-case DNS host name and nothing else. After a save
or an import the tray offers to restart the service or the local proxy, which read their
settings only when they start.

**When the MSI owns the service.** Install service and Uninstall service are in the tray menu
(Workstation and Server role respectively), not in the setup window. While the MSI of section
05.3 is installed, in either role, it alone installs the service: the Workstation menu has no
Install service item, and the elevated helper refuses `install` with exit code 12
(`ManagedByInstaller`), whoever starts it. Only the Server role installs the service, so only it
removes it too:

| MSI | Service | Install service | Uninstall service | Tooltip on Uninstall service |
| --- | --- | --- | --- | --- |
| Server role | present | greyed | greyed; the helper's `uninstall` exits 12 | *Installed by the Dev.Conduit MSI. Use Settings → Apps (or msiexec) to change or remove it.* |
| Server role | missing | greyed | greyed | *The Dev.Conduit MSI installs this service. Repair it from Settings → Apps (or msiexec) to put the service back.* |
| Workstation role, or no role recorded | present | greyed | allowed | *This service was not installed by the Dev.Conduit MSI, which is installed in its Workstation role. You can uninstall it here; to run a server, reinstall the MSI with ROLE=Server.* |
| Workstation role, or no role recorded | missing | greyed | greyed | *The Dev.Conduit MSI is installed in its Workstation role. To run the service, install the MSI with ROLE=Server.* |

A Workstation-role MSI installs no service, so one present there came some other way (a zip
folder's `install`, or the tray before the MSI) and can be removed with the menu item, the
helper or `.\Dev.Conduit.exe uninstall`. An installation with no recorded role counts as
Workstation, the MSI's default. Restart service, the tray menu's Start, Stop and Restart, and
saving settings work as before in every case. The tray installs the service with the Production
environment; it never shows or changes an environment name.

### 05.3 Install with the MSI

`.\build.ps1 -Configuration Release` also builds a Windows Installer package from a
self-contained `win-x64` publish, so the target machine needs no .NET runtime:

| Artifact | Contents |
| --- | --- |
| `artifacts\Dev.Conduit-<version>.msi` | the package |
| `artifacts\Dev.Conduit-<version>.msi.sha256` | its SHA-256 (lower-case hex, two spaces, the file name), which the updater checks a download against (section 05.5) |
| `artifacts\Dev.Conduit-<version>-installer.zip` | the package and `INSTALL.txt`, the instructions below for whoever installs it |

The version is `<Version>` in `src\Proxylayer.DevConduit\Proxylayer.DevConduit.csproj` (the exe's file version,
and through it the MSI's product version); `-Version` overrides it for one build. The package
is built by `installer\Proxylayer.DevConduit.Installer.wixproj` with WiX v5, restored from NuGet by
`dotnet build`; nothing needs installing on the build machine. It is not in the solution, so a
plain solution build does not need the self-contained publish.

It installs per machine into `C:\Program Files\Proxylayer\Dev.Conduit`, with Proxylayer as the
publisher, in one of two roles chosen at install time:

| Role | Installs |
| --- | --- |
| `Workstation` (default) | The tray, started at sign-in for every user (`Dev.Conduit` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`), and a Start menu shortcut, *Dev.Conduit*. No service. Optionally, a server's `.conduit` imported during the install (`CONDUITCONFIG`, below); without one the install is blank and the tray's Servers page asks for a file. The last page of an interactive install offers to start the tray, unelevated, as the signed-in user. |
| `Server` | The `Dev.Conduit` service through the MSI's own service tables, with the behaviour of section 05.1: LocalSystem, automatic delayed start, restart a minute after a failure with the count reset daily, `DOTNET_ENVIRONMENT=<ENVIRONMENT>` in the service's own environment (Production unless given), and the `Dev.Conduit` event log source. `%ProgramData%\Proxylayer\Dev.Conduit`, the service's data root, is created with its own access list (Administrators and SYSTEM full control, Users read and execute, not inherited). A Start menu shortcut, *Dev.Conduit*, opens the tray that manages the service. |

```powershell
# interactive: choose the role on the Role page, and for Workstation optionally a .conduit
.\Dev.Conduit-1.0.0.msi

# silent, from an elevated prompt
msiexec /i Dev.Conduit-1.0.0.msi ROLE=Server /qn
msiexec /i Dev.Conduit-1.0.0.msi ROLE=Workstation /qn
msiexec /i Dev.Conduit-1.0.0.msi ROLE=Workstation CONDUITCONFIG="C:\path\devconduit.example.internal.conduit" /qn

msiexec /x Dev.Conduit-1.0.0.msi /qn   # uninstall
```

`ENVIRONMENT` is for silent installs only and is not on the Role page. It defaults to
`Production`, sets the service's `DOTNET_ENVIRONMENT`, and follows the elevated helper's rule:
letters, digits, dot, dash and underscore, at most 64 characters. Anything else, or a `ROLE`
other than the two above, stops the install with a message before anything is written.

**`CONDUITCONFIG`** is the full path of a `.conduit` file (or a legacy `.proxyconfig` file) to
import during a Workstation install or upgrade: typed on the Role page or chosen with its Browse
button, or given on the command line. It is checked before anything is installed: it must be a
Workstation install, a full path on a local disk (not a share or a mapped network drive, which
the import, running as LocalSystem, cannot read), a `.conduit` or `.proxyconfig` name, and an
existing file. Anything else stops the install with the reason. The property was named
`PROXYCONFIG` before the rename; this package reads `CONDUITCONFIG` only, with no alias. Once
the files are in place the install runs `"<install folder>\Dev.Conduit.exe" import "<file>" --machine`
quietly as LocalSystem, a deferred custom action that does exactly what section 05.4's import
does: the hosts entry, Windows trust, the pinned certificates and client identity under
`%ProgramData%\Proxylayer\Dev.Conduit\client`, and the remote and routes in the machine settings
file. Its output goes to the install log (`msiexec ... /l*v install.log`); a file it refuses, for example
an expired one or one for another format, fails the install, which rolls back. The file is
checked before the import changes anything. `CONDUITCONFIG` is not remembered, so an upgrade or
repair imports again only when it is given again.

| Concern | Behaviour |
| --- | --- |
| Upgrade | Run the newer MSI, or let the updater do it (section 05.5). It replaces the installed version in place, stopping the service while its files change and starting it again. Without `ROLE`, `ENVIRONMENT` or `INSTALLFOLDER` on the command line it reuses the ones recorded under `HKLM\SOFTWARE\Proxylayer\Dev.Conduit` (values `Role`, `Environment` and `InstallFolder`), so an install to another folder is upgraded where it is. A package of the same version replaces the installed one; an older one refuses to install. Versions before the `InstallFolder` value was added did not record the folder: an upgrade from one of those to a folder other than the default needs `INSTALLFOLDER="<folder>"` once; after that it is remembered. |
| Uninstall | Removes the program files, the shortcut, the `HKLM` Run value, the service, its event log source and `HKLM\SOFTWARE\Proxylayer\Dev.Conduit`. Keeps `%ProgramData%\Proxylayer\Dev.Conduit` (machine settings, the server's data root and certificates, a workstation's imported servers) and every user's `%LOCALAPPDATA%\Proxylayer\Dev.Conduit`. A server's own certificates stay in `LocalMachine\Root` until they expire (a year at most); to remove them, run `Dev.Conduit.exe certs untrust` before uninstalling (section 05.4). |
| POC.DevProxy installed | Dev.Conduit is a new product with its own UpgradeCode. It does not upgrade or remove POC.DevProxy and migrates none of its settings or data. Its package detects POC.DevProxy's UpgradeCode only, and while that product is installed the install stops with *POC.DevProxy is installed. Uninstall it first (Settings > Apps), then install Dev.Conduit and import your .conduit file.* |
| First start of the service | The service is started at the end of the install without waiting for it, so a server without its certificate or settings yet does not roll the install back. Until they are in place it logs why it stopped to the Application event log. |
| Still manual | Server: enter the settings on the tray's Server and Routes pages (Entra ids, managed identity, client address, routes; the host name defaults to this computer's name), or copy the Entra ids and routes from another server's `.conduit` with Copy settings from .conduit..., and restart the service; it creates its own certificate and trusts it on this machine. Then export a `.conduit` for the workstations. Workstation: import the server's `.conduit` if the install did not (the tray's Servers page, or section 05.4). |

The zip of section 05 is still built alongside, for machines that already have the runtimes.

**The MSI owns the service.** While the package is installed (in either role) installing the
service is the MSI's: the tray offers no Install service, and the elevated helper and
`Dev.Conduit.exe install` refuse (section 05.2); a server comes from running the MSI again with
`ROLE=Server`. In the Server role, which installs the service, changing and removing it are the
MSI's too, and the tray's Uninstall service, the helper's `uninstall` and `Dev.Conduit.exe
uninstall` refuse as well. In the Workstation role (or with no role recorded, which counts as
Workstation) a service present was installed some other way, and those three remove it. Start,
stop and restart, and the settings, stay with the tray. To change the role, run the MSI again
with `ROLE=`. A service registered from a zip folder before the MSI came must be removed before
a Server install (`.\Dev.Conduit.exe uninstall`), since the Server role installs the service itself.

### 05.4 Server certificate and client configuration (.conduit)

A server with a host name owns its TLS certificate. Nothing about certificates appears in
settings. Clients get everything they need from one file the server exports, `<hostname>.conduit`.

**The host name.** `DevConduit:Server:Hostname` when it is set. Otherwise, for a server run as the
service or a console run on a machine where the service is installed (the rule the data root
follows, section 04.1), this computer's DNS name: its fully qualified name when it has a primary
DNS suffix or a domain, else its computer name, in lower case. The managed HTTPS endpoint, its
certificate, the tray's console link and exports all use it, so a fresh server needs no host name
to come up or to export. A console run on a developer machine without the service keeps
`http://localhost:8091`, and an explicit `Kestrel:Endpoints` still overrides. The computer's
name need not resolve from the clients: an export carries the client address, and the import maps
the name to it in the client's hosts file. Set `DevConduit:Server:Hostname` (the Server page) when
clients should use another name; changing it issues a new certificate and clients must import a
new `.conduit`.

**Windows trust on the server.** The server adds its own current and next certificates to the
server's `LocalMachine\Root`, so a browser on the server, and the tray, trust
`https://<hostname>:<port>`. Each is checked first against the rules a client applies
(self-signed, CA=false, server authentication, naming exactly the host name). The thumbprints it
adds are recorded in `{DataRoot}\certs\root-store.json`; when a certificate is promoted, replaced
or expires, or the host name changes, the ones it added are removed, and nothing it did not add
is ever touched. The service runs as LocalSystem and can write the store; a console server run
unelevated cannot, logs one warning and carries on. `DevConduit:Server:TrustStore` set to `None`
turns this off. Uninstalling the MSI cannot take them out of the store: they stay until they
expire, unless `certs untrust` (below) removes them first.

**The server's certificate.** Self-signed, ECDSA P-256, end-entity only (CA=false), server
authentication only, with the host name as its one subject alternative name, valid for a year.
Windows trust and Schannel accept ECDSA P-256. Kestrel takes the certificate through a selector
on every handshake, so a rotation needs no restart.

| When | What happens |
| --- | --- |
| First start, or no certificate yet | A certificate is issued for the host name. |
| Less than 60 days left | The **next** certificate is issued and published through `/cert` beside the current one. Clients pin it at their next sync. |
| Less than 30 days left, or expired, or not valid yet | The server switches to the next certificate. Every client that synced in the 30 days between already has it pinned. |
| The host name changed | A new certificate is issued at once, and a new client authority with it (below), with a warning in the log: clients pinned to the old certificate cannot validate the new one and must import a new `.conduit`. |
| Checked | On start and every 12 hours. |

A second, private **client authority** (ECDSA P-256, CA=true, path length 0, certificate signing
only, 10 years, named for the host) signs the client certificates and nothing else. It never
leaves the server and is never installed in any certificate store. When the host name changes, or
the authority expires, a new authority replaces it: every enrollment the old one signed is
revoked (reason `hostname changed`, or `client authority expired`), the log says plainly that
every client must import a new `.conduit`, and the old authority's key is deleted at once.
All that remains of it is its thumbprint and host name on its enrollments in `issued.json`, so
`certs list` can report them, until they are dropped with their last certificate.

Everything lives in `{DataRoot}\certs`: each certificate and key as a PKCS#12 blob encrypted with
machine-scope DPAPI, `index.json` naming the current, next and authority certificates, and
`issued.json` recording every **enrollment** (id, subject, host name, the authority that signed
it, when it was created, and whether, when and why it was revoked) and every client certificate
issued (serial, subject, expiry, issue time, enrollment, `revoked`, the serial a renewal replaced
it with). Records are kept a year past expiry. The folder gets the access list of the Data
Protection key folder: SYSTEM, Administrators and the account the server runs as, nothing
inherited (section 08.7). On a server `{DataRoot}` is `%ProgramData%\Proxylayer\Dev.Conduit` unless set
(section 04.1), for the service and the commands below alike. A command run before the server
ever started finds no certificate folder and says so at once.

**Enrollments.** Each export creates an enrollment: a random id of 32 hex digits, recorded in
`issued.json` and carried in the client certificate itself as its subject's one organizational
unit (`CN=<hostname> client <date>, OU=<enrollment>`), so `/cert` reads it from the certificate.
A renewal stays in its enrollment: it must ask for exactly the enrollment's subject. Revoking
revokes the whole enrollment, every certificate in it, those issued before and any a renewal
would issue after.

**Export.** On the server, from the tray (Server page or menu, Export client configuration...),
or from an elevated prompt (the certificate folder is for administrators):

```powershell
.\Dev.Conduit.exe export                                  # .\<hostname>.conduit, address from DevConduit:Server:ClientAddress
.\Dev.Conduit.exe export --out D:\handover --address 10.20.30.40
```

The tray reaches the same command elevated as `service-admin export <out.conduit> [address]`.
Export runs only in server mode with a host name (configured, or this computer's name as above)
and the `AzureAd` ids set, and without any `Kestrel:Endpoints` URL; it needs an address from `--address` or
`DevConduit:Server:ClientAddress`. The tray checks the host name and the ids before it asks for
elevation, and says to fill them in on the Server page. The output is checked before anything is
issued: it must be a `.conduit` file name in an existing folder, and not a folder or a link
(`--out` may name a folder from the command line; the default file name goes into it). Each
export then creates an enrollment and issues its client certificate, and writes the file to a
temporary file in the target folder that is moved into place, so a failure leaves no partial
file; if the write fails, the enrollment it just created is revoked (`the export could not be
written`). The file is UTF-8 JSON:

| Field | Holds |
| --- | --- |
| `format` | `dev-conduit/1`. An import also accepts `poc-devproxy/1`, the same schema as written by POC.DevProxy. |
| `hostname`, `address`, `port` | The server's name, what clients map it to (an IP address goes into the hosts file; a name must resolve through DNS), and its HTTPS port. |
| `remote` | The remote name a client gives the server: the host name's first label. |
| `tenantId`, `clientId` | The Entra tenant and app registration clients sign in with. |
| `scope` | The delegated scope the server accepts, its first `AzureAd:Scopes` entry (`DevConduit.Forward` unless set): a plain name of letters, digits, `.`, `-` and `_`, at most 120 characters. Optional: files exported before it was carried have none and still import, and their clients use the default. |
| `routes` | The server's `DevConduit:Routes`, name to upstream origin. |
| `serverCertificates` | The current and next server certificates, DER, base64. |
| `clientCertificate` | A client certificate **with its private key**, PKCS#12 without a password, base64. |
| `exportedAt` | When it was written. |

> **Caution.** The file is a secret: it carries a private key. It is written readable only by
> the account that exported it and Administrators; hand it over the way you would a password.
> What a stolen copy allows is narrow: calling `/cert` as that enrollment, which returns public
> server certificates, and renewing within the last 60 days of a certificate. It grants no
> sign-in, no token, and no use of `/route` or the console, which still require Entra ID. Every
> certificate it leads to stays in its enrollment, so one revoke ends all of them. Revoke it when
> in doubt.

**Import.** On a workstation:

```powershell
.\Dev.Conduit.exe import .\devconduit.example.internal.conduit            # asks for elevation (UAC)
.\Dev.Conduit.exe import .\devconduit.example.internal.conduit --machine  # from an elevated prompt, in this process
```

The file is checked first, unelevated and strictly: the format, a host name every server
certificate names, server certificates that are self-signed, CA=false and for server
authentication, a client certificate with its key and the client authentication usage, Entra
ids, routes that are a name and an `http` or `https` origin, and a scope, when there is one, that is a plain scope name. Unknown fields are refused.
A `.proxyconfig` file exported by POC.DevProxy (format `poc-devproxy/1`) imports the same way;
exports always write `.conduit` with `dev-conduit/1`. Then, idempotently:

| Step | Effect |
| --- | --- |
| Hosts entry | One line `<address>\t<hostname>\t# Dev.Conduit` is added or replaced in `%SystemRoot%\System32\drivers\etc\hosts`. No other line is touched: each keeps its own line ending (CRLF or LF), a UTF-8 byte order mark stays if there was one, ANSI and non-ASCII bytes pass through unchanged, and whether the file ended with a line ending is kept, so removing the line later gives back the original bytes. The new line uses the file's usual line ending and is ASCII only; a host name or address that is not is refused. An earlier unmanaged line for the same name is reported, since it wins. An address that is a name rather than an IP is not written. The file is replaced in one step, keeping its access list, with retries while another process holds it. |
| Windows trust | The server certificates go into `LocalMachine\Root`. Their thumbprints are recorded in `%ProgramData%\Proxylayer\Dev.Conduit\client\root-store.json`; later imports and `trust` remove the ones this app added that expired or were replaced, and never anything it did not add. |
| App trust | The server certificates are also **pinned** in the machine layer, `%ProgramData%\Proxylayer\Dev.Conduit\client\<hostname>\trust.json`, so the relay works without administrator rights even before Windows trust has a new certificate. |
| Client identity | `identity.pfx` beside it, encrypted with machine-scope DPAPI. The folder gives Administrators and SYSTEM full control and Users read, nothing inherited: every user's unelevated sync authenticates with this identity, and only an elevated or SYSTEM process writes here. |
| Settings | In the machine settings file: the remote, named by the file, with `Endpoint` `https://<hostname>:<port>`, and `TenantId` and `ClientId` only where they differ from `AzureAd` (blank `AzureAd` ids are filled from the file instead), and `Scopes` set to `["api://<clientId>/<scope>"]` when the file's scope is not `DevConduit.Forward`. Re-importing changes only those keys on an existing remote: its `Targets` and anything else you added stay. A file with the default scope removes only a `Scopes` an import wrote, so the default applies; a file with no scope leaves `Scopes` as it is. The routes are merged in with `Remote` set to that remote, replacing routes an earlier import from the same server added (recorded in `client\imports.json`) and routes already naming that remote. A route name another remote uses is left alone and reported. |

Several servers' files coexist: each becomes its own remote, folder and hosts line. Restart the
local proxy afterwards; it reads remotes when it starts. The tray's Servers page reaches the
import elevated as `service-admin import <transfer>`, with the file copied to a randomly named
transfer file first, as for a settings save. A Workstation install of the MSI can run the same
import as LocalSystem (`CONDUITCONFIG`, section 05.3); nothing in it depends on the signed-in user.

**Two layers of client trust.** What the import wrote is the **machine layer**. Each user's
local proxy keeps what it learns from the server afterwards in that user's own **user layer**,
`%LOCALAPPDATA%\Proxylayer\Dev.Conduit\client\<hostname>\`, which only that user's unelevated sync writes:

| Layer | Where | Written by | Access | Holds |
| --- | --- | --- | --- | --- |
| Machine | `%ProgramData%\Proxylayer\Dev.Conduit\client\<hostname>\` | Import, `service-admin trust` and `forget`: elevated or SYSTEM | Administrators and SYSTEM full control, Users read, nothing inherited | `trust.json`, the imported pins and those `trust` added; `identity.pfx`, the imported client certificate or `trust`'s renewal of it, machine-scope DPAPI |
| User | `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\client\<hostname>\` | That user's local proxy sync | The user profile's own list; checked when the folder is created, and replaced by one for the user, SYSTEM and Administrators alone if it would let anyone else read it | `trust.json`, the pins learned from `/cert`; `identity.pfx`, the renewed client certificate, user-scope DPAPI |

A user's relay trusts the pins of both layers together. It authenticates to `/cert` with the
user's renewed certificate while that is valid and belongs to the same enrollment as the
machine's, and with the machine's otherwise, so a renewal from an older `.conduit` is set
aside once a new one is imported. Without the machine identity there is none: the user layer
alone never authenticates.

Users can read the machine identity by design, and machine-scope DPAPI lets any local account
decrypt it: that is what lets every user's sync work without elevation. Its power is the
narrow one above: it fetches public server certificates, and renews in the last 60 days of a
certificate, in its own enrollment, which one revoke ends.

**Remove.** The Servers page's Remove runs `service-admin forget <hostname>` elevated, the reverse
of an import: the remote whose endpoint names the server, every route naming that remote and
every route its import added, the server's machine-layer folder, its hosts line, the certificates
this app recorded adding to Windows trust for it, and its import record. Nothing this app did
not write is touched, and the `AzureAd` ids stay, since other servers may share them. The tray
then deletes the current user's layer for the server and drops the routes in the user settings
file that still point at the server. Other users' layers are theirs; without the machine layer
nothing uses them, and Remove on their own tray deletes them.

**Keeping current.** Local mode (console or tray) syncs each remote imported with a client
identity on start, every 12 hours, and on request after an import. It calls `GET /cert` over
HTTP/1.1 with the effective client certificate, accepting the server only if Windows trusts it or
its certificate is pinned for that host in either layer; pins new server certificates in the
user layer and drops expired ones there; and, with less than 60 days left on its client
certificate, generates a new key, renews through `POST /cert/renew` and stores the renewal in the
user layer. It never writes the machine layer. Two things only an administrator can settle are
flagged and logged: a pinned certificate missing from `LocalMachine\Root`
(`ClientTrustStatus.RootStoreUpdateNeeded`), which the relay itself does not need; and a machine
identity inside its last 60 days (`ClientTrustStatus.MachineIdentityRenewalDue`), which this
user's relay does not need either, having renewed its own, but which new users on the workstation
and `service-admin trust` itself start from. Either one makes `ClientTrustStatus.UpdateNeeded`
true. The tray then says so once in a notification and offers Update Dev.Conduit certificates (needs
admin)... in its menu and Update certificates on the Servers page, both of which run
`service-admin trust <hostname>` elevated. Per-user renewals stay automatic; one admin prompt
about once a year keeps the machine identity and Windows trust current.

`service-admin trust` takes its certificates only from a source it can trust while elevated,
never from the user layer, which its user controls. It calls `GET /cert` itself, authenticated
with the machine identity and accepting the server only by Windows trust or the machine pins, at
`https://<hostname>:<port>/` with the port of the machine settings file's remote; checks each
published certificate against the server certificate rules for that host (self-signed, CA=false,
server authentication, naming exactly the host); adds the new ones to the machine pins and to
`LocalMachine\Root`; and removes the expired ones this app added. It fails without changing
anything when the server cannot be reached or refuses the machine identity. Then, when the machine
identity has less than 60 days left, it generates a new key, asks `POST /cert/renew` for a
certificate with the same subject, again as the machine identity, checks the answer (same subject,
client authentication, the new key, a later expiry) and replaces `identity.pfx` in the machine
layer in one step, machine-scope DPAPI. A refused renewal (`409` when the server's clock says it is
not due yet, `403` when the enrollment was revoked) or any other failure keeps the current
identity; the pins and Windows trust stay updated, and the helper ends with exit code 20
(`IdentityRenewalFailed`), which the tray reports. A machine folder an earlier build left with a
broader access list (Users allowed to modify it, say) gets the protected Administrators and SYSTEM
full control, Users read list again first; an import resets it as well. `trust` and `forget`
accept the host name in any case and lower-case it.

**Relay trust.** For a remote Dev.Conduit, local mode accepts a certificate Windows already trusts,
or one whose thumbprint is pinned for that host in either layer, inside its validity period and
naming the host. Nothing else is accepted, and hosts nothing was imported for are validated by
Windows alone.

**`/cert` and mTLS.** `/cert` is internal: not part of the API in section 09 and not used by the
console. The HTTPS endpoint uses Kestrel's `DelayCertificate` client certificate mode, so the
TLS handshake never asks for a client certificate and a browser opening the console is never
prompted; `/cert` asks for one when the request arrives, which needs HTTP/1.1 (TLS 1.3
post-handshake authentication, or TLS 1.2 renegotiation). The certificate must chain to the
client authority alone, carry client authentication, be in its validity period, name its
enrollment, and have a serial recorded in `issued.json` under that enrollment, which must not be
revoked. Anything else is a bare `401` or `403`. `/cert` sits outside Entra sign-in and is rate
limited to 30 requests a minute per client address.

`POST /cert/renew` checks everything again under the store's lock and needs all of:

| Check | Refused with |
| --- | --- |
| The presented certificate is accepted as above: chain, client authentication, validity period, its enrollment not revoked | `403` |
| Fewer than 60 days remain on it, by the rule the client uses (`ClientCertificateRenewal`) | `409` |
| The request names exactly the enrollment's subject, with a new P-256 key | `400` |

The new certificate joins the enrollment. The presented one is marked superseded and stays valid
until it expires: it still authenticates `GET /cert`, and may renew again within its own window.
That is deliberate: several users of one workstation renew from the same machine identity, so
one certificate may be renewed more than once. Revoking the enrollment is the control that ends
them all.

**Revoke.**

```powershell
.\Dev.Conduit.exe certs list                          # every enrollment, newest first, with its latest certificate and state
.\Dev.Conduit.exe certs revoke <serial>               # elevated; revokes the serial's whole enrollment
.\Dev.Conduit.exe certs revoke --enrollment <id>      # elevated; the same, by enrollment id
```

The export prints the enrollment and serial it issued. `certs list` prints one line per
enrollment (id, created, `active` or `revoked <date> (<reason>)`, subject, and the retired
authority and host name when the old client authority signed it), then its latest certificate
and how many it holds. The tray reaches revoke as `service-admin revoke <serial>` or
`service-admin revoke --enrollment <id>`. A revoked enrollment can no longer sync or renew, from
any certificate in it; it keeps working against the server until a certificate rotation it has
not pinned, and must then import a new file.

**Remove the server's own certificates from Windows trust.**

```powershell
.\Dev.Conduit.exe certs untrust     # asks for elevation (UAC) through service-admin untrust
```

Removes every certificate the server recorded adding to its `LocalMachine\Root`, and nothing
else. A running service adds the ones in use back at its next check, so run it when retiring a
server: just before uninstalling the Server role, or with the service stopped.

**Seed a server from a `.conduit`.** A new server can copy its Entra settings and routes from
another server's export, from the Server page (Copy settings from .conduit...) or:

```powershell
.\Dev.Conduit.exe import .\devconduit.example.internal.conduit --seed-server   # asks for elevation (UAC)
```

The file, `.conduit` or legacy `.proxyconfig`, is checked as strictly as an import. Only the
machine settings file changes, and only `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:Scopes`
and `DevConduit:Routes` (`ManagedSettingKeys.Seed`): the file's routes become this server's, name to
target with no remote, replacing the routes it had. The file's scope becomes this server's
(`DevConduit.Forward` removes the key, so the default applies); a file with no scope leaves it alone. The host name, port and client address stay
this server's own; no hosts entry, pin, client identity or Windows trust is written; the file's
certificates and client certificate are ignored. The managed identity client id is not in a
`.conduit`, so set it on the Server page if the server uses one. The tray shows the tenant, client,
scope and route count before asking for elevation, and reaches the helper as
`service-admin seed <transfer>`.

---

### 05.5 Updates

An MSI installation keeps itself up to date from the **GitHub Releases** of
`https://github.com/Proxylayer/Dev.Conduit`. Only published releases count: a draft (which is how
the release workflow creates every release, section 05.6) is invisible until someone publishes it,
and a prerelease counts only with `IncludePrerelease`. The package, the per-machine install, the
service and the `%ProgramData%` layout stay exactly as section 05.3 describes; the updater only
runs the next MSI silently with the role, environment and install folder the MSI recorded.

Only a build the MSI installed is ever updated: the MSI must be installed, must have recorded its
install folder, and the running `Dev.Conduit.exe` must be in that folder. A development build, a
zip folder or a console run logs *update checks are disabled (not an installed build)* and does
nothing else.

| `DevConduit:Update` setting | Default | Meaning |
| --- | --- | --- |
| `FeedUrl` | `https://github.com/Proxylayer/Dev.Conduit` | The repository whose releases are the feed. **Empty turns updates off.** Only `https://github.com/<owner>/<repository>` is accepted. |
| `CheckIntervalHours` | `1` | Hours between checks after the one at startup; `0` checks at startup only. |
| `AutoInstall` | `true` | Install a verified release without asking. The tray's *Install updates automatically* item and the About page's check box write it: to the user settings file on a workstation, to the machine settings file on a server. |
| `InstallHour` | `3` | The server's quiet hour, local time (0 to 23): the service installs in the hour starting then. |
| `IncludePrerelease` | `false` | Count prereleases too. |

**The feed.** One anonymous `GET https://api.github.com/repos/<owner>/<repository>/releases/latest`
per check, with a generic `User-Agent: Dev.Conduit-Updater/<version>` and nothing that identifies
the user or machine. The answer's ETag is sent back as `If-None-Match`, so an unchanged feed costs a
304, which GitHub does not count against the anonymous limit of 60 requests an hour. When the limit
is reached anyway, no request is made until the reset time GitHub gave. Being offline is logged and
tried again at the next interval. A newer release is logged at Information once; everything else
at Debug. The release must carry `Dev.Conduit-X.Y.Z.msi` and `Dev.Conduit-X.Y.Z.msi.sha256`, named
exactly so, and be tagged `vX.Y.Z`; versions compare on major.minor.patch.

**Verification.** A downloaded package is written under a temporary name, verified, and only then
kept as `Dev.Conduit-X.Y.Z.msi`; one that fails a check is deleted and the reason logged. Only the
latest package is kept. Every package must pass all of these, in the tray or service that downloads
it and again in the elevated helper that installs it:

| Check | Rule |
| --- | --- |
| Checksum | Its SHA-256 equals the release's `.msi.sha256` asset. |
| Signature | When the running `Dev.Conduit.exe` is Authenticode-signed, the package must carry a signature Windows trusts (WinVerifyTrust, revocation checked) from the same subject. So once builds are signed, an unsigned package, or one signed by anyone else, is refused, which also stops a downgrade from signed to unsigned builds. An unsigned running build skips this check. |
| Package | Its Property table, read read-only through the Windows Installer API, names Dev.Conduit's UpgradeCode and ProductName `Dev.Conduit`, and a ProductVersion newer than the installed version (and equal to the release's). |

**Server role.** The service (LocalSystem) checks at startup and every interval, downloads into
`%ProgramData%\Proxylayer\Dev.Conduit\updates` (SYSTEM and Administrators only, not inherited, like
the key folder) and, with `AutoInstall` on, waits for the next quiet hour. Then it logs to the
Application event log and starts `msiexec /i "<msi>" /qn /norestart /l*v "<updates>\install-X.Y.Z.log"`
with `ROLE`, `ENVIRONMENT` and `INSTALLFOLDER` from the recorded values, detached so it outlives the
service, which the package stops while it replaces the files. When the service starts again it
logs *Dev.Conduit was updated from A to B*, or, when the old version is still the one running, the
failure with msiexec's exit code (read from the log) and the log's path; it retries that version no
sooner than a day later, at the quiet hour. The service writes what it knows to
`%ProgramData%\Proxylayer\Dev.Conduit\update-status.json`, which the tray on the server shows on
its About page; the tray there never installs updates, and its *Check for updates* only looks.

**Workstation role.** The tray checks 20 seconds after it starts and every interval, and downloads
and verifies a newer release in the background into `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\updates`.
With *Install updates automatically* on, it then hands the package to the elevated helper
(`service-admin update`), which shows **one UAC prompt**:

1. The tray copies the package to a random-named transfer file and starts the helper.
2. The helper copies it into the restricted `%ProgramData%` update folder, makes every check again on
   that copy (with the feed named by the machine's own settings, never the user's), and waits for
   the tray to exit.
3. The tray starts a hidden, unelevated relauncher and exits, stopping local mode cleanly.
4. The helper starts msiexec silently and exits itself: it is the same exe msiexec replaces, so it
   must not be running while msiexec does.
5. The relauncher waits for the helper and then for the msiexec it started, and starts
   `"<install folder>\Dev.Conduit.exe" tray` again, which says whether the update installed.

If the UAC prompt is declined, the tray stays as it is and that version becomes an offer until the
tray next starts: *Install update vX.Y.Z (needs admin)...* in the menu and one balloon, never
another prompt. With the switch off the same offer appears without a prompt. Balloons appear once
per version.

**In the tray.** The menu has *Check for updates*, *Install update vX.Y.Z (needs admin)...* (on a
workstation, when one is ready) and *Install updates automatically*. Settings > About shows the
running version, the feed, the last check, the available version and its state, with *Check for
updates* and the same switch.

**Turning updates off.** Set `DevConduit:Update:FeedUrl` to an empty string in the machine settings
file (or, for one user's tray, in that user's settings file). To keep checking but install by hand,
turn *Install updates automatically* off.

**Logs.**

| Where | What |
| --- | --- |
| Application event log, source `Dev.Conduit` | the service's checks, downloads, refusals, the install it starts and its outcome |
| the tray's *View log...* | the workstation tray's checks, downloads, refusals and handoffs |
| `%ProgramData%\Proxylayer\Dev.Conduit\updates\install-X.Y.Z.log` | msiexec's verbose log of each install (administrators can read it) |
| `%ProgramData%\Proxylayer\Dev.Conduit\updates\update-helper.log` | what the elevated helper refused or started, one line each |

**When an update fails.** The running version stays; nothing is half-installed, since msiexec rolls
a failed install back. Read the `install-X.Y.Z.log` (search for `Return value 3` or the
`MainEngineThread is returning` line) or `update-helper.log`. A refused package means it failed a
check: look for the reason, for example a checksum mismatch from a damaged download (the next check
downloads it again) or a release signed by someone else. The updater tries the same version again
after a day; to install at once, download the MSI from the release page and run it as in section
05.3, which also keeps the recorded role, environment and folder.

### 05.6 Releasing

Releases are built by `.github/workflows/release.yml` on GitHub's Windows runners:

1. Tag the commit `vX.Y.Z` and push the tag (or run the workflow by hand with a version).
2. The workflow builds and tests, runs `.\build.ps1 -Configuration Release -Version X.Y.Z`, signs
   when signing secrets are configured, writes a CycloneDX SBOM, attests build provenance for the
   MSI and the exe (`actions/attest-build-provenance`), and creates a **draft** release with
   `Dev.Conduit-X.Y.Z.msi`, `Dev.Conduit-X.Y.Z.msi.sha256`, `Dev.Conduit-X.Y.Z-installer.zip` and
   `sbom.json`.
3. Review the draft: the notes, the assets, and (optionally) the provenance with
   `gh attestation verify Dev.Conduit-X.Y.Z.msi --repo Proxylayer/Dev.Conduit`.
4. Publish it. Only then does the updater see it; within the hour every tray and server finds it.

`.github/workflows/pr.yml` builds and tests every pull request to `main` and uploads the TRX test
results. Tests that depend on this machine's state skip themselves on a runner.

**Signing (optional).** Unsigned releases work: every release still carries its build provenance
attestation and checksum. Once releases are signed, keep signing every one: installed signed
builds refuse an unsigned package. The workflow signs `Dev.Conduit.exe` and the custom action DLL
before the MSI is built, and the MSI after, with an RFC 3161 timestamp, choosing by which
repository secrets exist:

| Mode | Secrets |
| --- | --- |
| Azure Trusted Signing (preferred) | `AZURE_TRUSTED_SIGNING_ENDPOINT` (for example `https://eus.codesigning.azure.net`), `AZURE_TRUSTED_SIGNING_ACCOUNT`, `AZURE_TRUSTED_SIGNING_PROFILE`, and an app registration allowed to sign with the profile (the *Trusted Signing Certificate Profile Signer* role): `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`. Signs through the dotnet `sign` tool. |
| A code signing certificate | `SIGN_PFX_BASE64` (the `.pfx` file, base64) and `SIGN_PFX_PASSWORD`. Signs with `signtool`. |
| neither | unsigned |

Locally, `.\build.ps1 -Sign signtool` takes `SIGN_PFX_PATH` and `SIGN_PFX_PASSWORD`, or
`SIGN_CERT_THUMBPRINT` for a certificate in a certificate store; `.\build.ps1 -Sign trusted-signing`
takes the `AZURE_TRUSTED_SIGNING_*` variables and signs in as `DefaultAzureCredential` does (the
`AZURE_*` client variables, or an Azure CLI login). `SIGN_TIMESTAMP_URL` overrides the timestamp
server. `global.json` pins the .NET 10 SDK the builds use.

## 06 Using the console

| Area | Behaviour |
| --- | --- |
| Composer | Method, including custom verbs, URL, and **Send** — or `Ctrl`/`Cmd`+`Enter`. |
| Headers tab | Key/value rows with an enable checkbox, a `SEC` secret toggle, a `SHOW` reveal toggle, and a delete button. A blank row appears as you type. |
| Body tab | Raw body plus a content type, with a *Format JSON* button. |
| Options tab | Timeout in seconds, follow redirects, allow invalid TLS certificate. |
| Saved header sets | Apply a set into the current headers, save the current headers as a new set, mark a set *apply by default* so it pre-populates on page load, or delete it. |
| History | Newest first, with method badge, URL, status chip, and relative time. Click an entry to load it back into the composer; delete one entry or clear them all. |
| Response | Status chip, duration, size, body with a pretty/raw toggle for JSON and a copy button, plus the full header table. Binary payloads are reported by size rather than rendered. |

Redirects are not followed by default, so 3xx responses are visible as received.

### 06.1 Colour meaning

The console follows the house technical-document palette. Colour carries meaning and nothing
else:

| Colour | Meaning |
| --- | --- |
| Cyan | Structure, current focus, active tab, selected history entry, 1xx and 3xx responses |
| Green | 2xx responses |
| Amber | 4xx responses, and a header marked as a secret |
| Red | 5xx responses, transport errors, and destructive controls |

---

## 07 Where data is stored

```text
{DataRoot}\users\{userId}\headers.json
{DataRoot}\users\{userId}\history\index.json
{DataRoot}\users\{userId}\history\{entryId}.json
{DataRoot}\users\{userId}\routed\index.json
{DataRoot}\users\{userId}\routed\{entryId}.json
{DataRoot}\keys\key-{id}.xml
{DataRoot}\certs\index.json, issued.json, root-store.json, *.pfx.dpapi     server mode with a host name
```

`{DataRoot}` is `%LOCALAPPDATA%\Proxylayer\Dev.Conduit`, or `%ProgramData%\Proxylayer\Dev.Conduit` on a server, unless
`DevConduit:DataRoot` says otherwise (section 04.1); the tray's About page shows the one in use.

`{userId}` is the `oid` claim from the signed-in principal, validated against
`^[A-Za-z0-9._-]{1,128}$` before it is ever used as a path segment. In local mode there is no
Entra principal, so it is the fixed pseudo-user `local`. `index.json` holds newest-first
summaries; each entry file holds the complete request and response.

Traffic forwarded through `/route` is kept in `routed\`, with its own index and its own caps,
so it can never evict console history.

Writes go to a `.tmp` file and are then moved over the target, and writes for a given user are
serialised, so a crash cannot leave half a file behind.

`keys\` holds the ASP.NET Core Data Protection key ring, which signs and encrypts the sign-in
cookie and the sign-in correlation state. It lives under `DataRoot` rather than in the running
account's profile, under the fixed application name `Dev.Conduit` rather than one derived
from the install folder, so a password reset, a different service account or a zip extracted
to a new folder no longer invalidates it. Startup logs the directory and how the keys are
protected; see section 08.7. Deleting `keys\` signs everyone out once and is otherwise
harmless: a new key is created on the next start.

`certs\` holds the server's app-managed certificates, its client authority and the record of
issued client certificates (section 05.4). Deleting it issues a new server certificate and a new
authority on the next start, so every client must import a new `.conduit`.

A workstation keeps what imports bring under `%ProgramData%\Proxylayer\Dev.Conduit\client`, the machine
layer, and what each user's local proxy learns afterwards under that user's
`%LOCALAPPDATA%\Proxylayer\Dev.Conduit\client`, the user layer (section 05.4, section 08.8).

Settings written by the tray's setup window live in the two `settings.json` files of section
04.4, not under `DataRoot`.

Updates (section 05.5) keep the latest downloaded package and the install logs in
`%ProgramData%\Proxylayer\Dev.Conduit\updates` (SYSTEM and Administrators only) and, for a
workstation's tray, `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\updates`; a server's service writes its
update status to `%ProgramData%\Proxylayer\Dev.Conduit\update-status.json`. All of it can be deleted.

Nothing is sent anywhere else. Delete the folder to wipe the tool's state.

---

## 08 Security posture

Read this section before running the tool anywhere but your own machine.

### 08.1 What the tool is

It executes arbitrary outbound HTTP requests on behalf of whoever is signed in, from this
machine, with this machine's network access. That is the purpose of the tool and also its main
risk: anyone who can reach it and sign in can reach whatever it can reach, including internal
endpoints and management interfaces. Cloud instance metadata services are the exception:
they are refused by default, because on the VM they hand out the app's own credential. See
section 08.5. `DevConduit:Route:AllowedTargets` narrows the rest to the hosts you name; see
section 08.9.

> **Caution.** In server mode, keep it on localhost unless you mean to expose it. Exposing it
> takes a `Kestrel:Endpoints` entry (or `urls`) on another address; use `https` when you do,
> as the UAT endpoint does. A plain `http` listener beyond loopback logs a prominent warning.
> In local mode there is no such option: a non-loopback `BindAddress` is a fatal startup
> error and configured Kestrel endpoints are ignored, because local mode authenticates
> nothing on the way in.

### 08.2 Access control

| Control | Behaviour |
| --- | --- |
| Sign-in | A global fallback authorization policy covers every endpoint and every static asset. Only `/health` is anonymous: it is answered ahead of sign-in, so it returns `200` even before any Entra id is configured. In local mode every request is authenticated as the fixed `local` principal instead, and loopback is the boundary. |
| Narrowing | `AllowedGroupIds` and `AllowedUserOids` restrict access further in server mode. With both empty every user the tenant signs in may use the server: startup logs a warning saying so, and the Server page shows it. In local mode, `DevConduit:Local:SharedSecret` narrows proxy ingress to callers holding the secret. |
| Forwarding | `/route` requires a bearer token carrying the server's scope (`AzureAd:Scopes`, `DevConduit.Forward` unless set), or the console session. See [docs/ROUTING.md](docs/ROUTING.md). |
| CSRF | Every `/api` call must carry `X-DevConduit-Client: 1`, enforced by an endpoint filter. The auth cookie is `SameSite=Lax`, `HttpOnly`, `SecurePolicy=SameAsRequest`. A cross-site form post or image request cannot set a custom header, so it cannot drive the API with your session. |

### 08.3 Secret handling

A header is secret when you flag it or when its name matches `SecretHeaderNames`. Secret values
*are* stored in saved header sets — that is what makes the sets reusable — but they are written
to history as the literal `***redacted***`, so no live token lands in a history file. In the
console they render masked, with a reveal toggle.

### 08.4 Cookies on forwarded requests

A browser attaches every cookie it holds for the Dev.Conduit origin to a `/route` call,
`DevConduit.Auth` among them. Forwarding that verbatim would hand each upstream a replayable
Dev.Conduit session, so:

- Dev.Conduit's own authentication and antiforgery cookies are stripped from any forwarded
  `Cookie` header, in both modes, with no setting that turns it off.
- The `Cookie` header is dropped entirely by default. `DevConduit:Route:ForwardCookies` turns
  passthrough back on for callers that genuinely need upstream cookies; the stripping above
  still applies.

### 08.5 The cloud metadata egress block

The VM carries the managed identity that backs the app registration's credential. Any process
on the VM can ask the instance metadata service for that identity's token:

```text
GET http://169.254.169.254/metadata/identity/oauth2/token?resource=...
Metadata: true
```

Dev.Conduit is a process on the VM that sends requests of the caller's choosing. Without a block,
any signed-in user could compose that request in the console and read the app's credential
out of the response. So Dev.Conduit refuses to connect to the networks in
`DevConduit:BlockedEgressNetworks`:

| Default | What it is |
| --- | --- |
| `169.254.0.0/16` | IPv4 link-local, including the instance metadata service at `169.254.169.254` |
| `168.63.129.16/32` | The Azure WireServer |
| `fe80::/10` | IPv6 link-local |

| Concern | Behaviour |
| --- | --- |
| Configuration | The binder appends configured entries to the defaults. Configuration can add networks; it can never remove a default. |
| Malformed entries | Logged at startup and ignored. Startup also logs the effective list. |
| When it is checked | At connect time, against the resolved address. A hostname that resolves into a blocked network, DNS rebinding, and a redirect to a blocked address are all caught. |
| Multiple addresses | A host is refused if *any* of its resolved addresses is blocked. |
| IPv4-mapped IPv6 | Normalised, so `::ffff:169.254.169.254` is caught as `169.254.169.254`. |
| Coverage | The console's outbound calls and `/route` upstream calls. Not local mode's relay to the remote Dev.Conduit, which is not an upstream. |

A refused call surfaces as error kind `Blocked`, naming the host, the address and the
setting: the console shows **BLOCKED**, and `/route` answers `400`. See section 09 and
[docs/ROUTING.md](docs/ROUTING.md), section 4.

> **Caution.** With an ambient `HTTP_PROXY` in the server's environment, the connect-time
> check sees the proxy's address, not the target's, and the block no longer protects the
> metadata endpoint. Keep `HTTP_PROXY` unset for server mode on the VM, or block these
> networks at the proxy as well.

### 08.6 Rendering and transport

- Untrusted response bodies are rendered with `textContent` only, never as HTML.
- Certificate validation is on unless you tick *Allow invalid TLS certificate* for a request.
  That option can be switched off entirely with `DevConduit:AllowInsecureTlsOptIn`, and every use
  of it is logged.
- Without a host name (`DevConduit:Server:Hostname`, or the computer's name beside the service) the listener is `http://localhost:8091`, so the session
  cookie is not encrypted on the wire. That is acceptable for loopback traffic on a developer
  machine and for nothing else. With it, the server serves HTTPS with its app-managed
  certificate (section 05.4).

### 08.7 The Data Protection key ring

The session cookie is only as private as the keys in `{DataRoot}\keys`: whoever can read them
can forge or decrypt any user's session.

| Concern | Behaviour |
| --- | --- |
| Encryption at rest | On Windows each key is encrypted with machine-scope DPAPI, so a password reset or a change of service account cannot lock the app out of its own keys. Off Windows the keys are stored unencrypted and the directory's permissions are the only protection. |
| Directory access | Machine-scope DPAPI lets *any* local account decrypt a key it can read, so on Windows the directory is limited to `SYSTEM`, `BUILTIN\Administrators` and the account the app runs as, with inheritance from `DataRoot` turned off. A new directory is created that way; an existing one with a broader list is tightened at startup. |
| When tightening fails | Startup logs a warning naming the directory and carries on. Restrict the directory by hand. |
| Moving or copying | Machine-scope keys decrypt only on the machine that wrote them. Do not copy `keys\` between machines; let each create its own. |

### 08.8 Client certificates and workstation trust

A `.conduit` and the client certificates that follow from it authenticate to `/cert` and to
nothing else (section 05.4). What limits them, and what a workstation keeps:

| Concern | Behaviour |
| --- | --- |
| What a client certificate can do | Fetch the server's public certificates from `GET /cert`, and renew through `POST /cert/renew` only in its last 60 days, only for its own enrollment's subject. No sign-in, no token, no `/route`, no console. |
| Revocation | By enrollment: `certs revoke <serial>` or `certs revoke --enrollment <id>` refuses every certificate of the export, including renewals made before the revoke and any after. A certificate superseded by a renewal still authenticates and may renew until it expires, because several users of one workstation renew from the same machine identity; revoking the enrollment is the control. |
| A host name change | Replaces the client authority and revokes every enrollment it signed, so client certificates from the old name stop working. Every client must import a new `.conduit`. |
| The machine identity | `%ProgramData%\Proxylayer\Dev.Conduit\client\<hostname>\identity.pfx`, machine-scope DPAPI, readable by Users by design: every user's unelevated sync authenticates with it. Any local account can therefore use it, with the power above and nothing more. |
| Who writes machine trust | Only elevated or SYSTEM processes: the folder gives Users read, not modify, and `service-admin trust` and the import put that list back on a folder an earlier build left broader. A user cannot plant a pin that `service-admin trust` would push into `LocalMachine\Root`. |
| The user layer | `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\client\<hostname>\`: the pins a user's sync learned and the user's renewed certificate, user-scope DPAPI, in a folder no other user can read. It widens only that user's own relay trust. |
| What `service-admin trust` trusts | Never the user layer. It asks the server itself with the machine identity, accepting the server only by Windows trust or the machine pins, and applies the server certificate rules to every certificate before adding it to the machine pins and `LocalMachine\Root`. |
| Keeping the machine identity current | Only `service-admin trust` renews it, in its last 60 days, with the machine identity itself and in its own enrollment; an unelevated sync renews only into its user's layer. The tray prompts when the machine identity is due or Windows trust lags, so one admin prompt about once a year keeps the machine identity and Windows trust current, while per-user renewals stay automatic. Missed until it expires, the workstation needs a new `.conduit`. |

### 08.9 The target allow-list

On a server, `DevConduit:Route:AllowedTargets` limits which hosts `/route` and the console may call.
Local mode always names its target in `X-DevConduit-Target`, so `AliasesOnly` cannot be used with
it, and without a list any signed-in user can make the server call any host it can reach, apart
from the blocked networks of section 08.5. Set it on the Server page (Allowed targets, one per
line) or in the machine settings file:

```json
"DevConduit": {
  "Route": {
    "AllowedTargets": [ "api.internal.example", "*.orders.internal:8443", "10.0.0.0/16", "[fd00::/8]:443" ]
  }
}
```

| Entry | Matches |
| --- | --- |
| `api.internal.example` | That host name exactly, ignoring case, whatever it resolves to. |
| `*.internal.example` | Every name below the domain (`a.internal.example`, `a.b.internal.example`), not the domain itself. |
| `10.0.0.5`, `10.0.0.0/16`, `fd00::/8` | An address, or a network written from its first address, matched against the address actually connected to. |
| any of them with `:port` | Only that port. An IPv6 address or network with a port goes in brackets: `[fd00::1]:8443`. |

| Concern | Behaviour |
| --- | --- |
| Empty list | Every host not blocked is allowed, as before. Startup logs *Any reachable host is allowed. Add allowed targets to restrict.*, and the Server page shows the same line. |
| Routes | Every configured route's target (its host and port) is allowed besides, with no entry of its own. |
| When it is checked | Twice. On the target a request names, before anything is sent; and at connect time on the resolved address, after the blocked networks. A host name entry or route target allows the name whatever it resolves to; otherwise every resolved address must lie in a network entry, so a name that resolves, or rebinds, outside the networks is refused. A redirect the console follows is checked the same way. |
| Refusal | `/route` answers `403` with `errorKind: "target_not_allowed"`, naming the target and the setting. The console reports `errorKind: "TargetNotAllowed"`. |
| Malformed entries | Logged at startup and ignored. A list whose every entry is malformed still restricts: only route targets are allowed. |
| Local mode | Not applied. A workstation relays to its server, which applies its own list. |

> **Caution.** As for the blocked networks, an ambient `HTTP_PROXY` in the server's environment
> makes the connect-time check see the proxy's address, which the list then refuses unless it
> names the proxy. Keep `HTTP_PROXY` unset for server mode.

---

## 09 API

All endpoints require an authenticated session and the `X-DevConduit-Client: 1` header.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/me` | `{ userId, displayName, userName }` |
| `POST` | `/api/proxy/execute` | Runs a `ProxyRequest`; returns `{ response, error, errorKind, durationMs, historyId }` |
| `GET` | `/api/history?skip=&take=&q=` | Paged index summaries; `q` matches URL or method |
| `GET` | `/api/history/{id}` | Full entry, `404` when unknown |
| `DELETE` | `/api/history/{id}` | Delete one entry |
| `DELETE` | `/api/history` | Clear the caller's history |
| `GET` | `/api/headers` | Saved header sets |
| `POST` | `/api/headers` | Create a set; the server assigns the id |
| `PUT` | `/api/headers/{setId}` | Replace a set |
| `DELETE` | `/api/headers/{setId}` | Delete a set |
| `GET` | `/api/routed?skip=&take=&q=` | Paged routed-traffic summaries, same envelope as `/api/history` |
| `GET` | `/api/routed/{id}` | One full routed entry, headers and bodies included |
| `DELETE` | `/api/routed/{id}` | Purge one routed entry |
| `DELETE` | `/api/routed` | Purge all routed traffic for the caller |
| `GET` | `/api/storage` | Byte usage and entry counts for every store |
| `GET` | `/health` | Anonymous, answered before sign-in in both modes; `{ status: "ok", configured }`, `configured` false while the Entra ids are blank or placeholders |

`/route/{...}` sits outside the `/api` group and outside these rules: any verb, no client
header, and its own authentication. It is documented in [docs/ROUTING.md](docs/ROUTING.md).

**Internal endpoints.** `GET /cert` and `POST /cert/renew` are not part of this API. They exist
only with a host name (configured or the computer's name), answer only a client certificate this server issued
(mutual TLS), and are used by local mode to keep imported certificates current. Section 05.4.

A failed outbound call — DNS, TLS, refused connection, timeout, bad URL — still returns HTTP
`200` from `/api/proxy/execute`, with `response: null` and `error` / `errorKind` populated, and
is recorded in history.

A destination in `DevConduit:BlockedEgressNetworks` takes the same path, with
`errorKind: "Blocked"` and an `error` naming the host, the resolved address and the setting.
The console shows it as **BLOCKED**. See section 08.5. A destination the server's
`DevConduit:Route:AllowedTargets` does not cover takes it too, with `errorKind: "TargetNotAllowed"`
(section 08.9).

---

## 10 Layout

```text
Proxylayer.DevConduit.sln
global.json        the .NET SDK the builds use
build.ps1          build; zips the app to artifacts\Dev.Conduit-<Configuration>.zip, and for Release
                   builds the MSI, its .sha256 and its zip, optionally signed (sections 05.3, 05.6)
test.ps1           runs the tests; -Trx writes TRX results for CI
.github/workflows/ pr.yml builds and tests pull requests; release.yml builds a draft release (section 05.6)
deploy.ps1
register-app.ps1   Entra registration, managed identity and federated credential (section 03)
installer/
  Proxylayer.DevConduit.Installer.wixproj  the WiX v5 MSI project, built by build.ps1, not in the solution
  Package.wxs      package, roles, service, registry, shortcuts, the ProgramData folder
  Ui.wxs           the install pages: welcome, role, folder, ready, finish
  INSTALL.txt      the instructions zipped with the MSI
  CustomActions/   the ENVIRONMENT check the package runs (.NET Framework, WiX DTF)
docs/
  ROUTING.md       the /route forwarding contract
  LOCAL-MODE.md    running on the workstation
src/Proxylayer.DevConduit/
  Configuration/   options, mode selection, per-mode composition, startup diagnostics, Data Protection,
                   the machine and user settings files, the managed HTTPS endpoint
  Certificates/    the app-managed server certificate, client authority and rotation; .conduit
                   export, validation and import; hosts file, Windows trust, pins and /cert sync
  Hosting/         building the web application; the console, service, tray and elevated-helper launch paths
  Tray/            the tray app: roles, in-process local mode, log buffer, elevation client
  Tray/Setup/      setup window models: settings layers, routing, local and server settings
  Tray/Ui/         Windows Forms: the notification icon, menus, log window, setup window pages
  Tray/Updates/    the tray's updater: checks, the handoff to the elevated helper, the relauncher
  Updates/         the GitHub Releases feed, download and verification, the service's quiet-hour installer
  Models/          request, response, history, header-set records
  Services/        user context, JSON store, proxy executor, secret policy, /route forwarding
  Services/Local/  token acquisition, target matching, the remote relay, the forward proxy
  Endpoints/       minimal API groups, the /route branch, the client-header filter
  wwwroot/         index.html, app.js, styles.css
```

There is one application project. Local mode is a mode of the same executable, not a separate library the
application references: that is what keeps the two ends of the wire contract in step.

---

## 11 Troubleshooting

| Symptom | First check |
| --- | --- |
| Every page returns `503` with configuration text | `TenantId` or `ClientId` is blank or a placeholder. A fresh install is blank until the setup window saves them to the machine settings file. See section 04. |
| Sign-in loops back to the login page | The redirect URI in the app registration must be exactly the address users browse to plus `AzureAd:CallbackPath`, platform **Web**: `https://<host>:8091/signin-oidc` behind the HTTPS endpoint, `http://localhost:8091/signin-oidc` on loopback. The `503` configuration text lists the URIs derived from the listen URLs. |
| Sign-in fails with an invalid client error | Read the `Entra client credential: …` startup line. With the managed identity credential, confirm the process is on the VM, the UAMI is attached to it, and `ManagedIdentityClientId` is the UAMI's client id. With the secret fallback, the secret is missing or stale: in development re-run the `dotnet user-secrets set` command in section 04; in a published instance user secrets are not read at all, so set `AzureAd__ClientSecret`. |
| `AADSTS70021` at sign-in | The federated credential's subject does not match the managed identity presenting the assertion, usually because the UAMI was deleted and recreated with a new principal id. Re-run `register-app.ps1`. |
| `AADSTS700016`, application not found in the directory | The users' tenant has no service principal for the app, because admin consent was never granted. Have an admin open the consent link. See section 03.1. |
| `AADSTS65001`, consent missing | Admin consent in the users' tenant is missing or predates a permission change. Have an admin open the consent link again. |
| A request shows **BLOCKED** | The destination resolves into `DevConduit:BlockedEgressNetworks`. That is intended for metadata and link-local addresses. See section 08.5. |
| An update did not install | The running version stays. Settings > About shows the last outcome; `%ProgramData%\Proxylayer\Dev.Conduit\updates\install-X.Y.Z.log` and `update-helper.log` say why, and a server logs it to the Application event log. See section 05.5. |
| A request shows **TARGETNOTALLOWED**, or `/route` answers `403` with `target_not_allowed` | The server's `DevConduit:Route:AllowedTargets` does not cover the host, or a name resolves outside its networks. Add an entry on the Server page, or call it through a route. See section 08.9. |
| `AADSTS65005` when local mode signs in | The client asks for a scope the app registration does not expose, typically `DevConduit.Forward` from a registration that still exposes `DevProxy.Forward`. Set Scope to the registration's scope on the server's Server page, restart the service, export a new `.conduit` and import it on the workstation. See section 03.1. |
| `403` after a successful sign-in | `AllowedGroupIds` or `AllowedUserOids` excludes the account. Group ids also need the groups claim enabled on the registration. |
| A replayed history entry fails with `401` | Secret header values are stored in history as `***redacted***`. Re-enter them, or apply a saved header set, before sending. |
| A request fails but the console shows `200` in the API tab | Transport failures are reported in the response pane as an error callout, not as an HTTP status. Read the error kind there. |
| The server listens on the wrong address or port | The listen address comes only from `Kestrel:Endpoints`, or `urls` / `ASPNETCORE_URLS`, which `dotnet run` sets from `launchSettings.json`. With none set it is `http://localhost:8091`. See section 04.1. |
| Pages of `CryptographicException` from `DpapiXmlDecryptor` / `XmlKeyManager` at startup ("key ... is ineligible to be the default key"), and everyone is signed out | Data Protection keys written by an earlier build to the account's own `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` under user-scope DPAPI, which a password reset or a different account cannot decrypt. Current builds keep the keys in `{DataRoot}\keys` (section 07) and no longer use that folder, so delete `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` for that account. If the message names `{DataRoot}\keys` instead, the folder came from another machine: delete it, and users sign in again. |
| The service will not start | Read the Application event log, source `Dev.Conduit`. A service configured for local mode stops by design (section 05.1); a port already in use or an unreadable certificate is logged there too. |
| Startup refuses: `The settings file ... is not valid` | The machine or user `settings.json` is not a JSON object. Correct it or delete it; the message names the file. See section 04.4. |
| Startup refuses: the machine settings file `is owned by` or `can be changed by` another account | The file was not written by an administrator. Save from the tray's setup window, which rewrites it elevated with the right permissions, or delete it. |
| The local proxy does not start by itself | Start proxy automatically is unticked, or the configuration is not ready: no server imported, a problem the Routes page lists, or no Entra ids. The log says which. An automatic start that failed is not retried until the next save or tray start; use Start local proxy. |
| The tray's icon is red, `Local proxy failed` | Click the notification, or open View log: a configuration refusal lists its problems there and on the Routes page; anything else, such as a port in use, is logged. With no server imported, import a `.conduit` on the Servers page. |
| Saving in the setup window, or a service action, does nothing | The UAC prompt was declined, which is a silent no-op. Anything else that fails shows a message. |
| A route from an import (or `appsettings.json`) cannot be deleted on the Routes page | By design: a settings file cannot delete a lower file's key. Deleting the row clears its On box, which saves `"Enabled": false`. To drop a server and all its routes, use Remove on the Servers page. |
| The MSI stops with a `CONDUITCONFIG ...` message | The file is missing, not a full local path, on a share or network drive, or given with `ROLE=Server`. Copy it to a local folder and give the full path. See section 05.3. |
| A Workstation install with `CONDUITCONFIG` rolls back | The import refused the file. Install again with `/l*v install.log` and read the `ImportConduitConfig` lines, or run `Dev.Conduit.exe import <file>` to see why; an expired file needs a new export. |
| `Dev.Conduit.exe` will not start and names `Microsoft.WindowsDesktop.App` | Install the .NET Desktop Runtime 10 as well as the ASP.NET Core Runtime 10, or install from the MSI, which needs neither. See section 02. |
| The service is not running after an MSI install | Expected on a new server until the certificate and settings are in place: the install starts it without waiting. Read the Application event log, source `Dev.Conduit`, then fix the settings from *Dev.Conduit* and restart the service. See section 05.3. |
| An MSI upgrade installed the wrong role | Without `ROLE` on the command line an upgrade reuses `HKLM\SOFTWARE\Proxylayer\Dev.Conduit\Role`. Run the upgrade with `ROLE=Server` or `ROLE=Workstation`, or pick the role on the Role page. |
| The MSI stops with `ENVIRONMENT must be ...` or `ROLE must be ...` | The value on the command line, or the one recorded by the previous install, breaks the rule in section 05.3. |
| *Start with Windows* is ticked and greyed | A Workstation install set it for every user. Uninstall to remove it. |
| The tray's *Uninstall service* is greyed, with *Installed by the Dev.Conduit MSI* | By design: the MSI is installed in its Server role and owns the service. Remove or change it from Settings → Apps or with `msiexec`. See section 05.3. |
| The tray has no *Install service* on a Workstation install of the MSI | By design: with the MSI installed, the service comes from it. Run the MSI again with `ROLE=Server`. A service already present there was not installed by the MSI and can be uninstalled from the tray menu. See section 05.2. |
| A service action says the service belongs to the Dev.Conduit MSI, or `install` / `uninstall` exits with code 12 | The elevated helper's `ManagedByInstaller`: `install` while the MSI is installed in either role, `uninstall` while it is installed in its Server role. The helper and the Topshelf verbs leave those to the MSI. Use Settings → Apps or `msiexec`. |
| A workstation's relay fails with a TLS error after the server's certificate rotated | The workstation did not sync in the 30 days the next certificate was published (it was off, or `/cert` was unreachable). Import a new `.conduit`. See section 05.4. |
| `/cert` answers `403` to a workstation | Its enrollment was revoked, its client certificate expired or came from another server, the server's host name changed, or the server's `{DataRoot}\certs` was deleted. Import a new `.conduit`. |
| `/cert/renew` answers `409` | The client certificate has more than 60 days left by the server's clock; the workstation's clock is probably ahead. The next sync, 12 hours later, tries again. |
| `service-admin trust` fails, or the certificate update prompt keeps coming back | It asks the server itself, with the machine identity from the import. If the server cannot be reached, or that identity's enrollment was revoked or its certificate expired, import a new `.conduit`. |
| The certificate update ends with exit code 20, *this computer's client certificate could not be renewed* | Windows trust was updated; the machine identity was not renewed and is kept. A `409` means the server's clock says it is not due yet, so try again later; a `403` means the enrollment was revoked, so import a new `.conduit`. |
| Startup warns that `Kestrel:Endpoints` is configured and the managed certificate is not used | An explicit endpoint wins over `DevConduit:Server:Hostname`. Remove `Kestrel:Endpoints` from the settings files to serve the app-managed certificate and `/cert`. |
| `export` is refused | It needs server mode, a host name (`DevConduit:Server:Hostname`, or this computer's name on a server with the service installed), the `AzureAd` ids, an address (`--address` or `DevConduit:Server:ClientAddress`) and no `Kestrel:Endpoints`. The message names what is missing. |
| A browser on the server does not trust `https://<hostname>:<port>` | The service adds its own certificates to `LocalMachine\Root`; a console run unelevated cannot and logs a warning saying so. Check `DevConduit:Server:TrustStore` is not `None`, and that the browser uses the same host name the certificate names. |
| The server's certificates are still in `LocalMachine\Root` after the Server role was removed | The MSI cannot remove them; they stay until they expire. Run `Dev.Conduit.exe certs untrust` before uninstalling next time, or delete the certificates named `Dev.Conduit <hostname>` from the store by hand. |
| The Server page shows a greyed host name the clients cannot resolve | It is this computer's name, the default. Exports carry the client address and imports map the name in the hosts file, so it works; set a host name on the Server page if clients should use another. |
| The startup banner says the wrong mode | `--mode` was not passed and `DevConduit:Mode` still says otherwise. |
| `Dev.Conduit cannot start in LOCAL mode` | Read the listed problems; each names the setting and why it matters. A missing remote after an upgrade usually means the configuration still uses `DevConduit:Local:RemoteEndpoint`; move it to `DevConduit:Remotes`. See [docs/LOCAL-MODE.md](docs/LOCAL-MODE.md). |
| `/route` answers `400` with `no_remote` | Several remotes are configured with no `DevConduit:DefaultRemote`, and the target matches no route and no remote's `Targets`. Set a default, or add a `Targets` rule. See section 04.3. |
| Startup warns that a setting has been removed | The key moved: `DevConduit:Route:Aliases` to `DevConduit:Routes`, the local endpoint, identity and targets to `DevConduit:Remotes`. The warning names the replacement. |
| An upstream that needs a cookie stopped working | Cookies are no longer forwarded by default. Set `DevConduit:Route:ForwardCookies`. See section 08.4. |
