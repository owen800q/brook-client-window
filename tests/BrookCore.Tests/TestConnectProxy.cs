using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BrookCore.Tests;

/// <summary>
/// A minimal HTTP CONNECT proxy for tests, like a corporate proxy that requires a Basic login.
/// It records every request so tests can check what the client sent. Also used by the Windows tests.
/// </summary>
public sealed class TestConnectProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _user;
    private readonly string? _password;

    public TestConnectProxy(string? user = null, string? password = null)
    {
        _user = user;
        _password = password;
        _listener.Start();
        _ = Accept();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Request heads received, oldest first.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>CONNECT targets that were tunnelled successfully.</summary>
    public List<string> Tunnels { get; } = [];

    private async Task Accept()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Serve(c);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var s = client.GetStream();
                var head = await ReadHead(s);
                lock (Requests) Requests.Add(head);
                var lines = head.Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 3 || first[0] != "CONNECT")
                {
                    await Reply(s, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\n\r\n");
                    return;
                }
                if (_user is not null)
                {
                    var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}"));
                    var auth = lines.FirstOrDefault(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                    if (auth?.Split(':', 2)[1].Trim() != expected)
                    {
                        await Reply(s, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\n\r\n");
                        return;
                    }
                }
                var target = first[1];
                var colon = target.LastIndexOf(':');
                using var upstream = new TcpClient();
                try { await upstream.ConnectAsync(target[..colon].Trim('[', ']'), int.Parse(target[(colon + 1)..])); }
                catch (SocketException)
                {
                    await Reply(s, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
                    return;
                }
                lock (Tunnels) Tunnels.Add(target);
                await Reply(s, "HTTP/1.1 200 Connection established\r\n\r\n");
                var u = upstream.GetStream();
                await Task.WhenAny(s.CopyToAsync(u, _cts.Token), u.CopyToAsync(s, _cts.Token));
            }
            catch (Exception)
            {
                // test proxy: drop the connection
            }
        }
    }

    private static async Task Reply(NetworkStream s, string text) => await s.WriteAsync(Encoding.ASCII.GetBytes(text));

    private static async Task<string> ReadHead(NetworkStream s)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (await s.ReadAsync(one) == 1)
        {
            buf.Add(one[0]);
            if (buf.Count >= 4 && buf[^4] == '\r' && buf[^3] == '\n' && buf[^2] == '\r' && buf[^1] == '\n') break;
        }
        return Encoding.ASCII.GetString(buf.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>Echoes whatever it receives; stands in for the Brook server.</summary>
public sealed class EchoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public EchoServer()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        try { await c.GetStream().CopyToAsync(c.GetStream(), _cts.Token); } catch (Exception) { }
                    }
                });
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
