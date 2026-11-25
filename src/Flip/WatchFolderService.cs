using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flip.Core;
using Flip.Engines;
using Flip.Routing;

namespace Flip;

/// <summary>
/// Servizio background per il monitoraggio delle cartelle (Watch Folders):
/// osserva le cartelle configurate, attende il completamento della scrittura del file,
/// previene i loop di auto-conversione ed esegue automaticamente la conversione con RoutePlanner.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    private readonly object _sync = new();
    private readonly SettingsStore? _store;
    private readonly FlipSettings? _initialSettings;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _generatedOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlightFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrencySemaphore = new(Math.Max(2, Environment.ProcessorCount / 2));
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private string? _activeRules;

    /// <summary>True mentre una conversione automatica è in corso: l'aggiornamento dell'app deve aspettarla.</summary>
    public bool IsConverting => Volatile.Read(ref _converting) > 0;
    private int _converting;

    /// <summary>Notificato al completamento dell'elaborazione di un file (utile per log e test).</summary>
    public event Action<string, string, bool>? FileProcessed;

    public WatchFolderService(SettingsStore? store = null, FlipSettings? initialSettings = null)
    {
        EngineBootstrap.EnsureInitialized();
        _initialSettings = initialSettings;
        _store = store ?? (initialSettings == null ? SettingsStore.Current : null);
        if (_store != null)
        {
            _store.SettingsChanged += OnSettingsChanged;
        }
        var settings = initialSettings ?? _store?.Settings ?? new FlipSettings();
        ReloadRules(settings);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_store != null)
        {
            ReloadRules(_store.Settings);
        }
    }

    /// <summary>
    /// Ricarica e ricrea i FileSystemWatcher per tutte le regole attive in snapshot.
    /// </summary>
    public void ReloadRules(FlipSettings snapshot)
    {
        if (_disposed) return;
        lock (_sync)
        {
            var active = snapshot.CreateSnapshot().WatchFolders.Where(rule => rule.Enabled &&
                !string.IsNullOrWhiteSpace(rule.FolderPath) &&
                !string.IsNullOrWhiteSpace(rule.TargetExtension) &&
                Directory.Exists(rule.FolderPath)).ToList();

            // Ogni conversione salva la cronologia: ricreare gli osservatori a ogni salvataggio farebbe perdere file.
            string signature = System.Text.Json.JsonSerializer.Serialize(active);
            if (signature == _activeRules) return;
            _activeRules = signature;
            DisposeWatchers();

            // Un solo osservatore per cartella: più regole sullo stesso file devono essere eseguite tutte.
            foreach (var folder in active.GroupBy(rule => Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.FolderPath)), StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder.Key)
                    {
                        IncludeSubdirectories = false,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                    };

                    // Le regole che spostano l'originale nel Cestino vanno per ultime.
                    var rules = folder.Select(rule => rule.Copy()).OrderBy(rule => rule.DeleteSourceToRecycleBin).ToArray();
                    watcher.Created += (_, e) => OnFileEvent(rules, e.FullPath);
                    watcher.Changed += (_, e) => OnFileEvent(rules, e.FullPath);
                    watcher.Renamed += (_, e) => OnFileEvent(rules, e.FullPath);
                    watcher.Error += (_, e) =>
                    {
                        Evidence.Log("watchfolder.watcher.error", new { folder = folder.Key, e.GetException().Message });
                        lock (_sync) _activeRules = null;
                    };
                    watcher.EnableRaisingEvents = true;

                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Evidence.Log("watchfolder.init.error", new { folder = folder.Key, ex.Message });
                }
            }
        }
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) { Evidence.Log("watchfolder.dispose.error", ex.Message); }
        }
        _watchers.Clear();
    }

    private void OnFileEvent(WatchFolderRule[] folderRules, string fullPath)
    {
        if (_disposed) return;
        try
        {
            string normalized = Path.GetFullPath(fullPath);

            // Un collegamento porterebbe a convertire un file fuori dalla cartella scelta dall'utente.
            if (new FileInfo(normalized).LinkTarget != null) return;

            // Anti-loop Check A: file generato da FLIP
            if (IsGeneratedOutput(normalized)) return;

            // Verifica estensione e filtri
            var rules = folderRules.Where(rule => MatchesRule(rule, normalized)).ToArray();
            if (rules.Length == 0) return;

            // Anti-loop Check B: deduplica eventi concorrenti sullo stesso file
            if (!_inFlightFiles.TryAdd(normalized, 0)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < rules.Length; i++)
                    {
                        var rule = rules[i];
                        // Solo l'ultima regola può togliere l'originale, che serve a tutte le altre.
                        if (i < rules.Length - 1 && rule.DeleteSourceToRecycleBin)
                        {
                            rule = rule.Copy();
                            rule.DeleteSourceToRecycleBin = false;
                        }
                        await ProcessFileAsync(rule, normalized, keepSource: i < rules.Length - 1).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                finally
                {
                    await Task.Delay(400).ConfigureAwait(false);
                    _inFlightFiles.TryRemove(normalized, out _);
                }
            });
        }
        catch (Exception ex)
        {
            Evidence.Log("watchfolder.event.error", ex.Message);
        }
    }

    private async Task ProcessFileAsync(WatchFolderRule rule, string filePath, bool keepSource)
    {
        if (_disposed) return;

        // 1. Attendi che il file sia completamente scritto e rilasciato da altri processi
        bool ready = await WaitForFileReadyAsync(filePath, _cts.Token).ConfigureAwait(false);
        if (!ready || _disposed) return;

        // Ricontrolla anti-loop dopo l'attesa di scrittura
        if (IsGeneratedOutput(filePath)) return;

        await _concurrencySemaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _converting);
        try
        {
            var snapshot = _initialSettings?.CreateSnapshot() ?? _store?.CreateSnapshot() ?? new FlipSettings().CreateSnapshot();
            if (rule.DeleteSourceToRecycleBin || keepSource)
            {
                var mutable = snapshot.CreateMutableCopy();
                mutable.ReplaceOriginalToRecycleBin = !keepSource;
                snapshot = mutable.CreateSnapshot();
            }

            string sourceExt = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            string targetExt = rule.TargetExtension.TrimStart('.').ToLowerInvariant();

            if (sourceExt == targetExt) return;
            if (!RoutePlanner.TryPlanRoute(sourceExt, targetExt, snapshot, out _)) return;

            var batch = await RoutePlanner.ExecuteBatchAsync(
                [filePath],
                [targetExt],
                snapshot,
                ct: _cts.Token).ConfigureAwait(false);

            bool anySuccess = false;
            foreach (var item in batch)
            {
                if (item.Result.Success)
                {
                    anySuccess = true;
                    if (item.Result.OutputFilePaths != null)
                    {
                        foreach (var p in item.Result.OutputFilePaths)
                            RegisterGeneratedOutput(p);
                    }
                    if (!string.IsNullOrEmpty(item.Result.OutputFilePath))
                    {
                        RegisterGeneratedOutput(item.Result.OutputFilePath);
                    }
                }
                else
                {
                    Evidence.Log("watchfolder.batch.item.error", new { item.SourcePath, item.TargetExtension, item.Result.ErrorMessage });
                }
            }

            FileProcessed?.Invoke(filePath, targetExt, anySuccess);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Evidence.Log("watchfolder.process.error", new { filePath, target = rule.TargetExtension, ex.Message });
            FileProcessed?.Invoke(filePath, rule.TargetExtension, false);
        }
        finally
        {
            Interlocked.Decrement(ref _converting);
            try { _concurrencySemaphore.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Attende in modo non bloccante che il file sia completamente scritto su disco
    /// (dimensione stabile e lock rilasciato da altri processi come browser o explorer).
    /// </summary>
    public static async Task<bool> WaitForFileReadyAsync(string filePath, CancellationToken ct = default)
    {
        // Una copia o un download lungo continua finché il file cresce o resta occupato.
        var idleLimit = TimeSpan.FromSeconds(15);
        var lockedLimit = DateTime.UtcNow.AddMinutes(30);
        var deadline = DateTime.UtcNow + idleLimit;
        long lastSize = -1;
        long seenSize = -1;

        while (DateTime.UtcNow < deadline && DateTime.UtcNow < lockedLimit && !ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var fi = new FileInfo(filePath);
                    long currentSize = fi.Length;
                    if (currentSize != seenSize)
                    {
                        seenSize = currentSize;
                        deadline = DateTime.UtcNow + idleLimit;
                    }

                    if (currentSize > 0)
                    {
                        // La sola lettura basta a scoprire chi sta scrivendo e funziona con i file di sola lettura.
                        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (currentSize == lastSize)
                        {
                            return true; // Dimensione stabile e nessun lock attivo
                        }
                        lastSize = currentSize;
                    }
                }
            }
            catch (IOException ex) when (ex is not FileNotFoundException)
            {
                // File ancora bloccato in scrittura da un altro processo
                deadline = DateTime.UtcNow + idleLimit;
            }
            catch (IOException)
            {
                // File spostato o rinominato fra il controllo e l'apertura
            }
            catch (UnauthorizedAccessException)
            {
                // Permessi temporaneamente occupati
            }

            try
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifica se un file corrisponde ai filtri di una regola escludendo file temporanei,
    /// file di lock e file che hanno già l'estensione target.
    /// </summary>
    public static bool MatchesRule(WatchFolderRule rule, string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        // Esclusione file temporanei, parziali o di lock
        if (fileName.StartsWith("~$", StringComparison.Ordinal) ||
            fileName.StartsWith('.') ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".download", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        string target = rule.TargetExtension.TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || ext == target) return false;

        if (rule.IncludeExtensions == null || rule.IncludeExtensions.Count == 0)
            return true;

        foreach (var raw in rule.IncludeExtensions)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string item = raw.Trim().ToLowerInvariant();
            if (item is "*" or "*.*") return true;

            var parts = item.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                string clean = part.TrimStart('*', '.').Trim();
                if (clean == "*" || string.Equals(clean, ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifica se un percorso è stato generato recentemente da FLIP (negli ultimi 10 minuti).
    /// </summary>
    public bool IsGeneratedOutput(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        string normalized = Path.GetFullPath(fullPath);
        if (_generatedOutputs.TryGetValue(normalized, out var timestamp))
        {
            if ((DateTime.UtcNow - timestamp).TotalMinutes < 10)
            {
                return true;
            }
            _generatedOutputs.TryRemove(normalized, out _);
        }
        return false;
    }

    /// <summary>
    /// Registra un file di output nella blacklist anti-loop.
    /// </summary>
    public void RegisterGeneratedOutput(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        try
        {
            string normalized = Path.GetFullPath(fullPath);
            _generatedOutputs[normalized] = DateTime.UtcNow;

            if (_generatedOutputs.Count > 500)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-10);
                foreach (var kvp in _generatedOutputs)
                {
                    if (kvp.Value < cutoff) _generatedOutputs.TryRemove(kvp.Key, out _);
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        if (_store != null)
        {
            _store.SettingsChanged -= OnSettingsChanged;
        }

        lock (_sync)
        {
            DisposeWatchers();
        }

        _concurrencySemaphore.Dispose();
        _cts.Dispose();
    }
}

