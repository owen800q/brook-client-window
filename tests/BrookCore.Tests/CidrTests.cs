using BrookClient.Core;

namespace BrookCore.Tests;

public class CidrTests
{
    [Fact]
    public void NormalizesHostBits()
    {
        Assert.Equal("192.168.0.0/16", IPv4Cidr.Parse("192.168.1.7/16").ToString());
        Assert.Equal("10.0.0.0/8", IPv4Cidr.Parse("10.0.0.0/8").ToString());
    }

    [Fact]
    public void BareAddressIsSlash32()
    {
        Assert.Equal("192.168.0.128/32", IPv4Cidr.Parse("192.168.0.128").ToString());
        Assert.True(IPv4Cidr.Parse("192.168.0.128").IsHost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("300.1.1.1/8")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3/8")]
    [InlineData("1.2.3.4/8;ls")]
    [InlineData("1.2.3.4/-1")]
    [InlineData(" 1.2.3.4/8 x")]
    [InlineData("01.2.3.4/8")]
    [InlineData("1.2.3.4/")]
    [InlineData("::1/128")]
    [InlineData("１.2.3.4")] // full-width digit: char.IsDigit would accept it
    public void RejectsInvalid(string raw) => Assert.Throws<CidrException>(() => IPv4Cidr.Parse(raw));

    [Fact]
    public void ToleratesSurroundingWhitespace() =>
        Assert.Equal("172.16.0.0/12", IPv4Cidr.Parse("  172.16.0.0/12 ").ToString());

    [Fact]
    public void Containment()
    {
        var lan = IPv4Cidr.Parse("192.168.0.0/16");
        Assert.True(lan.Contains("192.168.5.5"));
        Assert.False(lan.Contains("192.169.0.1"));
        Assert.False(lan.Contains("garbage"));
        Assert.True(IPv4Cidr.Parse("0.0.0.0/0").Contains("8.8.8.8"));
        Assert.True(IPv4Cidr.Parse("224.0.0.0/4").Contains(IPv4Cidr.Parse("239.255.255.250")));
    }

    [Fact]
    public void IPv4Validation()
    {
        Assert.True(IPv4.IsValid("1.2.3.4"));
        Assert.True(IPv4.IsValid("255.255.255.255"));
        Assert.False(IPv4.IsValid("256.1.1.1"));
        Assert.False(IPv4.IsValid("1.2.3"));
        Assert.False(IPv4.IsValid("1.2.3.4 "));
        Assert.False(IPv4.IsValid("example.com"));
    }
}
