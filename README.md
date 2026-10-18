# QuickClip

A lightweight instant-replay clipper and clip editor for Windows. QuickClip keeps the last few minutes of your
screen in memory, saves them as a clip when you press a hotkey, files each clip under the game you were playing,
and lets you trim, crop and remix the audio tracks before sharing.

## Recording

- **Instant replay** from 10 seconds up to 5 minutes, held in RAM (Settings shows how much memory that takes).
  Nothing is written to disk until you save a clip.
- **Any monitor**, including virtual displays (DesktopSplitter, Duet, …), or just its 16:9 (21:9, 16:10, 4:3) center,
  like the middle of an ultrawide. The capture itself crops, so it costs nothing extra.
- **Separate audio tracks**: *Desktop* (everything else), *Chat* (Discord, TeamSpeak, Mumble, Teams, Zoom, Skype,
  Slack and other voice apps), *Music* (Spotify, TIDAL, Apple Music, Deezer, Amazon Music, YouTube Music, foobar2000
  and other music apps) and *Mic*. Chat and music apps are detected automatically.
- **Quality presets** Low → Medium → High → Ultra → Indistinguishable, with **frame rate** (30–240 fps) and
  **output resolution** set independently.
- **GPU encoding**, newest codec first: AV1 on NVIDIA/AMD/Intel cards that support it, then HEVC, then H.264.
  Capture and encoding stay on the GPU (about 2% of one CPU core while recording).
- **Hotkey** of your choice (default Alt+F10), tray icon, start with Windows, minimize/close to tray.
- **Game detection**: clips go in a folder per game (from Windows' game list, the Steam/Epic/Riot/GOG/EA/Ubisoft/Xbox
  install folder, or a fullscreen window); anything else goes in *Desktop*.

## Gallery, player and editor

- QuickClip opens on the **gallery**: *All clips* first, then a folder per game, with thumbnails, lengths and how long
  ago each clip was made.
- Click a clip to watch it in the **player**, with every audio track playing, full screen, the previous or next clip
  of the folder, and buttons to share, edit, show or delete it.
- **Share** (the share button on a clip, its right-click menu, or **S** in the player) makes a copy that's under 30 MB,
  at most 60 fps and H.264 with every audio track mixed into one, then copies it to the clipboard: paste it into
  Discord or anywhere with Ctrl+V. A clip that's already small enough H.264 only has its audio mixed; a bigger one is
  compressed to fit, at a smaller size when 30 MB is too little for its resolution. The original stays as it is; the
  copies go in `Videos\QuickClip Shared` and are cleared out after two days. The status bar shows the
  progress. Clips over 60 fps (like 120 fps recordings) have their extra frames blended into motion blur rather than
  dropped, so the 60 fps copy looks smoother (**Settings → Sharing**; on by default).
- **Projects** for montages: tick clips (hover a clip for its checkbox, or Ctrl+click), then **Add to project**, or
  drag clips onto a project in the list (or onto its **+** to start a new one); a clip's right-click menu has
  **Add to project** and **Copy to folder…** too, for it or for every ticked clip. A
  project is a folder in `Projects` inside the clips folder. Its clips play one after another in the preview,
  drag them to change the order, and add a song to play under them. **Export to editor** joins everything into one
  video (the music on its own track, faded out at the end) and opens it in the editor to set the volumes and export.
  Clips are added as hard links when they're on the same drive, so they take no extra space.
- **Copy to folder…** copies the ticked clips anywhere.
- The **editor** previews video with every audio track playing at once, each at its own volume (0–200%), shows a
  waveform per track, trims frame-accurately, crops with aspect presets, and exports as a new file or over the
  original. Tracks can be kept separate or merged into one (**Merge all audio tracks into one** under Export, which
  asks first, and warns before saving over the original that way). After **Save over original**, **Undo** (Ctrl+Z)
  puts the clip back as it was, from the Recycle Bin, and sends the edited version there instead. The preview volume
  slider is QuickClip's own level in the Windows volume mixer.
- **Add sound** puts a song or sound effect on its own track: click **Add sound** or drop audio files onto the
  editor (onto the timeline to start them there). Drag a sound's block along its track to move it; it snaps to the
  clip's start and end, the in and out points and the playhead. Right-click it to start it at the playhead.
