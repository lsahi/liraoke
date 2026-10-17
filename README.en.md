# Liraoke

[中文](README.md)

Liraoke is a Windows karaoke video maker. It keeps a song, lyrics, and background media in one workspace so you can edit lyrics, time each sung unit, preview the result, and export an MP4 with progressive karaoke highlighting.

The project is currently being polished for its first public release.

## What it does

- Imports songs and image or video backgrounds;
- Imports TXT, LRC, and SRT lyrics or searches LRCLIB;
- Edits Chinese, English, and Japanese lyrics with offline language and reading preparation;
- Records timing per character, word, or sung syllable from keyboard input;
- Adjusts subtitle styles, aspect ratio, clips, output range, beat assistance, and audio visualizations;
- Previews and exports H.264/AAC MP4 video.

## Main modules

| Module | Purpose |
| --- | --- |
| Workspace | Manages `.lrn` projects and media with recovery and portable relative paths |
| Lyrics | Imports, searches, edits, and analyzes multilingual lyrics |
| Timing | Records sung-unit timing with retakes, slow playback, and optional beat snapping |
| Timeline | Arranges a single image/video track, mixes audio, and selects the output range |
| Subtitles & visuals | Controls progressive highlighting, readings, fonts, aspect ratio, and five audio visualizers |
| Preview & export | Uses shared rendering behavior to preview and export MP4 |

## Quick start

1. Download and fully extract the Windows x64 portable package; do not copy only the EXE.
2. Run `Liraoke.exe`, create a workspace, and import a song.
3. Add lyrics and background media, then press Space along with the vocal in the timing view.
4. Preview the result, choose output settings, and export an MP4.

The portable package includes the .NET runtime, FFmpeg, and the Japanese dictionary. Visual Studio, the SDK, a global FFmpeg installation, and Japanese speech components are not required.

Projects use the `.lrn` format and relative paths, so a complete workspace can be moved as a unit. Legacy single-track `project.kfp` projects can still be opened.

## Current boundaries

- Windows x64 only;
- A single background image/video track;
- Automatic language detection and Japanese readings require human review;
- Beat analysis is optional assistance, not the source of timeline truth;
- No AI-generated first cut, advanced video effects, or external subtitle-project export.

## System requirements and tested environment

The minimum supported system is **Windows 10 version 1809 (build 17763), x64**. Later 64-bit Windows versions are also supported. The portable package includes the required .NET runtime, FFmpeg, and Japanese dictionary.

Automated tests and local builds currently run on this development machine:

| Component | Tested configuration |
| --- | --- |
| Operating system | Windows 11 Pro 64-bit, version 10.0.26200 (build 26200) |
| Processor | AMD Ryzen 7 9800X3D |
| Memory | 32 GB (approximately 30.8 GiB physical memory reported by the system) |
| Discrete GPU | NVIDIA GeForce RTX 5080, driver 32.0.16.1088 |
| Integrated GPU | AMD Radeon Graphics, driver 32.0.21030.2001 |

This is a verified configuration, not a minimum hardware requirement. Long previews, full-song exports, and performance on older or lower-spec hardware still require device testing.

See [MVP requirements and architecture](docs/MVP_REQUIREMENTS_AND_ARCHITECTURE.md) for details. Language plug-in authors should read the [language plug-in guide](docs/LANGUAGE_PLUGINS.md).

## License

See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
