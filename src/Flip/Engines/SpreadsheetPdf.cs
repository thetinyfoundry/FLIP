using System.IO;
using Flip.Core;
using SkiaSharp;

namespace Flip.Engines;

/// <summary>Stampa i fogli di calcolo in PDF come tabelle: tutti i fogli visibili, testo selezionabile e niente celle tagliate.</summary>
internal static class SpreadsheetPdf
{
    private const float Margin = 34f, PadX = 4f, PadY = 2.6f, MinFont = 6f, BaseFont = 9.5f;
    private const int MaxPages = 2000;
    private static readonly SKColor Grid = new(0xC4, 0xC9, 0xD4);

    private sealed record Placed(int Row, int Column, int Columns, SheetCell Cell);

    internal static void Write(List<Sheet> sheets, string path, SKDocumentPdfMetadata metadata, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var document = SKDocument.CreatePdf(stream, metadata) ?? throw new IOException("Encoder PDF non disponibile.");
        using var fonts = new Fonts();
        var visible = sheets.Where(sheet => !sheet.IsEmpty).ToList();
        int pages = 0;
        foreach (var sheet in visible)
        {
            token.ThrowIfCancellationRequested();
            WriteSheet(document, sheet, visible.Count > 1 && sheet.Name.Length > 0, fonts, ref pages, token);
        }
        document.Close();
        stream.Flush(true);
    }

    private static void WriteSheet(SKDocument document, Sheet sheet, bool title, Fonts fonts, ref int pages, CancellationToken token)
    {
        // Solo le colonne usate: un foglio vuoto fino a XFD non deve generare pagine.
        var used = sheet.Rows.Values.SelectMany(row => row.Keys).Distinct().Where(column => !(sheet.Widths.TryGetValue(column, out double width) && width == 0)).Order().ToList();
        if (used.Count == 0) return;
        var natural = NaturalWidths(sheet, used, fonts);

        float total = natural.Values.Sum();
        bool landscape = total > 595 - 2 * Margin;
        float pageWidth = landscape ? 842 : 595, pageHeight = landscape ? 595 : 842, room = pageWidth - 2 * Margin;
        // Si riduce il carattere finché resta leggibile, poi le colonne proseguono su altre pagine come in Excel.
        float scale = Math.Clamp(room / total, MinFont / BaseFont, 1f);
        if (total * scale <= room && landscape is false) scale = Math.Min(1f, room / total);
        float size = BaseFont * scale;
        var widths = natural.ToDictionary(item => item.Key, item => Math.Min(item.Value * scale, room));

        foreach (var columns in SplitColumns(used, widths, room))
            WriteColumns(document, sheet, columns, widths, size, pageWidth, pageHeight, title, fonts, ref pages, token);
    }

    private static Dictionary<int, float> NaturalWidths(Sheet sheet, List<int> used, Fonts fonts)
    {
        var result = used.ToDictionary(column => column, _ => 24f);
        foreach (var (rowIndex, row) in sheet.Rows)
            foreach (var (column, cell) in row)
            {
                if (cell.Display.Length == 0 || !result.ContainsKey(column)) continue;
                // Una cella unita usa lo spazio di più colonne e non deve allargare la prima.
                if (sheet.Merges.TryGetValue((rowIndex, column), out var merge) && merge.Columns > 1) continue;
                float needed = cell.Display.Split('\n').Max(line => fonts.Measure(line, BaseFont, cell.Bold)) + 2 * PadX + 2;
                // I numeri non vanno mai a capo; i testi lunghi sì.
                result[column] = Math.Max(result[column], cell.IsNumber ? needed : Math.Min(needed, 190f));
            }
        foreach (int column in used)
            if (sheet.Widths.TryGetValue(column, out double characters) && characters > 0)
                result[column] = Math.Max(result[column], Math.Min((float)characters * 5.1f, 300f));
        return result;
    }