- **Smooth 60 fps** (the editor's frame rate choice for clips of about 90 fps and up) exports at 60 fps with the
  frames in between blended into motion blur, mixed in linear light like a camera shutter open for the whole frame.
  It's only offered above 60 fps, so a clip can't be blurred twice.
- **What's new**: after an update, QuickClip shows the release notes of every version since the one you had, the
  next time its window opens (never over a game); **Settings → Updates → What's new** shows them again.
- Opens practically anything FFmpeg reads; exports MP4, MKV, MOV, WebM, GIF, MP3, M4A, WAV, FLAC, Opus.
- **Right-click → Edit with QuickClip** on video and audio files in Explorer.

## Install

Download **QuickClip-<version>-setup.exe** from [Releases](https://github.com/swiiftex/QuickClip/releases) and run it.
It installs for your user only (no admin prompt) into `%LOCALAPPDATA%\Programs\QuickClip`, adds a Start menu
shortcut, and offers to add the right-click entry, start QuickClip when you sign in, and a desktop shortcut.
Uninstall it from **Settings → Apps**. Nothing else needs installing (.NET is included).

Prefer no installer? The **-win-x64.zip** on the same page runs from any folder.

QuickClip checks for new releases when it starts (turn that off under **Settings → Updates**) and can install them
for you; installed copies update by running the new setup silently, unzipped copies replace their own files. The
**Stable** channel gets tested releases; **Nightly** also gets development builds (GitHub prereleases named like
`0.6.0-nightly.1`) as new features are made.

Requires Windows 10 version 2004 or later. Separating voice chat and music from desktop audio needs Windows 11 (or
Windows 10 build 20348+); on older Windows 10 the Desktop track records all system audio and the Chat and Music tracks
stay empty. Music played in a web browser stays on the Desktop track.

On Windows 11 the right-click entry sits under **Show more options**. To put it on the main menu, see
`scripts\add-win11-menu.ps1` (it builds and signs a small app package; trusting its certificate needs one UAC prompt).

## Using it

1. Play. When something happens, press **Alt+F10** (or click **Save clip**). You'll hear a sound and get a
   notification; the clip appears in the gallery.
2. Click a clip to watch it. **Edit** (or **E**) opens it in the editor: trim with **I** / **O**, set track volumes,
   tick **Crop video** if needed, and **Export as new file** or **Save over original**.

### Player keys

| Key | Action |
| --- | --- |
| Space or K | Play / pause |
| ← / → · Shift+← / → · J / L | Back / forward 5 seconds · 1 second · 10 seconds |
| , / . | Previous / next frame |
| ↑ / ↓ · M | Volume up / down · mute |
| F or double-click | Full screen (Esc leaves it) |
| Page Up / Page Down | Previous / next clip |
| S · E · Delete · Esc | Share · edit · move to the Recycle Bin · back to the gallery |

### Editor keys

| Key | Action |
| --- | --- |
| Space | Play / pause |
| ← / → | Previous / next frame |
| Shift+← / → · Ctrl+← / → | ±1 second · ±5 seconds |
| I / O · Shift+I / O | Set in / out · jump to in / out |
| Home / End | Start / end of file |
| L · C · M | Loop selection · crop on/off · mute |
| + / − / 0 | Zoom timeline in / out / fit (or mouse wheel; Shift+wheel scrolls) |
| Ctrl+O / Ctrl+E / Ctrl+S | Open / export as new file / save over original |
| Ctrl+Z | Undo saving over the original |

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
  loopback. Windows can leave one process tree out of a capture, so *Desktop* is everything except the chat or music
  app; with more than one of those running, *Desktop* instead captures every other app that has audio, one by one.
  *Chat* and *Music* capture only their apps, *Mic* the microphone. Every stream is timestamped on the same clock and kept as encoded packets in a RAM ring buffer; saving
  writes the packets from the last keyframe before the start point into an MP4 with named tracks.
- **Player and preview** use [libmpv](https://mpv.io) embedded in a native child window; the tracks are merged by mpv's
  `lavfi-complex` and remixed per track by a volume filter that the sliders adjust live. An added sound is previewed
  from a temporary FLAC copy already placed on the clip's timeline, loaded as an extra track, so it stays in sync
  when seeking; the export places it from the original file with the same filters.
- **Export** builds an FFmpeg command line (`src/QuickClip/Media/Export.cs`).

## License

QuickClip is free software under the [GNU GPL v3](LICENSE). It ships [FFmpeg](https://ffmpeg.org) (BtbN GPL build)
and libmpv ([shinchiro build](https://github.com/shinchiro/mpv-winbuild-cmake)), both GPL-licensed; the installer is
built with [Inno Setup](https://jrsoftware.org/isinfo.php).
