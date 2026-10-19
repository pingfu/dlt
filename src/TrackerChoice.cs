using Dlt.Trackers;

namespace Dlt;

internal static class TrackerChoice
{
    /// <summary>The tracker to download through: most seeds, then most leechers, then table order. Null when none reported the torrent.</summary>
    public static TrackerProbe? Best(IEnumerable<TrackerProbe> probes)
        => probes
            .Where(p => p.Outcome == ProbeOutcome.Found)
            .OrderByDescending(p => p.Seeders ?? 0)
            .ThenByDescending(p => p.Leechers ?? 0)
            .ThenBy(p => p.Index)
            .FirstOrDefault();

    /// <summary>Why a tracker was picked, for the line printed before the download starts.</summary>
    public static string Reason(TrackerProbe best, int foundCount)
        => foundCount == 1 ? "the only tracker that knows it" : $"most seeds: {best.Seeders ?? 0:N0}";
}