    private static IEnumerable<List<int>> SplitColumns(List<int> used, Dictionary<int, float> widths, float room)
    {
        var current = new List<int>();
        float sum = 0;
        foreach (int column in used)
        {
            if (current.Count > 0 && sum + widths[column] > room + 0.5f) { yield return current; current = []; sum = 0; }
            current.Add(column);
            sum += widths[column];
        }
        if (current.Count > 0) yield return current;
    }

    private static void WriteColumns(SKDocument document, Sheet sheet, List<int> columns, Dictionary<int, float> widths, float size,
        float pageWidth, float pageHeight, bool title, Fonts fonts, ref int pages, CancellationToken token)
    {
        using var paint = new SKPaint { IsAntialias = true };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = Grid };
        var covered = Covered(sheet);
        float lineHeight = size * 1.28f, y = 0;
        SKCanvas? canvas = null;
        int pageCount = pages;

        void NewPage()
        {
            if (canvas != null) document.EndPage();
            if (++pageCount > MaxPages) throw new IOException(Loc.F("Il foglio di calcolo supera {0} pagine PDF.", "The spreadsheet exceeds {0} PDF pages.", MaxPages));
            canvas = document.BeginPage(pageWidth, pageHeight);
            canvas.Clear(SKColors.White);
            y = Margin;
        }

        var rows = sheet.Rows.Where(row => row.Value.Any(cell => columns.Contains(cell.Key) && cell.Value.Display.Length > 0)).Select(row => row.Key).ToList();
        if (rows.Count == 0) return;
        int? header = HeaderRow(sheet, rows);

        NewPage();
        if (title)
        {
            fonts.Draw(canvas!, sheet.Name, Margin, y + 13, 13, true, paint, SKColors.Black);
            y += 24;
        }

        int previous = -1;
        foreach (int rowIndex in rows)
        {
            token.ThrowIfCancellationRequested();
            // Le righe vuote restano come spazio, senza creare pagine bianche.
            if (previous >= 0 && rowIndex > previous + 1) y += Math.Min(rowIndex - previous - 1, 3) * (lineHeight + 2 * PadY) * 0.6f;
            previous = rowIndex;
            DrawRow(rowIndex);
        }
        document.EndPage();
        pages = pageCount;

        void DrawRow(int rowIndex)
        {
            var cells = Layout(rowIndex);
            float height = lineHeight + 2 * PadY;
            var texts = new List<(Placed Cell, float X, float Width, List<string> Lines)>();
            foreach (var placed in cells)
            {
                float x = Margin + columns.TakeWhile(column => column < placed.Column).Sum(column => widths[column]);
                float width = columns.Where(column => column >= placed.Column && column < placed.Column + placed.Columns).Sum(column => widths[column]);
                var lines = Wrap(placed.Cell, width - 2 * PadX, size, fonts);
                texts.Add((placed, x, width, lines));
                height = Math.Max(height, lines.Count * lineHeight + 2 * PadY);
            }
            float available = pageHeight - 2 * Margin - (header != null && header != rowIndex ? lineHeight + 2 * PadY : 0);
            if (y + Math.Min(height, available) > pageHeight - Margin && y > Margin + 1)
            {
                NewPage();
                if (header is int repeated && repeated != rowIndex) DrawRow(repeated);
            }

            // Una cella più alta della pagina prosegue su quella successiva invece di essere tagliata.
            int offset = 0, longest = texts.Count == 0 ? 0 : texts.Max(text => text.Lines.Count);
            do
            {
                int fit = Math.Max(1, (int)((pageHeight - Margin - y - 2 * PadY) / lineHeight));
                int count = Math.Clamp(longest - offset, 1, fit);
                float part = count * lineHeight + 2 * PadY;
                foreach (var (placed, x, width, lines) in texts)
                {
                    var box = new SKRect(x, y, x + width, y + part);
                    if (placed.Cell.Fill is { } fill && SKColor.TryParse("#" + fill, out var background))
                    {
                        paint.Style = SKPaintStyle.Fill;
                        paint.Color = background;
                        canvas!.DrawRect(box, paint);
                    }
                    canvas!.DrawRect(box, line);
                    var color = placed.Cell.Color is { } hex && SKColor.TryParse("#" + hex, out var parsed) ? parsed : SKColors.Black;
                    for (int i = 0; i < count && offset + i < lines.Count; i++)
                    {
                        string text = lines[offset + i];
                        float measured = fonts.Measure(text, size, placed.Cell.Bold);
                        var align = placed.Cell.Align == SheetAlign.General && placed.Cell.IsNumber ? SheetAlign.Right : placed.Cell.Align;
                        float left = align switch
                        {
                            SheetAlign.Right => x + width - PadX - measured,
                            SheetAlign.Center => x + (width - measured) / 2,
                            _ => x + PadX
                        };
                        fonts.Draw(canvas!, text, Math.Max(x + 1, left), y + PadY + size * 0.95f + i * lineHeight, size, placed.Cell.Bold, paint, color);
                    }
                }
                y += part;
                offset += count;
                if (offset < longest) NewPage();
            } while (offset < longest);
        }

