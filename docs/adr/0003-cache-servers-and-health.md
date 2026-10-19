# ADR 0003 – Cache servers and health states

Status: Accepted

## Context
Replaces lancache-checker (TCP/HTTP probe, fail/ok thresholds, flips a dnsdist backend). New requirements:
multiple cache servers, one IP address per server (decided), per-server enable flag, four health states.

## Model
```
CacheServer { name, enabled, address (one IPv4), healthCheck }
```
Health is tracked per server, which is the health of its single address. More capacity or redundancy is
expressed with more servers and the route failover (ADR 0004), not with multiple IPs per server.

## States
| State     | Meaning                                         | Returned in DNS? |
|-----------|-------------------------------------------------|------------------|
| Unknown   | Startup / no conclusive result yet              | No               |
| Healthy   | Passed `successThreshold` consecutive probes    | Yes              |
| Unhealthy | Failed `failThreshold` consecutive probes       | No               |
| Disabled  | `enabled=false` (server) – no probing at all    | No               |

Transitions:
- Unknown → Healthy after `successThreshold` consecutive successes; → Unhealthy after `failThreshold` failures.
- Healthy → Unhealthy after `failThreshold` consecutive failures (a single failure does not flip).
- Unhealthy → Healthy after `successThreshold` consecutive successes.
- Any → Disabled when server disabled; Disabled → Unknown when re-enabled (probes restart, counters reset).
- App start / config reload: all enabled servers start Unknown (state is not persisted; stale "healthy"
  must never be trusted). Unchanged servers keep their state across a config reload.

A disabled server has no probe loop and is reported as Disabled. A changed address or changed effective
health check also restarts the server as Unknown.

DNS answers contain the server's address only while the server is Healthy. In every other state the server
counts as not usable (→ failover, ADR 0004).

## Probes (replaces check-lancache.sh)
Same knobs as the script, per server (defaults in `healthCheckDefaults`, overridable per server):

| Script variable   | Config                  | Notes                                                    |
|-------------------|-------------------------|----------------------------------------------------------|
| PROBE_MODE        | `type`                  | `tcp` (connect), `http`, `https`, `none` (always Healthy, for tests) |
| PROBE_HOST/PORT   | server `address`, `port` | the server address is probed on `port`                  |
| INTERVAL_SEC      | `intervalSeconds`       | default 10                                               |
| TIMEOUT_SEC       | `timeoutSeconds`        | default 2                                                |
| FAIL_THRESHOLD    | `failThreshold`         | default 2                                                |
| OK_THRESHOLD      | `successThreshold`      | default 3                                                |
| VERBOSE           | `verbose`               | log every probe result, not only state changes           |
| (new)             | `path`, `expectedStatus[]` | http(s) only; default `/lancache-heartbeat`, 200/204  |
| (new)             | `startupProbe`          | see below                                                |

One async loop per server with start jitter; every state change is logged (server, address, old → new,
reason). ICMP is dropped from v1 (needs privileges).

**Startup probe (decided):** when the app initializes (server start, or app config save adding/changing a
server) the server is probed immediately, without waiting for the first interval. While a server is
Unknown and `startupProbe.fastIntervalSeconds` (default 1 s) is configured, probes run at
that faster rate until the server leaves Unknown, then fall back to `intervalSeconds`. With defaults this
makes Unknown → Healthy take ≈ `successThreshold` seconds (3 s) instead of 30 s. Unknown servers are never
returned in DNS answers.

## Consequences
- No external control channel (dnsdist socket + key) needed → the secret in the compose file disappears.
- Probing runs from the DNS server's network position, which may differ from the clients'. Accepted;
  note that per-subnet reachability is out of scope.
- Unknown-is-not-served adds up to `successThreshold*interval` (30 s with current values) delay after
  restart before caching resumes; fail-safe by design.

## Decisions on former open questions
- Unknown is never served (no optimistic start); the startup probe shortens the wait instead.
- v1 exposes state via logs only. A diagnostic TXT query is backlog item B3.
- Cluster: health state is **per Technitium node** (each node probes independently and keeps its own
  state, in memory only); configuration is shared (ADR 0001/0006). Nodes may therefore briefly disagree.
- IPv4 only in v1: the address must be IPv4 (backlog B1).
