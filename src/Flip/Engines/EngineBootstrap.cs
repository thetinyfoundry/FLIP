using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

/// <summary>
/// Bootstrap centrale per la registrazione thread-safe e idempotente di tutti i motori
/// (built-in ed esterni on-demand) e di tutti gli strumenti rapidi (Quick Tools) nel <see cref="RoutePlanner"/>.
/// </summary>
public static class EngineBootstrap
{
    private static readonly object _initLock = new();
    private static bool _initialized = false;

    /// <summary>
    /// Inizializza e registra tutti i motori e i quick tool nel <see cref="RoutePlanner"/>.
    /// È thread-safe e può essere invocato più volte senza effetti collaterali.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            // ── 1. Motori Built-in nativi (offline, nessuna dipendenza esterna) ────
            RoutePlanner.RegisterEngine(new DataSerializationEngine());
            RoutePlanner.RegisterEngine(new ArchiveEngine());
            RoutePlanner.RegisterEngine(new ImageEngine());
            RoutePlanner.RegisterEngine(new PdfEngine());
            RoutePlanner.RegisterEngine(new OfficeOpenXmlEngine());
            RoutePlanner.RegisterEngine(new Mesh3DVectorGeoEngine());

            // ── 2. Motori Esterni On-Demand (CLI wrappers) ────────────────────────
            RoutePlanner.RegisterEngine(new FfmpegEngine());
            RoutePlanner.RegisterEngine(new PandocEngine());
            RoutePlanner.RegisterEngine(new LibreOfficeEngine());
            RoutePlanner.RegisterEngine(new CalibreEngine());
            RoutePlanner.RegisterEngine(new LocalAiEngine());

            // ── 3. Quick Tools per il Retro della Ruota (12 strumenti rapidi) ─────
            RoutePlanner.RegisterQuickTool(new CropQuickTool());
            RoutePlanner.RegisterQuickTool(new RotateQuickTool());
            RoutePlanner.RegisterQuickTool(new MergePdfQuickTool());
            RoutePlanner.RegisterQuickTool(new PackZipQuickTool());
            RoutePlanner.RegisterQuickTool(new PackRarQuickTool());
            RoutePlanner.RegisterQuickTool(new TargetSizeQuickTool());
            RoutePlanner.RegisterQuickTool(new RemoveBgQuickTool());
            RoutePlanner.RegisterQuickTool(new OcrQuickTool());
            RoutePlanner.RegisterQuickTool(new PaletteQuickTool());
            RoutePlanner.RegisterQuickTool(new PrivacyCleanQuickTool());
            RoutePlanner.RegisterQuickTool(new WaveformQuickTool());
            RoutePlanner.RegisterQuickTool(new Turntable3DQuickTool());

            _initialized = true;
        }
    }

    /// <summary>
    /// Diagnostica dettagliata sullo stato del sistema motori, grafi e strumenti rapidi.
    /// </summary>
    public sealed class DiagnosticsSummary
    {
        public int TotalEnginesRegistered { get; init; }
        public int AvailableEnginesCount { get; init; }
        public int BuiltInEnginesCount { get; init; }
        public int ExternalEnginesCount { get; init; }
        public int ExternalEnginesAvailableCount { get; init; }
        public int TotalAvailableEdges { get; init; }
        public int RegisteredQuickToolsCount { get; init; }
        public Dictionary<string, (bool Available, string? Path)> ExternalEngineStatuses { get; init; } = new();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== DIAGNOSTICA MOTORI FLIP ===");
            sb.AppendLine($"Motori registrati: {TotalEnginesRegistered} (Built-in: {BuiltInEnginesCount}, Esterni: {ExternalEnginesCount})");
            sb.AppendLine($"Motori disponibili: {AvailableEnginesCount} (Esterni pronti: {ExternalEnginesAvailableCount})");
            sb.AppendLine($"Archi di conversione attivi: {TotalAvailableEdges}");
            sb.AppendLine($"Quick Tools registrati: {RegisteredQuickToolsCount}");
            sb.AppendLine("Stato motori esterni:");
            foreach (var (id, (avail, path)) in ExternalEngineStatuses)
            {
                string status = avail ? $"[PRONTO] ({path})" : "[NON DISPONIBILE]";
                sb.AppendLine($"  - {id}: {status}");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Restituisce un riepilogo diagnostico sui motori attivi, percorsi risolti e copertura del grafo.
    /// </summary>
    public static DiagnosticsSummary GetDiagnosticsSummary(FlipSettings settings)
    {
        EnsureInitialized();

        var engines = RoutePlanner.GetRegisteredEngines();
        int builtIn = engines.Count(e => !e.IsExternal);
        int external = engines.Count(e => e.IsExternal);
        int availableTotal = engines.Count(e => e.IsAvailable(settings));
        int availableExternal = engines.Count(e => e.IsExternal && e.IsAvailable(settings));

        var availableEngines = engines.Where(e => e.IsAvailable(settings)).ToList();
        int totalEdges = availableEngines.Sum(e => e.SupportedEdges.Count);

        var statuses = new Dictionary<string, (bool, string?)>(StringComparer.OrdinalIgnoreCase);
        foreach (var eng in engines.Where(e => e.IsExternal))
        {
            string? resolvedPath = CliResolver.Resolve(eng.Id, settings);
            statuses[eng.Id] = (resolvedPath != null, resolvedPath);
        }

        return new DiagnosticsSummary
        {
            TotalEnginesRegistered = engines.Count,
            BuiltInEnginesCount = builtIn,
            ExternalEnginesCount = external,
            AvailableEnginesCount = availableTotal,
            ExternalEnginesAvailableCount = availableExternal,
            TotalAvailableEdges = totalEdges,
            RegisteredQuickToolsCount = 12,
            ExternalEngineStatuses = statuses
        };
    }
}

