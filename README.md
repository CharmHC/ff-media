# FFMedia
# <img width="1400" height="500" alt="Cover" src="https://github.com/user-attachments/assets/d7413d62-d135-44c5-a6c4-4eb2f2242c2e" />

An all-in-one **Windows media toolbox**. The first tool is a **YouTube Downloader**
— paste a URL, pick a format (mp4, mkv, mp3, wav, m4a, opus, flac, …), and download
it with live progress. More media tools (standardize & merge, and beyond) are on
the roadmap.

Under the hood, FFMedia is a polished orchestrator over **[yt-dlp](https://github.com/yt-dlp/yt-dlp)**
(extraction/download) and **[FFmpeg](https://ffmpeg.org/)** (transcode/mux/trim).

## Status

🚧 Early development. See **[SDD.md](SDD.md)** — the single source of truth for
architecture, scope, and milestones.

## Tech stack

C# / .NET 9 · WPF + WPF-UI · CommunityToolkit.Mvvm · YoutubeDLSharp · FFMpegCore ·
Serilog · Velopack.

## Roadmap (high level)

| Milestone | Focus |
|---|---|
| M0 | Foundation: shell, DI, binary management |
| M1 | Vertical slice: URL → MP4 with progress + cancel |
| M2 | Full format matrix (video + audio) |
| M3 | Queue, concurrency, playlists |
| M4 | Trim, subtitles, metadata/thumbnail embed |
| M5 | Settings, presets, history, theming |
| M6 | Installer + auto-update, v1 release |

## Legal

FFMedia is a general-purpose media tool. Users are responsible for complying with
content owners' rights and the terms of service of the sites they download from.
