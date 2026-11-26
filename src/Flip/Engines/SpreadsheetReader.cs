using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Flip.Core;

namespace Flip.Engines;

internal enum SheetAlign { General, Left, Center, Right }

/// <summary>Cella letta: testo come lo mostra Excel e testo neutro per CSV, JSON e simili.</summary>
internal sealed record SheetCell(string Display, string Raw, bool IsNumber = false, bool Bold = false,
    string? Fill = null, string? Color = null, SheetAlign Align = SheetAlign.General);

internal sealed class Sheet
{
    internal string Name { get; init; } = "";
    internal SortedDictionary<int, SortedDictionary<int, SheetCell>> Rows { get; } = [];
    /// <summary>Larghezza in caratteri delle colonne impostate dall'utente.</summary>
    internal Dictionary<int, double> Widths { get; } = [];
    /// <summary>Cella iniziale e numero di colonne e righe unite.</summary>
    internal Dictionary<(int Row, int Column), (int Columns, int Rows)> Merges { get; } = [];
    internal bool IsEmpty => Rows.Values.All(row => row.Values.All(cell => string.IsNullOrWhiteSpace(cell.Display)));

    internal void Set(int row, int column, SheetCell cell)
    {
        if (row is < 0 or >= 1_048_576 || column is < 0 or >= 16_384) throw new IOException($"Cella fuori dai limiti di Excel: riga {row + 1}, colonna {column + 1}.");
        if (!Rows.TryGetValue(row, out var cells)) Rows[row] = cells = [];
        cells[column] = cell;
    }

    /// <summary>Tabella rettangolare senza righe vuote in coda.</summary>
    internal List<List<string>> ToRows()
    {
        var result = new List<List<string>>();
        if (Rows.Count == 0) return result;
        int last = Rows.Where(row => row.Value.Values.Any(cell => cell.Raw.Length > 0)).Select(row => row.Key).DefaultIfEmpty(-1).Max();
        for (int r = 0; r <= last; r++)
        {
            var line = new List<string>();
            if (Rows.TryGetValue(r, out var cells))
            {
                int width = cells.Where(cell => cell.Value.Raw.Length > 0).Select(cell => cell.Key + 1).DefaultIfEmpty(0).Max();
                for (int c = 0; c < width; c++) line.Add(cells.TryGetValue(c, out var cell) ? cell.Raw : "");
            }
            result.Add(line);
        }
        return result;
    }
}

/// <summary>
/// Legge XLSX, XLSM, XLTX, XLS, ODS, XML di Excel 2003 e le tabelle HTML o di testo salvate con estensione .xls,
/// senza Office né LibreOffice. Riconosce il contenuto e non si fida dell'estensione. Le macro non vengono mai eseguite.
/// </summary>
internal static class SpreadsheetReader
{
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxCells = 2_000_000;

    internal static List<Sheet> Read(string path)
    {
        byte[] head = new byte[8];
        using (var stream = File.OpenRead(path)) _ = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        List<Sheet> sheets;
        if (head[0] == 'P' && head[1] == 'K') sheets = ReadZip(path);
        else if (head.AsSpan().SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) sheets = XlsReader.Read(File.ReadAllBytes(path));
        else sheets = ReadText(path);

        if (sheets.Count == 0 || sheets.All(sheet => sheet.IsEmpty))
            throw new IOException(Loc.T("Il foglio di calcolo non contiene dati.", "The spreadsheet contains no data."));
        return sheets;
    }

    private static List<Sheet> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 8192 || zip.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
            throw new IOException(Loc.T("Foglio di calcolo troppo grande.", "The spreadsheet is too large."));
        if (zip.GetEntry("xl/workbook.xml") != null) return ReadOpenXml(zip);
        if (zip.GetEntry("xl/workbook.bin") != null)
            throw new IOException(Loc.T("I file .xlsb richiedono LibreOffice: installalo da Motori & GPU oppure salva il file come .xlsx.", ".xlsb files need LibreOffice: install it from Engines & GPU or save the file as .xlsx."));
        if (zip.GetEntry("content.xml") != null) return ReadOds(zip);
        throw new IOException(Loc.T("Il file non è un foglio di calcolo leggibile.", "The file is not a readable spreadsheet."));
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    // Excel salva in due varianti, con spazi dei nomi diversi: contano i nomi locali.
    private static IEnumerable<XElement> Children(XElement? parent, string name) => parent?.Elements().Where(element => element.Name.LocalName == name) ?? [];
    private static XElement? Child(XElement? parent, string name) => Children(parent, name).FirstOrDefault();
    private static string? Attribute(XElement? element, string name) => element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    // ── XLSX / XLSM / XLTX ────────────────────────────────────────────────────

    private sealed record CellStyle(int FormatId, string? FormatCode, bool Bold, string? Color, string? Fill, SheetAlign Align);

