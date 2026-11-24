# Local Mode

Dev.Conduit runs on the privileged machine, but the developer works somewhere else. Local mode
is the second half of that pair: the same executable, started with `--mode local`, running on
the workstation and relaying traffic to the remote instance.

The point is where the Entra token lives. In local mode the developer's application talks
plain HTTP to loopback — no token, no headers, no Dev.Conduit reference, no code change. Local
mode holds the token and attaches it on the way out. That works identically for an Angular
dev server, a `curl` script, a Java client, and a legacy executable nobody can rebuild.

One binary serves both roles, so the wire contract between the two ends cannot drift.

For the contract local mode speaks to the remote instance, see [ROUTING.md](ROUTING.md).

---

## 01 The two modes

| | Server mode | Local mode |
| --- | --- | --- |
| Runs on | The privileged machine | The developer workstation |
| Binds | Whatever Kestrel configuration names; `http://localhost:8091` by default | Loopback only, `:8092` and `:8093` |
| Entra ID | **Validates** bearer tokens and console sessions | **Acquires** tokens with MSAL |
| Forwards to | The real upstream | A remote Dev.Conduit instance, chosen per request |
| Console UI | Served, behind sign-in | Served, no sign-in |
| Routed capture | On | Off by default |

Mode comes from `DevConduit:Mode` (default `Server`), and the `--mode server|local` argument
overrides it:

```powershell
dotnet run --project src\Proxylayer.DevConduit -- --mode local
.\publish\Dev.Conduit.exe --mode local
```

Startup prints which mode came up, what it bound, and where the next hop is. If that banner
says `SERVER` when you meant `LOCAL`, nothing else in this document applies.

### 01.1 Running from the tray

Instead of a console, local mode can run inside the tray app, which is the same exe:

```powershell
.\publish\Dev.Conduit.exe tray
```

On a machine where the `Dev.Conduit` service is not installed the tray takes the workstation
role and hosts local mode in its own process, exactly as `--mode local` builds it:

