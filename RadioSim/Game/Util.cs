using System.Buffers.Binary;
using System.Text;

namespace RadioSim.Game;

/// <summary>Rockstar's one-at-a-time hash (joaat) and the pieces of it the speech table uses.</summary>
public static class Hash
{
    public const uint Mask = 0x1FFFFFFF;

    /// <summary>joaat without its final mixing step; speech contexts are stored this way.</summary>
    public static uint Partial(string s, uint h = 0)
    {
        foreach (char ch in s)
        {
            if (ch > 0xFF) continue;                 // latin-1, unencodable characters dropped
            h += ch; h += h << 10; h ^= h >> 6;
        }
        return h;
    }

    public static uint Finish(uint h) { h += h << 3; h ^= h >> 11; h += h << 15; return h; }

    public static uint Joaat(string s) => Finish(Partial(s));

    /// <summary>Stream ID of take <paramref name="i"/> of a speech context: joaat("&lt;context&gt;_NN")
    /// from the context's partial hash alone, so unnamed contexts resolve too.</summary>
    public static uint TakeId(uint ctx, int i) => Finish(Partial($"_{i:00}", ctx)) & Mask;
}

static class Bin
{
    public static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    public static int I32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
    public static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    public static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    public static double F32(uint x) => BitConverter.Int32BitsToSingle((int)x);

    /// <summary>The object's data as u32 words (trailing bytes dropped).</summary>
    public static uint[] Words(ReadOnlySpan<byte> d, int maxBytes = int.MaxValue)
    {
        int n = Math.Min(d.Length, maxBytes) / 4;
        var v = new uint[n];
        for (int i = 0; i < n; i++) v[i] = U32(d, 4 * i);
        return v;
    }

    /// <summary>NUL-terminated latin-1 string in [start, end).</summary>
    public static string CStr(ReadOnlySpan<byte> d, int start, int end)
    {
        end = Math.Min(end, d.Length);
        if (start >= end) return "";
        var s = d[start..end];
        int z = s.IndexOf((byte)0);
        if (z >= 0) s = s[..z];
        return Encoding.Latin1.GetString(s);
    }

    public static IEnumerable<int> FindAll(byte[] b, ReadOnlyMemory<byte> pat, int from = 0)
    {
        int i = from;
        while (i <= b.Length - pat.Length)
        {
            int k = b.AsSpan(i).IndexOf(pat.Span);
            if (k < 0) yield break;
            yield return i + k;
            i += k + 1;
        }
    }
}

/// <summary>Python's str.title() for the [A-Z0-9_ ] track IDs: a letter is upper-cased when the
/// character before it is not a letter.</summary>
static class Text
{
    public static string Title(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevLetter = false;
        foreach (var c in s)
        {
            bool letter = char.IsLetter(c);
            sb.Append(letter ? (prevLetter ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
            prevLetter = letter;
        }
        return sb.ToString();
    }
}
