using System.Text.Json.Serialization;

namespace Flip.Core;

// ── Enums for Settings ──────────────────────────────────────────────────────

/// <summary>Dove salvare i file convertiti.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputDirectoryMode
{
    /// <summary>Salva nella stessa cartella del file sorgente.</summary>
    SameAsSource = 0,
    /// <summary>Salva in una cartella personalizzata specificata dall'utente.</summary>
    CustomFolder = 1,
    /// <summary>Crea una sottocartella automatica per batch con più di N file.</summary>
    SubfolderOnBatch = 2
}

/// <summary>Politica di gestione dei conflitti di nome file in output.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConflictPolicy
{
    /// <summary>Aggiunge un numero progressivo (es. foto_1.png, foto_2.png).</summary>
    AutoNumber = 0,
    /// <summary>Sovrascrive il file esistente.</summary>
    Overwrite = 1,
    /// <summary>Salta la conversione se il file di output esiste già.</summary>
    Skip = 2
}

/// <summary>Strategia di accelerazione hardware per la codifica video/audio.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HardwareAcceleration
{
    /// <summary>Rileva automaticamente la GPU disponibile.</summary>
    Auto = 0,
    /// <summary>NVIDIA NVENC.</summary>
    Nvenc = 1,
    /// <summary>AMD AMF.</summary>
    Amf = 2,
    /// <summary>Intel Quick Sync Video.</summary>
    Qsv = 3,
    /// <summary>Solo CPU, nessuna accelerazione hardware.</summary>
    CpuOnly = 4
}

// ── Watch Folder Rule ───────────────────────────────────────────────────────

/// <summary>Regola di monitoraggio cartella per conversione automatica.</summary>
public sealed class WatchFolderRule
{
    /// <summary>Identificativo univoco della regola.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Percorso assoluto della cartella da monitorare.</summary>
    public string FolderPath { get; set; } = "";

    /// <summary>Se la regola è attiva.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Estensioni di ingresso monitorate (es. ["png", "jpg"]). Lista vuota = tutte.</summary>
    public List<string> IncludeExtensions { get; set => field = value ?? []; } = [];

    /// <summary>Estensione target di conversione (es. "webp").</summary>
    public string TargetExtension { get; set; } = "";

    /// <summary>Se true, dopo una conversione riuscita il sorgente viene spostato nel Cestino.</summary>
    public bool DeleteSourceToRecycleBin { get; set; } = false;

    internal WatchFolderRule Copy()
    {
        var copy = (WatchFolderRule)MemberwiseClone();
        copy.IncludeExtensions = [.. IncludeExtensions];
        return copy;
    }
}

// ── Flip Recipe ─────────────────────────────────────────────────────────────

/// <summary>Preset & Macro multi-step personalizzati (nome classe mantenuto per retrocompatibilità settings.json).</summary>
public sealed class FlipRecipe
{
    /// <summary>Identificativo univoco del preset.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Se true, il preset viene applicato automaticamente alle conversioni verso TargetExtension.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Nome visualizzato del preset (es. "Comprimi per WhatsApp").</summary>
    public string Name { get; set; } = "";

    /// <summary>Estensione target (es. "jpg", "mp4").</summary>
    public string TargetExtension { get; set; } = "";

    /// <summary>Dimensione massima del lato più lungo in pixel, 0 = nessun ridimensionamento.</summary>
    public int MaxDimensionPx { get; set; } = 0;

    /// <summary>Qualità di compressione (1-100), 0 = default del motore.</summary>
    public int Quality { get; set; } = 0;

    /// <summary>Rimuovi metadati EXIF/XMP dal file convertito.</summary>
    public bool StripMetadata { get; set; } = false;

    /// <summary>Dimensione massima del file di output in MB, 0 = nessun limite.</summary>
    public double TargetMaxMegabytes { get; set; } = 0;

    internal Dictionary<string, string> ToParameters()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (MaxDimensionPx > 0)
            map["max_dimension"] = MaxDimensionPx.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Quality is >= 1 and <= 100)
            map["quality"] = Quality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (StripMetadata)
            map["strip_metadata"] = "true";
        if (TargetMaxMegabytes > 0)
            map["target_mb"] = TargetMaxMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return map;
    }

    internal FlipRecipe Copy() => (FlipRecipe)MemberwiseClone();
}

// ── Recent Conversion ───────────────────────────────────────────────────────

/// <summary>Record di una conversione recente per la cronologia.</summary>
public sealed class RecentConversion
{
    /// <summary>Timestamp UTC della conversione.</summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Nome del file sorgente (solo nome, non percorso completo).</summary>
    public string SourceFileName { get; set; } = "";

