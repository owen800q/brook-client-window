using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using BrookClient.Core;

namespace BrookCore.Tests;

public class UpstreamProxyParsingTests
{
    [Theory]
    [InlineData("proxy.corp:8080", "proxy.corp", 8080)]
    [InlineData("http=web:80;https=secure:8443;socks=s:1080", "secure", 8443)]
    [InlineData("http=web:3128", "web", 3128)]
    [InlineData("ftp=f:21;proxy.corp:8080", "proxy.corp", 8080)]
    [InlineData("http://proxy.corp:8080/", "proxy.corp", 8080)]
    [InlineData("10.0.0.1:3128", "10.0.0.1", 3128)]
    [InlineData("proxy.corp", "proxy.corp", 80)]
    public void ParsesWindowsProxyServer(string value, string host, int port) =>
        Assert.Equal((host, port), UpstreamProxyEndpoint.ParseWindowsProxyServer(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("socks=s:1080")]
    public void NoHttpProxyInWindowsValue(string? value) => Assert.Null(UpstreamProxyEndpoint.ParseWindowsProxyServer(value));

    [Theory]
    [InlineData("PROXY a.corp:8080; DIRECT", "a.corp", 8080)]
    [InlineData("SOCKS s:1080; PROXY b:3128", "b", 3128)]
    [InlineData("HTTPS c:443", "c", 443)]
    public void ParsesPacResult(string result, string host, int port) =>
        Assert.Equal((host, port), UpstreamProxyEndpoint.ParsePacResult(result));

    [Theory]
    [InlineData("DIRECT")]
    [InlineData("DIRECT; PROXY a:1")]
    [InlineData("SOCKS s:1080")]
    [InlineData("")]
    public void PacDirectOrSocksMeansNoHttpProxy(string result) => Assert.Null(UpstreamProxyEndpoint.ParsePacResult(result));

    [Fact]
    public void ParsesWinHttpList() =>
        Assert.Equal(("a", 8080), UpstreamProxyEndpoint.ParseWinHttpProxyList("a:8080;b:3128"));

    [Theory]
    [InlineData("proxy.corp", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("", false)]
    [InlineData("http://proxy", false)]
    [InlineData("a b", false)]
    public void HostValidation(string host, bool ok) => Assert.Equal(ok, UpstreamProxyEndpoint.IsValidHost(host));
}

public class RelayLinkTests
{
    [Fact]
    public void ServerLinkDialsRelayAndCarriesUdpInTcp()
    {
        var link = new BrookLink("brook://server?password=hello&server=myvps.example.com%3A9999");
        var relayed = new BrookLink(link.ThroughRelay(40000));
        Assert.Equal("127.0.0.1", relayed.Host);
        Assert.Equal(40000, relayed.Port);
        Assert.Equal("true", relayed.QueryValue("udpovertcp")); // an HTTP proxy carries TCP only
        Assert.Equal("hello", relayed.QueryValue("password"));
    }

    [Fact]
    public void WssLinkKeepsServerNameForTls()
    {
        var link = new BrookLink("brook://wssserver?password=x&wssserver=wss%3A%2F%2Fexample.com%2Fws");
        var relayed = new BrookLink(link.ThroughRelay(40000));
        Assert.Equal("example.com", relayed.Host);
        Assert.Equal("wss://example.com:443/ws", relayed.QueryValue("wssserver"));
        Assert.Equal("127.0.0.1:40000", relayed.QueryValue("address"));
    }

    [Fact]
    public void WsLinkWithAddressIsRedirectedToRelay()
    {
        var link = new BrookLink("brook://wsserver?address=5.6.7.8%3A80&password=x&wsserver=ws%3A%2F%2Fexample.com%3A80%2Fws");
        Assert.Equal("127.0.0.1:40000", new BrookLink(link.ThroughRelay(40000)).QueryValue("address"));
    }

    [Fact]
    public void QuicCannotBeRelayed() =>
        Assert.Throws<BrookLinkException>(() =>
            new BrookLink("brook://quicserver?password=x&quicserver=quic%3A%2F%2Fexample.com%3A443").ThroughRelay(40000));

    [Fact]
    public void RelayArguments()
    {
        var link = new BrookLink("brook://server?password=hello&server=myvps.example.com%3A9999");
        var args = BrookCommandRelay.Arguments(link, 40000, new AppConfig());
        Assert.Equal("127.0.0.1:40000", new BrookLink(args[2]).QueryValue("server"));
        Assert.Equal(["--socks5", "127.0.0.1:1080", "--http", "127.0.0.1:1081"], args.Skip(3));
    }
}

public class UpstreamProxyConfigTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "brookclient-test-" + Guid.NewGuid(), "config.json");

    [Fact]
    public void OffByDefault()
    {
        var c = new AppConfig();
        Assert.Equal(UpstreamProxyMode.Off, c.UpstreamProxy);
        Assert.Equal(8080, c.UpstreamProxyPort);
    }

