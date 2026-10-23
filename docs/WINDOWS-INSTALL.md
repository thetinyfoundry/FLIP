# Windows installation

FLIP is a Windows x64 beta. Windows 11 is the initial acceptance target; a clean-machine test is still required. No administrator service is needed. ARM64 and 32-bit packages are not provided.

## Prebuilt release

Download the Windows x64 ZIP from [Releases](https://github.com/thetinyfoundry/FLIP/releases), extract it and double-click `Install-FLIP.cmd`. Read the included `README.txt` for prerequisites and optional document engines. No compiler or SDK is needed for the packaged app.

Download the `Flip-*-win-x64.zip` asset from GitHub Releases, extract it, and run `Install-Release.ps1`. The script installs the bundled app into `%LOCALAPPDATA%\Programs\Flip`, verifies copied files, creates Desktop/Start Menu shortcuts and enables per-user startup. It does not require the .NET SDK or administrator rights; the .NET 10 Windows Desktop Runtime x64 is required.

## From a source download

GitHub's Download ZIP contains source code, not an installed application. Extract it first. Install the Microsoft .NET 10 SDK x64 from https://dotnet.microsoft.com/download/dotnet/10.0 (the SDK includes the runtime needed for local development).

Open PowerShell in the extracted repository:

```powershell
.\scripts\Build.ps1
.\scripts\Install.ps1
```

The installer copies the local build to `%LOCALAPPDATA%\Programs\Flip`, checks every copied file against its source with SHA256 and creates Desktop/Start shortcuts and enables per-user startup by default. Use `-NoShortcuts` / `-NoStartup` to skip those steps. It refuses to overwrite a folder without a Flip executable and refuses updates while Flip is running. An existing Flip folder is backed up beside the installation before copying. Keep that backup until the update is accepted.

PowerShell execution policy is respected. If scripts are blocked, use an allowed PowerShell session or the manual build commands in README; no global policy change is required. `Install.ps1` supports Windows PowerShell 5.1 and PowerShell 7.

To add document conversions:

```powershell
.\scripts\Build.ps1 -DocumentEngine
```

Pandoc is downloaded separately and its archive checksum is checked. LibreOffice is a separate optional installation, needed for rendering non-TXT documents to PDF/images. FLIP does not install it automatically. Basic image conversion does not need either tool.

## Everyday use

Start Flip from its shortcut, or run the installed `Flip.exe`. It stays in the notification area. Down Arrow is the default gesture key on new installations. Right-click the tray icon → **Impostazioni…** to choose arrows/Space/`<>` or capture another supported single key. Existing supported saved choices are preserved. The tray picker remains available without dragging.

Both orders work: start dragging and press the key, or hold the key first and then start dragging. Release the key once the wheel is visible. Outside the gesture the key still performs its normal action; a key pressed before dragging is initially normal keyboard input. While the wheel gesture is active, repeats of that press are reserved without swallowing an unmatched key-up. A short key tap released before any drag does not arm a later unrelated drag.

The chosen key is consumed during Explorer/Desktop mouse gestures so Explorer does not also receive the command. This does not change Windows sound settings. Ctrl/Alt/Shift/Fn/system keys are not supported standalone triggers. Avoid holding Ctrl/Shift during the gesture: native copy/move/link rules still apply outside the wheel. Press Escape before releasing the mouse to cancel. Drag integration with other file managers is not claimed.

Startup is enabled by default by the installer and can be disabled through **Avvia con Windows** in the tray menu or excluded with `-NoStartup`. Running only a built executable does not install startup/shortcuts. No registry file associations are changed. The classic context menu remains separately opt-in.

## Package requirements and validation

End users can download the versioned Windows x64 package from GitHub Releases. It includes component notices and checksums. The current build is framework-dependent and needs **.NET Desktop Runtime 10 x64**, not only the base .NET runtime. It is unsigned. Broader native drag/drop, listening, mixed-DPI and clean-Windows coverage remain testing limits, described in the release notes and [publication status](PUBLICATION-STATUS.md).

A self-contained build could remove that runtime prerequisite at the cost of a larger download and runtime servicing responsibility. Start with a portable ZIP plus this per-user installer; defer MSI/MSIX until the installation contract is accepted. Publish checksums alongside the package. A checksum proves integrity against a trusted published value; it is not code signing.

## Update and remove

Exit Flip before updating. Install to the same stable path. If startup points to another installation, remove that old startup entry using that installation before enabling the new one.

Before deleting an installation folder, run its `Flip.exe` with `--disable-startup`, `--remove-shortcuts`, and, if previously enabled, `--remove-context-menu`. Then exit Flip and remove its folder. Converted documents remain untouched. Settings and potentially sensitive diagnostics are under `%LOCALAPPDATA%\Flip`; review before sharing them.
