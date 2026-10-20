# Health checks

[Documentation](README.md) / Health checks

Health checks decide whether a cache can appear in new DNS answers. Each enabled server is checked independently, from each Technitium node. A route uses only servers in the Healthy state.

## Start with the heartbeat check

The built-in defaults request `http://<cache-address>:80/lancache-heartbeat` and accept HTTP `200` or `204`. No health-check section is needed if this endpoint works on your cache.

To use TCP checks for all caches, add:

```jsonc
"healthCheckDefaults": {
  "type": "tcp",
  "port": 80
}
```

A server's own `healthCheck` overrides individual values in `healthCheckDefaults`, which in turn override the built-in values. Unspecified settings retain their defaults.

| Type | Successful result | What it tells you |
| :--- | :--- | :--- |
| `http` | GET returns a status in `expectedStatus`. | The configured HTTP endpoint responds. This is the default. |
| `tcp` | Connection opens to the configured port. | The port accepts connections; no application request is sent. |
| `https` | Same request as HTTP, over TLS. | The HTTPS endpoint responds. Certificate validation is skipped. |
| `none` | No probe runs; immediately Healthy. | A fixed assumption of availability, useful for controlled tests. |

HTTP checks do not follow redirects. A `301` therefore fails unless explicitly accepted in `expectedStatus`. They send `User-Agent: LanCacheRouter-HealthCheck`. Errors and timeouts count as failures.

## Understanding server states

| State | Meaning | Eligible for answers? |
| :--- | :--- | :--- |
| Unknown | Waiting for enough initial results. | No |
| Healthy | Has reached the consecutive success threshold. | Yes |
| Unhealthy | Has reached the consecutive failure threshold. | No |
| Disabled | Administratively disabled; no checks run. | No |

The default thresholds are two failures to become Unhealthy and three successes to become Healthy. A success resets the failure counter; a failure resets the success counter. A single failed check therefore leaves a Healthy cache in service.

Health checks test the endpoint from the DNS server's position. They cannot detect a firewall rule affecting only clients, prove that downloads complete, or measure storage performance.

## Timing and DNS caching

| Setting | Default | Purpose |
| :--- | :--- | :--- |
| `intervalSeconds` | `10` | Delay after a completed probe before the next probe. |
| `timeoutSeconds` | `2` | Maximum time allowed for a probe. |
| `failThreshold` | `2` | Consecutive failures needed to stop selecting a cache. |
| `successThreshold` | `3` | Consecutive successes needed to select it again. |
| `dns.ttl` | `60` | How long clients may retain a synthesized DNS answer. |

Detection time depends on when the failure happens and how long each probe takes. With quick failures, two failed checks at the default interval take roughly 10–20 seconds to detect a new outage. Timeouts add their own duration. Recovery from Unhealthy requires three successful normal-interval checks, roughly 20–30 seconds with quick responses.

After detection, new lookups select a standby or pass through. Clients holding an earlier answer can continue using it until its TTL expires, and open connections are not redirected. Reducing the probe interval and TTL can shorten reaction time, at the cost of more checks and DNS lookups.

### Startup behavior

Newly started, changed, or re-enabled servers begin Unknown. By default, the first probe runs immediately and subsequent probes run one second after the previous probe completes until the state becomes Healthy or Unhealthy.

With quick successful responses, the default three successes finish in about two seconds plus probe time. A slow or unreachable endpoint takes longer. Once the state settles, the normal ten-second interval applies, including during later recovery from Unhealthy.

Set `startupProbe.enabled` to `false` to use the normal interval from the beginning. The initial delay is then randomized within that interval. `type: none` always starts Healthy without checks.

## Configuration changes and maintenance

| Change | Result |
| :--- | :--- |
| Change routes, DNS settings, or unrelated servers | Unchanged servers keep their state and checks. |
| Change a server's address or any effective health-check value | That server's tracking restarts. |
| Set a cache server's `enabled` to `false` | It becomes Disabled and stops being checked. |
| Re-enable a cache or restart the app | Probed servers start Unknown again. |
| Set the app's `enabled` to `false` | All probing stops; all queries pass through. |

For maintenance, disable the **cache server entry** and allow time for old DNS answers and connections to finish before stopping the cache. Routes can then use their standbys or normal DNS.

## Read the results

State changes are always written to Technitium's log:

```text
LanCache Router: Cache server 'cache-1' (192.168.17.44): initial state Unknown
LanCache Router: Cache server 'cache-1' (192.168.17.44): Unknown -> Healthy
LanCache Router: Cache server 'cache-1' (192.168.17.44): Healthy -> Unhealthy
```

Temporarily set `logging.verbose` to `true` for individual probe results and query decisions. A server's own `healthCheck.verbose` also logs probes, but changing that setting restarts its tracking because it is part of the effective health check. Global verbose logging avoids that reset.

---

**Related:** [Standby routing](05-routing.md#add-a-standby) · [All health-check settings](04-configuration-reference.md#health-check-settings) · [Operations](09-operations.md)
