# CLAUDE.md — Working Rules & Progress Log for FFMedia

## Project

FFMedia is an all-in-one **Windows media toolbox** (C# / .NET 9, WPF + WPF-UI).
The v1 tool is a **YouTube Downloader** orchestrating **yt-dlp** (download) +
**ffmpeg** (transcode/mux). Architecture is modular — an app shell hosts pluggable
`ITool` modules.

**[`SDD.md`](SDD.md) is the single source of truth** for architecture, scope, and
milestones. Read it before making design decisions.

---

## 🔴 Standing Rules (always follow)

1. **Keep [`SDD.md`](SDD.md) up to date.** Whenever a design decision, scope,
   convention, dependency, or milestone changes — update `SDD.md` in the same
   change, and bump its version + Changelog entry. The SDD must never lag reality.
2. **Record progress after every task** in the [Progress Log](#-progress-log)
   below. Append a dated entry describing what was done, what changed, and what's
   next. Newest entries go at the top.
3. **Branch per task; deliver via PR.** Never commit task work directly to `main`.
   For each task, branch off the latest `main` (e.g. `feat/…`, `fix/…`, `docs/…`),
   commit there, push, and open a **PR for the user to review**. Do not merge — the
   user reviews and merges.
4. When these rules conflict with anything else, these rules win unless the user
   says otherwise.

---

## 📓 Progress Log

_Newest first. One entry per completed task/session._

### 2026-07-04 — Add branch-per-task / PR-review workflow rule

- **Done:** Added standing Rule 3 — always branch off `main` per task and deliver
  via a PR for user review (never commit task work to `main`, never self-merge).
  This change itself delivered on branch `docs/pr-workflow-rule` via PR.
- **Note:** `gh` CLI not yet installed, so PRs are teed up via a GitHub compare
  link rather than opened automatically. Install `gh` to let me open PRs directly.
- **Next:** M0 (Foundation) implementation plan — on its own branch + PR.

### 2026-07-04 — Project bootstrap & design

- **Done:**
  - Initialized git repo, wired `origin` → `github.com/ChamHC-dev/ff-media.git`,
    branch `main` (pushed).
  - Ran research (YoutubeDLSharp, FFMpegCore vs Xabe, WPF vs WinUI 3, Velopack).
  - Brainstormed & locked v1 decisions: WPF + WPF-UI / .NET 9, bundled binaries,
    full-featured downloader, modular shell.
  - Wrote **`SDD.md`** (single source of truth): architecture, stack, downloader
    design, testing strategy, milestones M0–M7.
  - Added brainstorming spec record (`docs/superpowers/specs/`), `README.md`,
    `.gitignore`.
  - Created this `CLAUDE.md` with standing rules + progress log.
- **State:** Design phase complete. No code/solution scaffolded yet.
- **Next:** Turn **Milestone M0 (Foundation)** into a detailed implementation plan,
  then scaffold the solution (shell + Core + DI + binary management).
