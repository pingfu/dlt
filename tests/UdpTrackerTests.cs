using System.Buffers.Binary;
using Dlt.Trackers;

namespace Dlt.Tests;

public class UdpTrackerTests
{
    private const long ConnectionId = 0x1122334455667788L;

    private static IEnumerable<byte[]> HappyTracker(int action, byte[] request)
    {
        var tid = FakeUdpTracker.TransactionId(request);
        if (action == 0) yield return FakeUdpTracker.ConnectReply(tid, ConnectionId);
        if (action == 2) yield return FakeUdpTracker.ScrapeReply(tid, seeders: 9, completed: 4, leechers: 2);
    }

    [Fact]
    public async Task ConnectThenScrapeReportsCounts()
    {
        using var tracker = new FakeUdpTracker(HappyTracker);
        var p = new TrackerProbe(1, tracker.Url);
        using var checker = new TrackerChecker(TimeSpan.FromSeconds(5));

        await checker.RunAsync(p, Probes.Hash, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Found, p.Outcome);
        Assert.Equal((9L, 2L, 4L), (p.Seeders, p.Leechers, p.Completed));
        Assert.Equal("udp scrape", p.Method);
        Assert.Equal($"127.0.0.1:{tracker.Port}", p.Endpoint);
        Assert.Equal(ProbeStage.Done, p.Stage);

        // The scrape must carry the connection id from the handshake and the hash being asked about.
        var scrape = tracker.Received.Single(r => BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(8)) == 2);
        Assert.Equal(ConnectionId, BinaryPrimitives.ReadInt64BigEndian(scrape.AsSpan(0)));
        Assert.Equal(Probes.Hash, scrape[16..36]);
    }

    [Fact]
    public async Task IgnoresDatagramsWithTheWrongTransactionId()
    {
        using var tracker = new FakeUdpTracker((action, request) =>
        {
            var tid = FakeUdpTracker.TransactionId(request);
            var stray = FakeUdpTracker.ScrapeReply(tid + 1, 99, 99, 99);
            return [stray, .. HappyTracker(action, request)];
        });
        var p = new TrackerProbe(1, tracker.Url);
        using var checker = new TrackerChecker(TimeSpan.FromSeconds(5));

        await checker.RunAsync(p, Probes.Hash, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Found, p.Outcome);
        Assert.Equal(9L, p.Seeders);
    }

    [Fact]
    public async Task TrackerErrorActionBecomesErrorOutcome()
    {
        using var tracker = new FakeUdpTracker((action, request) =>
        {
            var tid = FakeUdpTracker.TransactionId(request);
            return action == 0
                ? [FakeUdpTracker.ConnectReply(tid, ConnectionId)]
                : [FakeUdpTracker.ErrorReply(tid, "unregistered torrent")];
        });
        var p = new TrackerProbe(1, tracker.Url);
        using var checker = new TrackerChecker(TimeSpan.FromSeconds(5));

        await checker.RunAsync(p, Probes.Hash, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Error, p.Outcome);
        Assert.Equal("tracker error: unregistered torrent", p.Detail);
    }

    [Fact]
    public async Task SilenceIsATimeout()
    {
        using var tracker = new FakeUdpTracker((_, _) => []);
        var p = new TrackerProbe(1, tracker.Url);
        using var checker = new TrackerChecker(TimeSpan.FromMilliseconds(150));

        await checker.RunAsync(p, Probes.Hash, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unreachable, p.Outcome);
        Assert.Equal("timeout", p.Detail);
        Assert.Equal(2, tracker.Received.Count); // two connect attempts, no scrape
    }

    [Fact]
    public async Task ScrapeSilenceAfterConnectIsReportedDistinctly()
    {
        using var tracker = new FakeUdpTracker((action, request) =>
            action == 0 ? [FakeUdpTracker.ConnectReply(FakeUdpTracker.TransactionId(request), ConnectionId)] : []);
        var p = new TrackerProbe(1, tracker.Url);
        using var checker = new TrackerChecker(TimeSpan.FromMilliseconds(150));

        await checker.RunAsync(p, Probes.Hash, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unreachable, p.Outcome);
        Assert.Equal("connected, but scrape timed out", p.Detail);
    }

    [Fact]
    public async Task UrlWithoutPortIsUnsupported()
    {
        var p = new TrackerProbe(1, "udp://tracker.example/announce");
        using var checker = new TrackerChecker(TimeSpan.FromSeconds(1));

        await checker.RunAsync(p, Probes.Hash, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unsupported, p.Outcome);
        Assert.Equal("udp tracker URL has no port", p.Detail);
    }
}
