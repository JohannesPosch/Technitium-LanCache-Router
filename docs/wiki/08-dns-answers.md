# DNS answers

[Documentation](README.md) / DNS answers

For an eligible download lookup, the app returns the selected cache's IPv4 address. This page covers the exact response behavior when checking client compatibility or reading a packet capture.

## Which queries are answered

The app must be enabled, the request must contain one question of class `IN`, and its type must be `A`, `AAAA`, `HTTPS`, or `SVCB`. The client must match a cache route, at least one listed server must be Healthy, and the queried name must match the active domain list.

Queries failing any of those checks pass through. The `aaaaMode` and `httpsMode` settings can also pass their respective types through after the checks succeed. See [How it works](02-how-it-works.md#what-happens-to-one-query).

## A records

The response contains one A record owned by the queried name, with the selected cache's address and `dns.ttl` (default 60 seconds). It does not add a CNAME.

```text
;; QUESTION
lancache.steamcontent.com.       IN A
;; ANSWER
lancache.steamcontent.com. 60    IN A 192.168.17.44
```

## AAAA, HTTPS, and SVCB

Cache addresses are currently IPv4 only. With the default `nodata` modes, eligible AAAA lookups return `NOERROR` with an empty answer and an SOA in the authority section:

```text
;; status: NOERROR, ANSWER: 0, AUTHORITY: 1
;; QUESTION
lancache.steamcontent.com.       IN AAAA
;; AUTHORITY
lancache.steamcontent.com. 60    IN SOA dns01.example.test. hostmaster.example.test. 1 14400 3600 604800 60
```

This is a successful no-data response, rather than `NXDOMAIN`. It prevents a client from using a public IPv6 address for a name being directed to an IPv4 cache. HTTPS and SVCB responses are empty for the same reason: their address hints could provide another path around the cache.

The SOA's TTL and minimum are both `dns.ttl`, allowing the negative answer to be cached for that duration. Its primary server and responsible-person values come from Technitium; the example above is illustrative.

To resolve these types normally, set `dns.aaaaMode` or `dns.httpsMode` to `passthrough`. The latter covers both HTTPS and SVCB. Clients can then reach public endpoints using those responses. If the route has no Healthy server, these queries already pass through regardless of the modes.

## Other record types and aliases

The app leaves MX, TXT, NS, SOA, CNAME, PTR, and other types to Technitium. Matching uses the name handed to the app; it does not fetch or follow aliases itself. Technitium can invoke blocking handlers for CNAME targets encountered during upstream resolution, so a target may be handled later in that resolution path.

## Caching and failover

Synthesized answers are returned before Technitium's recursive resolver cache and are not stored in it. Each new eligible query can therefore use the current health snapshot, while unrouted clients continue to receive normal resolver results.

Clients and intermediate resolvers can cache the synthesized answer for its TTL. A health change does not invalidate an answer they already hold or move an existing connection. The default TTL is 60 seconds for both A and no-data responses.

## Response headers

<details>
<summary>Packet fields and flags</summary>

| Field | Value |
| :--- | :--- |
| Identifier and question | Copied from the request. |
| QR / RCODE | Response / `NOERROR`. |
| AA / TC / AD | All clear. |
| RD / CD | Copied from the request. |
| RA | Set. |
| EDNS | OPT record only when the request contains EDNS, using Technitium's configured UDP payload size. Response EDNS flags are clear. |

</details>

The app tags responses as `Authoritative` for Technitium's statistics so they are not counted as blocked. That internal label is separate from the DNS AA flag, which remains clear; the exact dashboard presentation depends on Technitium.

Synthesized answers are unsigned and have `AD=0`. A client performing its own DNSSEC validation cannot authenticate a rewritten answer against the public domain's signed data.

## IPv6 clients

Native IPv6 clients can match IPv6 subnet routes and receive the cache's IPv4 address when requesting A records. They need an IPv4 path to that address to use it. AAAA queries follow `aaaaMode`; the app does not synthesize an IPv6 cache address.

---

**Related:** [DNS settings](04-configuration-reference.md#dns) · [Health-check timing](06-health-checks.md#timing-and-dns-caching) · [Limitations](10-limitations-and-roadmap.md)
