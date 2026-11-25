using System.Text.Json;

namespace RadioSim;

/// <summary>Remembers the game folder between runs: Data\settings.json next to the program.</summary>
public sealed class Settings
{
    public string? GameFolder { get; set; }

    // the window's choices, restored on the next start (the command line ignores them)
    public string? LastStation { get; set; }
    public int Volume { get; set; } = 80;
    public int DjTalk { get; set; }
    public string Weather { get; set; } = "good";
    public bool Rotation { get; set; }
    public bool MediaKeys { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool SongNotifications { get; set; }
    public bool NowPlayingFile { get; set; }

    static string? dataDir;

    /// <summary>The "Data" folder next to the program, where the settings and the station data read
    /// from the game are kept. If that folder can't be written (e.g. the program sits in Program
    /// Files), %LOCALAPPDATA%\GTA VI Radio Simulator is used instead.</summary>
    public static string DataDir => dataDir ??= PickDataDir();

    static string PickDataDir()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Data");
        try
        {
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return local;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTA VI Radio Simulator");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    static string PathOf => Path.Combine(DataDir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(PathOf), JsonContext.Default.Settings) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(PathOf, JsonSerializer.Serialize(this, JsonContext.Default.Settings)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // only a preference: keep going
    }
}
