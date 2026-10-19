namespace Dlt;

public sealed class MagnetFormatException(string message) : Exception(message);

/// <summary>A parsed magnet URI (BEP 9 / BEP 53 plus the common extension parameters).</summary>
public sealed class MagnetUri
{
    /// <summary>"magnet" or "torrent": where the data came from, used for labelling only.</summary>
    public string Kind { get; init; } = "magnet";
    public required string Original { get; init; }
    public required string Decoded { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>Number of files, known only for .torrent input.</summary>
    public int? FileCount { get; init; }
    /// <summary>20-byte SHA-1 info hash from urn:btih.</summary>
    public byte[]? InfoHashV1 { get; init; }
    /// <summary>32-byte SHA-256 info hash from urn:btmh (multihash prefix stripped).</summary>
    public byte[]? InfoHashV2 { get; init; }
    public long? ExactLength { get; init; }
    public IReadOnlyList<string> Trackers { get; init; } = [];
    public IReadOnlyList<string> WebSeeds { get; init; } = [];
    public IReadOnlyList<string> AcceptableSources { get; init; } = [];
    public IReadOnlyList<string> ExactSources { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> Peers { get; init; } = [];
    public IReadOnlyList<string> SelectOnly { get; init; } = [];
    public IReadOnlyList<string> OtherTopics { get; init; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Unknown { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The 20-byte key trackers use: the v1 hash, or the truncated v2 digest for v2-only torrents (BEP 52).</summary>
    public byte[]? TrackerInfoHash => InfoHashV1 ?? InfoHashV2?[..20];
}

public static class MagnetParser
{
    private const StringComparison Oic = StringComparison.OrdinalIgnoreCase;

    public static MagnetUri Parse(string input)
    {
        var original = input;
        var text = input.Trim().Trim('"', '\'').Trim();

        // The whole URI may have been percent-encoded (sometimes more than once, e.g. copied out of a redirect URL).
        var layers = 0;
        while (!text.StartsWith("magnet:", Oic) && layers < 5)
        {
            var unescaped = Uri.UnescapeDataString(text);
            if (unescaped == text) break;
            text = unescaped;
            layers++;
        }

        if (!text.StartsWith("magnet:", Oic))
            throw new MagnetFormatException("input does not look like a magnet URI (expected it to start with 'magnet:?')");

        var warnings = new List<string>();
        if (layers > 0) warnings.Add($"input was percent-encoded {layers} time(s) and was decoded before parsing");

        var q = text.IndexOf('?');
        var query = q < 0 ? string.Empty : text[(q + 1)..];
        if (q < 0 || query.Length == 0) warnings.Add("magnet URI has no query parameters");

        string? displayName = null;
        byte[]? v1 = null, v2 = null;
        long? exactLength = null;
        List<string> trackers = [], webSeeds = [], acceptable = [], exact = [], keywords = [], peers = [], selectOnly = [], otherTopics = [];
        List<KeyValuePair<string, string>> unknown = [];

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var rawKey = eq < 0 ? part : part[..eq];
            var rawValue = eq < 0 ? string.Empty : part[(eq + 1)..];
            var key = NormaliseKey(rawKey);

            // '+' is only conventionally a space in the free-text parameters.
            var value = key is "dn" or "kt"
                ? Uri.UnescapeDataString(rawValue.Replace('+', ' '))
                : Uri.UnescapeDataString(rawValue);

            switch (key)
            {
                case "xt":
                    ParseTopic(value, ref v1, ref v2, otherTopics, warnings);
                    break;
                case "dn":
                    displayName = value;
                    break;
                case "tr":
                    if (value.Length == 0) warnings.Add("empty tracker entry ignored");
                    else if (trackers.Contains(value, StringComparer.OrdinalIgnoreCase)) warnings.Add($"duplicate tracker ignored: {value}");
                    else trackers.Add(value);
                    break;
                case "xl":
                    if (long.TryParse(value, out var n) && n >= 0) exactLength = n;
                    else warnings.Add($"xl is not a valid length: '{value}'");
                    break;
                case "ws": webSeeds.Add(value); break;
                case "as": acceptable.Add(value); break;
                case "xs": exact.Add(value); break;
                case "kt": keywords.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)); break;
                case "x.pe": peers.Add(value); break;
                case "so": selectOnly.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                default: unknown.Add(new(rawKey, value)); break;
            }
        }

        if (v1 is null && v2 is null)
            warnings.Add(otherTopics.Count > 0
                ? "no BitTorrent info hash (urn:btih / urn:btmh) present, so trackers cannot be probed"
                : "no exact topic (xt) present, so trackers cannot be probed");
        if (trackers.Count == 0)
            warnings.Add("no trackers listed (tr); peers would have to come from DHT / PEX");

        return new MagnetUri
        {
            Original = original,
            Decoded = text,
            DisplayName = displayName,
            InfoHashV1 = v1,
            InfoHashV2 = v2,
            ExactLength = exactLength,
            Trackers = trackers,
            WebSeeds = webSeeds,
            AcceptableSources = acceptable,
            ExactSources = exact,
            Keywords = keywords,
            Peers = peers,
            SelectOnly = selectOnly,
            OtherTopics = otherTopics,
            Unknown = unknown,
            Warnings = warnings,
        };
    }

    /// <summary>Strips the optional ".N" group suffix (e.g. "tr.1" -> "tr") and lower-cases the key.</summary>
    private static string NormaliseKey(string key)
    {
        var dot = key.LastIndexOf('.');
        if (dot > 0 && dot < key.Length - 1 && key[(dot + 1)..].All(char.IsAsciiDigit))
            key = key[..dot];
        return key.ToLowerInvariant();
    }

    private static void ParseTopic(string value, ref byte[]? v1, ref byte[]? v2, List<string> other, List<string> warnings)
    {
        if (value.StartsWith("urn:btih:", Oic))
        {
            var hash = DecodeV1(value[9..]);
            if (hash is null) warnings.Add($"urn:btih value is neither 40 hex nor 32 base32 characters: '{value[9..]}'");
            else if (v1 is not null && !hash.AsSpan().SequenceEqual(v1)) warnings.Add("multiple differing urn:btih hashes; using the first");
            else v1 ??= hash;
        }
        else if (value.StartsWith("urn:btmh:", Oic))
        {
            var hex = value[9..];
            // Multihash: 0x12 (sha2-256), 0x20 (32 bytes), then the digest.
            if (hex.Length == 68 && hex.StartsWith("1220", Oic) && TryHex(hex[4..], out var digest))
            {
                if (v2 is not null && !digest.AsSpan().SequenceEqual(v2)) warnings.Add("multiple differing urn:btmh hashes; using the first");
                else v2 ??= digest;
            }
            else warnings.Add($"urn:btmh value is not a sha-256 multihash (expected '1220' + 64 hex): '{hex}'");
        }
        else
        {
            other.Add(value);
        }
    }

    private static byte[]? DecodeV1(string s)
    {
        if (s.Length == 40 && TryHex(s, out var hex)) return hex;
        if (s.Length == 32) return Base32Decode(s) is { Length: 20 } b32 ? b32 : null;
        return null;
    }

    private static bool TryHex(string s, out byte[] bytes)
    {
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { bytes = []; return false; }
    }

    private static byte[]? Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.ToUpperInvariant().TrimEnd('=');
        var output = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = alphabet.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }
        return output.ToArray();
    }
}