    /// <summary>Estensione target (es. "png").</summary>
    public string TargetExtension { get; set; } = "";

    /// <summary>Byte risparmiati (positivo = file più piccolo, negativo = più grande).</summary>
    public long BytesSaved { get; set; } = 0;

    /// <summary>Durata della conversione in millisecondi.</summary>
    public long DurationMs { get; set; } = 0;

    /// <summary>Se la conversione è riuscita.</summary>
    public bool Success { get; set; } = true;

    internal RecentConversion Copy() => (RecentConversion)MemberwiseClone();
}

// ── Main Settings Model ─────────────────────────────────────────────────────

/// <summary>
/// Modello unificato di tutte le impostazioni di FLIP, serializzabile in JSON.
/// Ogni proprietà ha un valore di default sensato per evitare null-reference.
/// Suddiviso logicamente nelle 6 aree dell'Hub Impostazioni.
/// </summary>
public sealed class FlipSettings
{
    private bool _isSnapshot;
    internal Lock? StoreLock { private get; set; }

    // ── 1. Wheel & Studio ───────────────────────────────────────────────

    /// <summary>Grado massimo di visibilità dei formati nella ruota (default: General).</summary>
    public FormatGrade MaxVisibleGrade { get; set => Set(ref field, value); } = FormatGrade.General;

    /// <summary>Pacchetti specialistici attivati (flag combinabili).</summary>
    public SpecialistField EnabledSpecialistFields { get; set => Set(ref field, value); } = SpecialistField.None;

