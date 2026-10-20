using MonoTorrent;
using MonoTorrent.Client;

namespace Dlt.Tests;

public class DownloadSettingsTests
{
    [Fact]
    public void DefaultIsDownloadOnly()
    {
        var engine = Download.BuildEngineSettings(seed: false, dht: true);
        var torrent = Download.BuildTorrentSettings(seed: false, dht: true);

        Assert.NotNull(engine.DhtEndPoint);
        Assert.Equal(0, engine.DhtEndPoint!.Port);   // OS-assigned UDP port
        Assert.False(engine.AllowLocalPeerDiscovery);
        Assert.False(engine.AllowPortForwarding);
        Assert.Empty(engine.ListenEndPoints);
        Assert.False(engine.AutoSaveLoadFastResume);
        Assert.False(engine.AutoSaveLoadMagnetLinkMetadata);
        Assert.False(engine.AutoSaveLoadDhtCache);

        Assert.True(torrent.AllowDht);
        Assert.Equal(0, torrent.UploadSlots);
        Assert.Equal(1, torrent.MaximumUploadRate);
    }

    [Fact]
    public void NoDhtLeavesTheEngineTrackerOnly()
    {
        var engine = Download.BuildEngineSettings(seed: false, dht: false);
        var torrent = Download.BuildTorrentSettings(seed: false, dht: false);

        Assert.Null(engine.DhtEndPoint);
        Assert.False(torrent.AllowDht);
        Assert.False(engine.AutoSaveLoadDhtCache);
    }

    [Fact]
    public void SeedModeOpensTheDoor()
    {
        var engine = Download.BuildEngineSettings(seed: true, dht: true);
        var torrent = Download.BuildTorrentSettings(seed: true, dht: true);

        Assert.True(engine.AllowPortForwarding);
        Assert.Equal(2, engine.ListenEndPoints.Count);
        Assert.All(engine.ListenEndPoints.Values, ep => Assert.Equal(0, ep.Port));

        Assert.True(torrent.UploadSlots > 0);
        Assert.Equal(0, torrent.MaximumUploadRate); // 0 is MonoTorrent's "unlimited"
        Assert.NotNull(engine.DhtEndPoint);
    }

    [Fact]
    public async Task EngineScratchDirectoryIsEmptyAndRemovedAfterRun()
    {
        // The README promises nothing is written outside the download directory. MonoTorrent creates its cache
        // directory regardless of the auto-save settings, so dlt removes it afterwards. This pins both halves:
        // the engine leaves only empty directories, and the cleanup takes them away.
        var root = Path.Combine(Path.GetTempPath(), "dlt-tests", Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        var save = Path.Combine(root, "save");
        try
        {
            // DHT on, as it is by default: the DHT engine must not leave a node cache behind either.
            var (engine, _) = Download.BuildEngine(seed: false, dht: true, cache);
            using (engine)
            {
                var link = new MagnetLink(InfoHashes.FromV1(new InfoHash(Probes.Hash)), "unit", ["http://127.0.0.1:1/announce"], null, 1234);
                var manager = await engine.AddAsync(link, save, Download.BuildTorrentSettings(seed: false, dht: true));
                await manager.StartAsync();
                await Task.Delay(500);
                await manager.StopAsync(TimeSpan.FromSeconds(2));
            }

            Assert.True(Directory.Exists(cache), "expected MonoTorrent to create its cache directory; if it no longer does, the cleanup can go");
            Assert.Empty(Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(save), "no metadata arrived, so the save directory must not appear");

            Download.RemoveCacheIfEmpty(cache);

            Assert.False(Directory.Exists(cache));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanupLeavesDirectoriesWithFilesAlone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dlt-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "metadata"));
            File.WriteAllText(Path.Combine(dir, "metadata", "keep.torrent"), "x");

            Download.RemoveCacheIfEmpty(dir);

            Assert.True(File.Exists(Path.Combine(dir, "metadata", "keep.torrent")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CleanupIgnoresAMissingDirectory()
        => Download.RemoveCacheIfEmpty(Path.Combine(Path.GetTempPath(), "dlt-tests", "does-not-exist-" + Guid.NewGuid().ToString("N")));
}
