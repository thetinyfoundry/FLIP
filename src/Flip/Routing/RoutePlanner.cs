using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Flip.Core;

namespace Flip.Routing;

/// <summary>
/// Singolo salto pianificato in una rotta di conversione.
/// </summary>
public readonly record struct PlannedHop(ConversionEdge Edge, IConversionEngine Engine);

/// <summary>
/// Rotta completa di conversione, potenzialmente multi-hop (es. EPUB -> PDF -> PNG).
/// </summary>
public sealed class ConversionRoute
{
    public string SourceExtension { get; }
    public string TargetExtension { get; }
    public IReadOnlyList<PlannedHop> Hops { get; }
    public int TotalCost { get; }

    public ConversionRoute(string sourceExtension, string targetExtension, IReadOnlyList<PlannedHop> hops, int totalCost)
    {
        SourceExtension = (sourceExtension ?? "").TrimStart('.').ToLowerInvariant();
        TargetExtension = (targetExtension ?? "").TrimStart('.').ToLowerInvariant();
        Hops = hops ?? [];
        TotalCost = totalCost;
    }
}

/// <summary>
/// Pianificatore ed esecutore di percorsi di conversione a grafo multi-hop per FLIP.
/// Registra motori e quick tool, calcola cammini ottimali su grafo ed esegue pipeline
/// multi-step in sandbox temporanee isolate con commit atomico finale.
/// </summary>
public static class RoutePlanner
{
    private static readonly ConcurrentDictionary<string, IConversionEngine> _engines =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, IQuickTool> _quickTools =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Registrazione ────────────────────────────────────────────────────────

    public static void RegisterEngine(IConversionEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engines[engine.Id] = engine;
    }

    public static void RegisterQuickTool(IQuickTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _quickTools[tool.Id] = tool;
    }

    /// <summary>Restituisce un Quick Tool registrato per Id, indipendentemente dalla sua visibilità sul retro della ruota.</summary>
    public static IQuickTool? FindQuickTool(string id) =>
        _quickTools.TryGetValue(id, out var tool) ? tool : null;

    public static IReadOnlyList<IConversionEngine> GetRegisteredEngines() =>
        _engines.Values.ToList();

    public static IReadOnlyList<IQuickTool> GetAvailableQuickTools(IReadOnlyList<string> inputExtensions, FlipSettings? settings = null)
    {
        var snapshot = CaptureSettings(settings);
        var enabledTools = snapshot.EnabledBackSideTools;
        List<string> normalized = inputExtensions
            .Select(e => e.TrimStart('.').ToLowerInvariant())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _quickTools.Values
            .Where(t => enabledTools.Contains(t.Id, StringComparer.OrdinalIgnoreCase))
            .Where(t => t.CanHandle(normalized, snapshot))
            .ToList();
    }

    private const int MaxRouteHops = 3;

    /// <summary>
    /// Trova il percorso ammissibile meno costoso entro il limite di salti.
    /// </summary>
    public static bool TryPlanRoute(
        string sourceExt,
        string targetExt,
        FlipSettings? settings,
        out ConversionRoute? route,
        int maxHops = MaxRouteHops)
    {
        route = null;
        string source = NormalizeExtension(sourceExt);
        string target = NormalizeExtension(targetExt);
        if (source.Length == 0 || target.Length == 0 || source == target || maxHops <= 0)
            return false;

        return FindRoutes(source, BuildGraph(CaptureSettings(settings)), maxHops).TryGetValue(target, out route);
    }

    /// <summary>
    /// Elenca i formati raggiungibili usando lo stesso pianificatore, poi applica i filtri della ruota.
    /// </summary>
    public static IReadOnlyList<FormatDefinition> GetReachableTargetFormats(
        string sourceExt,
        FlipSettings? settings = null,
        int maxHops = MaxRouteHops)
    {
        return GetReachableTargetFormats([sourceExt], settings, maxHops);
    }

    public static IReadOnlyList<FormatDefinition> GetReachableTargetFormats(
        IReadOnlyList<string> sourceExtensions,
        FlipSettings? settings = null,
        int maxHops = MaxRouteHops)
    {
        return PlanWheelFormats(sourceExtensions, settings, out _, maxHops);
    }

    internal static IReadOnlyList<FormatDefinition> PlanWheelFormats(
        IReadOnlyList<string> sourceExtensions, FlipSettings? settings,
        out IReadOnlyDictionary<string, ConversionRoute> primaryRoutes, int maxHops = MaxRouteHops)
    {
        primaryRoutes = System.Collections.Immutable.ImmutableDictionary<string, ConversionRoute>.Empty;
        string[] sources = sourceExtensions.Select(NormalizeExtension).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0 || sources.Contains("") || maxHops <= 0)
            return [];

        var snapshot = CaptureSettings(settings);
        var graph = BuildGraph(snapshot);
        var firstRoutes = FindRoutes(sources[0], graph, maxHops);
        primaryRoutes = firstRoutes;
        var costs = firstRoutes.ToDictionary(pair => pair.Key, pair => pair.Value.TotalCost, StringComparer.Ordinal);
        var hopCounts = firstRoutes.ToDictionary(pair => pair.Key, pair => pair.Value.Hops.Count, StringComparer.Ordinal);
        foreach (string source in sources.Skip(1))
        {
            if (costs.Count == 0) return [];
            var routes = FindRoutes(source, graph, maxHops);
            foreach (string target in costs.Keys.ToArray())
            {
                if (routes.TryGetValue(target, out var route))
                {
                    costs[target] += route.TotalCost;
                    hopCounts[target] = Math.Max(hopCounts[target], route.Hops.Count);
                }
                else
                {
                    costs.Remove(target);
                    hopCounts.Remove(target);
                }
            }
        }

