using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dlt.Trackers;

/// <summary>UDP tracker protocol (BEP 15): connect handshake followed by a scrape for the single info hash.</summary>
internal static class UdpTracker
{
    private const long ProtocolId = 0x41727101980L;
    private const int ActionConnect = 0, ActionScrape = 2, ActionError = 3;
    private const int ConnectAttempts = 2;

    public static async Task ProbeAsync(TrackerProbe p, byte[] infoHash, TimeSpan timeout, CancellationToken ct)
    {
        var uri = p.Uri!;
        if (uri.Port <= 0)
        {
            p.Finish(ProbeOutcome.Unsupported, "udp tracker URL has no port");
            return;
        }

        p.Stage = ProbeStage.Resolving;
        var addresses = await ResolveAsync(uri.IdnHost, timeout, ct);
        if (addresses.Length == 0)
        {
            p.Finish(ProbeOutcome.Unreachable, "dns: name resolved to no addresses");
            return;
        }

        // Try one IPv4 and, failing that, one IPv6 address. Keeps the worst case bounded.
        var candidates = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(1)
            .Concat(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(1))
            .ToList();

        string? lastError = null;
        foreach (var address in candidates)
        {
            p.Endpoint = new IPEndPoint(address, uri.Port).ToString();
            try
            {
                using var udp = new UdpClient(address.AddressFamily);
                udp.Connect(address, uri.Port);

                p.Stage = ProbeStage.Connecting;
                var connectionId = await ConnectAsync(udp, timeout, ct);
                if (connectionId is null)
                {
                    lastError = "timeout";
                    continue;
                }

                p.Stage = ProbeStage.Scraping;
                var counts = await ScrapeAsync(udp, connectionId.Value, infoHash, timeout, ct);
                if (counts is null)
                {
                    lastError = "connected, but scrape timed out";
                    continue;
                }

                var (seeders, completed, leechers) = counts.Value;
                p.ReportCounts(seeders, leechers, completed, "udp scrape");
                return;
            }
            catch (SocketException ex)
            {
                lastError = SocketErrors.Describe(ex);
            }
        }

        p.Finish(ProbeOutcome.Unreachable, lastError ?? "no response");
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await Dns.GetHostAddressesAsync(host, cts.Token);
    }

    private static async Task<long?> ConnectAsync(UdpClient udp, TimeSpan timeout, CancellationToken ct)
    {
        for (var attempt = 0; attempt < ConnectAttempts; attempt++)
        {
            var transactionId = Random.Shared.Next();
            var request = new byte[16];
            BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(0), ProtocolId);
            BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), ActionConnect);
            BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(12), transactionId);
            await udp.SendAsync(request, ct);

            var response = await ReceiveAsync(udp, transactionId, timeout, ct);
            if (response is null) continue;

            var action = BinaryPrimitives.ReadInt32BigEndian(response);
            if (action == ActionError) throw new TrackerResponseException(ErrorText(response));
            if (action != ActionConnect || response.Length < 16) throw new TrackerResponseException($"unexpected connect response (action {action}, {response.Length} bytes)");
            return BinaryPrimitives.ReadInt64BigEndian(response.AsSpan(8));
        }
        return null;
    }

    private static async Task<(long Seeders, long Completed, long Leechers)?> ScrapeAsync(UdpClient udp, long connectionId, byte[] infoHash, TimeSpan timeout, CancellationToken ct)
    {
        var transactionId = Random.Shared.Next();
        var request = new byte[16 + 20];
        BinaryPrimitives.WriteInt64BigEndian(request.AsSpan(0), connectionId);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(8), ActionScrape);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(12), transactionId);
        infoHash.AsSpan(0, 20).CopyTo(request.AsSpan(16));
        await udp.SendAsync(request, ct);

        var response = await ReceiveAsync(udp, transactionId, timeout, ct);
        if (response is null) return null;

        var action = BinaryPrimitives.ReadInt32BigEndian(response);
        if (action == ActionError) throw new TrackerResponseException(ErrorText(response));
        if (action != ActionScrape) throw new TrackerResponseException($"unexpected scrape response (action {action})");
        if (response.Length < 20) throw new TrackerResponseException("scrape response contains no entry for the hash");

        return (
            BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(8)),
            BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(12)),
            BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(16)));
    }

    /// <summary>Waits for a datagram carrying the expected transaction id. Returns null on timeout; rethrows on real cancellation.</summary>
    private static async Task<byte[]?> ReceiveAsync(UdpClient udp, int transactionId, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }

            if (result.Buffer.Length >= 8 && BinaryPrimitives.ReadInt32BigEndian(result.Buffer.AsSpan(4)) == transactionId)
                return result.Buffer;
        }
    }

    private static string ErrorText(byte[] response)
    {
        var text = response.Length > 8 ? Encoding.UTF8.GetString(response, 8, response.Length - 8).TrimEnd('\0') : string.Empty;
        return text.Length > 0 ? $"tracker error: {text}" : "tracker returned an error with no message";
    }
}

internal static class SocketErrors
{
    public static string Describe(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.HostNotFound => "dns: host not found",
        SocketError.NoData => "dns: no address records",
        SocketError.TryAgain => "dns: temporary failure",
        SocketError.ConnectionRefused => "connection refused",
        SocketError.ConnectionReset => "connection reset (port unreachable?)",
        SocketError.TimedOut => "timed out",
        SocketError.HostUnreachable => "host unreachable",
        SocketError.NetworkUnreachable => "network unreachable",
        SocketError.AddressFamilyNotSupported => "address family not supported on this machine",
        _ => $"socket error: {ex.SocketErrorCode}",
    };
}
