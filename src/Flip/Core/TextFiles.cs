using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flip.Core;

/// <summary>Legge i testi dell'utente senza perdere gli accenti e riconosce il separatore dei CSV.</summary>
internal static class TextFiles
{
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];

    /// <summary>JSON leggibile: lettere accentate invariate invece di sequenze \u00E8.</summary>
    internal static readonly JsonSerializerOptions ReadableJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static readonly JsonSerializerOptions ReadableJsonLine = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static string Read(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>BOM, poi la codifica dichiarata nell'XML, poi UTF-8 rigoroso, infine la codifica ANSI di Windows.</summary>
    internal static string Decode(byte[] bytes)
    {
        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF)) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (StartsWith(bytes, 0xFF, 0xFE, 0, 0)) return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (StartsWith(bytes, 0xFF, 0xFE)) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (StartsWith(bytes, 0xFE, 0xFF)) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (StartsWith(bytes, (byte)'<', 0, (byte)'?', 0)) return Encoding.Unicode.GetString(bytes);

        if (DeclaredXmlEncoding(bytes) is { } declared) return declared.GetString(bytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return AnsiEncoding().GetString(bytes); }
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix) => bytes.AsSpan().StartsWith(prefix);

    private static Encoding? DeclaredXmlEncoding(byte[] bytes)
    {
        string head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        var match = Regex.Match(head, "^\\s*<\\?xml[^>]*?encoding\\s*=\\s*[\"']([A-Za-z0-9._:-]+)[\"']");
        if (!match.Success || match.Groups[1].Value.StartsWith("utf", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Encoding.GetEncoding(match.Groups[1].Value); }
        catch (ArgumentException) { return null; }
    }

    internal static string DecodeAnsi(byte value, int codePage)
    {
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(codePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { encoding = Encoding.GetEncoding(1252); }
        return encoding.GetString([value]);
    }

    private static Encoding AnsiEncoding()
    {
        int codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        try { return Encoding.GetEncoding(codePage > 0 && codePage != 65001 ? codePage : 1252); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.GetEncoding(1252); }
    }

    /// <summary>Righe e celle di un file CSV/TSV, con separatore riconosciuto per i CSV italiani (;).</summary>
    internal static List<List<string>> ReadTable(string text, string extension)
    {
        if (extension.Equals("tsv", StringComparison.OrdinalIgnoreCase)) return ReadRows(text, '\t');

        // Excel salva talvolta il separatore nella prima riga: sep=;
        var hint = Regex.Match(text, @"\Asep=(.)\r?\n", RegexOptions.IgnoreCase);
        if (hint.Success) return ReadRows(text[hint.Length..], hint.Groups[1].Value[0]);
        return ReadRows(text, DetectDelimiter(text));
    }

    internal static char DetectDelimiter(string text)
    {
        char best = ',';
        double bestScore = 0;
        foreach (char candidate in Delimiters)
        {
            var sample = ReadRows(text, candidate, maxRows: 20).Where(row => row.Count > 1 || row[0].Trim().Length > 0).ToList();
            if (sample.Count == 0 || sample[0].Count < 2) continue;
            int width = sample[0].Count;
            double score = (double)sample.Count(row => row.Count == width) / sample.Count + Math.Min(width, 50) / 1000d;
            if (score > bestScore + 1e-9) { best = candidate; bestScore = score; }
        }
        return best;
    }

    internal static List<List<string>> ReadRows(string text, char delimiter, int maxRows = int.MaxValue)
    {
        var result = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        bool quotedCell = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch != '"') cell.Append(ch);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else inQuotes = false;
            }
            // Le virgolette aprono un campo solo al suo inizio: un diametro di 5" resta 5".
            else if (ch == '"' && cell.Length == 0 && !quotedCell) { inQuotes = true; quotedCell = true; }
            else if (ch == delimiter) EndCell();
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndCell();
                result.Add(row);
                if (result.Count >= maxRows) return result;
                row = [];
            }
            else cell.Append(ch);
        }

        if (cell.Length > 0 || row.Count > 0 || quotedCell)
        {
            EndCell();
            result.Add(row);
        }
        return result;

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
            quotedCell = false;
        }
    }

    /// <summary>Nomi di colonna non vuoti e unici per tutte le celle, comprese quelle oltre l'intestazione.</summary>
    internal static List<string> UniqueHeaders(List<List<string>> rows)
    {
        var headers = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int width = rows.Max(row => row.Count);
        for (int i = 0; i < width; i++)
        {
            string name = i < rows[0].Count && !string.IsNullOrWhiteSpace(rows[0][i]) ? rows[0][i].Trim() : $"col_{i + 1}";
            string unique = name;
            for (int suffix = 2; !used.Add(unique); suffix++) unique = $"{name}_{suffix}";
            headers.Add(unique);
        }
        return headers;
    }
}
