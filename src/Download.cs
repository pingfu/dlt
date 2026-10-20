using System.Diagnostics;
using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Dht;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dlt;

/// <summary>Downloads the torrent behind a magnet with MonoTorrent, discovering peers through the one chosen tracker and, unless switched off, the DHT.</summary>
internal static class Download
{
    /// <summary>Where MonoTorrent insists on having a scratch directory. Nothing is ever written into it; see <see cref="RemoveCacheIfEmpty"/>.</summary>
    internal static readonly string DefaultCacheDirectory = Path.Combine(Path.GetTempPath(), "dlt");

    /// <param name="trackerUrl">The tracker to announce to, or null for a DHT-only download (no tracker knew the torrent, or none was listed).</param>
    /// <param name="seed">Lift the upload restriction, accept incoming peers, and keep running as a seed once complete.</param>
    /// <param name="dht">Find peers through the DHT as well. Bootstraps from scratch every run: nothing is cached.</param>
    public static async Task<bool> RunAsync(MagnetUri magnet, string? trackerUrl, string saveDirectory, bool seed, bool dht, CancellationToken ct)
    {
        try { return await RunCoreAsync(magnet, trackerUrl, saveDirectory, seed, dht, ct); }
        finally { RemoveCacheIfEmpty(DefaultCacheDirectory); }
    }

    private static async Task<bool> RunCoreAsync(MagnetUri magnet, string? trackerUrl, string saveDirectory, bool seed, bool dht, CancellationToken ct)
    {
        IList<string> trackers = trackerUrl is null ? [] : [trackerUrl];
        var link = new MagnetLink(ToInfoHashes(magnet), magnet.DisplayName, trackers, magnet.WebSeeds, magnet.ExactLength);

        var (engine, dhtEngine) = BuildEngine(seed, dht);
        using var engineLifetime = engine;
        var manager = await engine.AddAsync(link, saveDirectory, BuildTorrentSettings(seed, dht));
        var phase = new MetadataPhase(manager, hasTracker: trackerUrl is not null, dht);
        var routers = dhtEngine is null ? null : await SeedDhtAsync(dhtEngine, ct);

        AnsiConsole.MarkupLine($"  [grey]tracker[/]  {(trackerUrl is null ? "[grey italic]none[/]" : Markup.Escape(trackerUrl))}");
        AnsiConsole.MarkupLine($"  [grey]dht[/]      {(routers is null ? "[grey]off[/]" : $"on: {routers.Describe()}")}");
        AnsiConsole.MarkupLine($"  [grey]save to[/]  {Markup.Escape(Path.GetFullPath(saveDirectory))}");
        if (seed) AnsiConsole.MarkupLine("  [grey]mode[/]     seed: uploads unlimited, incoming peers accepted, stays running after completion");
        AnsiConsole.WriteLine();

        var clock = Stopwatch.StartNew();
        try
        {
            await manager.StartAsync();
            using var nudgeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var nudge = dhtEngine is null ? Task.CompletedTask : NudgeDhtAsync(dhtEngine, manager, nudgeCts.Token);
            try { await ShowProgressAsync(manager, phase, ct); }
            finally { nudgeCts.Cancel(); await nudge; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("  [yellow]download interrupted[/]");
        }
        finally
        {
            clock.Stop();
            if (!seed && manager.State != TorrentState.Stopped)
                await manager.StopAsync(TimeSpan.FromSeconds(5));
        }

        if (manager.State == TorrentState.Error)
        {
            AnsiConsole.MarkupLine($"  [red]error:[/] {Markup.Escape(manager.Error?.Exception?.Message ?? manager.Error?.Reason.ToString() ?? "unknown")}");
            return false;
        }
        if (!manager.Complete)
        {
            if (!ct.IsCancellationRequested) AnsiConsole.MarkupLine("  [red]download did not complete[/]");
            return false;
        }

        var size = manager.Torrent!.Size;
        var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        AnsiConsole.MarkupLine($"  [green bold]complete[/]  {Markup.Escape(manager.Name)}  "
            + $"[grey]({Render.HumanSize(size)} in {Render.HumanDuration(clock.Elapsed)}, {Render.HumanSize((long)(size / seconds))}/s)[/]");
        foreach (var file in manager.Files)
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(file.FullPath)}[/]");
        AnsiConsole.WriteLine();

        if (seed)
        {
            await SeedForeverAsync(manager, ct);   // returns only on error; Ctrl+C terminates the process
            await manager.StopAsync(TimeSpan.FromSeconds(5));
            return false;
        }
        return true;
    }

