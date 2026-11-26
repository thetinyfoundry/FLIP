using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using Flip.Core;
using Flip.Routing;
using SkiaSharp;

namespace Flip.Engines;

/// <summary>Ricomprime archivi in ZIP, TAR o TGZ. Usa bsdtar per leggere 7Z e RAR.</summary>
public sealed class ArchiveEngine : IConversionEngine
{
    public string Id => "archive_builtin";
    public string DisplayName => "Archive & Compression Engine";
    public bool IsExternal => false;

    private static readonly string[] NativeInputs = ["zip", "tar", "gz", "tgz", "cbz", "epub"];
    private static readonly string[] ExtendedInputs = ["7z", "rar"];
    private static readonly string[] Outputs = ["zip", "tar", "gz", "tgz"];
    private static readonly string SystemTar = Path.Combine(Environment.SystemDirectory, "tar.exe");
    private readonly List<ConversionEdge> supportedEdges = [];

    public IReadOnlyList<ConversionEdge> SupportedEdges => supportedEdges;

    public ArchiveEngine()
    {
        string[] allOutputs = File.Exists(SystemTar) ? [.. Outputs, "7z"] : Outputs;
        foreach (string source in NativeInputs)
            foreach (string target in allOutputs)
                if (source != target)
                    supportedEdges.Add(new(source, target, Id, 10));

        if (File.Exists(SystemTar))
            foreach (string source in ExtendedInputs)
                foreach (string target in allOutputs)
                    if (source != target)
                        supportedEdges.Add(new(source, target, Id, 20));
    }

    public bool IsAvailable(FlipSettings settings) => true;

    public async Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? workDirectory = null;

        try
        {
            var ct = context.CancellationToken;
            ct.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(context.InputFilePath);
            string destination = Path.GetFullPath(context.OutputFilePath);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Il percorso di output coincide con l'archivio sorgente.");
            if (!File.Exists(source))
                throw new FileNotFoundException("Archivio sorgente non trovato.", source);

            string sourceExt = Extension(context.SourceExtension, source);
            string targetExt = Extension(context.TargetExtension, destination);
            if (!(Outputs.Contains(targetExt) || targetExt is "gz" or "cbz" or "7z"))
                throw new NotSupportedException($"Formato di destinazione non supportato: {targetExt}.");
            if (sourceExt == "epub" && targetExt == "cbz")
                throw new NotSupportedException("EPUB → CBZ richiede un motore che impagini il libro.");

            string parent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(parent);
            workDirectory = Directory.CreateDirectory(Path.Combine(parent, $"flip-archive-{Guid.NewGuid():N}")).FullName;
            string contents = Directory.CreateDirectory(Path.Combine(workDirectory, "contents")).FullName;
            string staged = Path.Combine(workDirectory, "output");

            context.Progress?.Report(0.15);
            await ExtractAsync(source, sourceExt, contents, ct).ConfigureAwait(false);
            context.Progress?.Report(0.55);
            await CompressAsync(contents, staged, targetExt, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            long bytes = new FileInfo(staged).Length;
            File.Move(staged, destination, overwrite: true);
            context.Progress?.Report(1);
            return new(true, destination, null, bytes);
        }
        catch (OperationCanceledException)
        {
            return new(false, "", "Operazione archivio annullata.");
        }
        catch (Exception ex)
        {
            return new(false, "", $"Errore conversione archivio: {ex.Message}");
        }
        finally
        {
            if (workDirectory != null)
            {
                try { Directory.Delete(workDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string Extension(string declared, string path) =>
        (string.IsNullOrEmpty(declared) ? Path.GetExtension(path) : declared).TrimStart('.').ToLowerInvariant();

    private static async Task ExtractAsync(string path, string extension, string root, CancellationToken ct)
    {
        if (extension is "7z" or "rar" ||
            (extension == "bz2" && path.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)) ||
            (extension == "xz" && path.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)))
        {
            await ExtractWithSystemTarAsync(path, root, ct).ConfigureAwait(false);
            return;
        }

        await using var source = File.OpenRead(path);
        switch (extension)
        {
            case "zip" or "cbz" or "epub":
                using (var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
                {
                    foreach (var entry in zip.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                        if (unixType is not (0 or 0x4000 or 0x8000) ||
                            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                            throw new IOException($"Tipo di voce ZIP non supportato: {entry.FullName}.");
                        if (directory)
                        {
                            if (entry.Length != 0)
                                throw new IOException($"Voce ZIP di directory con dati: {entry.FullName}.");
                            Directory.CreateDirectory(EntryPath(root, entry.FullName, directory: true));
                            continue;
                        }

                        using var data = entry.Open();
                        await WriteEntryAsync(root, entry.FullName, data, ct).ConfigureAwait(false);
                    }
                }
                break;
            case "tar":
                await ExtractTarAsync(source, root, ct).ConfigureAwait(false);
                break;
            case "gz" or "tgz" or "tar.gz":
                using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                {
                    if (extension is "tgz" or "tar.gz" || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                        await ExtractTarAsync(gzip, root, ct).ConfigureAwait(false);
                    else
                        await WriteEntryAsync(root, Path.GetFileNameWithoutExtension(path), gzip, ct).ConfigureAwait(false);
                }
                break;
            default:
                throw new NotSupportedException($"Formato sorgente non supportato: {extension}.");
        }
    }

    private static async Task ExtractTarAsync(Stream source, string root, CancellationToken ct)
    {
        using var tar = new TarReader(source, leaveOpen: true);
        while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(EntryPath(root, entry.Name, directory: true));
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    await WriteEntryAsync(root, entry.Name, entry.DataStream ?? Stream.Null, ct).ConfigureAwait(false);
                    break;
                case TarEntryType.GlobalExtendedAttributes:
                    // Commento generale dell'archivio (lo aggiunge GitHub): non è un file.
                    break;
                default:
                    throw new IOException($"Tipo di voce TAR non supportato: {entry.EntryType} ({entry.Name}).");
            }
        }
    }

    private static async Task WriteEntryAsync(string root, string name, Stream data, CancellationToken ct)
    {
        string path = EntryPath(root, name, directory: false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await data.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    private static string EntryPath(string root, string name, bool directory)
    {
        string relative = name.Replace('\\', '/');
        if (relative.StartsWith('/') || Path.IsPathRooted(relative))
            throw new IOException($"Percorso assoluto nell'archivio: {name}.");

        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part == ".") continue;
            if (part == ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsWindowsDeviceName(part))
                throw new IOException($"Percorso non valido nell'archivio: {name}.");
        }

        string path = Path.GetFullPath(relative, root);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !(directory && string.Equals(Path.TrimEndingDirectorySeparator(path), root, StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"Percorso fuori dalla directory di estrazione: {name}.");
        if (!directory && (parts.Length == 0 || parts[^1] == "." || relative.EndsWith('/')))
            throw new IOException($"Nome file non valido nell'archivio: {name}.");
        return path;
    }

    private static bool IsWindowsDeviceName(string part)
    {
        string stem = part.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                  stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[3]));
    }

    private static async Task ExtractWithSystemTarAsync(string path, string root, CancellationToken ct)
    {
        if (!File.Exists(SystemTar))
            throw new NotSupportedException("tar.exe di Windows non è disponibile.");

        var start = new ProcessStartInfo(SystemTar)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // bsdtar emits a TAR stream. Our reader validates each path before writing.
        foreach (string arg in new[] { "-cf", "-", "--format", "pax", "@" + path })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new IOException("Impossibile avviare tar.exe.");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            Stream data = process.StandardOutput.BaseStream;
            await ExtractTarAsync(data, root, ct).ConfigureAwait(false);
            await data.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error))
                throw new IOException($"Lettura archivio incompleta: {error.Trim()}");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await stderr.ConfigureAwait(false);
        }
    }

