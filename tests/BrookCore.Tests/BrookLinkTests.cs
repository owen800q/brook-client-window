using BrookClient.Core;

namespace BrookCore.Tests;

public class BrookLinkTests
{
    [Fact]
    public void ParsesPlainServerLink()
    {
        var link = new BrookLink("brook://server?password=hello&server=1.2.3.4%3A9999");
        Assert.Equal(BrookLinkKind.Server, link.Kind);
        Assert.Equal("1.2.3.4", link.Host);
        Assert.Equal(9999, link.Port);
        Assert.Equal("1.2.3.4", link.DialHost);
        Assert.Null(link.Name);
    }

    [Fact]
    public void ParsesWssLinkWithAddressOverride()
    {
        var link = new BrookLink("brook://wssserver?address=5.6.7.8%3A443&password=hello&wssserver=wss%3A%2F%2Fexample.com%3A443%2Fws");
        Assert.Equal(BrookLinkKind.WssServer, link.Kind);
        Assert.Equal("example.com", link.Host);
        Assert.Equal(443, link.Port);
        // The address parameter is what brook actually dials.
        Assert.Equal("5.6.7.8", link.DialHost);
    }

    [Fact]
    public void ParsesWsLinkWithName()
    {
        var link = new BrookLink("brook://wsserver?name=Home+HK&password=x&wsserver=ws%3A%2F%2Fexample.com%3A80%2Fws");
        Assert.Equal(BrookLinkKind.WsServer, link.Kind);
        Assert.Equal("Home HK", link.Name);
        Assert.Equal("example.com", link.Host);
        Assert.Equal(80, link.Port);
    }

    [Fact]
    public void WssDefaultsTo443WhenPortMissing() =>
        Assert.Equal(443, new BrookLink("brook://wssserver?password=x&wssserver=wss%3A%2F%2Fexample.com%2Fws").Port);

    [Fact]
    public void ParsesQuicAndSocks5()
    {
        var quic = new BrookLink("brook://quicserver?password=x&quicserver=quic%3A%2F%2Fexample.com%3A443");
        Assert.Equal(BrookLinkKind.QuicServer, quic.Kind);
        Assert.Equal("example.com", quic.Host);
        var socks = new BrookLink("brook://socks5?password=p&socks5=socks5%3A%2F%2F1.2.3.4%3A1080&username=u");
        Assert.Equal(BrookLinkKind.Socks5, socks.Kind);
        Assert.Equal("1.2.3.4", socks.Host);
        Assert.Equal(1080, socks.Port);
    }

