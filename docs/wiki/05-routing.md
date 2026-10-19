# 5. Routing

The routing table answers one question for every query: **"which cache server, if any, may this client be sent to?"**
It looks only at the **client's source address** and at the **current health** of the servers.

## Evaluation rules

1. Only **enabled** rows are considered, in ascending `priority` order (lowest number first).
2. The **first row whose `sourceCidr` contains the client address wins.** Later rows are not looked at, even if they
   match more precisely. There is no "longest prefix" rule; priority is explicit.
3. If no row matches, the query is **passed through**. (This is the implicit default row.)
4. For the winning row:

| `behavior` | Primary server Healthy | Primary server not Healthy |
|---|---|---|
| `passthrough` | passed through | passed through |
| `cacheIfHealthy` | **answer with the primary's address** | apply `failover` ↓ |

| `failover.action` | Result |
|---|---|
| `passthrough` | passed through |
| `failover` (server X) | answer with X's address **if X is Healthy**, otherwise passed through |

5. **Failover is exactly one hop.** X's own state is checked, but X's other routes or failover settings are never
   consulted – there are no chains and no loops.

"Not Healthy" means the server is *Unknown*, *Unhealthy*, *Disabled*, or not defined.

## Decision table: one route with failover

Route: `priority 20, 192.168.17.0/24 → monolith-1, failover → monolith-2`

| monolith-1 | monolith-2 | Client in 192.168.17.0/24 gets |
|---|---|---|
| Healthy | any | monolith-1 |
| Unknown / Unhealthy / Disabled | Healthy | **monolith-2** (failover) |
| Unknown / Unhealthy / Disabled | not Healthy | real CDN addresses (passthrough) |

Clients outside `192.168.17.0/24` are unaffected by all of this.

## Why priority and not "most specific wins"?

It keeps the table predictable and lets you write deliberate exceptions. Put the narrow rows **before** the wide
ones:

```jsonc
"routes": [
  { "priority": 10, "sourceCidr": "192.168.17.99/32", "behavior": "passthrough", "comment": "a PC that must not use the cache" },
  { "priority": 20, "sourceCidr": "192.168.17.0/24", "server": "monolith-1" },
  { "priority": 30, "sourceCidr": "192.168.0.0/16",  "server": "monolith-2" }
]
```

If you swapped priorities 10 and 20, the `/32` row could never match, because `192.168.17.0/24` would catch the client
first. The app tells you: *"`routes[priority=…]` … is never used: shadowed by priority …"* (a warning, not an error).

## Patterns

### One cache for one VLAN, everything else normal

```jsonc
"routes": [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "monolith-1" } ]
```

### Different cache per VLAN

```jsonc
"routes": [
  { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "cache-a" },
  { "priority": 20, "sourceCidr": "192.168.18.0/24", "server": "cache-b" }
]
```

### Primary/standby

```jsonc
{ "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "cache-a",
  "failover": { "action": "failover", "server": "cache-b" } }
```

`cache-b` can also be the primary of another VLAN's route; the two uses are independent.

### Everything except one network

There is no negation. Put a `passthrough` row for the exception first and a wide row after it:

```jsonc
{ "priority": 10, "sourceCidr": "192.168.20.0/24", "behavior": "passthrough" },
{ "priority": 20, "sourceCidr": "0.0.0.0/0",       "server": "cache-a" }
```

### Temporarily switch a row off

Set `"enabled": false` on the row instead of deleting it. A disabled row takes no part in evaluation or in the
"shadowed" warning.

### Test from the server itself

Add `{ "priority": 1, "sourceCidr": "127.0.0.1/32", "server": "…" }` and query `@127.0.0.1`.

## Details worth knowing

- **IPv6 clients** are matched against IPv6 rows (`fd00:17::/64`). An IPv4 client that reaches Technitium over a
  dual-stack socket as `::ffff:192.168.17.50` is treated as IPv4 and matches IPv4 rows.
- A route can only *answer with IPv4 addresses* in v1. IPv6 clients can be routed, but AAAA answers are always empty
  (see [DNS answers](08-dns-answers.md)).
- Server names in routes are matched **ignoring case**.
- The routing table is rebuilt on every configuration save and switched over atomically.
- The client address is what Technitium sees. See [Installation → Client addresses](03-installation.md#client-addresses).
