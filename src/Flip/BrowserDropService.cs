using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Flip.Core;

namespace Flip;

/// <summary>
/// Candidato di drop/drag identificato da un browser web o da una risorsa remota/inline.
/// </summary>
public readonly record struct BrowserDropCandidate(
    string RawUri,
    string GuessedExtension,
    string SuggestedFileName,
    bool IsDataUri,
    string? PageHost = null);

/// <summary>
/// Risultato della materializzazione su disco locale del candidato browser.
/// </summary>
public readonly record struct BrowserMaterializedItem(
    bool Success,
    string LocalFilePath,
    string EffectiveExtension,
    string? ErrorMessage = null);

/// <summary>
/// Servizio per l'ispezione immediata a zero chiamate di rete sul thread UI durante il Drag
/// e per la materializzazione asincrona protetta al Drop di immagini e media da browser web.
/// </summary>
public static class BrowserDropService
{
    private static readonly Regex ImgRegex = new(
        @"<img[^<>]+?(?<![a-zA-Z0-9_-])src\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));

    private static readonly Regex SourceUrlRegex = new(@"^SourceURL:(https?://[^\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private const int MaxRedirects = 5;
    private const int MaxDataUriCharacters = 64 * 1024 * 1024;

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly HttpRequestOptionsKey<BrowserDropCandidate> CandidateKey = new("Flip.BrowserDropCandidate");

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // Senza Brotli: il suo decoder nei .NET 10 precedenti alla 10.0.14 ha una falla nota (CVE-2026-111497).
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // I reindirizzamenti si seguono a mano, per controllare ogni destinazione.
            AllowAutoRedirect = false,
            // Una connessione riusata salterebbe il controllo dell'indirizzo.
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = ConnectAsync
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) FLIP/1.0");
        return client;
    }

