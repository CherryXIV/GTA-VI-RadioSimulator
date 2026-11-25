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
- **Podcasts:** the tab next to Stations lists the in-game podcasts by episode. Double-click an episode to play from it to the podcast's last episode; Skip goes to the next episode. **Render WAV…** works for podcasts too.
- **Controls:** Play, Pause, Skip and Stop.
- **Render WAV…** saves a mix to a file, plus a `.txt` of song timestamps.
- **Settings:** seed (untick Shuffle to get the same running order again), hour, weather, how often the DJ talks, and "Play every song before repeating".
- **Now playing:** the station's logo, the current item and its DJ lines, highlighted while the DJ talks. **Up next** shows the next 50 items, **History** what has played, and **Songs on this station** the selected station's songs (with a filter box). Songs in blue are on the station's night playlist: some stations (Honey FM, for one) have separate day and night song lists, and the station's own scheduling decides when it switches between them. Hover over the tab or a song for a reminder.
- **Station logos** are read from the game's UI textures and shown in the station list. **File → Export station logos…** saves them as PNGs.
- **Keys:** Space plays/pauses, Ctrl+Right skips, Page Down / Page Up go to the next / previous station. The keyboard's media keys (play/pause, next, stop) work while a station is on, even in the background.
- **Options:** minimize to the notification area, a notification for each song, and "now playing" files (`Data\Now playing\song.txt`, `station.txt`, `station.png`) for a text or image source in OBS. Volume, DJ talk, weather, rotation and the last station are remembered.

**Where it keeps things:** a `Data` folder next to the program holds your settings and the station data and logos read from the game (about 2 MB). Delete it to reset. If the program's folder can't be written to, it uses `%LOCALAPPDATA%\GTA VI Radio Simulator` instead.

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
.\RadioSim.exe --export-logos logos                     # every station logo as a PNG
.\RadioSim.exe --podcast "Drunk Kings"                  # a podcast, first episode to last
.\RadioSim.exe --podcast "Final Act" --episode 3 --out fa.wav
.\RadioSim.exe --help                                   # all options
```

## How it works

There is no extraction step. On first run the program reads the station data straight from `audio.rpf` and `data.rpf` and caches it in `Data\`, keyed to the archives' sizes and dates. Nothing is decrypted: it only reads the parts of the archives that are stored as plain or zlib-compressed data.

- **Audio config:** the zlib pages before the first sound bank in `audio.rpf`. They hold the stations, their categories, scheduling tables and DJ rules, and the speech table that lists every DJ line.
- **Text tables:** the zlib pages in `data.rpf`. They hold the station names and song titles.
- **Station logos:** 256×256 textures in the UI texture pool `data\ttdb\ui\ttdb_ui.ttpf` (a loose file, not in an archive). See [Station logos](#station-logos) below.
- **Sound banks:** while a station plays, each clip is decoded from its AWC bank in `audio.rpf`. Opus is decoded in-process (Concentus) at each stream's real playback rate. ffmpeg isn't needed. Streamed banks reuse channel IDs, so one clip ID can be in many banks; the bank whose stream length matches the clip's sound record (within 0.05 s) is used.

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build RadioSim.slnx -c Release
& "RadioSim.Gui\bin\Release\net10.0-windows\GTA VI Radio Simulator.exe"
RadioSim\bin\Release\net10.0\RadioSim.exe --help
```

`.\publish.ps1` builds `dist\GTA VI Radio Simulator.zip`, the release download. It holds both exes, self-contained so users need no .NET install, `RadioSim/README.txt` as the user guide, and `THIRD_PARTY_NOTICES.md`.

- **`RadioSim/`** is the engine and the command line. `Scheduler.cs` decides what plays next, and `Audio.cs` handles decoding, mixing and output.
- **`RadioSim/Game/`** reads the game files:
  - `Awc.cs`: sound banks and Opus decoding
  - `Zlib.cs`: finding and inflating zlib pages
  - `AudioConfig.cs`: game objects, sounds, curves and the speech table
  - `TextTables.cs`: station names and song titles
  - `GameIndex.cs`: builds the stations and caches them
  - `TexturePool.cs`: RDS5/ETPF texture pools and the Gfx10 tiling; `Bc.cs`: BC1/BC3/BC7 decoding; `BcPack.cs`: BCPack BC7 chunks (a port of bcpack-rs)
  - `StationLogos.cs`: finds each station's logo, caches it as a PNG