    private static async Task CompressAsync(string root, string output, string extension, CancellationToken ct)
    {
        switch (extension)
        {
            case "cbz":
                ValidateComicPages(root, ct);
                goto case "zip";
            case "zip":
                await ZipFile.CreateFromDirectoryAsync(root, output, CompressionLevel.Optimal,
                    includeBaseDirectory: false, cancellationToken: ct).ConfigureAwait(false);
                break;
            case "tar":
                await TarFile.CreateFromDirectoryAsync(root, output, includeBaseDirectory: false, cancellationToken: ct)
                    .ConfigureAwait(false);
                break;
            case "tgz" or "gz":
                string? singleFile = null;
                if (extension == "gz")
                {
                    var entries = Directory.GetFileSystemEntries(root);
                    if (entries.Length == 1 && File.Exists(entries[0]))
                        singleFile = entries[0];
                }
                await using (var destination = File.Create(output))
                await using (var gzip = new GZipStream(destination, CompressionLevel.Optimal))
                {
                    if (singleFile == null)
                        await TarFile.CreateFromDirectoryAsync(root, gzip, includeBaseDirectory: false, cancellationToken: ct)
                            .ConfigureAwait(false);
                    else
                    {
                        await using var source = File.OpenRead(singleFile);
                        await source.CopyToAsync(gzip, ct).ConfigureAwait(false);
                    }
                }
                break;
            case "7z":
                await CompressWithSystemTarAsync(root, output, "7zip", ct).ConfigureAwait(false);
                break;
        }
    }

    private static async Task CompressWithSystemTarAsync(string root, string output, string format, CancellationToken ct)
    {
        if (!File.Exists(SystemTar))
            throw new NotSupportedException("tar.exe di Windows non è disponibile per la compressione 7z.");

        var start = new ProcessStartInfo(SystemTar)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "--format", format, "-cf", output, "-C", root, "." })
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new IOException("Impossibile avviare tar.exe.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Senza questa chiusura tar continuerebbe a scrivere e la cartella temporanea resterebbe sul disco.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
        finally { await Task.WhenAll(stderr, stdout).ConfigureAwait(false); }
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"Compressione {format} fallita: {error.Trim()}");
    }

    private static void ValidateComicPages(string root, CancellationToken ct)
    {
        bool hasPages = false;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            SKEncodedImageFormat? expected = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => SKEncodedImageFormat.Png,
                ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
                ".webp" => SKEncodedImageFormat.Webp,
                ".gif" => SKEncodedImageFormat.Gif,
                ".bmp" => SKEncodedImageFormat.Bmp,
                _ => null
            };

            using var codec = expected.HasValue ? SKCodec.Create(path) : null;
            if (codec == null || codec.EncodedFormat != expected || !CanDecodeComicPage(codec))
                throw new IOException($"CBZ richiede esclusivamente immagini compatibili. Voce non valida: {Path.GetRelativePath(root, path)}.");
            hasPages = true;
        }

        if (!hasPages)
            throw new IOException("L'archivio non contiene pagine per il CBZ.");
    }

    private static bool CanDecodeComicPage(SKCodec codec)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height);
        if (info.Width < 1 || info.Height < 1 || (long)info.Width * info.Height > 64_000_000)
            throw new IOException("Pagina CBZ troppo grande per la verifica (limite 64 megapixel).");

        using var bitmap = new SKBitmap(info);
        return codec.GetPixels(bitmap.Info, bitmap.GetPixels()) == SKCodecResult.Success;
    }
}
