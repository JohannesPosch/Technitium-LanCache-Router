using System.Net;
using LanCacheRouter.Config;
using LanCacheRouter.DomainList;
using LanCacheRouter.Health;
using LanCacheRouter.Routing;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCacheRouter.Dns;

/// <summary>
/// The per-query pipeline of the app (ADR 0001): route lookup by client address, cache-domain lookup, answer building.
/// It never throws and answers <c>null</c> ("let Technitium resolve this") whenever the app is disabled, unconfigured,
/// the query is not interesting, or anything is unusable or unexpected, so DNS keeps working if the app has a problem.
/// </summary>
public sealed class QueryHandler
{
	private readonly Func<HealthSnapshot> _health;
	private readonly Func<DomainMatcher> _matcher;
	private readonly Action<string> _log;
	private State? _state;

	/// <summary>
	/// Creates a handler that answers nothing until <see cref="Configure"/> has been called with an enabled configuration.
	/// </summary>
	/// <param name="health">Returns the latest health snapshot; called for every routed query, so it must be cheap.</param>
	/// <param name="matcher">Returns the active domain matcher; called for every routed query, so it must be cheap.</param>
	/// <param name="log">Receives log lines: per-query lines if enabled in the config, and unexpected errors.</param>
	public QueryHandler(Func<HealthSnapshot> health, Func<DomainMatcher> matcher, Action<string> log)
	{
		_health = health;
		_matcher = matcher;
		_log = SafeLog.Wrap(log);
	}

	/// <summary>
	/// Applies a validated configuration atomically: queries being handled finish with the previous one.
	/// </summary>
	/// <param name="config">The configuration. If it is not enabled, all queries are passed through.</param>
	/// <param name="context">Server values for building responses.</param>
	public void Configure(RouterConfig config, ResponseContext context)
	{
		Volatile.Write(ref _state, config.Enabled
			? new State(RoutingTable.Create(config), config.Dns, context, config.Logging.Queries || config.Logging.Verbose, config.Logging.Verbose)
			: null);
	}

	/// <summary>
	/// Handles one query.
	/// </summary>
	/// <param name="request">The client's query.</param>
	/// <param name="remoteEP">The client's address as seen by Technitium (the real client address, also behind a PROXY-protocol front).</param>
	/// <returns>A synthesized response, or <c>null</c> if Technitium should resolve the query normally.</returns>
	public DnsDatagram? Handle(DnsDatagram request, IPEndPoint remoteEP)
	{
		State? state = Volatile.Read(ref _state);
		if (state is null)
			return null;

		try
		{
			if (request.Question.Count != 1)
				return Skip(state, remoteEP, null, $"{request.Question.Count} questions, only single-question queries are handled");

			DnsQuestionRecord question = request.Question[0];
			if (question.Class != DnsClass.IN || !CacheAnswerBuilder.IsHandledType(question.Type))
				return Skip(state, remoteEP, question, $"class {question.Class} / type {question.Type} is not handled");

			RoutingDecision decision = state.Routing.Resolve(remoteEP.Address, _health());
			if (decision.Outcome != RoutingOutcome.Cache)
				return Skip(state, remoteEP, question, DescribePassthrough(decision));

			string? service = _matcher().Match(question.Name);
			if (service is null)
				return Skip(state, remoteEP, question, $"not a cache domain (route {decision.Route!.SourceCidr} -> {decision.Server!.Name})");

			DnsDatagram? response = CacheAnswerBuilder.Build(request, decision.Server!, state.Dns, state.Context);

			if (response is not null && state.LogQueries)
			{
				_log($"Answered {remoteEP.Address} {question.Type} {question.Name} [{service}] -> {decision.Server!.Name} ({decision.Server.Address})"
					+ (decision.FailedOver ? " (failover)" : "")
					+ (response.Answer.Count == 0 ? " (no data)" : ""));
			}
			else if (response is null)
			{
				return Skip(state, remoteEP, question, $"[{service}] answer builder returned nothing (type/mode passthrough)");
			}

			return response;
		}
		catch (Exception ex)
		{
			_log($"Error handling query, passing it through: {ex}");
			return null;
		}
	}

	/// <summary>
	/// Passes the query through and, in verbose mode, logs why.
	/// </summary>
	private DnsDatagram? Skip(State state, IPEndPoint remoteEP, DnsQuestionRecord? question, string reason)
	{
		if (state.Verbose)
			_log($"Passed through {remoteEP.Address} {question?.Type} {question?.Name}: {reason}");

		return null;
	}

	/// <summary>
	/// Explains a passthrough decision of the routing table for the verbose log.
	/// </summary>
	private static string DescribePassthrough(RoutingDecision decision)
	{
		if (decision.Route is null)
			return "client matches no route";

		string route = $"route {decision.Route.SourceCidr} (priority {decision.Route.Priority})";
		return decision.Route.Behavior == RouteBehavior.Passthrough
			? $"{route} is a passthrough route"
			: $"{route}: server '{decision.Route.Server}' is not Healthy and no usable failover";
	}

	/// <summary>
	/// Everything derived from one configuration, swapped as a unit.
	/// </summary>
	private sealed record State(RoutingTable Routing, DnsConfig Dns, ResponseContext Context, bool LogQueries, bool Verbose);
}
