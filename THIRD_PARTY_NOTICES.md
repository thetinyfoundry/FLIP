# Third-party components

From v0.6.0, FLIP source code, icon, interface and procedurally generated wheel sound are licensed under GNU GPLv3 (`GPL-3.0-only`). This license does not replace third-party licenses.

| Component | Pinned version | License / upstream |
|---|---|---|
| PDFtoImage | 5.4.0 | MIT, https://github.com/sungaila/PDFtoImage |
| PdfPig | 0.1.16 | Apache-2.0, https://github.com/UglyToad/PdfPig |
| Microsoft.ML.OnnxRuntime | 1.32.0 | MIT, https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.32.0 |
| IS-Net (`isnet-general-use.onnx`, downloaded on demand) | v0.0.0 | Apache-2.0, https://github.com/danielgatis/rembg |
| SkiaSharp and native assets | 4.157.0 | MIT and native third-party notices, https://github.com/mono/SkiaSharp |
| PDFium Windows native binaries | 160.0.8165 | BSD-style and third-party notices, https://github.com/bblanchon/pdfium-binaries |
| FFmpeg essentials build (bundled only in the Microsoft Store package) | 9.1 | GPL-3.0-or-later. Binary and source information: https://github.com/GyanD/codexffmpeg/releases/tag/9.1 · Sources: https://git.ffmpeg.org/ffmpeg.git and the library list in `licenses/ffmpeg/README.txt` |
| whisper.cpp (bundled only in the Microsoft Store package) | 1.9.2 | MIT, https://github.com/ggml-org/whisper.cpp/tree/v1.9.2 |
| Pandoc (optional external executable) | 3.12.1 | GPL-2.0-or-later, https://github.com/jgm/pandoc/tree/3.12.1 |
| LibreOffice (separate user installation) | Not bundled | MPL-2.0 and additional licenses, https://www.libreoffice.org/about-us/licenses/ |
| .NET (included only in the Microsoft Store package) | 10 | MIT and runtime notices, https://github.com/dotnet/runtime |

The source repository contains no compiled third-party programs or libraries: NuGet and the packaging scripts obtain them from their publishers and verify them.

Each package includes the license texts of its components in the `licenses` folder. The optional Pandoc download preserves its own copyright and license files.
