using System.Net;
using MonoTorrent;

namespace Dlt;

/// <summary>Loads a .torrent file (local path or http(s) URL, held in memory only) and presents it as a MagnetUri so the rest of the tool treats both alike.</summary>
internal static class TorrentInput
{
    private const int MaxBytes = 16 << 20;

    public static bool IsHttpUrl(string input)
        => Uri.TryCreate(input, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public static async Task<MagnetUri> LoadAsync(string input, CancellationToken ct)
    {
        byte[] data;
        if (IsHttpUrl(input))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = MaxBytes };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Trackers.HttpTracker.UserAgent);
            using var response = await http.GetAsync(input, ct);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new MagnetFormatException($"fetching the torrent returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            data = await response.Content.ReadAsByteArrayAsync(ct);
        }
        else
        {
            if (new FileInfo(input).Length > MaxBytes) throw new MagnetFormatException("torrent file is larger than 16 MiB; refusing to parse it");
            data = await File.ReadAllBytesAsync(input, ct);
        }

        Torrent torrent;
        try { torrent = Torrent.Load(data); }
        catch (Exception ex) { throw new MagnetFormatException($"not a valid torrent file: {ex.Message}"); }

        var trackers = new List<string>();
        var warnings = new List<string>();
        foreach (var url in torrent.AnnounceUrls.SelectMany(tier => tier))
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (trackers.Contains(url, StringComparer.OrdinalIgnoreCase)) warnings.Add($"duplicate tracker ignored: {url}");
            else trackers.Add(url);
        }
        if (trackers.Count == 0) warnings.Add("torrent lists no trackers; peers would have to come from DHT / PEX");

        return new MagnetUri
        {
            Kind = "torrent",
            Original = input,
            Decoded = input,
            DisplayName = torrent.Name,
            InfoHashV1 = torrent.InfoHashes.V1?.AsMemory().ToArray(),
            InfoHashV2 = torrent.InfoHashes.V2?.AsMemory().ToArray(),
            ExactLength = torrent.Size,
            FileCount = torrent.Files.Count,
            Trackers = trackers,
            WebSeeds = torrent.HttpSeeds.Select(u => u.ToString()).ToList(),
            Warnings = warnings,
        };
    }
}
