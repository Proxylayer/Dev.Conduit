# Transparent Request Forwarding

`/route` relays a request to an upstream service without altering it. The verb, path,
query string, headers and body arrive at the upstream as the caller composed them, and
the upstream's status, headers and body come back untouched. Dev.Conduit records the whole
exchange against the developer whose token authorized it.

This is a different contract from `POST /api/proxy/execute`, which composes a request
from a JSON description for the console UI. Use `/route` when existing code should keep
calling its normal address and simply travel through Dev.Conduit on the way.

The same endpoint exists in both modes. In server mode the next hop is the upstream; in
local mode it is a remote Dev.Conduit instance, which then performs the upstream call. The URL
contract, the header rules and the streaming behaviour below are identical either way; where
local mode differs it is called out, and [LOCAL-MODE.md](LOCAL-MODE.md) covers the rest.

---

## 1. URL Translation

The upstream URI is the target origin, then the remaining path, then the original query
string verbatim. The port lives only in the origin; it is never repeated in the path.

Original call:

```text
http://10.0.0.5:8080/services/orders/service.svr?wsdl
```

Routed through Dev.Conduit:

```text
POST http://localhost:8091/route/services/orders/service.svr?wsdl
X-DevConduit-Target: http://10.0.0.5:8080
```

Dev.Conduit reconstructs:

```text
http://10.0.0.5:8080/services/orders/service.svr?wsdl
```

### Alias form

An alias replaces the header with a leading path segment. Aliases are the names under
`DevConduit:Routes`. With this configuration:

```json
"DevConduit": {
  "Routes": { "orders": "http://10.0.0.5:8080" },
  "Route": {
    "AliasesOnly": false,
    "TimeoutSeconds": 100
  }
}
```

the same call becomes:

```text
POST http://localhost:8091/route/orders/services/orders/service.svr?wsdl
```

The `orders` segment is consumed; everything after it is the upstream path.

A route is either a string, the upstream origin, or an object whose `Target` is the origin.
The object form exists for local mode, where `Remote` names the remote Dev.Conduit instance that
carries the route (see [LOCAL-MODE.md](LOCAL-MODE.md), section 02):

```json
"Routes": {
  "orders":      "http://10.0.0.5:8080",
  "orders-prod": { "Target": "http://10.50.1.10:8080", "Remote": "prod" }
}
```

Server mode reads `Target` and ignores `Remote`. Route names are unique and case-insensitive.

### Target resolution order

| Order | Rule | Consumes |
| --- | --- | --- |
| 1 | First remaining path segment matches a route name under `DevConduit:Routes`, case-insensitively | That segment |
| 2 | `X-DevConduit-Target` header names the origin | Nothing |
| 3 | Neither applies | `400` with an explanatory body |

A resolved origin must be an absolute `http` or `https` URI carrying no path, query,
fragment or embedded credentials. When `DevConduit:Route:AliasesOnly` is `true`,
`X-DevConduit-Target` is refused and only aliases resolve.

### Allowed targets

Local mode always names its target in `X-DevConduit-Target`, so a server that local-mode clients
use cannot set `AliasesOnly`. `DevConduit:Route:AllowedTargets` narrows what such a server will
call instead. Each entry is one of:

| Entry | Example | Matches |
| --- | --- | --- |
| Host name | `api.internal.example` | That name exactly, ignoring case |
| Wildcard host | `*.internal.example` | Every name below the domain, not the domain itself |
| IP address or CIDR network | `10.0.0.5`, `10.0.0.0/16`, `fd00::/8` | The address actually connected to |

Any entry may end in `:port` to allow only that port; an IPv6 address or network with a port is
bracketed, `[fd00::/8]:443`. Every configured route's target is allowed besides, without an
entry. An empty list allows every host not in `DevConduit:BlockedEgressNetworks`, and startup
warns that it does.

The list is applied twice. After the target resolves, a server refuses a target the list does
not cover with `403` and `errorKind` `target_not_allowed`, before any upstream call is built: an
IP literal must lie in a network entry, and a host name must match a host name entry, unless a
network entry might cover the addresses it resolves to. Then, at connect time, in the same step
that applies the blocked networks, the name dialled is allowed by a host name entry or a route
target whatever it resolves to; otherwise every address it resolved to must lie in a network
entry. A DNS answer, or a rebind, outside the networks is refused the same way, with the same
`403`. Local mode does not apply the list; the server it relays to does. README section 08.9 has
the security view.

