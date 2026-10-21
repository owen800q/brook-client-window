using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BrookClient.Core;

namespace BrookClient.Platform;

/// <summary>Encrypts the proxy password for the current Windows user (DPAPI). No admin rights; unreadable to other accounts.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BrookClient.UpstreamProxy");

    public string Protect(string secret) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string data)
    {
        if (!data.StartsWith(Prefix, StringComparison.Ordinal)) return PlainSecretProtector.Instance.Unprotect(data);
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null; // e.g. config copied from another user or PC: ask for the password again
        }
    }
}

/// <summary>Finds the HTTP proxy Windows uses for a URL: a PAC script (via WinHTTP) or the manual proxy server.</summary>
internal static class UpstreamDetection
{
    /// <summary>
    /// The proxy for <c>https://host:port/</c> under <paramref name="settings"/> (the user's own settings, not ours),
    /// or null when Windows would connect directly.
    /// </summary>
    public static (string Host, int Port)? Detect(ProxySettings settings, string host, int port, int ownHttpPort)
    {
        (string Host, int Port)? found = null;
        if (!string.IsNullOrWhiteSpace(settings.AutoConfigUrl))
            found = UpstreamProxyEndpoint.ParseWinHttpProxyList(PacProxyFor(settings.AutoConfigUrl!, $"https://{host}:{port}/"));
        if (found is null && settings.Enable)
            found = UpstreamProxyEndpoint.ParseWindowsProxyServer(settings.Server);
        // Never chain to ourselves (our own Windows proxy setting left over).
        if (found is { } f && f.Port == ownHttpPort && f.Host is "127.0.0.1" or "localhost") return null;
        return found;
    }

    /// <summary>Human-readable summary for the settings window.</summary>
    public static string Describe(ProxySettings settings, int ownHttpPort)
    {
        try
        {
            var p = Detect(settings, "example.com", 443, ownHttpPort);
            if (p is { } x) return $"{x.Host}:{x.Port}" + (string.IsNullOrWhiteSpace(settings.AutoConfigUrl) ? "" : " (from PAC script)");
            return string.IsNullOrWhiteSpace(settings.AutoConfigUrl) ? "none found, connects directly" : "PAC script says direct";
        }
        catch (Exception e)
        {
            return $"could not read ({e.Message})";
        }
    }

    // ---- WinHTTP PAC evaluation ----------------------------------------------------------------

    private const int WINHTTP_ACCESS_TYPE_NO_PROXY = 1;
    private const int WINHTTP_ACCESS_TYPE_NAMED_PROXY = 3;
    private const int WINHTTP_AUTOPROXY_CONFIG_URL = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINHTTP_AUTOPROXY_OPTIONS
    {
        public int dwFlags;
        public int dwAutoDetectFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszAutoConfigUrl;
        public IntPtr lpvReserved;
        public int dwReserved;
        [MarshalAs(UnmanagedType.Bool)] public bool fAutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_PROXY_INFO
    {
        public int dwAccessType;
        public IntPtr lpszProxy;
        public IntPtr lpszProxyBypass;
    }

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpen(string agent, int accessType, string? proxyName, string? proxyBypass, int flags);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WinHttpGetProxyForUrl(IntPtr session, string url, ref WINHTTP_AUTOPROXY_OPTIONS options, out WINHTTP_PROXY_INFO info);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpCloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr mem);

    /// <summary>The proxy list the PAC script at <paramref name="pacUrl"/> returns for <paramref name="url"/>, or null for direct.</summary>
    private static string? PacProxyFor(string pacUrl, string url)
    {
        var session = WinHttpOpen("BrookClient", WINHTTP_ACCESS_TYPE_NO_PROXY, null, null, 0);
        if (session == IntPtr.Zero) return null;
        try
        {
            var options = new WINHTTP_AUTOPROXY_OPTIONS
            {
                dwFlags = WINHTTP_AUTOPROXY_CONFIG_URL,
                lpszAutoConfigUrl = pacUrl,
                fAutoLogonIfChallenged = true,
            };
            if (!WinHttpGetProxyForUrl(session, url, ref options, out var info))
            {
                Log.Write($"PAC {pacUrl}: WinHTTP error {Marshal.GetLastWin32Error()}");
                return null;
            }
            try
            {
                return info.dwAccessType == WINHTTP_ACCESS_TYPE_NAMED_PROXY && info.lpszProxy != IntPtr.Zero
                    ? Marshal.PtrToStringUni(info.lpszProxy)
                    : null;
            }
            finally
            {
                if (info.lpszProxy != IntPtr.Zero) GlobalFree(info.lpszProxy);
                if (info.lpszProxyBypass != IntPtr.Zero) GlobalFree(info.lpszProxyBypass);
            }
        }
        finally
        {
            WinHttpCloseHandle(session);
        }
    }
}
