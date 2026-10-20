using System.Diagnostics;
using Dlt.Trackers;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Dlt;

internal static class Render
{
    private static bool Unicode => AnsiConsole.Profile.Capabilities.Unicode;

    public static void MagnetInfo(MagnetUri m, bool listTrackers)
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        void Row(string label, string? value, string style = "white")
        {
            if (string.IsNullOrEmpty(value)) return;
            grid.AddRow(new Markup($"[grey]{Markup.Escape(label)}[/]"), new Markup($"[{style}]{Markup.Escape(value)}[/]"));
        }

        void Rows(string label, IReadOnlyList<string> values, string style = "white")
        {
            for (var i = 0; i < values.Count; i++) Row(i == 0 ? label : string.Empty, values[i], style);
        }

        if (m.DisplayName is { Length: > 0 }) Row("Name", m.DisplayName, "bold white");
        else grid.AddRow(new Markup("[grey]Name[/]"), new Markup("[grey italic](none given)[/]"));

        if (m.InfoHashV1 is not null) Row("Info hash v1", Convert.ToHexString(m.InfoHashV1).ToLowerInvariant(), "cyan");
        if (m.InfoHashV2 is not null) Row("Info hash v2", Convert.ToHexString(m.InfoHashV2).ToLowerInvariant(), "cyan");
        if (m.InfoHashV1 is null && m.InfoHashV2 is not null) Row("Tracker key", Convert.ToHexString(m.TrackerInfoHash!).ToLowerInvariant() + "  (v2 truncated to 20 bytes)", "grey");
        if (m.ExactLength is { } size) Row("Size", $"{HumanSize(size)}  ({size:N0} bytes)");
        if (m.FileCount is { } files) Row("Files", files.ToString("N0"));

