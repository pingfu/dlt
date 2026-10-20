using Dlt;
using Dlt.Trackers;
using Spectre.Console;

static int Usage()
{
    AnsiConsole.MarkupLine("usage: dlt <magnet-uri | file.torrent | https://host/file.torrent> [[--out <dir>]] [[--select]] [[--seed]]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("  probes every tracker, then downloads through the one reporting the most seeds");
    AnsiConsole.MarkupLine("  [cyan]--out <dir>[/]  save into this directory, created if missing (default: current directory)");
    AnsiConsole.MarkupLine("  [cyan]--select[/]     ask which tracker to use instead of picking automatically");
    AnsiConsole.MarkupLine("  [cyan]--seed[/]       allow uploads, accept incoming peers, and keep seeding after the download completes");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[grey]quote a magnet URI so the shell does not split it on '&'[/]");
    return 2;
}

// Windows consoles default to a legacy code page, which makes Spectre fall back to ASCII borders and glyphs.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { /* no console attached */ }

var cli = CliArgs.Parse(args);
if (cli.Help || cli.Input is null) return Usage();
string input = cli.Input;
bool select = cli.Select, seed = cli.Seed;

var outDir = Path.GetFullPath(cli.OutDir ?? Environment.CurrentDirectory);
if (File.Exists(outDir))
{
    AnsiConsole.MarkupLine($"[red]error:[/] --out is a file, not a directory: {Markup.Escape(outDir)}");
    return 2;
}
try { Directory.CreateDirectory(outDir); }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
{
    AnsiConsole.MarkupLine($"[red]error:[/] cannot create --out directory {Markup.Escape(outDir)}: {Markup.Escape(ex.Message)}");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C exits immediately: no draining probes, no graceful tracker goodbye.
    // Environment.Exit is not used here: it runs the orderly runtime shutdown on the console control thread, and
    // that stalls while Spectre's live renderer is mid-write, which left Ctrl+C doing nothing during a download.
    // So: restore the cursor Spectre hid with a raw write that takes no managed locks, then terminate outright.
    e.Cancel = true;
    try
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write("\x1b[?25h\r\n"u8);
        stdout.Flush();
    }
    catch { /* nothing sensible to do on the way out */ }
    Download.RemoveCacheIfEmpty(Download.DefaultCacheDirectory); // a few empty folders; quick, and best effort
    Terminate.Now(130); // 130 is the conventional SIGINT exit code
};

MagnetUri magnet;
try
{
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
Render.Summary(probes, cts.IsCancellationRequested);

// Default: the tracker reporting the most seeds (then most leechers, then table order). --select asks instead.
var found = probes.Count(p => p.Outcome == ProbeOutcome.Found);
var best = TrackerChoice.Best(probes);
if (best is null) return 1;

TrackerProbe? chosen;
var interactive = AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected && !Console.IsInputRedirected;
if (select && interactive)
{
    chosen = await Render.ChooseTrackerAsync(probes, best, cts.Token);
    if (chosen is null) return 0;
}
else
{
    if (select) AnsiConsole.MarkupLine("  [yellow]--select needs a terminal; picking automatically[/]");
    chosen = best;
    AnsiConsole.MarkupLine($"  using tracker [grey]#[/][cyan]{best.Index}[/]  [grey]({TrackerChoice.Reason(best, found)})[/]");
    AnsiConsole.WriteLine();
}

return await Download.RunAsync(magnet, chosen.Url, outDir, seed, cts.Token) ? 0 : 1;
