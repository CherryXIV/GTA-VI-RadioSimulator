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


After a game update
-------------------
Nothing to do. It notices that the game files changed and reads them again on the next
start. File > "Read game files again" does it on demand.


Command line (optional): RadioSim.exe
-------------------------------------
  RadioSim.exe --game "D:\Games\GTA VI" --list
  RadioSim.exe --station "Flash FM"
  RadioSim.exe --station "VCRR" --seed 1 --minutes 30 --out vcrr.wav
  RadioSim.exe --help                      (all options)

--game is only needed once; the folder is remembered (the GUI and command line share it).


Where it keeps things
---------------------
The "Data" folder next to the program: settings and the station data read from the game
(about 1 MB). Delete it to reset. Keep the program in a folder you can write to; otherwise
(for example in Program Files) it uses %LOCALAPPDATA%\GTA VI Radio Simulator instead.
