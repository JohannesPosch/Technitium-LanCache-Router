# Configuration reference

[Documentation](README.md) / Configuration reference

Edit the JSON document under **Apps → LanCacheRouter → Config** in Technitium. It is stored as `dnsApp.config`. Start with the [installation example](03-installation.md#3-configure-your-first-route), then add the settings you need.

**Jump to:** [Master switch](#enabled) · [Domains](#domainlist) · [Health checks](#health-check-settings) · [Servers](#cacheservers) · [Routes](#routes) · [DNS](#dns) · [Security](#security) · [Logging](#logging)

## Editing rules

- Use case-sensitive camelCase property names. Unknown properties are rejected. Use the enum spellings shown here, such as `cacheIfHealthy` and `nodata`.
- Comments and trailing commas are accepted. Omitted settings keep their defaults; required names, addresses, and route details must still be supplied.
- Durations are integer seconds from `1` to `86400`. Probe thresholds are positive integers counting consecutive results.
- Invalid settings reject the save and leave the previous configuration active. Validation reports the problems it finds together; a malformed document may need its JSON errors fixed first.
- Configuration is shared across a Technitium cluster. Use settings that work from every node.

The `enabled` property defaults to `true` in a supplied document. The packaged configuration explicitly sets it to `false`, and a missing or blank configuration is treated as disabled.

## Complete example

Two caches serve the gaming subnet, with `cache-2` as standby. The test PC is exempt. Replace addresses and networks before saving.

```jsonc
{
  "enabled": true,
  "domainList": {
    "refreshCron": "0 4 * * *",
    "services": { "exclude": ["test"] }
  },
  "healthCheckDefaults": {
    "type": "http",
    "port": 80,
    "path": "/lancache-heartbeat",
    "expectedStatus": [200, 204],
    "intervalSeconds": 10,
    "timeoutSeconds": 2,
    "failThreshold": 2,
    "successThreshold": 3
  },
  "cacheServers": [
    { "name": "cache-1", "address": "192.168.17.44" },
    { "name": "cache-2", "address": "192.168.18.44" }
  ],
  "routes": [
    {
      "priority": 10,
      "sourceCidr": "192.168.17.99/32",
      "behavior": "passthrough",
      "comment": "Test PC"
    },
    {
      "priority": 20,
      "sourceCidr": "192.168.17.0/24",
      "servers": ["cache-1", "cache-2"]
    }
  ],
  "dns": { "ttl": 60, "aaaaMode": "nodata", "httpsMode": "nodata" },
  "logging": { "queries": false, "verbose": false }
}
```

Security and other omitted options retain the defaults below.

## `enabled`

Boolean; default `true`.

Set `false` to pass every query through and stop health probes and list downloads. Re-enabling restarts health tracking and applies the startup domain-refresh rules. This differs from disabling a single server or route.

## `domainList`

Controls the domain source, refresh schedule, and filters. See [Domain list](07-domain-list.md) for examples and matching behavior.

The `services` object holds the `include` and `exclude` arrays. Each `extraDomains` entry has a `domain` and a `service` label.

| Setting | Default | Accepted values and effect |
| :--- | :--- | :--- |
| `baseUrl` | `https://raw.githubusercontent.com/uklans/cache-domains/master/` | Absolute HTTP(S) folder URL containing `cache_domains.json` and the referenced files. No embedded credentials. A trailing slash is added when fetching. Changing the source triggers a download. Plain HTTP is accepted with a warning. |
| `refreshCron` | `"0 4 * * *"` | Five cron fields, or six with leading seconds. Uses the Technitium process's time zone, with a per-node delay of up to two minutes. |
| `refreshOnStartup` | `true` | Download on startup even if a saved list exists. With `false`, startup still downloads if no list is available. |
| `httpTimeoutSeconds` | `30` | `1–86400`; timeout for each individual request. |
| `services.include` | `[]` | Case-insensitive service names. Empty means all services; otherwise only listed services are included. |
| `services.exclude` | `[]` | Case-insensitive service names to omit. A name in both filters is an error. |
| `extraDomains` | `[]` | Objects with a required `domain` pattern and an optional `service` label, default `"custom"`. Added regardless of service filters once a base list is available. |
| `excludeDomains` | `[]` | Patterns that exclude matching query names, including names covered by a broader included wildcard. |

Set the process time zone on the host or, for a container, with `TZ` where supported. There is no app time-zone option.

Patterns use exact names (`example.com`) or leading wildcards (`*.example.com`). A wildcard matches subdomains at any depth, but excludes the base name itself. Matching ignores case and trailing dots. Patterns require at least two labels; labels use ASCII letters, digits, underscores, and hyphens, with no leading or trailing hyphen. A label is at most 63 characters; the name without the wildcard prefix is at most 253 characters. Bare TLDs and `*.com` are rejected.

## Health-check settings

Use these options in `healthCheckDefaults` or in a server's `healthCheck`. Each option is resolved independently: server override, then shared default, then built-in value. See [Health checks](06-health-checks.md) for timing and state transitions.

The `startupProbe` object groups its `enabled` and `fastIntervalSeconds` settings.

| Setting | Built-in value | Accepted values and effect |
| :--- | :--- | :--- |
| `type` | `"http"` | `tcp`, `http`, `https`, or `none`. `none` skips probing and treats the enabled server as Healthy. |
| `port` | `80` | `1–65535`. HTTPS also retains port 80 unless you change it. |
| `path` | `"/lancache-heartbeat"` | Must begin with `/`; used for HTTP(S). |
| `expectedStatus` | `[200, 204]` | Non-empty array of status codes from `100` to `599`. HTTP(S) redirects are not followed. |
| `intervalSeconds` | `10` | `1–86400`; delay after each completed probe. |
| `timeoutSeconds` | `2` | `1–86400`; must not exceed the effective `intervalSeconds`. |
| `failThreshold` | `2` | Integer at least `1`; consecutive failures before Unhealthy. |
| `successThreshold` | `3` | Integer at least `1`; consecutive successes before Healthy. |
| `verbose` | `false` | Log every probe for this server. Changing it resets that server's health tracking. |
| `startupProbe.enabled` | `true` | Probe immediately and use the fast interval while Unknown. Otherwise start after a randomized normal-interval delay. |
| `startupProbe.fastIntervalSeconds` | `1` | `1–86400`; delay between completed probes while Unknown and startup probing is enabled. |

HTTPS probes skip certificate validation. Their result establishes endpoint liveness, not the identity or trustworthiness of its certificate.

## `cacheServers`

Array; default `[]`. Each entry defines one cache address.

| Setting | Default | Accepted values and effect |
| :--- | :--- | :--- |
| `name` | Required | Non-empty, unique without case sensitivity. Routes use this name. |
| `address` | Required | One IPv4 address in four-part dotted notation. `0.0.0.0`, `255.255.255.255`, and addresses with a first octet of `224` or higher are rejected. |
| `enabled` | `true` | `false` makes the server Disabled: no probes and no selection. |
| `healthCheck` | No overrides | Any of the health-check settings above. |

Define additional server entries for standby caches. Each route selects one server at a time; adding entries does not distribute load between them.

## `routes`

Array; default `[]`. See [Routing](05-routing.md) for practical patterns.

| Setting | Default | Accepted values and effect |
| :--- | :--- | :--- |
| `priority` | `0` | Integer, unique across all routes. Set it explicitly: lowest number is evaluated first. |
| `sourceCidr` | Required | IPv4 or IPv6 CIDR, or a bare host address. Host bits are ignored. |
| `behavior` | `"cacheIfHealthy"` | `cacheIfHealthy` selects the first Healthy listed server; `passthrough` leaves the query to Technitium. |
| `servers` | `[]` | Ordered names of existing cache servers, without duplicates. Required and non-empty for `cacheIfHealthy`; ignored with a warning on `passthrough`. |
| `enabled` | `true` | `false` skips the route. A later route may then match. |
| `comment` | Unset | Optional text for maintainers. |

The first matching enabled route wins. If it has no Healthy server, the app passes through rather than trying a later route. A client matching no route also passes through. A route entirely covered by an earlier enabled route produces a shadowing warning.

## `dns`

See [DNS answers](08-dns-answers.md) for response details.

| Setting | Default | Accepted values and effect |
| :--- | :--- | :--- |
| `ttl` | `60` | `1–86400`; lifetime in seconds of synthesized A answers and negative no-data answers. |
| `aaaaMode` | `"nodata"` | `nodata` returns an empty successful answer for eligible AAAA queries. `passthrough` allows normal resolution, which can let clients bypass the IPv4 cache. |
| `httpsMode` | `"nodata"` | Same modes for both HTTPS and SVCB queries, preventing address hints from bypassing the cache by default. |

These modes apply only when the route, health, and domain checks have passed. They do not suppress AAAA or service records for unrelated domains or unrouted clients.

## `security`

Controls domain-download protection and resource limits. Every numeric limit must be at least `1`. Exceeding a limit rejects that refresh and retains the previous list.

| Setting | Default | Effect |
| :--- | :--- | :--- |
| `restrictDownloadRedirects` | `true` | Reject responses redirected outside the source's scheme, host, port, or base path. Setting `false` allows such redirected content and logs a warning. |
| `maxDomainFiles` | `500` | Maximum distinct domain-file references fetched in one refresh; excludes the index. |
| `maxDomainListCharacters` | `20000000` | Maximum total text characters across the index and domain files. |
| `maxDomainPatterns` | `100000` | Maximum patterns across services. A pattern used by multiple services counts for each service. |

Each downloaded file also has a fixed 5 MB limit. Index file references must remain within the base URL even when redirect restriction is disabled. The redirect check happens **after** the target request has been sent; rejected response content is discarded unread. See [Safety limits](07-domain-list.md#safety-limits).

## `logging`

State changes, list refresh results, configuration warnings, and errors are always logged.

| Setting | Default | Effect |
| :--- | :--- | :--- |
| `queries` | `false` | Log each query answered by the app, with client, domain, service, selected server, and its position in the route. |
| `verbose` | `false` | Include answered queries, passthrough reasons, all health probes, list-download details and service counts, and effective configuration. |

Verbose mode can produce a line for every query reaching the enabled app. Use it briefly when diagnosing a problem. See [Operations](09-operations.md#logging).

## Validation messages

| Message or symptom | Check |
| :--- | :--- |
| Unknown property or `could not be mapped` | Spelling and case of the property name. |
| Enum conversion error | Use a documented string value rather than a number. |
| `must not be null` | Supply an object, array, or value of the expected type; omit optional settings to use defaults. |
| Invalid IPv4 address | Use the cache's full IPv4 address, not a hostname, IPv6 address, or shorthand. |
| Duplicate server name or priority | Names are case-insensitive; route priorities must be unique even on disabled entries. |
| Server does not exist | Match a route's server names to entries in `cacheServers`. |
| Effective timeout exceeds interval | Check both shared defaults and server overrides. |
| Invalid cron expression | Use five fields, or six with seconds first. |
| Route is shadowed | Put narrow exceptions before broader routes. This warning does not reject the save. |

---

**Related:** [Installation](03-installation.md) · [Routing examples](05-routing.md) · [Troubleshooting](09-operations.md)
