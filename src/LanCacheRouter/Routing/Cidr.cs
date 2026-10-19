using System.Net;
using System.Net.Sockets;

namespace LanCacheRouter.Routing;

/// <summary>An IPv4 or IPv6 network in CIDR notation. A bare address is treated as a host route (/32, /128).</summary>
public sealed class Cidr : IEquatable<Cidr>
{
    /// <summary>
    /// Network address bytes with the host bits cleared.
    /// </summary>
    private readonly byte[] _network;

    /// <summary>
    /// Creates a CIDR from an already masked network address.
    /// </summary>
    /// <param name="network">Network address bytes with the host bits cleared.</param>
    /// <param name="prefixLength">Number of leading network bits.</param>
    /// <param name="family">IPv4 or IPv6.</param>
    private Cidr(byte[] network, int prefixLength, AddressFamily family)
    {
        _network = network;
        PrefixLength = prefixLength;
        Family = family;
    }

    /// <summary>
    /// Address family of the network: <see cref="AddressFamily.InterNetwork"/> (IPv4) or <see cref="AddressFamily.InterNetworkV6"/> (IPv6).
    /// </summary>
    public AddressFamily Family { get; }

    /// <summary>
    /// Number of leading bits that identify the network (0 to 32 for IPv4, 0 to 128 for IPv6).
    /// </summary>
    public int PrefixLength { get; }

    /// <summary>
    /// Parses <c>a.b.c.d/n</c>, an IPv6 equivalent, or a bare address (host route). Host bits are masked off,
    /// so <c>192.168.17.5/24</c> becomes <c>192.168.17.0/24</c>.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="cidr">The parsed network; only valid when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> if the text is a valid CIDR.</returns>
    public static bool TryParse(string? text, out Cidr cidr)
    {
        cidr = null!;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string addressPart = text.Trim();
        string? prefixPart = null;

        int slash = addressPart.IndexOf('/');
        if (slash >= 0)
        {
            prefixPart = addressPart[(slash + 1)..];
            addressPart = addressPart[..slash];
        }

        if (!TryParseStrictAddress(addressPart, out IPAddress? address))
            return false;

        byte[] bytes = address.GetAddressBytes();
        int maxPrefix = bytes.Length * 8;
        int prefix = maxPrefix;

        if (prefixPart is not null
            && (prefixPart.Length is 0 or > 3 || !prefixPart.All(char.IsAsciiDigit) || !int.TryParse(prefixPart, out prefix) || prefix > maxPrefix))
        {
            return false;
        }

        cidr = new Cidr(Mask(bytes, prefix), prefix, address.AddressFamily);
        return true;
    }

    /// <summary>
    /// Parses an IPv4 (exactly four dotted decimal parts) or IPv6 address. Rejects the shorthand forms
    /// IPAddress.TryParse accepts for IPv4 (e.g. "1" or "10.1").
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="address">The parsed address; only valid when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> if the text is a complete IPv4 or IPv6 address.</returns>
    public static bool TryParseStrictAddress(string? text, out IPAddress address)
    {
        address = IPAddress.None;

        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text, out IPAddress? parsed))
            return false;

        if (parsed.AddressFamily == AddressFamily.InterNetwork && text.Count(c => c == '.') != 3)
            return false;

        if (parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;

        address = parsed;
        return true;
    }

    /// <summary>
    /// Tests whether an address lies inside this network. IPv4-mapped IPv6 addresses (<c>::ffff:a.b.c.d</c>) are treated as IPv4.
    /// </summary>
    /// <param name="address">The address to test.</param>
    /// <returns><c>true</c> if the address is in the network; always <c>false</c> for a different address family.</returns>
    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != Family)
            return false;

        return Matches(address.GetAddressBytes());
    }

    /// <summary>
    /// Tests whether every address of <paramref name="other"/> is also inside this network.
    /// </summary>
    /// <param name="other">The network to test.</param>
    /// <returns><c>true</c> if <paramref name="other"/> is the same as or a subnet of this network.</returns>
    public bool Contains(Cidr other)
    {
        return other.Family == Family && other.PrefixLength >= PrefixLength && Matches(other._network);
    }

    /// <summary>
    /// Compares the first <see cref="PrefixLength"/> bits of the given address bytes with the network.
    /// </summary>
    /// <param name="addressBytes">Address bytes of the same family as this network.</param>
    /// <returns><c>true</c> if the network bits are equal.</returns>
    private bool Matches(byte[] addressBytes)
    {
        int fullBytes = PrefixLength / 8;
        int remainingBits = PrefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != _network[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        int mask = (0xFF << (8 - remainingBits)) & 0xFF;
        return (addressBytes[fullBytes] & mask) == _network[fullBytes];
    }

    /// <summary>
    /// Clears all bits after the first <paramref name="prefixLength"/> bits.
    /// </summary>
    /// <param name="address">Address bytes.</param>
    /// <param name="prefixLength">Number of leading bits to keep.</param>
    /// <returns>A new array with the host bits set to zero.</returns>
    private static byte[] Mask(byte[] address, int prefixLength)
    {
        byte[] result = new byte[address.Length];

        for (int i = 0; i < address.Length; i++)
        {
            int bitsInByte = Math.Clamp(prefixLength - (i * 8), 0, 8);
            int mask = (0xFF << (8 - bitsInByte)) & 0xFF;
            result[i] = (byte)(address[i] & mask);
        }

        return result;
    }

    /// <summary>
    /// Two CIDRs are equal if they describe the same network, regardless of how they were written.
    /// </summary>
    /// <param name="other">The CIDR to compare with.</param>
    public bool Equals(Cidr? other)
    {
        return other is not null && Family == other.Family && PrefixLength == other.PrefixLength && _network.AsSpan().SequenceEqual(other._network);
    }

    /// <inheritdoc cref="Equals(Cidr?)"/>
    public override bool Equals(object? obj) => Equals(obj as Cidr);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Family, PrefixLength, Convert.ToHexString(_network));

    /// <summary>
    /// The normalized notation, for example <c>192.168.17.0/24</c>.
    /// </summary>
    public override string ToString() => $"{new IPAddress(_network)}/{PrefixLength}";
}
