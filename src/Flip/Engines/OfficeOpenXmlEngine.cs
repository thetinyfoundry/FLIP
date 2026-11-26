using System.Globalization;
using System.IO;
using System.Text.Json;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

/// <summary>
/// Motore built-in per documenti Office Open XML (DOCX, XLSX, PPTX, ODT).
/// Manipola direttamente i contenitori ZIP e XML senza richiedere Office o LibreOffice.
/// </summary>
public sealed class OfficeOpenXmlEngine : IConversionEngine
{
    public string Id => "openxml_builtin";
    public string DisplayName => "Native Office OpenXML Engine (No Office Required)";
    public bool IsExternal => false;

    private readonly List<ConversionEdge> _supportedEdges = [];

    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace OdtText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace OdtOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace OdtTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const int MaxSpreadsheetColumns = 16_384;
    private static readonly XNamespace Ssml = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Pml = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static readonly string[] DocAndTextFormats = ["txt", "md", "html", "docx", "odt", "rtf"];
    private static readonly string[] SpreadsheetFormats = ["xlsx", "xlsm", "xltx", "xls", "ods"];
    private static readonly string[] TabularFormats = ["csv", "tsv", "json", "xml", "yaml", "yml"];

    public OfficeOpenXmlEngine()
    {
        // Full 1-hop mesh among document & text/markup formats (TXT <-> MD <-> HTML <-> DOCX <-> ODT <-> RTF)
        foreach (var src in DocAndTextFormats)
            foreach (var dst in DocAndTextFormats)
                if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));

        // Direct 1-hop text/markdown -> JSON & CSV
        foreach (var src in new[] { "txt", "md" })
            foreach (var dst in new[] { "json", "csv" })
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 12));

        // XLSX -> tabular & markup formats
        foreach (var dst in new[] { "csv", "tsv", "json", "xml", "yaml", "html", "md" })
            foreach (var src in SpreadsheetFormats)
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));

        // Tabular formats -> XLSX
        foreach (var src in TabularFormats)
            _supportedEdges.Add(new ConversionEdge(src, "xlsx", Id, 12));

        // PPTX -> text
        foreach (var dst in new[] { "txt", "md", "html" })
            _supportedEdges.Add(new ConversionEdge("pptx", dst, Id, 10));
    }

    public bool IsAvailable(FlipSettings settings) => true;

    public Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;

            if (!File.Exists(srcPath))
                return Task.FromResult(new EngineExecutionResult(false, "", $"File non trovato: {srcPath}"));

            string srcExt = (context.SourceExtension ?? Path.GetExtension(srcPath)).TrimStart('.').ToLowerInvariant();
            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();

            string? dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(dstPath)) File.Delete(dstPath);

            context.Progress?.Report(0.10);

            if (srcExt == "docx" && dstExt == "rtf")
                ConvertDocxToRtf(srcPath, dstPath);
            else if (srcExt is "docx" && dstExt is "txt" or "md" or "html")
                ExtractDocx(srcPath, dstPath, dstExt);
            else if (srcExt is "odt" && dstExt is "txt" or "md" or "html")
                ExtractOdt(srcPath, dstPath, dstExt);
            else if (srcExt is "txt" or "md" or "html" && dstExt == "docx")
                GenerateDocx(srcPath, dstPath, srcExt);
            else if (DocAndTextFormats.Contains(srcExt) && DocAndTextFormats.Contains(dstExt))
                ConvertDocOrText(srcPath, srcExt, dstPath, dstExt);
            else if (srcExt is "txt" or "md" && dstExt is "json" or "csv")
                ConvertTextToData(srcPath, dstPath, dstExt);
            else if (SpreadsheetFormats.Contains(srcExt) && dstExt is "csv" or "tsv" or "json" or "xml" or "yaml" or "html" or "md")
                ExtractXlsx(srcPath, dstPath, dstExt);
            else if (TabularFormats.Contains(srcExt) && dstExt == "xlsx")
                GenerateXlsx(srcPath, dstPath, srcExt);
            else if (srcExt == "pptx" && dstExt is "txt" or "md" or "html")
                ExtractPptx(srcPath, dstPath, dstExt);
            else
                return Task.FromResult(new EngineExecutionResult(false, "", $"Conversione OpenXML non supportata: {srcExt} -> {dstExt}"));

            context.Progress?.Report(1.0);
            long bytes = File.Exists(dstPath) ? new FileInfo(dstPath).Length : 0;
            return Task.FromResult(new EngineExecutionResult(true, dstPath, null, bytes));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", "Conversione OpenXML annullata."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", $"Errore OpenXML: {ex.Message}"));
        }
    }

    private static void ExtractDocx(string docxPath, string outputPath, string format)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new IOException("document.xml non trovato nel DOCX.");

        using var stream = entry.Open();
        var doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var body = doc.Root?.Element(doc.Root.Name.Namespace + "body")
            ?? throw new IOException("Struttura DOCX non valida: body non trovato.");
        WriteDocument(outputPath, format, body, word: true, Path.GetFileNameWithoutExtension(docxPath));
    }

    private static void ExtractOdt(string odtPath, string outputPath, string format)
    {
        using var zip = ZipFile.OpenRead(odtPath);
        var entry = zip.GetEntry("content.xml")
            ?? throw new IOException("content.xml non trovato nel file ODT.");

        using var stream = entry.Open();
        var doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var body = doc.Root?.Element(OdtOffice + "body")?.Element(OdtOffice + "text")
            ?? throw new IOException("Struttura ODT non valida: corpo del documento non trovato.");
        WriteDocument(outputPath, format, body, word: false, Path.GetFileNameWithoutExtension(odtPath));
    }

    private static void WriteDocument(string outputPath, string format, XElement body, bool word, string title)
    {
        XNamespace textNamespace = word ? body.Name.Namespace : OdtText;
        XName paragraphName = textNamespace + "p";
        XName headingName = textNamespace + "h";
        XName tableName = word ? textNamespace + "tbl" : OdtTable + "table";
        var output = new StringBuilder();
        if (format == "html")
            output.AppendLine($"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(title)}</title></head><body>");

        AppendBlocks(body);
        if (format == "html") output.AppendLine("</body></html>");
        File.WriteAllText(outputPath, output.ToString(), new UTF8Encoding(false));

        void AppendBlocks(XElement container)
        {
            foreach (var block in container.Elements())
            {
                if (block.Name == tableName)
                {
                    var rows = ReadDocumentTable(block, textNamespace, word);
                    if (format == "html") AppendHtmlTable(output, rows);
                    else if (format == "md") AppendMarkdownTable(output, rows);
                    else
                    {
                        foreach (var row in rows)
                            output.AppendLine(string.Join('\t', row.Select(cell => cell.Replace("\n", " / ").Replace('\t', ' '))));
                        output.AppendLine();
                    }
                }
                else if (block.Name == paragraphName || block.Name == headingName)
                {
                    string text = ReadParagraph(block, textNamespace, word);
                    string style = (string?)block.Element(textNamespace + "pPr")?
                        .Element(textNamespace + "pStyle")?.Attribute(textNamespace + "val") ?? "";
                    bool heading = block.Name == headingName || style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
                        || style.StartsWith("Titolo", StringComparison.OrdinalIgnoreCase);
                    if (format == "html")
                    {
                        string tag = heading ? "h2" : "p";
                        output.AppendLine($"<{tag}>{HtmlText(text)}</{tag}>");
                    }
                    else
                    {
                        output.AppendLine(format == "md" && heading ? $"## {text}" : text);
                        if (format == "md") output.AppendLine();
                    }
                }
                else
                {
                    AppendBlocks(block);
                }
            }
        }
    }

    private static List<List<string>> ReadDocumentTable(XElement table, XNamespace textNamespace, bool word)
    {
        XNamespace tableNamespace = table.Name.Namespace;
        XName rowName = tableNamespace + (word ? "tr" : "table-row");
        XName cellName = tableNamespace + (word ? "tc" : "table-cell");
        var rows = new List<List<string>>();
        long cellCount = 0;
        foreach (var row in table.Descendants(rowName).Where(row => row.Ancestors(table.Name).First() == table))
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements())
            {
                if (cell.Name != cellName && (word || cell.Name != tableNamespace + "covered-table-cell"))
                    continue;
                string text = string.Join('\n', cell.Descendants().Where(element =>
                    element.Name == textNamespace + "p" || (!word && element.Name == textNamespace + "h"))
                    .Select(paragraph => ReadParagraph(paragraph, textNamespace, word)));
                int repeat = word ? 1 : ReadRepeat(cell, tableNamespace + "number-columns-repeated", MaxSpreadsheetColumns);
                if (cells.Count + repeat > MaxSpreadsheetColumns)
                    throw new IOException("La tabella contiene troppe colonne.");
                cells.AddRange(Enumerable.Repeat(text, repeat));
            }
            if (cells.Count == 0) continue;

            int repetitions = word ? 1 : ReadRepeat(row, tableNamespace + "number-rows-repeated", 1_000_000);
            cellCount += (long)cells.Count * repetitions;
            if (cellCount > 1_000_000) throw new IOException("La tabella contiene troppe celle.");
            rows.AddRange(Enumerable.Repeat(cells, repetitions));
        }
        return rows;
    }

    private static int ReadRepeat(XElement element, XName attributeName, int limit)
    {
        string? raw = (string?)element.Attribute(attributeName);
        if (raw is null) return 1;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count < 1 || count > limit)
            throw new IOException($"Valore non valido per {attributeName.LocalName}.");
        return count;
    }

    private static string ReadParagraph(XElement paragraph, XNamespace ns, bool word)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.DescendantNodes())
        {
            if (node is XText run && (!word || run.Parent?.Name == ns + "t"))
                text.Append(run.Value);
            else if (node is XElement element)
            {
                if (element.Name == ns + "tab") text.Append('\t');
                else if (element.Name == ns + (word ? "br" : "line-break") || (word && element.Name == ns + "cr"))
                    text.Append('\n');
                else if (!word && element.Name == ns + "s")
                    text.Append(' ', ReadRepeat(element, ns + "c", 1_000_000));
            }
        }
        return text.ToString();
    }

    private static void AppendHtmlTable(StringBuilder output, List<List<string>> rows)
    {
        if (rows.Count == 0) return;
        output.AppendLine("<table>");
        foreach (var row in rows)
        {
            output.Append("<tr>");
            foreach (string cell in row)
                output.Append("<td>").Append(HtmlText(cell)).Append("</td>");
            output.AppendLine("</tr>");
        }
        output.AppendLine("</table>");
    }

    private static string HtmlText(string text) => System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br>");

    private static void AppendMarkdownTable(StringBuilder output, List<List<string>> rows)
    {
        if (rows.Count == 0) return;
        int columns = rows.Max(row => row.Count);
        output.AppendLine();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var cells = Enumerable.Range(0, columns).Select(i => i < row.Count ? EscapeMarkdownCell(row[i]) : "");
            output.AppendLine("| " + string.Join(" | ", cells) + " |");
            if (r == 0)
                output.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |");
        }
        output.AppendLine();
    }

    private static string EscapeMarkdownCell(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\\", "\\\\").Replace("|", "\\|").Replace("\t", " ").Replace("\n", "<br>");

    // ── Text -> DOCX ──────────────────────────────────────────────────────────

    private static void GenerateDocx(string srcPath, string docxPath, string srcExt)
    {
        string raw = TextFiles.Read(srcPath);
        if (string.IsNullOrWhiteSpace(raw)) throw new IOException("File sorgente vuoto.");

        GenerateDocx(srcExt switch
        {
            "md" => ReadMarkdownBlocks(raw),
            "html" => ReadHtmlBlocks(raw),
            _ => raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => (false, line)).ToList()
        }, docxPath);
    }

    private static void GenerateDocx(List<(bool IsHeading, string Text)> blocks, string docxPath)
    {
        // Genera paragrafi Word
        var paragraphs = new List<XElement>();
        foreach (var (isHeading, text) in blocks)
        {
            var run = new XElement(W + "r");
            if (isHeading)
                run.Add(new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "sz", new XAttribute(W + "val", 32))));
            string[] parts = RemoveInvalidXml(text).Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) run.Add(new XElement(W + "br"));
                string[] columns = parts[i].Split('\t');
                for (int c = 0; c < columns.Length; c++)
                {
                    if (c > 0) run.Add(new XElement(W + "tab"));
                    run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), columns[c]));
                }
            }
            var para = new XElement(W + "p");
            if (isHeading) para.Add(new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", "Heading1"))));
            para.Add(run);
            paragraphs.Add(para);
        }

        // Assembla il pacchetto DOCX
        var documentXml = new XDocument(
            new XElement(W + "document",
                new XAttribute(XNamespace.Xmlns + "w", W.NamespaceName),
                new XElement(W + "body", paragraphs)));

        using var fs = new FileStream(docxPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // [Content_Types].xml
        WriteZipEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>");

        // _rels/.rels
        WriteZipEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>");

        // word/document.xml
        WriteZipEntry(zip, "word/document.xml", documentXml.ToString());
    }

    // ── XLSX -> Tabular ──────────────────────────────────────────────────────

    private static void ExtractXlsx(string xlsxPath, string outputPath, string format)
    {
        if (!IsTransitionalXlsx(xlsxPath))
        {
            WriteTable(SpreadsheetReader.Read(xlsxPath).First(sheet => !sheet.IsEmpty).ToRows(), outputPath, format);
            return;
        }
        using var zip = ZipFile.OpenRead(xlsxPath);

        // Leggi shared strings
        var sharedStrings = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            using var ssStream = ssEntry.Open();
            var ssDoc = XDocument.Load(ssStream);
            foreach (var si in ssDoc.Descendants(Ssml + "si"))
            {
                sharedStrings.Add(string.Join("", si.Descendants(Ssml + "t").Where(t => t.Parent?.Name != Ssml + "rPh").Select(t => t.Value)));
            }
        }

        // Il primo foglio visibile all'utente non è sempre sheet1.xml.
        var sheetEntry = FindFirstSheet(zip)
            ?? throw new IOException("Nessun foglio trovato nel file XLSX.");
        var dateStyles = ReadDateStyles(zip, out bool date1904);

        using var sheetStream = sheetEntry.Open();
        var sheetDoc = XDocument.Load(sheetStream);

        var rows = new List<List<string>>();
        foreach (var rowEl in sheetDoc.Descendants(Ssml + "row"))
        {
            var cells = new List<string>();
            var occupied = new HashSet<int>();
            int nextColumn = 0;
            foreach (var cell in rowEl.Elements(Ssml + "c"))
            {
                string type = cell.Attribute("t")?.Value ?? "";
                string rawVal = cell.Element(Ssml + "v")?.Value ?? "";

                string resolved;
                if (type == "s")
                {
                    if (!int.TryParse(rawVal, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                        || index >= sharedStrings.Count)
                        throw new IOException("Indice di sharedStrings non valido.");
                    resolved = sharedStrings[index];
                }
                else if (type == "inlineStr")
                    resolved = string.Concat(cell.Element(Ssml + "is")?.Descendants(Ssml + "t")
                        .Where(t => t.Parent?.Name != Ssml + "rPh").Select(t => t.Value) ?? []);
                else if (type == "b")
                    resolved = rawVal == "1" ? "TRUE" : "FALSE";
                else if (type is "" or "n")
                    resolved = FormatNumericCell(rawVal, (string?)cell.Attribute("s"), dateStyles, date1904);
                else
                    resolved = rawVal;

                string? reference = (string?)cell.Attribute("r");
                int column = reference is null ? nextColumn : ReadColumnIndex(reference);
                if (column >= MaxSpreadsheetColumns || !occupied.Add(column))
                    throw new IOException($"Colonna XLSX duplicata o fuori limite: {reference ?? (column + 1).ToString()}.");
                while (cells.Count <= column)
                    cells.Add("");
                cells[column] = resolved;
                nextColumn = column + 1;
            }
            rows.Add(cells);
        }

        if (rows.Count == 0) throw new IOException("Foglio Excel vuoto.");
        WriteTable(rows, outputPath, format);
    }

    /// <summary>XLS, ODS, file protetti e la variante Strict di XLSX seguono un altro lettore.</summary>
    private static bool IsTransitionalXlsx(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("xl/workbook.xml") is not { } workbook) return zip.GetEntry("xl/worksheets/sheet1.xml") != null;
            using var stream = workbook.Open();
            return XDocument.Load(stream).Root?.Name.Namespace == Ssml;
        }
        catch (InvalidDataException) { return false; }
    }

    private static void WriteTable(List<List<string>> rows, string outputPath, string format)
    {
        if (rows.Count == 0) throw new IOException("Foglio Excel vuoto.");
        var utf8 = new UTF8Encoding(false);
        var sb = new StringBuilder();

        switch (format)
        {
            case "csv":
                foreach (var row in rows)
                    sb.AppendLine(string.Join(',', row.Select(cell => EscapeDelimited(cell, ','))));
                break;

            case "tsv":
                foreach (var row in rows)
                    sb.AppendLine(string.Join('\t', row.Select(cell => EscapeDelimited(cell, '\t'))));
                break;

            case "json":
                var headers = new List<string>();
                var used = new HashSet<string>(StringComparer.Ordinal);
                int columns = rows.Max(row => row.Count);
                for (int c = 0; c < columns; c++)
                {
                    string heading = c < rows[0].Count && !string.IsNullOrWhiteSpace(rows[0][c])
                        ? rows[0][c] : $"col_{c + 1}";
                    string uniqueHeading = heading;
                    for (int suffix = 2; !used.Add(uniqueHeading); suffix++)
                        uniqueHeading = $"{heading}_{suffix}";
                    headers.Add(uniqueHeading);
                }

                var records = rows.Skip(1).Select(row => headers.Select((header, index) => (header, index))
                    .ToDictionary(item => item.header, item => item.index < row.Count ? row[item.index] : ""));
                sb.AppendLine(JsonSerializer.Serialize(records, TextFiles.ReadableJson));
                break;

            case "md":
                AppendMarkdownTable(sb, rows);
                break;

            case "xml":
                {
                    var headersXml = BuildSheetHeaders(rows);
                    var rootXml = new XElement("rows");
                    foreach (var row in rows.Skip(1))
                    {
                        var rowEl = new XElement("row");
                        for (int c = 0; c < headersXml.Count; c++)
                        {
                            string tag = DataSerializationEngine.SanitizeXmlName(headersXml[c]);
                            rowEl.Add(new XElement(tag, c < row.Count ? row[c] : ""));
                        }
                        rootXml.Add(rowEl);
                    }
                    sb.AppendLine(new XDocument(new XDeclaration("1.0", "utf-8", null), rootXml).ToString());
                }
                break;

            case "yaml":
                {
                    var headersYaml = BuildSheetHeaders(rows);
                    foreach (var row in rows.Skip(1))
                    {
                        for (int c = 0; c < headersYaml.Count; c++)
                        {
                            string prefix = c == 0 ? "- " : "  ";
                            string val = c < row.Count ? row[c] : "";
                            sb.AppendLine($"{prefix}{JsonSerializer.Serialize(headersYaml[c], TextFiles.ReadableJsonLine)}: {JsonSerializer.Serialize(val, TextFiles.ReadableJsonLine)}");
                        }
                    }
                }
                break;

            case "html":
                sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>XLSX</title>");
                sb.AppendLine("<style>body{font-family:sans-serif;margin:24px;background:#18181b;color:#f4f4f5;} table{border-collapse:collapse;width:100%;} th,td{border:1px solid #3f3f46;padding:8px;text-align:left;} th{background:#27272a;}</style>");
                sb.AppendLine("</head><body><table><thead><tr>");
                if (rows.Count > 0)
                {
                    foreach (var h in rows[0])
                        sb.Append($"<th>{System.Net.WebUtility.HtmlEncode(h)}</th>");
                    sb.AppendLine("</tr></thead><tbody>");
                    for (int r = 1; r < rows.Count; r++)
                    {
                        sb.Append("<tr>");
                        foreach (var c in rows[r]) sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(c)}</td>");
                        sb.AppendLine("</tr>");
                    }
                }
                sb.AppendLine("</tbody></table></body></html>");
                break;
        }

        File.WriteAllText(outputPath, sb.ToString(), utf8);
    }

    private static ZipArchiveEntry? FindFirstSheet(ZipArchive zip)
    {
        try
        {
            if (zip.GetEntry("xl/workbook.xml") is { } workbook && zip.GetEntry("xl/_rels/workbook.xml.rels") is { } rels)
            {
                using var workbookStream = workbook.Open();
                using var relsStream = rels.Open();
                // Il primo foglio visibile: quelli nascosti sono dati di servizio.
                var candidates = XDocument.Load(workbookStream).Descendants(Ssml + "sheet").ToList();
                var chosen = candidates.FirstOrDefault(sheet => (string?)sheet.Attribute("state") is not ("hidden" or "veryHidden")) ?? candidates.FirstOrDefault();
                string? id = chosen?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
                string? target = (string?)XDocument.Load(relsStream).Root?.Elements()
                    .FirstOrDefault(element => (string?)element.Attribute("Id") == id)?.Attribute("Target");
                if (target is not null && zip.GetEntry(target.StartsWith('/') ? target[1..] : "xl/" + target) is { } sheet) return sheet;
            }
        }
        catch (System.Xml.XmlException ex) { Evidence.Log("xlsx.workbook.invalid", ex.Message); }
        return zip.GetEntry("xl/worksheets/sheet1.xml");
    }

    /// <summary>Indici di stile che Excel mostra come date o ore: nel file sono numeri seriali.</summary>
    private static HashSet<int> ReadDateStyles(ZipArchive zip, out bool date1904)
    {
        date1904 = false;
        var result = new HashSet<int>();
        try
        {
            if (zip.GetEntry("xl/workbook.xml") is { } workbook)
            {
                using var stream = workbook.Open();
                string? value = (string?)XDocument.Load(stream).Descendants(Ssml + "workbookPr").FirstOrDefault()?.Attribute("date1904");
                date1904 = value is "1" or "true";
            }
            if (zip.GetEntry("xl/styles.xml") is not { } styles) return result;
            using var stylesStream = styles.Open();
            var doc = XDocument.Load(stylesStream);
            var custom = new Dictionary<int, string>();
            foreach (var format in doc.Descendants(Ssml + "numFmts").Elements(Ssml + "numFmt"))
                if (int.TryParse((string?)format.Attribute("numFmtId"), out int id)) custom[id] = (string?)format.Attribute("formatCode") ?? "";
            int index = 0;
            foreach (var style in doc.Root?.Element(Ssml + "cellXfs")?.Elements(Ssml + "xf") ?? [])
            {
                if (int.TryParse((string?)style.Attribute("numFmtId"), out int id) &&
                    (custom.TryGetValue(id, out string? code) ? IsDateFormat(code) : id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58))
                    result.Add(index);
                index++;
            }
        }
        catch (System.Xml.XmlException ex) { Evidence.Log("xlsx.styles.invalid", ex.Message); }
        return result;
    }

    internal static bool IsDateFormat(string code)
    {
        // Testo fra virgolette, colori, valute e caratteri protetti non sono segnaposto di data.
        string bare = Regex.Replace(code, "\"[^\"]*\"|\\\\.|_.|\\*.|\\[(?!(?:h+|m+|s+)\\])[^\\]]*\\]", "", RegexOptions.IgnoreCase);
        return !bare.Contains("General", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(bare, "[dmyhs]", RegexOptions.IgnoreCase);
    }

    private static string FormatNumericCell(string raw, string? style, HashSet<int> dateStyles, bool date1904)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) return raw;
        if (int.TryParse(style, out int index) && dateStyles.Contains(index) && number is >= 0 and < 2950000)
        {
            if (number < 1) return TimeSpan.FromSeconds(Math.Round(number * 86400)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            // Excel considera bisestile il 1900: prima del 1° marzo la serie è spostata di un giorno.
            double days = date1904 || number >= 60 ? number : number + 1;
            var date = (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30)).AddSeconds(Math.Round(days * 86400));
            return date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        // 0.1 viene salvato da Excel come 0.10000000000000001.
        if (raw.Contains('.') && raw.Count(char.IsAsciiDigit) > 15 && Math.Abs(number) is >= 1e-4 and < 1e15)
            return number.ToString("G15", CultureInfo.InvariantCulture);
        return raw;
    }

    // ── Tabular -> XLSX ──────────────────────────────────────────────────────

    private static void GenerateXlsx(string srcPath, string xlsxPath, string srcExt)
    {
        string raw = TextFiles.Read(srcPath);
        if (string.IsNullOrWhiteSpace(raw)) throw new IOException("File sorgente vuoto.");

        // Parsing rows
        List<List<string>> rows;
        if (srcExt == "json")
        {
            rows = ParseJsonToRows(raw);
        }
        else if (srcExt is "yaml" or "yml" or "xml")
        {
            string tempJson = DataSerializationEngine.ConvertRawToJson(raw, srcExt);
            rows = ParseJsonToRows(tempJson);
        }
        else
        {
            rows = TextFiles.ReadTable(raw, srcExt);
        }

        if (rows.Count == 0) throw new IOException("Nessun dato da convertire in XLSX.");

        // Shared Strings
        var allStrings = rows.SelectMany(r => r).Distinct().ToList();
        var stringIndex = new Dictionary<string, int>();
        for (int i = 0; i < allStrings.Count; i++) stringIndex[allStrings[i]] = i;

        // Build sheet XML
        var sheetRows = new List<XElement>();
        for (int r = 0; r < rows.Count; r++)
        {
            var cells = new List<XElement>();
            for (int c = 0; c < rows[r].Count; c++)
            {
                string cellRef = $"{ColumnName(c)}{r + 1}";
                int si = stringIndex[rows[r][c]];
                cells.Add(new XElement(Ssml + "c",
                    new XAttribute("r", cellRef),
                    new XAttribute("t", "s"),
                    new XElement(Ssml + "v", si)));
            }
            sheetRows.Add(new XElement(Ssml + "row", new XAttribute("r", r + 1), cells));
        }

        var worksheetDoc = new XDocument(
            new XElement(Ssml + "worksheet",
                new XElement(Ssml + "sheetData", sheetRows)));

        var sharedStringsDoc = new XDocument(
            new XElement(Ssml + "sst",
                new XAttribute("count", allStrings.Count),
                new XAttribute("uniqueCount", allStrings.Count),
                allStrings.Select(s => new XElement(Ssml + "si", new XElement(Ssml + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"), RemoveInvalidXml(s))))));

        using var fs = new FileStream(xlsxPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteZipEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
            "</Types>");

        WriteZipEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        WriteZipEntry(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

        WriteZipEntry(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
            "</Relationships>");

        WriteZipEntry(zip, "xl/worksheets/sheet1.xml", worksheetDoc.ToString());
        WriteZipEntry(zip, "xl/sharedStrings.xml", sharedStringsDoc.ToString());
    }

    // ── PPTX -> Text ──────────────────────────────────────────────────────────

    private static void ExtractPptx(string pptxPath, string outputPath, string format)
    {
        using var zip = ZipFile.OpenRead(pptxPath);

        // Trova tutte le slide ordinate per numero
        var slideEntries = zip.Entries
            .Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
            .OrderBy(e =>
            {
                var m = Regex.Match(e.Name, @"slide(\d+)\.xml", RegexOptions.IgnoreCase);
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            })
            .ToList();

        if (slideEntries.Count == 0) throw new IOException("Nessuna slide trovata nel PPTX.");

        var slides = new List<(int Num, string Text)>();
        int num = 0;
        foreach (var entry in slideEntries)
        {
            num++;
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);

            // Estrai tutti i testi dagli shape delle slide
            var texts = doc.Descendants(A + "t").Select(t => t.Value);
            string slideText = string.Join(" ", texts).Trim();
            slides.Add((num, slideText));
        }

        var utf8 = new UTF8Encoding(false);
        var sb = new StringBuilder();

        switch (format)
        {
            case "txt":
                foreach (var (n, text) in slides)
                {
                    sb.AppendLine($"--- Slide {n} ---");
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
                break;

            case "md":
                foreach (var (n, text) in slides)
                {
                    sb.AppendLine($"## Slide {n}");
                    sb.AppendLine();
                    sb.AppendLine(text);
                    sb.AppendLine();
                }
                break;

            case "html":
                sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Presentazione</title></head><body>");
                foreach (var (n, text) in slides)
                {
                    sb.AppendLine($"<h2>Slide {n}</h2>");
                    sb.AppendLine($"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>");
                }
                sb.AppendLine("</body></html>");
                break;
        }

        File.WriteAllText(outputPath, sb.ToString(), utf8);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void WriteZipEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string EscapeDelimited(string s, char delimiter)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(delimiter) || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static int ReadColumnIndex(string reference)
    {
        int index = 0;
        int letters = 0;
        while (letters < reference.Length && reference[letters] is >= 'A' and <= 'Z')
        {
            index = index * 26 + reference[letters++] - 'A' + 1;
            if (index > MaxSpreadsheetColumns) break;
        }
        if (letters == 0 || index > MaxSpreadsheetColumns ||
            !int.TryParse(reference.AsSpan(letters), NumberStyles.None, CultureInfo.InvariantCulture, out int row) ||
            row is < 1 or > 1_048_576)
            throw new IOException($"Riferimento cella XLSX non valido: {reference}.");
        return index - 1;
    }

    private static string ColumnName(int index)
    {
        if (index >= MaxSpreadsheetColumns)
            throw new IOException("Il foglio supera il limite di 16.384 colonne.");
        Span<char> letters = stackalloc char[3];
        int cursor = letters.Length;
        for (int column = index + 1; column > 0; column = (column - 1) / 26)
            letters[--cursor] = (char)('A' + (column - 1) % 26);
        return new string(letters[cursor..]);
    }

    /// <summary>I caratteri di controllo non sono ammessi in XML e renderebbero illeggibile il file Office.</summary>
    internal static string RemoveInvalidXml(string text) =>
        text.All(ch => System.Xml.XmlConvert.IsXmlChar(ch) || char.IsSurrogate(ch))
            ? text : new string(text.Where(ch => System.Xml.XmlConvert.IsXmlChar(ch) || char.IsSurrogate(ch)).ToArray());

    private static string StripHtmlTags(string html) =>
        Regex.Replace(html, @"<[^>]+>", "").Trim();

    private static List<List<string>> ParseJsonToRows(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1 &&
            root.EnumerateObject().First().Value is { ValueKind: JsonValueKind.Array } nested)
            root = nested;
        JsonElement[] items = root.ValueKind == JsonValueKind.Array ? [.. root.EnumerateArray()] : [root];
        if (items.Length == 0) return [];
        if (items.All(item => item.ValueKind == JsonValueKind.Array))
            return items.Select(item => item.EnumerateArray().Select(CellText).ToList()).ToList();

        // Le colonne sono l'unione delle chiavi: un campo presente solo in una riga successiva non va perso.
        var headers = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in item.EnumerateObject())
                    if (known.Add(property.Name)) headers.Add(property.Name);
            }
            else if (known.Add("value")) headers.Add("value");
        }

        var rows = new List<List<string>> { headers };
        foreach (var item in items)
        {
            rows.Add(headers.Select(header => item.ValueKind == JsonValueKind.Object
                ? item.TryGetProperty(header, out var value) ? CellText(value) : ""
                : header == "value" ? CellText(item) : "").ToList());
        }
        return rows;

        static string CellText(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => value.GetRawText()
        };
    }

    private static List<string> BuildSheetHeaders(List<List<string>> rows)
    {
        var headers = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int columns = rows.Max(row => row.Count);
        for (int c = 0; c < columns; c++)
        {
            string heading = c < rows[0].Count && !string.IsNullOrWhiteSpace(rows[0][c])
                ? rows[0][c] : $"col_{c + 1}";
            string uniqueHeading = heading;
            for (int suffix = 2; !used.Add(uniqueHeading); suffix++)
                uniqueHeading = $"{heading}_{suffix}";
            headers.Add(uniqueHeading);
        }
        return headers;
    }

    // ── Conversione universale Document & Text (TXT/MD/HTML/DOCX/ODT/RTF) ────

    internal static string ExtractPlainTextForPdf(string srcPath, string srcExt)
    {
        string ext = srcExt.TrimStart('.').ToLowerInvariant();
        if (ext is "docx" or "odt" or "rtf" or "html" or "md" or "txt")
        {
            var blocks = ReadStructuredBlocks(srcPath, ext);
            return string.Join("\n", blocks.Select(b => b.IsHeading ? b.Text.ToUpperInvariant() : b.Text));
        }
        if (ext == "xlsx")
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"flip-xlsx-txt-{Guid.NewGuid():N}.tsv");
            try
            {
                ExtractXlsx(srcPath, tmp, "tsv");
                return File.ReadAllText(tmp, Encoding.UTF8);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
        return TextFiles.Read(srcPath);
    }

    internal static void WriteFromPlainText(string fullText, string dstPath, string dstExt)
    {
        string[] lines = fullText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = lines.Select(line => (IsHeading: false, Text: line)).ToList();
        WriteStructuredBlocks(blocks, dstPath, dstExt.TrimStart('.').ToLowerInvariant());
    }

    private static void ConvertDocOrText(string srcPath, string srcExt, string dstPath, string dstExt)
    {
        var blocks = ReadStructuredBlocks(srcPath, srcExt);
        if (blocks.Count == 0 || blocks.All(b => string.IsNullOrWhiteSpace(b.Text)))
            throw new IOException("File sorgente vuoto o privo di testo.");
        WriteStructuredBlocks(blocks, dstPath, dstExt, Path.GetFileNameWithoutExtension(srcPath));
    }

    private static List<(bool IsHeading, string Text)> ReadStructuredBlocks(string srcPath, string srcExt)
    {
        if (srcExt is "docx" or "odt")
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"flip-doc-md-{Guid.NewGuid():N}.md");
            try
            {
                if (srcExt == "docx") ExtractDocx(srcPath, tmp, "md");
                else ExtractOdt(srcPath, tmp, "md");
                return ReadMarkdownBlocks(File.ReadAllText(tmp, Encoding.UTF8));
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        string raw = TextFiles.Read(srcPath);
        if (string.IsNullOrWhiteSpace(raw))
            throw new IOException("File sorgente vuoto.");

        return srcExt switch
        {
            "md" => ReadMarkdownBlocks(raw),
            "html" => ReadHtmlBlocks(raw),
            "rtf" => ReadRtfBlocks(raw),
            _ => raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(l => (IsHeading: false, Text: l)).ToList()
        };
    }

    private static List<(bool IsHeading, string Text)> ReadMarkdownBlocks(string raw)
    {
        var list = new List<(bool IsHeading, string Text)>();
        string[] lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool code = false;
        foreach (string line in lines.Skip(FrontMatterLength(lines)))
        {
            string trimmed = line.TrimEnd();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) code = !code;
            // "#parola" è un hashtag e dentro un blocco di codice # è un commento.
            if (!code && Regex.IsMatch(trimmed, @"^#{1,6}\s"))
            {
                string heading = trimmed.TrimStart('#').Trim();
                if (heading.Length > 0) list.Add((true, heading));
            }
            else
            {
                list.Add((false, trimmed));
            }
        }
        return list;
    }

    /// <summary>Righe della testata YAML (fra due ---) all'inizio di un Markdown.</summary>
    internal static int FrontMatterLength(string[] lines)
    {
        if (lines.Length < 3 || lines[0].TrimEnd() != "---") return 0;
        int end = Array.FindIndex(lines, 1, line => line.TrimEnd() is "---" or "...");
        return end > 0 && lines[1..end].All(line => line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '-' or '#' ||
            Regex.IsMatch(line, @"^[A-Za-z0-9_. -]+:")) ? end + 1 : 0;
    }

    private static List<(bool IsHeading, string Text)> ReadHtmlBlocks(string raw)
    {
        string cleaned = Regex.Replace(raw, @"<(script|style|head)\b[^>]*>.*?</\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var list = new List<(bool IsHeading, string Text)>();
        cleaned = Regex.Replace(cleaned, @"<!--.*?-->", "", RegexOptions.Singleline);
        // Il testo è delimitato dai tag di blocco, anche quando sono annidati o non chiusi.
        foreach (string part in Regex.Split(cleaned, @"(<pre\b[^>]*>.*?</pre>)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            bool preformatted = Regex.IsMatch(part, @"^<pre\b", RegexOptions.IgnoreCase);
            string flow = preformatted ? "\uE000" + part.ReplaceLineEndings("\uE002") + "\uE000" : Regex.Replace(part, @"\s+", " ");
            flow = Regex.Replace(flow, @"<br\s*/?>", "\uE002", RegexOptions.IgnoreCase);
            flow = Regex.Replace(flow, @"<h[1-6]\b[^>]*>", "\uE000\uE001", RegexOptions.IgnoreCase);
            flow = Regex.Replace(flow, @"</t[dh]>\s*(?=<t[dh]\b)", "\t", RegexOptions.IgnoreCase);
            flow = Regex.Replace(flow, @"</?(?:h[1-6]|p|li|ul|ol|div|tr|table|thead|tbody|section|article|header|footer|main|nav|aside|blockquote|figure|figcaption|dl|dt|dd|form|hr|address|body|html)\b[^>]*>", "\uE000", RegexOptions.IgnoreCase);
            flow = System.Net.WebUtility.HtmlDecode(Regex.Replace(flow, @"<[^>]+>", ""));
            foreach (string block in flow.Split('\uE000'))
            {
                bool heading = block.Contains('\uE001');
                string text = block.Replace("\uE001", "").Replace('\uE002', '\n');
                text = preformatted ? text.Trim('\n') : Regex.Replace(text, @" *\n *", "\n").Trim(' ', '\n', '\t');
                if (!string.IsNullOrWhiteSpace(text)) list.Add((heading, text));
            }
        }
        if (list.Count == 0)
        {
            string fallback = System.Net.WebUtility.HtmlDecode(StripHtmlTags(cleaned)).Trim();
            foreach (string line in fallback.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                list.Add((false, line));
        }
        return list;
    }

    private static List<(bool IsHeading, string Text)> ReadRtfBlocks(string rtf)
    {
        var ansi = Regex.Match(rtf, @"\\ansicpg(\d+)");
        int codePage = ansi.Success ? int.Parse(ansi.Groups[1].Value, CultureInfo.InvariantCulture) : 1252;
        // Rimuovi i gruppi RTF di intestazione (\fonttbl, \colortbl, \stylesheet, \info, \*\...)
        string text = Regex.Replace(rtf, @"\{\\\*\\[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", "");
        text = Regex.Replace(text, @"\{\\(?:fonttbl|colortbl|stylesheet|info|pict)\b[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", "");
        text = Regex.Replace(text, @"\\(?:par|line|row|page)\b\s?", "\n");
        text = Regex.Replace(text, @"\\(?:tab|cell)\b\s?", "\t");
        // Prima i codici Unicode (\uN? oppure \uN\'xx) così il byte ANSI di fallback non duplica il carattere.
        text = Regex.Replace(text, @"\\u(-?\d+)(?:\s?\\'[0-9a-fA-F]{2}|\s?\??)", m =>
        {
            int code = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (code < 0) code += 65536;
            if (code is >= 0xD800 and <= 0xDFFF) return ((char)code).ToString();
            return char.ConvertFromUtf32(Math.Clamp(code, 0, 0x10FFFF));
        });
        text = Regex.Replace(text, @"\\'([0-9a-fA-F]{2})", m =>
        {
            // Windows-1252: 92 è l'apostrofo tipografico e 80 l'euro, non caratteri di controllo.
            return TextFiles.DecodeAnsi(Convert.ToByte(m.Groups[1].Value, 16), codePage);
        });
        text = Regex.Replace(text, @"\\([\\{}])", "$1");
        text = Regex.Replace(text, @"\\[a-zA-Z]+-?\d*\s?", "");
        text = text.Replace("{", "").Replace("}", "");
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => (IsHeading: false, Text: l.TrimEnd())).ToList();
    }

    private static void WriteStructuredBlocks(List<(bool IsHeading, string Text)> blocks, string dstPath, string dstExt, string title = "")
    {
        var utf8 = new UTF8Encoding(false);
        switch (dstExt)
        {
            case "txt":
                File.WriteAllText(dstPath, string.Join("\n", blocks.Select(b => b.Text)), utf8);
                break;

            case "md":
                {
                    var sb = new StringBuilder();
                    bool wroteTitle = false;
                    foreach (var (isHeading, text) in blocks)
                    {
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            sb.AppendLine();
                            continue;
                        }
                        if (isHeading || (!wroteTitle && blocks.Count > 1 && !text.StartsWith('#') && text.Length < 80))
                        {
                            sb.AppendLine($"## {text.TrimStart('#').Trim()}");
                            sb.AppendLine();
                            wroteTitle = true;
                        }
                        else
                        {
                            sb.AppendLine(text);
                            wroteTitle = true;
                        }
                    }
                    File.WriteAllText(dstPath, sb.ToString().TrimEnd() + "\n", utf8);
                }
                break;

            case "html":
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>{System.Net.WebUtility.HtmlEncode(title)}</title></head><body>");
                    foreach (var (isHeading, text) in blocks)
                    {
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        string tag = isHeading ? "h2" : "p";
                        sb.AppendLine($"<{tag}>{HtmlText(text)}</{tag}>");
                    }
                    sb.AppendLine("</body></html>");
                    File.WriteAllText(dstPath, sb.ToString(), utf8);
                }
                break;

            case "docx":
                GenerateDocx(blocks, dstPath);
                break;

            case "odt":
                GenerateOdt(blocks, dstPath);
                break;

            case "rtf":
                GenerateRtf(blocks, dstPath);
                break;

            default:
                throw new NotSupportedException($"Formato documento non supportato: {dstExt}");
        }
    }

    private static void GenerateOdt(List<(bool IsHeading, string Text)> blocks, string odtPath)
    {
        XNamespace manifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        var bodyElements = new List<XElement>();
        foreach (var (isHeading, text) in blocks)
        {
            if (isHeading)
                bodyElements.Add(new XElement(OdtText + "h", new XAttribute(OdtText + "outline-level", "2"), OdtContent(text)));
            else
                bodyElements.Add(new XElement(OdtText + "p", OdtContent(text)));
        }

        var contentDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(OdtOffice + "document-content",
                new XAttribute(XNamespace.Xmlns + "office", OdtOffice.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "text", OdtText.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "table", OdtTable.NamespaceName),
                new XAttribute(OdtOffice + "version", "1.2"),
                new XElement(OdtOffice + "body",
                    new XElement(OdtOffice + "text", bodyElements))));

        var manifestDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(manifestNs + "manifest",
                new XAttribute(XNamespace.Xmlns + "manifest", manifestNs.NamespaceName),
                new XAttribute(manifestNs + "version", "1.2"),
                new XElement(manifestNs + "file-entry",
                    new XAttribute(manifestNs + "full-path", "/"),
                    new XAttribute(manifestNs + "media-type", "application/vnd.oasis.opendocument.text")),
                new XElement(manifestNs + "file-entry",
                    new XAttribute(manifestNs + "full-path", "content.xml"),
                    new XAttribute(manifestNs + "media-type", "text/xml")),
                new XElement(manifestNs + "file-entry",
                    new XAttribute(manifestNs + "full-path", "styles.xml"),
                    new XAttribute(manifestNs + "media-type", "text/xml"))));
        // Pandoc e altri lettori rifiutano un ODT senza stili, anche se vuoti.
        var stylesDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(OdtOffice + "document-styles",
                new XAttribute(XNamespace.Xmlns + "office", OdtOffice.NamespaceName),
                new XAttribute(OdtOffice + "version", "1.2"),
                new XElement(OdtOffice + "styles"),
                new XElement(OdtOffice + "automatic-styles"),
                new XElement(OdtOffice + "master-styles")));

        using var fs = new FileStream(odtPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var mimeEntry = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mimeEntry.Open())
        using (var w = new StreamWriter(s, Encoding.ASCII))
            w.Write("application/vnd.oasis.opendocument.text");

        WriteZipEntry(zip, "META-INF/manifest.xml", manifestDoc.ToString());
        WriteZipEntry(zip, "styles.xml", stylesDoc.ToString());
        WriteZipEntry(zip, "content.xml", contentDoc.ToString());
    }

    /// <summary>In ODT spazi ripetuti, tabulazioni e a capo sono elementi: come testo verrebbero ridotti a uno spazio.</summary>
    private static IEnumerable<object> OdtContent(string text)
    {
        foreach (Match part in Regex.Matches(RemoveInvalidXml(text), "\n|\t| {2,}|^ |[^\n\t ]+(?: [^\n\t ]+)*| "))
        {
            if (part.Value == "\n") yield return new XElement(OdtText + "line-break");
            else if (part.Value == "\t") yield return new XElement(OdtText + "tab");
            else if (part.Value.Trim(' ').Length == 0)
                yield return new XElement(OdtText + "s", new XAttribute(OdtText + "c", part.Length));
            else yield return new XText(part.Value);
        }
    }

    private static void GenerateRtf(List<(bool IsHeading, string Text)> blocks, string rtfPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\viewkind4\uc1\pard\f0\fs22");
        foreach (var (isHeading, text) in blocks)
        {
            if (isHeading) sb.Append(@"\b\fs28 ");
            foreach (char c in text)
            {
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append(@"\line ");
                else if (c == '\t') sb.Append(@"\tab ");
                else if (c < ' ') continue;
                else if (c <= 0x7F) sb.Append(c);
                else sb.Append($@"\u{(short)c}?");
            }
            if (isHeading) sb.Append(@"\b0\fs22");
            sb.AppendLine(@"\par");
        }
        sb.Append('}');
        File.WriteAllText(rtfPath, sb.ToString(), Encoding.ASCII);
    }

    private static void ConvertTextToData(string srcPath, string dstPath, string dstExt)
    {
        string raw = TextFiles.Read(srcPath);
        if (string.IsNullOrWhiteSpace(raw)) throw new IOException("File di testo vuoto.");
        string[] lines = raw.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var utf8 = new UTF8Encoding(false);
        if (dstExt == "json")
        {
            var payload = lines.Select((line, index) => new { line = index + 1, text = line }).ToArray();
            File.WriteAllText(dstPath, JsonSerializer.Serialize(payload, TextFiles.ReadableJson), utf8);
        }
        else if (dstExt == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine("line,text");
            for (int i = 0; i < lines.Length; i++)
                sb.AppendLine($"{i + 1},{EscapeDelimited(lines[i], ',')}");
            File.WriteAllText(dstPath, sb.ToString(), utf8);
        }
    }

    // ── Rich OpenXML Parser & High-Fidelity RTF Writer ───────────────────────

    private static void ConvertDocxToRtf(string docxPath, string rtfPath)
    {
        var richDoc = ParseDocxToRichDocument(docxPath);
        if (!richDoc.HasAnyContent())
            throw new IOException("File sorgente vuoto o privo di testo.");
        GenerateRichRtf(richDoc, rtfPath);
    }

    internal static RichDocument? TryReadRichDocumentForPdf(string srcPath, string srcExt)
    {
        string ext = srcExt.TrimStart('.').ToLowerInvariant();
        if (ext == "docx")
            return ParseDocxToRichDocument(srcPath);
        if (ext == "md")
            return ParseMarkdownToRichDocument(TextFiles.Read(srcPath));
        if (ext is "odt" or "rtf" or "html")
            return BuildRichDocumentFromStructuredBlocks(ReadStructuredBlocks(srcPath, ext));
        return null;
    }

    internal static RichDocument ParseDocxToRichDocument(string docxPath)
    {
        using var zip = ZipFile.OpenRead(docxPath);
        var docEntry = zip.GetEntry("word/document.xml")
            ?? throw new IOException("document.xml non trovato nel DOCX.");

        using var docStream = docEntry.Open();
        var xDoc = XDocument.Load(docStream, LoadOptions.PreserveWhitespace);
        XNamespace ns = xDoc.Root?.Name.Namespace ?? W;
        var body = xDoc.Root?.Element(ns + "body")
            ?? throw new IOException("Struttura DOCX non valida: body non trovato.");

        var (majorFont, minorFont) = ReadThemeFonts(zip);
        var styles = ReadStyles(zip, ns, majorFont, minorFont);
        var numbering = ReadNumbering(zip, ns);

        var result = new RichDocument
        {
            DefaultFontFamily = styles.DefaultRun.FontFamily ?? minorFont,
            DefaultFontSizePt = styles.DefaultRun.FontSizePt ?? 11f
        };

        ApplySectionPageLayout(body.Element(ns + "sectPr"), ns, result);

        var listCounters = new Dictionary<(string NumId, int Level), int>();
        ParseContainerBlocks(body, ns, result.Blocks, styles, numbering, listCounters, majorFont, minorFont);

        return result;
    }

    private static void ApplySectionPageLayout(XElement? sectPr, XNamespace ns, RichDocument doc)
    {
        if (sectPr == null) return;
        var pgSz = sectPr.Element(ns + "pgSz");
        if (pgSz != null)
        {
            float? w = TryParseTwipsToPt((string?)pgSz.Attribute(ns + "w"));
            float? h = TryParseTwipsToPt((string?)pgSz.Attribute(ns + "h"));
            if (w is >= 180f and <= 2400f && h is >= 180f and <= 2400f)
            {
                doc.PageWidthPt = w.Value;
                doc.PageHeightPt = h.Value;
            }
        }

        var pgMar = sectPr.Element(ns + "pgMar");
        if (pgMar != null)
        {
            float? top = TryParseTwipsToPt((string?)pgMar.Attribute(ns + "top"));
            float? bottom = TryParseTwipsToPt((string?)pgMar.Attribute(ns + "bottom"));
            float? left = TryParseTwipsToPt((string?)pgMar.Attribute(ns + "left"));
            float? right = TryParseTwipsToPt((string?)pgMar.Attribute(ns + "right"));
            if (top is >= 12f and <= 180f) doc.MarginTopPt = top.Value;
            if (bottom is >= 12f and <= 180f) doc.MarginBottomPt = bottom.Value;
            if (left is >= 12f and <= 180f) doc.MarginLeftPt = left.Value;
            if (right is >= 12f and <= 180f) doc.MarginRightPt = right.Value;
        }
    }

    private static (string MajorFont, string MinorFont) ReadThemeFonts(ZipArchive zip)
    {
        string major = "Calibri Light";
        string minor = "Calibri";
        try
        {
            var entry = zip.GetEntry("word/theme/theme1.xml");
            if (entry == null) return (major, minor);
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            string? maj = (string?)doc.Descendants(A + "majorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface");
            string? min = (string?)doc.Descendants(A + "minorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface");
            if (!string.IsNullOrWhiteSpace(maj)) major = maj.Trim();
            if (!string.IsNullOrWhiteSpace(min)) minor = min.Trim();
        }
        catch { /* best effort theme font fallback */ }
        return (major, minor);
    }

    private sealed class PartialParagraphStyle
    {
        public int? HeadingLevel;
        public RichAlignment? Alignment;
        public float? SpaceBeforePt;
        public float? SpaceAfterPt;
        public float? LineSpacingMultiplier;
        public float? IndentLeftPt;
        public float? IndentRightPt;
        public float? FirstLineIndentPt;
        public bool? PageBreakBefore;
        public string? NumId;
        public int? ListLevel;
        public string? BackgroundColorHex;
        public bool? HasBottomBorder;

        public void MergeFrom(PartialParagraphStyle other)
        {
            if (other.HeadingLevel.HasValue) HeadingLevel = other.HeadingLevel;
            if (other.Alignment.HasValue) Alignment = other.Alignment;
            if (other.SpaceBeforePt.HasValue) SpaceBeforePt = other.SpaceBeforePt;
            if (other.SpaceAfterPt.HasValue) SpaceAfterPt = other.SpaceAfterPt;
            if (other.LineSpacingMultiplier.HasValue) LineSpacingMultiplier = other.LineSpacingMultiplier;
            if (other.IndentLeftPt.HasValue) IndentLeftPt = other.IndentLeftPt;
            if (other.IndentRightPt.HasValue) IndentRightPt = other.IndentRightPt;
            if (other.FirstLineIndentPt.HasValue) FirstLineIndentPt = other.FirstLineIndentPt;
            if (other.PageBreakBefore.HasValue) PageBreakBefore = other.PageBreakBefore;
            if (other.NumId != null) NumId = other.NumId;
            if (other.ListLevel.HasValue) ListLevel = other.ListLevel;
            if (other.BackgroundColorHex != null) BackgroundColorHex = other.BackgroundColorHex;
            if (other.HasBottomBorder.HasValue) HasBottomBorder = other.HasBottomBorder;
        }
    }

    private sealed class PartialRunStyle
    {
        public bool? Bold;
        public bool? Italic;
        public bool? Underline;
        public bool? Strike;
        public RichVerticalAlign? VerticalAlign;
        public string? FontFamily;
        public float? FontSizePt;
        public string? TextColorHex;
        public string? HighlightColorHex;

        public void MergeFrom(PartialRunStyle other)
        {
            if (other.Bold.HasValue) Bold = other.Bold;
            if (other.Italic.HasValue) Italic = other.Italic;
            if (other.Underline.HasValue) Underline = other.Underline;
            if (other.Strike.HasValue) Strike = other.Strike;
            if (other.VerticalAlign.HasValue) VerticalAlign = other.VerticalAlign;
            if (!string.IsNullOrWhiteSpace(other.FontFamily)) FontFamily = other.FontFamily;
            if (other.FontSizePt.HasValue) FontSizePt = other.FontSizePt;
            if (other.TextColorHex != null) TextColorHex = other.TextColorHex;
            if (other.HighlightColorHex != null) HighlightColorHex = other.HighlightColorHex;
        }
    }

    private sealed class RawStyleDef
    {
        public string StyleId = "";
        public string Name = "";
        public string Type = "paragraph";
        public string? BasedOn;
        public bool IsDefault;
        public PartialParagraphStyle Paragraph = new();
        public PartialRunStyle Run = new();
    }

    private sealed class DocxStyleCatalog
    {
        public PartialParagraphStyle DefaultParagraph = new();
        public PartialRunStyle DefaultRun = new();
        public string? DefaultParagraphStyleId;
        public Dictionary<string, RawStyleDef> Styles = new(StringComparer.OrdinalIgnoreCase);

        public (PartialParagraphStyle Para, PartialRunStyle Run) ResolveParagraphStyle(string? styleId)
        {
            var para = new PartialParagraphStyle();
            var run = new PartialRunStyle();
            para.MergeFrom(DefaultParagraph);
            run.MergeFrom(DefaultRun);

            string? targetId = !string.IsNullOrWhiteSpace(styleId) ? styleId : DefaultParagraphStyleId;
            var chain = new List<RawStyleDef>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? cur = targetId;
            int guard = 0;
            while (!string.IsNullOrWhiteSpace(cur) && guard++ < 16 && visited.Add(cur) && Styles.TryGetValue(cur, out var def))
            {
                chain.Add(def);
                cur = def.BasedOn;
            }
            chain.Reverse();

            int inferredHeading = 0;
            foreach (var def in chain)
            {
                para.MergeFrom(def.Paragraph);
                run.MergeFrom(def.Run);
                int h = DetectHeadingLevel(def.StyleId, def.Name);
                if (h != 0) inferredHeading = h;
            }

            if (inferredHeading == 0 && !string.IsNullOrWhiteSpace(styleId))
                inferredHeading = DetectHeadingLevel(styleId, styleId);
            if (!para.HeadingLevel.HasValue && inferredHeading != 0)
                para.HeadingLevel = inferredHeading;

            // Applica i default visivi per Heading/Title solo se styles.xml non ha esplicitato dimensione/grassetto.
            if (para.HeadingLevel is int hl and not 0)
                ApplyHeadingDefaultsIfMissing(hl, para, run);

            return (para, run);
        }

        public PartialRunStyle ResolveCharacterStyle(string? styleId)
        {
            var run = new PartialRunStyle();
            if (string.IsNullOrWhiteSpace(styleId)) return run;
            var chain = new List<RawStyleDef>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? cur = styleId;
            int guard = 0;
            while (!string.IsNullOrWhiteSpace(cur) && guard++ < 16 && visited.Add(cur) && Styles.TryGetValue(cur, out var def))
            {
                chain.Add(def);
                cur = def.BasedOn;
            }
            chain.Reverse();
            foreach (var def in chain)
                run.MergeFrom(def.Run);
            return run;
        }
    }

    private sealed class NumberingLevelDef
    {
        public string NumFmt = "bullet";
        public string LvlText = "•";
        public int Start = 1;
        public float? IndentLeftPt;
        public float? FirstLineIndentPt;
    }

    private sealed class NumberingCatalog
    {
        public Dictionary<string, Dictionary<int, NumberingLevelDef>> AbstractNums = new(StringComparer.Ordinal);
        public Dictionary<string, (string AbstractNumId, Dictionary<int, int> StartOverrides)> Nums = new(StringComparer.Ordinal);

        public NumberingLevelDef? FindLevel(string numId, int level, out int startOverride)
        {
            startOverride = -1;
            if (!Nums.TryGetValue(numId, out var num)) return null;
            if (num.StartOverrides.TryGetValue(level, out int ov)) startOverride = ov;
            if (!AbstractNums.TryGetValue(num.AbstractNumId, out var levels)) return null;
            return levels.TryGetValue(level, out var def) ? def : null;
        }
    }

    private static DocxStyleCatalog ReadStyles(ZipArchive zip, XNamespace ns, string majorFont, string minorFont)
    {
        var catalog = new DocxStyleCatalog();
        catalog.DefaultRun.FontFamily = minorFont;
        catalog.DefaultRun.FontSizePt = 11f;
        catalog.DefaultParagraph.SpaceAfterPt = 6f;
        catalog.DefaultParagraph.LineSpacingMultiplier = 1.15f;

        try
        {
            var entry = zip.GetEntry("word/styles.xml");
            if (entry == null) return catalog;
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root == null) return catalog;
            XNamespace sns = root.Name.Namespace;

            var docDefaults = root.Element(sns + "docDefaults");
            if (docDefaults != null)
            {
                var rPrDefault = docDefaults.Element(sns + "rPrDefault")?.Element(sns + "rPr");
                if (rPrDefault != null)
                    catalog.DefaultRun.MergeFrom(ParseRunProperties(rPrDefault, sns, majorFont, minorFont));
                var pPrDefault = docDefaults.Element(sns + "pPrDefault")?.Element(sns + "pPr");
                if (pPrDefault != null)
                    catalog.DefaultParagraph.MergeFrom(ParseParagraphProperties(pPrDefault, sns));
            }

            foreach (var styleEl in root.Elements(sns + "style"))
            {
                string? styleId = (string?)styleEl.Attribute(sns + "styleId");
                if (string.IsNullOrWhiteSpace(styleId)) continue;
                string type = (string?)styleEl.Attribute(sns + "type") ?? "paragraph";
                bool isDefault = IsOpenXmlTruthy((string?)styleEl.Attribute(sns + "default"));
                string name = (string?)styleEl.Element(sns + "name")?.Attribute(sns + "val") ?? styleId;
                string? basedOn = (string?)styleEl.Element(sns + "basedOn")?.Attribute(sns + "val");

                var def = new RawStyleDef
                {
                    StyleId = styleId,
                    Name = name,
                    Type = type,
                    BasedOn = basedOn,
                    IsDefault = isDefault
                };
                if (styleEl.Element(sns + "pPr") is { } pPr)
                    def.Paragraph = ParseParagraphProperties(pPr, sns);
                if (styleEl.Element(sns + "rPr") is { } rPr)
                    def.Run = ParseRunProperties(rPr, sns, majorFont, minorFont);

                catalog.Styles[styleId] = def;
                if (isDefault && type.Equals("paragraph", StringComparison.OrdinalIgnoreCase))
                    catalog.DefaultParagraphStyleId = styleId;
            }
        }
        catch { /* fallback gracefully to default styles */ }

        return catalog;
    }

    private static NumberingCatalog ReadNumbering(ZipArchive zip, XNamespace ns)
    {
        var catalog = new NumberingCatalog();
        try
        {
            var entry = zip.GetEntry("word/numbering.xml");
            if (entry == null) return catalog;
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root == null) return catalog;
            XNamespace nns = root.Name.Namespace;

            foreach (var abs in root.Elements(nns + "abstractNum"))
            {
                string? absId = (string?)abs.Attribute(nns + "abstractNumId");
                if (string.IsNullOrWhiteSpace(absId)) continue;
                var map = new Dictionary<int, NumberingLevelDef>();
                foreach (var lvl in abs.Elements(nns + "lvl"))
                {
                    if (!int.TryParse((string?)lvl.Attribute(nns + "ilvl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ilvl))
                        continue;
                    string numFmt = (string?)lvl.Element(nns + "numFmt")?.Attribute(nns + "val") ?? "bullet";
                    string lvlText = (string?)lvl.Element(nns + "lvlText")?.Attribute(nns + "val") ?? "•";
                    int start = 1;
                    if (int.TryParse((string?)lvl.Element(nns + "start")?.Attribute(nns + "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
                        start = s;

                    var def = new NumberingLevelDef { NumFmt = numFmt, LvlText = lvlText, Start = start };
                    var ind = lvl.Element(nns + "pPr")?.Element(nns + "ind");
                    if (ind != null)
                    {
                        def.IndentLeftPt = TryParseTwipsToPt((string?)ind.Attribute(nns + "left") ?? (string?)ind.Attribute(nns + "start"));
                        float? hanging = TryParseTwipsToPt((string?)ind.Attribute(nns + "hanging"));
                        if (hanging.HasValue) def.FirstLineIndentPt = -hanging.Value;
                    }
                    map[ilvl] = def;
                }
                catalog.AbstractNums[absId] = map;
            }

            foreach (var num in root.Elements(nns + "num"))
            {
                string? numId = (string?)num.Attribute(nns + "numId");
                string? absId = (string?)num.Element(nns + "abstractNumId")?.Attribute(nns + "val");
                if (string.IsNullOrWhiteSpace(numId) || string.IsNullOrWhiteSpace(absId)) continue;
                var overrides = new Dictionary<int, int>();
                foreach (var ov in num.Elements(nns + "lvlOverride"))
                {
                    if (int.TryParse((string?)ov.Attribute(nns + "ilvl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ilvl) &&
                        int.TryParse((string?)ov.Element(nns + "startOverride")?.Attribute(nns + "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int st))
                    {
                        overrides[ilvl] = st;
                    }
                }
                catalog.Nums[numId] = (absId, overrides);
            }
        }
        catch { /* fallback gracefully to simple bullets */ }

        return catalog;
    }

    private static void ParseContainerBlocks(
        XElement container,
        XNamespace ns,
        List<RichBlock> targetBlocks,
        DocxStyleCatalog styles,
        NumberingCatalog numbering,
        Dictionary<(string NumId, int Level), int> listCounters,
        string majorFont,
        string minorFont)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == ns + "p")
            {
                targetBlocks.Add(ParseRichParagraph(child, ns, styles, numbering, listCounters, majorFont, minorFont));
            }
            else if (child.Name == ns + "tbl")
            {
                var table = ParseRichTable(child, ns, styles, numbering, listCounters, majorFont, minorFont);
                if (table.Rows.Count > 0)
                    targetBlocks.Add(table);
            }
            else if (child.Name == ns + "sdt")
            {
                var sdtContent = child.Element(ns + "sdtContent");
                if (sdtContent != null)
                    ParseContainerBlocks(sdtContent, ns, targetBlocks, styles, numbering, listCounters, majorFont, minorFont);
            }
        }
    }

    private static RichTable ParseRichTable(
        XElement tableEl,
        XNamespace ns,
        DocxStyleCatalog styles,
        NumberingCatalog numbering,
        Dictionary<(string NumId, int Level), int> listCounters,
        string majorFont,
        string minorFont)
    {
        var table = new RichTable();
        var grid = tableEl.Element(ns + "tblGrid");
        if (grid != null)
        {
            foreach (var col in grid.Elements(ns + "gridCol"))
            {
                if (float.TryParse((string?)col.Attribute(ns + "w"), NumberStyles.Float, CultureInfo.InvariantCulture, out float w) && w > 0)
                    table.ColumnWidthTwips.Add(w);
            }
        }

        long totalCells = 0;
        foreach (var tr in tableEl.Elements(ns + "tr"))
        {
            var row = new RichTableRow();
            var trPr = tr.Element(ns + "trPr");
            if (trPr != null && trPr.Element(ns + "tblHeader") is { } hdr && ReadOpenXmlToggle(hdr) != false)
                row.IsHeader = true;

            foreach (var tc in tr.Elements(ns + "tc"))
            {
                totalCells++;
                if (totalCells > 200_000)
                    throw new IOException("La tabella contiene troppe celle.");

                var cell = new RichTableCell();
                var tcPr = tc.Element(ns + "tcPr");
                if (tcPr != null)
                {
                    if (int.TryParse((string?)tcPr.Element(ns + "gridSpan")?.Attribute(ns + "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int span) && span > 1)
                        cell.GridSpan = Math.Min(span, 64);

                    var tcW = tcPr.Element(ns + "tcW");
                    string? tcWType = (string?)tcW?.Attribute(ns + "type");
                    if (tcW != null && (string.IsNullOrEmpty(tcWType) || tcWType.Equals("dxa", StringComparison.OrdinalIgnoreCase)) &&
                        float.TryParse((string?)tcW.Attribute(ns + "w"), NumberStyles.Float, CultureInfo.InvariantCulture, out float wTwips) && wTwips > 0)
                    {
                        cell.WidthTwips = wTwips;
                    }

                    string? fill = NormalizeHexColor((string?)tcPr.Element(ns + "shd")?.Attribute(ns + "fill"));
                    if (fill != null && !fill.Equals("FFFFFF", StringComparison.OrdinalIgnoreCase))
                        cell.ShadingColorHex = fill;
                }

                foreach (var pEl in tc.Elements(ns + "p"))
                {
                    var p = ParseRichParagraph(pEl, ns, styles, numbering, listCounters, majorFont, minorFont);
                    // Nelle celle tabella usa spaziatura compatta se non specificata.
                    if (p.SpaceAfterPt > 3f && pEl.Element(ns + "pPr")?.Element(ns + "spacing")?.Attribute(ns + "after") == null)
                        p.SpaceAfterPt = 2f;
                    cell.Paragraphs.Add(p);
                }

                if (cell.Paragraphs.Count == 0)
                    cell.Paragraphs.Add(new RichParagraph { SpaceAfterPt = 2f });

                row.Cells.Add(cell);
            }

            if (row.Cells.Count > 0)
                table.Rows.Add(row);
        }

        return table;
    }

    private static RichParagraph ParseRichParagraph(
        XElement pEl,
        XNamespace ns,
        DocxStyleCatalog styles,
        NumberingCatalog numbering,
        Dictionary<(string NumId, int Level), int> listCounters,
        string majorFont,
        string minorFont)
    {
        var pPr = pEl.Element(ns + "pPr");
        string? pStyleId = (string?)pPr?.Element(ns + "pStyle")?.Attribute(ns + "val");
        var (resolvedPara, resolvedRun) = styles.ResolveParagraphStyle(pStyleId);

        if (pPr != null)
        {
            resolvedPara.MergeFrom(ParseParagraphProperties(pPr, ns));
            if (pPr.Element(ns + "rPr") is { } pRunPr)
                resolvedRun.MergeFrom(ParseRunProperties(pRunPr, ns, majorFont, minorFont));
        }

        var para = new RichParagraph
        {
            HeadingLevel = resolvedPara.HeadingLevel ?? 0,
            Alignment = resolvedPara.Alignment ?? RichAlignment.Left,
            SpaceBeforePt = resolvedPara.SpaceBeforePt ?? 0f,
            SpaceAfterPt = resolvedPara.SpaceAfterPt ?? 6f,
            LineSpacingMultiplier = resolvedPara.LineSpacingMultiplier ?? 1.15f,
            IndentLeftPt = resolvedPara.IndentLeftPt ?? 0f,
            IndentRightPt = resolvedPara.IndentRightPt ?? 0f,
            FirstLineIndentPt = resolvedPara.FirstLineIndentPt ?? 0f,
            PageBreakBefore = resolvedPara.PageBreakBefore ?? false,
            BackgroundColorHex = resolvedPara.BackgroundColorHex,
            HasBottomBorder = resolvedPara.HasBottomBorder ?? false
        };

        // Gestione elenchi puntati / numerati
        if (!string.IsNullOrWhiteSpace(resolvedPara.NumId) && resolvedPara.NumId != "0")
        {
            int ilvl = Math.Clamp(resolvedPara.ListLevel ?? 0, 0, 8);
            para.IsListItem = true;
            para.ListLevel = ilvl;

            var lvlDef = numbering.FindLevel(resolvedPara.NumId, ilvl, out int startOverride);
            if (lvlDef != null)
            {
                if (para.IndentLeftPt <= 0f && lvlDef.IndentLeftPt is > 0f)
                    para.IndentLeftPt = lvlDef.IndentLeftPt.Value;
                if (para.FirstLineIndentPt == 0f && lvlDef.FirstLineIndentPt.HasValue)
                    para.FirstLineIndentPt = lvlDef.FirstLineIndentPt.Value;

                bool isBullet = lvlDef.NumFmt.Equals("bullet", StringComparison.OrdinalIgnoreCase) ||
                                lvlDef.NumFmt.Equals("none", StringComparison.OrdinalIgnoreCase);
                para.IsOrderedList = !isBullet;
                if (isBullet)
                {
                    para.ListMarker = lvlDef.NumFmt.Equals("none", StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : NormalizeBulletSymbol(lvlDef.LvlText, ilvl);
                }
                else
                {
                    var key = (resolvedPara.NumId, ilvl);
                    int nextVal = listCounters.TryGetValue(key, out int prev)
                        ? prev + 1
                        : (startOverride >= 0 ? startOverride : lvlDef.Start);
                    listCounters[key] = nextVal;
                    // Azzera i sottolivelli più profondi della stessa lista.
                    foreach (var deeper in listCounters.Keys.Where(k => k.NumId == resolvedPara.NumId && k.Level > ilvl).ToArray())
                        listCounters.Remove(deeper);

                    string formatted = FormatListCounter(nextVal, lvlDef.NumFmt);
                    string pattern = string.IsNullOrEmpty(lvlDef.LvlText) ? $"%{ilvl + 1}." : lvlDef.LvlText;
                    string marker = pattern;
                    for (int l = 0; l <= 8; l++)
                    {
                        string token = $"%{l + 1}";
                        if (!marker.Contains(token, StringComparison.Ordinal)) continue;
                        string valStr = l == ilvl
                            ? formatted
                            : FormatListCounter(listCounters.GetValueOrDefault((resolvedPara.NumId, l), 1), "decimal");
                        marker = marker.Replace(token, valStr, StringComparison.Ordinal);
                    }
                    para.ListMarker = marker.Trim();
                }
            }
            else
            {
                para.IsOrderedList = false;
                para.ListMarker = ilvl % 3 == 0 ? "•" : ilvl % 3 == 1 ? "◦" : "▪";
            }

            if (para.IndentLeftPt <= 0f)
                para.IndentLeftPt = 24f * (ilvl + 1);
        }

        // Leggi i run del paragrafo gestendo hyperlink, w:ins, w:sdt e saltando w:del / w:instrText
        bool inFieldInstruction = false;
        ParseParagraphInlines(pEl, ns, para.Runs, resolvedRun, styles, majorFont, minorFont, isHyperlink: false, ref inFieldInstruction);

        return para;
    }

    private static void ParseParagraphInlines(
        XElement container,
        XNamespace ns,
        List<RichRun> runs,
        PartialRunStyle paragraphRunDefaults,
        DocxStyleCatalog styles,
        string majorFont,
        string minorFont,
        bool isHyperlink,
        ref bool inFieldInstruction)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name == ns + "pPr" || child.Name == ns + "del" || child.Name == ns + "moveFrom" ||
                child.Name == ns + "bookmarkStart" || child.Name == ns + "bookmarkEnd" || child.Name == ns + "proofErr")
                continue;

            if (child.Name == ns + "r")
            {
                ParseRunElement(child, ns, runs, paragraphRunDefaults, styles, majorFont, minorFont, isHyperlink, ref inFieldInstruction);
            }
            else if (child.Name == ns + "hyperlink")
            {
                ParseParagraphInlines(child, ns, runs, paragraphRunDefaults, styles, majorFont, minorFont, isHyperlink: true, ref inFieldInstruction);
            }
            else if (child.Name == ns + "ins" || child.Name == ns + "smartTag" || child.Name == ns + "fldSimple")
            {
                ParseParagraphInlines(child, ns, runs, paragraphRunDefaults, styles, majorFont, minorFont, isHyperlink, ref inFieldInstruction);
            }
            else if (child.Name == ns + "sdt")
            {
                var content = child.Element(ns + "sdtContent");
                if (content != null)
                    ParseParagraphInlines(content, ns, runs, paragraphRunDefaults, styles, majorFont, minorFont, isHyperlink, ref inFieldInstruction);
            }
        }
    }

    private static void ParseRunElement(
        XElement rEl,
        XNamespace ns,
        List<RichRun> runs,
        PartialRunStyle paragraphRunDefaults,
        DocxStyleCatalog styles,
        string majorFont,
        string minorFont,
        bool isHyperlink,
        ref bool inFieldInstruction)
    {
        var merged = new PartialRunStyle();
        merged.MergeFrom(paragraphRunDefaults);
        if (isHyperlink)
        {
            merged.TextColorHex ??= "0563C1";
            merged.Underline ??= true;
        }

        var rPr = rEl.Element(ns + "rPr");
        if (rPr != null)
        {
            string? rStyleId = (string?)rPr.Element(ns + "rStyle")?.Attribute(ns + "val");
            if (!string.IsNullOrWhiteSpace(rStyleId))
                merged.MergeFrom(styles.ResolveCharacterStyle(rStyleId));
            merged.MergeFrom(ParseRunProperties(rPr, ns, majorFont, minorFont));
        }

        RichRun CreateStyledRun(string text) => new()
        {
            Text = text,
            Bold = merged.Bold ?? false,
            Italic = merged.Italic ?? false,
            Underline = merged.Underline ?? false,
            Strike = merged.Strike ?? false,
            VerticalAlign = merged.VerticalAlign ?? RichVerticalAlign.Baseline,
            FontFamily = string.IsNullOrWhiteSpace(merged.FontFamily) ? minorFont : merged.FontFamily!,
            FontSizePt = Math.Clamp(merged.FontSizePt ?? 11f, 5f, 96f),
            TextColorHex = merged.TextColorHex,
            HighlightColorHex = merged.HighlightColorHex
        };

        foreach (var node in rEl.Elements())
        {
            if (node.Name == ns + "rPr" || node.Name == ns + "instrText" || node.Name == ns + "delText")
                continue;

            if (node.Name == ns + "fldChar")
            {
                string? fType = (string?)node.Attribute(ns + "fldCharType");
                if (string.Equals(fType, "begin", StringComparison.OrdinalIgnoreCase)) inFieldInstruction = true;
                else if (string.Equals(fType, "separate", StringComparison.OrdinalIgnoreCase)) inFieldInstruction = false;
                else if (string.Equals(fType, "end", StringComparison.OrdinalIgnoreCase)) inFieldInstruction = false;
                continue;
            }

            if (inFieldInstruction) continue;

            if (node.Name == ns + "t")
            {
                string value = node.Value;
                if (value.Length > 0)
                    runs.Add(CreateStyledRun(value));
            }
            else if (node.Name == ns + "tab")
            {
                var tabRun = CreateStyledRun("\t");
                tabRun.IsTab = true;
                runs.Add(tabRun);
            }
            else if (node.Name == ns + "br")
            {
                string? brType = (string?)node.Attribute(ns + "type");
                var brRun = CreateStyledRun("");
                if (string.Equals(brType, "page", StringComparison.OrdinalIgnoreCase))
                    brRun.IsPageBreak = true;
                else
                    brRun.IsLineBreak = true;
                runs.Add(brRun);
            }
            else if (node.Name == ns + "cr")
            {
                var crRun = CreateStyledRun("");
                crRun.IsLineBreak = true;
                runs.Add(crRun);
            }
            else if (node.Name == ns + "noBreakHyphen")
            {
                runs.Add(CreateStyledRun("-"));
            }
        }
    }

    private static PartialParagraphStyle ParseParagraphProperties(XElement pPr, XNamespace ns)
    {
        var style = new PartialParagraphStyle();

        var outlineLvl = pPr.Element(ns + "outlineLvl");
        if (outlineLvl != null && int.TryParse((string?)outlineLvl.Attribute(ns + "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lvl) && lvl is >= 0 and <= 5)
            style.HeadingLevel = lvl + 1;

        string? jc = (string?)pPr.Element(ns + "jc")?.Attribute(ns + "val");
        if (!string.IsNullOrWhiteSpace(jc))
        {
            style.Alignment = jc.ToLowerInvariant() switch
            {
                "center" => RichAlignment.Center,
                "right" or "end" => RichAlignment.Right,
                "both" or "distribute" => RichAlignment.Justify,
                _ => RichAlignment.Left
            };
        }

        var spacing = pPr.Element(ns + "spacing");
        if (spacing != null)
        {
            float? before = TryParseTwipsToPt((string?)spacing.Attribute(ns + "before"));
            float? after = TryParseTwipsToPt((string?)spacing.Attribute(ns + "after"));
            if (before is >= 0f and <= 200f) style.SpaceBeforePt = before.Value;
            if (after is >= 0f and <= 200f) style.SpaceAfterPt = after.Value;

            if (float.TryParse((string?)spacing.Attribute(ns + "line"), NumberStyles.Float, CultureInfo.InvariantCulture, out float lineVal) && lineVal > 0)
            {
                string? lineRule = (string?)spacing.Attribute(ns + "lineRule");
                if (string.IsNullOrEmpty(lineRule) || lineRule.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    style.LineSpacingMultiplier = Math.Clamp(lineVal / 240f, 0.9f, 3.0f);
                else
                    style.LineSpacingMultiplier = Math.Clamp((lineVal / 20f) / 13f, 0.9f, 3.0f);
            }
        }

        var ind = pPr.Element(ns + "ind");
        if (ind != null)
        {
            float? left = TryParseTwipsToPt((string?)ind.Attribute(ns + "left") ?? (string?)ind.Attribute(ns + "start"));
            float? right = TryParseTwipsToPt((string?)ind.Attribute(ns + "right") ?? (string?)ind.Attribute(ns + "end"));
            float? firstLine = TryParseTwipsToPt((string?)ind.Attribute(ns + "firstLine"));
            float? hanging = TryParseTwipsToPt((string?)ind.Attribute(ns + "hanging"));
            if (left is >= 0f and <= 360f) style.IndentLeftPt = left.Value;
            if (right is >= 0f and <= 360f) style.IndentRightPt = right.Value;
            if (firstLine is > 0f and <= 240f) style.FirstLineIndentPt = firstLine.Value;
            else if (hanging is > 0f and <= 240f) style.FirstLineIndentPt = -hanging.Value;
        }

        var pageBreak = pPr.Element(ns + "pageBreakBefore");
        if (pageBreak != null)
            style.PageBreakBefore = ReadOpenXmlToggle(pageBreak) ?? true;

        var numPr = pPr.Element(ns + "numPr");
        if (numPr != null)
        {
            string? numId = (string?)numPr.Element(ns + "numId")?.Attribute(ns + "val");
            if (!string.IsNullOrWhiteSpace(numId)) style.NumId = numId;
            if (int.TryParse((string?)numPr.Element(ns + "ilvl")?.Attribute(ns + "val"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ilvl))
                style.ListLevel = ilvl;
        }

        string? shdFill = NormalizeHexColor((string?)pPr.Element(ns + "shd")?.Attribute(ns + "fill"));
        if (shdFill != null && !shdFill.Equals("FFFFFF", StringComparison.OrdinalIgnoreCase))
            style.BackgroundColorHex = shdFill;

        var bottomBorder = pPr.Element(ns + "pBdr")?.Element(ns + "bottom");
        if (bottomBorder != null)
        {
            string? bVal = (string?)bottomBorder.Attribute(ns + "val");
            if (!string.IsNullOrWhiteSpace(bVal) &&
                !bVal.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                !bVal.Equals("nil", StringComparison.OrdinalIgnoreCase))
                style.HasBottomBorder = true;
        }

        return style;
    }

    private static PartialRunStyle ParseRunProperties(XElement rPr, XNamespace ns, string majorFont, string minorFont)
    {
        var style = new PartialRunStyle();

        if (rPr.Element(ns + "b") is { } bEl) style.Bold = ReadOpenXmlToggle(bEl) ?? true;
        if (rPr.Element(ns + "i") is { } iEl) style.Italic = ReadOpenXmlToggle(iEl) ?? true;
        if (rPr.Element(ns + "strike") is { } stEl) style.Strike = ReadOpenXmlToggle(stEl) ?? true;
        else if (rPr.Element(ns + "dstrike") is { } dstEl) style.Strike = ReadOpenXmlToggle(dstEl) ?? true;

        if (rPr.Element(ns + "u") is { } uEl)
        {
            string? uVal = (string?)uEl.Attribute(ns + "val");
            style.Underline = string.IsNullOrWhiteSpace(uVal) ||
                (!uVal.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                 !uVal.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                 uVal != "0");
        }

        string? vert = (string?)rPr.Element(ns + "vertAlign")?.Attribute(ns + "val");
        if (!string.IsNullOrWhiteSpace(vert))
        {
            style.VerticalAlign = vert.ToLowerInvariant() switch
            {
                "superscript" => RichVerticalAlign.Superscript,
                "subscript" => RichVerticalAlign.Subscript,
                _ => RichVerticalAlign.Baseline
            };
        }

        var rFonts = rPr.Element(ns + "rFonts");
        if (rFonts != null)
        {
            string? font = (string?)rFonts.Attribute(ns + "ascii")
                ?? (string?)rFonts.Attribute(ns + "hAnsi")
                ?? (string?)rFonts.Attribute(ns + "cs");
            if (string.IsNullOrWhiteSpace(font))
            {
                string? theme = (string?)rFonts.Attribute(ns + "asciiTheme") ?? (string?)rFonts.Attribute(ns + "hAnsiTheme");
                if (!string.IsNullOrWhiteSpace(theme))
                    font = theme.StartsWith("major", StringComparison.OrdinalIgnoreCase) ? majorFont : minorFont;
            }
            if (!string.IsNullOrWhiteSpace(font))
                style.FontFamily = font.Trim();
        }

        if (float.TryParse((string?)rPr.Element(ns + "sz")?.Attribute(ns + "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out float halfPts) && halfPts > 0)
            style.FontSizePt = Math.Clamp(halfPts / 2f, 5f, 96f);

        string? color = NormalizeHexColor((string?)rPr.Element(ns + "color")?.Attribute(ns + "val"));
        if (color != null)
            style.TextColorHex = color;

        string? highlight = (string?)rPr.Element(ns + "highlight")?.Attribute(ns + "val");
        if (!string.IsNullOrWhiteSpace(highlight) && !highlight.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            string? hlHex = MapWordHighlightColor(highlight);
            if (hlHex != null) style.HighlightColorHex = hlHex;
        }
        else
        {
            string? shdFill = NormalizeHexColor((string?)rPr.Element(ns + "shd")?.Attribute(ns + "fill"));
            if (shdFill != null && !shdFill.Equals("FFFFFF", StringComparison.OrdinalIgnoreCase))
                style.HighlightColorHex = shdFill;
        }

        return style;
    }

    private static int DetectHeadingLevel(string? styleId, string? styleName)
    {
        foreach (string candidate in new[] { styleId ?? "", styleName ?? "" })
        {
            string s = candidate.Trim();
            if (s.Length == 0) continue;
            if (s.Equals("Title", StringComparison.OrdinalIgnoreCase) || s.Equals("Titolo", StringComparison.OrdinalIgnoreCase))
                return -1;
            if (s.Equals("Subtitle", StringComparison.OrdinalIgnoreCase) || s.Equals("Sottotitolo", StringComparison.OrdinalIgnoreCase))
                return -2;
            var m = Regex.Match(s, @"^(?:Heading|Titolo)\s*([1-6])$", RegexOptions.IgnoreCase);
            if (m.Success)
                return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return 0;
    }

    private static void ApplyHeadingDefaultsIfMissing(int headingLevel, PartialParagraphStyle para, PartialRunStyle run)
    {
        switch (headingLevel)
        {
            case -1: // Title
                run.FontSizePt ??= 24f;
                run.Bold ??= true;
                para.SpaceBeforePt ??= 4f;
                para.SpaceAfterPt ??= 10f;
                break;
            case -2: // Subtitle
                run.FontSizePt ??= 14f;
                run.Italic ??= true;
                run.TextColorHex ??= "595959";
                para.SpaceBeforePt ??= 2f;
                para.SpaceAfterPt ??= 8f;
                break;
            case 1:
                run.FontSizePt ??= 20f;
                run.Bold ??= true;
                run.TextColorHex ??= "1F4E79";
                para.SpaceBeforePt ??= 14f;
                para.SpaceAfterPt ??= 6f;
                break;
            case 2:
                run.FontSizePt ??= 16f;
                run.Bold ??= true;
                run.TextColorHex ??= "2E74B5";
                para.SpaceBeforePt ??= 12f;
                para.SpaceAfterPt ??= 4f;
                break;
            case 3:
                run.FontSizePt ??= 13.5f;
                run.Bold ??= true;
                run.TextColorHex ??= "1F4E79";
                para.SpaceBeforePt ??= 10f;
                para.SpaceAfterPt ??= 4f;
                break;
            case 4:
                run.FontSizePt ??= 12f;
                run.Bold ??= true;
                run.Italic ??= true;
                run.TextColorHex ??= "2E74B5";
                para.SpaceBeforePt ??= 8f;
                para.SpaceAfterPt ??= 3f;
                break;
            case 5:
                run.FontSizePt ??= 11.5f;
                run.Bold ??= true;
                run.TextColorHex ??= "2E74B5";
                para.SpaceBeforePt ??= 6f;
                para.SpaceAfterPt ??= 2f;
                break;
            case 6:
                run.FontSizePt ??= 11f;
                run.Bold ??= true;
                run.TextColorHex ??= "595959";
                para.SpaceBeforePt ??= 6f;
                para.SpaceAfterPt ??= 2f;
                break;
        }
    }

    private static bool? ReadOpenXmlToggle(XElement el)
    {
        var attr = el.Attributes().FirstOrDefault(a => a.Name.LocalName == "val");
        if (attr == null) return true;
        string v = attr.Value.Trim();
        if (v.Equals("0", StringComparison.Ordinal) || v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsOpenXmlTruthy(string? val) =>
        !string.IsNullOrWhiteSpace(val) &&
        (val.Equals("1", StringComparison.Ordinal) || val.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static float? TryParseTwipsToPt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float twips)
            ? twips / 20f
            : null;
    }

    private static string? NormalizeHexColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().TrimStart('#');
        if (s.Equals("auto", StringComparison.OrdinalIgnoreCase) || s.Length != 6) return null;
        for (int i = 0; i < 6; i++)
        {
            char c = s[i];
            if (!((c is >= '0' and <= '9') || (c is >= 'a' and <= 'f') || (c is >= 'A' and <= 'F')))
                return null;
        }
        return s.ToUpperInvariant();
    }

    private static string? MapWordHighlightColor(string name) => name.Trim().ToLowerInvariant() switch
    {
        "yellow" => "FFFF00",
        "green" => "00FF00",
        "cyan" => "00FFFF",
        "magenta" => "FF00FF",
        "blue" => "0000FF",
        "red" => "FF0000",
        "darkblue" => "00008B",
        "darkcyan" => "008B8B",
        "darkgreen" => "006400",
        "darkmagenta" => "8B008B",
        "darkred" => "8B0000",
        "darkyellow" => "808000",
        "darkgray" => "A9A9A9",
        "lightgray" => "D3D3D3",
        "black" => "000000",
        "white" => "FFFFFF",
        _ => NormalizeHexColor(name)
    };

    private static string NormalizeBulletSymbol(string lvlText, int level)
    {
        string trimmed = lvlText.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => c >= '\uE000' && c <= '\uF8FF') ||
            trimmed is "o" or "§" or "q" or "v" or "n" or "l")
            return level % 3 == 0 ? "•" : level % 3 == 1 ? "◦" : "▪";
        return trimmed;
    }

    private static string FormatListCounter(int value, string numFmt)
    {
        int n = Math.Max(1, value);
        return numFmt.ToLowerInvariant() switch
        {
            "lowerletter" => ToLetters(n).ToLowerInvariant(),
            "upperletter" => ToLetters(n),
            "lowerroman" => ToRoman(n).ToLowerInvariant(),
            "upperroman" => ToRoman(n),
            _ => n.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string ToLetters(int n)
    {
        var sb = new StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + (n % 26)));
            n /= 26;
        }
        return sb.ToString();
    }

    private static string ToRoman(int n)
    {
        if (n > 3999) return n.ToString(CultureInfo.InvariantCulture);
        (int Val, string Sym)[] map =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];
        var sb = new StringBuilder();
        foreach (var (val, sym) in map)
        {
            while (n >= val) { sb.Append(sym); n -= val; }
        }
        return sb.ToString();
    }

    private static RichDocument ParseMarkdownToRichDocument(string raw)
    {
        var doc = new RichDocument { DefaultFontFamily = "Calibri", DefaultFontSizePt = 11f };
        foreach (string line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                doc.Blocks.Add(new RichParagraph { SpaceAfterPt = 4f });
                continue;
            }

            var para = new RichParagraph();
            string content = trimmed;
            var hm = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
            if (hm.Success)
            {
                int lvl = hm.Groups[1].Value.Length;
                para.HeadingLevel = lvl;
                var pDef = new PartialParagraphStyle();
                var rDef = new PartialRunStyle();
                ApplyHeadingDefaultsIfMissing(lvl, pDef, rDef);
                para.SpaceBeforePt = pDef.SpaceBeforePt ?? 8f;
                para.SpaceAfterPt = pDef.SpaceAfterPt ?? 4f;
                para.Runs.Add(new RichRun
                {
                    Text = hm.Groups[2].Value.Trim(),
                    Bold = rDef.Bold ?? true,
                    Italic = rDef.Italic ?? false,
                    FontFamily = doc.DefaultFontFamily,
                    FontSizePt = rDef.FontSizePt ?? 14f,
                    TextColorHex = rDef.TextColorHex
                });
                doc.Blocks.Add(para);
                continue;
            }

            var bm = Regex.Match(trimmed, @"^(\s*)[-*+]\s+(.*)$");
            var om = Regex.Match(trimmed, @"^(\s*)(\d+[.)])\s+(.*)$");
            if (bm.Success)
            {
                para.IsListItem = true;
                para.IsOrderedList = false;
                para.ListLevel = Math.Min(4, bm.Groups[1].Value.Length / 2);
                para.ListMarker = "•";
                para.IndentLeftPt = 24f * (para.ListLevel + 1);
                content = bm.Groups[2].Value;
            }
            else if (om.Success)
            {
                para.IsListItem = true;
                para.IsOrderedList = true;
                para.ListLevel = Math.Min(4, om.Groups[1].Value.Length / 2);
                para.ListMarker = om.Groups[2].Value;
                para.IndentLeftPt = 24f * (para.ListLevel + 1);
                content = om.Groups[3].Value;
            }

            // Semplice parser inline Markdown (**grassetto**, *corsivo*, `codice`)
            foreach (Match m in Regex.Matches(content, @"\*\*(.+?)\*\*|\*(.+?)\*|`(.+?)`|([^*`]+)"))
            {
                if (m.Groups[1].Success)
                    para.Runs.Add(new RichRun { Text = m.Groups[1].Value, Bold = true, FontFamily = doc.DefaultFontFamily, FontSizePt = doc.DefaultFontSizePt });
                else if (m.Groups[2].Success)
                    para.Runs.Add(new RichRun { Text = m.Groups[2].Value, Italic = true, FontFamily = doc.DefaultFontFamily, FontSizePt = doc.DefaultFontSizePt });
                else if (m.Groups[3].Success)
                    para.Runs.Add(new RichRun { Text = m.Groups[3].Value, FontFamily = "Consolas", FontSizePt = 10f, HighlightColorHex = "F3F4F6" });
                else
                    para.Runs.Add(new RichRun { Text = m.Groups[4].Value, FontFamily = doc.DefaultFontFamily, FontSizePt = doc.DefaultFontSizePt });
            }
            doc.Blocks.Add(para);
        }
        return doc;
    }

    private static RichDocument BuildRichDocumentFromStructuredBlocks(List<(bool IsHeading, string Text)> blocks)
    {
        var doc = new RichDocument { DefaultFontFamily = "Calibri", DefaultFontSizePt = 11f };
        foreach (var (isHeading, text) in blocks)
        {
            var para = new RichParagraph
            {
                HeadingLevel = isHeading ? 2 : 0,
                SpaceBeforePt = isHeading ? 10f : 0f,
                SpaceAfterPt = isHeading ? 4f : 5f
            };
            if (!string.IsNullOrEmpty(text))
            {
                para.Runs.Add(new RichRun
                {
                    Text = text,
                    Bold = isHeading,
                    FontFamily = doc.DefaultFontFamily,
                    FontSizePt = isHeading ? 15f : 11f,
                    TextColorHex = isHeading ? "1F4E79" : null
                });
            }
            doc.Blocks.Add(para);
        }
        return doc;
    }

    internal static void GenerateRichRtf(RichDocument doc, string rtfPath)
    {
        var fonts = new List<string>();
        var fontMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int EnsureFont(string? family)
        {
            string name = string.IsNullOrWhiteSpace(family) ? doc.DefaultFontFamily : family.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Calibri";
            if (fontMap.TryGetValue(name, out int idx)) return idx;
            idx = fonts.Count;
            fonts.Add(name);
            fontMap[name] = idx;
            return idx;
        }

        // Indice 0 nella \colortbl è sempre auto (vuoto ";"); i colori effettivi partono da 1.
        var colors = new List<string>();
        var colorMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int EnsureColor(string? hex)
        {
            string? norm = NormalizeHexColor(hex);
            if (norm == null) return 0;
            if (colorMap.TryGetValue(norm, out int idx)) return idx;
            colors.Add(norm);
            idx = colors.Count; // 1-based
            colorMap[norm] = idx;
            return idx;
        }

        EnsureFont(doc.DefaultFontFamily);
        void CollectParagraphResources(RichParagraph p)
        {
            EnsureColor(p.BackgroundColorHex);
            foreach (var r in p.Runs)
            {
                EnsureFont(r.FontFamily);
                EnsureColor(r.TextColorHex);
                EnsureColor(r.HighlightColorHex);
            }
        }

        foreach (var block in doc.Blocks)
        {
            if (block is RichParagraph p)
                CollectParagraphResources(p);
            else if (block is RichTable t)
            {
                foreach (var row in t.Rows)
                    foreach (var cell in row.Cells)
                    {
                        EnsureColor(cell.ShadingColorHex);
                        foreach (var pCell in cell.Paragraphs)
                            CollectParagraphResources(pCell);
                    }
            }
        }

        var sb = new StringBuilder(4096);
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\nouicompat");

        // Font table
        sb.Append(@"{\fonttbl");
        for (int i = 0; i < fonts.Count; i++)
        {
            sb.Append(@"{\f").Append(i.ToString(CultureInfo.InvariantCulture))
              .Append(@"\fnil\fcharset0 ");
            AppendRtfEscapedText(sb, fonts[i]);
            sb.Append(";}");
        }
        sb.AppendLine("}");

        // Color table
        sb.Append(@"{\colortbl;");
        foreach (string hex in colors)
        {
            int r = Convert.ToInt32(hex[..2], 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\red{r}\green{g}\blue{b};"));
        }
        sb.AppendLine("}");

        // Page geometry in twips
        int paperW = Math.Max(3600, (int)Math.Round(doc.PageWidthPt * 20));
        int paperH = Math.Max(3600, (int)Math.Round(doc.PageHeightPt * 20));
        int margL = Math.Max(240, (int)Math.Round(doc.MarginLeftPt * 20));
        int margR = Math.Max(240, (int)Math.Round(doc.MarginRightPt * 20));
        int margT = Math.Max(240, (int)Math.Round(doc.MarginTopPt * 20));
        int margB = Math.Max(240, (int)Math.Round(doc.MarginBottomPt * 20));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $@"\paperw{paperW}\paperh{paperH}\margl{margL}\margr{margR}\margt{margT}\margb{margB}\viewkind4\uc1"));

        void EmitParagraph(RichParagraph p, bool inTable, bool emitPar)
        {
            sb.Append(inTable ? @"\pard\intbl" : @"\pard");
            if (p.PageBreakBefore && !inTable)
                sb.Append(@"\pagebb");

            sb.Append(p.Alignment switch
            {
                RichAlignment.Center => @"\qc",
                RichAlignment.Right => @"\qr",
                RichAlignment.Justify => @"\qj",
                _ => @"\ql"
            });

            int li = (int)Math.Round(p.IndentLeftPt * 20);
            int ri = (int)Math.Round(p.IndentRightPt * 20);
            int fi = (int)Math.Round(p.FirstLineIndentPt * 20);
            if (p.IsListItem && fi == 0)
            {
                if (li < 360) li = 360 * (p.ListLevel + 1);
                fi = -240;
            }
            if (li != 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\li{li}"));
            if (ri != 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\ri{ri}"));
            if (fi != 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\fi{fi}"));

            int sbTwips = (int)Math.Round(p.SpaceBeforePt * 20);
            int saTwips = (int)Math.Round(p.SpaceAfterPt * 20);
            if (sbTwips > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\sb{sbTwips}"));
            if (saTwips > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\sa{saTwips}"));
            if (Math.Abs(p.LineSpacingMultiplier - 1.0f) > 0.05f)
            {
                int sl = Math.Clamp((int)Math.Round(p.LineSpacingMultiplier * 240), 200, 720);
                sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\sl{sl}\slmult1"));
            }

            if (p.BackgroundColorHex != null)
            {
                int bgIdx = EnsureColor(p.BackgroundColorHex);
                if (bgIdx > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\cbpat{bgIdx}"));
            }
            if (p.HasBottomBorder)
                sb.Append(@"\brdrb\brdrs\brdrw10\brsp20");

            if (p.IsListItem && !string.IsNullOrEmpty(p.ListMarker))
            {
                var firstRun = p.Runs.FirstOrDefault(r => !r.IsLineBreak && !r.IsPageBreak && !r.IsTab);
                int markerFont = EnsureFont(firstRun?.FontFamily ?? doc.DefaultFontFamily);
                int markerHalfPts = (int)Math.Round((firstRun?.FontSizePt ?? doc.DefaultFontSizePt) * 2);
                int markerColor = EnsureColor(firstRun?.TextColorHex);
                sb.Append('{').Append(string.Create(CultureInfo.InvariantCulture, $@"\f{markerFont}\fs{markerHalfPts}"));
                if (markerColor > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\cf{markerColor}"));
                sb.Append(' ');
                AppendRtfEscapedText(sb, p.ListMarker);
                sb.Append(@"\tab }");
            }

            foreach (var run in p.Runs)
            {
                if (run.IsPageBreak)
                {
                    if (!inTable) sb.Append(@"\page ");
                    continue;
                }
                if (run.IsLineBreak)
                {
                    sb.Append(@"\line ");
                    continue;
                }
                if (run.IsTab)
                {
                    sb.Append(@"\tab ");
                    continue;
                }
                if (string.IsNullOrEmpty(run.Text)) continue;

                int fIdx = EnsureFont(run.FontFamily);
                int halfPts = Math.Clamp((int)Math.Round(run.FontSizePt * 2), 10, 192);
                int cfIdx = EnsureColor(run.TextColorHex);
                int hlIdx = EnsureColor(run.HighlightColorHex);

                sb.Append('{');
                sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\f{fIdx}\fs{halfPts}"));
                if (run.Bold) sb.Append(@"\b");
                if (run.Italic) sb.Append(@"\i");
                if (run.Underline) sb.Append(@"\ul");
                if (run.Strike) sb.Append(@"\strike");
                if (run.VerticalAlign == RichVerticalAlign.Superscript) sb.Append(@"\super");
                else if (run.VerticalAlign == RichVerticalAlign.Subscript) sb.Append(@"\sub");
                if (cfIdx > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\cf{cfIdx}"));
                if (hlIdx > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\highlight{hlIdx}"));
                sb.Append(' ');
                AppendRtfEscapedText(sb, run.Text);
                sb.Append('}');
            }

            if (emitPar) sb.AppendLine(@"\par");
        }

        int contentWidthTwips = Math.Max(3600, paperW - margL - margR);
        foreach (var block in doc.Blocks)
        {
            if (block is RichParagraph p)
            {
                EmitParagraph(p, inTable: false, emitPar: true);
            }
            else if (block is RichTable table)
            {
                foreach (var row in table.Rows)
                {
                    sb.Append(@"\trowd\trgaph108\trleft0");
                    if (row.IsHeader) sb.Append(@"\trhdr");

                    int totalUnits = Math.Max(1, row.Cells.Sum(c => Math.Max(1, c.GridSpan)));
                    float[] cellWidths = ComputeTableRowWidthsTwips(table, row, contentWidthTwips, totalUnits);

                    int rightEdge = 0;
                    for (int c = 0; c < row.Cells.Count; c++)
                    {
                        var cell = row.Cells[c];
                        rightEdge += Math.Max(360, (int)Math.Round(cellWidths[c]));
                        sb.Append(@"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10");
                        if (cell.ShadingColorHex != null)
                        {
                            int shadeIdx = EnsureColor(cell.ShadingColorHex);
                            if (shadeIdx > 0) sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\clcbpat{shadeIdx}"));
                        }
                        sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\cellx{rightEdge}"));
                    }
                    sb.AppendLine();

                    foreach (var cell in row.Cells)
                    {
                        for (int pi = 0; pi < cell.Paragraphs.Count; pi++)
                        {
                            bool isLast = pi == cell.Paragraphs.Count - 1;
                            EmitParagraph(cell.Paragraphs[pi], inTable: true, emitPar: !isLast);
                        }
                        sb.AppendLine(@"\cell");
                    }
                    sb.AppendLine(@"\row");
                }
            }
        }

        sb.Append('}');
        File.WriteAllText(rtfPath, sb.ToString(), Encoding.ASCII);
    }

    private static float[] ComputeTableRowWidthsTwips(RichTable table, RichTableRow row, int contentWidthTwips, int totalUnits)
    {
        float[] raw = new float[row.Cells.Count];
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
            raw[i] = w;
            gridCol += span;
        }

        if (!hasExplicit || raw.Sum() <= 0f)
        {
            for (int i = 0; i < row.Cells.Count; i++)
                raw[i] = (float)contentWidthTwips * Math.Max(1, row.Cells[i].GridSpan) / totalUnits;
            return raw;
        }

        float sum = raw.Sum();
        float scale = contentWidthTwips / sum;
        for (int i = 0; i < raw.Length; i++)
            raw[i] = raw[i] > 0f ? raw[i] * scale : (float)contentWidthTwips / row.Cells.Count;
        return raw;
    }

    private static void AppendRtfEscapedText(StringBuilder sb, string text)
    {
        foreach (char c in text)
        {
            if (c == '\\' || c == '{' || c == '}')
            {
                sb.Append('\\').Append(c);
            }
            else if (c == '\t')
            {
                sb.Append(@"\tab ");
            }
            else if (c == '\n')
            {
                sb.Append(@"\line ");
            }
            else if (c == '\r')
            {
                // skip CR
            }
            else if (c >= 0x20 && c <= 0x7E)
            {
                sb.Append(c);
            }
            else if (c < 0x20)
            {
                sb.Append(' ');
            }
            else
            {
                short signed = unchecked((short)c);
                sb.Append(string.Create(CultureInfo.InvariantCulture, $@"\u{signed}?"));
            }
        }
    }
}

internal enum RichAlignment { Left, Center, Right, Justify }

internal enum RichVerticalAlign { Baseline, Superscript, Subscript }

internal sealed class RichDocument
{
    public List<RichBlock> Blocks { get; } = [];
    public string DefaultFontFamily { get; set; } = "Calibri";
    public float DefaultFontSizePt { get; set; } = 11f;
    public float PageWidthPt { get; set; } = 595.28f;
    public float PageHeightPt { get; set; } = 841.89f;
    public float MarginTopPt { get; set; } = 56.7f;
    public float MarginBottomPt { get; set; } = 56.7f;
    public float MarginLeftPt { get; set; } = 56.7f;
    public float MarginRightPt { get; set; } = 56.7f;

    public bool HasAnyContent()
    {
        foreach (var block in Blocks)
        {
            if (block is RichParagraph p)
            {
                if (p.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Text))) return true;
            }
            else if (block is RichTable t)
            {
                foreach (var row in t.Rows)
                    foreach (var cell in row.Cells)
                        if (cell.Paragraphs.Any(cp => cp.Runs.Any(r => !string.IsNullOrWhiteSpace(r.Text))))
                            return true;
            }
        }
        return false;
    }
}

internal abstract class RichBlock;

internal sealed class RichParagraph : RichBlock
{
    public List<RichRun> Runs { get; } = [];
    public int HeadingLevel { get; set; }
    public RichAlignment Alignment { get; set; } = RichAlignment.Left;
    public float SpaceBeforePt { get; set; }
    public float SpaceAfterPt { get; set; } = 6f;
    public float LineSpacingMultiplier { get; set; } = 1.15f;
    public float IndentLeftPt { get; set; }
    public float IndentRightPt { get; set; }
    public float FirstLineIndentPt { get; set; }
    public bool PageBreakBefore { get; set; }
    public bool IsListItem { get; set; }
    public bool IsOrderedList { get; set; }
    public int ListLevel { get; set; }
    public string? ListMarker { get; set; }
    public string? BackgroundColorHex { get; set; }
    public bool HasBottomBorder { get; set; }
}

internal sealed class RichRun
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public RichVerticalAlign VerticalAlign { get; set; } = RichVerticalAlign.Baseline;
    public string FontFamily { get; set; } = "Calibri";
    public float FontSizePt { get; set; } = 11f;
    public string? TextColorHex { get; set; }
    public string? HighlightColorHex { get; set; }
    public bool IsPageBreak { get; set; }
    public bool IsLineBreak { get; set; }
    public bool IsTab { get; set; }
}

internal sealed class RichTable : RichBlock
{
    public List<RichTableRow> Rows { get; } = [];
    public List<float> ColumnWidthTwips { get; } = [];
}

internal sealed class RichTableRow
{
    public List<RichTableCell> Cells { get; } = [];
    public bool IsHeader { get; set; }
}

internal sealed class RichTableCell
{
    public List<RichParagraph> Paragraphs { get; } = [];
    public string? ShadingColorHex { get; set; }
    public int GridSpan { get; set; } = 1;
    public float WidthTwips { get; set; }
}

