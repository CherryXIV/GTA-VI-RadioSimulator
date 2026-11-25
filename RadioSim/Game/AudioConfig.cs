namespace RadioSim.Game;

/// <summary>
/// The audio config: the zlib pages before the first AWC bank in audio.rpf, joined. Inside it:
/// <list type="bullet">
/// <item>game objects: one resource with a (name hash → offset) index. After the index come a few
/// offset lists and a reference table of (name hash, type) pairs: an object number N used anywhere
/// in the data means entry N of that table. Type 4 objects live in this resource, type 2 in the
/// sound resource.</item>
/// <item>sounds: a second resource with its own index and reference table. Its records carry
/// hash_b, the AWC stream name, whose low 29 bits are the stream ID.</item>
/// </list>
/// </summary>
public sealed class AudioConfig
{
    static readonly byte[] Footer = { 4, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 };
    static readonly HashSet<uint> Types = new() { 1, 2, 4, 32, 64, 66, 256 };

    public readonly byte[] B;
    public readonly Dictionary<uint, int> GOff = new();     // in index order
    public readonly List<uint> GKeys = new();
    readonly Dictionary<int, int> gend;
    public readonly int GBase;
    public readonly List<(uint hash, uint type)> GRef;
    readonly Dictionary<uint, int> soff = new();
    readonly List<(uint hash, uint type)> sref;
    readonly int sbase;

    public AudioConfig(byte[] blob)
    {
        B = blob;
        // ---- game-object resource: the footer index with the most entries
        int ia = -1; uint best = 0;
        foreach (int i in Bin.FindAll(B, Footer))
        {
            if (i + 20 > B.Length) continue;
            uint n0 = Bin.U32(B, i + 16);
            if (n0 < 2000000 && (ia < 0 || n0 > best)) { ia = i; best = n0; }
        }
        if (ia < 0) throw new InvalidDataException("game-object index not found in the audio config");
        int n = (int)best;
        for (int k = 0; k < n; k++)
        {
            uint key = Bin.U32(B, ia + 20 + 8 * k);
            int off = (int)Bin.U32(B, ia + 24 + 8 * k);
            if (!GOff.ContainsKey(key)) GKeys.Add(key);
            GOff[key] = off;
        }
        GBase = -1;
        for (int st = ia - 8; st > Math.Max(0, ia - (64 << 20)); st--)
            if (Bin.U32(B, st + 4) == (uint)(ia - st - 8)) { GBase = st + 8; break; }
        if (GBase < 0) throw new InvalidDataException("game-object resource header not found");
        gend = Ends(GOff.Values, ia - GBase);
        GRef = RefTable(ia + 20 + 8 * n);
        // ---- sound resource: the index holding the type-2 hashes of the game reference table
        var t2 = GRef.Where(r => r.type == 2).Select(r => r.hash).ToList();
        var (sp, sn) = FindBucketIndex(t2[t2.Count / 2]);
        for (int k = 0; k < sn; k++)
            soff[Bin.U32(B, sp + 8 * k)] = (int)Bin.U32(B, sp + 8 * k + 4);
        sref = RefTable(sp + 8 * sn);
        sbase = SoundBase();
    }

    /// <summary>Each offset's end: the next larger offset, the last one ends at <paramref name="last"/>.</summary>
    public static Dictionary<int, int> Ends(IEnumerable<int> offsets, int last)
    {
        var s = offsets.Distinct().OrderBy(x => x).ToList();
        var e = new Dictionary<int, int>();
        for (int i = 0; i < s.Count; i++) e[s[i]] = i + 1 < s.Count ? s[i + 1] : last;
        return e;
    }

