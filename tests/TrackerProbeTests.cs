using Dlt.Trackers;

namespace Dlt.Tests;

public class TrackerProbeTests
{
    [Fact]
    public void CountsAboveZeroMeanFound()
    {
        var p = new TrackerProbe(1, "udp://t:1/a");
        p.ReportCounts(null, null, 5, "scrape");
        Assert.Equal(ProbeOutcome.Found, p.Outcome);
        Assert.Equal(5, p.Completed);
        Assert.Equal("scrape", p.Method);
        Assert.Null(p.Detail);
    }

    [Fact]
    public void AllZeroMeansNotFoundWithExplanation()
    {
        var p = new TrackerProbe(1, "udp://t:1/a");
        p.ReportCounts(0, 0, 0, "scrape");
        Assert.Equal(ProbeOutcome.NotFound, p.Outcome);
        Assert.Equal("tracker answered but has no peers for this hash", p.Detail);
    }

    [Fact]
    public void NoteOverridesDefaultDetail()
    {
        var p = new TrackerProbe(1, "http://t/announce");
        p.ReportCounts(0, 0, null, "announce", "custom");
        Assert.Equal("custom", p.Detail);
    }

    [Fact]
    public void FinishRecordsOutcomeAndDetail()
    {
        var p = new TrackerProbe(1, "udp://t:1/a");
        p.Finish(ProbeOutcome.Unreachable, "dns: host not found");
        Assert.Equal(ProbeOutcome.Unreachable, p.Outcome);
        Assert.Equal("dns: host not found", p.Detail);
    }

    [Fact]
    public void InvalidUrlHasNoUriAndUnknownScheme()
    {
        var p = new TrackerProbe(1, "::not a url::");
        Assert.Null(p.Uri);
        Assert.Equal("?", p.Scheme);
    }

    [Fact]
    public void SchemeIsLowerCased()
        => Assert.Equal("udp", new TrackerProbe(1, "UDP://T:1/a").Scheme);
}
