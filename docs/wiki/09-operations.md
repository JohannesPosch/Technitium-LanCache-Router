# Operations and troubleshooting

[Documentation](README.md) / Operations and troubleshooting

Start with a direct A lookup from an affected client, then read the app's decision in Technitium's logs. Most unexpected results come from a different client address, an earlier DNS rule, an unavailable cache, or a domain absent from the active list.

## A quick check

Replace the DNS and cache addresses with yours. Run DNS lookups from the client network being tested:

```bash
dig @192.168.17.53 lancache.steamcontent.com A +noall +answer +comments
dig @192.168.17.53 lancache.steamcontent.com AAAA +noall +comments +authority
```

Check the configured endpoint **from the DNS node's network**, for example inside its container when relevant:

```bash
curl -i --max-time 2 http://192.168.17.44/lancache-heartbeat
nc -zv 192.168.17.44 80
```

The default HTTP check accepts `200` or `204`; the TCP check only needs a successful connection. A successful test from your workstation does not establish reachability from the DNS node.

On Windows, use `Resolve-DnsName lancache.steamcontent.com -Type A -Server 192.168.17.53` for the DNS test. A test from the DNS host needs a matching route for that host's source address.

## Logging

App messages appear in Technitium's logs with the prefix `LanCache Router:`. Routine state changes, refresh results, warnings, and errors are always logged.

To see cache answers, set `logging.queries` to `true`. To explain both answers and passthrough decisions, temporarily use:

```jsonc
"logging": {
  "verbose": true
}
```

Verbose mode includes query decisions, probes, effective configuration, and domain-refresh details. It can log every query reaching the enabled app; turn it off when finished. It cannot report queries Technitium never sends to the handler, and a disabled app does not log per-query decisions.

An answered-query line looks like this:

```text
LanCache Router: Answered 192.168.17.50 A lancache.steamcontent.com [steam] -> cache-2 (192.168.18.44) [2/2] (failover, preferred: cache-1 not Healthy)
```

`[2/2]` identifies the second server in a two-server preference list. An empty AAAA, HTTPS, or SVCB answer adds `(no data)`.

<details>
<summary>Log message reference</summary>

| Log text | Meaning and next step |
| :--- | :--- |
| `enabled with ... cache server(s) and ... route(s)` | A configuration was applied. The cache still needs to become Healthy and the domain list must be available. |
| `configuration rejected, keeping the previous one` | Fix the reported values; the proposed settings are not running. |
| `Unknown -> Healthy` | The cache can now be selected. |
| `Healthy -> Unhealthy` | The route will select its next Healthy server or pass through. Read probe details in verbose mode. |
| `Domain list: loaded persisted snapshot` | A saved base list was loaded. |
| `Domain list: refreshed` / `patterns active` | Download succeeded / the filtered matcher was built. Counts depend on your source and filters. |
| `refresh failed, keeping the previous list` | An automatic retry is pending; existing list data stays active. |
| `ignoring unreadable snapshot` / `ignoring persisted snapshot from` | The saved file is unreadable or belongs to a different source. |
| `could not save snapshot` | The active list works in memory; check app-folder write access so it survives a restart. |
| `service ... does not exist in the list` | Check service filter names against the index. |
| `skipped ... invalid line(s)` | Those source entries were omitted; inspect the source if expected names are missing. |
| `refreshCron has no upcoming occurrence` | A valid-looking cron expression never fires. Choose a usable schedule. |
| `unexpected error in the refresh loop, trying again` | The loop will retry; retain the error text if reporting a recurring problem. |
| `the refresh loop stopped unexpectedly` | Scheduled refreshes have stopped. Save the configuration again to restart the loop and retain the error for a report. |
| `Error handling query, passing it through` | The app left the query to normal resolution after an unexpected error. Include the error in a report. |

</details>

## Why is this name not answered with the cache?

1. **Confirm the running configuration.** Check that the app is enabled and the last save succeeded. A rejected save leaves the previous settings active.
2. **Confirm the query reaches this server and handler.** Query Technitium directly from the affected client. Check local authoritative zones, recursion permissions, other apps, and Advanced Blocking allow or bypass rules. A client using another resolver will not use this app.
3. **Test an A record.** The default response to eligible AAAA, HTTPS, and SVCB queries is deliberately empty. Other record types pass through.
4. **Read the client address and route decision.** Use `logging.verbose`, since `logging.queries` omits passthrough queries. Check subnet, priority, enabled flags, and any passthrough exception. NAT or a forwarding resolver may hide the original address.
5. **Check health on the responding DNS node.** The selected route needs a Healthy server. Read its latest state transition and probe results. Newly started or changed servers wait for startup checks.
6. **Check the name against the active domain source.** Verify service filters, exact versus wildcard matching, and exclusions. A wildcard does not include its base name. Use `lancache.steamcontent.com` for a Steam test with the default list.
7. **Confirm there is a base list.** Look for loaded-snapshot or refreshed-list messages. With no usable list, even configured extra domains wait for a successful base download. Resolve refresh failures from that node.

Once a direct lookup behaves correctly, allow previous client or intermediate-resolver answers to expire. A direct query avoids those intermediate caches; it still allows Technitium's own cache to participate during passthrough.

## Common situations

| Situation | Expected behavior | What to check |
| :--- | :--- | :--- |
| Cache stops responding | After the failure threshold, new answers use a Healthy standby or normal DNS. | Detection takes time; clients may retain earlier answers for the TTL. |
| Cache returns | The preferred server is selected after its success threshold. | Recovery from Unhealthy uses the normal probe interval. |
| Technitium restarts | Probed caches start Unknown; a usable saved list loads first. | Wait for successful startup probes and list availability. |
| Domain source is unreachable | The previous list remains active; refresh retries. | On a first install without a snapshot, queries pass through. |
| Nodes disagree | Each node uses its own health state and list. | Check cache and source reachability from each node. |
| A new download service is absent | Its hosts may be missing from the source. | Add verified cacheable hosts with `extraDomains` or update your source. |

## Maintenance and updates

For cache maintenance, set that server entry's `enabled` to `false`. Keep it reachable long enough for prior DNS answers and connections to finish, then take it offline. Re-enable it when ready; probed servers must pass startup checks again.

To stop cache routing entirely, set the app's `enabled` to `false`. Queries pass through and background work stops. Re-enabling follows the startup rules.

Before updating, save a copy of your configuration and note the compatible Technitium version. Install the new ZIP over the existing app. Technitium preserves `dnsApp.config`; health is rebuilt, and a domain snapshot may need to be downloaded again. To roll back, install the earlier compatible ZIP and restore a configuration it supports if needed.

Uninstalling removes the app and its saved snapshot. Existing client answers can persist until they expire.

## Network and security behavior

Outbound traffic consists of domain-list HTTP(S) requests and cache health probes. Domain downloads use Technitium's proxy and IP-family preferences. HTTPS health probes skip certificate validation and test endpoint liveness only.

The app parses domain data as text with [download limits](07-domain-list.md#safety-limits). Embedded credentials in `baseUrl` are rejected. External text in logs has control characters escaped and length bounded. See [release verification](03-installation.md#1-get-the-app) for checksum and build-provenance checks.

When reporting a problem, include the app and Technitium versions, the affected client's source address as seen by the app, a configuration with private details removed, and relevant log lines. If it involves a cluster, identify which node answered.

---

**Related:** [Configuration reference](04-configuration-reference.md) · [How it works](02-how-it-works.md) · [DNS answers](08-dns-answers.md)
