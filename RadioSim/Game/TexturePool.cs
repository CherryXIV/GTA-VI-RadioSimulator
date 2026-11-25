using System.IO.Compression;
using System.Text;

namespace RadioSim.Game;

/// <summary>
/// Reads textures from a TTDB texture pool (<c>data/ttdb/*.ttpf</c>): an RDS5 container whose
/// chunk table maps stored or zlib-compressed chunks into a virtual address space, plus an ETPF
/// dictionary describing each texture (size, mips, DXGI format, Gfx10 tile mode, first page).
/// A C# port of the extractor's gta6x.rds5 / gta6x.texture / gta6x.gfx10, for 2D textures only.
/// Nothing is decrypted: chunks in the console's BCPack codecs are skipped.
/// </summary>
public sealed class TexturePool : IDisposable
{
    public sealed record Texture(ulong Hash, int Width, int Height, int Mips, int Format, int TileMode,
                                 int Slices, int Dimension, long VirtualOffset);

    record struct Chunk(long Source, int Packed, long Virtual, int Size, int Codec);

    readonly FileStream fs;
    readonly Chunk[] chunks;
    readonly long[] starts;
    readonly Dictionary<ulong, Texture> byHash = new();

    public IReadOnlyDictionary<ulong, Texture> Textures => byHash;

    public TexturePool(string path)
    {
        fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> h = stackalloc byte[24];
        fs.ReadExactly(h);
        if (Encoding.ASCII.GetString(h[..4]) != "RDS5") throw new InvalidDataException($"{path}: not an RDS5 texture pool");
        int count = (int)Bin.U32(h, 4);
        if (count < 3 || count > 1 << 24) throw new InvalidDataException($"{path}: bad chunk count {count}");
        var table = new byte[(count - 1) * 8];
        fs.ReadExactly(table);
        long etpf = (24 + table.Length + 255) / 256 * 256;

        int n = count - 2;
        chunks = new Chunk[n];
        for (int i = 0; i < n; i++)
        {
            ulong v = Bin.U64(table, i * 8), nx = Bin.U64(table, i * 8 + 8);
            long start = (long)(v & 0x3FFFFFF) * 256, end = (long)(nx & 0x3FFFFFF) * 256;
            long virt = (long)(v >> 30 & 0x3FFFFF) * 4096, vend = (long)(nx >> 30 & 0x3FFFFF) * 4096;
            int pad = (int)(v >> 52), codec = (int)(v >> 26 & 0xF);
            chunks[i] = new Chunk(start, (int)(end - start - pad), virt, (int)(vend - virt), codec);
        }
        starts = chunks.Select(c => c.Virtual).ToArray();

        fs.Position = etpf;
        Span<byte> eh = stackalloc byte[16];
        fs.ReadExactly(eh);
        if (Encoding.ASCII.GetString(eh[..4]) != "ETPF") throw new InvalidDataException($"{path}: no ETPF texture dictionary");
        int tc = (int)Bin.U32(eh, 8);
        var meta = new byte[tc * 24];
        fs.ReadExactly(meta);
        for (int i = 0; i < tc; i++)
        {
            uint d0 = Bin.U32(meta, i * 16), d1 = Bin.U32(meta, i * 16 + 4);
            ulong loc = Bin.U64(meta, i * 16 + 8), hash = Bin.U64(meta, tc * 16 + i * 8);
            int nonTail = (int)(d1 & 7);
            var t = new Texture(hash, (int)(d0 & 0x3FFF) + 1, (int)(d0 >> 14 & 0x3FFF) + 1, Math.Max(1, (int)(d0 >> 28) + nonTail),
                                (int)(d1 >> 3 & 0x7F), (int)(d1 >> 24 & 0xF), (int)(d1 >> 10 & 0x7FF) + 1, (int)(d1 >> 21 & 3),
                                (long)(loc & 0x1FFFFF) * 0x1000);
            byHash.TryAdd(hash, t);
        }
    }

    /// <summary><paramref name="length"/> bytes of the virtual space, or null when part of it is
    /// in a BCPack chunk or no chunk covers it.</summary>
    public byte[]? ReadVirtual(long virt, int length)
    {
        var outp = new byte[length];
        long end = virt + length, covered = 0;
        int pos = Array.BinarySearch(starts, virt);
        if (pos < 0) pos = Math.Max(0, ~pos - 1);
        for (int i = pos; i < chunks.Length && chunks[i].Virtual < end; i++)
        {
            var c = chunks[i];
            long lo = Math.Max(virt, c.Virtual), hi = Math.Min(end, c.Virtual + c.Size);
            if (hi <= lo) continue;
            if (c.Codec is not (0 or 8)) return null;
            var raw = new byte[c.Packed];
            fs.Position = c.Source;
            fs.ReadExactly(raw);
            if (c.Codec == 8)
            {
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                var inflated = new byte[c.Size];
                z.ReadAtLeast(inflated, c.Size, throwOnEndOfStream: true);
                raw = inflated;
            }
            raw.AsSpan((int)(lo - c.Virtual), (int)(hi - lo)).CopyTo(outp.AsSpan((int)(lo - virt)));
            covered += hi - lo;
        }
        return covered == length ? outp : null;
    }

