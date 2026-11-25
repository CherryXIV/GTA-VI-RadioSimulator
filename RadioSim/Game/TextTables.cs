using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace RadioSim.Game;

/// <summary>Strings of a text table: NUL-separated byte runs in one inflated page.</summary>
public sealed class StringList
{
    readonly byte[] data;
    readonly List<int> starts = new(), lens = new();
    public StringList(byte[] d, int s0)
    {
        data = d;
        int end = d.AsSpan(Math.Min(s0, d.Length)).IndexOf("\0\0\0"u8);
        end = end < 0 ? d.Length : s0 + end;
        int a = s0;
        for (int i = s0; i < end; i++)
            if (d[i] == 0) { starts.Add(a); lens.Add(i - a); a = i + 1; }
        starts.Add(a); lens.Add(Math.Max(0, end - a));
    }
    public int Count => starts.Count;
    /// <summary>Bytes the strings cover, separators included, plus one (Python's sum(len(s)+1)).</summary>
    public int Span => lens.Sum() + lens.Count;

    static readonly Regex Tags = new(@"~fo\|[^~]*~|~fo~", RegexOptions.Compiled);
    public string Clean(int i) => Tags.Replace(Encoding.UTF8.GetString(data, starts[i], lens[i]), "").Trim();
}

/// <summary>One localized string table: strings and (label hash → string index).</summary>
public sealed record TextTable(StringList Strings, Dictionary<uint, int> Entries);

/// <summary>
/// The localized string tables in data.rpf are zlib pages that inflate to plaintext: NUL-terminated
/// strings and a hash map of 16-byte entries (u32 key, u32 0, u8 1, u16 0, u32 string index),
/// grouped 16 at a time behind 16 control bytes (0x80 = empty slot).
/// </summary>
public static class TextTables
{
    static bool IsMap(byte[] d, int g)
    {
        if (g + 272 > d.Length) return false;
        for (int i = 0; i < 16; i++) if (d[g + i] > 0x80) return false;
        for (int i = 0; i < 16; i++)
        {
            byte c = d[g + i];
            if (c == 0x80) continue;
            int e = g + 16 + i * 16;
            return (d[e] & 0x7F) == c && Bin.U32(d, e + 4) == 0 && d[e + 8] == 1;
        }
        return false;
    }

    static bool Empty(byte[] d, int g)
    {
        if (g + 16 > d.Length) return false;
        for (int i = 0; i < 16; i++) if (d[g + i] != 0x80) return false;
        return true;
    }

    static Dictionary<uint, int> ReadMap(byte[] d, int g)
    {
        var entries = new Dictionary<uint, int>();
        while (IsMap(d, g) || (g + 272 <= d.Length && Empty(d, g)))
        {
            for (int i = 0; i < 16; i++)
            {
                byte c = d[g + i];
                if (c == 0x80) continue;
                int e = g + 16 + i * 16;
                uint key = Bin.U32(d, e);
                if ((key & 0x7F) != c || d[e + 8] != 1) return entries;
                entries[key] = (int)Bin.U32(d, e + 11);
            }
            g += 272;
        }
        return entries;
    }

    static int FirstNonZero(byte[] d, int from, int to)
    {
        to = Math.Min(to, d.Length);
        for (int i = from; i < to; i++) if (d[i] != 0) return i;
        return -1;
    }

    /// <summary>Two layouts: one page with header, strings, then the map; or header + map in one
    /// page and the strings in the following page.</summary>
    public static TextTable? Parse(byte[] d, byte[]? next)
    {
        if (d.Length < 0x400) return null;
        StringList strings;
        Dictionary<uint, int> entries;
        if (IsMap(d, 0x60))
        {
            entries = ReadMap(d, 0x60);
            int g = 0x60;
            while (IsMap(d, g) || Empty(d, g)) g += 272;
            int need = entries.Count > 0 ? entries.Values.Max() + 1 : 0;
            int s0 = FirstNonZero(d, g, d.Length);
            var opts = new List<StringList> { new(d, s0 < 0 ? d.Length : s0) };
            if (next != null) opts.Add(new StringList(next, 0));
            strings = opts[0];
            foreach (var o in opts.Skip(1))
                if ((Math.Min(o.Count, need), -Math.Abs(o.Count - need)).CompareTo((Math.Min(strings.Count, need), -Math.Abs(strings.Count - need))) > 0)
                    strings = o;
        }
        else
        {
            int s0 = FirstNonZero(d, 0x60, 0x1000);
            if (s0 < 0) return null;
            strings = new StringList(d, s0);
            int g = (s0 + strings.Span + 15) & ~15;
            while (g < d.Length && !IsMap(d, g)) g += 16;
            entries = ReadMap(d, g);
        }
        var keep = entries.Where(kv => kv.Value < strings.Count).ToDictionary(kv => kv.Key, kv => kv.Value);
        return keep.Count == 0 ? null : new TextTable(strings, keep);
    }

