**English** · [Italiano](README.it.md)

# FLIP

[![Version](https://img.shields.io/badge/version-v1.0.2-FF7A18?style=flat-square)](https://github.com/thetinyfoundry/FLIP/releases/tag/v1.0.2)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-1A1C23?style=flat-square)
[![CI](https://img.shields.io/github/actions/workflow/status/thetinyfoundry/FLIP/ci.yml?branch=main&style=flat-square&label=CI)](https://github.com/thetinyfoundry/FLIP/actions/workflows/ci.yml)
[![Downloads](https://img.shields.io/github/downloads/thetinyfoundry/FLIP/total?style=flat-square&label=downloads&color=FF7A18)](https://github.com/thetinyfoundry/FLIP/releases)
[![License](https://img.shields.io/badge/license-GPL--3.0--only-2ECC71?style=flat-square)](LICENSE)
[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-Support-FFDD00?style=flat-square&logo=buymeacoffee&logoColor=000000)](https://www.buymeacoffee.com/1berimHY9M)

**Drag, press a key, pick a format.** FLIP converts files on your Windows PC. Drag one or more files from File Explorer or the desktop, press **Down Arrow** (or your configured key), and drop them on a format in the wheel. The converted copy appears beside the original.

![FLIP conversion wheel](docs/assets/wheel.png)

[Demo video](docs/assets/flip-demo.mp4) · [Release notes](https://github.com/thetinyfoundry/FLIP/releases/tag/v1.0.2) · [Changelog](CHANGELOG.md) · [Windows guide](docs/WINDOWS-INSTALL.md) · [Privacy](PRIVACY.md)

## Install

### Microsoft Store (recommended)

<a href="https://apps.microsoft.com/detail/9ND9SDDBWW0W"><img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200" alt="Get it from Microsoft"></a>

Signed by Microsoft, with .NET, FFmpeg and Whisper included. Updates from the Store. Choose it if Smart App Control blocks the ZIP.

### ZIP from GitHub

1. Install the [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows 10 or 11 x64.
2. Download [`Flip-1.0.2-win-x64.zip`](https://github.com/thetinyfoundry/FLIP/releases/download/v1.0.2/Flip-1.0.2-win-x64.zip) and extract the entire archive.
3. Close FLIP and run `Installa-FLIP.cmd` from the extracted folder. The installer verifies the package and copies it into `%LOCALAPPDATA%\Programs\Flip`.

**Already using FLIP?** From 0.6.3 update in **Settings → Guide & Shortcuts → Updates**. FLIP contacts GitHub only when you press the button. From earlier versions run `Installa-FLIP.cmd` from the new ZIP. To move to the Store, run `Disinstalla-FLIP.cmd` from the new ZIP first. FLIP Converter imports your settings on its first start and reuses the engines and models you already have. Keep the `%AppData%\FLIP` folder.

Both editions share the same version number and are released together.

## What's new in 1.0

- **FLIP is on the Microsoft Store**, with one-click installation and a Windows 11 context menu entry.
- **Excel to PDF with nothing else installed.** XLSX, XLSM, XLTX, XLS and ODS, with every visible sheet, dates, currencies and merged cells.
- **Better PDFs.** Merge and metadata removal keep the text. Pages converted to images keep their proportions.
- **Your data intact.** Accents, semicolon CSV files, long codes and Excel dates stay correct.
- **Video and audio.** More compatible MP4 files, target size based on the real duration, no 20-minute limit.
- **Same engines in both editions.** Moving to the Store keeps your Whisper model, background-removal model and Pandoc.
- **More reliable.** Protected settings, originals restored by Undo, fixed watch folders and lower memory use.

[All fixes](CHANGELOG.md)

## What you can do

- **Wheel and aura.** The wheel follows the cursor across displays. Press **Tab** or scroll to switch between formats and tools, including when the wheel was opened by holding the mouse button. Mixed selections can be grouped by file type.
- **QuickLook.** Press **Space** while dragging to preview a file. Explore 3D models with Orbit and a four-view blueprint, inspect images and their color palettes, or preview text and audio waveforms.
- **Hold-to-open.** Enable it under *Activation & Input* and hold the mouse button on a selected file in File Explorer or on the Desktop (3 seconds by default, adjustable) to open the wheel without pressing any key. A small progress ring appears after half a second; press the right mouse button or Esc to close the wheel.
- **Quick tools.** Crop, rotate, resize, blur sensitive regions, merge PDFs, remove image backgrounds, extract text with OCR, and bundle outputs in ZIP or, when WinRAR is installed, RAR. Selected folders are archived next to the original folder.
- **Optional engines.** If a selected format needs an engine that is not installed, FLIP asks whether to install it before starting the conversion.
- **Archival PDF and offline transcription.** Convert supported files to PDF/A-2b. **Merge PDF** preserves each source page's original size and orientation; press **A** to choose Original, A4 or A3, portrait or landscape. With the optional Whisper engine, transcribe audio or video to TXT or SRT locally.
- **Watch folders, presets and accessibility.** Automatic conversions, recipes, the Settings Hub and Tobii gaze selection.

## Formats and engines

| Category | Examples | Engine |
| --- | --- | --- |
| Images | PNG, JPEG, WebP, BMP, TIFF, GIF, ICO | Built in |
| PDF and documents | PDF/A-2b, PDF page images, text extraction, DOCX, ODT, Markdown | Built in, Pandoc or LibreOffice depending on the route |
| Structured data | JSON, JSONL, YAML, TOML, XML, CSV, TSV, INI | Built in |
| Spreadsheets | XLSX, XLSM, XLTX, XLS, ODS to PDF, CSV, JSON and more | Built in (LibreOffice preferred for PDF when installed) |
| 3D and vector | OBJ, FBX, STL, PLY, GLB, GLTF, 3MF, DAE, SVG, DXF | Built in |
| Archives | ZIP, TAR, GZ, TGZ, CBZ, 7Z | Built in |
| Audio and video | MP3, WAV, FLAC, MP4, MKV, WebM, offline TXT and SRT | FFmpeg or Whisper, included in the Store edition |

Available outputs depend on the selected files and installed engines. Conversion runs on your PC. FLIP uses temporary workspace files for processing and saves completed outputs to the configured destination.

## Build from source

Install the [.NET 10 SDK x64](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build src/Flip/Flip.csproj -c Release -warnaserror
dotnet run --project src/Flip/Flip.csproj -c Release --no-build -- --test-input
dotnet run --project src/Flip/Flip.csproj -c Release --no-build -- --test-conversions
```

[Architecture](ARCHITECTURE.md) · [Contributing](CONTRIBUTING.md)

## Contributors

Thanks to [@AlphaGolf-sec](https://github.com/AlphaGolf-sec) for the second-launch fix in v0.6.1 and in-app updates in v0.6.3, and [@giovamolinaro](https://github.com/giovamolinaro) for the YAML, XML and CSV fix in v0.6.2.

## Support

<a href="https://www.buymeacoffee.com/1berimHY9M"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" height="44" alt="Buy me a coffee"></a>

## License

Starting with v0.6.0, FLIP's original code and assets are licensed under [GNU GPLv3](LICENSE) (`GPL-3.0-only`). Copyright © 2026 The Tiny Foundry / Raffaele Acanfora. [Third-party components](THIRD_PARTY_NOTICES.md) retain their respective licenses.
