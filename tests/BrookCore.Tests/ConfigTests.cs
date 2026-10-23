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
    public void CloneIsDeep()
    {
        var c = new AppConfig { Profiles = [new Profile("a", "brook://x")] };
        var d = c.Clone();
        Assert.Equal(c, d);
        d.Profiles[0].Name = "b";
        Assert.Equal("a", c.Profiles[0].Name);
    }
}
