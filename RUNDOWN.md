# What I added: station logos and more

Everything is on the local branch **`feature/station-logos-and-more`**, checked out in its own folder:
`F:\Programming\Repos\GTA-VI-RadioSimulator-fork` (a git worktree of your repo). Nothing was pushed. Your `main` and its uncommitted changes are untouched.

## Commits (oldest first, all as CherryXIV)

| Commit | What |
|---|---|
| `fb0fc96` | **Your uncommitted work from `main`, unchanged:** clips picked by length, DJ lines named by song title, cracked category names. I committed it first so the branch builds on it. |
| `5d541b8` | **Station logos read straight from the game** (engine and command line) |
| `db99828` | **Window features:** logos, songs tab, keys, media keys, tray icon, now-playing files, remembered settings, no-sound-device fix |
| `62ff78e` | README and user-guide updates |
| (this file) | This rundown |

## 1. Station logos, with no extraction step

Your extractor's `export_logos.py` showed where the logos are. I ported what it relies on to C#, so the simulator reads them from `data\ttdb\ui\ttdb_ui.ttpf` the same way it reads the audio. Users don't need the Python tools.

- `RadioSim/Game/TexturePool.cs`
  - RDS5 chunk table and ETPF texture dictionary.
  - Reads zlib and stored chunks. BCPack chunks are skipped.
  - Gfx10 S4K/D64K untiling, ported from `gta6x.gfx10`.
  - The MurmurHash3 x64 128 texture-name hash.
- `RadioSim/Game/Bc.cs`: BC1, BC3 and BC7 decoders. The logos use BC1 and BC7.
- `RadioSim/Game/StationLogos.cs`
  - Picks the logo name the same way `export_logos.py` does: label or display name, trailing words dropped, exact hash match only.
  - Caches the logos as PNGs in `Data\logos\<pool size-date>\`. A game update gets new logos automatically.
  - Includes a small PNG writer, so the console app needs no image library.
- **Command line:** `RadioSim.exe --export-logos <dir>`.

**Checked against your extractor:** 21 of 22 logos found, The Vault missing, same as `logos.tsv`. I compared every pixel with `F:\GTA6 Audio v3\Radio Logos\*.png`:
- The 6 BC7 logos are **byte-identical**.
- The 15 BC1 logos differ by **at most 2/255** per channel. My BC1 decoder rounds the interpolated colours the D3D way, `(2a+b+1)/3`. The Python one truncates.

Reading and caching all 21 takes well under a second.

## 2. Window (GUI) additions

- **Logos** next to each station in the list, and a large one in **Now playing**.
- **File → Export station logos…** saves them as PNGs.
- **"Songs on this station" tab** next to History:
  - lists artist, title, length and number of DJ lines;
  - night songs are tinted;
  - has a filter box.
- **Keys:** Space play/pause, Ctrl+Right skip, Page Down / Page Up next / previous station. There's also a new **Radio** menu with the same actions.
- **Media keys** (play/pause, next, stop) work even with the window in the background:
  - They are only taken **while a station is on**, so they go back to Spotify or the browser when you stop.
  - You can turn them off under **Options**.
  - If another app already holds a key, that app keeps it.
- **Notification-area icon** with Play/pause, Skip, Next/Previous station, Show window and Exit.
  - The tooltip shows the station and the current item.
  - **Options → Minimize to the notification area**.
  - **Options → Show a notification for each song**.
- **Options → Write "now playing" files** keeps `Data\Now playing\song.txt`, `station.txt` and `station.png` (the logo) up to date, for OBS overlays.
- **File → Open the "now playing" folder** opens that folder.
- **Remembered between runs:** volume, DJ talk, weather, "Play every song before repeating", the last station and the Options toggles. They are new fields in `settings.json`, and the command line ignores them.
- **No-sound-device fix (this bug was already there):** with no audio output, Play used to throw `waveOutOpen failed (2)` as an unhandled-exception dialog. It now shows "No sound output device was found…" in the window.

## How I tested

- `dotnet build RadioSim.slnx -c Release`: 0 warnings, 0 errors.
- `RadioSim.exe --export-logos`: 21 logos, compared pixel by pixel as described above.
- `--list` and `--plan` still work.
- The GUI `--selftest` on Flash FM and Honey FM:
  - window opens;
  - 21 logos load and show in the list and in Now playing;
  - the songs tab fills (83 and 56 songs).

## Not verified

- **Actual audio playback in the window.** The test process had no audio output device, which is how I found the crash above. So these weren't exercised live: the now-playing files, song notifications, media keys during playback, and Page Up/Down tuning. Please try them with speakers connected.
- **Minimize to tray** wasn't clicked through by hand.

## Ideas I didn't do

- **"Play this song next"** from the songs tab. The engine plans 50 items ahead, so a request would need the scheduler to re-plan from a saved state.
- **DJ host names:** the station data has `person` (e.g. `ROBYN`, `BURNABOY`). Showing "Hosted by …" needs a mapping to display names.
- **A radio-wheel style station picker** using the logos.
- **A sleep timer**, i.e. stop after N minutes.

## How to look at it / merge it

```powershell
cd F:\Programming\Repos\GTA-VI-RadioSimulator
git log --oneline main..feature/station-logos-and-more
git diff main...feature/station-logos-and-more --stat
```

Your `main` still has the WIP as uncommitted changes, and the branch's first commit contains that same WIP. Before merging, commit or stash the WIP on `main` so the two don't conflict.

When you're done, `git worktree remove ..\GTA-VI-RadioSimulator-fork` removes the extra folder and keeps the branch.

**Side note:** your global git email is `295001382+CherryXIV@users.noreply.github.com.`, with a trailing dot. GitHub may not link commits made with that address to your account.
