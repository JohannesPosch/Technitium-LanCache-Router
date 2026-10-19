using System.Net;
using LanCacheRouter.Routing;

namespace LanCacheRouter.Tests.Routing;

public class CidrTests
{
    private static Cidr Parse(string text)
    {
        Assert.True(Cidr.TryParse(text, out Cidr? cidr), $"'{text}' should parse");
        return cidr;
    }

    [Theory]
    [InlineData("192.168.17.0/24", "192.168.17.44", true)]
    [InlineData("192.168.17.0/24", "192.168.18.1", false)]
    [InlineData("192.168.17.5/24", "192.168.17.200", true)] // host bits are masked
    [InlineData("127.0.0.1/32", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "127.0.0.2", false)] // bare address = host route
    [InlineData("10.0.0.0/9", "10.127.255.255", true)]
    [InlineData("10.0.0.0/9", "10.128.0.0", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("fd00::/64", "fd00::1", true)]
    [InlineData("fd00::/64", "fd00:0:0:1::1", false)]
    [InlineData("192.168.17.0/24", "::ffff:192.168.17.9", true)] // IPv4-mapped IPv6 client
    [InlineData("192.168.17.0/24", "fd00::1", false)]
    public void Contains_Address(string cidr, string address, bool expected)
    {
        Assert.Equal(expected, Parse(cidr).Contains(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("192.168.0.0/16", "192.168.17.0/24", true)]
    [InlineData("192.168.17.0/24", "192.168.0.0/16", false)]
    [InlineData("192.168.17.0/24", "192.168.17.0/24", true)]
    [InlineData("192.168.17.0/24", "192.168.18.0/24", false)]
    [InlineData("0.0.0.0/0", "::/0", false)]
    public void Contains_Network(string outer, string inner, bool expected)
    {
        Assert.Equal(expected, Parse(outer).Contains(Parse(inner)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.17.0/33")]
    [InlineData("192.168.17.0/-1")]
    [InlineData("192.168.17.0/abc")]
    [InlineData("192.168.17/24")]
    [InlineData("1/8")]
    [InlineData("not-an-ip")]
    [InlineData("fd00::/129")]
    [InlineData("192.168.17.0/")]
    [InlineData("192.168.17.0/24/8")]
    [InlineData("192.168.17.0/+24")]
    [InlineData("192.168.17.0/ 24")]
    [InlineData("192.168.17.0/2 4")]
    [InlineData("192.168.17.256/24")]
    [InlineData("192.168.017.0/24x")]
    [InlineData("/24")]
    public void TryParse_RejectsInvalid(string text)
    {
        Assert.False(Cidr.TryParse(text, out _));
    }

    [Fact]
    public void ToString_NormalizesToNetworkAddress()
    {
        Assert.Equal("192.168.17.0/24", Parse("192.168.17.5/24").ToString());
        Assert.Equal(Parse("192.168.17.5/24"), Parse("192.168.17.0/24"));
    }

    private static UInt128 ToInteger(byte[] bytes)
    {
        UInt128 value = 0;
        foreach (byte b in bytes)
            value = (value << 8) | b;

        return value;
    }

    /// <summary>The obvious implementation of "is this address in that network": mask both sides as big integers.</summary>
    private static bool ReferenceContains(byte[] network, int prefix, byte[] address)
    {
        int bits = network.Length * 8;
        UInt128 all = bits == 128 ? UInt128.MaxValue : (UInt128.One << bits) - 1;
        UInt128 mask = prefix == 0 ? 0 : (all << (bits - prefix)) & all;

        return (ToInteger(network) & mask) == (ToInteger(address) & mask);
    }

    [Fact]
    public void Contains_AgreesWithAReferenceImplementation_ForRandomNetworksAndAddresses()
    {
        Random random = new(20261019);

        for (int i = 0; i < 20000; i++)
        {
            bool v6 = i % 2 == 1;
            byte[] network = new byte[v6 ? 16 : 4];
            random.NextBytes(network);
            int prefix = random.Next(0, network.Length * 8 + 1);

            // Half of the addresses share the network's leading bits, otherwise a random address would almost never match.
            byte[] address = new byte[network.Length];
            random.NextBytes(address);
            if (random.Next(2) == 0)
            {
                for (int bit = 0; bit < prefix; bit++)
                    SetBit(address, bit, GetBit(network, bit));

                if (prefix < network.Length * 8 && random.Next(4) == 0)
                    SetBit(address, prefix, !GetBit(network, prefix)); // differ exactly at the first host bit: must still match
            }

            Cidr cidr = Parse($"{new IPAddress(network)}/{prefix}");
            IPAddress client = new(address);

            // An IPv6 address in ::ffff:0:0/96 is an IPv4 client by definition, and is matched as such.
            if (v6 && client.IsIPv4MappedToIPv6)
                continue;

            Assert.True(ReferenceContains(network, prefix, address) == cidr.Contains(client), $"{cidr} vs {client}");
        }
    }

    private static bool GetBit(byte[] bytes, int bit) => (bytes[bit / 8] & (0x80 >> (bit % 8))) != 0;

    private static void SetBit(byte[] bytes, int bit, bool value)
    {
        if (value)
            bytes[bit / 8] |= (byte)(0x80 >> (bit % 8));
        else
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
    }

    [Theory]
    [InlineData("0.0.0.0/0", "255.255.255.255", true)]
    [InlineData("0.0.0.0/0", "::1", false)] // a default IPv4 row does not catch IPv6 clients
    [InlineData("::/0", "2001:db8::1", true)]
    [InlineData("::/0", "192.168.1.1", false)] // ...and the other way round
    [InlineData("::/0", "::ffff:192.168.1.1", false)] // an IPv4-mapped client is an IPv4 client
    [InlineData("10.0.0.0/31", "10.0.0.1", true)]
    [InlineData("10.0.0.0/31", "10.0.0.2", false)]
    [InlineData("10.0.0.7/32", "10.0.0.7", true)]
    [InlineData("::1/128", "::1", true)]
    [InlineData("::1/128", "::2", false)]
    [InlineData("fd00::/61", "fd00::7:ffff", true)] // prefix is not a multiple of 8
    [InlineData("fd00::/61", "fd00:0:0:8::", false)]
    [InlineData("fe80::/10", "febf:ffff::1", true)]
    [InlineData("fe80::/10", "fec0::1", false)]
    public void Contains_Address_EdgeCases(string cidr, string address, bool expected)
    {
        Assert.Equal(expected, Parse(cidr).Contains(IPAddress.Parse(address)));
    }

    [Fact]
    public void Contains_Address_IgnoresTheScopeOfAnIpv6Address()
    {
        IPAddress scoped = IPAddress.Parse("fe80::1%1");

        Assert.True(Parse("fe80::/10").Contains(scoped));
    }

    [Fact]
    public void EqualNetworks_WrittenDifferently_AreEqual_AndHashTheSame()
    {
        Cidr a = Parse("192.168.17.5/24");
        Cidr b = Parse(" 192.168.17.200/24 ");

        Assert.True(a.Equals(b));
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Single(new HashSet<Cidr> { a, b });
    }

    [Fact]
    public void DifferentNetworks_AreNotEqual()
    {
        Cidr net = Parse("10.0.0.0/8");

        Assert.False(net.Equals(Parse("10.0.0.0/9")));
        Assert.False(net.Equals(Parse("11.0.0.0/8")));
        Assert.False(net.Equals(Parse("::/0")));
        Assert.False(net.Equals(null));
        Assert.False(net.Equals("10.0.0.0/8"));
        Assert.False(Parse("0.0.0.0/0").Equals(Parse("::/0")));
    }

    [Theory]
    [InlineData("1.2.3.4", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:1.2.3.4", true)]
    [InlineData("1.2.3", false)]
    [InlineData("1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1.2.3.256", false)]
    [InlineData("1.2.3.-4", false)]
    [InlineData("1,2,3,4", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseStrictAddress_OnlyAcceptsCompleteAddresses(string? text, bool expected)
    {
        Assert.Equal(expected, Cidr.TryParseStrictAddress(text, out _));
    }

    [Fact]
    public void ToString_OfIpv6Networks_IsNormalized()
    {
        Assert.Equal("fd00:17::/64", Parse("FD00:0017:0:0:0:0:0:5/64").ToString());
        Assert.Equal("::/0", Parse("2001:db8::1/0").ToString());
    }
}
