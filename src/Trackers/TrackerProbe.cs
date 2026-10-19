using System.Diagnostics;

namespace Dlt.Trackers;

public enum ProbeStage { Queued, Resolving, Connecting, Scraping, Announcing, Done }

public enum ProbeOutcome
{
    Pending,
    /// <summary>Tracker reachable and it reports seeders, leechers or completed downloads for the hash.</summary>
    Found,
    /// <summary>Tracker reachable but it does not know the hash (or knows no peers for it).</summary>
    NotFound,
    /// <summary>No usable response: DNS failure, refused, timed out, reset.</summary>
    Unreachable,
    /// <summary>Tracker answered, but with an error or something we could not interpret.</summary>
    Error,
    /// <summary>Scheme or URL shape this tool cannot probe.</summary>
    Unsupported,
}

/// <summary>Thrown when a tracker answers but the answer is an error (failure reason, UDP action 3, non-bencode body, ...).</summary>
public sealed class TrackerResponseException(string message) : Exception(message);

/// <summary>Mutable, per-tracker state. Written by the probe task, read by the live renderer.</summary>
public sealed class TrackerProbe
{
    public TrackerProbe(int index, string url)
    {
        Index = index;
        Url = url;
        Uri = System.Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    public int Index { get; }
    public string Url { get; }
    public Uri? Uri { get; }
    public string Scheme => Uri?.Scheme.ToLowerInvariant() ?? "?";
    public Stopwatch Clock { get; } = new();

    public ProbeStage Stage { get; set; } = ProbeStage.Queued;
    public ProbeOutcome Outcome { get; private set; } = ProbeOutcome.Pending;
    public string? Detail { get; set; }
    /// <summary>Resolved address (udp) or authority (http/ws) that was contacted.</summary>
    public string? Endpoint { get; set; }
    /// <summary>How the answer was obtained: "scrape", "announce", ...</summary>
    public string? Method { get; set; }
    public long? Seeders { get; private set; }
    public long? Leechers { get; private set; }
    public long? Completed { get; private set; }

    public void Finish(ProbeOutcome outcome, string? detail = null)
    {
        Outcome = outcome;
        Detail = detail;
    }

    public void ReportCounts(long? seeders, long? leechers, long? completed, string method, string? note = null)
    {
        Seeders = seeders;
        Leechers = leechers;
        Completed = completed;
        Method = method;
        var known = (seeders ?? 0) > 0 || (leechers ?? 0) > 0 || (completed ?? 0) > 0;
        Outcome = known ? ProbeOutcome.Found : ProbeOutcome.NotFound;
        Detail = note ?? (known ? null : "tracker answered but has no peers for this hash");
    }
}
