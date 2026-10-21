namespace BrookClient.Core;

public static class IPv4
{
    /// <summary>Strict dotted-quad: four decimal octets 0...255, no leading zeros, no whitespace.</summary>
    public static uint? Parse(string? s)
    {
        if (s is null) return null;
        var parts = s.Split('.');
        if (parts.Length != 4) return null;
        uint value = 0;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)) return null;
            if (part.Length > 1 && part[0] == '0') return null;
            var octet = uint.Parse(part);
            if (octet > 255) return null;
            value = value << 8 | octet;
        }
        return value;
    }

    public static bool IsValid(string? s) => Parse(s) is not null;

    public static string Format(uint v) => $"{v >> 24 & 0xff}.{v >> 16 & 0xff}.{v >> 8 & 0xff}.{v & 0xff}";
}

public sealed class CidrException(string raw) : FormatException($"'{raw}' is not an IPv4 address/CIDR")
{
    public string Raw { get; } = raw;
}

public readonly record struct IPv4Cidr
{
    public uint Network { get; }
    public int Prefix { get; }

    public IPv4Cidr(uint network, int prefix)
    {
        if (prefix is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(prefix));
        Prefix = prefix;
        Network = network & Mask(prefix);
    }

    public static IPv4Cidr Parse(string raw)
    {
        var s = raw.Trim();
        var pieces = s.Split('/');
        if (pieces.Length is not (1 or 2) || IPv4.Parse(pieces[0]) is not { } addr) throw new CidrException(raw);
        var prefix = 32;
        if (pieces.Length == 2)
        {
            var p = pieces[1];
            if (p.Length is 0 or > 2 || !p.All(char.IsAsciiDigit) || int.Parse(p) > 32) throw new CidrException(raw);
            prefix = int.Parse(p);
        }
        return new IPv4Cidr(addr, prefix);
    }

    public static bool TryParse(string raw, out IPv4Cidr cidr)
    {
        try { cidr = Parse(raw); return true; }
        catch (CidrException) { cidr = default; return false; }
    }

    internal static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    public bool IsHost => Prefix == 32;

    public bool Contains(string ip) => IPv4.Parse(ip) is { } v && (v & Mask(Prefix)) == Network;

    public bool Contains(IPv4Cidr other) => other.Prefix >= Prefix && (other.Network & Mask(Prefix)) == Network;

    public override string ToString() => $"{IPv4.Format(Network)}/{Prefix}";
}
