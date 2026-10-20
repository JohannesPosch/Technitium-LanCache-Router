# Routing

[Documentation](README.md) / Routing

A route connects a client network to an ordered list of caches. Use it to keep downloads local to a VLAN, add a standby, or exempt a device from caching.

## The two orders that matter

**Route priority** decides which rule applies. Enabled routes are checked from the lowest `priority` number upward, and the first matching `sourceCidr` wins. A more specific subnet does not automatically take precedence.

**Server list order** decides which cache to use. Within the winning route, the app selects the first Healthy server in `servers`. Unknown, Unhealthy, and Disabled servers are skipped. If none is Healthy, the lookup passes through to Technitium; later routes are not considered.

Clients matching no route also pass through. You can leave unrelated networks out of the configuration.

## One cache for one subnet

The examples below show configuration sections. Define every referenced server in `cacheServers`; the [installation guide](03-installation.md#3-configure-your-first-route) has a complete starting configuration.

```jsonc
"routes": [
  {
    "priority": 10,
    "sourceCidr": "192.168.17.0/24",
    "servers": ["cache-1"]
  }
]
```

Only clients in this subnet are eligible. Their queries must also match the active domain list, and `cache-1` must be Healthy.

## Add a standby

List the preferred cache first, followed by its alternatives:

```jsonc
"routes": [
  {
    "priority": 10,
    "sourceCidr": "192.168.17.0/24",
    "servers": ["cache-1", "cache-2", "cache-3"]
  }
]
```

| Available servers | Selected cache |
| :--- | :--- |
| `cache-1` is Healthy | `cache-1` |
| Only `cache-2` and `cache-3` are Healthy | `cache-2` |
| Only `cache-3` is Healthy | `cache-3` |
| None is Healthy | Normal DNS resolution. |

The preferred cache is selected again as soon as it becomes Healthy. This automatic return affects new lookups; clients can continue using a previous answer until its TTL expires.

## Different caches for different networks

Each route has its own preference list, so two networks can prefer different caches while sharing a standby:

```jsonc
"routes": [
  { "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["cache-1", "cache-2"] },
  { "priority": 20, "sourceCidr": "192.168.18.0/24", "servers": ["cache-2", "cache-1"] }
]
```

Make sure each network can reach every cache in its own list. Health checks run from the DNS node and cannot prove that a client has the same access.

## Exempt a device or network

Put the exception before the broader route:

```jsonc
"routes": [
  {
    "priority": 10,
    "sourceCidr": "192.168.17.99/32",
    "behavior": "passthrough",
    "comment": "Test PC uses normal DNS"
  },
  {
    "priority": 20,
    "sourceCidr": "192.168.17.0/24",
    "servers": ["cache-1"]
  }
]
```

Use the same pattern for a network exception ahead of a wide route such as `0.0.0.0/0`. Omit `servers` on passthrough routes. If an earlier route completely covers a later one, validation warns that the later route is shadowed.

> **Temporarily disable a route:** set its `enabled` to `false`. It is skipped, so a broader enabled route may then match those clients. Use an enabled `passthrough` route when you want an explicit exemption.

## Address and priority details

| Setting | Rule |
| :--- | :--- |
| `priority` | Unique integer across all routes, including disabled ones. Lower numbers are checked first. |
| `sourceCidr` | IPv4 or IPv6 network. A bare address is a single-host route. Host bits are ignored. |
| `servers` | Existing server names, compared without case sensitivity; no duplicates within a route. |
| `behavior` | Defaults to `cacheIfHealthy`. Use `passthrough` for exemptions. |
| `comment` | Optional note for the people maintaining the configuration. |

IPv4-mapped IPv6 client addresses are normalized to IPv4 before matching. Write their networks as IPv4 CIDRs rather than mapped IPv6 CIDRs. Native IPv6 clients can match routes such as `fd00:17::/64`, but cache answers still contain IPv4 addresses; [AAAA handling](08-dns-answers.md) is controlled separately.

To test from the DNS host itself, add a route for `127.0.0.1/32` with a unique priority and your actual cache name, then query `@127.0.0.1`. A test from the host does not exercise a remote client's subnet route.

---

**Related:** [Health checks](06-health-checks.md) · [Configuration reference](04-configuration-reference.md#routes) · [Troubleshooting](09-operations.md)
