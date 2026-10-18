using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Magstat.Trackers;

/// <summary>Dispatches a probe to the right protocol and folds every failure mode into the probe's outcome.</summary>
public sealed class TrackerChecker : IDisposable
{
    private readonly TimeSpan _timeout;
    private readonly HttpClient _http;

    public TrackerChecker(TimeSpan timeout)
    {
        _timeout = timeout;
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts are applied with cancellation tokens
            MaxResponseContentBufferSize = 1 << 20,
        };
    }

    public async Task RunAsync(TrackerProbe p, byte[] infoHash, CancellationToken ct)
    {
        p.Clock.Start();
        try
        {
            if (p.Uri is null)
            {
                p.Finish(ProbeOutcome.Unsupported, "not a valid absolute URL");
                return;
            }

            switch (p.Scheme)
            {
                case "udp":
                    await UdpTracker.ProbeAsync(p, infoHash, _timeout, ct);
                    break;
                case "http" or "https":
                    await HttpTracker.ProbeAsync(_http, p, infoHash, _timeout, ct);
                    break;
                case "ws" or "wss":
                    await WebSocketTracker.ProbeAsync(p, infoHash, _timeout, ct);
                    break;
                default:
                    p.Finish(ProbeOutcome.Unsupported, $"scheme '{p.Scheme}' is not a tracker protocol this tool speaks");
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            p.Finish(ProbeOutcome.Error, "cancelled");
        }
        catch (OperationCanceledException)
        {
            p.Finish(ProbeOutcome.Unreachable, $"timed out while {StageVerb(p.Stage)}");
        }
        catch (TrackerResponseException ex)
        {
            p.Finish(ProbeOutcome.Error, ex.Message);
        }
        catch (SocketException ex)
        {
            p.Finish(ProbeOutcome.Unreachable, SocketErrors.Describe(ex));
        }
        catch (HttpRequestException ex)
        {
            p.Finish(ProbeOutcome.Unreachable, DescribeHttp(ex));
        }
        catch (WebSocketException ex)
        {
            p.Finish(ProbeOutcome.Unreachable, ex.InnerException is HttpRequestException hre ? DescribeHttp(hre) : $"websocket: {ex.Message}");
        }
        catch (Exception ex)
        {
            p.Finish(ProbeOutcome.Error, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            p.Clock.Stop();
            p.Stage = ProbeStage.Done;
        }
    }

    private static string StageVerb(ProbeStage stage) => stage switch
    {
        ProbeStage.Resolving => "resolving the host name",
        ProbeStage.Connecting => "connecting",
        ProbeStage.Scraping => "waiting for the scrape response",
        ProbeStage.Announcing => "waiting for the announce response",
        _ => "probing",
    };

    private static string DescribeHttp(HttpRequestException ex)
    {
        var inner = ex.InnerException;
        while (inner is not null)
        {
            switch (inner)
            {
                case SocketException se: return SocketErrors.Describe(se);
                case TimeoutException: return "timed out while connecting";
                case System.Security.Authentication.AuthenticationException ae: return $"tls: {ae.Message}";
                case System.IO.IOException io when io.InnerException is null: return $"io: {io.Message}";
            }
            inner = inner.InnerException;
        }
        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "dns: host not found",
            HttpRequestError.ConnectionError => "connection failed",
            HttpRequestError.SecureConnectionError => "tls handshake failed",
            HttpRequestError.Unknown => ex.Message,
            var other => $"{other}: {ex.Message}",
        };
    }

    public void Dispose() => _http.Dispose();
}
