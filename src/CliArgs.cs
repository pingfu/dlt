namespace Dlt;

/// <summary>Parsed command line: one input (magnet, path or URL) plus flags. Help is true for -h, no input, or anything unrecognised.</summary>
internal sealed record CliArgs(string? Input, bool Select, bool Seed, bool Dht, string? OutDir, bool Help)
{
    public static CliArgs Parse(string[] args)
    {
        string? input = null, outDir = null;
        bool select = false, seed = false, dht = true;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--select-tracker": select = true; break;
                case "--seed": seed = true; break;
                case "--no-dht": dht = false; break;
                case "--out" or "-o":
                    if (i + 1 >= args.Length || outDir is not null) return Invalid();
                    outDir = Trim(args[++i]);
                    break;
                case var s when s.StartsWith("--out=", StringComparison.Ordinal):
                    if (outDir is not null) return Invalid();
                    outDir = Trim(s["--out=".Length..]);
                    break;
                case "-h" or "--help": return Invalid();
                default:
                    if (arg.StartsWith('-') || input is not null) return Invalid();
                    input = Trim(arg);
                    break;
            }
        }
        return input is null ? Invalid() : new(input, select, seed, dht, outDir, Help: false);

        CliArgs Invalid() => new(null, select, seed, dht, outDir, Help: true);
        static string Trim(string s) => s.Trim().Trim('"', '\'');
    }
}