    /// <summary>Mip 0 of a 2D texture as RGBA8, or null when its format, tiling or chunks aren't supported.</summary>
    public byte[]? ReadRgba(Texture t)
    {
        int bb = Bc.BlockBytes(t.Format);
        if (bb == 0 || t.Dimension != 1 || t.TileMode is >= 9 and <= 12) return null;
        string? swz = t.TileMode switch { 0 => "D64K", 1 or 2 or 3 => "S4K", _ => null };
        if (swz == null) return null;
        int bw = (t.Width + 3) / 4, bh = (t.Height + 3) / 4;
        var lay = Gfx10Layout.Create(swz, bw, bh, t.Mips, bb);
        if (lay == null || lay.Total > 64 << 20) return null;
        var data = ReadVirtual(t.VirtualOffset, (int)(lay.Total * Math.Max(1, t.Slices)));
        if (data == null) return null;
        var linear = new byte[bw * bh * bb];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                long at = lay.Addr(0, x, y);
                if (at >= 0 && at + bb <= data.Length) data.AsSpan((int)at, bb).CopyTo(linear.AsSpan((y * bw + x) * bb));
            }
        return Bc.Decode(t.Format, linear, t.Width, t.Height);
    }

    public void Dispose() => fs.Dispose();

    // ---- texture names

    /// <summary>Texture pools store a name as the low 64 bits of MurmurHash3 x64 128 (seed 0)
    /// of its exact UTF-8 bytes.</summary>
    public static ulong NameHash(string name) => Murmur3Low64(Encoding.UTF8.GetBytes(name));

    static ulong Murmur3Low64(ReadOnlySpan<byte> d)
    {
        const ulong c1 = 0x87c37b91114253d5, c2 = 0x4cf5ad432745937f;
        ulong h1 = 0, h2 = 0;
        int nb = d.Length / 16;
        for (int i = 0; i < nb; i++)
        {
            ulong k1 = Bin.U64(d, i * 16), k2 = Bin.U64(d, i * 16 + 8);
            k1 *= c1; k1 = ulong.RotateLeft(k1, 31); k1 *= c2; h1 ^= k1;
            h1 = ulong.RotateLeft(h1, 27); h1 += h2; h1 = h1 * 5 + 0x52dce729;
            k2 *= c2; k2 = ulong.RotateLeft(k2, 33); k2 *= c1; h2 ^= k2;
            h2 = ulong.RotateLeft(h2, 31); h2 += h1; h2 = h2 * 5 + 0x38495ab5;
        }
        var tail = d[(nb * 16)..];
        ulong t1 = 0, t2 = 0;
        for (int i = tail.Length - 1; i >= 8; i--) t2 ^= (ulong)tail[i] << ((i - 8) * 8);
        for (int i = Math.Min(tail.Length, 8) - 1; i >= 0; i--) t1 ^= (ulong)tail[i] << (i * 8);
        if (tail.Length > 8) { t2 *= c2; t2 = ulong.RotateLeft(t2, 33); t2 *= c1; h2 ^= t2; }
        if (tail.Length > 0) { t1 *= c1; t1 = ulong.RotateLeft(t1, 31); t1 *= c2; h1 ^= t1; }
        h1 ^= (ulong)d.Length; h2 ^= (ulong)d.Length;
        h1 += h2; h2 += h1;
        h1 = Fmix(h1); h2 = Fmix(h2);
        h1 += h2;
        return h1;
    }

    static ulong Fmix(ulong k)
    {
        k ^= k >> 33; k *= 0xff51afd7ed558ccd; k ^= k >> 33; k *= 0xc4ceb9fe1a85ec53; k ^= k >> 33;
        return k;
    }
}

