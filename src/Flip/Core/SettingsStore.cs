using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Flip.Core;

/// <summary>Gestisce le impostazioni di FLIP e le salva atomicamente in %AppData%\FLIP\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly string GitHubDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLIP");

    public static string DataDirectory { get; } = StoreEdition.LocalState ?? GitHubDataDirectory;

    public static string SettingsFilePath { get; } = Path.Combine(DataDirectory, "settings.json");

    private static readonly Lazy<SettingsStore> _lazy =
        new(() =>
        {
            if (StoreEdition.IsPackaged)
                StoreEdition.ImportGitHubSettings(SettingsFilePath, Path.Combine(GitHubDataDirectory, "settings.json"));
            return new SettingsStore(SettingsFilePath, AppPreferences.LegacySettingsFilePath);
        });

    private static SettingsStore? testStore;
    public static SettingsStore Current => testStore ?? _lazy.Value;

    internal static void UseTestDirectory(string directory)
    {
        testStore = new SettingsStore(Path.Combine(directory, "settings.json"), Path.Combine(directory, "legacy.json"));
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Lock _lock = new();
    private readonly string _settingsFilePath;
    private readonly string _legacySettingsFilePath;
    private FlipSettings _settings;
    private bool _preserveUnreadable;

    internal SettingsStore(string settingsFilePath, string legacySettingsFilePath)
    {
        _settingsFilePath = Path.GetFullPath(settingsFilePath);
        _legacySettingsFilePath = Path.GetFullPath(legacySettingsFilePath);
        _settings = LoadFromDisk();
        _settings.StoreLock = _lock;
    }

    /// <summary>Modello modificabile per l'Hub esistente. Per i job usare <see cref="CreateSnapshot"/>.</summary>
    public FlipSettings Settings
    {
        get { lock (_lock) { return _settings; } }
    }

    /// <summary>Acquisisce uno snapshot coerente e immutabile sotto lo stesso lock degli aggiornamenti.</summary>
    public FlipSettings CreateSnapshot()
    {
        lock (_lock)
        {
            return _settings.CreateSnapshot();
        }
    }

    /// <summary>Notifica il cambiamento dopo il salvataggio o il caricamento.</summary>
    public event EventHandler? SettingsChanged;

    public void Save()
    {
        lock (_lock)
        {
            SaveToDisk(_settings);
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reload()
    {
        lock (_lock)
        {
            _settings = LoadFromDisk();
            _settings.StoreLock = _lock;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ExportToFile(string path)
    {
        FlipSettings exportCopy;
        lock (_lock)
        {
            exportCopy = _settings.CreateMutableCopy();
        }
        // Privacy pre-rilascio: escludi la cronologia file dell'utente e i percorsi personali dei motori.
        exportCopy.RecentConversions.Clear();
        exportCopy.CustomEnginePaths.Clear();

        string json = JsonSerializer.Serialize(exportCopy, _jsonOptions);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Importa le preferenze da un file. Ciò che vale solo per questo PC resta com'è: un file ricevuto da altri
    /// non deve poter scegliere i programmi da avviare, né attivare cartelle monitorate o salvare in rete.
    /// Restituisce quante cartelle monitorate sono state importate spente.
    /// </summary>
    public int ImportFromFile(string path)
    {
        var (imported, fields) = ReadSettings(path);
        int disabled = 0;
        lock (_lock)
        {
            imported.CustomEnginePaths = new(_settings.CustomEnginePaths, StringComparer.OrdinalIgnoreCase);
            imported.RecentConversions = [.. _settings.RecentConversions];
            imported.WatchFolders = imported.WatchFolders.Where(rule => rule is not null && !Policy.IsNetworkPath(rule.FolderPath)).ToList();
            foreach (var rule in imported.WatchFolders.Where(rule => rule.Enabled))
            {
                if (_settings.WatchFolders.Any(known => known.Enabled && SameRule(known, rule))) continue;
                rule.Enabled = false;
                disabled++;
            }
            if (Policy.IsNetworkPath(imported.CustomOutputDirectory))
            {
                imported.CustomOutputDirectory = _settings.CustomOutputDirectory;
                imported.OutputDirectoryMode = _settings.OutputDirectoryMode;
            }
            // Le esportazioni precedenti alla migrazione non contengono queste preferenze.
            if (!fields.Contains(nameof(FlipSettings.SoundEnabled))) imported.SoundEnabled = _settings.SoundEnabled;
            if (!fields.Contains(nameof(FlipSettings.GestureEnabled))) imported.GestureEnabled = _settings.GestureEnabled;
            if (!fields.Contains(nameof(FlipSettings.Language))) imported.Language = _settings.Language;
            if (!fields.Contains(nameof(FlipSettings.TriggerKey))) imported.TriggerKey = _settings.TriggerKey;
            imported.LegacyPreferencesMigrated = true;
            AddNewBackSideTools(imported, fields);
            SaveToDisk(imported);
            imported.StoreLock = _lock;
            _settings = imported;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        return disabled;
    }

    private static bool SameRule(WatchFolderRule a, WatchFolderRule b) =>
        string.Equals(a.FolderPath, b.FolderPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.TargetExtension, b.TargetExtension, StringComparison.OrdinalIgnoreCase) &&
        a.DeleteSourceToRecycleBin == b.DeleteSourceToRecycleBin &&
        a.IncludeExtensions.SequenceEqual(b.IncludeExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Esegue e salva una modifica al modello dell'Hub sotto lock.</summary>
    public void Update(Action<FlipSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_lock)
        {
            mutate(_settings);
            SaveToDisk(_settings);
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Aggiunge un risultato e pubblica la cronologia aggiornata dopo il salvataggio.</summary>
    public void RecordConversion(RecentConversion entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var next = _settings.CreateMutableCopy();
            next.AddRecentConversion(entry);
            SaveToDisk(next);
            _settings.RecentConversions = next.RecentConversions;
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal (bool SoundEnabled, bool GestureEnabled, string TriggerKey, string Language) ReadPreferences()
    {
        lock (_lock)
        {
            return (_settings.SoundEnabled, _settings.GestureEnabled, _settings.TriggerKey, _settings.Language);
        }
    }

    internal void UpdatePreferences(bool? soundEnabled, bool? gestureEnabled, string? trigger, string? language)
    {
        void Apply(FlipSettings settings)
        {
            if (soundEnabled is bool sound) settings.SoundEnabled = sound;
            if (gestureEnabled is bool gesture) settings.GestureEnabled = gesture;
            if (trigger is not null) settings.TriggerKey = trigger;
            if (language is not null) settings.Language = language;
            settings.LegacyPreferencesMigrated = true;
        }

        lock (_lock)
        {
            var next = _settings.CreateMutableCopy();
            Apply(next);
            SaveToDisk(next);
            Apply(_settings);
        }
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private FlipSettings LoadFromDisk()
    {
        FlipSettings settings;
        HashSet<string> fields;
        try
        {
            (settings, fields) = ReadWithRetry(_settingsFilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            settings = new FlipSettings();
            fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Evidence.Log("settings.load.error", ex.Message);
            _preserveUnreadable = true;
            return new FlipSettings();
        }
        catch (JsonException ex)
        {
            // Un valore sconosciuto (per esempio di una versione più recente) non deve azzerare tutto il resto.
            Evidence.Log("settings.load.error", ex.Message);
            _preserveUnreadable = true;
            if (Salvage(_settingsFilePath) is not { } salvaged) return new FlipSettings();
            (settings, fields) = salvaged;
        }

        bool normalizedOutput = NormalizeOutputDirectoryDefault(settings, fields);
        if (AddNewBackSideTools(settings, fields) || normalizedOutput)
        {
            try { SaveToDisk(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Evidence.Log("settings.migration.error", ex.Message);
            }
        }

        if (settings.LegacyPreferencesMigrated) return settings;

        try
        {
            if (AppPreferences.ReadLegacy(_legacySettingsFilePath) is { } legacy)
            {
                if (legacy.SoundEnabled is bool sound && !fields.Contains(nameof(FlipSettings.SoundEnabled)))
                    settings.SoundEnabled = sound;
                if (legacy.GestureEnabled is bool gesture && !fields.Contains(nameof(FlipSettings.GestureEnabled)))
                    settings.GestureEnabled = gesture;
                if (!string.IsNullOrWhiteSpace(legacy.Language) && !fields.Contains(nameof(FlipSettings.Language)))
                    settings.Language = legacy.Language;
                // Il vecchio Hub serializzava sempre "Down", anche senza una scelta dell'utente.
                if (!string.IsNullOrWhiteSpace(legacy.Trigger) &&
                    (string.IsNullOrWhiteSpace(settings.TriggerKey) ||
                     string.Equals(settings.TriggerKey, FlipSettings.DefaultTriggerKey, StringComparison.OrdinalIgnoreCase)))
                    settings.TriggerKey = legacy.Trigger;
            }

            settings.LegacyPreferencesMigrated = true;
            SaveToDisk(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Evidence.Log("settings.migration.error", ex.Message);
        }

        return settings;
    }

    /// <summary>
    /// Le impostazioni salvate prima della v0.6.0 non conoscono "Turntable 360°":
    /// lo aggiunge una sola volta, riconoscendo i file privi di BackSideToolsRevision.
    /// </summary>
    internal static bool AddNewBackSideTools(FlipSettings settings, HashSet<string> fields)
    {
        if (!fields.Contains(nameof(FlipSettings.EnabledBackSideTools)) ||
            fields.Contains(nameof(FlipSettings.BackSideToolsRevision)))
            return false;

        settings.BackSideToolsRevision = FlipSettings.CurrentBackSideToolsRevision;
        if (!settings.EnabledBackSideTools.Contains("turntable_3d", StringComparer.OrdinalIgnoreCase))
            settings.EnabledBackSideTools.Add("turntable_3d");
        return true;
    }

    /// <summary>
    /// Garantisce che la destinazione predefinita sia "Accanto al file originale" (SameAsSource)
    /// se CustomOutputDirectory è vuota o era rimasta agganciata a Downloads senza scelta esplicita dell'utente.
    /// </summary>
    internal static bool NormalizeOutputDirectoryDefault(FlipSettings settings, HashSet<string> fields)
    {
        if (settings.OutputDirectoryMode != OutputDirectoryMode.CustomFolder)
            return false;

        if (string.IsNullOrWhiteSpace(settings.CustomOutputDirectory))
        {
            settings.OutputDirectoryMode = OutputDirectoryMode.SameAsSource;
            settings.CustomOutputDirectory = "";
            return true;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = Path.Combine(userProfile, "Downloads");
        bool isDownloads = string.Equals(
            Path.GetFullPath(settings.CustomOutputDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(downloads).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        if (isDownloads && !settings.UserSelectedCustomOutputDirectory)
        {
            settings.OutputDirectoryMode = OutputDirectoryMode.SameAsSource;
            settings.CustomOutputDirectory = "";
            return true;
        }

        return false;
    }

    /// <summary>Un antivirus o un backup può tenere occupato il file per qualche istante all'avvio.</summary>
    private static (FlipSettings Settings, HashSet<string> Fields) ReadWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return ReadSettings(path); }
            catch (IOException ex) when (attempt < 5 && ex is not (FileNotFoundException or DirectoryNotFoundException))
            {
                Thread.Sleep(120);
            }
        }
    }

    /// <summary>Recupera le singole proprietà valide di un file che nel complesso non si legge.</summary>
    private static (FlipSettings Settings, HashSet<string> Fields)? Salvage(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not JsonObject source)
                return null;
            var valid = new JsonObject();
            foreach (var (name, value) in source)
            {
                try
                {
                    _ = new JsonObject { [name] = value?.DeepClone() }.Deserialize<FlipSettings>(_jsonOptions);
                    valid[name] = value?.DeepClone();
                }
                catch (JsonException ex) { Evidence.Log("settings.load.property", new { name, ex.Message }); }
            }
            return (valid.Deserialize<FlipSettings>(_jsonOptions)!,
                valid.Select(property => property.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Evidence.Log("settings.salvage.error", ex.Message);
            return null;
        }
    }

    /// <summary>Prima di riscrivere un file non leggibile ne conserva una copia; senza copia non si scrive.</summary>
    private void PreserveUnreadableFile()
    {
        if (!_preserveUnreadable) return;
        if (File.Exists(_settingsFilePath))
            File.Copy(_settingsFilePath, $"{_settingsFilePath}.backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}", overwrite: false);
        _preserveUnreadable = false;
    }

    private static (FlipSettings Settings, HashSet<string> Fields) ReadSettings(string path)
    {
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Il file delle impostazioni deve contenere un oggetto JSON.");
        var settings = json.RootElement.Deserialize<FlipSettings>(_jsonOptions)!;
        var fields = json.RootElement.EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (settings, fields);
    }

    private void SaveToDisk(FlipSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
        PreserveUnreadableFile();
        string temp = _settingsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, _jsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _settingsFilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