    /// <summary>
    /// Removes the engine's scratch directory, but only if it holds no files. MonoTorrent creates the directory
    /// (with empty fastresume and metadata subfolders) when the engine is built, even though every auto-save option
    /// is off and nothing is ever written there. Deleting it on the way out keeps "nothing outside the download
    /// directory" true. A tree with files in it is left alone: it is not ours to delete.
    /// </summary>
    internal static void RemoveCacheIfEmpty(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            if (Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any()) return;
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>
    /// The client engine and, when DHT is on, the DHT engine it was built around. The DHT engine is created here rather
    /// than left to MonoTorrent so that <see cref="SeedDhtAsync"/> can hand it bootstrap nodes before anything starts.
    /// </summary>
    internal static (ClientEngine Engine, DhtEngine? Dht) BuildEngine(bool seed, bool dht, string? cacheDirectory = null)
    {
        var settings = BuildEngineSettings(seed, dht, cacheDirectory);
        if (!dht) return (new ClientEngine(settings), null);
        var dhtEngine = new DhtEngine();
        return (new ClientEngine(settings, Factories.Default.WithDhtCreator(() => dhtEngine)), dhtEngine);
    }

    /// <summary>Queues the public routers as the DHT engine's first contacts. Must run before the torrent starts: nodes added later are pinged, not bootstrapped from.</summary>
    internal static async Task<DhtBootstrap.Seed> SeedDhtAsync(DhtEngine dhtEngine, CancellationToken ct)
    {
        DhtBootstrap.TrySetQueryTimeout(dhtEngine, DhtBootstrap.QueryTimeout);
        var seed = await DhtBootstrap.ResolveAsync(TimeSpan.FromSeconds(3), ct);
        if (seed.Nodes.Count > 0) dhtEngine.Add(seed.Nodes);
        return seed;
    }

    /// <summary>
    /// MonoTorrent only asks the DHT for peers once the engine reports Ready, and on a cold start Ready can take a minute
    /// or more: the bootstrap walk waits for every node it queried, and a dead one costs four 15-second timeouts. The
    /// routing table is usable long before that, so while the engine is still initialising and the torrent has no
    /// metadata, this asks for peers directly every few seconds. Peers found this way reach the manager the usual way.
    /// </summary>
    internal static async Task NudgeDhtAsync(DhtEngine dht, TorrentManager manager, CancellationToken ct)
    {
        try
        {
            while (!manager.HasMetadata && dht.State != DhtState.Ready)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                if (manager.HasMetadata || dht.State == DhtState.Ready || dht.NodeCount == 0) continue;
                if (manager.InfoHashes.V1 is { } v1) dht.GetPeers(v1);
                if (manager.InfoHashes.V2 is { } v2) dht.GetPeers(v2.Truncate());
            }
        }
        catch (OperationCanceledException) { /* download finished or was interrupted */ }
    }

