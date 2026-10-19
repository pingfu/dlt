using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dlt;

/// <summary>Downloads the torrent behind a magnet with MonoTorrent, discovering peers only through the one chosen tracker.</summary>
internal static class Download
{
    /// <param name="seed">Lift the upload restriction, accept incoming peers, and keep running as a seed once complete.</param>
    public static async Task<bool> RunAsync(MagnetUri magnet, string trackerUrl, string saveDirectory, bool seed, CancellationToken ct)
    {
        var link = new MagnetLink(ToInfoHashes(magnet), magnet.DisplayName, [trackerUrl], magnet.WebSeeds, magnet.ExactLength);

        var engineSettings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "dlt", "cache"),
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

        var torrentBuilder = new TorrentSettingsBuilder
        {
            AllowDht = false,
            AllowPeerExchange = true,
            CreateContainingDirectory = true,
        };
        if (!seed)
        {
            torrentBuilder.UploadSlots = 0;           // seeding off: never unchoke a peer
            torrentBuilder.MaximumUploadRate = 1;     // belt and braces; 0 would mean unlimited
        }
        var torrentSettings = torrentBuilder.ToSettings();

        using var engine = new ClientEngine(engineSettings);
        var manager = await engine.AddAsync(link, saveDirectory, torrentSettings);
        var phase = new MetadataPhase(manager);

        AnsiConsole.MarkupLine($"  [grey]tracker[/]  {Markup.Escape(trackerUrl)}");
        AnsiConsole.MarkupLine($"  [grey]save to[/]  {Markup.Escape(Path.GetFullPath(saveDirectory))}");
        if (seed) AnsiConsole.MarkupLine("  [grey]mode[/]     seed: uploads unlimited, incoming peers accepted, stays running after completion");
        AnsiConsole.WriteLine();

        try
        {
            await manager.StartAsync();
            await ShowProgressAsync(manager, phase, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("  [yellow]download interrupted[/]");
        }
        finally
        {
            if (!seed && manager.State != TorrentState.Stopped)
                await manager.StopAsync(TimeSpan.FromSeconds(5));
        }

        AnsiConsole.WriteLine();
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

        AnsiConsole.MarkupLine($"  [green bold]complete[/]  {Markup.Escape(manager.Name)}  [grey]({Render.HumanSize(manager.Torrent!.Size)})[/]");
        foreach (var file in manager.Files)
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(file.FullPath)}[/]");

        if (seed)
        {
            AnsiConsole.WriteLine();
            await SeedForeverAsync(manager, ct);   // returns only on error; Ctrl+C terminates the process
            await manager.StopAsync(TimeSpan.FromSeconds(5));
            return false;
        }
        return true;
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

        await AnsiConsole.Progress()
            .AutoClear(false)
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
