using System.IO;
using System.Text;
using SkiaSharp;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

/// <summary>
/// Motore built-in PDF: renderizza PDF in immagini (via PDFtoImage) ed estrae testo (via PdfPig).
/// Genera PDF nativi multi-pagina ad alta qualità da immagini o testo (via SkiaSharp SKDocument).
/// </summary>
public sealed class PdfEngine : IConversionEngine
{
    public string Id => "pdf_builtin";
    public string DisplayName => "PDF Rendering & Document Hub Engine";
    public bool IsExternal => false;

    private readonly List<ConversionEdge> _supportedEdges = [];

    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    // Formati rasterizzabili dal PDF
    private static readonly string[] PdfToImageFormats = ["png", "jpg", "jpeg", "webp", "bmp"];
    // Formati di testo/documento estraibili dal PDF
    private static readonly string[] PdfToTextFormats = ["txt", "md", "html", "json", "docx", "odt", "rtf"];
    // Formati che possono diventare PDF
    private static readonly string[] ImageToPdfFormats = ["png", "jpg", "jpeg", "webp", "bmp"];
    private static readonly string[] TextToPdfFormats = ["txt", "md", "html", "rtf", "docx", "odt", "xlsx", "xlsm", "xltx", "xls", "ods", "csv", "tsv", "json", "yaml", "xml"];
    // Fogli di calcolo e tabelle: diventano tabelle PDF, non testo.
    private static readonly string[] SheetFormats = ["xlsx", "xlsm", "xltx", "xls", "ods", "csv", "tsv"];

    public PdfEngine()
    {
        // PDF -> PDF/A-2b (ISO 19005-2)
        _supportedEdges.Add(new ConversionEdge("pdf", "pdfa", Id, 10));

        // PDF -> Immagini
        foreach (var dst in PdfToImageFormats)
            _supportedEdges.Add(new ConversionEdge("pdf", dst, Id, 10));

        // PDF -> Testo
        foreach (var dst in PdfToTextFormats)
            _supportedEdges.Add(new ConversionEdge("pdf", dst, Id, 10));

        // Immagini -> PDF & PDF/A
        foreach (var src in ImageToPdfFormats)
        {
            _supportedEdges.Add(new ConversionEdge(src, "pdf", Id, 10));
            _supportedEdges.Add(new ConversionEdge(src, "pdfa", Id, 12));
        }

        // Testo/Documenti -> PDF (& PDF/A diretto solo per i formati non gestiti da LibreOffice:
        // per docx/odt/rtf la rotta passa da src -> pdf -> pdfa così LibreOffice viene sempre preferito quando installato)
        foreach (var src in TextToPdfFormats)
        {
            _supportedEdges.Add(new ConversionEdge(src, "pdf", Id, 10));
            if (src is not ("docx" or "odt" or "rtf"))
                _supportedEdges.Add(new ConversionEdge(src, "pdfa", Id, 12));
        }
    }

    public bool IsAvailable(FlipSettings settings) => true;

    public async Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;

            if (!File.Exists(srcPath))
                return new EngineExecutionResult(false, "", $"File non trovato: {srcPath}");

            string srcExt = (context.SourceExtension ?? Path.GetExtension(srcPath)).TrimStart('.').ToLowerInvariant();
            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();

            string? dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            context.Progress?.Report(0.10);

            if (srcExt == "pdf" && PdfToImageFormats.Contains(dstExt))
                return await RenderPdfToImagesAsync(srcPath, dstPath, dstExt, context).ConfigureAwait(false);

            if (File.Exists(dstPath)) File.Delete(dstPath);

            bool stripMetadata = context.CustomParameters.TryGetValue("strip_metadata", out var stripStr) &&
                (stripStr.Equals("true", StringComparison.OrdinalIgnoreCase) || stripStr == "1");

            if (dstExt == "pdfa")
            {
                if (srcExt == "pdf")
                {
                    ConvertToPdfA2b(srcPath, dstPath, stripMetadata);
                }
                else if (ImageToPdfFormats.Contains(srcExt))
                {
                    string tempPdf = dstPath + ".base.tmp.pdf";
                    try
                    {
                        ImageToPdf(srcPath, tempPdf, stripMetadata);
                        ConvertToPdfA2b(tempPdf, dstPath, stripMetadata);
                    }
                    finally
                    {
                        try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
                    }
                }
                else if (TextToPdfFormats.Contains(srcExt))
                {
                    string tempPdf = dstPath + ".base.tmp.pdf";
                    try
                    {
                        TextToPdf(srcPath, tempPdf, srcExt, stripMetadata, context.CancellationToken);
                        ConvertToPdfA2b(tempPdf, dstPath, stripMetadata);
                    }
                    finally
                    {
                        try { if (File.Exists(tempPdf)) File.Delete(tempPdf); } catch { }
                    }
                }
                else
                {
                    return new EngineExecutionResult(false, "", $"Conversione PDF/A non supportata da: {srcExt}");
                }
            }
            else if (srcExt == "pdf" && PdfToTextFormats.Contains(dstExt))
            {
                ExtractPdfText(srcPath, dstPath, dstExt, context);
            }
            else if (ImageToPdfFormats.Contains(srcExt) && dstExt == "pdf")
            {
                ImageToPdf(srcPath, dstPath, stripMetadata);
            }
            else if (TextToPdfFormats.Contains(srcExt) && dstExt == "pdf")
            {
                TextToPdf(srcPath, dstPath, srcExt, stripMetadata, context.CancellationToken);
            }
            else
            {
                return new EngineExecutionResult(false, "", $"Conversione non supportata: {srcExt} -> {dstExt}");
            }

