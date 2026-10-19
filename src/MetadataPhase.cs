using MonoTorrent.Client;
using Spectre.Console;

namespace Dlt;

/// <summary>Point-in-time view of the metadata fetch, separated from the engine so the wording can be tested on its own.</summary>
internal sealed record MetadataSnapshot(
    bool Announced,
    bool AnnounceOk,
    string? TrackerError,
    int TrackerPeers,
    int PexPeers,
    int ConnectFailed,
    ConnectionFailureReason? LastFailure,
    int KnownPeers,
    int OpenConnections,
    int WithExtensions,
    TimeSpan? RetryIn);

/// <summary>
/// Names the stage of the metadata fetch (announce, connect, ask peers for the info dict) from tracker and peer events.
/// The engine itself only exposes "no metadata yet", so the stage is reconstructed here.
/// </summary>
internal sealed class MetadataPhase
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
            return new MetadataSnapshot(
                _announced, _announceOk, _trackerError, _trackerPeers, _pexPeers, _connectFailed, _lastFailure,
                _manager.Peers.Available, _manager.OpenConnections, _withExtensions,
                tracker is null ? null : tracker.UpdateInterval - tracker.TimeSinceLastAnnounce);
        }
    }

    public string Describe() => Describe(Snapshot());

    public static string Describe(MetadataSnapshot s)
    {
        if (!s.Announced)
            return "[grey]announcing to tracker[/]";

        if (!s.AnnounceOk)
            return $"[red]tracker: {Markup.Escape(s.TrackerError ?? "announce failed")}[/]  [grey]retrying in {Wait(s.RetryIn)}[/]";

        var pool = s.TrackerPeers + (s.PexPeers > 0 ? $" (+{s.PexPeers} pex)" : string.Empty);

        if (s.TrackerPeers == 0 && s.KnownPeers == 0 && s.OpenConnections == 0)
            return $"[yellow]tracker returned no peers[/]  [grey]next announce in {Wait(s.RetryIn)}[/]";

        if (s.OpenConnections == 0)
        {
            var failed = s.ConnectFailed > 0 ? $", {s.ConnectFailed} failed ({Reason(s.LastFailure)})" : string.Empty;
            return $"[grey]tracker gave {pool} peers · connecting{failed}[/]";
        }

        var capable = s.WithExtensions == s.OpenConnections ? string.Empty : $", {s.WithExtensions} speak extensions";
        return $"[grey]tracker gave {pool} peers · {s.OpenConnections} connected{capable} · requesting info dict[/]";
    }

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
