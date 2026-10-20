# 3. Installation

## Requirements

- Technitium DNS Server running the same major build the app was compiled against (the app uses
  `DnsServerCore.ApplicationCommon` 10.2 / `TechnitiumLibrary.Net` 15.2; build against the version you run, see below).
- A LanCache server (for example the `lancachenet/monolithic` container) reachable from the DNS server on the port
  you use for health checks.
- Outbound HTTPS from every Technitium node to `raw.githubusercontent.com` (or a mirror) for the domain list.
- Technitium must see the **real client address** of queries ([see below](#client-addresses)).

## Build the package

You need the .NET 10 SDK. The app references three Technitium assemblies from a `lib/` folder next to the solution.
Copy them from a running Technitium container so that you compile against exactly the version you deploy:

```bash
mkdir -p lib
for f in DnsServerCore.ApplicationCommon TechnitiumLibrary TechnitiumLibrary.Net TechnitiumLibrary.IO; do
  docker cp technitium:/opt/technitium/dns/$f.dll lib/$f.dll      # adjust the path for the image you use
done
```

(`TechnitiumLibrary.IO.dll` is only needed to run the wire-format unit tests; the app itself does not ship it.)

Run the tests and create the zip:

```bash
dotnet test
dotnet publish src/LanCacheRouter -c Release -o out
cd out && zip -j ../LanCacheRouter.zip LanCacheRouter.dll LanCacheRouter.deps.json Cronos.dll dnsApp.config
```

The zip must be **flat** (all files at the top level, no folder inside). It contains four files:

| File | Purpose |
|---|---|
| `LanCacheRouter.dll` | the app |
| `LanCacheRouter.deps.json` | tells Technitium which dependencies to load |
| `Cronos.dll` | cron expression library |
| `dnsApp.config` | the initial configuration – **inactive** until you edit it |

## Or use a release

Tagged versions (`vX.Y.Z`) are built by the [release workflow](../../.github/workflows/release.yml) and published as
`LanCacheRouter.zip` on the repository's releases page, built and tested against the Technitium version named in the
release notes. Each release also carries `LanCacheRouter.zip.sha256` and a signed build-provenance attestation. Check the
download before you install it:

```bash
sha256sum -c LanCacheRouter.zip.sha256
gh attestation verify LanCacheRouter.zip --repo JohannesPosch/Technitium-LanCache-Router
```

The checksum detects a damaged download; the attestation proves that the file was built by that workflow from a tagged
commit of the repository.

## Install in Technitium

1. Web UI → **Apps** → **Install** (or *Install App from file*) → choose `LanCacheRouter.zip`.
2. The app appears as *LanCacheRouter*. It does nothing yet: the shipped config has `"enabled": false`.
3. Open the app's **Config**, replace the example addresses and networks with the real ones, set `"enabled": true`, save.
4. If the configuration is invalid, Technitium shows the list of problems and keeps the old configuration.
5. Check **Logs** for lines starting with `LanCache Router:` (see [Operations](09-operations.md)).

Updating the app (installing a newer zip over it) keeps the existing `dnsApp.config`. Uninstalling removes the app folder,
including the saved domain-list snapshot.

## A first working configuration

```jsonc
{
  "enabled": true,

  "cacheServers": [
    { "name": "lancache", "address": "192.168.17.44", "healthCheck": { "type": "tcp", "port": 80 } }
  ],

  "routes": [
    { "priority": 10, "sourceCidr": "192.168.17.0/24", "servers": ["lancache"] }
  ]
}
```

That is the complete minimum: every other setting has a default (see the
[reference](04-configuration-reference.md)). Clients in `192.168.17.0/24` now get the cache address for cache
domains while port 80 on `192.168.17.44` accepts connections. Everyone else is unaffected.

### Verify it

From a client in the routed network (replace `<dns>` with the address of the Technitium server):

```bash
dig +short lancache.steamcontent.com @<dns>     # → 192.168.17.44   (a cache domain)
dig +short www.example.com @<dns>               # → the real address (not a cache domain)
dig AAAA +short lancache.steamcontent.com @<dns> # → (empty)         (IPv4-only cache: no data)
```

From a client **outside** the routed network, the first query returns the real address. Stop the cache (or block
port 80) and, after a few probe intervals, the first query returns the real address for routed clients too.

For a step-by-step diagnosis when it does not behave like this, see [Operations](09-operations.md#why-is-this-name-not-answered-with-the-cache).

## Client addresses

Routing decisions use the source address of the query as Technitium sees it. If every query appears to come from the
Docker gateway (for example `172.17.0.1`), no network-based routing can work. Options:

- **Host networking** for the Technitium container (simplest), or a **macvlan** network with its own LAN address.
- **A DNS front that preserves the client address using the PROXY protocol.** Technitium
  accepts PROXY protocol only on its dedicated *DNS-over-UDP/TCP-PROXY* ports, and only from senders listed in its
  reverse-proxy network ACL.
- Check with `logging.queries = true`: the log line shows the client address the app saw.

## Clustering

When Technitium runs as a cluster, the app and its `dnsApp.config` replicate from the primary node to the others.
Runtime state does not:

- **Health states are per node.** Each node probes the cache servers itself, so every node must be able to reach them.
  For a short time nodes may disagree (for example right after a failure).
- **The domain list is per node.** Every node downloads its own copy and keeps its own snapshot. Scheduled refreshes
  get a random per-node delay of up to two minutes so that nodes do not all hit GitHub in the same second.
- The configuration must therefore be **node-neutral**: no node-specific addresses.