    private static List<Sheet> ReadOpenXml(ZipArchive zip)
    {
        var workbook = Load(zip, "xl/workbook.xml")!.Root;
        bool date1904 = Attribute(Child(workbook, "workbookPr"), "date1904") is "1" or "true";
        var targets = Children(Load(zip, "xl/_rels/workbook.xml.rels")?.Root, "Relationship")
            .Where(relation => Attribute(relation, "Id") != null && Attribute(relation, "Target") != null)
            .ToDictionary(relation => Attribute(relation, "Id")!, relation => Attribute(relation, "Target")!);

        var strings = new List<string>();
        string sharedName = targets.Values.Select(Normalize).FirstOrDefault(name => name.EndsWith("sharedStrings.xml", StringComparison.OrdinalIgnoreCase)) ?? "xl/sharedStrings.xml";
        foreach (var item in Children(Load(zip, sharedName)?.Root, "si")) strings.Add(RichText(item));
        var styles = ReadStyles(zip);

        var sheets = new List<Sheet>();
        int cells = 0;
        foreach (var info in Children(Child(workbook, "sheets"), "sheet"))
        {
            // I fogli nascosti sono dati di servizio: Excel non li stampa.
            if (Attribute(info, "state") is "hidden" or "veryHidden") continue;
            string? id = Attribute(info, "id");
            string name = id != null && targets.TryGetValue(id, out string? target) ? Normalize(target) : "";
            if (Load(zip, name)?.Root is not { } root || root.Name.LocalName != "worksheet") continue;
            var sheet = new Sheet { Name = Attribute(info, "name") ?? "" };

            foreach (var column in Children(Child(root, "cols"), "col"))
            {
                if (!int.TryParse(Attribute(column, "min"), out int from) || !int.TryParse(Attribute(column, "max"), out int to)) continue;
                double width = Attribute(column, "hidden") is "1" or "true" ? 0 :
                    double.TryParse(Attribute(column, "width"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : -1;
                for (int c = Math.Max(1, from); c <= Math.Min(to, 16_384) && width >= 0; c++) sheet.Widths[c - 1] = width;
            }

            int nextRow = 0;
            foreach (var row in Children(Child(root, "sheetData"), "row"))
            {
                int rowIndex = int.TryParse(Attribute(row, "r"), out int number) && number > 0 ? number - 1 : nextRow;
                nextRow = rowIndex + 1;
                if (Attribute(row, "hidden") is "1" or "true") continue;
                int nextColumn = 0;
                foreach (var cell in Children(row, "c"))
                {
                    int column = ColumnIndex(Attribute(cell, "r")) ?? nextColumn;
                    nextColumn = column + 1;
                    if (++cells > MaxCells) throw new IOException(Loc.T("Il foglio di calcolo supera due milioni di celle.", "The spreadsheet exceeds two million cells."));
                    var style = int.TryParse(Attribute(cell, "s"), out int index) && index >= 0 && index < styles.Count ? styles[index] : null;
                    if (ReadCell(cell, strings, style, date1904) is { } value) sheet.Set(rowIndex, column, value);
                }
            }

            foreach (var merge in Children(Child(root, "mergeCells"), "mergeCell"))
            {
                var match = Regex.Match(Attribute(merge, "ref") ?? "", @"^([A-Z]{1,3})(\d+):([A-Z]{1,3})(\d+)$");
                if (!match.Success || ColumnIndex(match.Groups[1].Value) is not int left || ColumnIndex(match.Groups[3].Value) is not int right) continue;
                int top = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) - 1, bottom = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) - 1;
                if (right >= left && bottom >= top) sheet.Merges[(top, left)] = (right - left + 1, bottom - top + 1);
            }
            sheets.Add(sheet);
        }
        return sheets;

        static string Normalize(string target) => target.StartsWith('/') ? target[1..] : "xl/" + target;
    }

    private static string RichText(XElement item) => string.Concat(item.Descendants()
        .Where(element => element.Name.LocalName == "t" && element.Parent?.Name.LocalName != "rPh").Select(element => element.Value));

