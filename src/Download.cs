using System.Diagnostics;
using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dlt;

/// <summary>Downloads the torrent behind a magnet with MonoTorrent, discovering peers only through the one chosen tracker.</summary>
internal static class Download
{
    /// <summary>Where MonoTorrent insists on having a scratch directory. Nothing is ever written into it; see <see cref="RemoveCacheIfEmpty"/>.</summary>
    internal static readonly string DefaultCacheDirectory = Path.Combine(Path.GetTempPath(), "dlt");

    /// <param name="seed">Lift the upload restriction, accept incoming peers, and keep running as a seed once complete.</param>
    public static async Task<bool> RunAsync(MagnetUri magnet, string trackerUrl, string saveDirectory, bool seed, CancellationToken ct)
    {
        try { return await RunCoreAsync(magnet, trackerUrl, saveDirectory, seed, ct); }
        finally { RemoveCacheIfEmpty(DefaultCacheDirectory); }
    }

    private static async Task<bool> RunCoreAsync(MagnetUri magnet, string trackerUrl, string saveDirectory, bool seed, CancellationToken ct)
    {
        var link = new MagnetLink(ToInfoHashes(magnet), magnet.DisplayName, [trackerUrl], magnet.WebSeeds, magnet.ExactLength);

        using var engine = new ClientEngine(BuildEngineSettings(seed));
        var manager = await engine.AddAsync(link, saveDirectory, BuildTorrentSettings(seed));
        var phase = new MetadataPhase(manager);

        AnsiConsole.MarkupLine($"  [grey]tracker[/]  {Markup.Escape(trackerUrl)}");
        AnsiConsole.MarkupLine($"  [grey]save to[/]  {Markup.Escape(Path.GetFullPath(saveDirectory))}");
        if (seed) AnsiConsole.MarkupLine("  [grey]mode[/]     seed: uploads unlimited, incoming peers accepted, stays running after completion");
        AnsiConsole.WriteLine();

        var clock = Stopwatch.StartNew();
        try
        {
            await manager.StartAsync();
            await ShowProgressAsync(manager, phase, ct);
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

    /// <summary>Engine settings. Every auto-save option is off, so the cache directory stays empty.</summary>
    internal static EngineSettings BuildEngineSettings(bool seed, string? cacheDirectory = null)
        => new EngineSettingsBuilder
        {
            CacheDirectory = cacheDirectory ?? DefaultCacheDirectory,
            DhtEndPoint = null,                       // no DHT: peers come from the chosen tracker only
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

    internal static TorrentSettings BuildTorrentSettings(bool seed)
    {
        var builder = new TorrentSettingsBuilder
        {
            AllowDht = false,
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
        while (!IsFinished(manager))
        {
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

    /// <summary>Trailing progress column: what the engine is doing while it has no metadata yet, blank afterwards.</summary>
    private sealed class PhaseColumn(MetadataPhase phase) : ProgressColumn
    {
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
            => task.IsIndeterminate ? new Markup(phase.Describe()) : Text.Empty;
    }
}