    /// <summary>Si collega solo a indirizzi che la pagina di partenza poteva raggiungere da sola.</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var request = context.InitialRequestMessage;
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        // Con un proxy l'indirizzo è quello del proxy scelto dall'utente: non va filtrato.
        bool direct = request.RequestUri is { } uri && string.Equals(uri.IdnHost.Trim('[', ']'), context.DnsEndPoint.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
        if (direct && request.Options.TryGetValue(CandidateKey, out var candidate))
            addresses = addresses.Where(address => IsAllowedAddress(address, request.RequestUri!, candidate)).ToArray();
        if (addresses.Length == 0)
            throw new HttpRequestException(Loc.T("La pagina rimanda a un indirizzo della rete locale: download bloccato.", "The page points to a local network address: download blocked."));

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Un sito pubblico non deve usare FLIP per leggere servizi del PC o della rete di casa. Restano permessi i file trascinati
    /// da una pagina locale (router, NAS, ComfyUI...) e quelli di una intranet che serve sia la pagina sia il file.
    /// </summary>
    internal static bool IsAllowedAddress(IPAddress address, Uri target, BrowserDropCandidate candidate)
    {
        if (!IsLocalAddress(address, out bool thisComputer)) return true;
        if (!Uri.TryCreate(candidate.RawUri, UriKind.Absolute, out var start)) return false;
        bool fromLocalPage = candidate.PageHost == null || LooksLocal(candidate.PageHost);
        if (LooksLocal(start.IdnHost) && fromLocalPage && LooksLocal(target.IdnHost)) return true;
        return !thisComputer && candidate.PageHost != null &&
            string.Equals(candidate.PageHost, start.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(target.IdnHost, start.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLocal(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.');
        if (IPAddress.TryParse(host, out var address)) return IsLocalAddress(address, out _);
        return !host.Contains('.') || new[] { ".local", ".localhost", ".lan", ".home", ".internal", ".home.arpa" }
            .Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalAddress(IPAddress address, out bool thisComputer)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        bool v4 = b.Length == 4;
        // Anche 169.254.x.x: è l'indirizzo dei servizi interni delle macchine cloud.
        thisComputer = IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal ||
            address.Equals(IPAddress.IPv6Any) || (v4 && (b[0] is 0 or 127 || (b[0] == 169 && b[1] == 254)));
        return thisComputer || address.IsIPv6Multicast || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal ||
            (v4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || b[0] >= 224));
    }

    private static async Task<HttpResponseMessage> GetAsync(BrowserDropCandidate candidate, CancellationToken ct)
    {
        var uri = new Uri(candidate.RawUri, UriKind.Absolute);
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Options.Set(CandidateKey, candidate);
            var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                return response;
            response.Dispose();
            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (redirects >= MaxRedirects || next.Scheme is not ("http" or "https") || (uri.Scheme == "https" && next.Scheme == "http"))
                throw new HttpRequestException(Loc.T("Reindirizzamento non valido: download bloccato.", "Invalid redirect: download blocked."));
            uri = next;
        }
    }

    private static Match SafeMatch(Regex regex, string text)
    {
        try { return regex.Match(text); }
        catch (RegexMatchTimeoutException) { return Match.Empty; }
    }

    /// <summary>
    /// Ispezione veloce a zero chiamate di rete (&lt; 1 ms) sul thread UI durante il Drag.
    /// Se i dati contengono già file locali su disco, restituisce false per consentire
    /// il normale flusso locale di FLIP. Altrimenti cerca tag &lt;img src=...&gt; in CF_HTML
    /// o URL/Data-URI in UniformResourceLocator/UnicodeText.
    /// </summary>
    public static bool TryInspectBrowserData(System.Windows.IDataObject? data, out BrowserDropCandidate candidate)
    {
        candidate = default;
        if (data == null) return false;

        // 1. Se contiene già file locali esistenti su disco, ignora il flusso browser
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            try
            {
                if (data.GetData(DataFormats.FileDrop) is string[] files && files.Any(file => !Policy.IsNetworkPath(file) && File.Exists(file)))
                {
                    return false;
                }
            }
            catch
            {
                // Se la lettura di FileDrop solleva eccezioni, proseguiamo l'ispezione
            }
        }

        // 2. Ispeziona HTML Format (CF_HTML) con priorità al tag <img src="..."> rispetto ai link <a>
        string? html = GetStringFromDataObject(data, DataFormats.Html) ?? GetStringFromDataObject(data, "HTML Format");
        if (!string.IsNullOrWhiteSpace(html))
        {
            var match = SafeMatch(ImgRegex, html);
            if (match.Success)
            {
                var sourceMatch = SafeMatch(SourceUrlRegex, html);
                Uri? baseUri = sourceMatch.Success && Uri.TryCreate(sourceMatch.Groups[1].Value.Trim(), UriKind.Absolute, out var page) ? page : null;
                string src = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(src))
                {
                    // Risolvi URL relativi se presente SourceURL nell'header CF_HTML
                    if (!src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                        !src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (baseUri != null && Uri.TryCreate(baseUri, src, out var resolvedUri))
                        {
                            src = resolvedUri.AbsoluteUri;
                        }
                    }

                    if (TryParseCandidate(src, fromImgTag: true, out candidate))
                    {
                        candidate = candidate with { PageHost = baseUri?.IdnHost };
                        return true;
                    }
                }
            }
        }

        // 3. Ispeziona UniformResourceLocatorW / UniformResourceLocator / UnicodeText / Text
        string? url = ExtractUrlFromDataObject(data, "UniformResourceLocatorW")
                   ?? ExtractUrlFromDataObject(data, "UniformResourceLocator")
                   ?? GetStringFromDataObject(data, DataFormats.UnicodeText)
                   ?? GetStringFromDataObject(data, DataFormats.Text);

        if (!string.IsNullOrWhiteSpace(url))
        {
            string[] lines = url.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseCandidate(trimmed, fromImgTag: false, out candidate))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryParseCandidate(string rawUri, bool fromImgTag, out BrowserDropCandidate candidate)
    {
        candidate = default;
        if (string.IsNullOrWhiteSpace(rawUri)) return false;

        // Gestione Data-URI
        if (rawUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            string rest = rawUri.Substring("data:image/".Length);
            int semiIdx = rest.IndexOfAny([';', ',']);
            string rawSubtype = semiIdx >= 0 ? rest[..semiIdx].Trim().ToLowerInvariant() : rest.Trim().ToLowerInvariant();
            string ext = MapDataUriSubtypeToExtension(rawSubtype);
            string fileName = $"image.{ext}";
            candidate = new BrowserDropCandidate(rawUri, ext, fileName, IsDataUri: true);
            return true;
        }

        // Gestione HTTP / HTTPS
        if (rawUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            rawUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
            {
                string path = uri.AbsolutePath;
                string fileName = Path.GetFileName(path);
                try { fileName = Uri.UnescapeDataString(fileName); } catch { }
                string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

                if (ext == "jpeg") ext = "jpg";

                // Se l'estensione nel path è assente, controlla i parametri di query (es. ?format=webp, ?ext=jpg, ?fm=png)
                if (string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(uri.Query))
                {
                    var queryMatch = Regex.Match(uri.Query, @"[?&](?:format|fm|ext)=([a-zA-Z0-9]+)", RegexOptions.IgnoreCase);
                    if (queryMatch.Success)
                    {
                        string qExt = queryMatch.Groups[1].Value.ToLowerInvariant();
                        if (qExt == "jpeg") qExt = "jpg";
                        ext = qExt;
                    }
                }

                if (string.IsNullOrEmpty(ext))
                {
                    if (fromImgTag)
                    {
                        // Fallback come da specifiche: per tag <img> senza estensione certa usiamo "png" provvisorio
                        ext = "png";
                        if (string.IsNullOrWhiteSpace(fileName)) fileName = "image.png";
                        else fileName = $"{Path.GetFileNameWithoutExtension(fileName)}.png";
                    }
                    else
                    {
                        // Se non proviene da un tag <img> e l'URL non ha estensione, non è un candidato sicuro
                        return false;
                    }
                }

                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = $"media.{ext}";
                }

                fileName = SanitizeFileName(fileName, ext);
                candidate = new BrowserDropCandidate(rawUri, ext, fileName, IsDataUri: false);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Materializza in modo asincrono la risorsa remota o inline nella cartella di staging specificata.
    /// Verifica la dimensione massima (250 MB), decodifica il payload, corregge l'estensione effettiva
    /// tramite header Content-Type e garantisce che il file finale abbia dimensione &gt; 0 byte.
    /// </summary>
    public static async Task<BrowserMaterializedItem> MaterializeAsync(
        BrowserDropCandidate candidate,
        string stagingDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingDirectory);

        // Caso Data-URI inline
        if (candidate.IsDataUri)
        {
            try
            {
                int commaIdx = candidate.RawUri.IndexOf(',');
                if (commaIdx < 0)
                {
                    return new BrowserMaterializedItem(false, "", candidate.GuessedExtension, "Data URI non valido: separatore virgola assente.");
                }
                if (candidate.RawUri.Length > MaxDataUriCharacters)
                {
                    return new BrowserMaterializedItem(false, "", candidate.GuessedExtension, Loc.T("Immagine della pagina troppo grande.", "The page image is too large."));
                }

                string header = candidate.RawUri[..commaIdx];
                string payload = candidate.RawUri[(commaIdx + 1)..];

                byte[] bytes;
                if (header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                {
                    string cleanBase64 = Regex.Replace(payload, @"\s+", "");
                    bytes = Convert.FromBase64String(cleanBase64);
                }
                else
                {
                    string decoded = Uri.UnescapeDataString(payload);
                    bytes = Encoding.UTF8.GetBytes(decoded);
                }

                if (bytes.Length == 0)
                {
                    return new BrowserMaterializedItem(false, "", candidate.GuessedExtension, "Il payload del Data URI è vuoto.");
                }

                string fileName = SanitizeFileName(candidate.SuggestedFileName, candidate.GuessedExtension);
                string targetPath = Path.Combine(stagingDirectory, fileName);
                if (File.Exists(targetPath))
                {
                    string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
                    targetPath = Path.Combine(stagingDirectory, $"{nameNoExt}_{Guid.NewGuid().ToString("N")[..8]}.{candidate.GuessedExtension}");
                }

                await File.WriteAllBytesAsync(targetPath, bytes, ct).ConfigureAwait(false);
                progress?.Report(100.0);

                var fi = new FileInfo(targetPath);
                if (!fi.Exists || fi.Length == 0)
                {
                    return new BrowserMaterializedItem(false, targetPath, candidate.GuessedExtension, "Il file generato da Data URI ha dimensione zero byte.");
                }

                return new BrowserMaterializedItem(true, targetPath, candidate.GuessedExtension);
            }
            catch (Exception ex)
            {
                return new BrowserMaterializedItem(false, "", candidate.GuessedExtension, ex.Message);
            }
        }

        // Caso URL HTTP / HTTPS
        string targetFile = "";
        string effectiveExt = candidate.GuessedExtension;
        try
        {
            using var response = await GetAsync(candidate, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            const long maxAllowedBytes = 250L * 1024 * 1024; // Limite di sicurezza: 250 MB
            if (totalBytes.HasValue && totalBytes.Value > maxAllowedBytes)
            {
                return new BrowserMaterializedItem(false, "", candidate.GuessedExtension, $"Dimensione del file ({totalBytes.Value / (1024 * 1024)} MB) superiore al limite consentito di 250 MB.");
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            string? mimeExt = MapMimeTypeToExtension(mediaType);
            if (!string.IsNullOrEmpty(mimeExt))
            {
                if (string.Equals(candidate.GuessedExtension, "png", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mimeExt, "png", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveExt = mimeExt;
                }
                else if (FormatRegistry.TryGet(mimeExt, out _))
                {
                    effectiveExt = mimeExt;
                }
            }
            if (string.IsNullOrWhiteSpace(effectiveExt)) effectiveExt = "bin";

            string baseName = Path.GetFileNameWithoutExtension(candidate.SuggestedFileName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "browser_media";
            string fileName = SanitizeFileName($"{baseName}.{effectiveExt}", effectiveExt);
            targetFile = Path.Combine(stagingDirectory, fileName);
            if (File.Exists(targetFile))
            {
                targetFile = Path.Combine(stagingDirectory, $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid().ToString("N")[..8]}.{effectiveExt}");
            }

            using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    totalRead += bytesRead;
                    if (totalRead > maxAllowedBytes)
                    {
                        fileStream.Close();
                        try { File.Delete(targetFile); } catch { }
                        return new BrowserMaterializedItem(false, targetFile, effectiveExt, "Download interrotto: superato il limite di 250 MB.");
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = Math.Min(100.0, (double)totalRead / totalBytes.Value * 100.0);
                        progress?.Report(pct);
                    }
                }
            }

            progress?.Report(100.0);

            var fi = new FileInfo(targetFile);
            if (!fi.Exists || fi.Length == 0)
            {
                try { File.Delete(targetFile); } catch { }
                return new BrowserMaterializedItem(false, targetFile, effectiveExt, "Il file scaricato ha dimensione zero byte.");
            }

            return new BrowserMaterializedItem(true, targetFile, effectiveExt);
        }
        catch (Exception ex)
        {
            try { if (!string.IsNullOrEmpty(targetFile) && File.Exists(targetFile)) File.Delete(targetFile); } catch { }
            return new BrowserMaterializedItem(false, targetFile, effectiveExt, ex is HttpRequestException { InnerException: HttpRequestException inner } ? inner.Message : ex.Message);
        }
    }

    /// <summary>
    /// Regola le impostazioni di output per i drop dal browser: se la modalità di output è
    /// impostata su SameAsSource, reindirizza automaticamente alla cartella Downloads dell'utente
    /// e disattiva ReplaceOriginalToRecycleBin per evitare l'invio al Cestino dei file temporanei di staging.
    /// </summary>
    public static FlipSettings GetEffectiveOutputSettings(FlipSettings snapshot)
    {
        var copy = snapshot.CreateMutableCopy();
        if (copy.OutputDirectoryMode == OutputDirectoryMode.SameAsSource)
        {
            copy.OutputDirectoryMode = OutputDirectoryMode.CustomFolder;
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            copy.CustomOutputDirectory = Path.Combine(userProfile, "Downloads");
        }
        copy.ReplaceOriginalToRecycleBin = false;
        return copy.CreateSnapshot();
    }

    private static string MapDataUriSubtypeToExtension(string subtype)
    {
        return subtype switch
        {
            "jpeg" => "jpg",
            "svg+xml" => "svg",
            "x-icon" or "vnd.microsoft.icon" => "ico",
            "x-ms-bmp" => "bmp",
            // Solo immagini: "data:image/docx" non deve arrivare ai motori dei documenti.
            "png" or "jpg" or "gif" or "webp" or "bmp" or "ico" or "avif" or "heic" or "heif" or "tiff" or "tif" or "svg" or "jxl" => subtype,
            _ => "png"
        };
    }

    private static string? MapMimeTypeToExtension(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return null;
        mediaType = mediaType.Trim().ToLowerInvariant();

        return mediaType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            "image/gif" => "gif",
            "image/svg+xml" => "svg",
            "image/bmp" or "image/x-ms-bmp" => "bmp",
            "image/x-icon" or "image/vnd.microsoft.icon" => "ico",
            "image/avif" => "avif",
            "image/tiff" => "tiff",
            "video/mp4" => "mp4",
            "video/webm" => "webm",
            "video/x-matroska" => "mkv",
            "video/quicktime" => "mov",
            "video/x-msvideo" => "avi",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/ogg" or "application/ogg" => "ogg",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/aac" => "aac",
            "application/pdf" => "pdf",
            "application/zip" => "zip",
            _ => FormatRegistry.All.FirstOrDefault(d => string.Equals(d.MimeType, mediaType, StringComparison.OrdinalIgnoreCase)) is { } def && !string.IsNullOrEmpty(def.Extension)
                ? def.Extension
                : null
        };
    }

    private static string SanitizeFileName(string fileName, string fallbackExtension)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return $"media.{fallbackExtension}";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(fileName.Length);
        foreach (char c in fileName)
        {
            sb.Append(invalidChars.Contains(c) ? '_' : c);
        }
        string result = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(result))
            return $"media.{fallbackExtension}";

        if (!Path.HasExtension(result))
            result += "." + fallbackExtension;

        return result;
    }

    private static string? GetStringFromDataObject(System.Windows.IDataObject data, string format)
    {
        if (!data.GetDataPresent(format)) return null;
        try
        {
            object? obj = data.GetData(format);
            if (obj is string s && !string.IsNullOrWhiteSpace(s)) return s;
            if (obj is byte[] bytes && bytes.Length > 0)
            {
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            }
            if (obj is MemoryStream ms)
            {
                byte[] msBytes = ms.ToArray();
                if (msBytes.Length > 0)
                {
                    return Encoding.UTF8.GetString(msBytes).TrimEnd('\0');
                }
            }
        }
        catch
        {
            // Eccezioni di lettura ignorate
        }
        return null;
    }

    private static string? ExtractUrlFromDataObject(System.Windows.IDataObject data, string format)
    {
        if (!data.GetDataPresent(format)) return null;
        try
        {
            object? obj = data.GetData(format);
            if (obj is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
            if (obj is MemoryStream ms)
            {
                byte[] bytes = ms.ToArray();
                if (bytes.Length == 0) return null;
                if (format.EndsWith("W", StringComparison.OrdinalIgnoreCase))
                {
                    string text = Encoding.Unicode.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n');
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                else
                {
                    string text = Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ', '\r', '\n');
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        catch
        {
            // Eccezioni di lettura ignorate
        }
        return null;
    }
}
