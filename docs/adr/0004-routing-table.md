# ADR 0004 – CIDR routing table, behavior and failover

Status: Accepted

## Context
Currently one hard-coded subnet (192.168.17.0/24 + 127.0.0.1) goes to the one cache server, because only
that VLAN has a firewall-free path to the 2x25G cache. Wanted: a proper table mapping source CIDR → cache
server with behavior and failover, plus an implicit default of "passthrough".

## Route entry
| Field        | Values                                                            |
|--------------|-------------------------------------------------------------------|
| priority     | integer, lower = evaluated first, must be unique                  |
| sourceCidr   | IPv4/IPv6 CIDR (`192.168.17.0/24`, `127.0.0.1/32`, `fd00::/64`)    |
| server       | name of a cache server (omitted for `passthrough`)               |
| behavior     | `passthrough` \| `cacheIfHealthy`                                 |
| failover     | `{ action: passthrough }` \| `{ action: failover, server: "x" }` |
| comment      | free text, optional                                               |
| enabled      | optional, default true (disable a row without deleting it)        |

Implicit last row: priority `int.MaxValue`, `0.0.0.0/0` and `::/0`, behavior `passthrough`.

## Evaluation
1. First matching row in ascending priority wins (no longest-prefix magic; priority is explicit).
2. `passthrough` → return null (normal Technitium resolution).
3. `cacheIfHealthy`: if the server is usable (ADR 0003) → answer with its address.
   Otherwise apply `failover`:
   - `passthrough` → normal DNS.
   - `failover` to server X → use X if usable, else **fall through to normal DNS**. Failover is one hop
     only (X's own route/failover settings are not consulted) – no chains, no loops.
4. A disabled/unknown/unhealthy server counts as "not usable".

Client address is the request's remote endpoint; IPv4-mapped IPv6 is normalized to IPv4 before matching.
Lookup is a pre-sorted list with prefix compare (tables are tiny); rebuilt on config change.

## Validation (rejects the whole config; old config stays active)
unique priorities · valid CIDR · referenced servers exist · failover server != primary server ·
`cacheIfHealthy` requires `server` · warn on rows fully shadowed by a higher-priority wider CIDR.

## Example mapping of today's setup
```
10  127.0.0.1/32       monolith-1  cacheIfHealthy  failover: passthrough
20  192.168.17.0/24    monolith-1  cacheIfHealthy  failover: passthrough
99  (default)          –           passthrough
```

## Decisions / backlog
- Per-service override is backlog B2 (route entry stays unchanged in v1 so it can be added compatibly).
- Client address is the transport/PROXY source only; ECS is ignored.
- Decided: strict primary/failover. A route uses exactly one server at a time; the failover server is
  used only while the primary is not usable. A server has exactly one address, so capacity/redundancy comes from more servers plus failover.
  Load distribution across servers is not planned; it can be revisited if wanted later.