    public sealed class Result
    {
        /// <summary>English tables with more than 1000 entries (station names live here).</summary>
        public List<TextTable> Main = new();
        /// <summary>track ID → (artist, title), from the radio table (labels "&lt;id&gt;a" / "&lt;id&gt;s").</summary>
        public SortedDictionary<int, (string artist, string title)> Songs = new();
    }

    const int MaxTrackId = 100000;

    /// <summary>One pass over data.rpf: every zlib page is inflated and tried as a text table, alone
    /// and together with the page before it. Stops once the station-name tables (English, near the
    /// start) and the first copy of the radio song table are found.</summary>
    public static unsafe Result Read(string dataRpf, Action<double>? progress = null)
    {
        var labels = new Dictionary<uint, (int n, bool artist)>();
        for (int n = 0; n < MaxTrackId; n++)
        {
            labels[Hash.Joaat($"{n}s")] = (n, false);
            labels[Hash.Joaat($"{n}a")] = (n, true);
        }
        var res = new Result();
        long size = new FileInfo(dataRpf).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(dataRpf, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* mm = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref mm);
        try
        {
            long pos = 0;
            byte[]? prev = null; long prevEnd = 0;
            bool namesDone = false;
            Dictionary<int, (string, string)>? songs = null; int songsAt = -1, step = 0;
            while (!(namesDone && songs != null && step > songsAt))
            {
                long o = Zlib.NextHead(mm, size, pos);
                if (o < 0) break;
                pos = o + 1;
                if (((mm[o] << 8) | mm[o + 1]) % 31 != 0) continue;
                long avail = Math.Min(4 << 20, size - o);
                var outp = Zlib.Inflate(mm + o, avail, 64 << 20, out bool eof, out long used);
                if (outp == null) continue;
                long cend = o + (eof ? used : avail);
                pos = Math.Max(o + 2, cend);
                if (outp.Length < 0x100) continue;
                step++;
                progress?.Invoke((double)o / size);
                var single = Parse(outp, null);
                var pair = prev != null ? Parse(prev, outp) : null;
                if (!namesDone)
                {
                    foreach (var t in new[] { single, pair })
                        if (t != null && t.Entries.Count > 1000) res.Main.Add(t);
                    if (o > 0x2000000 && res.Main.Count > 0) namesDone = true;
                }
                if (songs == null || step == songsAt + 1)
                {
                    // the first copy (English) wins; a page pair starting at the same page as an
                    // earlier single-page find replaces it
                    var s1 = SongsOf(single, labels);
                    var s2 = prev != null && o - prevEnd <= 0x1000 ? SongsOf(pair, labels) : null;
                    if (songs == null)
                    {
                        if (s2 != null && s2.Count >= 20) { songs = s2; songsAt = step - 1; }
                        else if (s1 != null && s1.Count >= 20) { songs = s1; songsAt = step; }
                    }
                    else if (s2 != null && s2.Count >= 20) songs = s2;
                }
                prev = outp; prevEnd = cend;
            }
            if (songs != null)
                foreach (var (n, v) in songs) res.Songs[n] = v;
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        return res;
    }

    static Dictionary<int, (string, string)>? SongsOf(TextTable? t, Dictionary<uint, (int n, bool artist)> labels)
    {
        if (t == null) return null;
        var songs = new Dictionary<int, (string, string)>();
        foreach (var (key, idx) in t.Entries)
        {
            if (!labels.TryGetValue(key, out var l)) continue;
            var cur = songs.TryGetValue(l.n, out var c) ? c : ("", "");
            var s = t.Strings.Clean(idx);
            songs[l.n] = l.artist ? (s, cur.Item2) : (cur.Item1, s);
        }
        return songs;
    }
}
