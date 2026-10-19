# ADR 0001 – Technitium extension point and overall design

Status: Accepted

## Context
Current stack: dnsdist routes queries by source subnet to lancache-dns (which rewrites *all* cache
domains unconditionally) or to Technitium. lancache-checker toggles the dnsdist backend via the control
socket. Problems: three extra containers, one global on/off switch, one cache server, a hard-coded subnet.

Technitium apps are .NET class libraries loaded by the server, configured by a `dnsApp.config` JSON blob
edited in the web UI. Relevant interfaces in `DnsServerCore.ApplicationCommon`:

- `IDnsApplication` – lifecycle (`InitializeAsync(IDnsServer, string config)`, `Dispose`).
- `IDnsRequestBlockingHandler` – used by *Advanced Blocking*; runs **before** the resolver cache. Two
  methods (verified): `Task<bool> IsAllowedAsync(request, remoteEP)` (we always return `false`) and
  `Task<DnsDatagram?> ProcessRequestAsync(request, remoteEP)`; null = continue. No transport parameter.
  `IDnsApplication` also requires a `Description` property.
- `IDnsAuthoritativeRequestHandler` – **not** zone-only (earlier assumption was wrong): it fires for every
  query no local zone answers, before the RD/recursion gate and before the blocking pipeline. Rejected
  because Advanced Blocking allow-lists could not override it, answers are tagged Authoritative, and the
  core may post-process the response. Revisit only if RD=0 / non-recursion clients must be served.
- `IDnsRequestController` can only allow/drop (no synthesized answers); `IDnsPostProcessor` runs after
  resolution. Both wrong fits.

Verified against DnsServer source (master, 2026-10-19; permalink to a commit before citing line numbers).

## Decision
Implement the app as an **`IDnsRequestBlockingHandler`** (the same slot Advanced Blocking uses), because
it sees every query with the real client address, before cache/recursion, and can answer directly.
Flow per query:

1. Type is A/AAAA(/HTTPS, see 0005) and class IN? Otherwise return null (continue normally).
2. Route lookup by client IP (ADR 0004) → server/behavior. Passthrough → return null.
3. Domain lookup in the in-memory index (ADR 0002). No match → return null.
4. Resolve the address of the chosen server, if Healthy (ADR 0003), applying failover (ADR 0004).
5. Build and return the synthesized response (ADR 0005); if nothing usable → return null (normal DNS).

Components (all in one assembly, no external services):
`DomainListManager` (fetch+cron+index) · `CacheServerRegistry` + `HealthMonitor` (background probes) ·
`RoutingTable` · `QueryHandler` (the interface implementation) · `ConfigModel` (parse/validate).

Hot path is lock-free: domain index, routing table and health snapshot are immutable objects swapped
atomically (`Volatile.Write`) by background tasks.

## Consequences
- dnsdist, lancache-dns and lancache-checker are removed; Technitium listens on :53 directly.
- Client IP must reach Technitium unmodified. Docker bridge NAT hides it → use host/macvlan networking,
  or a PROXY-protocol front. PROXY is parsed only on Technitium's dedicated UDP/TCP PROXY listeners (your
  port 538), and the sender must be in the reverse-proxy network ACL – not on port 53.
- Fail-open: any app error → return null; the core also logs handler exceptions and continues.
- Gates before our handler: no local auth/special zone answers the name, RD=1, and recursion allowed for
  the client. Class IN and exactly one question are guaranteed.
- Ordering: handlers are sorted by `IDnsApplicationPreference.Preference` (byte, lower first, default 100,
  ties undefined). We implement it with `Preference = 150`, so we run after Advanced Blocking (default 100) and its blocks win.
  `IsAllowedAsync` is called on all handlers first: an Advanced Blocking allow rule or bypass network
  skips our handler for that query. First non-null response wins.
- Synthesized answers are returned before `RecursiveResolveAsync` and not stored in the resolver cache
  (from code flow, not an explicit statement). They are tagged `Blocked` in stats/logs unless we set `Tag`.
- Packaging: `net10.0`; reference ApplicationCommon + TechnitiumLibrary(.Net) with `Private=false`; build
  against the deployed server version. Zip must be flat (`<Name>.dll` + `<Name>.deps.json` at root); one
  public class with a parameterless ctor. `InitializeAsync` is re-called on the same instance on every
  config save and a thrown exception rejects the config (old file kept) → make it idempotent, stop old
  timers/probes, validate then swap snapshots. Writable state goes in subfolders of `ApplicationFolder`;
  install wipes the folder, update preserves `dnsApp.config`. Outbound HTTP should use `IDnsServer.Proxy`.
- Clustering (decided): runs in a Technitium cluster. Configuration is shared – app zip and
  `dnsApp.config` replicate from the primary, and secondaries re-run `InitializeAsync` with the same
  config. Runtime state is per node and in memory: health states, probe counters, and the downloaded domain
  list (each node runs its own cron refresh and keeps its own snapshot file in its `ApplicationFolder`).
  Consequences: the config must not contain node-specific values; cron times are identical on all nodes,
  so add small random jitter to list refreshes; a node may briefly differ from another in health state.
  Implication for routing: probes originate from each DNS node, so each node must be able to reach the
  cache servers.
- Scope: IPv4 only for v1, future work is in [backlog](../backlog.md).

## Implementation notes
- Types and members were checked by reflection against the DLLs of the target Technitium build (ApplicationCommon 10.2,
  TechnitiumLibrary 7.2, TechnitiumLibrary.Net 15.2): `IDnsRequestBlockingHandler`, `IDnsApplicationPreference`, `DnsDatagram` ctor
  and `Tag`, `DnsServerResponseType`. Build against the same Technitium version you deploy on.
- `IsAllowedAsync` always returns `false`: `true` would make Technitium skip all other blocking handlers for that query.
- Domain downloads use `HttpClientNetworkHandler` with `IDnsServer.Proxy` and `IPv6Mode`, like Technitium's own list downloads.
- The package ships an inert `dnsApp.config` (`"enabled": false`, TEST-NET addresses); a missing config also means inactive.
- Invalid config on save: `InitializeAsync` throws with all problems listed; Technitium shows the message and the previous
  configuration stays active.