    private static SheetCell? ReadCell(XElement cell, List<string> strings, CellStyle? style, bool date1904)
    {
        string raw = Child(cell, "v")?.Value ?? "";
        string? type = Attribute(cell, "t");
        string display, neutral;
        bool number = false;
        switch (type)
        {
            case "s":
                if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= strings.Count)
                    throw new IOException("Indice di sharedStrings non valido.");
                display = neutral = strings[index];
                break;
            case "inlineStr":
                display = neutral = Child(cell, "is") is { } inline ? RichText(inline) : "";
                break;
            case "b":
                display = neutral = raw == "1" ? "TRUE" : raw.Length == 0 ? "" : "FALSE";
                if (raw.Length > 0) display = raw == "1" ? Loc.T("VERO", "TRUE") : Loc.T("FALSO", "FALSE");
                break;
            case "str" or "e":
                display = neutral = raw;
                break;
            case "d":
                display = neutral = raw;
                break;
            default:
                number = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
                display = number ? ExcelFormat.Display(value, style?.FormatId ?? 0, style?.FormatCode, date1904) : raw;
                neutral = number ? ExcelFormat.Neutral(value, raw, style?.FormatId ?? 0, style?.FormatCode, date1904) : raw;
                break;
        }
        // Una cella senza testo conta solo se è colorata.
        if (display.Length == 0 && style?.Fill == null) return null;
        return new SheetCell(Clean(display), Clean(neutral), number, style?.Bold == true, style?.Fill, style?.Color, style?.Align ?? SheetAlign.General);
    }

    internal static string Clean(string text) => text.Replace("_x000D_", "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static List<CellStyle> ReadStyles(ZipArchive zip)
    {
        var result = new List<CellStyle>();
        if (Load(zip, "xl/styles.xml")?.Root is not { } root) return result;
        var formats = new Dictionary<int, string>();
        foreach (var format in Children(Child(root, "numFmts"), "numFmt"))
            if (int.TryParse(Attribute(format, "numFmtId"), out int id)) formats[id] = Attribute(format, "formatCode") ?? "";
        var fonts = Children(Child(root, "fonts"), "font").Select(font =>
            (Bold: Child(font, "b") is { } bold && Attribute(bold, "val") is not ("0" or "false"), Color: Rgb(Child(font, "color")))).ToList();
        var fills = Children(Child(root, "fills"), "fill").Select(fill => Child(fill, "patternFill") is { } pattern &&
            Attribute(pattern, "patternType") == "solid" ? Rgb(Child(pattern, "fgColor")) : null).ToList();

        foreach (var xf in Children(Child(root, "cellXfs"), "xf"))
        {
            int format = Index("numFmtId"), font = Index("fontId"), fill = Index("fillId");
            var align = Attribute(Child(xf, "alignment"), "horizontal") switch
            {
                "left" => SheetAlign.Left, "center" or "centerContinuous" => SheetAlign.Center, "right" => SheetAlign.Right, _ => SheetAlign.General
            };
            result.Add(new CellStyle(format, formats.GetValueOrDefault(format), font < fonts.Count && fonts[font].Bold,
                font < fonts.Count ? fonts[font].Color : null, fill < fills.Count ? fills[fill] : null, align));

            int Index(string name) => int.TryParse(Attribute(xf, name), out int value) && value >= 0 ? value : 0;
        }
        return result;

        static string? Rgb(XElement? color) => Attribute(color, "rgb") is { Length: 8 } argb ? argb[2..] : null;
    }

    internal static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        int value = 0, letters = 0;
        foreach (char ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            value = value * 26 + (ch - 'A' + 1);
            if (++letters > 3) return null;
        }
        return letters == 0 ? null : value - 1;
    }

    // ── ODS ───────────────────────────────────────────────────────────────────

    private static List<Sheet> ReadOds(ZipArchive zip)
    {
        var root = Load(zip, "content.xml")!.Root;
        if (Load(zip, "META-INF/manifest.xml")?.Descendants().Any(element => element.Name.LocalName == "encryption-data") == true)
            throw new IOException(Loc.T("Il foglio di calcolo è protetto da password.", "The spreadsheet is password protected."));
        var sheets = new List<Sheet>();
        int total = 0;
        foreach (var table in Children(Child(Child(root, "body"), "spreadsheet"), "table"))
        {
            var sheet = new Sheet { Name = Attribute(table, "name") ?? "" };
            int rowIndex = 0;
            foreach (var row in table.Descendants().Where(element => element.Name.LocalName == "table-row"))
            {
                int repeatRows = Repeat(row, "number-rows-repeated");
                int column = 0;
                var values = new List<(int Column, SheetCell Cell)>();
                foreach (var cell in row.Elements().Where(element => element.Name.LocalName is "table-cell" or "covered-table-cell"))
                {
                    int repeat = Repeat(cell, "number-columns-repeated");
                    string text = string.Join('\n', Children(cell, "p").Select(OdsText));
                    if (text.Length > 0)
                    {
                        string? type = Attribute(cell, "value-type");
                        string neutral = type switch
                        {
                            "float" or "percentage" or "currency" => Attribute(cell, "value") ?? text,
                            "date" => (Attribute(cell, "date-value") ?? text).Replace('T', ' '),
                            "boolean" => Attribute(cell, "boolean-value") == "true" ? "TRUE" : "FALSE",
                            _ => text
                        };
                        // Migliaia di celle vuote ripetute sono normali; migliaia uguali e piene no.
                        for (int i = 0; i < Math.Min(repeat, 1024); i++)
                            values.Add((column + i, new SheetCell(text, neutral, type is "float" or "percentage" or "currency")));
                        int columns = Repeat(cell, "number-columns-spanned"), rows = Repeat(cell, "number-rows-spanned");
                        if (columns > 1 || rows > 1) sheet.Merges[(rowIndex, column)] = (columns, rows);
                    }
                    column += repeat;
                }
                if (values.Count > 0)
                    for (int i = 0; i < Math.Min(repeatRows, 65_536); i++)
                    {
                        if ((total += values.Count) > MaxCells) throw new IOException(Loc.T("Il foglio di calcolo supera due milioni di celle.", "The spreadsheet exceeds two million cells."));
                        foreach (var (c, cell) in values) sheet.Set(rowIndex + i, c, cell);
                    }
                rowIndex += repeatRows;
            }
            sheets.Add(sheet);
        }
        return sheets;

        static int Repeat(XElement element, string name) => int.TryParse(Attribute(element, name), out int value) && value is > 0 and <= 1_048_576 ? value : 1;

        static string OdsText(XElement paragraph)
        {
            var text = new StringBuilder();
            foreach (var node in paragraph.DescendantNodes())
            {
                if (node is XText run) text.Append(run.Value);
                else if (node is XElement { Name.LocalName: "line-break" }) text.Append('\n');
                else if (node is XElement { Name.LocalName: "tab" }) text.Append('\t');
                else if (node is XElement { Name.LocalName: "s" } space) text.Append(' ', Math.Min(1000, Repeat(space, "c")));
            }
            return text.ToString();
        }
    }

    // ── File di testo salvati come .xls ───────────────────────────────────────

    private static List<Sheet> ReadText(string path)
    {
        string text = TextFiles.Read(path);
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        string start = extension is "csv" or "tsv" ? "" : text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (start.Contains("urn:schemas-microsoft-com:office:spreadsheet", StringComparison.Ordinal) && start.StartsWith('<'))
            return ReadXml2003(start);
        if (start.StartsWith('<')) return ReadHtml(start);
        var sheet = new Sheet();
        var rows = TextFiles.ReadTable(text, extension);
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < rows[r].Count; c++)
                if (rows[r][c].Length > 0) sheet.Set(r, c, Text(rows[r][c]));
        return [sheet];
    }

    private static SheetCell Text(string value, bool bold = false) =>
        new(Clean(value), Clean(value), Regex.IsMatch(value.Trim(), @"^-?(?:\d{1,3}(?:[.,\s]\d{3})*|\d+)(?:[.,]\d+)?\s*[€$£%]?$") && !Regex.IsMatch(value, @"^0\d"), bold);

    private static List<Sheet> ReadXml2003(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var sheets = new List<Sheet>();
        foreach (var worksheet in Children(XDocument.Load(reader).Root, "Worksheet"))
        {
            var sheet = new Sheet { Name = Attribute(worksheet, "Name") ?? "" };
            int rowIndex = 0;
            foreach (var row in Children(Child(worksheet, "Table"), "Row"))
            {
                if (int.TryParse(Attribute(row, "Index"), out int explicitRow)) rowIndex = explicitRow - 1;
                int column = 0;
                foreach (var cell in Children(row, "Cell"))
                {
                    if (int.TryParse(Attribute(cell, "Index"), out int explicitColumn)) column = explicitColumn - 1;
                    if (Child(cell, "Data") is { } data)
                    {
                        string value = data.Value;
                        bool number = Attribute(data, "Type") == "Number";
                        if (number && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                            sheet.Set(rowIndex, column, new SheetCell(ExcelFormat.Display(parsed, 0, null, false), value, true));
                        else if (Attribute(data, "Type") == "DateTime" && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                            sheet.Set(rowIndex, column, new SheetCell(date.ToString(date.TimeOfDay == TimeSpan.Zero ? "d" : "g", CultureInfo.CurrentCulture), value));
                        else if (value.Length > 0) sheet.Set(rowIndex, column, new SheetCell(Clean(value), Clean(value)));
                    }
                    column += 1 + (int.TryParse(Attribute(cell, "MergeAcross"), out int across) ? Math.Max(0, across) : 0);
                }
                rowIndex++;
            }
            sheets.Add(sheet);
        }
        return sheets;
    }

    private static List<Sheet> ReadHtml(string text)
    {
        var sheet = new Sheet();
        int row = 0;
        foreach (Match line in Regex.Matches(text, @"<tr\b[^>]*>(.*?)(?=<tr\b|</tr>|</table>)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            int column = 0;
            foreach (Match cell in Regex.Matches(line.Groups[1].Value, @"<(t[dh])\b([^>]*)>(.*?)(?=<t[dh]\b|</t[dh]>|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string value = Regex.Replace(cell.Groups[3].Value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                value = System.Net.WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", "")).Replace(' ', ' ').Trim();
                if (value.Length > 0) sheet.Set(row, column, Text(value, cell.Groups[1].Value.Equals("th", StringComparison.OrdinalIgnoreCase)));
                var span = Regex.Match(cell.Groups[2].Value, @"colspan\s*=\s*[""']?(\d{1,3})", RegexOptions.IgnoreCase);
                int columns = span.Success ? Math.Max(1, int.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture)) : 1;
                if (columns > 1) sheet.Merges[(row, column)] = (columns, 1);
                column += columns;
            }
            row++;
        }
        return [sheet];
    }
}

