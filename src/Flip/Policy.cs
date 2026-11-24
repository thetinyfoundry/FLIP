using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using Flip.Core;
namespace Flip;

internal static class Evidence
{
    static readonly object Sync = new();
    internal static volatile bool Enabled;
    internal static string DirectoryPath = Path.Combine(StoreEdition.LocalState ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flip"), "logs");

    internal static void Log(string name, object? detail = null)
    {
        if (!Enabled) return;
        try
        {
            object? sanitizedDetail = detail is null ? null : SanitizeDetail(detail);
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "events.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, name, detail = sanitizedDetail }, new JsonSerializerOptions { IncludeFields = true }) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string SanitizeString(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string value = raw;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "data:[redacted]";

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            value = value.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        value = Regex.Replace(value, @"[A-Za-z]:\\Users\\[^\\""\r\n]+", @"%USERPROFILE%", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"data:[a-zA-Z0-9/+.-]+;base64,[A-Za-z0-9+/=]+", "data:[redacted]", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"https?://[^\s""'<>]+", "[redacted-url]", RegexOptions.IgnoreCase);

        if (value.Length > 200)
            value = value[..200] + "…";
        return value;
    }

    private static JsonNode? SanitizeDetail(object detail)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(detail, new JsonSerializerOptions { IncludeFields = true });
            return SanitizeNode(node);
        }
        catch
        {
            return JsonValue.Create(SanitizeString(detail.ToString()));
        }
    }

    private static JsonNode? SanitizeNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue val when val.TryGetValue<string>(out var s):
                return JsonValue.Create(SanitizeString(s));
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                    arr[i] = SanitizeNode(arr[i]);
                return arr;
            case JsonObject obj:
                foreach (string key in obj.Select(kv => kv.Key).ToArray())
                    obj[key] = SanitizeNode(obj[key]);
                return obj;
            default:
                return node;
        }
    }
}
internal static class Policy
{
    internal static readonly string[] Images = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif"];
    internal static readonly string[] Documents = [".txt", ".md", ".markdown", ".html", ".htm", ".docx", ".odt", ".rtf", ".epub", ".rst", ".fb2"];
    internal static readonly string[] Audio = [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus"];
    internal static readonly string[] Video = [".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v"];
    internal static readonly string[] Data = [".json", ".yaml", ".yml", ".xml", ".csv"];
    internal static string[] Outputs(IEnumerable<string> paths) => Outputs(paths, DocumentTools.HasPandoc, DocumentTools.HasOffice, MediaTools.HasFfmpeg);
    internal static string[] Outputs(IEnumerable<string> paths, bool pandoc, bool office) => Outputs(paths, pandoc, office, MediaTools.HasFfmpeg);
    internal static string[] Outputs(IEnumerable<string> paths, bool pandoc, bool office, bool ffmpeg)
    {
        var all = paths.ToArray(); if (all.Length is 0 or > 128) return [];
        IEnumerable<string>? shared = null;
        foreach (var p in all)
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            string[] targets = Images.Contains(ext) ? ["PNG", "JPG", "WEBP", "PDF", "BMP", "TIFF", "GIF"] :
                Data.Contains(ext) ? ["JSON", "YAML", "XML", "CSV", "TXT", "HTML", "MD", "PDF"] :
                Audio.Contains(ext) && ffmpeg ? ["WAV", "MP3", "FLAC", "OGG", "M4A", "AAC"] :
                Video.Contains(ext) && ffmpeg ? ["MP4", "MP3", "WAV", "MKV", "WEBM", "AVI", "MOV"] :
                ext == ".pdf" ? (pandoc ? ["PNG", "JPG", "WEBP", "TXT", "DOCX", "MD", "HTML", "EPUB", "ODT", "RTF", "FB2", "RST"] : ["PNG", "JPG", "WEBP", "TXT"]) :
                ext == ".txt" && !pandoc ? ["PDF", "PNG", "JPG", "WEBP"] :
                Documents.Contains(ext) && pandoc ? (office || ext == ".txt" ? ["PDF", "DOCX", "EPUB", "MD", "HTML", "TXT", "ODT", "RTF", "PNG", "JPG", "WEBP", "FB2", "RST"] : ["DOCX", "EPUB", "MD", "HTML", "TXT", "ODT", "RTF", "FB2", "RST"]) : [];
            string same = ext.TrimStart('.') switch { "jpeg" => "JPG", "tif" => "TIFF", "markdown" => "MD", "htm" => "HTML", "yml" => "YAML", var e => e.ToUpperInvariant() };
            targets = targets.Where(t => t != same).ToArray();
            shared = shared == null ? targets : shared.Intersect(targets);
        }
        return shared?.ToArray() ?? [];
    }
    internal static string AvailabilityMessage(string[] paths)
    {
        if (paths.Length == 0) return Loc.T("Trascina qui il file", "Drop files here");
        if (Outputs(paths).Length > 0) return "";
        string noExt = Loc.T("senza estensione", "no extension");
        var unsupported = paths.Where(p => Outputs([p]).Length == 0)
            .Select(p => Path.GetExtension(p).TrimStart('.').ToUpperInvariant())
            .Select(e => string.IsNullOrEmpty(e) ? noExt : e.Length > 18 ? e[..18] : e)
            .Distinct().Take(4).ToArray();
        var listed = unsupported.Length > 0 ? unsupported : paths
            .Select(p => Path.GetExtension(p).TrimStart('.').ToUpperInvariant())
            .Select(e => string.IsNullOrEmpty(e) ? noExt : e.Length > 18 ? e[..18] : e)
            .Distinct().Take(4).ToArray();
        return (paths.Length == 1
                ? Loc.T("1 file selezionato", "1 file selected")
                : Loc.F("{0} file selezionati", "{0} files selected", paths.Length)) +
            (unsupported.Length > 0
                ? "\n" + Loc.T("Non supportati: ", "Unsupported: ") + string.Join(", ", unsupported)
                : listed.Length > 0
                    ? "\n" + Loc.T("Formati incompatibili: ", "Incompatible formats: ") + string.Join(", ", listed)
                    : "") +
            "\n" + Loc.T("Nessun formato comune", "No common format");
    }
    internal static string[] ReadFiles(IDataObject data)
        => ValidateFilePaths(RawFilePaths(data));

    internal static string[] RawFilePaths(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop, false) && data.GetData(DataFormats.FileDrop, false) is string[] { Length: > 0 and <= 128 } paths ? paths : [];

    internal static string[] ValidateFilePaths(string[] paths)
    {
        try
        {
            foreach (var path in paths)
            {
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network || !File.Exists(path)) return [];
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Offline | FileAttributes.ReparsePoint)) != 0) return [];
            }
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
        return paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static DragDropEffects Effect(DragDropEffects allowed, bool valid) => valid && (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
}