| Menu item | Does |
| --- | --- |
| Start / Stop local proxy | Builds local mode afresh from the current settings and starts it, or stops it. While it starts, the icon is amber and the status reads *signing in*: the token warmup of section 05 runs then, and its browser sign-in opens as it would from a console, because the tray runs in your session. |
| Open local console | `http://127.0.0.1:{Port}`, `8092` by default. |
| Copy proxy address | `http://127.0.0.1:{ForwardProxyPort}`, `8093` by default, for `HTTP_PROXY`. |
| View log... | The last 2,000 log lines from the tray and the local proxy, with copy and clear. There is no console, so this is where the startup banner goes. |
| Start with Windows | Starts the tray at sign-in, through `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. It does not start the proxy; use Start local proxy. |
| Update Dev.Conduit certificates (needs admin)... | Shown only when a server's new certificate is pinned but not yet in `LocalMachine\Root`, or the machine's client certificate for it is in its last 60 days; runs `service-admin trust` (UAC) for each such server, which asks the server for its certificates itself, with the machine's client certificate, adds them, and renews that client certificate when it is due. A notification says so once. |
| Settings... | The setup window: Servers (the imported servers, Import .conduit..., Remove, Update certificates; Remove also deletes your own pins and renewed certificate for the server), Routes (name, target and server), and Local proxy (the ports, forward proxy and capture of section 02's `DevConduit:Local` table). It shows no server settings and nothing about certificates. |
| Exit | Stops the local proxy cleanly, then the tray. |

The icon is green while running, amber while starting or once the running proxy has logged an
error, red when it could not start, grey when stopped. If the configuration refuses to start
(the same problems a console run lists), a notification names the first one and opens the
setup window when clicked; the log has the whole list.

The Routes and Local proxy pages save to `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\settings.json`, which
every console and tray launch reads above the machine settings file and which an update leaves
alone (README section 04.4). Routes that come from an import (in the machine settings file) are
grey; changing one saves only the change, and deleting one switches it off with
`"Enabled": false`, since a settings file cannot delete a lower file's key. With one imported
server a route naming none goes to it; with several, each route names its server. Importing and
removing servers change the machine settings file through the elevated helper. The shared
secret is not on any page; set `DevConduit:Local:SharedSecret` in a settings file if you need it.
Restart the local proxy after saving; the tray offers to.

> **Caution.** Local mode refuses to start bound to anything but a loopback address, and
> listens only on `BindAddress:Port`: any `Kestrel:Endpoints` in configuration, such as the
> UAT https endpoint, are ignored and logged at startup. It performs no inbound
> authentication by design, so loopback is the boundary that keeps the developer's token out
> of reach of anything off the machine. The refusal names the setting and exits non-zero.

---

## 02 Configuration

Local mode reads three things: the remote Dev.Conduit instances it relays to, under
`DevConduit:Remotes`; the named routes it shares with server mode, under `DevConduit:Routes`; and
its own workstation settings, under `DevConduit:Local`. Server mode ignores the first and the
last. The sign-in identity defaults to the `AzureAd` section, so with one remote the whole of
it is a name and an address. A fresh install carries none of it: importing the server's
`.conduit` (section 02.3) writes it to the machine settings file, which looks like this:

```json
"DevConduit": {
  "Remotes": { "uat": "https://devconduit.example.internal:8091" },
  "Routes": { "orders": "http://10.0.0.5:8080" }
}
```

With that, `/route/orders/...` and the forward proxy both relay `10.0.0.5:8080` through
`uat`, and so does any `X-DevConduit-Target` header, because a single remote is the default.
Start with `--mode local`, or set `DevConduit:Mode` to `Local`.

Several remotes are written the same way. An entry that needs more than an address becomes
an object; the values below are illustrative:

```json
"DevConduit": {
  "Remotes": {
    "uat":  "https://devconduit.example.internal:8091",
    "prod": {
      "Endpoint": "https://devconduit-prod.example.internal:8091",
      "TenantId": "<tenant id>",
      "ClientId": "<prod app registration client id>",
      "Scopes": [ "api://<prod app registration client id>/DevConduit.Forward" ],
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

Here `orders` and any unclaimed header-targeted request go to `uat`; `orders-prod` and every host
under `prod.internal` go to `prod`, which signs in with its own app registration.

**`DevConduit:Remotes`** maps a name to a remote. Names are case-insensitive, and at least one
remote is required. The value is the endpoint itself, or an object:

| Key | Default | Meaning |
| --- | --- | --- |
| `Endpoint` | required | Base address of the remote Dev.Conduit, an absolute `http` or `https` URI. In the string form, the string is the endpoint. |
| `Enabled` | `true` | `false` leaves the remote out as if absent, which is how a settings file switches off a remote `appsettings.json` defines. Routes take the same key. |
| `TenantId` | `AzureAd:TenantId` | Entra tenant the developer signs in against for this remote: the users' tenant. Set it only to differ from the server's. |
| `ClientId` | `AzureAd:ClientId` | Application id of the app registration this remote validates against. Set it only to differ from the server's. |
| `Scopes` | `api://{ClientId}/DevConduit.Forward` | Scopes requested for this remote, from its effective client id. |
| `Targets` | `[]` | Extra host rules the forward proxy relays to this remote rather than contacting directly. Routes need no entry here: each route's origin is added as a `host:port` rule for its remote. See section 02.1. |

**`DevConduit:Routes`** maps a name to an upstream: a string origin, or an object with `Target`
and, optionally, `Remote`, the remote that carries it. A route without `Remote` goes to the
default remote. The name is the `/route/{name}/...` segment, as in server mode, which reads
the same section and ignores `Remote`.

**`DevConduit:DefaultRemote`** names the remote used when nothing more specific applies. It is
optional, and defaults to the only remote when exactly one is configured. With several remotes
and no default, every route must name its `Remote`, and a header-targeted request nothing
claims is refused; see section 02.2.

**`DevConduit:Local`** holds the workstation's own settings:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Port` | `8092` | Reverse proxy and console. Differs from `8091` so both modes can run on one machine. |
| `BindAddress` | `127.0.0.1` | Must be a loopback address. Anything else is a fatal startup error. |
| `SharedSecret` | `""` | When set, proxy ingress requires `X-DevConduit-Local-Secret`. |
| `ForwardProxyEnabled` | `true` | Whether the raw `HTTP_PROXY` listener runs. Never runs in server mode. |
| `ForwardProxyPort` | `8093` | Port for that listener. Must differ from `Port`. |
| `CaptureEnabled` | `false` | Whether local mode also records what it relays. |
| `CacheDirectory` | `%LOCALAPPDATA%\Proxylayer\Dev.Conduit\msal` | Where the encrypted token cache lives, shared by every remote. |
| `InteractiveFallback` | `true` | When false, sign-in skips the browser and goes straight to device code. |

Local mode refuses to start, naming the setting, when no remote is configured, when an
endpoint is not an absolute `http` or `https` URI, when a route or `DevConduit:DefaultRemote`
names a remote that does not exist, or when there are several remotes, no default, and a
route that names none.

`DevConduit:Route:Aliases`, `DevConduit:Local:RemoteEndpoint`, `DevConduit:Local:Targets` and
`DevConduit:Local:TenantId`, `ClientId` and `Scopes` have been removed. Startup warns about each
one it still finds and names its replacement; nothing is migrated automatically.

At startup the log lists each remote with its endpoint and where its identity comes from
(`inherits AzureAd`, or the explicit client id), the default remote, each route as
`name -> target via remote`, and the effective forward-proxy rules per remote.

`TenantId` is the users' tenant, where the developer's account lives, while `ClientId` names the
multi-tenant app registration in the server's tenant; see the README, section 03. A remote
validates tokens against that same registration, which is why the `AzureAd` values are the
default.

A missing tenant or client id is a warning rather than a fatal error: the host starts and
serves the console, and each request relayed to that remote then fails with a message naming
the settings. That is deliberate — it lets a developer bring local mode up and read the banner
before touching the app registration.

### 02.1 Target rules

The same forms server mode's client contract always used:

| Form | Example | Matches |
| --- | --- | --- |
| Bare host | `orders.internal.example.com` | That host on any port |
| Host and port | `10.0.0.5:8080` | That host on that port only |
| Bare IP address | `10.0.0.5` | That address on any port |
| Leading wildcard | `*.staging.example.com` | Any subdomain; not the apex itself |
| With a scheme | `http://10.0.0.5:8080` | Same as the host-and-port form; the scheme is ignored |

Matching is case-insensitive. A host matching nothing is contacted directly, untouched.
Traffic addressed to any configured remote Dev.Conduit, or to local mode's own two ports, never
matches: that loop guard is what stops `HTTP_PROXY` pointing at this process from folding it
into itself.

The effective rules are every remote's `Targets`, in order of remote name, followed by one
`host:port` rule per route origin, the port being the scheme default when the origin names
none. A route origin already relayed to the same remote by an earlier rule, or claimed by an
earlier route, adds no rule of its own.

### 02.2 Which remote carries a request

The same order applies to both ingress styles, first match wins:

1. **A route.** The route the `/route/{name}/...` segment named, or a route whose target is
   the same host and port as the destination. Its `Remote`, or the default remote.
2. **A `Targets` rule.** The first remote, in order of remote name, with a rule matching the
   destination's host and port.
3. **The default remote.** `/route` only: a header-targeted request nothing above claims goes
   to `DevConduit:DefaultRemote`, or to the only remote.
4. **Nothing.** `/route` answers `400`:

```json
{
  "errorKind": "no_remote",
  "error": "No remote Dev.Conduit is configured for http://10.99.0.5:8080. It matches no route under DevConduit:Routes and no Targets rule under DevConduit:Remotes, ..."
}
```

The forward proxy never falls back to the default: a host matching neither a route nor a
`Targets` rule is contacted directly, however many remotes there are.

### 02.3 Importing a server's .conduit

The usual way to configure a workstation is not to write `DevConduit:Remotes` by hand but to
import the file the server exports (README section 05.4):

```powershell
.\Dev.Conduit.exe import .\devconduit.example.internal.conduit
```

A `.proxyconfig` file exported by POC.DevProxy, this product's previous name, imports the same
way.

The import replaces three manual steps of earlier versions:

| Before | Now |
| --- | --- |
| A hand-written remote and routes under `DevConduit:Remotes` and `DevConduit:Routes` | The import adds the remote (named after the host name's first label, endpoint `https://<hostname>:<port>`) and the server's routes, with `Remote` set to it, to the machine settings file. Ids that match `AzureAd` are inherited; blank `AzureAd` ids are filled from the file. A later import from the same server replaces its routes; another server's file adds another remote. |
| A hosts entry where DNS does not resolve the server | One line, marked `# Dev.Conduit`, added or replaced in the hosts file when the file's address is an IP address. |
| Trusting the server's certificate issuer by hand | The server's self-signed certificates go into `LocalMachine\Root`, and are also pinned in `%ProgramData%\Proxylayer\Dev.Conduit\client\<hostname>\trust.json`. |

It needs administrator rights and asks for them (UAC); from an elevated prompt add `--machine`
to run it in that process. Restart the local proxy afterwards. A re-import updates only the
remote's endpoint and its own Entra ids; host rules, scopes and anything else you added to the
remote stay.

**Two layers.** What the import wrote is the machine layer, under
`%ProgramData%\Proxylayer\Dev.Conduit\client\<hostname>\`: the pinned server certificates and the client
certificate (machine-scope DPAPI), which administrators and SYSTEM write and every user only
reads. What your local proxy learns afterwards goes into your own layer,
`%LOCALAPPDATA%\Proxylayer\Dev.Conduit\client\<hostname>\`: new server certificates, and your renewed client
certificate, protected with user-scope DPAPI in a folder no other user can read. Your relay
trusts the pins of both layers, and authenticates with your renewed certificate while it is valid
and from the same import, otherwise with the machine's. Every user of the workstation can use
the machine's client certificate; that is what lets each sync work without elevation, and all it
can do is fetch the server's public certificates and renew in its last 60 days, in an
enrollment the server's administrator can revoke as a whole (README section 05.4).

**Certificate trust for the relay.** The relay to a remote accepts a certificate Windows trusts,
or one pinned for that remote's host in either layer, inside its validity period and naming the
host. Hosts nothing was imported for are unaffected. Because of the pins the relay keeps working
when the server rotates its certificate, even before Windows trust has the new one: local mode
calls the server's internal `/cert` endpoint with the client certificate on start and every 12
hours, pins the next certificate the server publishes in your layer, drops expired ones, and
renews the client certificate in its last 60 days into your layer. The server refuses a renewal
outside that window (`409`) and from a revoked enrollment (`403`). A pinned certificate not yet
in `LocalMachine\Root` is flagged for the tray and logged; `service-admin trust <hostname>`,
elevated, adds it, taking the certificates not from your layer but from the server itself, asked
with the machine's client certificate and checked against the machine's pins.

**Keeping the machine's client certificate current.** Your renewal lives in your layer, so it
does nothing for the machine's client certificate, which new users of the workstation and
`service-admin trust` itself authenticate with. When that one is in its last 60 days, the tray
flags it the same way, and `service-admin trust <hostname>` renews it too: a new key, a request
with the same subject sent to `/cert/renew` with the machine's client certificate, and the
answer stored in the machine layer with machine-scope DPAPI. If the server refuses (`409`, `403`)
or cannot be reached, the current one is kept and the tray says the renewal failed; Windows trust
is still updated. So one admin prompt about once a year keeps the machine's client certificate
and Windows trust current, while your own renewals stay automatic. The same run puts the machine
layer's access list (Administrators and SYSTEM full control, Users read) back on a folder an
earlier build left broader. A workstation that misses the whole 30-day window before a rotation
(switched off for a month), whose machine client certificate expired before anyone accepted the
prompt, or whose enrollment was revoked, must import a new file.

---

## 03 Ingress A: the reverse proxy

Same URL contract as server mode. Point the client at `http://127.0.0.1:8092/route/...` and
name the upstream with a header or an alias segment.

```bash
curl -i \
  -H "X-DevConduit-Target: http://10.0.0.5:8080" \
  "http://127.0.0.1:8092/route/services/orders/service.svr?wsdl"
```

With `"Routes": { "orders": "http://10.0.0.5:8080" }` configured, the header goes away:

```bash
curl -i "http://127.0.0.1:8092/route/orders/services/orders/service.svr?wsdl"
```

What local mode does with that call:

| Element | Change |
| --- | --- |
| Request URI | Rewritten to `{remote endpoint}/route{remaining path}{query}`, the remote chosen as in section 02.2 |
| `X-DevConduit-Target` | Set to the origin resolved here, from the route table or the caller's header |
| Existing `Authorization` | Moved to `X-DevConduit-Upstream-Authorization`, unless the caller set that header itself |
| `Authorization` | Set to `Bearer <token acquired for the developer>`, for that remote's identity and scopes |
| `Host` | Follows the remote endpoint |
| Verb, body, all other headers | Unchanged |

The remote instance renames `X-DevConduit-Upstream-Authorization` back to `Authorization` on
its own egress, so a credential the application meant for the upstream arrives there and the
Entra token does not.

Bodies stream in both directions, exactly as in server mode: nothing is buffered, and a large
SOAP response starts arriving before the upstream has finished sending it.

### 03.1 When the remote refuses

A `401` or `403` raised by the remote instance is about this machine's token, not about the
upstream. Relaying it verbatim would send a developer looking at the wrong end of the chain,
so local mode replaces the body with one that names what to fix:

```json
{
  "errorKind": "remote_forbidden",
  "error": "The remote Dev.Conduit 'uat' at https://devconduit.corp:8091/ accepted the token but refused the call. ...",
  "target": "http://10.0.0.5:8080"
}
```

The status code is preserved. An upstream's *own* `401` or `403`, relayed through the remote,
still arrives untouched: the two are told apart by the shape of the refusal, not by the status
alone.

---

## 04 Ingress B: the forward proxy

Set `HTTP_PROXY` and change nothing else. This is the option for a client that has no notion
of a base URL you can rewrite.

```powershell
$env:HTTP_PROXY = 'http://127.0.0.1:8093'
curl -i http://10.0.0.5:8080/services/orders/service.svr?wsdl
```

```bash
export http_proxy=http://127.0.0.1:8093
```

Per request, the listener decides one thing: does the host match a forward-proxy rule — a
route's origin or a remote's `Targets` (section 02.1)? A match is relayed to the remote that
rule belongs to, chosen as in section 02.2.

| Request | Host matches a rule | Host matches nothing |
| --- | --- | --- |
| `GET http://host/path` | Relayed through the matching remote Dev.Conduit, with its token and `X-DevConduit-Target` | Forwarded directly to the origin, unmodified |
| `CONNECT host:port` | Refused with `502` and an explanation — see section 06 | Tunnelled: `200 Connection Established`, then bytes both ways, nothing inspected |

A local forward proxy that broke unrelated traffic would be worse than no forward proxy, so
everything outside the rules passes through untouched.

This is a raw `TcpListener`, not a Kestrel endpoint, because Kestrel accepts neither the
absolute-form request target nor the `CONNECT` verb.

### 04.1 What the listener guarantees

| Concern | Behaviour |
| --- | --- |
| Slow loris | A connection has 30 seconds to finish sending a request head, 120 seconds while idle between keep-alive requests |
| Oversized head | Request line capped at 8 KB, whole head at 64 KB, 128 headers |
| Keep-alive | Held open when the response has a `Content-Length` and the client did not ask to close; otherwise answered with `Connection: close` and closed cleanly |
| Errors | Always a well-formed HTTP response with an explanatory plain-text body, never a dropped connection |
| Sockets | Disposed on every path, including tunnels torn down at shutdown |

Two limits are worth knowing before you meet them:

- **Chunked request bodies are refused** with `501` and a message pointing at the reverse
  proxy, which streams them without re-framing. The forward proxy relays length-delimited
  bodies only.
- **HTTPS to a matched host is refused.** That is section 06.

---

## 05 Sign-in and the token cache

On startup, local mode acquires a token for each distinct sign-in identity — each
tenant and client id pair among the remotes — before the listener starts taking application
traffic. Remotes sharing an identity share its sign-in, so the usual one-registration setup
signs in once however many remotes there are. Deferring it to the first proxied call would mean an application appearing to hang
while a browser window opens behind it, with nothing on screen explaining the wait.

Acquisition order:

1. **Silent**, against the encrypted on-disk cache. With a cached token this is instant and
   silent, and the log says so.
2. **Interactive browser sign-in**, when `InteractiveFallback` is true and the process is
   interactive. A browser opens against `http://localhost`, which the app registration must
   list as a **Mobile and desktop applications** redirect URI.
3. **Device code**, when interactive sign-in is disabled or no browser is available. The code
   and URL are logged at warning level, so they are visible in a console or a CI log. Device
   code needs **Allow public client flows** set to Yes on the app registration, which
   `register-app.ps1` sets.

Local mode is a public client: it holds no secret and no managed identity, and needs none. The
federated credential belongs to the server on the VM.

Each identity gets its own MSAL public client, and every request uses the token for its
remote's identity and scopes. Tokens are held in memory and reused until five minutes before
expiry, and persisted to `{CacheDirectory}\devconduit.msal.cache`, DPAPI-protected on Windows.
All identities share that one file: MSAL keeps each client id's entries apart inside it, and
local mode serialises cache access across identities. A developer signs in once per identity
per machine, not once per process. If the cache cannot be opened, local mode logs a warning
and continues with an in-memory cache — an extra sign-in, not a failed request.

Concurrent requests are serialised around acquisition, so a cold start with ten parallel calls
opens one sign-in prompt rather than ten.

If sign-in fails at startup the host keeps running: the console stays reachable, the error is
visible in one place, and each relayed request reports the same failure with its own context.

---

## 06 Why HTTPS through the forward proxy is refused

A `CONNECT` to a host that matches a target rule is answered with `502` and a body that says
what to do instead:

```text
The client asked for a CONNECT tunnel to orders.internal.example.com:443, which means the
traffic is HTTPS from the client to that host. ... Rewriting the request would mean
terminating TLS here and re-issuing a certificate for orders.internal.example.com from a
certificate authority your runtime trusts. This tool deliberately does not generate,
install or trust one.

Use the reverse-proxy form for this host instead. ...
```

The workaround is ingress A. Point the client at
`http://127.0.0.1:8092/route/...` with an alias segment or an `X-DevConduit-Target` header, and
the same host works — the TLS leg to the upstream is then established by the remote instance,
which is where it belongs.

Plain `http://` requests to the same host through the forward proxy are relayed normally. Only
`CONNECT` is refused.

### 06.1 Why there is no certificate authority

Rewriting an HTTPS request means intercepting TLS, and intercepting TLS means presenting a
certificate the client accepts for a host you do not own. Producing that certificate is the
easy part. Getting it *trusted* is the part that does not scale, and the part that carries
the risk.

**Trust is per-runtime, and consent-gated by design.** Distributing a `.crt` file achieves
nothing on its own. Each runtime on the workstation has to be told to trust it, separately:

| Runtime | What installing trust means |
| --- | --- |
| Windows | Adding to the machine or user root store, which prompts, and which many managed workstations forbid |
| Node.js | `NODE_EXTRA_CA_CERTS` pointing at the file, set for every process that matters |
| Java | `keytool -importcert` into the JDK's `cacerts`, per JDK installation |
| Firefox, Thunderbird | Their own NSS database, separate from the operating system's |
| Containers, CI agents | Whatever the base image does, repeated per image |

Every one of those prompts is a security control working correctly. A tool that talks a
developer through disabling five of them has taught them a habit worth more to an attacker
than anything the tool provides.

**A shared CA would be a genuine compromise vector.** The tempting shortcut — generate one
authority, ship its key with the tool, have everyone install it — means every developer holds
a key that mints trusted certificates for *any* domain, valid on *every other* developer's
machine. One leaked repository, one laptop off a desk, and the blast radius is the whole team's
TLS. That is not a trade this tool is willing to make on a user's behalf.

**Per-machine generation stays a future option.** A CA generated locally, never leaving the
machine, installed once by an explicit `--install-cert` the developer runs knowingly, avoids
the shared-key problem entirely. It is a reasonable design. It is deliberately not built,
because the reverse-proxy form already covers the same hosts without asking anyone to add a
root of trust.

---

## 07 The Angular recipe

An Angular application needs no package and no code change. Point the dev server's proxy at
local mode, rewrite the path to `/route`, and name the upstream.

`proxy.conf.json`, header form:

```json
{
  "/api/orders": {
    "target": "http://127.0.0.1:8092",
    "secure": false,
    "changeOrigin": true,
    "pathRewrite": { "^/api/orders": "/route" },
    "headers": {
      "X-DevConduit-Target": "http://10.0.0.5:8080"
    }
  }
}
```

Route form, with `"Routes": { "orders": "http://10.0.0.5:8080" }` configured — no headers
at all:

```json
{
  "/api/orders": {
    "target": "http://127.0.0.1:8092",
    "secure": false,
    "changeOrigin": true,
    "pathRewrite": { "^/api/orders": "/route/orders" }
  }
}
```

Wire it up in `angular.json`:

```json
"serve": {
  "options": {
    "proxyConfig": "proxy.conf.json"
  }
}
```

A browser call to `/api/orders/services/orders/service.svr` now reaches
`http://127.0.0.1:8092/route/services/orders/service.svr`, local mode attaches the developer's
token and relays it to the remote instance, and the remote calls
`http://10.0.0.5:8080/services/orders/service.svr`.

No sign-in happens in the browser. That is the difference from pointing `proxy.conf.json`
straight at a server-mode instance, which needs a session cookie the dev server cannot supply.

Set `"cookieDomainRewrite": "localhost"` if the upstream sets cookies of its own. Note that
upstream-bound cookies are stripped by default — see
[ROUTING.md](ROUTING.md), section 02.

---

## 08 The shared secret

Local mode performs no inbound sign-in. That absence is what buys zero application changes,
and loopback is what makes it safe: nothing off the machine can reach the listener. Every
process *on* the machine can, though, and `DevConduit:Local:SharedSecret` narrows that to the
processes a developer chose to tell.

With a secret set, proxy ingress requires the header:

```bash
curl -i \
  -H "X-DevConduit-Local-Secret: $DEVCONDUIT_LOCAL_SECRET" \
  -H "X-DevConduit-Target: http://10.0.0.5:8080" \
  "http://127.0.0.1:8092/route/services/orders/service.svr"
```

and a request without it gets `401` with a body naming the header. It applies to `/route` and
to the forward proxy — the paths that carry application traffic. It does **not** apply to the
console UI or the `/api` surface, which stay reachable so the tool remains usable; the console
is a browser page and cannot attach a custom header to its own navigation.

Comparison is fixed-time. Unset — the default — means loopback is the only control.

For the Angular recipe, add the header alongside `X-DevConduit-Target` in `proxy.conf.json`.

---

## 09 Capture

Off by default. The remote instance already captures the exchange in full, and capturing at
both ends stores every request and response body twice.

Turn it on with `DevConduit:Local:CaptureEnabled` when you want the record on the workstation —
during an outage of the remote's console, say. The existing routed store, its caps
(`DevConduit:MaxRoutedHistoryEntries`, `DevConduit:MaxRoutedStoredBodyBytes`) and its redaction
rules apply unchanged.

History in local mode is filed under the fixed pseudo-user `local`:

```text
{DataRoot}\users\local\routed\index.json
{DataRoot}\users\local\routed\{entryId}.json
```

There is no Entra principal to take an object id from, so there is nothing else it could
reasonably be keyed on. The console served on `:8092` reads that store.

Each captured request records the remote it was relayed to as an `X-DevConduit-Remote` header
item, next to `X-DevConduit-Target`. It is recorded only; it is never sent.

---

## 10 Troubleshooting

| Symptom | First check |
| --- | --- |
| `DevConduit:Remotes is required` on a new install | Nothing has been imported yet. Import the server's `.conduit` (section 02.3), from the tray's Servers page, or give it to the MSI as `CONDUITCONFIG`. |
| The relay fails with a TLS or certificate error | The server's certificate is neither trusted by Windows nor pinned for its host: import its `.conduit` again. The log shows whether the last `/cert` sync failed and why. |
| `Dev.Conduit cannot start in LOCAL mode` | Read the list. Each entry names the setting and why it matters. `DevConduit:Remotes is required` after an upgrade means the endpoint is still under the removed `DevConduit:Local:RemoteEndpoint`. |
| A startup warning says a setting has been removed | Move it as the warning says: aliases to `DevConduit:Routes`; the endpoint, tenant, client id, scopes and targets to the remote under `DevConduit:Remotes`. |
| The tray's icon is red after Start local proxy | The configuration refused to start: the notification names the first problem, View log lists them all, and the setup window's Routes page shows them as you edit. |
| The tray shows the server role on a workstation | The `Dev.Conduit` service is installed on this machine. The tray manages it instead of running local mode; run `--mode local` from a console, or uninstall the service. |
| The banner says `SERVER` | `--mode local` was not passed and `DevConduit:Mode` is not `Local`. |
| `502` with `"error": "No Entra token can be acquired for the remote ..."` | No tenant or client id for that remote: `AzureAd:TenantId` / `ClientId` are unset and the remote under `DevConduit:Remotes` gives none. |
| `400 no_remote` | Several remotes, no `DevConduit:DefaultRemote`, and the target matches no route and no `Targets` rule. Set a default or add a rule. See section 02.2. |
| `401 remote_unauthorized` | The token this machine holds is not one the named remote accepts. Confirm both ends name the same app registration, then delete the token cache. |
| `403 remote_forbidden` | The scope is missing from the token, or the account is not on the named remote's allow list. |
| `502` refusing a `CONNECT` | The host matches a target rule and the request is HTTPS. Use the reverse-proxy form. See section 06. |
| `501` on a request with a body | A chunked request body through the forward proxy. Send `Content-Length`, or use `/route`. |
| `400` naming the absolute form | Something addressed the forward-proxy port as an ordinary web server. The reverse proxy is the other port. |
| Sign-in prompt on every start | The token cache could not be opened; look for the warning naming the directory. |
| A request went straight out, unrouted | No rule matched. Rules are matched on host and port, never on path. The startup log lists the rules per remote. |
| A request went to the wrong remote | A route claiming the host wins over a `Targets` rule, and the first remote's rule wins among `Targets`. See section 02.2. |
| Traffic loops or times out | `HTTP_PROXY` is set in the environment local mode itself runs in. Its own outbound calls ignore it, but the application's may not resolve as expected. |
