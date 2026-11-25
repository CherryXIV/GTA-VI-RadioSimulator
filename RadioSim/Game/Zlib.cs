using System.IO.Compression;

namespace RadioSim.Game;

/// <summary>
/// Inflates zlib streams found by signature in game archives (the payloads are compressed, not
/// encrypted). zlib streams end with the Adler-32 of their output, which is how the number of
/// input bytes a stream used is found, so scanning can resume right after it.
/// </summary>
public static unsafe class Zlib
{
    public static bool IsHead(byte a, byte b) => a == 0x78 && (b is 0x01 or 0x5E or 0x9C or 0xDA);

    /// <summary>Index of the next zlib header at or after <paramref name="from"/>, or -1.</summary>
    public static long NextHead(byte* p, long len, long from)
    {
        for (long i = from; i < len - 1;)
        {
            int span = (int)Math.Min(int.MaxValue, len - 1 - i);
            int k = new ReadOnlySpan<byte>(p + i, span).IndexOf((byte)0x78);
            if (k < 0) return -1;
            i += k;
            if (IsHead(p[i], p[i + 1])) return i;
            i++;
        }
        return -1;
    }

    sealed class Tracking(UnmanagedMemoryStream inner) : Stream
    {
        public long LastReadStart;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) { LastReadStart = inner.Position; return inner.Read(buffer); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Inflate the zlib stream at <paramref name="p"/>. Returns null when it is not valid
    /// zlib. <paramref name="eof"/> says whether the stream ended within <paramref name="avail"/>
    /// bytes (and output stayed within <paramref name="maxOut"/>); <paramref name="consumed"/> is
    /// then its compressed length.</summary>
    public static byte[]? Inflate(byte* p, long avail, long maxOut, out bool eof, out long consumed)
    {
        eof = false; consumed = 0;
        using var ums = new UnmanagedMemoryStream(p, avail);
        var src = new Tracking(ums);
        var outp = new MemoryStream();
        var buf = new byte[1 << 16];
        try
        {
            using var z = new ZLibStream(src, CompressionMode.Decompress);
            int n;
            while ((n = z.Read(buf)) > 0)
            {
                long room = maxOut - outp.Length;
                outp.Write(buf, 0, (int)Math.Min(n, room));
                if (n > room) return outp.ToArray();        // more than maxOut: not at eof
            }
        }
        catch (InvalidDataException) { return null; }
        var data = outp.ToArray();
        // the stream's last 4 bytes are the big-endian Adler-32 of its output; it ends inside the
        // last block read from the source
        uint ad = Adler32(data);
        Span<byte> tail = stackalloc byte[] { (byte)(ad >> 24), (byte)(ad >> 16), (byte)(ad >> 8), (byte)ad };
        long hi = src.Position;
        foreach (long lo in new[] { Math.Max(2, src.LastReadStart - 4), 2 })
        {
            if (lo >= hi) continue;
            var win = new ReadOnlySpan<byte>(p + lo, (int)Math.Min(int.MaxValue, hi - lo));
            int k = win.IndexOf(tail);
            if (k >= 0) { eof = true; consumed = lo + k + 4; return data; }
        }
        return data;                                           // ran out of input before the end
    }

    public static uint Adler32(ReadOnlySpan<byte> d)
    {
        uint a = 1, b = 0;
        while (d.Length > 0)
        {
            int n = Math.Min(d.Length, 5552);
            foreach (var x in d[..n]) { a += x; b += a; }
            a %= 65521; b %= 65521;
            d = d[n..];
        }
        return (b << 16) | a;
    }

    static long TryAt(byte[] region, long pos, long maxOut, out byte[]? d, out bool eof, out long used)
    {
        d = null; eof = false; used = 0;
        fixed (byte* p = region)
        {
            long idx = NextHead(p, region.Length, pos);
            if (idx < 0 || idx >= region.Length - 8) return -1;
            d = Inflate(p + idx, region.Length - idx, maxOut, out eof, out used);
            return idx;
        }
    }

    /// <summary>Every complete stream in a region that inflates to at least <paramref name="minOut"/>
    /// bytes, without overlaps: (offset in the region, data).</summary>
    public static IEnumerable<(long off, byte[] data)> Carve(byte[] region, int minOut = 256, long maxOut = 512L << 20)
    {
        long pos = 0;
        while (true)
        {
            long idx = TryAt(region, pos, maxOut, out var d, out bool eof, out long used);
            if (idx < 0) yield break;
            if (d == null || !eof || d.Length < minOut) { pos = idx + 1; continue; }
            yield return (idx, d);
            pos = idx + Math.Max(2, used);
        }
    }
}
