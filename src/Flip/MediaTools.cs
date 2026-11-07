using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Flip;

internal static class MediaTools
{
    static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (File.Exists(local)) return local;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Flip.csproj"))) continue;
            string? root = dir.Parent?.Parent?.FullName;
            if (root != null)
            {
                var bundled = Path.Combine(root, "tools", "ffmpeg", "ffmpeg.exe");
                if (File.Exists(bundled)) return bundled;
            }
            break;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string entry in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(entry, "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string wingetLink = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            if (File.Exists(wingetLink)) return wingetLink;

            string wingetPkgs = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPkgs))
            {
                foreach (string pkgDir in Directory.EnumerateDirectories(wingetPkgs, "*FFmpeg*", SearchOption.TopDirectoryOnly))
                {
                    var found = Directory.EnumerateFiles(pkgDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
            }
        }
        catch { }

        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string candidate = Path.Combine(root, "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    internal static readonly string? Ffmpeg = FindFfmpeg();
    internal static bool HasFfmpeg => Ffmpeg != null;

    static readonly Regex DurationRegex = new(@"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex TimeRegex = new(@"time=(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static double ParseSeconds(Match m) =>
        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600.0 +
        int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60.0 +
        double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

    internal static async Task Convert(string source, string target, string staged, CancellationToken token, Action<int>? onPercent = null)
    {
        if (Ffmpeg == null)
            throw new IOException("Per convertire file audio e video serve FFmpeg installato sul computer.");

        var args = new List<string> { "-y", "-nostdin", "-hide_banner", "-i", source };
        switch (target)
        {
            case "WAV":
                args.AddRange(["-vn", "-acodec", "pcm_s16le", "-ar", "44100", "-ac", "2"]);
                break;
            case "MP3":
                args.AddRange(["-vn", "-acodec", "libmp3lame", "-q:a", "2"]);
                break;
            case "FLAC":
                args.AddRange(["-vn", "-acodec", "flac"]);
                break;
            case "OGG":
                args.AddRange(["-vn", "-acodec", "libvorbis", "-q:a", "4"]);
                break;
            case "AAC":
                args.AddRange(["-vn", "-acodec", "aac", "-b:a", "192k"]);
                break;
            case "M4A":
                args.AddRange(["-vn", "-acodec", "aac", "-b:a", "192k", "-f", "ipod"]);
                break;
            case "MP4":
                args.AddRange(["-c:v", "libx264", "-preset", "fast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"]);
                break;
            case "MKV":
                args.AddRange(["-c:v", "libx264", "-preset", "fast", "-crf", "23", "-c:a", "aac", "-b:a", "160k"]);
                break;
            case "WEBM":
                args.AddRange(["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-deadline", "realtime", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k"]);
                break;
            case "AVI":
                args.AddRange(["-c:v", "mpeg4", "-q:v", "5", "-c:a", "libmp3lame", "-q:a", "4"]);
                break;
            case "MOV":
                args.AddRange(["-c:v", "libx264", "-preset", "fast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart"]);
                break;
            default:
                throw new IOException("Formato multimediale non supportato: " + target);
        }
        args.Add(staged);

        var start = new ProcessStartInfo(Ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(staged)!
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new IOException("Impossibile avviare FFmpeg.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));

        var errorLog = new StringBuilder();
        double totalSeconds = 0;
        onPercent?.Invoke(5);

        var stderrTask = Task.Run(async () =>
        {
            char[] buffer = new char[512];
            var lineBuffer = new StringBuilder();
            while (true)
            {
                int read = await process.StandardError.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
                if (read <= 0) break;
                string chunk = new(buffer, 0, read);
                if (errorLog.Length < 16384) errorLog.Append(chunk);
                lineBuffer.Append(chunk);
                string current = lineBuffer.ToString();

                if (totalSeconds <= 0)
                {
                    var dm = DurationRegex.Match(current);
                    if (dm.Success) totalSeconds = ParseSeconds(dm);
                }
                if (totalSeconds > 0)
                {
                    var matches = TimeRegex.Matches(current);
                    if (matches.Count > 0)
                    {
                        double elapsed = ParseSeconds(matches[^1]);
                        int pct = Math.Clamp((int)Math.Round(elapsed / totalSeconds * 100.0), 5, 98);
                        onPercent?.Invoke(pct);
                    }
                }
                if (lineBuffer.Length > 2048)
                    lineBuffer.Remove(0, lineBuffer.Length - 512);
            }
        }, timeout.Token);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stderrTask, stdoutTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            if (!token.IsCancellationRequested)
                throw new IOException("La conversione multimediale ha superato il tempo massimo di 10 minuti.");
            throw;
        }

        if (process.ExitCode != 0)
        {
            string details = errorLog.ToString().Trim();
            if (details.Length > 600) details = details[^600..];
            throw new IOException($"FFmpeg non riuscito (exit {process.ExitCode}): {details}");
        }

        if (!File.Exists(staged) || new FileInfo(staged).Length == 0)
            throw new IOException("FFmpeg non ha generato un file di output valido.");

        onPercent?.Invoke(100);
    }
}

internal static class DataTools
{
    internal static void Convert(string source, string target, string staged)
    {
        if (new FileInfo(source).Length > 20 * 1024 * 1024)
            throw new IOException("File dati oltre il limite di 20 MiB.");

        string ext = Path.GetExtension(source).ToLowerInvariant();
        string raw = File.ReadAllText(source, new UTF8Encoding(false, true));
        if (string.IsNullOrWhiteSpace(raw))
            throw new IOException("Il file sorgente è vuoto.");

        JsonNode? root = ext switch
        {
            ".json" => ParseJson(raw),
            ".csv" => ParseCsv(raw),
            ".xml" => ParseXml(raw),
            ".yaml" or ".yml" => ParseYaml(raw),
            _ => throw new IOException("Estensione dati non supportata: " + ext)
        };

        var utf8NoBom = new UTF8Encoding(false);
        switch (target)
        {
            case "JSON":
                File.WriteAllText(staged, root?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", utf8NoBom);
                break;
            case "YAML":
                File.WriteAllText(staged, ToYaml(root, 0), utf8NoBom);
                break;
            case "XML":
                File.WriteAllText(staged, ToXml(root), utf8NoBom);
                break;
            case "CSV":
                File.WriteAllText(staged, ToCsv(root), utf8NoBom);
                break;
            case "TXT":
                File.WriteAllText(staged, ToReadableText(root), utf8NoBom);
                break;
            case "MD":
                File.WriteAllText(staged, ToMarkdown(root, Path.GetFileName(source)), utf8NoBom);
                break;
            case "HTML":
                File.WriteAllText(staged, ToHtml(root, Path.GetFileName(source)), utf8NoBom);
                break;
            case "PDF":
                ConversionEngine.TextPdf(ToReadableText(root), staged);
                break;
            default:
                throw new IOException("Formato di destinazione non supportato per i dati: " + target);
        }
    }

    static JsonNode? ParseJson(string raw)
    {
        try
        {
            return JsonNode.Parse(raw, new JsonNodeOptions { PropertyNameCaseInsensitive = false }, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new IOException("JSON non valido: " + ex.Message);
        }
    }

    static JsonNode ParseCsv(string raw)
    {
        var rows = ReadCsvRows(raw);
        if (rows.Count == 0) throw new IOException("CSV vuoto.");
        var headers = rows[0];
        var array = new JsonArray();
        if (rows.Count == 1)
        {
            var single = new JsonObject();
            for (int i = 0; i < headers.Count; i++) single[$"col_{i + 1}"] = headers[i];
            array.Add(single);
            return array;
        }
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            var obj = new JsonObject();
            for (int c = 0; c < headers.Count; c++)
            {
                string key = string.IsNullOrWhiteSpace(headers[c]) ? $"col_{c + 1}" : headers[c].Trim();
                string val = c < row.Count ? row[c] : "";
                obj[key] = ParseScalarNode(val);
            }
            array.Add(obj);
        }
        return array;
    }

    static List<List<string>> ReadCsvRows(string text)
    {
        var result = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(ch);
            }
            else
            {
                if (ch == '"') inQuotes = true;
                else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (ch is '\r' or '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    result.Add(row); row = [];
                }
                else cell.Append(ch);
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); result.Add(row); }
        return result;
    }

    static JsonNode? ParseXml(string raw)
    {
        try
        {
            var doc = XDocument.Parse(raw);
            return doc.Root == null ? new JsonObject() : XmlElementToJson(doc.Root);
        }
        catch (Exception ex)
        {
            throw new IOException("XML non valido: " + ex.Message);
        }
    }

    static JsonNode? XmlElementToJson(XElement element)
    {
        if (!element.HasElements && !element.HasAttributes)
            return ParseScalarNode(element.Value);

        var obj = new JsonObject();
        foreach (var attr in element.Attributes())
            obj["@" + attr.Name.LocalName] = ParseScalarNode(attr.Value);

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

        if (!element.HasElements && element.HasAttributes && !string.IsNullOrWhiteSpace(element.Value))
            obj["#text"] = ParseScalarNode(element.Value);

        return obj;
    }

    static JsonNode ParseYaml(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var rootObj = new JsonObject();
        var rootArr = new JsonArray();
        bool isArrayRoot = false;
        string? currentSection = null;
        JsonObject? currentSubObj = null;

        foreach (string rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#') || trimmed == "---" || trimmed == "...") continue;

            int indent = rawLine.TakeWhile(c => c == ' ').Count();
            if (trimmed.StartsWith("- "))
            {
                string itemText = trimmed[2..].Trim();
                int colon = itemText.IndexOf(':');
                if (colon > 0)
                {
                    var itemObj = new JsonObject
                    {
                        [itemText[..colon].Trim().Trim('"', '\'')] = ParseScalarNode(itemText[(colon + 1)..].Trim())
                    };
                    if (currentSection != null && rootObj[currentSection] is JsonArray secArr) secArr.Add(itemObj);
                    else { isArrayRoot = true; rootArr.Add(itemObj); }
                }
                else
                {
                    if (currentSection != null && rootObj[currentSection] is JsonArray secArr) secArr.Add(ParseScalarNode(itemText));
                    else { isArrayRoot = true; rootArr.Add(ParseScalarNode(itemText)); }
                }
                continue;
            }

            int idx = trimmed.IndexOf(':');
            if (idx > 0)
            {
                string key = trimmed[..idx].Trim().Trim('"', '\'');
                string val = trimmed[(idx + 1)..].Trim();
                if (string.IsNullOrEmpty(val))
                {
                    currentSection = key;
                    currentSubObj = new JsonObject();
                    rootObj[key] = currentSubObj;
                }
                else if (indent > 0 && currentSubObj != null)
                {
                    currentSubObj[key] = ParseScalarNode(val);
                }
                else
                {
                    currentSection = null;
                    currentSubObj = null;
                    rootObj[key] = ParseScalarNode(val);
                }
            }
        }
        return isArrayRoot && rootObj.Count == 0 ? rootArr : rootObj;
    }

    static JsonNode? ParseScalarNode(string value)
    {
        string v = value.Trim();
        if (v.Length >= 2 && ((v.StartsWith('"') && v.EndsWith('"')) || (v.StartsWith('\'') && v.EndsWith('\''))))
            return JsonValue.Create(v[1..^1]);
        if (v.Equals("null", StringComparison.OrdinalIgnoreCase) || v == "~") return null;
        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return JsonValue.Create(l);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) && !double.IsInfinity(d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    static string ToYaml(JsonNode? node, int indent)
    {
        string pad = new(' ', indent * 2);
        if (node is null) return "null\n";
        if (node is JsonValue val)
            return FormatYamlScalar(val) + "\n";
        if (node is JsonArray arr)
        {
            if (arr.Count == 0) return "[]\n";
            var sb = new StringBuilder();
            foreach (var item in arr)
            {
                if (item is JsonObject or JsonArray)
                {
                    string nested = ToYaml(item, indent + 1).TrimStart();
                    sb.Append(pad).Append("- ").Append(nested);
                }
                else
                {
                    sb.Append(pad).Append("- ").Append(ToYaml(item, 0));
                }
            }
            return sb.ToString();
        }
        if (node is JsonObject obj)
        {
            if (obj.Count == 0) return "{}\n";
            var sb = new StringBuilder();
            foreach (var kv in obj)
            {
                if (kv.Value is JsonObject or JsonArray)
                {
                    sb.Append(pad).Append(kv.Key).Append(":\n").Append(ToYaml(kv.Value, indent + 1));
                }
                else
                {
                    sb.Append(pad).Append(kv.Key).Append(": ").Append(ToYaml(kv.Value, 0));
                }
            }
            return sb.ToString();
        }
        return node.ToJsonString() + "\n";
    }

    static string FormatYamlScalar(JsonValue val)
    {
        var el = val.GetValue<JsonElement>();
        return el.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => el.GetRawText(),
            _ => NeedsYamlQuotes(el.GetString() ?? "") ? JsonSerializer.Serialize(el.GetString() ?? "") : (el.GetString() ?? "")
        };
    }

    static bool NeedsYamlQuotes(string s) =>
        string.IsNullOrEmpty(s) || s.Contains(':') || s.Contains('#') || s.Contains('\n') || s.Contains('\r') || s.Contains('"') || s.StartsWith(' ') || s.EndsWith(' ') || s.StartsWith('-');

    static string ToXml(JsonNode? node)
    {
        var root = new XElement("root");
        PopulateXml(root, node, "item");
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
        return doc.ToString();
    }

    static void PopulateXml(XElement parent, JsonNode? node, string itemName)
    {
        if (node is null) return;
        if (node is JsonValue val)
        {
            var el = val.GetValue<JsonElement>();
            parent.Value = el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.GetRawText();
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
                if (kv.Key.StartsWith('@') && kv.Value is JsonValue attrVal)
                {
                    parent.SetAttributeValue(SanitizeXmlName(kv.Key[1..]), attrVal.ToString());
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

    static string SanitizeXmlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "item";
        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        if (!char.IsLetter(sb[0]) && sb[0] != '_') sb.Insert(0, '_');
        return sb.ToString();
    }

    static string ToCsv(JsonNode? node)
    {
        var sb = new StringBuilder();
        if (node is JsonArray arr && arr.Count > 0)
        {
            if (arr.All(x => x is JsonObject))
            {
                var columns = arr.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().ToList();
                sb.AppendLine(string.Join(",", columns.Select(EscapeCsv)));
                foreach (var obj in arr.OfType<JsonObject>())
                {
                    sb.AppendLine(string.Join(",", columns.Select(c => EscapeCsv(ScalarString(obj[c])))));
                }
                return sb.ToString();
            }
            sb.AppendLine("value");
            foreach (var item in arr)
                sb.AppendLine(EscapeCsv(ScalarString(item)));
            return sb.ToString();
        }
        if (node is JsonObject rootObj)
        {
            var firstArray = rootObj.FirstOrDefault(kv => kv.Value is JsonArray a && a.Count > 0 && a.All(x => x is JsonObject));
            if (firstArray.Value is JsonArray nestedArr)
                return ToCsv(nestedArr);

            sb.AppendLine("key,value");
            foreach (var kv in rootObj)
                sb.AppendLine($"{EscapeCsv(kv.Key)},{EscapeCsv(ScalarString(kv.Value))}");
            return sb.ToString();
        }
        sb.AppendLine("value");
        sb.AppendLine(EscapeCsv(ScalarString(node)));
        return sb.ToString();
    }

    static string ScalarString(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v)
        {
            var el = v.GetValue<JsonElement>();
            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.GetRawText();
        }
        return node.ToJsonString();
    }

    static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    static string ToReadableText(JsonNode? node) =>
        node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    static string ToMarkdown(JsonNode? node, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        if (node is JsonArray arr && arr.Count > 0 && arr.All(x => x is JsonObject))
        {
            var cols = arr.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().ToList();
            sb.AppendLine("| " + string.Join(" | ", cols) + " |");
            sb.AppendLine("| " + string.Join(" | ", cols.Select(_ => "---")) + " |");
            foreach (var obj in arr.OfType<JsonObject>())
            {
                sb.AppendLine("| " + string.Join(" | ", cols.Select(c => ScalarString(obj[c]).Replace("|", "\\|").Replace("\n", " "))) + " |");
            }
            return sb.ToString();
        }
        sb.AppendLine("```json");
        sb.AppendLine(ToReadableText(node));
        sb.AppendLine("```");
        return sb.ToString();
    }

    static string ToHtml(JsonNode? node, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{WebUtility.HtmlEncode(title)}</title>");
        sb.AppendLine("<style>body{font-family:system-ui,-apple-system,sans-serif;background:#181a1c;color:#f1f3f5;padding:28px;margin:0}h1{font-size:20px;margin-bottom:16px;color:#ff8c24}table{border-collapse:collapse;width:100%;background:#222528;border-radius:8px;overflow:hidden}th,td{border:1px solid #363a3e;padding:10px 12px;text-align:left;font-size:14px}th{background:#2c3034;color:#ff8c24}pre{background:#222528;padding:18px;border-radius:8px;overflow:auto;border:1px solid #363a3e;font-family:Consolas,monospace;font-size:13px;line-height:1.5}</style>");
        sb.AppendLine($"</head><body><h1>{WebUtility.HtmlEncode(title)}</h1>");
        if (node is JsonArray arr && arr.Count > 0 && arr.All(x => x is JsonObject))
        {
            var cols = arr.OfType<JsonObject>().SelectMany(o => o.Select(kv => kv.Key)).Distinct().ToList();
            sb.AppendLine("<table><thead><tr>");
            foreach (var col in cols) sb.Append($"<th>{WebUtility.HtmlEncode(col)}</th>");
            sb.AppendLine("</tr></thead><tbody>");
            foreach (var obj in arr.OfType<JsonObject>())
            {
                sb.Append("<tr>");
                foreach (var col in cols) sb.Append($"<td>{WebUtility.HtmlEncode(ScalarString(obj[col]))}</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
        }
        else
        {
            sb.Append("<pre>").Append(WebUtility.HtmlEncode(ToReadableText(node))).AppendLine("</pre>");
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
