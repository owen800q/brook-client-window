using System.Text;

namespace BrookClient.Core;

public sealed class BrookLinkException(string message) : FormatException(message);

public enum BrookLinkKind { Server, WsServer, WssServer, QuicServer, Socks5 }

/// <summary>A parsed <c>brook://kind?kind=...&amp;password=...</c> link, as produced by <c>brook link</c>.</summary>
public sealed class BrookLink : IEquatable<BrookLink>
{
    private static readonly (BrookLinkKind Kind, string Name, string? Scheme, int? DefaultPort)[] Kinds =
    [
        (BrookLinkKind.Server, "server", null, null),
        (BrookLinkKind.WsServer, "wsserver", "ws", 80),
        (BrookLinkKind.WssServer, "wssserver", "wss", 443),
        (BrookLinkKind.QuicServer, "quicserver", "quic", 443),
        (BrookLinkKind.Socks5, "socks5", "socks5", 1080),
    ];

    private readonly List<KeyValuePair<string, string>> _query;
    private readonly int? _addressPort;
    /// <summary>Server URL with the implicit default port made explicit, when it was missing (brook requires it).</summary>
    private readonly string? _serverValueWithPort;

    public string Raw { get; }
    public BrookLinkKind Kind { get; }
    public string KindName => Kinds.First(k => k.Kind == Kind).Name;
    /// <summary>Host in the server parameter (domain or IP). For ws/wss this is also the TLS/Host name.</summary>
    public string Host { get; }
    public int Port { get; }
    /// <summary>Host from the <c>address</c> parameter, which brook dials instead of resolving <see cref="Host"/>.</summary>
    public string? AddressHost { get; }

    public BrookLink(string input)
    {
        var raw = input.Trim();
        const string prefix = "brook://";
        var q = raw.IndexOf('?');
        if (!raw.StartsWith(prefix, StringComparison.Ordinal) || q < 0) throw new BrookLinkException("not a brook:// link");
        var kindName = raw[prefix.Length..q];
        var kindIndex = Array.FindIndex(Kinds, k => k.Name == kindName);
        if (kindIndex < 0) throw new BrookLinkException($"unsupported kind '{kindName}'");
        var kind = Kinds[kindIndex];
        var query = ParseQuery(raw[(q + 1)..]);
        var serverValue = Lookup(query, kindName);
        if (string.IsNullOrEmpty(serverValue)) throw new BrookLinkException($"missing {kindName} parameter");

        var endpoint = kind.Scheme is { } scheme
            ? ParseUrlEndpoint(serverValue, scheme, kind.DefaultPort)
            : ParseHostPort(serverValue);
        if (endpoint is null) throw new BrookLinkException($"bad {kindName} address '{serverValue}'");

        if (Lookup(query, "address") is { Length: > 0 } address)
        {
            var a = ParseHostPort(address) ?? throw new BrookLinkException($"bad address '{address}'");
            AddressHost = a.Host;
            _addressPort = a.Port;
        }

        if (kind.Scheme is not null && UrlPort(serverValue) is null)
            _serverValueWithPort = InsertPort(serverValue, endpoint.Value.Port);

        Raw = raw;
        Kind = kind.Kind;
        Host = endpoint.Value.Host;
        Port = endpoint.Value.Port;
        _query = query;
    }

    public static bool TryParse(string input, out BrookLink? link)
    {
        try { link = new BrookLink(input); return true; }
        catch (BrookLinkException) { link = null; return false; }
    }

    public string? Name => QueryValue("name");

    /// <summary>What brook connects to: <c>address</c> if given, otherwise the server host.</summary>
    public string DialHost => AddressHost ?? Host;
    public int DialPort => _addressPort ?? Port;
    public bool DialHostIsIPv4 => IPv4.IsValid(DialHost);

    public string? QueryValue(string key) => Lookup(_query, key);

    /// <summary>
    /// Returns a link that makes brook dial <paramref name="ip"/> exactly, so the server connection does not depend on
    /// later DNS answers. Also makes implicit URL ports explicit, since <c>brook connect</c> rejects URLs without one.
    /// <paramref name="udpOverTcp"/> carries UDP inside the TCP/QUIC stream (brook's udpovertcp / udpoverstream),
    /// for servers whose firewall drops UDP. ws/wss already do this inherently.
    /// </summary>
    public string Pinned(string ip, bool udpOverTcp = false)
    {
        var items = new List<KeyValuePair<string, string>>(_query);
        var changed = false;
        void Set(string key, string value)
        {
            changed = true;
            var i = items.FindIndex(kv => kv.Key == key);
            if (i >= 0) items[i] = new(key, value); else items.Add(new(key, value));
        }
        switch (Kind)
        {
            case BrookLinkKind.Server:
                if (!DialHostIsIPv4) Set("server", $"{ip}:{Port}");
                break;
            case BrookLinkKind.Socks5:
                break; // not usable with `brook connect`; rejected by config validation
            default:
                if (_serverValueWithPort is { } v) Set(KindName, v);
                // Keep the domain in the URL for TLS SNI / Host header; pin the dial address.
                if (!DialHostIsIPv4) Set("address", $"{ip}:{DialPort}");
                break;
        }
        if (udpOverTcp)
        {
            var flag = Kind switch
            {
                BrookLinkKind.Server => "udpovertcp",
                BrookLinkKind.QuicServer => "udpoverstream",
                _ => null,
            };
            if (flag is not null && QueryValueIn(items, flag) != "true") Set(flag, "true");
        }
        return changed ? $"brook://{KindName}?" + EncodeQuery(items) : Raw;
    }

