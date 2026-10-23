# FLIP for Windows

Convert a file without leaving Explorer. Drag it, press **Down Arrow**, and drop it on a format in the wheel. FLIP saves a new copy beside the original.

FLIP is a local file converter for Windows x64. The current version is **0.4.0-beta.3**.

![FLIP conversion wheel](docs/assets/wheel.png)

[Download the demo (MP4)](docs/assets/flip-demo.mp4) · [Italiano](README.it.md) · [Installation guide](docs/WINDOWS-INSTALL.md)

## Download and install

**[Download FLIP for Windows x64](https://github.com/thetinyfoundry/FLIP/releases/download/v0.4.0-beta.3/Flip-0.4.0-beta.3-win-x64.zip)** · [Release notes and checksum](https://github.com/thetinyfoundry/FLIP/releases/tag/v0.4.0-beta.3)

Open [Releases](https://github.com/thetinyfoundry/FLIP/releases), download the Windows x64 ZIP and extract the whole archive. Install the [.NET 10 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0), then double-click `Install-FLIP.cmd`. You do not need the SDK or a compiler to use the packaged app. The package includes `README.txt`, installation scripts, license notices and checksums.

Optional document conversions use Pandoc: close FLIP and run `Install-DocumentEngine.ps1` from the extracted package. It downloads the official engine directly from its publisher and verifies its checksum. LibreOffice is a separate optional installation for document rendering.

## Build from source

Install the [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0), download or clone this repository, and extract it. Open PowerShell in the project folder:

~~~powershell
.\scripts\Build.ps1
.\scripts\Install.ps1
~~~

The installer copies FLIP to `%LOCALAPPDATA%\Programs\Flip`, creates Desktop and Start Menu shortcuts, and enables startup when you sign in to Windows. These defaults apply per user. Use `-NoShortcuts` or `-NoStartup` to opt out. You can also change startup from the tray menu. Windows can independently disable startup apps.

FLIP stays in the notification area near the clock. Right-click its icon to open Settings, choose a shortcut, change the language or test the wheel sound. Italian Windows uses Italian by default; other system languages use English. You can override this in Settings.

The build needs Internet access to restore packages. Conversion runs locally without an account or cloud service. FLIP uses C#, .NET 10 and WPF.

## Use the wheel

1. Select a file in Explorer or on the Desktop.
2. Start dragging and press Down Arrow, or hold the key first and then start dragging. Release the key when the wheel appears.
3. Drop on an available output format. Press Escape to cancel.

Choose another supported single key in Settings. Avoid Ctrl, Alt and Shift during dragging: Windows uses them to change its own copy/move behavior. Outside the wheel, normal Windows drag and drop still applies. Press Escape before releasing the mouse to cancel the entire drag.

FLIP reserves the selected key during Explorer/Desktop mouse gestures. Outside that context it passes keyboard input through. Integration with other file managers has not been validated.

Output names include `.converted` and a number when needed. FLIP does not overwrite the original or an existing output. Batches offer formats supported by every selected file. Document-to-image conversion creates a separate image for each rendered page.

## Supported conversions

| Input | Outputs |
| --- | --- |
| PNG, JPG/JPEG, WebP, BMP, TIFF, GIF | PNG, JPG, WebP, PDF, BMP, TIFF, GIF, except the input format |
| TXT | PDF, PNG, JPG, WebP with the core; DOCX, EPUB, Markdown, HTML, ODT, RTF, FB2, RST with Pandoc |
| Markdown, HTML, DOCX, EPUB, ODT, RTF, FB2, RST | Other document formats with Pandoc; rendering to PDF/images also needs LibreOffice |
| PDF | PNG/JPG/WebP per page and extracted TXT; other document formats with Pandoc |

To add the optional Pandoc engine:

~~~powershell
.\scripts\Build.ps1 -DocumentEngine
~~~

The build helper downloads the pinned official Pandoc archive and checks its SHA256. LibreOffice is a separate optional installation; FLIP does not install it for you. See [third-party notices](THIRD_PARTY_NOTICES.md).

PDF-to-document conversion extracts text; it does not reconstruct the original layout. GIF input uses the first frame. HTML conversion does not execute JavaScript. Pages, audio/video conversion and OCR are not implemented.

## Validation

Local build and automated checks cover parts of the application. They do not replace real drag/drop, sound, display-scaling and document-fidelity checks on a clean Windows machine. Read [test results](TEST_RESULTS.md) and the [release review](PUBLIC_RELEASE_REVIEW.md) for evidence and remaining work. The Windows workflow checks build and input behavior; it does not publish binaries.

The packaged app requires the **.NET 10 Windows Desktop Runtime x64**. It is unsigned. Automated build/input checks run on GitHub's Windows runner; they do not certify every keyboard, display or output device. The version currently retains its prerelease suffix.

## Privacy and removal

The repository excludes local builds, downloaded tools, backups and runtime logs. The audit script checks source and history for common credential and personal-path patterns; it cannot guarantee that every secret or identifying detail is absent.

Runtime logs under `%LOCALAPPDATA%\Flip\logs` may contain document paths. Review and redact logs before sharing them. See [security guidance](SECURITY.md).

Before deleting an installation, run its executable with `--disable-startup` and `--remove-shortcuts`. If you enabled the optional classic Explorer menu, also run `--remove-context-menu`. Exit FLIP, then remove the installation folder. Converted documents stay where you saved them.

## License and inspiration

FLIP's own code and original assets use the [MIT License](LICENSE). You may fork, modify and redistribute them, including commercially, while retaining the copyright and license notices. A visible credit to **The Tiny Foundry** is appreciated. Third-party components have their own licenses.

The interaction is inspired by Tangerine. FLIP is an independent implementation with original branding and assets, without affiliation or a claim to the same feature coverage.

[Contributing](CONTRIBUTING.md) · [Changelog](CHANGELOG.md) · [Architecture](ARCHITECTURE.md)
