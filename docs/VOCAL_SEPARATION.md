# Local vocal separation and optional GPU acceleration

## Product contract (0.4.5.1)

- CPU is the bundled default engine; no account, training, Python setup or song upload.
- “制作伴奏” opens a task window. Choose non-vocal stem sum (A) or original minus vocals (B). The end-user workflow no longer offers a vocals-only result.
- A successful full-song WAV is copied to `accompaniments/<unique-id>.wav`, outside the media library. Schema 9 persists one current mapping per original media asset ID, original content hash/duration and generation date. The master asset, media list, lyrics/timing, metronome, crop and subtitle styles stay unchanged. Save/recovery includes mappings; failed/cancelled regeneration keeps the previous mapping. Old files remain for undo/recovery. Removing the source removes its mapping, not its physical files.
- Old projects migrate without guessing associations for previously imported separation assets. Missing accompaniment files disable accompaniment preview/export while leaving the original project usable. The generation button tooltip reports whether the currently loaded song has a usable accompaniment. All paths are workspace-relative; moving the entire workspace preserves associations. Older clients reject schema 9 instead of dropping mappings.
- The preview inspector has “节拍器” and “伴奏” tabs. The latter contains only a slider-style switch. It defaults off on project/song changes and is a transient listening choice, not project/timing data. Switching preserves position and playback speed; the timing page always uses the original song. Returning to preview restores its switch choice.
- Export first asks for original and/or accompaniment using checkboxes (at least one; accompaniment disabled when unavailable), then a common output basename. Outputs append ` -on vocal-` or ` -off vocal-` before `.mp4`, regardless of the preview switch. Existing final targets require confirmation. Exports are sequential with aggregate progress; cancellation cleans unfinished output and retains/reports any completed version. Both versions share images, subtitles, original-song visualizer data and crop.
- When the master song is a video, aligned background clips still avoid duplicate sound; shifted/cut clips of that same source use the accompaniment too when requested. Other videos retain their configured sound. This is rendering-time routing only: no clip mute flags or master references change.
- Pending taps must be applied or explicitly discarded before generation/export; unapplied lyric drafts must be handled first. The top toolbar order is new/open/make accompaniment/undo/save/export/cancel. Whole-song timing restart appears only in the timing page transport, immediately after play/pause.
- Separation is offline, full-song (not cropped), 44.1 kHz stereo PCM24. Trailing codec padding is aligned to the original media duration. Only a shared anti-clipping gain is applied. First release limits inputs to 30 minutes to bound RAM usage; this is not a low-memory guarantee.
- Cancellation terminates the worker process tree and discards unfinished audio. GPU processing failures fall back to CPU; the source song is never changed.

## GPU support and installation

The GPU button is disabled, with a visible explanation, unless all of these are met:

- Windows x64, CPU base component present, NVIDIA CUDA driver successfully initializes.
- Driver API supports CUDA 12.8 or newer.
- Compute capability is in the pinned support list: 7.5, 8.0, 8.6, 8.7, 8.9, 9.0, 10.0, 12.0. Pascal/Volta and unknown future architectures are deliberately excluded from the first release. AMD/Intel GPU backends are not implemented; CPU remains usable. With multiple NVIDIA adapters, select the first supported adapter, not necessarily adapter 0.

The hardware check calls the NVIDIA driver from the Windows system directory. No executable is discovered from the workspace or PATH. Detecting a supported architecture is not a guarantee of available VRAM: a real model inference self-test must pass before activation.

The embedded `gpu-runtime.json` pins the official PyTorch 2.8.0+cu128 CPython 3.12 Windows x64 wheel URL, size and SHA-256. The installer:

1. Requests consent for the download and applicable licenses. It never installs or updates a system GPU driver.
2. Requires 12 GiB free temporary space, with bounded timeouts, retry and byte-range resume. Valid cached downloads are reused; a hash mismatch never becomes executable.
3. Copies the bundled Python/common dependencies into a private staging folder under `%LOCALAPPDATA%/KaraokeFilmMaker/Components/Demucs` and extracts the verified wheel. Development `.lib` files and Torch headers are omitted, runtime DLLs and license materials retained. This runtime does not support compiling C++ extensions.
4. Runs the same local HTDemucs model on synthetic audio using the selected GPU. Only after successful inference does it atomically switch the active runtime pointer.
5. Keeps CPU untouched and the previous GPU installation active until successful replacement. Cancellation cleans staging but preserves resumable download bytes. An exclusive filesystem lock prevents installation/uninstallation during inference or concurrent installs across application instances.

The UI provides re-detection, repair (verified reinstall), uninstall, and official driver/licensing links. Uninstall removes Liraoke's active GPU runtime only, not system drivers, CPU, models or generated media. No object storage or Liraoke-hosted multi-GB GPU download is required. Availability and speed depend on upstream connectivity.

## Packaging CPU

The source repository does not contain runtime binaries. Build-time provisioning is separate from end-user GPU installation. `scripts/bootstrap-demucs.ps1` fetches the pinned Astral standalone Python 3.12.15 build, CPU Torch wheel and model with SHA-256 verification, installs pinned PyPI versions from `scripts/demucs-requirements.txt` into an isolated build venv and runs `pip check`. It does not install global Python or CUDA. Standard model: `adefossez/HTDemucs`, revision `cbc8a9b1a87023b7fd74e7b3412e6321c0eab003`.

Run `scripts/prepare-demucs-runtime.py` with `--python-root`, `--site-packages`, `--model` and `--output tools/demucs`, using absolute input paths. It refuses to overwrite a destination, preserves dependency license files, dereferences the model copy, verifies pinned core package versions and runs CPU inference before publishing `cpu/runtime.json`. The worker uses `-I`, the local safetensors model and an integrity check; no hub download occurs during inference.

`scripts/publish-mvp.ps1` automatically runs CPU bootstrap when the marker is missing and refuses incomplete assemblies. Thus existing CI and local release entrypoints provision the CPU runtime without requiring a new global Python prerequisite. The application packages Python and model files and can run after moving the installation; it does not ship the development venv or its absolute paths. Developer `dotnet build` does not require copying the full runtime to every test output; the development application can find `tools/demucs` in its repository. Fresh CI provisioning requires upstream network access and additional temporary disk space. No GPU package is downloaded by CI.

## Acceptance and distribution boundaries

Run unit tests for compatibility policy, download/hash/resume behavior, archive path validation, operation locks, unchanged project timing and disabled UI states. Run real CPU/GPU separation, install/self-test/uninstall in a private QA directory, cancellation, output media duration validation, and WPF snapshots. Old hardware/driver combinations and clean Windows installations still require real-machine acceptance.

Retain all third-party notices. CUDA/cuDNN are not all open-source. Public distribution still requires checking the exact binary/model licenses and obligations; a successful internal build is not a licensing sign-off. There is no cloud separation, AI generation or automatic lyric timing in this feature.
