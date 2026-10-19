using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using DnsServerCore.ApplicationCommon;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;
using TechnitiumLibrary.Net.Proxy;

namespace LanCacheRouter.Tests.Dns;

/// <summary>Marks a theory that serializes DNS messages; skipped while TechnitiumLibrary.IO.dll is not in the lib folder.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WireFormatTheoryAttribute : TheoryAttribute
{
    public WireFormatTheoryAttribute()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "TechnitiumLibrary.IO.dll")))
            Skip = "TechnitiumLibrary.IO.dll is missing from lib/ (copy it from the Technitium container to run wire-format tests)";
    }
}

/// <summary>Helpers for building DNS messages in tests.</summary>
public static class TestDns
{
    public static DnsDatagram Query(
        string name,
        DnsResourceRecordType type = DnsResourceRecordType.A,
        DnsClass @class = DnsClass.IN,
        bool recursionDesired = true,
        bool checkingDisabled = false,
        ushort ednsPayload = 0,
        ushort id = 0x1234)
        => new(id, false, DnsOpcode.StandardQuery, false, false, recursionDesired, false, false, checkingDisabled, DnsResponseCode.NoError,
            [new DnsQuestionRecord(name, type, @class)], udpPayloadSize: ednsPayload);

    public static IPEndPoint Client(string address) => new(IPAddress.Parse(address), 50000);

    /// <summary>Serializes and parses a datagram, proving it is well-formed on the wire.</summary>
    public static DnsDatagram RoundTrip(DnsDatagram datagram)
    {
        using MemoryStream stream = new();
        datagram.WriteTo(stream);
        stream.Position = 0;
        return DnsDatagram.ReadFrom(stream);
    }
}

/// <summary>Minimal <see cref="IDnsServer"/> that records log lines. DNS resolution members are not needed by the app.</summary>
public sealed class FakeDnsServer : IDnsServer
{
    public ConcurrentQueue<string> Logs { get; } = new();

    public string ApplicationName => "LanCacheRouter";

    public string ApplicationFolder { get; init; } = Path.Combine(Path.GetTempPath(), "lancacherouter-app-" + Guid.NewGuid().ToString("N"));

    public string ServerDomain => "dns01.example.test";

    /// <summary>Technitium has no responsible person configured (the app must fall back to hostmaster@server-domain).</summary>
    public bool WithoutResponsiblePerson { get; init; }

    public MailAddress ResponsiblePerson => WithoutResponsiblePerson ? null! : new("hostmaster@example.test");

    /// <summary>Called for every log line, after it is recorded; lets a test make the logger misbehave.</summary>
    public Action<string>? OnLog { get; init; }

    public IDnsCache DnsCache => throw new NotSupportedException();

    public NetProxy Proxy => null!;

    public IPv6Mode IPv6Mode => IPv6Mode.Disabled;

    public ushort UdpPayloadSize => 1232;

    public bool LogContains(string text) => Logs.Any(l => l.Contains(text));

    public void WriteLog(string message)
    {
        Logs.Enqueue(message);
        OnLog?.Invoke(message);
    }

    public void WriteLog(Exception ex) => Logs.Enqueue(ex.ToString());

    public void WriteLog(string message, Exception ex) => Logs.Enqueue(message + ": " + ex);

    public bool IsLocallyServedZone(string domain) => false;

    public Task<DnsDatagram> ResolveAsync(DnsQuestionRecord question, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DnsDatagram> DirectQueryAsync(DnsQuestionRecord question, int timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DnsDatagram> DirectQueryAsync(DnsDatagram request, int timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DnsDatagram> DirectQueryAsync(DnsDatagram request, IPEndPoint remoteEP, int timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
}
