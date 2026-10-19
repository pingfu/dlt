namespace Dlt.Tests;

public class TrackerChoiceTests
{
    [Fact]
    public void PicksMostSeeds()
    {
        var best = TrackerChoice.Best([Probes.Found(1, 5), Probes.Found(2, 50), Probes.Found(3, 7)]);
        Assert.Equal(2, best!.Index);
    }

    [Fact]
    public void BreaksSeedTiesByLeechers()
    {
        var best = TrackerChoice.Best([Probes.Found(1, 5, 1), Probes.Found(2, 5, 9)]);
        Assert.Equal(2, best!.Index);
    }

    [Fact]
    public void BreaksFullTiesByTableOrder()
    {
        var best = TrackerChoice.Best([Probes.Found(3, 5, 1), Probes.Found(1, 5, 1), Probes.Found(2, 5, 1)]);
        Assert.Equal(1, best!.Index);
    }

    [Fact]
    public void TreatsUnknownSeedsAsZero()
    {
        var best = TrackerChoice.Best([Probes.Found(1, null, 4), Probes.Found(2, 1)]);
        Assert.Equal(2, best!.Index);
    }

    [Fact]
    public void IgnoresTrackersThatDidNotReportTheTorrent()
    {
        var best = TrackerChoice.Best([Probes.Unreachable(1), Probes.NotFound(2), Probes.Found(3, 1)]);
        Assert.Equal(3, best!.Index);
    }

    [Fact]
    public void NullWhenNoneFound()
        => Assert.Null(TrackerChoice.Best([Probes.Unreachable(1), Probes.NotFound(2)]));

    [Fact]
    public void ExplainsTheChoice()
    {
        Assert.Equal("the only tracker that knows it", TrackerChoice.Reason(Probes.Found(1, 5), 1));
        Assert.Equal("most seeds: 1,234", TrackerChoice.Reason(Probes.Found(1, 1234), 3));
        Assert.Equal("most seeds: 0", TrackerChoice.Reason(Probes.Found(1, null, 2), 2));
    }
}