    [Fact]
    public void ParsesBracketedIPv6Server()
    {
        var link = new BrookLink("brook://server?password=x&server=%5B2001%3Adb8%3A%3A1%5D%3A9999");
        Assert.Equal("2001:db8::1", link.Host);
        Assert.Equal(9999, link.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://server?server=1.2.3.4%3A1")]
    [InlineData("brook://server?password=x")] // missing server param
    [InlineData("brook://unknown?unknown=1.2.3.4%3A1")]
    [InlineData("brook://server?server=1.2.3.4")] // missing port
    [InlineData("brook://server?server=1.2.3.4%3A99999")] // bad port
    [InlineData("brook://wssserver?wssserver=notaurl")]
    [InlineData("brook://wssserver?wssserver=ws%3A%2F%2Fexample.com")] // wrong scheme for the kind
    [InlineData("brook://server?server=1.2.3.4%3A1%ZZ")] // malformed escape
    public void RejectsInvalidLinks(string raw)
    {
        Assert.Throws<BrookLinkException>(() => new BrookLink(raw));
        Assert.False(BrookLink.TryParse(raw, out _));
    }

    [Fact]
    public void TrimsWhitespace() =>
        Assert.Equal("1.2.3.4", new BrookLink("  brook://server?password=hello&server=1.2.3.4%3A9999\n").Host);

    // Pinning makes brook dial exactly the resolved IP, so a DNS change (CDN rotation) can't move the server connection.
    [Fact]
    public void PinningServerLinkRewritesServerParam()
    {
        var link = new BrookLink("brook://server?password=hello&server=myvps.example.com%3A9999");
        var pinned = new BrookLink(link.Pinned("9.9.9.9"));
        Assert.Equal(BrookLinkKind.Server, pinned.Kind);
        Assert.Equal("9.9.9.9", pinned.Host);
        Assert.Equal(9999, pinned.Port);
        Assert.Equal("hello", pinned.QueryValue("password"));
    }

    [Fact]
    public void PinningWssLinkSetsAddressAndKeepsHostForTls()
    {
        var link = new BrookLink("brook://wssserver?password=hello&wssserver=wss%3A%2F%2Fexample.com%3A8443%2Fws");
        var pinned = new BrookLink(link.Pinned("9.9.9.9"));
        Assert.Equal("example.com", pinned.Host);
        Assert.Equal("9.9.9.9", pinned.DialHost);
        Assert.Equal("9.9.9.9:8443", pinned.QueryValue("address"));
    }

    [Fact]
    public void PinningIsNoOpWhenAlreadyAnIP()
    {
        const string raw = "brook://server?password=hello&server=1.2.3.4%3A9999";
        Assert.Equal(raw, new BrookLink(raw).Pinned("1.2.3.4"));
    }

    [Fact]
    public void DialHostIsIPDetection()
    {
        Assert.True(new BrookLink("brook://server?server=1.2.3.4%3A1").DialHostIsIPv4);
        Assert.False(new BrookLink("brook://server?server=a.example%3A1").DialHostIsIPv4);
    }

    // Encoding matches Go's url.Values.Encode: sorted keys, '+' for spaces, %XX for the rest.
    [Fact]
    public void EncodesQueryLikeGo()
    {
        var link = new BrookLink("brook://server?server=a.example%3A1&password=p%26w+d&name=Home+%E9%A6%99%E6%B8%AF");
        var pinned = link.Pinned("9.9.9.9");
        Assert.Equal("brook://server?name=Home+%E9%A6%99%E6%B8%AF&password=p%26w+d&server=9.9.9.9%3A1", pinned);
        Assert.Equal("Home 香港", new BrookLink(pinned).Name);
    }
}

/// <summary>Defects found by review of the macOS implementation, kept for the port.</summary>
public class LinkRegressionTests
{
    // brook dials the address param's port, not the URL's.
    [Fact]
    public void PinningKeepsAddressPort()
    {
        var link = new BrookLink("brook://wssserver?address=cdn.example.net%3A2053&password=x&wssserver=wss%3A%2F%2Fexample.com%3A8443%2Fws");
        var pinned = new BrookLink(link.Pinned("9.9.9.9"));
        Assert.Equal("9.9.9.9:2053", pinned.QueryValue("address"));
        Assert.Equal(2053, link.DialPort);
    }

    // `brook connect` rejects URLs without an explicit port ("missing port in address").
    [Fact]
    public void PinningAddsImplicitPortToUrl()
    {
        var link = new BrookLink("brook://wssserver?password=x&wssserver=wss%3A%2F%2Fexample.com%2Fws");
        var pinned = new BrookLink(link.Pinned("9.9.9.9"));
        Assert.Equal("wss://example.com:443/ws", pinned.QueryValue("wssserver"));
        Assert.Equal("9.9.9.9:443", pinned.QueryValue("address"));
    }

    [Fact]
    public void ImplicitPortAddedEvenWhenHostIsAlreadyIP()
    {
        var link = new BrookLink("brook://wsserver?password=x&wsserver=ws%3A%2F%2F1.2.3.4%2Fws");
        Assert.Equal("ws://1.2.3.4:80/ws", new BrookLink(link.Pinned("1.2.3.4")).QueryValue("wsserver"));
    }

    [Fact]
    public void ImplicitPortAddedToUrlWithoutPath()
    {
        var link = new BrookLink("brook://quicserver?password=x&quicserver=quic%3A%2F%2Fexample.com");
        Assert.Equal("quic://example.com:443", new BrookLink(link.Pinned("9.9.9.9")).QueryValue("quicserver"));
    }

    // brook connect can't use a socks5 link ("create socks5 from a socks5").
    [Fact]
    public void Socks5ProfilesAreReportedUnsupported()
    {
        var c = new AppConfig { Profiles = [new Profile("s5", "brook://socks5?password=p&socks5=socks5%3A%2F%2F1.2.3.4%3A1080&username=u")] };
        Assert.Contains(c.ValidationErrors(), e => e.Contains("socks5"));
    }
}

/// <summary>Live finding on macOS: the server passes TCP but drops UDP, so DNS died. brook's UDP-over-TCP fixes it.</summary>
public class UdpOverTcpTests
{
    [Fact]
    public void ServerLinkGetsUdpOverTcp()
    {
        var link = new BrookLink("brook://server?password=hello&server=myvps.example.com%3A9999");
        var output = new BrookLink(link.Pinned("9.9.9.9", udpOverTcp: true));
        Assert.Equal("true", output.QueryValue("udpovertcp"));
        Assert.Equal("9.9.9.9", output.Host);
        Assert.Equal("hello", output.QueryValue("password"));
    }

    [Fact]
    public void AppliedEvenWhenServerIsAlreadyAnIP()
    {
        var link = new BrookLink("brook://server?password=hello&server=1.2.3.4%3A9999");
        Assert.Equal("true", new BrookLink(link.Pinned("1.2.3.4", udpOverTcp: true)).QueryValue("udpovertcp"));
    }

    [Fact]
    public void NotDuplicatedWhenAlreadyPresent()
    {
        const string raw = "brook://server?password=hello&server=1.2.3.4%3A9999&udpovertcp=true";
        var output = new BrookLink(raw).Pinned("1.2.3.4", udpOverTcp: true);
        Assert.Equal(2, output.Split("udpovertcp").Length);
    }

    [Fact]
    public void QuicUsesUdpOverStream()
    {
        var link = new BrookLink("brook://quicserver?password=x&quicserver=quic%3A%2F%2Fexample.com%3A443");
        var output = new BrookLink(link.Pinned("9.9.9.9", udpOverTcp: true));
        Assert.Equal("true", output.QueryValue("udpoverstream"));
        Assert.Null(output.QueryValue("udpovertcp"));
    }

    // ws/wss already carry UDP inside the websocket (TCP) stream.
    [Fact]
    public void WebsocketLinksUnchanged()
    {
        var link = new BrookLink("brook://wssserver?password=x&wssserver=wss%3A%2F%2F1.2.3.4%3A443%2Fws");
        Assert.Equal(link.Raw, link.Pinned("1.2.3.4", udpOverTcp: true));
    }

    [Fact]
    public void OffLeavesLinkAlone()
    {
        const string raw = "brook://server?password=hello&server=1.2.3.4%3A9999";
        Assert.Equal(raw, new BrookLink(raw).Pinned("1.2.3.4", udpOverTcp: false));
    }
}
