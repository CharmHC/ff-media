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

C# / .NET 9 · WPF + WPF-UI · CommunityToolkit.Mvvm · YoutubeDLSharp · Serilog ·
Velopack. (FFmpeg is orchestrated as a bundled external executable; an FFMpegCore
wrapper is planned for future in-app processing tools.)

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

## License

FFMedia's own source code is released under the **[MIT License](LICENSE)**.

FFMedia bundles third-party executables (**FFmpeg**, **yt-dlp**) and libraries that
are licensed separately — see **[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)**.
Note in particular that the bundled **FFmpeg** binary is distributed under the
**GNU GPL v3**; that license governs the FFmpeg binary, not FFMedia's own code. If
you redistribute FFMedia's installer, you must comply with the GPL for that binary
(the notices file explains how).

## Legal & disclaimer

- **Use responsibly.** FFMedia is a general-purpose media tool. **You** are
  responsible for complying with copyright, content owners' rights, and the terms
  of service of the sites you download from. Only download content you have the
  right to (e.g. your own uploads, public-domain, or content you are licensed to).
- **No DRM circumvention.** FFMedia does not, and is not intended to, bypass DRM,
  paywalls, or access controls.
- **No affiliation.** FFMedia is an independent project and is **not** affiliated
  with, endorsed by, or sponsored by YouTube, Google, the FFmpeg project, or the
  yt-dlp project. All trademarks belong to their respective owners.
- **No warranty.** The software is provided "as is", without warranty of any kind;
  see the [MIT License](LICENSE) for the full disclaimer.
