namespace RadioSim;

/// <summary>DJ speech (a stream ref) mixed over a segment, starting <see cref="At"/> seconds in.</summary>
public sealed record Voice(string Src, double At, string Label);

/// <summary>One thing the station plays: a main file plus any DJ speech over it. A song of a DJ mix
/// is the stretch of the mix's stream from <paramref name="Start"/> seconds, <paramref name="Duration"/>
/// long; <paramref name="Part"/> then says which ("DJ mix, song 3 of 23"). A long item played in
/// pieces (a podcast episode) has its full length in <paramref name="Whole"/>; each piece is the
/// stretch from <paramref name="Start"/>, and every piece after the first continues the item.</summary>
public sealed record Segment(string Category, string Src, string Label, double Duration, List<Voice> Voices,
                             double Start = 0, string Part = "", double Whole = 0)
{
    /// <summary>Only a stretch of the stream plays (a song of a mix, or a piece of an episode).</summary>
    public bool Ranged => Part.Length > 0 || Whole > 0;

    /// <summary>A later piece of the same item as <paramref name="prev"/>.</summary>
    public bool Continues(Segment? prev) =>
        prev != null && Whole > 0 && Start > 0 && prev.Src == Src && Math.Abs(prev.Start + prev.Duration - Start) < 0.01;

    /// <summary>Seconds a piece is long: short enough that no piece needs much memory decoded
    /// (3 minutes of 48 kHz stereo float is 66 MB).</summary>
    public const double PieceSeconds = 180;

    /// <summary>A podcast's episodes from episode <paramref name="from"/> (0-based) to the last, each
    /// in pieces of <see cref="PieceSeconds"/>.</summary>
    public static IEnumerable<Segment> Episodes(string podcast, IReadOnlyList<Episode> episodes, int from = 0)
    {
        for (int i = Math.Max(0, from); i < episodes.Count; i++)
        {
            var e = episodes[i];
            string label = $"{podcast} - {e.Title}", part = $"{podcast}, episode {i + 1} of {episodes.Count}";
            for (double a = 0; a < e.Duration - 0.01;)
            {
                double b = e.Duration - a < PieceSeconds * 1.5 ? e.Duration : a + PieceSeconds;   // no short last piece
                yield return new Segment("podcast", e.Src, label, b - a, new List<Voice>(), a, part, e.Duration);
                a = b;
            }
        }
    }
}

public sealed class Options
{
    /// <summary>Overrides the station's own speech chance (from its 0x37 settings object).</summary>
    public double? DjIntroChance;
    public double? DjOutroChance;
    /// <summary>Hour of day the station starts at (time-of-day lines); default: the local time.</summary>
    public double StartHour = DateTime.Now.Hour + DateTime.Now.Minute / 60.0;
    /// <summary>"good" or "bad": which weather lines the DJ uses.</summary>
    public string Weather = "good";
    /// <summary>A song is not repeated until this fraction of the station's songs has played.</summary>
    public double NoRepeatFraction = 0.5;
    /// <summary>Play every song of the station once before any song repeats (a shuffled bag
    /// per music category, refilled when empty; the last song never opens the next round).</summary>
    public bool FullRotation;
}

/// <summary>
/// What plays next comes from the station's transition table: for the category that just played,
/// every candidate gets weight = product of its conditions (fixed weights, curves over "times in a
/// row" and "seconds since last played"), and one is drawn by weight; when every weight is zero
/// the station plays music.
/// DJ speech follows the station's intro and outro rule lists: at a song's start (intro window)
/// and end (outro window), with the station's speech chance, every speech type (generic intro or
/// outro, the song's own line, time of day, weather, links to ads/news/night/day) is weighted by
/// its conditions, and one with lines available is drawn.
/// </summary>
public sealed class Scheduler
{
    readonly Station st;
    readonly Random rng;
    readonly Options opt;
    readonly Dictionary<string, double> lastPlayed = new();
    readonly Dictionary<string, double> lastSpeech = new();
    readonly Queue<string> recentSongs = new();
    readonly Dictionary<string, HashSet<string>> songBag = new();
    string? lastSong;
    readonly Dictionary<string, Queue<string>> recentClips = new();
    readonly Dictionary<string, Queue<string>> recentLines = new();
    string prev = "*";
    string? lastSpeechType;
    int run;
    public double Clock { get; private set; }     // seconds of station time

    public Scheduler(Station station, int seed, Options options)
    {
        st = station; rng = new Random(seed); opt = options;
        startAge = rng.NextDouble() * 900;
    }

    // Things not heard since tuning in count from a random "station age" (0-15 min), so the
    // first ad break or ident lands at a natural point. This offset is ours, not game data.
    readonly double startAge;
    double SinceLast(string cat) => lastPlayed.TryGetValue(cat, out var t) ? Clock - t : Clock + startAge;
    double SinceSpeech(string type) => lastSpeech.TryGetValue(type, out var t) ? Clock - t : Clock + startAge;
    public double Hour => ((opt.StartHour * 3600 + Clock) / 3600) % 24;

