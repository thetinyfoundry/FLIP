using System.IO;
using System.Text.Json;
using System.Windows;
namespace Flip;

internal static class Evidence
{
    static readonly object Sync = new();
    internal static string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flip", "logs");
    internal static void Log(string name, object? detail = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = Path.Combine(DirectoryPath, "events.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, name, detail }, new JsonSerializerOptions { IncludeFields = true }) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
internal static class Policy
{
    internal static readonly string[] Images = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".gif"];
    internal static readonly string[] Documents = [".txt", ".md", ".markdown", ".html", ".htm", ".docx", ".odt", ".rtf", ".epub", ".rst", ".fb2"];
    internal static string[] Outputs(IEnumerable<string> paths) => Outputs(paths, DocumentTools.HasPandoc, DocumentTools.HasOffice);
    internal static string[] Outputs(IEnumerable<string> paths, bool pandoc, bool office)
    {
        var all = paths.ToArray(); if (all.Length is 0 or > 128) return [];
        IEnumerable<string>? shared = null;
        foreach (var p in all)
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            string[] targets = Images.Contains(ext) ? ["PNG", "JPG", "WEBP", "PDF", "BMP", "TIFF", "GIF"] :
                ext == ".pdf" ? (pandoc ? ["PNG", "JPG", "WEBP", "TXT", "DOCX", "MD", "HTML", "EPUB", "ODT", "RTF", "FB2", "RST"] : ["PNG", "JPG", "WEBP", "TXT"]) :
                ext == ".txt" && !pandoc ? ["PDF", "PNG", "JPG", "WEBP"] :
                Documents.Contains(ext) && pandoc ? (office || ext == ".txt" ? ["PDF", "DOCX", "EPUB", "MD", "HTML", "TXT", "ODT", "RTF", "PNG", "JPG", "WEBP", "FB2", "RST"] : ["DOCX", "EPUB", "MD", "HTML", "TXT", "ODT", "RTF", "FB2", "RST"]) : [];
            string same = ext.TrimStart('.') switch { "jpeg" => "JPG", "tif" => "TIFF", "markdown" => "MD", "htm" => "HTML", var e => e.ToUpperInvariant() };
            targets = targets.Where(t => t != same).ToArray();
            shared = shared == null ? targets : shared.Intersect(targets);
        }
        return shared?.ToArray() ?? [];
    }
    internal static string AvailabilityMessage(string[] paths)
    {
        if (paths.Length == 0) return "Trascina qui il file";
        if (Outputs(paths).Length > 0) return "";
        var unsupported = paths.Where(p => Outputs([p]).Length == 0)
            .Select(p => Path.GetExtension(p).TrimStart('.').ToUpperInvariant())
            .Select(e => string.IsNullOrEmpty(e) ? "senza estensione" : e.Length > 18 ? e[..18] : e)
            .Distinct().Take(4).ToArray();
        return (paths.Length == 1 ? "1 file selezionato" : $"{paths.Length} file selezionati") +
            (unsupported.Length > 0 ? "\nNon supportati: " + string.Join(", ", unsupported) : "") +
            "\nNessun formato comune";
    }
    internal static string[] ReadFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop, false) || data.GetData(DataFormats.FileDrop, false) is not string[] paths || paths.Length is 0 or > 128) return [];
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
