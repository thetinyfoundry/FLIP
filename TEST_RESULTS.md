# Validation

## Current: 0.4.0-beta.3

Custom single-key shortcuts and both held-key/drag input orders are covered by **94 passing input checks**; the conversion engine has **99 passing checks**. See [the current input/install report](docs/CUSTOM-SHORTCUTS.md) for exact evidence and native acceptance limits. The earlier results below describe previous candidates.

## 0.4.0-beta.2 audit, 23 October 2026

See [the current audit](docs/AUDIT-2026-10-23.md) for results and limits. The current gesture is `<>`, not Ctrl. The historical results below describe beta.1 and do not certify native behavior of beta.2.

## Historical: 0.4.0-beta.1
Date: 22 October 2026. Raw evidence stays under ignored _local/evidence and _local/review; it is not exported.

## Automated results
- **99 conversion checks passed, 0 failed**, covering **57 conversion cases**, with LibreOffice 26.8.1.1 before the installer-requested Windows restart.
- **45 input/policy/layout/audio/bounds checks passed, 0 failed**.
- Release build and publish: zero compiler warnings/errors.
- Locked restore with NuGet audit enabled, all dependencies, and audit-connection errors treated as errors: succeeded. No NuGet advisories were reported for the resolved graph at this check. An earlier sandbox-only attempt could not reach the audit feed; it was not counted as a successful security check.
- PowerShell helper syntax parsed without errors. Script execution follows the host policy; no policy bypass was used. The prepared GitHub workflow has not run remotely.

Conversion checks include signatures/decoding, white JPEG alpha, EXIF orientation, PDF pages, Italian text, DOCX/EPUB structure, RTF/ODT/FB2/RST text readback, collision concurrency, cancellation, corrupt images, source hashes and no staging residue. A synthetic compressed document expanding past 256 MiB was rejected before conversion.
The initial FB2 readback failure was corrected and the full suite rerun successfully; failed runs were retained as private evidence.
The authorized LibreOffice upgrade initially exposed five failures (90 passed, 5 failed), reproduced on a second run. A short-path control succeeded. Moving the isolated Office profile out of the source-adjacent staging folder to the Windows temporary directory fixed the tested failures; a deep-folder PDF regression and temporary-profile cleanup checks now pass. These failed runs are retained privately.

Input/layout checks cover both Ctrl sides/orderings, swapped primary buttons, all TXT outputs represented in the wheel, missing-engine capabilities, unsupported batches, empty/oversized batches, radial geometry at 1/2/3/6/8/12 sectors, log rotation, preference parsing and original PCM audio with no clipping/faded endpoints.

## Native Windows flow
The real optional panel displayed twelve TXT output choices. UI actions selected PDF/DOCX/HTML/JPG and clicked Convert.
It reported four outputs and zero errors. Files were present beside the synthetic source; an independent SHA256 comparison confirmed the original remained unchanged. No job directory remained.
This is native panel E2E evidence, not proof of the complete Ctrl+Explorer drag on the new dynamic layout.

A separate runtime copy without Pandoc converted TXT to PDF/JPG and PDF to TXT. The installed module no longer falls back silently to ancestor development tools.

Earlier user tests established Ctrl-before and Ctrl-during wheel activation, swapped-button Explorer dragging and actual conversions. Physical mixed-DPI monitors, Desktop-origin drag and the complete cancellation matrix remain incomplete.

## Measured idle resources
One current candidate run, Windows 11 x64, .NET 10. After three seconds settling:
- Observation: 9.9916179 s.
- CPU time delta: 0 ms at the observed process-counter resolution.
- Working set: 107171840 bytes (**102.21 MiB**).
- Private bytes: 70864896 (**67.58 MiB**).
- Threads: 25. Handles: 672.
- Mouse listener off, no job/panel/wheel open.

This is a single observation, not proof of zero CPU use over time.
The local app folder is about 334.65 MiB including Pandoc and debug symbols; a small source archive is not a small runtime bundle.

## Visual and audio evidence
WPF renders show the slightly translucent backing, larger rim margin, dynamic compatible sectors and no extra-formats button. Native panel appearance and behavior were inspected.
The sound changed from a low tone to an original **40 ms tic**. Its waveform passed programmatic checks; subjective/device acceptance of this new sound is still open. Playback-request logs are not acoustic proof.

## Release gate
LibreOffice was upgraded from 24.2.5.2 to 26.8.1.1 with explicit authorization. The official installer SHA256 matched its published checksum and Windows SDK SignTool verified the Authenticode signature by The Document Foundation with zero errors/warnings. The personal profile was backed up privately. Installation returned 3010 (restart required); no automatic restart was performed.
The new engine passed the full corrected suite above. This is pre-restart evidence on synthetic documents, not qualification of arbitrary untrusted documents. Executable release remains blocked pending post-restart checks, broader native acceptance and redistribution work. See PUBLIC_RELEASE_REVIEW.md.
