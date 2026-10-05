# QuickClip

A lightweight instant-replay clipper and clip editor for Windows. QuickClip keeps the last few minutes of your
screen in memory, saves them as a clip when you press a hotkey, files each clip under the game you were playing,
and lets you trim, crop and remix the audio tracks before sharing.

## Recording

- **Instant replay** from 10 seconds up to 5 minutes, held in RAM (Settings shows how much memory that takes).
  Nothing is written to disk until you save a clip.
- **Any monitor**, including virtual displays (DesktopSplitter, Duet, …).
- **Separate audio tracks**: *Desktop* (everything else), *Chat* (Discord, TeamSpeak, Mumble, Teams, Zoom, Skype,
  Slack and other voice apps, detected automatically) and *Mic*.
- **Quality presets** Low → Medium → High → Ultra → Indistinguishable, with **frame rate** (30–240 fps) and
  **output resolution** set independently.
- **GPU encoding**, newest codec first: AV1 on NVIDIA/AMD/Intel cards that support it, then HEVC, then H.264.
  Capture and encoding stay on the GPU (about 2% of one CPU core while recording).
- **Hotkey** of your choice (default Alt+F10), tray icon, start with Windows, minimize/close to tray.
- **Game detection**: clips go in a folder per game (from Windows' game list, the Steam/Epic/Riot/GOG/EA/Ubisoft/Xbox
  install folder, or a fullscreen window); anything else goes in *Desktop*.

## Gallery and editor

- QuickClip opens on the **gallery**: *All clips* first, then a folder per game, with thumbnails, lengths and how long
  ago each clip was made.
- The **editor** previews video with every audio track playing at once, each at its own volume (0–200%), shows a
  waveform per track, trims frame-accurately, crops with aspect presets, and exports as a new file or over the
  original. Tracks can be kept separate or merged into one.
- Opens practically anything FFmpeg reads; exports MP4, MKV, MOV, WebM, GIF, MP3, M4A, WAV, FLAC, Opus.
- **Right-click → Edit with QuickClip** on video and audio files in Explorer.

## Install

Download **QuickClip-<version>-setup.exe** from [Releases](https://github.com/swiiftex/QuickClip/releases) and run it.
It installs for your user only (no admin prompt) into `%LOCALAPPDATA%\Programs\QuickClip`, adds a Start menu
shortcut, and offers to add the right-click entry, start QuickClip when you sign in, and a desktop shortcut.
Uninstall it from **Settings → Apps**. Nothing else needs installing (.NET is included).

Prefer no installer? The **-win-x64.zip** on the same page runs from any folder.

QuickClip checks for new releases when it starts (turn that off under **Settings → Updates**) and can install them
for you; installed copies update by running the new setup silently, unzipped copies replace their own files.

Requires Windows 10 version 2004 or later. Separating voice chat from desktop audio needs Windows 11 (or Windows 10
build 20348+); on older Windows 10 the Desktop track records all system audio and the Chat track stays empty.

On Windows 11 the right-click entry sits under **Show more options**. To put it on the main menu, see
`scripts\add-win11-menu.ps1` (it builds and signs a small app package; trusting its certificate needs one UAC prompt).

## Using it

1. Play. When something happens, press **Alt+F10** (or click **Save clip**). You'll hear a sound and get a
   notification; the clip appears in the gallery.
2. Double-click a clip to open it in the editor. Trim with **I** / **O**, set track volumes, tick **Crop video** if
   needed, and **Export as new file** or **Save over original**.

### Editor keys

| Key | Action |
| --- | --- |
| Space | Play / pause |
| ← / → | Previous / next frame |
| Shift+← / → · Ctrl+← / → | ±1 second · ±5 seconds |
| I / O · Shift+I / O | Set in / out · jump to in / out |
| Home / End | Start / end of file |
| L · C | Loop selection · crop on/off |
| + / − / 0 | Zoom timeline in / out / fit (or mouse wheel; Shift+wheel scrolls) |
| Ctrl+O / Ctrl+E / Ctrl+S | Open / export as new file / save over original |

### Command line

| | |
| --- | --- |
| `QuickClip.exe <file>` | Open a file in the editor (in the running QuickClip if there is one) |
| `QuickClip.exe --tray` | Start hidden in the tray (what start with Windows uses) |
| `QuickClip.exe --save-clip` | Save a clip from the running QuickClip (e.g. from a Stream Deck) |
| `QuickClip.exe --quit` | Stop the running QuickClip |

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Visual Studio's C++ build tools (for the
recording engine).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-deps.ps1   # FFmpeg (+ headers), libmpv and Inno Setup into .\deps
dotnet build                                                    # also builds the C++ recording engine
dotnet test                                                     # real exports, preview mixing and a short capture
powershell -ExecutionPolicy Bypass -File scripts\package.ps1    # release: installer + portable zip in .\publish
```

The version comes from `<Version>` in `src/QuickClip/QuickClip.csproj`. To release, attach both files from `publish`
to a GitHub release tagged `v<version>` (mark betas as pre-releases); running copies pick it up on their next check.

Settings live in `%APPDATA%\QuickClip`, thumbnails and the log in `%LOCALAPPDATA%\QuickClip`.

## How it works

- **Recording engine** (`src/QuickClip.Capture`, C++): FFmpeg's Desktop Duplication source (or Windows Graphics
  Capture when scaling) feeds GPU frames straight into the hardware encoder. Audio comes from WASAPI process
  loopback: *Desktop* captures everything except the chat app's process tree, *Chat* only the chat apps, *Mic* the
  microphone. Every stream is timestamped on the same clock and kept as encoded packets in a RAM ring buffer; saving
  writes the packets from the last keyframe before the start point into an MP4 with named tracks.
- **Preview** uses [libmpv](https://mpv.io) embedded in a native child window; the tracks are merged by mpv's
  `lavfi-complex` and remixed per track by a volume filter that the sliders adjust live.
- **Export** builds an FFmpeg command line (`src/QuickClip/Media/Export.cs`).

## License

QuickClip is free software under the [GNU GPL v3](LICENSE). It ships [FFmpeg](https://ffmpeg.org) (BtbN GPL build)
and libmpv ([shinchiro build](https://github.com/shinchiro/mpv-winbuild-cmake)), both GPL-licensed; the installer is
built with [Inno Setup](https://jrsoftware.org/isinfo.php).
