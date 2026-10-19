# Backlog (explicitly out of scope for v1)

| ID | Item | Notes |
|----|------|-------|
| B1 | IPv6 cache support | v1 caches IPv4 only. AAAA/HTTPS for matched domains answer NODATA (ADR 0005). Later: IPv6 addresses on cache servers, `aaaaMode` other than `nodata`. Schema already accepts IPv6 CIDRs for routes; server addresses are validated IPv4-only in v1. |
| B2 | Per-service routing | e.g. steam → server A, rest → server B (`serviceOverrides` on a route). The domain index already stores domain → service, so this needs no index change. |
| B3 | Diagnostic TXT query | Read-only status of server/address health via a synthetic TXT name. v1 uses logs only. |
