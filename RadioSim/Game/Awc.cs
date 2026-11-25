using Concentus;
using Microsoft.Win32.SafeHandles;

namespace RadioSim.Game;

/// <summary>Tag lists of one stream in an AWC header: tag type → (size, offset) entries.</summary>
public sealed record AwcStream(uint Id, Dictionary<int, List<(int size, int off)>> Tags)
{
    public (int size, int off)? Tag(int t) => Tags.TryGetValue(t, out var l) && l.Count > 0 ? l[0] : null;
}

/// <summary>
/// An AWC sound bank header. Banks sit in plaintext at 4 KiB boundaries in audio.rpf. Header:
/// "ADAT", flags 0xFFxx0001, stream count, data offset; a flag-dependent preamble; one u32 per stream
/// (tag count in bits 29-31, ID in bits 0-28); then u64 tags (type in bits 56-63, size in 28-55,
/// offset in 0-27).
/// </summary>
public sealed class AwcHeader
{
    public long Base;
    public uint Flags;
    public int DataOffset;
    public List<AwcStream> Streams = new();
    public long Size;
    /// <summary>Streamed banks (music): stream 0 holds a channel table (tag 0x48) and the data is
    /// in 0x80000-byte blocks; single-stream banks hold many short sounds.</summary>
    public bool IsStreamed => Streams.Count > 0 && Streams[0].Id == 0 && Streams[0].Tags.ContainsKey(0x48);

    static readonly HashSet<int> Known = new() { 0x36, 0x48, 0x55, 0x76, 0xA3, 0xBD };

    public static bool LooksLikeBank(ReadOnlySpan<byte> d) =>
        d.Length >= 16 && d[..4].SequenceEqual("ADAT"u8) && (Bin.U32(d, 4) & 0xFF00FFFF) == 0xFF000001;

    /// <summary>The stream-info table is found by the invariant that it ends exactly at the data offset.</summary>
    public static AwcHeader Parse(ReadOnlySpan<byte> d, long bas)
    {
        uint flags = Bin.U32(d, 4), cnt = Bin.U32(d, 8);
        int doff = (int)Bin.U32(d, 12);
        int n = (int)(cnt & 0x0FFFFFFF);
        if (n == 0 || doff > d.Length || doff < 0) throw new InvalidDataException("bad AWC header");
        int pre = ((flags & 0x400000) != 0 ? 4 : 0) + ((flags & 0x10000) != 0 ? 2 * n : 0) + ((flags & 0x200000) != 0 ? 4 + 4 * n : 0);
        var cands = new List<int> { 0x10 + pre };
        for (int o = 0x10; o < Math.Min(0x10 + 0x40 + 10 * n, doff); o += 2) cands.Add(o);
        foreach (int o in cands)
        {
            if (o + 4L * n > doff) continue;
            long T = 0;
            for (int i = 0; i < n; i++) T += Bin.U32(d, o + 4 * i) >> 29;
            if (o + 4L * n + 8 * T != doff) continue;
            int p = o + 4 * n;
            var S = new List<AwcStream>(n);
            for (int i = 0; i < n; i++)
            {
                uint inf = Bin.U32(d, o + 4 * i);
                var tags = new Dictionary<int, List<(int, int)>>();
                for (uint k = 0; k < inf >> 29; k++)
                {
                    ulong q = Bin.U64(d, p); p += 8;
                    int ty = (int)(q >> 56);
                    if (!tags.TryGetValue(ty, out var l)) tags[ty] = l = new();
                    l.Add(((int)((q >> 28) & 0x0FFFFFFF), (int)(q & 0x0FFFFFFF)));
                }
                S.Add(new AwcStream(inf & 0x1FFFFFFF, tags));
            }
            if (!S.Any(s => s.Tags.ContainsKey(0x55))) continue;
            bool ok = S.All(s => s.Tags.Where(kv => kv.Key is 0x55 or 0x76 or 0x48 or 0xA3)
                                       .All(kv => kv.Value.All(t => t.Item2 >= doff - 8 && (long)t.Item2 + t.Item1 < (1L << 28))));
            if (ok || o == 0x10 + pre)
            {
                long size = 0;
                foreach (var s in S)
                    foreach (var (ty, l) in s.Tags)
                        if (Known.Contains(ty))
                            foreach (var (sz, off) in l) size = Math.Max(size, (long)off + sz);
                return new AwcHeader { Base = bas, Flags = flags, DataOffset = doff, Streams = S, Size = size };
            }
        }
        throw new InvalidDataException("AWC stream table not found");
    }
}

/// <summary>One playable stream: where it is and how to play it (from the AWC header and format).</summary>
public sealed record StreamInfo(long Bank, uint Id, bool Streamed, int Channel, int Rate, long Frames, int Codec)
{
    public double Duration => Rate > 0 ? Math.Round((double)Frames / Rate, 3) : 0;
    /// <summary>Compact reference used in the schedule: bank offset and stream ID, in hex.</summary>
    public string Ref => $"{Bank:X}:{Id:X8}";
}

