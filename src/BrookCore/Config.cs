using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrookClient.Core;

/// <summary>How brook reaches the Brook server: directly, or through an HTTP proxy (CONNECT).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UpstreamProxyMode>))]
public enum UpstreamProxyMode
{
    /// <summary>Connect to the server directly.</summary>
    Off,
    /// <summary>Use the proxy Windows had before we connected (manual server or PAC script); direct if none.</summary>
    System,
    /// <summary>Use <see cref="AppConfig.UpstreamProxyHost"/>:<see cref="AppConfig.UpstreamProxyPort"/>.</summary>
    Manual,
}

public sealed record Profile(string Name, string Link)
{
    public string Name { get; set; } = Name;
    public string Link { get; set; } = Link;
}

/// <summary>
/// User settings. Keys match the macOS client's config.json, so a config copied from a Mac loads as is
/// (macOS-only keys such as <c>mode</c>, <c>dns</c> and <c>blockIPv6</c> are ignored).
/// </summary>
public sealed class AppConfig : IEquatable<AppConfig>
{
    public static readonly string[] DefaultBypass = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4"];

    public List<Profile> Profiles { get; set; } = [];
    public int? SelectedIndex { get; set; }
    /// <summary>Addresses that never go through the proxy (LAN devices such as a TV, NAS or printer).</summary>
    [JsonPropertyName("bypassCIDRs")]
    public List<string> BypassCidrs { get; set; } = [.. DefaultBypass];
    public int SocksPort { get; set; } = 1080;
    public int HttpPort { get; set; } = 1081;
    public bool ConnectOnLaunch { get; set; }
    /// <summary>Tunnel UDP (incl. DNS) inside TCP; needed when the server's firewall drops UDP.</summary>
    [JsonPropertyName("udpOverTCP")]
    public bool UdpOverTcp { get; set; } = true;
    /// <summary>Point the Windows (per-user) proxy settings at brook while connected. Off = only run the local proxies.</summary>
    public bool SetSystemProxy { get; set; } = true;

    /// <summary>For networks where the internet is only reachable through an HTTP proxy.</summary>
    public UpstreamProxyMode UpstreamProxy { get; set; } = UpstreamProxyMode.Off;
    public string UpstreamProxyHost { get; set; } = "";
    public int UpstreamProxyPort { get; set; } = 8080;
    /// <summary>Basic authentication; empty = the proxy needs no login.</summary>
    public string UpstreamProxyUsername { get; set; } = "";
    /// <summary>Never written as plain text: <see cref="ConfigStore"/> stores it encrypted as <c>upstreamProxyPasswordData</c>.</summary>
    [JsonIgnore]
    public string UpstreamProxyPassword { get; set; } = "";
    /// <summary>The encrypted password, as stored on disk. Managed by <see cref="ConfigStore"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("upstreamProxyPasswordData")]
    internal string? UpstreamProxyPasswordData { get; set; }

    [JsonIgnore]
    public Profile? SelectedProfile =>
        SelectedIndex is { } i && i >= 0 && i < Profiles.Count ? Profiles[i] : null;

    public List<string> ValidationErrors()
    {
        var errors = new List<string>();
        foreach (var p in Profiles)
        {
            if (!BrookLink.TryParse(p.Link, out var link))
            {
                errors.Add($"Server '{p.Name}': invalid brook link");
                continue;
            }
            if (link!.Kind == BrookLinkKind.Socks5) errors.Add($"Server '{p.Name}': socks5 links are not supported by brook connect");
        }
        foreach (var c in BypassCidrs)
        {
            if (!IPv4Cidr.TryParse(c, out var cidr))
            {
                if (!BypassDomain.TryNormalize(c, out _)) errors.Add($"Bypass: '{c}' is not an IPv4 address/CIDR or a domain like *.example.com");
            }
            else if (cidr.Prefix < ProxyPlan.MinBypassPrefix) errors.Add($"Bypass: '{c}' is too broad (at most /{ProxyPlan.MinBypassPrefix})");
        }
        foreach (var (label, port) in new[] { ("SOCKS5", SocksPort), ("HTTP", HttpPort) })
            if (port is < 1 or > 65535) errors.Add($"{label} port {port} is out of range");
        if (SocksPort == HttpPort) errors.Add("SOCKS5 and HTTP ports must differ");
        if (UpstreamProxy == UpstreamProxyMode.Manual)
        {
            if (!UpstreamProxyEndpoint.IsValidHost(UpstreamProxyHost.Trim())) errors.Add("Proxy: enter the proxy's host name or IP address");
            if (UpstreamProxyPort is < 1 or > 65535) errors.Add($"Proxy port {UpstreamProxyPort} is out of range");
        }
        if (UpstreamProxy != UpstreamProxyMode.Off && UpstreamProxyUsername.Contains(':'))
            errors.Add("Proxy: the username can't contain ':'");
        if (UpstreamProxy != UpstreamProxyMode.Off && SelectedProfile is { } sel && BrookLink.TryParse(sel.Link, out var selLink)
            && selLink!.Kind == BrookLinkKind.QuicServer)
            errors.Add($"Server '{sel.Name}': QUIC servers can't be reached through an HTTP proxy (it only carries TCP)");
        return errors;
    }

