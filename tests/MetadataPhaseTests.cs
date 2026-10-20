using MonoTorrent.Client;
using MonoTorrent.Dht;

namespace Dlt.Tests;

public class MetadataPhaseTests
{
    private static MetadataSnapshot Snapshot(
        bool hasTracker = true, bool announced = true, bool ok = true, string? error = null,
        int trackerPeers = 0, int pex = 0, int dhtPeers = 0, DhtState? dht = null, int nodes = 0,
        int failed = 0, ConnectionFailureReason? lastFailure = null,
        int known = 0, int open = 0, int ext = 0, TimeSpan? retry = null)
        => new(hasTracker, announced, ok, error, trackerPeers, pex, dhtPeers, dht, nodes, failed, lastFailure, known, open, ext, retry);

    [Fact]
    public void BeforeAnnounce()
        => Assert.Equal("[grey]announcing to tracker[/]", MetadataPhase.Describe(Snapshot(announced: false)));

    [Fact]
    public void BeforeAnnounceWithDhtBootstrapping()
        => Assert.Equal("[grey]announcing to tracker[/]  [grey]· dht bootstrapping, 12 nodes[/]",
            MetadataPhase.Describe(Snapshot(announced: false, dht: DhtState.Initialising, nodes: 12)));

    [Fact]
    public void AnnounceFailedShowsTrackerMessageEscaped()
    {
        var text = MetadataPhase.Describe(Snapshot(ok: false, error: "torrent [not] registered", retry: TimeSpan.FromSeconds(45)));
        Assert.StartsWith("[red]tracker: torrent [[not]] registered[/]", text);
        Assert.EndsWith("retrying in 45s[/]", text);
    }

    [Fact]
    public void AnnounceFailedWithoutMessage()
        => Assert.Contains("tracker: announce failed", MetadataPhase.Describe(Snapshot(ok: false)));

    [Fact]
    public void AnnounceFailedButDhtDeliveredPeersIsNotAnError()
    {
        // The tracker is a lost cause, the DHT is not: the status describes the peers in hand, not the dead tracker.
        var text = MetadataPhase.Describe(Snapshot(ok: false, error: "gone", dht: DhtState.Ready, nodes: 200, dhtPeers: 6));
        Assert.Equal("[grey]tracker gave 0 (+6 dht) peers · connecting[/]", text);
    }

    [Fact]
    public void NoPeersAtAllNamesTheWait()
    {
        var text = MetadataPhase.Describe(Snapshot(retry: TimeSpan.FromMinutes(29.6)));
        Assert.Equal("[yellow]tracker returned no peers[/]  [grey]next announce in 30m[/]", text);
    }

    [Fact]
    public void NoPeersFromTrackerMentionsDhtSearch()
    {
        var text = MetadataPhase.Describe(Snapshot(retry: TimeSpan.FromSeconds(30), dht: DhtState.Ready, nodes: 150));
        Assert.Equal("[yellow]tracker returned no peers[/]  [grey]next announce in 30s[/]  [grey]· dht searching, 150 nodes[/]", text);
    }

    [Fact]
    public void ConnectingWithFailures()
    {
        var text = MetadataPhase.Describe(Snapshot(trackerPeers: 12, failed: 3, lastFailure: ConnectionFailureReason.Unreachable));
        Assert.Equal("[grey]tracker gave 12 peers · connecting, 3 failed (unreachable)[/]", text);
    }

    [Fact]
    public void ConnectingWithoutFailuresAndPexPeers()
    {
        var text = MetadataPhase.Describe(Snapshot(trackerPeers: 12, pex: 4));
        Assert.Equal("[grey]tracker gave 12 (+4 pex) peers · connecting[/]", text);
    }

    [Fact]
    public void PexAndDhtExtrasAreBothListed()
    {
        var text = MetadataPhase.Describe(Snapshot(trackerPeers: 12, pex: 4, dhtPeers: 9, dht: DhtState.Ready));
        Assert.Equal("[grey]tracker gave 12 (+4 pex, +9 dht) peers · connecting[/]", text);
    }

    [Fact]
    public void KnownPeersButNoneFromTrackerStillCountsAsConnecting()
    {
        var text = MetadataPhase.Describe(Snapshot(trackerPeers: 0, known: 3));
        Assert.StartsWith("[grey]tracker gave 0 peers · connecting", text);
    }

    [Fact]
    public void ConnectedPeersRequestingMetadata()
    {
        var text = MetadataPhase.Describe(Snapshot(trackerPeers: 100, open: 7, ext: 3));
        Assert.Equal("[grey]tracker gave 100 peers · 7 connected, 3 speak extensions · requesting info dict[/]", text);
    }

    [Fact]
    public void ExtensionCountOmittedWhenAllPeersSpeakIt()
    {
        var text = MetadataPhase.Describe(Snapshot(trackerPeers: 5, open: 2, ext: 2));
        Assert.Equal("[grey]tracker gave 5 peers · 2 connected · requesting info dict[/]", text);
    }

    [Fact]
    public void DhtOnlyWhileBootstrapping()
        => Assert.Equal("[grey]bootstrapping dht · 3 nodes[/]",
            MetadataPhase.Describe(Snapshot(hasTracker: false, dht: DhtState.Initialising, nodes: 3)));

    [Fact]
    public void DhtOnlyReadyButEmptyHanded()
        => Assert.Equal("[yellow]dht found no peers yet[/]  [grey]180 nodes, still searching[/]",
            MetadataPhase.Describe(Snapshot(hasTracker: false, dht: DhtState.Ready, nodes: 180)));

    [Fact]
    public void DhtOnlyWithPeersNeverMentionsATracker()
    {
        var text = MetadataPhase.Describe(Snapshot(hasTracker: false, dht: DhtState.Ready, nodes: 180, dhtPeers: 8, pex: 2, open: 3, ext: 3));
        Assert.Equal("[grey]dht gave 8 (+2 pex) peers · 3 connected · requesting info dict[/]", text);
    }

    [Fact]
    public void DhtLineIsEmptyWhenDhtIsOff()
        => Assert.Equal(string.Empty, MetadataPhase.DescribeDht(Snapshot()));

    [Theory]
    [InlineData(DhtState.NotReady, 0, 0, "not ready · 0 nodes · 0 peers found")]
    [InlineData(DhtState.Initialising, 12, 0, "bootstrapping · 12 nodes · 0 peers found")]
    [InlineData(DhtState.Ready, 142, 1, "ready · 142 nodes · 1 peer found")]
    [InlineData(DhtState.Ready, 142, 23, "ready · 142 nodes · 23 peers found")]
    public void DhtLineNamesStateNodesAndPeers(DhtState state, int nodes, int peers, string expected)
        => Assert.Equal(expected, MetadataPhase.DescribeDht(Snapshot(dht: state, nodes: nodes, dhtPeers: peers)));

    [Theory]
    [InlineData(null, "?")]
    [InlineData(-5.0, "a moment")]
    [InlineData(0.0, "a moment")]
    [InlineData(45.0, "45s")]
    [InlineData(89.0, "89s")]
    [InlineData(90.0, "2m")]
    [InlineData(1800.0, "30m")]
    public void WaitFormatting(double? seconds, string expected)
        => Assert.Equal(expected, MetadataPhase.Wait(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value)));
}