/// <summary>Formati numerici di Excel: abbastanza per mostrare importi, percentuali, date e ore come nel foglio.</summary>
internal static class ExcelFormat
{
    private static readonly Dictionary<int, string> BuiltIn = new()
    {
        [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [5] = "#,##0", [6] = "#,##0", [7] = "#,##0.00", [8] = "#,##0.00", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00",
        [37] = "#,##0", [38] = "#,##0", [39] = "#,##0.00", [40] = "#,##0.00", [44] = "#,##0.00", [43] = "#,##0.00", [41] = "#,##0", [42] = "#,##0",
        [14] = "\u0001d", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "\u0001g",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mm:ss", [49] = "@"
    };

    internal static bool IsDate(int id, string? code) => code != null ? OfficeOpenXmlEngine.IsDateFormat(code) :
        id is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58;

    internal static DateTime? ToDate(double value, bool date1904)
    {
        if (value is < 0 or >= 2_950_000) return null;
        // Excel considera bisestile il 1900: prima del 1° marzo la serie è spostata di un giorno.
        double days = date1904 || value >= 60 ? value : value + 1;
        return (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30)).AddSeconds(Math.Round(days * 86400));
    }

    internal static string Neutral(double value, string raw, int id, string? code, bool date1904)
    {
        if (IsDate(id, code) && ToDate(value, date1904) is { } date)
            return value < 1 ? date.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return raw.Contains('.') && raw.Count(char.IsAsciiDigit) > 15 && Math.Abs(value) is >= 1e-4 and < 1e15
            ? value.ToString("G15", CultureInfo.InvariantCulture) : raw;
    }

    internal static string Display(double value, int id, string? code, bool date1904)
    {
        string? format = code ?? BuiltIn.GetValueOrDefault(id);
        if (string.IsNullOrEmpty(format) || format.Equals("General", StringComparison.OrdinalIgnoreCase) || format == "@")
            return General(value);
        if (IsDate(id, code)) return ToDate(value, date1904) is { } date ? FormatDate(date, value, format) : General(value);

        string[] sections = SplitSections(format);
        string section = sections[0];
        double number = value;
        bool literalSign = false;
        if (value < 0 && sections.Length > 1) { section = sections[1]; number = -value; literalSign = true; }
        else if (value == 0 && sections.Length > 2) section = sections[2];
        if (section.Contains("General", StringComparison.OrdinalIgnoreCase)) return General(value);
        return FormatNumber(number, section, literalSign);
    }

    private static string General(double value) => Math.Abs(value) is >= 1e15 or (> 0 and < 1e-9)
        ? value.ToString("0.#####E+00", CultureInfo.CurrentCulture) : Math.Round(value, 10).ToString("0.##########", CultureInfo.CurrentCulture);

