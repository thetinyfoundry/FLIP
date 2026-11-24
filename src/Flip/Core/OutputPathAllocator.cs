using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace Flip.Core;

/// <summary>Owns staging, destination reservations and publication for the new conversion pipeline.</summary>
public static class OutputPathAllocator
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly ConcurrentDictionary<string, byte> Reserved = new(PathComparer);
    private static readonly ConcurrentDictionary<string, string> RecycledOriginals = new(PathComparer);
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "FLIP", "Jobs");

    internal static string GetDownloadsFallbackDirectory()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = Path.Combine(userProfile, "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    internal static string BuildOutputFileName(string sourceName, string sourceExt, string targetExt, string pageSuffix = "")
    {
        if (string.Equals(targetExt, "pdfa", StringComparison.OrdinalIgnoreCase))
        {
            bool sourceIsPdf = string.Equals(sourceExt.TrimStart('.'), "pdf", StringComparison.OrdinalIgnoreCase);
            return sourceIsPdf
                ? $"{sourceName}{pageSuffix}_pdfa.pdf"
                : $"{sourceName}{pageSuffix}.pdf";
        }
        return $"{sourceName}{pageSuffix}.{targetExt}";
    }

    internal static bool IsTransientStagingDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        string full = Path.GetFullPath(directory);
        if (full.Contains("INetCache", StringComparison.OrdinalIgnoreCase) ||
            full.Contains("Temporary Internet Files", StringComparison.OrdinalIgnoreCase))
            return true;

        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullWithSep = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullWithSep.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
    }

    internal readonly record struct StagedOutput(string Path, string FileName);
    internal readonly record struct PublishedOutput(string Path, long Bytes);
    internal sealed record CommitResult(IReadOnlyList<PublishedOutput> Outputs, string? Error = null, string? Warning = null);

    internal sealed class Batch
    {
        internal IReadOnlyList<string> Sources { get; }
        internal ConflictPolicy ConflictPolicy { get; }
        internal bool KeepOnlyIfSmaller { get; }
        internal bool ReplaceOriginal { get; }

        private readonly HashSet<string> sourcePaths;
        private readonly OutputDirectoryMode directoryMode;
        private readonly string? customDirectory;
        private readonly int batchThreshold;
        private readonly string batchFolder = $"Flip_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";

        internal Batch(IEnumerable<string> sources, FlipSettings settings)
        {
            Sources = Array.AsReadOnly(sources.Select(Path.GetFullPath).Distinct(PathComparer).ToArray());
            sourcePaths = new HashSet<string>(Sources, PathComparer);
            ConflictPolicy = settings.ConflictPolicy;
            KeepOnlyIfSmaller = settings.KeepOnlyIfSmaller;
            ReplaceOriginal = settings.ReplaceOriginalToRecycleBin;
            directoryMode = settings.OutputDirectoryMode;
            batchThreshold = settings.BatchSubfolderThreshold;
            if (directoryMode == OutputDirectoryMode.CustomFolder && !string.IsNullOrWhiteSpace(settings.CustomOutputDirectory))
                customDirectory = Path.GetFullPath(settings.CustomOutputDirectory);
        }

        internal Job CreateJob(params string[] inputs) => new(this, inputs);

        internal bool IsSource(string path) => sourcePaths.Contains(path);

        internal string GetDirectory(string source)
        {
            if (customDirectory != null) return customDirectory;
            string directory = Path.GetDirectoryName(source)!;
            if (IsTransientStagingDirectory(directory))
                return GetDownloadsFallbackDirectory();
            if (directoryMode == OutputDirectoryMode.SubfolderOnBatch && Sources.Count >= batchThreshold)
                return Path.Combine(directory, batchFolder);
            return directory;
        }
    }

    internal sealed class Job : IDisposable
    {
        private readonly Batch batch;
        private readonly FileInfo[] sources;
        private readonly string directory;
        private readonly List<FileStream> sourceGuards = [];
        private bool commitStarted;
        private bool disposed;

        internal long SourceBytes { get; }

        internal Job(Batch batch, string[] inputs)
        {
            this.batch = batch;
            sources = inputs.Select(Path.GetFullPath).Distinct(PathComparer).Select(path => new FileInfo(path)).ToArray();
            if (sources.Length == 0 || sources.Any(source => !batch.IsSource(source.FullName)))
                throw new ArgumentException("Il job deve riferirsi ai file del batch.", nameof(inputs));

            try
            {
                // An overlapping job must finish reading before Windows can move a source.
                foreach (FileInfo source in sources)
                    sourceGuards.Add(source.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

                SourceBytes = sources.Sum(source => source.Length);
                Directory.CreateDirectory(TempRoot);
                directory = Directory.CreateDirectory(Path.Combine(TempRoot, $"flip-job-{Guid.NewGuid():N}")).FullName;
            }
            catch
            {
                CloseGuards(sourceGuards);
                throw;
            }
        }

        internal string Stage(string fileName)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return Path.Combine(directory, SafeFileName(fileName));
        }

        internal CommitResult CommitOutputAndApplyRules(
            IReadOnlyList<StagedOutput> outputs,
            CancellationToken token,
            bool allowRecycle = true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (commitStarted) throw new InvalidOperationException("Il job è già stato finalizzato.");
            commitStarted = true;

            var leases = new List<DestinationLease>(outputs.Count);
            var pending = new List<(string TemporaryPath, long Bytes)>(outputs.Count);
            var published = new List<PublishedOutput>(outputs.Count);
            var protectedInputs = new List<FileStream>();
            try
            {
                token.ThrowIfCancellationRequested();
                if (outputs.Count == 0) return new(published, "Il job non ha prodotto file di output.");

                var artifacts = outputs.Select(output => new FileInfo(Path.GetFullPath(output.Path))).ToArray();
                if (artifacts.Any(file => !PathComparer.Equals(file.DirectoryName, directory) ||
                    !file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length == 0))
                    return new(published, "Uno o più file di output temporanei mancano, sono vuoti o non appartengono al job.");

                if (batch.KeepOnlyIfSmaller && artifacts.Any(file => file.Length >= SourceBytes))
                    return new(published, "Almeno un output occupa uno spazio pari o superiore a quello dei sorgenti.");

                string destination = batch.GetDirectory(sources[0].FullName);
                void StageToDestination(string targetDir)
                {
                    Directory.CreateDirectory(targetDir);
                    foreach (var output in outputs)
                        leases.Add(Reserve(batch, targetDir, SafeFileName(output.FileName)));

                    // Copy to the destination volume before publishing, so even cross-volume output uses a rename.
                    foreach (var artifact in artifacts)
                    {
                        token.ThrowIfCancellationRequested();
                        string temporary = Path.Combine(targetDir, $".flip-publish-{Guid.NewGuid():N}.tmp");
                        using var input = artifact.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        pending.Add((temporary, artifact.Length));
                        CopyForPublish(input, output, token);
                        output.Flush(flushToDisk: true);
                        if (output.Length != artifact.Length) throw new IOException("Copia temporanea incompleta.");
                    }
                }

                try
                {
                    StageToDestination(destination);
                }
                catch (UnauthorizedAccessException)
                {
                    foreach (var (temporary, _) in pending)
                    {
                        try { File.Delete(temporary); } catch { }
                    }
                    pending.Clear();
                    foreach (var lease in leases) lease.Dispose();
                    leases.Clear();
                    destination = GetDownloadsFallbackDirectory();
                    StageToDestination(destination);
                }

                if (batch.ConflictPolicy == ConflictPolicy.Overwrite)
                {
                    // Windows also blocks replacement through alternate paths to an open input.
                    foreach (string source in batch.Sources)
                    {
                        if (File.Exists(source))
                            protectedInputs.Add(File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    }
                }

                for (int i = 0; i < pending.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var (temporary, bytes) = pending[i];
                    string finalPath = leases[i].Path;
                    File.Move(temporary, finalPath, overwrite: batch.ConflictPolicy == ConflictPolicy.Overwrite);
                    published.Add(new(finalPath, bytes));
                }

                CloseGuards(protectedInputs);

                if (!batch.ReplaceOriginal || !allowRecycle) return new(published);
                try
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var output in published)
                    {
                        var guard = File.Open(output.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        protectedInputs.Add(guard);
                        if (guard.Length != output.Bytes)
                            throw new IOException("Un output pubblicato è cambiato dopo il salvataggio.");
                    }
                    CloseGuards(sourceGuards);
                    Recycle(sources, token, RecycledOriginals);
                    foreach (var output in published)
                        ReplacedSources[output.Path] = sources.Select(source => source.FullName).ToArray();
                    return new(published);
                }
                catch (Exception ex)
                {
                    return new(published, Warning: $"Output salvati. Sostituzione degli originali incompleta: {ex.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                return new(published, "Salvataggio annullato.");
            }
            catch (Exception ex)
            {
                return new(published, ex.Message);
            }
            finally
            {
                CloseGuards(protectedInputs);
                foreach (var (temporary, _) in pending)
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Evidence.Log("output.staging.cleanup.failed", ex.Message);
                    }
                }
                foreach (var lease in leases) lease.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CloseGuards(sourceGuards);
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Evidence.Log("output.job.cleanup.failed", ex.Message);
            }
        }
    }

    private static readonly ConcurrentDictionary<string, string[]> ReplacedSources = new(PathComparer);

    /// <summary>
    /// Rimette al loro posto gli originali spostati nel Cestino per produrre questi file.
    /// Va chiamato prima di cestinare gli output: annullare non deve lasciare l'utente senza nessuna copia.
    /// </summary>
    internal static (int Restored, string[] Missing) RestoreOriginals(IEnumerable<string> outputs)
    {
        int restored = 0;
        var missing = new List<string>();
        foreach (string source in outputs.SelectMany(output => ReplacedSources.TryRemove(Path.GetFullPath(output), out var paths) ? paths : [])
            .Distinct(PathComparer).ToArray())
        {
            try
            {
                if (File.Exists(source)) continue;
                if (!RecycledOriginals.TryRemove(source, out string? recycled) || !File.Exists(recycled)) { missing.Add(source); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.Move(recycled, source, overwrite: false);
                restored++;
                // $R contiene i dati, $I la voce mostrata da Esplora file: senza dati la voce resterebbe orfana.
                string name = Path.GetFileName(recycled);
                if (name.StartsWith("$R", StringComparison.OrdinalIgnoreCase))
                    File.Delete(Path.Combine(Path.GetDirectoryName(recycled)!, "$I" + name[2..]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Evidence.Log("undo.restore.error", new { source, ex.Message });
                if (!File.Exists(source)) missing.Add(source);
            }
        }
        return (restored, [.. missing]);
    }

    private static void CloseGuards(List<FileStream> guards)
    {
        foreach (FileStream guard in guards) guard.Dispose();
        guards.Clear();
    }

    private sealed class DestinationLease(string path) : IDisposable
    {
        internal string Path { get; } = path;
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Reserved.TryRemove(Path, out _);
        }
    }

    private static DestinationLease Reserve(Batch batch, string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (batch.ConflictPolicy != ConflictPolicy.AutoNumber)
        {
            if (batch.IsSource(path)) throw new IOException($"La destinazione coincide con un file sorgente: {path}");
            if (Directory.Exists(path) || (batch.ConflictPolicy == ConflictPolicy.Skip && File.Exists(path)))
                throw new IOException($"La destinazione esiste già: {path}");
            if (!Reserved.TryAdd(path, 0)) throw new IOException($"La destinazione è in uso da un altro job: {path}");
            return new(path);
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 0; i < 10000; i++)
        {
            path = Path.Combine(directory, i == 0 ? fileName : $"{stem}_{i}{extension}");
            if (!batch.IsSource(path) && !File.Exists(path) && !Directory.Exists(path) && Reserved.TryAdd(path, 0))
                return new(path);
        }
        throw new IOException($"Nessun nome di destinazione disponibile per {fileName}.");
    }

    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.TrimEnd(' ', '.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Nome file di output non valido.", nameof(name));
        return name;
    }

    private static void CopyForPublish(Stream input, Stream output, CancellationToken token)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int bytes;
            while ((bytes = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, bytes);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void VerifyOriginal(FileInfo original)
    {
        var current = new FileInfo(original.FullName);
        if (current.Length != original.Length || current.LastWriteTimeUtc != original.LastWriteTimeUtc ||
            current.CreationTimeUtc != original.CreationTimeUtc)
            throw new IOException($"Il file sorgente è cambiato: {original.Name}");
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0 ||
            new DriveInfo(Path.GetPathRoot(original.FullName)!).DriveType != DriveType.Fixed)
            throw new IOException($"Il Cestino non è disponibile per questo percorso: {original.FullName}");
    }

    /// <summary>Sposta in modo sicuro nel Cestino di Windows una collezione di file di output generati.</summary>
    public static int MoveToRecycleBin(IEnumerable<string> filePaths, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        FileInfo[] existing = filePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .Select(p => new FileInfo(p))
            .ToArray();

        if (existing.Length == 0) return 0;
        Recycle(existing, token);
        return existing.Length;
    }

    private static void Recycle(FileInfo[] sources, CancellationToken token, ConcurrentDictionary<string, string>? recycledPaths = null)
    {
        if (!Environment.UserInteractive)
            throw new IOException("Il Cestino richiede una sessione Windows interattiva.");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RecycleOnSta(sources, token, recycledPaths); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new IOException(failure.Message, failure);
    }

    private static void RecycleOnSta(FileInfo[] sources, CancellationToken token, ConcurrentDictionary<string, string>? recycledPaths)
    {
        Guid recycleFolderId = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
        Guid shellItemId = typeof(IShellItem).GUID;
        Guid transferHandlerId = new("D5E346A1-F753-4932-B403-4574800E2498");
        Guid transferSourceId = typeof(ITransferSource).GUID;
        Marshal.ThrowExceptionForHR(SHGetKnownFolderItem(in recycleFolderId, 0, 0, in shellItemId, out IShellItem bin));
        try
        {
            foreach (FileInfo source in sources) VerifyOriginal(source);
            foreach (FileInfo source in sources)
            {
                token.ThrowIfCancellationRequested();
                VerifyOriginal(source);
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source.FullName, 0, in shellItemId, out IShellItem item));
                try
                {
                    item.GetParent(out IShellItem parent);
                    try
                    {
                        parent.BindToHandler(0, in transferHandlerId, in transferSourceId, out ITransferSource transfer);
                        try
                        {
                            // RecycleItem has no fallback to the separate RemoveItem operation.
                            transfer.RecycleItem(item, bin, 0, out IShellItem? recycled);
                            try
                            {
                                if (recycled is null || File.Exists(source.FullName))
                                    throw new IOException($"Windows non ha confermato lo spostamento nel Cestino: {source.Name}");
                                if (recycledPaths != null && recycled.GetDisplayName(0x80058000, out nint name) >= 0)
                                {
                                    recycledPaths[source.FullName] = Marshal.PtrToStringUni(name) ?? "";
                                    Marshal.FreeCoTaskMem(name);
                                }
                            }
                            finally
                            {
                                if (recycled is not null) Marshal.ReleaseComObject(recycled);
                            }
                        }
                        finally { Marshal.ReleaseComObject(transfer); }
                    }
                    finally { Marshal.ReleaseComObject(parent); }
                }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        finally { Marshal.ReleaseComObject(bin); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId, out IShellItem item);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderItem(in Guid folderId, uint flags, nint token, in Guid interfaceId, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handler, in Guid interfaceId, out ITransferSource transfer);
        void GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out nint name);
    }

    // The preceding slots are declared in Windows SDK order.
    [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferSource
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetProperties(nint properties);
        void OpenItem(IShellItem item, uint flags, in Guid interfaceId, out nint resources);
        void MoveItem(IShellItem item, IShellItem parent, [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags, out IShellItem moved);
        void RecycleItem(IShellItem item, IShellItem recycleBin, uint flags, out IShellItem? recycled);
    }
}

