# 4. Configuration reference

The configuration is one JSON document, edited in the Technitium web UI under **Apps → LanCacheRouter → Config**
(stored as `dnsApp.config`).

## Rules that apply to the whole document

- **Comments and trailing commas are allowed** (`// like this`, `/* or this */`), because the document is edited by hand.
- **Property names are case-sensitive camelCase.** Enum values are camelCase strings (`"cacheIfHealthy"`).
- **Unknown properties are rejected.** A typo such as `"rotes"` makes the save fail instead of being silently ignored.
- **Everything has a default.** A minimal configuration is just `cacheServers` and `routes`.
- **All problems are reported together** when validation fails, and the previous configuration stays active.
- Durations are whole **seconds**, at most 86400 (one day). Thresholds are counts of **consecutive** probe results.
- The configuration is **shared by all cluster nodes**, so it must not contain node-specific values.
- A missing configuration (no `dnsApp.config`) is treated as `{ "enabled": false }`.

## Overview

```jsonc
{
  "enabled": true,
  "domainList":          { … },   // where the cache domains come from
  "healthCheckDefaults": { … },   // health check values shared by all servers
  "cacheServers":        [ … ],   // the caches
  "routes":              [ … ],   // which client network uses which cache
  "dns":                 { … },   // what the DNS answers look like
  "logging":             { … }    // extra logging
}
```

## `enabled`

| | |
|---|---|
| Type / default | boolean, `true` |
| Effect | Master switch. `false`: every query is passed through, **no health probes are sent and nothing is downloaded**. Switching back to `true` starts everything again, including a domain-list refresh. |

---

## `domainList`

Where the cache domains come from and how they are filtered. Details in [Domain list](07-domain-list.md).

| Option | Type, default | Valid values | Effect |
|---|---|---|---|
| `baseUrl` | string, `https://raw.githubusercontent.com/uklans/cache-domains/master/` | absolute `http(s)` URL; a missing trailing `/` is added | Folder that contains `cache_domains.json` and the domain files. Point it at a tag or commit (`…/cache-domains/<commit>/`) to **pin** the list, or at a mirror you host. **Changing it triggers an immediate download.** |
| `refreshCron` | string, `"0 4 * * *"` | cron expression with **5 fields** (`min hour day month weekday`) or **6 fields** (leading `seconds`) | When the list is downloaded again. Evaluated in the **time zone of the Technitium process** (see below). Each node adds a random delay of 0–2 minutes. |
| `refreshOnStartup` | boolean, `true` | | Download once when the app first starts. Even when `false`, a download happens at start if there is **no list at all** (no snapshot yet). |
| `httpTimeoutSeconds` | integer, `30` | 1–86400 | Timeout for **each** HTTP request (the index and each domain file). |
| `services.include` | string array, `[]` | service names, case-insensitive | If not empty, **only** these services are used. |
| `services.exclude` | string array, `[]` | service names, case-insensitive | These services are left out. A service in both lists is an error. |
| `extraDomains` | array, `[]` | objects `{ "domain": "...", "service": "..." }` | Domains added by hand, **always active** (not affected by `services.include/exclude`). `service` defaults to `"custom"`. |
| `excludeDomains` | string array, `[]` | domain patterns | Names matching these patterns are **never** treated as cache domains, even if a service contains them. |

**Domain patterns** (in `extraDomains` and `excludeDomains`):

| Pattern | Matches |
|---|---|
| `example.com` | exactly `example.com` |
| `*.example.com` | `a.example.com`, `a.b.example.com`, … – **not** `example.com` itself |

Patterns are case-insensitive, must have at least two labels (`*.com` and `com` are rejected), and only letters,
digits, `-` and `_` are allowed in labels.

**Time zone.** There is no time-zone setting. `refreshCron` uses the time zone of the Technitium process; in Docker set
the `TZ` environment variable on the **Technitium** container (for example `TZ=Europe/Berlin`).

**Examples**

```jsonc
"domainList": {
  "refreshCron": "30 3 * * 1",                          // Mondays at 03:30
  "services": { "exclude": ["test"] },                   // drop the lancache test domains
  "extraDomains": [ { "domain": "*.patch.mygame.example", "service": "mygame" } ],
  "excludeDomains": ["login.steamcontent.com"]           // carve one name out of a wildcard
}
```

---

