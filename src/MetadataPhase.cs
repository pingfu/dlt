using MonoTorrent.Client;
using MonoTorrent.Dht;
using Spectre.Console;

namespace Dlt;

/// <summary>Point-in-time view of the metadata fetch, separated from the engine so the wording can be tested on its own.</summary>
/// <param name="HasTracker">False when the download is DHT only: no tracker was chosen, so none of the announce fields apply.</param>
/// <param name="Dht">Null when DHT is off. Otherwise the DHT engine's state, which is Ready once bootstrapping is over.</param>
internal sealed record MetadataSnapshot(
    bool HasTracker,
    bool Announced,
    bool AnnounceOk,
    string? TrackerError,
    int TrackerPeers,
    int PexPeers,
    int DhtPeers,
    DhtState? Dht,
    int DhtNodes,
    int ConnectFailed,
    ConnectionFailureReason? LastFailure,
    int KnownPeers,
    int OpenConnections,
    int WithExtensions,
    TimeSpan? RetryIn);

/// <summary>
/// Names the stage of the metadata fetch (announce, connect, ask peers for the info dict) from tracker, DHT and peer events.
/// The engine itself only exposes "no metadata yet", so the stage is reconstructed here.
/// </summary>
internal sealed class MetadataPhase
{
    private readonly TorrentManager _manager;
    private readonly bool _hasTracker;
    private readonly bool _dht;
    private readonly object _lock = new();
    private bool _announced;
    private bool _announceOk;
    private string? _trackerError;
    private int _trackerPeers;
    private int _pexPeers;
    private int _dhtPeers;
    private int _connectFailed;
    private ConnectionFailureReason? _lastFailure;
    private int _withExtensions;

    public MetadataPhase(TorrentManager manager, bool hasTracker, bool dht)
    {
        _manager = manager;
        _hasTracker = hasTracker;
        _dht = dht;
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
            lock (_lock)
            {
                if (e is PeerExchangePeersAdded) _pexPeers += e.NewPeers;
                else if (e is DhtPeersAdded) _dhtPeers += e.NewPeers;
            }
        };
        manager.ConnectionAttemptFailed += (_, e) =>
        {
            lock (_lock) { _connectFailed++; _lastFailure = e.Reason; }
        };
        manager.PeerConnected += (_, e) =>
        {
            lock (_lock) if (e.Peer.SupportsLTMessages) _withExtensions++;
        };
        manager.PeerDisconnected += (_, e) =>
        {
            lock (_lock) if (e.Peer.SupportsLTMessages) _withExtensions = Math.Max(0, _withExtensions - 1);
        };
    }

    public MetadataSnapshot Snapshot()
    {
        lock (_lock)
        {
            var tracker = _manager.TrackerManager.Tiers.FirstOrDefault()?.ActiveTracker;
            var dht = _dht ? _manager.Engine?.Dht : null;
            return new MetadataSnapshot(
                _hasTracker, _announced, _announceOk, _trackerError, _trackerPeers, _pexPeers,
                _dhtPeers, dht?.State, dht?.NodeCount ?? 0,
                _connectFailed, _lastFailure,
                _manager.Peers.Available, _manager.OpenConnections, _withExtensions,
                tracker is null ? null : tracker.UpdateInterval - tracker.TimeSinceLastAnnounce);
        }
    }

    public string Describe() => Describe(Snapshot());

    public static string Describe(MetadataSnapshot s)
    {
        var found = s.TrackerPeers + s.PexPeers + s.DhtPeers;
        var nothingYet = found == 0 && s.KnownPeers == 0 && s.OpenConnections == 0;

        if (s.HasTracker)
        {
            if (!s.Announced)
                return "[grey]announcing to tracker[/]" + DhtAside(s);

            // A failed announce is only the headline while nothing else has produced a peer: DHT may still carry the download.
            if (!s.AnnounceOk && nothingYet)
                return $"[red]tracker: {Markup.Escape(s.TrackerError ?? "announce failed")}[/]  [grey]retrying in {Wait(s.RetryIn)}[/]" + DhtAside(s);

            if (nothingYet)
                return $"[yellow]tracker returned no peers[/]  [grey]next announce in {Wait(s.RetryIn)}[/]" + DhtAside(s);
        }
        else
        {
            if (s.Dht is not DhtState.Ready && nothingYet)
                return $"[grey]bootstrapping dht · {s.DhtNodes} nodes[/]";

            if (nothingYet)
                return $"[yellow]dht found no peers yet[/]  [grey]{s.DhtNodes} nodes, still searching[/]";
        }

        var pool = Pool(s);

        if (s.OpenConnections == 0)
        {
            var failed = s.ConnectFailed > 0 ? $", {s.ConnectFailed} failed ({Reason(s.LastFailure)})" : string.Empty;
            return $"[grey]{pool} peers · connecting{failed}[/]";
        }

        var capable = s.WithExtensions == s.OpenConnections ? string.Empty : $", {s.WithExtensions} speak extensions";
        return $"[grey]{pool} peers · {s.OpenConnections} connected{capable} · requesting info dict[/]";
    }

    /// <summary>The DHT on its own, for the live line that outlives the metadata phase: "ready · 142 nodes · 23 peers found". Empty with DHT off.</summary>
    public static string DescribeDht(MetadataSnapshot s)
    {
        if (s.Dht is null) return string.Empty;
        var state = s.Dht switch
        {
            DhtState.Ready => "ready",
            DhtState.Initialising => "bootstrapping",
            _ => "not ready",
        };
        var peers = s.DhtPeers == 1 ? "1 peer found" : $"{s.DhtPeers} peers found";
        return $"{state} · {s.DhtNodes} nodes · {peers}";
    }

    /// <summary>"tracker gave 12 (+4 pex, +3 dht)" or, with no tracker, "dht gave 3 (+4 pex)".</summary>
    private static string Pool(MetadataSnapshot s)
    {
        var extras = new List<string>();
        if (s.PexPeers > 0) extras.Add($"+{s.PexPeers} pex");
        if (s.HasTracker && s.DhtPeers > 0) extras.Add($"+{s.DhtPeers} dht");
        var head = s.HasTracker ? $"tracker gave {s.TrackerPeers}" : $"dht gave {s.DhtPeers}";
        return head + (extras.Count > 0 ? $" ({string.Join(", ", extras)})" : string.Empty);
    }

    /// <summary>What DHT is up to while the tracker line is the headline. Empty when DHT is off.</summary>
    private static string DhtAside(MetadataSnapshot s) => s.Dht switch
    {
        null => string.Empty,
        DhtState.Ready => $"  [grey]· dht searching, {s.DhtNodes} nodes[/]",
        _ => $"  [grey]· dht bootstrapping, {s.DhtNodes} nodes[/]",
    };

    internal static string Wait(TimeSpan? wait) => wait switch
    {
        null => "?",
        { } w when w <= TimeSpan.Zero => "a moment",
        { TotalSeconds: < 90 } w => $"{w.TotalSeconds:0}s",
        { } w => $"{w.TotalMinutes:0}m",
    };

    private static string Reason(ConnectionFailureReason? reason) => reason switch
    {
        ConnectionFailureReason.Unreachable => "unreachable",
        ConnectionFailureReason.HandshakeFailed => "handshake failed",
        ConnectionFailureReason.EncryptionNegiotiationFailed => "encryption failed",
        _ => "unknown",
    };
}
