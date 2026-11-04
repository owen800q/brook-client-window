using System.Text.Json;
using BrookClient.Core;

namespace BrookCore.Tests;

public class ConfigTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "brookclient-test-" + Guid.NewGuid(), "config.json");

    [Fact]
    public void DefaultsBypassPrivateNetworks()
    {
        var c = new AppConfig();
        foreach (var cidr in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4" })
            Assert.Contains(cidr, c.BypassCidrs);
        Assert.Equal(1080, c.SocksPort);
        Assert.Equal(1081, c.HttpPort);
        Assert.True(c.UdpOverTcp);
        Assert.True(c.SetSystemProxy);
        Assert.Empty(c.Profiles);
        Assert.Null(c.SelectedProfile);
        Assert.Empty(c.ValidationErrors());
    }

    [Fact]
    public void MissingFileLoadsDefaults() => Assert.Equal(new AppConfig(), new ConfigStore(TempPath()).Load());

    [Fact]
    public void RoundTrip()
    {
        var store = new ConfigStore(TempPath());
        var c = new AppConfig
        {
            Profiles = [new Profile("HK", "brook://server?password=hello&server=1.2.3.4%3A9999")],
            SelectedIndex = 0,
            BypassCidrs = ["192.168.0.0/16"],
            SetSystemProxy = false,
        };
        store.Save(c);
        Assert.Equal(c, store.Load());
        Assert.Equal("HK", store.Load().SelectedProfile?.Name);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path)); // links contain passwords
    }

    // A config.json copied from the macOS client loads; its macOS-only keys are ignored.
    [Fact]
    public void LoadsMacConfig()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "blockIPv6" : true,
              "bypassCIDRs" : [ "192.168.0.0/16" ],
              "connectOnLaunch" : true,
              "dns" : [ "8.8.8.8" ],
              "httpPort" : 2081,
              "mode" : "tun",
              "profiles" : [ { "link" : "brook://server?password=x&server=1.2.3.4%3A1", "name" : "HK" } ],
              "selectedIndex" : 0,
              "socksPort" : 2080,
              "udpOverTCP" : false
            }
            """);
        var c = new ConfigStore(path).Load();
        Assert.Equal("HK", c.SelectedProfile?.Name);
        Assert.Equal(["192.168.0.0/16"], c.BypassCidrs);
        Assert.Equal(2080, c.SocksPort);
        Assert.Equal(2081, c.HttpPort);
        Assert.True(c.ConnectOnLaunch);
        Assert.False(c.UdpOverTcp);
    }

    [Fact]
    public void PartialJsonFillsDefaults()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"socksPort":2000,"profiles":null}""");
        var c = new ConfigStore(path).Load();
        Assert.Equal(2000, c.SocksPort);
        Assert.Empty(c.Profiles);
        Assert.Equal(new AppConfig().BypassCidrs, c.BypassCidrs);
        Assert.True(c.UdpOverTcp);
    }

    [Fact]
    public void CorruptFileThrows()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{nope");
        Assert.ThrowsAny<JsonException>(() => new ConfigStore(path).Load());
    }

    [Fact]
    public void SelectedProfileOutOfRangeIsNil()
    {
        var c = new AppConfig { Profiles = [new Profile("a", "x")], SelectedIndex = 3 };
        Assert.Null(c.SelectedProfile);
        c.SelectedIndex = -1;
        Assert.Null(c.SelectedProfile);
    }

    [Fact]
    public void ValidationReportsProblems()
    {
        var c = new AppConfig
        {
            Profiles = [new Profile("bad", "nope")],
            SelectedIndex = 0,
            BypassCidrs = ["192.168.0.0/16", "*.corp", "bogus", "0.0.0.0/1"],
            SocksPort = 70000,
        };
        var problems = c.ValidationErrors();
        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("bad"));
        Assert.Contains(problems, p => p.Contains("bogus"));
        Assert.Contains(problems, p => p.Contains("too broad"));
        Assert.Contains(problems, p => p.Contains("70000"));
    }

    [Fact]
    public void SamePortsAreRejected() =>
        Assert.Contains(new AppConfig { HttpPort = 1080 }.ValidationErrors(), p => p.Contains("must differ"));

    [Fact]
    public void BrookPathDefaultsToBrookExeNextToTheApp()
    {
        var app = Path.Combine(Path.GetTempPath(), "app");
        var exe = Path.Combine(app, "brook.exe");
        Assert.Equal(exe, BrookBinary.Resolve("", app, p => p == exe));
        Assert.Equal(exe, BrookBinary.Resolve(null, app, p => p == exe));
        Assert.Null(BrookBinary.Resolve("", app, _ => false));
        Assert.Contains("brook.exe not found", BrookBinary.NotFound("", app));
    }

    [Fact]
    public void BrookPathCanBeRenamedOrAnywhere()
    {
        var app = Path.Combine(Path.GetTempPath(), "app");
        var renamed = Path.Combine(app, "core.exe");
        Assert.Equal(renamed, BrookBinary.Resolve(" core.exe ", app, p => p == renamed));
        // A chosen file is used as is: no falling back to brook.exe when it's missing.
        Assert.Null(BrookBinary.Resolve("core.exe", app, p => p == Path.Combine(app, "brook.exe")));
        Assert.Contains(renamed, BrookBinary.NotFound("core.exe", app));
        var elsewhere = Path.Combine(Path.GetTempPath(), "tools", "x.exe");
        Assert.Equal(elsewhere, BrookBinary.Resolve(elsewhere, app, p => p == elsewhere));
    }

    [Fact]
    public void BrookPathIsValidated()
    {
        Assert.Null(BrookBinary.Problem(""));
        Assert.Null(BrookBinary.Problem("core.exe"));
        Assert.Null(BrookBinary.Problem("CORE.EXE"));
        Assert.NotNull(BrookBinary.Problem("core"));
        Assert.NotNull(BrookBinary.Problem("co?re.exe"));
        Assert.Contains(new AppConfig { BrookPath = "core.txt" }.ValidationErrors(), e => e.Contains(".exe"));
    }

    [Fact]
    public void BrookPathRoundTrips()
    {
        var store = new ConfigStore(TempPath());
        var c = new AppConfig { BrookPath = "core.exe" };
        store.Save(c);
        Assert.Equal("core.exe", store.Load().BrookPath);
        Assert.NotEqual(new AppConfig(), store.Load());
    }

    [Fact]
    public void CloneIsDeep()
    {
        var c = new AppConfig { Profiles = [new Profile("a", "brook://x")] };
        var d = c.Clone();
        Assert.Equal(c, d);
        d.Profiles[0].Name = "b";
        Assert.Equal("a", c.Profiles[0].Name);
    }
}
