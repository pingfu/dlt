using System.Security.Cryptography;

namespace Dlt.Tests;

public class TorrentInputTests
{
    private static readonly byte[] Info = Ben.Bytes(
        "d6:lengthi3e4:name5:a.txt12:piece lengthi16384e6:pieces20:", new byte[20], "e");

    private static readonly byte[] Torrent = Ben.Bytes(
        "d8:announce25:http://t.example/announce",
        "13:announce-listll25:http://t.example/announceel26:udp://u.example:1/announceee",
        "4:info", Info,
        "8:url-listl19:https://w.example/xe",
        "e");

    [Fact]
    public void ParsesTorrentBytesIntoTheMagnetShape()
    {
        var m = TorrentInput.FromBytes(Torrent, "unit.torrent");

        Assert.Equal("torrent", m.Kind);
        Assert.Equal("unit.torrent", m.Original);
        Assert.Equal("a.txt", m.DisplayName);
        Assert.Equal(3L, m.ExactLength);
        Assert.Equal(1, m.FileCount);
        Assert.Equal(SHA1.HashData(Info), m.InfoHashV1);
        Assert.Null(m.InfoHashV2);
        Assert.Equal(["https://w.example/x"], m.WebSeeds);
    }

    [Fact]
    public void ListsEachTrackerOnce()
    {
        var m = TorrentInput.FromBytes(Torrent, "unit.torrent");
        Assert.Contains("http://t.example/announce", m.Trackers);
        Assert.Contains("udp://u.example:1/announce", m.Trackers);
        Assert.Equal(m.Trackers.Distinct(StringComparer.OrdinalIgnoreCase).Count(), m.Trackers.Count);
    }

    [Fact]
    public void WarnsWhenTorrentHasNoTrackers()
    {
        var bare = Ben.Bytes("d4:info", Info, "e");
        var m = TorrentInput.FromBytes(bare, "x");
        Assert.Empty(m.Trackers);
        Assert.Contains(m.Warnings, w => w.Contains("no trackers"));
    }

    [Fact]
    public void RejectsBytesThatAreNotATorrent()
    {
        var ex = Assert.Throws<MagnetFormatException>(() => TorrentInput.FromBytes("not a torrent"u8, "x"));
        Assert.StartsWith("not a valid torrent file", ex.Message);
    }

    [Theory]
    [InlineData("http://host/file.torrent", true)]
    [InlineData("https://host/file.torrent", true)]
    [InlineData("HTTPS://host/x", true)]
    [InlineData("magnet:?xt=urn:btih:abc", false)]
    [InlineData(@"C:\files\x.torrent", false)]
    [InlineData("udp://host:1/announce", false)]
    public void RecognisesHttpUrls(string input, bool expected)
        => Assert.Equal(expected, TorrentInput.IsHttpUrl(input));
}