/// <summary>Reads AWC banks straight out of audio.rpf: scanning, stream details, decoding.</summary>
public sealed class AwcArchive : IDisposable
{
    public const int Sub = 0x800;
    public const int PreSkip = 312;             // libopus encoder lookahead at 48 kHz
    public const int CodecOpus = 0x0B;
    readonly SafeFileHandle fh;
    public readonly long Length;
    public readonly string Path;

    public AwcArchive(string path)
    {
        Path = path;
        fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(fh);
    }

    public void Dispose() => fh.Dispose();

    public byte[] Read(long off, long n)
    {
        n = Math.Max(0, Math.Min(n, Length - off));
        var b = new byte[n];
        int got = 0;
        while (got < n)
        {
            int r = RandomAccess.Read(fh, b.AsSpan(got), off + got);
            if (r <= 0) break;
            got += r;
        }
        if (got < n) Array.Resize(ref b, got);
        return b;
    }

    /// <summary>RPF9 header field f1: bytes taken by the encrypted index; payloads start after it.</summary>
    public long IndexSize() => Bin.U32(Read(0, 8), 4);

    public AwcHeader Header(long bank, byte[]? head = null)
    {
        head ??= Read(bank, 0x10000);
        int doff = head.Length >= 16 ? (int)Bin.U32(head, 12) : 0;
        if (doff > head.Length && doff < (64 << 20)) head = Read(bank, doff + 0x1000);
        return AwcHeader.Parse(head, bank);
    }

    /// <summary>Every bank from <paramref name="from"/> on. Banks follow each other closely, so after
    /// each bank the scan jumps to its end; between banks it checks every 4 KiB boundary.</summary>
    public IEnumerable<AwcHeader> Banks(long from, Action<double>? progress = null)
    {
        const int Win = 0x10000;
        long pos = (from + 0xFFF) & ~0xFFFL;
        long lastReport = 0;
        while (pos < Length)
        {
            if (progress != null && pos - lastReport > (64 << 20)) { progress((double)pos / Length); lastReport = pos; }
            var buf = Read(pos, Win);
            if (buf.Length < 16) yield break;
            long next = pos + buf.Length;
            for (int a = 0; a + 16 <= buf.Length; a += 0x1000)
            {
                if (!AwcHeader.LooksLikeBank(buf.AsSpan(a))) continue;
                AwcHeader? h = null;
                try { h = Header(pos + a, a == 0 ? buf : null); } catch (InvalidDataException) { } catch (ArgumentOutOfRangeException) { }
                if (h == null) continue;
                yield return h;
                next = (pos + a + Math.Max(h.Size, 1) + 0xFFF) & ~0xFFFL;
                break;
            }
            pos = next;
        }
        progress?.Invoke(1);
    }

    /// <summary>A streamed bank's channel table: (stream ID, samples, rate, codec) per channel.</summary>
    public List<(uint id, uint samples, int rate, int codec)> Channels(AwcHeader h, byte[]? bank = null)
    {
        var (_, o48) = h.Streams[0].Tag(0x48)!.Value;
        var t = bank != null ? bank.AsSpan(o48).ToArray() : Read(h.Base + o48, 12);
        int nch = (int)Bin.U32(t, 8);
        if (nch <= 0 || nch > 64) return new();
        if (bank == null) t = Read(h.Base + o48, 12 + 16 * nch);
        var l = new List<(uint, uint, int, int)>();
        for (int c = 0; c < nch && 12 + 16 * c + 16 <= t.Length; c++)
            l.Add((Bin.U32(t, 12 + 16 * c), Bin.U32(t, 16 + 16 * c), Bin.U16(t, 22 + 16 * c), t[24 + 16 * c]));
        return l;
    }

    /// <summary>The stream IDs a bank holds (channel hashes for streamed banks).</summary>
    public IEnumerable<uint> Ids(AwcHeader h) =>
        h.IsStreamed ? Channels(h).Select(c => c.id) : h.Streams.Where(s => s.Tags.ContainsKey(0x55)).Select(s => s.Id);

    static bool StereoToc(byte toc) => (toc & 0x04) != 0;