    /// <summary>Formati preferiti ("pinnati") nel primo anello per categoria.
    /// Chiave = nome FormatCategory (es. "RasterAndRaw"), Valore = lista estensioni (es. ["png", "jpg"]).</summary>
    public Dictionary<string, List<string>> PinnedWheelFormatsByCategory
    {
        get => _isSnapshot ? CopyPinnedFormats(field) : field;
        set => Set(ref field, WithCaseInsensitiveKeys(value));
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Strumenti attivi sul retro della ruota (Flip Tab/Scroll).</summary>
    public List<string> EnabledBackSideTools
    {
        get => _isSnapshot ? [.. field] : field;
        set => Set(ref field, value ?? []);
    } = ["crop", "rotate", "merge_pdf", "pack_zip", "pack_rar", "target_size", "remove_bg", "ocr", "palette", "clean_meta", "waveform", "turntable_3d"];

    internal const int CurrentBackSideToolsRevision = 2;

    /// <summary>Revisione dell'elenco strumenti del retro: i nuovi strumenti vengono aggiunti
    /// una sola volta alle installazioni esistenti, senza riattivare quelli disattivati dall'utente.</summary>
    public int BackSideToolsRevision { get; set => Set(ref field, value); } = CurrentBackSideToolsRevision;

    /// <summary>
    /// Motori già suggeriti con l'avviso "installalo per avere più formati": ognuno compare una sola volta.
    /// Installarli o ignorarli resta una scelta dell'utente, dall'Hub.
    /// </summary>
    public List<string> SuggestedEngines
    {
        get => _isSnapshot ? [.. field] : field;
        set => Set(ref field, value ?? []);
    } = [];

    // ── 2. Trigger & OS ─────────────────────────────────────────────────

    /// <summary>Tasto di attivazione della ruota durante il drag (default: Freccia Giù).</summary>
    public string TriggerKey { get; set => Set(ref field, value ?? DefaultTriggerKey); } = DefaultTriggerKey;

    internal const string DefaultTriggerKey = "Down";

    /// <summary>Attiva il suono al cambio di settore della ruota.</summary>
    public bool SoundEnabled { get; set => Set(ref field, value); } = true;

    /// <summary>Abilita il gesto di apertura della ruota.</summary>
    public bool GestureEnabled { get; set => Set(ref field, value); } = true;

    /// <summary>
    /// Apre la ruota tenendo premuto il pulsante del mouse su un file selezionato
    /// in Esplora File o sul Desktop, senza premere alcun tasto.
    /// Richiede il gesto di apertura attivo.
    /// </summary>
    public bool HoldToOpenEnabled { get; set => Set(ref field, value); } = false;

    /// <summary>Durata della pressione prima dell'apertura della ruota, in millisecondi (1000 - 8000).</summary>
    public int HoldToOpenMilliseconds
    {
        get => field;
        set => Set(ref field, Math.Clamp(value, 1000, 8000));
    } = 3000;

    /// <summary>Lingua dell'interfaccia ("auto", "it", "en").</summary>
    public string Language { get; set => Set(ref field, Loc.NormalizePreference(value)); } = Loc.Auto;

    [JsonInclude]
    internal bool LegacyPreferencesMigrated { get; set => Set(ref field, value); }

    /// <summary>Se true, il tasto si attiva solo quando un tasto del mouse è premuto durante un drag.</summary>
    public bool RequireDragButton { get; set => Set(ref field, value); } = true;

    /// <summary>Accetta anche il pulsante secondario per attivare la ruota durante un drag.</summary>
    public bool AcceptSecondaryDragButton { get; set => Set(ref field, value); }

    /// <summary>Abilita il Flip Fronte/Retro della ruota con Tab o rotellina del mouse.</summary>
    public bool EnableFlipWithTabOrScroll { get; set => Set(ref field, value); } = true;

    /// <summary>Mostra l'aura luminosa arancione ai bordi del monitor mentre la ruota è aperta o un job è in corso.</summary>
    public bool EnableScreenEdgeAura { get; set => Set(ref field, value); } = true;

    /// <summary>Scorciatoia da tastiera per il Clipboard Flip (conversione rapida dagli appunti).</summary>
    public string EnableClipboardFlipShortcut { get; set => Set(ref field, value); } = "Ctrl+Shift+F";

    /// <summary>Avvia FLIP all'avvio di Windows.</summary>
    public bool StartWithWindows { get; set => Set(ref field, value); }

    /// <summary>Registra la voce "Converti con Flip" nel menu contestuale di Esplora Risorse.</summary>
    public bool EnableExplorerContextMenu { get; set => Set(ref field, value); }

    /// <summary>Abilita la modalità accessibilità Tobii / Sguardo senza mani (Gaze Orb + Dwell-to-Select).</summary>
    public bool GazeAccessibilityMode { get; set => Set(ref field, value); } = false;

    /// <summary>Tempo di sosta dello sguardo (Dwell Time) in millisecondi (600 - 2500 ms).</summary>
    public int GazeDwellMilliseconds
    {
        get => field;
        set => Set(ref field, Math.Clamp(value, 600, 2500));
    } = 1100;

    /// <summary>True dopo che l'utente ha completato o chiuso la guida visuale di primo avvio.</summary>
    public bool OnboardingCompleted { get; set => Set(ref field, value); } = false;

    // ── 3. Output Rules ─────────────────────────────────────────────────

    /// <summary>Dove salvare i file convertiti (default: sempre accanto al file originale).</summary>
    public OutputDirectoryMode OutputDirectoryMode { get; set => Set(ref field, value); } = OutputDirectoryMode.SameAsSource;

    /// <summary>Cartella personalizzata per la modalità CustomFolder.</summary>
    public string CustomOutputDirectory { get; set => Set(ref field, value ?? ""); } = "";

    /// <summary>True solo quando l'utente ha scelto esplicitamente una cartella personalizzata nell'Hub.</summary>
    [JsonInclude]
    internal bool UserSelectedCustomOutputDirectory { get; set => Set(ref field, value); } = false;

    /// <summary>Numero minimo di file in batch per creare una sottocartella automatica.</summary>
    public int BatchSubfolderThreshold { get; set => Set(ref field, value); } = 4;

    /// <summary>Politica in caso di conflitto di nome file.</summary>
    public ConflictPolicy ConflictPolicy { get; set => Set(ref field, value); } = ConflictPolicy.AutoNumber;

    /// <summary>Se true, sostituisci l'originale spostando il vecchio file nel Cestino di Windows.</summary>
    public bool ReplaceOriginalToRecycleBin { get; set => Set(ref field, value); }

    /// <summary>Se true, conserva il file convertito solo se è più piccolo dell'originale.</summary>
    public bool KeepOnlyIfSmaller { get; set => Set(ref field, value); }

    // ── 4. Engines & Performance ────────────────────────────────────────

    /// <summary>Numero massimo di conversioni parallele (default: 2).</summary>
    public int MaxParallelJobs { get; set => Set(ref field, value); } = 2;

    /// <summary>Strategia di accelerazione hardware.</summary>
    public HardwareAcceleration HardwareAcceleration { get; set => Set(ref field, value); } = HardwareAcceleration.Auto;

    /// <summary>Percorsi personalizzati per i motori esterni già installati sul PC.
    /// Chiave = nome motore ("ffmpeg", "pandoc", "libreoffice", "calibre", "whisper"),
    /// Valore = percorso assoluto dell'eseguibile.</summary>
    public Dictionary<string, string> CustomEnginePaths
    {
        get => _isSnapshot ? new(field, field.Comparer) : field;
        set => Set(ref field, WithCaseInsensitiveKeys(value));
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordine di priorità dei motori per le conversioni con più di un motore disponibile.</summary>
    public List<string> EnginePriorityOrder
    {
        get => _isSnapshot ? [.. field] : field;
        set => Set(ref field, value ?? []);
    } = [];

    // ── 5. Watch Folders ────────────────────────────────────────────────

    /// <summary>Lista delle regole di monitoraggio cartelle.</summary>
    public List<WatchFolderRule> WatchFolders
    {
        get => _isSnapshot ? field.Select(rule => rule.Copy()).ToList() : field;
        set => Set(ref field, value ?? []);
    } = [];

    // ── 6. History & Recipes ────────────────────────────────────────────

    /// <summary>Ricette multi-step personalizzate.</summary>
    public List<FlipRecipe> Recipes
    {
        get => _isSnapshot ? field.Select(recipe => recipe.Copy()).ToList() : field;
        set => Set(ref field, value ?? []);
    } = [];

    /// <summary>Cronologia delle conversioni recenti (ultime 500, rotazione automatica).</summary>
    public List<RecentConversion> RecentConversions
    {
        get => _isSnapshot ? field.Select(entry => entry.Copy()).ToList() : field;
        set => Set(ref field, value ?? []);
    } = [];

    // ── Utility ─────────────────────────────────────────────────────────

    /// <summary>Numero massimo di conversioni recenti conservate.</summary>
    internal const int MaxRecentConversions = 500;

    /// <summary>Crea una copia immutabile. Per lo stato condiviso usa <see cref="SettingsStore.CreateSnapshot"/>.</summary>
    public FlipSettings CreateSnapshot()
    {
        if (_isSnapshot) return this;
        if (StoreLock is not { } sync) return FreezeCopy();
        lock (sync)
        {
            return FreezeCopy();
        }
    }

    private FlipSettings FreezeCopy()
    {
        var copy = CreateMutableCopy();
        copy._isSnapshot = true;
        return copy;
    }

    internal FlipSettings CreateMutableCopy()
    {
        var copy = (FlipSettings)MemberwiseClone();
        copy._isSnapshot = false;
        copy.StoreLock = null;
        copy.PinnedWheelFormatsByCategory = CopyPinnedFormats(copy.PinnedWheelFormatsByCategory);
        copy.EnabledBackSideTools = [.. copy.EnabledBackSideTools];
        copy.SuggestedEngines = [.. copy.SuggestedEngines];
        copy.CustomEnginePaths = new(copy.CustomEnginePaths, copy.CustomEnginePaths.Comparer);
        copy.EnginePriorityOrder = [.. copy.EnginePriorityOrder];
        copy.WatchFolders = copy.WatchFolders.Where(rule => rule is not null).Select(rule => rule.Copy()).ToList();
        copy.Recipes = copy.Recipes.Where(recipe => recipe is not null).Select(recipe => recipe.Copy()).ToList();
        copy.RecentConversions = copy.RecentConversions.Where(entry => entry is not null).Select(entry => entry.Copy()).ToList();
        return copy;
    }

    private static Dictionary<string, List<string>> CopyPinnedFormats(Dictionary<string, List<string>> formats) =>
        formats.ToDictionary(pair => pair.Key, pair => pair.Value is null ? [] : new List<string>(pair.Value), formats.Comparer);

    private static Dictionary<string, T> WithCaseInsensitiveKeys<T>(Dictionary<string, T>? source)
    {
        if (source?.Comparer == StringComparer.OrdinalIgnoreCase) return source;
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
            foreach (var (key, value) in source)
                result[key] = value;
        return result;
    }

    private void Set<T>(ref T property, T value)
    {
        if (_isSnapshot) throw new InvalidOperationException("Lo snapshot delle impostazioni è immutabile.");
        property = value;
    }

    /// <summary>Aggiunge una conversione alla cronologia, mantenendo il limite di MaxRecentConversions.</summary>
    internal void AddRecentConversion(RecentConversion entry)
    {
        // I lettori dell'Hub possono continuare a enumerare la lista precedente.
        var history = RecentConversions.TakeLast(MaxRecentConversions - 1).ToList();
        history.Add(entry.Copy());
        RecentConversions = history;
    }

    /// <summary>Calcola il totale dei byte risparmiati dalle conversioni riuscite nella cronologia.</summary>
    internal long TotalBytesSaved() => RecentConversions.Where(c => c.Success).Sum(c => c.BytesSaved);
}

