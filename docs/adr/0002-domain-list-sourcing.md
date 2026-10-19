# ADR 0002 – Cache-domain list sourcing and cron refresh

Status: Accepted

## Context
https://github.com/uklans/cache-domains publishes `cache_domains.json`:
```json
{ "cache_domains": [ { "name": "steam", "description": "...", "domain_files": ["steam.txt"], "notes": "..." } ] }
```
Each `domain_files` entry is a text file with one domain per line, wildcards as `*.example.com`,
`#` comments and blank lines. Services (steam, epicgames, blizzard, …) are the unit users want to toggle.

## Decision
- Source: configurable base URL (default raw GitHub `master`), optionally pinned to a tag/commit.
  Fetch `cache_domains.json`, then every referenced file, over HTTPS with timeout and size limit.
- Schedule: `refreshCron` – a standard cron expression (5-field, optional seconds) evaluated in the
  application's own time zone (the Technitium host/container, e.g. `TZ`; setting it correctly is a devops
  concern, there is no app setting). Library: **Cronos**. Plus optional `refreshOnStartup`.
- Atomic update: build a complete new index; swap it in whenever download+parse of *everything* succeeded
  (decided: no plausibility/ratio guard – a successful refresh is always applied). If any download or parse
  fails, the old index stays active.
- Persistence: write last good snapshot to the app's config folder (`domains-snapshot.json`); load it on
  startup so DNS works without GitHub being reachable.
- Filtering: `services.include` / `services.exclude` by service `name`; `extraDomains` (manual additions,
  optionally attached to a service name) and `excludeDomains` (manual removals).
- Matching: case-insensitive, reversed-label trie. `*.example.com` matches subdomains at any depth but
  **not** the apex (cache-domains README and lancache-dns' BIND RPZ agree; uklans' unbound script differs,
  don't copy it). Bare lines (`example.com`) match exactly that name; lists contain both forms.
- Parsing: trim whitespace/CRLF, skip blanks and `#`; `*` only allowed as leading `*.`. `domain_files` is an
  array; `name` is not strictly patterned (e.g. `wargaming.net.txt`), so don't enforce a pattern.
- Index maps domain → service name, enabling later per-service server overrides (see 0004, open question).
- Manual refresh: no extra API in v1 (apps can expose none easily); changing config re-triggers a refresh.

## Consequences
- Supply-chain trust: we execute no code from the repo, only parse text; a malicious list entry could
  redirect a domain to your cache IP, so pinning (`ref`) + sanity guard are offered. Entries are
  validated as DNS names; entries like `*` or single-label TLDs are rejected.
- GitHub rate limiting avoided by using raw content, not the API.

## Open questions
- Support alternate source (local file / own fork URL)? Cheap to add: URL scheme `file://`.
- Per-service TTL or per-service target server? (defer to v2 unless wanted now)

## Implementation notes (decided while building)
- Persisted snapshot = the *unfiltered* download; include/exclude/extra/exclude-domain settings are applied after
  loading, so filter changes take effect at once with no download. A snapshot from a different `baseUrl` is ignored.
- Refresh triggers: first config load with `refreshOnStartup`, a changed `baseUrl`, no list available at all, and the
  cron schedule. Other config saves only rebuild the matcher and restart the schedule.
- After a failed refresh the old list stays; retry after 5 minutes or at the next cron time, whichever is earlier.
- After a `baseUrl` change the list in memory keeps serving until the new source delivers (or a persisted list of the
  new source is used immediately).
- Cluster: each node refreshes on its own; a random per-process jitter (0–2 min) is added to scheduled refreshes.
- `excludeDomains` use the same pattern syntax and *block matching*, so `login.example.com` can be carved out of a
  `*.example.com` wildcard.
- A domain file may only reference URLs on the same host below the base path; `cache_domains.json` entries pointing
  elsewhere (absolute URLs, `..`) fail the refresh. Responses are limited to 5 MB per file.
- Invalid lines in a domain file are skipped and logged; they do not fail the refresh.
