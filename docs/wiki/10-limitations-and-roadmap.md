# Limitations and roadmap

[Documentation](README.md) / Limitations and roadmap

LanCache Router currently supports routing by client network, ordered standby caches, and health checks. Check these boundaries when deciding how it fits your setup.

## Supported boundaries

| Area | Current behavior |
| :--- | :--- |
| Cache addresses | One IPv4 address per server entry. Native IPv6 clients can match routes, but need IPv4 connectivity to use the returned cache address. |
| Server selection | One server per route at a time: the first Healthy entry. There is no round-robin or weighted load balancing. |
| Service routing | All enabled services for a client use its selected cache. A single client cannot route Steam to one cache and Epic to another. |
| Reachability | Probes test from the DNS node. They do not check the client's network path or actual download performance. |
| Visibility | State and decisions are available through logs. There is no status page or diagnostic TXT endpoint. |
| DNS types | A, AAAA, HTTPS, and SVCB are handled. Other types use normal resolution. |
| DNSSEC | Synthesized answers are unsigned; independent client validation cannot authenticate the rewritten public name. |
| DNS source address | The app needs the address seen by Technitium to distinguish networks. EDNS Client Subnet is not used. |
| Compatibility | The app is built against Technitium's app assemblies. Use a compatible release or build against your deployment. |

## Plan for DNS timing

A failover changes new answers after health checks detect the failure. Clients may hold earlier answers until their TTL expires, and existing connections are not moved. The app cannot make an ongoing download switch immediately to another server.

AAAA, HTTPS, and SVCB responses are empty by default for eligible cache domains. Passing these types through is configurable, but may give clients a public path around the cache. See [DNS answers](08-dns-answers.md).

## Details to keep in mind

- Routes use explicit priority: the first match wins, including when its servers are unavailable. Put exceptions before wider networks.
- Wildcards include subdomains, but exclude their base name. Add both forms if both are needed.
- Probed caches start Unknown after a restart, address change, or effective health-check change. Unchanged servers retain state across ordinary configuration saves.
- Domain filters are reapplied from the current list without a new download. A changed source triggers a refresh, while the existing in-memory list can remain active until it succeeds.
- A first installation needs a successful base-list download before manual extra domains become active.
- Each cluster node has independent health state and a domain snapshot. Snapshot persistence covers ordinary restarts; package updates can replace app-folder state.

## Roadmap

The [backlog](../backlog.md) records three deferred areas:

| Item | Intended direction |
| :--- | :--- |
| B1 — IPv6 cache support | Return IPv6 cache addresses. |
| B2 — Per-service routing | Choose different caches for different services within a client route. |
| B3 — Diagnostic TXT query | Inspect health through a read-only DNS query. |

These are recorded directions without a release schedule. If one matters to your deployment, describe the use case when opening a feature request. The [design records](../adr/README.md) provide background on the current choices.

---

**Continue:** [Install the app](03-installation.md) · [Plan your routes](05-routing.md) · [Return to documentation](README.md)
