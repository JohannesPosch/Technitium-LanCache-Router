# 11. Limitations and roadmap

## Limitations of version 1

| Limitation | Why / consequence | Planned |
|---|---|---|
| **IPv4 caches only.** A server has one IPv4 address. | AAAA/HTTPS queries for cache domains are answered empty so clients stay on the cache. | [B1](../backlog.md) |
| **One address per server.** | Use several servers in a route's `servers` list for redundancy. | – |
| **One server per route at a time** (the first Healthy one of an ordered list). No load balancing between servers. | Strict priority order is the decided model. | – |
| **No per-service routing.** All services of a matched client go to the same server. | e.g. "Steam → cache A, Epic → cache B" is not possible yet. | [B2](../backlog.md) |
| **State is visible in logs only.** | No status page or diagnostic DNS name. | [B3](../backlog.md) (diagnostic TXT query) |
| **Health is checked from the DNS server.** | A path problem that only affects clients is invisible. | – |
| **Only A, AAAA, HTTPS, SVCB are handled.** | Other record types are never cache redirected (they are not needed). | – |
| **Answers are not DNSSEC-signed.** | Same as any DNS cache redirect. | – |
| **Needs the real client address in Technitium.** | See [Installation](03-installation.md#client-addresses). | – |
| **Needs a matching Technitium build.** | The app is compiled against Technitium's app API. Rebuild against the new `DnsServerCore.ApplicationCommon` when you upgrade Technitium. | – |

## Behaviors that surprise people

- A wildcard such as `*.example.com` does **not** match `example.com`.
- The first matching route wins, not the most specific one.
- A server is *Unknown* (and not used) for a few seconds after every start or change.
- Changing the `baseUrl` downloads at once; changing filters does not download.
- Cluster nodes each have their own health state and domain list.
- Technitium statistics may label the answers differently than you expect (they are tagged *Authoritative*).

## Roadmap

See the [backlog](../backlog.md) for the list of deferred features. Items are added there when something is
consciously left out of the current version.
