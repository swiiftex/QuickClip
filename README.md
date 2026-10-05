# QuickClip

A small Windows app for trimming and cropping recordings, with proper multi-track audio. Built for clips that
record the mic and the PC audio on separate tracks (OBS, ShadowPlay, etc.).

- **Preview** video with all audio tracks playing together, each at its own volume
- **Trim** with in/out points (frame-accurate), a zoomable timeline and a waveform per audio track
- **Crop** by dragging on the video, with aspect presets (16:9, 9:16, 1:1, 4:3, 4:5, 21:9)
- **Audio tracks** are listed by name (from the file), can be renamed, unchecked, soloed for preview and
  given a volume from 0–200%
- **Merge all tracks into one** on export, or keep them as separate tracks
- **Export as a new file** or **save over the original** (the original goes to the Recycle Bin)
- Opens practically anything FFmpeg can read. Exports MP4, MKV, MOV, WebM, GIF, MP3, M4A, WAV, FLAC, Opus,
  or keeps the source format
- H.264 / HEVC / AV1 / VP9, using your GPU's encoder (NVIDIA, AMD, Intel) when there is one
- Quality presets or a **target file size** (e.g. 10 MB for Discord)
- **Stream copy** mode: instant, lossless trims (cuts land on keyframes)
- **Right-click → Edit with QuickClip** for video and audio files in Explorer

## Install

Needs Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

This downloads FFmpeg and libmpv (first run only), builds QuickClip into `%LOCALAPPDATA%\Programs\QuickClip`,
adds a Start menu shortcut and adds **Edit with QuickClip** to the right-click menu of video and audio files.

On Windows 11 the entry goes on the **main** right-click menu. Windows only allows that for app packages, so the
installer also builds a tiny package (just a manifest plus `QuickClipShell.dll`, a context-menu handler; needs Visual
Studio's C++ tools). It signs that package with a certificate created on your PC. The first install shows one UAC
prompt to trust that certificate. Later installs don't. If you decline, or the C++ tools are missing, the entry goes
in the classic menu instead (**Show more options**). `-ClassicMenuOnly` skips the package.

To remove it, including the package and certificate: `powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1`

## Using it

1. Open a file (right-click it, drag it onto the window, or **Open**).
2. Move the playhead and press **I** / **O** (or *Set in* / *Set out*), drag the yellow handles, or type the
   times in the *In*/*Out* boxes.
3. Set each audio track's volume. Untick a track to leave it out. **Solo** only affects what you hear.
4. Optionally tick **Crop video** and drag a rectangle on the video.
5. **Export as new file…** or **Save over original**.

Audio that is left at 100% and not merged is copied without re-encoding when the format allows it.

### Keyboard

| Key | Action |
| --- | --- |
| Space | Play / pause |
| ← / → | Previous / next frame |
| Shift+← / → | ±1 second |
| Ctrl+← / → | ±5 seconds |
| I / O | Set in / out at the playhead |
| Shift+I / Shift+O | Jump to in / out |
| Home / End | Start / end of file |
| L | Loop the selection while previewing |
| C | Crop on / off |
| + / − / 0 | Zoom timeline in / out / fit (or mouse wheel; Shift+wheel scrolls) |
| Ctrl+O / Ctrl+E / Ctrl+S | Open / export as new file / save over original |

## Building from source

```powershell
powershell -ExecutionPolicy Bypass -File scripts\get-deps.ps1   # FFmpeg + libmpv into .\deps
dotnet build
dotnet test                                                     # exports real files and checks them with ffprobe
```

`deps\` is copied next to the executable on build. Settings live in `%APPDATA%\QuickClip`, and a log is kept in
`%LOCALAPPDATA%\QuickClip\quickclip.log`.

## How it works

- **Preview** uses [libmpv](https://mpv.io) embedded in a native child window. To hear every track at once, mpv's
  `lavfi-complex` merges the tracks into one multichannel stream, and an audio filter splits them back out, applies
  a named `volume` filter per track and mixes them. Volume sliders update those filters live.
- **The crop rectangle** is drawn by mpv itself as an on-screen overlay, so it sits exactly on the video.
- **Export** builds an FFmpeg command line (`src/QuickClip/Media/Export.cs`) and reports progress from
  `-progress pipe:1`.

## Third-party software

QuickClip downloads and ships [FFmpeg](https://ffmpeg.org) (BtbN GPL build) and libmpv
([shinchiro build](https://github.com/shinchiro/mpv-winbuild-cmake)), both GPL-licensed. If you share builds of
QuickClip, the GPL applies to those components.
