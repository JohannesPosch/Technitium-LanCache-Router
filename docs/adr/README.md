# Architecture Decision Records – Technitium LanCache Router App

Goal: replace the `dnsdist + lancache-dns + lancache-checker` chain with **one Technitium DNS Server app**
that (a) knows which domains are cacheable (uklans/cache-domains), (b) decides per client CIDR which
cache server (if any) to answer with, and (c) only answers with cache IPs that are currently healthy.

| #    | Title                                             | Status   |
|------|---------------------------------------------------|----------|
| 0001 | Technitium extension point and overall design     | Accepted |
| 0002 | Cache-domain list sourcing and cron refresh       | Accepted |
| 0003 | Cache servers, addresses and health states        | Accepted |
| 0004 | CIDR routing table, behavior and failover         | Accepted |
| 0005 | DNS response semantics                            | Accepted |
| 0006 | Configuration schema (`dnsApp.config`)            | Accepted |

Deferred features: see [../backlog.md](../backlog.md).

User documentation (what the app does and how to configure it): [../wiki/README.md](../wiki/README.md).

`Proposed` = open for discussion. Technitium API assumptions in 0001/0002/0005 were verified against the
DnsServer source on 2026-10-19; the few remaining **[VERIFY]** items are unconfirmed details.
Open questions are collected at the bottom of each ADR.
