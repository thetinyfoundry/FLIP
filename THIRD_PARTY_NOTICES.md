# Third-party components

Flip source code, icon, interface and procedurally generated wheel sound are MIT licensed. This license does not replace third-party licenses.

| Component | Pinned version | License / upstream |
|---|---|---|
| PDFtoImage | 5.4.0 | MIT, https://github.com/sungaila/PDFtoImage |
| PdfPig | 0.1.16 | Apache-2.0, https://github.com/UglyToad/PdfPig |
| SkiaSharp and native assets | 4.150.1 | MIT and native third-party notices, https://github.com/mono/SkiaSharp |
| PDFium Windows native binaries | 152.0.7961 | BSD-style and third-party notices, https://github.com/bblanchon/pdfium-binaries |
| Pandoc (optional external executable) | 3.12.1 | GPL-2.0-or-later, https://github.com/jgm/pandoc/tree/3.12.1 |
| LibreOffice (separate user installation) | Not bundled | MPL-2.0 and additional licenses, https://www.libreoffice.org/about-us/licenses/ |
| .NET | 10 | MIT and runtime notices, https://github.com/dotnet/runtime |

The source repository does not contain Pandoc, LibreOffice, NuGet package archives or compiled native DLLs. NuGet restores dependencies from their publishers.
The optional build-script download is from Pandoc's official release, verified by SHA256 before extraction. It preserves the supplied Pandoc copyright/license files in the local build.

A source repository being MIT does not make a binary bundle automatically MIT-only. Before distributing executable bundles, collect the exact native library notices and satisfy redistribution and corresponding-source requirements for every included component. Binary release publication is intentionally separate from source preparation.