    double Weight(Condition c) => c.Kind switch
    {
        "const" => c.Value,
        "run" => c.Curve?.Eval(prev == c.Category ? run : 0) ?? 1,
        "since" => c.Curve?.Eval(SinceLast(c.Category)) ?? 1,
        "all" => c.Of.Aggregate(1.0, (w, x) => w * Weight(x)),
        _ => 1.0,                               // condition classes not decoded yet
    };

    bool Playable(string cat) =>
        cat is "music" or "music_night" ? st.Songs.Count > 0
        : st.Categories.ContainsKey(cat) || !IsKnownClipCategory(cat);

    // categories without clips (e.g. the ident-to-music links) pass straight through;
    // news and weather reports have no station clips, so they are never chosen
    static bool IsKnownClipCategory(string cat) => cat is "advert" or "ident" or "news" or "weather" or "talk"
        or "talk_night" or "music_bed" or "showintro" or "showintro_night" or "showoutro" or "showoutro_night"
        or "ident_night" or "identfromads" or "identfromnews" or "ident_to_ads" or "identfromtalk"
        or "dj_banter" or "dj_banter_night" or "dj_banter_story" or "dj_banter_welcome";

    string NextCategory()
    {
        if (!st.Transitions.TryGetValue(prev, out var opts) && !st.Transitions.TryGetValue("*", out opts))
            return "music";
        var weights = opts.Select(o => Playable(o.Category) ? Math.Max(0, o.Conditions.Aggregate(1.0, (w, c) => w * Weight(c))) : 0).ToList();
        double total = weights.Sum();
        if (total <= 0) return "music";
        double r = rng.NextDouble() * total;
        for (int i = 0; i < opts.Count; i++)
        {
            r -= weights[i];
            if (r <= 0) return opts[i].Category;
        }
        return opts[^1].Category;
    }

    void Mark(string cat, double duration)
    {
        Clock += duration;
        lastPlayed[cat] = Clock;
        run = cat == prev ? run + 1 : 1;
        prev = cat;
    }

    T Pick<T>(IList<T> items, Func<T, string> key, Queue<string> recent, double fraction)
    {
        int keep = Math.Max(0, Math.Min(items.Count - 1, (int)(items.Count * fraction)));
        var pool = items.Where(x => !recent.Contains(key(x))).ToList();
        if (pool.Count == 0) pool = items.ToList();
        var pick = pool[rng.Next(pool.Count)];
        recent.Enqueue(key(pick));
        while (recent.Count > keep) recent.Dequeue();
        return pick;
    }

    Song FromBag(string cat, List<Song> pool)
    {
        if (!songBag.TryGetValue(cat, out var played)) songBag[cat] = played = new HashSet<string>();
        var left = pool.Where(s => !played.Contains(s.Stream)).ToList();
        if (left.Count == 0)
        {
            played.Clear();                                   // a new round starts
            left = pool.Where(s => s.Stream != lastSong).ToList();
            if (left.Count == 0) left = pool;
        }
        var pick = left[rng.Next(left.Count)];
        played.Add(pick.Stream);
        return pick;
    }

    string Daypart()
    {
        double h = Hour;
        return h >= 5 && h < 12 ? "morning" : h >= 12 && h < 17 ? "afternoon" : h >= 17 && h < 22 ? "evening" : "night";
    }

    /// <summary>Lines a speech type can use right now (none: the type is unavailable).</summary>
    (List<string> lines, string label) LinesFor(string type, Song song, string? next)
    {
        List<string> L(string k) => st.Dj.Lines.TryGetValue(k, out var l) ? l : new();
        switch (type)
        {
            case "intro": return (L("intro"), "DJ intro");
            case "outro": return (L("outro"), "DJ outro");
            // dj_speech_category_intro/outro_track_specific ("song_intro" / "song_outro" in older exports)
            case "intro_track_specific" or "song_intro": return (song.Intros, "DJ intro for this song");
            case "outro_track_specific" or "song_outro": return (song.Outros, "DJ outro for this song");
            case "time_of_day": return (L("tod_" + Daypart()), $"DJ time of day ({Daypart()})");
            case "weather": return (L("weather_" + opt.Weather), $"DJ weather ({opt.Weather})");
            case "to":
                if (next == "advert") return (L("to_ads"), "DJ link to ads");
                if (next == "news") return (L("to_news"), "DJ link to news");
                if (next == "music_night" && song.Category == "music") return (L("to_night"), "DJ link to night");
                if (next == "music" && song.Category == "music_night") return (L("to_day"), "DJ link to day");
                return (new(), "");
            default: return (new(), "");
        }
    }

