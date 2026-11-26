using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flip.Core;

namespace Flip;

/// <summary>A stable GitHub release of FLIP with a package that can be verified before it is installed.</summary>
internal sealed record UpdatePackage(string TagName, string Version, string Name, string HtmlUrl,
    string ZipName, string ZipUrl, long ZipSize, string? ZipSha256, string? ChecksumUrl);

/// <summary>
/// In-app update: downloads the official release ZIP, verifies it, then hands over to the package's own
/// Install.ps1 (SHA256SUMS check, backup, verified copy) after FLIP has exited, and restarts FLIP.
/// </summary>
internal static class UpdateInstaller
{
    const string LatestReleaseApi = "https://api.github.com/repos/thetinyfoundry/FLIP/releases/latest";
    internal const string DownloadPrefix = "https://github.com/thetinyfoundry/FLIP/releases/download/";
    const long MaxPackageBytes = 512L * 1024 * 1024;
    const int MaxChecksumBytes = 4096;
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", RegexOptions.CultureInvariant);
    static readonly Regex Sha256Pattern = new(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    /// <summary>Set by the tray host so the updater can close FLIP cleanly before the installer runs.</summary>
    internal static Action? RequestExit;

    /// <summary>True while an update is being downloaded or handed to the installer.</summary>
    internal static volatile bool Busy;

    /// <summary>Set by the tray host: true while a conversion is running, so an update never interrupts it.</summary>
    internal static Func<bool>? ConversionRunning;

    internal static string UpdatesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flip", "updates");

    static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Flip-Desktop-Converter/1.0");
        return client;
    }

    /// <summary>
    /// Picks the Windows package from a GitHub "release" JSON object. Returns null for drafts, pre-releases,
    /// unexpected tags, assets hosted outside this repository's releases, or packages without any SHA256 to check.
    /// </summary>
    internal static UpdatePackage? SelectPackage(string releaseJson)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;

        string tag = GetString(root, "tag_name").Trim();
        string version = tag.TrimStart('v', 'V');
        if (!VersionPattern.IsMatch(version)) return null;
        string zipName = $"Flip-{version}-win-x64.zip";
        string expectedZipUrl = DownloadPrefix + tag + "/" + zipName;
        string expectedChecksumUrl = expectedZipUrl + ".sha256";

