using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RadioSim.Game;

/// <summary>
/// Builds the radio schedule straight from the game's own files (audio.rpf and data.rpf), with no
/// extraction step: stations, their categories and scheduling tables come from the audio config,
/// song titles and station names from the text tables, DJ lines from the speech voice table, and
/// every clip is a reference to a stream inside audio.rpf. Nothing is decrypted; only plaintext
/// pages and AWC banks are read. The result is cached per game version (file sizes and dates).
/// </summary>
public static class GameIndex
{
    /// <summary>Bump when the schedule format or how it is read changes, so old caches are rebuilt.</summary>
    public const int Version = 2;

    public static readonly string[] CategoryNames =
    {
        "music", "music_night", "music_bed", "advert", "ident", "ident_night", "ident_to_ads",
        "identfromads", "identfromnews", "talk", "talk_night", "showintro", "showintro_night",
        "showoutro", "showoutro_night", "news", "weather", "djsolo", "to_ad", "to_news",
        // recovered by the extractor's crack_hashes.py (hash-verified)
        "identfromtalk", "dj_banter", "dj_banter_night", "dj_banter_story", "dj_banter_welcome",
    };
    static readonly Dictionary<uint, string> CatOf = CategoryNames.ToDictionary(c => Hash.Joaat("radio_track_category_" + c));

    public static readonly string[] DjContexts =
    {
        "dj_intro", "dj_outro", "dj_to_ads", "dj_to_news", "dj_to_night", "dj_to_day",
        "dj_weather_good", "dj_weather_bad", "dj_tod_morning", "dj_tod_afternoon", "dj_tod_evening",
    };

