namespace BrookClient.Core;

/// <summary>
/// The per-user Windows proxy settings (HKCU\...\Internet Settings) that point apps at brook's local HTTP proxy.
/// Writing these needs no administrator rights.
/// </summary>
public sealed class ProxyPlan
{
    /// <summary>224.0.0.0/4 (multicast) is the broadest legitimate range; anything wider defeats the proxy.</summary>
    public const int MinBypassPrefix = 4;

    /// <summary>Always bypassed: loopback, mDNS names and dot-less intranet names (<c>&lt;local&gt;</c>, kept last).</summary>
    public static readonly string[] LocalNames = ["localhost", "127.*", "[::1]", "*.local"];

    /// <summary>Value for ProxyServer. Only HTTP/HTTPS: Windows treats a <c>socks=</c> entry as SOCKS4, which brook does not serve.</summary>
    public string ProxyServer { get; }

    /// <summary>Value for ProxyOverride: the bypass list as WinINet wildcards, separated by ';'.</summary>
    public string ProxyOverride { get; }

    public IReadOnlyList<string> BypassPatterns { get; }

    public ProxyPlan(int httpPort, IEnumerable<string> bypass)
    {
        if (httpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(httpPort), $"HTTP port {httpPort} is out of range");
        ProxyServer = $"http=127.0.0.1:{httpPort};https=127.0.0.1:{httpPort}";

        var patterns = new List<string>(LocalNames);
        foreach (var raw in bypass)
        {
            if (!IPv4Cidr.TryParse(raw, out _) && BypassDomain.TryNormalize(raw, out var domain))
            {
                if (!patterns.Contains(domain)) patterns.Add(domain);
                continue;
            }
            var cidr = IPv4Cidr.Parse(raw);
            if (cidr.Prefix < MinBypassPrefix) throw new ArgumentException($"Bypass '{raw}' is too broad", nameof(bypass));
            foreach (var w in Wildcards(cidr)) if (!patterns.Contains(w)) patterns.Add(w);
        }
        patterns.Add("<local>");
        BypassPatterns = patterns;
        ProxyOverride = string.Join(";", patterns);
    }

    public ProxySettings Settings => new(true, ProxyServer, ProxyOverride, null);

    /// <summary>
    /// WinINet bypass entries match the host text with '*' wildcards, so a CIDR becomes one pattern per covered
    /// octet value: 192.168.0.0/16 → 192.168.*, 172.16.0.0/12 → 172.16.* … 172.31.*, 10.0.0.1/32 → 10.0.0.1.
    /// </summary>
    public static IReadOnlyList<string> Wildcards(IPv4Cidr cidr)
    {
        var octets = new[] { cidr.Network >> 24 & 0xff, cidr.Network >> 16 & 0xff, cidr.Network >> 8 & 0xff, cidr.Network & 0xff };
        var full = cidr.Prefix / 8;
        if (cidr.Prefix % 8 == 0)
        {
            if (full == 4) return [IPv4.Format(cidr.Network)];
            if (full == 0) return ["*"];
            return [string.Join(".", octets.Take(full)) + ".*"];
        }
        var count = 1u << (8 - cidr.Prefix % 8);
        var head = string.Join("", octets.Take(full).Select(o => $"{o}."));
        var tail = full == 3 ? "" : ".*";
        var list = new List<string>((int)count);
        for (var i = 0u; i < count; i++) list.Add($"{head}{octets[full] + i}{tail}");
        return list;
    }
}

/// <summary>
/// A domain bypass entry: <c>example.com</c> (that exact name) or <c>*.example</c> (every name under it), which WinINet
/// matches as written. A bare dot-less name is not accepted: <c>&lt;local&gt;</c> already bypasses those.
/// </summary>
public static class BypassDomain
{
    public static bool TryNormalize(string raw, out string domain)
    {
        domain = raw.Trim().ToLowerInvariant();
        var name = domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;
        var labels = name.Split('.');
        var ok = name.Length is > 0 and <= 253
            && (labels.Length >= 2 || name.Length < domain.Length)
            && labels.All(l => l.Length is > 0 and <= 63 && l[0] != '-' && l[^1] != '-' && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            && !labels[^1].All(char.IsAsciiDigit); // "10.0.0" is a mistyped address, not a domain
        if (!ok) domain = "";
        return ok;
    }
}

/// <summary>The four Internet Settings values we change. Null means the value is absent.</summary>
public sealed record ProxySettings(bool Enable, string? Server, string? Override, string? AutoConfigUrl);

public interface IProxySettingsStore
{
    ProxySettings Read();
    void Write(ProxySettings settings);
}

/// <summary>
/// Applies a <see cref="ProxyPlan"/> and restores the user's previous settings afterwards. The previous settings are
/// saved to a file first, so they survive a crash: the watchdog or the next launch restores them.
/// </summary>
public sealed class SystemProxy(IProxySettingsStore store, string backupPath)
{
    private sealed record Backup(ProxySettings Previous, ProxySettings Applied);

    public bool IsApplied => File.Exists(backupPath);

    /// <summary>The user's own proxy settings: the saved ones while ours are applied, otherwise the current ones.</summary>
    public ProxySettings UserSettings => ReadBackup()?.Previous ?? store.Read();

    public void Apply(ProxyPlan plan)
    {
        // Reconnects keep the original backup, so we never "restore" our own proxy.
        var previous = ReadBackup()?.Previous ?? store.Read();
        var applied = plan.Settings;
        WriteBackup(new Backup(previous, applied));
        store.Write(applied);
    }

    /// <summary>Puts the user's settings back, unless they changed the proxy themselves since we applied ours.</summary>
    public void Restore()
    {
        var backup = ReadBackup();
        if (backup is null)
        {
            DeleteBackup();
            return;
        }
        var current = store.Read();
        if (current.Enable == backup.Applied.Enable && current.Server == backup.Applied.Server)
            store.Write(backup.Previous);
        DeleteBackup();
    }

    private Backup? ReadBackup()
    {
        try
        {
            return File.Exists(backupPath)
                ? System.Text.Json.JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupPath), ConfigStore.JsonOptions)
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // corrupt file: nothing we can trust to restore
        }
    }

    private void WriteBackup(Backup b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        var tmp = backupPath + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(b, ConfigStore.JsonOptions));
        File.Move(tmp, backupPath, overwrite: true);
    }

    private void DeleteBackup()
    {
        if (File.Exists(backupPath)) File.Delete(backupPath);
    }
}
