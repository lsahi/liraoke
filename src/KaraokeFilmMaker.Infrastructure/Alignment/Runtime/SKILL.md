---
name: local-alignment
description: Install and inspect local lyric-alignment plugins, select a backend, and align corrected lyrics in Liraoke.
---

Use only the supplied Liraoke tools. No shell commands or credentials are needed.

1. Call `get_alignment_plugins` to read installed plugins, hardware eligibility, and the preferred backend for the current project.
2. For Japanese-majority songs prefer `nextfire`; otherwise prefer `qwen`. The user may explicitly select either backend. Do not silently install or switch to another backend when the preferred one is missing.
3. To install, call `install_alignment_plugin` with the catalog ID and `cpu` or `gpu`. Use GPU only when the hardware report permits it. The client presents the model license, size estimate and an Approve card. A successful download alone is not installation success: report ready only when the tool returns successful inference self-test. Cancellation or failure leaves the previous installation active.
4. `open_alignment_plugins` opens the same graphical management page when requested.
5. Preserve pronunciation correction. Before aligning Japanese lyrics, explain or offer `correct_japanese_readings` if the readings have not been corrected. Never claim alignment can repair kanji readings. Users can explicitly use manually reviewed readings instead.
6. Call `align_lyrics` after the user asks for automatic timing. The client obtains approval, applies validated results and saves with undo. Existing manual timings are protected. Report applied/skipped rows and save failures accurately; do not describe partial results as complete.

The alignment workflow first extracts vocals with the bundled Demucs service, then locates lyric segments on the vocals, crops vocals and aligns corrected units within each segment. Extraction uses installed compatible Demucs GPU acceleration when available, otherwise CPU; it does not install anything or silently fall back to original-song alignment. This adds processing time. Temporary vocals never become project media. It uses the original song clock. A failed segment is skipped with its reason; never invent uniform timing. Model license and language support are separate properties: NextFire is Japanese-specialized, English sections may need manual review. Local inference does not require an API key. ASR/cloud pronunciation correction still uses the user's existing configuration UI.

Large downloads use up to eight concurrent 16 MiB ranges, persist completed chunks across cancellation, and verify the whole file before installation. A retry can reuse verified cache/chunks; do not promise a particular speed. If the user already has a wheel or model file, offer `open_alignment_plugins` and guide them to “导入本地下载文件…”. The client accepts only files matching the pinned manifest. Do not suggest copying arbitrary virtual environments or using untrusted mirrors. Another instance's active installation holds an exclusive lock; do not interrupt it just to import a cache file.

For each plugin, request installation at most once per user message, including across CPU/GPU device changes. If installation fails or the user declines, explain that outcome and wait for a new explicit user request before retrying. A model download integrity failure is not a GPU compatibility or VRAM failure: CPU uses the same model weights and is not a download repair. Read-only status queries are allowed; do not use a second approval card as an automatic fallback.

Version 0.5.2.1: use import_lyrics for supplied TXT/LRC text and edit_lyrics for exact line-ID edits and Japanese span readings after get_lyrics. These require approval and save with undo. Do not claim readings cannot be edited. Alignment can fill unfinished rows while retaining manual units. Some automatic timings may be interpolated; report review warnings honestly. Adjacent automatic rows may overlap; third-row overlap remains forbidden. A failed neighbour-gap retry does not establish real vocal overlap.
