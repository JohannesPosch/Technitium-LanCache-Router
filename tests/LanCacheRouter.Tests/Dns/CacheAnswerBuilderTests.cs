using System.Net;
using DnsServerCore.ApplicationCommon;
using LanCacheRouter.Config;
using LanCacheRouter.Dns;
using LanCacheRouter.Health;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCacheRouter.Tests.Dns;

public class CacheAnswerBuilderTests
{
    private static readonly ServerHealth Server = new("cache-1", HealthState.Healthy, IPAddress.Parse("192.168.17.44"));
    private static readonly ResponseContext Context = new("dns01.example.test", "hostmaster@example.test", 1232);

    private static DnsDatagram? Build(DnsDatagram request, DnsConfig? dns = null)
        => CacheAnswerBuilder.Build(request, Server, dns ?? new DnsConfig(), Context);

    [Fact]
    public void A_ReturnsOneARecordWithTheServerAddress()
    {
        DnsDatagram response = Build(TestDns.Query("Cdn.SteamContent.com"))!;

        DnsResourceRecord record = Assert.Single(response.Answer);
        Assert.Equal(DnsResourceRecordType.A, record.Type);
        Assert.Equal(DnsClass.IN, record.Class);
        Assert.Equal("Cdn.SteamContent.com", record.Name); // owner = queried name, as sent
        Assert.Equal(60u, record.TTL);
        Assert.Equal(IPAddress.Parse("192.168.17.44"), ((DnsARecordData)record.RDATA).Address);
        Assert.Empty(response.Authority);
    }

    [Fact]
    public void A_UsesTheConfiguredTtl()
    {
        DnsDatagram response = Build(TestDns.Query("a.example.com"), new DnsConfig { Ttl = 5 })!;

        Assert.Equal(5u, response.Answer[0].TTL);
    }

