using System.Diagnostics;
using System.Net;
using System.Net.Http;
using BrookClient.Core;
using BrookClient.Platform;

namespace BrookClient.WindowsTests;

/// <summary>
/// The real thing, as a normal (non-admin) user: a local brook server, the engine running brook.exe, the real per-user
/// registry proxy, an HTTPS request through it, recovery after brook dies, and restoring the user's settings.
/// </summary>
[Collection("windows")]
public sealed class EndToEndTests(UiThread ui)
{
    private const int ServerPort = 29999;
    private const int SocksPort = 21080;
    private const int HttpPort = 21081;

    private static string BrookExe =>
        AppPaths.Brook ?? throw new FileNotFoundException("brook.exe missing from the test output; run scripts/fetch-brook.ps1 first");

    [Fact]
    public void ConnectsThroughBrookAndRestoresProxy()
    {
        using var guard = new RegistryGuard();
        using var server = StartBrookServer();
        var backup = Path.Combine(ui.DataDir, "e2e-backup.json");
        var config = new AppConfig
        {
            Profiles = [new Profile("Local", $"brook://server?password=pw&server=127.0.0.1%3A{ServerPort}")],
            SelectedIndex = 0,
            SocksPort = SocksPort,
            HttpPort = HttpPort,
        };

        ui.Invoke(() =>
        {
            ui.App.InitializeForTests(config, new SystemProxy(new RegistryProxyStore(), backup));
            ui.App.Engine.Connect(config);
        });
        WaitConnected();

        // Windows proxy points at brook, with the LAN bypassed.
        var applied = guard.Current;
        Assert.True(applied.Enable);
        Assert.Equal($"http=127.0.0.1:{HttpPort};https=127.0.0.1:{HttpPort}", applied.Server);
        Assert.Contains("192.168.*", applied.Override);
        Assert.EndsWith("<local>", applied.Override);
        Assert.Null(applied.AutoConfigUrl);
        Assert.True(File.Exists(backup));

        // Traffic flows through brook (HTTP proxy → brook connect → brook server → internet).
        Assert.Equal(HttpStatusCode.OK, GetVia(HttpPort));

        // brook dies → the engine notices and reconnects on its own.
        var firstPid = ui.Invoke(() => ui.App.Engine.BrookProcessId)!.Value;
        Process.GetProcessById(firstPid).Kill();
        ui.WaitFor(() => ui.App.Engine.State is EngineState.Connecting, TimeSpan.FromSeconds(10), Describe);
        WaitConnected();
        Assert.NotEqual(firstPid, ui.Invoke(() => ui.App.Engine.BrookProcessId));
        Assert.Equal(applied, guard.Current); // proxy stayed ours while reconnecting
        Assert.Equal(HttpStatusCode.OK, GetVia(HttpPort));

        // Disconnect puts the user's own settings back and stops brook.
        var pid = ui.Invoke(() => ui.App.Engine.BrookProcessId)!.Value;
        ui.Invoke(() => ui.App.Engine.Disconnect());
        ui.WaitFor(() => !File.Exists(backup), TimeSpan.FromSeconds(10), () => "backup not removed");
        Assert.Equal(guard.Original, guard.Current);
        Assert.True(WaitExited(pid), "brook still running after disconnect");
    }

    [Fact]
    public void LocalProxiesOnlyLeavesWindowsProxyAlone()
    {
        using var guard = new RegistryGuard();
        using var server = StartBrookServer();
        var config = new AppConfig
        {
            Profiles = [new Profile("Local", $"brook://server?password=pw&server=127.0.0.1%3A{ServerPort}")],
            SelectedIndex = 0,
            SocksPort = SocksPort,
            HttpPort = HttpPort,
            SetSystemProxy = false,
        };
        ui.Invoke(() =>
        {
            ui.App.InitializeForTests(config, new SystemProxy(new RegistryProxyStore(), Path.Combine(ui.DataDir, "e2e2-backup.json")));
            ui.App.Engine.Connect(config);
        });
        WaitConnected();
        Assert.Equal(guard.Original, guard.Current);
        Assert.Equal(HttpStatusCode.OK, GetVia(HttpPort));
        ui.Invoke(() => ui.App.Engine.ShutdownSync());
    }

    [Fact]
    public void ReportsBusyPort()
    {
        using var server = StartBrookServer();
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, HttpPort);
        blocker.Start();
        try
        {
            var config = new AppConfig
            {
                Profiles = [new Profile("Local", $"brook://server?password=pw&server=127.0.0.1%3A{ServerPort}")],
                SelectedIndex = 0,
                SocksPort = SocksPort,
                HttpPort = HttpPort,
            };
            ui.Invoke(() =>
            {
                ui.App.InitializeForTests(config, new SystemProxy(new FakeStore(), Path.Combine(ui.DataDir, "e2e3-backup.json")));
                ui.App.Engine.Connect(config);
            });
            ui.WaitFor(() => ui.App.Engine.State is EngineState.Failed, TimeSpan.FromSeconds(10), Describe);
            Assert.Contains($"Port {HttpPort}", ui.Invoke(Describe));
        }
        finally
        {
            blocker.Stop();
        }
    }

    private void WaitConnected() =>
        ui.WaitFor(() => ui.App.Engine.State is EngineState.Connected, TimeSpan.FromSeconds(40), Describe);

    private string Describe() => $"engine state: {ui.App.Engine.State}; brook log: {ReadLog()}";

    private static string ReadLog()
    {
        try
        {
            using var fs = new FileStream(Paths.BrookLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(fs).ReadToEnd();
        }
        catch (IOException) { return "(none)"; }
    }

    private static HttpStatusCode GetVia(int httpPort)
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"), UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        Exception? last = null;
        for (var i = 0; i < 3; i++)
        {
            try { return client.GetAsync("https://www.example.com/").GetAwaiter().GetResult().StatusCode; }
            catch (HttpRequestException e) { last = e; Thread.Sleep(1000); }
            catch (TaskCanceledException e) { last = e; }
        }
        throw new InvalidOperationException("request through brook failed", last);
    }

    private static bool WaitExited(int pid)
    {
        try { return Process.GetProcessById(pid).WaitForExit(5000); }
        catch (ArgumentException) { return true; }
    }

    private static ProcessHandle StartBrookServer()
    {
        var psi = new ProcessStartInfo(BrookExe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "server", "--listen", $"127.0.0.1:{ServerPort}", "--password", "pw" }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Net.AcceptsConnections(ServerPort))
        {
            if (p.HasExited || DateTime.UtcNow > deadline) throw new InvalidOperationException("brook server did not start");
            Thread.Sleep(100);
        }
        return new ProcessHandle(p);
    }

    private sealed class ProcessHandle(Process p) : IDisposable
    {
        public void Dispose()
        {
            try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { }
            p.Dispose();
        }
    }
}