    private static string[] SplitSections(string format)
    {
        var sections = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < format.Length; i++)
        {
            char ch = format[i];
            if (ch == '"') quoted = !quoted;
            if (ch == '\\' && i + 1 < format.Length) { current.Append(ch).Append(format[++i]); continue; }
            if (ch == ';' && !quoted) { sections.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        sections.Add(current.ToString());
        return [.. sections];
    }

    private static string FormatNumber(double value, string section, bool unsigned)
    {
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        var pattern = new StringBuilder();
        int percent = 0;
        for (int i = 0; i < section.Length; i++)
        {
            char ch = section[i];
            var literal = pattern.Length == 0 ? prefix : suffix;
            switch (ch)
            {
                case '"':
                    int end = section.IndexOf('"', i + 1);
                    if (end < 0) end = section.Length;
                    literal.Append(section, i + 1, Math.Max(0, end - i - 1));
                    i = end;
                    break;
                case '\\' when i + 1 < section.Length: literal.Append(section[++i]); break;
                case '_' when i + 1 < section.Length: i++; break;
                case '*' when i + 1 < section.Length: i++; break;
                case '[':
                    int close = section.IndexOf(']', i);
                    if (close < 0) { literal.Append(ch); break; }
                    // [$€-410] è una valuta; [Red] e [>100] non sono testo.
                    if (section[i + 1] == '$') literal.Append(section[(i + 2)..close].Split('-')[0]);
                    i = close;
                    break;
                case '0' or '#' or '?' or '.' or ',': pattern.Append(ch == '?' ? '#' : ch); break;
                case 'E' or 'e' when i + 1 < section.Length && section[i + 1] is '+' or '-' && pattern.Length > 0:
                    pattern.Append("E+");
                    i++;
                    break;
                case '/' when pattern.Length > 0: return General(unsigned ? -value : value);
                case '%': percent++; literal.Append('%'); break;
                case '@': break;
                default: literal.Append(ch); break;
            }
        }
        string digits = pattern.ToString();
        if (digits.Length == 0) return (prefix.ToString() + suffix).Trim();
        for (int i = 0; i < percent; i++) value *= 100;
        // Le virgole finali dividono per mille.
        while (digits.EndsWith(',')) { value /= 1000; digits = digits[..^1]; }
        if (digits.Contains("E+", StringComparison.Ordinal))
            return prefix + value.ToString(digits.Replace(",", ""), CultureInfo.CurrentCulture) + suffix;
        int point = digits.IndexOf('.');
        string fraction = point < 0 ? "" : digits[(point + 1)..].Replace(",", "").Replace(".", "");
        string whole = point < 0 ? digits : digits[..point];
        bool grouping = whole.Contains(',');
        int zeros = Math.Clamp(whole.Count(ch => ch == '0'), 0, 20);
        string net = (grouping ? "#,##" + new string('0', Math.Max(zeros, 1)) : zeros == 0 ? "#" : new string('0', zeros)) + (fraction.Length > 0 ? "." + fraction : "");
        string text = Math.Abs(value) < 1e28 ? Math.Round((decimal)value, Math.Min(fraction.Length, 28), MidpointRounding.AwayFromZero).ToString(net, CultureInfo.CurrentCulture) : General(value);
        if (text.Length == 0) text = "0";
        // -0,00 dopo l'arrotondamento
        if (text.StartsWith('-') && !text.Any(ch => ch is >= '1' and <= '9')) text = text[1..];
        return (prefix + text + suffix).Trim();
    }

    private static string FormatDate(DateTime date, double value, string format)
    {
        if (format == "\u0001d") return date.ToString("d", CultureInfo.CurrentCulture);
        if (format == "\u0001g") return date.ToString("g", CultureInfo.CurrentCulture);
        string section = SplitSections(format)[0];
        bool twelve = Regex.IsMatch(section, "AM/PM|A/P", RegexOptions.IgnoreCase);
        var output = new StringBuilder();
        bool afterHour = false;
        for (int i = 0; i < section.Length; i++)
        {
            char ch = char.ToLowerInvariant(section[i]);
            if (ch == '"')
            {
                int end = section.IndexOf('"', i + 1);
                if (end < 0) end = section.Length;
                output.Append(section, i + 1, Math.Max(0, end - i - 1));
                i = end;
            }
            else if (ch == '\\' && i + 1 < section.Length) output.Append(section[++i]);
            else if (ch is '_' or '*' && i + 1 < section.Length) i++;
            else if (ch == '[')
            {
                int close = section.IndexOf(']', i);
                if (close < 0) break;
                string inside = section[(i + 1)..close].ToLowerInvariant();
                if (inside.All(letter => letter == 'h') && inside.Length > 0) { output.Append(((long)Math.Floor(value * 24 + 1e-9)).ToString(new string('0', inside.Length), CultureInfo.InvariantCulture)); afterHour = true; }
                else if (inside.Length > 0 && inside.All(letter => letter == 'm')) output.Append(((long)Math.Floor(value * 1440 + 1e-9)).ToString(CultureInfo.InvariantCulture));
                else if (inside.Length > 0 && inside.All(letter => letter == 's')) output.Append(((long)Math.Round(value * 86400)).ToString(CultureInfo.InvariantCulture));
                i = close;
            }
            else if (ch == 'a' && twelve && section.AsSpan(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase)) { output.Append(date.ToString("tt", CultureInfo.InvariantCulture)); i += 4; }
            else if (ch == 'a' && twelve) { output.Append(date.ToString("tt", CultureInfo.InvariantCulture)[0]); i += 2; }
            else if (ch is 'd' or 'm' or 'y' or 'h' or 's')
            {
                int run = 1;
                while (i + run < section.Length && char.ToLowerInvariant(section[i + run]) == ch) run++;
                // m vale minuti dopo le ore o prima dei secondi, altrimenti mese.
                bool minutes = ch == 'm' && run <= 2 && (afterHour || Regex.IsMatch(section[(i + run)..], @"^[:.\s]*s", RegexOptions.IgnoreCase));
                output.Append(ch switch
                {
                    'd' => date.ToString(run switch { 1 => "%d", 2 => "dd", 3 => "ddd", _ => "dddd" }, CultureInfo.CurrentCulture),
                    'y' => date.ToString(run <= 2 ? "yy" : "yyyy", CultureInfo.CurrentCulture),
                    'h' => date.ToString((twelve ? "h" : "H") is var hour && run == 1 ? "%" + hour : hour + hour, CultureInfo.CurrentCulture),
                    's' => date.ToString(run == 1 ? "%s" : "ss", CultureInfo.CurrentCulture),
                    _ => minutes ? date.ToString(run == 1 ? "%m" : "mm", CultureInfo.CurrentCulture)
                        : date.ToString(run switch { 1 => "%M", 2 => "MM", 3 => "MMM", _ => "MMMM" }, CultureInfo.CurrentCulture)
                });
                if (ch == 'h') afterHour = true;
                else if (ch is 'm' or 's') afterHour = false;
                i += run - 1;
            }
            else if (section[i] is not ('@' or '0' or '#')) output.Append(section[i]);
        }
        return output.ToString().Trim();
    }
}

/// <summary>Excel 97-2003 (BIFF8 in un contenitore OLE). Legge solo valori e formati: nessuna macro o formula viene eseguita.</summary>
internal static class XlsReader
{
    internal static List<Sheet> Read(byte[] file)
    {
        var streams = ReadCompoundFile(file);
        if (streams.ContainsKey("EncryptedPackage"))
            throw new IOException(Loc.T("Il foglio di calcolo è protetto da password.", "The spreadsheet is password protected."));
        if (!streams.TryGetValue("Workbook", out byte[]? data))
            throw new IOException(streams.ContainsKey("Book")
                ? Loc.T("File Excel 5.0/95: aprilo in Excel o LibreOffice e salvalo come .xlsx.", "Excel 5.0/95 file: open it in Excel or LibreOffice and save it as .xlsx.")
                : Loc.T("Il file non è un foglio di calcolo leggibile.", "The file is not a readable spreadsheet."));
        try { return ReadWorkbook(data); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or ArgumentException)
        {
            throw new IOException(Loc.T("Il file .xls è danneggiato.", "The .xls file is damaged."), ex);
        }
    }

