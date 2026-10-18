using Magstat;
using Magstat.Trackers;
using Spectre.Console;

if (args.Length != 1)
{
    AnsiConsole.MarkupLine("usage: magstat \"magnet:?xt=urn:btih:...&dn=...&tr=...\"");
    AnsiConsole.MarkupLine("       magstat file.torrent");
    AnsiConsole.MarkupLine("       magstat https://host/path/file.torrent");
    AnsiConsole.MarkupLine("[grey]quote a magnet URI so the shell does not split it on '&'[/]");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C exits immediately: no draining probes, no graceful tracker goodbye. Spectre hides the cursor
    // during live rendering, so put it back before leaving; 130 is the conventional SIGINT exit code.
    e.Cancel = true;
    AnsiConsole.Cursor.Show();
    Console.Out.WriteLine();
    Console.Out.Flush();
    Environment.Exit(130);
};

MagnetUri magnet;
try
{
    var input = args[0].Trim().Trim('"', '\'');
    magnet = File.Exists(input) || TorrentInput.IsHttpUrl(input)
        ? await TorrentInput.LoadAsync(input, cts.Token)
        : MagnetParser.Parse(input);
}
catch (MagnetFormatException ex)
{
    AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
    return 2;
}
catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
{
    AnsiConsole.MarkupLine($"[red]error:[/] could not read the torrent: {Markup.Escape(ex.Message)}");
    return 2;
}

AnsiConsole.WriteLine();
Render.MagnetInfo(magnet, listTrackers: magnet.TrackerInfoHash is null);

if (magnet.Trackers.Count == 0)
{
    AnsiConsole.MarkupLine($"  [yellow]nothing to probe: the {magnet.Kind} lists no trackers[/]");
    return 1;
}
if (magnet.TrackerInfoHash is null)
{
    AnsiConsole.MarkupLine($"  [yellow]nothing to probe: the {magnet.Kind} carries no BitTorrent info hash[/]");
    return 1;
}

var probes = magnet.Trackers.Select((url, i) => new TrackerProbe(i + 1, url)).ToList();
using var checker = new TrackerChecker(TimeSpan.FromSeconds(5));

await Render.ProbeLiveAsync(probes, checker, magnet.TrackerInfoHash, cts.Token);
AnsiConsole.WriteLine();
Render.Summary(probes, cts.IsCancellationRequested);

var found = probes.Where(p => p.Outcome == ProbeOutcome.Found).ToList();
if (found.Count == 0 || cts.IsCancellationRequested) return 1;

// Offer a download only on a real terminal; when piped, the probe report is the whole job.
if (!AnsiConsole.Profile.Capabilities.Interactive || Console.IsOutputRedirected || Console.IsInputRedirected) return 0;

TrackerProbe? chosen;
try { chosen = await Render.ChooseTrackerAsync(probes, found[0], cts.Token); }
catch (OperationCanceledException) { return 0; }
if (chosen is null) return 0;

return await Download.RunAsync(magnet, chosen.Url, Environment.CurrentDirectory, cts.Token) ? 0 : 1;
