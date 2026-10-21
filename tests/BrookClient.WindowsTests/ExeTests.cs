using System.Diagnostics;
using BrookClient.Core;

namespace BrookClient.WindowsTests;

/// <summary>Launches the built BrookClient.exe itself, as a normal user, with its data in a temp folder.</summary>
[Collection("windows")]
public sealed class ExeTests
{
    private static string Exe
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BrookClient.exe");
            return File.Exists(path) ? path : throw new FileNotFoundException("BrookClient.exe not in the test output", path);
        }
    }

    private static string NewDataDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "brookclient-exe-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    private static Process Start(string dataDir, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        psi.Environment["BROOKCLIENT_DATA"] = dataDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    /// <summary>Starts, shows its window, refuses a second instance, and cleans up a crashed run's proxy settings.</summary>
    [Fact]
    public void StartsAsSingleInstanceAndRecoversFromCrash()
    {
        using var guard = new RegistryGuard();
        var data = NewDataDir();

        // Simulate a crashed run: our proxy is set and its backup file is still there.
        var crashed = new SystemProxy(new Platform.RegistryProxyStore(), Path.Combine(data, "proxy-backup.json"));
        crashed.Apply(new ProxyPlan(1081, AppConfig.DefaultBypass));
        Assert.True(guard.Current.Enable);

        using var app = Start(data);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                app.Refresh();
                Assert.False(app.HasExited, $"BrookClient.exe exited with {(app.HasExited ? app.ExitCode : 0)}; log: {Log(data)}");
                if (app.MainWindowHandle != IntPtr.Zero) break;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("no main window; log: " + Log(data));
                Thread.Sleep(200);
            }
            Assert.Equal("Brook Client", app.MainWindowTitle);
            Assert.Equal(guard.Original, guard.Current); // restored at startup
            Assert.False(File.Exists(Path.Combine(data, "proxy-backup.json")));

            // A second launch just hands over to the running one.
            using var second = Start(data);
            Assert.True(second.WaitForExit(15000), "second instance kept running");
            Assert.Equal(0, second.ExitCode);
        }
        finally
        {
            if (!app.HasExited) app.Kill();
        }
    }

    /// <summary>If the app is killed while connected, its watchdog restores the user's proxy settings.</summary>
    [Fact]
    public void WatchdogRestoresProxyWhenAppDies()
    {
        using var guard = new RegistryGuard();
        var data = NewDataDir();
        // Stand-in for the app process the watchdog guards.
        using var victim = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;

        var proxy = new SystemProxy(new Platform.RegistryProxyStore(), Path.Combine(data, "proxy-backup.json"));
        proxy.Apply(new ProxyPlan(1081, AppConfig.DefaultBypass));
        Assert.True(guard.Current.Enable);

        using var watchdog = Start(data, "--watchdog", victim.Id.ToString());
        Thread.Sleep(1000);
        Assert.False(watchdog.HasExited, "watchdog quit before the app died");
        Assert.True(guard.Current.Enable);

        victim.Kill();
        Assert.True(watchdog.WaitForExit(15000), "watchdog did not finish");
        Assert.Equal(0, watchdog.ExitCode);
        Assert.Equal(guard.Original, guard.Current);
        Assert.False(proxy.IsApplied);
    }

    private static string Log(string data)
    {
        var path = Path.Combine(data, "client.log");
        return File.Exists(path) ? File.ReadAllText(path) : "(no client.log)";
    }
}
