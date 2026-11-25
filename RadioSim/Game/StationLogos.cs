using System.IO.Compression;
using System.Text.RegularExpressions;

namespace RadioSim.Game;

/// <summary>
/// Station logos, read from the game's UI texture pool (<c>data/ttdb/ui/ttdb_ui.ttpf</c>) the way
/// the extractor's export_logos.py does: a logo is the 256×256 texture named <c>radio_&lt;station&gt;</c>,
/// usually the station's audio label (radio_stockyard_fm), sometimes shortened or spelled out
/// (radio_kaleidoscope, radio_leonida_jazz, radio_worldwide_fm). Only an exact name-hash match
/// counts. Logos are cached as PNGs in Data\logos\, keyed to the pool's size and date.
/// </summary>
public static class StationLogos
{
    public const string PoolPath = @"data\ttdb\ui\ttdb_ui.ttpf";

    static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    /// <summary>Texture names to try for one station, most likely first (export_logos.candidates).</summary>
    public static List<string> Candidates(string label, string station)
    {
        var stems = new List<string> { label.StartsWith("radio_") ? label["radio_".Length..] : label, Slug(station) };
        var outp = new List<string>();
        foreach (var stem in stems)
        {
            var words = stem.Split('_');
            for (int n = words.Length; n > 0; n--) outp.Add("radio_" + string.Join("_", words[..n]));
            var core = words.Where(w => !Regex.IsMatch(w, @"^(\d+|radio|the)$")).ToArray();
            if (core.Length > 0) outp.Add("radio_" + string.Join("_", core));
        }
        return outp.Distinct().ToList();
    }

    static string CacheDir(string game)
    {
        var f = new FileInfo(Path.Combine(game, PoolPath));
        return Path.Combine(Settings.DataDir, "logos", $"{f.Length:X}-{f.LastWriteTimeUtc.Ticks:X}");
    }

    static string Safe(string s) => Regex.Replace(s, @"[<>:""/\\|?*\x00-\x1f]+", "_").Trim(' ', '.');

    /// <summary>PNG files of the station logos, by station name, extracted on the first call per
    /// game version. Stations without a logo are left out; a game without the pool gives none.</summary>
    public static Dictionary<string, string> Load(ScheduleFile sched, Action<string>? log = null)
    {
        var res = new Dictionary<string, string>();
        var pool = Path.Combine(sched.Game, PoolPath);
        if (!File.Exists(pool)) return res;
        var dir = CacheDir(sched.Game);
        var missing = sched.Stations.Keys.Where(n => !File.Exists(Path.Combine(dir, Safe(n) + ".png"))).ToList();
        var none = Path.Combine(dir, "none.txt");                 // stations already known to have no logo
        var known = File.Exists(none) ? File.ReadAllLines(none).ToHashSet() : new HashSet<string>();
        if (missing.Any(n => !known.Contains(n)))
        {
            try
            {
                var parent = Path.GetDirectoryName(dir)!;
                if (Directory.Exists(parent))
                    foreach (var old in Directory.GetDirectories(parent)) if (old != dir) try { Directory.Delete(old, true); } catch (IOException) { }
                Directory.CreateDirectory(dir);
                log?.Invoke("Reading the station logos…");
                var noLogo = new List<string>();
                using var tp = new TexturePool(pool);
                foreach (var (name, st) in sched.Stations)
                {
                    var rgba = Extract(tp, st.Label, name, out var w, out var h);
                    if (rgba == null) { noLogo.Add(name); continue; }
                    Png.Write(Path.Combine(dir, Safe(name) + ".png"), w, h, rgba);
                }
                File.WriteAllLines(none, noLogo);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log?.Invoke("Could not read the station logos: " + e.Message);
            }
        }
        foreach (var n in sched.Stations.Keys)
        {
            var p = Path.Combine(dir, Safe(n) + ".png");
            if (File.Exists(p)) res[n] = p;
        }
        return res;
    }

    /// <summary>The logo of one station as RGBA8, or null when no texture of its candidate names
    /// is in the pool (The Vault has none) or it can't be decoded.</summary>
    public static byte[]? Extract(TexturePool tp, string label, string station, out int w, out int h)
    {
        w = h = 0;
        foreach (var c in Candidates(label, station))
        {
            if (!tp.Textures.TryGetValue(TexturePool.NameHash(c), out var t)) continue;
            var rgba = tp.ReadRgba(t);
            if (rgba == null) continue;
            w = t.Width; h = t.Height;
            return rgba;
        }
        return null;
    }

    /// <summary>Writes every station logo found to <paramref name="dir"/> as &lt;Station&gt;.png;
    /// returns (written, stations without a logo).</summary>
    public static (int written, List<string> missing) Export(ScheduleFile sched, string dir)
    {
        Directory.CreateDirectory(dir);
        using var tp = new TexturePool(Path.Combine(sched.Game, PoolPath));
        int n = 0; var miss = new List<string>();
        foreach (var (name, st) in sched.Stations.OrderBy(k => k.Key))
        {
            var rgba = Extract(tp, st.Label, name, out var w, out var h);
            if (rgba == null) { miss.Add(name); continue; }
            Png.Write(Path.Combine(dir, Safe(name) + ".png"), w, h, rgba);
            n++;
        }
        return (n, miss);
    }
}

/// <summary>Minimal PNG writer (RGBA8, no filtering), so the console app needs no imaging library.</summary>
public static class Png
{
    static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static void Write(string path, int w, int h, byte[] rgba)
    {
        using var f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;                           // 8-bit RGBA
        Chunk(f, "IHDR", ihdr);
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var hdr = new byte[8];
        BE(hdr, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
        s.Write(hdr);
        s.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var b in hdr.AsSpan(4)) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var t = new byte[4]; BE(t, 0, ~c);
        s.Write(t);
    }
}