        if (m.Trackers.Count > 0)
        {
            var byScheme = m.Trackers
                .GroupBy(t => Uri.TryCreate(t, UriKind.Absolute, out var u) ? u.Scheme.ToLowerInvariant() : "invalid")
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}");
            Row("Trackers", $"{m.Trackers.Count}  ({string.Join(", ", byScheme)})" + (listTrackers ? string.Empty : "  — probed below"));
            if (listTrackers) Rows(string.Empty, m.Trackers, "grey");
        }

        Rows("Web seeds", m.WebSeeds);
        Rows("Exact sources", m.ExactSources);
        Rows("Acceptable sources", m.AcceptableSources);
        if (m.Keywords.Count > 0) Row("Keywords", string.Join(", ", m.Keywords));
        Rows("Peers (x.pe)", m.Peers);
        if (m.SelectOnly.Count > 0) Row("Select only", string.Join(", ", m.SelectOnly));
        Rows("Other topics", m.OtherTopics, "grey");
        Rows("Unknown params", m.Unknown.Select(kv => $"{kv.Key} = {kv.Value}").ToList(), "grey");

        AnsiConsole.Write(new Panel(grid)
            .Header($" {m.Kind} ", Justify.Left)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Padding(1, 0));

        foreach (var warning in m.Warnings)
            AnsiConsole.MarkupLine($"  [yellow]warning:[/] [yellow]{Markup.Escape(warning)}[/]");
        AnsiConsole.WriteLine();
    }

    public static async Task ProbeLiveAsync(IReadOnlyList<TrackerProbe> probes, TrackerChecker checker, byte[] infoHash, CancellationToken ct)
    {
        // Fixed widths stop Spectre from wrapping the short columns when the terminal is narrow; the tracker
        // column takes about half of what is left and the detail column absorbs the remainder.
        const int indexWidth = 3, statusWidth = 13, countWidth = 7, timeWidth = 8; // "10,123ms"
        const int fixedWidths = indexWidth + statusWidth + countWidth * 3 + timeWidth;
        const int chrome = 8 * 3 + 1; // 8 columns × (2 padding + 1 border) + closing border
        var remaining = Math.Max(30, AnsiConsole.Profile.Width - fixedWidths - chrome);
        var longestUrl = probes.Max(p => p.Url.Length);
        var trackerWidth = Math.Min(longestUrl, Math.Max(24, remaining * 55 / 100));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[grey]#[/]").RightAligned().NoWrap().Width(indexWidth))
            .AddColumn(new TableColumn("Tracker").NoWrap().Width(trackerWidth))
            .AddColumn(new TableColumn("Status").NoWrap().Width(statusWidth))
            .AddColumn(new TableColumn("Seeds").RightAligned().NoWrap().Width(countWidth))
            .AddColumn(new TableColumn("Leech").RightAligned().NoWrap().Width(countWidth))
            .AddColumn(new TableColumn("Done").RightAligned().NoWrap().Width(countWidth))
            .AddColumn(new TableColumn("Time").RightAligned().NoWrap().Width(timeWidth))
            .AddColumn(new TableColumn("Detail"));

        foreach (var p in probes)
            table.AddRow(
                new Markup($"[grey]{p.Index}[/]"),
                new Text(p.Url, new Style(Color.White)).Overflow(Overflow.Ellipsis),
                Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty, Text.Empty);

        var spinner = Unicode ? Spinner.Known.Dots : Spinner.Known.Line;
        var clock = Stopwatch.StartNew();

        void Refresh()
        {
            var frame = spinner.Frames[(int)(clock.ElapsedMilliseconds / spinner.Interval.TotalMilliseconds) % spinner.Frames.Count];
            for (var i = 0; i < probes.Count; i++)
            {
                var p = probes[i];
                table.UpdateCell(i, 2, StatusCell(p, frame));
                table.UpdateCell(i, 3, Count(p.Seeders, "green"));
                table.UpdateCell(i, 4, Count(p.Leechers, "yellow"));
                table.UpdateCell(i, 5, Count(p.Completed, "grey"));
                table.UpdateCell(i, 6, new Markup(p.Stage == ProbeStage.Queued ? string.Empty : $"[grey]{p.Clock.ElapsedMilliseconds:N0}ms[/]"));
                table.UpdateCell(i, 7, DetailCell(p));
            }
        }

        var all = Task.WhenAll(probes.Select(p => checker.RunAsync(p, infoHash, ct)));

        // Live redraws need a real terminal; when piped (or without a console at all) print the final table once.
        var interactive = AnsiConsole.Profile.Capabilities.Interactive && !Console.IsOutputRedirected;
        if (!interactive)
        {
            AnsiConsole.MarkupLine($"  [grey]probing {probes.Count} tracker(s)…[/]");
            await all;
            Refresh();
            AnsiConsole.Write(table);
            return;
        }

        // The live region clears itself when done and the finished table is written once, plainly. That keeps the
        // spacing around it deterministic; a live region left in place pads with lines of its own.
        await AnsiConsole.Live(table)
            .AutoClear(true)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!all.IsCompleted)
                {
                    Refresh();
                    ctx.Refresh();
                    await Task.WhenAny(all, Task.Delay(spinner.Interval, CancellationToken.None));
                }
            });
        Refresh();
        AnsiConsole.Write(table);
    }

    public static void Summary(IReadOnlyList<TrackerProbe> probes, bool cancelled)
    {
        int Count(ProbeOutcome o) => probes.Count(p => p.Outcome == o);
        var found = Count(ProbeOutcome.Found);
        var parts = new List<string>();
        if (Count(ProbeOutcome.NotFound) is > 0 and var nf) parts.Add($"[yellow]{nf} reachable without it[/]");
        if (Count(ProbeOutcome.Unreachable) is > 0 and var ur) parts.Add($"[red]{ur} unreachable[/]");
        if (Count(ProbeOutcome.Error) is > 0 and var er) parts.Add($"[red]{er} errored[/]");
        if (Count(ProbeOutcome.Unsupported) is > 0 and var us) parts.Add($"[grey]{us} unsupported[/]");

        var headline = found > 0
            ? $"[green bold]{found}[/] of {probes.Count} trackers know this torrent"
            : $"[red bold]none[/] of the {probes.Count} trackers know this torrent";
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  {headline}" + (parts.Count > 0 ? $"  [grey]·[/]  {string.Join("  [grey]·[/]  ", parts)}" : string.Empty));
        if (cancelled) AnsiConsole.MarkupLine("  [yellow]probing was interrupted; results above are partial[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>Asks which tracker row to download through. Enter accepts the default; n, q or 0 skips. Null means skip.</summary>
    public static async Task<TrackerProbe?> ChooseTrackerAsync(IReadOnlyList<TrackerProbe> probes, TrackerProbe suggested, CancellationToken ct)
    {
        var prompt = new TextPrompt<string>($"  download using tracker [grey]#[/][cyan]{suggested.Index}[/]? [grey](number, or n to skip)[/]")
            .AllowEmpty()
            .Validate(input =>
            {
                var t = input.Trim();
                if (t.Length == 0 || t is "n" or "q" or "0") return ValidationResult.Success();
                return int.TryParse(t, out var n) && n >= 1 && n <= probes.Count
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"[red]enter a number between 1 and {probes.Count}[/]");
            });

        var answer = (await AnsiConsole.PromptAsync(prompt, ct)).Trim();
        if (answer is "n" or "q" or "0") return null;
        var chosen = answer.Length == 0 ? suggested : probes[int.Parse(answer) - 1];
        if (chosen.Outcome != ProbeOutcome.Found)
            AnsiConsole.MarkupLine($"  [yellow]tracker #{chosen.Index} did not report this torrent; trying it anyway[/]");
        AnsiConsole.WriteLine();
        return chosen;
    }

    private static IRenderable StatusCell(TrackerProbe p, string frame)
    {
        if (p.Stage != ProbeStage.Done)
        {
            var verb = p.Stage switch
            {
                ProbeStage.Queued => "queued",
                ProbeStage.Resolving => "resolving",
                ProbeStage.Connecting => "connecting",
                ProbeStage.Scraping => "scraping",
                ProbeStage.Announcing => "announcing",
                _ => "working",
            };
            return p.Stage == ProbeStage.Queued
                ? new Markup("[grey]· queued[/]")
                : new Markup($"[blue]{Markup.Escape(frame)} {verb}[/]");
        }

        return p.Outcome switch
        {
            ProbeOutcome.Found => new Markup("[green bold]found[/]"),
            ProbeOutcome.NotFound => new Markup("[yellow]not found[/]"),
            ProbeOutcome.Unreachable => new Markup("[red]unreachable[/]"),
            ProbeOutcome.Error => new Markup("[red]error[/]"),
            ProbeOutcome.Unsupported => new Markup("[grey]unsupported[/]"),
            _ => new Markup("[grey]?[/]"),
        };
    }

    private static IRenderable DetailCell(TrackerProbe p)
    {
        var bits = new List<string>();
        if (p.Stage != ProbeStage.Done)
        {
            if (p.Endpoint is not null) bits.Add($"[grey]{Markup.Escape(p.Endpoint)}[/]");
        }
        else
        {
            if (p.Endpoint is not null && p.Scheme == "udp") bits.Add($"[grey]{Markup.Escape(p.Endpoint)}[/]");
            if (p.Method is not null && p.Outcome is ProbeOutcome.Found or ProbeOutcome.NotFound) bits.Add($"[grey]{Markup.Escape(p.Method)}[/]");
            if (p.Detail is not null)
            {
                var colour = p.Outcome switch
                {
                    ProbeOutcome.Found => "grey",
                    ProbeOutcome.NotFound => "yellow",
                    ProbeOutcome.Unsupported => "grey",
                    _ => "red",
                };
                bits.Add($"[{colour}]{Markup.Escape(p.Detail)}[/]");
            }
        }
        return new Markup(string.Join(" / ", bits));
    }

    private static IRenderable Count(long? value, string colour)
        => value is null ? Text.Empty : new Markup(value > 0 ? $"[{colour}]{value:N0}[/]" : "[grey]0[/]");


    public static string HumanDuration(TimeSpan t) => t.TotalSeconds switch
    {
        < 1 => "under a second",
        < 60 => $"{t.TotalSeconds:0}s",
        < 3600 => $"{(int)t.TotalMinutes}m {t.Seconds:00}s",
        _ => $"{(int)t.TotalHours}h {t.Minutes:00}m",
    };

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