    /// <summary>The folder with audio.rpf and data.rpf: the one given or one of its subfolders.</summary>
    public static string? FindGameFolder(string dir)
    {
        static bool Has(string d) => File.Exists(Path.Combine(d, "audio.rpf")) && File.Exists(Path.Combine(d, "data.rpf"));
        if (!Directory.Exists(dir)) return null;
        if (Has(dir)) return Path.GetFullPath(dir);
        try { return Directory.EnumerateDirectories(dir).FirstOrDefault(Has); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static string CacheDir => Settings.DataDir;

    /// <summary>Identifies the game version: sizes and dates of the two archives read.</summary>
    public static string GameKey(string game)
    {
        var parts = new[] { "audio.rpf", "data.rpf" }.Select(f => new FileInfo(Path.Combine(game, f)))
            .Select(f => $"{f.Length:X}-{f.LastWriteTimeUtc.Ticks:X}");
        return $"v{Version}-" + string.Join("-", parts);
    }

    public static string CachePath(string game) => Path.Combine(CacheDir, $"schedule-{GameKey(game)}.json");

    /// <summary>The schedule for this game folder: from the cache, or built (about a minute).</summary>
    public static ScheduleFile Load(string game, Action<string>? log = null, bool rebuild = false)
    {
        var cache = CachePath(game);
        if (!rebuild && File.Exists(cache))
        {
            try
            {
                var s = JsonSerializer.Deserialize<ScheduleFile>(File.ReadAllText(cache));
                if (s != null && s.Version == Version && s.Stations.Count > 0) { s.Game = game; return s; }
            }
            catch (JsonException) { }
        }
        var sched = Build(game, log);
        Directory.CreateDirectory(CacheDir);
        foreach (var old in Directory.GetFiles(CacheDir, "schedule-*.json")) try { File.Delete(old); } catch (IOException) { }
        Save(sched, cache);
        return sched;
    }

    public static void Save(ScheduleFile s, string path)
    {
        var o = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        File.WriteAllText(path, JsonSerializer.Serialize(s, o));
    }

    /// <summary>A station clip; <paramref name="Duration"/> is the length its sound record gives (seconds), when known.</summary>
    sealed record Row(uint Sid, string Station, string Label, string Category, double? Duration);

    public static ScheduleFile Build(string game, Action<string>? log = null)
    {
        log ??= _ => { };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string audioPath = Path.Combine(game, "audio.rpf"), dataPath = Path.Combine(game, "data.rpf");
        using var awc = new AwcArchive(audioPath);

        // ---- audio config: the zlib pages between the RPF index and the first AWC bank
        log("Reading the audio config…");
        long start = awc.IndexSize();
        var first = awc.Banks(start).FirstOrDefault() ?? throw new InvalidDataException("no AWC banks in audio.rpf");
        var region = awc.Read(start, first.Base - start);
        var blob = new MemoryStream();
        foreach (var (_, d) in Zlib.Carve(region)) blob.Write(d);
        var cfg = new AudioConfig(blob.ToArray());
        var curves = new Curves(cfg);

        // ---- text tables
        log("Reading station names and song titles from data.rpf…");
        var text = TextTables.Read(dataPath);
        log($"  {text.Songs.Count} song titles, {text.Main.Count} text tables");

        // ---- stations and the streams they list
        var rows = StationRows(cfg, text.Main);
        var stationSids = rows.Select(r => r.Sid).ToHashSet();
        log($"  {rows.Select(r => r.Station).Distinct().Count()} stations, {stationSids.Count} streams");

        // ---- DJ voices: every take of every radio DJ context
        var songIds = new Dictionary<string, uint>();
        foreach (var key in cfg.GKeys)
        {
            var d = cfg.ObjByKey(key);
            if (d is not { Length: 108 } || d[0] != 0x0c) continue;
            var s = Bin.CStr(d, 12, 80);
            if (Regex.IsMatch(s, "^[A-Z0-9_]{3,64}$")) songIds[s.ToLowerInvariant()] = Hash.Joaat(s.ToLowerInvariant()) & Hash.Mask;
        }
        uint pIntro = Hash.Partial("dj_intro"), pOutro = Hash.Partial("dj_outro");
        var voices = cfg.VoiceTable().Where(v => v.Value.Any(c => c.ctx == pIntro || c.ctx == pOutro)).ToList();
        var needed = new HashSet<uint>(stationSids);
        foreach (var (_, ctxs) in voices)
            foreach (var (h, c) in ctxs)
                for (int i = 1; i <= c; i++) needed.Add(Hash.TakeId(h, i));

        // ---- which banks hold those streams (headers only; the banks follow each other)
        log("Indexing the sound banks in audio.rpf…");
        var banksOf = new Dictionary<uint, List<long>>();
        var headers = new Dictionary<long, AwcHeader>();
        int nb = 0;
        int lastPct = -1;
        foreach (var h in awc.Banks(start, f => { int p = (int)(f * 100); if (p / 10 != lastPct / 10) { lastPct = p; log($"  {p}%"); } }))
        {
            nb++;
            bool any = false;
            foreach (var id in awc.Ids(h))
            {
                if (!needed.Contains(id)) continue;
                if (!banksOf.TryGetValue(id, out var l)) banksOf[id] = l = new();
                if (l.Count == 0 || l[^1] != h.Base) l.Add(h.Base);
                any = true;
            }
            if (any) headers[h.Base] = h;
        }
        log($"  {nb} banks, {headers.Count} with radio streams");

        var infos = new Dictionary<(long, uint), StreamInfo?>();
        StreamInfo? Info(long bank, uint sid)
        {
            if (!infos.TryGetValue((bank, sid), out var i)) infos[(bank, sid)] = i = awc.Info(headers[bank], sid);
            return i;
        }
        // Streamed banks reuse channel IDs, so a station clip's ID can occur in many banks (02DE0C31,
        // a dj_banter clip on 18 stations, is in 18). When several banks hold a playable copy, the one
        // whose length matches the station's sound record (within 0.05 s) is it; otherwise the first
        // bank (lowest offset).
        var firsts = new Dictionary<(uint, double?), StreamInfo?>();
        StreamInfo? First(uint sid, double? want = null)
        {
            if (firsts.TryGetValue((sid, want), out var f)) return f;
            if (!banksOf.TryGetValue(sid, out var l)) return firsts[(sid, want)] = null;
            var ok = l.Select(b => Info(b, sid)).Where(i => i != null && (!i.Streamed || i.Codec == AwcArchive.CodecOpus)).ToList();
            if (ok.Count > 1 && want is { } w && ok.FirstOrDefault(i => Math.Abs(i!.Duration - w) < 0.05) is { } hit)
                return firsts[(sid, want)] = hit;
            return firsts[(sid, want)] = ok.FirstOrDefault();
        }
        StreamInfo? FirstOf(Row r) => First(r.Sid, r.Duration);
        var bankEvents = new Dictionary<long, List<(uint type, uint param, uint sample)>>();
        List<(uint type, uint param, uint sample)> Events(long bank)
        {
            if (!bankEvents.TryGetValue(bank, out var e)) bankEvents[bank] = e = awc.Events(headers[bank]);
            return e;
        }
        uint evTrack = Hash.Joaat("trackid"), evDj = Hash.Joaat("dj");
        // the TRACKID marker numbers of a streamed song (one for a normal song)
        List<int> TrackIds(StreamInfo fr) => fr.Streamed
            ? Events(fr.Bank).Where(e => e.type == evTrack).Select(e => Bin.F32(e.param)).Where(x => x > 0 && x < 1e6)
                             .Select(x => (int)x).Distinct().ToList()
            : new List<int>();
        int picked = rows.Count(r => FirstOf(r) is { } fi && First(r.Sid) is { } f0 && fi.Bank != f0.Bank);
        log($"  {picked} station clips whose ID is in several banks matched to their bank by length");

        // songs by title, for DJ lines named after the title (dj_intro_dont_chart): the text table's
        // track ID → the station song stream carrying that TRACKID marker
        var streamOfTid = new Dictionary<int, uint>();
        foreach (var r in rows)
            if (r.Category is "music" or "music_night" && FirstOf(r) is { Streamed: true, Channel: 0 } fr && TrackIds(fr) is [var tid1])
                streamOfTid.TryAdd(tid1, r.Sid);
        var titled = text.Songs.Select(kv => (kv.Value.title, streamOfTid.TryGetValue(kv.Key, out var ss) ? ss : (uint?)null)).ToList();

        // ---- DJ lines per station
        log("Matching DJ lines to stations…");
        var djRows = DjLines(rows, voices, songIds, titled, banksOf, log);
        var djSong = new Dictionary<string, Dictionary<uint, Dictionary<string, List<string>>>>();
        var djGen = new Dictionary<string, Dictionary<string, List<string>>>();
        foreach (var r in djRows)
        {
            if (r.Station.Length == 0 || Info(r.Bank, r.Sid) is not { } inf) continue;
            if (r.Song is { } ssid)
                Get(Get(Get(djSong, r.Station), ssid), r.Context.StartsWith("dj_intro_") ? "intro" : "outro").Add(inf.Ref);
            else if (DjContexts.Contains(r.Context))
                Get(Get(djGen, r.Station), r.Context[3..]).Add(inf.Ref);
        }

        // ---- song titles: the TRACKID marker → text table; else the track metadata object
        var spaced = new Dictionary<string, string>();
        foreach (var (_, (artist, _)) in text.Songs)
            if (artist.Length > 0) spaced[Regex.Replace(artist.ToUpperInvariant(), "[^A-Z0-9]", "")] = artist;
        var metaTitle = new Dictionary<uint, (string, string)>();
        foreach (var key in cfg.GKeys)
        {
            var d = cfg.ObjByKey(key);
            if (d is not { Length: 108 } || d[0] != 0x0c) continue;
            var tid = Bin.CStr(d, 12, 80);
            if (!Regex.IsMatch(tid, "^[A-Z0-9_]{2,64}$")) continue;
            var v = Bin.Words(d);
            var art = v[25] < cfg.GRef.Count ? cfg.Obj(v[25]) : null;
            var a = art is { Length: > 0 } && art[0] == 0x07 ? Bin.CStr(art, 5, 40) : "";
            metaTitle[Hash.Joaat(tid.ToLowerInvariant()) & Hash.Mask] = (spaced.GetValueOrDefault(a, a), Text.Title(tid.Replace('_', ' ')));
        }
        var djMarkers = new[] { "intro_start", "intro_end", "outro_start", "outro_end" }.ToDictionary(n => Hash.Joaat(n));
        (string artist, string title) SongTitle(StreamInfo fr)
        {
            var tids = TrackIds(fr);
            (string, string)? s = tids.Count == 1 && text.Songs.TryGetValue(tids[0], out var t) ? t : null;
            if (s is { } ss && ss.Item2.Length > 0) return ss;
            return metaTitle.TryGetValue(fr.Id, out var mt) ? mt : s is { } s2 ? (s2.Item1, "") : ("", "");
        }

        // ---- stations
        log("Building the station schedules…");
        var clips = new Dictionary<string, Dictionary<string, List<Row>>>();
        foreach (var r in rows)
            if (FirstOf(r) != null) Get(Get(clips, r.Station), r.Category).Add(r);
        var sched = new ScheduleFile { Version = Version, GameKey = GameKey(game), Game = game };
        var ex = new Extract(cfg, curves);
        foreach (var key in cfg.GKeys)
        {
            var d = cfg.ObjByKey(key);
            if (d == null || d.Length < 16 || d[0] != 0x10 || d[2] != 0xa0 || d[3] != 0xaa || d[4] != 0xaa || Bin.U32(d, 8) != key) continue;
            var lrow = rows.FirstOrDefault(r => Hash.Joaat(r.Label) == key);
            if (lrow == null) continue;
            string name = lrow.Station;
            var st = ex.Station(d, lrow.Label);
            foreach (var (cat, crows) in clips.GetValueOrDefault(name) ?? new())
            {
                var files = new List<Clip>();
                foreach (var row in crows)                 // one row per stream (StationRows)
                {
                    uint sid = row.Sid;
                    var fr = FirstOf(row)!;
                    var (artist, title) = SongTitle(fr);
                    if (cat is "music" or "music_night" && fr.Streamed)
                    {
                        var w = new Dictionary<string, double>();
                        foreach (var e in Events(fr.Bank))
                            if (e.type == evDj && djMarkers.TryGetValue(e.param, out var mk))
                                w[mk] = Math.Round(e.sample / (double)(fr.Rate > 0 ? fr.Rate : 48000), 3);
                        var ds = djSong.GetValueOrDefault(name)?.GetValueOrDefault(sid);
                        st.Songs.Add(new Song
                        {
                            Stream = $"{sid:X8}", Src = fr.Ref, Category = cat, Artist = artist, Title = title,
                            Duration = fr.Duration, Dj = w,
                            Intros = ds?.GetValueOrDefault("intro") ?? new(), Outros = ds?.GetValueOrDefault("outro") ?? new(),
                        });
                    }
                    else
                        files.Add(new Clip
                        {
                            Stream = $"{sid:X8}", Src = fr.Ref, Duration = fr.Duration,
                            Title = string.Join(" - ", new[] { artist, title }.Where(x => x.Length > 0)),
                        });
                }
                if (files.Count > 0) st.Categories[cat] = files;
            }
            st.Dj.Lines = djGen.GetValueOrDefault(name) ?? new();
            sched.Stations[name] = st;
            log($"  {name,-28} {st.Songs.Count,3} songs, {st.Categories.Count} categories, DJ {(st.Person.Length > 0 ? st.Person : "-")}");
        }
        log($"Done in {sw.Elapsed.TotalSeconds:0}s.");
        return sched;
    }

    static V Get<K, V>(Dictionary<K, V> d, K k) where K : notnull where V : new()
    {
        if (!d.TryGetValue(k, out var v)) d[k] = v = new V();
        return v;
    }

    // ---- stations → streams (extract_stations.py)

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');

    /// <summary>The label's letters appear in order in the text: radio_wwfm ~ "Worldwide FM".</summary>
    static bool Fits(string label, string text)
    {
        var lab = label.ToLowerInvariant();
        int i0 = lab.IndexOf("radio_", StringComparison.Ordinal);
        if (i0 >= 0) lab = lab.Remove(i0, 6);
        var want = Regex.Replace(lab, "[^a-z0-9]", "");
        var have = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");
        if (want.Length == 0) return false;
        int j = 0;
        foreach (var ch in want)
        {
            while (j < have.Length && have[j] != ch) j++;
            if (j >= have.Length) return false;
            j++;
        }
        return true;
    }

    /// <summary>Station display text for an object label. In one index range of the main text table the
    /// strings sit one slot later than the map says, so neighbours are checked against the label.</summary>
    static string? DisplayName(string label, List<TextTable> tables)
    {
        foreach (var lab in new[] { label, label + "_short" })
        {
            uint k = Hash.Joaat(lab);
            foreach (var t in tables)
            {
                if (!t.Entries.TryGetValue(k, out var i)) continue;
                foreach (var j in new[] { i, i + 1, i - 1 })
                    if (j >= 0 && j < t.Strings.Count && t.Strings.Clean(j) is var s && Fits(label, s)) return s;
            }
        }
        return null;
    }

    static List<Row> StationRows(AudioConfig cfg, List<TextTable> text)
    {
        var packs = Regex.Matches(System.Text.Encoding.Latin1.GetString(cfg.B), @"Core\.RadioStation\.([a-z0-9_]+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var rows = new List<Row>();
        foreach (var key in cfg.GKeys)
        {
            var d = cfg.ObjByKey(key);
            if (d == null || d.Length < 16 || d[0] != 0x10 || d[2] != 0xa0 || d[3] != 0xaa || d[4] != 0xaa || Bin.U32(d, 8) != key) continue;
            // the object's label: radio_<pack>, or radio_<display text> of the strings at its key
            var cands = new HashSet<string>();
            foreach (var p in packs)
                foreach (var x in new[] { p, Regex.Replace(p, "_radio$", "") })
                    cands.Add("radio_" + Regex.Replace(x, "^radio_", ""));
            foreach (var t in text)
                if (t.Entries.TryGetValue(key, out var i))
                    for (int j = i - 1; j <= i + 1; j++)
                        if (j >= 0 && j < t.Strings.Count) cands.Add("radio_" + Regex.Replace(Norm(t.Strings.Clean(j)), "^radio_", ""));
            var label = cands.FirstOrDefault(c => Hash.Joaat(c) == key);
            if (label == null) continue;
            var name = DisplayName(label, text);
            if (name == null) continue;
            foreach (var c in cfg.Categories(d))
            {
                var co = cfg.Obj(c);
                uint ctype = co != null && co.Length >= 12 ? Bin.U32(co, 8) : uint.MaxValue;
                uint th = cfg.RefHash(ctype);
                var cat = CatOf.GetValueOrDefault(th, $"category_{th:x8}");
                foreach (var t in cfg.Tracks(c))
                {
                    var tv = cfg.Obj(t);
                    if (tv == null || tv.Length < 16) continue;
                    uint tr = Bin.U32(tv, 12);
                    if (cfg.StreamOf(tr) is { } hb) rows.Add(new Row(hb & Hash.Mask, name, label, cat, cfg.DurationOf(tr)));
                }
            }
        }
        // one row per (stream, station, category), sorted by station then stream
        return rows.DistinctBy(r => (r.Sid, r.Station, r.Category))
                   .OrderBy(r => r.Station, StringComparer.Ordinal).ThenBy(r => r.Sid).ToList();
    }

    // ---- DJ lines (extract_stations.dj_lines)

    /// <summary>A DJ take; <paramref name="Song"/> is the station stream of the song a song line is for.</summary>
    sealed record DjRow(long Bank, uint Sid, string Name, string Station, string Context, uint? Song);

    /// <summary>A song title as context-name tokens (crack_hashes.norm): NFKD folded to ASCII,
    /// lowercased, apostrophes dropped, &amp; → " and ", other runs → "_".</summary>
    static string TitleToken(string s)
    {
        var a = new string(s.Normalize(System.Text.NormalizationForm.FormKD).Where(c => c < 128).ToArray()).ToLowerInvariant();
        a = a.Replace("'", "").Replace("’", "").Replace("&", " and ");
        return Regex.Replace(a, "[^a-z0-9]+", "_").Trim('_');
    }

    /// <summary>crack_hashes.title_forms: 'Don't Chart' → dont_chart, dontchart; also without a
    /// (...) / [...] part or a feat. credit.</summary>
    static IEnumerable<string> TitleForms(string title)
    {
        var t = Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        var outp = new List<string>();
        foreach (var x in new[] { title, t, Regex.Split(t, @"\s+(?:feat\.?|ft\.?)\s+", RegexOptions.IgnoreCase)[0] })
        {
            var n = TitleToken(x);
            if (n.Length == 0) continue;
            foreach (var f in new[] { n, n.Replace("_", "") }) if (!outp.Contains(f)) outp.Add(f);
        }
        return outp;
    }

    /// <summary>Each DJ voice lists its contexts and take counts; take NN of context C is the stream
    /// joaat("C_NN"). Song lines are dj_intro_/dj_outro_ plus the track's metadata ID, or else plus
    /// the song's title (dj_intro_dont_chart for "Don't Chart"). A voice belongs to the station whose
    /// songs its song lines cover (else the station of its bank's other lines); its lines are the
    /// takes found in its most common bank.</summary>
    static List<DjRow> DjLines(List<Row> rows, List<KeyValuePair<int, List<(uint ctx, int takes)>>> voices,
                               Dictionary<string, uint> songIds, List<(string title, uint? sid)> titled,
                               Dictionary<uint, List<long>> banksOf, Action<string> log)
    {
        var names = DjContexts.ToDictionary(c => Hash.Partial(c), c => c);
        var songOfCtx = new Dictionary<uint, List<uint>>();
        foreach (var (song, ssid) in songIds)
            foreach (var kind in new[] { "intro", "outro" })
            {
                uint h = Hash.Partial($"dj_{kind}_{song}");
                names[h] = $"dj_{kind}_{song}"; songOfCtx[h] = new List<uint> { ssid };
            }
        // songs whose lines are named after the title; only for contexts nothing else names
        foreach (var (title, ssid) in titled)
            foreach (var t in TitleForms(title))
                foreach (var kind in new[] { "intro", "outro" })
                {
                    var n = $"dj_{kind}_{t}";
                    uint h = Hash.Partial(n);
                    if (!names.TryAdd(h, n) && names[h] != n) continue;
                    var l = Get(songOfCtx, h);
                    if (ssid is { } x && !l.Contains(x)) l.Add(x);   // songs sharing a title: settled by station below
                }
        var stOf = new Dictionary<uint, List<string>>();
        foreach (var r in rows)
        {
            var l = Get(stOf, r.Sid);
            if (!l.Contains(r.Station)) l.Add(r.Station);
        }
        var found = new List<DjRow>();
        foreach (var (_, ctxs) in voices)
        {
            var votes = new Counter<string>(); var bankVotes = new Counter<long>();
            foreach (var (h, c) in ctxs)
            {
                if (songOfCtx.TryGetValue(h, out var cands))
                    foreach (var s in cands.SelectMany(ss => stOf.GetValueOrDefault(ss) ?? new()).Distinct()) votes.Add(s);
                for (int i = 1; i <= c; i++)
                    if (banksOf.TryGetValue(Hash.TakeId(h, i), out var bl)) foreach (var b in bl) bankVotes.Add(b);
            }
            if (bankVotes.Count == 0) continue;
            long bank = bankVotes.Top();
            string station = votes.Count > 0 ? votes.Top() : "";
            foreach (var (h, c) in ctxs)
            {
                var ctx = names.GetValueOrDefault(h, $"ctx_{h:x8}");
                var cands = songOfCtx.GetValueOrDefault(h) ?? new();
                uint? songSid = cands.Where(ss => stOf.GetValueOrDefault(ss)?.Contains(station) == true).Select(ss => (uint?)ss).FirstOrDefault()
                             ?? (cands.Count > 0 ? cands[0] : null);
                for (int i = 1; i <= c; i++)
                {
                    uint x = Hash.TakeId(h, i);
                    if (banksOf.TryGetValue(x, out var bl) && bl.Contains(bank))
                        found.Add(new DjRow(bank, x, $"{ctx}_{i:00}", station, ctx, songSid));
                }
            }
        }
        // voices whose songs did not say: the station of the same bank's other voices
        var byBank = new Counter<(long, string)>();
        foreach (var f in found) if (f.Station.Length > 0) byBank.Add((f.Bank, f.Station));
        var fill = new Dictionary<long, string>();
        foreach (var ((b, s), _) in byBank.MostCommon()) fill.TryAdd(b, s);
        var res = found.Select(f => f.Station.Length > 0 ? f : f with { Station = fill.GetValueOrDefault(f.Bank, "") })
                       .OrderBy(f => f.Station, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
        int named = res.Count(f => !f.Context.StartsWith("ctx_")), forSong = res.Count(f => f.Song != null);
        var titleCtx = res.Where(f => f.Song != null && !songIds.Keys.Any(k => f.Context == $"dj_intro_{k}" || f.Context == $"dj_outro_{k}"))
                          .Select(f => f.Context).Distinct().Count();
        log($"  {res.Count} DJ lines, {named} named, {forSong} for a song ({titleCtx} contexts named after the title)");
        return res;
    }

    /// <summary>Python's collections.Counter: most common first, ties in insertion order.</summary>
    sealed class Counter<T> where T : notnull
    {
        readonly Dictionary<T, int> n = new(); readonly List<T> order = new();
        public void Add(T k) { if (!n.ContainsKey(k)) { n[k] = 0; order.Add(k); } n[k]++; }
        public int Count => order.Count;
        public IEnumerable<(T key, int count)> MostCommon() => order.Select(k => (k, n[k])).OrderByDescending(x => x.Item2);
        public T Top() => MostCommon().First().key;
    }

    // ---- scheduling tables, DJ rules (export_schedule.py)

    sealed class Extract(AudioConfig cfg, Curves curves)
    {
        // rule keys: class 0x1d objects named dj_speech_category_<type>; intro/outro_track_specific
        // (0x14398f13 / 0xa838719f, a line for this one song) were labelled song_intro / song_outro before
        static readonly Dictionary<uint, string> Speech = new[] { "intro", "outro", "to", "weather", "time_of_day", "general",
                                                                  "intro_track_specific", "outro_track_specific" }
            .ToDictionary(x => Hash.Joaat("dj_speech_category_" + x), x => x);

        string CatName(uint n)
        {
            uint th = cfg.RefHash(n);
            return CatOf.GetValueOrDefault(th, $"category_{th:x8}");
        }

        uint? RefKey(uint n) => n < cfg.GRef.Count ? cfg.GRef[(int)n].hash : null;

        static uint W(uint[] v, int i) => i < v.Length ? v[i] : 0;

        Condition Cond(uint n, int depth = 0)
        {
            var d = cfg.Obj(n);
            if (d == null || d.Length == 0 || depth > 6) return new Condition { Kind = "other" };
            var v = Bin.Words(d);
            switch (d[0])
            {
                case 0x21: return new Condition { Kind = "const", Value = Bin.F32(W(v, 2)) };
                case 0x23 or 0x24:
                    var ck = RefKey(W(v, 3));
                    return new Condition { Kind = d[0] == 0x23 ? "run" : "since", Category = CatName(W(v, 2)), Curve = ck != null ? curves.Get(ck) : null };
                case 0x05:
                    return new Condition { Kind = "all", Of = v.Skip(2).Where(x => x != 0xffffffff).Select(x => Cond(x, depth + 1)).ToList() };
                default: return new Condition { Kind = "other" };
            }
        }

        /// <summary>A class 0x1f list: count in byte 5, then (category, ncond, cond...) entries.</summary>
        List<Option> Options(uint n)
        {
            var d = cfg.Obj(n);
            var opts = new List<Option>();
            if (d == null || d.Length < 8 || d[0] != 0x1f) return opts;
            var v = Bin.Words(d);
            int i = 2;
            for (int k = 0; k < d[5]; k++)
            {
                if (i + 1 >= v.Length) break;
                uint cat = v[i], nc = v[i + 1];
                opts.Add(new Option { Category = CatName(cat), Conditions = v.Skip(i + 2).Take((int)Math.Min(nc, 1000)).Select(x => Cond(x)).ToList() });
                i += 2 + (int)Math.Min(nc, 100000);
            }
            return opts;
        }

        string? SpeechType(uint n)
        {
            var d = cfg.Obj(n);
            return d is { Length: >= 12 } && d[0] == 0x1d ? Speech.GetValueOrDefault(Bin.U32(d, 8), $"type_{Bin.U32(d, 8):x8}") : null;
        }

        DjCond DjCondition(uint n)
        {
            var d = cfg.Obj(n);
            if (d == null || d.Length == 0) return new DjCond { Kind = "other" };
            var v = Bin.Words(d);
            switch (d[0])
            {
                case 0x21: return new DjCond { Kind = "const", Value = Bin.F32(W(v, 2)) };
                case 0x2d or 0x2e or 0x2c:   // category that played before / comes next: a if equal else b
                    return new DjCond
                    {
                        Kind = d[0] switch { 0x2d => "prev", 0x2e => "next", _ => "recent" }, Category = CatName(W(v, 2)),
                        A = Bin.F32(W(v, 3)), B = Bin.F32(W(v, 4)),
                    };
                case 0x28:                   // scale * curve(seconds since this speech type last played)
                    var ck = RefKey(W(v, 3));
                    return new DjCond { Kind = "since_speech", Type = SpeechType(W(v, 2)), Curve = ck is > 0 ? curves.Get(ck) : null, Value = Bin.F32(W(v, 4)) };
                case 0x29:                   // a if the DJ's last speech was this type else b
                    return new DjCond { Kind = "after_speech", Type = SpeechType(W(v, 2)), A = Bin.F32(W(v, 3)), B = Bin.F32(W(v, 4)) };
                case 0x34 or 0x35:           // inferred: a if the song has its own line for this list else b
                    return new DjCond { Kind = "song_has_line", A = Bin.F32(W(v, 3)), B = Bin.F32(W(v, 4)) };
                default: return new DjCond { Kind = "other" };
            }
        }

        List<DjRule> DjRules(uint n)
        {
            var d = cfg.Obj(n)!;
            var v = Bin.Words(d);
            var rules = new List<DjRule>();
            int i = 2;
            for (int k = 0; k < d[5] && i + 1 < v.Length; k++)
            {
                uint key = v[i], nc = v[i + 1];
                rules.Add(new DjRule { Type = SpeechType(key) ?? "", Conditions = v.Skip(i + 2).Take((int)Math.Min(nc, 1000)).Select(DjCondition).ToList() });
                i += 2 + (int)Math.Min(nc, 100000);
            }
            return rules;
        }

        public Station Station(byte[] d, string label)
        {
            var v = Bin.Words(d, 256);
            uint? table = null; string person = ""; double? chance = null;
            var ruleLists = new List<List<DjRule>>();
            for (int k = 3; k < Math.Min(20, v.Length); k++)
            {
                uint x = v[k];
                var o = cfg.Obj(x);
                if (o == null || o.Length == 0) continue;
                if (o[0] == 0x1f && o.Length >= 12 && SpeechType(Bin.U32(o, 8)) != null) ruleLists.Add(DjRules(x));
                else if (o[0] == 0x03 && o.Length >= 12)
                {
                    var o37 = cfg.Obj(Bin.U32(o, 8));
                    if (o37 is { Length: >= 12 } && o37[0] == 0x37) chance = Bin.F32(Bin.U32(o37, 8));
                }
                if (o[0] == 0x1e && table == null) table = x;
                else if (o[0] == 0x38)
                {
                    var pv = Bin.Words(o);
                    var p = pv.Length > 4 ? cfg.Obj(pv[4]) : null;
                    if (p is { Length: > 0 } && p[0] == 0x07) person = Bin.CStr(p, 5, 40);
                }
            }
            var trans = new Dictionary<string, List<Option>>();
            if (table is { } tb && cfg.Obj(tb) is { } td)
            {
                var tv = Bin.Words(td);
                trans["*"] = Options(W(tv, 2));
                for (int j = 0; j < W(tv, 3) && 5 + 2 * j < tv.Length; j++)
                    trans[CatName(tv[4 + 2 * j])] = Options(tv[5 + 2 * j]);
            }
            return new Station
            {
                Label = label, Person = person, Transitions = trans,
                Dj = new DjInfo
                {
                    SpeechChance = chance,
                    IntroRules = ruleLists.FirstOrDefault(r => r.Any(x => x.Type == "intro")) ?? new(),
                    OutroRules = ruleLists.FirstOrDefault(r => r.Any(x => x.Type == "outro")) ?? new(),
                },
            };
        }
    }
}