    /// <summary>
    /// A link that makes brook dial a local relay (127.0.0.1:<paramref name="relayPort"/>) instead of the server, for
    /// reaching the server through an HTTP proxy. ws/wss keep the server name in the URL for TLS SNI and the Host header.
    /// UDP is always carried inside the stream, since an HTTP proxy only passes TCP. QUIC (UDP) can't be relayed.
    /// </summary>
    public string ThroughRelay(int relayPort)
    {
        var items = new List<KeyValuePair<string, string>>(_query);
        void Set(string key, string value)
        {
            var i = items.FindIndex(kv => kv.Key == key);
            if (i >= 0) items[i] = new(key, value); else items.Add(new(key, value));
        }
        var local = $"127.0.0.1:{relayPort}";
        switch (Kind)
        {
            case BrookLinkKind.Server:
                Set("server", local);
                Set("udpovertcp", "true");
                break;
            case BrookLinkKind.WsServer:
            case BrookLinkKind.WssServer:
                if (_serverValueWithPort is { } v) Set(KindName, v);
                Set("address", local);
                break;
            default:
                throw new BrookLinkException($"{KindName} links can't go through an HTTP proxy");
        }
        return $"brook://{KindName}?" + EncodeQuery(items);
    }

    public bool Equals(BrookLink? other) => other is not null && other.Raw == Raw;
    public override bool Equals(object? obj) => Equals(obj as BrookLink);
    public override int GetHashCode() => Raw.GetHashCode(StringComparison.Ordinal);
    public override string ToString() => Raw;

    // ---- Parsing helpers -------------------------------------------------------------------

    private static string? Lookup(List<KeyValuePair<string, string>> q, string key) => QueryValueIn(q, key);

    private static string? QueryValueIn(List<KeyValuePair<string, string>> q, string key)
    {
        foreach (var kv in q) if (kv.Key == key) return kv.Value;
        return null;
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string s)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var pair in s.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = Decode(eq < 0 ? pair : pair[..eq]);
            if (k is null) continue;
            list.Add(new(k, eq < 0 ? "" : Decode(pair[(eq + 1)..]) ?? ""));
        }
        return list;
    }

    /// <summary>Form decoding (<c>+</c> is a space). Returns null for malformed percent escapes, like Swift's removingPercentEncoding.</summary>
    private static string? Decode(string s)
    {
        s = s.Replace('+', ' ');
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '%')
            {
                if (i + 2 >= s.Length || !Uri.IsHexDigit(s[i + 1]) || !Uri.IsHexDigit(s[i + 2])) return null;
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }
        try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>Matches Go's url.Values.Encode (sorted keys, QueryEscape) like <c>brook link</c> emits.</summary>
    internal static string EncodeQuery(IEnumerable<KeyValuePair<string, string>> items) =>
        string.Join("&", items.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{Escape(kv.Key)}={Escape(kv.Value)}"));

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary><c>host:port</c> or <c>[v6]:port</c>.</summary>
    internal static (string Host, int Port)? ParseHostPort(string s)
    {
        string host, portPart;
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close < 0 || close + 1 >= s.Length || s[close + 1] != ':') return null;
            host = s[1..close];
            portPart = s[(close + 2)..];
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon < 0) return null;
            host = s[..colon];
            portPart = s[(colon + 1)..];
            if (host.Contains(':')) return null;
        }
        return host.Length > 0 && ParsePort(portPart) is { } port ? (host, port) : null;
    }

    private static (string Host, int Port)? ParseUrlEndpoint(string s, string scheme, int? defaultPort)
    {
        var m = SplitUrl(s);
        if (m is null || m.Value.Scheme != scheme) return null;
        var host = m.Value.Host;
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.Length == 0) return null;
        int? port = m.Value.Port is { } p ? ParsePort(p) : defaultPort;
        if (m.Value.Port is not null && port is null) return null;
        return port is { } ok ? (host, ok) : null;
    }

    private static int? UrlPort(string s) => SplitUrl(s)?.Port is { } p ? ParsePort(p) : null;

    /// <summary>Splits <c>scheme://[userinfo@]host[:port][/path...]</c>; enough for brook server URLs.</summary>
    private static (string Scheme, string Host, string? Port, int AuthorityEnd)? SplitUrl(string s)
    {
        var sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return null;
        var scheme = s[..sep];
        if (!scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return null;
        var start = sep + 3;
        var end = s.IndexOfAny(['/', '?', '#'], start);
        if (end < 0) end = s.Length;
        var authority = s[start..end];
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        string host; string? port = null;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return null;
            host = authority[..(close + 1)];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':') return null;
                port = rest[1..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0) { host = authority[..colon]; port = authority[(colon + 1)..]; }
            else host = authority;
        }
        if (port is { Length: 0 }) port = null;
        return (scheme, host, port, end);
    }

    private static string InsertPort(string url, int port)
    {
        var m = SplitUrl(url)!.Value;
        var authorityEnd = m.AuthorityEnd;
        // Drop a trailing ':' with an empty port before adding ours.
        var head = url[..authorityEnd].TrimEnd(':');
        return $"{head}:{port}{url[authorityEnd..]}";
    }

    private static int? ParsePort(string s) =>
        s.Length is > 0 and <= 5 && s.All(char.IsAsciiDigit) && int.Parse(s) is var p and >= 1 and <= 65535 ? p : null;
}
