using System.Text;

namespace Dlt;

/// <summary>
/// Minimal bencode decoder: integers become long, strings become byte[], lists become List&lt;object&gt;,
/// dictionaries become Dictionary&lt;string, object&gt; with keys decoded as Latin-1 so binary keys survive.
/// </summary>
public static class Bencode
{
    public static object Decode(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        return Parse(data, ref pos);
    }

    public static Dictionary<string, object> DecodeDictionary(ReadOnlySpan<byte> data)
        => Decode(data) as Dictionary<string, object> ?? throw new FormatException("top-level bencode value is not a dictionary");

    private static object Parse(ReadOnlySpan<byte> d, ref int i)
    {
        if (i >= d.Length) throw new FormatException("unexpected end of bencoded data");
        switch (d[i])
        {
            case (byte)'i':
            {
                i++;
                var end = d[i..].IndexOf((byte)'e');
                if (end < 0) throw new FormatException("unterminated integer");
                var n = long.Parse(Encoding.ASCII.GetString(d.Slice(i, end)));
                i += end + 1;
                return n;
            }
            case (byte)'l':
            {
                i++;
                var list = new List<object>();
                while (i < d.Length && d[i] != (byte)'e') list.Add(Parse(d, ref i));
                if (i >= d.Length) throw new FormatException("unterminated list");
                i++;
                return list;
            }
            case (byte)'d':
            {
                i++;
                var dict = new Dictionary<string, object>();
                while (i < d.Length && d[i] != (byte)'e')
                {
                    var key = Parse(d, ref i) as byte[] ?? throw new FormatException("dictionary key is not a string");
                    dict[Encoding.Latin1.GetString(key)] = Parse(d, ref i);
                }
                if (i >= d.Length) throw new FormatException("unterminated dictionary");
                i++;
                return dict;
            }
            case >= (byte)'0' and <= (byte)'9':
            {
                var colon = d[i..].IndexOf((byte)':');
                if (colon < 0) throw new FormatException("malformed string length");
                var len = int.Parse(Encoding.ASCII.GetString(d.Slice(i, colon)));
                i += colon + 1;
                if (len < 0 || i + len > d.Length) throw new FormatException("string length exceeds data");
                var s = d.Slice(i, len).ToArray();
                i += len;
                return s;
            }
            default:
                throw new FormatException($"unexpected byte 0x{d[i]:x2} at offset {i}");
        }
    }

    public static long? GetInt(Dictionary<string, object> dict, string key)
        => dict.TryGetValue(key, out var v) && v is long n ? n : null;

    public static string? GetString(Dictionary<string, object> dict, string key)
        => dict.TryGetValue(key, out var v) && v is byte[] b ? Encoding.UTF8.GetString(b) : null;
}