    /// <summary>Engine settings. Every auto-save option is off, so the cache directory stays empty.</summary>
    internal static EngineSettings BuildEngineSettings(bool seed, bool dht, string? cacheDirectory = null)
        => new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory ?? DefaultCacheDirectory,
            // DHT binds one UDP socket on an OS-assigned port. It only needs replies to its own queries, so no port
            // mapping is attempted for it. Null switches the DHT engine off entirely.
            DhtEndPoint = dht ? new IPEndPoint(IPAddress.Any, 0) : null,
            AllowLocalPeerDiscovery = false,
            // Seeding needs peers to reach us: listen on an OS-assigned port and try to map it. Otherwise outgoing only.
            AllowPortForwarding = seed,
            ListenEndPoints = seed
                ? new() { ["ipv4"] = new IPEndPoint(IPAddress.Any, 0), ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, 0) }
                : new(),
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            AutoSaveLoadDhtCache = false,
        }.ToSettings();

    internal static TorrentSettings BuildTorrentSettings(bool seed, bool dht)
    {
        var builder = new TorrentSettingsBuilder
        {
            AllowDht = dht,
            AllowPeerExchange = true,
            CreateContainingDirectory = true,
        };
        if (!seed)
        {
            builder.UploadSlots = 0;           // seeding off: never unchoke a peer
            builder.MaximumUploadRate = 1;     // belt and braces; 0 would mean unlimited
        }
        return builder.ToSettings();
    }

    private static InfoHashes ToInfoHashes(MagnetUri magnet)
    {
        var v1 = magnet.InfoHashV1 is { } a ? new InfoHash(a) : null;
        var v2 = magnet.InfoHashV2 is { } b ? new InfoHash(b) : null;
        return (v1, v2) switch
        {
            (not null, not null) => new InfoHashes(v1, v2),
            (not null, null) => InfoHashes.FromV1(v1),
            (null, not null) => InfoHashes.FromV2(v2),
            _ => throw new InvalidOperationException("magnet has no info hash"),
        };
    }

    private static bool Interactive => AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;

    private static async Task ShowProgressAsync(TorrentManager manager, MetadataPhase phase, CancellationToken ct)
    {
        if (!Interactive)
        {
            await RunPlainAsync(manager, phase, ct);
            return;
        }

        // Cleared when done; the completion line that follows carries the final numbers. See Render.ProbeLiveAsync.
        await AnsiConsole.Progress()
            .AutoClear(true)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(),
                new PhaseColumn(phase))
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("fetching metadata", autoStart: true, maxValue: 1);
                task.IsIndeterminate = true;

                while (!IsFinished(manager))
                {
                    Update(manager, task);
                    await Task.Delay(250, ct);
                }
                Update(manager, task);
                if (manager.Complete) task.Value = task.MaxValue;
                task.StopTask();
            });
    }

    private static async Task RunPlainAsync(TorrentManager manager, MetadataPhase phase, CancellationToken ct)
    {
        var lastPct = -1;
        var lastPhase = string.Empty;
        (DhtState?, int) lastDht = default;
        while (!IsFinished(manager))
        {
            // The DHT line repeats on a state change or a new batch of peers, not on every node-count tick.
            var snapshot = phase.Snapshot();
            if (snapshot.Dht is not null && (snapshot.Dht, snapshot.DhtPeers) != lastDht)
            {
                lastDht = (snapshot.Dht, snapshot.DhtPeers);
                AnsiConsole.MarkupLine($"  [grey]dht:[/] {MetadataPhase.DescribeDht(snapshot)}");
            }

            if (!manager.HasMetadata)
            {
                var text = phase.Describe();
                if (text != lastPhase)
                {
                    lastPhase = text;
                    AnsiConsole.MarkupLine($"  [grey]metadata:[/] {text}");
                }
            }
            else
            {
                var pct = (int)manager.Progress;
                if (pct != lastPct)
                {
                    lastPct = pct;
                    AnsiConsole.MarkupLine($"  {pct,3}%  {Render.HumanSize(manager.Monitor.DownloadRate)}/s  {manager.OpenConnections} peer(s)");
                }
            }
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>Seed mode after completion: a live status line (or a periodic line when piped) until the process is killed or the engine errors.</summary>
    private static async Task SeedForeverAsync(TorrentManager manager, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        string Line()
        {
            var size = Math.Max(1, manager.Torrent!.Size);
            var uploaded = manager.Monitor.DataBytesSent;
            var ratio = (double)uploaded / size;
            var up = DateTime.UtcNow - started;
            return $"seeding  [green]{Render.HumanSize(manager.Monitor.UploadRate)}/s[/]  "
                 + $"[grey]uploaded[/] {Render.HumanSize(uploaded)}  [grey]ratio[/] {ratio:0.00}  "
                 + $"[grey]peers[/] {manager.OpenConnections} ([grey]sending to[/] {manager.UploadingTo})  "
                 + $"[grey]up for[/] {up:h\\:mm\\:ss}  [grey]ctrl+c to stop[/]";
        }

        if (!Interactive)
        {
            while (manager.State != TorrentState.Error)
            {
                AnsiConsole.MarkupLine("  " + Line());
                await Task.Delay(30_000, ct);
            }
            return;
        }

        await AnsiConsole.Status()
            .Spinner(AnsiConsole.Profile.Capabilities.Unicode ? Spinner.Known.Dots : Spinner.Known.Line)
            .StartAsync(Line(), async ctx =>
            {
                while (manager.State != TorrentState.Error)
                {
                    ctx.Status(Line());
                    await Task.Delay(1000, ct);
                }
            });
    }

    private static bool IsFinished(TorrentManager manager)
        => manager.Complete || manager.State is TorrentState.Error or TorrentState.Stopped or TorrentState.Stopping;

    private static void Update(TorrentManager manager, ProgressTask task)
    {
        if (!manager.HasMetadata)
        {
            task.Description = "fetching metadata";
            return;
        }

        var size = manager.Torrent!.Size;
        if (task.IsIndeterminate)
        {
            task.IsIndeterminate = false;
            task.MaxValue = size;
        }
        task.Value = Math.Min(size, (long)(manager.Progress / 100.0 * size));
        var state = manager.State switch
        {
            TorrentState.Hashing => "hashing",
            TorrentState.Downloading => "downloading",
            TorrentState.Seeding => "done",
            var s => s.ToString().ToLowerInvariant(),
        };
        task.Description = $"{state}  [grey]{manager.OpenConnections} peer(s), {manager.Peers.Seeds} seed(s)[/]";
    }

    /// <summary>Trailing progress column: what the engine is doing while it has no metadata yet, then the live DHT state (blank with DHT off).</summary>
    private sealed class PhaseColumn(MetadataPhase phase) : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            if (task.IsIndeterminate) return new Markup(phase.Describe());
            var dht = MetadataPhase.DescribeDht(phase.Snapshot());
            return dht.Length == 0 ? Text.Empty : new Markup($"[grey]dht {dht}[/]");
        }
    }
}
