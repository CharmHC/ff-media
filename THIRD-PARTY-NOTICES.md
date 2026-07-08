# Third-Party Notices

FFMedia is licensed under the MIT License (see [LICENSE](LICENSE)). However,
FFMedia **bundles and depends on** third-party components that are licensed
separately. This file lists them and their licenses.

FFMedia's own MIT license applies **only** to FFMedia's source code. It does
**not** relicense any of the components below.

---

## Bundled executables (shipped inside FFMedia installers)

FFMedia invokes these as **separate processes** — they are not linked into
FFMedia's code. They are downloaded by `build/fetch-binaries.ps1` and packaged
into the installer.

### FFmpeg

- **Project:** https://ffmpeg.org/
- **License:** **GNU General Public License, version 3 (GPL-3.0)**. FFMedia
  ships the BtbN `win64-gpl` build, which enables GPL-licensed components
  (e.g. x264/x265). That build, and therefore the bundled `ffmpeg.exe`, is
  covered by the GPL — **not** by FFMedia's MIT license.
- **Build source:** https://github.com/BtbN/FFmpeg-Builds
- **Corresponding source:** The complete corresponding source for the exact
  FFmpeg version bundled is available from https://ffmpeg.org/download.html and
  https://github.com/FFmpeg/FFmpeg (see the version string reported by
  `ffmpeg -version`), and the build recipes from the BtbN repository above.
- **Trademark:** "FFmpeg" is a trademark of Fabrice Bellard, originator of the
  FFmpeg project. FFMedia is **not affiliated with or endorsed by** the FFmpeg
  project.

> If you redistribute FFMedia (or its installer), you redistribute this GPL
> `ffmpeg.exe` and must comply with the GPL for that binary — keep this notice
> and make the corresponding FFmpeg source available (the links above satisfy
> this).

### yt-dlp

- **Project:** https://github.com/yt-dlp/yt-dlp
- **License:** **The Unlicense** (public-domain dedication).
- FFMedia can also self-update this binary in place via `yt-dlp -U`.

---

## Bundled libraries (NuGet packages compiled into FFMedia)

| Package | License |
|---|---|
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Extensions.* (Hosting, DependencyInjection, Logging) | MIT |
| WPF-UI, WPF-UI.DependencyInjection | MIT |
| Velopack | MIT |
| YoutubeDLSharp | BSD-3-Clause |
| Serilog (+ Serilog.Extensions.Hosting, Sinks.File, Sinks.Debug) | Apache-2.0 |

Each package remains under its own license; consult the linked project for the
full text. Test-only packages (xUnit, Microsoft.NET.Test.Sdk, coverlet, and the
xUnit runners) are used for development and are **not** distributed with FFMedia.

---

_Last reviewed: 2026-07-08. If dependencies or the bundled FFmpeg build change,
update this file in the same change._
