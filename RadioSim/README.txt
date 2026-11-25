GTA VI Radio Simulator - the radio stations, played from your own game files
============================================================================

It plays the game's radio stations the way the game's own station data describes them:
songs, DJ intros and outros, idents, ad breaks, time-of-day and weather lines.

It contains no music and no game data. Everything is read from YOUR copy of the game while
it plays. Nothing in the game folder is changed.


What you need
-------------
- Windows 10 or 11 (64-bit). Nothing else to install.
- The game's files: the folder that holds audio.rpf and data.rpf. (Made for the Xbox Series
  build's files; other builds are untested.)


Getting started
---------------
1. Unzip this folder anywhere.
2. Run "GTA VI Radio Simulator.exe".
   The app isn't signed, so Windows may show "Windows protected your PC". Click
   "More info", then "Run anyway".
3. The first time, it asks for your game folder. Pick the folder with audio.rpf and data.rpf
   (or the folder just above it).
4. It reads the station data from the game files. This takes a few seconds, or longer on a
   hard drive. Then double-click a station.

Controls: Play / Pause / Skip / Stop. "Render WAV..." saves a mix to a file, plus a .txt of
song timestamps. The Seed box gives the same running order again; untick Shuffle to keep it.

Station logos are read from the game too (data\ttdb\ui\ttdb_ui.ttpf) and shown next to each
station and in "Now playing". File > "Export station logos..." saves them as PNG files.

The two secret stations are included: The Sniper of La Perle and The Vault Radio. The Vault
Radio is one long DJ mix; it plays song by song, so each song shows up in "Now playing", Up
next, the notifications and a render's timestamps. La Perle has a 40-minute mixtape too.

Keys (in the window):
  Space         play / pause
  Ctrl+Right    skip
  Page Down/Up  next / previous station (like flicking through the radio in the game)
The keyboard's media keys (play/pause, next, stop) work too while a station is on, even with
the window in the background (Options > "Use the keyboard's media keys").

"Songs on this station" (next to History) lists the selected station's songs; type in the box
to filter them.

Options menu:
  - Minimize to the notification area: the icon there has Play/pause, Skip and station
    controls; double-click it to bring the window back.
  - Show a notification for each song.
  - Write "now playing" files: keeps Data\Now playing\song.txt, station.txt and station.png
    (the logo) up to date, for a text or image source in OBS. File > 'Open the "now playing"
    folder' opens it.
Volume, DJ talk, weather, "Play every song before repeating" and the last station are
remembered.


After a game update
-------------------
Nothing to do. It notices that the game files changed and reads them again on the next
start. File > "Read game files again" does it on demand.


Command line (optional): RadioSim.exe
-------------------------------------
  RadioSim.exe --game "D:\Games\GTA VI" --list
  RadioSim.exe --station "Flash FM"
  RadioSim.exe --station "VCRR" --seed 1 --minutes 30 --out vcrr.wav
  RadioSim.exe --export-logos logos        (every station logo as a PNG)
  RadioSim.exe --help                      (all options)

--game is only needed once; the folder is remembered (the GUI and command line share it).


Where it keeps things
---------------------
The "Data" folder next to the program: settings, the station data and logos read from the
game (about 2 MB). Delete it to reset. Keep the program in a folder you can write to; otherwise
(for example in Program Files) it uses %LOCALAPPDATA%\GTA VI Radio Simulator instead.