In local mode the resolved origin is then given a remote Dev.Conduit instance to travel through:
the remote of the route that named or matches it, else of a remote whose `Targets` rule
matches it, else the default remote. When none applies — several remotes, no
`DevConduit:DefaultRemote`, nothing claims the origin — the answer is `400` with `errorKind`
`no_remote`. Server mode has no remotes and skips this step. See
[LOCAL-MODE.md](LOCAL-MODE.md), section 02.2.

Any HTTP verb is accepted, including custom ones. The endpoint is a terminal middleware
branch rather than a mapped route precisely because route patterns cannot express an
open verb set.

---

## 2. Header Rules

### Request headers

| Header | Treatment |
| --- | --- |
| `Authorization` | **Stripped.** This is Dev.Conduit's own Entra token and must never reach the upstream. |
| `X-DevConduit-Upstream-Authorization` | **Renamed to `Authorization`** on the outbound request. |
| `X-DevConduit-Target` | Consumed by Dev.Conduit, not forwarded. |
| `X-DevConduit-Client` | Consumed by Dev.Conduit, not forwarded. |
| `X-DevConduit-Local-Secret` | Consumed by Dev.Conduit, not forwarded. It authenticates a local listener and has no meaning beyond it. |
| `Cookie` | **Dropped**, unless `DevConduit:Route:ForwardCookies` is `true`. Dev.Conduit's own cookies are removed either way. See below. |
| `Host` | Rewritten to the upstream authority. |
| `Connection`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade` | Dropped as hop-by-hop, along with any header named in `Connection`. |
| `Content-Length` | Taken from the inbound framing rather than copied. |
| Everything else | Forwarded verbatim, split between request and content headers as the runtime requires. |

The rename is the whole point of the two-Authorization design: the caller keeps one
credential for Dev.Conduit and a separate one for the upstream, and neither leaks into the
other's request.

### Cookies

A browser attaches every cookie it holds for the Dev.Conduit origin to every request it sends
there, `/route` calls included. On a browser-driven route that set contains `DevConduit.Auth`,
the console's own session cookie. Forwarding it verbatim would hand each upstream a
replayable Dev.Conduit session — a session that can execute arbitrary outbound requests from the
Dev.Conduit host.

Two rules, applied in both modes:

1. **Dev.Conduit's own cookies are always removed.** `DevConduit.Auth`, the antiforgery cookie, and
   the OIDC correlation and nonce cookies never leave, and there is no setting that lets them.
2. **The `Cookie` header is dropped entirely by default.** `DevConduit:Route:ForwardCookies`
   (default `false`) turns passthrough back on for a caller that genuinely needs upstream
   cookies. Rule 1 still applies when it does.

An upstream that authenticates with a cookie therefore needs `ForwardCookies` set, and is
better served by `X-DevConduit-Upstream-Authorization` where the upstream supports it.

### Response headers

Status code and all response headers are copied through, minus the hop-by-hop set.
Content is relayed with whatever `Content-Encoding` the upstream chose; Dev.Conduit does
not decompress on the caller's behalf. Redirects are relayed as `3xx` responses rather
than followed.

---

## 3. Streaming

Request and response bodies are streamed, never buffered. The request body is wrapped in
a pass-through reader as `HttpClient` consumes it; the response is read with
`HttpCompletionOption.ResponseHeadersRead` and copied to the caller in 64 KB chunks.
Memory does not scale with payload size, and a large SOAP response starts arriving
before the upstream has finished sending it.

---

## 4. Failures

`/route` answers with proxy semantics, not console semantics.

| Condition | Status | Body |
| --- | --- | --- |
| No target could be resolved, or the target is malformed | `400` | `{ "errorKind": "no_target" \| "invalid_target" \| "aliases_only", "error": "..." }` |
| Caller is unauthenticated | `401` with `WWW-Authenticate: Bearer`, or `302` to sign-in for a browser | empty |
| Token lacks the required scope, or the principal is not on the allow list | `403` | `{ "errorKind": "insufficient_scope" \| "not_allowed", "error": "..." }` |
| The upstream resolves into `DevConduit:BlockedEgressNetworks` | `400` | `{ "errorKind": "Blocked", "error": "...", "target": "...", "upstreamUrl": "..." }` |
| The target, or an address it resolved to, is not covered by `DevConduit:Route:AllowedTargets` | `403` | `{ "errorKind": "target_not_allowed", "error": "..." }`, with `target` and `upstreamUrl` when refused at connect time |
| Upstream DNS, TLS, connection or timeout failure | `502` | `{ "errorKind": "Dns" \| "Tls" \| "Connection" \| "Timeout", "error": "...", "target": "...", "upstreamUrl": "..." }` |
| Local mode only: `DevConduit:Local:SharedSecret` is set and the request did not present it | `401` | `{ "errorKind": "local_secret_required", "error": "..." }` |
| Local mode only: the remote instance refused this machine's token | `401` or `403` as the remote sent it | `{ "errorKind": "remote_unauthorized" \| "remote_forbidden", "error": "...", "target": "..." }` |

`Blocked` is a `400`, not a `502`, because it is a request Dev.Conduit will not route, like the
`aliases_only` refusal; `502` stays reserved for an upstream that could not be reached. The
check runs at connect time against the resolved address, so a hostname, a DNS rebind or a
redirect into a blocked network is caught the same way. The `error` names the host, the address
and the setting, and routed history records the attempt with error kind `Blocked`. The block
applies to server mode's upstream calls only, not to local mode's relay to the remote Dev.Conduit.
See the README, section 08.5.

Per-request timeout comes from `DevConduit:Route:TimeoutSeconds` (default `100`, clamped
to `600`) and is linked to the caller's own cancellation, so a caller who disconnects
does not leave an upstream call running.

---

## 5. Authentication

This section describes server mode. Local mode authenticates no inbound request at all —
that absence is what lets an application call it with no token and no code change — and
relies on its loopback bind, optionally backed by a shared secret. See
[LOCAL-MODE.md](LOCAL-MODE.md), section 08.

Two schemes coexist. The browser console keeps its cookie session; `/route` accepts
either a bearer token or that same cookie, so a signed-in developer can exercise the
endpoint by hand from the browser or from `curl` with a session cookie.

Bearer callers must present a delegated scope, configured at `AzureAd:Scopes` and
defaulting to `DevConduit.Forward`. Cookie callers are exempt: a cookie session is backed
by an id_token, which carries no scope claim.

The scope the registration exposes and the scope the server accepts must be the same name. The
server's first scope travels to clients in every `.conduit` it exports (`scope`), and an import
makes the workstation request `api://<client-id>/<scope>` for that server, so clients never
configure it themselves. A registration that exposes another name, such as the older
`DevProxy.Forward`, keeps working once the server's Scope (setup window, Server page) is set to
that name and a new `.conduit` is exported and imported; clients that still request
`DevConduit.Forward` from it fail with `AADSTS65005`. See the README, section 03.1.

