# 9. Operations and troubleshooting

## Where to look

Everything the app logs goes to Technitium's log (web UI → **Logs**, or the log files) and starts with
`LanCache Router:`.

| Message | Meaning |
|---|---|
| `enabled with N cache server(s) and M route(s)` | A configuration was applied. |
| `disabled, all queries are passed through` | `enabled` is `false` (or no config). |
| `warning: routes[priority=20] (…) is never used: shadowed by priority 10 (…)` | The configuration is accepted but has a likely mistake. |
| `configuration rejected, keeping the previous one: …` | A save failed validation; the list of problems follows. |
| `Cache server 'x' (a.b.c.d): initial state Unknown` | First sighting of a server after start or change. |
| `Cache server 'x' (a.b.c.d): Unknown -> Healthy` | The server is now being handed out. |
| `Cache server 'x' (a.b.c.d): Healthy -> Unhealthy` | The server was taken out of DNS. |
| `Health probe x/a.b.c.d: FAILED (ConnectionRefused)` | Only with `verbose: true`: one probe result and the reason. |
| `Domain list: loaded persisted snapshot from 2026-10-19 04:00:12Z` | Start-up, before any download. |
| `Domain list: refreshed, 128 patterns in 26 service(s)` | A download succeeded and was applied. |
| `Domain list: 128 patterns active in 26 service(s)` | The matcher was (re)built, e.g. after a filter change. |
| `Domain list: refresh failed, keeping the previous list (from …); retrying in 5 min or at the next scheduled time: …` | A download failed. DNS keeps using the old list. |
| `Domain list: skipped N invalid line(s) in f.txt, first: '…'` | The upstream list has odd lines. Harmless. |
| `Domain list: refreshCron has no upcoming occurrence, so the list is only refreshed when the source changes or the app is configured again` | The cron expression never fires (for example `0 0 31 2 *`). Fix the expression. |
| `Domain list: unexpected error in the refresh loop, trying again in 5 min: …` | An internal error in the scheduler. The loop continues; please report it with the text. |
| `Domain list: service 'x' in the service filter does not exist in the list` | Probably a typo in `include`/`exclude`. |
| `Domain list: ignoring persisted snapshot from …` / `ignoring unreadable snapshot` | The snapshot belongs to another `baseUrl` or is damaged. |
| `Answered 192.168.17.50 A cdn.steamcontent.com [steam] -> monolith-1 (192.168.17.44)` | Per-query line (`logging.queries: true`). `[1/2]` is the position of the answering server in the route's list; `(failover, preferred: … not Healthy)` and `(no data)` are appended when they apply. |
| `Error handling query, passing it through: …` | An unexpected exception. The query was resolved normally. Please report it with the text. |

## Why is this name not answered with the cache?

Work through the pipeline in order; the first "no" is the cause.

1. **Is the app enabled and loaded?** Look for `LanCache Router: enabled with …` after the last save. If you see
   `configuration rejected`, the old config is still running.
2. **Does Technitium ask the app at all?** It does not if the name is served by a local zone, if another app answers
   first, if *recursion desired* is not set, or if the client may not use recursion. Test with `dig` against the server
   (below) and compare with another name.
3. **Is it an A query?** AAAA/HTTPS/SVCB are intentionally answered empty; other types are never answered.
4. **Does the client match a route?** Turn on `logging.queries`. If the query line never appears, the client either
   matched no row (check priorities and the shadowed-warnings), matched a `passthrough` row, or Technitium saw a
   different source address than you expect (Docker NAT, proxy).
5. **Is the server Healthy?** Look at the last `Cache server '…'` line. Anything other than `-> Healthy` means it is
   not handed out; with `verbose: true` the probe lines tell you why. Remember the startup delay.
6. **Is the name in the list?** Compare with the domain file in the upstream repo. Wildcards do not cover the apex.
   Check `services.include/exclude` and `excludeDomains`. A line `Domain list: N patterns active in M service(s)` with
   a small N points at filters.
7. **Is the list there at all?** If GitHub has never been reachable and there is no snapshot, nothing matches. Look
   for `refresh failed`.

### Useful commands

```bash
# What does the server answer? (run from a client in a routed network)
dig @<dns> cdn1.steamcontent.com A +noall +answer +comments
dig @<dns> cdn1.steamcontent.com AAAA +noall +comments +authority

# From the DNS host itself (add a 127.0.0.1/32 route to test this way)
dig @127.0.0.1 lancache.steamcontent.com

# Is the cache itself fine? The same check the app does:
curl -i http://192.168.17.44/lancache-heartbeat       # expect 204
nc -zv 192.168.17.44 80
```

If a resolver caches answers, wait for the TTL (60 s) or use `dig` directly against Technitium.

## Common situations

| Situation | What happens | What to do |
|---|---|---|
| **The cache server goes down** | After `failThreshold` failed probes (≈ 20 s) the server turns Unhealthy; routed clients get real addresses on their next lookup (≤ `ttl`). Connections already open to the failed cache are the client's business; new lookups no longer return it. | Nothing. Fix the cache; it returns after `successThreshold` successes. |
| **The cache comes back** | Back in service after 3 good probes (≈ 30 s). | Nothing. |
| **Technitium restarts** | Servers start Unknown, probed at once and every second until settled → in service within seconds. The domain list is read from the snapshot immediately. | Nothing. |
| **GitHub is unreachable** | The last list stays active; refresh retried every 5 minutes. | Nothing, unless it lasts for weeks and the list grows stale. |
| **You break the config** | The save is refused and the previous config keeps running. | Fix what the error message lists. |
| **You want to take a cache out for maintenance** | Set its `enabled` to `false` (state Disabled). Routes switch to the next server in their list; routes without another Healthy server pass through. | Re-enable afterwards; it starts as Unknown. |
| **Two Technitium nodes disagree** | Health is per node; a node that cannot reach the cache will pass through while the other still serves it. | Check network reachability from the failing node. |
| **A new game is not cached** | Not in the upstream list yet. | Add its hosts under `extraDomains`, and send them upstream. |

## Planned maintenance of the app

- **Update:** install the new zip over the old app in the Apps page. The configuration is kept. The app is
  initialized again, so servers start as Unknown and recover within seconds.
- **Roll back:** install the previous zip the same way.
- **Switch it off quickly:** set `"enabled": false`. Probing and downloads stop at once and every query is passed through.
- **Uninstall:** removes the app and its snapshot. Queries return to normal DNS.

## Security notes

- The app **sends** nothing to clients except DNS answers and **receives** nothing except what Technitium hands it.
- Outbound connections: HTTP(S) to `baseUrl` (through Technitium's proxy if configured) and probes to the cache servers.
- It never executes anything from the downloaded list, only parses text. Download size, time and file locations are
  restricted ([Domain list → Safety limits](07-domain-list.md#safety-limits)).
- HTTPS health checks skip certificate validation by design (the target is an IP address and only liveness is tested).
- There are no secrets in the configuration.
