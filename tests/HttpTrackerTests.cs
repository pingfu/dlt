using System.Net;
using Dlt.Trackers;

namespace Dlt.Tests;

public class HttpTrackerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static byte[] ScrapeBody(byte[] hash, long complete, long incomplete, long downloaded)
        => Ben.Bytes("d5:filesd", Ben.Str(hash), $"d8:completei{complete}e10:downloadedi{downloaded}e10:incompletei{incomplete}eeee");

    private static readonly byte[] AnnounceBody = Ben.Bytes("d8:completei2e10:incompletei1e8:intervali1800e5:peers6:", new byte[6], "e");

    [Fact]
    public async Task ScrapeHitReportsCounts()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/scrape"
            ? FakeHttp.Response(HttpStatusCode.OK, ScrapeBody(Probes.Hash, 5, 3, 7))
            : FakeHttp.Response(HttpStatusCode.InternalServerError, []));
        var p = new TrackerProbe(1, "http://t.example/announce");

        await HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Found, p.Outcome);
        Assert.Equal((5L, 3L, 7L), (p.Seeders, p.Leechers, p.Completed));
        Assert.Equal("scrape", p.Method);
        Assert.Equal("t.example", p.Endpoint);
        Assert.Equal($"http://t.example/scrape?info_hash={Probes.HashParam}", http.Requests.Single().ToString());
    }

    [Fact]
    public async Task ScrapeWithoutTheHashIsNotFound()
    {
        var other = Enumerable.Repeat((byte)0xEE, 20).ToArray();
        var http = new FakeHttp(_ => FakeHttp.Response(HttpStatusCode.OK, ScrapeBody(other, 1, 1, 1)));
        var p = new TrackerProbe(1, "http://t.example/announce");

        await HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.NotFound, p.Outcome);
        Assert.Contains("does not list this hash", p.Detail);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task ScrapeFailureFallsBackToPassiveAnnounce()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/scrape"
            ? FakeHttp.Response(HttpStatusCode.NotFound, [])
            : FakeHttp.Response(HttpStatusCode.OK, AnnounceBody));
        var p = new TrackerProbe(1, "http://t.example/announce");

        await HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Found, p.Outcome);
        Assert.Equal("announce", p.Method);
        Assert.Equal((2L, 1L), (p.Seeders, p.Leechers));
        Assert.Contains("scrape returned HTTP 404", p.Detail);
        Assert.Contains("1 peer(s) listed", p.Detail);

        var announce = http.Requests[1].ToString();
        Assert.StartsWith($"http://t.example/announce?info_hash={Probes.HashParam}&peer_id=", announce);
        Assert.Contains("&numwant=0&event=stopped", announce);
    }

    [Fact]
    public async Task NonBencodeScrapeBodyFallsBackToo()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/scrape"
            ? FakeHttp.Response(HttpStatusCode.OK, "<html>nope</html>"u8.ToArray())
            : FakeHttp.Response(HttpStatusCode.OK, AnnounceBody));
        var p = new TrackerProbe(1, "http://t.example/announce");

        await HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Found, p.Outcome);
        Assert.Contains("non-bencode", p.Detail);
    }

    [Fact]
    public async Task AnnounceFailureReasonBecomesTrackerError()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/scrape"
            ? FakeHttp.Response(HttpStatusCode.NotFound, [])
            : FakeHttp.Response(HttpStatusCode.OK, "d14:failure reason6:nopasse"u8.ToArray()));
        var p = new TrackerProbe(1, "http://t.example/announce");

        var ex = await Assert.ThrowsAsync<TrackerResponseException>(
            () => HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None));
        Assert.Contains("announce failure: nopass", ex.Message);
    }

    [Fact]
    public async Task AnnounceHtmlBodyIsDescribedNotDumped()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/scrape"
            ? FakeHttp.Response(HttpStatusCode.NotFound, [])
            : FakeHttp.Response(HttpStatusCode.OK, "<!doctype html><html>…</html>"u8.ToArray()));
        var p = new TrackerProbe(1, "http://t.example/announce");

        var ex = await Assert.ThrowsAsync<TrackerResponseException>(
            () => HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None));
        Assert.Contains("looks like HTML", ex.Message);
    }

    [Fact]
    public async Task UrlWithoutAnnounceSegmentSkipsScrape()
    {
        var http = new FakeHttp(_ => FakeHttp.Response(HttpStatusCode.OK, AnnounceBody));
        var p = new TrackerProbe(1, "http://t.example/tracker?x=1");

        await HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None);

        var only = Assert.Single(http.Requests).ToString();
        Assert.StartsWith($"http://t.example/tracker?x=1&info_hash={Probes.HashParam}", only);
        Assert.Contains("no scrape endpoint derivable", p.Detail);
    }

    [Fact]
    public async Task ExistingQueryIsPreservedOnScrape()
    {
        var http = new FakeHttp(_ => FakeHttp.Response(HttpStatusCode.OK, ScrapeBody(Probes.Hash, 1, 0, 0)));
        var p = new TrackerProbe(1, "https://t.example/announce?passkey=abc");

        await HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None);

        Assert.Equal($"https://t.example/scrape?passkey=abc&info_hash={Probes.HashParam}", http.Requests.Single().ToString());
    }

    [Fact]
    public async Task AnnounceSuffixIsCarriedToScrape()
    {
        var http = new FakeHttp(_ => FakeHttp.Response(HttpStatusCode.OK, ScrapeBody(Probes.Hash, 1, 0, 0)));
        var p = new TrackerProbe(1, "http://t.example/announce.php");

        await HttpTracker.ProbeAsync(http.Client, p, Probes.Hash, Timeout, CancellationToken.None);

        Assert.StartsWith("http://t.example/scrape.php?", http.Requests.Single().ToString());
    }
}
