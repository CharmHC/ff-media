# M4 — Processing: Design

> **Status:** Approved · **Date:** 2026-07-05 · **Milestone:** M4 (see [SDD.md](../../../SDD.md) §17)
>
> Adds **trim/clip**, **subtitle embedding**, and **metadata + thumbnail embedding** to the
> YouTube Downloader, all driven by yt-dlp flags through the existing pure `OptionSetBuilder`.
> Builds on M2's `DownloadConfig`/`OptionSetBuilder` and M3's per-job queue. Design defers to
> [SDD.md](../../../SDD.md); §7.3 is expanded by this work.

---

## 1. Goal & Scope

**Goal (SDD §17, M4):** Four post-processing options, applied per download and flowing through
the M3 queue: **trim/clip** (with a per-download precise-cut toggle), **embed subtitles** (manual
+ auto-generated fallback, chosen language), **embed metadata**, **embed thumbnail**.

**Confirmed decisions (brainstorming):**
- **Trim:** user picks fast vs precise per download (a "precise cut" toggle).
- **Subtitles:** embed manual **and** auto-generated (`--write-subs --write-auto-subs --embed-subs`),
  with a language field (default `en`). **Applied to video output only** (moot for audio-only).
- **Embed extras:** two independent toggles (metadata, thumbnail), **both default ON**.
- **All four features ship in M4.**

**In scope**
- `TrimRange` + `ProcessingOptions` model; `DownloadConfig` gains a `Processing` field.
- `OptionSetBuilder` emits the processing flags (pure, exhaustively tested).
- ViewModel processing selections + timestamp parsing; page "Processing" section.

**Out of scope (deferred)**
- Standalone ffmpeg trim via `FFMedia.Media` (M4 uses yt-dlp's `--download-sections`; the FFMpegCore
  path stays reserved for future tools).
- Open-ended trims (start-only / end-only), subtitle format conversion, sidecar `.srt` files,
  chapter handling — later if wanted.
- Settings/presets/history/theming — M5.

---

## 2. Key Design Decisions

1. **All processing via yt-dlp flags through `OptionSetBuilder`** — keeps the pure, tested
   `DownloadConfig → OptionSet` seam (no separate ffmpeg pass). yt-dlp performs the recode/embed.
2. **Group M4 concerns in a `ProcessingOptions` sub-record** so `DownloadConfig` stays readable
   (it already has 5 fields; a flat add of 6 more would bloat it).
3. **Subtitles are video-only.** The builder emits subtitle flags only when `Kind == Video`;
   audio-only output ignores them (yt-dlp would otherwise warn/no-op).
4. **Metadata + thumbnail default ON; subtitles + trim default off.** Sensible "richer file by
   default" without surprising re-encodes or missing-subtitle noise.
5. **Trim precision is per-download.** `PreciseCut` adds `--force-keyframes-at-cuts` (exact, re-encodes
   around the cut); off = keyframe-fast (no re-encode).
6. **Thumbnail-embed container caveat is not blocked.** `--embed-thumbnail` works for mp4/mkv/mp3/m4a
   but not reliably webm/opus; yt-dlp warns and proceeds — FFMedia does not pre-validate the combo.

---

## 3. Verified API Facts (installed YoutubeDLSharp 1.2.0)

Confirmed by reflection — all are typed `OptionSet` properties (no custom options needed):
`EmbedMetadata` (bool), `EmbedThumbnail` (bool), `WriteSubs` (bool), `WriteAutoSubs` (bool),
`EmbedSubs` (bool), `SubLangs` (string), `DownloadSections` (`MultiValue<string>`),
`ForceKeyframesAtCuts` (bool). The exact assignment for `DownloadSections` (a `MultiValue`)
and the rendered flag strings are finalized in the plan against the package (as M2 did for
`--audio-quality`).

---

## 4. Components

All in `FFMedia.Tools.YouTubeDownloader`.

### 4.1 Model — `Models/`
```csharp
public sealed record TrimRange(TimeSpan Start, TimeSpan End);

public sealed record ProcessingOptions(
    TrimRange? Trim,
    bool PreciseCut,
    bool EmbedSubtitles,
    string SubtitleLanguage,
    bool EmbedMetadata,
    bool EmbedThumbnail)
{
    // Metadata + thumbnail on; subtitles/trim off; default subtitle language "en".
    public static ProcessingOptions Default { get; } =
        new(Trim: null, PreciseCut: false, EmbedSubtitles: false, SubtitleLanguage: "en",
            EmbedMetadata: true, EmbedThumbnail: true);
}
```
`DownloadConfig` gains `ProcessingOptions Processing`, and `DownloadConfig.Default` uses
`ProcessingOptions.Default`.

