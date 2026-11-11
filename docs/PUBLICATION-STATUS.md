# Publication status — 12 November 2026

The public repository contains the application source, build and installation scripts, assets, MIT license, component notices and documentation.

The Windows workflow passes locked restore, source/history pattern checks, compilation and input/policy/audio-logic tests. These automated checks do not replace physical drag/drop, listening or clean-machine testing.

The Windows download is built with `scripts/Package.ps1`. It includes the executable, installer scripts, dependency notices and SHA256 checksums. It requires the .NET 10 Windows Desktop Runtime x64. The package does not include debug symbols, local logs, personal documents or development caches.

Pandoc and FFmpeg are optional engines:
- FFmpeg enables audio and video format conversions and audio track extraction.
- Pandoc enables advanced document conversions.
- LibreOffice is a separate optional installation for Office/PDF rendering.
- The built-in core supports images and structured data (JSON, YAML, XML, CSV) with zero external tools.

The current version is **0.5.1**.

The [Windows package v0.5.1](https://github.com/thetinyfoundry/FLIP/releases/tag/v0.5.1) includes the Windows x64 ZIP and its SHA-256 checksum. Local validation covered the extracted installer, 100 input checks, 117 conversion checks and the wheel shortcut hint in the multiple-conversion window.
