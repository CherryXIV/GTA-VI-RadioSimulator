<p align="center"><img src="icon.png" width="128" alt=""></p>

# GTA VI Radio Simulator

Listen to GTA VI's radio stations outside the game. Each station plays the way the game's own station data describes it: songs, DJ intros and outros, idents, ad breaks, and time-of-day and weather lines.

It comes with **no music and no game data**. It reads everything from your own copy of the game while it plays, and it never changes the game files.

## Download and play

1. Download `GTA VI Radio Simulator.zip` from the [latest release](https://github.com/CherryXIV/GTA-VI-RadioSimulator/releases/latest) and unzip it anywhere you can write to (not Program Files).
2. Run `GTA VI Radio Simulator.exe`. The app isn't signed, so Windows may say "Windows protected your PC". Click **More info**, then **Run anyway**.
3. The first time, choose your game folder: the one that holds `audio.rpf` and `data.rpf`, or the folder just above it.
4. It reads the station data from the game files. This takes a few seconds, or longer on a hard drive. Then double-click a station.

**What you need:** Windows 10 or 11 (64-bit) and the game's files. Nothing else to install. It's made for the Xbox Series build's files; other builds are untested.

**After a game update:** nothing to do. The program notices that the game files changed and reads them again on the next start. **File → Read game files again** does it on demand.

**In the window:**

- **Stations:** double-click one to play it.
- **Controls:** Play, Pause, Skip and Stop.
- **Render WAV…** saves a mix to a file, plus a `.txt` of song timestamps.
- **Settings:** seed (untick Shuffle to get the same running order again), hour, weather, how often the DJ talks, and "Play every song before repeating".
- **Now playing:** the current item and its DJ lines, highlighted while the DJ talks. **Up next** shows the next 50 items and **History** what has played.

**Where it keeps things:** a `Data` folder next to the program holds your game folder setting and the station data read from the game (about 1 MB). Delete it to reset. If the program's folder can't be written to, it uses `%LOCALAPPDATA%\GTA VI Radio Simulator` instead.

### Command line

The zip also has `RadioSim.exe`, which plays or renders from a terminal:

```powershell
.\RadioSim.exe --game "D:\Games\GTA VI" --list         # --game is remembered
.\RadioSim.exe --station "Flash FM"                     # live, new shuffle each time
.\RadioSim.exe --station "Flash FM" --seed 42           # same running order every time
.\RadioSim.exe --station "VCRR" --seed 1 --minutes 30 --out vcrr.wav
.\RadioSim.exe --station "Honey FM" --plan              # running order only
.\RadioSim.exe --station "VCRR" --hour 20 --weather bad # evening, bad-weather lines
.\RadioSim.exe --rescan --export schedule.json          # read the game again, save the station data
.\RadioSim.exe --help                                   # all options
```

## How it works

There is no extraction step. On first run the program reads the station data straight from `audio.rpf` and `data.rpf` and caches it in `Data\`, keyed to the archives' sizes and dates. Nothing is decrypted: it only reads the parts of the archives that are stored as plain or zlib-compressed data.

- **Audio config:** the zlib pages before the first sound bank in `audio.rpf`. They hold the stations, their categories, scheduling tables and DJ rules, and the speech table that lists every DJ line.
- **Text tables:** the zlib pages in `data.rpf`. They hold the station names and song titles.
- **Sound banks:** while a station plays, each clip is decoded from its AWC bank in `audio.rpf`. Opus is decoded in-process (Concentus) at each stream's real playback rate. ffmpeg isn't needed.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build RadioSim.slnx -c Release
& "RadioSim.Gui\bin\Release\net10.0-windows\GTA VI Radio Simulator.exe"
RadioSim\bin\Release\net10.0\RadioSim.exe --help
```

`.\publish.ps1` builds `dist\GTA VI Radio Simulator.zip`, the release download. It holds both exes, self-contained so users need no .NET install, and `RadioSim/README.txt` as the user guide.

- **`RadioSim/`** is the engine and the command line. `Scheduler.cs` decides what plays next, and `Audio.cs` handles decoding, mixing and output.
- **`RadioSim/Game/`** reads the game files:
  - `Awc.cs`: sound banks and Opus decoding
  - `Zlib.cs`: finding and inflating zlib pages
  - `AudioConfig.cs`: game objects, sounds, curves and the speech table
  - `TextTables.cs`: station names and song titles
  - `GameIndex.cs`: builds the stations and caches them
- **`RadioSim.Gui/`** is the window. It compiles the same engine files, so a given seed gives the same running order in both.
- **`icon.ico`** is the program icon. It's made from `icon.png` and used for both exes and the window.
- **Test mode:** `"GTA VI Radio Simulator.exe" --selftest out.png "Station"` plays muted for a few seconds, saves a screenshot and exits.

## Format notes

### Read from the game data

- **What plays next.** Each station has a transition table (class `0x1e`): for the category that just played, the candidate next categories and their conditions. A candidate's weight is the product of:
  - fixed weights (class `0x21`);
  - curves over how many times a category has played in a row (`0x23`);
  - curves over the seconds since it last played (`0x24`).

  Curves come from the curves resource (family 25): step tables and piecewise-linear points.
- **Ad breaks.** On Flash FM, the first ad becomes possible 8 minutes after the last break and grows more likely up to 13 minutes. A break is exactly 3 ads (`[4,4,4,0]` / `[0,0,0,4]`), followed by an ident.
- **Idents** wait at least 5 minutes, and another song only gets weight after a run of 3. When nothing has weight, the station plays music.
- **DJ windows.** The DJ talks inside each song's `intro_start`–`intro_end` / `outro_start`–`outro_end` markers.
- **DJ rules.** Each station has an intro rule list and an outro rule list. Their keys are named `dj_speech_category_<type>`:
  - intro list: `intro`, the song's own intro, `time_of_day`, `weather`;
  - outro list: `outro`, `to`, `time_of_day`, the song's own outro, `weather`.

  Conditions: fixed weights; whether the previous or next category is X (`0x2d` / `0x2e`, e.g. no talk right after a talk segment, no generic outro before ads); a curve over the seconds since this speech type last played (`0x28`; time of day 0 until 500 s, rising to 1.5 by 20 min; weather 0 until 700 s); and the last speech type (`0x29`). At each window the DJ speaks with the station's speech chance (class `0x37`: 0.5, or 1.0 on Back Country, Gloriana Blues, Vice City FM, Emotion 98.3 and Leonida Jazz). Then one available type is drawn by weight.
- **DJ lines** come from the speech voice table: each voice lists its contexts and take counts, and take NN of context C is the stream `joaat("C_NN")`. Contexts are stored as joaat without its final mixing step, so every take resolves without knowing its name. Named contexts: `dj_intro`, `dj_outro`, `dj_to_ads`, `dj_to_news`, `dj_to_night`, `dj_to_day`, `dj_weather_good`, `dj_weather_bad`, `dj_tod_morning`, `dj_tod_afternoon`, `dj_tod_evening`, and `dj_intro_<song>` / `dj_outro_<song>`. The rest stay `ctx_<hash>`.

### Not decoded (our defaults)

- Two rule-key names are not recovered. They are labelled `song_intro` / `song_outro` from their role.
- Classes `0x34`/`0x35` are read as "the song has its own line" (they switch the generic lines off). That is inferred.
- `0xc5` (Back Country) and `0x27` (Circoloco) count as 1.
- Time of day uses `--hour` (default: now): morning 5–12, afternoon 12–17, evening 17–22, and no time-of-day lines at night. Weather uses `--weather good|bad`.
- The game's random number generator and song-rotation code are in the executable, which isn't readable. RadioSim uses .NET `Random(seed)` and skips songs from the most recent half of the station's list. With `--rotation` (GUI: "Play every song before repeating") it plays the whole list in a fresh random order each round instead.
- On tune-in, categories count from a random "station age" of 0–15 minutes.
- News has no station clips, so it never plays.
