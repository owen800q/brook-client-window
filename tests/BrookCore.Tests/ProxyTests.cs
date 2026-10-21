using BrookClient.Core;

namespace BrookCore.Tests;

public class ProxyPlanTests
{
    [Theory]
    [InlineData("192.168.0.0/16", "192.168.*")]
    [InlineData("10.0.0.0/8", "10.*")]
    [InlineData("10.1.2.0/24", "10.1.2.*")]
    [InlineData("192.168.0.128", "192.168.0.128")]
    public void OctetAlignedCidrsBecomeOnePattern(string cidr, string expected) =>
        Assert.Equal([expected], ProxyPlan.Wildcards(IPv4Cidr.Parse(cidr)));

    [Fact]
    public void UnalignedCidrsExpandPerOctet()
    {
        var p12 = ProxyPlan.Wildcards(IPv4Cidr.Parse("172.16.0.0/12"));
        Assert.Equal(16, p12.Count);
        Assert.Equal("172.16.*", p12[0]);
        Assert.Equal("172.31.*", p12[^1]);

        var p4 = ProxyPlan.Wildcards(IPv4Cidr.Parse("224.0.0.0/4"));
        Assert.Equal(16, p4.Count);
        Assert.Equal("224.*", p4[0]);
        Assert.Equal("239.*", p4[^1]);

        Assert.Equal(["192.168.1.4", "192.168.1.5", "192.168.1.6", "192.168.1.7"], ProxyPlan.Wildcards(IPv4Cidr.Parse("192.168.1.5/30")));
        Assert.Equal(["10.0.4.*", "10.0.5.*"], ProxyPlan.Wildcards(IPv4Cidr.Parse("10.0.4.0/23")));
    }

    // Each pattern covers exactly the CIDR: every address in it matches, the neighbours just outside don't.
    [Theory]
    [InlineData("172.16.0.0/12", "172.16.0.1", "172.31.255.254", "172.15.255.255", "172.32.0.0")]
    [InlineData("169.254.0.0/16", "169.254.0.0", "169.254.255.255", "169.253.255.255", "169.255.0.0")]
    [InlineData("10.0.4.0/23", "10.0.4.0", "10.0.5.255", "10.0.3.255", "10.0.6.0")]
    public void PatternsMatchTheCidrExactly(string cidr, string firstIn, string lastIn, string before, string after)
    {
        var patterns = ProxyPlan.Wildcards(IPv4Cidr.Parse(cidr));
        bool Matches(string ip) => patterns.Any(p => p.EndsWith(".*") ? ip.StartsWith(p[..^1]) : ip == p);
        Assert.True(Matches(firstIn));
        Assert.True(Matches(lastIn));
        Assert.False(Matches(before));
        Assert.False(Matches(after));
    }

    [Fact]
    public void DefaultPlan()
    {
        var plan = new ProxyPlan(1081, AppConfig.DefaultBypass);
        Assert.Equal("http=127.0.0.1:1081;https=127.0.0.1:1081", plan.ProxyServer);
        Assert.StartsWith("localhost;127.*;[::1];*.local;10.*;172.16.*;", plan.ProxyOverride);
        Assert.Contains(";192.168.*;169.254.*;224.*;", plan.ProxyOverride);
        Assert.EndsWith(";239.*;<local>", plan.ProxyOverride);
        Assert.Equal(new ProxySettings(true, plan.ProxyServer, plan.ProxyOverride, null), plan.Settings);
    }

    [Fact]
    public void DeduplicatesPatterns()
    {
        var plan = new ProxyPlan(1081, ["192.168.0.0/16", "192.168.1.7/16"]);
        Assert.Single(plan.BypassPatterns, "192.168.*");
    }

    [Fact]
    public void RejectsBadInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyPlan(0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyPlan(70000, []));
        Assert.Throws<CidrException>(() => new ProxyPlan(1081, ["nope"]));
        Assert.Throws<ArgumentException>(() => new ProxyPlan(1081, ["0.0.0.0/1"]));
        // 224.0.0.0/4 (multicast) is the broadest legitimate default.
        Assert.NotNull(new ProxyPlan(1081, ["224.0.0.0/4"]));
    }
}

public class SystemProxyTests
{
    private sealed class FakeStore(ProxySettings initial) : IProxySettingsStore
    {
        public ProxySettings Current = initial;
        public int Writes;
        public ProxySettings Read() => Current;
        public void Write(ProxySettings s) { Current = s; Writes++; }
    }

    private static readonly ProxySettings UserSettings = new(false, "corp:8080", "*.corp", "http://corp/proxy.pac");

    private static string BackupPath() => Path.Combine(Path.GetTempPath(), "brookclient-test-" + Guid.NewGuid(), "proxy-backup.json");

    [Fact]
    public void ApplyThenRestoreGivesBackUserSettings()
    {
        var store = new FakeStore(UserSettings);
        var proxy = new SystemProxy(store, BackupPath());
        var plan = new ProxyPlan(1081, AppConfig.DefaultBypass);
        proxy.Apply(plan);
        Assert.True(proxy.IsApplied);
        Assert.Equal(plan.Settings, store.Current); // a PAC URL would take precedence, so it is cleared
        proxy.Restore();
        Assert.False(proxy.IsApplied);
        Assert.Equal(UserSettings, store.Current);
    }

    // Reconnecting re-applies; the backup must still hold the user's settings, not ours.
    [Fact]
    public void ReapplyKeepsOriginalBackup()
    {
        var store = new FakeStore(UserSettings);
        var proxy = new SystemProxy(store, BackupPath());
        proxy.Apply(new ProxyPlan(1081, []));
        proxy.Apply(new ProxyPlan(2081, []));
        Assert.Equal("http=127.0.0.1:2081;https=127.0.0.1:2081", store.Current.Server);
        proxy.Restore();
        Assert.Equal(UserSettings, store.Current);
    }

    // A crashed app leaves the backup behind; a new instance (or the watchdog) restores from it.
    [Fact]
    public void RestoreWorksFromAnotherInstance()
    {
        var path = BackupPath();
        var store = new FakeStore(UserSettings);
        new SystemProxy(store, path).Apply(new ProxyPlan(1081, []));
        new SystemProxy(store, path).Restore();
        Assert.Equal(UserSettings, store.Current);
    }

    // If the user picked another proxy while we were connected, leave their choice alone.
    [Fact]
    public void RestoreLeavesUserChangesAlone()
    {
        var store = new FakeStore(UserSettings);
        var proxy = new SystemProxy(store, BackupPath());
        proxy.Apply(new ProxyPlan(1081, []));
        var mine = new ProxySettings(true, "other:3128", null, null);
        store.Current = mine;
        proxy.Restore();
        Assert.Equal(mine, store.Current);
        Assert.False(proxy.IsApplied);
    }

    [Fact]
    public void RestoreWithoutBackupDoesNothing()
    {
        var store = new FakeStore(UserSettings);
        new SystemProxy(store, BackupPath()).Restore();
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void CorruptBackupIsDiscarded()
    {
        var path = BackupPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{garbage");
        var store = new FakeStore(UserSettings);
        var proxy = new SystemProxy(store, path);
        proxy.Restore();
        Assert.Equal(0, store.Writes);
        Assert.False(proxy.IsApplied);
    }
}
