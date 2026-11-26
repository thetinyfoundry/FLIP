using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

/// <summary>Risolve i motori CLI esterni per i convertitori e per la diagnostica dell'Hub.</summary>
public static class CliResolver
{
    private sealed record EngineFiles(string[] Executables, string[] InstallationFolders);

    private static readonly Dictionary<string, EngineFiles> KnownEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ffmpeg"] = new(["ffmpeg.exe", "ffmpeg"], [Path.Combine("ffmpeg", "bin"), "ffmpeg"]),
        ["pandoc"] = new(["pandoc.exe", "pandoc"], ["Pandoc"]),
        ["libreoffice"] = new(["soffice.com", "soffice.exe", "soffice"], [Path.Combine("LibreOffice", "program")]),
        ["calibre"] = new(["ebook-convert.exe", "ebook-convert"], ["Calibre2", "Calibre"]),
        ["whisper"] = new(["whisper-cli.exe", "faster-whisper-xxl.exe", "whisper-faster.exe", "whisper.exe", "whisper"],
            [Path.Combine("whisper.cpp", "bin"), "whisper.cpp", "Whisper"]),
        ["tesseract"] = new(["tesseract.exe", "tesseract"], ["Tesseract-OCR", "Tesseract"]),
        ["winrar"] = new(["rar.exe", "WinRAR.exe"], ["WinRAR"])
    };

    /// <summary>Solo per test automatizzati: simula un PC pulito senza motori CLI esterni installati.</summary>
    internal static bool SuppressAutoDiscoveryForTest { get; set; }

    /// <summary>Usa lo snapshot del job, oppure le impostazioni correnti se omesso.</summary>
    public static string? Resolve(string engineId, FlipSettings? settings = null)
    {
        if (string.IsNullOrWhiteSpace(engineId)) return null;
        engineId = engineId.Trim().ToLowerInvariant();
        if (!KnownEngines.TryGetValue(engineId, out var engine)) return null;
        var paths = (settings ?? SettingsStore.Current.CreateSnapshot()).CustomEnginePaths;
        if (paths.TryGetValue(engineId, out var configuredPath) && !string.IsNullOrWhiteSpace(configuredPath))
        {
            string path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (engineId == "whisper" && WhisperLocator.IsExcludedWhisperExecutable(path))
            {
                string? replacement = SuppressAutoDiscoveryForTest
                    ? null
                    : WhisperLocator.FindBestExecutable(SearchFolders(engineId, engine.InstallationFolders));
                if (replacement != null && settings == null)
                {
                    try { SettingsStore.Current.Update(s => s.CustomEnginePaths["whisper"] = replacement); }
                    catch { /* best effort */ }
                }
                return replacement;
            }
            if (Path.IsPathFullyQualified(path) && File.Exists(path)) return Path.GetFullPath(path);
        }

        if (SuppressAutoDiscoveryForTest) return null;
        // Il motore incluso precede quelli trovati sul PC, ma non quello scelto dall'utente.
        if (StoreEdition.BundledEngine(engineId) is { } bundled) return bundled;

        if (engineId == "whisper")
        {
            return WhisperLocator.FindBestExecutable(SearchFolders(engineId, engine.InstallationFolders));
        }

        return FindExecutable(SearchFolders(engineId, engine.InstallationFolders), engine.Executables, engineId)
            ?? FindWinGetPackageBinary(engineId, engine.Executables);
    }

    /// <summary>
    /// Auto-rileva silenziosamente tutti i motori esterni sul computer e aggiorna i percorsi mancanti o obsoleti.
    /// </summary>
    public static void AutoDetectAndPersistEngines(SettingsStore? store = null) =>
        _ = VerifyAndDetectEngines(store);

    /// <summary>
    /// Verifica con <see cref="File.Exists"/> ogni percorso salvato nelle impostazioni, rimuove quelli obsoleti
    /// non più presenti su disco (incluso whisper_model) e riesegue il rilevamento su PATH, Cartelle Programmi e WinGet.
    /// </summary>
    public static int VerifyAndDetectEngines(SettingsStore? store = null)
    {
        var targetStore = store ?? SettingsStore.Current;
        WhisperLocator.InvalidateCache();
        var current = targetStore.CreateSnapshot().CustomEnginePaths;
        var toRemove = new List<string>();
        var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (current.TryGetValue("whisper_model", out string? modelPath) && !string.IsNullOrWhiteSpace(modelPath))
        {
            string expModel = Environment.ExpandEnvironmentVariables(modelPath.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expModel) || !File.Exists(expModel) || !WhisperLocator.IsUsableModelFile(expModel))
                toRemove.Add("whisper_model");
        }

        foreach (string engineId in KnownEngines.Keys)
        {
            // Il percorso del pacchetto cambia a ogni aggiornamento: non va salvato.
            if (StoreEdition.BundledEngine(engineId) != null) continue;
            current.TryGetValue(engineId, out string? existing);
            string expanded = string.IsNullOrWhiteSpace(existing)
                ? ""
                : Environment.ExpandEnvironmentVariables(existing.Trim().Trim('"'));

            bool hasValidExisting = !string.IsNullOrWhiteSpace(expanded) &&
                Path.IsPathFullyQualified(expanded) &&
                File.Exists(expanded) &&
                !(engineId == "whisper" && WhisperLocator.IsExcludedWhisperExecutable(expanded));

            if (engineId == "whisper" && hasValidExisting &&
                Path.GetFileName(expanded).Equals("whisper.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Se è configurato un wrapper Python generico ma sul computer esiste un motore C++ nativo, promuovi quello nativo.
                string? bestNative = WhisperLocator.FindBestExecutable();
                if (!string.IsNullOrEmpty(bestNative) && !bestNative.Equals(expanded, StringComparison.OrdinalIgnoreCase) &&
                    (LocalAiEngine.IsWhisperCpp(bestNative) || LocalAiEngine.IsFasterWhisper(bestNative)))
                {
                    updates["whisper"] = bestNative;
                    continue;
                }
            }

            if (!hasValidExisting)
            {
                if (!string.IsNullOrWhiteSpace(existing))
                    toRemove.Add(engineId);

                string? detected = Resolve(engineId, new FlipSettings());
                if (!string.IsNullOrWhiteSpace(detected))
                    updates[engineId] = detected;
            }
        }

        if (toRemove.Count > 0 || updates.Count > 0)
        {
            try
            {
                targetStore.Update(s =>
                {
                    foreach (string key in toRemove)
                        s.CustomEnginePaths.Remove(key);
                    foreach (var (key, value) in updates)
                        s.CustomEnginePaths[key] = value;
                });
            }
            catch { /* best effort */ }
        }

        try
        {
            DocumentTools.Refresh();
            MediaTools.Refresh();
        }
        catch { /* best effort */ }

        return KnownEngines.Keys.Count(k => Resolve(k, targetStore.CreateSnapshot()) != null);
    }

    private static string? FindExecutable(IEnumerable<string> folders, string[] executables, string engineId)
    {
        foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(folder)) continue;
            foreach (string name in executables)
            {
                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            // Older whisper.cpp builds used main.exe. Only try it in a dedicated Whisper folder.
            string directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            if (engineId == "whisper" &&
                (directoryName.Equals("whisper", StringComparison.OrdinalIgnoreCase) ||
                 directoryName.Equals("whisper.cpp", StringComparison.OrdinalIgnoreCase)))
            {
                string legacy = Path.Combine(folder, "main.exe");
                if (File.Exists(legacy)) return Path.GetFullPath(legacy);
            }
        }
        return null;
    }

    private static IEnumerable<string> SearchFolders(string engineId, string[] installationFolders)
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            string managed = Path.Combine(SettingsStore.DataDirectory, "Engines");
            yield return Path.Combine(managed, engineId);
            yield return managed;
            // L'edizione Store riusa i motori già scaricati dalla versione GitHub.
            if (StoreEdition.GitHubEngines is { } shared) yield return Path.Combine(shared, engineId);
        }

        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "Flip", "tools", engineId);
            yield return Path.Combine(local, "Flip", "tools");
            yield return Path.Combine(local, "Programs", "Flip", "tools", engineId);
            yield return Path.Combine(local, "Programs", "Flip", "tools");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "tools", engineId);
        yield return Path.Combine(AppContext.BaseDirectory, "tools");

        // Solo dalle cartelle di compilazione: l'app installata non cerca motori nelle cartelle superiori.
        bool development = AppContext.BaseDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
        for (var project = development ? new DirectoryInfo(AppContext.BaseDirectory) : null; project != null; project = project.Parent)
        {
            if (!File.Exists(Path.Combine(project.FullName, "Flip.csproj"))) continue;
            if (project.Parent?.Parent is { } repo)
            {
                string bundled = Path.Combine(repo.FullName, "tools", engineId);
                yield return bundled;
                if (engineId == "pandoc") yield return Path.Combine(bundled, "pandoc-3.12.1");
            }
            break;
        }

        string[] installRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            string.IsNullOrWhiteSpace(local) ? "" : Path.Combine(local, "Programs")
        ];
        foreach (string root in installRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            foreach (string folder in installationFolders)
                yield return Path.Combine(root, folder);

        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Environment.ExpandEnvironmentVariables(folder.Trim('"'));
    }

    private static string? FindWinGetPackageBinary(string engineId, string[] executables)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return null;
        string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) return null;

        string pattern = engineId switch
        {
            "ffmpeg" => "*FFmpeg*",
            "pandoc" => "*Pandoc*",
            "tesseract" => "*Tesseract*",
            "calibre" => "*calibre*",
            "libreoffice" => "*LibreOffice*",
            "winrar" => "*WinRAR*",
            _ => ""
        };
        if (pattern.Length == 0) return null;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (string package in Directory.EnumerateDirectories(packages, pattern).Order(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string exeName in executables)
                {
                    string? executable = Directory.EnumerateFiles(package, exeName, options).FirstOrDefault();
                    if (executable != null) return executable;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    private static readonly Regex ProgressPercentRegex = new(
        @"(?:progress\s*=\s*|(?<!\d))(\d{1,3})\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Esegue un processo CLI in background con gestione del timeout, iniezione di FFmpeg nel PATH,
    /// chiusura immediata di stdin e cattura di output/progresso.
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        int timeoutSeconds = 600,
        IProgress<double>? progress = null,
        bool idleTimeout = false,
        Func<string, bool>? onLine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        using var timeoutCts = new CancellationTokenSource();
        void KeepAlive()
        {
            if (!idleTimeout) return;
            try { timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds)); }
            catch (ObjectDisposedException) { }
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                ? workingDirectory
                : Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };

        InjectHelperPathsIntoEnvironment(startInfo, executable);

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        void TryReportProgressFromLine(string? line)
        {
            if (progress == null || string.IsNullOrWhiteSpace(line)) return;
            var match = ProgressPercentRegex.Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct) &&
                pct is >= 0 and <= 100)
            {
                progress.Report(Math.Clamp(0.20 + 0.76 * (pct / 100.0), 0.20, 0.96));
            }
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                KeepAlive();
                if (onLine?.Invoke(e.Data) == true) return;
                stdoutBuilder.AppendLine(e.Data);
                TryReportProgressFromLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                KeepAlive();
                stderrBuilder.AppendLine(e.Data);
                TryReportProgressFromLine(e.Data);
            }
        };

        if (!process.Start())
            throw new IOException($"Impossibile avviare il processo: {executable}");

        // Chiudi subito stdin per evitare che il processo figlio resti appeso in attesa di input da console.
        try { process.StandardInput.Close(); }
        catch { /* best effort */ }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* best effort */ }

            await process.WaitForExitAsync().ConfigureAwait(false);

            if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"Il processo {Path.GetFileName(executable)} ha superato il tempo limite di {timeoutSeconds} secondi.");

            throw;
        }

        return (process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private static void InjectHelperPathsIntoEnvironment(ProcessStartInfo startInfo, string executable)
    {
        try
        {
            var extraDirs = new List<string>();

            // 1. Aggiungi la directory dell'eseguibile ed eventuali cartelle padre che ospitano DLL di runtime
            // (es. ggml.dll, whisper.dll).
            string? exeDir = Path.GetDirectoryName(Path.GetFullPath(executable));
            if (!string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir))
            {
                extraDirs.Add(exeDir);
                DirectoryInfo? parent = Directory.GetParent(exeDir);
                for (int depth = 0; depth < 4 && parent != null; depth++, parent = parent.Parent)
                {
                    if (File.Exists(Path.Combine(parent.FullName, "ggml.dll")) ||
                        File.Exists(Path.Combine(parent.FullName, "whisper.dll")))
                    {
                        extraDirs.Add(parent.FullName);
                    }
                }
            }

            // 2. Risolvi e inietta la cartella di ffmpeg.exe in testa al PATH del processo figlio
            string exeName = Path.GetFileNameWithoutExtension(executable);
            if (!exeName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                string? ffmpegExe = Resolve("ffmpeg");
                string? ffmpegDir = string.IsNullOrWhiteSpace(ffmpegExe) ? null : Path.GetDirectoryName(ffmpegExe);
                if (!string.IsNullOrWhiteSpace(ffmpegDir) && Directory.Exists(ffmpegDir))
                    extraDirs.Insert(0, ffmpegDir);
            }

            if (extraDirs.Count == 0) return;
            string currentPath = startInfo.Environment.TryGetValue("PATH", out var envPath) && !string.IsNullOrWhiteSpace(envPath)
                ? envPath
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            string prefix = string.Join(Path.PathSeparator, extraDirs.Distinct(StringComparer.OrdinalIgnoreCase));
            startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
                ? prefix
                : prefix + Path.PathSeparator + currentPath;
        }
        catch { /* best effort */ }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  1. FFMPEG MULTIMEDIA ENGINE
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Motore multimediale FFmpeg on-demand per 22 formati video, 30 audio, sottotitoli e ottimizzazione bitrate.
/// </summary>
public sealed class FfmpegEngine : IConversionEngine
{
    public string Id => "ffmpeg";
    public string DisplayName => "FFmpeg Multimedia Engine";
    public bool IsExternal => true;

    private readonly List<ConversionEdge> _supportedEdges = [];
    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    private static readonly string[] VideoFormats =
    [
        "mp4", "mkv", "webm", "mov", "avi", "flv", "wmv", "ts", "m2ts", "gif",
        "m4v", "3gp", "vob", "ogv", "mpg", "mpeg", "asf", "f4v", "rmvb", "divx", "xvid", "hevc"
    ];

    // MIDI contiene note, non suono: FFmpeg non può leggerlo né scriverlo.
    private static readonly string[] AudioFormats =
    [
        "mp3", "wav", "flac", "aac", "m4a", "ogg", "opus", "wma", "aiff", "alac",
        "ac3", "eac3", "dts", "mp2", "mka", "amr", "wv", "ra", "voc", "au", "caf"
    ];

    // Formati che FFmpeg legge ma per cui non esiste un codificatore.
    private static readonly string[] AudioInputOnly = ["ape"];

    private static readonly string[] SubtitleFormats = ["srt", "vtt", "ass"];

    public FfmpegEngine()
    {
        // Video ↔ Video
        foreach (var src in VideoFormats)
            foreach (var dst in VideoFormats)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));

        // Video → Audio (estrazione tracce audio)
        foreach (var src in VideoFormats)
            foreach (var dst in AudioFormats)
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 8));

        // Audio ↔ Audio
        foreach (var src in AudioFormats.Concat(AudioInputOnly))
            foreach (var dst in AudioFormats)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));

        // Video → Sottotitoli (estrazione stream)
        foreach (var src in VideoFormats)
            foreach (var dst in SubtitleFormats)
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 12));

        // Sottotitoli ↔ Sottotitoli
        foreach (var src in SubtitleFormats)
            foreach (var dst in SubtitleFormats)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 8));
    }

    public bool IsAvailable(FlipSettings settings) =>
        CliResolver.Resolve(Id, settings) != null;

    public async Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            string? exe = CliResolver.Resolve(Id, context.Settings);
            if (exe == null)
                return new EngineExecutionResult(false, "", Loc.T("FFmpeg non è installato o non è configurato.", "FFmpeg is not installed or configured."));

            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;

            if (!File.Exists(srcPath))
                return new EngineExecutionResult(false, "", Loc.F("File non trovato: {0}", "File not found: {0}", srcPath));

            string srcExt = (context.SourceExtension ?? Path.GetExtension(srcPath)).TrimStart('.').ToLowerInvariant();
            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();

            string? dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(dstPath)) File.Delete(dstPath);

            context.Progress?.Report(0.10);

            double? duration = await ProbeDurationAsync(exe, srcPath, context.CancellationToken).ConfigureAwait(false);
            var acceleration = context.Settings.HardwareAcceleration;
            var (exitCode, stdout, stderr) = await RunAsync(acceleration).ConfigureAwait(false);
            // Codificatore della scheda video assente o occupato: la conversione deve riuscire comunque.
            if (exitCode != 0 && acceleration is HardwareAcceleration.Nvenc or HardwareAcceleration.Amf or HardwareAcceleration.Qsv)
                (exitCode, stdout, stderr) = await RunAsync(HardwareAcceleration.CpuOnly).ConfigureAwait(false);

            Task<(int, string, string)> RunAsync(HardwareAcceleration hardware)
            {
                var args = BuildFfmpegArgs(srcPath, dstPath, srcExt, dstExt, context, hardware, duration);
                // Un film può richiedere ore: conta solo se FFmpeg smette di dare segni di vita.
                return CliResolver.RunProcessAsync(exe, args, Path.GetDirectoryName(dstPath), context.CancellationToken,
                    timeoutSeconds: 1200, context.Progress, idleTimeout: true, onLine: line => ReportProgress(line, duration, context.Progress));
            }

            if (exitCode != 0)
            {
                string errMsg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return new EngineExecutionResult(false, "", Loc.F("Errore FFmpeg (exit {0}): {1}", "FFmpeg error (exit {0}): {1}", exitCode, errMsg.Trim()));
            }

            if (!File.Exists(dstPath) || new FileInfo(dstPath).Length == 0)
                return new EngineExecutionResult(false, "", Loc.T("FFmpeg non ha prodotto un file di output valido.", "FFmpeg did not produce a valid output file."));

            context.Progress?.Report(1.0);
            long bytes = new FileInfo(dstPath).Length;
            return new EngineExecutionResult(true, dstPath, null, bytes);
        }
        catch (OperationCanceledException)
        {
            return new EngineExecutionResult(false, "", Loc.T("Elaborazione FFmpeg annullata.", "FFmpeg processing cancelled."));
        }
        catch (Exception ex)
        {
            return new EngineExecutionResult(false, "", Loc.F("Eccezione FFmpeg: {0}", "FFmpeg exception: {0}", ex.Message));
        }
    }

    private static readonly Regex DurationRegex = new(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);

    /// <summary>Durata dichiarata dal file, letta con FFmpeg perché ffprobe può mancare.</summary>
    internal static async Task<double?> ProbeDurationAsync(string exe, string source, CancellationToken token)
    {
        var (_, _, stderr) = await CliResolver.RunProcessAsync(exe, ["-hide_banner", "-nostdin", "-i", source], null, token, timeoutSeconds: 60).ConfigureAwait(false);
        var match = DurationRegex.Match(stderr);
        if (!match.Success) return null;
        double seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600.0 +
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60.0 +
            double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return seconds > 0.04 ? seconds : null;
    }

    /// <summary>Righe key=value di -progress: aggiornano la percentuale e non vanno conservate in memoria.</summary>
    private static bool ReportProgress(string line, double? duration, IProgress<double>? progress)
    {
        int equals = line.IndexOf('=');
        if (equals <= 0 || line.AsSpan(0, equals).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz_0123456789")) return false;
        if (duration is > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(equals + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds) && microseconds > 0)
            progress?.Report(Math.Clamp(0.10 + 0.86 * microseconds / 1e6 / duration.Value, 0.10, 0.96));
        return true;
    }

    private static List<string> BuildFfmpegArgs(string src, string dst, string srcExt, string dstExt, ConversionContext ctx,
        HardwareAcceleration hardware, double? duration)
    {
        var args = new List<string> { "-y", "-hide_banner", "-nostdin", "-nostats", "-progress", "pipe:1", "-i", src };

        bool isDstVideo = VideoFormats.Contains(dstExt);
        bool isDstAudio = AudioFormats.Contains(dstExt);
        bool isDstSubtitle = SubtitleFormats.Contains(dstExt);
        long? videoBitrate = null;
        long audioBitrate = 128_000;

        // Dimensione massima: il bitrate dipende dalla durata reale del video.
        if (isDstVideo && ctx.CustomParameters.TryGetValue("target_mb", out var mbStr) &&
            double.TryParse(mbStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double targetMb) && targetMb > 0)
        {
            double seconds = duration ?? throw new IOException(Loc.T("Durata del video non leggibile: impossibile calcolare la dimensione.", "Video duration unavailable: unable to compute the target size."));
            long total = (long)(targetMb * 8 * 1024 * 1024 / seconds);
            // Un file già piccolo non deve crescere.
            total = Math.Min(total, (long)(new FileInfo(src).Length * 8 / seconds));
            audioBitrate = Math.Clamp(total / 8, 24_000, 128_000);
            videoBitrate = Math.Max(30_000, total - audioBitrate);
        }

        if (isDstAudio)
        {
            args.Add("-vn"); // disabilita video
            switch (dstExt)
            {
                case "mp3": args.AddRange(["-c:a", "libmp3lame", "-q:a", "2"]); break;
                case "m4a" or "aac": args.AddRange(["-c:a", "aac", "-b:a", "192k"]); break;
                case "flac": args.AddRange(["-c:a", "flac"]); break;
                case "wav": args.AddRange(["-c:a", "pcm_s16le"]); break;
                case "ogg": args.AddRange(["-c:a", "libvorbis", "-q:a", "4"]); break;
                case "opus": args.AddRange(["-c:a", "libopus", "-b:a", "128k"]); break;
                case "alac": args.AddRange(["-c:a", "alac", "-f", "ipod"]); break;
                case "dts": args.AddRange(["-c:a", "dca", "-strict", "experimental", "-ar", "48000", "-ac", "2", "-b:a", "768k"]); break;
                case "amr": args.AddRange(["-c:a", "libopencore_amrnb", "-ar", "8000", "-ac", "1", "-b:a", "12.2k"]); break;
                case "ra": args.AddRange(["-c:a", "real_144", "-ar", "8000", "-ac", "1", "-f", "rm"]); break;
                case "voc": args.AddRange(["-c:a", "pcm_s16le"]); break;
            }
        }
        else if (isDstSubtitle)
        {
            args.AddRange(["-an", "-vn"]);
            if (dstExt == "srt") args.AddRange(["-c:s", "subrip"]);
            else if (dstExt == "vtt") args.AddRange(["-c:s", "webvtt"]);
            else if (dstExt == "ass") args.AddRange(["-c:s", "ass"]);
        }
        else if (isDstVideo)
        {
            if (dstExt == "gif")
            {
                // Ottimizzazione palette per GIF ad alta qualità
                args.AddRange(["-vf", "fps=15,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"]);
            }
            else
            {
                ApplyVideoCodec(args, dstExt, hardware, videoBitrate, audioBitrate);
            }
        }

        if (ctx.CustomParameters.TryGetValue("strip_metadata", out var stripStr) &&
            (stripStr.Equals("true", StringComparison.OrdinalIgnoreCase) || stripStr == "1"))
        {
            args.AddRange(["-map_metadata", "-1"]);
        }

        args.Add(dst);
        return args;
    }

    private static void ApplyVideoCodec(List<string> args, string dstExt, HardwareAcceleration hw, long? videoBitrate, long audioBitrate)
    {
        bool h264 = dstExt is "mp4" or "mkv" or "mov" or "m4v" or "f4v" or "3gp" or "ts" or "m2ts" or "flv";
        // H.264 e molti lettori richiedono dimensioni pari e colori 4:2:0 (le GIF non li hanno).
        string size = videoBitrate switch
        {
            < 250_000 => "scale=-2:'trunc(min(360,ih)/2)*2'",
            < 700_000 => "scale=-2:'trunc(min(480,ih)/2)*2'",
            < 1_500_000 => "scale=-2:'trunc(min(720,ih)/2)*2'",
            _ => "scale=trunc(iw/2)*2:trunc(ih/2)*2"
        };
        args.AddRange(["-vf", size]);

        if (h264)
        {
            string codec = hw switch
            {
                HardwareAcceleration.Nvenc => "h264_nvenc",
                HardwareAcceleration.Amf => "h264_amf",
                HardwareAcceleration.Qsv => "h264_qsv",
                _ => "libx264"
            };
            args.AddRange(["-c:v", codec, "-pix_fmt", codec == "h264_qsv" ? "nv12" : "yuv420p"]);
            if (codec == "libx264") args.AddRange(["-preset", "fast"]);
            if (codec == "libx264" && videoBitrate is null) args.AddRange(["-crf", "22"]);
            if (codec == "h264_nvenc") args.AddRange(["-preset", "p4"]);
            args.AddRange(["-c:a", "aac"]);
            // Indice in testa al file: il video parte subito sul web e nelle chat.
            if (dstExt is "mp4" or "mov" or "m4v" or "f4v") args.AddRange(["-movflags", "+faststart"]);
        }
        else if (dstExt == "webm")
        {
            args.AddRange(["-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-c:a", "libopus"]);
            if (videoBitrate is null) args.AddRange(["-crf", "30", "-b:v", "0"]);
        }
        else if (dstExt is "divx" or "xvid") args.AddRange(["-c:v", "mpeg4", "-vtag", "XVID", "-q:v", "4", "-c:a", "libmp3lame", "-f", "avi"]);
        else if (dstExt == "avi") args.AddRange(["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame"]);
        else if (dstExt == "rmvb") args.AddRange(["-c:v", "rv20", "-c:a", "ac3", "-ar", "44100", "-f", "rm"]);
        else if (dstExt is "wmv" or "asf") args.AddRange(["-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2"]);
        else if (dstExt is "mpg" or "mpeg" or "vob") args.AddRange(["-c:v", "mpeg2video", "-q:v", "3", "-c:a", "mp2"]);
        else if (dstExt == "ogv") args.AddRange(["-c:v", "libtheora", "-q:v", "7", "-c:a", "libvorbis"]);

        if (videoBitrate is long bitrate)
        {
            string rate = $"{Math.Max(1, bitrate / 1000)}k";
            args.AddRange(["-b:v", rate, "-maxrate", rate, "-bufsize", $"{Math.Max(2, bitrate / 500)}k", "-b:a", $"{audioBitrate / 1000}k"]);
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  2. PANDOC UNIVERSAL DOCUMENT ENGINE
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Motore per documenti e markup Pandoc on-demand.
/// </summary>
public sealed class PandocEngine : IConversionEngine
{
    public string Id => "pandoc";
    public string DisplayName => "Pandoc Universal Document Converter";
    public bool IsExternal => true;

    private readonly List<ConversionEdge> _supportedEdges = [];
    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    private static readonly string[] PandocFormats =
    [
        "md", "markdown", "rst", "tex", "typ", "adoc", "org", "ipynb",
        "html", "htm", "docx", "odt", "epub", "rtf", "pptx", "txt"
    ];

    public PandocEngine()
    {
        foreach (var src in PandocFormats)
            foreach (var dst in PandocFormats)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));
    }

    public bool IsAvailable(FlipSettings settings) =>
        CliResolver.Resolve(Id, settings) != null;

    public async Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            string? exe = CliResolver.Resolve(Id, context.Settings);
            if (exe == null)
                return new EngineExecutionResult(false, "", Loc.T("Pandoc non è installato o non è configurato.", "Pandoc is not installed or configured."));

            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;

            if (!File.Exists(srcPath))
                return new EngineExecutionResult(false, "", Loc.F("File non trovato: {0}", "File not found: {0}", srcPath));

            string srcExt = (context.SourceExtension ?? Path.GetExtension(srcPath)).TrimStart('.').ToLowerInvariant();
            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();

            string? dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(dstPath)) File.Delete(dstPath);

            context.Progress?.Report(0.15);

            string from = srcExt switch
            {
                "md" or "markdown" => "markdown",
                "htm" or "html" => "html",
                "txt" => "html",
                "tex" => "latex",
                "typ" => "typst",
                "adoc" => "asciidoc",
                _ => srcExt
            };

            string to = dstExt switch
            {
                "md" or "markdown" => "gfm",
                "htm" or "html" => "html5",
                "txt" => "plain",
                "tex" => "latex",
                "typ" => "typst",
                "adoc" => "asciidoc",
                _ => dstExt
            };

            bool stripMetadata = context.CustomParameters.TryGetValue("strip_metadata", out var stripStr) &&
                (stripStr.Equals("true", StringComparison.OrdinalIgnoreCase) || stripStr == "1");

            var args = new List<string>
            {
                "--sandbox",
                "--standalone",
                "--from", from,
                "--to", to,
                "--output", dstPath
            };
            if (to == "html5") args.Add("--embed-resources");

            string work = Directory.CreateTempSubdirectory("Flip-Pandoc-").FullName;
            (int exitCode, string stdout, string stderr) run;
            try
            {
                if (!stripMetadata || to is "epub" or "html5") args.AddRange(DocumentTools.PandocTitle(work, to, stripMetadata ? "-" : srcPath));
                string input = srcPath;
                if (srcExt == "txt")
                {
                    // Pandoc non legge il testo semplice: i paragrafi passano come HTML, senza interpretare simboli Markdown.
                    input = Path.Combine(work, "text.html");
                    var paragraphs = Regex.Split(TextFiles.Read(srcPath).ReplaceLineEndings("\n"), @"\n[ \t]*\n+")
                        .Where(block => !string.IsNullOrWhiteSpace(block))
                        .Select(block => "<p>" + System.Net.WebUtility.HtmlEncode(block.Trim('\n')).Replace("\n", "<br>\n") + "</p>");
                    File.WriteAllText(input, "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>\n" + string.Join("\n", paragraphs) + "\n</body></html>", new UTF8Encoding(false));
                }
                args.Add(input);
                run = await CliResolver.RunProcessAsync(
                    exe, args, Path.GetDirectoryName(dstPath), context.CancellationToken, timeoutSeconds: 300, context.Progress);
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Evidence.Log("pandoc.temp.cleanup.failed", ex.Message); }
            }
            var (exitCode, stdout, stderr) = run;

            if (exitCode != 0)
            {
                string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return new EngineExecutionResult(false, "", Loc.F("Errore Pandoc (exit {0}): {1}", "Pandoc error (exit {0}): {1}", exitCode, err.Trim()));
            }

            if (!File.Exists(dstPath) || new FileInfo(dstPath).Length == 0)
                return new EngineExecutionResult(false, "", Loc.T("Pandoc non ha generato alcun file valido.", "Pandoc did not produce a valid output file."));

            context.Progress?.Report(1.0);
            long bytes = new FileInfo(dstPath).Length;
            return new EngineExecutionResult(true, dstPath, null, bytes);
        }
        catch (OperationCanceledException)
        {
            return new EngineExecutionResult(false, "", Loc.T("Conversione Pandoc annullata.", "Pandoc conversion cancelled."));
        }
        catch (Exception ex)
        {
            return new EngineExecutionResult(false, "", Loc.F("Eccezione Pandoc: {0}", "Pandoc exception: {0}", ex.Message));
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  3. LIBREOFFICE HEADLESS ENGINE
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Motore da ufficio LibreOffice headless on-demand per formati legacy e moderni.
/// </summary>
public sealed class LibreOfficeEngine : IConversionEngine
{
    public string Id => "libreoffice";
    public string DisplayName => "LibreOffice Headless Engine";
    public bool IsExternal => true;

    private readonly List<ConversionEdge> _supportedEdges = [];
    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    private static readonly string[] OfficeSourceFormats =
    [
        "doc", "docx", "odt", "rtf", "wps", "pages",
        "xls", "xlsx", "xlsm", "xltx", "xlsb", "ods", "csv", "numbers",
        "ppt", "pptx", "odp", "key", "pub"
    ];

    private static readonly string[] OfficeTargetFormats =
    [
        "pdf", "docx", "odt", "rtf", "xlsx", "ods", "pptx", "odp", "html", "txt"
    ];

    public LibreOfficeEngine()
    {
        _supportedEdges.Add(new ConversionEdge("docx", "doc", Id, 10));
        foreach (var src in OfficeSourceFormats)
            foreach (var dst in OfficeTargetFormats)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));
    }

    public bool IsAvailable(FlipSettings settings) =>
        CliResolver.Resolve(Id, settings) != null;

    public async Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            string? exe = CliResolver.Resolve(Id, context.Settings);
            if (exe == null)
                return new EngineExecutionResult(false, "", Loc.T("LibreOffice non è installato o non è configurato.", "LibreOffice is not installed or configured."));

            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;

            if (!File.Exists(srcPath))
                return new EngineExecutionResult(false, "", Loc.F("File non trovato: {0}", "File not found: {0}", srcPath));

            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();
            string outDir = Path.GetDirectoryName(dstPath) ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);

            context.Progress?.Report(0.15);

            // Crea un profilo isolato per prevenire collisioni e blocchi di LibreOffice
            string profile = Directory.CreateTempSubdirectory("Flip-LO-").FullName;
            string tempOutDir = Path.Combine(profile, "out");
            Directory.CreateDirectory(tempOutDir);

            try
            {
                DocumentTools.DisableMacros(profile);
                var args = new List<string>
                {
                    "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri,
                    "--headless",
                    "--nologo",
                    "--nodefault",
                    "--norestore",
                    "--convert-to", dstExt == "doc" ? "doc:MS Word 97" : dstExt,
                    "--outdir", tempOutDir,
                    srcPath
                };

                var (exitCode, stdout, stderr) = await CliResolver.RunProcessAsync(
                    exe, args, profile, context.CancellationToken, timeoutSeconds: 300, context.Progress);

                if (exitCode != 0 && await TryBuiltInSheetAsync(context).ConfigureAwait(false) is { } fallback) return fallback;
                if (exitCode != 0)
                {
                    string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    return new EngineExecutionResult(false, "", Loc.F("Errore LibreOffice (exit {0}): {1}", "LibreOffice error (exit {0}): {1}", exitCode, err.Trim()));
                }

                string produced = Path.Combine(tempOutDir, Path.GetFileNameWithoutExtension(srcPath) + "." + dstExt);
                if (!File.Exists(produced))
                {
                    // Cerca eventuali file generati con estensione diversa
                    var any = Directory.GetFiles(tempOutDir).FirstOrDefault();
                    if (any != null) produced = any;
                    else if (await TryBuiltInSheetAsync(context).ConfigureAwait(false) is { } substitute) return substitute;
                    else return new EngineExecutionResult(false, "", Loc.T("LibreOffice non ha prodotto il file atteso.", "LibreOffice did not produce the expected file."));
                }

                if (File.Exists(dstPath)) File.Delete(dstPath);
                File.Move(produced, dstPath);

                context.Progress?.Report(1.0);
                long bytes = new FileInfo(dstPath).Length;
                return new EngineExecutionResult(true, dstPath, null, bytes);
            }
            finally
            {
                try { Directory.Delete(profile, true); } catch { /* best effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            return new EngineExecutionResult(false, "", Loc.T("Conversione LibreOffice annullata.", "LibreOffice conversion cancelled."));
        }
        catch (Exception ex)
        {
            if (await TryBuiltInSheetAsync(context).ConfigureAwait(false) is { } rescued) return rescued;
            return new EngineExecutionResult(false, "", Loc.F("Eccezione LibreOffice: {0}", "LibreOffice exception: {0}", ex.Message));
        }
    }

    /// <summary>Se LibreOffice non parte, è bloccato o si interrompe, il foglio di calcolo diventa PDF con il lettore interno.</summary>
    private static async Task<EngineExecutionResult?> TryBuiltInSheetAsync(ConversionContext context)
    {
        string source = (context.SourceExtension ?? "").TrimStart('.').ToLowerInvariant();
        string target = (context.TargetExtension ?? "").TrimStart('.').ToLowerInvariant();
        if (target != "pdf" || source is not ("xlsx" or "xlsm" or "xltx" or "xls" or "ods" or "csv") || context.CancellationToken.IsCancellationRequested) return null;
        var result = await new PdfEngine().ConvertAsync(context).ConfigureAwait(false);
        return result.Success ? result : null;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  4. CALIBRE EBOOK ENGINE
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Motore Calibre (ebook-convert) per il formato ebook e publishing.
/// </summary>
public sealed class CalibreEngine : IConversionEngine
{
    public string Id => "calibre";
    public string DisplayName => "Calibre E-Book Engine";
    public bool IsExternal => true;

    private readonly List<ConversionEdge> _supportedEdges = [];
    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    private static readonly string[] EbookOutputs =
    [
        "epub", "kepub", "mobi", "azw3", "fb2", "lit", "lrf", "pdb", "pdf", "txt"
    ];
    private static readonly string[] EbookInputs = [.. EbookOutputs, "cbz"];

    public CalibreEngine()
    {
        foreach (var src in EbookInputs)
            foreach (var dst in EbookOutputs)
                if (src != dst)
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));
    }

    public bool IsAvailable(FlipSettings settings) =>
        CliResolver.Resolve(Id, settings) != null;

    public async Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;
            if (string.Equals(context.TargetExtension?.TrimStart('.'), "cbz", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(dstPath), ".cbz", StringComparison.OrdinalIgnoreCase))
                return new EngineExecutionResult(false, "", Loc.T("Calibre non supporta CBZ come formato di output.", "Calibre does not support CBZ as an output format."));

            string? exe = CliResolver.Resolve(Id, context.Settings);
            if (exe == null)
                return new EngineExecutionResult(false, "", Loc.T("Calibre (ebook-convert) non è installato o non è configurato.", "Calibre (ebook-convert) is not installed or configured."));

            if (!File.Exists(srcPath))
                return new EngineExecutionResult(false, "", Loc.F("File non trovato: {0}", "File not found: {0}", srcPath));

            string? dir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(dstPath)) File.Delete(dstPath);

            context.Progress?.Report(0.15);

            var args = new[] { srcPath, dstPath };
            var (exitCode, stdout, stderr) = await CliResolver.RunProcessAsync(
                exe, args, Path.GetDirectoryName(dstPath), context.CancellationToken, timeoutSeconds: 600, context.Progress);

            if (exitCode != 0)
            {
                string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return new EngineExecutionResult(false, "", Loc.F("Errore Calibre (exit {0}): {1}", "Calibre error (exit {0}): {1}", exitCode, err.Trim()));
            }

            if (!File.Exists(dstPath) || new FileInfo(dstPath).Length == 0)
                return new EngineExecutionResult(false, "", Loc.T("Calibre non ha prodotto alcun file valido.", "Calibre did not produce a valid output file."));

            context.Progress?.Report(1.0);
            long bytes = new FileInfo(dstPath).Length;
            return new EngineExecutionResult(true, dstPath, null, bytes);
        }
        catch (OperationCanceledException)
        {
            return new EngineExecutionResult(false, "", Loc.T("Conversione Calibre annullata.", "Calibre conversion cancelled."));
        }
        catch (Exception ex)
        {
            return new EngineExecutionResult(false, "", Loc.F("Eccezione Calibre: {0}", "Calibre exception: {0}", ex.Message));
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  5. LOCAL AI / WHISPER ENGINE
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Motore di trascrizione e sottotitolazione locale Whisper AI.
/// Con whisper.cpp ogni sorgente viene prima normalizzata in WAV 16 kHz mono PCM16.
/// </summary>
public sealed class LocalAiEngine : IConversionEngine
{
    public string Id => "whisper";
    public string DisplayName => "Local Whisper AI Speech-to-Text";
    public bool IsExternal => true;

    private readonly List<ConversionEdge> _supportedEdges = [];
    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    private static readonly string[] AudioVideoSources =
    [
        "mp3", "wav", "m4a", "mp4", "mkv", "ogg", "opus", "flac", "aac", "mov", "webm", "avi"
    ];

    /// <summary>Messaggio mostrato (anche al centro della ruota) quando whisper-cli o il modello ggml mancano.</summary>
    internal static string ModuleMissingMessage => Loc.T(
        "Modulo Whisper AI non installato: aprilo da Impostazioni -> Motori & GPU",
        "Whisper AI module is not installed: install it in Settings → Engines & GPU");

    private static readonly string[] TextTargets = ["srt", "vtt", "txt", "md"];

    /// <summary>Modelli ggml in ordine di qualità: viene usato il migliore presente accanto all'eseguibile.</summary>
    private static readonly string[] PreferredModels =
    [
        "ggml-large-v3-turbo.bin", "ggml-large-v3.bin", "ggml-medium.bin",
        "ggml-small.bin", "ggml-base.bin", "ggml-tiny.bin"
    ];

    public LocalAiEngine()
    {
        foreach (var src in AudioVideoSources)
            foreach (var dst in TextTargets)
                _supportedEdges.Add(new ConversionEdge(src, dst, Id, 15));
    }

    public bool IsAvailable(FlipSettings settings)
    {
        string? exe = CliResolver.Resolve(Id, settings);
        return exe != null && (!IsWhisperCpp(exe) || FindModel(exe) != null);
    }

    /// <summary>whisper.cpp si distingue dal pacchetto Python per il nome dell'eseguibile.</summary>
    internal static bool IsWhisperCpp(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("whisper-cli", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("main", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Riconosce i binari standalone di faster-whisper (faster-whisper-xxl, whisper-faster).</summary>
    internal static bool IsFasterWhisper(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("faster-whisper-xxl", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("whisper-faster", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("faster-whisper", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cerca un modello ggml scelto manualmente, accanto all'eseguibile, nella sua sottocartella "models",
    /// oppure nelle directory standard di Whisper già presenti sul computer.
    /// </summary>
    internal static string? FindModel(string? executable = null, string? requested = null)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string explicitPath = Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'));
            if (Path.IsPathFullyQualified(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        }

        // Se l'eseguibile è uno script di test (.cmd / .bat), cerca solo nelle cartelle locali del test
        // così i test automatici isolati non vengono influenzati dai modelli reali presenti sul PC.
        bool isTestScript = !string.IsNullOrWhiteSpace(executable) &&
            Path.GetExtension(executable) is ".cmd" or ".bat";

        if (FindModelBeside(executable, requested, requireMinimumSize: !isTestScript) is { } beside) return beside;

        if (isTestScript) return null;

        try
        {
            var customPaths = SettingsStore.Current.CreateSnapshot().CustomEnginePaths;
            if (customPaths.TryGetValue("whisper_model", out string? customModel) && !string.IsNullOrWhiteSpace(customModel))
            {
                string expanded = Environment.ExpandEnvironmentVariables(customModel.Trim().Trim('"'));
                if (Path.IsPathFullyQualified(expanded) && File.Exists(expanded) && WhisperLocator.IsUsableModelFile(expanded))
                    return Path.GetFullPath(expanded);
            }
        }
        catch { /* best effort */ }

        return WhisperLocator.FindGlobalModel(requested);
    }

    /// <summary>Cerca un modello accanto all'eseguibile o nelle cartelle "models" vicine.</summary>
    internal static string? FindModelBeside(string? executable, string? requested = null, bool requireMinimumSize = true)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded) || Path.GetDirectoryName(expanded) is not { } exeFolder) return null;
        var parent = Directory.GetParent(exeFolder);
        string?[] folders =
        [
            exeFolder,
            Path.Combine(exeFolder, "models"),
            parent is null ? null : Path.Combine(parent.FullName, "models"),
            parent?.Parent is { } grandparent ? Path.Combine(grandparent.FullName, "models") : null
        ];
        foreach (string? folder in folders)
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            if (SearchModelInFolder(folder, requested, requireMinimumSize) is { } found) return found;
        }
        return null;
    }

    internal static string? SearchModelInFolder(string folder, string? requested, bool requireMinimumSize)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string named = Path.Combine(folder, Path.GetFileName(requested.Trim()));
                if (File.Exists(named) && (!requireMinimumSize || WhisperLocator.IsUsableModelFile(named)))
                    return Path.GetFullPath(named);
            }

            foreach (string model in PreferredModels)
            {
                string candidate = Path.Combine(folder, model);
                if (File.Exists(candidate) && (!requireMinimumSize || WhisperLocator.IsUsableModelFile(candidate)))
                    return Path.GetFullPath(candidate);
            }

            foreach (string file in Directory.EnumerateFiles(folder, "ggml-*.bin").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!requireMinimumSize || WhisperLocator.IsUsableModelFile(file))
                    return Path.GetFullPath(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public async Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;

            if (!File.Exists(srcPath))
                return new EngineExecutionResult(false, "", Loc.F("File non trovato: {0}", "File not found: {0}", srcPath));

            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();
            if (!TextTargets.Contains(dstExt))
                return new EngineExecutionResult(false, "", Loc.F("Formato di trascrizione non supportato: {0}", "Unsupported transcription format: {0}", dstExt));

            string? exe = CliResolver.Resolve(Id, context.Settings);
            bool isTestScript = !string.IsNullOrWhiteSpace(exe) && Path.GetExtension(exe) is ".cmd" or ".bat";

            // Download "Just-In-Time" solo dei componenti mancanti (es. solo whisper-cli.exe se il modello è già presente, o viceversa)
            if (!isTestScript && (exe == null || (IsWhisperCpp(exe) && FindModel(exe) == null)))
            {
                var installProgress = new Progress<WhisperInstallProgress>(p =>
                    context.Progress?.Report(Math.Clamp(0.02 + 0.16 * p.Fraction, 0.02, 0.18)));
                exe = await WhisperModuleInstaller.InstallAsync(installProgress, context.CancellationToken).ConfigureAwait(false);
            }

            if (exe == null)
                return new EngineExecutionResult(false, "", ModuleMissingMessage);

            string tempDir = Directory.CreateTempSubdirectory("Flip-Whisper-").FullName;

            try
            {
                context.Progress?.Report(0.18);
                string outFormat = dstExt == "md" ? "txt" : dstExt;

                string produced;
                if (IsWhisperCpp(exe))
                {
                    var (file, error) = await RunWhisperCppAsync(exe, srcPath, tempDir, outFormat, context).ConfigureAwait(false);
                    if (file == null) return new EngineExecutionResult(false, "", error);
                    produced = file;
                }
                else
                {
                    var (file, error) = await RunPythonOrFasterWhisperAsync(exe, srcPath, tempDir, outFormat, context).ConfigureAwait(false);
                    if (file == null) return new EngineExecutionResult(false, "", error);
                    produced = file;
                }

                context.Progress?.Report(0.95);
                string raw = File.ReadAllText(produced, Encoding.UTF8);
                string content = outFormat switch
                {
                    "txt" => CleanTranscript(raw),
                    "srt" => CleanSrt(raw),
                    _ => raw.Trim() + Environment.NewLine
                };
                if (dstExt == "md")
                    content = $"# {Loc.T("Trascrizione", "Transcript")} — {Path.GetFileNameWithoutExtension(srcPath)}{Environment.NewLine}{Environment.NewLine}{content}";

                string? dir = Path.GetDirectoryName(dstPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(dstPath)) File.Delete(dstPath);
                File.WriteAllText(dstPath, content, new UTF8Encoding(false));

                context.Progress?.Report(1.0);
                long bytes = new FileInfo(dstPath).Length;
                return new EngineExecutionResult(true, dstPath, null, bytes);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            return new EngineExecutionResult(false, "", Loc.T("Trascrizione Whisper AI annullata.", "Whisper AI transcription cancelled."));
        }
        catch (Exception ex)
        {
            return new EngineExecutionResult(false, "", Loc.F("Eccezione Whisper AI: {0}", "Whisper AI exception: {0}", ex.Message));
        }
    }

    /// <summary>Pipeline whisper.cpp: sorgente → WAV 16 kHz mono PCM16 → whisper-cli → TXT/SRT/VTT.</summary>
    private static async Task<(string? File, string Error)> RunWhisperCppAsync(
        string exe, string srcPath, string tempDir, string outFormat, ConversionContext context)
    {
        context.CustomParameters.TryGetValue("model", out var requestedModel);
        string? model = FindModel(exe, requestedModel);
        if (model == null)
            return (null, ModuleMissingMessage);

        string wavPath = Path.Combine(tempDir, "input16k.wav");
        await WhisperAudio.PrepareWav16kMonoAsync(srcPath, wavPath, context.Settings, context.CancellationToken).ConfigureAwait(false);
        context.Progress?.Report(0.25);

        string language = context.CustomParameters.TryGetValue("language", out var lang) && !string.IsNullOrWhiteSpace(lang)
            ? lang.Trim().ToLowerInvariant()
            : "auto";

        // whisper-cli aggiunge da solo ".txt"/".srt": -of riceve il percorso SENZA estensione.
        string outPrefix = Path.Combine(tempDir, OutputStem(context.OutputFilePath));
        var args = new List<string>
        {
            "-m", model,
            "-f", wavPath,
            "-l", language,
            "-t", Math.Clamp(Environment.ProcessorCount - 1, 1, 8).ToString(CultureInfo.InvariantCulture),
            "-of", outPrefix,
            "-o" + outFormat,
            "-pp"
        };

        var (exitCode, stdout, stderr) = await CliResolver.RunProcessAsync(
            exe, args, Path.GetDirectoryName(exe), context.CancellationToken, timeoutSeconds: 3600, context.Progress).ConfigureAwait(false);

        context.Progress?.Report(0.92);
        string? produced = FindWhisperOutput(tempDir, outPrefix, outFormat);
        if (exitCode == 0 && (produced is null || new FileInfo(produced).Length == 0))
            return (null, Loc.T("Nessun parlato riconosciuto nell'audio.", "No speech was recognised in the audio."));
        if (exitCode != 0 || produced is null)
        {
            string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (null, $"Errore Whisper AI (exit {exitCode}): {Tail(err)}");
        }
        return (produced, "");
    }

    /// <summary>Nome base sicuro (ASCII, senza estensione) per -of: whisper-cli su Windows non gestisce bene i percorsi Unicode.</summary>
    internal static string OutputStem(string? targetPath)
    {
        string stem = Path.GetFileNameWithoutExtension(targetPath ?? "");
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(stem.Length);
        foreach (char c in stem)
            sb.Append(c > 0x7E || c < 0x20 || invalid.Contains(c) ? '_' : c);
        string safe = sb.ToString().Trim().TrimEnd('.');
        return safe.Length == 0 || safe.Equals("input16k", StringComparison.OrdinalIgnoreCase) ? "transcript" : safe;
    }

    /// <summary>Trova il file prodotto da whisper-cli, tollerando build che duplicano il suffisso (".txt.txt").</summary>
    internal static string? FindWhisperOutput(string tempDir, string outPrefix, string outFormat)
    {
        string[] candidates = [outPrefix + "." + outFormat, outPrefix + "." + outFormat + "." + outFormat];
        foreach (string candidate in candidates)
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 0) return candidate;
        try
        {
            return Directory.EnumerateFiles(tempDir, "*." + outFormat)
                .Where(path => new FileInfo(path).Length > 0)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Compatibilità con faster-whisper-xxl / whisper-faster e con la CLI Python openai-whisper.</summary>
    private static async Task<(string? File, string Error)> RunPythonOrFasterWhisperAsync(
        string exe, string srcPath, string tempDir, string outFormat, ConversionContext context)
    {
        var args = new List<string>
        {
            srcPath,
            "--output_dir", tempDir,
            "--output_format", outFormat
        };

        if (IsFasterWhisper(exe))
        {
            string modelName = context.CustomParameters.TryGetValue("model", out var reqModel) && !string.IsNullOrWhiteSpace(reqModel)
                ? reqModel.Trim()
                : "base";
            args.AddRange(["--model", modelName]);
        }

        // Parametro lingua opzionale (es. "it", "en"). Senza parametro Whisper rileva la lingua.
        if (context.CustomParameters.TryGetValue("language", out var lang) && !string.IsNullOrWhiteSpace(lang) &&
            !lang.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--language", lang.Trim()]);
        }

        var (exitCode, stdout, stderr) = await CliResolver.RunProcessAsync(
            exe, args, tempDir, context.CancellationToken, timeoutSeconds: 3600, context.Progress).ConfigureAwait(false);

        if (exitCode != 0)
        {
            string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (null, $"Errore Whisper AI (exit {exitCode}): {Tail(err)}");
        }

        string candidate = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(srcPath) + "." + outFormat);
        if (File.Exists(candidate)) return (candidate, "");
        string? any = Directory.GetFiles(tempDir, "*." + outFormat).FirstOrDefault();
        return any != null ? (any, "") : (null, "Whisper AI non ha prodotto file di trascrizione.");
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= 600 ? text : "…" + text[^600..];
    }

    private static bool IsNonSpeechMarker(string line) =>
        line.Length == 0 ||
        line.Equals("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("[ Silence ]", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("[SILENCE]", StringComparison.OrdinalIgnoreCase);

    /// <summary>Testo pulito: una riga per segmento, senza spazi iniziali né marcatori di silenzio.</summary>
    internal static string CleanTranscript(string raw)
    {
        var lines = raw.Replace("\r", "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => !IsNonSpeechMarker(line));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>SRT pulito: rimuove i blocchi vuoti o di silenzio e rinumera i sottotitoli da 1.</summary>
    internal static string CleanSrt(string raw)
    {
        var sb = new StringBuilder();
        int index = 0;
        foreach (string block in Regex.Split(raw.Replace("\r", "").Trim(), @"\n\s*\n"))
        {
            string[] lines = block.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            int timing = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timing < 0) continue;

            string[] text = lines.Skip(timing + 1).Where(line => !IsNonSpeechMarker(line)).ToArray();
            if (text.Length == 0) continue;

            index++;
            sb.Append(index.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(lines[timing]).Append("\r\n");
            foreach (string line in text) sb.Append(line).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  5b. AUDIO → WAV 16 kHz MONO PCM16 (ffmpeg oppure decoder nativo Windows)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Prepara l'audio nel formato richiesto da whisper.cpp.</summary>
internal static class WhisperAudio
{
    internal const int TargetRate = 16000;

    internal static async Task PrepareWav16kMonoAsync(
        string sourcePath, string wavPath, FlipSettings settings, CancellationToken cancellationToken)
    {
        if (IsWhisperReadyWav(sourcePath))
        {
            File.Copy(sourcePath, wavPath, overwrite: true);
            return;
        }

        string? ffmpeg = CliResolver.Resolve("ffmpeg", settings);
        if (ffmpeg != null)
        {
            var (exitCode, _, stderr) = await CliResolver.RunProcessAsync(
                ffmpeg,
                ["-y", "-i", sourcePath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath],
                Path.GetDirectoryName(wavPath), cancellationToken, timeoutSeconds: 1800).ConfigureAwait(false);
            if (exitCode == 0 && File.Exists(wavPath) && new FileInfo(wavPath).Length > 44) return;

            // Un WAV non decodificato da ffmpeg può ancora essere letto dal parser nativo qui sotto.
            if (!IsRiffWave(sourcePath))
                throw new IOException($"Decodifica audio FFmpeg fallita: {stderr.Trim()}");
        }

        await Task.Run(() =>
        {
            var (samples, rate) = IsRiffWave(sourcePath)
                ? ReadWavMono(sourcePath)
                : MediaFoundationAudio.DecodeMono(sourcePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            WriteWav16(wavPath, Resample(samples, rate, TargetRate), TargetRate);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsRiffWave(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            return stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) == 12 &&
                header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private readonly record struct WavFormat(int Tag, int Channels, int Rate, int Bits, long DataOffset, long DataLength);

    private static WavFormat? ReadWavFormat(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 44) return null;
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("RIFF"u8)) return null;
        reader.ReadUInt32();
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("WAVE"u8)) return null;

        int tag = 0, channels = 0, rate = 0, bits = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            byte[] id = reader.ReadBytes(4);
            long size = reader.ReadUInt32();
            long start = stream.Position;
            if (id.AsSpan().SequenceEqual("fmt "u8) && size >= 16)
            {
                tag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                // WAVE_FORMAT_EXTENSIBLE: il formato reale è nei primi 2 byte del SubFormat.
                if (tag == 0xFFFE && size >= 40)
                {
                    reader.ReadBytes(8);
                    tag = reader.ReadUInt16();
                }
            }
            else if (id.AsSpan().SequenceEqual("data"u8))
            {
                if (channels <= 0 || rate <= 0 || bits <= 0) return null;
                long available = stream.Length - start;
                // I file registrati in streaming dichiarano spesso 0 o 0xFFFFFFFF.
                long length = size == 0 || size > available ? available : size;
                return new WavFormat(tag, channels, rate, bits, start, length);
            }
            stream.Position = Math.Min(stream.Length, start + size + (size & 1));
        }
        return null;
    }

    internal static bool IsWhisperReadyWav(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ReadWavFormat(stream) is { Tag: 1, Channels: 1, Rate: TargetRate, Bits: 16, DataLength: > 0 };
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Legge un WAV PCM (8/16/24/32 bit) o float (32/64 bit) e lo miscela in mono.</summary>
    internal static (float[] Samples, int Rate) ReadWavMono(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        WavFormat format = ReadWavFormat(stream) ?? throw new IOException("File WAV non valido o privo di dati audio.");
        if (format.Tag is not (1 or 3))
            throw new IOException("WAV compresso non supportato senza FFmpeg: installa FFmpeg da Impostazioni > Motori & GPU.");

        int bytesPerSample = format.Bits / 8;
        if (bytesPerSample is < 1 or > 8 || (format.Tag == 3 && bytesPerSample is not (4 or 8)))
            throw new IOException($"Profondità WAV non supportata: {format.Bits} bit.");

        int frameSize = bytesPerSample * format.Channels;
        long frames = format.DataLength / frameSize;
        if (frames <= 0) throw new IOException("File WAV senza campioni audio.");
        if (frames > int.MaxValue / 2) throw new IOException("File WAV troppo lungo per il decoder nativo: installa FFmpeg.");

        var mono = new float[frames];
        byte[] buffer = new byte[frameSize * 4096];
        stream.Position = format.DataOffset;
        long done = 0;
        while (done < frames)
        {
            int wanted = (int)Math.Min(4096, frames - done) * frameSize;
            int read = stream.ReadAtLeast(buffer.AsSpan(0, wanted), wanted, throwOnEndOfStream: false);
            int got = read / frameSize;
            if (got == 0) break;
            MixToMono(buffer, got, format.Channels, format.Bits, format.Tag == 3, mono, (int)done);
            done += got;
        }
        if (done < frames) Array.Resize(ref mono, (int)done);
        return (mono, format.Rate);
    }

    /// <summary>Converte frame PCM interlacciati in campioni mono normalizzati [-1, 1].</summary>
    internal static void MixToMono(byte[] buffer, int frames, int channels, int bits, bool isFloat, float[] destination, int offset)
    {
        int bytesPerSample = bits / 8;
        int position = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0f;
            for (int channel = 0; channel < channels; channel++, position += bytesPerSample)
            {
                sum += (isFloat, bits) switch
                {
                    (true, 32) => BitConverter.ToSingle(buffer, position),
                    (true, 64) => (float)BitConverter.ToDouble(buffer, position),
                    (_, 8) => (buffer[position] - 128) / 128f,
                    (_, 16) => BitConverter.ToInt16(buffer, position) / 32768f,
                    (_, 24) => (buffer[position] | (buffer[position + 1] << 8) | ((sbyte)buffer[position + 2] << 16)) / 8388608f,
                    (_, 32) => BitConverter.ToInt32(buffer, position) / 2147483648f,
                    _ => 0f
                };
            }
            destination[offset + frame] = sum / channels;
        }
    }

    /// <summary>
    /// Ricampiona in mono: media pesata della finestra sorgente in discesa (anti-aliasing),
    /// interpolazione lineare in salita.
    /// </summary>
    internal static float[] Resample(float[] source, int sourceRate, int targetRate)
    {
        if (sourceRate <= 0) throw new IOException("Frequenza di campionamento non valida.");
        if (sourceRate == targetRate || source.Length == 0) return source;

        double ratio = (double)sourceRate / targetRate;
        int length = Math.Max(1, (int)Math.Floor(source.Length / ratio));
        var result = new float[length];

        if (ratio > 1)
        {
            for (int i = 0; i < length; i++)
            {
                double start = i * ratio, end = Math.Min(source.Length, start + ratio);
                int first = (int)start, last = Math.Min(source.Length - 1, (int)Math.Ceiling(end) - 1);
                double sum = 0, weight = 0;
                for (int s = first; s <= last; s++)
                {
                    double w = Math.Min(end, s + 1) - Math.Max(start, s);
                    if (w <= 0) continue;
                    sum += source[s] * w;
                    weight += w;
                }
                result[i] = weight > 0 ? (float)(sum / weight) : 0f;
            }
        }
        else
        {
            for (int i = 0; i < length; i++)
            {
                double position = i * ratio;
                int a = (int)position, b = Math.Min(source.Length - 1, a + 1);
                double t = position - a;
                result[i] = (float)(source[a] * (1 - t) + source[b] * t);
            }
        }
        return result;
    }

    internal static void WriteWav16(string path, float[] mono, int rate)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        int dataBytes = checked(mono.Length * 2);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (float sample in mono)
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
    }
}

/// <summary>
/// Decoder audio nativo di Windows (Media Foundation Source Reader) usato quando FFmpeg non è installato.
/// Supporta i codec presenti nel sistema: MP3, AAC/M4A, FLAC, WMA e, con le estensioni Web Media, OGG/Opus.
/// </summary>
internal static class MediaFoundationAudio
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint FlagError = 0x1;
    private const uint FlagEndOfStream = 0x2;
    private const uint FlagMediaTypeChanged = 0x20;

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid AudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    [System.Runtime.InteropServices.DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [System.Runtime.InteropServices.DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [System.Runtime.InteropServices.DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr mediaType);

    [System.Runtime.InteropServices.DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);

    // Slot delle vtable COM (IUnknown occupa 0-2).
    private const int ReaderSetStreamSelection = 4;
    private const int ReaderGetCurrentMediaType = 6;
    private const int ReaderSetCurrentMediaType = 7;
    private const int ReaderReadSample = 9;
    private const int AttributesGetUInt32 = 7;
    private const int AttributesSetGuid = 24;
    private const int SampleConvertToContiguousBuffer = 41;
    private const int BufferLock = 3;
    private const int BufferUnlock = 4;

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int SetStreamSelectionFn(IntPtr self, uint stream, int selected);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int GetCurrentMediaTypeFn(IntPtr self, uint stream, out IntPtr mediaType);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int SetCurrentMediaTypeFn(IntPtr self, uint stream, IntPtr reserved, IntPtr mediaType);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int ReadSampleFn(IntPtr self, uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IntPtr sample);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int GetUInt32Fn(IntPtr self, ref Guid key, out uint value);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int SetGuidFn(IntPtr self, ref Guid key, ref Guid value);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int ConvertToContiguousBufferFn(IntPtr self, out IntPtr buffer);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int BufferLockFn(IntPtr self, out IntPtr data, out uint maxLength, out uint currentLength);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate int BufferUnlockFn(IntPtr self);

    private static T Method<T>(IntPtr comObject, int slot) where T : Delegate
    {
        IntPtr vtable = System.Runtime.InteropServices.Marshal.ReadIntPtr(comObject);
        IntPtr function = System.Runtime.InteropServices.Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private static void Check(int hresult, string step)
    {
        if (hresult < 0)
            throw new IOException($"Decoder audio di Windows: {step} non riuscito (0x{hresult:X8}). Installa FFmpeg per supportare questo formato.");
    }

    private static void Release(ref IntPtr comObject)
    {
        if (comObject == IntPtr.Zero) return;
        System.Runtime.InteropServices.Marshal.Release(comObject);
        comObject = IntPtr.Zero;
    }

    /// <summary>Decodifica la prima traccia audio in campioni mono alla frequenza nativa del file.</summary>
    internal static (float[] Samples, int Rate) DecodeMono(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Il decoder audio nativo richiede Windows.");

        (float[] Samples, int Rate) result = default;
        Exception? failure = null;
        // Media Foundation richiede un apartment COM inizializzato: un thread MTA dedicato lo garantisce.
        var worker = new Thread(() =>
        {
            try { result = DecodeOnCurrentThread(Path.GetFullPath(path), cancellationToken); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true, Name = "FLIP-MF-Audio" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();

        if (failure is OperationCanceledException) throw new OperationCanceledException(cancellationToken);
        if (failure != null) throw new IOException(failure.Message, failure);
        return result;
    }

    private static (float[] Samples, int Rate) DecodeOnCurrentThread(string path, CancellationToken cancellationToken)
    {
        Check(MFStartup(MfVersion, MfStartupLite), "avvio di Media Foundation");
        IntPtr reader = IntPtr.Zero, requested = IntPtr.Zero, current = IntPtr.Zero;
        try
        {
            Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader), "apertura del file audio");

            var select = Method<SetStreamSelectionFn>(reader, ReaderSetStreamSelection);
            Check(select(reader, AllStreams, 0), "selezione delle tracce");
            Check(select(reader, FirstAudioStream, 1), "selezione della traccia audio");

            Check(MFCreateMediaType(out requested), "creazione del formato PCM");
            var setGuid = Method<SetGuidFn>(requested, AttributesSetGuid);
            Guid majorKey = MajorType, audio = MediaTypeAudio, subKey = SubType, pcm = AudioFormatPcm;
            Check(setGuid(requested, ref majorKey, ref audio), "impostazione del tipo audio");
            Check(setGuid(requested, ref subKey, ref pcm), "impostazione del formato PCM");
            Check(Method<SetCurrentMediaTypeFn>(reader, ReaderSetCurrentMediaType)(reader, FirstAudioStream, IntPtr.Zero, requested),
                "attivazione del decoder");

            var (channels, rate, bits) = ReadPcmFormat(reader, ref current);
            var readSample = Method<ReadSampleFn>(reader, ReaderReadSample);
            var samples = new List<float>(rate * 60);
            byte[] managed = [];
            float[] mixed = [];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Check(readSample(reader, FirstAudioStream, 0, out _, out uint flags, out _, out IntPtr sample), "lettura dei campioni");
                try
                {
                    if ((flags & FlagError) != 0) throw new IOException("Decoder audio di Windows: flusso audio danneggiato.");
                    if ((flags & FlagMediaTypeChanged) != 0)
                    {
                        var changed = ReadPcmFormat(reader, ref current);
                        if (changed.Rate != rate)
                            throw new IOException("Decoder audio di Windows: frequenza variabile non supportata. Installa FFmpeg.");
                        (channels, bits) = (changed.Channels, changed.Bits);
                    }

                    if (sample != IntPtr.Zero)
                    {
                        IntPtr buffer = IntPtr.Zero;
                        Check(Method<ConvertToContiguousBufferFn>(sample, SampleConvertToContiguousBuffer)(sample, out buffer), "lettura del buffer");
                        try
                        {
                            Check(Method<BufferLockFn>(buffer, BufferLock)(buffer, out IntPtr data, out _, out uint length), "blocco del buffer");
                            try
                            {
                                int frameSize = bits / 8 * channels;
                                int frames = frameSize > 0 ? (int)length / frameSize : 0;
                                if (frames > 0)
                                {
                                    if (managed.Length < frames * frameSize) managed = new byte[frames * frameSize];
                                    if (mixed.Length < frames) mixed = new float[frames];
                                    System.Runtime.InteropServices.Marshal.Copy(data, managed, 0, frames * frameSize);
                                    WhisperAudio.MixToMono(managed, frames, channels, bits, isFloat: false, mixed, 0);
                                    samples.AddRange(new ArraySegment<float>(mixed, 0, frames));
                                }
                            }
                            finally
                            {
                                Method<BufferUnlockFn>(buffer, BufferUnlock)(buffer);
                            }
                        }
                        finally
                        {
                            Release(ref buffer);
                        }
                    }

                    if ((flags & FlagEndOfStream) != 0) break;
                }
                finally
                {
                    Release(ref sample);
                }
            }

            if (samples.Count == 0) throw new IOException("Decoder audio di Windows: nessun campione audio nel file.");
            return (samples.ToArray(), rate);
        }
        finally
        {
            Release(ref current);
            Release(ref requested);
            Release(ref reader);
            MFShutdown();
        }
    }

    private static (int Channels, int Rate, int Bits) ReadPcmFormat(IntPtr reader, ref IntPtr current)
    {
        Release(ref current);
        Check(Method<GetCurrentMediaTypeFn>(reader, ReaderGetCurrentMediaType)(reader, FirstAudioStream, out current), "lettura del formato decodificato");
        var getUInt32 = Method<GetUInt32Fn>(current, AttributesGetUInt32);
        Guid channelsKey = AudioChannels, rateKey = AudioSamplesPerSecond, bitsKey = AudioBitsPerSample;
        Check(getUInt32(current, ref channelsKey, out uint channels), "lettura dei canali");
        Check(getUInt32(current, ref rateKey, out uint rate), "lettura della frequenza");
        Check(getUInt32(current, ref bitsKey, out uint bits), "lettura della profondità");
        if (channels is 0 or > 32 || rate == 0 || bits is not (8 or 16 or 24 or 32))
            throw new IOException("Decoder audio di Windows: formato PCM non supportato.");
        return ((int)channels, (int)rate, (int)bits);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  5c. WHISPER LOCATOR ("ZERO-DUPLICATI": RIUSO C++ E MODELLI LOCALI)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Ricerca intelligente con cache per individuare installazioni e modelli Whisper già presenti nelle
/// directory applicative standard (AppData, LocalAppData, Program Files), evitando download duplicati
/// ed escludendo gli ambienti Python virtuali o embedded.
/// </summary>
public static class WhisperLocator
{
    internal const long MinimumRealModelBytes = 30L * 1024 * 1024;
    private const uint GgmlMagic = 0x67676d6c;

    internal sealed record Discovery(string[] Executables, string[] Models)
    {
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> ExtraFolders =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class DiscoveryCache(Func<Discovery> scan, Func<long> ticks)
    {
        readonly Lock gate = new();
        Discovery? snapshot;
        long expires;

        internal Discovery Get()
        {
            lock (gate)
            {
                if (snapshot != null && ticks() < expires) return snapshot;
                snapshot = scan();
                expires = ticks() + 45_000;
                return snapshot;
            }
        }

        internal void Invalidate() { lock (gate) snapshot = null; }
    }

    static readonly DiscoveryCache Cache = new(Scan, () => Environment.TickCount64);

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "ProgramData", "$Recycle.Bin", "System Volume Information",
        "node_modules", ".git", ".vs", "obj", "packages", "__pycache__",
        "python_embeded", "venv", ".venv", "conda"
    };

    private static readonly string[] KeywordTokens =
    [
        "whisper", "whisper.cpp", "faster-whisper"
    ];

    private static readonly string[] ModelPriorityPrefixes =
    [
        "ggml-large-v3-turbo",
        "ggml-large-v3",
        "ggml-large",
        "ggml-medium",
        "ggml-small",
        "ggml-base",
        "ggml-tiny"
    ];

    public static void InvalidateCache() => Cache.Invalidate();

    /// <summary>True se il percorso appartiene a un ambiente Python embedded o virtuale da escludere.</summary>
    public static bool IsExcludedWhisperExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = path.Replace('/', '\\');
        return normalized.Contains("python_embeded", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\venv\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\.venv\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\conda\", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(@"\Scripts\whisper.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifica che un file modello .bin sia valido (dimensione >= 30 MB e non uno stub di test).</summary>
    public static bool IsUsableModelFile(string path, long minBytes = MinimumRealModelBytes)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            string name = Path.GetFileName(path);
            if (name.StartsWith("for-tests-", StringComparison.OrdinalIgnoreCase)) return false;

            var info = new FileInfo(path);
            if (info.Length < minBytes) return false;

            using var stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) != 4) return false;
            uint header = BitConverter.ToUInt32(magic);
            // Accetta header ggml standard (0x67676d6c, 0x67676d66, 0x67676a74) o file modello > 30 MB
            return header is GgmlMagic or 0x67676d66u or 0x67676a74u || info.Length >= minBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static string? FindBestExecutable(IEnumerable<string>? extraFolders = null)
    {
        if (StoreEdition.BundledEngine("whisper") is { } bundled) return bundled;
        var snapshot = Cache.Get();
        var candidates = new List<string>(snapshot.Executables);

        if (extraFolders != null)
        {
            foreach (string folder in extraFolders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || IsExcludedWhisperExecutable(folder))
                    continue;
                candidates.AddRange(snapshot.ExtraFolders.GetOrAdd(folder, static path =>
                {
                    var found = new List<string>();
                    CollectExecutablesInDirectory(path, found);
                    return found.ToArray();
                }));
            }
        }

        return candidates
            .Where(path => File.Exists(path) && !IsExcludedWhisperExecutable(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: Path.GetFullPath(path), Score: ScoreExecutable(path)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    public static string? FindGlobalModel(string? requested = null)
    {
        var models = new List<string>(Cache.Get().Models);

        try
        {
            var customPaths = SettingsStore.Current.CreateSnapshot().CustomEnginePaths;
            if (customPaths.TryGetValue("whisper_model", out string? customModel) && !string.IsNullOrWhiteSpace(customModel))
            {
                string expanded = Environment.ExpandEnvironmentVariables(customModel.Trim().Trim('"'));
                if (Path.IsPathFullyQualified(expanded) && File.Exists(expanded) && IsUsableModelFile(expanded))
                    models.Insert(0, Path.GetFullPath(expanded));
            }
        }
        catch { /* best effort */ }

        // Aggiungi sempre il percorso gestito da FLIP se presente e valido
        if (File.Exists(WhisperModuleInstaller.ModelPath) && IsUsableModelFile(WhisperModuleInstaller.ModelPath))
        {
            models.Insert(0, WhisperModuleInstaller.ModelPath);
        }

        var validModels = models
            .Where(m => File.Exists(m) && IsUsableModelFile(m))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validModels.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            string reqFile = Path.GetFileName(requested.Trim());
            string? exact = validModels.FirstOrDefault(m =>
                Path.GetFileName(m).Equals(reqFile, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        return validModels
            .OrderByDescending(ScoreModel)
            .FirstOrDefault();
    }

    private static int ScoreExecutable(string path)
    {
        if (IsExcludedWhisperExecutable(path)) return 0;
        string name = Path.GetFileName(path);
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return 0;

        bool hasLocalDlls = File.Exists(Path.Combine(dir, "whisper.dll")) && File.Exists(Path.Combine(dir, "ggml.dll"));
        bool hasParentDlls = false;
        if (!hasLocalDlls)
        {
            DirectoryInfo? p = Directory.GetParent(dir);
            for (int i = 0; i < 4 && p != null; i++, p = p.Parent)
            {
                if (File.Exists(Path.Combine(p.FullName, "ggml.dll")))
                {
                    hasParentDlls = true;
                    break;
                }
            }
        }

        bool isFlipManaged = dir.StartsWith(WhisperModuleInstaller.InstallDirectory, StringComparison.OrdinalIgnoreCase);

        if (name.Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
        {
            int score = hasLocalDlls ? 1000 : hasParentDlls ? 950 : 880;
            if (isFlipManaged) score += 40;
            return score;
        }

        if (name.Equals("main.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!path.Contains("whisper", StringComparison.OrdinalIgnoreCase)) return 0;
                long len = new FileInfo(path).Length;
                if (len < 60_000 && !hasLocalDlls) return 0; // esclude piccoli stub di build
                return hasLocalDlls ? 820 : 750;
            }
            catch { return 0; }
        }

        if (name.Equals("faster-whisper-xxl.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("whisper-faster.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("faster-whisper.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 700;
        }

        if (name.Equals("whisper.exe", StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        return 0;
    }

    private static int ScoreModel(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        int rank = 100;
        for (int i = 0; i < ModelPriorityPrefixes.Length; i++)
        {
            if (name.StartsWith(ModelPriorityPrefixes[i], StringComparison.OrdinalIgnoreCase))
            {
                rank = 1000 - i * 100;
                break;
            }
        }

        // Preferisci modelli multilingua rispetto a quelli ".en.bin"
        if (!name.Contains(".en.", StringComparison.OrdinalIgnoreCase))
            rank += 40;

        if (path.StartsWith(WhisperModuleInstaller.InstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            rank += 20;
        }

        return rank;
    }

    private static Discovery Scan()
    {
        var foundExes = new List<string>();
        var foundModels = new List<string>();

        foreach (string seed in GetPrioritySeedDirectories())
        {
            ScanDirectoryTree(seed, maxDepth: 4, foundExes, foundModels);
        }

        foreach (string searchRoot in GetTopLevelSearchRoots())
        {
            ScanMatchingSubfolders(searchRoot, foundExes, foundModels);
        }

        return new(foundExes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            foundModels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<string> GetPrioritySeedDirectories()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return WhisperModuleInstaller.InstallDirectory;
        if (StoreEdition.GitHubEngines is { } shared) yield return Path.Combine(shared, "Whisper");

        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return Path.Combine(local, "Programs", "Flip");
            yield return Path.Combine(local, "Flip", "tools");
            yield return Path.Combine(local, "Whisper");
            yield return Path.Combine(local, "whisper.cpp");
        }

        if (!string.IsNullOrWhiteSpace(roaming))
        {
            yield return Path.Combine(roaming, "Whisper");
            yield return Path.Combine(roaming, "whisper.cpp");
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            yield return Path.Combine(profile, ".unsloth", "whisper.cpp");
            yield return Path.Combine(profile, ".cache", "whisper");
        }
    }

    private static IEnumerable<string> GetTopLevelSearchRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrWhiteSpace(local))
        {
            yield return local;
            yield return Path.Combine(local, "Programs");
        }
        if (!string.IsNullOrWhiteSpace(roaming))
            yield return roaming;
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return programFiles;
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return programFilesX86;
    }

    private static void ScanMatchingSubfolders(string root, List<string> exes, List<string> models)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(root))
            {
                string dirName = Path.GetFileName(subDir);
                if (SkippedDirectories.Contains(dirName)) continue;

                if (MatchesWhisperKeyword(dirName))
                {
                    ScanDirectoryTree(subDir, maxDepth: 4, exes, models);
                }
            }
        }
        catch { /* ignora errori di accesso su root */ }
    }

    private static bool MatchesWhisperKeyword(string folderName)
    {
        foreach (string token in KeywordTokens)
        {
            if (folderName.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void ScanDirectoryTree(string directory, int maxDepth, List<string> exes, List<string> models)
    {
        if (maxDepth < 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        if (IsExcludedWhisperExecutable(directory)) return;

        CollectExecutablesInDirectory(directory, exes);
        CollectModelsInDirectory(directory, models);

        if (maxDepth == 0) return;

        try
        {
            foreach (string sub in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(sub);
                if (SkippedDirectories.Contains(name)) continue;
                ScanDirectoryTree(sub, maxDepth - 1, exes, models);
            }
        }
        catch { /* ignora sottocartelle non accessibili */ }
    }

    private static void CollectExecutablesInDirectory(string directory, List<string> exes)
    {
        try
        {
            string[] names = ["whisper-cli.exe", "faster-whisper-xxl.exe", "whisper-faster.exe", "faster-whisper.exe", "main.exe"];
            foreach (string exeName in names)
            {
                string candidate = Path.Combine(directory, exeName);
                if (File.Exists(candidate) && !IsExcludedWhisperExecutable(candidate))
                    exes.Add(candidate);
            }
        }
        catch { }
    }

    private static void CollectModelsInDirectory(string directory, List<string> models)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "ggml-*.bin"))
            {
                if (IsUsableModelFile(file))
                    models.Add(file);
            }
        }
        catch { }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  5d. INSTALLER 1-CLICK DEL MODULO WHISPER AI (whisper.cpp + ggml-base)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Avanzamento dell'installazione: frazione 0-1 e messaggio per l'Hub.</summary>
public readonly record struct WhisperInstallProgress(double Fraction, string Status);

/// <summary>
/// Scarica in %AppData%\FLIP\Engines\Whisper la build ufficiale Windows x64 di whisper.cpp
/// e il modello multilingua ggml-base, poi registra il percorso nelle impostazioni.
/// </summary>
public static class WhisperModuleInstaller
{
    internal const string RuntimeAssetName = "whisper-bin-x64.zip";
    internal const string ExecutableName = "whisper-cli.exe";
    internal const string ModelFileName = "ggml-base.bin";
    private const string ReleasesApi = "https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=15";
    private const string ReleaseDownloadPrefix = "https://github.com/ggml-org/whisper.cpp/releases/download/";
    private const string FallbackRuntimeUrl = ReleaseDownloadPrefix + "v1.9.2/" + RuntimeAssetName;
    private const string FallbackRuntimeSha256 = "49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a";
    private const string ModelSha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelFileName;
    private const long MinimumModelBytes = 100L * 1024 * 1024;
    private const uint GgmlMagic = 0x67676d6c;

    internal static readonly Lazy<System.Net.Http.HttpClient> Http = new(() =>
    {
        var client = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Flip-Desktop-Converter/1.0");
        return client;
    });

    public static string InstallDirectory { get; } = Path.Combine(SettingsStore.DataDirectory, "Engines", "Whisper");
    public static string ExecutablePath => Path.Combine(InstallDirectory, ExecutableName);
    public static string ModelPath => Path.Combine(InstallDirectory, ModelFileName);

    /// <summary>True quando un eseguibile Whisper e un modello valido sono presenti e pronti sul PC.</summary>
    public static bool IsInstalled()
    {
        string? exe = WhisperLocator.FindBestExecutable();
        if (exe != null && LocalAiEngine.IsWhisperCpp(exe) && LocalAiEngine.FindModel(exe) != null)
            return true;
        return File.Exists(ExecutablePath) && LocalAiEngine.FindModel(ExecutablePath) != null;
    }

    internal static bool IsValidModel(string path) =>
        WhisperLocator.IsUsableModelFile(path, MinimumModelBytes);

    /// <summary>
    /// Sceglie dalla lista delle release GitHub la più recente che pubblica la build Windows x64.
    /// Le release "vX.Y.Z" recenti non hanno allegati: i binari sono nelle build "bNNNN".
    /// </summary>
    internal static (string Url, string? Sha256)? SelectRuntimeAsset(string releasesJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == System.Text.Json.JsonValueKind.True) continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != System.Text.Json.JsonValueKind.Array) continue;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!asset.TryGetProperty("name", out var name) || name.GetString() != RuntimeAssetName) continue;
                if (!asset.TryGetProperty("browser_download_url", out var link) || link.GetString() is not { } url) continue;
                if (!url.StartsWith(ReleaseDownloadPrefix, StringComparison.Ordinal)) continue;

                string? sha256 = asset.TryGetProperty("digest", out var digest) &&
                    digest.ValueKind == System.Text.Json.JsonValueKind.String &&
                    digest.GetString() is { } value && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? value["sha256:".Length..]
                        : null;
                // Senza impronta pubblicata il file non si può verificare.
                if (sha256 == null) continue;
                return (url, sha256);
            }
        }
        return null;
    }

    /// <summary>Estrae solo whisper-cli.exe e le DLL di runtime, ignorando le cartelle dello zip.</summary>
    internal static void ExtractRuntime(string zipPath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        bool foundExecutable = false;
        foreach (var entry in archive.Entries)
        {
            // Path.GetFileName scarta ogni componente di percorso: nessuna scrittura fuori cartella.
            string name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (name.Length == 0) continue;
            bool isExecutable = name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase);
            if (!isExecutable && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            string destination = Path.Combine(targetDirectory, name);
            string staged = destination + ".part";
            using (var input = entry.Open())
            using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            File.Move(staged, destination, overwrite: true);
            foundExecutable |= isExecutable;
        }

        if (!foundExecutable)
            throw new IOException($"Il pacchetto whisper.cpp non contiene {ExecutableName}.");
    }

    /// <summary>
    /// Installa (o ripara) solo i componenti mancanti (Zero-Duplicati):
    /// - Se sul computer esiste già un modello ggml-*.bin valido, NON riscarica i 142 MB di ggml-base.bin.
    /// - Se manca whisper-cli.exe, scarica solo lo zip leggero del runtime (~2 MB).
    /// Salva il percorso in FlipSettings.CustomEnginePaths["whisper"] e restituisce l'eseguibile attivo.
    /// </summary>
    public static async Task<string> InstallAsync(
        IProgress<WhisperInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(InstallDirectory);
        WhisperLocator.InvalidateCache();

        string? existingExe = WhisperLocator.FindBestExecutable();
        bool hasNativeCppExe = existingExe != null && LocalAiEngine.IsWhisperCpp(existingExe) && File.Exists(existingExe);

        if (!File.Exists(ExecutablePath) && !hasNativeCppExe)
        {
            progress?.Report(new(0.02, Loc.T("Ricerca della build ufficiale whisper.cpp…", "Looking up official whisper.cpp build…")));
            var (url, sha256) = await ResolveRuntimeAssetAsync(cancellationToken).ConfigureAwait(false);
            string zipPath = Path.Combine(InstallDirectory, RuntimeAssetName + ".download");
            try
            {
                await DownloadAsync(url, zipPath, sha256,
                    (done, total) => progress?.Report(new(0.04 + 0.14 * Fraction(done, total), Loc.F("Download motore whisper.cpp… {0}", "Downloading whisper.cpp engine… {0}", FormatMb(done, total)))),
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(new(0.19, Loc.T("Estrazione del motore…", "Extracting engine…")));
                ExtractRuntime(zipPath, InstallDirectory);
            }
            finally
            {
                TryDelete(zipPath);
            }
        }

        string activeExecutable = File.Exists(ExecutablePath)
            ? ExecutablePath
            : (existingExe ?? ExecutablePath);

        WhisperLocator.InvalidateCache();
        string? existingModel = LocalAiEngine.FindModel(activeExecutable);

        if (string.IsNullOrWhiteSpace(existingModel) && !IsValidModel(ModelPath))
        {
            string staged = ModelPath + ".download";
            try
            {
                await DownloadAsync(ModelUrl, staged, ModelSha256,
                    (done, total) => progress?.Report(new(0.20 + 0.78 * Fraction(done, total), Loc.F("Download modello multilingua {0}… {1}", "Downloading multilingual model {0}… {1}", ModelFileName, FormatMb(done, total)))),
                    cancellationToken).ConfigureAwait(false);
                if (!IsValidModel(staged))
                    throw new IOException(Loc.T("Il modello scaricato non è un file ggml valido. Riprova più tardi.", "The downloaded model is not a valid ggml file. Please try again later."));
                File.Move(staged, ModelPath, overwrite: true);
            }
            finally
            {
                TryDelete(staged);
            }
        }
        else if (!string.IsNullOrWhiteSpace(existingModel))
        {
            progress?.Report(new(0.95, Loc.F("Modello già presente su disco ({0}) — nessun download duplicato.", "Model already on disk ({0}) — skipping duplicate download.", Path.GetFileName(existingModel))));
        }

        WhisperLocator.InvalidateCache();
        if (StoreEdition.BundledEngine("whisper") == null)
            SettingsStore.Current.Update(settings => settings.CustomEnginePaths["whisper"] = activeExecutable);
        progress?.Report(new(1.0, Loc.T("Attivo (Locale)", "Active (Local)")));
        return activeExecutable;
    }

    private static async Task<(string Url, string? Sha256)> ResolveRuntimeAssetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, ReleasesApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await Http.Value.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (SelectRuntimeAsset(json) is { } asset) return asset;
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Evidence.Log("whisper.install.release-lookup", ex.Message);
        }

        // API GitHub non raggiungibile o limitata: usa l'ultima release nota con i binari allegati.
        return (FallbackRuntimeUrl, FallbackRuntimeSha256);
    }

    internal static async Task DownloadAsync(
        string url, string destination, string? expectedSha256, Action<long, long?> onProgress, CancellationToken cancellationToken)
    {
        using var response = await Http.Value.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new IOException(Loc.F("Download non riuscito ({0}) da {1}.", "Download failed ({0}) from {1}.", (int)response.StatusCode, new Uri(url).Host));

        long? total = response.Content.Headers.ContentLength;
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long done = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true))
        {
            byte[] buffer = new byte[1 << 17];
            long lastReport = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (done - lastReport >= 512 * 1024)
                {
                    lastReport = done;
                    onProgress(done, total);
                }
            }
        }

        if (total is long expected && done != expected)
            throw new IOException(Loc.T("Download interrotto: il file ricevuto è incompleto.", "Download interrupted: the received file is incomplete."));
        if (expectedSha256 != null &&
            !Convert.ToHexString(sha.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("Verifica SHA-256 non superata: il pacchetto scaricato è stato scartato.", "SHA-256 verification failed: downloaded package discarded."));
        onProgress(done, total);
    }

    internal static double Fraction(long done, long? total) =>
        total is > 0 ? Math.Clamp((double)done / total.Value, 0, 1) : 0;

    internal static string FormatMb(long done, long? total) => total is > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{done / 1048576.0:F0} / {total.Value / 1048576.0:F0} MB")
        : string.Create(CultureInfo.InvariantCulture, $"{done / 1048576.0:F0} MB");

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  5e. INSTALLER UNIFICATO 1-CLICK PER TUTTI I 6 MOTORI ESTERNI
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Installer reale 1-Click per tutti i 6 motori esterni di FLIP:
/// Whisper AI, Pandoc, FFmpeg, Tesseract OCR, Calibre e LibreOffice.
/// </summary>
public static class EngineInstaller
{
    private const string PandocReleasesApi = "https://api.github.com/repos/jgm/pandoc/releases/latest";
    private const string PandocDownloadPrefix = "https://github.com/jgm/pandoc/releases/download/";
    private const string PandocFallbackUrl = "https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-windows-x86_64.zip";
    private const string PandocFallbackSha256 = "6ef431cb20b2c24a0371fc19200dc49d452be54b1b5c9239f3462841ef53e587";

    private const string FfmpegGyanUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string FfmpegBtbnUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private const string FfmpegBtbnChecksumsUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256";

    private static readonly Dictionary<string, string> WinGetPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ffmpeg"] = "Gyan.FFmpeg",
        ["tesseract"] = "UB-Mannheim.TesseractOCR",
        ["calibre"] = "calibre.calibre",
        ["libreoffice"] = "TheDocumentFoundation.LibreOffice"
    };

    public static string? GetWinGetPackageId(string engineId) =>
        WinGetPackages.TryGetValue(engineId ?? "", out var pkg) ? pkg : null;

    public static async Task<string> InstallAsync(
        string engineId,
        IProgress<WhisperInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        string id = engineId.Trim().ToLowerInvariant();

        return id switch
        {
            "whisper" => await WhisperModuleInstaller.InstallAsync(progress, cancellationToken).ConfigureAwait(false),
            "pandoc" => await InstallPandocAsync(progress, cancellationToken).ConfigureAwait(false),
            "ffmpeg" => await InstallFfmpegAsync(progress, cancellationToken).ConfigureAwait(false),
            "tesseract" or "calibre" or "libreoffice" =>
                await InstallViaWinGetAsync(WinGetPackages[id], id, progress, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(Loc.F("Motore non supportato dall'installer 1-Click: {0}", "Engine not supported by 1-Click installer: {0}", engineId))
        };
    }

    internal static (string Url, string? Sha256)? SelectPandocWindowsAsset(string releaseJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(releaseJson);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (!asset.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { } name) continue;
            if (!name.EndsWith("-windows-x86_64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!asset.TryGetProperty("browser_download_url", out var urlEl) || urlEl.GetString() is not { } url) continue;
            if (!url.StartsWith(PandocDownloadPrefix, StringComparison.Ordinal)) continue;

            string? sha256 = asset.TryGetProperty("digest", out var digest) &&
                digest.ValueKind == System.Text.Json.JsonValueKind.String &&
                digest.GetString() is { } val && val.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? val["sha256:".Length..]
                    : null;
            if (sha256 == null) continue;
            return (url, sha256);
        }
        return null;
    }

    internal static string ExtractBinariesFlat(string zipPath, string targetDirectory, params string[] requiredExecutables)
    {
        Directory.CreateDirectory(targetDirectory);
        string? primaryFound = null;
        var wanted = new HashSet<string>(requiredExecutables, StringComparer.OrdinalIgnoreCase);

        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            string fileName = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (fileName.Length == 0) continue;
            bool isWantedExe = wanted.Contains(fileName);
            if (!isWantedExe && !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            string dest = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
            string fullRoot = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Loc.T("Percorso archivio non valido.", "Invalid archive path."));

            string staged = dest + ".part";
            using (var input = entry.Open())
            using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            File.Move(staged, dest, overwrite: true);

            if (isWantedExe && (primaryFound == null || fileName.Equals(requiredExecutables[0], StringComparison.OrdinalIgnoreCase)))
                primaryFound = dest;
        }

        if (primaryFound == null || !File.Exists(primaryFound))
            throw new IOException(Loc.F("L'archivio scaricato non contiene {0}.", "The downloaded archive does not contain {0}.", requiredExecutables[0]));
        return primaryFound;
    }

    private static async Task<string> InstallPandocAsync(
        IProgress<WhisperInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string installDir = Path.Combine(SettingsStore.DataDirectory, "Engines", "Pandoc");
        Directory.CreateDirectory(installDir);
        progress?.Report(new(0.05, Loc.T("Ricerca ultima release Pandoc Windows x64…", "Looking up latest Pandoc Windows x64 release…")));

        var (url, sha256) = await ResolvePandocAssetAsync(cancellationToken).ConfigureAwait(false);
        string zipPath = Path.Combine(installDir, "pandoc-windows-x86_64.zip.download");
        try
        {
            await WhisperModuleInstaller.DownloadAsync(
                url,
                zipPath,
                sha256,
                (done, total) => progress?.Report(new(
                    0.08 + 0.78 * WhisperModuleInstaller.Fraction(done, total),
                    Loc.F("Download Pandoc… {0}", "Downloading Pandoc… {0}", WhisperModuleInstaller.FormatMb(done, total)))),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new(0.90, Loc.T("Estrazione pandoc.exe…", "Extracting pandoc.exe…")));
            string exePath = ExtractBinariesFlat(zipPath, installDir, "pandoc.exe");
            SettingsStore.Current.Update(s => s.CustomEnginePaths["pandoc"] = exePath);
            DocumentTools.Refresh();
            progress?.Report(new(1.0, Loc.T("● PRONTO", "● READY")));
            return exePath;
        }
        finally
        {
            WhisperModuleInstaller.TryDelete(zipPath);
        }
    }

    private static async Task<(string Url, string? Sha256)> ResolvePandocAssetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, PandocReleasesApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await WhisperModuleInstaller.Http.Value.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                if (SelectPandocWindowsAsset(json) is { } asset) return asset;
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Text.Json.JsonException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Evidence.Log("pandoc.install.release-lookup", ex.Message);
        }
        return (PandocFallbackUrl, PandocFallbackSha256);
    }

    private static async Task<string> InstallFfmpegAsync(
        IProgress<WhisperInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string installDir = Path.Combine(SettingsStore.DataDirectory, "Engines", "FFmpeg");
        Directory.CreateDirectory(installDir);
        string zipPath = Path.Combine(installDir, "ffmpeg-essentials.zip.download");

        Exception? downloadError = null;
        foreach (string url in new[] { FfmpegGyanUrl, FfmpegBtbnUrl })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(new(0.05, Loc.F("Download FFmpeg portatile ({0})…", "Downloading portable FFmpeg ({0})…", new Uri(url).Host)));
                string checksum = await ResolveFfmpegChecksumAsync(url, cancellationToken).ConfigureAwait(false);
                await WhisperModuleInstaller.DownloadAsync(
                    url,
                    zipPath,
                    checksum,
                    (done, total) => progress?.Report(new(
                        0.06 + 0.80 * WhisperModuleInstaller.Fraction(done, total),
                        Loc.F("Download FFmpeg… {0}", "Downloading FFmpeg… {0}", WhisperModuleInstaller.FormatMb(done, total)))),
                    cancellationToken).ConfigureAwait(false);

                progress?.Report(new(0.90, Loc.T("Estrazione ffmpeg.exe + ffprobe.exe…", "Extracting ffmpeg.exe + ffprobe.exe…")));
                string exePath = ExtractBinariesFlat(zipPath, installDir, "ffmpeg.exe", "ffprobe.exe");
                SettingsStore.Current.Update(s => s.CustomEnginePaths["ffmpeg"] = exePath);
                MediaTools.Refresh();
                progress?.Report(new(1.0, Loc.T("● PRONTO", "● READY")));
                return exePath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                downloadError = ex;
                WhisperModuleInstaller.TryDelete(zipPath);
            }
        }

        // Fallback automatico tramite WinGet (Gyan.FFmpeg)
        progress?.Report(new(0.25, Loc.T("Download diretto non riuscito, avvio fallback WinGet (Gyan.FFmpeg)…", "Direct download failed, falling back to WinGet (Gyan.FFmpeg)…")));
        try
        {
            return await InstallViaWinGetAsync("Gyan.FFmpeg", "ffmpeg", progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception wingetEx)
        {
            throw new IOException(Loc.F("Installazione FFmpeg non riuscita: {0}", "FFmpeg installation failed: {0}", downloadError?.Message ?? wingetEx.Message), wingetEx);
        }
    }

    /// <summary>Impronta pubblicata accanto al pacchetto: senza, il download non viene usato.</summary>
    private static async Task<string> ResolveFfmpegChecksumAsync(string url, CancellationToken cancellationToken)
    {
        string checksumUrl = url == FfmpegBtbnUrl ? FfmpegBtbnChecksumsUrl : url + ".sha256";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string text = await WhisperModuleInstaller.Http.Value.GetStringAsync(checksumUrl, timeout.Token).ConfigureAwait(false);
        return ParseFfmpegChecksum(text, url[(url.LastIndexOf('/') + 1)..])
            ?? throw new IOException(Loc.T("Impronta SHA-256 di FFmpeg non disponibile: download annullato.", "FFmpeg SHA-256 checksum unavailable: download cancelled."));
    }

    internal static string? ParseFfmpegChecksum(string text, string fileName)
    {
        string single = text.Trim();
        if (Regex.IsMatch(single, @"\A[0-9a-fA-F]{64}\z")) return single;
        return UpdateInstaller.ParseChecksumFile(text, fileName);
    }

    private static async Task<string> InstallViaWinGetAsync(
        string packageId,
        string engineId,
        IProgress<WhisperInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(0.08, Loc.F("Avvio WinGet per {0}…", "Launching WinGet for {0}…", packageId)));
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(winget)) throw new IOException(Loc.T("WinGet non è disponibile su questo PC.", "WinGet is not available on this PC."));
        var startInfo = new ProcessStartInfo(winget)
        {
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[]
        {
            "install", "--id", packageId, "-e", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var outputTail = new StringBuilder();
        double lastFrac = 0.12;

        void HandleLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (outputTail)
            {
                if (outputTail.Length > 2000) outputTail.Remove(0, outputTail.Length - 1200);
                outputTail.AppendLine(line);
            }
            string clean = line.Trim();
            if (clean.Contains('%'))
            {
                var m = Regex.Match(clean, @"(\d{1,3})\s*%");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int pct) && pct is >= 0 and <= 100)
                {
                    lastFrac = Math.Clamp(0.15 + 0.72 * (pct / 100.0), lastFrac, 0.88);
                    progress?.Report(new(lastFrac, Loc.F("Installazione {0} via WinGet… {1}%", "Installing {0} via WinGet… {1}%", packageId, pct)));
                }
            }
            else if (clean.Length > 4 && !clean.All(c => c is '-' or '\\' or '|' or '/'))
            {
                lastFrac = Math.Min(0.86, lastFrac + 0.03);
                string summary = clean.Length > 70 ? clean[..67] + "…" : clean;
                progress?.Report(new(lastFrac, summary));
            }
        }

        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);

        try
        {
            if (!process.Start())
                throw new IOException(Loc.T("Impossibile avviare WinGet.", "Unable to launch WinGet."));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException(Loc.T(
                "WinGet non è disponibile su questo sistema. Usa il pulsante Sfoglia o installa manualmente il pacchetto.",
                "WinGet is not available on this system. Use Browse or install the package manually."), ex);
        }

        try { process.StandardInput.Close(); } catch { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }

        // -1978335189 (0x8A15002B) = pacchetto già installato e aggiornato secondo WinGet
        const int WinGetNoApplicableUpgrade = unchecked((int)0x8A15002B);
        progress?.Report(new(0.92, Loc.F("Verifica eseguibile {0} su disco…", "Verifying {0} executable on disk…", engineId)));
        CliResolver.VerifyAndDetectEngines(SettingsStore.Current);
        string? resolved = CliResolver.Resolve(engineId);

        if (resolved == null && process.ExitCode != 0 && process.ExitCode != WinGetNoApplicableUpgrade)
        {
            string logText;
            lock (outputTail) logText = outputTail.ToString().Trim();
            throw new IOException(Loc.F("WinGet ha restituito codice {0} per {1}: {2}", "WinGet returned code {0} for {1}: {2}", process.ExitCode, packageId, logText));
        }

        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
            throw new IOException(Loc.F(
                "Installazione di {0} completata ma l'eseguibile di '{1}' non è ancora visibile. Riavvia FLIP o usa 'Sfoglia...'.",
                "{0} installed, but '{1}' executable is not visible yet. Restart FLIP or use 'Browse...'.",
                packageId, engineId));

        SettingsStore.Current.Update(s => s.CustomEnginePaths[engineId] = resolved);
        progress?.Report(new(1.0, Loc.T("● PRONTO", "● READY")));
        return resolved;
    }
}
