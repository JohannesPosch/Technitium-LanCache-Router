using System.Net;
using DnsServerCore.ApplicationCommon;
using LanCacheRouter.Config;
using LanCacheRouter.Dns;
using LanCacheRouter.DomainList;
using LanCacheRouter.Health;
using TechnitiumLibrary.Net.Dns;

namespace LanCacheRouter;

/// <summary>
/// The Technitium DNS Server app (ADR 0001). It answers queries for game-download domains with the address of a healthy
/// cache server, depending on which network the client is in, and leaves every other query to Technitium. It replaces
/// dnsdist, lancache-dns and lancache-checker.
/// </summary>
/// <remarks>
/// Technitium calls <see cref="InitializeAsync"/> on the same instance whenever the configuration is saved, so everything
/// here is built to be re-applied. An invalid configuration is rejected by throwing, which leaves the previous one active.
/// </remarks>
public sealed class App : IDnsApplication, IDnsRequestBlockingHandler, IDnsApplicationPreference
{
	/// <summary>
	/// Configuration used when no <c>dnsApp.config</c> exists: the app is installed but inactive.
	/// </summary>
	private const string InactiveConfig = """{ "enabled": false }""";

	private const string LogPrefix = "LanCache Router: ";

	private readonly object _gate = new();
	private readonly Func<IDnsServer, IDomainSource> _sourceFactory;
	private readonly HealthMonitor _health;
	private readonly QueryHandler _handler;
	private IDnsServer? _dnsServer;
	private DomainListManager? _domains;

	/// <summary>
	/// Creates the app. Technitium requires a public parameterless constructor.
	/// </summary>
	public App() : this(server => new TechnitiumDomainSource(() => server))
	{
	}

	/// <summary>
	/// Creates the app with a custom download source for the domain list. Used by tests so they need no network.
	/// </summary>
	/// <param name="sourceFactory">Creates the source once the DNS server is known.</param>
	internal App(Func<IDnsServer, IDomainSource> sourceFactory)
	{
		_sourceFactory = sourceFactory;
		_health = new HealthMonitor(Log);
		_handler = new QueryHandler(() => _health.Snapshot, () => _domains?.Matcher ?? DomainMatcher.Empty, Log);
	}

	/// <inheritdoc/>
	public string Description => "Answers game-download (LanCache) domains with the address of a healthy cache server, per client network, with health checks and failover.";

	/// <summary>
	/// Run order among apps that implement <see cref="IDnsRequestBlockingHandler"/>: lower runs first and the default is 100.
	/// The app runs after other blocking apps such as Advanced Blocking, so blocked domains stay blocked.
	/// </summary>
	public byte Preference => 150;

	/// <summary>
	/// Validates and applies the configuration. Throws if it is invalid, in which case the previous configuration stays
	/// active and Technitium shows the message.
	/// </summary>
	/// <param name="dnsServer">The DNS server hosting the app.</param>
	/// <param name="config">The content of <c>dnsApp.config</c>, or <c>null</c> if there is none.</param>
	/// <exception cref="InvalidOperationException">The configuration is not valid; the message lists every problem.</exception>
	public Task InitializeAsync(IDnsServer dnsServer, string? config)
	{
		ConfigParseResult result = ConfigParser.Parse(string.IsNullOrWhiteSpace(config) ? InactiveConfig : config);

		if (!result.IsValid)
		{
			SafeLog.Write(dnsServer.WriteLog, LogPrefix + "configuration rejected, keeping the previous one: " + string.Join("; ", result.Errors));
			throw new InvalidOperationException("Invalid LanCache Router configuration:" + string.Concat(result.Errors.Select(e => Environment.NewLine + " - " + e)));
		}

		RouterConfig newConfig = result.Config!;

		lock (_gate)
		{
			_dnsServer = dnsServer;

			foreach (string warning in result.Warnings)
				Log("warning: " + warning);

			_handler.Configure(newConfig, new ResponseContext(
				dnsServer.ServerDomain,
				dnsServer.ResponsiblePerson?.Address ?? "hostmaster." + dnsServer.ServerDomain,
				dnsServer.UdpPayloadSize));

			if (newConfig.Enabled)
			{
				_domains ??= new DomainListManager(
					_sourceFactory(dnsServer),
					new DomainSnapshotStore(Path.Combine(dnsServer.ApplicationFolder, "state", "domains-snapshot.json"), Log),
					Log);

				_health.Apply(newConfig);
				_domains.Apply(newConfig.DomainList);
				Log($"enabled with {newConfig.CacheServers.Count} cache server(s) and {newConfig.Routes.Count} route(s)");
			}
			else
			{
				_health.Apply(new RouterConfig { Enabled = false });
				_domains?.Pause();
				Log("disabled, all queries are passed through");
			}
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// Never allows a query to bypass the app. Allow-listing is the business of the blocking apps; returning
	/// <c>true</c> here would make Technitium skip all other blocking handlers for the query.
	/// </summary>
	public Task<bool> IsAllowedAsync(DnsDatagram request, IPEndPoint remoteEP) => Task.FromResult(false);

	/// <summary>
	/// Answers the query with a cache address if the client's network is routed to a healthy cache server and the
	/// name is a cache domain; otherwise returns <c>null</c> and Technitium continues as usual.
	/// </summary>
	public Task<DnsDatagram?> ProcessRequestAsync(DnsDatagram request, IPEndPoint remoteEP)
		=> Task.FromResult(_handler.Handle(request, remoteEP));

	/// <summary>
	/// Stops all background work (health probes and list refreshes).
	/// </summary>
	public void Dispose()
	{
		_health.Dispose();
		_domains?.Dispose();
	}

	/// <summary>
	/// Writes to Technitium's log, if the server is known yet. A failing log never affects the app.
	/// </summary>
	/// <param name="message">The message without prefix.</param>
	private void Log(string message)
	{
		if (_dnsServer is { } server)
			SafeLog.Write(server.WriteLog, LogPrefix + message);
	}
}