### 4.2 `OptionSetBuilder` — pure `ApplyProcessing`
`Build(config, outputFolder)` builds the video/audio `OptionSet` as today, then calls a private
`ApplyProcessing(OptionSet options, DownloadConfig config)`:
- **Trim:** `config.Processing.Trim` non-null ⇒ set `DownloadSections` to `"*{start}-{end}"`
  (times as total seconds); `PreciseCut` ⇒ `ForceKeyframesAtCuts = true`.
- **Subtitles (video only):** `Kind == Video && EmbedSubtitles` ⇒ `WriteSubs = WriteAutoSubs =
  EmbedSubs = true`, `SubLangs = SubtitleLanguage`.
- **Embed:** `EmbedMetadata` / `EmbedThumbnail` set from the flags.

### 4.3 ViewModel / Page
The ViewModel adds a processing group: `TrimStart`/`TrimEnd` (strings), `PreciseCut` (bool),
`EmbedSubtitles` (bool), `SubtitleLanguage` (string, default `en`), `EmbedMetadata` (bool, default
true), `EmbedThumbnail` (bool, default true). When building the `DownloadConfig` for a job it
assembles `ProcessingOptions`, parsing `TrimStart`/`TrimEnd` via a pure helper
(`TimeSpan` from `HH:MM:SS`, `MM:SS`, or plain seconds). A `TrimRange` is produced only when **both**
parse and `End > Start`; blank inputs ⇒ no trim; invalid non-blank input ⇒ no trim + a status hint.
The page gains a "Processing" section with those controls.

---

## 5. Data Flow

```
User sets trim / subs / embed options
      │
      ▼
DownloaderViewModel  ──parse timestamps──▶  ProcessingOptions (+ TrimRange?)
      │  DownloadConfig{…, Processing}
      ▼
DownloadJob → DownloadManager (M3 queue) → OptionSetBuilder.Build → ApplyProcessing → OptionSet
      │
      ▼
yt-dlp performs trim / subtitle-embed / metadata+thumbnail embed
```

---

## 6. Testing Strategy

**Unit (no network):**
- **`OptionSetBuilder` processing matrix:** trim off ⇒ no `DownloadSections`; trim on ⇒
  `DownloadSections` contains `"*<start>-<end>"`; precise ⇒ `ForceKeyframesAtCuts` true, else false;
  subtitles on (video) ⇒ `WriteSubs`/`WriteAutoSubs`/`EmbedSubs` true and `SubLangs` set; subtitles on
  but **audio output** ⇒ no subtitle flags; `EmbedMetadata`/`EmbedThumbnail` reflect the flags; the
  `Default` config embeds metadata + thumbnail.
- **Timestamp parsing helper:** `HH:MM:SS` / `MM:SS` / seconds parse; blank ⇒ null; invalid ⇒ null;
  `End <= Start` ⇒ no `TrimRange`.
- **ViewModel:** processing selections assemble the expected `ProcessingOptions` (incl. a parsed
  `TrimRange`), verified via the captured `DownloadJob.Config` (M3 fake-manager pattern).

**Integration (trait-gated, off in CI):** a download with metadata + thumbnail embedding succeeds and
produces a file; a short trim (e.g. `*0-5`) produces a file. (Deep assertion of embedded tags/exact
duration is out of scope; success + file presence is the smoke test.)

All unit tests use plain xUnit `Assert`; no network.

---

## 7. SDD Updates (Rule 1)

- **§7.3** — restore/expand the trim, subtitles, embed-metadata, embed-thumbnail rows with the real
  flags emitted by `ApplyProcessing`; note trim precision (`--force-keyframes-at-cuts`) and
  subtitles-are-video-only.
- **§8** — note that M4 trims via yt-dlp `--download-sections` (FFMpegCore trim remains reserved for
  future tools).
- **§17** — mark **M4 ✅ delivered**; header **Version → 0.6**, `Last updated`, Changelog row.
- **CLAUDE.md** progress entry.

---

## 8. Definition of Done (M4)

- `dotnet build FFMedia.sln` clean; `dotnet test --filter "Category!=Integration"` all green.
- App: set a trim range (fast or precise), toggle embed-subtitles + language, and metadata/thumbnail
  checkboxes; the resulting download reflects those options.
- `OptionSetBuilder`'s processing flags and the timestamp parser are pure and covered by a full
  test matrix; `FFMedia.Core` stays UI-free.
- Trait-gated integration (embed + trim) passes locally; CI excludes it and stays green.
- SDD updated to v0.6 (with the §7.3 processing flags); CLAUDE.md progress logged.
- Delivered as a single PR (`feat/m4-processing` → its base branch).
