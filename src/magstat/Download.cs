using MonoTorrent;
using MonoTorrent.Client;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Magstat;

/// <summary>Downloads the torrent behind a magnet with MonoTorrent, discovering peers only through the one chosen tracker.</summary>
internal static class Download
{
    public static async Task<bool> RunAsync(MagnetUri magnet, string trackerUrl, string saveDirectory, CancellationToken ct)
    {
        var link = new MagnetLink(ToInfoHashes(magnet), magnet.DisplayName, [trackerUrl], magnet.WebSeeds, magnet.ExactLength);

        var engineSettings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "magstat", "cache"),
            DhtEndPoint = null,                       // no DHT: peers come from the chosen tracker only
            AllowLocalPeerDiscovery = false,
            AllowPortForwarding = false,
            ListenEndPoints = new(),                  // outgoing connections only; nothing to firewall
            AutoSaveLoadFastResume = false,
            AutoSaveLoadMagnetLinkMetadata = false,
            AutoSaveLoadDhtCache = false,
        }.ToSettings();

        var torrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = false,
            AllowPeerExchange = true,
            CreateContainingDirectory = true,
            UploadSlots = 0,                          // seeding off: never unchoke a peer
            MaximumUploadRate = 1,                    // belt and braces; 0 would mean unlimited
        }.ToSettings();

        using var engine = new ClientEngine(engineSettings);
        var manager = await engine.AddAsync(link, saveDirectory, torrentSettings);
        var phase = new MetadataPhase(manager);

        AnsiConsole.MarkupLine($"  [grey]tracker[/]  {Markup.Escape(trackerUrl)}");
        AnsiConsole.MarkupLine($"  [grey]save to[/]  {Markup.Escape(Path.GetFullPath(saveDirectory))}");
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
            if (manager.State != TorrentState.Stopped)
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

    private static async Task ShowProgressAsync(TorrentManager manager, MetadataPhase phase, CancellationToken ct)
    {
        var interactive = AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;
        if (!interactive)
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

    /// <summary>
    /// Names the stage of the metadata fetch (announce, connect, ask peers for the info dict) from tracker and peer events.
    /// The engine itself only exposes "no metadata yet", so the stage is reconstructed here.
    /// </summary>
    private sealed class MetadataPhase
    {
        private readonly TorrentManager _manager;
        private readonly object _lock = new();
        private bool _announced;
        private bool _announceOk;
        private string? _trackerError;
        private int _trackerPeers;
        private int _pexPeers;
        private int _connectFailed;
        private ConnectionFailureReason? _lastFailure;
        private int _connected;
        private int _withExtensions;

        public MetadataPhase(TorrentManager manager)
        {
            _manager = manager;
            manager.TrackerManager.AnnounceComplete += (_, e) =>
            {
                lock (_lock)
                {
                    _announced = true;
                    _announceOk = e.Successful;
                    _trackerError = e.Successful ? null : e.Tracker?.FailureMessage ?? "announce failed";
                    _trackerPeers = e.Peers.Values.Sum(list => list.Count);
                }
            };
            manager.PeersFound += (_, e) =>
            {
                lock (_lock) if (e is PeerExchangePeersAdded) _pexPeers += e.NewPeers;
            };
            manager.ConnectionAttemptFailed += (_, e) =>
            {
                lock (_lock) { _connectFailed++; _lastFailure = e.Reason; }
            };
            manager.PeerConnected += (_, e) =>
            {
                lock (_lock) { _connected++; if (e.Peer.SupportsLTMessages) _withExtensions++; }
            };
            manager.PeerDisconnected += (_, e) =>
            {
                lock (_lock)
                {
                    _connected = Math.Max(0, _connected - 1);
                    if (e.Peer.SupportsLTMessages) _withExtensions = Math.Max(0, _withExtensions - 1);
                }
            };
        }

        public string Describe()
        {
            lock (_lock)
            {
                if (!_announced)
                    return "[grey]announcing to tracker[/]";

                if (!_announceOk)
                    return $"[red]tracker: {Markup.Escape(_trackerError ?? "announce failed")}[/]  [grey]retrying in {Retry()}[/]";

                var known = _manager.Peers.Available;
                var open = _manager.OpenConnections;
                var pool = _trackerPeers + (_pexPeers > 0 ? $" (+{_pexPeers} pex)" : string.Empty);

                if (_trackerPeers == 0 && known == 0 && open == 0)
                    return $"[yellow]tracker returned no peers[/]  [grey]next announce in {Retry()}[/]";

                if (open == 0)
                {
                    var failed = _connectFailed > 0 ? $", {_connectFailed} failed ({Reason(_lastFailure)})" : string.Empty;
                    return $"[grey]tracker gave {pool} peers · connecting{failed}[/]";
                }

                var capable = _withExtensions == open ? string.Empty : $", {_withExtensions} speak extensions";
                return $"[grey]tracker gave {pool} peers · {open} connected{capable} · requesting info dict[/]";
            }
        }

        private string Retry()
        {
            var tracker = _manager.TrackerManager.Tiers.FirstOrDefault()?.ActiveTracker;
            if (tracker is null) return "?";
            var wait = tracker.UpdateInterval - tracker.TimeSinceLastAnnounce;
            return wait <= TimeSpan.Zero ? "a moment" : wait.TotalSeconds < 90 ? $"{wait.TotalSeconds:0}s" : $"{wait.TotalMinutes:0}m";
        }

        private static string Reason(ConnectionFailureReason? reason) => reason switch
        {
            ConnectionFailureReason.Unreachable => "unreachable",
            ConnectionFailureReason.HandshakeFailed => "handshake failed",
            ConnectionFailureReason.EncryptionNegiotiationFailed => "encryption failed",
            _ => "unknown",
        };
    }
}
