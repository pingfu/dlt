using System.Buffers.Binary;
using System.Net;
using System.Text;
using Dlt.Trackers;

// Several tests swap the global Spectre console or bind loopback sockets; keep them from interleaving.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Dlt.Tests;

/// <summary>Builds byte sequences from a mix of Latin-1 strings and raw byte arrays, for hand-written bencode.</summary>
internal static class Ben
{
    public static byte[] Bytes(params object[] parts)
    {
        var ms = new MemoryStream();
        foreach (var part in parts)
        {
            var bytes = part switch
            {
                string s => Encoding.Latin1.GetBytes(s),
                byte[] b => b,
                _ => throw new ArgumentException($"unsupported part {part.GetType()}"),
            };
            ms.Write(bytes);
        }
        return ms.ToArray();
    }

    /// <summary>A bencoded string: length prefix, colon, bytes.</summary>
    public static byte[] Str(byte[] value) => Bytes($"{value.Length}:", value);
}

internal static class Probes
{
    /// <summary>A 20-byte hash 00 01 02 … 13, whose percent-encoding is predictable.</summary>
    public static readonly byte[] Hash = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
    public static readonly string HashParam = string.Concat(Hash.Select(b => $"%{b:X2}"));

    public static TrackerProbe Found(int index, long? seeders, long? leechers = null, string url = "udp://t:1/a")
    {
        var p = new TrackerProbe(index, url);
        p.ReportCounts(seeders, leechers, null, "scrape");
        return p;
    }

    public static TrackerProbe NotFound(int index, string url = "udp://t:1/a")
    {
        var p = new TrackerProbe(index, url);
        p.ReportCounts(0, 0, 0, "scrape");
        return p;
    }

    public static TrackerProbe Unreachable(int index, string url = "udp://t:1/a")
    {
        var p = new TrackerProbe(index, url);
        p.Finish(ProbeOutcome.Unreachable, "timeout");
        return p;
    }
}

/// <summary>HttpMessageHandler that answers from a delegate and records every request URI.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public HttpClient Client => new(this);

    public static HttpResponseMessage Response(HttpStatusCode status, byte[] body)
        => new(status) { Content = new ByteArrayContent(body) };
}

/// <summary>In-process UDP tracker on loopback. The delegate gets (action, request datagram) and returns zero or more datagrams to send back.</summary>
internal sealed class FakeUdpTracker : IDisposable
{
    private readonly System.Net.Sockets.UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly Func<int, byte[], IEnumerable<byte[]>> _respond;

    public FakeUdpTracker(Func<int, byte[], IEnumerable<byte[]>> respond)
    {
        _respond = respond;
        _ = Task.Run(LoopAsync);
    }

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public string Url => $"udp://127.0.0.1:{Port}/announce";
    public List<byte[]> Received { get; } = [];

    private async Task LoopAsync()
    {
        try
        {
            while (true)
            {
                var r = await _udp.ReceiveAsync();
                lock (Received) Received.Add(r.Buffer);
                var action = BinaryPrimitives.ReadInt32BigEndian(r.Buffer.AsSpan(8));
                foreach (var reply in _respond(action, r.Buffer))
                    await _udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
            }
        }
        catch (ObjectDisposedException) { }
        catch (System.Net.Sockets.SocketException) { }
    }

    public static int TransactionId(byte[] request) => BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(12));

    public static byte[] ConnectReply(int transactionId, long connectionId)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0), 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), transactionId);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(8), connectionId);
        return b;
    }

    public static byte[] ScrapeReply(int transactionId, uint seeders, uint completed, uint leechers)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), transactionId);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), seeders);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), completed);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), leechers);
        return b;
    }

    public static byte[] ErrorReply(int transactionId, string message)
    {
        var text = Encoding.UTF8.GetBytes(message);
        var b = new byte[8 + text.Length];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0), 3);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), transactionId);
        text.CopyTo(b.AsSpan(8));
        return b;
    }

    public void Dispose() => _udp.Dispose();
}