    /// <summary>Skip the offset lists after an index; return the (hash, type) reference table.</summary>
    List<(uint, uint)> RefTable(int p)
    {
        for (int r = 0; r < 16; r++)
        {
            int n = (int)Bin.U32(B, p);
            bool ok = n > 0;
            for (int k = 0; ok && k < Math.Min(n, 16); k++)
                ok = p + 12 + 8 * k <= B.Length && Types.Contains(Bin.U32(B, p + 8 + 8 * k));
            if (ok)
            {
                var t = new List<(uint, uint)>(n);
                for (int k = 0; k < n; k++) t.Add((Bin.U32(B, p + 4 + 8 * k), Bin.U32(B, p + 8 + 8 * k)));
                return t;
            }
            p += 4 + 4 * n;
            if (p < 0 || p + 4 > B.Length) break;
        }
        throw new InvalidDataException("reference table not found in the audio config");
    }

    /// <summary>Bounds of the (hash, offset) index, ordered by the hash's low byte, holding key.</summary>
    (int p, int n) FindBucketIndex(uint key)
    {
        bool Ok(int q) => q >= 0 && q + 8 <= B.Length && Bin.U32(B, q + 4) is > 0 and < 0x4000000;
        var pk = BitConverter.GetBytes(key);
        foreach (int i in Bin.FindAll(B, pk))
        {
            if (!(Ok(i) && Ok(i - 8) && Ok(i + 8) && B[i - 8] <= B[i] && B[i] <= B[i + 8])) continue;
            int p = i;
            while (Ok(p - 8) && B[p - 8] <= B[p]) p -= 8;
            int q = i;
            while (Ok(q + 8) && B[q + 8] >= B[q]) q += 8;
            int n = (q + 8 - p) / 8;
            if (Bin.U32(B, p - 4) == n) return (p, n);
        }
        throw new InvalidDataException("sound index not found in the audio config");
    }

    /// <summary>Music records carry their own number; its reference-table name gives the record's
    /// index offset, which fixes where the sound data starts.</summary>
    int SoundBase()
    {
        var votes = new Dictionary<int, int>(); var order = new List<int>();
        int total = 0;
        byte[] pat = { 0x08, 0x0b, 0x00, 0x01, 0x02 };
        int i = 0;
        while (true)
        {
            int k = B.AsSpan(i).IndexOf(pat);
            if (k < 0) break;
            int p = i + k; i = p + pat.Length;                // non-overlapping, like re.finditer
            if (p + 25 > B.Length || B[p + 19] != 0x0c || B[p + 20] != 0x01) continue;
            uint n = Bin.U32(B, p + 21);
            if (n < sref.Count && soff.TryGetValue(sref[(int)n].hash, out var off))
            {
                if (!votes.ContainsKey(p - off)) { votes[p - off] = 0; order.Add(p - off); }
                votes[p - off]++; total++;
            }
            if (total > 400) break;
        }
        if (order.Count == 0) throw new InvalidDataException("sound data start not found in the audio config");
        return order.OrderByDescending(x => votes[x]).First();   // stable: first seen wins ties
    }

    // ---- object access

    /// <summary>Game object number <paramref name="n"/> (a type-4 entry of the reference table).</summary>
    public byte[]? Obj(long n)
    {
        if (n < 0 || n >= GRef.Count || GRef[(int)n].type != 4) return null;
        return ObjByKey(GRef[(int)n].hash);
    }

    public byte[]? ObjByKey(uint key)
    {
        if (!GOff.TryGetValue(key, out var o)) return null;
        int a = GBase + o, e = GBase + gend[o];
        if (a < 0 || e > B.Length || e < a) return null;
        return B[a..e];
    }

    public uint RefHash(long n) => n >= 0 && n < GRef.Count ? GRef[(int)n].hash : 0;

    /// <summary>hash_b of the sound record that track-item reference n points to.</summary>
    public uint? StreamOf(uint n) => Record(n) is var (p, at) ? Bin.U32(B, at) : null;

    /// <summary>Length in seconds the sound record that track-item reference n points to gives its
    /// stream: the u32 milliseconds just before the record's [0c 01], in both layouts (e.g. 0xBFDB
    /// = 49.115 s for AfroBank's copy of dj_banter clip 02DE0C31, 0x33B2 = 13.234 s for Back Country's).</summary>
    public double? DurationOf(uint n) => Record(n) is var (p, _) && p >= 6 ? Bin.U32(B, p - 6) / 1000.0 : null;

