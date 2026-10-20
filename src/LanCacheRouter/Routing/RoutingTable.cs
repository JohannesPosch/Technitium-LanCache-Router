using System.Net;
using LanCacheRouter.Config;
using LanCacheRouter.Health;

namespace LanCacheRouter.Routing;

/// <summary>
/// The compiled routing table (ADR 0004): maps a client address to a cache server, honoring behavior and the ordered server list.
/// Immutable and thread-safe; build a new one when the configuration changes. The health of the servers is not part
/// of the table, it is passed to <see cref="Resolve"/> so the request path always sees the latest snapshot.
/// </summary>
public sealed class RoutingTable
{
    private readonly (Cidr Network, RouteConfig Route)[] _rows;

    /// <summary>
    /// Creates a table from already parsed and ordered rows.
    /// </summary>
    /// <param name="rows">Enabled routes in ascending priority order.</param>
    private RoutingTable((Cidr Network, RouteConfig Route)[] rows)
    {
        _rows = rows;
    }

    /// <summary>
    /// A table without rows, which passes every query through.
    /// </summary>
    public static RoutingTable Empty { get; } = new([]);

    /// <summary>
    /// Compiles the routes of a configuration: disabled routes are dropped and the rest is ordered by priority.
    /// </summary>
    /// <param name="config">A configuration that passed validation, so all CIDRs are valid.</param>
    /// <returns>The routing table.</returns>
    public static RoutingTable Create(RouterConfig config)
    {
        List<(Cidr, RouteConfig)> rows = [];

        foreach (RouteConfig route in config.Routes.Where(r => r.Enabled).OrderBy(r => r.Priority))
        {
            if (!Cidr.TryParse(route.SourceCidr, out Cidr? network))
                throw new ArgumentException($"Route with priority {route.Priority} has an invalid CIDR '{route.SourceCidr}'; validate the configuration first.", nameof(config));

            rows.Add((network, route));
        }

        return new RoutingTable([.. rows]);
    }

    /// <summary>
    /// Decides what to do with a query from <paramref name="client"/>:
    /// <list type="number">
    /// <item>The first enabled route (lowest priority number) whose network contains the client wins; no match means passthrough.</item>
    /// <item>A <see cref="RouteBehavior.Passthrough"/> route passes the query through.</item>
    /// <item>A <see cref="RouteBehavior.CacheIfHealthy"/> route uses the first Healthy server of its ordered list, so later entries act as failovers.</item>
    /// <item>If no server of the list is Healthy the query is passed through.</item>
    /// </list>
    /// </summary>
    /// <param name="client">The client address; IPv4-mapped IPv6 addresses are treated as IPv4.</param>
    /// <param name="health">The current health of all cache servers.</param>
    /// <returns>The decision. Servers that are unknown, Unknown, Unhealthy or Disabled are never chosen.</returns>
    public RoutingDecision Resolve(IPAddress client, HealthSnapshot health)
    {
        foreach ((Cidr network, RouteConfig route) in _rows)
        {
            if (!network.Contains(client))
                continue;

            if (route.Behavior == RouteBehavior.Passthrough)
                return new RoutingDecision(RoutingOutcome.Passthrough, null, route, 0);

            for (int i = 0; i < route.Servers.Count; i++)
            {
                ServerHealth? server = UsableServer(health, route.Servers[i]);
                if (server is not null)
                    return new RoutingDecision(RoutingOutcome.Cache, server, route, i + 1);
            }

            return new RoutingDecision(RoutingOutcome.Passthrough, null, route, 0);
        }

        return RoutingDecision.DefaultPassthrough;
    }

    /// <summary>
    /// Looks up a server and returns it only if it can be used for answers.
    /// </summary>
    /// <param name="health">The current health snapshot.</param>
    /// <param name="name">The server name; may be <c>null</c> for a route without one.</param>
    /// <returns>The server if it is known and Healthy, otherwise <c>null</c>.</returns>
    private static ServerHealth? UsableServer(HealthSnapshot health, string? name)
    {
        ServerHealth? server = name is null ? null : health.Get(name);
        return server is { IsUsable: true } ? server : null;
    }
}
