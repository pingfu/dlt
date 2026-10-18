using System.Net;
using System.Text;

namespace Magstat.Trackers;

/// <summary>
/// HTTP(S) tracker probe. Prefers the scrape convention (BEP 48: last path segment "announce..." becomes "scrape...").
/// Falls back to a passive announce (event=stopped, numwant=0) when the tracker exposes no scrape endpoint.
/// </summary>
internal static class HttpTracker
{
    public const string UserAgent = "magstat/1.0";

    public static async Task ProbeAsync(HttpClient http, TrackerProbe p, byte[] infoHash, TimeSpan timeout, CancellationToken ct)
    {
        var uri = p.Uri!;
        p.Endpoint = uri.Authority;
        var hashParam = PercentEncode(infoHash);

        string? scrapeNote = null;
        var scrapeUri = BuildScrapeUri(uri, hashParam);
        if (scrapeUri is null)
        {
            scrapeNote = "no scrape endpoint derivable";
        }
        else
        {
            p.Stage = ProbeStage.Scraping;
            var (status, body) = await GetAsync(http, scrapeUri, timeout, ct);
            if (status == HttpStatusCode.OK)
            {
                Dictionary<string, object>? dict = null;
                try { dict = Bencode.DecodeDictionary(body); }
                catch (Exception ex) when (ex is FormatException or OverflowException) { scrapeNote = "scrape returned non-bencode body"; }

                if (dict is not null)
                {
                    var failure = Bencode.GetString(dict, "failure reason");
                    if (failure is not null) throw new TrackerResponseException($"scrape failure: {failure}");

                    if (dict.TryGetValue("files", out var filesObj) && filesObj is Dictionary<string, object> files)
                    {
                        if (files.TryGetValue(Encoding.Latin1.GetString(infoHash), out var entryObj) && entryObj is Dictionary<string, object> entry)
                        {
                            p.ReportCounts(Bencode.GetInt(entry, "complete"), Bencode.GetInt(entry, "incomplete"), Bencode.GetInt(entry, "downloaded"), "scrape");
                        }
                        else
                        {
                            p.Method = "scrape";
                            p.Finish(ProbeOutcome.NotFound, "scrape ok, but the tracker does not list this hash");
                        }
                        return;
                    }

                    scrapeNote = "scrape response had no 'files' entry";
                }
            }
            else
            {
                scrapeNote = $"scrape returned HTTP {(int)status}";
            }
        }

        // Fallback: passive announce. event=stopped keeps us out of the swarm; numwant=0 asks for no peer list.
        p.Stage = ProbeStage.Announcing;
        var announceUri = BuildAnnounceUri(uri, hashParam);
        var (announceStatus, announceBody) = await GetAsync(http, announceUri, timeout, ct);
        if (announceStatus != HttpStatusCode.OK)
            throw new TrackerResponseException($"{scrapeNote}; announce returned HTTP {(int)announceStatus} {announceStatus}");

        Dictionary<string, object> response;
        try { response = Bencode.DecodeDictionary(announceBody); }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new TrackerResponseException($"{scrapeNote}; announce returned a non-bencode body ({Snippet(announceBody)})");
        }

        var announceFailure = Bencode.GetString(response, "failure reason");
        if (announceFailure is not null) throw new TrackerResponseException($"{scrapeNote}; announce failure: {announceFailure}");

        var complete = Bencode.GetInt(response, "complete");
        var incomplete = Bencode.GetInt(response, "incomplete");
        var peerCount = CountPeers(response);
        var note = $"via announce ({scrapeNote})" + (peerCount > 0 ? $", {peerCount} peer(s) listed" : string.Empty);
        if ((complete ?? 0) > 0 || (incomplete ?? 0) > 0 || peerCount > 0)
            p.ReportCounts(complete, incomplete, Bencode.GetInt(response, "downloaded"), "announce", note);
        else
            p.ReportCounts(complete, incomplete, null, "announce", $"announce ok but no peers reported ({scrapeNote})");
    }

    private static async Task<(HttpStatusCode Status, byte[] Body)> GetAsync(HttpClient http, string url, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var body = await response.Content.ReadAsByteArrayAsync(cts.Token);
        return (response.StatusCode, body);
    }

    /// <summary>BEP 48: replace a trailing "announce…" path segment with "scrape…"; otherwise scraping is unsupported.</summary>
    private static string? BuildScrapeUri(Uri uri, string hashParam)
    {
        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        var last = path[(slash + 1)..];
        if (!last.StartsWith("announce", StringComparison.Ordinal)) return null;
        var scrapePath = path[..(slash + 1)] + "scrape" + last["announce".Length..];
        return uri.GetLeftPart(UriPartial.Authority) + scrapePath + QueryPrefix(uri) + "info_hash=" + hashParam;
    }

    private static string BuildAnnounceUri(Uri uri, string hashParam)
    {
        var peerId = "-MS0100-" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant();
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath + QueryPrefix(uri)
            + "info_hash=" + hashParam
            + "&peer_id=" + peerId
            + "&port=6881&uploaded=0&downloaded=0&left=0&compact=1&numwant=0&event=stopped";
    }

    private static string QueryPrefix(Uri uri)
    {
        var existing = uri.Query.TrimStart('?');
        return existing.Length > 0 ? "?" + existing + "&" : "?";
    }

    private static string PercentEncode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append('%').Append(b.ToString("X2"));
        return sb.ToString();
    }

    private static int CountPeers(Dictionary<string, object> response)
    {
        var count = 0;
        if (response.TryGetValue("peers", out var peers))
            count += peers switch { byte[] compact => compact.Length / 6, List<object> list => list.Count, _ => 0 };
        if (response.TryGetValue("peers6", out var peers6) && peers6 is byte[] compact6)
            count += compact6.Length / 18;
        return count;
    }

    private static string Snippet(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 200));
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.StartsWith("<", StringComparison.Ordinal)) return "looks like HTML";
        return text.Length > 60 ? text[..60] + "…" : text.Length == 0 ? "empty body" : text;
    }
}
