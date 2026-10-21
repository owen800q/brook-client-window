namespace BrookClient.Core;

/// <summary>Arguments for <c>brook connect</c>, which runs unprivileged and serves SOCKS5 + HTTP on loopback.</summary>
public static class BrookCommand
{
    public static IReadOnlyList<string> Arguments(BrookLink link, string pinIP, AppConfig config) =>
    [
        "connect",
        "--link", link.Pinned(pinIP, config.UdpOverTcp),
        "--socks5", $"127.0.0.1:{config.SocksPort}",
        "--http", $"127.0.0.1:{config.HttpPort}",
    ];
}

public static class BrookCommandRelay
{
    /// <summary><c>brook connect</c> arguments when the server is reached through a local relay to an HTTP proxy.</summary>
    public static IReadOnlyList<string> Arguments(BrookLink link, int relayPort, AppConfig config) =>
    [
        "connect",
        "--link", link.ThroughRelay(relayPort),
        "--socks5", $"127.0.0.1:{config.SocksPort}",
        "--http", $"127.0.0.1:{config.HttpPort}",
    ];
}

public static class RetryPolicy
{
    public const int MaxRetries = 5;

    /// <summary>Back-off before retry number <paramref name="attempt"/> (1-based): 4, 8, 16, 30, 30 seconds.</summary>
    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Math.Min(2 << Math.Min(attempt, 8), 30));
}

public static class LinkImport
{
    /// <summary>Every brook:// link in pasted text (one per line or separated by spaces), in order, without duplicates.</summary>
    public static IReadOnlyList<string> Links(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<string>();
        foreach (var token in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith("brook://", StringComparison.Ordinal) && !found.Contains(token)) found.Add(token);
        return found;
    }

    /// <summary>New profiles for the pasted links; a link without a <c>name</c> becomes "Server N".</summary>
    public static IReadOnlyList<Profile> Profiles(string? text, int existingCount)
    {
        var list = new List<Profile>();
        foreach (var link in Links(text))
        {
            var name = BrookLink.TryParse(link, out var parsed) && !string.IsNullOrWhiteSpace(parsed!.Name)
                ? parsed.Name!
                : $"Server {existingCount + list.Count + 1}";
            list.Add(new Profile(name, link));
        }
        return list;
    }
}

/// <summary>The per-user "Run" entry (HKCU, no admin) that starts the client at sign-in.</summary>
public static class Autostart
{
    public const string ValueName = "BrookClient";
    public const string Flag = "--autostart";

    public static string Command(string exePath) => $"\"{exePath}\" {Flag}";

    /// <summary>True when <paramref name="command"/> launches <paramref name="exePath"/> (ignoring case and quoting).</summary>
    public static bool Matches(string? command, string exePath)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var c = command.Trim();
        string target;
        if (c.StartsWith('"'))
        {
            var close = c.IndexOf('"', 1);
            target = close > 0 ? c[1..close] : c[1..];
        }
        else
        {
            var space = c.IndexOf(' ');
            target = space > 0 ? c[..space] : c;
        }
        return string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase);
    }
}
