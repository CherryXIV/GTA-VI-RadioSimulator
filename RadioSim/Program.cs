using RadioSim;

const string Usage = """
RadioSim - play a GTA VI radio station the way its schedule data describes.

  RadioSim [--game <folder>] --station "<name>" [options]
  RadioSim [--game <folder>] --list

  --game <folder>      your game folder (the one with audio.rpf and data.rpf). Remembered, so
                       you only pass it once. The first run reads the station data from the
                       game files (about a minute) and caches it; after a game update that
                       happens again by itself.
  --rescan             read the game files again now
  --export <file.json> write the station data read from the game to a file

  --station <name>     station to play (e.g. "Flash FM"); a unique prefix is enough
  --seed <n>           repeatable run; leave out for a fresh shuffle every time
  --out <file.wav>     render to a WAV file instead of playing (also writes <file>.txt:
                       song timestamps in YouTube chapter format)
  --minutes <m>        stop after m minutes of station time (default: forever, or 60 with --out)
  --plan               only print the running order, no audio
  --dj-intro <0..1>    override the station's DJ speech chance for song intros
  --dj-outro <0..1>    override it for outros and links (default: the station's own, 0.5 or 1.0)
  --hour <0..24>       start time of day for the DJ's time-of-day lines (default: now)
  --weather good|bad   weather the DJ talks about (default good)
  --rotation           play every song once before any song repeats
""";

string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);

if (args.Length == 0 || Flag("--help") || Flag("-h")) { Console.WriteLine(Usage); return 0; }
Console.OutputEncoding = System.Text.Encoding.UTF8;

var settings = Settings.Load();
var gameArg = Arg("--game") ?? settings.GameFolder;
if (gameArg == null) { Console.Error.WriteLine("Pass --game <folder>: your game folder, with audio.rpf and data.rpf."); return 1; }
var game = RadioSim.Game.GameIndex.FindGameFolder(gameArg);
if (game == null) { Console.Error.WriteLine($"audio.rpf and data.rpf not found in {gameArg}"); return 1; }
if (settings.GameFolder != game) { settings.GameFolder = game; settings.Save(); }
ScheduleFile sched;
try { sched = RadioSim.Game.GameIndex.Load(game, m => Console.Error.WriteLine(m), Flag("--rescan")); }
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not read the game files: {e.Message}");
    return 1;
}
if (Arg("--export") is { } exp) { RadioSim.Game.GameIndex.Save(sched, exp); Console.WriteLine($"wrote {exp}"); }
// debugging aid: --decode <bank:stream> <out.wav> writes one clip
if (Arg("--decode") is { } one && args.Length > Array.IndexOf(args, "--decode") + 2)
{
    using var ga = new GameAudio(sched.Game);
    using var w = new WavWriter(args[Array.IndexOf(args, "--decode") + 2]);
    w.Write(ga.Decode(one));
    return 0;
}
if (Arg("--station") == null && !Flag("--list") && (Flag("--rescan") || Arg("--export") != null)) return 0;

if (Flag("--list"))
{
    foreach (var (name, s) in sched.Stations.OrderBy(k => k.Key))
        Console.WriteLine($"  {name,-28} {s.Songs.Count,3} songs  {s.Categories.GetValueOrDefault("advert")?.Count ?? 0,3} ads  " +
                          $"{s.Songs.Count(x => x.Intros.Count > 0),3} songs with DJ intros");
    return 0;
}

var want = Arg("--station");
if (want == null) { Console.Error.WriteLine("--station is required (see --list)"); return 1; }
var match = sched.Stations.Keys.Where(k => k.Equals(want, StringComparison.OrdinalIgnoreCase)).ToList();
if (match.Count == 0) match = sched.Stations.Keys.Where(k => k.StartsWith(want, StringComparison.OrdinalIgnoreCase)).ToList();
if (match.Count != 1) { Console.Error.WriteLine($"station '{want}' {(match.Count == 0 ? "not found" : "is ambiguous")} (see --list)"); return 1; }
var station = sched.Stations[match[0]];

int seed = int.TryParse(Arg("--seed"), out var sd) ? sd : Random.Shared.Next();
var opt = new Options();
if (double.TryParse(Arg("--dj-intro"), System.Globalization.CultureInfo.InvariantCulture, out var di)) opt.DjIntroChance = di;
if (double.TryParse(Arg("--dj-outro"), System.Globalization.CultureInfo.InvariantCulture, out var dout)) opt.DjOutroChance = dout;
if (double.TryParse(Arg("--hour"), System.Globalization.CultureInfo.InvariantCulture, out var hr)) opt.StartHour = hr;
if (Arg("--weather") is { } wth) opt.Weather = wth.ToLowerInvariant() == "bad" ? "bad" : "good";
opt.FullRotation = Flag("--rotation");
var outWav = Arg("--out");
double minutes = double.TryParse(Arg("--minutes"), System.Globalization.CultureInfo.InvariantCulture, out var m) ? m
               : outWav != null || Flag("--plan") ? 60 : double.PositiveInfinity;

Console.WriteLine($"{match[0]}  (seed {seed})");
var sch = new Scheduler(station, seed, opt);

if (Flag("--plan"))
{
    double t = 0;
    foreach (var seg in sch.Run())
    {
        Print(seg, t);
        t += seg.Duration;
        if (t >= minutes * 60) break;
    }
    return 0;
}

using var audio = new GameAudio(sched.Game);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// decode one segment ahead so playback never waits
float[] Render(Segment seg) => audio.Render(seg);

WavWriter? wav = outWav != null ? new WavWriter(outWav) : null;
var tracklist = new Tracklist();
WaveOutPlayer? player = wav == null ? new WaveOutPlayer() : null;
try
{
    double clock = 0;
    using var it = sch.Run().GetEnumerator();
    it.MoveNext();
    var cur = it.Current;
    var curPcm = Task.Run(() => Render(cur));
    while (!cts.IsCancellationRequested && clock < minutes * 60)
    {
        var pcm = curPcm.Result;
        it.MoveNext();
        var nextSeg = it.Current;
        curPcm = Task.Run(() => Render(nextSeg));
        Print(cur, clock);
        tracklist.Add(cur, clock);
        if (wav != null) wav.Write(pcm); else player!.Play(pcm, cts.Token);
        clock += pcm.Length / 2.0 / GameAudio.Rate;
        cur = nextSeg;
    }
    player?.Drain();
}
finally
{
    player?.Dispose();
    wav?.Dispose();
    if (outWav != null)
    {
        double total = new FileInfo(outWav).Length > 44 ? (new FileInfo(outWav).Length - 44) / 8.0 / GameAudio.Rate : 0;
        tracklist.Write(Path.ChangeExtension(outWav, ".txt"), match[0], seed, total);
    }
}
if (outWav != null) Console.WriteLine($"wrote {outWav} and {Path.ChangeExtension(outWav, ".txt")}");
return 0;

void Print(Segment seg, double t)
{
    var ts = TimeSpan.FromSeconds(t);
    var what = seg.Category switch
    {
        "music" or "music_night" => seg.Label,
        "advert" => $"Advert {seg.Label}",
        _ => $"{seg.Category} {seg.Label}",
    };
    Console.WriteLine($"[{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}] {what}" +
                      string.Concat(seg.Voices.Select(v => $"   + {v.Label} at {v.At:0.0}s")));
}
