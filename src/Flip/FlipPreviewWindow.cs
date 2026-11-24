using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Flip.Core;
using Flip.Engines;
using Flip.Routing;
using SkiaSharp;
using Shapes = System.Windows.Shapes;

namespace Flip;

/// <summary>
/// Dati di un'anteprima QuickLook: file generati in <c>%TEMP%\FLIP\Preview\&lt;guid&gt;</c> e
/// impostazioni originali da usare solo se l'utente decide di salvare.
/// </summary>
internal sealed class FlipPreviewSession
{
    public required string ToolId { get; init; }
    public required string ToolDisplayName { get; init; }
    public required string SourcePath { get; init; }
    public required string PreviewDirectory { get; init; }
    public required string PrimaryPath { get; init; }
    public string? BlueprintPath { get; init; }
    /// <summary>Mesh normalizzata con normali smooth per il viewport 3D interattivo (null se non disponibile).</summary>
    public Mesh3DVectorGeoEngine.InteractiveMeshData? InteractiveMesh { get; init; }
    public int? PdfPageNumber { get; init; }
    public IReadOnlyList<string> HexColors { get; init; } = [];
    public string Metadata { get; init; } = "";
    public string? TextContent { get; init; }
    public required FlipSettings SaveSettings { get; init; }
    public long GenerationMs { get; init; }
    public bool Is3D => BlueprintPath is not null;
    public bool IsTextMode => TextContent is not null;

    /// <summary>Elimina la cartella temporanea dell'anteprima (nessun file resta su disco).</summary>
    public void DeleteTemporaryFiles()
    {
        try
        {
            if (Directory.Exists(PreviewDirectory)) Directory.Delete(PreviewDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Evidence.Log("preview.cleanup.failed", ex.Message);
        }
    }
}

/// <summary>
/// Genera le anteprime FLIP QuickLook in una cartella temporanea isolata, senza toccare la cartella dell'utente.
/// Usato solo dal flusso interattivo della ruota: <see cref="IQuickTool.ExecuteAsync"/> resta invariato per i test.
/// </summary>
internal static class FlipPreviewService
{
    static readonly HashSet<string> PreviewTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "turntable_3d", "palette", "waveform", UniversalQuickLookTool.ToolId
    };