    double DjWeight(DjCond c, Song song, bool intro, string before, string? next) => c.Kind switch
    {
        "const" => c.Value,
        "prev" or "recent" => before == c.Category ? c.A : c.B,
        "next" => next == c.Category ? c.A : c.B,
        "since_speech" => c.Value * (c.Curve?.Eval(SinceSpeech(c.Type ?? "")) ?? 1),
        "after_speech" => lastSpeechType == c.Type ? c.A : c.B,
        "song_has_line" => (intro ? song.Intros.Count : song.Outros.Count) > 0 ? c.A : c.B,
        _ => 1.0,                               // condition classes not decoded yet
    };

    /// <summary>Maybe pick DJ speech for a song's intro or outro window.</summary>
    Voice? Speech(Song song, bool intro, string before, string? next)
    {
        if (!song.Dj.TryGetValue(intro ? "intro_start" : "outro_start", out var at)) return null;
        double chance = (intro ? opt.DjIntroChance : opt.DjOutroChance) ?? st.Dj.SpeechChance ?? 0.5;
        if (rng.NextDouble() >= chance) return null;
        var cands = new List<(DjRule rule, List<string> lines, string label, double w)>();
        foreach (var r in intro ? st.Dj.IntroRules : st.Dj.OutroRules)
        {
            var (lines, label) = LinesFor(r.Type, song, next);
            if (lines.Count == 0) continue;
            double w = r.Conditions.Aggregate(1.0, (acc, c) => acc * DjWeight(c, song, intro, before, next));
            if (w > 0) cands.Add((r, lines, label, w));
        }
        if (cands.Count == 0) return null;
        double x = rng.NextDouble() * cands.Sum(c => c.w);
        var pick = cands[^1];
        foreach (var c in cands) { x -= c.w; if (x <= 0) { pick = c; break; } }
        if (!recentLines.TryGetValue(pick.rule.Type, out var rq)) recentLines[pick.rule.Type] = rq = new Queue<string>();
        var file = Pick(pick.lines, f => f, rq, 0.5);
        lastSpeechType = pick.rule.Type;
        lastSpeech[pick.rule.Type] = Clock;
        return new Voice(file, at, pick.label);
    }

    /// <summary>The running order, forever. Categories without audio pass straight through.</summary>
    public IEnumerable<Segment> Run()
    {
        Song? song = null;                     // the song still waiting for its outro decision
        Segment? pending = null;
        string before = "*";
        while (true)
        {
            var cat = NextCategory();
            if (pending != null && song != null)
            {
                // the song ends: the DJ may talk over its outro, depending on what comes next
                double end = Clock;
                Clock = end - song.Duration + song.Dj.GetValueOrDefault("outro_start");
                var v = Speech(song, intro: false, before, cat);
                Clock = end;
                if (v != null) pending.Voices.Add(v);
                yield return pending;
                pending = null; song = null;
            }
            if (cat is "music" or "music_night")
            {
                var pool = st.Songs.Where(s => s.Category == cat).ToList();
                if (pool.Count == 0) pool = st.Songs;
                song = opt.FullRotation ? FromBag(cat, pool) : Pick(pool, s => s.Stream, recentSongs, opt.NoRepeatFraction);
                lastSong = song.Stream;
                before = prev;
                if (song.Tracks is { Count: > 0 } mix)
                {
                    // a DJ mix plays through, one segment per song in it (no DJ lines over a mix)
                    Mark(cat, song.Duration);
                    for (int i = 0; i < mix.Count; i++)
                    {
                        double a = i == 0 ? 0 : mix[i].At, b = i + 1 < mix.Count ? mix[i + 1].At : song.Duration;
                        if (b - a <= 0) continue;
                        yield return new Segment(cat, song.Src, mix[i].Name.Length > 0 ? mix[i].Name : song.Name, b - a,
                                                 new List<Voice>(), a, $"{song.Name}, song {i + 1} of {mix.Count}");
                    }
                    song = null;
                    continue;
                }
                pending = new Segment(cat, song.Src, song.Name, song.Duration, new List<Voice>());
                var v = Speech(song, intro: true, before, null);
                if (v != null) pending.Voices.Add(v);
                Mark(cat, song.Duration);
                continue;
            }
            if (st.Categories.TryGetValue(cat, out var clips) && clips.Count > 0)
            {
                if (!recentClips.TryGetValue(cat, out var rq)) recentClips[cat] = rq = new Queue<string>();
                var c = Pick(clips, x => x.Stream, rq, 0.5);
                yield return new Segment(cat, c.Src, c.Title.Length > 0 ? c.Title : $"0x{c.Stream}", c.Duration, new List<Voice>());
                Mark(cat, c.Duration);
            }
            else Mark(cat, 0);
        }
    }
}