        string? zipUrl = null, digest = null, checksumUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = GetString(asset, "name");
                string url = GetString(asset, "browser_download_url");
                if (name == zipName && url == expectedZipUrl)
                {
                    zipUrl = url;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                    string raw = GetString(asset, "digest");
                    if (raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && Sha256Pattern.IsMatch(raw[7..]))
                        digest = raw[7..].ToLowerInvariant();
                }
                else if (name == zipName + ".sha256" && url == expectedChecksumUrl)
                {
                    checksumUrl = url;
                }
            }
        }

        if (zipUrl == null || (digest == null && checksumUrl == null)) return null;
        string title = GetString(root, "name");
        string htmlUrl = GetString(root, "html_url");
        if (!htmlUrl.StartsWith("https://github.com/thetinyfoundry/FLIP/", StringComparison.Ordinal)) htmlUrl = UpdateChecker.ReleasesPageUrl;
        return new UpdatePackage(tag, version, string.IsNullOrWhiteSpace(title) ? tag : title, htmlUrl,
            zipName, zipUrl, size, digest, checksumUrl);
    }

    /// <summary>Reads the hash for <paramref name="fileName"/> from a "&lt;sha256&gt;  &lt;file&gt;" checksum file.</summary>
    internal static string? ParseChecksumFile(string text, string fileName)
    {
        foreach (string line in text.Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$", RegexOptions.CultureInvariant);
            if (match.Success && string.Equals(match.Groups[2].Value.Trim(), fileName, StringComparison.OrdinalIgnoreCase))
                return match.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Returns the latest stable release when it is newer than the running version, otherwise null.</summary>
    internal static async Task<UpdatePackage?> FindNewerAsync(CancellationToken token)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string json = await client.GetStringAsync(LatestReleaseApi, token).ConfigureAwait(false);

        using (var doc = JsonDocument.Parse(json))
        {
            string tag = GetString(doc.RootElement, "tag_name");
            if (!UpdateChecker.IsRemoteNewer(UpdateChecker.CurrentVersion, tag)) return null;
        }

        return SelectPackage(json) ?? throw new IOException(Loc.T(
            "La nuova release non contiene un pacchetto Windows verificabile. Scaricala dalla pagina GitHub.",
            "The new release has no verifiable Windows package. Download it from the GitHub page."));
    }

    /// <summary>Null when this copy of FLIP can update itself, otherwise the reason it cannot.</summary>
    internal static string? SelfUpdateBlockReason()
    {
        if (StoreEdition.IsPackaged)
            return Loc.T("Questa versione si aggiorna da Microsoft Store.", "This version is updated by the Microsoft Store.");
        string baseDir = AppContext.BaseDirectory;
        if (baseDir.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
            baseDir.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Questa è una build di sviluppo: aggiorna il codice sorgente invece dell'app.",
                "This is a development build: update the source code instead of the app.");
        if (!File.Exists(Path.Combine(baseDir, "Flip.exe")))
            return Loc.T("Flip.exe non trovato nella cartella dell'app.", "Flip.exe was not found in the app folder.");
        if (!File.Exists(PowerShellPath))
            return Loc.T("Windows PowerShell non è disponibile.", "Windows PowerShell is not available.");
        // The package is staged in a folder the user can write to: never run it with administrator rights.
        if (Environment.IsPrivilegedProcess)
            return Loc.T("FLIP è aperto come amministratore: chiudilo, riaprilo normalmente e riprova.",
                "FLIP is running as administrator: close it, open it normally and try again.");
        // Install.ps1 replaces this folder and creates the backup next to it.
        string folder = baseDir.TrimEnd('\\');
        if (!CanWrite(folder) || Path.GetDirectoryName(folder) is not { Length: > 0 } parent || !CanWrite(parent))
            return Loc.T("La cartella di FLIP non è modificabile da questo account: scarica la nuova versione da GitHub.",
                "This account cannot change the FLIP folder: download the new version from GitHub.");
        return null;
    }

    static bool CanWrite(string folder)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(folder, ".flip-update-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Downloads the package, checks its size and SHA256 against every published value, extracts it and
    /// confirms it contains FLIP <see cref="UpdatePackage.Version"/>. Returns the extracted package folder.
    /// </summary>
    internal static async Task<string> DownloadAndVerifyAsync(UpdatePackage package, IProgress<double>? progress, CancellationToken token)
    {
        if (IsLink(UpdatesRoot)) throw new IOException(Loc.T("La cartella degli aggiornamenti è un collegamento: aggiornamento annullato.",
            "The updates folder is a link: update canceled."));
        CleanupStagedUpdates(keepVersion: null);
        string workDir = Path.Combine(UpdatesRoot, package.Version);
        Directory.CreateDirectory(workDir);
        string zipPath = Path.Combine(workDir, package.ZipName);

        using var client = CreateClient(Timeout.InfiniteTimeSpan);
        string? publishedFileHash = null;
        if (package.ChecksumUrl != null)
        {
            using var response = await client.GetAsync(package.ChecksumUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxChecksumBytes) throw new IOException("Checksum file too large.");
            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (text.Length > MaxChecksumBytes) throw new IOException("Checksum file too large.");
            publishedFileHash = ParseChecksumFile(text, package.ZipName) ?? throw new IOException(Loc.F(
                "Il file {0}.sha256 non contiene un hash valido.", "{0}.sha256 does not contain a valid hash.", package.ZipName));
        }

        string actual;
        using (var response = await client.GetAsync(package.ZipUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long expected = package.ZipSize > 0 ? package.ZipSize : response.Content.Headers.ContentLength ?? 0;
            if (expected > MaxPackageBytes) throw new IOException("Update package too large.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await ReadOrStallAsync(input, buffer, token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes) throw new IOException("Update package too large.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (expected > 0) progress?.Report(Math.Min(1.0, (double)total / expected));
                }
                if (package.ZipSize > 0 && total != package.ZipSize)
                    throw new IOException(Loc.T("Download incompleto: la dimensione non corrisponde.", "Incomplete download: size mismatch."));
            }
            actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        foreach (string? published in new[] { package.ZipSha256, publishedFileHash })
        {
            if (published != null && !string.Equals(published, actual, StringComparison.Ordinal))
                throw new IOException(Loc.T("Hash SHA256 del pacchetto non corrispondente: aggiornamento annullato.",
                    "Package SHA256 mismatch: update canceled."));
        }

        string extractDir = Path.Combine(workDir, "package");
        // ExtractToDirectory rejects entries that would land outside extractDir.
        ZipFile.ExtractToDirectory(zipPath, extractDir);
        string stagedExe = Path.Combine(extractDir, "payload", "Flip.exe");
        // Install.ps1 skips its own check when SHA256SUMS.txt is missing, so require it here.
        if (!File.Exists(stagedExe) || !File.Exists(Path.Combine(extractDir, "Install.ps1")) || !File.Exists(Path.Combine(extractDir, "SHA256SUMS.txt")))
            throw new IOException(Loc.T("Il pacchetto non contiene payload\\Flip.exe, Install.ps1 e SHA256SUMS.txt.", "The package lacks payload\\Flip.exe, Install.ps1 and SHA256SUMS.txt."));
        string stagedVersion = FileVersionInfo.GetVersionInfo(stagedExe).ProductVersion ?? "";
        if (!stagedVersion.Split('+')[0].Equals(package.Version, StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.F("Il pacchetto contiene la versione {0} invece di {1}.", "The package contains version {0} instead of {1}.",
                stagedVersion, package.Version));
        token.ThrowIfCancellationRequested();
        progress?.Report(1.0);
        return extractDir;
    }

    /// <summary>Reads the next block, failing if the connection stays silent for <see cref="StallTimeout"/>.</summary>
    static async Task<int> ReadOrStallAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(StallTimeout);
        try { return await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException(Loc.T("Connessione interrotta: riprova.", "The connection stalled: try again."));
        }
    }

    /// <summary>
    /// Starts the package installer in a separate PowerShell window that waits for this process to exit,
    /// then asks FLIP to exit. Install.ps1 verifies SHA256SUMS.txt, backs up the current folder, copies,
    /// verifies the copy and restarts FLIP. Startup and shortcut settings are left as they are.
    /// </summary>
    internal static void LaunchInstallerAndExit(string extractDir)
    {
        string installer = Path.Combine(extractDir, "Install.ps1");
        string payload = Path.Combine(extractDir, "payload");
        string destination = AppContext.BaseDirectory.TrimEnd('\\');
        string script = BuildInstallScript(installer, payload, destination, Environment.ProcessId);
        var start = new ProcessStartInfo(PowerShellPath,
            "-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = false,
            // Install.ps1 restarts FLIP with this as its working directory: keep it out of the
            // downloaded package so the package folder can be deleted afterwards.
            WorkingDirectory = destination
        };
        using (Process.Start(start) ?? throw new IOException(Loc.T("Impossibile avviare l'installer.", "Could not start the installer."))) { }
        Evidence.Log("update.install.started", new { destination });

        if (RequestExit != null) RequestExit();
        else System.Windows.Application.Current?.Shutdown();
    }

    /// <summary>The script that waits for FLIP, runs the package installer and reopens FLIP if the update fails.</summary>
    internal static string BuildInstallScript(string installer, string payload, string destination, int processId)
    {
        string failed = Loc.T("Aggiornamento non riuscito: ", "Update failed: ");
        string pressEnter = Loc.T("Premi Invio per chiudere", "Press Enter to close");
        string timeout = Loc.T("FLIP non si è chiuso entro 60 secondi.", "FLIP did not exit within 60 seconds.");

        return $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $p = Get-Process -Id {{processId}} -ErrorAction SilentlyContinue
                if ($p -and -not $p.WaitForExit(60000)) { throw '{{Quote(timeout)}}' }
                & '{{Quote(installer)}}' -Source '{{Quote(payload)}}' -Destination '{{Quote(destination)}}' -NoShortcuts -NoStartup
            } catch {
                Write-Host ''
                Write-Host ('{{Quote(failed)}}' + $_.Exception.Message) -ForegroundColor Red
                Read-Host '{{Quote(pressEnter)}}'
                $exe = '{{Quote(Path.Combine(destination, "Flip.exe"))}}'
                if ((Test-Path -LiteralPath $exe) -and -not (Get-Process Flip -ErrorAction SilentlyContinue)) { Start-Process -FilePath $exe }
            }
            """;
    }

    /// <summary>Removes downloaded packages that are no longer needed (all of them, or all but <paramref name="keepVersion"/>).</summary>
    internal static void CleanupStagedUpdates(string? keepVersion)
    {
        try
        {
            if (!Directory.Exists(UpdatesRoot) || IsLink(UpdatesRoot)) return;
            foreach (string dir in Directory.EnumerateDirectories(UpdatesRoot))
            {
                if (keepVersion != null && string.Equals(Path.GetFileName(dir), keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool IsLink(string folder) => Directory.Exists(folder) && File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Escapes a value for a single-quoted PowerShell string. PowerShell also closes it on ‘ ’ ‚ and ‛, which are valid in folder names.</summary>
    internal static string Quote(string value) => Regex.Replace(value, "['\u2018\u2019\u201A\u201B]", "$0$0");
    static bool IsTrue(JsonElement element, string property) => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;
    static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
