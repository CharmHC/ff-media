# M2 — Formats: Design

> **Status:** Approved · **Date:** 2026-07-05 · **Milestone:** M2 (see [SDD.md](../../../SDD.md) §17)
>
> Turns the M1 hardcoded single-MP4 download into a **configurable format matrix**
> (video containers + audio-only) with a **quality/resolution** selector, backed by a
> pure, exhaustively-tested `OptionSet` builder. Design defers to [SDD.md](../../../SDD.md);
> §7.3 is finalized against the installed yt-dlp/YoutubeDLSharp as part of this work.

---

## 1. Goal & Scope

**Goal (SDD §17, M2):** Full format matrix — video containers (**MP4/MKV/WebM**) and
audio-only extraction (**MP3/WAV/M4A/Opus/FLAC**) with **quality/resolution** selection.
The `config → yt-dlp args` builder becomes a pure function and is fully unit-tested.

**In scope**
- A `DownloadConfig` domain model (output kind, container, resolution cap, audio format, bitrate).
- A pure `OptionSetBuilder.Build(config, outputFolder)` replacing M1's `DownloadOptions.Mp4`.
- ViewModel + page UI to choose Video/Audio and the relevant format + quality.
- Full unit-test matrix for the builder; ViewModel config-assembly tests.

**Out of scope (deferred, unchanged milestones)**
- Download **queue** + bounded **concurrency**, **playlist/channel** — **M3**.
- **Trim/clip**, **subtitles**, **metadata + thumbnail** embedding — **M4**.
- **Settings/presets/history/notifications/theming** — **M5**.
- **Parsing each video's available-formats list.** M2 uses a **resolution cap** and lets
  yt-dlp pick the best stream at or below it. Dynamic per-video format lists are a later,
  optional enhancement (YAGNI for M2).

---

## 2. Key Design Decisions

1. **Mux, not re-encode.** M1 used `RecodeVideo = Mp4`, which forces a full re-encode
   (slow, lossy). M2 selects streams with a `-f` format string and sets
   **`MergeOutputFormat`** (container mux) — fast, quality-preserving. `RecodeVideo` is
   only a fallback consideration and is **not** used in M2's happy path.
2. **Resolution is a cap.** `VideoResolution` maps to a `[height<=N]` filter in the `-f`
   selector (`Best` ⇒ no filter). yt-dlp chooses the best available stream ≤ cap.
3. **Audio bitrate via a custom option.** YoutubeDLSharp's typed `OptionSet.AudioQuality`
   is `byte?` — the yt-dlp **0–10 VBR scale**, which *cannot* express a specific bitrate
   like `192K`. Specific bitrates are therefore emitted through
   `OptionSet.SetCustomOption("--audio-quality", "192K")`. **Lossless formats (WAV/FLAC)
   ignore bitrate** (no `--audio-quality` emitted).
4. **Container-native stream preference with fallback.** The `-f` selector prefers streams
   that mux cleanly into the chosen container (mp4/m4a for MP4; webm/opus for WebM; any for
   MKV), then falls back to "best video+audio / best" so a download never fails purely for
   lack of a container-native stream.
5. **Pure builder = the tested core.** All format/quality logic lives in one pure function
   with no I/O, matching SDD §14's "orchestration/argument-building logic gets the most tests."

---

## 3. Verified API Facts (installed YoutubeDLSharp 1.2.0)

Confirmed by reflection against `YoutubeDLSharp.dll`:

- `OptionSet.Format : string` → `-f`
- `OptionSet.MergeOutputFormat : DownloadMergeFormat` → `--merge-output-format`
  - enum: `Unspecified, Mp4, Mkv, Ogg, Webm, Flv`
- `OptionSet.RecodeVideo : VideoRecodeFormat` (`None, Mp4, Mkv, Ogg, Webm, Flv, Avi`) — **not used in M2**
- `OptionSet.ExtractAudio : bool` → `-x`
- `OptionSet.AudioFormat : AudioConversionFormat` → `--audio-format`
  - enum: `Best, Aac, Flac, Mp3, M4a, Opus, Vorbis, Wav`