- **`RadioSim.Gui/`** is the window. It compiles the same engine files, so a given seed gives the same running order in both.
- **`icon.ico`** is the program icon. It's made from `icon.png` and used for both exes and the window.
- **Test mode:** `"GTA VI Radio Simulator.exe" --selftest out.png "Station"` plays muted for a few seconds, saves a screenshot and exits.

## Format notes

### Station logos

The logos live in the UI texture pool `data\ttdb\ui\ttdb_ui.ttpf`. The extractor's `export_logos.py` worked this out; `TexturePool.cs` is a C# port of the parts it uses (`gta6x.rds5`, `gta6x.texture`, `gta6x.gfx10`).

- **Pool:** `RDS5` header, then a chunk table. Each `u64` entry packs the source offset / 256 (bits 0–25), the codec (26–29: 0 stored, 8 zlib, others BCPack), the virtual offset / 4096 (30–51) and padding (52–55). The chunks make a virtual address space.
- **Dictionary:** an `ETPF` block (version 634) at the next 256-byte boundary. It has 16 bytes per texture (width, height, mips, DXGI format, tile mode, first virtual page), then one 64-bit name hash per texture.
- **Names:** the hash is the low 64 bits of MurmurHash3 x64 128 (seed 0) of the texture name. A station's logo is `radio_<station>`. The program tries the station's audio label and its display name, each with trailing words dropped, and keeps the first exact hash match (`radio_kaleidoscope`, `radio_leonida_jazz`, `radio_worldwide_fm`). 21 of 23 stations match. The two secret stations' covers aren't named `radio_<station>`: they were found by comparing the pool's textures with the cover art, and are looked up by their hash (`StationLogos.KnownHashes`).
- **Pixels:** the logos are BC1 or BC7, one mip, tile mode 1 (Gfx10 `SW_4KB_S`): 4 KiB blocks of 32×16 (BC1) or 16×16 (BC7) compressed blocks, with a fixed x/y bit order inside each one. Every logo but one sits in plain zlib chunks. The Vault Radio's is in a BCPack chunk, see [BCPack](#bcpack) below. The output matches the extractor's PNGs exactly for BC7. For BC1 it differs by at most 2/255 per channel, because the D3D rule rounds the interpolated colours where the Python decoder truncates them.

### BCPack