    internal static readonly HashSet<string> MeshExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "stl", "fbx", "glb", "gltf", "ply", "3mf", "dae"
    };

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "tif"
    };

    static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "wav", "mp3", "flac", "m4a", "ogg", "aac", "opus"
    };

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "srt", "vtt", "ass", "ssa", "sub", "sbv", "lrc",
        "md", "markdown", "json", "jsonl", "ndjson", "csv", "tsv",
        "xml", "yaml", "yml", "toml", "ini", "env", "log", "reg", "properties",
        "html", "htm", "xhtml", "rst", "adoc", "asciidoc", "tex", "latex",
        "sql", "py", "cs", "js", "ts", "jsx", "tsx", "css", "scss",
        "cpp", "c", "h", "hpp", "java", "go", "rs", "rb", "php",
        "sh", "ps1", "bat", "cmd", "kml", "gpx", "geojson", "vcf", "eml"
    };

    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v", "mpg", "mpeg", "ts", "m2ts", "ogv", "3gp"
    };

    internal sealed class UniversalQuickLookTool : IQuickTool
    {
        internal const string ToolId = "quicklook_universal";
        internal static readonly UniversalQuickLookTool Instance = new();
        public string Id => ToolId;
        public string DisplayName => "FLIP QuickLook";
        public string IconGlyph => "\uE890";
        public bool SupportsMultipleFiles => false;
        public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings) => true;
        public Task<QuickToolResult> ExecuteAsync(QuickToolContext context) =>
            Task.FromResult(new QuickToolResult(true, context.InputFilePaths));
    }

    internal static string PreviewRoot => Path.Combine(Path.GetTempPath(), "FLIP", "Preview");

    /// <summary>
    /// Strumento usato da Spazio o dal pulsante 👁 ANTEPRIMA:
    /// 3D → Turntable/Viewport istantaneo, immagine → Palette #HEX, audio → Onda Sonora,
    /// qualsiasi altro file (testo, sottotitoli, codice, PDF, video, archivi) → UniversalQuickLookTool.
    /// </summary>
    internal static IQuickTool? ResolveQuickLookTool(string path, FlipSettings settings)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (MeshExtensions.Contains(ext))
            return RoutePlanner.FindQuickTool("turntable_3d") ?? UniversalQuickLookTool.Instance;
        if (ImageExtensions.Contains(ext))
            return RoutePlanner.FindQuickTool("palette") ?? UniversalQuickLookTool.Instance;
        if (AudioExtensions.Contains(ext) && (ext == "wav" || CliResolver.Resolve("ffmpeg", settings) is not null))
            return RoutePlanner.FindQuickTool("waveform") ?? UniversalQuickLookTool.Instance;
        return UniversalQuickLookTool.Instance;
    }

    internal static bool Supports(IQuickTool tool, QuickToolContext context) =>
        PreviewTools.Contains(tool.Id) && context.InputFilePaths.Count == 1;

    internal static async Task<FlipPreviewSession> PrepareAsync(IQuickTool tool, QuickToolContext context, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(context);
        if (context.InputFilePaths.Count != 1) throw new ArgumentException(Loc.T("L'anteprima QuickLook richiede un solo file.", "QuickLook requires a single file."), nameof(context));

        string source = Path.GetFullPath(context.InputFilePaths[0]);
        var saveSettings = context.Settings.CreateSnapshot();
        string directory = Path.Combine(PreviewRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var watch = Stopwatch.StartNew();

        try
        {
            string ext = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();

            // 1. Modelli 3D: apertura istantanea (< 0.2s) col Viewport3D interattivo + Blueprint 4 viste,
            //    senza attendere la renderizzazione software dei 24 fotogrammi della GIF Turntable!
            if (tool.Id.Equals("turntable_3d", StringComparison.OrdinalIgnoreCase) && MeshExtensions.Contains(ext))
            {
                var stats = await Task.Run<(Mesh3DVectorGeoEngine.MeshStats info, string bpPath, Mesh3DVectorGeoEngine.InteractiveMeshData? mesh)>(() =>
                {
                    token.ThrowIfCancellationRequested();
                    string bpPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + "_blueprint.png");
                    try
                    {
                        var (mesh, info) = Mesh3DVectorGeoEngine.PrepareQuickLookPreviewData(source, ext, bpPath, token);
                        return (info, bpPath, mesh);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Evidence.Log("preview.mesh3d.error", ex.Message);
                        var info = Mesh3DVectorGeoEngine.InspectMeshStats(source, ext);
                        if (!File.Exists(bpPath))
                            Mesh3DVectorGeoEngine.RenderBlueprintPng(source, ext, bpPath);
                        return (info, bpPath, null);
                    }
                }, token).ConfigureAwait(false);

                string turntablePath = Path.Combine(directory, Path.GetFileNameWithoutExtension(source) + "_turntable.gif");
                if (stats.mesh is null)
                {
                    // Se il viewport non è disponibile, usa il Blueprint come immagine primaria immediata
                    turntablePath = stats.bpPath;
                }

                string meshMeta = string.Format(CultureInfo.InvariantCulture,
                    Loc.T(
                        "Vertici: {0:N0}   ·   Triangoli: {1:N0}   ·   Dimensioni: {2:0.##} × {3:0.##} × {4:0.##}",
                        "Vertices: {0:N0}   ·   Triangles: {1:N0}   ·   Bounds: {2:0.##} × {3:0.##} × {4:0.##}"),
                    stats.info.VertexCount, stats.info.TriangleCount, stats.info.SizeX, stats.info.SizeY, stats.info.SizeZ);

                return new FlipPreviewSession
                {
                    ToolId = tool.Id,
                    ToolDisplayName = tool.DisplayName,
                    SourcePath = source,
                    PreviewDirectory = directory,
                    PrimaryPath = turntablePath,
                    BlueprintPath = stats.bpPath,
                    InteractiveMesh = stats.mesh,
                    HexColors = [],
                    Metadata = meshMeta,
                    SaveSettings = saveSettings,
                    GenerationMs = watch.ElapsedMilliseconds
                };
            }

            // 2. Anteprima universale per Testo, Sottotitoli, Codice, PDF, Video, Vettoriali o qualsiasi altro file
            if (tool.Id.Equals(UniversalQuickLookTool.ToolId, StringComparison.OrdinalIgnoreCase))
            {
                return await Task.Run(() => BuildUniversalSession(source, ext, directory, saveSettings, watch, token), token)
                    .ConfigureAwait(false);
            }

            // 3. Immagini (Palette #HEX) e Audio (Onda Sonora)
            var previewSettings = context.Settings.CreateMutableCopy();
            previewSettings.OutputDirectoryMode = OutputDirectoryMode.CustomFolder;
            previewSettings.CustomOutputDirectory = directory;
            previewSettings.ConflictPolicy = ConflictPolicy.AutoNumber;
            previewSettings.KeepOnlyIfSmaller = false;
            previewSettings.ReplaceOriginalToRecycleBin = false;

            context.InputFilePaths = [source];
            context.Settings = previewSettings.CreateSnapshot();
            context.CancellationToken = token;

            var result = await Task.Run(() => tool.ExecuteAsync(context), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string? primary = result.OutputFilePaths.FirstOrDefault(path =>
                File.Exists(path) && IsInside(directory, path));
            if (!result.Success || primary is null)
                throw new IOException(string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? Loc.F("{0}: anteprima non generata.", "{0}: preview could not be generated.", tool.DisplayName)
                    : result.ErrorMessage);

            IReadOnlyList<string> hex = [];
            string metadata;

            if (tool.Id.Equals("palette", StringComparison.OrdinalIgnoreCase))
            {
                (hex, metadata) = await Task.Run(() =>
                {
                    using var bitmap = SKBitmap.Decode(source) ?? throw new IOException(Loc.F("Immagine non decodificabile: {0}", "Could not decode image: {0}", source));
                    var colors = PaletteQuickTool.ExtractHexPalette(bitmap, 6);
                    return (colors, string.Format(CultureInfo.InvariantCulture,
                        Loc.T("{0} × {1} px   ·   {2}   ·   {3} colori dominanti", "{0} × {1} px   ·   {2}   ·   {3} dominant colors"),
                        bitmap.Width, bitmap.Height, FormatBytes(new FileInfo(source).Length), colors.Count));
                }, token).ConfigureAwait(false);
            }
            else
            {
                metadata = Loc.F("{0}   ·   {1}   ·   Onda sonora PNG", "{0}   ·   {1}   ·   Waveform PNG", ext.ToUpperInvariant(), FormatBytes(new FileInfo(source).Length));
            }

            return new FlipPreviewSession
            {
                ToolId = tool.Id,
                ToolDisplayName = tool.DisplayName,
                SourcePath = source,
                PreviewDirectory = directory,
                PrimaryPath = primary,
                BlueprintPath = null,
                InteractiveMesh = null,
                HexColors = hex,
                Metadata = metadata,
                SaveSettings = saveSettings,
                GenerationMs = watch.ElapsedMilliseconds
            };
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static FlipPreviewSession BuildUniversalSession(
        string source,
        string ext,
        string directory,
        FlipSettings saveSettings,
        Stopwatch watch,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fi = new FileInfo(source);
        long fileBytes = fi.Exists ? fi.Length : 0;
        string sizeStr = FormatBytes(fileBytes);
        string baseName = Path.GetFileNameWithoutExtension(source);

        // A) PDF: renderizza prima pagina in PNG + conteggio pagine/testo
        if (ext == "pdf")
        {
            try
            {
                string pdfPng = Path.Combine(directory, baseName + "_page1.png");
                using (var pdfStream = File.OpenRead(source))
                {
#pragma warning disable CA1416
                    PDFtoImage.Conversion.SavePng(pdfPng, pdfStream, page: 0, options: new(Dpi: 150));
#pragma warning restore CA1416
                }
                int pages = 1;
                try
                {
                    using var doc = UglyToad.PdfPig.PdfDocument.Open(source);
                    pages = doc.NumberOfPages;
                }
                catch { }
                if (File.Exists(pdfPng) && new FileInfo(pdfPng).Length > 0)
                {
                    return new FlipPreviewSession
                    {
                        ToolId = UniversalQuickLookTool.ToolId,
                        ToolDisplayName = Loc.T("Anteprima PDF", "PDF Preview"),
                        SourcePath = source,
                        PreviewDirectory = directory,
                        PrimaryPath = pdfPng,
                        PdfPageNumber = 1,
                        Metadata = Loc.F(
                            "PDF   ·   {0} {1}   ·   {2}",
                            "PDF   ·   {0} {1}   ·   {2}",
                            pages,
                            pages == 1 ? Loc.T("pagina", "page") : Loc.T("pagine", "pages"),
                            sizeStr),
                        SaveSettings = saveSettings,
                        GenerationMs = watch.ElapsedMilliseconds
                    };
                }
            }
            catch { }
        }

        // B) SVG / DXF / ICO: renderizza in PNG + palette colori
        if (ext is "svg" or "dxf" or "ico")
        {
            try
            {
                string renderedPng = Path.Combine(directory, baseName + "_preview.png");
                // L'anteprima resta nella sua cartella temporanea: con le impostazioni dell'utente potrebbe
                // sovrascrivere un PNG accanto all'originale o spostare l'originale nel Cestino.
                var previewSettings = saveSettings.CreateMutableCopy();
                previewSettings.OutputDirectoryMode = OutputDirectoryMode.CustomFolder;
                previewSettings.CustomOutputDirectory = directory;
                previewSettings.ConflictPolicy = ConflictPolicy.AutoNumber;
                previewSettings.ReplaceOriginalToRecycleBin = false;
                previewSettings.KeepOnlyIfSmaller = false;
                var res = RoutePlanner.ExecuteRouteAsync(source, "png", previewSettings.CreateSnapshot(), ct: token).GetAwaiter().GetResult();
                if (res.Success && File.Exists(res.OutputFilePath))
                {
                    File.Move(res.OutputFilePath, renderedPng, overwrite: true);
                    using var bmp = SKBitmap.Decode(renderedPng);
                    var colors = bmp != null ? PaletteQuickTool.ExtractHexPalette(bmp, 6) : [];
                    return new FlipPreviewSession
                    {
                        ToolId = UniversalQuickLookTool.ToolId,
                        ToolDisplayName = Loc.F("Anteprima {0}", "{0} Preview", ext.ToUpperInvariant()),
                        SourcePath = source,
                        PreviewDirectory = directory,
                        PrimaryPath = renderedPng,
                        HexColors = colors,
                        Metadata = bmp != null
                            ? $"{ext.ToUpperInvariant()}   ·   {bmp.Width} × {bmp.Height} px   ·   {sizeStr}"
                            : $"{ext.ToUpperInvariant()}   ·   {sizeStr}",
                        SaveSettings = saveSettings,
                        GenerationMs = watch.ElapsedMilliseconds
                    };
                }
            }
            catch { }
        }

        // C) Video: estrai frame via FFmpeg (se disponibile) o genera poster card con metadati completi
        if (VideoExtensions.Contains(ext))
        {
            string framePng = Path.Combine(directory, baseName + "_frame.png");
            string extraMeta = "";
            string? ffmpeg = CliResolver.Resolve("ffmpeg", saveSettings);
            if (ffmpeg is not null)
            {
                try
                {
                    var probe = CliResolver.RunProcessAsync(
                        ffmpeg,
                        ["-y", "-ss", "00:00:00.50", "-i", source, "-frames:v", "1", "-q:v", "2", framePng],
                        timeoutSeconds: 4).GetAwaiter().GetResult();
                    if (!File.Exists(framePng) || new FileInfo(framePng).Length == 0)
                    {
                        probe = CliResolver.RunProcessAsync(
                            ffmpeg,
                            ["-y", "-i", source, "-frames:v", "1", "-q:v", "2", framePng],
                            timeoutSeconds: 4).GetAwaiter().GetResult();
                    }
                    extraMeta = ParseFfmpegMediaSummary(probe.StdErr);
                }
                catch { }
            }

            if (!File.Exists(framePng) || new FileInfo(framePng).Length == 0)
                RenderMediaPosterPng(source, ext.ToUpperInvariant() + " VIDEO", sizeStr, extraMeta, framePng);

            using var vBmp = SKBitmap.Decode(framePng);
            var vColors = vBmp != null ? PaletteQuickTool.ExtractHexPalette(vBmp, 6) : [];
            string resInfo = vBmp != null && string.IsNullOrEmpty(extraMeta) ? $"   ·   {vBmp.Width} × {vBmp.Height} px" : extraMeta;
            return new FlipPreviewSession
            {
                ToolId = UniversalQuickLookTool.ToolId,
                ToolDisplayName = Loc.T("Anteprima Video", "Video Preview"),
                SourcePath = source,
                PreviewDirectory = directory,
                PrimaryPath = framePng,
                HexColors = vColors,
                Metadata = $"{ext.ToUpperInvariant()}   ·   {sizeStr}{resInfo}",
                SaveSettings = saveSettings,
                GenerationMs = watch.ElapsedMilliseconds
            };
        }

        // D) Archivi ZIP / CBZ / EPUB: elenco file contenuti
        if (ext is "zip" or "cbz" or "epub")
        {
            try
            {
                using var archive = ZipFile.OpenRead(source);
                var sb = new StringBuilder();
                sb.AppendLine(Loc.F("Archivio {0}: {1}", "{0} Archive: {1}", ext.ToUpperInvariant(), Path.GetFileName(source)));
                sb.AppendLine(Loc.F("Elementi totali: {0}   ·   Dimensione archivio: {1}", "Total entries: {0}   ·   Archive size: {1}", archive.Entries.Count, sizeStr));
                sb.AppendLine(new string('─', 72));
                foreach (var entry in archive.Entries.Take(400))
                {
                    sb.AppendLine($"{FormatBytes(entry.Length),10}   {entry.FullName}");
                }
                if (archive.Entries.Count > 400)
                    sb.AppendLine(Loc.F("… (+{0} altri elementi)", "… (+{0} more entries)", archive.Entries.Count - 400));

                string summaryText = sb.ToString();
                string txtPath = Path.Combine(directory, baseName + "_contents.txt");
                File.WriteAllText(txtPath, summaryText, Encoding.UTF8);
                return new FlipPreviewSession
                {
                    ToolId = UniversalQuickLookTool.ToolId,
                    ToolDisplayName = Loc.T("Ispezione Archivio", "Archive Inspector"),
                    SourcePath = source,
                    PreviewDirectory = directory,
                    PrimaryPath = txtPath,
                    TextContent = summaryText,
                    Metadata = Loc.F("{0}   ·   {1} file   ·   {2}", "{0}   ·   {1} files   ·   {2}", ext.ToUpperInvariant(), archive.Entries.Count, sizeStr),
                    SaveSettings = saveSettings,
                    GenerationMs = watch.ElapsedMilliseconds
                };
            }
            catch { }
        }

        // E) Testo, Sottotitoli, Codice, Configurazione, Dati o qualsiasi altro file
        var (textPreview, lines, words, isReadableText) = ReadTextOrFileSummary(source, ext, fi);
        string savedNotePath = Path.Combine(directory, baseName + "_preview.txt");
        File.WriteAllText(savedNotePath, textPreview, Encoding.UTF8);
        string toolTitle = TextExtensions.Contains(ext) || isReadableText
            ? Loc.T("Visualizzatore Testo & Codice", "Text & Code Viewer")
            : Loc.T("Ispezione File", "File Inspector");

        return new FlipPreviewSession
        {
            ToolId = UniversalQuickLookTool.ToolId,
            ToolDisplayName = toolTitle,
            SourcePath = source,
            PreviewDirectory = directory,
            PrimaryPath = savedNotePath,
            TextContent = textPreview,
            Metadata = Loc.F(
                "{0}   ·   {1}   ·   Righe: {2:N0}   ·   Parole: {3:N0}",
                "{0}   ·   {1}   ·   Lines: {2:N0}   ·   Words: {3:N0}",
                string.IsNullOrEmpty(ext) ? "FILE" : ext.ToUpperInvariant(), sizeStr, lines, words),
            SaveSettings = saveSettings,
            GenerationMs = watch.ElapsedMilliseconds
        };
    }

    private static (string Text, int Lines, int Words, bool IsReadableText) ReadTextOrFileSummary(string path, string ext, FileInfo fi)
    {
        const int maxPreviewBytes = 256 * 1024;
        byte[] buffer;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int toRead = (int)Math.Min(maxPreviewBytes, fs.Length);
            buffer = new byte[toRead];
            int offset = 0;
            while (offset < toRead)
            {
                int r = fs.Read(buffer, offset, toRead - offset);
                if (r <= 0) break;
                offset += r;
            }
            if (offset < buffer.Length) Array.Resize(ref buffer, offset);
        }

        int nullBytes = 0;
        int sampleLen = Math.Min(buffer.Length, 4096);
        for (int i = 0; i < sampleLen; i++)
        {
            if (buffer[i] == 0) nullBytes++;
        }

        bool looksText = TextExtensions.Contains(ext) || nullBytes == 0;
        if (looksText && buffer.Length > 0)
        {
            string text = Encoding.UTF8.GetString(buffer).TrimStart('\uFEFF');
            if (fi.Length > maxPreviewBytes)
                text += Loc.F(
                    "\n\n[… Anteprima troncata ai primi {0} su {1} totali …]",
                    "\n\n[… Preview truncated to the first {0} of {1} total …]",
                    FormatBytes(maxPreviewBytes), FormatBytes(fi.Length));

            int lineCount = 1;
            int wordCount = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (c == '\n') lineCount++;
                if (char.IsWhiteSpace(c)) inWord = false;
                else if (!inWord) { inWord = true; wordCount++; }
            }
            return (text, lineCount, wordCount, true);
        }

        bool hasDef = FormatRegistry.TryGet(ext, out var def);
        var sb = new StringBuilder();
        sb.AppendLine($"File:         {fi.Name}");
        sb.AppendLine(Loc.F("Formato:      {0}", "Format:       {0}", hasDef ? $"{def.DisplayName} (.{def.NormalizedExtension})" : ext.ToUpperInvariant()));
        sb.AppendLine(Loc.F("Categoria:    {0}", "Category:     {0}", hasDef ? def.Category.ToString() : Loc.T("Generico", "Generic")));
        sb.AppendLine(Loc.F("Dimensione:   {0} ({1:N0} byte)", "Size:         {0} ({1:N0} bytes)", FormatBytes(fi.Length), fi.Length));
        sb.AppendLine(Loc.F("Modificato:   {0:yyyy-MM-dd HH:mm:ss}", "Modified:     {0:yyyy-MM-dd HH:mm:ss}", fi.LastWriteTime));
        if (buffer.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.T("Primi 128 byte (Hex / ASCII):", "First 128 bytes (Hex / ASCII):"));
            sb.AppendLine(new string('─', 64));
            int dumpLen = Math.Min(buffer.Length, 128);
            for (int row = 0; row < dumpLen; row += 16)
            {
                var hexPart = new StringBuilder(48);
                var ascPart = new StringBuilder(16);
                for (int col = 0; col < 16; col++)
                {
                    if (row + col < dumpLen)
                    {
                        byte b = buffer[row + col];
                        hexPart.Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                        ascPart.Append(b is >= 32 and <= 126 ? (char)b : '.');
                    }
                    else
                    {
                        hexPart.Append("   ");
                    }
                }
                sb.AppendLine($"{row:X4}  {hexPart}  {ascPart}");
            }
        }
        string infoText = sb.ToString();
        int lines = infoText.Count(c => c == '\n') + 1;
        return (infoText, lines, 24, false);
    }

    private static string ParseFfmpegMediaSummary(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "";
        var parts = new List<string>();
        var durMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"Duration:\s*(\d{2}:\d{2}:\d{2}\.\d{2})");
        if (durMatch.Success) parts.Add(Loc.T("Durata: ", "Duration: ") + durMatch.Groups[1].Value);
        var resMatch = System.Text.RegularExpressions.Regex.Match(stderr, @"\b(\d{3,5}x\d{3,5})\b");
        if (resMatch.Success) parts.Add(resMatch.Groups[1].Value.Replace("x", " × ") + " px");
        return parts.Count > 0 ? "   ·   " + string.Join("   ·   ", parts) : "";
    }

    private static void RenderMediaPosterPng(string source, string badge, string sizeStr, string extraMeta, string outPath)
    {
        const int w = 1280, h = 720;
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(new SKColor(0x14, 0x16, 0x1D));

        using var cardPaint = new SKPaint { Color = new SKColor(0x1E, 0x21, 0x2B), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(80, 90, w - 80, h - 90), 22, 22, cardPaint);

        using var accentPaint = new SKPaint { Color = new SKColor(0xFF, 0x7A, 0x18), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(w / 2f - 48, 180, w / 2f + 48, 276), 20, 20, accentPaint);

        using var titleFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 34);
        using var subFont = new SKFont(SKTypeface.FromFamilyName("Consolas", SKFontStyle.Normal), 22);
        using var whitePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = new SKColor(0xA0, 0xA8, 0xB8), IsAntialias = true };

        string name = Path.GetFileName(source);
        if (name.Length > 48) name = name[..45] + "…";
        canvas.DrawText( badge, w / 2f, 340, SKTextAlign.Center, subFont, accentPaint);
        canvas.DrawText(name, w / 2f, 405, SKTextAlign.Center, titleFont, whitePaint);
        canvas.DrawText($"{sizeStr}{extraMeta}", w / 2f, 465, SKTextAlign.Center, subFont, mutedPaint);
        canvas.Flush();

        using var data = bmp.Encode(SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(outPath);
        data.SaveTo(fs);
    }

    /// <summary>Salva il file dell'anteprima nella destinazione configurata e lo registra nella cronologia.</summary>
    internal static IReadOnlyList<string> Save(FlipPreviewSession session, string previewFile)
    {
        if (!File.Exists(session.SourcePath)) throw new IOException(Loc.T("Il file sorgente non è più disponibile.", "The source file is no longer available."));
        if (!IsInside(session.PreviewDirectory, previewFile))
            throw new IOException(Loc.T("Il percorso di anteprima non appartiene alla sessione.", "The preview path does not belong to this session."));

        // Se l'utente salva mentre è nella vista 3D e la GIF Turntable non è ancora stata generata su disco,
        // la generiamo adesso on-demand così l'apertura dell'anteprima 3D resta istantanea (< 0.2s).
        if (!File.Exists(previewFile) && session.Is3D &&
            string.Equals(Path.GetExtension(previewFile), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            Mesh3DVectorGeoEngine.RenderTurntableGifToFile(
                session.SourcePath,
                Path.GetExtension(session.SourcePath),
                previewFile);
        }

        if (!File.Exists(previewFile))
            throw new IOException(Loc.T("Il file di anteprima non è più disponibile.", "The preview file is no longer available."));

        var batch = new OutputPathAllocator.Batch([session.SourcePath], session.SaveSettings);
        using var job = batch.CreateJob(session.SourcePath);
        string name = Path.GetFileName(previewFile);
        string staged = job.Stage(name);
        File.Copy(previewFile, staged, overwrite: false);
        var result = job.CommitOutputAndApplyRules([new(staged, name)], CancellationToken.None, allowRecycle: false);
        if (result.Error is not null) throw new IOException(result.Error);
        if (result.Outputs.Count == 0) throw new IOException(Loc.T("Nessun file salvato.", "No file was saved."));

        long sourceBytes = new FileInfo(session.SourcePath).Length;
        long outputBytes = result.Outputs.Sum(o => o.Bytes);
        SettingsStore.Current.RecordConversion(new RecentConversion
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            SourceFileName = Path.GetFileName(session.SourcePath),
            TargetExtension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant(),
            BytesSaved = sourceBytes - outputBytes,
            DurationMs = session.GenerationMs,
            Success = true
        });
        return result.Outputs.Select(o => o.Path).ToArray();
    }

    internal static bool IsInside(string directory, string path)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };
}

