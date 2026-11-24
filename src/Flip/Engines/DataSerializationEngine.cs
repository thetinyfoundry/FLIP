using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

/// <summary>
/// Motore built-in di conversione per dati strutturati e configurazioni:
/// JSON, JSONL, CSV, TSV, XML, YAML/YML, TOML, INI, HTML (tabella), Markdown (tabella).
/// Non richiede strumenti esterni ed è completamente nativo in .NET 10.
/// </summary>
public sealed class DataSerializationEngine : IConversionEngine
{
    public string Id => "data_builtin";
    public string DisplayName => "Data & Config Native Engine";
    public bool IsExternal => false;

    private static readonly string[] InputFormats =
    [
        "json", "jsonl", "csv", "tsv", "xml", "yaml", "yml", "toml", "ini"
    ];
    private static readonly string[] OutputFormats = [.. InputFormats, "html", "md"];

    private readonly List<ConversionEdge> _supportedEdges = [];

    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    public DataSerializationEngine()
    {
        foreach (var src in InputFormats)
        {
            foreach (var dst in OutputFormats)
            {
                if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                {
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));
                }
            }
        }
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
            {
                return Task.FromResult(new EngineExecutionResult(false, "", $"File di origine non trovato: {srcPath}"));
            }

            if (new FileInfo(srcPath).Length > 50 * 1024 * 1024)
            {
                return Task.FromResult(new EngineExecutionResult(false, "", "File dati oltre il limite di sicurezza di 50 MiB."));
            }

            string srcExt = (context.SourceExtension ?? Path.GetExtension(srcPath)).TrimStart('.').ToLowerInvariant();
            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();

            string raw = srcExt == "jsonl"
                ? new UTF8Encoding(false, true).GetString(File.ReadAllBytes(srcPath)).TrimStart('\uFEFF')
                : TextFiles.Read(srcPath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Task.FromResult(new EngineExecutionResult(false, "", "Il file di origine è vuoto."));
            }

            context.Progress?.Report(0.2);
            context.CancellationToken.ThrowIfCancellationRequested();

            JsonNode? rootNode = ParseToIntermediateNode(raw, srcExt);
            context.Progress?.Report(0.6);
            context.CancellationToken.ThrowIfCancellationRequested();

            string outputContent = SerializeFromIntermediateNode(rootNode, dstExt, Path.GetFileNameWithoutExtension(srcPath));

            string? dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(dstPath, outputContent, new UTF8Encoding(false));
            context.Progress?.Report(1.0);

            long bytes = new FileInfo(dstPath).Length;
            return Task.FromResult(new EngineExecutionResult(true, dstPath, null, bytes));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", "Conversione annullata."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", $"Errore conversione dati ({context.SourceExtension} -> {context.TargetExtension}): {ex.Message}"));
        }
    }

    // ── Parser Intermedio -> JsonNode ─────────────────────────────────────────

    internal static string ConvertRawToJson(string raw, string ext)
    {
        var node = ParseToIntermediateNode(raw, ext.TrimStart('.').ToLowerInvariant());
        if (node is JsonObject obj && obj.Count == 1 && obj.First().Value is JsonArray singleArray)
            return singleArray.ToJsonString();
        return node?.ToJsonString() ?? "[]";
    }

    private static JsonNode? ParseToIntermediateNode(string raw, string ext)
    {
        return ext switch
        {
            "json" => JsonNode.Parse(raw, new JsonNodeOptions { PropertyNameCaseInsensitive = false }, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }),
            "jsonl" => ParseJsonl(raw),
            "csv" => ParseDelimited(raw, "csv"),
            "tsv" => ParseDelimited(raw, "tsv"),
            "xml" => ParseXml(raw),
            "yaml" or "yml" => ParseYaml(raw),
            "toml" => ParseToml(raw),
            "ini" => ParseIni(raw),
            _ => throw new NotSupportedException($"Formato dati in ingresso non supportato: {ext}")
        };
    }

    private static JsonArray ParseJsonl(string raw)
    {
        var array = new JsonArray();
        int lineNumber = 0;
        foreach (var range in raw.AsSpan().Split('\n'))
        {
            var line = raw.AsSpan()[range];
            if (line.IsEmpty && range.Start.Value == raw.Length) break;
            lineNumber++;
            try
            {
                array.Add(JsonNode.Parse(line.ToString()));
            }
            catch (JsonException ex)
            {
                throw new FormatException($"JSONL non valido alla riga {lineNumber}.", ex);
            }
        }
        return array;
    }

    internal static JsonArray ParseDelimited(string raw, string extension)
    {
        var rows = TextFiles.ReadTable(raw, extension);
        if (rows.Count == 0) return [];

        var array = new JsonArray();
        if (rows.Count == 1)
        {
            var single = new JsonObject();
            for (int i = 0; i < rows[0].Count; i++) single[$"col_{i + 1}"] = rows[0][i];
            array.Add(single);
            return array;
        }

        var headers = TextFiles.UniqueHeaders(rows);
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var obj = new JsonObject();
            for (int c = 0; c < headers.Count; c++)
                obj[headers[c]] = ParseCell(c < row.Count ? row[c] : "");
            array.Add(obj);
        }

        return array;
    }

    private static JsonNode? ParseXml(string raw)
    {
        var doc = XDocument.Parse(raw);
        return doc.Root == null ? new JsonObject() : XmlElementToJson(doc.Root);
    }

    internal static JsonNode? XmlElementToJson(XElement element)
    {
        var attributes = element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToList();
        if (!element.HasElements && attributes.Count == 0)
            return ParseCell(element.Value.Trim());

        var obj = new JsonObject();
        foreach (var attr in attributes)
            obj["@" + attr.Name.LocalName] = ParseCell(attr.Value.Trim());

        // Il testo fra gli elementi figli fa parte del documento e va conservato.
        string text = string.Join(" ", element.Nodes().OfType<XText>()
            .Select(node => node.Value.Trim()).Where(value => value.Length > 0));
        if (text.Length > 0) obj["#text"] = ParseCell(text);

        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
        {
            var items = group.ToList();
            if (items.Count == 1)
                obj[group.Key] = XmlElementToJson(items[0]);
            else
            {
                var arr = new JsonArray();
                foreach (var item in items) arr.Add(XmlElementToJson(item));
                obj[group.Key] = arr;
            }
        }

        return obj;
    }

    private readonly record struct YamlLine(int Number, int Indent, string Text);

    internal static JsonNode? ParseYaml(string raw)
    {
        var lines = new List<YamlLine>();
        using var reader = new StringReader(raw);
        string? rawLine;
        int number = 0;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            number++;
            string text = StripComment(rawLine, yaml: true).TrimEnd();
            if (text.Length == 0) continue;
            int indent = text.Length - text.TrimStart(' ').Length;
            if (text[indent] == '\t') throw new FormatException($"Tabulazione nel rientro YAML alla riga {number}.");
            lines.Add(new(number, indent, text[indent..]));
        }
        if (lines.Count > 0 && lines[0].Text == "---") lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Text == "...") lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return null;

        int cursor = 0;
        var result = ReadYamlBlock(lines, ref cursor, 0);
        if (cursor != lines.Count)
            throw new FormatException($"Blocco YAML inatteso alla riga {lines[cursor].Number}.");
        return result;
    }

    private static JsonNode? ReadYamlBlock(List<YamlLine> lines, ref int cursor, int depth)
    {
        CheckDepth(depth);
        var first = lines[cursor];
        if (IsYamlItem(first.Text))
        {
            var items = new JsonArray();
            while (cursor < lines.Count && lines[cursor].Indent == first.Indent && IsYamlItem(lines[cursor].Text))
            {
                var line = lines[cursor];
                string tail = line.Text[1..];
                string item = tail.TrimStart(' ');
                if (item.Length == 0)
                {
                    cursor++;
                    items.Add(cursor < lines.Count && lines[cursor].Indent > first.Indent
                        ? ReadYamlBlock(lines, ref cursor, depth + 1) : null);
                }
                else
                {
                    // The first entry after a sequence marker belongs to the indented item.
                    lines[cursor] = line with { Indent = line.Indent + 1 + tail.Length - item.Length, Text = item };
                    items.Add(ReadYamlBlock(lines, ref cursor, depth + 1));
                }
            }
            return items;
        }

        if (FindSeparators(first.Text, ':', yaml: true, out _).Count == 0)
        {
            cursor++;
            return ParseConfigValue(first.Text, yaml: true, depth);
        }

        var mapping = new JsonObject();
        while (cursor < lines.Count && lines[cursor].Indent == first.Indent && !IsYamlItem(lines[cursor].Text))
        {
            var line = lines[cursor++];
            var separators = FindSeparators(line.Text, ':', yaml: true, out _);
            if (separators.Count != 1)
                throw new FormatException($"Chiave YAML non valida alla riga {line.Number}.");
            int colon = separators[0];
            string key = ReadConfigKey(line.Text[..colon], yaml: true);
            string value = line.Text[(colon + 1)..].Trim();
            bool hasChild = cursor < lines.Count && (lines[cursor].Indent > line.Indent ||
                (lines[cursor].Indent == line.Indent && IsYamlItem(lines[cursor].Text)));
            mapping.Add(key, value.Length > 0 ? ParseConfigValue(value, yaml: true, depth + 1)
                : hasChild ? ReadYamlBlock(lines, ref cursor, depth + 1) : null);
        }
        return mapping;
    }

    private static bool IsYamlItem(string value) => value == "-" || value.StartsWith("- ", StringComparison.Ordinal);

    private static JsonObject ParseIni(string raw)
    {
        var root = new JsonObject();
        JsonObject current = root;
        using var reader = new StringReader(raw);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                string secName = trimmed[1..^1].Trim();
                // Una sezione ripetuta prosegue la precedente, senza cancellarla.
                if (root[secName] is JsonObject existing) current = existing;
                else root[secName] = current = new JsonObject();
                continue;
            }

            int eq = trimmed.IndexOf('=');
            if (eq > 0)
            {
                string key = trimmed[..eq].Trim();
                string val = trimmed[(eq + 1)..].Trim();
                current[key] = ParseScalar(val);
            }
        }
        return root;
    }

    private static JsonObject ParseToml(string raw)
    {
        var root = new JsonObject();
        JsonObject table = root;
        var headers = new HashSet<JsonObject>();
        var tableArrays = new HashSet<JsonArray>();
        using var reader = new StringReader(raw);
        var statement = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string content = StripComment(line, yaml: false).Trim();
            if (content.Length == 0) continue;
            statement.AppendLine(content);
            string text = statement.ToString().Trim();
            var assignments = FindSeparators(text, '=', yaml: false, out bool complete);
            if (!complete) continue;
            statement.Clear();

            if (text[0] == '[')
            {
                bool arrayTable = text.StartsWith("[[", StringComparison.Ordinal);
                int brackets = arrayTable ? 2 : 1;
                var keys = ReadTomlKeys(text[brackets..^brackets]);
                var parent = GetTomlTable(root, keys.Take(keys.Length - 1), tableArrays);
                string name = keys[^1];
                if (arrayTable)
                {
                    if (!text.EndsWith("]]", StringComparison.Ordinal))
                        throw new FormatException("Intestazione di array di tabelle TOML non valida.");
                    if (!parent.TryGetPropertyValue(name, out var existing))
                    {
                        existing = new JsonArray();
                        parent.Add(name, existing);
                        tableArrays.Add((JsonArray)existing);
                    }
                    if (existing is not JsonArray array || !tableArrays.Contains(array))
                        throw new FormatException($"Tabella TOML incompatibile: {name}.");
                    table = new JsonObject();
                    array.Add(table);
                }
                else
                {
                    table = GetTomlTable(parent, [name], tableArrays);
                    if (!headers.Add(table))
                        throw new FormatException($"Intestazione TOML duplicata: {text}.");
                }
            }
            else
            {
                if (assignments.Count != 1)
                    throw new FormatException("Assegnazione TOML non valida.");
                int equals = assignments[0];
                var keys = ReadTomlKeys(text[..equals]);
                var parent = GetTomlTable(table, keys.Take(keys.Length - 1), tableArrays);
                parent.Add(keys[^1], ParseConfigValue(text[(equals + 1)..], yaml: false, 0));
            }
        }

        if (statement.Length > 0) throw new FormatException("Valore TOML incompleto.");
        return root;
    }

    private static string[] ReadTomlKeys(string text) =>
        SplitSyntax(text, '.', yaml: false).Select(key => ReadConfigKey(key, yaml: false)).ToArray();

    private static JsonObject GetTomlTable(JsonObject table, IEnumerable<string> keys, HashSet<JsonArray> tableArrays)
    {
        foreach (string key in keys)
        {
            if (!table.TryGetPropertyValue(key, out var node))
            {
                node = new JsonObject();
                table.Add(key, node);
            }
            if (node is JsonArray array && tableArrays.Contains(array)) node = array[^1];
            table = node as JsonObject ?? throw new FormatException($"Chiave TOML incompatibile con una tabella: {key}.");
        }
        return table;
    }

    private static JsonNode? ParseConfigValue(string text, bool yaml, int depth)
    {
        CheckDepth(depth);
        string value = text.Trim();
        if (value.Length == 0) throw new FormatException("Valore mancante.");
        if (value[0] is '[' or '{')
        {
            bool isArray = value[0] == '[';
            if (value[^1] != (isArray ? ']' : '}')) throw new FormatException("Collezione incompleta.");
            string body = value[1..^1].Trim();
            var entries = body.Length == 0 ? [] : SplitSyntax(body, ',', yaml).ToList();
            if (entries.Count > 0 && entries[^1].Trim().Length == 0 && (isArray || yaml))
                entries.RemoveAt(entries.Count - 1);
            if (isArray)
            {
                var result = new JsonArray();
                foreach (string item in entries) result.Add(ParseConfigValue(item, yaml, depth + 1));
                return result;
            }

            var mapping = new JsonObject();
            foreach (string entry in entries)
            {
                var separators = FindSeparators(entry, yaml ? ':' : '=', yaml, out _);
                if (separators.Count != 1) throw new FormatException("Voce di mappa non valida.");
                int position = separators[0];
                if (yaml)
                {
                    mapping.Add(ReadConfigKey(entry[..position], yaml: true),
                        ParseConfigValue(entry[(position + 1)..], yaml: true, depth + 1));
                }
                else
                {
                    var keys = ReadTomlKeys(entry[..position]);
                    GetTomlTable(mapping, keys.Take(keys.Length - 1), []).Add(keys[^1],
                        ParseConfigValue(entry[(position + 1)..], yaml: false, depth + 1));
                }
            }
            return mapping;
        }
        if (value[0] is '"' or '\'') return JsonValue.Create(ReadQuoted(value, yaml));
        if (yaml)
        {
            if (value[0] is '&' or '*' or '!' or '|' or '>' or '%' or '@' or '\x60'
                || value is "---" or "..." || value.Contains('\n'))
                throw new FormatException($"Sintassi YAML non supportata: {value}.");
            return ParseScalar(value);
        }

        return ParseTomlScalar(value);
    }

    private static JsonValue ParseTomlScalar(string value)
    {
        if (value is "true" or "false") return JsonValue.Create(value == "true");
        string numeric = value.Replace("_", "");
        if (Regex.IsMatch(value, @"^[+-]?(?:0|[1-9](?:_?[0-9])*)$") &&
            long.TryParse(numeric, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            return JsonValue.Create(integer);
        if (Regex.IsMatch(value, @"^0(?:x[0-9a-fA-F](?:_?[0-9a-fA-F])*|o[0-7](?:_?[0-7])*|b[01](?:_?[01])*)$"))
        {
            int radix = numeric[1] switch { 'x' => 16, 'o' => 8, _ => 2 };
            return JsonValue.Create(checked((long)Convert.ToUInt64(numeric[2..], radix)));
        }
        if (Regex.IsMatch(value, @"^[+-]?(?:0|[1-9](?:_?[0-9])*)(?:\.[0-9](?:_?[0-9])*(?:[eE][+-]?[0-9](?:_?[0-9])*)?|[eE][+-]?[0-9](?:_?[0-9])*)$") &&
            double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number))
            return JsonNode.Parse(numeric.TrimStart('+'))!.AsValue();
        if (Regex.IsMatch(value, @"^(?:[0-9]{4}-[0-9]{2}-[0-9]{2}(?:[Tt ][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})?)?|[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?)$"))
            return JsonValue.Create(value);
        throw new FormatException($"Valore TOML non valido o non rappresentabile in JSON: {value}.");
    }

    private static string ReadConfigKey(string text, bool yaml)
    {
        string key = text.Trim();
        if (key.Length > 0 && key[0] is '"' or '\'') return ReadQuoted(key, yaml);
        if (key.Length == 0 || (yaml
            ? key[0] is '?' or '[' or '{' or '&' or '*' or '!' or '%' or '@' or '\x60' || key == "<<"
            : !key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new FormatException($"Chiave non valida o non supportata: {key}.");
        return key;
    }

    private static string ReadQuoted(string text, bool yaml)
    {
        if (text.Length < 2 || text[^1] != text[0] || text.Contains('\n') || text.Contains('\r'))
            throw new FormatException("Stringa tra virgolette non valida o multilinea.");
        if (text[0] == '"')
            return JsonSerializer.Deserialize<string>(text) ?? "";

        string content = text[1..^1];
        if (yaml && Regex.IsMatch(content, "^(?:[^']|'')*$")) return content.Replace("''", "'");
        if (content.Contains('\'')) throw new FormatException("Stringa letterale non valida.");
        return content;
    }

    private static void CheckDepth(int depth)
    {
        if (depth >= 64) throw new FormatException("Il documento supera il limite di 64 livelli annidati.");
    }

    private static string StripComment(string text, bool yaml)
    {
        var comments = FindSeparators(text, '#', yaml, out _);
        return comments.Count == 0 ? text : text[..comments[0]];
    }

    private static IEnumerable<string> SplitSyntax(string text, char separator, bool yaml)
    {
        var separators = FindSeparators(text, separator, yaml, out bool complete);
        if (!complete) throw new FormatException("Stringa o collezione incompleta.");
        int start = 0;
        foreach (int position in separators)
        {
            yield return text[start..position];
            start = position + 1;
        }
        yield return text[start..];
    }

    private static List<int> FindSeparators(string text, char separator, bool yaml, out bool complete)
    {
        var positions = new List<int>();
        var brackets = new Stack<char>();
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quote != '\0')
            {
                if (quote == '"' && ch == '\\') i++;
                else if (ch == quote)
                {
                    if (yaml && quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') i++;
                    else quote = '\0';
                }
                continue;
            }

            if (ch is '"' or '\'' && (!yaml || i == 0 || char.IsWhiteSpace(text[i - 1])
                || text[i - 1] is '[' or '{' or ',' or ':'))
                quote = ch;
            else if (separator == '#' && ch == '#' && (!yaml || i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                positions.Add(i);
                break;
            }
            else if (separator != '#')
            {
                if (ch is '[' or '{') brackets.Push(ch == '[' ? ']' : '}');
                else if (ch is ']' or '}')
                {
                    if (brackets.Count == 0 || brackets.Pop() != ch) throw new FormatException("Parentesi non bilanciate.");
                }
                else if (ch == separator && brackets.Count == 0 &&
                    (separator != ':' || !yaml || i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]) ||
                        (i > 0 && text[i - 1] is '"' or '\'')))
                    positions.Add(i);
            }
        }
        complete = quote == '\0' && brackets.Count == 0;
        return positions;
    }

    /// <summary>Postal codes, phone numbers and IDs such as 00123, 007 or +39081 are text: a number would drop the prefix.</summary>
    internal static bool IsNumericIdentifier(string value) =>
        Regex.IsMatch(value, @"^(?:[+-]?0[0-9]|\+[0-9])");

    private static JsonNode? ParseScalar(string value)
    {
        string v = value.Trim();
        if (v.Length >= 2 && ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\''))))
            return JsonValue.Create(v[1..^1]);
        if (v.Equals("null", StringComparison.OrdinalIgnoreCase) || v == "~") return null;
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        return ParseNumber(v, allowExponent: true) ?? JsonValue.Create(v);
    }

    /// <summary>Cella CSV o testo XML: null, ~ e virgolette sono contenuto, non sintassi.</summary>
    internal static JsonNode? ParseCell(string value)
    {
        string v = value.Trim();
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        // Un esponente in una cella è quasi sempre un codice prodotto (12E5).
        return ParseNumber(v, allowExponent: false) ?? JsonValue.Create(value);
    }

    /// <summary>Numero JSON con le stesse cifre del testo (1.50 resta 1.50), oppure null se diventerebbe un altro valore.</summary>
    internal static JsonNode? ParseNumber(string value, bool allowExponent)
    {
        if (IsNumericIdentifier(value) && !Regex.IsMatch(value, @"\A-?0(?:\.[0-9]+)?\z")) return null;
        if (!Regex.IsMatch(value, allowExponent
                ? @"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z"
                : @"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?\z"))
            return null;
        // Oltre 15 cifre un double perde precisione: i codici lunghi restano testo.
        if (value.Count(char.IsAsciiDigit) > 15) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) return null;
        return JsonNode.Parse(value);
    }

    // ── Serializzatore JsonNode -> Output String ──────────────────────────────

    private static string SerializeFromIntermediateNode(JsonNode? node, string ext, string docTitle)
    {
        return ext switch
        {
            "json" => node?.ToJsonString(TextFiles.ReadableJson) ?? "null",
            "jsonl" => ToJsonl(node),
            "csv" => ToDelimited(node, ','),
            "tsv" => ToDelimited(node, '\t'),
            "xml" => ToXml(node),
            "yaml" or "yml" => ToYaml(node, 0),
            "toml" => ToToml(node),
            "ini" => ToIni(node),
            "md" => ToMarkdownTable(node, docTitle),
            "html" => ToHtmlTable(node, docTitle),
            _ => throw new NotSupportedException($"Formato dati in uscita non supportato: {ext}")
        };
    }

    private static string ToJsonl(JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var item in arr)
            {
                sb.AppendLine(item?.ToJsonString(TextFiles.ReadableJsonLine) ?? "null");
            }
            return sb.ToString();
        }
        return (node?.ToJsonString(TextFiles.ReadableJsonLine) ?? "null") + Environment.NewLine;
    }

    private static string ToDelimited(JsonNode? node, char delimiter)
    {
        var sb = new StringBuilder();
        if (node is JsonArray arr && arr.Count > 0)
        {
            if (arr.All(x => x is JsonObject))
            {
                var columns = arr.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().ToList();
                sb.AppendLine(string.Join(delimiter, columns.Select(c => EscapeDelimited(c, delimiter))));
                foreach (var obj in arr.OfType<JsonObject>())
                {
                    sb.AppendLine(string.Join(delimiter, columns.Select(c => EscapeDelimited(ScalarString(obj[c]), delimiter))));
                }
                return sb.ToString();
            }
            sb.AppendLine("value");
            foreach (var item in arr) sb.AppendLine(EscapeDelimited(ScalarString(item), delimiter));
            return sb.ToString();
        }
        if (node is JsonObject rootObj)
        {
            var firstArray = rootObj.FirstOrDefault(kv => kv.Value is JsonArray a && a.Count > 0 && a.All(x => x is JsonObject));
            if (rootObj.Count == 1 && firstArray.Value is JsonArray nestedArr)
                return ToDelimited(nestedArr, delimiter);

            sb.AppendLine($"key{delimiter}value");
            foreach (var kv in rootObj)
                sb.AppendLine($"{EscapeDelimited(kv.Key, delimiter)}{delimiter}{EscapeDelimited(ScalarString(kv.Value), delimiter)}");
            return sb.ToString();
        }
        sb.AppendLine("value");
        sb.AppendLine(EscapeDelimited(ScalarString(node), delimiter));
        return sb.ToString();
    }

    private static string EscapeDelimited(string s, char delimiter)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(delimiter) || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static string ScalarString(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue val && val.TryGetValue<string>(out string? text))
            return text;
        return node.ToJsonString(TextFiles.ReadableJsonLine);
    }

    private static string ToXml(JsonNode? node)
    {
        var root = new XElement("root");
        PopulateXml(root, node, "item");
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
        return doc.ToString();
    }

    internal static void PopulateXml(XElement parent, JsonNode? node, string itemName)
    {
        if (node is null) return;
        if (node is JsonValue)
        {
            parent.Value = ScalarString(node);
            return;
        }
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var child = new XElement(SanitizeXmlName(itemName));
                PopulateXml(child, item, "item");
                parent.Add(child);
            }
            return;
        }
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Key == "#text" && kv.Value is JsonValue)
                {
                    parent.Add(new XText(ScalarString(kv.Value)));
                }
                else if (kv.Key.StartsWith('@') && kv.Value is JsonValue attrVal)
                {
                    parent.SetAttributeValue(SanitizeXmlName(kv.Key[1..]), ScalarString(attrVal));
                }
                else if (kv.Value is JsonArray childArr)
                {
                    PopulateXml(parent, childArr, kv.Key);
                }
                else
                {
                    var child = new XElement(SanitizeXmlName(kv.Key));
                    PopulateXml(child, kv.Value, "item");
                    parent.Add(child);
                }
            }
        }
    }

    internal static string SanitizeXmlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "item";
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        if (!char.IsLetter(sb[0]) && sb[0] != '_') sb.Insert(0, '_');
        return sb.ToString();
    }

    private static string ToYaml(JsonNode? node, int indent)
    {
        string pad = new(' ', indent * 2);
        var output = new StringBuilder();
        if (node is JsonArray items && items.Count > 0)
        {
            foreach (var item in items) AppendValue("-", item);
        }
        else if (node is JsonObject mapping && mapping.Count > 0)
        {
            foreach (var (key, value) in mapping) AppendValue(JsonSerializer.Serialize(key, TextFiles.ReadableJsonLine) + ":", value);
        }
        else
        {
            output.Append(pad).AppendLine(node?.ToJsonString(TextFiles.ReadableJsonLine) ?? "null");
        }
        return output.ToString();

        void AppendValue(string label, JsonNode? value)
        {
            output.Append(pad).Append(label);
            if (value is JsonArray { Count: > 0 } or JsonObject { Count: > 0 })
                output.AppendLine().Append(ToYaml(value, indent + 1));
            else
                output.Append(' ').AppendLine(value?.ToJsonString(TextFiles.ReadableJsonLine) ?? "null");
        }
    }

    private static string ToIni(JsonNode? node)
    {
        var sb = new StringBuilder();
        var rootObj = AsMapping(node);
        if (node is JsonArray rows)
        {
            // Una tabella diventa una sezione numerata per riga.
            rootObj = new JsonObject();
            for (int i = 0; i < rows.Count; i++) rootObj[(i + 1).ToString(CultureInfo.InvariantCulture)] = rows[i]?.DeepClone();
        }
        {
            // Prima le chiavi senza sezione: dopo un'intestazione apparterrebbero a quella sezione.
            foreach (var kv in rootObj.OrderBy(kv => kv.Value is JsonObject))
            {
                if (kv.Value is JsonObject subObj)
                {
                    sb.AppendLine($"[{kv.Key}]");
                    foreach (var subKv in subObj)
                        sb.AppendLine($"{subKv.Key} = {ScalarString(subKv.Value)}");
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine($"{kv.Key} = {ScalarString(kv.Value)}");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Elenchi e valori singoli vanno sotto una chiave: INI e TOML hanno sempre una mappa alla radice.</summary>
    private static JsonObject AsMapping(JsonNode? node) => node as JsonObject ?? new JsonObject { [node is JsonArray ? "items" : "value"] = node?.DeepClone() };

    private static string ToToml(JsonNode? node)
    {
        var output = new StringBuilder();
        // TOML non rappresenta i valori null: una chiave assente ha lo stesso significato.
        foreach (var (key, value) in AsMapping(node).Where(item => item.Value is not null))
            output.Append(FormatTomlKey(key)).Append(" = ").AppendLine(FormatTomlValue(value));
        return output.Length == 0 ? "# Documento TOML vuoto" + Environment.NewLine : output.ToString();
    }

    private static string FormatTomlValue(JsonNode? value)
    {
        return value switch
        {
            null => throw new FormatException("TOML non rappresenta i valori null."),
            JsonArray array => "[" + string.Join(", ", array.Select(FormatTomlValue)) + "]",
            JsonObject mapping => "{ " + string.Join(", ", mapping.Where(item => item.Value is not null).Select(item =>
                $"{FormatTomlKey(item.Key)} = {FormatTomlValue(item.Value)}")) + " }",
            JsonValue scalar when scalar.TryGetValue<string>(out string? text) => QuoteToml(text),
            _ => value.ToJsonString()
        };
    }

    private static string FormatTomlKey(string key) => key.Length > 0 &&
        key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-') ? key : QuoteToml(key);

    private static string QuoteToml(string text)
    {
        var output = new StringBuilder("\"");
        foreach (char ch in text)
        {
            if (ch is '"' or '\\') output.Append('\\').Append(ch);
            else if (ch < ' ' || ch == '\x7f') output.Append($"\\u{(int)ch:x4}");
            else output.Append(ch);
        }
        return output.Append('"').ToString();
    }

    private static string MarkdownCell(string text) =>
        text.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    private static string ToMarkdownTable(JsonNode? node, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();

        if (node is JsonArray arr && arr.Count > 0 && arr.All(x => x is JsonObject))
        {
            var cols = arr.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().ToList();
            sb.AppendLine("| " + string.Join(" | ", cols.Select(MarkdownCell)) + " |");
            sb.AppendLine("| " + string.Join(" | ", cols.Select(_ => "---")) + " |");
            foreach (var row in arr.OfType<JsonObject>())
            {
                sb.AppendLine("| " + string.Join(" | ", cols.Select(c => MarkdownCell(ScalarString(row[c])))) + " |");
            }
            return sb.ToString();
        }

        if (node is JsonObject obj)
        {
            sb.AppendLine("| Chiave | Valore |");
            sb.AppendLine("|---|---|");
            foreach (var kv in obj)
            {
                sb.AppendLine($"| {MarkdownCell(kv.Key)} | {MarkdownCell(ScalarString(kv.Value))} |");
            }
            return sb.ToString();
        }

        sb.AppendLine("```");
        sb.AppendLine(node?.ToJsonString(TextFiles.ReadableJson) ?? "null");
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string ToHtmlTable(JsonNode? node, string title)
    {
        title = System.Net.WebUtility.HtmlEncode(title);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{title}</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;margin:24px;background:#18181b;color:#f4f4f5;} table{border-collapse:collapse;width:100%;margin-top:16px;} th,td{border:1px solid #3f3f46;padding:10px 14px;text-align:left;} th{background:#27272a;color:#a1a1aa;} tr:nth-child(even){background:#202023;}</style>");
        sb.AppendLine($"</head><body><h1>{title}</h1>");

        if (node is JsonArray arr && arr.Count > 0 && arr.All(x => x is JsonObject))
        {
            var cols = arr.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().ToList();
            sb.AppendLine("<table><thead><tr>");
            foreach (var c in cols) sb.Append($"<th>{System.Net.WebUtility.HtmlEncode(c)}</th>");
            sb.AppendLine("</tr></thead><tbody>");
            foreach (var row in arr.OfType<JsonObject>())
            {
                sb.Append("<tr>");
                foreach (var c in cols) sb.Append($"<td>{System.Net.WebUtility.HtmlEncode(ScalarString(row[c]))}</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
        }
        else if (node is JsonObject obj)
        {
            sb.AppendLine("<table><thead><tr><th>Chiave</th><th>Valore</th></tr></thead><tbody>");
            foreach (var kv in obj)
            {
                sb.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(kv.Key)}</td><td>{System.Net.WebUtility.HtmlEncode(ScalarString(kv.Value))}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }
        else
        {
            sb.AppendLine($"<pre>{System.Net.WebUtility.HtmlEncode(node?.ToJsonString(TextFiles.ReadableJson) ?? "null")}</pre>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