    // The password is stored only through the protector (DPAPI in the app), never as plain JSON.
    [Fact]
    public void PasswordIsNotWrittenInPlainText()
    {
        var store = new ConfigStore(TempPath(), new ReverseProtector());
        var c = new AppConfig
        {
            UpstreamProxy = UpstreamProxyMode.Manual,
            UpstreamProxyHost = "proxy.corp",
            UpstreamProxyPort = 3128,
            UpstreamProxyUsername = "alice",
            UpstreamProxyPassword = "s3cret-pw",
        };
        store.Save(c);
        var json = File.ReadAllText(store.Path);
        Assert.DoesNotContain("s3cret-pw", json);
        Assert.Contains("\"upstreamProxyPasswordData\": \"wp-terc3s\"", json);
        Assert.Contains("\"upstreamProxy\": \"Manual\"", json);
        var loaded = store.Load();
        Assert.Equal(c, loaded);
        Assert.Equal("s3cret-pw", loaded.UpstreamProxyPassword);
        Assert.Equal("s3cret-pw", c.Clone().UpstreamProxyPassword);
    }

    [Fact]
    public void UndecryptablePasswordLoadsEmpty()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"upstreamProxy":"System","upstreamProxyUsername":"bob","upstreamProxyPasswordData":"garbage"}""");
        var c = new ConfigStore(path).Load();
        Assert.Equal(UpstreamProxyMode.System, c.UpstreamProxy);
        Assert.Equal("bob", c.UpstreamProxyUsername);
        Assert.Equal("", c.UpstreamProxyPassword);
    }

    [Fact]
    public void EmptyPasswordIsNotStored()
    {
        var store = new ConfigStore(TempPath());
        store.Save(new AppConfig());
        Assert.DoesNotContain("upstreamProxyPasswordData\": \"", File.ReadAllText(store.Path));
    }

    [Fact]
    public void Validation()
    {
        var manual = new AppConfig { UpstreamProxy = UpstreamProxyMode.Manual, UpstreamProxyHost = "", UpstreamProxyPort = 0 };
        var errors = manual.ValidationErrors();
        Assert.Contains(errors, e => e.Contains("host name"));
        Assert.Contains(errors, e => e.Contains("Proxy port 0"));

        var colon = new AppConfig { UpstreamProxy = UpstreamProxyMode.System, UpstreamProxyUsername = "a:b" };
        Assert.Contains(colon.ValidationErrors(), e => e.Contains("':'"));

        var quic = new AppConfig
        {
            UpstreamProxy = UpstreamProxyMode.System,
            Profiles = [new Profile("Q", "brook://quicserver?password=x&quicserver=quic%3A%2F%2Fexample.com%3A443")],
            SelectedIndex = 0,
        };
        Assert.Contains(quic.ValidationErrors(), e => e.Contains("QUIC"));
        quic.UpstreamProxy = UpstreamProxyMode.Off;
        Assert.Empty(quic.ValidationErrors());

        // Off ignores the (unused) manual fields.
        Assert.Empty(new AppConfig { UpstreamProxyHost = "", UpstreamProxyPort = 0 }.ValidationErrors());
    }

    private sealed class ReverseProtector : ISecretProtector
    {
        public string Protect(string secret) => new(secret.Reverse().ToArray());
        public string? Unprotect(string data) => new(data.Reverse().ToArray());
    }
}

public class HttpConnectRelayTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CarriesTrafficThroughAuthenticatedProxy()
    {
        using var echo = new EchoServer();
        using var proxy = new TestConnectProxy("alice", "p@ss:word");
        using var relay = new HttpConnectRelay(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port, "alice", "p@ss:word"), "127.0.0.1", echo.Port);
        relay.Start();

        Assert.Equal("hello brook", await RoundTrip(relay.Port, "hello brook"));
        Assert.Equal("second connection", await RoundTrip(relay.Port, "second connection"));

        Assert.Equal(2, proxy.Tunnels.Count);
        Assert.All(proxy.Tunnels, t => Assert.Equal($"127.0.0.1:{echo.Port}", t));
        var head = proxy.Requests[0];
        Assert.StartsWith($"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n", head);
        Assert.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:p@ss:word")), head);
    }

    [Fact]
    public async Task LargeTransferIsIntact()
    {
        using var echo = new EchoServer();
        using var proxy = new TestConnectProxy();
        using var relay = new HttpConnectRelay(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port), "127.0.0.1", echo.Port);
        relay.Start();
        var payload = new string('x', 300_000) + "end";
        Assert.Equal(payload, await RoundTrip(relay.Port, payload));
        Assert.DoesNotContain("Proxy-Authorization", proxy.Requests[0]); // no login configured
    }

    [Fact]
    public async Task WrongPasswordIsReportedAsAuthFailure()
    {
        using var echo = new EchoServer();
        using var proxy = new TestConnectProxy("alice", "right");
        var e = await Assert.ThrowsAsync<UpstreamProxyException>(() =>
            HttpConnectRelay.TestAsync(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port, "alice", "wrong"), "127.0.0.1", echo.Port, Timeout));
        Assert.True(e.AuthenticationFailed);
        Assert.Contains("rejected the username or password", e.Message);

        var missing = await Assert.ThrowsAsync<UpstreamProxyException>(() =>
            HttpConnectRelay.TestAsync(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port), "127.0.0.1", echo.Port, Timeout));
        Assert.Contains("requires a username and password", missing.Message);

        // And the right one works.
        await HttpConnectRelay.TestAsync(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port, "alice", "right"), "127.0.0.1", echo.Port, Timeout);
    }

    [Fact]
    public async Task RelayDropsConnectionAndRecordsErrorWhenLoginFails()
    {
        using var echo = new EchoServer();
        using var proxy = new TestConnectProxy("alice", "right");
        using var relay = new HttpConnectRelay(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port, "alice", "wrong"), "127.0.0.1", echo.Port);
        relay.Start();
        using var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", relay.Port);
        var buf = new byte[16];
        Assert.Equal(0, await c.GetStream().ReadAsync(buf).AsTask().WaitAsync(Timeout)); // closed
        Assert.Contains("407", relay.LastError);
    }

    [Fact]
    public async Task ReportsUnreachableProxyAndRefusedTarget()
    {
        int deadPort;
        using (var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { l.Start(); deadPort = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        var unreachable = await Assert.ThrowsAsync<UpstreamProxyException>(() =>
            HttpConnectRelay.TestAsync(new UpstreamProxyEndpoint("127.0.0.1", deadPort), "example.com", 443, Timeout));
        Assert.Contains("Cannot reach proxy", unreachable.Message);
        Assert.False(unreachable.AuthenticationFailed);

        using var proxy = new TestConnectProxy();
        var refused = await Assert.ThrowsAsync<UpstreamProxyException>(() =>
            HttpConnectRelay.TestAsync(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port), "127.0.0.1", deadPort, Timeout));
        Assert.Contains("502", refused.Message);
    }

    [Fact]
    public async Task NonProxyIsDetected()
    {
        using var echo = new EchoServer(); // echoes our CONNECT back: not an HTTP answer
        var e = await Assert.ThrowsAsync<UpstreamProxyException>(() =>
            HttpConnectRelay.TestAsync(new UpstreamProxyEndpoint("127.0.0.1", echo.Port), "example.com", 443, Timeout));
        Assert.Contains("Not an HTTP proxy", e.Message);
    }

    /// <summary>
    /// The whole chain with the real brook binary, when BROOK_BIN points at one (CI's Windows tests cover it too):
    /// app → brook connect (HTTP proxy) → relay → authenticated CONNECT proxy → brook server → internet.
    /// </summary>
    [Fact]
    public async Task RealBrookThroughAuthenticatedProxy()
    {
        var brook = Environment.GetEnvironmentVariable("BROOK_BIN");
        if (string.IsNullOrEmpty(brook) || !File.Exists(brook)) return; // not configured on this machine

        var serverPort = FreePort();
        using var server = Process.Start(new ProcessStartInfo(brook, $"server --listen 127.0.0.1:{serverPort} --password pw")
            { RedirectStandardError = true, RedirectStandardOutput = true })!;
        try
        {
            await WaitListening(serverPort);
            using var proxy = new TestConnectProxy("alice", "secret");
            var link = new BrookLink($"brook://server?password=pw&server=127.0.0.1%3A{serverPort}");
            using var relay = new HttpConnectRelay(new UpstreamProxyEndpoint("127.0.0.1", proxy.Port, "alice", "secret"), link.DialHost, link.DialPort);
            relay.Start();
            var config = new AppConfig { SocksPort = FreePort(), HttpPort = FreePort() };
            var psi = new ProcessStartInfo(brook) { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in BrookCommandRelay.Arguments(link, relay.Port, config)) psi.ArgumentList.Add(a);
            using var client = Process.Start(psi)!;
            try
            {
                await WaitListening(config.HttpPort);
                using var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{config.HttpPort}"), UseProxy = true };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                var response = await http.GetAsync("https://www.example.com/");
                Assert.True(response.IsSuccessStatusCode, $"status {response.StatusCode}");
                Assert.Contains($"127.0.0.1:{serverPort}", proxy.Tunnels); // the server connection went through the proxy
            }
            finally { client.Kill(); }
        }
        finally { server.Kill(); }
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task WaitListening(int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return; }
            catch (SocketException) { await Task.Delay(100); }
        }
        throw new TimeoutException($"nothing listening on {port}");
    }

    private static async Task<string> RoundTrip(int port, string text)
    {
        using var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", port);
        var s = c.GetStream();
        var bytes = Encoding.UTF8.GetBytes(text);
        var writer = s.WriteAsync(bytes).AsTask();
        var received = new MemoryStream();
        var buf = new byte[8192];
        while (received.Length < bytes.Length)
        {
            var n = await s.ReadAsync(buf).AsTask().WaitAsync(Timeout);
            if (n == 0) break;
            received.Write(buf, 0, n);
        }
        await writer;
        return Encoding.UTF8.GetString(received.ToArray());
    }
}
