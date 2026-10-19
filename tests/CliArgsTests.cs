namespace Dlt.Tests;

public class CliArgsTests
{
    [Fact]
    public void SingleInputNoFlags()
    {
        var a = CliArgs.Parse(["magnet:?xt=x"]);
        Assert.Equal("magnet:?xt=x", a.Input);
        Assert.False(a.Select);
        Assert.False(a.Seed);
        Assert.Null(a.OutDir);
        Assert.False(a.Help);
    }

    [Fact]
    public void FlagsInAnyOrder()
    {
        var a = CliArgs.Parse(["--seed", "file.torrent", "--select"]);
        Assert.Equal("file.torrent", a.Input);
        Assert.True(a.Select);
        Assert.True(a.Seed);
        Assert.False(a.Help);
    }

    [Theory]
    [InlineData("--out", "downloads")]
    [InlineData("-o", "downloads")]
    public void OutTakesTheNextArgument(string flag, string dir)
    {
        var a = CliArgs.Parse([flag, dir, "x.torrent"]);
        Assert.Equal(dir, a.OutDir);
        Assert.Equal("x.torrent", a.Input);
        Assert.False(a.Help);
    }

    [Fact]
    public void OutAcceptsEqualsForm()
    {
        var a = CliArgs.Parse(["x.torrent", @"--out=C:\downloads\iso"]);
        Assert.Equal(@"C:\downloads\iso", a.OutDir);
        Assert.False(a.Help);
    }

    [Fact]
    public void OutTrimsQuotes()
        => Assert.Equal(@"C:\my dir", CliArgs.Parse(["--out", "\"C:\\my dir\"", "x"]).OutDir);

    [Fact]
    public void OutValueMayLookLikeAFlagTarget()
    {
        // The value after --out is taken verbatim, even if it starts with a dash.
        var a = CliArgs.Parse(["--out", "-weird", "x"]);
        Assert.Equal("-weird", a.OutDir);
        Assert.False(a.Help);
    }

    [Fact]
    public void TrimsQuotesAroundInput()
        => Assert.Equal("magnet:?x", CliArgs.Parse(["\"magnet:?x\""]).Input);

    [Theory]
    [InlineData("")]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("a b")]
    [InlineData("--bogus a")]
    [InlineData("--seed")]
    [InlineData("x --out")]
    [InlineData("x --out a --out b")]
    [InlineData("x --out=a -o b")]
    public void HelpForMissingOrBadArguments(string commandLine)
        => Assert.True(CliArgs.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Help);
}
