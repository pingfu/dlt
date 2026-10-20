using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using MonoTorrent.Dht;

namespace Dlt;

/// <summary>
/// Seeds the DHT routing table. MonoTorrent 3.0 bootstraps from router.bittorrent.com alone, and when that host is
/// slow or silent the DHT never gets going. Resolving a handful of well-known routers up front and handing them to the
/// engine as pending nodes makes a cold start reliable. Nothing is persisted: every run resolves afresh.
/// </summary>
internal static class DhtBootstrap
{
    /// <summary>Public DHT routers run by the major clients. Any one answering is enough to walk the network from.</summary>
    internal static readonly IReadOnlyList<(string Host, int Port)> Routers =
    [
        ("router.bittorrent.com", 6881),
        ("router.utorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("dht.aelitis.com", 6881),
    ];

    /// <summary>
    /// How long one DHT query waits for a reply. MonoTorrent's default is 15 seconds, retried four times, so each dead
    /// node a lookup touches can hold it up for a minute, and roughly half the nodes a fresh table learns about are dead.
    /// Other clients wait a few seconds. Three is enough for a reply from anywhere on the planet.
    /// </summary>
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Sets the query timeout on the engine's message loop. Both are internal to MonoTorrent, so this goes through
    /// reflection and gives up quietly (leaving the default) if a future version renames them. Returns true when set.
    /// </summary>
    internal static bool TrySetQueryTimeout(DhtEngine engine, TimeSpan timeout)
    {
        try
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var loop = typeof(DhtEngine).GetProperty("MessageLoop", any)?.GetValue(engine)
                    ?? typeof(DhtEngine).GetField("MessageLoop", any)?.GetValue(engine);
            var property = loop?.GetType().GetProperty("Timeout", any);
            if (loop is null || property is null || !property.CanWrite || property.PropertyType != typeof(TimeSpan)) return false;
            property.SetValue(loop, timeout);
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or MethodAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>What resolving the routers produced: the compact nodes to seed with, and how many of the routers answered DNS.</summary>
    internal sealed record Seed(IReadOnlyList<ReadOnlyMemory<byte>> Nodes, int RoutersResolved, int RoutersTotal)
    {
        /// <summary>For the header line: "4 of 5 public routers resolved".</summary>
        public string Describe() => RoutersResolved == 0
            ? $"none of the {RoutersTotal} public routers resolved"
            : $"{RoutersResolved} of {RoutersTotal} public routers resolved";
    }

    /// <summary>Resolves every router in parallel and returns one compact node per IPv4 address, in no particular order. Hosts that fail or time out are skipped.</summary>
    public static async Task<Seed> ResolveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var lookups = Routers.Select(async r =>
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(r.Host, System.Net.Sockets.AddressFamily.InterNetwork, cts.Token);
                return addresses.Select(a => (ReadOnlyMemory<byte>)CompactNode(a, r.Port)).ToList();
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
            {
                return [];
            }
        });
        var perRouter = await Task.WhenAll(lookups);
        return new Seed(perRouter.SelectMany(n => n).ToList(), perRouter.Count(n => n.Count > 0), Routers.Count);
    }

    /// <summary>BEP 5 compact node: 20-byte node id, 4-byte IPv4 address, 2-byte big-endian port. The id is random: a router's real id is learned from its first reply.</summary>
    internal static byte[] CompactNode(IPAddress address, int port)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("IPv4 only", nameof(address));
        var node = new byte[26];
        RandomNumberGenerator.Fill(node.AsSpan(0, 20));
        address.GetAddressBytes().CopyTo(node, 20);
        node[24] = (byte)(port >> 8);
        node[25] = (byte)port;
        return node;
    }
}
