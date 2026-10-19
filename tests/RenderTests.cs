using Dlt.Trackers;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Dlt.Tests;

public class RenderTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1_048_576, "1 MiB")]
    [InlineData(792_723_456, "756 MiB")]
    [InlineData(1L << 40, "1 TiB")]
    public void HumanSize(long bytes, string expected)
        => Assert.Equal(expected, Render.HumanSize(bytes));

    [Theory]
    [InlineData(0.4, "under a second")]
    [InlineData(19.6, "20s")]
    [InlineData(75, "1m 15s")]
    [InlineData(3661, "1h 01m")]
    public void HumanDuration(double seconds, string expected)
        => Assert.Equal(expected, Render.HumanDuration(TimeSpan.FromSeconds(seconds)));

    private static (TestConsole Console, List<TrackerProbe> Probes) Setup(params string[] lines)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        foreach (var line in lines) console.Input.PushTextWithEnter(line);
        AnsiConsole.Console = console;
        var probes = new List<TrackerProbe> { Probes.Found(1, 5), Probes.Found(2, 50), Probes.NotFound(3) };
        return (console, probes);
    }

    [Fact]
    public async Task EnterAcceptsTheSuggestedTracker()
    {
        var (_, probes) = Setup("");
        var chosen = await Render.ChooseTrackerAsync(probes, probes[1], CancellationToken.None);
        Assert.Same(probes[1], chosen);
    }

    [Fact]
    public async Task NumberPicksThatRow()
    {
        var (_, probes) = Setup("1");
        var chosen = await Render.ChooseTrackerAsync(probes, probes[1], CancellationToken.None);
        Assert.Same(probes[0], chosen);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("q")]
    [InlineData("0")]
    public async Task SkipAnswersReturnNull(string answer)
    {
        var (_, probes) = Setup(answer);
        Assert.Null(await Render.ChooseTrackerAsync(probes, probes[0], CancellationToken.None));
    }

    [Fact]
    public async Task OutOfRangeIsRejectedThenReprompted()
    {
        var (console, probes) = Setup("9", "2");
        var chosen = await Render.ChooseTrackerAsync(probes, probes[0], CancellationToken.None);
        Assert.Same(probes[1], chosen);
        Assert.Contains("enter a number between 1 and 3", console.Output);
    }

    [Fact]
    public async Task PickingATrackerThatDidNotReportWarns()
    {
        var (console, probes) = Setup("3");
        var chosen = await Render.ChooseTrackerAsync(probes, probes[0], CancellationToken.None);
        Assert.Same(probes[2], chosen);
        Assert.Contains("tracker #3 did not report this torrent", console.Output);
    }

    [Fact]
    public void MagnetInfoShowsTorrentSpecificRows()
    {
        var console = new TestConsole();
        AnsiConsole.Console = console;
        var m = new MagnetUri
        {
            Kind = "torrent", Original = "x", Decoded = "x", DisplayName = "a.txt",
            InfoHashV1 = Probes.Hash, ExactLength = 3, FileCount = 2, Trackers = ["udp://t:1/a"],
        };

        Render.MagnetInfo(m, listTrackers: false);

        Assert.Contains("torrent", console.Output);
        Assert.Contains("a.txt", console.Output);
        Assert.Contains("000102030405060708090a0b0c0d0e0f10111213", console.Output);
        Assert.Contains("Files", console.Output);
        Assert.Contains("probed below", console.Output);
    }

    [Fact]
    public void SummaryCountsOutcomes()
    {
        var console = new TestConsole();
        AnsiConsole.Console = console;
        var probes = new List<TrackerProbe> { Probes.Found(1, 1), Probes.NotFound(2), Probes.Unreachable(3), Probes.Unreachable(4) };

        Render.Summary(probes, cancelled: false);

        Assert.Contains("1 of 4 trackers know this torrent", console.Output);
        Assert.Contains("1 reachable without it", console.Output);
        Assert.Contains("2 unreachable", console.Output);
    }
}
