using BrookClient.Core;

namespace BrookCore.Tests;

public class ConnectionTests
{
    [Fact]
    public void BrookArgumentsPinAndServeBothProxies()
    {
        var link = new BrookLink("brook://server?password=hello&server=myvps.example.com%3A9999");
        var args = BrookCommand.Arguments(link, "9.9.9.9", new AppConfig { SocksPort = 2080, HttpPort = 2081 });
        Assert.Equal("connect", args[0]);
        Assert.Equal(["--socks5", "127.0.0.1:2080", "--http", "127.0.0.1:2081"], args.Skip(3));
        var pinned = new BrookLink(args[2]);
        Assert.Equal("9.9.9.9", pinned.Host);
        Assert.Equal("true", pinned.QueryValue("udpovertcp"));
    }

    [Fact]
    public void RetryBackoff()
    {
        Assert.Equal([4, 8, 16, 30, 30], Enumerable.Range(1, RetryPolicy.MaxRetries).Select(i => RetryPolicy.Delay(i).TotalSeconds));
        Assert.Equal(30, RetryPolicy.Delay(100).TotalSeconds);
    }

    [Fact]
    public void ImportsEveryLinkFromPastedText()
    {
        const string text = """
            My servers:
            brook://server?name=HK&password=x&server=1.2.3.4%3A1
              brook://wssserver?password=x&wssserver=wss%3A%2F%2Fexample.com%2Fws  junk
            brook://server?name=HK&password=x&server=1.2.3.4%3A1
            """;
        var profiles = LinkImport.Profiles(text, existingCount: 2);
        Assert.Equal(2, profiles.Count);
        Assert.Equal("HK", profiles[0].Name);
        Assert.Equal("Server 4", profiles[1].Name);
        Assert.StartsWith("brook://wssserver", profiles[1].Link);
        Assert.Empty(LinkImport.Profiles("hello", 0));
        Assert.Empty(LinkImport.Profiles(null, 0));
    }

    [Fact]
    public void AutostartCommand()
    {
        const string exe = @"C:\Users\me\AppData\Local\Programs\BrookClient\BrookClient.exe";
        var cmd = Autostart.Command(exe);
        Assert.Equal($"\"{exe}\" --autostart", cmd);
        Assert.True(Autostart.Matches(cmd, exe));
        Assert.True(Autostart.Matches(cmd, exe.ToUpperInvariant()));
        Assert.True(Autostart.Matches(@"C:\x\BrookClient.exe --autostart", @"C:\x\BrookClient.exe"));
        Assert.False(Autostart.Matches(cmd, @"D:\Other\BrookClient.exe"));
        Assert.False(Autostart.Matches(null, exe));
    }
}
