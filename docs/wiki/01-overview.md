# Overview

[Documentation](README.md) / Overview

A local download cache is most useful when clients can reach it quickly and reliably. LanCache Router adds that decision to Technitium DNS Server: which network should use which cache, and is that cache ready to serve it?

## Where it fits

You run Technitium for DNS and LanCache for download storage. The app connects the two through DNS answers. For a listed download domain, a client on a routed network receives the selected cache's IPv4 address. The client then connects directly to the cache.

If that cache becomes unhealthy, the app selects a healthy standby from the route. If there is no standby available, it leaves the lookup to Technitium. Normal resolution can then return the service's public download address, subject to your existing DNS rules.

This works well for a LAN party, a gaming room, or several VLANs with different access to shared storage. For example:

| Client network | Your routing choice |
| :--- | :--- |
| Gaming PCs | Use the nearby cache, with a second cache as standby. |
| Another building | Use that building's cache first. |
| Guests and servers | Keep normal DNS resolution. |
| One test machine | Exempt it from its subnet's cache route. |

## What you need

You need a compatible Technitium DNS Server installation, a working LanCache server, and clients that send their DNS queries to Technitium. The DNS server must see their source addresses to distinguish networks and must be able to reach the caches for health checks.

The app obtains download hostnames from the community cache-domain list and can keep a saved copy through restarts. You can select services or add and exclude individual domains.

## What to expect

| Feature | Behavior |
| :--- | :--- |
| Network selection | Explicit routes match a client's IPv4 or IPv6 subnet. Unmatched clients use normal DNS. |
| Server selection | Each route uses its first healthy cache. Later entries are standbys. |
| Health checks | TCP, HTTP, or HTTPS probes run from each DNS node. |
| Domain updates | Scheduled downloads, service filters, and manual additions. |
| Recovery | A preferred cache returns to use after passing its recovery checks. |

DNS changes take effect on subsequent lookups. Cached answers may remain in use until their TTL expires, and connections already open to a cache continue independently. The app does not measure download speed or distribute traffic evenly across servers.

Cache server addresses are currently IPv4 only. By default, matching AAAA, HTTPS, and SVCB queries receive empty answers so clients use the cache's IPv4 address. See [DNS answers](08-dns-answers.md) and [limitations](10-limitations-and-roadmap.md) for the details.

## A few terms used in these guides

| Term | Meaning |
| :--- | :--- |
| Cache domain | A hostname or wildcard in the active download-domain list. |
| Service | A named group of domains, such as `steam` or `epicgames`. |
| Cache server | A named cache address and its health-check settings. |
| Route | A client subnet, a priority, and an ordered list of cache servers or a passthrough rule. |
| Passthrough | The app leaves the query to Technitium's normal processing. |
| Healthy | The server has met the configured success threshold and can be selected. |

---

**Next:** [Install your first cache route](03-installation.md) or [follow a DNS lookup](02-how-it-works.md).