    public AppConfig Clone()
    {
        var c = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, ConfigStore.JsonOptions), ConfigStore.JsonOptions)!;
        c.UpstreamProxyPassword = UpstreamProxyPassword; // not serialized
        return c;
    }

    public bool Equals(AppConfig? o) =>
        o is not null && Profiles.SequenceEqual(o.Profiles) && SelectedIndex == o.SelectedIndex && BypassCidrs.SequenceEqual(o.BypassCidrs)
        && SocksPort == o.SocksPort && HttpPort == o.HttpPort && ConnectOnLaunch == o.ConnectOnLaunch && UdpOverTcp == o.UdpOverTcp
        && SetSystemProxy == o.SetSystemProxy && UpstreamProxy == o.UpstreamProxy && UpstreamProxyHost == o.UpstreamProxyHost
        && UpstreamProxyPort == o.UpstreamProxyPort && UpstreamProxyUsername == o.UpstreamProxyUsername
        && UpstreamProxyPassword == o.UpstreamProxyPassword;

    public override bool Equals(object? obj) => Equals(obj as AppConfig);
    public override int GetHashCode() => HashCode.Combine(Profiles.Count, SelectedIndex, SocksPort, HttpPort);
}

/// <summary>Encrypts secrets at rest. The app uses Windows DPAPI (current user); tests and other OSes use <see cref="Plain"/>.</summary>
public interface ISecretProtector
{
    string Protect(string secret);
    /// <summary>Returns null when the data can't be decrypted (e.g. copied from another user or PC).</summary>
    string? Unprotect(string data);
}

public sealed class PlainSecretProtector : ISecretProtector
{
    public static readonly PlainSecretProtector Instance = new();
    private const string Prefix = "plain:";

    public string Protect(string secret) => Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret));

    public string? Unprotect(string data)
    {
        if (!data.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data[Prefix.Length..])); }
        catch (FormatException) { return null; }
    }
}

public sealed class ConfigStore(string path, ISecretProtector? protector = null)
{
    private readonly ISecretProtector _protector = protector ?? PlainSecretProtector.Instance;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string Path { get; } = path;

    /// <summary><c>%APPDATA%\BrookClient\config.json</c>: per-user and private to the user by default ACLs.</summary>
    public static ConfigStore Standard => new(System.IO.Path.Combine(Paths.AppData, "config.json"), DefaultProtector);

    /// <summary>Set by the app at startup (DPAPI on Windows).</summary>
    public static ISecretProtector? DefaultProtector { get; set; }

    public AppConfig Load()
    {
        if (!File.Exists(Path)) return new AppConfig();
        var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path), JsonOptions) ?? new AppConfig();
        // Explicit nulls in hand-edited files would otherwise crash later.
        c.Profiles ??= [];
        c.BypassCidrs ??= [.. AppConfig.DefaultBypass];
        c.UpstreamProxyHost ??= "";
        c.UpstreamProxyUsername ??= "";
        c.UpstreamProxyPassword = c.UpstreamProxyPasswordData is { Length: > 0 } data ? _protector.Unprotect(data) ?? "" : "";
        c.UpstreamProxyPasswordData = null;
        return c;
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        config.UpstreamProxyPasswordData = config.UpstreamProxyPassword.Length > 0 ? _protector.Protect(config.UpstreamProxyPassword) : null;
        try { File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions)); }
        finally { config.UpstreamProxyPasswordData = null; }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); // links contain passwords
        File.Move(tmp, Path, overwrite: true);
    }
}

/// <summary>Per-user locations; nothing here needs administrator rights.</summary>
public static class Paths
{
    /// <summary>
    /// Keeps all files in one folder instead of %APPDATA% / %LOCALAPPDATA%: set by tests, or by the user through the
    /// BROOKCLIENT_DATA environment variable (e.g. to keep everything on a USB stick).
    /// </summary>
    public static string? RootOverride { get; set; } = NonEmpty(Environment.GetEnvironmentVariable("BROOKCLIENT_DATA"));

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static string AppData => Ensure(RootOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrookClient"));

    public static string LocalData => Ensure(RootOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrookClient"));

    public static string BrookLog => System.IO.Path.Combine(LocalData, "brook.log");
    public static string AppLog => System.IO.Path.Combine(LocalData, "client.log");
    public static string ProxyBackup => System.IO.Path.Combine(LocalData, "proxy-backup.json");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