## `healthCheckDefaults` and `cacheServers[].healthCheck`

Both blocks have the **same options**. `healthCheckDefaults` applies to every server; a server's own `healthCheck`
overrides single values of it. Anything set in neither place uses the built-in value.

> Order of precedence, per option: **server `healthCheck`** → **`healthCheckDefaults`** → **built-in value**.

Details in [Health checks](06-health-checks.md).

| Option | Type, built-in value | Valid values | Effect |
|---|---|---|---|
| `type` | string, `"http"` | `tcp`, `http`, `https`, `none` | How a server is probed. `tcp`: connection succeeds. `http`/`https`: GET succeeds with an expected status. `none`: never probed, **always Healthy** (testing, or caches you cannot probe). |
| `port` | integer, `80` | 1–65535 | TCP port that is probed. |
| `path` | string, `"/lancache-heartbeat"` | must start with `/` | Request path for `http`/`https`. The lancache monolithic container answers this path with `204`. |
| `expectedStatus` | integer array, `[200, 204]` | non-empty, each 100–599 | Status codes that count as success for `http`/`https`. Redirects are **not** followed, so a `301` is a failure unless you list it. |
| `intervalSeconds` | integer, `10` | 1–86400 | Time between two probes of a server. |
| `timeoutSeconds` | integer, `2` | 1–86400, **and ≤ `intervalSeconds`** (checked on the effective values) | A probe that takes longer counts as failed. |
| `failThreshold` | integer, `2` | ≥ 1 | Consecutive failed probes before a server becomes **Unhealthy**. |
| `successThreshold` | integer, `3` | ≥ 1 | Consecutive successful probes before a server becomes **Healthy**. |
| `verbose` | boolean, `false` | | Log **every** probe result, not only state changes. |
| `startupProbe.enabled` | boolean, `true` | | Probe immediately when the app starts and use the fast interval until the server leaves *Unknown*. If `false`, the first probe is delayed by a random part of the interval. |
| `startupProbe.fastIntervalSeconds` | integer, `1` | 1–86400 | Probe interval while a server is *Unknown*. |

---

## `cacheServers`

A list of cache servers. Routes refer to them by name.

| Option | Type, default | Valid values | Effect |
|---|---|---|---|
| `name` | string, **required** | unique, case-insensitive | Name used by `routes[].servers` and in logs. |
| `enabled` | boolean, `true` | | `false` = state **Disabled**: not probed, never handed out. Re-enabling starts it as *Unknown*. |
| `address` | string, **required** | one IPv4 address (four dotted parts); not `0.0.0.0` or `255.255.255.255` | The address that is probed and returned in answers. IPv6 is not supported in v1. |
| `healthCheck` | object, none | see above | Per-server overrides of the health check. |

One server has **exactly one address**. For more capacity or redundancy define more servers and list them in a route's `servers`.

```jsonc
"cacheServers": [
  { "name": "monolith-1", "address": "192.168.17.44", "healthCheck": { "type": "tcp" } },
  { "name": "monolith-2", "address": "192.168.18.44" },                     // uses the HTTP defaults
  { "name": "spare",      "address": "192.168.19.44", "enabled": false }      // defined but off
]
```

---

## `routes`

The routing table. Details in [Routing](05-routing.md).

| Option | Type, default | Valid values | Effect |
|---|---|---|---|
| `priority` | integer, **required in practice** (default `0`) | unique across rows | Evaluation order: **lowest number first; the first matching row wins.** |
| `sourceCidr` | string, **required** | IPv4 or IPv6 CIDR (`192.168.17.0/24`); a bare address is a host route (`127.0.0.1` = `/32`) | The client network this row applies to. Host bits are ignored (`192.168.17.5/24` = `192.168.17.0/24`). |
| `behavior` | string, `"cacheIfHealthy"` | `cacheIfHealthy`, `passthrough` | `cacheIfHealthy`: answer with the first Healthy server of `servers`. `passthrough`: do nothing for this network. |
| `servers` | list of strings | names of cache servers, no duplicates | Servers in **order of preference**: the first Healthy one answers, so later entries are failovers. If none is Healthy the query is passed through. **At least one required** for `cacheIfHealthy`; ignored (with a warning) for `passthrough`. |
| `enabled` | boolean, `true` | | `false` removes the row from evaluation without deleting it. |
| `comment` | string | | For humans; not used. |

