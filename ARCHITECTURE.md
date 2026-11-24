# Architecture

FLIP is a Windows x64 tray application written in C# for .NET 10 with WPF. All conversions run on the user's PC.

## Layout

| Path | Content |
| --- | --- |
| `src/Flip/Program.cs` | Entry point, single instance, tray host, drag handling, job lifetime |
| `src/Flip/Wheel*.cs` | Radial wheel, sector geometry and sound |
| `src/Flip/ShortcutHook.cs` | Low-level key handling during a drag |
| `src/Flip/Core` | Settings, format registry, output paths, image, PDF and text helpers |
| `src/Flip/Routing` | Conversion graph and route planner |
| `src/Flip/Engines` | Built-in engines, adapters for external programs and quick tools |
| `src/Flip/SettingsHub` | Settings window |
| `src/Flip/FlipPreviewWindow.cs` | QuickLook previews and crop editor |
| `src/Flip/WatchFolderService.cs` | Automatic folder conversions |
| `src/Flip/StoreEdition.cs` | Behaviour specific to the Microsoft Store package |
| `src/FlipShell` | Native context menu and startup helper for the Store package |
| `src/Store` | Package manifest and logos |
| `scripts` | Build, packaging, installation and repository checks |

## From a drag to a file

1. `ShortcutHook` recognises the configured key while the user drags. Unrelated typing passes through. The callback only posts a message: it does no file access.
2. `Wheel` shows the formats that `RoutePlanner` can reach from the selected files with the engines available.
3. `RoutePlanner` finds the cheapest sensible route (at most three steps) and rejects routes that would lose the kind of content.
4. Each job reads the sources without changing them and works in its own temporary folder.
5. `OutputPathAllocator` copies the result to the destination volume, applies the conflict policy and publishes it with a rename. Sources move to the Recycle Bin only when the user has chosen that option and every output has been saved.

## Engines

- **Built in:** images (SkiaSharp and Windows codecs), PDF (PDFium and PdfPig), Office and OpenDocument files, spreadsheets, data formats, archives, 3D, vector and geographic files.
- **External, optional:** FFmpeg, Pandoc, LibreOffice, Calibre, Whisper and Tesseract. They run as child processes with argument lists, timeouts and cancellation. Downloads are verified with SHA256 where FLIP downloads the file itself.

`SpreadsheetReader` reads XLSX, XLSM, XLTX, XLS and ODS without running macros or formulas. LibreOffice is preferred for spreadsheet PDFs when it is installed.

## Editions

Both editions come from the same commit and version.

- **GitHub ZIP:** needs the .NET 10 Desktop Runtime, stores settings in `%AppData%\FLIP` and updates itself on request.
- **Microsoft Store:** includes .NET, FFmpeg and Whisper, stores data in the package folder and is updated by the Store.

See [docs/MICROSOFT-STORE.md](docs/MICROSOFT-STORE.md).

## Tests

`--test-input` checks input handling, routing, user interface and regressions. `--test-conversions` runs conversions end to end and needs Pandoc, LibreOffice and FFmpeg. CI runs the first suite and installs and tests the Store package on a temporary Windows machine.