    // ── Contenitore OLE ───────────────────────────────────────────────────────

    private static Dictionary<string, byte[]> ReadCompoundFile(byte[] file)
    {
        try
        {
            int sectorShift = BitConverter.ToUInt16(file, 30), miniShift = BitConverter.ToUInt16(file, 32);
            if (sectorShift is < 9 or > 12 || miniShift is < 4 or > 8) throw new IOException("Contenitore OLE non valido.");
            int sectorSize = 1 << sectorShift, miniSize = 1 << miniShift;
            uint directoryStart = BitConverter.ToUInt32(file, 48), cutoff = BitConverter.ToUInt32(file, 56), miniFatStart = BitConverter.ToUInt32(file, 60), difatStart = BitConverter.ToUInt32(file, 68);

            ReadOnlySpan<byte> Sector(uint id)
            {
                long offset = ((long)id + 1) << sectorShift;
                if (offset >= file.Length) throw new IOException("Settore OLE fuori dal file.");
                // L'ultimo settore può essere troncato.
                return file.AsSpan((int)offset, (int)Math.Min(sectorSize, file.Length - offset));
            }

            var fatSectors = new List<uint>();
            for (int i = 0; i < 109; i++) Add(BitConverter.ToUInt32(file, 76 + i * 4));
            for (int guard = 0; difatStart < 0xFFFFFFFA && guard < 100_000; guard++)
            {
                byte[] block = Sector(difatStart).ToArray();
                if (block.Length < sectorSize) break;
                for (int i = 0; i < sectorSize / 4 - 1; i++) Add(BitConverter.ToUInt32(block, i * 4));
                difatStart = BitConverter.ToUInt32(block, sectorSize - 4);
            }
            void Add(uint id) { if (id < 0xFFFFFFFA) fatSectors.Add(id); }

            var fat = new List<uint>();
            foreach (uint id in fatSectors)
            {
                byte[] block = Sector(id).ToArray();
                for (int i = 0; i + 4 <= block.Length; i += 4) fat.Add(BitConverter.ToUInt32(block, i));
            }

            byte[] Chain(uint start)
            {
                using var output = new MemoryStream();
                int steps = 0;
                for (uint id = start; id < 0xFFFFFFFA; id = fat[(int)id])
                {
                    if (id >= fat.Count || ++steps > fat.Count) throw new IOException("Catena OLE non valida.");
                    output.Write(Sector(id));
                }
                return output.ToArray();
            }

            byte[] directory = Chain(directoryStart);
            byte[] miniFat = miniFatStart < 0xFFFFFFFA ? Chain(miniFatStart) : [];
            byte[] miniStream = [];
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int entry = 0; entry + 128 <= directory.Length; entry += 128)
            {
                int nameLength = BitConverter.ToUInt16(directory, entry + 64);
                byte type = directory[entry + 66];
                if (type is not (2 or 5) || nameLength is < 2 or > 64) continue;
                string name = Encoding.Unicode.GetString(directory, entry, nameLength - 2);
                uint start = BitConverter.ToUInt32(directory, entry + 116);
                uint size = BitConverter.ToUInt32(directory, entry + 120);
                if (type == 5) { miniStream = start < 0xFFFFFFFA ? Chain(start) : []; continue; }
                if (name is not ("Workbook" or "Book" or "EncryptedPackage")) continue;
                if (name == "EncryptedPackage") { result[name] = []; continue; }
                byte[] content;
                if (size >= cutoff) content = Chain(start);
                else
                {
                    using var output = new MemoryStream();
                    int steps = 0;
                    for (uint id = start; id < 0xFFFFFFFA; id = BitConverter.ToUInt32(miniFat, (int)id * 4))
                    {
                        if (++steps > miniFat.Length) throw new IOException("Catena OLE non valida.");
                        int offset = checked((int)id * miniSize);
                        output.Write(miniStream, offset, Math.Min(miniSize, miniStream.Length - offset));
                    }
                    content = output.ToArray();
                }
                if (content.Length > size) Array.Resize(ref content, (int)size);
                result[name] = content;
            }
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new IOException(Loc.T("Il file .xls è danneggiato.", "The .xls file is damaged."), ex);
        }
    }

    // ── BIFF8 ─────────────────────────────────────────────────────────────────