    /// <summary>Rate, frame count and codec of one stream. AWC sample counts of stereo Opus count both
    /// channels, so they are halved for stereo streams (whose first packet has the stereo flag).</summary>
    public StreamInfo? Info(AwcHeader h, uint id)
    {
        if (h.IsStreamed)
        {
            var ch = Channels(h);
            int c = ch.FindIndex(x => x.id == id);
            if (c < 0) return null;
            var (_, ns, sr, codec) = ch[c];
            bool stereo = codec == CodecOpus && FirstToc(h, ch.Count, c) is { } toc && StereoToc(toc);
            return new StreamInfo(h.Base, id, true, c, sr, stereo ? ns / 2 : ns, codec);
        }
        var s = h.Streams.FirstOrDefault(x => x.Id == id && x.Tags.ContainsKey(0x55));
        if (s == null) return null;
        var (dsz, dof) = s.Tag(0x55)!.Value;
        if (s.Tag(0x76) is { } f && f.size >= 0x1D)
        {
            var fm = Read(h.Base + f.off, f.size);
            if (fm.Length >= 0x1D)
            {
                uint ns = Bin.U32(fm, 0); int sr = Bin.U16(fm, 8), codec = fm[0x1C];
                bool stereo = false;
                if (codec == CodecOpus)
                {
                    var p = Read(h.Base + dof, 3);
                    stereo = p.Length == 3 && Bin.U16(p, 0) > 0 && StereoToc(p[2]);
                }
                return new StreamInfo(h.Base, id, false, 0, sr, stereo ? ns / 2 : ns, codec);
            }
        }
        // no format tag: Opus at 48 kHz, length from the packets themselves
        var pk = SinglePackets(Read(h.Base + dof, dsz), 0, dsz);
        long total = pk.Sum(OpusSamples) - PreSkip;
        return pk.Count > 0 && OpusSamples(pk[0]) > 0 ? new StreamInfo(h.Base, id, false, 0, 48000, Math.Max(0, total), CodecOpus) : null;
    }

    byte? FirstToc(AwcHeader h, int nch, int c)
    {
        var (dsz, dof) = h.Streams[0].Tag(0x55)!.Value;
        var (_, o48) = h.Streams[0].Tag(0x48)!.Value;
        var t = Read(h.Base + o48, 8);
        uint nb = Bin.U32(t, 0), bs = Bin.U32(t, 4);
        for (long b = 0; b < Math.Min(nb, 4u); b++)
        {
            long bb = dof + b * bs;
            if (bb >= dof + dsz) break;
            var hd = Read(h.Base + bb, 24 * nch);
            if (hd.Length < 24 * nch) return null;
            var ents = Enumerable.Range(0, nch).Select(k => Bin.U32(hd, 24 * k + 4)).ToArray();
            if (ents[c] == 0) continue;
            long p = bb + ((24 * nch + 4 * ents.Sum(x => (long)x) + Sub - 1) & ~(long)(Sub - 1));
            p += Sub * ents.Take(c).Sum(x => (long)x);
            var pk = Read(h.Base + p, 3);
            return pk.Length == 3 && Bin.U16(pk, 0) > 0 ? pk[2] : null;
        }
        return null;
    }

    /// <summary>0xBD marker events of a streamed bank: (type hash, param, sample, flags).</summary>
    public List<(uint type, uint param, uint sample)> Events(AwcHeader h)
    {
        var ev = new List<(uint, uint, uint)>();
        foreach (var s in h.Streams)
            if (s.Tags.TryGetValue(0xBD, out var l))
                foreach (var (sz, off) in l)
                {
                    var d = Read(h.Base + off, sz);
                    for (int i = 0; i + 16 <= d.Length; i += 16)
                        ev.Add((Bin.U32(d, i), Bin.U32(d, i + 4), Bin.U32(d, i + 8)));
                }
        return ev;
    }

    // ---- packets and decoding

    /// <summary>A 0x800 block: [u16 length][Opus packet]... until a zero length.</summary>
    static void Walk(ReadOnlySpan<byte> blk, List<byte[]> into)
    {
        int o = 0;
        while (o + 2 <= blk.Length)
        {
            int L = blk[o] | (blk[o + 1] << 8);
            if (L == 0 || o + 2 + L > blk.Length) break;
            into.Add(blk.Slice(o + 2, L).ToArray());
            o += 2 + L;
        }
    }

    static List<byte[]> SinglePackets(byte[] d, int dof, int dsz)
    {
        var pk = new List<byte[]>();
        for (int o = dof; o < Math.Min(d.Length, dof + dsz); o += Sub)
            Walk(d.AsSpan(o, Math.Min(Sub, Math.Min(d.Length, dof + dsz) - o)), pk);
        return pk;
    }

    public static int OpusSamples(byte[] pkt)
    {
        if (pkt.Length == 0) return 0;
        int toc = pkt[0], cfg = toc >> 3, c = toc & 3;
        int fs = cfg < 12 ? new[] { 480, 960, 1920, 2880 }[cfg & 3] : cfg < 16 ? new[] { 480, 960 }[cfg & 1] : new[] { 120, 240, 480, 960 }[cfg & 3];
        int nf = c == 0 ? 1 : c < 3 ? 2 : pkt.Length > 1 ? pkt[1] & 0x3F : 0;
        return fs * nf;
    }

