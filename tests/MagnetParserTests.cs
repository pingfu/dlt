namespace Dlt.Tests;

public class MagnetParserTests
{
    private const string Hex = "7acf8fb590b2060dd9c3146ef770169d593433b0";

    [Fact]
    public void ParsesHexInfoHash()
    {
        var m = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex.ToUpperInvariant()}&dn=x");
        Assert.Equal(Convert.FromHexString(Hex), m.InfoHashV1);
        Assert.Null(m.InfoHashV2);
        Assert.Equal(m.InfoHashV1, m.TrackerInfoHash);
        Assert.Equal("magnet", m.Kind);
    }

    [Fact]
    public void ParsesBase32InfoHash()
    {
        var m = MagnetParser.Parse("magnet:?xt=urn:btih:" + new string('A', 32));
        Assert.Equal(new byte[20], m.InfoHashV1);
    }

    [Fact]
    public void ParsesV2MultihashAndTruncatesForTrackers()
    {
        var digest = string.Concat(Enumerable.Repeat("ab", 32));
        var m = MagnetParser.Parse($"magnet:?xt=urn:btmh:1220{digest}");
        Assert.Null(m.InfoHashV1);
        Assert.Equal(Enumerable.Repeat((byte)0xAB, 32).ToArray(), m.InfoHashV2);
        Assert.Equal(20, m.TrackerInfoHash!.Length);
        Assert.Equal(m.InfoHashV2![..20], m.TrackerInfoHash);
    }

    [Fact]
    public void RejectsMalformedV2Multihash()
    {
        var m = MagnetParser.Parse("magnet:?xt=urn:btmh:1220abcd");
        Assert.Null(m.InfoHashV2);
        Assert.Contains(m.Warnings, w => w.Contains("not a sha-256 multihash"));
    }

    [Fact]
    public void WarnsOnBadV1Length()
    {
        var m = MagnetParser.Parse("magnet:?xt=urn:btih:abc");
        Assert.Null(m.InfoHashV1);
        Assert.Contains(m.Warnings, w => w.Contains("neither 40 hex nor 32 base32"));
    }

    [Fact]
    public void DecodesPercentEncodedWrapper()
    {
        var inner = $"magnet:?xt=urn:btih:{Hex}&dn=a";
        var m = MagnetParser.Parse(Uri.EscapeDataString(inner));
        Assert.Equal(Convert.FromHexString(Hex), m.InfoHashV1);
        Assert.Contains(m.Warnings, w => w.Contains("percent-encoded 1 time(s)"));
    }

    [Fact]
    public void TrimsSurroundingQuotes()
    {
        var m = MagnetParser.Parse($"\"magnet:?xt=urn:btih:{Hex}\"");
        Assert.NotNull(m.InfoHashV1);
    }

    [Fact]
    public void DisplayNameTreatsPlusAsSpace()
    {
        var m = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&dn=my+file%20name");
        Assert.Equal("my file name", m.DisplayName);
    }

    [Fact]
    public void TrackerUrlsKeepPlusLiteral()
    {
        var m = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&tr=http%3A%2F%2Ft%2Fa%2Bb");
        Assert.Equal("http://t/a+b", m.Trackers.Single());
    }

    [Fact]
    public void IgnoresDuplicateTrackersCaseInsensitively()
    {
        var m = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&tr=udp://a:1/x&tr=UDP://A:1/X&tr=udp://b:1/x");
        Assert.Equal(2, m.Trackers.Count);
        Assert.Contains(m.Warnings, w => w.StartsWith("duplicate tracker ignored"));
    }

    [Fact]
    public void NormalisesNumberedKeys()
    {
        var m = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&tr.1=udp://a:1&tr.2=udp://b:1&dn.1=n");
        Assert.Equal(2, m.Trackers.Count);
        Assert.Equal("n", m.DisplayName);
    }

    [Fact]
    public void ParsesExactLengthAndWarnsWhenInvalid()
    {
        Assert.Equal(123L, MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&xl=123").ExactLength);
        var bad = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&xl=lots");
        Assert.Null(bad.ExactLength);
        Assert.Contains(bad.Warnings, w => w.Contains("xl is not a valid length"));
    }

    [Fact]
    public void ParsesTheLongTail()
    {
        var m = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&ws=http://w/1&xs=http://x/1&as=http://a/1&kt=one+two&x.pe=1.2.3.4:5&so=0,2-4&foo=bar&xt=urn:sha1:ZZZ");
        Assert.Equal(["http://w/1"], m.WebSeeds);
        Assert.Equal(["http://x/1"], m.ExactSources);
        Assert.Equal(["http://a/1"], m.AcceptableSources);
        Assert.Equal(["one", "two"], m.Keywords);
        Assert.Equal(["1.2.3.4:5"], m.Peers);
        Assert.Equal(["0", "2-4"], m.SelectOnly);
        Assert.Equal(new KeyValuePair<string, string>("foo", "bar"), m.Unknown.Single());
        Assert.Equal(["urn:sha1:ZZZ"], m.OtherTopics);
    }

    [Fact]
    public void WarnsWhenNothingToProbe()
    {
        var m = MagnetParser.Parse("magnet:?dn=only-a-name");
        Assert.Null(m.TrackerInfoHash);
        Assert.Contains(m.Warnings, w => w.Contains("no exact topic"));
        Assert.Contains(m.Warnings, w => w.Contains("no trackers listed"));
    }

    [Fact]
    public void KeepsFirstOfDifferingHashes()
    {
        var other = "0000000000000000000000000000000000000000";
        var m = MagnetParser.Parse($"magnet:?xt=urn:btih:{Hex}&xt=urn:btih:{other}");
        Assert.Equal(Convert.FromHexString(Hex), m.InfoHashV1);
        Assert.Contains(m.Warnings, w => w.Contains("multiple differing urn:btih"));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("")]
    [InlineData("not even close")]
    public void RejectsNonMagnetInput(string input)
        => Assert.Throws<MagnetFormatException>(() => MagnetParser.Parse(input));
}
