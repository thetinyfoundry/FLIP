# Windows installation

FLIP runs on Windows 10 (2004 or later) and Windows 11, x64. No administrator service is needed. ARM64 and 32-bit packages are not provided.

## Microsoft Store

Install [FLIP Converter](https://apps.microsoft.com/detail/9ND9SDDBWW0W). The Store package is signed by Microsoft and includes .NET, FFmpeg and Whisper. It updates from the Store. Choose it if Smart App Control blocks the ZIP. If you already use the ZIP, exit FLIP and run `Disinstalla-FLIP.cmd` first: FLIP Converter imports your settings on its first start.

## ZIP from GitHub

1. Install the [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download `Flip-*-win-x64.zip` from [Releases](https://github.com/thetinyfoundry/FLIP/releases) and extract the whole archive.
3. Exit FLIP if it is running and open `Installa-FLIP.cmd`.

The installer verifies the package, copies it to `%LOCALAPPDATA%\Programs\Flip`, creates shortcuts and enables startup for your user. It needs no administrator rights or SDK. Use `-NoShortcuts` or `-NoStartup` with `Install.ps1` to skip those steps.

## From a source download

GitHub's Download ZIP contains source code, not an installed application. Extract it first. Install the Microsoft .NET 10 SDK x64 from https://dotnet.microsoft.com/download/dotnet/10.0 (the SDK includes the runtime needed for local development).

Open PowerShell in the extracted repository:

```powershell
.\scripts\Build.ps1
.\scripts\Install.ps1
```

The installer copies the local build to `%LOCALAPPDATA%\Programs\Flip`, checks every copied file against its source with SHA256 and creates Desktop/Start shortcuts and enables per-user startup by default. Use `-NoShortcuts` / `-NoStartup` to skip those steps. It refuses to overwrite a folder without a Flip executable and refuses updates while FLIP is running. An existing Flip folder is backed up beside the installation before copying. If the copy fails, the previous files are restored from that backup. After a verified copy, only the newest backup is kept.

PowerShell execution policy is respected. If scripts are blocked, use an allowed PowerShell session or the manual build commands in README; no global policy change is required. `Install.ps1` supports Windows PowerShell 5.1 and PowerShell 7.

To add document conversions:

```powershell
.\scripts\Build.ps1 -DocumentEngine
```

Pandoc is downloaded separately and its archive checksum is checked. LibreOffice is a separate optional installation. FLIP prefers it for rendering Word documents and spreadsheets to PDF and needs it for formats such as DOC and XLSB. Without it, the built-in exporters convert DOCX, XLSX, XLSM, XLTX, XLS and ODS. FLIP does not install it automatically. Basic image conversion does not need either tool.

## Everyday use

Start FLIP from its shortcut, or run the installed `Flip.exe`. It stays in the notification area. Down Arrow is the default gesture key on new installations. Right-click the tray icon → **Impostazioni…** to choose arrows/Space/`<>` or capture another supported single key. Existing supported saved choices are preserved. The tray picker remains available without dragging.

Both orders work: start dragging and press the key, or hold the key first and then start dragging. Release the key once the wheel is visible. Outside the gesture the key still performs its normal action; a key pressed before dragging is initially normal keyboard input. While the wheel gesture is active, repeats of that press are reserved without swallowing an unmatched key-up. A short key tap released before any drag does not arm a later unrelated drag.

The chosen key is consumed during Explorer/Desktop mouse gestures so Explorer does not also receive the command. This does not change Windows sound settings. Ctrl/Alt/Shift/Fn/system keys are not supported standalone triggers. Avoid holding Ctrl/Shift during the gesture: native copy/move/link rules still apply outside the wheel. Press Escape before releasing the mouse to cancel. Drag integration with other file managers is not claimed.

Startup is enabled by default by the installer and can be disabled through **Avvia con Windows** in the tray menu or excluded with `-NoStartup`. Running only a built executable does not install startup/shortcuts. No registry file associations are changed. The context menu is optional.

## Requirements and checks

The ZIP needs the **.NET Desktop Runtime 10 x64**, not only the base runtime. It is unsigned: compare it with the published `.sha256` file. A checksum proves integrity, not the publisher. Use the Microsoft Store edition if your PC blocks unsigned apps.

## Update and remove

Update from **Settings → Guide & Shortcuts → Updates**, or exit FLIP and run the installer from the new ZIP. The Store edition updates from the Store. If startup points to another installation, remove that old startup entry using that installation before enabling the new one.

Before deleting an installation folder, run its `Flip.exe` with `--disable-startup`, `--remove-shortcuts`, and, if previously enabled, `--remove-context-menu`. Then exit FLIP and remove its folder. Converted documents remain untouched. Settings are under `%APPDATA%\FLIP`; older settings, downloads and optional diagnostics are under `%LOCALAPPDATA%\Flip`. Review diagnostics before sharing them. `Disinstalla-FLIP.cmd` does this and keeps Pandoc in `%APPDATA%\FLIP\Engines`. The Store edition reuses that folder, so keep it if you move there. Uninstalling the Store edition removes its data.