    [Fact]
    public void Response_HasTheExpectedHeader()
    {
        DnsDatagram request = TestDns.Query("a.example.com", id: 0xBEEF);

        DnsDatagram response = Build(request)!;

        Assert.True(response.IsResponse);
        Assert.Equal(0xBEEF, response.Identifier);
        Assert.Equal(DnsOpcode.StandardQuery, response.OPCODE);
        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.False(response.AuthoritativeAnswer);
        Assert.False(response.Truncation);
        Assert.True(response.RecursionAvailable);
        Assert.False(response.AuthenticData);
        Assert.Equal(request.Question, response.Question);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Response_EchoesRecursionDesiredAndCheckingDisabled(bool rd, bool cd)
    {
        DnsDatagram response = Build(TestDns.Query("a.example.com", recursionDesired: rd, checkingDisabled: cd))!;

        Assert.Equal(rd, response.RecursionDesired);
        Assert.Equal(cd, response.CheckingDisabled);
    }

    [Fact]
    public void Response_IsTaggedAuthoritative_SoStatisticsDoNotCountItAsBlocked()
    {
        Assert.Equal(DnsServerResponseType.Authoritative, Build(TestDns.Query("a.example.com"))!.Tag);
    }

    [Fact]
    public void Edns_IsAnnouncedOnlyWhenTheRequestHadIt()
    {
        DnsDatagram withEdns = Build(TestDns.Query("a.example.com", ednsPayload: 4096))!;
        DnsDatagram without = Build(TestDns.Query("a.example.com"))!;

        Assert.NotNull(withEdns.EDNS);
        Assert.Equal(1232, withEdns.EDNS.UdpPayloadSize);
        Assert.Null(without.EDNS);
    }

    [Theory]
    [InlineData(DnsResourceRecordType.AAAA)]
    [InlineData(DnsResourceRecordType.HTTPS)]
    [InlineData(DnsResourceRecordType.SVCB)]
    public void AaaaAndHttps_AnswerNodataWithACacheableSoa(DnsResourceRecordType type)
    {
        DnsDatagram response = Build(TestDns.Query("cdn.example.com", type), new DnsConfig { Ttl = 30 })!;

        Assert.Equal(DnsResponseCode.NoError, response.RCODE);
        Assert.Empty(response.Answer);

        DnsResourceRecord soa = Assert.Single(response.Authority);
        Assert.Equal(DnsResourceRecordType.SOA, soa.Type);
        Assert.Equal("cdn.example.com", soa.Name);
        Assert.Equal(30u, soa.TTL);

        DnsSOARecordData data = (DnsSOARecordData)soa.RDATA;
        Assert.Equal("dns01.example.test", data.PrimaryNameServer);
        Assert.Equal("hostmaster@example.test", data.ResponsiblePerson);
        Assert.Equal(30u, data.Minimum); // the negative-caching TTL
    }

    [Fact]
    public void Aaaa_Passthrough_LeavesTheQueryToTechnitium()
    {
        Assert.Null(Build(TestDns.Query("a.example.com", DnsResourceRecordType.AAAA), new DnsConfig { AaaaMode = NodataMode.Passthrough }));
        Assert.NotNull(Build(TestDns.Query("a.example.com", DnsResourceRecordType.HTTPS), new DnsConfig { AaaaMode = NodataMode.Passthrough }));
    }

    [Fact]
    public void Https_Passthrough_LeavesBothHttpsTypesToTechnitium()
    {
        DnsConfig dns = new() { HttpsMode = NodataMode.Passthrough };

        Assert.Null(Build(TestDns.Query("a.example.com", DnsResourceRecordType.HTTPS), dns));
        Assert.Null(Build(TestDns.Query("a.example.com", DnsResourceRecordType.SVCB), dns));
        Assert.NotNull(Build(TestDns.Query("a.example.com", DnsResourceRecordType.AAAA), dns));
    }

    [Theory]
    [InlineData(DnsResourceRecordType.TXT)]
    [InlineData(DnsResourceRecordType.MX)]
    [InlineData(DnsResourceRecordType.NS)]
    [InlineData(DnsResourceRecordType.CNAME)]
    [InlineData(DnsResourceRecordType.SOA)]
    [InlineData(DnsResourceRecordType.PTR)]
    public void OtherTypes_AreNotAnswered(DnsResourceRecordType type)
    {
        Assert.Null(Build(TestDns.Query("a.example.com", type)));
        Assert.False(CacheAnswerBuilder.IsHandledType(type));
    }

    [Fact]
    public void NonInternetClass_IsNotAnswered()
    {
        Assert.Null(Build(TestDns.Query("a.example.com", @class: DnsClass.CH)));
    }

    [Fact]
    public void MissingOrMultipleQuestions_AreNotAnswered()
    {
        DnsDatagram none = new(1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError, []);
        DnsDatagram two = new(1, false, DnsOpcode.StandardQuery, false, false, true, false, false, false, DnsResponseCode.NoError,
            [new DnsQuestionRecord("a.example.com", DnsResourceRecordType.A, DnsClass.IN), new DnsQuestionRecord("b.example.com", DnsResourceRecordType.A, DnsClass.IN)]);

        Assert.Null(Build(none));
        Assert.Null(Build(two));
    }

    [WireFormatTheory]
    [InlineData(DnsResourceRecordType.A)]
    [InlineData(DnsResourceRecordType.AAAA)]
    [InlineData(DnsResourceRecordType.HTTPS)]
    public void Responses_SurviveASerializationRoundTrip(DnsResourceRecordType type)
    {
        DnsDatagram response = Build(TestDns.Query("cdn.example.com", type, ednsPayload: 4096))!;

        DnsDatagram parsed = TestDns.RoundTrip(response);

        Assert.True(parsed.IsResponse);
        Assert.Equal(response.Answer.Count, parsed.Answer.Count);
        Assert.Equal(response.Authority.Count, parsed.Authority.Count);
        Assert.Equal(DnsResponseCode.NoError, parsed.RCODE);
        if (type == DnsResourceRecordType.A)
            Assert.Equal(IPAddress.Parse("192.168.17.44"), ((DnsARecordData)parsed.Answer[0].RDATA).Address);
    }

    [Fact]
    public void IsHandledType_CoversExactlyTheFourTypes()
    {
        Assert.All(new[] { DnsResourceRecordType.A, DnsResourceRecordType.AAAA, DnsResourceRecordType.HTTPS, DnsResourceRecordType.SVCB },
            type => Assert.True(CacheAnswerBuilder.IsHandledType(type)));
    }

    [Fact]
    public void NegativeAnswer_CarriesTheSoaValuesDocumentedInTheWiki()
    {
        DnsDatagram response = Build(TestDns.Query("cdn.example.com", DnsResourceRecordType.AAAA), new DnsConfig { Ttl = 60 })!;

        DnsSOARecordData soa = (DnsSOARecordData)response.Authority.Single().RDATA;

        Assert.Equal(1u, soa.Serial);
        Assert.Equal(14400u, soa.Refresh);
        Assert.Equal(3600u, soa.Retry);
        Assert.Equal(604800u, soa.Expire);
        Assert.Equal(60u, soa.Minimum);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(86400)]
    public void TtlBoundaries_AreCarriedExactly(int ttl)
    {
        DnsConfig dns = new() { Ttl = ttl };

        Assert.Equal((uint)ttl, Build(TestDns.Query("a.example.com"), dns)!.Answer[0].TTL);

        DnsDatagram negative = Build(TestDns.Query("a.example.com", DnsResourceRecordType.AAAA), dns)!;
        Assert.Equal((uint)ttl, negative.Authority[0].TTL);
        Assert.Equal((uint)ttl, ((DnsSOARecordData)negative.Authority[0].RDATA).Minimum);
    }

    [Fact]
    public void EveryCallBuildsANewResponse_NothingIsSharedBetweenClients()
    {
        DnsDatagram first = Build(TestDns.Query("a.example.com", id: 1))!;
        DnsDatagram second = Build(TestDns.Query("a.example.com", id: 2))!;

        Assert.NotSame(first, second);
        Assert.NotSame(first.Answer, second.Answer);
        Assert.Equal(1, first.Identifier);
        Assert.Equal(2, second.Identifier);
    }

    [Fact]
    public void ManyThreadsBuildingAnswers_EachGetTheirOwnIdentifierBack()
    {
        int wrong = 0;

        Parallel.For(0, 20000, i =>
        {
            DnsDatagram? response = Build(TestDns.Query($"h{i}.example.com", (i % 3) switch { 0 => DnsResourceRecordType.A, 1 => DnsResourceRecordType.AAAA, _ => DnsResourceRecordType.HTTPS }, id: (ushort)i));

            if (response is null || response.Identifier != (ushort)i || response.Question[0].Name != $"h{i}.example.com")
                Interlocked.Increment(ref wrong);
        });

        Assert.Equal(0, wrong);
    }

    [Fact]
    public void Aaaa_AndHttps_CanBeSetIndependently()
    {
        DnsConfig onlyAaaaPasses = new() { AaaaMode = NodataMode.Passthrough, HttpsMode = NodataMode.Nodata };
        DnsConfig onlyHttpsPasses = new() { AaaaMode = NodataMode.Nodata, HttpsMode = NodataMode.Passthrough };

        Assert.Null(Build(TestDns.Query("a.example.com", DnsResourceRecordType.AAAA), onlyAaaaPasses));
        Assert.NotNull(Build(TestDns.Query("a.example.com", DnsResourceRecordType.HTTPS), onlyAaaaPasses));
        Assert.NotNull(Build(TestDns.Query("a.example.com", DnsResourceRecordType.SVCB), onlyAaaaPasses));
        Assert.NotNull(Build(TestDns.Query("a.example.com", DnsResourceRecordType.AAAA), onlyHttpsPasses));
        Assert.Null(Build(TestDns.Query("a.example.com", DnsResourceRecordType.HTTPS), onlyHttpsPasses));
        Assert.Null(Build(TestDns.Query("a.example.com", DnsResourceRecordType.SVCB), onlyHttpsPasses));
        Assert.NotNull(Build(TestDns.Query("a.example.com"), onlyHttpsPasses)); // A is answered in every mode
    }

    [WireFormatTheory]
    [InlineData(DnsResourceRecordType.SVCB, 0)]
    [InlineData(DnsResourceRecordType.A, 0)]
    [InlineData(DnsResourceRecordType.A, 512)]
    [InlineData(DnsResourceRecordType.AAAA, 1232)]
    public void MoreResponses_SurviveASerializationRoundTrip(DnsResourceRecordType type, int ednsPayload)
    {
        DnsDatagram response = Build(TestDns.Query("CDN.Example.COM", type, ednsPayload: (ushort)ednsPayload))!;

        DnsDatagram parsed = TestDns.RoundTrip(response);

        Assert.Equal(response.Identifier, parsed.Identifier);
        Assert.Equal(response.Answer.Count, parsed.Answer.Count);
        Assert.Equal(response.Authority.Count, parsed.Authority.Count);
        Assert.Equal(ednsPayload != 0, parsed.EDNS is not null);
        Assert.False(parsed.AuthoritativeAnswer);
        Assert.True(parsed.RecursionAvailable);
    }
}
