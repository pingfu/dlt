using System.Net;

namespace Dlt.Tests;

public class DhtBootstrapTests
{
    [Fact]
    public void CompactNodeIsIdThenAddressThenBigEndianPort()
    {
        var node = DhtBootstrap.CompactNode(IPAddress.Parse("87.98.162.88"), 6881);

        Assert.Equal(26, node.Length);
        Assert.Equal(new byte[] { 87, 98, 162, 88 }, node[20..24]);
        Assert.Equal(new byte[] { 0x1A, 0xE1 }, node[24..]);   // 6881
    }

    [Fact]
    public void CompactNodeIdsAreRandom()
    {
        var a = DhtBootstrap.CompactNode(IPAddress.Loopback, 1);
        var b = DhtBootstrap.CompactNode(IPAddress.Loopback, 1);
        Assert.NotEqual(a[..20], b[..20]);
    }

    [Fact]
    public void CompactNodeRejectsIPv6()
        => Assert.Throws<ArgumentException>(() => DhtBootstrap.CompactNode(IPAddress.IPv6Loopback, 6881));

    [Fact]
    public async Task ResolveSkipsHostsItCannotReachWithinTheTimeout()
    {
        // A zero timeout cancels every lookup before it can complete, and that must surface as "no nodes", not an exception.
        var seed = await DhtBootstrap.ResolveAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Empty(seed.Nodes);
        Assert.Equal(0, seed.RoutersResolved);
        Assert.Equal(DhtBootstrap.Routers.Count, seed.RoutersTotal);
        Assert.Equal($"none of the {DhtBootstrap.Routers.Count} public routers resolved", seed.Describe());
    }

    [Fact]
    public void SeedDescribesHowManyRoutersAnswered()
        => Assert.Equal("4 of 5 public routers resolved", new DhtBootstrap.Seed([], 4, 5).Describe());
}

public class DhtQueryTimeoutTests
{
    [Fact]
    public void QueryTimeoutCanBeSetOnThisMonoTorrentVersion()
    {
        // Pins the reflection path: if MonoTorrent renames MessageLoop or Timeout, this fails loudly instead of the DHT
        // silently going back to 15-second queries.
        using var engine = new MonoTorrent.Dht.DhtEngine();
        Assert.True(DhtBootstrap.TrySetQueryTimeout(engine, TimeSpan.FromSeconds(2)));
    }
}