    /// <summary>The sound record for track-item reference n: the position of its u32 id and of its hash_b.</summary>
    (int p, int hashB)? Record(uint n)
    {
        if (n >= GRef.Count || GRef[(int)n].type != 2) return null;
        if (!soff.TryGetValue(GRef[(int)n].hash, out var o)) return null;
        // two record layouts: [0c 01][u32 id][0c 00][hash_a][hash_b] and
        // [0c 01][u32 id][0c 11 00 40 03][hash_a][hash_b]; the index points near the id
        int bs = sbase + o;
        for (int p = bs - 8; p <= bs + 8; p++)
        {
            if (p < 2 || p + 17 > B.Length) continue;
            if (B[p - 2] != 0x0c || B[p - 1] != 0x01 || B[p + 4] != 0x0c) continue;
            if (B[p + 5] == 0x00) return (p, p + 10);
            if (B[p + 5] == 0x11 && B[p + 6] == 0x00 && B[p + 7] == 0x40 && B[p + 8] == 0x03) return (p, p + 13);
        }
        return null;
    }

    /// <summary>Track-item object numbers under a category/list object.</summary>
    public List<uint> Tracks(uint n, int depth = 0, HashSet<uint>? seen = null)
    {
        seen ??= new();
        var outp = new List<uint>();
        if (!seen.Add(n) || depth > 8) return outp;
        var d = Obj(n);
        if (d == null || d.Length < 8) return outp;
        var v = Bin.Words(d);
        if (d[0] == 0x0f) return new List<uint> { n };
        if (d[0] == 0x02 && v.Length > 2)
        {
            for (long i = 3; i < Math.Min(v.Length, 3L + v[2]); i++) outp.AddRange(Tracks(v[i], depth + 1, seen));
        }
        else if (d[0] == 0x11)
        {
            int k = Array.IndexOf(v, 0xffffffffu, 2);
            if (k >= 0)
            {
                if (k + 1 < v.Length)
                    for (long i = k + 2; i < Math.Min(v.Length, k + 2L + v[k + 1]); i++) outp.AddRange(Tracks(v[i], depth + 1, seen));
            }
            else if (v.Length > 17) outp = Tracks(v[17], depth + 1, seen);
        }
        return outp;
    }

    /// <summary>The station's (count, [ref, -1, 0] * count) category list.</summary>
    public List<uint> Categories(byte[] d)
    {
        var v = Bin.Words(d, 512);
        for (int i = 3; i < Math.Min(60, v.Length); i++)
        {
            uint c = v[i];
            if (c < 1 || c > 40 || i + 3 * c >= v.Length) continue;
            bool all = true;
            for (int j = 0; all && j < c; j++)
                all = v[i + 2 + 3 * j] == 0xffffffff && (Obj(v[i + 1 + 3 * j]) is { Length: > 0 } o ? o[0] : 0) == 0x11;
            if (all) return Enumerable.Range(0, (int)c).Select(j => v[i + 1 + 3 * j]).ToList();
        }
        return new();
    }