About a third of the UI pool's chunks (codec field `(a >> 24) & 0x3C` of 0x04 BC1, 0x10 BC4, 0x1C BC7) are BCPack: the Xbox Series' texture codec, BC blocks entropy-coded as one byte stream feeding 12 or 16 interleaved rANS lanes. `BcPack.cs` decodes the BC7 ones. It is a port of the BC7 path of [bcpack-rs](https://github.com/Ahmet-Dedeler/bcpack-rs) by Ahmet Dedeler (MIT OR Apache-2.0): its cycle-level model of the MDU rANS decoder (from Microsoft's MIT-licensed [silicon-rans](https://github.com/microsoft/silicon-rans) Verilog), and its BC7 block rebuild (two back references per block into the last 255 blocks or a fixed dictionary, zigzag endpoint and index residuals, a 9-bit control code for mode, partition, rotation and index selection). `BcPackTables.cs` holds its tables and is generated by `tools/gen_bcpack_tables.py` from a bcpack-rs checkout. The port gives byte-identical blocks to bcpack-rs on all 915 BC7 chunks of the UI pool. BC1 and BC4 chunks are still skipped.

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
  - intro list: `intro`, the song's own intro (`intro_track_specific`), `time_of_day`, `weather`;
  - outro list: `outro`, `to`, `time_of_day`, the song's own outro (`outro_track_specific`), `weather`.

  Conditions: fixed weights; whether the previous or next category is X (`0x2d` / `0x2e`, e.g. no talk right after a talk segment, no generic outro before ads); a curve over the seconds since this speech type last played (`0x28`; time of day 0 until 500 s, rising to 1.5 by 20 min; weather 0 until 700 s); and the last speech type (`0x29`). At each window the DJ speaks with the station's speech chance (class `0x37`: 0.5, or 1.0 on Back Country, Gloriana Blues, Vice City FM, Emotion 98.3 and Leonida Jazz). Then one available type is drawn by weight.
- **Which sound a track item plays.** A track item points at a sound in the game-object reference table; the sound index gives an offset into the sound records (`[0c 01][u32 id][0c 00 or 0c 11 00 40 03][hash_a][hash_b]`, where `hash_b` is the stream). The offset lands on the id of the record just *before* the sound's own, so the sound's record is the next one (`AudioConfig.Record`). This is checked against the track items that also name their metadata object, whose stream is `joaat(name)`: the next record is right for 557 of 558. Taking the record at the offset instead shifts every playlist by one: each list's last song shows up at the head of the next list (Real Dimez on Vice City FM, Schubert on The Chamber) and its own last song is lost.
- **Secret stations.** *The Sniper of La Perle* is the station object `hidden_radio_<pack>` (`hidden_radio_haitian_mixtape`): the label of a station pack with `hidden_radio_` in front. Its name string isn't keyed by that label, so the program names it from a small table, and only if that exact string is in the text tables. *The Vault Radio* is an ordinary station object whose playlist is one 83-minute DJ mix. Neither is on the radio wheel list.
- **DJ mixes.** The Vault Radio, Worldwide FM (2 hours, 39 songs) and Radio On-U (16 songs) each play one long stream with a TRACKID marker at each song change. The songs are named from the text tables; a marker the text tables don't name (Worldwide FM's 13-second intro) joins the song next to it. A mix plays song by song: each song is a separate item in the running order, decoded on its own (`AwcArchive.Decode` with a start and length, starting 160 ms early so the Opus decoder has settled), so a mix is never decoded whole (about 4 GB of samples for Worldwide FM's).
- **Podcasts.** Each podcast is a list of track items whose sounds are one episode each (5 to 15 minutes), on no station. An episode's stream carries one TRACKID marker, and the text table names it: the podcast in the artist field, the episode in the title (track IDs 10001–14006: Drunk Kings, Vice City True Stories, Final Act, The Safe Trap, Fuck, Marry, Murder). The program takes every list of such sounds not used by a station and keeps those whose markers name episodes of one show; talk-radio show parts (WKTT, The Wipe) carry only the show's name and are left out. An episode without a marker (two of Fuck, Marry, Murder's) takes the text entry its place in the list implies. Episodes are sorted by their `#` number when they all have one (Drunk Kings' list isn't in order). An episode plays in 3-minute pieces, each decoded on its own, shown as one item.
- **DJ lines** come from the speech voice table: each voice lists its contexts and take counts, and take NN of context C is the stream `joaat("C_NN")`. Contexts are stored as joaat without its final mixing step, so every take resolves without knowing its name. Named contexts: `dj_intro`, `dj_outro`, `dj_to_ads`, `dj_to_news`, `dj_to_night`, `dj_to_day`, `dj_weather_good`, `dj_weather_bad`, `dj_tod_morning`, `dj_tod_afternoon`, `dj_tod_evening`, and `dj_intro_<song>` / `dj_outro_<song>`, where `<song>` is the track's metadata ID or its title as a token (`dj_intro_dont_chart` for "Don't Chart"). The rest stay `ctx_<hash>`.

### Not decoded (our defaults)

- Classes `0x34`/`0x35` are read as "the song has its own line" (they switch the generic lines off). That is inferred.
- `0xc5` (Back Country) and `0x27` (Circoloco) count as 1.
- Time of day uses `--hour` (default: now): morning 5–12, afternoon 12–17, evening 17–22, and no time-of-day lines at night. Weather uses `--weather good|bad`.
- The game's random number generator and song-rotation code are in the executable, which isn't readable. RadioSim uses .NET `Random(seed)` and skips songs from the most recent half of the station's list. With `--rotation` (GUI: "Play every song before repeating") it plays the whole list in a fresh random order each round instead.
- On tune-in, categories count from a random "station age" of 0–15 minutes.
- News has no station clips, so it never plays.

## Credits

- [bcpack-rs](https://github.com/Ahmet-Dedeler/bcpack-rs) by Ahmet Dedeler: the BCPack decoder that `BcPack.cs` ports, which is how The Vault Radio's logo is read. Its rANS tables come from Microsoft's [silicon-rans](https://github.com/microsoft/silicon-rans). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- [Concentus](https://github.com/lostromb/concentus): Opus decoding.