- `OptionSet.AudioQuality : byte?` → `--audio-quality` (**0–10 VBR only**, not a bitrate)
- `OptionSet.Output : string` → `-o`
- `OptionSet.SetCustomOption<T>(string, T)` / `AddCustomOption` / `CustomOptions : IOption[]`
  — the seam used to emit `--audio-quality <bitrate>`.

> Every M2 target maps to an existing enum member: `DownloadMergeFormat` covers Mp4/Mkv/Webm,
> and `AudioConversionFormat` covers Mp3/M4a/Opus/Flac/Wav. No gaps.

---

## 4. Components

### 4.1 Domain model — `FFMedia.Tools.YouTubeDownloader/Models/`

```csharp
public enum OutputKind { Video, Audio }
public enum VideoContainer { Mp4, Mkv, Webm }
public enum VideoResolution { Best, P2160, P1440, P1080, P720, P480 }
public enum AudioFormat { Mp3, Wav, M4a, Opus, Flac }
public enum AudioBitrate { Best, K320, K256, K192, K128 }

public sealed record DownloadConfig(
    OutputKind Kind,
    VideoContainer Container,
    VideoResolution Resolution,
    AudioFormat AudioFormat,
    AudioBitrate Bitrate)
{
    // Sensible default: 1080p MP4 video.
    public static DownloadConfig Default { get; } = new(
        OutputKind.Video, VideoContainer.Mp4, VideoResolution.P1080,
        AudioFormat.Mp3, AudioBitrate.Best);
}
```

`DownloadRequest` gains the config:
```csharp
public sealed record DownloadRequest(string Url, string OutputFolder, DownloadConfig Config);
```

Each enum lives in its own file (SDD §18: one public type per file). `DownloadConfig`
carries *all* fields; the irrelevant group (video vs audio) is simply unused for a given
`Kind`. This keeps the record flat and trivially testable.

### 4.2 `OptionSetBuilder` (pure) — `Services/OptionSetBuilder.cs`

Replaces `DownloadOptions.Mp4`. Signature:
```csharp
public static class OptionSetBuilder
{
    public static OptionSet Build(DownloadConfig config, string outputFolder);
}
```

**Video branch** (`Kind == Video`):
- Height filter `h` = `""` for `Best`, else `[height<=N]` (2160/1440/1080/720/480).
- Container-specific selector, e.g. for MP4:
  `bv*{h}[ext=mp4]+ba[ext=m4a]/b{h}[ext=mp4]/bv*{h}+ba/b{h}`
  WebM prefers `[ext=webm]`; MKV uses the generic `bv*{h}+ba/b{h}` (mkv holds anything).
- `MergeOutputFormat` = Mp4 / Mkv / Webm.
- `Output` = `Path.Combine(outputFolder, "%(title)s.%(ext)s")`.

**Audio branch** (`Kind == Audio`):
- `ExtractAudio = true`, `Format = "ba/b"`, `AudioFormat` = mapped enum.
- Lossy (Mp3/M4a/Opus) with a non-`Best` bitrate ⇒ `SetCustomOption("--audio-quality", "192K")` etc.
- Lossless (Wav/Flac) ⇒ no `--audio-quality`.
- `Output` template as above.

The exact selector strings are finalized here and copied verbatim into SDD §7.3.

### 4.3 `YtDlpDownloadService`
One-line change: build options via `OptionSetBuilder.Build(request.Config, request.OutputFolder)`.
Error-isolation and cancellation behavior from M1 are unchanged.

