# 1. Overview

## What problem does it solve?

A LanCache stores game downloads locally. To use it, clients must resolve the download hostnames
(`cdn.steamcontent.com`, `*.download.epicgames.com`, …) to the **cache server's IP address instead of the real CDN**.
That is done in DNS. The classic way is a DNS server that rewrites those names unconditionally (for example the stock `lancache-dns`).

That has three weaknesses:

1. **It does not know whether the cache is alive.** If the cache server is down, clients are still told to use it, and
   downloads fail instead of falling back to the internet.
2. **It treats every client the same.** If only some VLANs have a fast path to the cache (for example no firewall in
   between), the others get sent to it anyway and may bottleneck on a firewall.
3. **It supports one cache.**

LanCache Router addresses all three inside the DNS server itself, as **one Technitium app**.

## What the app does

| Capability | In short |
|---|---|
| **Domain list** | Downloads the list of cacheable domains from [uklans/cache-domains](https://github.com/uklans/cache-domains) on a schedule you set with a cron expression. |
| **Cache servers** | You define any number of cache servers, each with one IPv4 address and a health check. |
| **Health checking** | Every enabled server is probed (TCP or HTTP). It is only handed out while **Healthy**. |
| **Routing table** | A table of *client network → cache server* rows, each with a priority, a behavior and an ordered list of servers (the later ones are failovers). |
| **Safe by default** | Clients that match no row, domains that are not in the list, and unhealthy caches are all handled by normal DNS. |

## What the app does *not* do

- It does not cache anything itself. It only decides which IP address a DNS answer contains.
- It does not move traffic. Clients connect to the cache directly.
- It does not touch queries that are not for cache domains, or from clients that are not routed.
- It does not manage the LanCache server (monolithic container), only checks that it answers.

## Concepts

| Term | Meaning |
|---|---|
| **Cache domain** | A hostname from the domain list, for example `lancache.steamcontent.com`. Belongs to a *service* such as `steam`. |
| **Service** | A group of domains for one platform (`steam`, `epicgames`, `blizzard`, `wsus`, …), named in `cache_domains.json`. |
| **Cache server** | An entry in `cacheServers`: a name, one IPv4 address and a health check. |
| **Route** | One row of the routing table. |
| **Passthrough** | The app does nothing; Technitium resolves the query as it always would. |
| **Healthy / Unknown / Unhealthy / Disabled** | The state of a cache server. Only **Healthy** servers are used. See [Health checks](06-health-checks.md). |
| **Failover** | A route lists its servers in order of preference; if the first is not Healthy the next one is used, and so on. |
