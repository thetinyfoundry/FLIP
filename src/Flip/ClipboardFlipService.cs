using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using Flip.Core;

namespace Flip;

/// <summary>
/// Rappresenta una scorciatoia da tastiera per il Clipboard Flip (es. Ctrl+Shift+F).
/// </summary>
public readonly record struct ClipboardShortcutGesture(
    bool Enabled,
    bool Control,
    bool Shift,
    bool Alt,
    int VirtualKey);

/// <summary>
/// Risultato dell'ispezione e materializzazione del contenuto degli Appunti.
/// </summary>
public readonly record struct ClipboardMaterializedPayload(
    bool Success,
    IReadOnlyList<string> FilePaths,
    bool UsesTemporaryStaging,
    string Description,
    string? ErrorMessage = null);

/// <summary>
/// Servizio per il Clipboard Flip: analizza il contenuto degli Appunti di Windows
/// (file copiati, immagini/screenshot o testo/tabelle) e lo materializza in staging per la ruota.
/// </summary>
public static class ClipboardFlipService
{
    /// <summary>
    /// Esegue il parsing di una stringa di scorciatoia (es. "Ctrl+Shift+F", "Alt+F8").
    /// Restituisce Enabled = false se la stringa è vuota, "None" o non valida.
    /// </summary>
    public static bool TryParseShortcut(string? shortcutText, out ClipboardShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(shortcutText))
            return false;

        string trimmed = shortcutText.Trim();
        if (trimmed.Equals("None", StringComparison.OrdinalIgnoreCase))
            return false;

        var tokens = trimmed.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        bool ctrl = false, shift = false, alt = false;
        string? keyToken = null;

        foreach (var rawToken in tokens)
        {
            string token = rawToken.ToLowerInvariant();
            if (token is "ctrl" or "control") ctrl = true;
            else if (token is "shift") shift = true;
            else if (token is "alt") alt = true;
            else
            {
                if (keyToken != null)
                {
                    // Più di un tasto principale non è supportato
                    return false;
                }
                keyToken = rawToken;
            }
        }

        if (string.IsNullOrEmpty(keyToken))
            return false;

        int vk = ResolveVirtualKey(keyToken);
        if (vk == 0)
            return false;

