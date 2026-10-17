# AGENTS.md

## Purpose

Liraoke is a Windows desktop application for producing karaoke-style videos. Its core workflow is:

1. Create or open a portable workspace.
2. Import one primary song and optional background images/videos.
3. Import or edit lyrics and prepare language-specific sung units.
4. Record per-unit timing manually against the song clock.
5. Preview the timeline, progressive subtitle highlighting, and visualizers.
6. Export an H.264/AAC MP4 through FFmpeg.

The public product name is **Liraoke / 莉拉OK!**. Internal solution, assembly, namespace, and local-data names still use `KaraokeFilmMaker`; do not rename them as incidental cleanup.

## Repository map

- `src/KaraokeFilmMaker.App`: WPF UI, view models, interaction behavior, and composition root.
- `src/KaraokeFilmMaker.Application`: use cases, editing workflows, orchestration, and application-level rules.
- `src/KaraokeFilmMaker.Domain`: project model, value objects, invariants, and domain rules. Keep it independent of UI and infrastructure.
- `src/KaraokeFilmMaker.Infrastructure`: persistence, media probing/encoding, playback, audio, Japanese analysis, rendering, and external integrations.
- `tests/*`: one test project corresponding to each production layer.
- `docs/MVP_REQUIREMENTS_AND_ARCHITECTURE.md`: current product requirements, architecture baseline, acceptance criteria, and implementation decisions.
- `docs/LANGUAGE_PLUGINS.md`: language plug-in contract and example workflow.
- `docs/POC_PLAYBACK_SYNC.md`: playback and long-running synchronization acceptance guidance.
- `docs/RELEASING.md`: CI synchronization, release automation, required secrets, and release procedure.
- `examples/language-plugin`: reference plug-in implementation; it demonstrates the API and is not a complete language pack.
- `scripts/bootstrap-media.ps1`: provisions the pinned media toolchain.
- `scripts/media-toolchain.json`: pinned FFmpeg version and hashes.
- `scripts/publish-mvp.ps1`: builds the portable Windows package.
- `scripts/test-ci.ps1`: runs the Release suite in CI, emits failed-test annotations, and leaves TRX reports for diagnostics.
- `scripts/build-app-icon.ps1`: creates ICO sizes from the existing source image; it must not redesign the artwork.
- `KARAOKE_PROJECT_CONTEXT.md`: early product context. Treat the requirements/architecture document and current code as newer authority where they differ.

## Product invariants

- The primary song clock is the source of truth for timing. Video, subtitles, visualizers, metronome playback, and export must remain synchronized to it.
- Preview and export must share rendering and timing behavior. A fix applied to only one path is incomplete.
- The current editing model has one background image/video track. Do not silently reintroduce the retired multi-track model.
- `.lrn` projects store workspace-relative media paths and must remain movable with their workspace.
- Never silently discard unsupported legacy content. Reject it with a clear message when lossless migration is unavailable.
- Explicit save and recovery are different concepts. Recovery data must not masquerade as a successful user save.
- Unsaved drafts, unapplied timing input, and redoable timing state require explicit handling before destructive navigation, close, export, or replacement.
- Timing is recorded per sung unit: Chinese normally by character, English by word, and Japanese by sung mora/syllable grouping. Punctuation and spacing are not independent timing units.
- Existing unit IDs, confirmed timing, raw tap time, mappings, and language snapshots must be preserved unless the requested edit logically invalidates them.
- Language detection is offline and heuristic. Ambiguity may warn without blocking valid timing; missing required readings, corrupt mappings, and plug-in validation failures may block.
- Japanese reading, sung-unit grouping, written-text mapping, and subtitle ruby layout are related but distinct data. Do not collapse them into one representation.
- Beat detection, the metronome, and snapping are optional aids. They must not alter existing timings merely because settings change.
- Project crop changes the preview/export range, not underlying media, lyric timestamps, or clip positions.
- Audio visualizers are local, optional, deterministic for a given input/time, and absent from the timing monitor.
- Third-party language DLLs require explicit user consent and are never automatically scanned, downloaded, or executed from a project.