        List<Placed> Layout(int rowIndex)
        {
            var result = new List<Placed>();
            sheet.Rows.TryGetValue(rowIndex, out var row);
            for (int i = 0; i < columns.Count; i++)
            {
                int column = columns[i];
                if (covered.TryGetValue((rowIndex, column), out bool inRow) && inRow) continue;
                var cell = row != null && row.TryGetValue(column, out var value) ? value : new SheetCell("", "");
                int span = sheet.Merges.TryGetValue((rowIndex, column), out var merge) ? merge.Columns : 1;
                // Come in Excel, un testo sporge sulle celle vuote alla sua destra.
                if (span == 1 && !cell.IsNumber && cell.Display.Length > 0 && cell.Align is SheetAlign.General or SheetAlign.Left &&
                    fonts.Measure(cell.Display, size, cell.Bold) + 2 * PadX > widths[column])
                {
                    float width = widths[column], needed = fonts.Measure(cell.Display, size, cell.Bold) + 2 * PadX;
                    for (int j = i + 1; j < columns.Count && width < needed && !(row!.TryGetValue(columns[j], out var right) && (right.Display.Length > 0 || right.Fill != null)) &&
                        !covered.ContainsKey((rowIndex, columns[j])) && !sheet.Merges.ContainsKey((rowIndex, columns[j])); j++)
                    {
                        width += widths[columns[j]];
                        span = columns[j] - column + 1;
                    }
                }
                result.Add(new Placed(rowIndex, column, span, cell));
                while (i + 1 < columns.Count && columns[i + 1] < column + span) i++;
            }
            return result;
        }
    }

    /// <summary>Celle nascoste da un'unione: true se coperte nella stessa riga, false se coperte da una riga sopra.</summary>
    private static Dictionary<(int Row, int Column), bool> Covered(Sheet sheet)
    {
        var result = new Dictionary<(int, int), bool>();
        foreach (var ((row, column), (columns, rows)) in sheet.Merges)
            for (int c = 0; c < Math.Min(columns, 256); c++)
                for (int r = 0; r < Math.Min(rows, 512); r++)
                    if (r > 0 || c > 0) result[(row + r, column + c)] = r == 0;
        return result;
    }

    /// <summary>La prima riga si ripete su ogni pagina quando è chiaramente un'intestazione.</summary>
    private static int? HeaderRow(Sheet sheet, List<int> rows)
    {
        if (rows.Count < 3) return null;
        var first = sheet.Rows[rows[0]].Values.Where(cell => cell.Display.Length > 0).ToList();
        int width = sheet.Rows[rows[1]].Values.Count(cell => cell.Display.Length > 0);
        return first.Count >= 2 && first.Count >= width && first.All(cell => !cell.IsNumber && cell.Display.Length <= 60) ? rows[0] : null;
    }

    private static List<string> Wrap(SheetCell cell, float width, float size, Fonts fonts)
    {
        var lines = new List<string>();
        foreach (string paragraph in cell.Display.Split('\n'))
        {
            string rest = paragraph.Replace('\t', ' ');
            while (true)
            {
                if (rest.Length == 0 || fonts.Measure(rest, size, cell.Bold) <= width + 0.5f) { lines.Add(rest); break; }
                int low = 1, high = rest.Length - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (fonts.Measure(rest[..middle], size, cell.Bold) <= width) low = middle;
                    else high = middle - 1;
                }
                int space = rest.LastIndexOf(' ', low);
                int cut = space > 0 ? space : low > 1 && char.IsHighSurrogate(rest[low - 1]) ? low - 1 : low;
                lines.Add(rest[..cut]);
                rest = rest[cut..].TrimStart(' ');
            }
        }
        return lines;
    }

    private sealed class Fonts : IDisposable
    {
        private readonly SKTypeface regular = Family(SKFontStyle.Normal), bold = Family(SKFontStyle.Bold);
        private readonly Dictionary<(bool Bold, int CodePoint), SKTypeface> fallback = [];
        private readonly Dictionary<(nint Typeface, float Size), SKFont> fonts = [];

        private static SKTypeface Family(SKFontStyle style) =>
            SKFontManager.Default.MatchFamily("Segoe UI", style) ?? SKTypeface.FromFamilyName("Arial", style) ?? SKTypeface.Default;

        internal float Measure(string text, float size, bool isBold) => Runs(text, isBold).Sum(run => Font(run.Typeface, size).MeasureText(run.Text));

        internal void Draw(SKCanvas canvas, string text, float x, float y, float size, bool isBold, SKPaint paint, SKColor color)
        {
            paint.Style = SKPaintStyle.Fill;
            paint.Color = color;
            foreach (var (run, typeface) in Runs(text, isBold))
            {
                var font = Font(typeface, size);
                canvas.DrawText(run, x, y, SKTextAlign.Left, font, paint);
                x += font.MeasureText(run);
            }
        }

        // Simboli, emoji e scritture non latine usano il carattere di Windows che li contiene.
        private IEnumerable<(string Text, SKTypeface Typeface)> Runs(string text, bool isBold)
        {
            var primary = isBold ? bold : regular;
            if (text.Length == 0) yield break;
            if (Font(primary, BaseFont).ContainsGlyphs(text)) { yield return (text, primary); yield break; }
            int start = 0;
            SKTypeface? current = null;
            for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            {
                int code = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i];
                var typeface = Font(primary, BaseFont).ContainsGlyph(code) ? primary : Fallback(code, isBold, primary);
                if (current != null && !ReferenceEquals(typeface, current)) { yield return (text[start..i], current); start = i; }
                current = typeface;
            }
            yield return (text[start..], current ?? primary);
        }

        private SKTypeface Fallback(int code, bool isBold, SKTypeface primary)
        {
            if (!fallback.TryGetValue((isBold, code), out var typeface))
                fallback[(isBold, code)] = typeface = SKFontManager.Default.MatchCharacter(null, isBold ? SKFontStyle.Bold : SKFontStyle.Normal, null, code) ?? primary;
            return typeface;
        }

        private SKFont Font(SKTypeface typeface, float size)
        {
            if (!fonts.TryGetValue((typeface.Handle, size), out var font))
                fonts[(typeface.Handle, size)] = font = new SKFont(typeface, size) { Subpixel = true };
            return font;
        }

        public void Dispose()
        {
            foreach (var font in fonts.Values) font.Dispose();
            foreach (var typeface in fallback.Values.Distinct()) if (!ReferenceEquals(typeface, regular) && !ReferenceEquals(typeface, bold)) typeface.Dispose();
            regular.Dispose();
            bold.Dispose();
        }
    }
}