        gesture = new ClipboardShortcutGesture(Enabled: true, Control: ctrl, Shift: shift, Alt: alt, VirtualKey: vk);
        return true;
    }

    private static int ResolveVirtualKey(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }

        if (key.StartsWith("f", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key.AsSpan(1), out int fNum) &&
            fNum >= 1 && fNum <= 12)
        {
            return 0x70 + (fNum - 1);
        }

        return key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "ins" or "insert" => 0x2D,
            "del" or "delete" => 0x2E,
            _ => 0
        };
    }

    /// <summary>
    /// Legge il contenuto attuale degli Appunti di sistema e lo materializza in staging.
    /// </summary>
    public static bool TryMaterializeClipboard(string stagingDirectory, out ClipboardMaterializedPayload payload)
    {
        payload = default;
        try
        {
            var data = Clipboard.GetDataObject();
            return TryMaterializeDataObject(data, stagingDirectory, out payload);
        }
        catch (Exception ex)
        {
            payload = new ClipboardMaterializedPayload(
                Success: false,
                FilePaths: Array.Empty<string>(),
                UsesTemporaryStaging: false,
                Description: Loc.T("Appunti non accessibili", "Clipboard unavailable"),
                ErrorMessage: ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Ispeziona un IDataObject (da Clipboard o sintetico per test) e materializza i dati
    /// gestendo file locali, immagini bitmap e testo/tabella/JSON.
    /// </summary>
    public static bool TryMaterializeDataObject(
        System.Windows.IDataObject? data,
        string stagingDirectory,
        out ClipboardMaterializedPayload payload)
    {
        payload = default;
        if (data == null)
        {
            payload = new ClipboardMaterializedPayload(
                Success: false,
                FilePaths: Array.Empty<string>(),
                UsesTemporaryStaging: false,
                Description: Loc.T("Dati assenti", "No data"),
                ErrorMessage: Loc.T("DataObject nullo.", "Null DataObject."));
            return false;
        }

        try
        {
            // 1. File copiati con Ctrl+C in Esplora File (DataFormats.FileDrop)
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                {
                    var existing = paths.Where(path => !Policy.IsNetworkPath(path) && File.Exists(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (existing.Count > 0)
                    {
                        payload = new ClipboardMaterializedPayload(
                            Success: true,
                            FilePaths: existing,
                            UsesTemporaryStaging: false,
                            Description: existing.Count == 1
                                ? Path.GetFileName(existing[0])
                                : Loc.F("{0} file da Esplora File", "{0} files from Explorer", existing.Count));
                        return true;
                    }
                }
            }

            // 2. Immagine / Screenshot copiato negli Appunti (BitmapSource / DataFormats.Bitmap)
            BitmapSource? bitmap = null;
            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                bitmap = data.GetData(DataFormats.Bitmap) as BitmapSource;
            }
            if (bitmap == null && data.GetDataPresent(typeof(BitmapSource)))
            {
                bitmap = data.GetData(typeof(BitmapSource)) as BitmapSource;
            }

            if (bitmap != null)
            {
                Directory.CreateDirectory(stagingDirectory);
                string fileName = $"clipboard_image_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.png";
                string targetPath = Path.Combine(stagingDirectory, fileName);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(targetPath))
                {
                    encoder.Save(stream);
                }

                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                {
                    payload = new ClipboardMaterializedPayload(
                        Success: true,
                        FilePaths: [targetPath],
                        UsesTemporaryStaging: true,
                        Description: Loc.T("Immagine dagli appunti", "Clipboard image"));
                    return true;
                }
            }

            // 3. Testo / Tabella copiata da Excel, Web o Editor (UnicodeText / Text)
            string? text = null;
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                text = data.GetData(DataFormats.UnicodeText) as string;
            }
            if (string.IsNullOrWhiteSpace(text) && data.GetDataPresent(DataFormats.Text))
            {
                text = data.GetData(DataFormats.Text) as string;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                Directory.CreateDirectory(stagingDirectory);

                // Controllo Tabella TSV (almeno 2 righe e tutte contengono '\t')
                var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= 2 && lines.All(line => line.Contains('\t')))
                {
                    string filePath = Path.Combine(stagingDirectory, "clipboard_table.tsv");
                    File.WriteAllText(filePath, text, Encoding.UTF8);
                    payload = new ClipboardMaterializedPayload(
                        Success: true,
                        FilePaths: [filePath],
                        UsesTemporaryStaging: true,
                        Description: Loc.F("Tabella copiata ({0} righe)", "Copied table ({0} rows)", lines.Length));
                    return true;
                }

                // Controllo JSON valido
                string trimmed = text.Trim();
                if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
                    (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
                {
                    try
                    {
                        var node = JsonNode.Parse(trimmed);
                        if (node != null)
                        {
                            string filePath = Path.Combine(stagingDirectory, "clipboard_data.json");
                            File.WriteAllText(filePath, trimmed, Encoding.UTF8);
                            payload = new ClipboardMaterializedPayload(
                                Success: true,
                                FilePaths: [filePath],
                                UsesTemporaryStaging: true,
                                Description: Loc.T("Dati JSON dagli appunti", "Clipboard JSON data"));
                            return true;
                        }
                    }
                    catch
                    {
                        // JSON non valido, fallback su nota Markdown
                    }
                }

                // Fallback: salvataggio come nota Markdown (.md)
                string notePath = Path.Combine(stagingDirectory, "clipboard_note.md");
                File.WriteAllText(notePath, text, Encoding.UTF8);
                payload = new ClipboardMaterializedPayload(
                    Success: true,
                    FilePaths: [notePath],
                    UsesTemporaryStaging: true,
                    Description: Loc.T("Testo dagli appunti", "Clipboard text"));
                return true;
            }

            payload = new ClipboardMaterializedPayload(
                Success: false,
                FilePaths: Array.Empty<string>(),
                UsesTemporaryStaging: false,
                Description: Loc.T("Formato appunti non supportato", "Unsupported clipboard format"),
                ErrorMessage: Loc.T("Nessun file, immagine o testo trovato negli appunti.", "No file, image, or text found in the clipboard."));
            return false;
        }
        catch (Exception ex)
        {
            payload = new ClipboardMaterializedPayload(
                Success: false,
                FilePaths: Array.Empty<string>(),
                UsesTemporaryStaging: false,
                Description: Loc.T("Errore lettura appunti", "Clipboard read error"),
                ErrorMessage: ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Restituisce le impostazioni effettive di output per il Clipboard Flip.
    /// Se usesTemporaryStaging è true (screenshot, tabelle o note in memoria), reindirizza
    /// SameAsSource verso la cartella Downloads dell'utente e disattiva ReplaceOriginalToRecycleBin.
    /// Se usesTemporaryStaging è false (file reali da Esplora File), mantiene la cartella originale
    /// ma disattiva ReplaceOriginalToRecycleBin per prevenire cancellazioni involontarie da appunti.
    /// </summary>
    public static FlipSettings GetEffectiveOutputSettings(FlipSettings snapshot, bool usesTemporaryStaging)
    {
        var copy = snapshot.CreateMutableCopy();
        if (usesTemporaryStaging)
        {
            if (copy.OutputDirectoryMode == OutputDirectoryMode.SameAsSource)
            {
                copy.OutputDirectoryMode = OutputDirectoryMode.CustomFolder;
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string downloads = Path.Combine(userProfile, "Downloads");
                copy.CustomOutputDirectory = Directory.Exists(downloads)
                    ? downloads
                    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }
        }
        copy.ReplaceOriginalToRecycleBin = false;
        return copy.CreateSnapshot();
    }
}