### App registration: Expose an API

The app registration lives in the server's tenant, beside the VM, and is multi-tenant; users
sign in from their own tenant, the users' tenant. `register-app.ps1` creates and converges
it, including every step below, and the README, section 03, explains the two-tenant
arrangement. The steps are listed for reading and for repair by hand.

The forwarding endpoint expects tokens whose audience is `api://<client-id>`. In the
Dev.Conduit app registration:

1. **Expose an API** → **Application ID URI** → set it to `api://<client-id>`.
2. **Add a scope**:
   - Scope name: `DevConduit.Forward`, or whatever `AzureAd:Scopes` names on the server
   - Who can consent: **Admins and users**
   - Admin consent display name: `Forward requests through Dev.Conduit`
3. **Authentication** → add a **Mobile and desktop applications** platform with the
   redirect URI `http://localhost`, so local mode can sign a developer in interactively.
4. **Authentication** → **Allow public client flows** = Yes, so local mode can fall back to
   device-code sign-in.
5. **API permissions** → add the `DevConduit.Forward` scope from step 2 under
   **My APIs**. Consent is needed in the users' tenant: an administrator there opens the
   admin consent link the script prints.

The existing **Web** platform redirect URI `http://localhost:8091/signin-oidc` stays as
it is; it is what the browser console uses.

---

## 6. Capture

Routed traffic is captured **in full, including request and response bodies**. The
volume risk is real and was accepted deliberately; these are the mitigations.

| Concern | Mitigation |
| --- | --- |
| Routed volume evicting console history | A separate store at `{DataRoot}\users\{oid}\routed\`, with its own index and its own cap. In local mode `{oid}` is the fixed pseudo-user `local` |
| Unbounded growth | `DevConduit:MaxRoutedHistoryEntries` (default `5000`) and `DevConduit:MaxRoutedStoredBodyBytes` (default `2097152`) |
| Capture interfering with streaming | Bodies are teed into a capped buffer that stops retaining once the cap is hit and keeps counting; the caller always receives the complete, unmodified response |
| Secrets on disk | `Authorization`, `X-DevConduit-Upstream-Authorization`, `Cookie`, `Set-Cookie` and the rest of `DevConduit:SecretHeaderNames` are stored redacted |
| Capture failing a request | Entries are queued and written on a background loop; a capture error is logged and swallowed, never surfaced to the caller |
| Latency | Serialization and storage happen off the response path, after the bytes have reached the caller |

A body larger than the cap is stored truncated with a flag; textual payloads are stored
as text so they are searchable, everything else as base64.

Local mode does not capture by default: the remote instance it relays to already holds the
authoritative record, and capturing at both ends stores every body twice.
`DevConduit:Local:CaptureEnabled` turns it on, with the same store and the same caps.

### Inspecting captured traffic

These sit under the existing `/api` group, so they use the browser cookie and require
the `X-DevConduit-Client: 1` header.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/routed?skip=&take=&q=` | Paged summaries, same envelope as `/api/history` |
| `GET /api/routed/{id}` | One full entry with headers and bodies |
| `DELETE /api/routed/{id}` | Purge one entry |
| `DELETE /api/routed` | Purge all routed traffic for the current user |
| `GET /api/storage` | Byte usage and entry counts for every store |

