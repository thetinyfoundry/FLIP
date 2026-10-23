# Publication status — 23 October 2026

The public repository contains the application source, build and installation scripts, assets, MIT license, component notices and documentation. The initial partial upload has been replaced with the complete reviewed source tree.

The Windows workflow passed locked restore, source/history pattern checks, compilation and input/policy/audio-logic tests on GitHub Actions. Local input checks also passed: 94 checks, zero failures. These automated checks do not replace physical drag/drop, listening or clean-machine testing.

The Windows download is built with `scripts/Package.ps1`. It includes the executable, installer scripts, dependency notices and SHA256 checksums. It requires .NET 10 Windows Desktop Runtime x64. The package does not include debug symbols, local logs, personal documents or development caches.

Pandoc is optional. `Install-DocumentEngine.ps1` obtains the exact official version directly from its publisher, verifies its SHA256 and preserves its notices. LibreOffice is a separate optional installation. The core works without either; consult the README format table for limitations.

The current version remains 0.4.0-beta.3. Packaging it does not imply code signing or universal hardware certification. The included installer's Desktop/Start shortcuts and startup defaults apply per user; Windows can independently disable startup apps.

The [Windows package](https://github.com/thetinyfoundry/FLIP/releases/tag/v0.4.0-beta.3) is published. An independent download of the public ZIP matched SHA256 `a8ef0d868147c2f894402f32089aa95708681135cbad5d1f0d6f9f18fefaa559`. The archive contains 11,305,696 bytes. Its component notices and all entries in its checksum manifest were checked locally.

Earlier audits and release reviews describe the state at their dates. Statements that nothing was published are historical. This document and the current README supersede those publication-status statements; their unresolved test limitations remain relevant.