## Project and compatibility notes

- The current project schema is documented in the requirements file and serializers. Do not bump it unless persistence compatibility requires it.
- Legacy single-track `project.kfp` files remain supported. Legacy multi-track projects with extra video content are rejected rather than truncated.
- User dialog-location memory lives under `%LOCALAPPDATA%/KaraokeFilmMaker/` and is not project data.
- The portable distribution includes the .NET runtime, pinned FFmpeg binaries, and the Japanese dictionary. Do not assume machine-global installations.
- Windows x64 is the supported platform; the minimum target is Windows 10 version 1809.
- Public distribution requires continued review of bundled third-party licenses and corresponding-source obligations.

## Development environment

Required tooling:

- .NET 10 SDK
- Visual Studio 2026 with the .NET desktop development workload, when using Visual Studio
- PowerShell
- Windows x64

Bootstrap, build, and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/bootstrap-media.ps1
dotnet restore KaraokeFilmMaker.slnx --configfile NuGet.Config
dotnet build KaraokeFilmMaker.slnx --no-restore
dotnet test KaraokeFilmMaker.slnx --no-build --no-restore
dotnet run --project src/KaraokeFilmMaker.App --no-restore
```

Create the portable package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-mvp.ps1
```

Generate rendering snapshots for development verification:

```powershell
$env:KFM_RENDER_SNAPSHOT_DIR = Join-Path (Get-Location) 'artifacts/rendering'
dotnet test KaraokeFilmMaker.slnx --no-build --no-restore
```

## Change workflow

1. Read the relevant section of `docs/MVP_REQUIREMENTS_AND_ARCHITECTURE.md` before changing behavior.
2. Trace the behavior across Domain, Application, App, and Infrastructure rather than patching the first visible layer.
3. Preserve backwards compatibility and unrelated user data.
4. Add or update focused tests in the matching test project.
5. Run the focused tests first, then the complete suite for cross-layer or persistence/rendering changes.
6. For visual changes, render and inspect actual WPF or exported frames at relevant window sizes/aspect ratios. Pixel or snapshot tests supplement visual inspection; they do not replace it.
7. For media changes, verify real FFmpeg output, audio/video duration, seek behavior, cancellation, and cleanup—not only mocked services.
8. For timing/playback changes, include pause, seek, slow playback, long-running synchronization, end-of-song behavior, and input-latency compensation.
9. Update durable requirement documents when the product contract changes. Do not turn README files back into changelogs or internal verification logs.

## Testing expectations

- Keep tests deterministic and offline.
- Prefer testing public behavior and persisted round trips over implementation details.
- Cover old project migration whenever serialized models change.
- Confirm that preview and export produce equivalent timing and layout behavior.
- Exercise Chinese, English, Japanese, mixed-language segments, punctuation, and ambiguous/unknown-language cases when modifying lyrics.
- Exercise horizontal, vertical, square, and 4:3 output when modifying layout or rendering.
- Do not interpret a green automated suite as proof of performance on older hardware or proof that a full song workflow has passed human acceptance.

## Scope boundaries

Unless a task explicitly changes scope, do not add:

- AI-generated video or automatic first-cut production;
- complex compositing/effects;
- multi-track background editing;
- cloud-required language analysis;
- automatic execution of project-provided plug-ins;
- external subtitle-project export;
- transparent subtitle-video export that is not already part of the current implemented contract.

## Source of truth order

When information conflicts, use this order:

1. Explicit user request for the current task.
2. Current tests and code behavior, especially compatibility and persisted-data behavior.
3. `docs/MVP_REQUIREMENTS_AND_ARCHITECTURE.md` and focused technical docs.
4. This file.
5. `KARAOKE_PROJECT_CONTEXT.md` and Git history.

Do not copy historical release notes back into README. Use Git history, tags, or a dedicated changelog if release notes are needed later.
