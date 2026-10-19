# ADR 0005 – DNS response semantics

Status: Accepted

## Decision
For a query that matched route → domain → usable server:

- **A**: `AA=0`, `RA=1`, `RCODE=NOERROR`, one A record with the server's IPv4 address, owner = queried name,
  TTL = `dns.ttl` (default 60 s; short so health changes propagate). Built like Advanced Blocking
  (`new DnsDatagram(request.Identifier, true, StandardQuery, false, false, RD, true, false, false, NoError,
  request.Question, answer, authority, null, request.EDNS is null ? 0 : UdpPayloadSize, ...)`); echo EDNS
  only if the request had it. A server has exactly one address, so there is nothing to rotate.
- **AAAA**: **NOERROR/NODATA** in v1 (servers have an IPv4 address only): empty answer plus
  an SOA in authority (owner = queried name, minimum = `dns.ttl`, built from `ServerDomain` /
  `ResponsiblePerson`) so the negative answer is cacheable (RFC 2308). Prevents dual-stack bypass.
  This matches lancache-dns, whose target zone has only A records.
- **Deviation from lancache-dns:** it answers `CNAME -> <service>.<domain>` plus an A on the target; we
  answer the A directly on the queried name. Clients and lancache nginx don't care.
  Configurable `dns.aaaaMode`: `nodata` (default) | `passthrough`.
- **HTTPS/SVCB (types 65/64)**: `dns.httpsMode` = `nodata` (default, avoids ipv4hint/ipv6hint bypass) | `passthrough`.
- **CNAME chains**: we match only the queried name. The core additionally re-invokes blocking handlers for
  each CNAME target of an upstream answer, so an alias pointing at a listed name yields the CNAME chain
  plus our A records (needs an upstream lookup first; verified in `DnsServer.cs`).
- The response is tagged `DnsServerResponseType.Authoritative` so statistics do not count it as blocked. `DnsDatagram.Tag` is
  settable (verified against the shipped DLLs); how the dashboard then displays these answers is still **[VERIFY]** on a live server.
- All other types (TXT, MX, NS, …) and other classes → null (normal resolution).
- Never answer for a domain without a usable address – return null instead of NXDOMAIN/SERVFAIL.
- Queries from `passthrough` rows are never touched. Our answers are returned before the resolver cache
  and not stored in it (verified from code flow), so passthrough clients get normal cached answers.

## Logging
Per-query logging off by default (`logging.queries=false`); state changes, list refreshes and config
errors always logged via `IDnsServer.WriteLog`. Optional sampled query log for debugging.

## Consequences
Behavior equals lancache-dns for matched domains but is conditional on route + health, replacing dnsdist's
whole-backend switching with per-answer decisions.

## Decisions
- v1 caches IPv4 only: AAAA and HTTPS/SVCB for matched domains are always NODATA (`aaaaMode`/`httpsMode`
  `passthrough` remain valid but are not recommended). IPv6 cache support is backlog B1.
- No diagnostic TXT name in v1 (backlog B3).