    private sealed record Xf(int Format, int Font, SheetAlign Align);

    private static List<Sheet> ReadWorkbook(byte[] data)
    {
        if (data.Length < 8 || BitConverter.ToUInt16(data, 4) != 0x0600)
            throw new IOException(Loc.T("File Excel 5.0/95: aprilo in Excel o LibreOffice e salvalo come .xlsx.", "Excel 5.0/95 file: open it in Excel or LibreOffice and save it as .xlsx."));

        var strings = new List<string>();
        var formats = new Dictionary<int, string>();
        var fonts = new List<bool>();
        var xfs = new List<Xf>();
        var bound = new List<(int Offset, string Name)>();
        bool date1904 = false;

        int position = 0;
        while (position + 4 <= data.Length)
        {
            int type = BitConverter.ToUInt16(data, position), length = BitConverter.ToUInt16(data, position + 2), body = position + 4;
            if (body + length > data.Length) break;
            switch (type)
            {
                case 0x002F: throw new IOException(Loc.T("Il foglio di calcolo è protetto da password.", "The spreadsheet is password protected."));
                case 0x0022: date1904 = BitConverter.ToUInt16(data, body) == 1; break;
                case 0x0031: fonts.Add(length >= 8 && BitConverter.ToUInt16(data, body + 6) >= 600); break;
                case 0x041E: formats[BitConverter.ToUInt16(data, body)] = ReadString(data, body + 2, body + length, twoByteCount: true); break;
                case 0x00E0:
                    xfs.Add(new Xf(BitConverter.ToUInt16(data, body + 2), BitConverter.ToUInt16(data, body),
                        (data[body + 6] & 7) switch { 1 => SheetAlign.Left, 2 or 6 => SheetAlign.Center, 3 => SheetAlign.Right, _ => SheetAlign.General }));
                    break;
                case 0x0085:
                    // Solo fogli di lavoro visibili: niente grafici, macro o fogli nascosti.
                    if ((data[body + 4] & 3) == 0 && data[body + 5] == 0)
                        bound.Add((BitConverter.ToInt32(data, body), ReadString(data, body + 6, body + length, twoByteCount: false)));
                    break;
                case 0x00FC:
                    var segments = new List<(int Start, int End)> { (body, body + length) };
                    int next = body + length;
                    while (next + 4 <= data.Length && BitConverter.ToUInt16(data, next) == 0x003C)
                    {
                        int size = BitConverter.ToUInt16(data, next + 2);
                        segments.Add((next + 4, Math.Min(data.Length, next + 4 + size)));
                        next += 4 + size;
                    }
                    strings = new StringTable(data, segments).Read();
                    break;
            }
            position = body + length;
            if (type == 0x000A) break;
        }

        var sheets = new List<Sheet>();
        foreach (var (offset, name) in bound) sheets.Add(ReadSheet(data, offset, name, strings, formats, fonts, xfs, date1904));
        return sheets;
    }

    private static Sheet ReadSheet(byte[] data, int position, string name, List<string> strings, Dictionary<int, string> formats, List<bool> fonts, List<Xf> xfs, bool date1904)
    {
        var sheet = new Sheet { Name = name };
        var hiddenRows = new HashSet<int>();
        int depth = 0, cells = 0;
        (int Row, int Column, int Xf)? pendingString = null;
        if (position < 0) throw new IOException("Posizione del foglio non valida.");

        while (position + 4 <= data.Length)
        {
            int type = BitConverter.ToUInt16(data, position), length = BitConverter.ToUInt16(data, position + 2), body = position + 4;
            if (body + length > data.Length) break;
            position = body + length;
            if (type == 0x0809) { depth++; continue; }
            if (type == 0x000A) { if (--depth <= 0) break; continue; }
            // I grafici incorporati hanno un blocco proprio dentro il foglio.
            if (depth != 1) continue;

            int U16(int at) => BitConverter.ToUInt16(data, body + at);
            switch (type)
            {
                case 0x0208: if (length >= 14 && (U16(12) & 0x20) != 0) hiddenRows.Add(U16(0)); break;
                case 0x007D:
                    for (int c = U16(0); c <= Math.Min(U16(2), 255); c++) sheet.Widths[c] = (U16(8) & 1) != 0 ? 0 : U16(4) / 256.0;
                    break;
                case 0x00FD:
                    int index = BitConverter.ToInt32(data, body + 6);
                    if (index >= 0 && index < strings.Count) Text(U16(0), U16(2), U16(4), strings[index]);
                    break;
                case 0x0204 or 0x00D6: Text(U16(0), U16(2), U16(4), ReadString(data, body + 6, body + length, twoByteCount: true)); break;
                case 0x0203: Number(U16(0), U16(2), U16(4), BitConverter.ToDouble(data, body + 6)); break;
                case 0x027E: Number(U16(0), U16(2), U16(4), Rk(BitConverter.ToInt32(data, body + 6))); break;
                case 0x00BD:
                    for (int i = 0, column = U16(2); 4 + i * 6 + 6 <= length - 2; i++, column++)
                        Number(U16(0), column, U16(4 + i * 6), Rk(BitConverter.ToInt32(data, body + 6 + i * 6)));
                    break;
                case 0x0205: Boolean(U16(0), U16(2), U16(4), data[body + 6], data[body + 7] != 0); break;
                case 0x0006:
                    if (U16(12) == 0xFFFF)
                    {
                        switch (data[body + 6])
                        {
                            case 0: pendingString = (U16(0), U16(2), U16(4)); break;
                            case 1: Boolean(U16(0), U16(2), U16(4), data[body + 8], error: false); break;
                            case 2: Boolean(U16(0), U16(2), U16(4), data[body + 8], error: true); break;
                        }
                    }
                    else Number(U16(0), U16(2), U16(4), BitConverter.ToDouble(data, body + 6));
                    break;
                case 0x0207:
                    if (pendingString is { } target) Text(target.Row, target.Column, target.Xf, ReadString(data, body, body + length, twoByteCount: true));
                    pendingString = null;
                    break;
                case 0x00E5:
                    for (int i = 0; i < U16(0) && 2 + i * 8 + 8 <= length; i++)
                    {
                        int top = U16(2 + i * 8), bottom = U16(4 + i * 8), left = U16(6 + i * 8), right = U16(8 + i * 8);
                        if (bottom >= top && right >= left) sheet.Merges[(top, left)] = (right - left + 1, bottom - top + 1);
                    }
                    break;
            }
        }
        foreach (int row in hiddenRows) sheet.Rows.Remove(row);
        return sheet;

        Xf? Style(int id) => id < xfs.Count ? xfs[id] : null;
        // L'indice 4 dei caratteri non esiste in Excel: i successivi sono spostati di uno.
        bool Bold(Xf? xf) => xf != null && (xf.Font > 4 ? xf.Font - 1 : xf.Font) is var font && font < fonts.Count && fonts[font];
        void Add(int row, int column, SheetCell cell)
        {
            if (++cells > 2_000_000) throw new IOException(Loc.T("Il foglio di calcolo supera due milioni di celle.", "The spreadsheet exceeds two million cells."));
            sheet.Set(row, column, cell);
        }
        void Text(int row, int column, int xf, string value)
        {
            value = SpreadsheetReader.Clean(value);
            if (value.Length > 0) Add(row, column, new SheetCell(value, value, false, Bold(Style(xf)), Align: Style(xf)?.Align ?? SheetAlign.General));
        }
        void Number(int row, int column, int xf, double value)
        {
            var style = Style(xf);
            int id = style?.Format ?? 0;
            string? code = formats.GetValueOrDefault(id);
            string raw = value.ToString("R", CultureInfo.InvariantCulture);
            Add(row, column, new SheetCell(ExcelFormat.Display(value, id, code, date1904), ExcelFormat.Neutral(value, raw, id, code, date1904), true, Bold(style), Align: style?.Align ?? SheetAlign.General));
        }
        void Boolean(int row, int column, int xf, byte value, bool error)
        {
            string neutral = error ? value switch { 0 => "#NULL!", 7 => "#DIV/0!", 15 => "#VALUE!", 23 => "#REF!", 29 => "#NAME?", 36 => "#NUM!", _ => "#N/A" } : value != 0 ? "TRUE" : "FALSE";
            string shown = error ? neutral : value != 0 ? Loc.T("VERO", "TRUE") : Loc.T("FALSO", "FALSE");
            Add(row, column, new SheetCell(shown, neutral, false, Bold(Style(xf)), Align: SheetAlign.Center));
        }
    }