    /// <summary>The speech voice table: blocks of [u32 count][(u32 context hash, u16 voice, u16 takes)...],
    /// separated by zero words. Returns voice → (context hash → takes), contexts in table order.</summary>
    public SortedDictionary<int, List<(uint ctx, int takes)>> VoiceTable()
    {
        var key = BitConverter.GetBytes(Hash.Partial("dj_intro"));
        int start = -1;
        foreach (int i in Bin.FindAll(B, key))
        {
            int p = i;
            while (p >= 8 && p + 6 <= B.Length)
            {
                ushort v = Bin.U16(B, p - 4), c = Bin.U16(B, p - 2);
                if (v > 0 && v < 4096 && c > 0 && c < 1000 && Bin.U16(B, p + 4) == v) p -= 8; else break;
            }
            if (p < 4) continue;
            uint n = Bin.U32(B, p - 4);
            if (n > 0 && n < 5000 && Bin.U16(B, p + 4) == 1) { start = p - 4; break; }
        }
        var voices = new SortedDictionary<int, List<(uint, int)>>();
        int q = start;
        while (start >= 0)
        {
            while (q < B.Length - 4 && Bin.U32(B, q) == 0) q += 4;
            if (q + 4 > B.Length) break;
            uint n = Bin.U32(B, q);
            if (n == 0 || n >= 5000 || q + 4 + 8 * n > B.Length) break;
            var ents = Enumerable.Range(0, (int)n).Select(k => (h: Bin.U32(B, q + 4 + 8 * k), v: Bin.U16(B, q + 8 + 8 * k), c: Bin.U16(B, q + 10 + 8 * k))).ToList();
            if (ents.Select(e => e.v).Distinct().Count() != 1 || !ents.All(e => e.c > 0 && e.c < 1000)) break;
            var list = new List<(uint, int)>();
            var seenCtx = new Dictionary<uint, int>();
            foreach (var e in ents)
            {
                if (seenCtx.TryGetValue(e.h, out var at)) list[at] = (e.h, e.c);   // dict: later wins
                else { seenCtx[e.h] = list.Count; list.Add((e.h, e.c)); }
            }
            voices[ents[0].v] = list;
            q += 4 + 8 * (int)n;
        }
        return voices;
    }
}

/// <summary>The curves resource: the footer index holding the type-256 hashes. Step tables
/// (class 6) and piecewise-linear points (class 4).</summary>
public sealed class Curves
{
    readonly byte[] b;
    readonly Dictionary<uint, int> off = new();
    readonly Dictionary<int, int> end = new();
    readonly int bas;
    public int Count => off.Count;

    public Curves(AudioConfig cfg)
    {
        b = cfg.B;
        var want = cfg.GRef.Where(r => r.type == 256).Select(r => r.hash).Distinct().Take(200).ToList();
        byte[] footer = { 4, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0 };
        foreach (int i in Bin.FindAll(b, footer))
        {
            if (i + 20 > b.Length) continue;
            uint n = Bin.U32(b, i + 16);
            if (n >= 200000 || i + 20 + 8L * n > b.Length) continue;
            var E = new Dictionary<uint, int>();
            for (int k = 0; k < n; k++) E[Bin.U32(b, i + 20 + 8 * k)] = (int)Bin.U32(b, i + 24 + 8 * k);
            if (want.Count(E.ContainsKey) <= 50) continue;
            for (int st = i - 8; st > Math.Max(0, i - (16 << 20)); st--)
                if (Bin.U32(b, st + 4) == (uint)(i - st - 8)) { bas = st + 8; break; }
            off = E;
            end = AudioConfig.Ends(E.Values, i - bas);
            break;
        }
    }

    public Curve? Get(uint? key)
    {
        if (key is not { } k || !off.TryGetValue(k, out var o)) return null;
        int a = bas + o, e = bas + end[o];
        if (a < 0 || e > b.Length || e <= a) return null;
        var d = b[a..e];
        byte cls = d[0];
        if (d.Length < 20) return new Curve { Kind = "raw" };
        double lo = Bin.F32(Bin.U32(d, 8)), hi = Bin.F32(Bin.U32(d, 12));
        int n = (int)Bin.U32(d, 16);
        if (cls == 6 && 20 + 4L * n <= d.Length)
            return new Curve { Kind = "step", Min = lo, Max = hi, Values = Enumerable.Range(0, n).Select(j => Bin.F32(Bin.U32(d, 20 + 4 * j))).ToList() };
        if (cls == 4 && 20 + 8L * n <= d.Length)
            return new Curve
            {
                Kind = "linear", Min = lo, Max = hi,
                Points = Enumerable.Range(0, n).Select(j => new[] { Bin.F32(Bin.U32(d, 20 + 8 * j)), Bin.F32(Bin.U32(d, 24 + 8 * j)) }).ToList(),
            };
        return new Curve { Kind = "raw", Min = lo, Max = hi };
    }
}
