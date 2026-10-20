# LanCache Router documentation

Get your first cache working, then shape routing and failover around your network.

LanCache Router runs inside Technitium DNS Server. It answers supported download domains with a healthy local cache address for the client networks you choose. When a route has no healthy cache, Technitium handles the lookup normally.

**[Install and try it](03-installation.md)** · [Configuration reference](04-configuration-reference.md) · [Troubleshooting](09-operations.md)

---

## Start here

| Guide | What you will find |
| :--- | :--- |
| [Overview](01-overview.md) | Where the app fits and what you need to use it. |
| [Installation](03-installation.md) | Download, configure, and verify a single cache. Build instructions are included. |
| [How it works](02-how-it-works.md) | Follow a DNS lookup and understand fallback, restarts, and configuration changes. |

## Set up your network

| Guide | What you can change |
| :--- | :--- |
| [Routing](05-routing.md) | Give VLANs different caches, exempt devices, and add standby servers. |
| [Health checks](06-health-checks.md) | Choose a probe and control failure and recovery timing. |
| [Domain list](07-domain-list.md) | Select services, add domains, and schedule updates. |
| [Configuration reference](04-configuration-reference.md) | Find every setting, default, and validation rule. |

## Run it day to day

| Guide | What you will find |
| :--- | :--- |
| [Operations and troubleshooting](09-operations.md) | Read logs, diagnose bypassed caches, and handle maintenance. |
| [DNS answers](08-dns-answers.md) | Understand IPv6 handling, answer caching, and packet details. |
| [Limitations and roadmap](10-limitations-and-roadmap.md) | Check supported setups and planned areas of work. |

> **A useful starting point:** one cache, one client subnet, and the default HTTP heartbeat check. Add routing exceptions and standby servers once the first lookup returns your cache address.

## Working on the project

The [architecture decision records](../adr/README.md) document design choices. The [backlog](../backlog.md) tracks deferred features. For the project introduction and source, return to the [repository README](../../README.md).