/// <summary>
/// FLIP QuickLook: finestra borderless dark, sempre in primo piano, per vedere il risultato di
/// Turntable 360° (Viewport 3D interattivo), Blueprint 4 viste, Palette #HEX e Onda Sonora prima di salvare.
/// Spazio/Esc chiudono ed eliminano i temporanei; Ctrl+C copia il frame; Ctrl+S salva.
/// </summary>
internal sealed class FlipPreviewWindow : Window
{
    static readonly SolidColorBrush Bg = Frozen(0x12, 0x13, 0x18);
    static readonly SolidColorBrush PanelBg = Frozen(0x1A, 0x1C, 0x23);
    static readonly SolidColorBrush StudioBg = Frozen(0x14, 0x13, 0x15);
    static readonly SolidColorBrush Stroke = Frozen(0x2A, 0x2E, 0x39);
    static readonly SolidColorBrush Accent = Frozen(0xFF, 0x7A, 0x18);
    static readonly SolidColorBrush AccentHover = Frozen(0xFF, 0x6B, 0x00);
    static readonly SolidColorBrush PillBg = Frozen(0x22, 0x25, 0x30);
    static readonly SolidColorBrush PillHover = Frozen(0x2E, 0x32, 0x40);
    static readonly SolidColorBrush TextPrimary = Frozen(0xF3, 0xF4, 0xF6);
    static readonly SolidColorBrush TextMuted = Frozen(0x9C, 0xA3, 0xAF);
    static readonly SolidColorBrush Danger = Frozen(0xEF, 0x44, 0x44);

    const double CameraFov = 38.0;
    const double DefaultDistance = 4.4;
    const double MinDistance = 0.6;
    const double MaxDistance = 30.0;
    const double AutoSpinDegreesPerSecond = 40.0;
    const double OrbitDegreesPerPixel = 0.45;
    const int MaxWireEdges = 160_000;

    static readonly Quaternion InitialOrientation =
        new Quaternion(new Vector3D(1, 0, 0), 14) * new Quaternion(new Vector3D(0, 1, 0), -32);

    enum ViewMode { Primary, Blueprint }
    enum DragKind { None, Orbit, Pan }

    sealed record PillColors(Brush Normal, Brush Hover);
    internal sealed record SavedView3DState(Quaternion Orientation, double PanX, double PanY, double PanZ, double Distance, bool AutoRotate);

    readonly FlipPreviewSession session;
    readonly Action<IReadOnlyList<string>>? onSaved;
    readonly Image image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    readonly DispatcherTimer timer = new(DispatcherPriority.Render);
    readonly TextBlock status = new() { Foreground = TextMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly List<(Border Pill, ViewMode Mode)> modePills = [];
    readonly List<(Border Pill, double Speed)> speedPills = [];
    readonly Stopwatch clock = Stopwatch.StartNew();

    // Viewport 3D interattivo (null quando la mesh non è disponibile: si usa la GIF Turntable)
    Border? viewportHost;
    Viewport3D? viewport;
    PerspectiveCamera? camera;
    Model3DGroup? meshGroup;
    GeometryModel3D? wireModel;
    readonly QuaternionRotation3D rotation = new(InitialOrientation);
    readonly TranslateTransform3D pan = new();
    Quaternion orientation = InitialOrientation;
    double distance = DefaultDistance;
    double targetDistance = DefaultDistance;
    double lastFrameSeconds;
    bool renderingHooked;
    DragKind drag;
    Point dragLast;
    bool wireframe;
    bool wireBuilding;

    Border? pausePill;
    Border? wirePill;
    Border? bookmarkPill;
    SavedView3DState? savedView;
    string swatchFgHex = "#F3F4F6";
    string swatchBgHex = "#121318";
    int swatchPickStep;
    Action? swapSwatchPair;
    Action? copySwatchPair;

    BitmapSource[] primaryFrames = [];
    TimeSpan[] primaryDelays = [];
    BitmapSource? blueprintFrame;
    ViewMode mode = ViewMode.Primary;
    double speed = 1.0;
    bool playing = true;
    int frameIndex;
    bool saving;
    bool cleaned;

    bool HasViewport => viewportHost is not null;
    bool IsGazeActive => session.SaveSettings.GazeAccessibilityMode || SettingsStore.Current.CreateSnapshot().GazeAccessibilityMode;

    /// <summary>
    /// Apre FlipPreviewWindow direttamente nell'editor <c>Ritaglia &amp; Oscura</c> (spicchio <c>tool:crop_interactive</c> della ruota).
    /// Nessuna anteprima temporanea: l'immagine originale viene letta in memoria e salvata solo su richiesta.
    /// </summary>
    internal static FlipPreviewWindow CreateCropEditor(string sourcePath, FlipSettings settings, Action<IReadOnlyList<string>>? onSaved = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException(Loc.T("Immagine sorgente non trovata.", "Source image not found."), sourcePath);
        if (!IsCropCapablePath(sourcePath))
            throw new NotSupportedException(Loc.T(
                "Il ritaglio interattivo supporta solo immagini raster (PNG, JPG, WEBP, BMP, GIF, TIFF).",
                "Interactive crop supports raster images only (PNG, JPG, WEBP, BMP, GIF, TIFF)."));
        var session = new FlipPreviewSession
        {
            ToolId = CropInteractiveToolId,
            ToolDisplayName = Loc.T("Ritaglia & Oscura", "Crop & Blur"),
            SourcePath = sourcePath,
            PreviewDirectory = Path.Combine(FlipPreviewService.PreviewRoot, Guid.NewGuid().ToString("N")),
            PrimaryPath = sourcePath,
            Metadata = FlipPreviewService.FormatBytes(new FileInfo(sourcePath).Length),
            SaveSettings = settings ?? new FlipSettings()
        };
        return new FlipPreviewWindow(session, onSaved, startInCropMode: true);
    }

    internal FlipPreviewWindow(FlipPreviewSession session, Action<IReadOnlyList<string>>? onSaved = null, bool startInCropMode = false)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.onSaved = onSaved;

        Title = "FLIP QuickLook";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(980, work.Width * 0.92);
        Height = Math.Min(860, work.Height * 0.92);
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI");

        if (session.InteractiveMesh is { } mesh)
        {
            try { BuildViewport(mesh); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
            {
                Evidence.Log("preview.viewport.error", ex.Message);
                viewportHost = null;
                viewport = null;
            }
        }
        LoadFrames();
        Content = BuildLayout();

        timer.Tick += (_, _) => AdvanceFrame();
        PreviewKeyDown += OnKey;
        // Aperta con Spazio durante un drag: un rilascio sopra la finestra non deve copiare/spostare nulla.
        AllowDrop = true;
        PreviewDragEnter += RejectDrop;
        PreviewDragOver += RejectDrop;
        PreviewDrop += RejectDrop;
        Loaded += (_, _) =>
        {
            Activate(); Focus(); ShowCurrent(); HookRendering(true);
            if (startInCropMode || IsCropSession) SetCropMode(true);
        };
        Closed += (_, _) => Cleanup();
    }

    static void RejectDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ── Caricamento frame (da memoria: nessun lock sui file temporanei) ──────

    void LoadFrames()
    {
        // Editor di ritaglio diretto: l'immagine viene decodificata (EXIF incluso) da EnsureCropSource.
        if (IsCropSession) return;
        // Con il viewport interattivo o in modalità testo non occorre decodificare frame raster primari.
        if (!HasViewport && !session.IsTextMode && File.Exists(session.PrimaryPath))
            (primaryFrames, primaryDelays) = DecodeFrames(session.PrimaryPath);
        if (session.BlueprintPath is { } blueprint && File.Exists(blueprint))
            blueprintFrame = DecodeFrames(blueprint).Frames.FirstOrDefault();
    }

    static (BitmapSource[] Frames, TimeSpan[] Delays) DecodeFrames(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frames = new BitmapSource[decoder.Frames.Count];
        var delays = new TimeSpan[frames.Length];
        for (int index = 0; index < frames.Length; index++)
        {
            var frame = decoder.Frames[index];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var bitmap = new WriteableBitmap(converted);
            bitmap.Freeze();
            frames[index] = bitmap;
            delays[index] = ReadGifDelay(frame);
        }
        if (frames.Length == 0) throw new IOException(Loc.T("Anteprima vuota: nessun fotogramma decodificabile.", "Empty preview: no decodable frames."));
        return (frames, delays);
    }

    static TimeSpan ReadGifDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/grctlext/Delay") is ushort centiseconds && centiseconds > 0)
                return TimeSpan.FromMilliseconds(Math.Max(20, centiseconds * 10));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
        return TimeSpan.FromMilliseconds(1000.0 / Turntable3DQuickTool.DefaultFps);
    }

    // ── Viewport 3D (WPF / DirectX) ──────────────────────────────────────────

    void BuildViewport(Mesh3DVectorGeoEngine.InteractiveMeshData data)
    {
        var geometry = new MeshGeometry3D();
        int vertexCount = data.VertexCount;
        var positions = new Point3DCollection(vertexCount);
        var normals = new Vector3DCollection(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            int o = i * 3;
            positions.Add(new Point3D(data.Positions[o], data.Positions[o + 1], data.Positions[o + 2]));
            normals.Add(new Vector3D(data.Normals[o], data.Normals[o + 1], data.Normals[o + 2]));
        }
        var indices = new Int32Collection(data.Indices.Length);
        foreach (int index in data.Indices) indices.Add(index);
        geometry.Positions = positions;
        geometry.Normals = normals;
        geometry.TriangleIndices = indices;
        geometry.Freeze();

        // Materiale "clay" scultoreo: diffuso caldo + speculare morbido.
        var clay = new MaterialGroup();
        clay.Children.Add(new DiffuseMaterial(Frozen(0xC4, 0xBD, 0xB4)));
        clay.Children.Add(new SpecularMaterial(Frozen(0x3A, 0x38, 0x36), 36));
        clay.Freeze();
        var back = new DiffuseMaterial(Frozen(0x5E, 0x58, 0x52));
        back.Freeze();

        meshGroup = new Model3DGroup();
        meshGroup.Children.Add(new GeometryModel3D(geometry, clay) { BackMaterial = back });
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(rotation));
        transform.Children.Add(pan);
        meshGroup.Transform = transform;

