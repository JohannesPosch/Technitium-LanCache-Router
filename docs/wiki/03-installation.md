# Installation

[Documentation](README.md) / Installation

Start with one cache and one client subnet. Once a test lookup returns your cache address, you can add more networks and standby servers.

## Before you begin

Have these ready:

- A Technitium DNS Server version compatible with the app package. Check the release notes for its build target; the repository's [release workflow](../../.github/workflows/release.yml) currently targets Technitium **15.6.1**.
- A working LanCache reachable from your clients and from the DNS server. The example uses HTTP on port 80 at `/lancache-heartbeat`.
- A client subnet whose DNS queries reach Technitium with their source addresses preserved.
- Outbound HTTPS from each DNS node to `raw.githubusercontent.com`, or to your chosen domain-list mirror.

Clients must use Technitium for these lookups, with recursion allowed. Existing local zones and blocking-app rules can take precedence; [How it works](02-how-it-works.md#how-it-fits-into-technitium) explains the interaction.

## 1. Get the app

Download `LanCacheRouter.zip` from the repository's [releases](https://github.com/JohannesPosch/Technitium-LanCache-Router/releases), choosing a release built for your Technitium version. If there is no suitable package, [build one from source](#build-the-package).

<details>
<summary>Verify a release download</summary>

The release workflow produces a SHA-256 checksum and a build-provenance attestation alongside the package. With the ZIP and checksum in the same directory, use:

```bash
sha256sum -c LanCacheRouter.zip.sha256
gh attestation verify LanCacheRouter.zip --repo JohannesPosch/Technitium-LanCache-Router
```

The checksum checks file integrity. The attestation ties the package to the repository's build workflow and commit.

</details>

## 2. Install in Technitium

Open **Apps** in Technitium's web interface, choose the option to install an app from a file, and select `LanCacheRouter.zip`.

The app appears as **LanCacheRouter**. Its packaged configuration is disabled, so installation alone does not start routing clients to the sample address.

## 3. Configure your first route

Open the app's **Config** and replace its contents with this example, using your own cache address and client subnet:

```json
{
  "enabled": true,
  "cacheServers": [
    { "name": "cache-1", "address": "192.168.17.44" }
  ],
  "routes": [
    {
      "priority": 10,
      "sourceCidr": "192.168.17.0/24",
      "servers": ["cache-1"]
    }
  ]
}
```

| Example value | Replace with |
| :--- | :--- |
| `192.168.17.44` | The cache's IPv4 address, reachable by both DNS nodes and routed clients. |
| `192.168.17.0/24` | The network containing the clients you want to use this cache. |
| `cache-1` | Any descriptive server name; use the same name in the route's `servers` list. |

Save the configuration. Invalid settings produce an error and leave the previous configuration active.

The default check requests `http://192.168.17.44:80/lancache-heartbeat` and accepts status `200` or `204`. To check only whether the port accepts connections, add `"healthCheck": { "type": "tcp", "port": 80 }` to the cache entry instead. See [Health checks](06-health-checks.md) for the difference.

## 4. Verify it

Give startup probes and the first domain-list download a moment to finish. In Technitium's logs, look for `LanCache Router:` lines showing the cache becoming Healthy and an active domain list.

Run these commands **from a client inside the routed subnet**. Replace `192.168.17.53` with your Technitium server's address:

```bash
dig @192.168.17.53 lancache.steamcontent.com A +short
dig @192.168.17.53 www.example.com A +short
dig @192.168.17.53 lancache.steamcontent.com AAAA +noall +comments +authority
```

| Lookup | Expected result with this example |
| :--- | :--- |
| `lancache.steamcontent.com A` | `192.168.17.44`, when the Steam domain is in the active list and the cache is Healthy. |
| `www.example.com A` | Normal DNS resolution. |
| `lancache.steamcontent.com AAAA` | `NOERROR`, no answer records, and an SOA in the authority section. |

On Windows, you can also check the A record with PowerShell:

```powershell
Resolve-DnsName lancache.steamcontent.com -Type A -Server 192.168.17.53
```

From outside the routed subnet, the Steam lookup should follow normal DNS resolution. You can test fallback by temporarily setting the cache entry's `enabled` to `false`: a direct lookup should then use normal resolution too. Re-enable it afterwards and wait for Healthy. This checks route fallback; stopping the cache during a suitable test window also checks failure detection.

Direct queries avoid an intermediate resolver, but Technitium can still use its normal cached answers during passthrough. Existing client answers may remain until their TTL expires. For unexpected results, use the [troubleshooting guide](09-operations.md#why-is-this-name-not-answered-with-the-cache).

## Client addresses

Routing uses the source address Technitium receives. If a container gateway or DNS proxy makes every query appear to come from one address, the app cannot distinguish the original client networks. A forwarding resolver has the same effect unless it preserves the source through a supported mechanism.

For containers, use networking that preserves source addresses, such as host networking where supported or an appropriate macvlan setup. A PROXY-protocol front is another option: Technitium requires its dedicated UDP/TCP PROXY listeners and an allowed sender in its reverse-proxy network ACL. Ordinary DNS port 53 does not interpret PROXY headers.

Temporarily enable `logging.verbose` to see the client address and decision for queries reaching the enabled app. `logging.queries` alone shows only queries the app answers.

## Clustering

Technitium replicates the app and `dnsApp.config` from the primary node. Each node independently probes caches, downloads the list, and saves its own snapshot.

Use addresses and settings that work from every node. Check reachability and logs on each node during setup; one node can legitimately select a cache while another passes through. Scheduled downloads have a per-node random delay of up to two minutes.

## Build the package

Use the .NET 10 SDK and assemblies from the Technitium version you will deploy to. Run the commands from the repository root. The app references three assemblies in `lib/`; a fourth enables the wire-format tests.

For a running Docker container named `technitium`, this Bash example copies all four. Adjust the container name and installation path for your image:

```bash
mkdir -p lib
for f in DnsServerCore.ApplicationCommon TechnitiumLibrary TechnitiumLibrary.Net TechnitiumLibrary.IO; do
  docker cp "technitium:/opt/technitium/dns/$f.dll" "lib/$f.dll"
done
dotnet test -c Release
dotnet publish src/LanCacheRouter -c Release -o out
```

PowerShell equivalent:

```powershell
New-Item -ItemType Directory -Force lib
$assemblies = 'DnsServerCore.ApplicationCommon', 'TechnitiumLibrary', 'TechnitiumLibrary.Net', 'TechnitiumLibrary.IO'
foreach ($assembly in $assemblies) {
  docker cp "technitium:/opt/technitium/dns/$assembly.dll" "lib/$assembly.dll"
}
dotnet test -c Release
dotnet publish src/LanCacheRouter -c Release -o out
```

Package exactly these files at the ZIP's top level:

| File | Purpose |
| :--- | :--- |
| `LanCacheRouter.dll` | Application assembly. |
| `LanCacheRouter.deps.json` | Dependency information for the app loader. |
| `Cronos.dll` | Cron scheduling library. |
| `dnsApp.config` | Initial, disabled configuration. |

Using Bash with `zip`, or PowerShell respectively:

```bash
(cd out && zip -j ../LanCacheRouter.zip LanCacheRouter.dll LanCacheRouter.deps.json Cronos.dll dnsApp.config)
```

```powershell
Compress-Archive -Path out/LanCacheRouter.dll, out/LanCacheRouter.deps.json, out/Cronos.dll, out/dnsApp.config -DestinationPath LanCacheRouter.zip -Force
```

Keep the Technitium assemblies out of the package; the host provides them. `TechnitiumLibrary.IO.dll` is needed only for the wire-format tests. A ZIP containing an enclosing `out/` directory will not have the required package layout.

## Updates and removal

Install a newer ZIP over the existing app to update it; Technitium preserves `dnsApp.config`. Keep a copy of your configuration before updating. A new app instance rebuilds health state, so cache use resumes after startup checks and list availability.

Uninstalling removes the app folder and its saved snapshot. Subsequent lookups follow normal DNS, while clients may still hold earlier answers until they expire.

---

**Next:** [Add routes and standby caches](05-routing.md) · [Look up a setting](04-configuration-reference.md)
