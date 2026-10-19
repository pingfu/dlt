using MonoTorrent.Client;

namespace Dlt.Tests;

public class MetadataPhaseTests
{
    private static MetadataSnapshot Snapshot(
        bool announced = true, bool ok = true, string? error = null,
        int trackerPeers = 0, int pex = 0, int failed = 0, ConnectionFailureReason? lastFailure = null,
        int known = 0, int open = 0, int ext = 0, TimeSpan? retry = null)
        => new(announced, ok, error, trackerPeers, pex, failed, lastFailure, known, open, ext, retry);

    [Fact]
    public void BeforeAnnounce()
        => Assert.Equal("[grey]announcing to tracker[/]", MetadataPhase.Describe(Snapshot(announced: false)));

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
    public void NoPeersAtAllNamesTheWait()
    {
        var text = MetadataPhase.Describe(Snapshot(retry: TimeSpan.FromMinutes(29.6)));
        Assert.Equal("[yellow]tracker returned no peers[/]  [grey]next announce in 30m[/]", text);
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
