using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Magstat.Trackers;

/// <summary>WebTorrent (ws/wss) tracker probe: open the socket and send a JSON scrape for the info hash.</summary>
internal static class WebSocketTracker
{
    public static async Task ProbeAsync(TrackerProbe p, byte[] infoHash, TimeSpan timeout, CancellationToken ct)
    {
        var uri = p.Uri!;
        p.Endpoint = uri.Authority;

        using var ws = new ClientWebSocket();
        try { ws.Options.SetRequestHeader("User-Agent", HttpTracker.UserAgent); } catch (ArgumentException) { /* restricted on some runtimes */ }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        p.Stage = ProbeStage.Connecting;
        await ws.ConnectAsync(uri, cts.Token);

        // WebTorrent trackers take the 20 raw bytes as a "binary string": one JS char per byte.
        var binaryHash = Encoding.Latin1.GetString(infoHash);

        p.Stage = ProbeStage.Scraping;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { action = "scrape", info_hash = binaryHash });
        await ws.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        var buffer = new ArrayBufferWriter<byte>();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(chunk, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new TrackerResponseException($"tracker closed the socket ({result.CloseStatus}: {result.CloseStatusDescription})");
            buffer.Write(chunk.AsSpan(0, result.Count));
            if (buffer.WrittenCount > 1 << 20) throw new TrackerResponseException("scrape response exceeded 1 MiB");
            if (result.EndOfMessage) break;
        }

        try
        {
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new TrackerResponseException("scrape response is not a JSON object");

            if (root.TryGetProperty("failure reason", out var failure))
                throw new TrackerResponseException($"scrape failure: {failure}");

            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
            {
                if (files.TryGetProperty(binaryHash, out var entry) && entry.ValueKind == JsonValueKind.Object)
                    p.ReportCounts(GetInt(entry, "complete"), GetInt(entry, "incomplete"), GetInt(entry, "downloaded"), "ws scrape");
                else
                {
                    p.Method = "ws scrape";
                    p.Finish(ProbeOutcome.NotFound, "scrape ok, but the tracker does not list this hash");
                }
            }
            else
            {
                throw new TrackerResponseException("scrape response had no 'files' object");
            }
        }
        catch (JsonException ex)
        {
            throw new TrackerResponseException($"scrape response is not valid JSON: {ex.Message}");
        }
        finally
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
            }
            catch { /* best effort */ }
        }
    }

    private static long? GetInt(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