            context.Progress?.Report(1.0);
            long bytes = File.Exists(dstPath) ? new FileInfo(dstPath).Length : 0;
            return new EngineExecutionResult(true, dstPath, null, bytes);
        }
        catch (OperationCanceledException)
        {
            return new EngineExecutionResult(false, "", "Conversione PDF annullata.");
        }
        catch (Exception ex)
        {
            return new EngineExecutionResult(false, "", $"Errore PDF ({context.SourceExtension} -> {context.TargetExtension}): {ex.Message}");
        }
    }

    // ── PDF -> Immagine ──────────────────────────────────────────────────────

    private static Task<EngineExecutionResult> RenderPdfToImagesAsync(
        string pdfPath, string outputPath, string imageFormat, ConversionContext ctx)
    {
        return Task.Run(() =>
        {
            byte[] pdf = File.ReadAllBytes(pdfPath);
            int pageCount = PDFtoImage.Conversion.GetPageCount(pdf);
            if (pageCount == 0) throw new IOException("PDF senza pagine.");
            if (pageCount > 200) throw new IOException("PDF oltre il limite di 200 pagine.");

            var options = PdfPages.RenderOptions(pdf);
            int quality = 92;
            if (ctx.CustomParameters.TryGetValue("quality", out var qStr) && int.TryParse(qStr, out int qVal))
                quality = Math.Clamp(qVal, 1, 100);

            var outputs = new string[pageCount];
            long totalBytes = 0;
            using var pages = PDFtoImage.Conversion.ToImages(pdf, options: options).GetEnumerator();
            for (int i = 0; i < pageCount; i++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                if (!pages.MoveNext()) throw new IOException($"Pagina PDF {i + 1} mancante.");

                using var bitmap = pages.Current;
                ctx.CancellationToken.ThrowIfCancellationRequested();
                string pagePath = i == 0
                    ? outputPath
                    : Path.ChangeExtension(outputPath, $"page-{i + 1:000}{Path.GetExtension(outputPath)}");
                WritePageImage(bitmap, pagePath, imageFormat, quality);
                outputs[i] = pagePath;
                totalBytes += new FileInfo(pagePath).Length;
                ctx.Progress?.Report(0.10 + 0.90 * (i + 1) / pageCount);
            }

            ctx.CancellationToken.ThrowIfCancellationRequested();
            return new EngineExecutionResult(true, outputPath, null, totalBytes) { OutputFilePaths = outputs };
        }, ctx.CancellationToken);
    }

    private static void WritePageImage(SKBitmap bitmap, string path, string format, int quality)
    {
        if (format == "bmp")
        {
            ConversionEngine.WriteImage(bitmap, path, "BMP");
            return;
        }

        SKEncodedImageFormat encoding = format switch
        {
            "png" => SKEncodedImageFormat.Png,
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            "webp" => SKEncodedImageFormat.Webp,
            _ => throw new NotSupportedException($"Formato immagine PDF non supportato: {format}.")
        };
        using var data = bitmap.Encode(encoding, quality)
            ?? throw new IOException($"Encoder immagine non disponibile per il formato {format}.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
        stream.Flush(flushToDisk: true);
    }

    // ── PDF -> Testo ──────────────────────────────────────────────────────────

    private static void ExtractPdfText(string pdfPath, string outputPath, string textFormat, ConversionContext ctx)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
        if (pdf.NumberOfPages == 0) throw new IOException("PDF senza pagine.");
        if (pdf.NumberOfPages > 200) throw new IOException("PDF oltre il limite di 200 pagine.");

        var sb = new StringBuilder();
        int pageNum = 0;
        foreach (var page in pdf.GetPages())
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            pageNum++;
            string text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);
            sb.AppendLine(text);
            sb.AppendLine();
        }

        string fullText = sb.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(fullText))
            throw new IOException("Il PDF non contiene testo estraibile. Per scansioni usa prima PNG/JPG.");

        ctx.Progress?.Report(0.70);

        if (textFormat is "docx" or "odt" or "rtf")
        {
            OfficeOpenXmlEngine.WriteFromPlainText(fullText, outputPath, textFormat);
            return;
        }

        var utf8 = new UTF8Encoding(false);
        string output = textFormat switch
        {
            "txt" => fullText,
            "md" => fullText,
            "html" => $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(pdfPath))}</title></head><body><pre>{System.Net.WebUtility.HtmlEncode(fullText)}</pre></body></html>",
            "json" => SerializePdfTextToJson(pdf, fullText),
            _ => fullText
        };

        File.WriteAllText(outputPath, output, utf8);
    }

    private static string SerializePdfTextToJson(UglyToad.PdfPig.PdfDocument pdf, string fullText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"pageCount\": {pdf.NumberOfPages},");
        sb.AppendLine($"  \"text\": {System.Text.Json.JsonSerializer.Serialize(fullText, TextFiles.ReadableJsonLine)}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── Immagine -> PDF ──────────────────────────────────────────────────────

    private static void ImageToPdf(string imagePath, string pdfPath, bool stripMetadata = false)
    {
        using var bitmap = LoadSkiaBitmap(imagePath);
        using var stream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var metadata = stripMetadata ? EmptyPdfMetadata() : default;
        using var document = (stripMetadata ? SKDocument.CreatePdf(stream, metadata) : SKDocument.CreatePdf(stream))
            ?? throw new IOException("Encoder PDF non disponibile.");

        float pageW = 595, pageH = 842, margin = 30;
        using var image = SKImage.FromBitmap(bitmap);
        var canvas = document.BeginPage(pageW, pageH);
        canvas.Clear(SKColors.White);

        float scale = Math.Min((pageW - 2 * margin) / bitmap.Width, (pageH - 2 * margin) / bitmap.Height);
        float w = bitmap.Width * scale, h = bitmap.Height * scale;
        canvas.DrawImage(image,
            new SKRect((pageW - w) / 2, (pageH - h) / 2, (pageW + w) / 2, (pageH + h) / 2),
            new SKSamplingOptions(SKFilterMode.Linear));

        document.EndPage();
        document.Close();
        stream.Flush(true);
    }

    // Con l'orientamento EXIF: le foto del telefono finirebbero coricate nel PDF.
    private static SKBitmap LoadSkiaBitmap(string path) => ImageEngine.LoadBitmap(path);

    private static SKDocumentPdfMetadata EmptyPdfMetadata() => new()
    {
        Title = "",
        Author = "",
        Subject = "",
        Keywords = "",
        Creator = "",
        Producer = ""
    };

    // ── Testo & Documenti Ricchi -> PDF ───────────────────────────────────────

    private static void TextToPdf(string textPath, string pdfPath, string srcExt, bool stripMetadata, CancellationToken token)
    {
        if (SheetFormats.Contains(srcExt))
        {
            if (new FileInfo(textPath).Length > 100 * 1024 * 1024)
                throw new IOException("Foglio di calcolo oltre il limite di 100 MiB.");
            SpreadsheetPdf.Write(SpreadsheetReader.Read(textPath), pdfPath,
                stripMetadata ? EmptyPdfMetadata() : new SKDocumentPdfMetadata { Creator = "FLIP", Producer = "FLIP PDF Engine" }, token);
            return;
        }
        if (new FileInfo(textPath).Length > 10 * 1024 * 1024)
            throw new IOException("File testo troppo grande per la conversione PDF (limite 10 MiB).");

        if (OfficeOpenXmlEngine.TryReadRichDocumentForPdf(textPath, srcExt) is { } richDoc)
        {
            RichDocumentToPdf(richDoc, pdfPath, stripMetadata);
            return;
        }

        string text = OfficeOpenXmlEngine.ExtractPlainTextForPdf(textPath, srcExt);
        if (string.IsNullOrWhiteSpace(text))
            throw new IOException("Documento privo di testo da esportare in PDF.");

        using var stream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var metadata = stripMetadata ? EmptyPdfMetadata() : default;
        using var document = (stripMetadata ? SKDocument.CreatePdf(stream, metadata) : SKDocument.CreatePdf(stream))
            ?? throw new IOException("Encoder PDF non disponibile.");
        using var typeface = SKTypeface.FromFamilyName("Consolas");
        using var font = new SKFont(typeface, 11);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        float pageW = 595, pageH = 842;
        SKCanvas canvas = document.BeginPage(pageW, pageH);
        canvas.Clear(SKColors.White);
        float y = 45;
        int pages = 1;

        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string value = line.Replace("\t", "    ");
            int offset = 0;
            do
            {
                if (y > 805)
                {
                    document.EndPage();
                    if (++pages > 200) throw new IOException("Testo oltre il limite di 200 pagine PDF.");
                    canvas = document.BeginPage(pageW, pageH);
                    canvas.Clear(SKColors.White);
                    y = 45;
                }
                int count = Math.Min(78, value.Length - offset);
                if (count > 0 && char.IsHighSurrogate(value[offset + count - 1]) && offset + count < value.Length) count--;
                if (count > 0)
                    canvas.DrawText(value.Substring(offset, count), 35, y, SKTextAlign.Left, font, paint);
                y += 15;
                offset += count;
            }
            while (offset < value.Length);
        }

        document.EndPage();
        document.Close();
        stream.Flush(true);
    }

    internal static void RichDocumentToPdf(RichDocument doc, string pdfPath, bool stripMetadata = false)
    {
        if (!doc.HasAnyContent())
            throw new IOException("Documento privo di testo da esportare in PDF.");

        float pageW = Math.Clamp(doc.PageWidthPt, 180f, 2400f);
        float pageH = Math.Clamp(doc.PageHeightPt, 180f, 2400f);
        float marginL = Math.Clamp(doc.MarginLeftPt, 18f, Math.Max(18f, pageW * 0.30f));
        float marginR = Math.Clamp(doc.MarginRightPt, 18f, Math.Max(18f, pageW * 0.30f));
        float marginT = Math.Clamp(doc.MarginTopPt, 18f, Math.Max(18f, pageH * 0.25f));
        float marginB = Math.Clamp(doc.MarginBottomPt, 18f, Math.Max(18f, pageH * 0.25f));
        float contentW = Math.Max(120f, pageW - marginL - marginR);
        float contentBottom = pageH - marginB;

        using var fonts = new PdfFontCache();
        using var stream = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var metadata = stripMetadata
            ? EmptyPdfMetadata()
            : new SKDocumentPdfMetadata { Creator = "FLIP", Producer = "FLIP PDF Engine" };
        using var document = SKDocument.CreatePdf(stream, metadata)
            ?? throw new IOException("Encoder PDF non disponibile.");

        int pages = 1;
        SKCanvas canvas = document.BeginPage(pageW, pageH);
        canvas.Clear(SKColors.White);
        float cursorY = marginT;
        bool pageHasContent = false;

        void StartNewPage()
        {
            document.EndPage();
            if (++pages > 200)
                throw new IOException("Documento oltre il limite di 200 pagine PDF.");
            canvas = document.BeginPage(pageW, pageH);
            canvas.Clear(SKColors.White);
            cursorY = marginT;
            pageHasContent = false;
        }

        using var paint = new SKPaint { IsAntialias = true };
        using var strokePaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        foreach (var block in doc.Blocks)
        {
            if (block is RichParagraph para)
            {
                if (para.PageBreakBefore && pageHasContent)
                    StartNewPage();

                // Suddividi su eventuali interruzioni di pagina inline (<w:br w:type="page"/>)
                var segments = SplitParagraphOnPageBreaks(para);
                for (int segIdx = 0; segIdx < segments.Count; segIdx++)
                {
                    if (segIdx > 0 && pageHasContent)
                        StartNewPage();

                    var segPara = segments[segIdx];
                    if (pageHasContent && segPara.SpaceBeforePt > 0f)
                        cursorY += segPara.SpaceBeforePt;

                    var lines = LayoutParagraph(segPara, contentW, doc.DefaultFontFamily, doc.DefaultFontSizePt, fonts);
                    if (lines.Count == 0)
                        continue;

                    for (int li = 0; li < lines.Count; li++)
                    {
                        var line = lines[li];
                        if (pageHasContent && cursorY + line.Height > contentBottom)
                            StartNewPage();

                        if (segPara.BackgroundColorHex is { } pBg && TryParseHexColor(pBg, out var bgColor))
                        {
                            paint.Style = SKPaintStyle.Fill;
                            paint.Color = bgColor;
                            canvas.DrawRect(marginL + line.BoxLeftOffset, cursorY, Math.Max(20f, contentW - line.BoxLeftOffset - segPara.IndentRightPt), line.Height, paint);
                        }

                        DrawLaidOutLine(canvas, line, segPara, marginL, cursorY, li == 0, li == lines.Count - 1, fonts, paint, strokePaint);
                        cursorY += line.Height;
                        pageHasContent = true;
                    }

                    if (segPara.HasBottomBorder)
                    {
                        strokePaint.Color = new SKColor(0xB8, 0xBF, 0xCC);
                        strokePaint.StrokeWidth = 0.75f;
                        float borderY = cursorY + 1.5f;
                        canvas.DrawLine(marginL + segPara.IndentLeftPt, borderY, marginL + contentW - segPara.IndentRightPt, borderY, strokePaint);
                        cursorY += 4f;
                    }

                    cursorY += segPara.SpaceAfterPt;
                }
            }
            else if (block is RichTable table)
            {
                RenderTable(table, doc, marginL, contentW, contentBottom, fonts, paint, strokePaint,
                    () => canvas, () => cursorY, y => cursorY = y, () => pageHasContent, v => pageHasContent = v, StartNewPage);
            }
        }

        document.EndPage();
        document.Close();
        stream.Flush(true);
    }

    private sealed class PdfFontCache : IDisposable
    {
        private readonly Dictionary<(string Family, bool Bold, bool Italic), SKTypeface> _cache = new();
        private readonly List<SKTypeface> _owned = [];

        private static readonly Dictionary<string, string[]> AliasMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Calibri Light"] = ["Calibri", "Segoe UI Light", "Segoe UI", "Arial"],
            ["Calibri"] = ["Calibri", "Segoe UI", "Arial"],
            ["Aptos"] = ["Aptos", "Segoe UI", "Calibri", "Arial"],
            ["Aptos Display"] = ["Aptos Display", "Segoe UI Semibold", "Calibri", "Arial"],
            ["Cambria"] = ["Cambria", "Georgia", "Times New Roman"],
            ["Helvetica"] = ["Arial", "Segoe UI"],
            ["Helvetica Neue"] = ["Segoe UI", "Arial"],
            ["Times"] = ["Times New Roman", "Georgia"],
            ["Courier"] = ["Courier New", "Consolas"],
            ["San Francisco"] = ["Segoe UI", "Arial"]
        };

        public SKTypeface GetTypeface(string? requestedFamily, bool bold, bool italic)
        {
            string family = string.IsNullOrWhiteSpace(requestedFamily) ? "Calibri" : requestedFamily.Trim();
            var key = (family.ToLowerInvariant(), bold, italic);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var style = new SKFontStyle(
                bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

            SKTypeface? resolved = TryMatchFamily(family, style);
            if (resolved == null && AliasMap.TryGetValue(family, out var aliases))
            {
                foreach (string alias in aliases)
                {
                    resolved = TryMatchFamily(alias, style);
                    if (resolved != null) break;
                }
            }

            if (resolved == null)
            {
                foreach (string fallback in new[] { "Calibri", "Segoe UI", "Arial", "Times New Roman" })
                {
                    resolved = TryMatchFamily(fallback, style);
                    if (resolved != null) break;
                }
            }

            if (resolved == null)
                resolved = SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;

            if (!ReferenceEquals(resolved, SKTypeface.Default))
                _owned.Add(resolved);
            _cache[key] = resolved;
            return resolved;
        }

        public SKTypeface GetFallbackForCodePoint(int codePoint, bool bold, bool italic, SKTypeface primary)
        {
            var style = new SKFontStyle(
                bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var matched = SKFontManager.Default.MatchCharacter(null, style, null, codePoint);
            if (matched != null)
            {
                _owned.Add(matched);
                return matched;
            }
            return primary;
        }

        private static SKTypeface? TryMatchFamily(string family, SKFontStyle style)
        {
            var tf = SKFontManager.Default.MatchFamily(family, style);
            if (tf == null) return null;
            if (!string.IsNullOrWhiteSpace(tf.FamilyName) &&
                tf.FamilyName.Contains(family.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                return tf;
            tf.Dispose();
            return null;
        }

        public void Dispose()
        {
            foreach (var tf in _owned)
            {
                try { tf.Dispose(); } catch { }
            }
            _owned.Clear();
            _cache.Clear();
        }
    }

    private sealed class PlacedFragment
    {
        public string Text = "";
        public RichRun Style = new();
        public SKTypeface Typeface = SKTypeface.Default;
        public float EffectiveFontSize;
        public float Width;
        public float Ascent;
        public float Descent;
        public bool IsWhitespace;
        public bool IsTab;
    }

    private sealed class LaidOutLine
    {
        public List<PlacedFragment> Fragments { get; } = [];
        public float StartXOffset;
        public float BoxLeftOffset;
        public float AvailableWidth;
        public float ContentWidth;
        public float MaxAscent;
        public float MaxDescent;
        public float Height;
        public bool EndedWithHardBreak;
    }

    private static List<RichParagraph> SplitParagraphOnPageBreaks(RichParagraph para)
    {
        if (!para.Runs.Any(r => r.IsPageBreak))
            return [para];

        var result = new List<RichParagraph>();
        var current = CloneParagraphShell(para, isFirstSegment: true);
        foreach (var run in para.Runs)
        {
            if (run.IsPageBreak)
            {
                result.Add(current);
                current = CloneParagraphShell(para, isFirstSegment: false);
            }
            else
            {
                current.Runs.Add(run);
            }
        }
        result.Add(current);
        return result;
    }

    private static RichParagraph CloneParagraphShell(RichParagraph src, bool isFirstSegment) => new()
    {
        HeadingLevel = src.HeadingLevel,
        Alignment = src.Alignment,
        SpaceBeforePt = isFirstSegment ? src.SpaceBeforePt : 0f,
        SpaceAfterPt = src.SpaceAfterPt,
        LineSpacingMultiplier = src.LineSpacingMultiplier,
        IndentLeftPt = src.IndentLeftPt,
        IndentRightPt = src.IndentRightPt,
        FirstLineIndentPt = isFirstSegment ? src.FirstLineIndentPt : 0f,
        PageBreakBefore = false,
        IsListItem = isFirstSegment && src.IsListItem,
        IsOrderedList = src.IsOrderedList,
        ListLevel = src.ListLevel,
        ListMarker = isFirstSegment ? src.ListMarker : null,
        BackgroundColorHex = src.BackgroundColorHex,
        HasBottomBorder = src.HasBottomBorder
    };

    private static List<LaidOutLine> LayoutParagraph(
        RichParagraph para,
        float containerWidth,
        string defaultFontFamily,
        float defaultFontSize,
        PdfFontCache fonts)
    {
        var lines = new List<LaidOutLine>();
        float indentLeft = Math.Clamp(para.IndentLeftPt, 0f, Math.Max(0f, containerWidth - 60f));
        float indentRight = Math.Clamp(para.IndentRightPt, 0f, Math.Max(0f, containerWidth - indentLeft - 60f));

        // Per gli elenchi il testo parte a indentLeft e il marker viene disegnato nel margine sinistro.
        float firstLineOffset = para.IsListItem
            ? indentLeft
            : Math.Clamp(indentLeft + para.FirstLineIndentPt, 0f, Math.Max(0f, containerWidth - indentRight - 50f));
        float contLineOffset = indentLeft;

        LaidOutLine CreateLine(int lineIndex)
        {
            float startOff = lineIndex == 0 ? firstLineOffset : contLineOffset;
            float avail = Math.Max(40f, containerWidth - startOff - indentRight);
            return new LaidOutLine
            {
                StartXOffset = startOff,
                BoxLeftOffset = indentLeft,
                AvailableWidth = avail,
                MaxAscent = defaultFontSize * 0.85f,
                MaxDescent = defaultFontSize * 0.25f
            };
        }

        void FinalizeLine(LaidOutLine line, bool hardBreak)
        {
            // Rimuovi gli spazi bianchi in coda sulle righe andate a capo automaticamente.
            while (!hardBreak && line.Fragments.Count > 0 && line.Fragments[^1].IsWhitespace && !line.Fragments[^1].IsTab)
                line.Fragments.RemoveAt(line.Fragments.Count - 1);

            line.ContentWidth = line.Fragments.Sum(f => f.Width);
            line.EndedWithHardBreak = hardBreak;
            float naturalH = Math.Max(defaultFontSize * 1.15f, line.MaxAscent + line.MaxDescent);
            line.Height = Math.Max(naturalH * Math.Clamp(para.LineSpacingMultiplier, 0.9f, 3.0f), naturalH + 1.5f);
            lines.Add(line);
        }

        if (para.Runs.Count == 0 || para.Runs.All(r => !r.IsLineBreak && !r.IsTab && string.IsNullOrEmpty(r.Text)))
        {
            var blank = CreateLine(0);
            blank.Height = Math.Max(defaultFontSize * 1.15f, 11f);
            lines.Add(blank);
            return lines;
        }

        var current = CreateLine(0);
        float currentWidth = 0f;

        foreach (var run in para.Runs)
        {
            if (run.IsPageBreak) continue;
            if (run.IsLineBreak)
            {
                FinalizeLine(current, hardBreak: true);
                current = CreateLine(lines.Count);
                currentWidth = 0f;
                continue;
            }

            float effSize = run.VerticalAlign == RichVerticalAlign.Baseline
                ? Math.Clamp(run.FontSizePt, 5f, 96f)
                : Math.Clamp(run.FontSizePt * 0.68f, 4.5f, 64f);
            var primaryTf = fonts.GetTypeface(string.IsNullOrWhiteSpace(run.FontFamily) ? defaultFontFamily : run.FontFamily, run.Bold, run.Italic);

            if (run.IsTab)
            {
                const float tabStop = 36f;
                float nextStop = ((float)Math.Floor(currentWidth / tabStop) + 1f) * tabStop;
                float tabW = Math.Max(12f, nextStop - currentWidth);
                if (currentWidth + tabW > current.AvailableWidth && current.Fragments.Count > 0)
                {
                    FinalizeLine(current, hardBreak: false);
                    current = CreateLine(lines.Count);
                    currentWidth = 0f;
                    tabW = tabStop;
                }
                current.Fragments.Add(new PlacedFragment
                {
                    Text = "",
                    Style = run,
                    Typeface = primaryTf,
                    EffectiveFontSize = effSize,
                    Width = tabW,
                    Ascent = effSize * 0.85f,
                    Descent = effSize * 0.25f,
                    IsWhitespace = true,
                    IsTab = true
                });
                currentWidth += tabW;
                continue;
            }

            if (string.IsNullOrEmpty(run.Text)) continue;

            foreach (var (token, isWs) in TokenizeInlineText(run.Text))
            {
                foreach (var (piece, pieceTf) in SplitByGlyphAvailability(token, primaryTf, run.Bold, run.Italic, effSize, fonts))
                {
                    using var font = CreatePdfFont(pieceTf, effSize);
                    float w = font.MeasureText(piece);
                    float asc = Math.Max(effSize * 0.72f, Math.Abs(font.Metrics.Ascent));
                    float desc = Math.Max(effSize * 0.20f, Math.Abs(font.Metrics.Descent));

                    if (isWs)
                    {
                        // Non iniziare una nuova riga con uno spazio residuo di a-capo automatico.
                        if (current.Fragments.Count == 0 && lines.Count > 0 && !lines[^1].EndedWithHardBreak)
                            continue;
                        if (currentWidth + w > current.AvailableWidth && current.Fragments.Count > 0)
                        {
                            FinalizeLine(current, hardBreak: false);
                            current = CreateLine(lines.Count);
                            currentWidth = 0f;
                            continue;
                        }
                        current.Fragments.Add(new PlacedFragment
                        {
                            Text = piece,
                            Style = run,
                            Typeface = pieceTf,
                            EffectiveFontSize = effSize,
                            Width = w,
                            Ascent = asc,
                            Descent = desc,
                            IsWhitespace = true
                        });
                        currentWidth += w;
                        current.MaxAscent = Math.Max(current.MaxAscent, asc);
                        current.MaxDescent = Math.Max(current.MaxDescent, desc);
                        continue;
                    }

                    // Parola non-whitespace: se non entra sulla riga corrente, vai a capo.
                    if (currentWidth + w > current.AvailableWidth && current.Fragments.Count > 0)
                    {
                        FinalizeLine(current, hardBreak: false);
                        current = CreateLine(lines.Count);
                        currentWidth = 0f;
                    }

                    // Se una singola parola supera l'intera larghezza riga, spezzala in modo sicuro sui confini di carattere.
                    if (w > current.AvailableWidth)
                    {
                        int pos = 0;
                        while (pos < piece.Length)
                        {
                            int fit = MeasureFittingPrefix(piece, pos, current.AvailableWidth - currentWidth, font);
                            if (fit == 0)
                            {
                                if (current.Fragments.Count > 0)
                                {
                                    FinalizeLine(current, hardBreak: false);
                                    current = CreateLine(lines.Count);
                                    currentWidth = 0f;
                                    continue;
                                }
                                fit = char.IsHighSurrogate(piece[pos]) && pos + 1 < piece.Length ? 2 : 1;
                            }
                            string chunk = piece.Substring(pos, fit);
                            float cw = font.MeasureText(chunk);
                            current.Fragments.Add(new PlacedFragment
                            {
                                Text = chunk,
                                Style = run,
                                Typeface = pieceTf,
                                EffectiveFontSize = effSize,
                                Width = cw,
                                Ascent = asc,
                                Descent = desc
                            });
                            currentWidth += cw;
                            current.MaxAscent = Math.Max(current.MaxAscent, asc);
                            current.MaxDescent = Math.Max(current.MaxDescent, desc);
                            pos += fit;
                            if (pos < piece.Length)
                            {
                                FinalizeLine(current, hardBreak: false);
                                current = CreateLine(lines.Count);
                                currentWidth = 0f;
                            }
                        }
                    }
                    else
                    {
                        current.Fragments.Add(new PlacedFragment
                        {
                            Text = piece,
                            Style = run,
                            Typeface = pieceTf,
                            EffectiveFontSize = effSize,
                            Width = w,
                            Ascent = asc,
                            Descent = desc
                        });
                        currentWidth += w;
                        current.MaxAscent = Math.Max(current.MaxAscent, asc);
                        current.MaxDescent = Math.Max(current.MaxDescent, desc);
                    }
                }
            }
        }

        if (current.Fragments.Count > 0 || lines.Count == 0)
            FinalizeLine(current, hardBreak: true);

        return lines;
    }

    private static void DrawLaidOutLine(
        SKCanvas canvas,
        LaidOutLine line,
        RichParagraph para,
        float originX,
        float topY,
        bool isFirstLine,
        bool isLastLine,
        PdfFontCache fonts,
        SKPaint paint,
        SKPaint strokePaint)
    {
        float baseX = originX + line.StartXOffset;
        float baselineY = topY + line.MaxAscent;

        // Disegna il marker elenco nel margine sinistro della prima riga.
        if (isFirstLine && para.IsListItem && !string.IsNullOrEmpty(para.ListMarker))
        {
            var refStyle = line.Fragments.FirstOrDefault(f => !f.IsWhitespace)?.Style;
            float mSize = refStyle?.FontSizePt ?? 11f;
            bool mBold = refStyle?.Bold ?? false;
            var mPrimaryTf = fonts.GetTypeface(refStyle?.FontFamily ?? "Calibri", mBold, false);
            var markerPieces = SplitByGlyphAvailability(para.ListMarker, mPrimaryTf, mBold, false, mSize, fonts);
            float markerTotalW = 0f;
            foreach (var (txt, tf) in markerPieces)
            {
                using var mf = CreatePdfFont(tf, mSize);
                markerTotalW += mf.MeasureText(txt);
            }

            float mx = Math.Max(originX, baseX - Math.Max(14f, markerTotalW + 6f));
            paint.Style = SKPaintStyle.Fill;
            paint.Color = TryParseHexColor(refStyle?.TextColorHex, out var mc) ? mc : SKColors.Black;
            foreach (var (txt, tf) in markerPieces)
            {
                using var mf = CreatePdfFont(tf, mSize);
                canvas.DrawText(txt, mx, baselineY, SKTextAlign.Left, mf, paint);
                mx += mf.MeasureText(txt);
            }
        }

        float slack = Math.Max(0f, line.AvailableWidth - line.ContentWidth);
        float startX = para.Alignment switch
        {
            RichAlignment.Center => baseX + slack / 2f,
            RichAlignment.Right => baseX + slack,
            _ => baseX
        };

        float justifyExtraPerSpace = 0f;
        if (para.Alignment == RichAlignment.Justify && !isLastLine && !line.EndedWithHardBreak &&
            line.ContentWidth >= line.AvailableWidth * 0.55f && slack > 0.5f)
        {
            int gaps = line.Fragments.Count(f => f.IsWhitespace && !f.IsTab);
            if (gaps > 0)
                justifyExtraPerSpace = slack / gaps;
        }

        float x = startX;
        foreach (var frag in line.Fragments)
        {
            float advance = frag.Width + (frag.IsWhitespace && !frag.IsTab ? justifyExtraPerSpace : 0f);

            if (frag.Style.HighlightColorHex is { } hlHex && TryParseHexColor(hlHex, out var hlColor))
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = hlColor;
                canvas.DrawRect(x, baselineY - frag.Ascent, advance, frag.Ascent + frag.Descent, paint);
            }

            if (!frag.IsTab && frag.Text.Length > 0)
            {
                float drawY = frag.Style.VerticalAlign switch
                {
                    RichVerticalAlign.Superscript => baselineY - frag.EffectiveFontSize * 0.38f,
                    RichVerticalAlign.Subscript => baselineY + frag.EffectiveFontSize * 0.24f,
                    _ => baselineY
                };

                using var font = CreatePdfFont(frag.Typeface, frag.EffectiveFontSize);
                paint.Style = SKPaintStyle.Fill;
                paint.Color = TryParseHexColor(frag.Style.TextColorHex, out var tc) ? tc : SKColors.Black;
                canvas.DrawText(frag.Text, x, drawY, SKTextAlign.Left, font, paint);

                if (frag.Style.Underline)
                {
                    strokePaint.Color = paint.Color;
                    strokePaint.StrokeWidth = Math.Max(0.7f, frag.EffectiveFontSize * 0.065f);
                    float uy = drawY + Math.Max(1.2f, frag.EffectiveFontSize * 0.12f);
                    canvas.DrawLine(x, uy, x + advance, uy, strokePaint);
                }
                if (frag.Style.Strike)
                {
                    strokePaint.Color = paint.Color;
                    strokePaint.StrokeWidth = Math.Max(0.7f, frag.EffectiveFontSize * 0.065f);
                    float sy = drawY - frag.EffectiveFontSize * 0.28f;
                    canvas.DrawLine(x, sy, x + advance, sy, strokePaint);
                }
            }

            x += advance;
        }
    }

    private static void RenderTable(
        RichTable table,
        RichDocument doc,
        float marginL,
        float contentW,
        float contentBottom,
        PdfFontCache fonts,
        SKPaint paint,
        SKPaint strokePaint,
        Func<SKCanvas> getCanvas,
        Func<float> getCursorY,
        Action<float> setCursorY,
        Func<bool> getPageHasContent,
        Action<bool> setPageHasContent,
        Action startNewPage)
    {
        const float padX = 6f;
        const float padY = 4f;

        if (getPageHasContent())
            setCursorY(getCursorY() + 4f);

        RichTableRow? headerRow = table.Rows.Count > 1 && table.Rows[0].IsHeader ? table.Rows[0] : null;

        void DrawSingleRow(RichTableRow row)
        {
            float[] colWidths = ComputePdfTableRowWidths(table, row, contentW);
            var cellLayouts = new List<List<(RichParagraph Para, List<LaidOutLine> Lines)>>(row.Cells.Count);
            float maxCellH = 0f;

            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                float innerW = Math.Max(24f, colWidths[c] - 2 * padX);
                var paraLayouts = new List<(RichParagraph, List<LaidOutLine>)>(cell.Paragraphs.Count);
                float cellH = 0f;
                for (int pi = 0; pi < cell.Paragraphs.Count; pi++)
                {
                    var cp = cell.Paragraphs[pi];
                    var lines = LayoutParagraph(cp, innerW, doc.DefaultFontFamily, doc.DefaultFontSizePt, fonts);
                    paraLayouts.Add((cp, lines));
                    cellH += lines.Sum(l => l.Height);
                    if (pi < cell.Paragraphs.Count - 1) cellH += cp.SpaceAfterPt;
                }
                cellLayouts.Add(paraLayouts);
                maxCellH = Math.Max(maxCellH, cellH);
            }

            float rowH = Math.Max(18f, maxCellH + 2 * padY);
            if (getPageHasContent() && getCursorY() + rowH > contentBottom)
            {
                startNewPage();
                if (headerRow != null && !ReferenceEquals(row, headerRow))
                    DrawSingleRow(headerRow);
            }

            SKCanvas canvas = getCanvas();
            float rowTop = getCursorY();
            float x = marginL;
            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                float cw = colWidths[c];
                var rect = new SKRect(x, rowTop, x + cw, rowTop + rowH);

                if (cell.ShadingColorHex is { } shHex && TryParseHexColor(shHex, out var fillCol))
                {
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = fillCol;
                    canvas.DrawRect(rect, paint);
                }
                else if (row.IsHeader)
                {
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = new SKColor(0xF3, 0xF4, 0xF6);
                    canvas.DrawRect(rect, paint);
                }

                strokePaint.Color = new SKColor(0xC4, 0xC9, 0xD4);
                strokePaint.StrokeWidth = 0.6f;
                canvas.DrawRect(rect, strokePaint);

                canvas.Save();
                canvas.ClipRect(new SKRect(x + 1f, rowTop + 1f, x + cw - 1f, rowTop + rowH - 1f));
                float cy = rowTop + padY;
                var paraLayouts = cellLayouts[c];
                for (int pi = 0; pi < paraLayouts.Count; pi++)
                {
                    var (cp, lines) = paraLayouts[pi];
                    for (int li = 0; li < lines.Count; li++)
                    {
                        DrawLaidOutLine(canvas, lines[li], cp, x + padX, cy, li == 0, li == lines.Count - 1, fonts, paint, strokePaint);
                        cy += lines[li].Height;
                    }
                    if (pi < paraLayouts.Count - 1) cy += cp.SpaceAfterPt;
                }
                canvas.Restore();

                x += cw;
            }

            setCursorY(rowTop + rowH);
            setPageHasContent(true);
        }

        foreach (var row in table.Rows)
            DrawSingleRow(row);

        setCursorY(getCursorY() + 8f);
    }

    private static float[] ComputePdfTableRowWidths(RichTable table, RichTableRow row, float totalContentW)
    {
        int totalUnits = Math.Max(1, row.Cells.Sum(c => Math.Max(1, c.GridSpan)));
        float[] widths = new float[row.Cells.Count];
        bool hasExplicit = false;
        int gridCol = 0;
        for (int i = 0; i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i];
            int span = Math.Max(1, cell.GridSpan);
            float w = 0f;
            if (table.ColumnWidthTwips.Count >= gridCol + span)
            {
                for (int s = 0; s < span; s++) w += table.ColumnWidthTwips[gridCol + s];
                hasExplicit = true;
            }
            else if (cell.WidthTwips > 0f)
            {
                w = cell.WidthTwips;
                hasExplicit = true;
            }
            widths[i] = w;
            gridCol += span;
        }

        if (!hasExplicit || widths.Sum() <= 0f)
        {
            for (int i = 0; i < row.Cells.Count; i++)
                widths[i] = totalContentW * Math.Max(1, row.Cells[i].GridSpan) / totalUnits;
            return widths;
        }

        float sum = widths.Sum();
        float scale = totalContentW / sum;
        for (int i = 0; i < widths.Length; i++)
            widths[i] = Math.Max(28f, widths[i] > 0f ? widths[i] * scale : totalContentW / row.Cells.Count);

        // Normalizza se i minimi hanno fatto sforare la larghezza disponibile.
        float adjustedSum = widths.Sum();
        if (adjustedSum > totalContentW + 0.5f)
        {
            float norm = totalContentW / adjustedSum;
            for (int i = 0; i < widths.Length; i++) widths[i] *= norm;
        }
        return widths;
    }

    private static SKFont CreatePdfFont(SKTypeface typeface, float sizePt) => new(typeface, sizePt)
    {
        Subpixel = true,
        Edging = SKFontEdging.Antialias
    };

    private static List<(string Token, bool IsWhitespace)> TokenizeInlineText(string text)
    {
        var tokens = new List<(string, bool)>();
        int i = 0;
        while (i < text.Length)
        {
            bool ws = char.IsWhiteSpace(text[i]);
            int start = i;
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i]) == ws) i++;
            tokens.Add((text.Substring(start, i - start), ws));
        }
        return tokens;
    }

    private static List<(string Text, SKTypeface Typeface)> SplitByGlyphAvailability(
        string text, SKTypeface primary, bool bold, bool italic, float sizePt, PdfFontCache fonts)
    {
        using var primaryFont = CreatePdfFont(primary, sizePt);
        bool allSupported = true;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.ConvertToUtf32(text, i);
            if (char.IsHighSurrogate(text[i])) i++;
            if (cp > 0x20 && !primaryFont.ContainsGlyph(cp))
            {
                allSupported = false;
                break;
            }
        }
        if (allSupported) return [(text, primary)];

        var parts = new List<(string, SKTypeface)>();
        var current = new StringBuilder();
        SKTypeface currentTf = primary;

        for (int i = 0; i < text.Length; i++)
        {
            int len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            int cp = len == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            SKTypeface tf = (cp <= 0x20 || primaryFont.ContainsGlyph(cp))
                ? primary
                : fonts.GetFallbackForCodePoint(cp, bold, italic, primary);

            if (!ReferenceEquals(tf, currentTf) && current.Length > 0)
            {
                parts.Add((current.ToString(), currentTf));
                current.Clear();
            }
            currentTf = tf;
            current.Append(text, i, len);
            if (len == 2) i++;
        }
        if (current.Length > 0)
            parts.Add((current.ToString(), currentTf));
        return parts;
    }

    private static int MeasureFittingPrefix(string text, int startIndex, float maxWidth, SKFont font)
    {
        int remaining = text.Length - startIndex;
        if (remaining <= 0 || maxWidth <= 0f) return 0;

        int lo = 0, hi = remaining;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            int adj = mid;
            if (adj > 0 && char.IsHighSurrogate(text[startIndex + adj - 1]) && startIndex + adj < text.Length)
                adj--;
            if (adj <= lo)
            {
                adj = mid + 1;
                if (adj > hi) break;
            }
            float w = font.MeasureText(text.AsSpan(startIndex, adj));
            if (w <= maxWidth) lo = adj;
            else hi = mid - 1;
        }
        if (lo > 0 && char.IsHighSurrogate(text[startIndex + lo - 1]) && startIndex + lo < text.Length)
            lo--;
        return lo;
    }

    private static bool TryParseHexColor(string? hex, out SKColor color)
    {
        color = SKColors.Black;
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 6) return false;
        if (byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out byte b))
        {
            color = new SKColor(r, g, b);
            return true;
        }
        return false;
    }

    // ── PDF -> PDF/A-2b (ISO 19005-2) ─────────────────────────────────────────

    private static readonly byte[] CachedSrgbIccProfile = BuildValidSrgbIccProfile();

    internal static byte[] GetSrgbIccProfileBytes() => (byte[])CachedSrgbIccProfile.Clone();

    internal static void ConvertToPdfA2b(string srcPdfPath, string dstPdfPath, bool stripMetadata = false)
    {
        byte[] srcBytes = File.ReadAllBytes(srcPdfPath);
        if (srcBytes.Length < 32 || !srcBytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            throw new IOException("Il file sorgente non è un documento PDF valido.");

        int pageCount;
        using (var checkDoc = UglyToad.PdfPig.PdfDocument.Open(srcBytes))
        {
            pageCount = checkDoc.NumberOfPages;
            if (pageCount <= 0)
                throw new IOException("Il documento PDF non contiene pagine.");
        }

        string initialLatin1 = Encoding.Latin1.GetString(srcBytes);
        if (!HasClassicXrefTable(initialLatin1))
        {
            // PDF 1.5+ con Cross-Reference Stream / Object Stream: normalizza prima in un PDF classico multi-pagina
            // preservando le dimensioni pagina reali così l'aggiornamento incrementale PDF/A-2b resta valido al 100%.
            string normalizedTmp = dstPdfPath + ".norm.tmp.pdf";
            try
            {
                RebuildAsClassicPdf(srcBytes, normalizedTmp);
                ConvertToPdfA2b(normalizedTmp, dstPdfPath, stripMetadata);
                return;
            }
            finally
            {
                try { if (File.Exists(normalizedTmp)) File.Delete(normalizedTmp); } catch { }
            }
        }

        // In-place upgrade "%PDF-1.x" -> "%PDF-1.7" (8 byte identici: mantiene invariati gli offset xref esistenti).
        byte[] working = (byte[])srcBytes.Clone();
        if (working.Length >= 8 && working[0] == (byte)'%' && working[1] == (byte)'P' &&
            working[2] == (byte)'D' && working[3] == (byte)'F' && working[4] == (byte)'-')
        {
            working[5] = (byte)'1';
            working[6] = (byte)'.';
            working[7] = (byte)'7';
        }

        string latin1 = Encoding.Latin1.GetString(working);

        // Estrai ultimo startxref e dizionario trailer reale (evitando match spuri dentro stream o stringhe).
        int startXrefIdx = latin1.LastIndexOf("startxref", StringComparison.Ordinal);
        long prevXrefOffset = 0;
        if (startXrefIdx >= 0)
        {
            var mXref = System.Text.RegularExpressions.Regex.Match(latin1[startXrefIdx..], @"startxref\s+(\d+)");
            if (mXref.Success) long.TryParse(mXref.Groups[1].Value, out prevXrefOffset);
        }

        string trailerDict = ExtractLastTrailerDictionary(latin1, startXrefIdx) ?? latin1;

        int rootObjNum = 1;
        var rootMatches = System.Text.RegularExpressions.Regex.Matches(trailerDict, @"/Root\s+(\d+)\s+0\s+R");
        if (rootMatches.Count > 0)
            int.TryParse(rootMatches[^1].Groups[1].Value, out rootObjNum);
        else
        {
            var fallbackRoot = System.Text.RegularExpressions.Regex.Matches(latin1, @"/Root\s+(\d+)\s+0\s+R");
            if (fallbackRoot.Count > 0)
                int.TryParse(fallbackRoot[^1].Groups[1].Value, out rootObjNum);
        }

        int maxObjNum = rootObjNum;
        var sizeMatches = System.Text.RegularExpressions.Regex.Matches(trailerDict, @"/Size\s+(\d+)");
        if (sizeMatches.Count > 0 && int.TryParse(sizeMatches[^1].Groups[1].Value, out int parsedSize))
            maxObjNum = Math.Max(maxObjNum, parsedSize - 1);

        foreach (System.Text.RegularExpressions.Match mObj in System.Text.RegularExpressions.Regex.Matches(latin1, @"(?:^|\r|\n)(\d+)\s+0\s+obj\b"))
        {
            if (int.TryParse(mObj.Groups[1].Value, out int n) && n > maxObjNum)
                maxObjNum = n;
        }

        string docIdHex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(working))[..32].ToLowerInvariant();
        string firstIdHex = docIdHex;
        var idMatch = System.Text.RegularExpressions.Regex.Match(trailerDict, @"/ID\s*\[\s*<([0-9A-Fa-f]+)>");
        if (idMatch.Success && idMatch.Groups[1].Value.Length >= 8)
            firstIdHex = idMatch.Groups[1].Value.ToLowerInvariant();

        // Trova il dizionario del Catalog (/Root) attivo (ultimo oggetto con quel numero, bilanciato e string-aware)
        string catalogInner = ExtractCatalogDictionaryInner(latin1, rootObjNum);

        int metaObjNum = maxObjNum + 1;
        int iccObjNum = maxObjNum + 2;
        int intentObjNum = maxObjNum + 3;
        int? infoNewObjNum = stripMetadata ? null : maxObjNum + 4;
        int newSize = (infoNewObjNum ?? intentObjNum) + 1;

        var utcNow = DateTimeOffset.UtcNow;
        string nowIso = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        string nowPdfDate = utcNow.ToString("'D:'yyyyMMddHHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        byte[] xmpBytes = BuildPdfA2bXmpPacket(nowIso, stripMetadata);
        byte[] iccBytes = CachedSrgbIccProfile;

        // Rimuovi eventuali /Metadata o /OutputIntents preesistenti dal Catalog con parsing bilanciato
        catalogInner = RemovePdfDictionaryEntry(catalogInner, "/Metadata");
        catalogInner = RemovePdfDictionaryEntry(catalogInner, "/OutputIntents");
        string updatedCatalog = $"<< {catalogInner.Trim()} /Metadata {metaObjNum} 0 R /OutputIntents [ {intentObjNum} 0 R ] >>";

        using var ms = new MemoryStream(working.Length + xmpBytes.Length + iccBytes.Length + 2048);
        ms.Write(working, 0, working.Length);
        if (working[^1] != (byte)'\n' && working[^1] != (byte)'\r')
            ms.WriteByte((byte)'\n');

        static void WriteAscii(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));

        long metaOffset = ms.Position;
        WriteAscii(ms, $"{metaObjNum} 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmpBytes.Length} >>\nstream\n");
        ms.Write(xmpBytes, 0, xmpBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        long iccOffset = ms.Position;
        WriteAscii(ms, $"{iccObjNum} 0 obj\n<< /N 3 /Length {iccBytes.Length} >>\nstream\n");
        ms.Write(iccBytes, 0, iccBytes.Length);
        WriteAscii(ms, "\nendstream\nendobj\n");

        long intentOffset = ms.Position;
        WriteAscii(ms,
            $"{intentObjNum} 0 obj\n" +
            $"<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB IEC61966-2.1) " +
            $"/OutputCondition (sRGB IEC61966-2.1) /RegistryName (http://www.color.org) " +
            $"/Info (sRGB IEC61966-2.1) /DestOutputProfile {iccObjNum} 0 R >>\n" +
            $"endobj\n");

        long infoOffset = 0;
        if (infoNewObjNum.HasValue)
        {
            infoOffset = ms.Position;
            WriteAscii(ms,
                $"{infoNewObjNum.Value} 0 obj\n" +
                $"<< /Creator (FLIP PDF/A-2b Engine) /Producer (FLIP ISO 19005-2 Archival Engine) " +
                $"/CreationDate ({nowPdfDate}) /ModDate ({nowPdfDate}) >>\n" +
                $"endobj\n");
        }

        long rootOffset = ms.Position;
        WriteAscii(ms, $"{rootObjNum} 0 obj\n{updatedCatalog}\nendobj\n");

        long newXrefOffset = ms.Position;
        int addedCount = infoNewObjNum.HasValue ? 4 : 3;
        var xref = new StringBuilder();
        xref.Append("xref\n");
        // Sottosezioni incrementali ordinate per numero oggetto (ogni riga esattamente 20 byte: "0000000000 00000 n \n")
        xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rootObjNum} 1\n{rootOffset:D10} 00000 n \n"));
        xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{metaObjNum} {addedCount}\n"));
        xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{metaOffset:D10} 00000 n \n"));
        xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{iccOffset:D10} 00000 n \n"));
        xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{intentOffset:D10} 00000 n \n"));
        if (infoNewObjNum.HasValue)
            xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{infoOffset:D10} 00000 n \n"));

        xref.Append("trailer\n<< ");
        xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/Size {newSize} /Root {rootObjNum} 0 R "));
        if (infoNewObjNum.HasValue)
            xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/Info {infoNewObjNum.Value} 0 R "));
        if (prevXrefOffset > 0)
            xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/Prev {prevXrefOffset} "));
        xref.Append($"/ID [<{firstIdHex}> <{docIdHex}>] >>\n");
        xref.Append(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"startxref\n{newXrefOffset}\n%%EOF\n"));

        WriteAscii(ms, xref.ToString());
        File.WriteAllBytes(dstPdfPath, ms.ToArray());
    }

    private static bool HasClassicXrefTable(string latin1)
    {
        int startXrefIdx = latin1.LastIndexOf("startxref", StringComparison.Ordinal);
        if (startXrefIdx < 0) return false;
        var mXref = System.Text.RegularExpressions.Regex.Match(latin1[startXrefIdx..], @"startxref\s+(\d+)");
        if (!mXref.Success || !long.TryParse(mXref.Groups[1].Value, out long offset)) return false;
        if (offset < 0 || offset + 4 > latin1.Length) return false;
        return latin1.AsSpan((int)offset).TrimStart().StartsWith("xref", StringComparison.Ordinal);
    }

    private static string? ExtractLastTrailerDictionary(string latin1, int beforeIndex)
    {
        int searchEnd = beforeIndex > 0 ? beforeIndex : latin1.Length;
        int trailerIdx = latin1.LastIndexOf("trailer", searchEnd, StringComparison.Ordinal);
        if (trailerIdx < 0) return null;
        int openIdx = latin1.IndexOf("<<", trailerIdx, StringComparison.Ordinal);
        if (openIdx < 0 || openIdx >= searchEnd) return null;
        return ReadBalancedDictionaryInner(latin1, openIdx + 2);
    }

    private static void RebuildAsClassicPdf(byte[] srcBytes, string dstPdfPath)
    {
        const float renderDpi = 200f;
        using var stream = new FileStream(dstPdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var doc = SKDocument.CreatePdf(stream, EmptyPdfMetadata()) ?? throw new IOException("Encoder PDF non disponibile.");
        foreach (var pageBmp in PDFtoImage.Conversion.ToImages(srcBytes, options: new PDFtoImage.RenderOptions(Dpi: (int)renderDpi, WithAspectRatio: true)))
        {
            using (pageBmp)
            {
                float pageW = pageBmp.Width * 72f / renderDpi;
                float pageH = pageBmp.Height * 72f / renderDpi;
                var canvas = doc.BeginPage(pageW, pageH);
                canvas.Clear(SKColors.White);
                using var img = SKImage.FromBitmap(pageBmp);
                canvas.DrawImage(img, new SKRect(0, 0, pageW, pageH), new SKSamplingOptions(SKFilterMode.Linear));
                doc.EndPage();
            }
        }
        doc.Close();
        stream.Flush(true);
    }

    private static string ExtractCatalogDictionaryInner(string latin1, int rootObjNum)
    {
        var objMatches = System.Text.RegularExpressions.Regex.Matches(
            latin1,
            $@"(?:^|\r|\n){rootObjNum}\s+0\s+obj\s*<<");
        for (int m = objMatches.Count - 1; m >= 0; m--)
        {
            int dictStart = objMatches[m].Index + objMatches[m].Length;
            string? balanced = ReadBalancedDictionaryInner(latin1, dictStart);
            if (balanced != null) return balanced;
        }

        var fallbacks = System.Text.RegularExpressions.Regex.Matches(latin1, @"<<\s*/Type\s*/Catalog\b");
        for (int m = fallbacks.Count - 1; m >= 0; m--)
        {
            int dictStart = fallbacks[m].Index + 2;
            string? balanced = ReadBalancedDictionaryInner(latin1, dictStart);
            if (balanced != null) return balanced;
        }

        return "/Type /Catalog /Pages 2 0 R";
    }

    private static string? ReadBalancedDictionaryInner(string text, int startAfterOpeningAngles)
    {
        int depth = 1;
        int parenDepth = 0;
        for (int i = startAfterOpeningAngles; i < text.Length; i++)
        {
            char c = text[i];
            if (parenDepth > 0)
            {
                if (c == '\\' && i + 1 < text.Length) { i++; continue; }
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                continue;
            }

            if (c == '(')
            {
                parenDepth = 1;
            }
            else if (c == '<' && i + 1 < text.Length && text[i + 1] == '<')
            {
                depth++;
                i++;
            }
            else if (c == '>' && i + 1 < text.Length && text[i + 1] == '>')
            {
                depth--;
                if (depth == 0)
                    return text[startAfterOpeningAngles..i];
                i++;
            }
        }
        return null;
    }

    private static string RemovePdfDictionaryEntry(string dictInner, string key)
    {
        int searchFrom = 0;
        while (searchFrom < dictInner.Length)
        {
            int idx = dictInner.IndexOf(key, searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;

            int afterKey = idx + key.Length;
            if (afterKey < dictInner.Length && !char.IsWhiteSpace(dictInner[afterKey]) &&
                dictInner[afterKey] is not ('[' or '<' or '(' or '/'))
            {
                searchFrom = afterKey;
                continue;
            }

            int pos = afterKey;
            while (pos < dictInner.Length && char.IsWhiteSpace(dictInner[pos])) pos++;
            if (pos >= dictInner.Length) break;

            int endPos = pos;
            if (dictInner[pos] == '[')
            {
                int bracketDepth = 1;
                int parenDepth = 0;
                endPos = pos + 1;
                while (endPos < dictInner.Length && bracketDepth > 0)
                {
                    char c = dictInner[endPos];
                    if (parenDepth > 0)
                    {
                        if (c == '\\' && endPos + 1 < dictInner.Length) endPos++;
                        else if (c == '(') parenDepth++;
                        else if (c == ')') parenDepth--;
                    }
                    else if (c == '(') parenDepth = 1;
                    else if (c == '[') bracketDepth++;
                    else if (c == ']') bracketDepth--;
                    endPos++;
                }
            }
            else if (dictInner[pos] == '<' && pos + 1 < dictInner.Length && dictInner[pos + 1] == '<')
            {
                string? inner = ReadBalancedDictionaryInner(dictInner, pos + 2);
                endPos = inner != null ? pos + 2 + inner.Length + 2 : pos + 2;
            }
            else
            {
                // Indirect reference "N 0 R" o singolo token
                var refMatch = System.Text.RegularExpressions.Regex.Match(dictInner[pos..], @"^\d+\s+\d+\s+R\b");
                if (refMatch.Success)
                {
                    endPos = pos + refMatch.Length;
                }
                else
                {
                    while (endPos < dictInner.Length && !char.IsWhiteSpace(dictInner[endPos]) && dictInner[endPos] != '/')
                        endPos++;
                }
            }

            dictInner = dictInner[..idx] + " " + dictInner[endPos..];
            searchFrom = idx;
        }
        return dictInner;
    }

    private static byte[] BuildPdfA2bXmpPacket(string isoTimestamp, bool stripMetadata)
    {
        string creatorProducer = stripMetadata
            ? ""
            : "      <xmp:CreatorTool>FLIP PDF/A-2b Engine</xmp:CreatorTool>\n" +
              "      <pdf:Producer>FLIP ISO 19005-2 Archival Engine</pdf:Producer>\n";

        string xml =
            "<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
            "  <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
            "    <rdf:Description rdf:about=\"\"\n" +
            "        xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"\n" +
            "        xmlns:dc=\"http://purl.org/dc/elements/1.1/\"\n" +
            "        xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"\n" +
            "        xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n" +
            "      <pdfaid:part>2</pdfaid:part>\n" +
            "      <pdfaid:conformance>B</pdfaid:conformance>\n" +
            "      <dc:format>application/pdf</dc:format>\n" +
            $"      <xmp:CreateDate>{isoTimestamp}</xmp:CreateDate>\n" +
            $"      <xmp:ModifyDate>{isoTimestamp}</xmp:ModifyDate>\n" +
            creatorProducer +
            "    </rdf:Description>\n" +
            "  </rdf:RDF>\n" +
            "</x:xmpmeta>\n" +
            "<?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>
    /// Genera un profilo ICC v2.1.0 sRGB IEC61966-2.1 Matrix-TRC completo e conforme a ICC.1:2001-04
    /// (tutti i 10 tag obbligatori/standard: desc, cprt, wtpt, bkpt, rXYZ, gXYZ, bXYZ adattati D50 con
    /// somma esatta sul punto bianco D50 e LUT curv IEC 61966-2.1 a 1024 punti condivisa su rTRC/gTRC/bTRC).
    /// </summary>
    private static byte[] BuildValidSrgbIccProfile()
    {
        static void WriteUInt16BE(Span<byte> buf, int offset, ushort val)
        {
            buf[offset] = (byte)(val >> 8);
            buf[offset + 1] = (byte)val;
        }
        static void WriteUInt32BE(Span<byte> buf, int offset, uint val)
        {
            buf[offset] = (byte)(val >> 24);
            buf[offset + 1] = (byte)(val >> 16);
            buf[offset + 2] = (byte)(val >> 8);
            buf[offset + 3] = (byte)val;
        }
        static void WriteTag4(Span<byte> buf, int offset, string fourCC)
        {
            for (int i = 0; i < 4; i++) buf[offset + i] = (byte)fourCC[i];
        }
        static byte[] BuildXyzTag(uint x, uint y, uint z)
        {
            byte[] tag = new byte[20];
            WriteTag4(tag, 0, "XYZ ");
            WriteUInt32BE(tag, 8, x);
            WriteUInt32BE(tag, 12, y);
            WriteUInt32BE(tag, 16, z);
            return tag;
        }
        static byte[] BuildDescTag(string asciiDescription)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(asciiDescription + "\0");
            // textDescriptionType ICC v2: sig(4) + res(4) + asciiCount(4) + ascii + uniLang(4) + uniCount(4) + scriptCode(2) + scriptCount(1) + scriptData(67)
            int size = 12 + ascii.Length + 4 + 4 + 2 + 1 + 67;
            byte[] tag = new byte[size];
            WriteTag4(tag, 0, "desc");
            WriteUInt32BE(tag, 8, (uint)ascii.Length);
            ascii.CopyTo(tag, 12);
            return tag;
        }
        static byte[] BuildTextTag(string asciiText)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(asciiText + "\0");
            byte[] tag = new byte[8 + ascii.Length];
            WriteTag4(tag, 0, "text");
            ascii.CopyTo(tag, 8);
            return tag;
        }
        static byte[] BuildSrgbCurvTag()
        {
            const int count = 1024;
            byte[] tag = new byte[12 + count * 2];
            WriteTag4(tag, 0, "curv");
            WriteUInt32BE(tag, 8, count);
            for (int i = 0; i < count; i++)
            {
                double x = i / (double)(count - 1);
                double y = x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
                ushort encoded = (ushort)Math.Clamp(Math.Round(y * 65535.0), 0, 65535);
                WriteUInt16BE(tag, 12 + i * 2, encoded);
            }
            return tag;
        }

        byte[] descData = BuildDescTag("sRGB IEC61966-2.1");
        byte[] cprtData = BuildTextTag("Public Domain - sRGB IEC61966-2.1");
        byte[] wtptData = BuildXyzTag(0x0000F6D6, 0x00010000, 0x0000D32D); // D50
        byte[] bkptData = BuildXyzTag(0x00000000, 0x00000000, 0x00000000);
        // Matrice primaria sRGB adattata a D50 (Bradford): rXYZ + gXYZ + bXYZ == D50 esatto al bit
        byte[] rXyzData = BuildXyzTag(0x00006FA0, 0x000038F5, 0x00000390);
        byte[] gXyzData = BuildXyzTag(0x00006297, 0x0000B787, 0x000018D9);
        byte[] bXyzData = BuildXyzTag(0x0000249F, 0x00000F84, 0x0000B6C4);
        byte[] trcData = BuildSrgbCurvTag();

        static int Align4(int n) => (n + 3) & ~3;

        const int headerSize = 128;
        const int tagCount = 10;
        int dataStart = headerSize + 4 + tagCount * 12; // 252 (già multiplo di 4)

        int descOff = dataStart;
        int cprtOff = descOff + Align4(descData.Length);
        int wtptOff = cprtOff + Align4(cprtData.Length);
        int bkptOff = wtptOff + Align4(wtptData.Length);
        int rXyzOff = bkptOff + Align4(bkptData.Length);
        int gXyzOff = rXyzOff + Align4(rXyzData.Length);
        int bXyzOff = gXyzOff + Align4(gXyzData.Length);
        int trcOff = bXyzOff + Align4(bXyzData.Length);
        int totalSize = trcOff + Align4(trcData.Length);

        byte[] profile = new byte[totalSize];
        Span<byte> span = profile;

        // Header ICC v2.1.0 (128 byte)
        WriteUInt32BE(span, 0, (uint)totalSize);
        WriteUInt32BE(span, 8, 0x02100000);  // ICC v2.1.0
        WriteTag4(span, 12, "mntr");         // Display device profile
        WriteTag4(span, 16, "RGB ");         // Data color space
        WriteTag4(span, 20, "XYZ ");         // Profile Connection Space
        // Data creazione valida (2026-01-01 00:00:00 UTC)
        WriteUInt16BE(span, 24, 2026);
        WriteUInt16BE(span, 26, 1);
        WriteUInt16BE(span, 28, 1);
        WriteTag4(span, 36, "acsp");         // Signature 'acsp'
        WriteTag4(span, 40, "MSFT");         // Primary platform
        WriteUInt32BE(span, 64, 0);          // Perceptual rendering intent
        WriteUInt32BE(span, 68, 0x0000F6D6); // D50 X
        WriteUInt32BE(span, 72, 0x00010000); // D50 Y
        WriteUInt32BE(span, 76, 0x0000D32D); // D50 Z

        // Tag table
        (string Sig, int Offset, int Size)[] tags =
        [
            ("desc", descOff, descData.Length),
            ("cprt", cprtOff, cprtData.Length),
            ("wtpt", wtptOff, wtptData.Length),
            ("bkpt", bkptOff, bkptData.Length),
            ("rXYZ", rXyzOff, rXyzData.Length),
            ("gXYZ", gXyzOff, gXyzData.Length),
            ("bXYZ", bXyzOff, bXyzData.Length),
            ("rTRC", trcOff, trcData.Length),
            ("gTRC", trcOff, trcData.Length),
            ("bTRC", trcOff, trcData.Length)
        ];

        WriteUInt32BE(span, headerSize, (uint)tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            int entryOff = headerSize + 4 + i * 12;
            WriteTag4(span, entryOff, tags[i].Sig);
            WriteUInt32BE(span, entryOff + 4, (uint)tags[i].Offset);
            WriteUInt32BE(span, entryOff + 8, (uint)tags[i].Size);
        }

        descData.CopyTo(profile, descOff);
        cprtData.CopyTo(profile, cprtOff);
        wtptData.CopyTo(profile, wtptOff);
        bkptData.CopyTo(profile, bkptOff);
        rXyzData.CopyTo(profile, rXyzOff);
        gXyzData.CopyTo(profile, gXyzOff);
        bXyzData.CopyTo(profile, bXyzOff);
        trcData.CopyTo(profile, trcOff);

        return profile;
    }
}

