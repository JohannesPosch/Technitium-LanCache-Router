using DnsServerCore.ApplicationCommon;
using LanCacheRouter.Config;
using LanCacheRouter.Health;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCacheRouter.Dns;

/// <summary>
/// Server values needed to build well-formed responses, taken from Technitium when the app is initialized.
/// </summary>
/// <param name="ServerDomain">The DNS server's own domain name, used as primary name server in SOA records.</param>
/// <param name="ResponsiblePerson">The administrator's mailbox in <c>user@domain</c> form, used in SOA records.</param>
/// <param name="UdpPayloadSize">The EDNS UDP payload size to announce when the request carried EDNS.</param>
public sealed record ResponseContext(string ServerDomain, string ResponsiblePerson, ushort UdpPayloadSize);

/// <summary>
/// Builds the synthesized DNS answers for cache domains (ADR 0005).
/// </summary>
public static class CacheAnswerBuilder
{
    /// <summary>
    /// SOA timers of negative answers. Only the minimum (the negative-caching TTL) matters to resolvers; the rest are conventional values.
    /// </summary>
    private const uint SoaRefresh = 14400, SoaRetry = 3600, SoaExpire = 604800;

    /// <summary>
    /// Tells whether the app ever answers queries of this type. Everything else is left to Technitium.
    /// </summary>
    /// <param name="type">The query type.</param>
    /// <returns><c>true</c> for A, AAAA, HTTPS and SVCB.</returns>
    public static bool IsHandledType(DnsResourceRecordType type)
        => type is DnsResourceRecordType.A or DnsResourceRecordType.AAAA or DnsResourceRecordType.HTTPS or DnsResourceRecordType.SVCB;

    /// <summary>
    /// Builds the response for a query for a cache domain:
    /// <list type="bullet">
    /// <item>A: one A record with the server's address.</item>
    /// <item>AAAA, HTTPS, SVCB: NOERROR with no data and an SOA in the authority section (so the negative answer is cacheable),
    /// unless the corresponding mode is <see cref="NodataMode.Passthrough"/>.</item>
    /// <item>Anything else: no response.</item>
    /// </list>
    /// The response is tagged <see cref="DnsServerResponseType.Authoritative"/> so Technitium's statistics do not count it as blocked.
    /// </summary>
    /// <param name="request">The client's query; it must have exactly one question of class IN.</param>
    /// <param name="server">The usable cache server to answer with.</param>
    /// <param name="dns">TTL and AAAA/HTTPS handling.</param>
    /// <param name="context">Server values for the SOA record and EDNS.</param>
    /// <returns>The response, or <c>null</c> if Technitium should resolve the query normally.</returns>
    public static DnsDatagram? Build(DnsDatagram request, ServerHealth server, DnsConfig dns, ResponseContext context)
    {
        if (request.Question.Count != 1 || request.Question[0].Class != DnsClass.IN)
            return null;

        DnsQuestionRecord question = request.Question[0];
        uint ttl = (uint)dns.Ttl;

        IReadOnlyList<DnsResourceRecord>? answer = null;
        IReadOnlyList<DnsResourceRecord>? authority = null;

        switch (question.Type)
        {
            case DnsResourceRecordType.A:
                answer = [new DnsResourceRecord(question.Name, DnsResourceRecordType.A, question.Class, ttl, new DnsARecordData(server.Address))];
                break;

            case DnsResourceRecordType.AAAA when dns.AaaaMode == NodataMode.Nodata:
            case DnsResourceRecordType.HTTPS or DnsResourceRecordType.SVCB when dns.HttpsMode == NodataMode.Nodata:
                authority = [new DnsResourceRecord(
                    question.Name,
                    DnsResourceRecordType.SOA,
                    question.Class,
                    ttl,
                    new DnsSOARecordData(context.ServerDomain, context.ResponsiblePerson, 1, SoaRefresh, SoaRetry, SoaExpire, ttl))];
                break;

            default:
                return null;
        }

        return new DnsDatagram(
            request.Identifier,
            isResponse: true,
            DnsOpcode.StandardQuery,
            authoritativeAnswer: false,
            truncation: false,
            request.RecursionDesired,
            recursionAvailable: true,
            authenticData: false,
            request.CheckingDisabled,
            DnsResponseCode.NoError,
            request.Question,
            answer,
            authority,
            additional: null,
            udpPayloadSize: request.EDNS is null ? (ushort)0 : context.UdpPayloadSize,
            EDnsHeaderFlags.None,
            options: null)
        {
            Tag = DnsServerResponseType.Authoritative
        };
    }
}