### 4.4 `DownloaderViewModel`
Adds observable selections and their option lists:
- `OutputKind SelectedKind` (Video/Audio) — drives which group is enabled.
- Video: `SelectedContainer`, `SelectedResolution` (+ `Containers`, `Resolutions` lists).
- Audio: `SelectedAudioFormat`, `SelectedBitrate` (+ `AudioFormats`, `Bitrates` lists).
- `DownloadAsync` assembles a `DownloadConfig` from the selections and passes it in the
  `DownloadRequest`. Defaults come from `DownloadConfig.Default`.

### 4.5 `DownloaderPage` (XAML)
- A Video/Audio selector (e.g., segmented `RadioButton`s / `ComboBox`).
- Video group: Container + Resolution `ComboBox`es (visible when Kind = Video).
- Audio group: Format + Bitrate `ComboBox`es (visible when Kind = Audio).
- Bitrate control disabled/hidden for lossless formats (WAV/FLAC) — nicety, not required.
- Existing Probe/Download/Cancel + progress/status unchanged.

---

## 5. Data Flow

```
User picks Kind + format + quality
      │
      ▼
DownloaderViewModel  ──assembles──▶  DownloadConfig
      │  DownloadRequest(Url, OutputFolder, Config)
      ▼
YtDlpDownloadService ──▶ OptionSetBuilder.Build(config, folder) ──▶ OptionSet
      │
      ▼
YoutubeDL.RunVideoDownload(url, options)  ──progress──▶  ViewModel  ──▶  UI
```

---

## 6. Testing Strategy

**Unit — `OptionSetBuilderTests` (the priority):**
- **Video:** for each container (Mp4/Mkv/Webm) × representative caps (Best, 1080p, 480p):
  assert `MergeOutputFormat`, presence/absence of `[height<=N]`, container-native `ext`
  preference, and output template. Substring-tolerant assertions (like M1's option test).
- **Audio:** for each format (Mp3/Wav/M4a/Opus/Flac): assert `ExtractAudio`, `AudioFormat`;
  lossy + bitrate ⇒ `--audio-quality <bitrate>` present in `CustomOptions`; lossless ⇒ absent;
  `Best` bitrate ⇒ no specific-bitrate option.
- **Defaults:** `DownloadConfig.Default` builds a 1080p MP4 option set.

**Unit — `DownloaderViewModel` config assembly:**
- Selecting Audio + Mp3 + 192K yields a `DownloadRequest` whose `Config` matches (verified via
  a fake `IDownloadService` capturing the request).
- Selecting Video + Mkv + 720p yields the corresponding video config.

**Integration (trait-gated, off in CI):** keep M1's probe + MP4 download; **add one audio
case** (e.g., extract MP3 of the test video) to smoke-test the audio branch end-to-end.

All unit tests use plain xUnit `Assert`; no network.

---

## 7. SDD Updates (Rule 1)

- **§7.3** — replace the "conceptual" mapping table with the **finalized** `-f` / merge /
  audio-format / audio-quality strings actually produced by `OptionSetBuilder`.
- **§7.3 note** — record the mux-over-recode decision and the custom-option bitrate mechanism.
- **§17** — mark **M2 ✅ delivered** (branch `feat/m2-formats`).
- **§19** — no change (concurrency still M3; history still M5).
- **Header version → 0.4**, `Last updated`, Changelog row.
- **CLAUDE.md** — progress-log entry (Rule 2).

---

## 8. Definition of Done (M2)

- `dotnet build FFMedia.sln` clean; `dotnet test --filter "Category!=Integration"` all green.
- App: choose Video (MP4/MKV/WebM) at a resolution cap **or** Audio (MP3/WAV/M4A/Opus/FLAC)
  at a bitrate, and download a matching file with live progress + cancel.
- `OptionSetBuilder` is pure and covered by the full format/quality matrix of tests.
- yt-dlp invoked via `IBinaryProvider` bundled paths (unchanged); `FFMedia.Core` stays UI-free.
- SDD updated to v0.4 (with finalized §7.3 flags); CLAUDE.md progress logged.
- Delivered as a single PR (`feat/m2-formats` → `main`).