---

## 7. Worked Examples

A GET through an explicit target, with a session cookie for the browser-authenticated
path:

```bash
curl -i \
  -H "X-DevConduit-Target: http://10.0.0.5:8080" \
  -b "DevConduit.Auth=<cookie value>" \
  "http://localhost:8091/route/services/orders/service.svr?wsdl"
```

The same call from a programmatic caller holding a bearer token:

```bash
curl -i \
  -H "Authorization: Bearer $DEVCONDUIT_TOKEN" \
  -H "X-DevConduit-Target: http://10.0.0.5:8080" \
  "http://localhost:8091/route/services/orders/service.svr?wsdl"
```

A SOAP POST through the `orders` alias, passing separate credentials to the upstream:

```bash
curl -i -X POST \
  -H "Authorization: Bearer $DEVCONDUIT_TOKEN" \
  -H "X-DevConduit-Upstream-Authorization: Basic dXNlcjpwYXNz" \
  -H "Content-Type: text/xml; charset=utf-8" \
  -H "SOAPAction: urn:Submit" \
  --data-binary @request.xml \
  "http://localhost:8091/route/orders/services/orders/service.svr"
```

The upstream sees `Authorization: Basic dXNlcjpwYXNz`. It never sees the Entra token.

A custom verb, which is why the endpoint is a middleware branch:

```bash
curl -i -X PROPFIND \
  -H "Authorization: Bearer $DEVCONDUIT_TOKEN" \
  -H "X-DevConduit-Target: http://10.0.0.5:8080" \
  "http://localhost:8091/route/dav/collection"
```

---

## 8. Configuration Reference

| Key | Default | Meaning |
| --- | --- | --- |
| `DevConduit:Routes` | `{}` | Route name to upstream: a string origin, or `{ "Target": origin, "Remote": name }`. The name is the alias segment. Read in both modes; server mode ignores `Remote`. In local mode each origin is also a forward-proxy target rule for its remote. An object with `"Enabled": false` is left out, as if absent |
| `DevConduit:Route:AliasesOnly` | `false` | Refuse `X-DevConduit-Target`, accept aliases only |
| `DevConduit:Route:ForwardCookies` | `false` | Allow non-Dev.Conduit cookies through to the upstream |
| `DevConduit:Route:TimeoutSeconds` | `100` | Per-request upstream timeout, clamped to `600` |
| `DevConduit:Route:AllowedTargets` | `[]` | Server mode. Host names, `*.` wildcards, IP addresses and CIDR networks, each with an optional `:port`, that upstream calls may reach; every route's target is allowed besides. Empty allows every host not blocked, with a startup warning; malformed entries are logged at startup and ignored |
| `DevConduit:MaxRoutedHistoryEntries` | `5000` | Routed entries retained per user |
| `DevConduit:MaxRoutedStoredBodyBytes` | `2097152` | Bytes of each routed body retained |
| `DevConduit:BlockedEgressNetworks` | `169.254.0.0/16`, `168.63.129.16/32`, `fe80::/10` | CIDR networks upstream calls may never connect to. Configured entries add to the defaults; malformed ones are logged at startup and ignored |
| `AzureAd:Scopes` | `["DevConduit.Forward"]` | Delegated scopes accepted on bearer tokens; the first is exported to clients in the `.conduit` |

Routes are validated at startup. A malformed origin is logged at error level, named
with its reason, and dropped; the host still starts. If `AliasesOnly` is `true` and no
usable route survives validation, startup warns that `/route` will refuse everything.

`DevConduit:Route:Aliases` has been removed; startup warns if it is still present and names
`DevConduit:Routes` as the replacement. The local-mode keys `DevConduit:Remotes` and
`DevConduit:DefaultRemote` are described in [LOCAL-MODE.md](LOCAL-MODE.md), section 02.
