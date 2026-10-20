using LanCacheRouter.Config;
using LanCacheRouter.Health;

namespace LanCacheRouter.Routing;

/// <summary>
/// What to do with a query from a given client.
/// </summary>
public enum RoutingOutcome
{
    /// <summary>Do not touch the query; Technitium resolves it as usual.</summary>
    Passthrough,

    /// <summary>Answer cache domains with the address of <see cref="RoutingDecision.Server"/>.</summary>
    Cache
}

/// <summary>
/// Result of looking up a client in the routing table (ADR 0004).
/// </summary>
public sealed class RoutingDecision
{
    /// <summary>
    /// The decision for clients that match no route, or whose route is a passthrough.
    /// </summary>
    public static readonly RoutingDecision DefaultPassthrough = new(RoutingOutcome.Passthrough, null, null, 0);

    /// <summary>
    /// Creates a decision.
    /// </summary>
    /// <param name="outcome">What to do with the query.</param>
    /// <param name="server">The server to answer with; only set for <see cref="RoutingOutcome.Cache"/>.</param>
    /// <param name="route">The route that matched, or <c>null</c> if none did.</param>
    /// <param name="position">1-based position of <paramref name="server"/> in the route's server list; 0 if no server was chosen.</param>
    internal RoutingDecision(RoutingOutcome outcome, ServerHealth? server, RouteConfig? route, int position)
    {
        Outcome = outcome;
        Server = server;
        Route = route;
        Position = position;
    }

    /// <summary>
    /// What to do with the query.
    /// </summary>
    public RoutingOutcome Outcome { get; }

    /// <summary>
    /// The usable (Healthy) server to answer with. Set if and only if <see cref="Outcome"/> is <see cref="RoutingOutcome.Cache"/>.
    /// </summary>
    public ServerHealth? Server { get; }

    /// <summary>
    /// The route that matched the client, or <c>null</c> if the client matched no route. For logging.
    /// </summary>
    public RouteConfig? Route { get; }

    /// <summary>
    /// 1-based position of <see cref="Server"/> in the route's server list, or 0 if no server was chosen. For logging.
    /// </summary>
    public int Position { get; }

    /// <summary>
    /// <c>true</c> if a more preferred server of the route was not usable, so a later one answers. For logging.
    /// </summary>
    public bool FailedOver => Position > 1;
}