        var pins = new Dictionary<string, int>(StringComparer.Ordinal);
        var category = FormatRegistry.Find(sources[0])?.Category;
        if (category.HasValue && sources.All(source => FormatRegistry.Find(source)?.Category == category) &&
            snapshot.PinnedWheelFormatsByCategory.TryGetValue(category.Value.ToString(), out var preferred))
        {
            foreach (string extension in preferred)
                pins.TryAdd(NormalizeExtension(extension), pins.Count);
        }

        var (unlockedCategories, unlockedFields) = GetContextualUnlocks(sources);
        var fields = snapshot.EnabledSpecialistFields;
        return costs.Keys.Select(FormatRegistry.Find).OfType<FormatDefinition>()
            .Where(format => FormatRegistry.IsVisibleInWheelForSource(
                format, snapshot.MaxVisibleGrade, fields, unlockedCategories, unlockedFields))
            .OrderBy(format => pins.GetValueOrDefault(format.Extension, int.MaxValue))
            .ThenBy(format => GetNaturalTargetOrder(sources[0], category, CanonicalExtension(format.Extension), hopCounts[format.Extension]))
            .ThenBy(format => hopCounts[format.Extension])
            .ThenBy(format => format.Grade)
            .ThenBy(format => costs[format.Extension])
            .ThenBy(format => string.Equals(format.Extension, CanonicalExtension(format.Extension), StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(format => format.Extension.Length)
            .ThenBy(format => format.Extension, StringComparer.Ordinal)
            .DistinctBy(format => CanonicalExtension(format.Extension), StringComparer.Ordinal)
            .ToArray();
    }

    private static (HashSet<FormatCategory> Categories, SpecialistField Fields) GetContextualUnlocks(IReadOnlyList<string> sources)
    {
        var categories = new HashSet<FormatCategory>();
        var fields = SpecialistField.None;

        foreach (string source in sources)
        {
            if (FormatRegistry.Find(source) is not { } def) continue;
            categories.Add(def.Category);
            fields |= def.Field;

            switch (def.Category)
            {
                case FormatCategory.Model3D:
                    fields |= SpecialistField.ThreeDAndGameDev;
                    break;
                case FormatCategory.Cad or FormatCategory.Vector:
                    categories.Add(FormatCategory.Cad);
                    categories.Add(FormatCategory.Vector);
                    fields |= SpecialistField.CadAndVector;
                    break;
                case FormatCategory.DataAndConfig or FormatCategory.Spreadsheet:
                    categories.Add(FormatCategory.DataAndConfig);
                    categories.Add(FormatCategory.Spreadsheet);
                    fields |= SpecialistField.DataAndDev;
                    break;
                case FormatCategory.TextAndMarkup or FormatCategory.DocumentAndEmail:
                    categories.Add(FormatCategory.TextAndMarkup);
                    categories.Add(FormatCategory.DocumentAndEmail);
                    break;
                case FormatCategory.EbookAndComic:
                    fields |= SpecialistField.EbookAndPublishing;
                    break;
                case FormatCategory.Font:
                    fields |= SpecialistField.WebAndFonts;
                    break;
            }
        }

        return (categories, fields);
    }

    private static int GetNaturalTargetOrder(string primarySource, FormatCategory? sourceCategory, string targetCanonical, int hops)
    {
        string src = CanonicalExtension(primarySource);
        ReadOnlySpan<string> priority = (src, sourceCategory) switch
        {
            ("pdf", _) or (_, FormatCategory.Pdf) =>
                ["pdfa", "docx", "png", "jpg", "txt", "md", "html", "webp", "odt", "rtf", "epub"],
            ("txt", _) => ["md", "pdf", "docx", "html", "rtf", "odt", "pdfa", "json", "csv", "epub", "rst", "adoc"],
            (_, FormatCategory.TextAndMarkup) => ["txt", "md", "html", "pdf", "docx", "odt", "rtf", "pdfa", "json", "csv", "epub", "rst", "adoc"],
            ("rtf", _) => ["pdf", "docx", "txt", "md", "html", "odt", "pdfa"],
            (_, FormatCategory.DocumentAndEmail) => ["pdf", "txt", "md", "html", "odt", "rtf", "docx", "pdfa", "epub", "png", "jpg", "webp", "bmp", "gif", "tiff", "ico"],
            // Il PDF è la richiesta più comune per un foglio: non deve finire dentro "+ EXTRA".
            ("csv" or "tsv", _) =>
                ["xlsx", "pdf", "json", "csv", "tsv", "xml", "yaml", "html", "md", "ods", "toml", "ini"],
            (_, FormatCategory.Spreadsheet) =>
                ["pdf", "csv", "xlsx", "json", "html", "xml", "tsv", "md", "yaml", "ods", "toml", "ini"],
            (_, FormatCategory.DataAndConfig) =>
                ["yaml", "json", "xml", "toml", "csv", "ini", "tsv", "jsonl", "xlsx", "html", "md", "pdf"],
            (_, FormatCategory.RasterAndRaw) =>
                ["jpg", "png", "webp", "bmp", "gif", "tiff", "ico", "pdf", "pdfa", "avif", "jxl"],
            (_, FormatCategory.Vector or FormatCategory.Cad) =>
                ["dxf", "svg", "pdf", "png", "webp", "jpg", "bmp"],
            (_, FormatCategory.Model3D) =>
                ["obj", "glb", "stl", "ply", "3mf", "dae", "fbx", "png", "gltf", "gif", "pdf", "jpg", "webp", "bmp", "svg", "dxf"],
            (_, FormatCategory.Audio) =>
                ["mp3", "wav", "txt", "srt", "flac", "m4a", "ogg", "aac", "opus", "wma", "aiff", "alac"],
            (_, FormatCategory.Video) =>
                ["mp4", "mkv", "mov", "webm", "avi", "mp3", "txt", "srt", "wav", "flac", "m4a", "aac", "ogg", "opus", "gif", "webp"],
            (_, FormatCategory.ArchiveAndDisk) =>
                ["tar", "gz", "tgz", "7z", "zip"],
            _ => []
        };

        int idx = priority.IndexOf(targetCanonical);
        if (idx >= 0) return idx;
        return hops == 1 ? 100 : 200 + hops * 10;
    }

    private static Dictionary<string, ConversionRoute> FindRoutes(
        string source, Dictionary<string, List<PlannedHop>> graph, int maxHops)
    {
        bool protectStructure = HasVisualStructure(source);
        var sourceCategory = FormatRegistry.Find(source)?.Category;
        bool rasterSource = sourceCategory == FormatCategory.RasterAndRaw;
        var bestRoutes = new Dictionary<string, ConversionRoute>(StringComparer.Ordinal);
        var frontier = new Dictionary<(string Extension, bool HasTextBridge), (PlannedHop[] Hops, int Cost)>
        {
            [(source, false)] = ([], 0)
        };

        // At each depth keep the cheapest path per extension and fidelity state.
        for (int depth = 0; depth < Math.Min(maxHops, MaxRouteHops) && frontier.Count > 0; depth++)
        {
            var next = new Dictionary<(string Extension, bool HasTextBridge), (PlannedHop[] Hops, int Cost)>();
            foreach (var (state, path) in frontier)
            {
                if (depth > 0 && state.Extension is "pdfa" or "doc") continue;

                // Wrapping pixels in PDF adds no text or document structure to extract.
                if (rasterSource && state.Extension == "pdf") continue;

                // Audio and video must never expand past non-media bridges into office/document/image formats.
                if (depth > 0 && sourceCategory == FormatCategory.Audio &&
                    FormatRegistry.Find(state.Extension)?.Category != FormatCategory.Audio) continue;
                if (depth > 0 && sourceCategory == FormatCategory.Video &&
                    FormatRegistry.Find(state.Extension)?.Category is not (FormatCategory.Video or FormatCategory.Audio)) continue;

                if (!graph.TryGetValue(state.Extension, out var outgoing))
                    continue;

                // Text or data is a bridge only when we continue past it.
                bool hasTextBridge = state.HasTextBridge || (protectStructure && depth > 0 && IsTextOrData(state.Extension));
                foreach (var hop in outgoing)
                {
                    // An image exported as GIF is a still, not a source video stream.
                    if (depth > 0 && state.Extension == "gif" && hop.Engine.Id == "ffmpeg") continue;
                    string target = hop.Edge.TargetExtension;
                    if (target == source || target == state.Extension)
                        continue;

                    // Pure text, markup, data and spreadsheets must never bridge through PDF into raster images
                    // (except archival PDF/A conformation pdf -> pdfa).
                    if (depth > 0 && state.Extension == "pdf" && target != "pdfa" &&
                        (sourceCategory is FormatCategory.TextAndMarkup or FormatCategory.DataAndConfig or FormatCategory.Spreadsheet ||
                         CanonicalExtension(source) == "rtf"))
                        continue;

                    PlannedHop[] candidateHops = [.. path.Hops, hop];
                    if (sourceCategory is FormatCategory.Audio or FormatCategory.Video &&
                        !IsMediaRouteAllowed(sourceCategory.Value, target, candidateHops))
                        continue;

                    var key = (target, hasTextBridge);
                    int cost = path.Cost + hop.Edge.BaseCost + depth * 25;
                    if (!next.TryGetValue(key, out var previous) || cost < previous.Cost)
                        next[key] = (candidateHops, cost);
                }
            }

            foreach (var (state, path) in next)
            {
                if (CanonicalExtension(state.Extension) == CanonicalExtension(source)) continue;
                if (state.HasTextBridge && HasVisualStructure(state.Extension))
                    continue;
                if (!IsSensibleRoute(source, state.Extension, path.Hops))
                    continue;
                if (!bestRoutes.TryGetValue(state.Extension, out var previous) || path.Cost < previous.TotalCost)
                    bestRoutes[state.Extension] = new ConversionRoute(source, state.Extension, path.Hops, path.Cost);
            }
            frontier = next;
        }
        return bestRoutes;
    }

    private static Dictionary<string, List<PlannedHop>> BuildGraph(FlipSettings settings)
    {
        var graph = new Dictionary<string, List<PlannedHop>>(StringComparer.Ordinal);
        var priorityOrder = settings.EnginePriorityOrder;
        foreach (var engine in _engines.Values.OrderBy(engine => engine.Id, StringComparer.Ordinal))
        {
            if (!engine.IsAvailable(settings))
                continue;

            int priorityBonus = GetEnginePriorityBonus(engine.Id, priorityOrder);
            foreach (var edge in engine.SupportedEdges)
            {
                if (!IsUsableEdge(engine.Id, edge)) continue;
                if (!graph.TryGetValue(edge.SourceExtension, out var outgoing))
                {
                    outgoing = [];
                    graph[edge.SourceExtension] = outgoing;
                }

                int cost = Math.Max(1, edge.BaseCost - priorityBonus);
                // OpenXML HTML/Markdown is a text/table export. Prefer the richer document reader.
                if (engine.Id == "openxml_builtin" && (edge.SourceExtension is "docx" or "odt") &&
                    (edge.TargetExtension is "html" or "md")) cost += 20;
                // Prefer LibreOffice over simplified built-in / Pandoc converters for Office document layout fidelity.
                if (IsOfficeDocumentToPdf(edge.SourceExtension, edge.TargetExtension) && engine.Id == "pdf_builtin")
                    cost += 20;
                // LibreOffice conserva grafici e stampa dei fogli; una tabella CSV è più fedele con il lettore interno.
                if (edge.TargetExtension is "pdf" or "pdfa" &&
                    (engine.Id == "pdf_builtin" ? edge.SourceExtension is "xlsx" or "xlsm" or "xltx" or "xls" or "ods"
                        : engine.Id == "libreoffice" && edge.SourceExtension == "csv"))
                    cost += 20;
                if (IsOfficeWordFormat(edge.SourceExtension) && IsOfficeWordFormat(edge.TargetExtension) &&
                    engine.Id is "openxml_builtin" or "pandoc")
                    cost += 20;
                var adjusted = new ConversionEdge(edge.SourceExtension, edge.TargetExtension, edge.EngineId, cost);
                outgoing.Add(new PlannedHop(adjusted, engine));
            }
        }
        return graph;
    }

    private static bool IsUsableEdge(string engineId, ConversionEdge edge)
    {
        if (engineId == "ffmpeg" && edge.SourceExtension == "gif" &&
            FormatRegistry.Find(edge.TargetExtension)?.Category is FormatCategory.Audio or FormatCategory.Subtitle)
            return false;

        // The CLI wrapper passes these reader/writer names verbatim. Pandoc uses plain only
        // as a writer, and expects latex, typst and asciidoc instead of file extensions.
        if (engineId == "pandoc")
            return edge.SourceExtension is not ("txt" or "tex" or "typ" or "adoc") &&
                edge.TargetExtension is not ("tex" or "typ" or "adoc");

        // LibreOffice's export filters are specific to Writer, Calc and Impress.
        if (engineId != "libreoffice") return true;
        return edge.TargetExtension switch
        {
            "docx" or "odt" or "rtf" => edge.SourceExtension is "doc" or "docx" or "odt" or "rtf" or "wps" or "pages",
            "xlsx" or "ods" => edge.SourceExtension is "xls" or "xlsx" or "xlsm" or "xltx" or "xlsb" or "ods" or "csv" or "numbers",
            "pptx" or "odp" => edge.SourceExtension is "ppt" or "pptx" or "odp" or "key",
            "txt" => edge.SourceExtension is "doc" or "docx" or "odt" or "rtf" or "wps" or "pages",
            "html" => edge.SourceExtension is "doc" or "docx" or "odt" or "rtf" or "wps" or "pages" or
                "xls" or "xlsx" or "xlsm" or "xltx" or "xlsb" or "ods" or "csv" or "numbers",
            _ => true
        };
    }

    private static string NormalizeExtension(string? extension) =>
        (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();

    private static string CanonicalExtension(string extension) =>
        FormatRegistry.CanonicalExtension(extension);

    private static bool IsMediaRouteAllowed(FormatCategory sourceCategory, string targetExtension, IReadOnlyList<PlannedHop> hops)
    {
        var targetCategory = FormatRegistry.Find(targetExtension)?.Category;
        if (sourceCategory == FormatCategory.Audio)
        {
            return targetCategory == FormatCategory.Audio ||
                (hops.Count == 1 && CanonicalExtension(targetExtension) is "srt" or "txt");
        }

        if (sourceCategory == FormatCategory.Video)
        {
            return targetCategory is FormatCategory.Video or FormatCategory.Audio ||
                (hops.Count == 1 && CanonicalExtension(targetExtension) is "gif" or "webp") ||
                // Trascrizione Whisper AI: solo a 1 salto e solo tramite il motore locale.
                (hops.Count == 1 && CanonicalExtension(targetExtension) is "txt" or "srt" &&
                 hops[0].Engine.Id.Equals("whisper", StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    private static bool IsSensibleRoute(string sourceExtension, string targetExtension, IReadOnlyList<PlannedHop> hops)
    {
        if (hops.Count == 0) return false;
        if (CanonicalExtension(sourceExtension) == CanonicalExtension(targetExtension)) return false;

        if (IsDocxToPdf(sourceExtension, targetExtension))
            return hops.Count == 1 && hops[0].Engine.Id is "libreoffice" or "pdf_builtin";

        // This Word export reconstructs editable text from the PDF, then saves a real Word 97–2003 file.
        if (targetExtension == "doc")
            return sourceExtension == "pdf" && hops.Count == 2 &&
                hops[0].Edge.TargetExtension == "docx" && hops[0].Engine.Id == "pdf_builtin" &&
                hops[1].Engine.Id == "libreoffice";

        var sourceCategory = FormatRegistry.Find(sourceExtension)?.Category;
        var targetCategory = FormatRegistry.Find(targetExtension)?.Category;

        if (sourceCategory is FormatCategory.Audio or FormatCategory.Video)
        {
            if (!IsMediaRouteAllowed(sourceCategory.Value, targetExtension, hops))
                return false;

            if (hops.Count == 1)
                return true;

            for (int i = 0; i < hops.Count - 1; i++)
            {
                var midCategory = FormatRegistry.Find(hops[i].Edge.TargetExtension)?.Category;
                if (sourceCategory == FormatCategory.Audio && midCategory != FormatCategory.Audio)
                    return false;
                if (sourceCategory == FormatCategory.Video &&
                    midCategory is not (FormatCategory.Video or FormatCategory.Audio))
                    return false;
            }
            return true;
        }

        if (sourceCategory is FormatCategory.TextAndMarkup or FormatCategory.DataAndConfig ||
            CanonicalExtension(sourceExtension) == "rtf")
        {
            if (targetCategory is
                FormatCategory.RasterAndRaw or FormatCategory.Audio or FormatCategory.Video or
                FormatCategory.Model3D or FormatCategory.Cad or FormatCategory.Vector)
                return false;
        }

        if (sourceCategory == FormatCategory.Spreadsheet &&
            targetCategory is FormatCategory.RasterAndRaw or FormatCategory.Audio or FormatCategory.Video or FormatCategory.Model3D)
        {
            return false;
        }

        if (sourceCategory == FormatCategory.Model3D)
        {
            return hops.Count == 1 && (targetCategory is
                FormatCategory.Model3D or FormatCategory.RasterAndRaw or
                FormatCategory.Pdf or FormatCategory.Vector or FormatCategory.Cad);
        }

        if (hops.Count == 1)
            return true;

        // Multi-hop (2-3 hops) is only allowed within the same family or across validated structural bridges.
        if (sourceCategory is null || targetCategory is null)
            return false;

        bool allSameCategory = targetCategory == sourceCategory && hops
            .Take(hops.Count - 1)
            .All(h => FormatRegistry.Find(h.Edge.TargetExtension)?.Category == sourceCategory);
        if (allSameCategory)
            return true;

        // Archival PDF/A conformation (e.g. docx/doc/odt/rtf/md/txt/html/png/jpg/webp/bmp -> pdf -> pdfa).
        if (CanonicalExtension(targetExtension) == "pdfa" && hops.Count == 2 &&
            CanonicalExtension(hops[0].Edge.TargetExtension) == "pdf" &&
            sourceCategory is FormatCategory.DocumentAndEmail or FormatCategory.Hwp or
                FormatCategory.Presentation or FormatCategory.EbookAndComic or
                FormatCategory.TextAndMarkup or FormatCategory.RasterAndRaw)
        {
            return true;
        }

        bool isDocBridge = (sourceCategory is
            FormatCategory.DocumentAndEmail or FormatCategory.Hwp or
            FormatCategory.Presentation or FormatCategory.EbookAndComic or FormatCategory.Pdf) &&
            CanonicalExtension(sourceExtension) != "rtf";
        if (isDocBridge && targetCategory is FormatCategory.Pdf or FormatCategory.RasterAndRaw)
        {
            bool seenRaster = false;
            for (int i = 0; i < hops.Count - 1; i++)
            {
                var midCategory = FormatRegistry.Find(hops[i].Edge.TargetExtension)?.Category;
                if (midCategory == FormatCategory.RasterAndRaw)
                    seenRaster = true;

                if (midCategory is not (FormatCategory.Pdf or FormatCategory.RasterAndRaw) &&
                    midCategory != sourceCategory)
                    return false;

                // Never step backwards from pixels to PDF/document structure.
                if (seenRaster && midCategory != FormatCategory.RasterAndRaw)
                    return false;
            }

            return !seenRaster || targetCategory == FormatCategory.RasterAndRaw;
        }

        bool isGeometryBridge = sourceCategory is FormatCategory.Vector or FormatCategory.Cad or FormatCategory.Model3D;
        if (isGeometryBridge && targetCategory is
            FormatCategory.Vector or FormatCategory.Cad or FormatCategory.Model3D or
            FormatCategory.Pdf or FormatCategory.RasterAndRaw)
        {
            for (int i = 0; i < hops.Count - 1; i++)
            {
                var midCategory = FormatRegistry.Find(hops[i].Edge.TargetExtension)?.Category;
                if (midCategory is not (
                    FormatCategory.Vector or FormatCategory.Cad or FormatCategory.Model3D or FormatCategory.Pdf))
                    return false;
            }
            return true;
        }

        return false;
    }

    private static FlipSettings CaptureSettings(FlipSettings? settings) =>
        settings?.CreateSnapshot() ?? SettingsStore.Current.CreateSnapshot();

    private static bool IsTextOrData(string extension) =>
        FormatRegistry.Find(extension)?.Category is FormatCategory.TextAndMarkup or FormatCategory.DataAndConfig;

    private static bool HasVisualStructure(string extension)
    {
        // HTML is a visual endpoint, but a bridge may contain only extracted text.
        return extension is "html" or "htm" or "xhtml" || FormatRegistry.Find(extension)?.Category is
            FormatCategory.DocumentAndEmail or FormatCategory.Hwp or FormatCategory.Spreadsheet or
            FormatCategory.Presentation or FormatCategory.Pdf or FormatCategory.EbookAndComic or
            FormatCategory.Vector or FormatCategory.Cad or FormatCategory.Model3D or FormatCategory.RasterAndRaw;
    }

    // ── Esecuzione Pipeline Multi-Hop ────────────────────────────────────────

    public sealed record BatchConversionResult(string SourcePath, string TargetExtension, EngineExecutionResult Result);

    /// <summary>Runs a single conversion through the same per-source commit as a batch.</summary>
    public static async Task<EngineExecutionResult> ExecuteRouteAsync(
        string sourceFilePath,
        string targetExt,
        FlipSettings? settings = null,
        Dictionary<string, string>? parameters = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExt);

        var results = await ExecuteBatchAsync([sourceFilePath], [targetExt], settings, parameters, progress, ct)
            .ConfigureAwait(false);
        return results.Count > 0 ? results[0].Result : new(false, "", "Conversione annullata dall'utente.");
    }

    /// <summary>Stages every requested target for a source before committing or recycling it.</summary>
    public static async Task<IReadOnlyList<BatchConversionResult>> ExecuteBatchAsync(
        IReadOnlyList<string> sourceFilePaths,
        IReadOnlyList<string> targetExtensions,
        FlipSettings? settings = null,
        Dictionary<string, string>? parameters = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePaths);
        ArgumentNullException.ThrowIfNull(targetExtensions);

        string[] targets = targetExtensions.Select(ext => ext.Trim().TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sourceFilePaths.Count == 0 || targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Seleziona almeno un file sorgente e un formato di output valido.");

        var snapshot = CaptureSettings(settings);
        var jobParameters = parameters is null ? null : new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        var batch = new OutputPathAllocator.Batch(sourceFilePaths, snapshot);
        if (ct.IsCancellationRequested) return [];

        string[] sourceExtensions = batch.Sources.Select(source => NormalizeExtension(Path.GetExtension(source))).ToArray();
        var graph = BuildGraph(snapshot);
        var routes = sourceExtensions.Distinct(StringComparer.Ordinal)
            .ToDictionary(extension => extension, extension => FindRoutes(extension, graph, MaxRouteHops));
        var results = new IReadOnlyList<BatchConversionResult>?[batch.Sources.Count];
        var batchProgress = progress is null ? null : new BatchProgress(progress, batch.Sources.Count);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, batch.Sources.Count), new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(snapshot.MaxParallelJobs, 1, batch.Sources.Count),
                CancellationToken = ct
            }, async (index, token) =>
            {
                results[index] = await ExecuteSourceAsync(batch, batch.Sources[index], targets,
                    routes[sourceExtensions[index]], snapshot, jobParameters, batchProgress?.ForSource(index), token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

        return results.OfType<IReadOnlyList<BatchConversionResult>>().SelectMany(source => source).ToArray();
    }

    private static async Task<IReadOnlyList<BatchConversionResult>> ExecuteSourceAsync(
        OutputPathAllocator.Batch batch,
        string source,
        string[] targets,
        Dictionary<string, ConversionRoute> routes,
        FlipSettings settings,
        Dictionary<string, string>? parameters,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var staged = new List<OutputPathAllocator.StagedOutput>(targets.Length);
        var outputCounts = new int[targets.Length];
        var targetErrors = new string?[targets.Length];
        long sourceBytes = 0;
        OutputPathAllocator.CommitResult commit;
        try
        {
            ct.ThrowIfCancellationRequested();
            using var job = batch.CreateJob(source);
            sourceBytes = job.SourceBytes;
            ConversionEngine.ValidateContainer(source);
            string sourceExt = Path.GetExtension(source).TrimStart('.');
            string sourceName = Path.GetFileNameWithoutExtension(source);
            for (int i = 0; i < targets.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                string target = targets[i];
                IReadOnlyList<string> artifacts;
                try
                {
                    if (!routes.TryGetValue(target, out var route))
                        throw new IOException($"Nessun percorso da '{sourceExt}' a '{target}'.");

                    var targetProgress = ScaleProgress(progress, 0.95 * i / targets.Length, 0.95 / targets.Length);
                    var targetParameters = MergeTargetParameters(settings, target, parameters);
                    artifacts = await StageRouteAsync(route, job, source, i, settings, targetParameters, targetProgress, ct)
                        .ConfigureAwait(false);
                }
                // Un formato non riuscito non deve annullare gli altri scelti insieme.
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested && targets.Length > 1)
                {
                    targetErrors[i] = ex.Message;
                    continue;
                }
                outputCounts[i] = artifacts.Count;
                for (int page = 0; page < artifacts.Count; page++)
                {
                    string suffix = artifacts.Count > 1 ? $".page-{page + 1:000}" : "";
                    staged.Add(new(artifacts[page], OutputPathAllocator.BuildOutputFileName(sourceName, sourceExt, target, suffix)));
                }
            }
            commit = staged.Count == 0 && targetErrors.Any(error => error != null)
                ? new([], targetErrors.First(error => error != null))
                : job.CommitOutputAndApplyRules(staged, ct, allowRecycle: targetErrors.All(error => error == null));
        }
        catch (OperationCanceledException)
        {
            commit = new([], "Conversione annullata dall'utente.");
        }
        catch (Exception ex)
        {
            commit = new([], ex.Message);
        }

        var results = new List<BatchConversionResult>(targets.Length);
        int publishedIndex = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            var published = commit.Outputs.Skip(publishedIndex).Take(outputCounts[i]).ToArray();
            publishedIndex += published.Length;
            bool success = outputCounts[i] > 0 && published.Length == outputCounts[i];
            string[] paths = published.Select(output => output.Path).ToArray();
            var result = new EngineExecutionResult(success, paths.FirstOrDefault() ?? "",
                success ? commit.Warning : targetErrors[i] ?? commit.Error, published.Sum(output => output.Bytes))
            {
                OutputFilePaths = paths
            };
            RecordHistory(Path.GetFileName(source), targets[i], result.Success ? sourceBytes - result.OutputBytes : 0,
                timer.ElapsedMilliseconds, result.Success);
            results.Add(new(source, targets[i], result));
        }

        if (!ct.IsCancellationRequested)
            progress?.Report(1);
        return results;
    }

    private static async Task<IReadOnlyList<string>> StageRouteAsync(
        ConversionRoute route,
        OutputPathAllocator.Job job,
        string source,
        int targetIndex,
        FlipSettings settings,
        Dictionary<string, string>? parameters,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        // The built-in exporter strips metadata itself; LibreOffice cannot.
        if (IsDocxToPdf(route.SourceExtension, route.TargetExtension) &&
            route.Hops[0].Engine.Id == "libreoffice" && RequestsMetadataRemoval(parameters))
            throw new IOException(Loc.T(
                "La conversione DOCX → PDF con impaginazione non supporta la rimozione dei metadati. Disattiva la rimozione nel preset PDF per continuare.",
                "Layout-preserving DOCX to PDF conversion does not support metadata removal. Turn off metadata removal in the PDF preset to continue."));

        IReadOnlyList<string> inputs = [source];
        for (int i = 0; i < route.Hops.Count; i++)
        {
            var hop = route.Hops[i];
            var hopProgress = ScaleProgress(progress, (double)i / route.Hops.Count, 1.0 / route.Hops.Count);
            var outputs = new List<string>(inputs.Count);
            for (int j = 0; j < inputs.Count; j++)
            {
                ct.ThrowIfCancellationRequested();
                string output = job.Stage($"target_{targetIndex}_hop_{i}_input_{j}.{hop.Edge.TargetExtension}");
                var context = new ConversionContext
                {
                    InputFilePath = inputs[j],
                    OutputFilePath = output,
                    SourceExtension = hop.Edge.SourceExtension,
                    TargetExtension = hop.Edge.TargetExtension,
                    Settings = settings,
                    CustomParameters = parameters is null
                        ? new(StringComparer.OrdinalIgnoreCase)
                        : new(parameters, StringComparer.OrdinalIgnoreCase),
                    Progress = ScaleProgress(hopProgress, (double)j / inputs.Count, 1.0 / inputs.Count),
                    CancellationToken = ct
                };
                var result = await ConvertHopAsync(hop, context).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!result.Success)
                    throw new IOException(result.ErrorMessage ?? $"Salto {i + 1}/{route.Hops.Count} fallito ({hop.Engine.DisplayName}).");

                AddStagedOutputs(result, output, hop, outputs);
            }
            inputs = outputs;
        }
        return inputs;
    }

    internal static Dictionary<string, string> MergeTargetParameters(
        FlipSettings settings,
        string targetExtension,
        Dictionary<string, string>? explicitParameters)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string target = NormalizeExtension(targetExtension);
        var recipe = settings.Recipes.FirstOrDefault(r =>
            r is { Enabled: true } &&
            string.Equals(NormalizeExtension(r.TargetExtension), target, StringComparison.OrdinalIgnoreCase));
        if (recipe != null)
        {
            foreach (var (key, value) in recipe.ToParameters())
                merged[key] = value;
        }
        if (explicitParameters != null)
        {
            foreach (var (key, value) in explicitParameters)
                merged[key] = value;
        }
        return merged;
    }

    public static async Task<IReadOnlyList<BatchConversionResult>> ExecuteRecipeAsync(
        IReadOnlyList<string> sourceFilePaths,
        FlipRecipe recipe,
        FlipSettings? settings = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceFilePaths);
        ArgumentNullException.ThrowIfNull(recipe);
        string target = NormalizeExtension(recipe.TargetExtension);
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Il preset non specifica un formato di destinazione valido.", nameof(recipe));

        var snapshot = CaptureSettings(settings);
        var recipeParams = recipe.ToParameters();
        var batch = new OutputPathAllocator.Batch(sourceFilePaths, snapshot);
        if (batch.Sources.Count == 0 || ct.IsCancellationRequested) return [];

        var sameExt = new List<string>();
        var routed = new List<string>();
        foreach (string src in batch.Sources)
        {
            string srcExt = CanonicalExtension(NormalizeExtension(Path.GetExtension(src)));
            if (string.Equals(srcExt, CanonicalExtension(target), StringComparison.OrdinalIgnoreCase))
                sameExt.Add(src);
            else
                routed.Add(src);
        }

        var combined = new List<BatchConversionResult>(batch.Sources.Count);
        if (routed.Count > 0)
        {
            combined.AddRange(await ExecuteBatchAsync(routed, [target], snapshot, recipeParams, progress, ct).ConfigureAwait(false));
        }

        if (sameExt.Count > 0 && _engines.TryGetValue("image_builtin", out var imgEngine))
        {
            foreach (string src in sameExt)
            {
                ct.ThrowIfCancellationRequested();
                string srcExt = NormalizeExtension(Path.GetExtension(src));
                string srcName = Path.GetFileNameWithoutExtension(src);
                var sw = Stopwatch.StartNew();
                using var job = batch.CreateJob(src);
                string stagedOut = job.Stage($"preset_{Guid.NewGuid():N}.{target}");
                var res = await imgEngine.ConvertAsync(new ConversionContext
                {
                    InputFilePath = src,
                    OutputFilePath = stagedOut,
                    SourceExtension = srcExt,
                    TargetExtension = target,
                    Settings = snapshot,
                    CustomParameters = new(recipeParams, StringComparer.OrdinalIgnoreCase),
                    CancellationToken = ct
                }).ConfigureAwait(false);
                EngineExecutionResult finalRes;
                if (res.Success)
                {
                    string outName = OutputPathAllocator.BuildOutputFileName(srcName, srcExt, target);
                    var commit = job.CommitOutputAndApplyRules([new(stagedOut, outName)], ct);
                    bool ok = commit.Outputs.Count > 0;
                    string[] paths = commit.Outputs.Select(o => o.Path).ToArray();
                    finalRes = new EngineExecutionResult(ok, paths.FirstOrDefault() ?? "", ok ? commit.Warning : commit.Error,
                        commit.Outputs.Sum(o => o.Bytes)) { OutputFilePaths = paths };
                }
                else
                {
                    finalRes = res;
                }
                RecordHistory(Path.GetFileName(src), target, finalRes.Success ? job.SourceBytes - finalRes.OutputBytes : 0,
                    sw.ElapsedMilliseconds, finalRes.Success);
                combined.Add(new(src, target, finalRes));
            }
        }

        return combined;
    }

