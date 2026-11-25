namespace RadioSim;

/// <summary>
/// Song timestamps for a render, written next to the WAV in YouTube's chapter format:
/// the first line is 0:00 (the station name when a song does not start the render), then one
/// line per song (or podcast episode). Untitled streams are left out; everything else (ads,
/// idents, DJ links) falls inside the chapter of the song before it.
/// </summary>
public sealed class Tracklist
{
    readonly List<(double at, string title)> songs = new();
    Segment? last;

    public void Add(Segment seg, double at)
    {
        bool more = seg.Continues(last);                         // a later piece of an episode
        last = seg;
        if (more || seg.Category is not ("music" or "music_night" or "podcast")) return;
        if (seg.Label.StartsWith("0x")) return;                  // no title in the game data
        songs.Add((at, seg.Label));
    }

    public static string Stamp(double s, bool hours)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(s)));
        return hours ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    public void Write(string path, string station, int seed, double totalSeconds)
    {
        bool hours = totalSeconds >= 3600;
        var lines = new List<string>();
        // YouTube chapters start at 0:00 and need 10 s between them: a song that starts within
        // the first 10 s takes the 0:00 line, otherwise the station name does
        if (songs.Count == 0 || songs[0].at >= 10) lines.Add($"{Stamp(0, hours)} {station}");
        for (int i = 0; i < songs.Count; i++)
            lines.Add($"{Stamp(i == 0 && songs[0].at < 10 ? 0 : songs[i].at, hours)} {songs[i].title}");
        lines.Add("");
        lines.Add($"{station} · RadioSim seed {seed} · {Stamp(totalSeconds, hours)}");
        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
    }
}
