# Microsoft Store edition

FLIP Converter is the Microsoft Store edition of FLIP. It comes from the same commit and has the same version number as the GitHub ZIP.

| | GitHub ZIP | Microsoft Store |
| --- | --- | --- |
| Signature | None | Microsoft |
| .NET | Installed by the user | Included |
| FFmpeg and Whisper | Downloaded on request | Included. The Whisper speech model is downloaded on request |
| Updates | From the FLIP settings | From the Store |
| Start with Windows | Per-user registry entry | Windows startup task |
| Context menu | Classic menu | Windows 11 menu |
| Settings and engines | `%AppData%\FLIP` | Package data folder. GitHub settings are imported once; engines and models already in `%AppData%\FLIP\Engines` are reused |
| Command line | `Flip.exe` | `flip-converter.exe` |

## Smart App Control

The Smart App Control policy in `C:\Windows\schemas\CodeIntegrity\ExamplePolicies\SmartAppControl.xml` accepts files signed by *Microsoft MarketPlace PCA 2011* for the Windows Store. The Store signs a catalog for the whole package, which also covers libraries without their own signature.

Programs installed separately, such as Pandoc, LibreOffice, Calibre and Tesseract, are evaluated on their own.

## Build the package

Requirements: .NET 10 SDK, Windows SDK and Visual Studio Build Tools with C++.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Package-Store.ps1
```

The `.msix` file in `dist\store` is unsigned because the Store signs it.

`scripts\Package-All.ps1` builds both editions from a clean commit and stops if their versions differ.

## Automated test

The [Store package workflow](../.github/workflows/store-package.yml) builds the package and installs it on a temporary Windows machine with a test signature. It runs FLIP inside the package under the Smart App Control rules in audit mode, checks conversions, startup, context menu registration and clean removal, and publishes nothing.
