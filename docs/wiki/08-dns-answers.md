# 8. DNS answers

This page describes exactly what the app puts on the wire, for people who read packet captures or want to know why a
client behaves as it does.

## Which queries are answered

Only if **all** of these hold (see [How it works](02-how-it-works.md#what-happens-to-one-query)): the app is enabled,
the class is `IN`, the type is one of A / AAAA / HTTPS / SVCB, the client matched a route, the chosen server is Healthy,
and the name is a cache domain. Everything else is untouched.

## Per query type

### `A` – the cache address

```
;; QUESTION   cdn1.steamcontent.com.  IN A
;; ANSWER     cdn1.steamcontent.com.  60  IN A  192.168.17.44
```

- Exactly one A record: the server's address.
- The owner name is the name the client asked for. **There is no CNAME** (unlike the stock `lancache-dns`, which answers
  `CNAME → <service>.<its cache domain>` plus an A record on the target). Clients and the cache do not care.
- TTL = `dns.ttl` (default 60 s).

### `AAAA`, `HTTPS`, `SVCB` – "no data"

With the default `nodata` modes the client gets a valid empty answer, so it does not fall back to the real CDN over IPv6:

```
;; QUESTION   cdn1.steamcontent.com.  IN AAAA
;; status: NOERROR, ANSWER: 0, AUTHORITY: 1
;; AUTHORITY  cdn1.steamcontent.com.  60  IN SOA  dns01.example.test. hostmaster.example.test. 1 14400 3600 604800 60
```

- `NOERROR` with an empty answer means "this name exists but has no records of this type".
- The **SOA record in the authority section** is what lets resolvers cache that negative answer (RFC 2308). Its
  `minimum` and its TTL are both `dns.ttl`.
- `HTTPS`/`SVCB` records can carry IP hints that would bypass the cache, so they are answered the same way (controlled
  by `httpsMode`).
- Set `aaaaMode` / `httpsMode` to `passthrough` if you want these queries resolved normally. That is only sensible
  if you do not mind clients reaching the real CDN over IPv6.

### Everything else

`MX`, `TXT`, `NS`, `SOA`, `CNAME`, `PTR`, … and any class other than `IN` are never answered by the app.

## Header flags

| Flag | Value |
|---|---|
| ID, question | copied from the query |
| QR | response |
| AA | 0 (not authoritative) |
| TC | 0 |
| RD | copied from the query |
| RA | 1 |
| AD | 0 |
| CD | copied from the query |
| RCODE | NOERROR |
| EDNS | an OPT record (with the server's UDP payload size) only if the query had one |

The answers are **not signed**. For DNSSEC-validating clients that ask for validation, cache domains cannot validate
against a real chain; this is inherent to any DNS-based cache redirect, including `lancache-dns`.

## Caching behavior

- The app's answers are produced **before** Technitium's resolver cache and are **not stored in it**. Clients that are
  not routed keep getting normal cached answers.
- Clients cache the answer for `dns.ttl` seconds. This is the main knob for how quickly a failover reaches them.
- Technitium's statistics and query log label these responses **Authoritative** (the app tags them that way so they
  are not counted as blocked). Check the label on the server's dashboard, as this display is decided by Technitium.

## IPv6 summary

| | v1 behavior |
|---|---|
| Cache server address | IPv4 only |
| IPv6 client | can be routed (IPv6 CIDRs work); gets A answers if it asks for A, empty AAAA answers |
| AAAA / HTTPS for cache domains | empty answer by default |

See [Limitations](10-limitations-and-roadmap.md).