    /// <summary>Packets, playback rate and sample count of one stream.</summary>
    (List<byte[]> pk, int rate, long frames) Packets(long bank, uint id)
    {
        var h = Header(bank);
        if (h.IsStreamed)
        {
            var d = Read(bank, h.Size);
            var ch = Channels(h, d);
            int c = ch.FindIndex(x => x.id == id);
            if (c < 0) throw new InvalidDataException($"stream {id:X8} not in bank {bank:X}");
            var (_, o48) = h.Streams[0].Tag(0x48)!.Value;
            uint nb = Bin.U32(d, o48), bs = Bin.U32(d, o48 + 4);
            int nch = ch.Count;
            var (dsz, dof) = h.Streams[0].Tag(0x55)!.Value;
            var pk = new List<byte[]>();
            for (long b = 0; b < nb; b++)
            {
                long bb = dof + b * bs;
                if (bb >= dof + dsz || bb + 24 * nch > d.Length) break;
                var ents = Enumerable.Range(0, nch).Select(k => (long)Bin.U32(d, (int)bb + 24 * k + 4)).ToArray();
                long p = bb + ((24 * nch + 4 * ents.Sum() + Sub - 1) & ~(long)(Sub - 1));
                for (int k = 0; k < nch; k++)
                    for (long e = 0; e < ents[k]; e++, p += Sub)
                        if (k == c && p < d.Length) Walk(d.AsSpan((int)p, (int)Math.Min(Sub, d.Length - p)), pk);
            }
            return (pk, ch[c].rate, Frames(ch[c].samples, pk));
        }
        var s = h.Streams.FirstOrDefault(x => x.Id == id && x.Tags.ContainsKey(0x55))
                ?? throw new InvalidDataException($"stream {id:X8} not in bank {bank:X}");
        var (sz, off) = s.Tag(0x55)!.Value;
        var pks = SinglePackets(Read(bank + off, sz), 0, sz);
        if (s.Tag(0x76) is { } f && Read(bank + f.off, f.size) is { Length: >= 0x1D } fm)
            return (pks, Bin.U16(fm, 8), Frames(Bin.U32(fm, 0), pks));
        return (pks, 48000, Math.Max(0, pks.Sum(OpusSamples) - PreSkip));
    }

    static long Frames(long ns, List<byte[]> pk)
    {
        long total = pk.Sum(OpusSamples);
        bool stereo = pk.Count > 0 && pk[0].Length > 0 && StereoToc(pk[0][0]);
        return stereo && ns > total * 1.2 ? ns / 2 : ns;
    }

    /// <summary>Decode one stream to interleaved stereo float PCM at 48 kHz. The game feeds raw samples
    /// to the encoder, so a stream whose AWC rate is not 48000 is resampled from that rate.</summary>
    public float[] Decode(long bank, uint id)
    {
        var (pk, rate, frames) = Packets(bank, id);
        int nch = pk.Count > 0 && pk[0].Length > 0 && StereoToc(pk[0][0]) ? 2 : 1;
        var dec = OpusCodecFactory.CreateDecoder(48000, nch);
        long total = pk.Sum(x => (long)OpusSamples(x));
        var pcm = new float[(total + 5760) * 2];
        long w = 0;
        var tmp = new float[5760 * nch];
        foreach (var p in pk)
        {
            int n;
            try { n = dec.Decode(p, tmp, 5760, false); }
            catch (Exception) { n = Math.Min(5760, OpusSamples(p)); Array.Clear(tmp); }
            if (w + n * 2 > pcm.Length) Array.Resize(ref pcm, (int)(w + n * 2));
            if (nch == 2) Array.Copy(tmp, 0, pcm, w, n * 2);
            else for (int i = 0; i < n; i++) pcm[w + 2 * i] = pcm[w + 2 * i + 1] = tmp[i] * 0.70710678f;   // centre, -3 dB (as ffmpeg upmixes)
            w += n * 2;
        }
        long avail = Math.Max(0, w / 2 - PreSkip);
        long keep = Math.Min(frames > 0 ? frames : avail, avail);
        var outp = new float[keep * 2];
        Array.Copy(pcm, PreSkip * 2, outp, 0, keep * 2);
        return rate is > 0 and not 48000 ? Resample(outp, rate, 48000) : outp;
    }

    static float[] Resample(float[] x, int from, int to)
    {
        long n = x.Length / 2, m = n * to / from;
        var y = new float[m * 2];
        for (long i = 0; i < m; i++)
        {
            double t = (double)i * from / to;
            long a = (long)t; double f = t - a;
            long b = Math.Min(a + 1, n - 1);
            for (int c = 0; c < 2; c++)
                y[2 * i + c] = (float)(x[2 * a + c] * (1 - f) + x[2 * b + c] * f);
        }
        return y;
    }
}