**Implicit default row:** clients that match no enabled row are **passed through**. You never need to write a catch-all.

**Warnings** (the configuration is still accepted): a row that an earlier, wider row makes unreachable
(`routes[priority=20] (192.168.17.0/24) is never used: shadowed by priority 10 (192.168.0.0/16)`), `servers` on a
`passthrough` row.

```jsonc
"routes": [
  { "priority": 10, "sourceCidr": "127.0.0.1/32",    "servers": ["monolith-1"] },                       // local test
  { "priority": 20, "sourceCidr": "192.168.17.0/24", "servers": ["monolith-1", "monolith-2"] },
  { "priority": 30, "sourceCidr": "192.168.20.0/24", "behavior": "passthrough", "comment": "servers VLAN" }
]
```

---

## `dns`

What the answers look like. Details in [DNS answers](08-dns-answers.md).

| Option | Type, default | Valid values | Effect |
|---|---|---|---|
| `ttl` | integer, `60` | 1–86400 | TTL in seconds of A records **and** of the negative (no-data) answers. Short on purpose: it is how quickly clients notice that a cache became unhealthy. |
| `aaaaMode` | string, `"nodata"` | `nodata`, `passthrough` | `nodata`: AAAA queries for cache domains get an empty answer so dual-stack clients cannot bypass the cache over IPv6. `passthrough`: resolved normally (the client may then reach the real CDN over IPv6). |
| `httpsMode` | string, `"nodata"` | `nodata`, `passthrough` | The same for `HTTPS` and `SVCB` queries (type 65/64), whose address hints could also bypass the cache. |

---

## `logging`

| Option | Type, default | Effect |
|---|---|---|
| `queries` | boolean, `false` | Log one line for **every query the app answers**: `Answered <client> <type> <name> [<service>] -> <server> (<address>)`. Use it to debug routing; turn it off afterwards. State changes, list refreshes, warnings and errors are always logged. |
| `verbose` | boolean, `false` | Debug mode. Logs **why** every decision was made: each query the app passes through with the reason (`client matches no route`, `server 'x' is not Healthy`, `not a cache domain`, …), every health probe result (as if `verbose` were set on all health checks), per-service pattern counts and download details of list refreshes, and the effective servers, health checks and routes whenever the config is applied. Implies `queries`. Very chatty on a busy server (every DNS query is logged); turn it off afterwards. |

---

## Complete example

A complete configuration with two cache servers, the second one used as failover:

```jsonc
{
  "enabled": true,

  "domainList": {
    "refreshCron": "0 4 * * *",
    "services": { "exclude": ["test"] }
  },

  "healthCheckDefaults": {
    "type": "tcp", "port": 80,
    "intervalSeconds": 10, "timeoutSeconds": 2, "failThreshold": 2, "successThreshold": 3
  },

  "cacheServers": [
    { "name": "monolith-1", "address": "192.168.17.44" },
    { "name": "monolith-2", "address": "192.168.18.44", "enabled": false }
  ],

  "routes": [
    { "priority": 10, "sourceCidr": "127.0.0.1/32" , "servers": ["monolith-1"] },
    { "priority": 20, "sourceCidr": "192.168.17.0/24", "servers": ["monolith-1", "monolith-2"] }
  ],

  "dns": { "ttl": 60 },
  "logging": { "queries": false, "verbose": false }
}
```

## Validation messages

| Message (shortened) | Cause |
|---|---|
| `domainList.refreshCron must have 5 or 6 fields` / `is invalid` | not a valid cron expression |
| `…address 'x' is not a valid IPv4 address` | typo, IPv6 address, or a shorthand like `10.1` |
| `…: duplicate server name` | names are compared ignoring case |
| `…effective timeoutSeconds (n) must not exceed intervalSeconds (m)` | check the values you set **and** the defaults they combine with |
| `routes[priority=n]: priority is used more than once` | priorities must be unique |
| `…: behavior cacheIfHealthy requires at least one server` | missing or empty `servers` |
| `…servers: server 'x' does not exist` | name mismatch (case does not matter) |
| `…servers: server 'x' is listed more than once` | a name appears twice in one `servers` list |
| `invalid JSON: …` | syntax error, an unknown property (`… could not be mapped to any .NET member …`), a wrong value type, or an unknown enum value (`… could not be converted …`); the message names the property path and line |
