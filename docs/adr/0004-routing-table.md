# ADR 0004 – CIDR routing table, behavior and failover

Status: Accepted

Revision (2026-10-20, before the first release): the single `server` + `failover` object was replaced by an
ordered `servers` list, because one failover hop could not express "cache-1, then cache-2, then cache-3".
The text below describes the current design.

## Context
Currently one hard-coded subnet (192.168.17.0/24 + 127.0.0.1) goes to the one cache server, because only
that VLAN has a firewall-free path to the 2x25G cache. Wanted: a proper table mapping source CIDR → cache
server with behavior and failover, plus an implicit default of "passthrough".

## Route entry
| Field        | Values                                                            |
|--------------|-------------------------------------------------------------------|
| priority     | integer, lower = evaluated first, must be unique                  |
| sourceCidr   | IPv4/IPv6 CIDR (`192.168.17.0/24`, `127.0.0.1/32`, `fd00::/64`)    |
| servers      | ordered list of cache server names, most preferred first (omitted for `passthrough`) |
| behavior     | `passthrough` \| `cacheIfHealthy`                                 |
| comment      | free text, optional                                               |
| enabled      | optional, default true (disable a row without deleting it)        |

Implicit last row: priority `int.MaxValue`, `0.0.0.0/0` and `::/0`, behavior `passthrough`.

## Evaluation
1. First matching row in ascending priority wins (no longest-prefix magic; priority is explicit).
2. `passthrough` → return null (normal Technitium resolution).
3. `cacheIfHealthy`: walk `servers` in list order and answer with the first server that is usable (ADR 0003).
   If none is usable → **fall through to normal DNS** (passthrough). The list is explicit and finite, so there
   are no chains and no loops; the servers' own routes are never consulted.
4. A disabled/unknown/unhealthy server counts as "not usable" and is skipped.
5. Failback is implicit: every query is evaluated from the top of the list, so a recovered, more preferred
   server takes traffic again as soon as it is Healthy (ADR 0003 thresholds apply).

Client address is the request's remote endpoint; IPv4-mapped IPv6 is normalized to IPv4 before matching.
Lookup is a pre-sorted list with prefix compare (tables are tiny); rebuilt on config change.

## Validation (rejects the whole config; old config stays active)
unique priorities · valid CIDR · referenced servers exist · no duplicate names within one `servers` list ·
`cacheIfHealthy` requires at least one server · warn if `servers` is set on a `passthrough` row ·
warn on rows fully shadowed by a higher-priority wider CIDR.

## Example mapping of today's setup
```
10  127.0.0.1/32       [monolith-1]              cacheIfHealthy
20  192.168.17.0/24    [monolith-1, monolith-2]  cacheIfHealthy
99  (default)          –                         passthrough
```

## Decisions / backlog
- Per-service override is backlog B2 (route entry stays unchanged in v1 so it can be added compatibly).
- Client address is the transport/PROXY source only; ECS is ignored.
- Decided: strict priority order. A route uses exactly one server at a time: the first usable one in its
  `servers` list; later entries are used only while all earlier ones are not usable. A server has exactly one
  address, so capacity/redundancy comes from more servers in the list. Load distribution across servers is not
  planned; it can be revisited if wanted later.
- Decided: there is no `failover` setting. When no listed server is usable the query always passes through, which
  was the only remaining action once failover servers became part of the list. A new action can be added
  compatibly later if needed.
- Decided: the answered-query log line names the chosen server and its position in the list, e.g.
  `cache-2 [2/3]`, and the not-usable skip message lists the servers that were tried.
