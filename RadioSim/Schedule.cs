using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadioSim;

// ---- the radio schedule, built from the game files by Game.GameIndex -------------------------

/// <summary>JSON for the station-data cache and the settings, generated at compile time (no
/// reflection at run time, so it also works if the programs are ever published trimmed).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScheduleFile))]
[JsonSerializable(typeof(Settings))]
internal partial class JsonContext : JsonSerializerContext { }

public sealed class ScheduleFile
{
    [JsonPropertyName("version")] public int Version { get; set; }
    /// <summary>Sizes and dates of the archives it was read from (see GameIndex.GameKey).</summary>
    [JsonPropertyName("game_key")] public string GameKey { get; set; } = "";
    [JsonPropertyName("stations")] public Dictionary<string, Station> Stations { get; set; } = new();
    /// <summary>The podcasts (by name), each a list of episodes in episode order.</summary>
    [JsonPropertyName("podcasts")] public Dictionary<string, List<Episode>> Podcasts { get; set; } = new();
    /// <summary>The game folder the clips are read from (not stored: the cache is per machine).</summary>
    [JsonIgnore] public string Game { get; set; } = "";
}

public sealed class Station
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("person")] public string Person { get; set; } = "";
    [JsonPropertyName("dj")] public DjInfo Dj { get; set; } = new();
    [JsonPropertyName("transitions")] public Dictionary<string, List<Option>> Transitions { get; set; } = new();
    [JsonPropertyName("categories")] public Dictionary<string, List<Clip>> Categories { get; set; } = new();
    [JsonPropertyName("songs")] public List<Song> Songs { get; set; } = new();

    /// <summary>The station's songs for listing: its own songs, then the songs of its DJ mixes that
    /// it doesn't also have on their own (with the length of their stretch of the mix). A song in
    /// both the day and the night list (all of Circoloco's) is listed once.</summary>
    public List<(string Artist, string Title, double Duration, Song Song, bool InMix)> SongRows()
    {
        var own = Songs.DistinctBy(s => s.Src).ToList();
        var rows = own.Where(s => s.Tracks == null).Select(s => (s.Artist, s.Title, s.Duration, s, false)).ToList();
        var have = rows.Select(r => r.s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mix in own.Where(s => s.Tracks != null))
            for (int i = 0; i < mix.Tracks!.Count; i++)
            {
                var t = mix.Tracks[i];
                double end = i + 1 < mix.Tracks.Count ? mix.Tracks[i + 1].At : mix.Duration;
                if (have.Add(t.Name)) rows.Add((t.Artist, t.Title, end - (i == 0 ? 0 : t.At), mix, true));
            }
        return rows;
    }
}

public sealed class DjInfo
{
    [JsonPropertyName("lines")] public Dictionary<string, List<string>> Lines { get; set; } = new();
    /// <summary>First value of the station's speech settings object (class 0x37): 0.5 or 1.0.</summary>
    [JsonPropertyName("speech_chance")] public double? SpeechChance { get; set; }
    [JsonPropertyName("intro_rules")] public List<DjRule> IntroRules { get; set; } = new();
    [JsonPropertyName("outro_rules")] public List<DjRule> OutroRules { get; set; } = new();
}

/// <summary>One kind of DJ speech (intro, intro_track_specific, time_of_day, weather, outro, outro_track_specific, to)
/// and the conditions whose product is its weight.</summary>
public sealed class DjRule
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("conditions")] public List<DjCond> Conditions { get; set; } = new();
}

public sealed class DjCond
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("value")] public double Value { get; set; } = 1;
    [JsonPropertyName("a")] public double A { get; set; }
    [JsonPropertyName("b")] public double B { get; set; } = 1;
    [JsonPropertyName("curve")] public Curve? Curve { get; set; }
}

public sealed class Clip
{
    [JsonPropertyName("stream")] public string Stream { get; set; } = "";
    /// <summary>Where the audio is: "&lt;bank offset&gt;:&lt;stream ID&gt;" in audio.rpf (hex).</summary>
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("duration")] public double Duration { get; set; }
    /// <summary>"Artist - Title" when the clip carries a track ID the text tables name.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
}

public sealed class Song
{
    [JsonPropertyName("stream")] public string Stream { get; set; } = "";
    /// <summary>Where the audio is: "&lt;bank offset&gt;:&lt;stream ID&gt;" in audio.rpf (hex).</summary>
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "music";
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("dj")] public Dictionary<string, double> Dj { get; set; } = new();
    /// <summary>DJ lines for this song (stream refs, like <see cref="Src"/>).</summary>
    [JsonPropertyName("intros")] public List<string> Intros { get; set; } = new();
    [JsonPropertyName("outros")] public List<string> Outros { get; set; } = new();
    /// <summary>For a DJ mix (one stream carrying several TRACKID markers): the songs in it and where
    /// each starts. Null for an ordinary song.</summary>
    [JsonPropertyName("tracks")] public List<MixTrack>? Tracks { get; set; }

    public string Name => Artist.Length > 0 && Title.Length > 0 ? $"{Artist} - {Title}"
        : Artist.Length > 0 ? Artist : Title.Length > 0 ? Title : $"0x{Stream}";
}

/// <summary>One podcast episode: a single stream, titled from the text tables.</summary>
public sealed class Episode
{
    [JsonPropertyName("stream")] public string Stream { get; set; } = "";
    /// <summary>Where the audio is: "&lt;bank offset&gt;:&lt;stream ID&gt;" in audio.rpf (hex).</summary>
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("duration")] public double Duration { get; set; }
}

/// <summary>One song inside a DJ mix: its start (seconds into the mix) and its name from the text tables.</summary>
public sealed class MixTrack
{
    [JsonPropertyName("at")] public double At { get; set; }
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    public string Name => Artist.Length > 0 && Title.Length > 0 ? $"{Artist} - {Title}" : Artist.Length > 0 ? Artist : Title;
}

public sealed class Option
{
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("conditions")] public List<Condition> Conditions { get; set; } = new();
}

public sealed class Condition
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("curve")] public Curve? Curve { get; set; }
    [JsonPropertyName("of")] public List<Condition> Of { get; set; } = new();
}

public sealed class Curve
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("min")] public double Min { get; set; }
    [JsonPropertyName("max")] public double Max { get; set; }
    [JsonPropertyName("values")] public List<double> Values { get; set; } = new();
    [JsonPropertyName("points")] public List<double[]> Points { get; set; } = new();

    /// <summary>Step tables are indexed by the (integer) input; linear curves interpolate between
    /// points. Both are clamped to the curve's [min, max] output range.</summary>
    public double Eval(double x)
    {
        double y;
        if (Kind == "step" && Values.Count > 0)
        {
            int i = (int)Math.Clamp(Math.Floor(x), 0, Values.Count - 1);
            y = Values[i];
        }
        else if (Kind == "linear" && Points.Count > 0)
        {
            if (x <= Points[0][0]) y = Points[0][1];
            else if (x >= Points[^1][0]) y = Points[^1][1];
            else
            {
                y = Points[^1][1];
                for (int i = 1; i < Points.Count; i++)
                {
                    var (x0, y0, x1, y1) = (Points[i - 1][0], Points[i - 1][1], Points[i][0], Points[i][1]);
                    if (x <= x1)
                    {
                        y = x1 == x0 ? y1 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);
                        break;
                    }
                }
            }
        }
        else return 1.0;
        return Math.Clamp(y, Math.Min(Min, Max), Math.Max(Min, Max));
    }
}