        // Illuminazione studio a 3 punti (fissa rispetto alla camera: il modello ruota sotto le luci).
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x1C, 0x1B, 0x1E)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xE6, 0xE0, 0xD6), new Vector3D(-0.55, -0.62, -0.56))); // key: front-top-right
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x4A, 0x50, 0x5E), new Vector3D(0.85, -0.18, -0.48)));  // fill: sinistra tenue
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xA8, 0xAC, 0xBA), new Vector3D(0.10, -0.38, 1.0)));    // rim: posteriore
        lights.Freeze();

        camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 0, distance),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = CameraFov,
            NearPlaneDistance = 0.01,
            FarPlaneDistance = 200
        };

        viewport = new Viewport3D { Camera = camera, ClipToBounds = true, IsHitTestVisible = false };
        viewport.Children.Add(new ModelVisual3D { Content = lights });
        viewport.Children.Add(new ModelVisual3D { Content = meshGroup });

        var host = new Border
        {
            Background = StudioBg,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Cursor = Cursors.SizeAll,
            Focusable = false,
            Child = viewport
        };
        host.MouseLeftButtonDown += OnViewportLeftDown;
        host.MouseRightButtonDown += OnViewportRightDown;
        host.MouseMove += OnViewportMove;
        host.MouseLeftButtonUp += OnViewportUp;
        host.MouseRightButtonUp += OnViewportUp;
        host.LostMouseCapture += (_, _) => drag = DragKind.None;
        host.MouseWheel += OnViewportWheel;
        viewportHost = host;
    }

    void OnViewportLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            ResetView();
            return;
        }
        BeginDrag((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? DragKind.Pan : DragKind.Orbit, e);
    }

    void OnViewportRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BeginDrag(DragKind.Pan, e);
    }

    void BeginDrag(DragKind kind, MouseButtonEventArgs e)
    {
        if (viewportHost is null) return;
        drag = kind;
        dragLast = e.GetPosition(viewportHost);
        if (kind == DragKind.Orbit && playing)
        {
            playing = false;
            UpdatePills();
        }
        viewportHost.CaptureMouse();
    }

    void OnViewportMove(object sender, MouseEventArgs e)
    {
        if (drag == DragKind.None || viewportHost is null) return;
        var now = e.GetPosition(viewportHost);
        double dx = now.X - dragLast.X, dy = now.Y - dragLast.Y;
        dragLast = now;
        if (drag == DragKind.Orbit)
        {
            // Rotazioni su assi del mondo: nessun gimbal lock ai poli, orbita libera a 360°.
            var yaw = new Quaternion(new Vector3D(0, 1, 0), dx * OrbitDegreesPerPixel);
            var pitch = new Quaternion(new Vector3D(1, 0, 0), dy * OrbitDegreesPerPixel);
            orientation = yaw * pitch * orientation;
            orientation.Normalize();
            rotation.Quaternion = orientation;
        }
        else
        {
            double height = Math.Max(1, viewportHost.ActualHeight);
            double unitsPerPixel = 2 * distance * Math.Tan(CameraFov * Math.PI / 360.0) / height;
            pan.OffsetX += dx * unitsPerPixel;
            pan.OffsetY -= dy * unitsPerPixel;
        }
    }

    void OnViewportUp(object sender, MouseButtonEventArgs e)
    {
        if (drag == DragKind.None) return;
        e.Handled = true;
        drag = DragKind.None;
        viewportHost?.ReleaseMouseCapture();
    }

    void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        targetDistance = Math.Clamp(targetDistance * Math.Pow(0.88, e.Delta / 120.0), MinDistance, MaxDistance);
    }

    void ResetView()
    {
        orientation = InitialOrientation;
        rotation.Quaternion = orientation;
        pan.OffsetX = 0;
        pan.OffsetY = 0;
        pan.OffsetZ = 0;
        targetDistance = DefaultDistance;
        SetStatus(Loc.T("Vista 3D ripristinata.", "3D view reset."), TextMuted);
    }

    void HookRendering(bool hook)
    {
        if (!HasViewport || hook == renderingHooked) return;
        renderingHooked = hook;
        if (hook)
        {
            lastFrameSeconds = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    void OnRendering(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - lastFrameSeconds, 0, 0.1);
        lastFrameSeconds = now;
        if (camera is null || mode != ViewMode.Primary) return;

        if (playing && drag == DragKind.None && speed > 0)
        {
            orientation = new Quaternion(new Vector3D(0, 1, 0), AutoSpinDegreesPerSecond * speed * dt) * orientation;
            orientation.Normalize();
            rotation.Quaternion = orientation;
        }

        if (Math.Abs(targetDistance - distance) > 1e-4)
        {
            distance += (targetDistance - distance) * (1 - Math.Exp(-dt * 12));
            camera.Position = new Point3D(0, 0, distance);
        }
    }

    void ToggleWireframe()
    {
        if (meshGroup is null || session.InteractiveMesh is not { } data || wireBuilding) return;
        wireframe = !wireframe;
        UpdatePills();
        if (!wireframe)
        {
            if (wireModel is not null) meshGroup.Children.Remove(wireModel);
            return;
        }
        if (wireModel is not null)
        {
            if (!meshGroup.Children.Contains(wireModel)) meshGroup.Children.Add(wireModel);
            return;
        }
        _ = BuildWireframeAsync(data);
    }

    async Task BuildWireframeAsync(Mesh3DVectorGeoEngine.InteractiveMeshData data)
    {
        wireBuilding = true;
        SetStatus(Loc.T("Generazione wireframe…", "Generating wireframe…"), TextMuted);
        try
        {
            var (geometry, truncated) = await Task.Run(() => BuildWireGeometry(data));
            if (cleaned || meshGroup is null) return;
            var material = new MaterialGroup();
            material.Children.Add(new DiffuseMaterial(Brushes.Black));
            material.Children.Add(new EmissiveMaterial(Accent));
            material.Freeze();
            wireModel = new GeometryModel3D(geometry, material) { BackMaterial = material };
            if (wireframe) meshGroup.Children.Add(wireModel);
            SetStatus(truncated
                ? Loc.T("Wireframe attivo (mesh molto densa: spigoli campionati).", "Wireframe on (very dense mesh: sampled edges).")
                : Loc.T("Wireframe attivo.", "Wireframe on."), TextMuted);
        }
        catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException or ArgumentException)
        {
            wireframe = false;
            UpdatePills();
            SetStatus(Loc.T("Wireframe non disponibile: ", "Wireframe unavailable: ") + ex.Message, Danger);
        }
        finally { wireBuilding = false; }
    }

    /// <summary>Spigoli unici come nastri sottili adagiati sulla superficie (WPF 3D non ha primitive linea).</summary>
    static (MeshGeometry3D Geometry, bool Truncated) BuildWireGeometry(Mesh3DVectorGeoEngine.InteractiveMeshData data)
    {
        var p = data.Positions;
        var n = data.Normals;
        var idx = data.Indices;
        var seen = new HashSet<long>(idx.Length);
        var edges = new List<(int A, int B)>(idx.Length);
        void AddEdge(int a, int b)
        {
            if (a == b) return;
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            if (seen.Add(((long)lo << 32) | (uint)hi)) edges.Add((lo, hi));
        }
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            AddEdge(idx[i], idx[i + 1]);
            AddEdge(idx[i + 1], idx[i + 2]);
            AddEdge(idx[i + 2], idx[i]);
        }

        int stride = Math.Max(1, (int)Math.Ceiling(edges.Count / (double)MaxWireEdges));
        const double halfWidth = 0.0024, lift = 0.0022;
        int estimated = edges.Count / stride + 1;
        var positions = new Point3DCollection(estimated * 4);
        var triangles = new Int32Collection(estimated * 6);

        for (int e = 0; e < edges.Count; e += stride)
        {
            var (a, b) = edges[e];
            int oa = a * 3, ob = b * 3;
            var pa = new Point3D(p[oa], p[oa + 1], p[oa + 2]);
            var pb = new Point3D(p[ob], p[ob + 1], p[ob + 2]);
            var normal = new Vector3D(n[oa] + n[ob], n[oa + 1] + n[ob + 1], n[oa + 2] + n[ob + 2]);
            if (normal.LengthSquared < 1e-12) normal = new Vector3D(0, 1, 0);
            normal.Normalize();
            var side = Vector3D.CrossProduct(pb - pa, normal);
            if (side.LengthSquared < 1e-18) continue;
            side.Normalize();
            side *= halfWidth;
            var offset = normal * lift;
            pa += offset;
            pb += offset;

            int start = positions.Count;
            positions.Add(pa - side);
            positions.Add(pa + side);
            positions.Add(pb + side);
            positions.Add(pb - side);
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = triangles };
        geometry.Freeze();
        return (geometry, stride > 1);
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var content = new Grid();
        if (session.IsTextMode)
        {
            var textBox = new TextBox
            {
                Text = session.TextContent ?? "",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = StudioBg,
                Foreground = TextPrimary,
                CaretBrush = Accent,
                SelectionBrush = Accent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 12, 14, 12),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Segoe UI"),
                FontSize = 13.5
            };
            content.Children.Add(textBox);
        }
        else
        {
            content.Children.Add(image);
            if (viewportHost is not null) content.Children.Add(viewportHost);
            if (IsCropCapable) content.Children.Add(BuildCropHost());
        }

        var stage = new Border
        {
            Background = PanelBg,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(18, 4, 18, 10),
            Padding = new Thickness(10),
            Child = content,
            ClipToBounds = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Grid.SetRow(stage, 1);
        root.Children.Add(stage);

        if (session.HexColors.Count > 0)
        {
            var swatches = BuildSwatches();
            Grid.SetRow(swatches, 2);
            root.Children.Add(swatches);
        }

        var footer = BuildFooter();
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return new Border
        {
            Background = Bg,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = new AdornerDecorator { Child = root },
            Focusable = true
        };
    }

    UIElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(18, 16, 14, 10), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.MouseLeftButtonDown += (_, e) =>
        {
            // Trascina la finestra solo dallo sfondo/testi dell'header, mai da una tile interattiva.
            if (e.Handled || e.ButtonState != MouseButtonState.Pressed || IsInteractiveSource(e.OriginalSource as DependencyObject, grid))
                return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };

        var badge = new Border
        {
            Background = Accent,
            CornerRadius = new CornerRadius(8),
            Width = 38,
            Height = 38,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "F",
                FontWeight = FontWeights.Black,
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var info = new StackPanel { Margin = new Thickness(14, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(session.SourcePath),
            Foreground = TextPrimary,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = session.SourcePath
        });
        info.Children.Add(new TextBlock
        {
            Text = session.ToolDisplayName.ToUpperInvariant() + "   ·   " + session.Metadata,
            Foreground = TextMuted,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Consolas, Segoe UI")
        });

        if (session.Is3D)
        {
            var switches = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            AddModePill(switches, HasViewport ? Loc.T("🧊 3D Interattivo", "🧊 Interactive 3D") : "↻ Turntable 360°", ViewMode.Primary);
            AddModePill(switches, Loc.T("📐 Blueprint 4 Viste", "📐 4-View Blueprint"), ViewMode.Blueprint);
            switches.Children.Add(new Border { Width = 1, Background = Stroke, Margin = new Thickness(6, 4, 10, 10) });
            if (HasViewport)
            {
                wirePill = AddPill(switches, "◇ Clay", ToggleWireframe, () => mode == ViewMode.Primary && !wireBuilding);
                wirePill.ToolTip = Loc.T("Attiva/disattiva il Wireframe arancione FLIP", "Toggle FLIP orange wireframe");
                bookmarkPill = AddPill(switches, Loc.T("📌 Memorizza Vista (A)", "📌 Save View (A)"), OnBookmarkPillClick, () => mode == ViewMode.Primary);
                bookmarkPill.ToolTip = Loc.T(
                    "Memorizza o richiama l'inquadratura 3D (Tasto A · Clic destro per sovrascrivere)",
                    "Save or recall 3D camera view (Key A · Right-click to overwrite)");
                bookmarkPill.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (mode == ViewMode.Primary) SaveBookmarkView();
                };
            }
            pausePill = AddPill(switches, Loc.T("⏸ Pausa", "⏸ Pause"), TogglePlaying);
            AddSpeedPill(switches, "1x", 1.0);
            AddSpeedPill(switches, "0.5x", 0.5);
            info.Children.Add(switches);
        }
        if (IsCropCapable)
        {
            var cropSwitches = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            cropModePill = AddPill(cropSwitches, Loc.T("✂ Ritaglia & Oscura", "✂ Crop & Blur"), () => SetCropMode(IsCropSession || !cropMode), () => !IsCropSession);
            cropModePill.ToolTip = Loc.T(
                "Ritaglio interattivo con preset social e oscuramento privacy delle aree sensibili",
                "Interactive crop with social aspect presets and privacy blur");
            info.Children.Add(cropSwitches);
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var close = MakePill("✕", accent: false, Close);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.ToolTip = Loc.T("Chiudi (Spazio / Esc)", "Close (Space / Esc)");
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        return grid;
    }

    static bool IsInteractiveSource(DependencyObject? source, DependencyObject stop)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, stop);
             current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is Border { Tag: PillColors }) return true;
        }
        return false;
    }

    Border AddPill(Panel host, string text, Action onClick, Func<bool>? canDwell = null)
    {
        var pill = MakePill(text, accent: false, onClick, canDwell);
        pill.Margin = new Thickness(0, 0, 8, 6);
        host.Children.Add(pill);
        return pill;
    }

    void AddModePill(Panel host, string text, ViewMode target)
    {
        modePills.Add((AddPill(host, text, () => SetMode(target),
            () => mode != target && (target != ViewMode.Blueprint || blueprintFrame is not null)), target));
    }

    void AddSpeedPill(Panel host, string text, double value)
    {
        speedPills.Add((AddPill(host, text, () => SetSpeed(value),
            () => !(playing && Math.Abs(speed - value) < 0.001)), value));
    }

    static bool TryParseHexColor(string hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 7 || hex[0] != '#' ||
            !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static double ComputeContrastRatio(string fgHex, string bgHex)
    {
        if (!TryParseHexColor(fgHex, out var fg) || !TryParseHexColor(bgHex, out var bg)) return 1.0;
        static double RelLum(Color c)
        {
            static double Chan(byte b)
            {
                double s = b / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Chan(c.R) + 0.7152 * Chan(c.G) + 0.0722 * Chan(c.B);
        }
        double l1 = RelLum(fg), l2 = RelLum(bg);
        if (l2 > l1) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    internal (string Fg, string Bg) GetContrastPairForTest() => (swatchFgHex, swatchBgHex);

    internal void SwapContrastPairForTest() => swapSwatchPair?.Invoke();

    internal void InvokeBookmarkForTest(bool overwrite = false)
    {
        if (overwrite) SaveBookmarkView();
        else OnBookmarkPillClick();
    }

    internal SavedView3DState? GetBookmarkStateForTest() => savedView;

    UIElement BuildSwatches()
    {
        var outer = new StackPanel { Margin = new Thickness(18, 0, 18, 10) };
        var grid = new UniformGrid { Columns = session.HexColors.Count };

        swatchFgHex = session.HexColors[0];
        swatchBgHex = session.HexColors.Count > 1 ? session.HexColors[1] : "#121318";
        swatchPickStep = 0;

        var previewSample = new TextBlock
        {
            Text = "Aa",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var previewChip = new Border
        {
            Width = 44,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            Child = previewSample
        };
        var pairInfoText = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var swatchCells = new List<(Border Cell, string Hex)>();

        void RefreshContrastPair()
        {
            if (TryParseHexColor(swatchFgHex, out var fg))
                previewSample.Foreground = new SolidColorBrush(fg);
            if (TryParseHexColor(swatchBgHex, out var bg))
                previewChip.Background = new SolidColorBrush(bg);

            double ratio = ComputeContrastRatio(swatchFgHex, swatchBgHex);
            string grade = ratio >= 7.0 ? "AAA" : ratio >= 4.5 ? "AA" : ratio >= 3.0 ? "AA Large" : Loc.T("Basso", "Low");
            pairInfoText.Text = $"{Loc.T("Aa · Prova Coppia", "Aa · Contrast Check")}   {swatchFgHex} / {swatchBgHex} · {ratio:0.0}:1 ({grade})";

            foreach (var (c, hex) in swatchCells)
            {
                bool isFg = string.Equals(hex, swatchFgHex, StringComparison.OrdinalIgnoreCase);
                bool isBg = string.Equals(hex, swatchBgHex, StringComparison.OrdinalIgnoreCase);
                c.BorderBrush = isFg ? Accent : isBg ? Frozen(0x4C, 0xC9, 0xF0) : Stroke;
                c.BorderThickness = new Thickness(isFg || isBg ? 1.6 : 1.0);
            }
        }

        foreach (string hex in session.HexColors)
        {
            if (!TryParseHexColor(hex, out var color))
                continue;

            var label = new TextBlock
            {
                Text = hex,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            var chip = new Border
            {
                Height = 34,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(color),
                BorderBrush = Stroke,
                BorderThickness = new Thickness(1)
            };
            var stack = new StackPanel();
            stack.Children.Add(chip);
            stack.Children.Add(label);

            var cell = new Border
            {
                Margin = new Thickness(5, 0, 5, 0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(9),
                Background = PillBg,
                BorderBrush = Stroke,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = Loc.F("Clicca per copiare {0} · imposta Testo/Sfondo sul provino Aa", "Click to copy {0} · sets Text/Background on Aa preview", hex),
                Child = stack
            };
            swatchCells.Add((cell, hex));
            cell.MouseEnter += (_, _) => cell.BorderBrush = Accent;
            cell.MouseLeave += (_, _) => RefreshContrastPair();
            cell.MouseLeftButtonUp += (_, _) =>
            {
                if (swatchPickStep == 0)
                {
                    swatchFgHex = hex;
                    swatchPickStep = 1;
                    RefreshContrastPair();
                    TryCopySilently(hex);
                    SetStatus(Loc.F("{0} impostato come Testo Aa e copiato · clicca un 2° colore per lo Sfondo.", "{0} set as Aa Text and copied · click a 2nd color for Background.", hex), Accent);
                }
                else
                {
                    swatchBgHex = hex;
                    swatchPickStep = 0;
                    RefreshContrastPair();
                    TryCopySilently(hex);
                    SetStatus(Loc.F(
                        "{0} impostato come Sfondo Aa · clicca [ Aa · Prova Coppia ] per copiare {1} / {2}.",
                        "{0} set as Aa Background · click [ Aa · Contrast Check ] to copy {1} / {2}.",
                        hex, swatchFgHex, swatchBgHex), Accent);
                }
            };
            grid.Children.Add(cell);
        }

        copySwatchPair = () => CopyText($"{swatchFgHex} / {swatchBgHex}");
        swapSwatchPair = () =>
        {
            (swatchFgHex, swatchBgHex) = (swatchBgHex, swatchFgHex);
            RefreshContrastPair();
            SetStatus(Loc.F("Coppia scambiata: {0} su {1}.", "Pair swapped: {0} on {1}.", swatchFgHex, swatchBgHex), TextPrimary);
        };

        var pairRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pairRow.Children.Add(previewChip);
        pairRow.Children.Add(pairInfoText);

        var pairPill = MakePill("", accent: false, () => copySwatchPair());
        pairPill.Child = pairRow;
        pairPill.Padding = new Thickness(8, 5, 12, 5);
        pairPill.Margin = new Thickness(0, 0, 8, 0);
        pairPill.ToolTip = Loc.T(
            "Clicca per copiare entrambi gli #HEX (#TESTO / #SFONDO) negli Appunti",
            "Click to copy both #HEX values (#TEXT / #BACKGROUND) to Clipboard");

        var swapPill = MakePill(Loc.T("⇄ Scambia", "⇄ Swap"), accent: false, () => swapSwatchPair());
        swapPill.ToolTip = Loc.T("Scambia colore testo e sfondo del provino Aa", "Swap text and background colors on Aa preview");

        var contrastBar = new DockPanel { Margin = new Thickness(5, 8, 5, 0) };
        DockPanel.SetDock(swapPill, Dock.Right);
        var css = MakePill(Loc.T("Copia CSS", "Copy CSS"), false, () => CopyText(
            ":root {" + Environment.NewLine + string.Join(Environment.NewLine,
                session.HexColors.Select((hex, i) => $"  --flip-color-{i + 1}: {hex};")) + Environment.NewLine + "}",
            Loc.T("Palette CSS copiata negli Appunti.", "CSS palette copied to Clipboard.")));
        DockPanel.SetDock(css, Dock.Right);
        contrastBar.Children.Add(css);
        contrastBar.Children.Add(swapPill);
        contrastBar.Children.Add(pairPill);

        outer.Children.Add(grid);
        outer.Children.Add(contrastBar);
        RefreshContrastPair();
        return outer;
    }

    void TryCopySilently(string text)
    {
        try { Clipboard.SetText(text); } catch { }
    }

    UIElement BuildFooter()
    {
        var grid = new Grid { Margin = new Thickness(18, 0, 18, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        defaultStatusText = HasViewport
            ? Loc.T(
                "Sinistro: ruota   ·   Destro/Shift: pan   ·   Rotellina: zoom   ·   Doppio clic: reset   ·   Tab: Blueprint   ·   Spazio/Esc: chiudi",
                "Left-drag: orbit   ·   Right/Shift: pan   ·   Wheel: zoom   ·   Double-click: reset   ·   Tab: Blueprint   ·   Space/Esc: close")
            : Loc.T(
                "Spazio / Esc  chiudi   ·   Ctrl+C  copia   ·   Ctrl+S  salva   ·   Nessun file salvato finché non premi Salva",
                "Space / Esc  close   ·   Ctrl+C  copy   ·   Ctrl+S  save   ·   No file saved until you click Save");
        status.Text = defaultStatusText;
        Grid.SetColumn(status, 0);
        grid.Children.Add(status);

        var copy = session.IsTextMode
            ? MakePill(Loc.T("Copia Testo", "Copy Text"), accent: false, CopyFullTextContent)
            : MakePill(Loc.T("Copia negli Appunti", "Copy to Clipboard"), accent: false, () =>
            {
                if (cropMode) _ = CopyCropAsync();
                else CopyImage();
            }, () => !cropBusy);
        copy.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(copy, 1);
        if (session.PdfPageNumber is int page)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(actions, 1);
            SetPillText(copy, Loc.T("Copia immagine", "Copy Image"));
            actions.Children.Add(copy);
            actions.Children.Add(MakePill(Loc.F("Copia testo pagina {0}", "Copy page {0} text", page), false, () => _ = CopyPdfTextAsync(page), () => !readingPdf));
            grid.Children.Add(actions);
        }
        else grid.Children.Add(copy);
        copyPill = copy;

        var save = MakePill(Loc.T("Salva File", "Save File"), accent: true, () =>
        {
            if (cropMode) _ = SaveCropAsync();
            else _ = SaveAsync();
        }, () => !saving && !cropBusy);
        savePill = save;
        save.ToolTip = Loc.T(
            "Ctrl+S: salva la GIF Turntable 360° (vista 3D) o il PNG Blueprint (vista Blueprint)",
            "Ctrl+S: save 360° Turntable GIF (3D view) or 4-View Blueprint PNG (Blueprint view)");
        Grid.SetColumn(save, 2);
        grid.Children.Add(save);
        return grid;
    }

    /// <summary>Tile cliccabile dark: sfondo trasparente hit-testabile, clic gestito con cattura del mouse.</summary>
    Border MakePill(string text, bool accent, Action onClick, Func<bool>? canDwell = null)
    {
        Brush normal = accent ? Accent : Brushes.Transparent;
        Brush hover = accent ? AccentHover : PillHover;
        var pill = new Border
        {
            Background = normal,
            BorderBrush = accent ? Accent : Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 7, 14, 7),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = accent ? Brushes.White : TextPrimary,
                FontWeight = accent ? FontWeights.Bold : FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        pill.Tag = new PillColors(normal, hover);
        pill.MouseEnter += (_, _) => { if (pill.Tag is PillColors colors) pill.Background = colors.Hover; };
        pill.MouseLeave += (_, _) => { if (pill.Tag is PillColors colors && !pill.IsMouseCaptured) pill.Background = colors.Normal; };
        pill.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            pill.CaptureMouse();
        };
        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!pill.IsMouseCaptured) return;
            pill.ReleaseMouseCapture();
            var point = e.GetPosition(pill);
            bool inside = point.X >= 0 && point.Y >= 0 && point.X <= pill.ActualWidth && point.Y <= pill.ActualHeight;
            if (pill.Tag is PillColors colors) pill.Background = inside ? colors.Hover : colors.Normal;
            if (inside) onClick();
        };
        if (IsGazeActive)
        {
            GazeDwell.Attach(
                pill,
                () =>
                {
                    var live = SettingsStore.Current.CreateSnapshot();
                    int ms = live.GazeAccessibilityMode ? live.GazeDwellMilliseconds : session.SaveSettings.GazeDwellMilliseconds;
                    return Math.Clamp(ms, 600, 2500);
                },
                onClick,
                canDwell,
                cornerRadius: 8);
        }
        return pill;
    }

    static void SetPillSelected(Border pill, bool selected)
    {
        Brush normal = selected ? PanelBg : Brushes.Transparent;
        pill.Tag = new PillColors(normal, PillHover);
        if (!pill.IsMouseOver) pill.Background = normal;
        pill.BorderBrush = selected ? Accent : Stroke;
        if (pill.Child is TextBlock text) text.Foreground = selected ? Accent : TextPrimary;
    }

    static void SetPillText(Border? pill, string text)
    {
        if (pill?.Child is TextBlock block) block.Text = text;
    }

    // ── Riproduzione ─────────────────────────────────────────────────────────

    void SetMode(ViewMode value)
    {
        if (value == ViewMode.Blueprint && blueprintFrame is null) return;
        mode = value;
        ShowCurrent();
    }

    void SetSpeed(double value)
    {
        speed = value;
        playing = true;
        mode = ViewMode.Primary;
        ShowCurrent();
    }

    void TogglePlaying()
    {
        playing = !playing;
        mode = ViewMode.Primary;
        ShowCurrent();
    }

    void UpdatePills()
    {
        foreach (var (pill, target) in modePills) SetPillSelected(pill, target == mode);
        double primaryOpacity = mode == ViewMode.Primary ? 1.0 : 0.45;
        foreach (var (pill, value) in speedPills)
        {
            SetPillSelected(pill, playing && Math.Abs(value - speed) < 0.001);
            pill.Opacity = primaryOpacity;
        }
        if (pausePill is not null)
        {
            SetPillText(pausePill, playing ? Loc.T("⏸ Pausa", "⏸ Pause") : Loc.T("▶ Auto-Giro", "▶ Auto-Spin"));
            SetPillSelected(pausePill, !playing);
            pausePill.Opacity = primaryOpacity;
        }
        if (wirePill is not null)
        {
            SetPillText(wirePill, wireframe ? "◆ Wireframe" : "◇ Clay");
            SetPillSelected(wirePill, wireframe);
            wirePill.Opacity = primaryOpacity;
        }
        if (bookmarkPill is not null)
        {
            SetPillText(bookmarkPill, savedView is null ? Loc.T("📌 Memorizza Vista (A)", "📌 Save View (A)") : Loc.T("↺ Richiama Vista (A)", "↺ Recall View (A)"));
            SetPillSelected(bookmarkPill, savedView is not null);
            bookmarkPill.Opacity = primaryOpacity;
        }
    }

    void OnBookmarkPillClick()
    {
        if (!HasViewport) return;
        if (savedView is null) SaveBookmarkView();
        else RecallBookmarkView();
    }

    void SaveBookmarkView()
    {
        if (!HasViewport) return;
        savedView = new SavedView3DState(orientation, pan.OffsetX, pan.OffsetY, pan.OffsetZ, targetDistance, playing);
        UpdatePills();
        SetStatus(Loc.T(
            "Inquadratura 3D salvata in (A) · clicca ↺ o premi A per richiamarla (clic destro sovrascrive).",
            "3D camera view saved to (A) · click ↺ or press A to recall (right-click overwrites)."), Accent);
    }

    void RecallBookmarkView()
    {
        if (!HasViewport || savedView is not { } v) return;
        orientation = v.Orientation;
        rotation.Quaternion = orientation;
        pan.OffsetX = v.PanX;
        pan.OffsetY = v.PanY;
        pan.OffsetZ = v.PanZ;
        targetDistance = distance = v.Distance;
        if (camera is not null) camera.Position = new Point3D(0, 0, distance);
        playing = v.AutoRotate;
        mode = ViewMode.Primary;
        ShowCurrent();
        SetStatus(Loc.T("Inquadratura 3D richiamata dal segnalibro (A).", "3D camera view recalled from bookmark (A)."), Accent);
    }

    void ShowCurrent()
    {
        UpdatePills();
        timer.Stop();
        if (session.IsTextMode) return;

        bool showViewport = HasViewport && mode == ViewMode.Primary;
        if (viewportHost is not null) viewportHost.Visibility = showViewport ? Visibility.Visible : Visibility.Collapsed;
        image.Visibility = showViewport || cropMode ? Visibility.Collapsed : Visibility.Visible;
        if (showViewport)
        {
            lastFrameSeconds = clock.Elapsed.TotalSeconds;
            return;
        }

        if (mode == ViewMode.Blueprint && blueprintFrame is not null)
        {
            image.Source = blueprintFrame;
            return;
        }

        if (primaryFrames.Length == 0) return;
        if (frameIndex >= primaryFrames.Length) frameIndex = 0;
        image.Source = primaryFrames[frameIndex];
        if (primaryFrames.Length > 1 && playing && speed > 0)
        {
            timer.Interval = ScaledDelay(frameIndex);
            timer.Start();
        }
    }

    TimeSpan ScaledDelay(int index) =>
        TimeSpan.FromMilliseconds(primaryDelays[index].TotalMilliseconds / Math.Max(speed, 0.05));

    void AdvanceFrame()
    {
        if (mode != ViewMode.Primary || primaryFrames.Length < 2 || !playing || speed <= 0)
        {
            timer.Stop();
            return;
        }
        frameIndex = (frameIndex + 1) % primaryFrames.Length;
        image.Source = primaryFrames[frameIndex];
        timer.Interval = ScaledDelay(frameIndex);
    }

    /// <summary>Vista 3D → GIF Turntable 360°; vista Blueprint → PNG 4 viste.</summary>
    string CurrentFile => mode == ViewMode.Blueprint && session.BlueprintPath is { } blueprint ? blueprint : session.PrimaryPath;

    // ── Tastiera & azioni ────────────────────────────────────────────────────

    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (cropMode && HandleCropKey(e.Key, ctrl))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Space:
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.C when ctrl:
                e.Handled = true;
                if (session.IsTextMode) CopyFullTextContent();
                else CopyImage();
                break;
            case Key.S when ctrl:
                e.Handled = true;
                _ = SaveAsync();
                break;
            case Key.Tab when session.Is3D:
                e.Handled = true;
                SetMode(mode == ViewMode.Primary ? ViewMode.Blueprint : ViewMode.Primary);
                break;
            case Key.A when HasViewport && mode == ViewMode.Primary:
                e.Handled = true;
                OnBookmarkPillClick();
                break;
        }
    }

    BitmapSource? CaptureCurrent()
    {
        if (mode == ViewMode.Primary && viewportHost is { ActualWidth: > 1, ActualHeight: > 1 } host)
        {
            var dpi = VisualTreeHelper.GetDpi(host);
            var target = new RenderTargetBitmap(
                (int)Math.Ceiling(host.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(host.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            target.Render(host);
            target.Freeze();
            return target;
        }
        return image.Source as BitmapSource;
    }

    void CopyImage()
    {
        if (CaptureCurrent() is not { } bitmap) return;
        try
        {
            Clipboard.SetImage(bitmap);
            SetStatus(Loc.T("Immagine copiata negli Appunti.", "Image copied to Clipboard."), TextPrimary);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            SetStatus(Loc.T("Appunti non disponibili: ", "Clipboard unavailable: ") + ex.Message, Danger);
        }
    }

    bool readingPdf;

    async Task CopyPdfTextAsync(int page)
    {
        if (readingPdf || cleaned) return;
        readingPdf = true;
        try
        {
            var text = await Task.Run(() =>
            {
                using var pdf = UglyToad.PdfPig.PdfDocument.Open(session.SourcePath);
                return UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(pdf.GetPage(page));
            });
            if (cleaned) return;
            if (string.IsNullOrWhiteSpace(text)) SetStatus(Loc.T("Nessun testo selezionabile in questa pagina", "No selectable text on this page"), TextMuted);
            else CopyText(text, Loc.F("Testo della pagina {0} copiato negli Appunti.", "Page {0} text copied to Clipboard.", page));
        }
        catch (Exception ex) { if (!cleaned) SetStatus(Loc.T("Lettura PDF non riuscita: ", "Failed to read PDF: ") + ex.Message, Danger); }
        finally { readingPdf = false; }
    }

    void CopyFullTextContent()
    {
        if (string.IsNullOrEmpty(session.TextContent)) return;
        try
        {
            Clipboard.SetText(session.TextContent);
            SetStatus(Loc.T("Testo copiato negli Appunti.", "Text copied to Clipboard."), TextPrimary);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            SetStatus(Loc.T("Appunti non disponibili: ", "Clipboard unavailable: ") + ex.Message, Danger);
        }
    }

    void CopyText(string text, string? success = null)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(success ?? Loc.F("{0} copiato negli Appunti.", "{0} copied to Clipboard.", text), TextPrimary);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            SetStatus(Loc.T("Appunti non disponibili: ", "Clipboard unavailable: ") + ex.Message, Danger);
        }
    }

    async Task SaveAsync()
    {
        if (saving || cleaned) return;
        saving = true;
        string file = CurrentFile;
        bool generatingGif = !File.Exists(file) && session.Is3D &&
            string.Equals(Path.GetExtension(file), ".gif", StringComparison.OrdinalIgnoreCase);
        SetStatus(generatingGif
            ? Loc.T("Generazione GIF Turntable 360° e salvataggio…", "Generating 360° Turntable GIF and saving…")
            : Loc.T("Salvataggio in corso…", "Saving…"), TextMuted);
        try
        {
            var saved = await Task.Run(() => FlipPreviewService.Save(session, file));
            SetStatus(Loc.T("Salvato: ", "Saved: ") + string.Join(", ", saved.Select(path => Path.GetFileName(path))), Accent);
            Evidence.Log("preview.saved", new { session.ToolId, files = saved });
            onSaved?.Invoke(saved);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Salvataggio non riuscito: ", "Save failed: ") + ex.Message, Danger);
            Evidence.Log("preview.save.error", new { session.ToolId, ex.Message });
        }
        finally { saving = false; }
    }

    // ── ✂ Ritaglia & Oscura: editor di ritaglio interattivo + oscuramento privacy ─────────────

    internal const string CropInteractiveToolId = "tool:crop_interactive";
    const double CropHandleSize = 12;
    const double CropHandleHitRadius = 11;
    const double CropStageMargin = 16;

    enum CropDrag { None, Move, New, Blur, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

    static readonly SolidColorBrush CropShadeBrush = FrozenArgb(0xAA, 0x08, 0x09, 0x0D);
    static readonly SolidColorBrush CropGridBrush = FrozenArgb(0x70, 0xFF, 0xFF, 0xFF);
    static readonly SolidColorBrush PrivacyFill = FrozenArgb(0xC8, 0x1A, 0x1C, 0x23);
    static readonly SolidColorBrush PrivacyStroke = FrozenArgb(0xFF, 0xFF, 0xB0, 0x20);

    static (string Key, string Label, double? Ratio)[] GetCropPresets() =>
    [
        ("free", Loc.T("Libero", "Free"), null),
        ("9:16", "9:16 Reel", 9.0 / 16.0),
        ("4:5", "4:5 Post", 4.0 / 5.0),
        ("1:1", "1:1", 1.0),
        ("16:9", "16:9", 16.0 / 9.0)
    ];

    readonly List<(Border Pill, string Key)> cropPresetPills = [];
    readonly List<Rect> blurPx = [];
    readonly Shapes.Line[] cropThirds = new Shapes.Line[4];
    readonly Shapes.Rectangle[] cropHandles = new Shapes.Rectangle[8];
    Grid? cropHost;
    Canvas? cropCanvas;
    Image? cropImage;
    Shapes.Path? cropShade;
    Shapes.Rectangle? cropFrame;
    Canvas? blurLayer;
    Shapes.Rectangle? blurDraft;
    TextBlock? cropSizeText;
    Border? cropModePill;
    Border? privacyPill;
    Border? copyPill;
    Border? savePill;
    string defaultStatusText = "";
    BitmapSource? cropSource;
    double cropW = 1, cropH = 1;
    Rect cropPx;
    Rect imageRect = Rect.Empty;
    double cropScale = 1;
    double? cropRatio;
    string cropPresetKey = "free";
    bool cropMode;
    bool cropBusy;
    bool privacyMode;
    CropDrag cropDrag;
    Point cropAnchorPx;
    Rect cropStartPx;

    bool IsCropSession => string.Equals(session.ToolId, CropInteractiveToolId, StringComparison.OrdinalIgnoreCase);
    bool IsCropCapable => !session.Is3D && !session.IsTextMode && IsCropCapablePath(session.SourcePath);

    static bool IsCropCapablePath(string path) =>
        BuiltInQuickTools.RasterExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    static SolidColorBrush FrozenArgb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    UIElement BuildCropHost()
    {
        var host = new Grid { Visibility = Visibility.Collapsed };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new WrapPanel { Margin = new Thickness(2, 0, 2, 4), VerticalAlignment = VerticalAlignment.Center };
        foreach (var preset in GetCropPresets())
        {
            string key = preset.Key;
            double? ratio = preset.Ratio;
            cropPresetPills.Add((AddPill(bar, preset.Label, () => ApplyCropPreset(key, ratio)), key));
        }
        AddPill(bar, Loc.T("⇄ Inverti", "⇄ Flip"), InvertCropOrientation).ToolTip = Loc.T(
            "Scambia orientamento verticale / orizzontale (I)",
            "Flip portrait / landscape orientation (I)");
        bar.Children.Add(new Border { Width = 1, Background = Stroke, Margin = new Thickness(4, 4, 12, 10) });
        privacyPill = AddPill(bar, Loc.T("▓ Oscura Privacy", "▓ Privacy Blur"), TogglePrivacyMode);
        privacyPill.ToolTip = Loc.T(
            "Traccia uno o più rettangoli da sfocare: volti, targhe, dati sensibili (P)",
            "Draw one or more blur boxes over faces, plates, or sensitive info (P)");
        AddPill(bar, Loc.T("↺ Rimuovi aree", "↺ Clear areas"), ClearBlurRegions, () => blurPx.Count > 0).ToolTip = Loc.T(
            "Elimina tutte le aree oscurate (Backspace: solo l'ultima)",
            "Clear all blurred areas (Backspace removes the last one)");
        cropSizeText = new TextBlock
        {
            Foreground = Accent,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Consolas, Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 6)
        };
        bar.Children.Add(cropSizeText);
        Grid.SetRow(bar, 0);
        host.Children.Add(bar);

        var canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
        cropImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(cropImage, BitmapScalingMode.HighQuality);
        canvas.Children.Add(cropImage);
        blurLayer = new Canvas { IsHitTestVisible = false };
        canvas.Children.Add(blurLayer);
        cropShade = new Shapes.Path { Fill = CropShadeBrush, IsHitTestVisible = false };
        canvas.Children.Add(cropShade);
        for (int i = 0; i < cropThirds.Length; i++)
        {
            cropThirds[i] = new Shapes.Line { Stroke = CropGridBrush, StrokeThickness = 1, IsHitTestVisible = false, SnapsToDevicePixels = true };
            canvas.Children.Add(cropThirds[i]);
        }
        cropFrame = new Shapes.Rectangle
        {
            Stroke = Accent,
            StrokeThickness = 2,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0x7A, 0x18), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7
            }
        };
        canvas.Children.Add(cropFrame);
        for (int i = 0; i < cropHandles.Length; i++)
        {
            cropHandles[i] = new Shapes.Rectangle
            {
                Width = CropHandleSize,
                Height = CropHandleSize,
                Fill = Accent,
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                RadiusX = 2.5,
                RadiusY = 2.5,
                IsHitTestVisible = false
            };
            canvas.Children.Add(cropHandles[i]);
        }
        blurDraft = MakePrivacyShape(new Rect(0, 0, 0, 0));
        blurDraft.Visibility = Visibility.Collapsed;
        canvas.Children.Add(blurDraft);

        canvas.SizeChanged += (_, _) => LayoutCrop();
        canvas.MouseLeftButtonDown += OnCropMouseDown;
        canvas.MouseMove += OnCropMouseMove;
        canvas.MouseLeftButtonUp += OnCropMouseUp;
        canvas.LostMouseCapture += (_, _) =>
        {
            if (cropDrag != CropDrag.None) EndCropDrag(commit: false);
        };
        cropCanvas = canvas;
        Grid.SetRow(canvas, 1);
        host.Children.Add(canvas);
        cropHost = host;
        return host;
    }

    void SetCropMode(bool on)
    {
        if (cropHost is null) return;
        if (on && !EnsureCropSource()) on = false;
        if (!on && cropDrag != CropDrag.None)
        {
            EndCropDrag(commit: false);
            cropCanvas?.ReleaseMouseCapture();
        }
        cropMode = on;
        cropHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            timer.Stop();
            image.Visibility = Visibility.Collapsed;
        }
        else ShowCurrent();
        if (cropModePill is not null) SetPillSelected(cropModePill, on);
        SetPillText(copyPill, on ? Loc.T("📋 Copia negli Appunti", "📋 Copy to Clipboard") : Loc.T("Copia negli Appunti", "Copy to Clipboard"));
        SetPillText(savePill, on ? Loc.T("💾 Salva Immagine", "💾 Save Image") : Loc.T("Salva File", "Save File"));
        SetStatus(on
            ? Loc.T(
                "Trascina riquadro e maniglie   ·   P: Oscura Privacy   ·   I: Inverti   ·   Ctrl+S salva   ·   Ctrl+C copia   ·   Esc " + (IsCropSession ? "chiudi" : "esci"),
                "Drag box & handles   ·   P: Privacy Blur   ·   I: Flip   ·   Ctrl+S save   ·   Ctrl+C copy   ·   Esc " + (IsCropSession ? "close" : "exit"))
            : defaultStatusText, TextMuted);
        UpdateCropPills();
        if (on) _ = Dispatcher.InvokeAsync(LayoutCrop, DispatcherPriority.Loaded);
    }

    /// <summary>Decodifica l'originale con orientamento EXIF tramite lo stesso helper usato per il salvataggio (coordinate coerenti).</summary>
    bool EnsureCropSource()
    {
        if (cropSource is not null) return true;
        try
        {
            var bitmap = BuiltInQuickTools.CropImageToFileOrBitmap(session.SourcePath, 0, 0, 1, 1, null, null);
            cropSource = bitmap;
            cropW = Math.Max(1, bitmap.PixelWidth);
            cropH = Math.Max(1, bitmap.PixelHeight);
            cropPx = new Rect(0, 0, cropW, cropH);
            if (cropImage is not null) cropImage.Source = bitmap;
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("Impossibile aprire l'immagine per il ritaglio: ", "Unable to open image for cropping: ") + ex.Message, Danger);
            Evidence.Log("preview.crop.load.error", new { source = session.SourcePath, ex.Message });
            return false;
        }
    }

    void LayoutCrop()
    {
        if (cropCanvas is null || cropSource is null || cropImage is null) return;
        double aw = cropCanvas.ActualWidth - 2 * CropStageMargin, ah = cropCanvas.ActualHeight - 2 * CropStageMargin;
        if (aw <= 1 || ah <= 1) return;
        cropScale = Math.Min(Math.Min(aw / cropW, ah / cropH), 4.0);
        double w = cropW * cropScale, h = cropH * cropScale;
        imageRect = new Rect((cropCanvas.ActualWidth - w) / 2, (cropCanvas.ActualHeight - h) / 2, w, h);
        cropImage.Width = w;
        cropImage.Height = h;
        Canvas.SetLeft(cropImage, imageRect.X);
        Canvas.SetTop(cropImage, imageRect.Y);
        RefreshCropOverlay();
    }

    Rect ToCanvas(Rect px) =>
        new(imageRect.X + px.X * cropScale, imageRect.Y + px.Y * cropScale, px.Width * cropScale, px.Height * cropScale);

    Point ToPixels(Point canvasPoint) => new(
        Math.Clamp((canvasPoint.X - imageRect.X) / cropScale, 0, cropW),
        Math.Clamp((canvasPoint.Y - imageRect.Y) / cropScale, 0, cropH));

    void RefreshCropOverlay()
    {
        if (cropShade is null || cropFrame is null || blurLayer is null || imageRect.IsEmpty) return;
        var box = ToCanvas(cropPx);
        cropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(imageRect), new RectangleGeometry(box));
        cropFrame.Width = box.Width;
        cropFrame.Height = box.Height;
        Canvas.SetLeft(cropFrame, box.X);
        Canvas.SetTop(cropFrame, box.Y);
        for (int i = 0; i < 2; i++)
        {
            double fx = box.X + box.Width * (i + 1) / 3.0, fy = box.Y + box.Height * (i + 1) / 3.0;
            SetLine(cropThirds[i], fx, box.Y, fx, box.Bottom);
            SetLine(cropThirds[i + 2], box.X, fy, box.Right, fy);
        }
        for (int i = 0; i < cropHandles.Length; i++)
        {
            var p = HandlePoint(box, i);
            Canvas.SetLeft(cropHandles[i], p.X - CropHandleSize / 2);
            Canvas.SetTop(cropHandles[i], p.Y - CropHandleSize / 2);
            cropHandles[i].Visibility = privacyMode ? Visibility.Hidden : Visibility.Visible;
        }
        blurLayer.Children.Clear();
        foreach (var region in blurPx) blurLayer.Children.Add(MakePrivacyShape(ToCanvas(region)));
        if (cropSizeText is not null)
            cropSizeText.Text = $"{Math.Max(1, (int)Math.Round(cropPx.Width))} × {Math.Max(1, (int)Math.Round(cropPx.Height))} px";
    }

    static void SetLine(Shapes.Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2;
    }

    /// <summary>Ordine maniglie: alto-sx, alto, alto-dx, destra, basso-dx, basso, basso-sx, sinistra (come <see cref="CropDrag"/>).</summary>
    static Point HandlePoint(Rect r, int index) => index switch
    {
        0 => r.TopLeft,
        1 => new Point(r.X + r.Width / 2, r.Y),
        2 => r.TopRight,
        3 => new Point(r.Right, r.Y + r.Height / 2),
        4 => r.BottomRight,
        5 => new Point(r.X + r.Width / 2, r.Bottom),
        6 => r.BottomLeft,
        _ => new Point(r.X, r.Y + r.Height / 2)
    };

    static readonly int[] HandleHitOrder = [0, 2, 4, 6, 1, 3, 5, 7];

    static CropDrag HitCropHandle(Rect box, Point point)
    {
        foreach (int i in HandleHitOrder)
            if ((point - HandlePoint(box, i)).Length <= CropHandleHitRadius)
                return (CropDrag)((int)CropDrag.TopLeft + i);
        return CropDrag.None;
    }

    static Shapes.Rectangle MakePrivacyShape(Rect area)
    {
        var shape = new Shapes.Rectangle
        {
            Width = area.Width,
            Height = area.Height,
            Fill = PrivacyFill,
            Stroke = PrivacyStroke,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = 3,
            RadiusY = 3,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(shape, area.X);
        Canvas.SetTop(shape, area.Y);
        return shape;
    }

    void UpdateBlurDraft(Rect px)
    {
        if (blurDraft is null) return;
        var area = ToCanvas(px);
        blurDraft.Width = area.Width;
        blurDraft.Height = area.Height;
        Canvas.SetLeft(blurDraft, area.X);
        Canvas.SetTop(blurDraft, area.Y);
    }

    Cursor CursorFor(Point point)
    {
        if (privacyMode) return imageRect.Contains(point) ? Cursors.Cross : Cursors.Arrow;
        var box = ToCanvas(cropPx);
        return HitCropHandle(box, point) switch
        {
            CropDrag.TopLeft or CropDrag.BottomRight => Cursors.SizeNWSE,
            CropDrag.TopRight or CropDrag.BottomLeft => Cursors.SizeNESW,
            CropDrag.Top or CropDrag.Bottom => Cursors.SizeNS,
            CropDrag.Left or CropDrag.Right => Cursors.SizeWE,
            _ => box.Contains(point) ? Cursors.SizeAll : imageRect.Contains(point) ? Cursors.Cross : Cursors.Arrow
        };
    }

    void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!cropMode || cropBusy || cropCanvas is null || cropSource is null || imageRect.IsEmpty) return;
        var point = e.GetPosition(cropCanvas);
        var box = ToCanvas(cropPx);
        if (e.ClickCount == 2 && !privacyMode && imageRect.Contains(point))
        {
            bool keepRatio = box.Contains(point) && cropRatio.HasValue;
            cropPx = keepRatio ? FitRatio(cropRatio!.Value, cropPx) : new Rect(0, 0, cropW, cropH);
            if (!keepRatio) ApplyCropPreset("free", null);
            else RefreshCropOverlay();
            e.Handled = true;
            return;
        }
        CropDrag kind;
        if (privacyMode) kind = imageRect.Contains(point) ? CropDrag.Blur : CropDrag.None;
        else
        {
            kind = HitCropHandle(box, point);
            if (kind == CropDrag.None)
                kind = box.Contains(point) ? CropDrag.Move : imageRect.Contains(point) ? CropDrag.New : CropDrag.None;
        }
        if (kind == CropDrag.None) return;
        cropDrag = kind;
        cropAnchorPx = ToPixels(point);
        cropStartPx = cropPx;
        if (kind == CropDrag.Blur && blurDraft is not null)
        {
            UpdateBlurDraft(new Rect(cropAnchorPx, cropAnchorPx));
            blurDraft.Visibility = Visibility.Visible;
        }
        cropCanvas.CaptureMouse();
        e.Handled = true;
    }

    void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        if (!cropMode || cropCanvas is null || cropSource is null || imageRect.IsEmpty) return;
        var point = e.GetPosition(cropCanvas);
        if (cropDrag == CropDrag.None)
        {
            cropCanvas.Cursor = CursorFor(point);
            return;
        }
        var px = ToPixels(point);
        if (cropDrag == CropDrag.Blur) UpdateBlurDraft(new Rect(cropAnchorPx, px));
        else if (ComputeCropRect(cropDrag, px) is { } next)
        {
            cropPx = next;
            RefreshCropOverlay();
        }
        e.Handled = true;
    }

    void OnCropMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (cropDrag == CropDrag.None || cropCanvas is null) return;
        EndCropDrag(commit: true, ToPixels(e.GetPosition(cropCanvas)));
        cropCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    void EndCropDrag(bool commit, Point? releasePx = null)
    {
        var kind = cropDrag;
        cropDrag = CropDrag.None;
        if (kind == CropDrag.Blur)
        {
            if (blurDraft is not null) blurDraft.Visibility = Visibility.Collapsed;
            if (commit && releasePx is { } end)
            {
                var region = new Rect(cropAnchorPx, end);
                if (region.Width >= 4 && region.Height >= 4)
                {
                    blurPx.Add(region);
                    SetStatus(Loc.F(
                        "Aree oscurate: {0}   ·   Backspace rimuove l'ultima   ·   ↺ Rimuovi aree le azzera",
                        "Blurred areas: {0}   ·   Backspace removes last   ·   ↺ Clear areas resets all",
                        blurPx.Count), PrivacyStroke);
                }
            }
            RefreshCropOverlay();
        }
        UpdateCropPills();
    }

    /// <summary>Nuovo riquadro in pixel per spostamento, nuova selezione o maniglie, con blocco proporzioni e clamping ai bordi.</summary>
    Rect? ComputeCropRect(CropDrag kind, Point p)
    {
        double min = Math.Min(16, Math.Min(cropW, cropH));
        var s = cropStartPx;
        Rect result;
        switch (kind)
        {
            case CropDrag.Move:
                return new Rect(
                    ClampCropValue(s.X + p.X - cropAnchorPx.X, 0, cropW - s.Width),
                    ClampCropValue(s.Y + p.Y - cropAnchorPx.Y, 0, cropH - s.Height),
                    s.Width, s.Height);
            case CropDrag.New:
                result = CornerRect(cropAnchorPx, p, p.X < cropAnchorPx.X, p.Y < cropAnchorPx.Y);
                break;
            case CropDrag.TopLeft: result = CornerRect(s.BottomRight, p, left: true, top: true); break;
            case CropDrag.TopRight: result = CornerRect(s.BottomLeft, p, left: false, top: true); break;
            case CropDrag.BottomRight: result = CornerRect(s.TopLeft, p, left: false, top: false); break;
            case CropDrag.BottomLeft: result = CornerRect(s.TopRight, p, left: true, top: false); break;
            case CropDrag.Left:
            case CropDrag.Right:
                result = HorizontalEdgeRect(s, p, kind == CropDrag.Left);
                break;
            case CropDrag.Top:
            case CropDrag.Bottom:
                result = VerticalEdgeRect(s, p, kind == CropDrag.Top);
                break;
            default:
                return null;
        }
        return result.Width >= min && result.Height >= min ? result : null;
    }

    Rect CornerRect(Point anchor, Point p, bool left, bool top)
    {
        double maxW = left ? anchor.X : cropW - anchor.X, maxH = top ? anchor.Y : cropH - anchor.Y;
        double w = ClampCropValue(left ? anchor.X - p.X : p.X - anchor.X, 0, maxW);
        double h = ClampCropValue(top ? anchor.Y - p.Y : p.Y - anchor.Y, 0, maxH);
        if (cropRatio is { } r)
        {
            if (w >= h * r) h = w / r; else w = h * r;
            if (w > maxW) { w = maxW; h = w / r; }
            if (h > maxH) { h = maxH; w = h * r; }
        }
        return new Rect(left ? anchor.X - w : anchor.X, top ? anchor.Y - h : anchor.Y, w, h);
    }

    Rect HorizontalEdgeRect(Rect s, Point p, bool left)
    {
        double anchor = left ? s.Right : s.Left;
        double maxW = left ? anchor : cropW - anchor;
        if (cropRatio is { } r) maxW = Math.Min(maxW, cropH * r);
        double w = ClampCropValue(left ? anchor - p.X : p.X - anchor, 0, maxW);
        double h = s.Height, y = s.Y;
        if (cropRatio is { } ratio)
        {
            h = w / ratio;
            y = ClampCropValue(s.Y + s.Height / 2 - h / 2, 0, cropH - h);
        }
        return new Rect(left ? anchor - w : anchor, y, w, h);
    }

    Rect VerticalEdgeRect(Rect s, Point p, bool top)
    {
        double anchor = top ? s.Bottom : s.Top;
        double maxH = top ? anchor : cropH - anchor;
        if (cropRatio is { } r) maxH = Math.Min(maxH, cropW / r);
        double h = ClampCropValue(top ? anchor - p.Y : p.Y - anchor, 0, maxH);
        double w = s.Width, x = s.X;
        if (cropRatio is { } ratio)
        {
            w = h * ratio;
            x = ClampCropValue(s.X + s.Width / 2 - w / 2, 0, cropW - w);
        }
        return new Rect(x, top ? anchor - h : anchor, w, h);
    }

    static double ClampCropValue(double value, double min, double max) => Math.Min(Math.Max(value, min), Math.Max(min, max));

    void ApplyCropPreset(string key, double? ratio)
    {
        if (cropSource is null) return;
        cropPresetKey = key;
        cropRatio = ratio;
        if (ratio is { } r) cropPx = FitRatio(r, cropPx);
        RefreshCropOverlay();
        UpdateCropPills();
    }

    /// <summary>Riquadro più grande possibile con proporzione <paramref name="ratio"/> (L/A), centrato sul riquadro attuale.</summary>
    Rect FitRatio(double ratio, Rect around)
    {
        double w = cropW, h = w / ratio;
        if (h > cropH) { h = cropH; w = h * ratio; }
        double cx = around.X + around.Width / 2, cy = around.Y + around.Height / 2;
        return new Rect(ClampCropValue(cx - w / 2, 0, cropW - w), ClampCropValue(cy - h / 2, 0, cropH - h), w, h);
    }

    void InvertCropOrientation()
    {
        if (cropSource is null) return;
        if (cropRatio is { } r)
        {
            double inverted = 1 / r;
            string key = GetCropPresets().FirstOrDefault(p => p.Ratio is { } pr && Math.Abs(pr - inverted) < 0.001).Key ?? "custom";
            ApplyCropPreset(key, inverted);
            return;
        }
        double cx = cropPx.X + cropPx.Width / 2, cy = cropPx.Y + cropPx.Height / 2;
        double w = Math.Min(cropPx.Height, cropW), h = Math.Min(cropPx.Width, cropH);
        cropPx = new Rect(ClampCropValue(cx - w / 2, 0, cropW - w), ClampCropValue(cy - h / 2, 0, cropH - h), w, h);
        RefreshCropOverlay();
    }

    void TogglePrivacyMode()
    {
        if (cropSource is null) return;
        privacyMode = !privacyMode;
        UpdateCropPills();
        RefreshCropOverlay();
        SetStatus(privacyMode
            ? Loc.T(
                "▓ Oscura Privacy: trascina sull'immagine per coprire volti, targhe o dati sensibili",
                "▓ Privacy Blur: drag over faces, license plates, or sensitive info")
            : Loc.T(
                "Ritaglio: trascina il riquadro, le 8 maniglie o disegna una nuova selezione",
                "Crop: drag box, 8 handles, or draw a new selection"), privacyMode ? PrivacyStroke : TextMuted);
    }

    void ClearBlurRegions()
    {
        if (blurPx.Count == 0) return;
        blurPx.Clear();
        RefreshCropOverlay();
        UpdateCropPills();
        SetStatus(Loc.T("Aree oscurate rimosse.", "Blurred areas cleared."), TextMuted);
    }

    void UpdateCropPills()
    {
        foreach (var (pill, key) in cropPresetPills) SetPillSelected(pill, key == cropPresetKey);
        if (privacyPill is not null)
        {
            SetPillSelected(privacyPill, privacyMode);
            string privacyLabel = Loc.T("▓ Oscura Privacy", "▓ Privacy Blur");
            SetPillText(privacyPill, blurPx.Count > 0 ? $"{privacyLabel} ({blurPx.Count})" : privacyLabel);
        }
    }

    bool HandleCropKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.Escape:
                if (cropDrag != CropDrag.None)
                {
                    EndCropDrag(commit: false);
                    cropCanvas?.ReleaseMouseCapture();
                    return true;
                }
                if (IsCropSession) Close();
                else SetCropMode(false);
                return true;
            case Key.C when ctrl:
                _ = CopyCropAsync();
                return true;
            case Key.S when ctrl:
                _ = SaveCropAsync();
                return true;
            case Key.Back:
            case Key.Delete:
                if (blurPx.Count > 0)
                {
                    blurPx.RemoveAt(blurPx.Count - 1);
                    RefreshCropOverlay();
                    UpdateCropPills();
                }
                return true;
            case Key.P:
                TogglePrivacyMode();
                return true;
            case Key.I:
                InvertCropOrientation();
                return true;
            default:
                return false;
        }
    }

    (double X, double Y, double W, double H) NormalizedCrop() =>
        (cropPx.X / cropW, cropPx.Y / cropH, cropPx.Width / cropW, cropPx.Height / cropH);

    List<(double X, double Y, double W, double H)> NormalizedBlurs() =>
        blurPx.Select(r => (X: r.X / cropW, Y: r.Y / cropH, W: r.Width / cropW, H: r.Height / cropH)).ToList();

    static string NextCropOutputPath(string source)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(source)) ?? Path.GetTempPath();
        string name = Path.GetFileNameWithoutExtension(source);
        string ext = Path.GetExtension(source);
        string candidate = Path.Combine(dir, $"{name}_crop{ext}");
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name}_crop_{i}{ext}");
        return candidate;
    }

    /// <summary>💾 Salva Immagine: ritaglio + aree privacy salvati accanto all'originale (<c>nome_crop.ext</c>).</summary>
    async Task SaveCropAsync()
    {
        if (cropBusy || cleaned || cropSource is null) return;
        cropBusy = true;
        string source = session.SourcePath;
        var (x, y, w, h) = NormalizedCrop();
        var blurs = NormalizedBlurs();
        SetStatus(Loc.T("Ritaglio e salvataggio in corso…", "Cropping and saving…"), TextMuted);
        try
        {
            string output = await Task.Run(() =>
            {
                string path = NextCropOutputPath(source);
                _ = BuiltInQuickTools.CropImageToFileOrBitmap(source, x, y, w, h, blurs, path);
                return path;
            });
            if (cleaned) return;
            SetStatus($"{Loc.T("Salvato: ", "Saved: ")}{Path.GetFileName(output)}   ·   {cropSizeText?.Text}", Accent);
            if (cropFrame != null)
            {
                var flash = Accent.Clone();
                cropFrame.Stroke = flash;
                flash.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(
                    Color.FromRgb(0x22, 0xC5, 0x5E), Accent.Color,
                    TimeSpan.FromMilliseconds(300)) { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
            }
            Evidence.Log("preview.crop.saved", new { source, output, blurRegions = blurs.Count });
            onSaved?.Invoke([output]);
        }
        catch (Exception ex)
        {
            if (!cleaned) SetStatus(Loc.T("Salvataggio non riuscito: ", "Save failed: ") + ex.Message, Danger);
            Evidence.Log("preview.crop.save.error", new { source, ex.Message });
        }
        finally { cropBusy = false; }
    }

    /// <summary>📋 Copia negli Appunti: il BitmapSource congelato restituito da BuiltInQuickTools va direttamente in Clipboard.SetImage.</summary>
    async Task CopyCropAsync()
    {
        if (cropBusy || cleaned || cropSource is null) return;
        cropBusy = true;
        string source = session.SourcePath;
        var (x, y, w, h) = NormalizedCrop();
        var blurs = NormalizedBlurs();
        SetStatus(Loc.T("Ritaglio negli Appunti…", "Copying crop to Clipboard…"), TextMuted);
        try
        {
            var bitmap = await Task.Run(() => BuiltInQuickTools.CropImageToFileOrBitmap(source, x, y, w, h, blurs, outputPath: null));
            if (cleaned) return;
            Clipboard.SetImage(bitmap);
            SetStatus(Loc.F(
                "Immagine ritagliata copiata negli Appunti   ·   {0} × {1} px",
                "Cropped image copied to Clipboard   ·   {0} × {1} px",
                bitmap.PixelWidth, bitmap.PixelHeight), Accent);
        }
        catch (Exception ex)
        {
            if (!cleaned) SetStatus(Loc.T("Copia non riuscita: ", "Copy failed: ") + ex.Message, Danger);
            Evidence.Log("preview.crop.copy.error", new { source, ex.Message });
        }
        finally { cropBusy = false; }
    }

    void SetStatus(string text, Brush brush)
    {
        status.Text = text;
        status.Foreground = brush;
    }

    void Cleanup()
    {
        if (cleaned) return;
        cleaned = true;
        HookRendering(false);
        timer.Stop();
        image.Source = null;
        primaryFrames = [];
        blueprintFrame = null;
        if (cropImage is not null) cropImage.Source = null;
        cropSource = null;
        blurPx.Clear();
        if (viewport is not null) viewport.Children.Clear();
        wireModel = null;
        meshGroup = null;
        session.DeleteTemporaryFiles();
    }
}
