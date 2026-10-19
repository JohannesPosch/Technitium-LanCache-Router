# LanCache Router

A [Technitium DNS Server](https://technitium.com/dns/) app that answers game-download domains (from
[uklans/cache-domains](https://github.com/uklans/cache-domains)) with the address of a **healthy** [LanCache](https://lancache.net/)
server – **per client network**, with health checks, failover and a cron-scheduled domain list.

```jsonc
{
  "enabled": true,
  "cacheServers": [ { "name": "lancache", "address": "192.168.17.44", "healthCheck": { "type": "tcp", "port": 80 } } ],
  "routes":       [ { "priority": 10, "sourceCidr": "192.168.17.0/24", "server": "lancache" } ]
}
```

## Documentation

- **[Wiki](docs/wiki/README.md)** – what it does, how it works, installation, every configuration option,
  routing, health checks, operations, limitations.
- [Architecture decision records](docs/adr/README.md) – why it is designed this way.
- [Backlog](docs/backlog.md) – deliberately deferred features.

## Build

Requires the .NET 10 SDK and three (four for the wire-format tests) Technitium assemblies in `lib/`
(see [Installation](docs/wiki/03-installation.md#build-the-package)).

```bash
dotnet test
dotnet publish src/LanCacheRouter -c Release -o out
```

## Layout

| Path | Content |
|---|---|
| `src/LanCacheRouter/App.cs` | the Technitium app (entry point) |
| `src/LanCacheRouter/Config/` | configuration model, parser, validator |
| `src/LanCacheRouter/Routing/` | CIDR routing table and failover decision |
| `src/LanCacheRouter/Health/` | health states, probes, background monitor |
| `src/LanCacheRouter/DomainList/` | list download, parsing, matcher, snapshot, cron refresh |
| `src/LanCacheRouter/Dns/` | per-query pipeline and DNS answer building |
| `tests/LanCacheRouter.Tests/` | unit and integration tests |
| `docs/` | wiki, ADRs, backlog |
