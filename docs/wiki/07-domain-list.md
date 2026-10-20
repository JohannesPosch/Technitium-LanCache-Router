# 7. Domain list

The app needs to know which hostnames are download CDNs of games and updates. It takes them from the community
list [uklans/cache-domains](https://github.com/uklans/cache-domains) – the same list `lancache-dns` is built from –
and keeps it current on a schedule you control.

## The source format

| File | Content |
|---|---|
| `cache_domains.json` | The index: `{ "cache_domains": [ { "name": "steam", "domain_files": ["steam.txt"], … }, … ] }` |
| `steam.txt`, `epicgames.txt`, … | One entry per line. `#` starts a comment. Blank lines are ignored. |

A line is either an exact name (`lancache.steamcontent.com`) or a wildcard (`*.cdn.blizzard.com`). The **service name**
comes from the index, not the file name (the service `wsus` reads `windowsupdates.txt`).

Files are read as UTF-8; a leading byte order mark (as written by some Windows editors) is ignored, which matters if you
host the files yourself.

At the time of writing the list has 26 services and 128 entries, of which 16 are wildcards.

## How names are matched

| Entry | Matches | Does not match |
|---|---|---|
| `lancache.steamcontent.com` | exactly that name | `a.lancache.steamcontent.com`, `steamcontent.com` |
| `*.cdn.blizzard.com` | `a.cdn.blizzard.com`, `x.y.cdn.blizzard.com` | **`cdn.blizzard.com`** (the apex) |

- A wildcard does **not** cover its own base name. The list therefore contains both `*.cdn.blizzard.com` and
  `cdn.blizzard.com` where both are meant.
- Matching ignores case and a trailing dot.
- If several entries match the same name, **an exact entry beats a wildcard, and a deeper wildcard beats a shallower
  one**. The matching entry decides which *service* the name is attributed to (visible in the per-query log).
- If two services contain the same entry, the first one in the list keeps it.
- `excludeDomains` is checked first and wins over everything.

> **Steam.** The list contains only `lancache.steamcontent.com` for Steam. That is correct: Steam clients look that
> name up to discover a cache and then talk to the address they get. Names like `cache1-vie1.steamcontent.com` are
> not (and need not be) cache domains.

## When the list is downloaded

| Trigger | Download? |
|---|---|
| App starts for the first time and `refreshOnStartup` is `true` | yes |
| App starts, there is **no** list at all (no snapshot yet), `refreshOnStartup` is `false` | yes |
| The cron schedule fires (`refreshCron`, plus a random 0–2 min per node) | yes |
| A failed refresh | retried after 5 minutes, or at the next scheduled time if that comes first |
| `baseUrl` changed in the configuration | yes, immediately |
| Any other configuration change (filters, routes, …) | **no** – the list in memory is re-filtered instantly. A download or a pending retry that is running at that moment is dropped; the next refresh is at the next scheduled time. |
| The app is disabled | nothing runs; enabling it again counts as a start |

Downloads are **all or nothing**: `cache_domains.json` and every file it references must arrive, or the whole refresh
is discarded and the previous list stays active. A refresh that arrives complete is **always applied**; there is no
"suspiciously small" check.

Downloads go through Technitium's own HTTP client, so the **server's configured proxy** and its IPv4/IPv6 preference
are used, exactly as for Technitium's block-list downloads.

### Safety limits

- Each file is limited to **5 MB**.
- Each request has the `httpTimeoutSeconds` timeout.
- Files must live on the **same host and below the base path** as the index; an index that points elsewhere
  (another host, `..`, an absolute path) fails the refresh.
- Lines that are not valid domain patterns are skipped and logged (`skipped 2 invalid line(s) in x.txt, first: '…'`);
  they do not fail the refresh. `*`, single labels like `com` and `*.com` are rejected.

## Persistence

After every successful refresh the **unfiltered** list is saved to
`<app folder>/state/domains-snapshot.json` on that node. At start the snapshot is loaded first, so DNS works even
if GitHub is unreachable after a reboot. A snapshot from a different `baseUrl` is ignored. A damaged snapshot is ignored
(and logged); its patterns are re-validated on load.

Because the snapshot is *unfiltered*, changing `services.include/exclude`, `extraDomains` or `excludeDomains` takes
effect immediately without a download.

## Filtering

Order of application:

1. Services: if `services.include` is not empty, only those; then remove `services.exclude`.
2. `extraDomains` are added (always, whatever the service filter says).
3. `excludeDomains` block matching names.

A service name in `include`/`exclude` that is not in the list is logged as a warning (usually a typo):

```
LanCache Router: Domain list: service 'stem' in the service filter does not exist in the list
```

### Examples

Only Steam and Epic:

```jsonc
"services": { "include": ["steam", "epicgames"] }
```

Everything except lancache's own test names (`canary`, `litmus`, `trigger` under `lancache.net` and `uklans.net`):

```jsonc
"services": { "exclude": ["test"] }
```

Add a game that is not in the list yet:

```jsonc
"extraDomains": [ { "domain": "*.patch.mygame.example", "service": "mygame" } ]
```

Keep a login host out of a wildcard:

```jsonc
"excludeDomains": ["login.steamcontent.com"]
```

## Pinning the list

The default `baseUrl` follows the `master` branch, so every refresh may change the list. To control updates, point at a
tag or commit:

```jsonc
"baseUrl": "https://raw.githubusercontent.com/uklans/cache-domains/<tag-or-commit>/"
```

or host the files yourself and use that URL. The list is only text that the app parses; no code from it is ever run.
A wrong or malicious entry could however send clients of a routed network to the cache for that name, which is why
pinning is available.

## Cron cheat sheet

| Expression | Meaning |
|---|---|
| `0 4 * * *` | every day at 04:00 (default) |
| `30 3 * * 1` | Mondays at 03:30 |
| `0 */6 * * *` | every six hours |
| `0 4 1 * *` | the first of every month at 04:00 |
| `0 4 1 */3 *` | the first of January, April, July and October at 04:00 (any distance works, even a year) |
| `30 0 4 * * *` | six fields: with seconds first – every day at 04:00:30 |

Times are in the Technitium process's time zone (set `TZ` on the container). Five fields are
`minute hour day-of-month month day-of-week`.