    private static Task<EngineExecutionResult> ConvertHopAsync(PlannedHop hop, ConversionContext context)
    {
        bool stripMetadata = RequestsMetadataRemoval(context.CustomParameters);
        if (stripMetadata && hop.Engine.Id == "libreoffice" &&
            context.SourceExtension is "docx" or "odt" or "rtf" &&
            context.TargetExtension == "pdf" &&
            _engines.TryGetValue("pdf_builtin", out var privacyEngine))
        {
            // LibreOffice can copy document properties into PDF. The built-in exporter honors this preset.
            // A direct DOCX -> PDF export is refused earlier, so for DOCX this only covers intermediate PDFs.
            return privacyEngine.ConvertAsync(context);
        }

        if (hop.Engine.Id == "pdf_builtin" && context.TargetExtension == "pdf" &&
            Policy.Images.Contains("." + context.SourceExtension))
        {
            // Reuse the historical EXIF-aware reader inside the isolated planner job.
            using var bitmap = ConversionEngine.ReadImage(context.InputFilePath);
            ConversionEngine.ImagePdf(bitmap, context.OutputFilePath, stripMetadata);
            return Task.FromResult(new EngineExecutionResult(true, context.OutputFilePath, null,
                new FileInfo(context.OutputFilePath).Length));
        }
        return hop.Engine.ConvertAsync(context);
    }