    private static double Rk(int value)
    {
        double number = (value & 2) != 0 ? value >> 2 : BitConverter.Int64BitsToDouble((long)(uint)(value & ~3) << 32);
        return (value & 1) != 0 ? number / 100 : number;
    }

    private static string ReadString(byte[] data, int at, int end, bool twoByteCount)
    {
        int count = twoByteCount ? BitConverter.ToUInt16(data, at) : data[at];
        at += twoByteCount ? 2 : 1;
        if (count == 0 || at >= end) return "";
        bool wide = (data[at++] & 1) != 0;
        int bytes = Math.Min(wide ? count * 2 : count, end - at);
        return wide ? Encoding.Unicode.GetString(data, at, bytes & ~1) : Encoding.Latin1.GetString(data, at, bytes);
    }

    /// <summary>Tabella delle stringhe: può essere spezzata in più record anche a metà di una parola.</summary>
    private sealed class StringTable(byte[] data, List<(int Start, int End)> segments)
    {
        private int segment;
        private int at = segments[0].Start;

        internal List<string> Read()
        {
            Skip(4);
            int count = (int)Integer(4);
            var result = new List<string>(Math.Clamp(count, 0, 1_000_000));
            for (int i = 0; i < count && Available(); i++) result.Add(Text());
            return result;
        }

        private string Text()
        {
            int count = (int)Integer(2);
            byte flags = Byte();
            int runs = (flags & 8) != 0 ? (int)Integer(2) : 0;
            long extra = (flags & 4) != 0 ? Integer(4) : 0;
            bool wide = (flags & 1) != 0;
            var text = new StringBuilder(count);
            while (text.Length < count)
            {
                if (at >= segments[segment].End)
                {
                    if (!Advance()) break;
                    // Dopo l'interruzione un nuovo byte indica la larghezza dei caratteri.
                    wide = (data[at++] & 1) != 0;
                }
                int room = (segments[segment].End - at) / (wide ? 2 : 1);
                int take = Math.Min(room, count - text.Length);
                if (take <= 0) { at = segments[segment].End; continue; }
                text.Append(wide ? Encoding.Unicode.GetString(data, at, take * 2) : Encoding.Latin1.GetString(data, at, take));
                at += wide ? take * 2 : take;
            }
            Skip(runs * 4L + extra);
            return text.ToString();
        }

        private bool Available()
        {
            while (at >= segments[segment].End) if (!Advance()) return false;
            return true;
        }

        private bool Advance()
        {
            if (segment + 1 >= segments.Count) return false;
            at = segments[++segment].Start;
            return at < segments[segment].End || Advance();
        }

        private byte Byte() => Available() ? data[at++] : throw new IndexOutOfRangeException();

        private long Integer(int bytes)
        {
            long value = 0;
            for (int i = 0; i < bytes; i++) value |= (long)Byte() << (8 * i);
            return value;
        }

        private void Skip(long bytes)
        {
            while (bytes > 0 && Available())
            {
                int step = (int)Math.Min(bytes, segments[segment].End - at);
                at += step;
                bytes -= step;
            }
        }
    }
}