/// <summary>
/// AMD Gfx10 (Xbox Series) placement of a 2D texture's mips (port of gta6x.gfx10): a swizzle
/// mode is a 4 KiB (S4K) or 64 KiB (D64K) block with a fixed order of element x/y bits; blocks are
/// row-major, mips are stored smallest first, and small mips share one mip-tail block.
/// </summary>
sealed class Gfx10Layout
{
    // element-coordinate bit order inside a block, low bit first: 'x'/'y' + bit number
    static readonly Dictionary<(string, int), string> Equations = new()
    {
        [("S4K", 16)] = "y0 y1 x0 x1 y2 x2 y3 x3",
        [("S4K", 8)] = "x0 y0 y1 x1 x2 y2 x3 y3 x4",
        [("S4K", 4)] = "x0 x1 y0 y1 y2 x2 y3 x3 y4 x4",
        [("S4K", 2)] = "x0 x1 x2 y0 y1 y2 x3 y3 x4 y4 x5",
        [("S4K", 1)] = "x0 x1 x2 x3 y0 y1 y2 y3 y4 x4 y5 x5",
        [("D64K", 16)] = "x0 y0 x1 y1 y2 x2 y3 x3 y4 x4 y5 x5",
        [("D64K", 8)] = "x0 y0 x1 x2 y1 y2 x3 y3 x4 y4 x5 y5 x6",
        [("D64K", 4)] = "x0 x1 y0 y1 y2 x2 y3 x3 y4 x4 y5 x5 y6 x6",
        [("D64K", 2)] = "x0 x1 x2 y0 y1 y2 x3 y3 x4 y4 x5 y5 x6 y6 x7",
        [("D64K", 1)] = "x0 x1 x2 y1 y0 y2 x3 y3 y4 x4 y5 x5 y6 x6 y7 x7",
    };

    readonly (bool x, int bit)[] eq;
    readonly int bpe, blockW, blockH, blockBytes;
    readonly (int w, int h, long offset, (int x, int y)? tail)[] mips;
    public long Total { get; }

    Gfx10Layout((bool, int)[] eq, int bpe, int bw, int bh, (int, int, long, (int, int)?)[] mips, long total)
    {
        this.eq = eq; this.bpe = bpe; blockW = bw; blockH = bh; blockBytes = bw * bh * bpe;
        this.mips = mips; Total = total;
    }

    static int Ceil(int v, int b) => (v + b - 1) / b;

    public static Gfx10Layout? Create(string swizzle, int w, int h, int mipCount, int bpe)
    {
        if (!Equations.TryGetValue((swizzle, bpe), out var s) || w < 1 || h < 1 || mipCount < 1) return null;
        var eq = s.Split(' ').Select(t => (t[0] == 'x', t[1] - '0')).ToArray();
        int nx = eq.Count(e => e.Item1);
        int bw = 1 << nx, bh = 1 << (eq.Length - nx), bbytes = bw * bh * bpe;
        int log2 = System.Numerics.BitOperations.Log2((uint)bbytes);
        int maxTail = log2 <= 11 ? 1 + (1 << (log2 - 9)) : log2 - 4;
        int tailW = bw / 2, tailH = bh;
        var dims = Enumerable.Range(0, mipCount).Select(i => (w: Math.Max(1, w >> i), h: Math.Max(1, h >> i))).ToArray();
        int first = mipCount;
        for (int i = 0; i < mipCount; i++)
            if (dims[i].w <= tailW && dims[i].h <= tailH && mipCount - i <= maxTail) { first = i; break; }
        var place = dims.Select(d => (d.w, d.h, offset: 0L, tail: ((int, int)?)null)).ToArray();
        long off = first < mipCount ? bbytes : 0;
        for (int i = first - 1; i >= 0; i--)
        {
            place[i].offset = off;
            off += (long)Ceil(dims[i].w, bw) * bw * Ceil(dims[i].h, bh) * bh * bpe;
        }
        var b256 = bpe == 16 ? (4, 4) : (8, 4);
        for (int k = first; k < mipCount; k++)
        {
            int m = maxTail - 1 - (k - first);
            int mo = m > 6 ? 16 << m : m << 8, mx = 0, my = 0;
            for (int b = 0; b < 6; b++)
            {
                mx |= (mo >> (9 + 2 * b) & 1) << b;
                my |= (mo >> (8 + 2 * b) & 1) << b;
            }
            place[k].offset = 0;
            place[k].tail = (mx * b256.Item1, my * b256.Item2);
        }
        return new Gfx10Layout(eq, bpe, bw, bh, place, off);
    }

    int InBlock(int x, int y)
    {
        int a = 0;
        for (int k = 0; k < eq.Length; k++) a |= ((eq[k].x ? x : y) >> eq[k].bit & 1) << k;
        return a;
    }

    /// <summary>Byte offset of element (x, y) of mip <paramref name="m"/> (first slice).</summary>
    public long Addr(int m, int x, int y)
    {
        var p = mips[m];
        if (p.tail is { } t) return (long)InBlock(x + t.x, y + t.y) * bpe;
        int pitch = Ceil(p.w, blockW);
        long block = (long)(y / blockH) * pitch + x / blockW;
        return p.offset + block * blockBytes + (long)InBlock(x % blockW, y % blockH) * bpe;
    }
}
