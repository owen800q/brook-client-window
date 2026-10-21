using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BrookClient.Core;

/// <summary>An HTTP proxy that brook's server connection goes through, with optional Basic login.</summary>
public sealed record UpstreamProxyEndpoint(string Host, int Port, string Username = "", string Password = "")
{
    public override string ToString() => $"{Host}:{Port}";

    public bool HasLogin => Username.Length > 0;

    /// <summary>A host name or IP literal (no scheme, port, path or spaces).</summary>
    public static bool IsValidHost(string host) =>
        host.Length is > 0 and <= 253 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '[' or ']')
        && !host.Contains("://", StringComparison.Ordinal);

    /// <summary>The proxy for https:// traffic in a WinINet ProxyServer value: "host:port", or "http=a:1;https=b:2;socks=...".</summary>
    public static (string Host, int Port)? ParseWindowsProxyServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var entries = value.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries);
        string? plain = null, http = null, https = null;
        foreach (var e in entries)
        {
            var eq = e.IndexOf('=');
            if (eq < 0) { plain ??= e; continue; }
            var scheme = e[..eq].Trim().ToLowerInvariant();
            var target = e[(eq + 1)..].Trim();
            if (scheme == "https") https ??= target;
            else if (scheme == "http") http ??= target;
        }
        return ParseHostPort(https ?? plain ?? http, defaultPort: 80);
    }

    /// <summary>The first "PROXY host:port" of a PAC answer such as "PROXY a:8080; DIRECT". Null for DIRECT/SOCKS-only answers.</summary>
    public static (string Host, int Port)? ParsePacResult(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        foreach (var part in result.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = part.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0) continue;
            var kind = words[0].ToUpperInvariant();
            if (kind == "DIRECT") return null;
            if (kind is "PROXY" or "HTTP" or "HTTPS" && words.Length == 2) return ParseHostPort(words[1], defaultPort: 80);
        }
        return null;
    }

    /// <summary>WinHTTP returns the bare list form: "a:8080;b:3128" (no PROXY keyword).</summary>
    public static (string Host, int Port)? ParseWinHttpProxyList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return null;
        return ParseWindowsProxyServer(list.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
    }

    private static (string Host, int Port)? ParseHostPort(string? s, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        s = s.TrimEnd('/');
        if (BrookLink.ParseHostPort(s) is { } hp) return hp;
        return IsValidHost(s) && !s.Contains(':') ? (s, defaultPort) : null;
    }
}

public sealed class UpstreamProxyException(string message, bool authenticationFailed = false) : Exception(message)
{
    /// <summary>407: wrong or missing username/password. Retrying won't help.</summary>
    public bool AuthenticationFailed { get; } = authenticationFailed;
}

/// <summary>
/// A loopback TCP relay: every connection brook makes to it is carried to <c>target</c> through the upstream HTTP
/// proxy with <c>CONNECT target</c>. brook dials 127.0.0.1:<see cref="Port"/> instead of the server; the proxy resolves
/// the server's name, which also works on networks where only the proxy has DNS.
/// </summary>
public sealed class HttpConnectRelay : IDisposable
{
    private readonly UpstreamProxyEndpoint _proxy;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string>? _log;
    private int _activeConnections;

    public HttpConnectRelay(UpstreamProxyEndpoint proxy, string targetHost, int targetPort, Action<string>? log = null)
    {
        _proxy = proxy;
        _targetHost = targetHost;
        _targetPort = targetPort;
        _log = log;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>The last tunnel error, e.g. a 407 after the password changed on the proxy.</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    /// <summary>Opens one tunnel and closes it: checks the proxy is reachable, accepts the login and allows the target.</summary>
    public static async Task TestAsync(UpstreamProxyEndpoint proxy, string targetHost, int targetPort, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var client = await OpenTunnel(proxy, targetHost, targetPort, cts.Token);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient inbound;
            try { inbound = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            catch (SocketException e) { _log?.Invoke($"relay accept: {e.Message}"); continue; }
            _ = Serve(inbound);
        }
    }

    private async Task Serve(TcpClient inbound)
    {
        Interlocked.Increment(ref _activeConnections);
        try
        {
            using (inbound)
            {
                TcpClient outbound;
                try
                {
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    outbound = await OpenTunnel(_proxy, _targetHost, _targetPort, connectTimeout.Token);
                }
                catch (Exception e) when (e is UpstreamProxyException or IOException or SocketException or OperationCanceledException)
                {
                    LastError = e is OperationCanceledException ? $"proxy {_proxy} timed out" : e.Message;
                    _log?.Invoke($"relay: {LastError}");
                    return;
                }
                using (outbound)
                {
                    inbound.NoDelay = outbound.NoDelay = true;
                    var a = inbound.GetStream();
                    var b = outbound.GetStream();
                    var up = Pump(a, b, outbound);
                    var down = Pump(b, a, inbound);
                    await Task.WhenAny(up, down);
                    // Give the other direction a moment to flush its tail, then close both.
                    await Task.WhenAny(Task.WhenAll(up, down), Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ContinueWith(_ => { }));
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private async Task Pump(NetworkStream from, NetworkStream to, TcpClient toClient)
    {
        var buf = new byte[32 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buf, _cts.Token)) > 0) await to.WriteAsync(buf.AsMemory(0, n), _cts.Token);
            toClient.Client.Shutdown(SocketShutdown.Send); // pass the half-close on
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Either side went away; Serve closes both.
        }
    }

    internal static async Task<TcpClient> OpenTunnel(UpstreamProxyEndpoint proxy, string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            try { await client.ConnectAsync(proxy.Host, proxy.Port, ct); }
            catch (SocketException e) { throw new UpstreamProxyException($"Cannot reach proxy {proxy}: {e.Message}"); }

            var stream = client.GetStream();
            var authority = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
            var request = new StringBuilder()
                .Append($"CONNECT {authority} HTTP/1.1\r\n")
                .Append($"Host: {authority}\r\n")
                .Append("Proxy-Connection: Keep-Alive\r\n");
            if (proxy.HasLogin)
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password}"));
                request.Append($"Proxy-Authorization: Basic {token}\r\n");
            }
            request.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);

            var (status, reason) = await ReadResponseHead(stream, ct);
            switch (status)
            {
                case >= 200 and < 300:
                    return client;
                case 407:
                    throw new UpstreamProxyException(proxy.HasLogin
                        ? $"Proxy {proxy} rejected the username or password (407)"
                        : $"Proxy {proxy} requires a username and password (407)", authenticationFailed: true);
                default:
                    throw new UpstreamProxyException($"Proxy {proxy} refused to connect to {authority}: {status} {reason}".TrimEnd());
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Reads the status line and headers byte by byte, so no tunnel data is consumed.</summary>
    private static async Task<(int Status, string Reason)> ReadResponseHead(NetworkStream stream, CancellationToken ct)
    {
        var head = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new UpstreamProxyException("Proxy closed the connection without answering");
            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') break;
            if (n > 16 * 1024) throw new UpstreamProxyException("Proxy sent an oversized response");
        }
        var statusLine = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n")[0];
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out var status))
            throw new UpstreamProxyException($"Not an HTTP proxy (answered '{statusLine}')");
        return (status, parts.Length > 2 ? parts[2] : "");
    }

    public void Dispose()
    {
        _cts.Cancel(); // not disposed: tunnels still winding down read its token
        _listener.Stop();
    }
}
