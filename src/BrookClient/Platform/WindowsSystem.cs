using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using BrookClient.Core;
using Microsoft.Win32;

namespace BrookClient.Platform;

/// <summary>The current user's Internet Settings (what Edge, Chrome and most apps follow). HKCU: no admin needed.</summary>
internal sealed class RegistryProxyStore : IProxySettingsStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public ProxySettings Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return new ProxySettings(
            key?.GetValue("ProxyEnable") is int e && e != 0,
            key?.GetValue("ProxyServer") as string,
            key?.GetValue("ProxyOverride") as string,
            key?.GetValue("AutoConfigURL") as string);
    }

    public void Write(ProxySettings s)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            key.SetValue("ProxyEnable", s.Enable ? 1 : 0, RegistryValueKind.DWord);
            Set(key, "ProxyServer", s.Server);
            Set(key, "ProxyOverride", s.Override);
            Set(key, "AutoConfigURL", s.AutoConfigUrl);
        }
        Native.NotifyProxyChanged();
    }

    private static void Set(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }
}

/// <summary>"Launch at sign-in" through the per-user Run key.</summary>
internal static class LoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return Autostart.Matches(key?.GetValue(Autostart.ValueName) as string, ExePath);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Autostart.ValueName, Autostart.Command(ExePath), RegistryValueKind.String);
        else key.DeleteValue(Autostart.ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Blocking network helpers. Call off the UI thread.</summary>
internal static class Net
{
    public static List<string> ResolveIPv4(string host)
    {
        if (IPv4.IsValid(host)) return [host];
        try
        {
            return Dns.GetHostAddresses(host)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (SocketException)
        {
            return [];
        }
    }

    /// <summary>True when something already listens on <paramref name="port"/> (any local address).</summary>
    public static bool PortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port);

    public static bool AcceptsConnections(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(300) && client.Connected;
        }
        catch (Exception e) when (e is SocketException or AggregateException)
        {
            return false;
        }
    }
}

/// <summary>Plain-text diagnostics in %LOCALAPPDATA%\BrookClient\client.log.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var path = Paths.AppLog;
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