    private static bool IsDocxToPdf(string source, string target) => source == "docx" && target == "pdf";

    private static bool RequestsMetadataRemoval(Dictionary<string, string>? parameters) =>
        parameters is not null && parameters.TryGetValue("strip_metadata", out var value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private static bool IsOfficeWordFormat(string extension) => extension is "doc" or "docx" or "odt" or "rtf";

    private static bool IsOfficeDocumentToPdf(string source, string target) =>
        IsOfficeWordFormat(source) && target is "pdf" or "pdfa";

    private static void AddStagedOutputs(EngineExecutionResult result, string requestedPath, PlannedHop hop, List<string> outputs)
    {
        var paths = result.OutputFilePaths;
        if (paths.Count == 0 || !string.Equals(Path.GetFullPath(paths[0]), requestedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Il motore {hop.Engine.DisplayName} non ha prodotto il file temporaneo richiesto.");

        string directory = Path.GetDirectoryName(requestedPath)!;
        string prefix = Path.GetFileNameWithoutExtension(requestedPath) + ".";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            var artifact = new FileInfo(path);
            if (!string.Equals(artifact.DirectoryName, directory, StringComparison.OrdinalIgnoreCase) ||
                !artifact.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(artifact.Extension, Path.GetExtension(requestedPath), StringComparison.OrdinalIgnoreCase) ||
                !seen.Add(artifact.FullName) || !artifact.Exists ||
                (artifact.Attributes & FileAttributes.ReparsePoint) != 0 || artifact.Length == 0)
                throw new IOException($"Il motore {hop.Engine.DisplayName} ha restituito un file temporaneo non valido.");

            outputs.Add(artifact.FullName);
        }
    }

    private static IProgress<double>? ScaleProgress(IProgress<double>? progress, double start, double scale) =>
        progress is null ? null : new ScaledProgress(progress, start, scale);

    private sealed class ScaledProgress(IProgress<double> target, double start, double scale) : IProgress<double>
    {
        public void Report(double value) => target.Report(start + (Math.Clamp(value, 0, 1) * scale));
    }

    private sealed class BatchProgress(IProgress<double> target, int count)
    {
        private readonly object sync = new();
        private readonly double[] fractions = new double[count];

        internal IProgress<double> ForSource(int index) => new SourceProgress(this, index);

        private sealed class SourceProgress(BatchProgress batch, int index) : IProgress<double>
        {
            public void Report(double value) => batch.Report(index, value);
        }

        private void Report(int index, double value)
        {
            if (!double.IsFinite(value)) return;
            lock (sync)
            {
                if (value <= fractions[index]) return;
                fractions[index] = Math.Clamp(value, 0, 1);
                target.Report(fractions.Average());
            }
        }
    }

    // ── Helper Interni ───────────────────────────────────────────────────────

    private static int GetEnginePriorityBonus(string engineId, List<string> priorityOrder)
    {
        if (priorityOrder == null || priorityOrder.Count == 0) return 0;
        int idx = priorityOrder.FindIndex(p => string.Equals(p, engineId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            // Motore a indice 0 riceve bonus massimo (+15), via via a scalare
            return Math.Max(1, 15 - (idx * 3));
        }
        return 0;
    }

    private static void RecordHistory(
        string fileName,
        string targetExt,
        long bytesSaved,
        long durationMs,
        bool success)
    {
        try
        {
            SettingsStore.Current.RecordConversion(new RecentConversion
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                SourceFileName = fileName,
                TargetExtension = targetExt,
                BytesSaved = bytesSaved,
                DurationMs = durationMs,
                Success = success
            });
        }
        catch (Exception ex)
        {
            Evidence.Log("conversion.history.failed", ex.Message);
        }
    }

    public sealed record FileExtensionGroup(string Extension, string Label, string[] Files);

    /// <summary>
    /// Raggruppa un elenco di file per estensione normalizzata (ordinati per numerosità decrescente)
    /// così sia la Ruota sia l'Hub possono recuperare batch misti senza bloccare l'utente.
    /// </summary>
    public static IReadOnlyList<FileExtensionGroup> GroupFilesByExtension(IEnumerable<string> filePaths)
    {
        if (filePaths == null) return [];
        return filePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .GroupBy(p =>
            {
                string norm = NormalizeExtension(Path.GetExtension(p));
                return string.IsNullOrEmpty(norm) ? "(altro)" : norm;
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                string[] groupFiles = g.ToArray();
                string extTag = g.Key == "(altro)" ? "ALTRI" : g.Key.ToUpperInvariant();
                return new FileExtensionGroup(g.Key, $"{groupFiles.Length} {extTag}", groupFiles);
            })
            .OrderByDescending(g => g.Files.Length)
            .ThenBy(g => g.Extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

