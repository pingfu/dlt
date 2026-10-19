using Dlt.Trackers;

namespace Dlt.Tests;

public class TrackerCheckerTests
{
    [Theory]
    [InlineData("::not a url::", "not a valid absolute URL")]
    [InlineData("ftp://t.example/announce", "scheme 'ftp' is not a tracker protocol")]
    [InlineData("mailto:someone@example.com", "scheme 'mailto' is not a tracker protocol")]
    public async Task UnprobeableUrlsAreUnsupported(string url, string detailStart)
    {
        var p = new TrackerProbe(1, url);
        using var checker = new TrackerChecker(TimeSpan.FromSeconds(1));

        await checker.RunAsync(p, Probes.Hash, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unsupported, p.Outcome);
        Assert.StartsWith(detailStart, p.Detail);
        Assert.Equal(ProbeStage.Done, p.Stage);
        Assert.False(p.Clock.IsRunning);
    }

    [Theory]
    [InlineData(System.Net.WebSockets.WebSocketError.ConnectionClosedPrematurely, "closed before answering")]
    [InlineData(System.Net.WebSockets.WebSocketError.NotAWebSocket, "not a websocket endpoint")]
    [InlineData(System.Net.WebSockets.WebSocketError.UnsupportedVersion, "websocket version not supported")]
    [InlineData(System.Net.WebSockets.WebSocketError.HeaderError, "bad websocket handshake")]
    [InlineData(System.Net.WebSockets.WebSocketError.Success, "websocket error")]
    public void WebSocketErrorsAreShort(System.Net.WebSockets.WebSocketError code, string expected)
        => Assert.Equal(expected, TrackerChecker.DescribeWebSocket(new System.Net.WebSockets.WebSocketException(code)));

    [Fact]
    public void WebSocketWrappingASocketErrorUsesTheSocketWording()
    {
        var inner = new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
        var ex = new System.Net.WebSockets.WebSocketException(System.Net.WebSockets.WebSocketError.Faulted, inner);
        Assert.Equal("connection refused", TrackerChecker.DescribeWebSocket(ex));
    }

    [Fact]
    public void WebSocketWrappingAnHttpErrorUsesTheHttpWording()
    {
        var inner = new HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound) { };
        var ex = new System.Net.WebSockets.WebSocketException(System.Net.WebSockets.WebSocketError.Faulted, inner);
        Assert.DoesNotContain("remote party", TrackerChecker.DescribeWebSocket(ex));
    }

    [Fact]
    public async Task CancellationIsReportedAsCancelled()
    {
        using var tracker = new FakeUdpTracker((_, _) => []); // bound but silent, so the probe sits waiting
        var p = new TrackerProbe(1, tracker.Url);
        using var checker = new TrackerChecker(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(100);

        await checker.RunAsync(p, Probes.Hash, cts.Token);

        Assert.Equal(ProbeOutcome.Error, p.Outcome);
        Assert.Equal("cancelled", p.Detail);
    }
}
