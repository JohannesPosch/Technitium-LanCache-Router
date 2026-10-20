# ADR 0006 – Configuration schema (`dnsApp.config`)

Status: Accepted

## Decision
One JSON document, edited in the Technitium web UI (Apps → Config), validated on save/load. Invalid config
→ logged error, **previous valid config stays active** (on first load: app inactive, pure passthrough).
Unknown properties are rejected (typo protection). Hot reload: Technitium calls `InitializeAsync` again on
save; unchanged cache servers keep their health state.

## Reference configuration
```jsonc
{
  "enabled": true,                       // master switch; false = pure passthrough

  "domainList": {
    "baseUrl": "https://raw.githubusercontent.com/uklans/cache-domains/master/",
    "refreshCron": "0 4 * * *",          // Cronos syntax, 5 or 6 fields; evaluated in the app's (container's) time zone
    "refreshOnStartup": true,
    "httpTimeoutSeconds": 30,
    "services": { "include": [], "exclude": [] },   // empty include = all services
    "extraDomains":   [ { "domain": "*.example-cdn.com", "service": "custom" } ],
    "excludeDomains": []
  },

  "healthCheckDefaults": {
    "type": "http",                      // tcp | http | https | none  (icmp dropped from v1, ADR 0003)
    "port": 80,
    "path": "/lancache-heartbeat",
    "expectedStatus": [200, 204],
    "intervalSeconds": 10,
    "timeoutSeconds": 2,
    "failThreshold": 2,
    "successThreshold": 3,
    "verbose": false,                    // log every probe result (script's VERBOSE)
    "startupProbe": { "enabled": true, "fastIntervalSeconds": 1 }  // probe immediately + fast until first state
  },

  "cacheServers": [
    {
      "name": "monolith-1",
      "enabled": true,
      "address": "192.168.17.44",        // exactly one IPv4 address in v1 (IPv6: backlog B1)
      "healthCheck": { "type": "tcp" }   // merged over healthCheckDefaults
    },
    {
      "name": "monolith-2",
      "enabled": false,
      "address": "192.168.18.44"
    }
  ],

  "routes": [
    { "priority": 10, "sourceCidr": "127.0.0.1/32",    "servers": ["monolith-1"], "behavior": "cacheIfHealthy",
      "comment": "local tests" },
    { "priority": 20, "sourceCidr": "192.168.17.0/24", "servers": ["monolith-1", "monolith-2"], "behavior": "cacheIfHealthy" }
    // implicit: { priority: MAX, 0.0.0.0/0 + ::/0, behavior: passthrough }
  ],

  "dns": { "ttl": 60, "aaaaMode": "nodata", "httpsMode": "nodata" },
  "security": {                          // protective checks (on by default; false switches one off, with a warning) and limits
    "restrictDownloadRedirects": true,
    "maxDomainFiles": 500,               // limits of one domain-list download
    "maxDomainListCharacters": 20000000,
    "maxDomainPatterns": 100000
  },
  "logging": { "queries": false }          // log every answered query (debugging); state changes/errors are always logged
}
```

## Field notes
- `behavior`: `passthrough` | `cacheIfHealthy`. `servers` is an ordered preference list (first usable server wins, ADR 0004); if none is usable the query passes through.
- Durations are integers in seconds; thresholds are consecutive counts.
- All values have defaults, so a minimal config is just `cacheServers` + `routes`.
- Time zone: there is no setting; the cron expression uses the time zone of the Technitium process (`TZ`).
- A successful domain-list refresh is always applied (no ratio guard).
- Secrets: none required (the dnsdist control key is gone). `domainList.baseUrl` must not contain credentials
  (`user:password@`); they would be written to logs and to the snapshot.
- `security` holds protective checks that are on by default; the config only ever *relaxes* them. A relaxed check
  produces a warning when the config is applied.
- Cluster: the config is shared by all nodes, so it must be node-neutral. Health state and domain snapshot
  are per node (ADR 0001/0003).

## Deployment impact (docker-compose)
Remove `dnsdist`, `lancache-dns`, `lancache-checker`. Technitium publishes/hosts :53 such that the client
source IP is preserved (host network or macvlan), the app zip is installed via Apps → Install (or the
store/URL), and the config above is pasted in.

## Decisions / notes
- JSON, since Technitium only offers a text area.
- The dnsdist ACL role moves to Technitium's own recursion ACL (`Allow Recursion`). Note our handler only
  runs for clients allowed to recurse.
