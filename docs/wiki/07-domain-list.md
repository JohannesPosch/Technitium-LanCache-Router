# Domain list

[Documentation](README.md) / Domain list

The domain list tells the app which hostnames belong to cacheable downloads. By default, it downloads the community list from [uklans/cache-domains](https://github.com/uklans/cache-domains), refreshes it daily, and keeps a local copy for restarts.

You can leave the defaults in place, select particular services, or add your own download domains. A domain match still needs a matching client route and a Healthy cache before the app answers.

## Select services and domains

Add a `domainList` section to your configuration. This example selects Steam and Epic, adds a custom game's patch hosts, and exempts that game's login host:

```jsonc
"domainList": {
  "services": { "include": ["steam", "epicgames"] },
  "extraDomains": [
    { "domain": "*.patch.mygame.example", "service": "mygame" }
  ],
  "excludeDomains": ["login.patch.mygame.example"]
}
```

An empty `include` uses all services. Use `services.exclude` to omit specific services instead; a service cannot appear in both filters. Names are case-insensitive and come from the source index. An unknown service name produces a log warning.

Filters apply in this order:

1. Select included services, then remove excluded services.
2. Add `extraDomains`, regardless of service filters. The service label defaults to `custom`.
3. Exclude any query name matching `excludeDomains`.

The app needs a downloaded or saved base list before it builds the matcher, including manual additions. Extra domains alone do not bootstrap an installation whose first download has never succeeded.

## How names are matched

| Pattern | Matches | Leaves out |
| :--- | :--- | :--- |
| `downloads.example.com` | Exactly that hostname. | `a.downloads.example.com` |
| `*.downloads.example.com` | Subdomains at any depth, such as `a.downloads.example.com` or `a.b.downloads.example.com`. | `downloads.example.com` itself. |

Matching ignores case and trailing dots. To include both a base name and its subdomains, add both forms. Exact entries take precedence over wildcards; a more specific wildcard takes precedence over a broader one. For identical patterns in multiple services, the first service keeps attribution. Exclusions take precedence over all included matches.

For a Steam verification lookup, use `lancache.steamcontent.com` with the default list. The app's tests and examples use this discovery name; another hostname under `steamcontent.com` is not automatically included just because it belongs to Steam. If you use a custom or pinned source, check its entries.

## Refresh schedule

The default is daily at 04:00 in the **Technitium process's time zone**. Five cron fields mean minute, hour, day of month, month, and day of week. Six fields add seconds at the beginning.

| `refreshCron` | Schedule |
| :--- | :--- |
| `0 4 * * *` | Daily at 04:00. |
| `30 3 * * 1` | Mondays at 03:30. |
| `0 */6 * * *` | Every six hours, on the hour. |
| `0 4 1 * *` | First day of each month at 04:00. |
| `0 4 1 */3 *` | First day of January, April, July, and October at 04:00. |
| `30 0 4 * * *` | Daily at 04:00:30. |

Each node adds a random delay of up to two minutes to scheduled refreshes. Set the host or container time zone to suit your schedule; there is no separate app time-zone setting.

### When the list is downloaded

| Event | Result |
| :--- | :--- |
| Startup with `refreshOnStartup: true` | Refresh immediately, using a usable snapshot while downloading. |
| Startup without any usable list | Download even if `refreshOnStartup` is `false`. |
| Scheduled refresh | Download after the node's added delay. |
| Failed download | Retain the previous list; retry after five minutes or at the next scheduled time, whichever comes first. |
| Change `baseUrl` | Start a download immediately. The existing in-memory list can remain active until the new source succeeds. |
| Save other settings | Re-filter the current list and restart the schedule. Cancel an in-progress download or pending retry; download immediately only if no base list is available. |
| Disable the app | Stop downloads. Re-enabling follows the startup rules. |

A refresh downloads the index and every referenced domain file, including services excluded by your filters. It replaces the list only after the whole refresh succeeds. Malformed domain lines are skipped and logged, while a missing file or invalid index fails the refresh. A successfully parsed list is applied even if its pattern count falls sharply; there is no minimum-size comparison with the previous list.

## Saved snapshots

Each successful download saves the **unfiltered** list to `state/domains-snapshot.json` inside the app folder on that node. At startup, a usable snapshot is loaded before a new download finishes. Changing filters can therefore take effect immediately without internet access.

Snapshots from a different source URL are ignored; unreadable snapshots are logged and ignored. A failed snapshot write leaves the in-memory list active but means the latest copy will not be available after a restart. App updates can replace files in the app folder, so snapshot persistence should not be relied on across package updates. Uninstalling removes the snapshot.

## Use a pinned source or mirror

`baseUrl` identifies the folder containing `cache_domains.json` and its domain files. The default follows the source's `master` branch. For controlled updates, replace the branch with a chosen tag or commit:

```jsonc
"domainList": {
  "baseUrl": "https://raw.githubusercontent.com/uklans/cache-domains/<tag-or-commit>/"
}
```

Replace the placeholder before saving. You can also point at an HTTP(S) mirror that serves the same format. Downloads use Technitium's HTTP stack, including its configured proxy and IPv4/IPv6 preference.

<details>
<summary>Source format for mirror maintainers</summary>

The index names each service and its domain files:

```json
{
  "cache_domains": [
    { "name": "mygame", "domain_files": ["mygame.txt"] }
  ]
}
```

Each domain file contains one exact name or wildcard per line. `#` starts a comment; blank lines are ignored. Files are decoded as UTF-8, with a leading byte order mark tolerated. The service name comes from the index rather than the file name. Patterns follow the [configuration reference](04-configuration-reference.md#domainlist).

</details>

## Safety limits

The source is parsed as text; it does not supply executable code. Its entries do decide which names routed clients send to your cache, so use a source you trust.

| Protection | Default behavior |
| :--- | :--- |
| Request timeout | 30 seconds per file, controlled by `httpTimeoutSeconds`. |
| File size | Fixed limit of 5 MB per file. |
| Whole refresh | At most 500 distinct domain-file references, 20 million text characters, and 100,000 patterns across services. |
| File locations | Index references must stay within the base URL's scheme, host, port, and folder; `..` references are rejected. |
| Redirects | Content redirected outside that scope is rejected by default. |

Whole-refresh limits are configurable under [`security`](04-configuration-reference.md#security). Redirect checks examine the response after the target request has already been sent, then discard out-of-scope content unread. Disabling this check permits redirected content from elsewhere and produces a warning. Plain HTTP sources also produce a warning because the content is not authenticated or encrypted in transit.

---

**Related:** [Domain settings](04-configuration-reference.md#domainlist) · [Troubleshoot a missing match](09-operations.md#why-is-this-name-not-answered-with-the-cache)
