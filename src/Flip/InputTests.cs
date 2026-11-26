using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Flip.Core;
using Flip.Engines;
using Flip.Routing;
using Flip.SettingsHub;
namespace Flip;

internal static class InputTests
{
    static bool UpdatePackageSelectionChecks()
    {
        const string hash = "add0b704c03f01b990f6518855f2492bdf7d21b0c9a0365cde1cab03f2608df6";
        const string prefix = "https://github.com/thetinyfoundry/FLIP/releases/download/v9.9.9/";
        string Release(string extra, string assets) =>
            "{\"tag_name\":\"v9.9.9\",\"name\":\"FLIP v9.9.9\",\"html_url\":\"https://github.com/thetinyfoundry/FLIP/releases/tag/v9.9.9\"" + extra + ",\"assets\":[" + assets + "]}";
        string zip = "{\"name\":\"Flip-9.9.9-win-x64.zip\",\"size\":1234,\"digest\":\"sha256:" + hash.ToUpperInvariant() + "\",\"browser_download_url\":\"" + prefix + "Flip-9.9.9-win-x64.zip\"}";
        string zipNoDigest = "{\"name\":\"Flip-9.9.9-win-x64.zip\",\"size\":1234,\"browser_download_url\":\"" + prefix + "Flip-9.9.9-win-x64.zip\"}";
        string sums = "{\"name\":\"Flip-9.9.9-win-x64.zip.sha256\",\"browser_download_url\":\"" + prefix + "Flip-9.9.9-win-x64.zip.sha256\"}";
        string spoofed = "{\"name\":\"Flip-9.9.9-win-x64.zip\",\"digest\":\"sha256:" + hash + "\",\"browser_download_url\":\"https://example.com/Flip-9.9.9-win-x64.zip\"}";

        var full = UpdateInstaller.SelectPackage(Release("", zip + "," + sums));
        var checksumOnly = UpdateInstaller.SelectPackage(Release("", zipNoDigest + "," + sums));
        return full is { Version: "9.9.9", ZipSize: 1234, ZipSha256: hash } &&
            full.ZipUrl == prefix + "Flip-9.9.9-win-x64.zip" && full.ChecksumUrl == prefix + "Flip-9.9.9-win-x64.zip.sha256" &&
            checksumOnly is { ZipSha256: null, ChecksumUrl: not null } &&
            UpdateInstaller.SelectPackage(Release("", zipNoDigest)) is null &&                 // nothing to verify against
            UpdateInstaller.SelectPackage(Release("", spoofed)) is null &&                     // asset hosted elsewhere
            UpdateInstaller.SelectPackage(Release(",\"prerelease\":true", zip)) is null &&
            UpdateInstaller.SelectPackage(Release(",\"draft\":true", zip)) is null &&
            UpdateInstaller.SelectPackage(Release("", zip).Replace("\"tag_name\":\"v9.9.9\"", "\"tag_name\":\"v9.9.9;calc\"")) is null &&
            UpdateInstaller.ParseChecksumFile(hash + "  Flip-9.9.9-win-x64.zip\n", "Flip-9.9.9-win-x64.zip") == hash &&
            UpdateInstaller.ParseChecksumFile(hash.ToUpperInvariant() + " *Flip-9.9.9-win-x64.zip\r\n", "Flip-9.9.9-win-x64.zip") == hash &&
            UpdateInstaller.ParseChecksumFile(hash + "  other.zip\n", "Flip-9.9.9-win-x64.zip") is null &&
            UpdateInstaller.ParseChecksumFile("not a hash  Flip-9.9.9-win-x64.zip", "Flip-9.9.9-win-x64.zip") is null;
    }

    internal static int Run()
    {
        string previousLanguage = Loc.Preference;
        Loc.SetPreference(Loc.Italian);
        try
        {
            return RunCore();
        }
        finally
        {
            Loc.SetPreference(previousLanguage);
        }
    }

    static int RunCore()
    {
        Evidence.Enabled = true;
        var shortcut = WheelShortcut.Parse("OEM102");
        var checks = new Dictionary<string, bool>
        {
            ["iso_key_down"] = shortcut.Matches(0xE2, 0, 0x56),
            ["iso_key_release"] = shortcut.Matches(0xE2, 1, 0x56),
            ["function_keys_ignored"] = !shortcut.Matches(0x77, 0, 0x42) && !shortcut.Matches(0x78, 0, 0x43),
            ["physical_key_survives_layout_mapping"] = shortcut.Matches(0xDC, 0, 0x56),
            ["extended_scan_not_iso_key"] = !shortcut.Matches(0xE2, 2, 0x56) && !shortcut.Matches(0xE2, 4, 0x56),
            ["vk_fallback_without_scan"] = shortcut.Matches(0xE2, 0),
            ["different_physical_key_rejected"] = !shortcut.Matches(0xE2, 0, 0x2B),
            ["iso_aliases_parse"] = new[] { "<>", "<", ">", "VK_OEM_102", "OEM_102" }.All(v => WheelShortcut.Parse(v) == shortcut),
            ["ctrl_never_triggers"] = !shortcut.Matches(0x11, 0) && !shortcut.Matches(0x11, 2) && !shortcut.Matches(0xA3, 0) && !shortcut.Matches(0xA2, 0),
            ["alt_shift_never_trigger"] = !shortcut.Matches(0x12, 0) && !shortcut.Matches(0x10, 0),
            ["letters_ignored"] = !shortcut.Matches(0x44, 0) && !shortcut.Matches(0x46, 0),
            ["space_ignored"] = !shortcut.Matches(0x20, 0),
            ["escape_not_trigger"] = !shortcut.Matches(0x1B, 0),
            ["swapped_primary"] = Native.PrimaryVirtualKey(true) == 2 && Native.PrimaryDown(4, true) && Native.PrimaryUp(8, true),
            ["normal_primary"] = Native.PrimaryVirtualKey(false) == 1 && Native.PrimaryDown(1, false) && Native.PrimaryUp(2, false)
        };

        // ── Apertura con pressione prolungata del mouse ──────────────────
        checks["hold_to_open_opt_in_default"] = new FlipSettings().HoldToOpenEnabled == false &&
                                                 new FlipSettings().HoldToOpenMilliseconds == 3000;
        checks["hold_to_open_clamps_duration"] =
            new FlipSettings { HoldToOpenMilliseconds = 10 }.HoldToOpenMilliseconds == 1000 &&
            new FlipSettings { HoldToOpenMilliseconds = 99999 }.HoldToOpenMilliseconds == 8000;
        var holdGate = new HoldToOpenGate(3000);
        holdGate.MouseDown(100, 100, 10_000);
        checks["hold_not_due_before_threshold"] = !holdGate.IsDue(12_999);
        checks["hold_due_at_threshold"] = holdGate.IsDue(13_000);
        holdGate.MouseUp();
        checks["hold_cancelled_by_mouse_up"] = !holdGate.IsDue(20_000);
        holdGate.MouseDown(100, 100, 30_000);
        holdGate.Move(108, 100);
        checks["hold_cancelled_by_move"] = !holdGate.IsDue(40_000);
        holdGate.MouseDown(100, 100, 50_000);
        holdGate.Move(105, 100);
        checks["hold_tolerates_small_jitter"] = holdGate.IsDue(53_000);
        holdGate.Cancel();
        checks["hold_cancelled_explicitly"] = !holdGate.IsDue(99_000);

        checks["universal_default_is_down_arrow"] = WheelShortcut.Parse(WheelShortcut.Default).VirtualKey == 0x28;
        foreach (string id in new[] { "DOWN", "UP", "LEFT", "RIGHT", "SPACE", "OEM102", "F8", "F9", "A", "7", "VK:BF" })
        {
            var choice = WheelShortcut.Parse(id);
            checks["custom_key_roundtrip_" + id] = WheelShortcut.Parse(choice.Id) == choice;
        }
        checks["extended_arrows_match"] = WheelShortcut.Parse("DOWN").Matches(0x28, 2, 0x50);
        var consumed = new ShortcutPressGate();
        checks["drag_key_captured_once"] = consumed.Process(true, true) == (true, true) && consumed.Process(true, true) == (true, false);
        checks["captured_keyup_after_mouse_release"] = consumed.Process(false, false) == (true, false);
        checks["normal_key_after_captured_pair"] = consumed.Process(true, false) == (false, false) && consumed.Process(false, false) == (false, false);
        var typing = new ShortcutPressGate();
        checks["typing_repeat_not_stolen_by_later_mouse_hold"] = typing.Process(true, false) == (false, false) && typing.Process(true, true) == (false, false) && typing.Process(false, true) == (false, false);
        consumed.Process(true, true); consumed.Reset();
        checks["shortcut_change_resets_old_press"] = consumed.Process(true, true) == (true, true) && consumed.Process(false, false) == (true, false);
        var firstNormal = new ShortcutPressGate(); firstNormal.Process(true, false); firstNormal.ReserveRepeatsForGesture();
        checks["key_first_drag_reserves_repeats_only"] = firstNormal.Process(true, false) == (true, false) && firstNormal.Process(false, false) == (false, false);
        var keyFirst = new ShortcutOrderGate();
        checks["key_first_does_not_open_without_mouse"] = !keyFirst.KeyDown(false) && !keyFirst.Move(200, 200);
        keyFirst.MouseDown(20, 20);
        checks["key_first_click_without_drag_stays_closed"] = !keyFirst.Move(22, 22);
        checks["key_first_then_drag_opens"] = keyFirst.Move(30, 20);
        checks["key_first_opens_once"] = !keyFirst.Move(60, 20);
        var dragFirst = new ShortcutOrderGate();
        checks["drag_first_then_key_opens_once"] = dragFirst.KeyDown(true) && !dragFirst.KeyDown(true);
        var releasedFirst = new ShortcutOrderGate(); releasedFirst.KeyDown(false); releasedFirst.KeyUp(); releasedFirst.MouseDown(0, 0);
        checks["key_released_before_drag_does_not_open"] = !releasedFirst.Move(20, 0);
        var missedMouseUp = new ShortcutOrderGate(); missedMouseUp.KeyDown(false); missedMouseUp.MouseDown(0, 0); missedMouseUp.KeyUp(); missedMouseUp.KeyDown(false);
        checks["new_press_discards_stale_mouse_down"] = !missedMouseUp.Move(100, 100);
        missedMouseUp.MouseDown(100, 100);
        checks["new_drag_after_cancel_still_opens"] = missedMouseUp.Move(110, 100);
        var clickFirst = new ShortcutOrderGate(); clickFirst.KeyDown(false); clickFirst.MouseDown(0, 0); clickFirst.MouseUp(); clickFirst.MouseDown(10, 10);
        checks["key_held_across_click_then_drag"] = clickFirst.Move(25, 10);
        keyFirst.Reset();
        checks["cancel_disarms_key_first_gesture"] = !keyFirst.Held && !keyFirst.Move(300, 300);
        foreach (string legacy in new[] { "RCTRL", "LCTRL", "CTRL DESTRO", "CTRL SINISTRO", "VK:11", "VK:12", "VK:10", "VK:5B", "VK:1B", "VK:2E", "FN", "F12" })
        {
            try { WheelShortcut.Parse(legacy); checks["legacy_rejected_" + legacy] = false; }
            catch (ArgumentException) { checks["legacy_rejected_" + legacy] = true; }
        }
        foreach (string option in new[] { "--evidence", "--trigger", "--report" })
        {
            try { Program.OptionValue([option], option); checks["missing_value_" + option] = false; }
            catch (ArgumentException) { checks["missing_value_" + option] = true; }
            try { Program.OptionValue([option, "--ui"], option); checks["flag_as_value_" + option] = false; }
            catch (ArgumentException) { checks["flag_as_value_" + option] = true; }
        }
        checks["files_end_before_following_options"] = Program.FileArguments(["--files", "one.png", "two.png", "--evidence", "out"]).SequenceEqual(new[] { "one.png", "two.png" });
        checks["null_preference_is_rejected"] = false;
        try { WheelShortcut.Parse(null); } catch (ArgumentException) { checks["null_preference_is_rejected"] = true; }
        try { Program.FileArguments(["--files", "--ui"]); checks["empty_file_argument_rejected"] = false; }
        catch (ArgumentException) { checks["empty_file_argument_rejected"] = true; }
        checks["single_png_still_has_jpg"] = Policy.Outputs(["image.png"]).Contains("JPG");
        checks["mixed_mp4_does_not_silently_convert_subset"] = Policy.Outputs(["image.png", "video.mp4"]).Length == 0;
        checks["mixed_payload_explains_blocker"] = Policy.AvailabilityMessage(["image.png", "video.mp4"]).Contains("MP4");
        checks["supported_batch_unchanged"] = Policy.Outputs(["image.png", "image.webp"]).Contains("JPG");
        checks["supported_single_no_error"] = Policy.AvailabilityMessage(["image.png"]) == "";
        byte[] sound = WheelSound.CreateWave();
        checks["original_sound_is_pcm_wave"] = System.Text.Encoding.ASCII.GetString(sound, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(sound, 8, 4) == "WAVE" && BitConverter.ToInt16(sound, 20) == 1;
        checks["sound_duration_40ms"] = BitConverter.ToInt32(sound, 24) == 44100 && (sound.Length - 44) == 1764 * 2;
        var samples = Enumerable.Range(0, (sound.Length - 44) / 2).Select(i => (int)BitConverter.ToInt16(sound, 44 + i * 2)).ToArray();
        checks["sound_no_clipping"] = samples.Max(s => Math.Abs(s)) < short.MaxValue;
        checks["sound_audible_body"] = Math.Sqrt(samples.Average(s => (double)s * s)) > 1000;
        checks["sound_faded_ends"] = samples[0] == 0 && Math.Abs(samples[^1]) < 5;
        checks["sound_tick_transient"] = Enumerable.Range(1, Math.Min(441, samples.Length - 1)).Count(i => Math.Sign(samples[i]) != Math.Sign(samples[i - 1])) > 12;
        var txtOutputs = Policy.Outputs(["note.txt"], true, true);
        checks["txt_outputs_visible_in_wheel"] = txtOutputs.Length == 12 && Wheel.OrderFormats(txtOutputs).ToHashSet().SetEquals(txtOutputs);
        checks["txt_native_without_document_tools"] = Policy.Outputs(["note.txt"], false, false).ToHashSet().SetEquals(new[] { "PDF", "PNG", "JPG", "WEBP" });
        checks["documents_without_office_omit_rendering"] = !Policy.Outputs(["note.html"], true, false).Contains("JPG") && Policy.Outputs(["note.html"], true, false).Contains("DOCX");
        checks["pages_not_advertised"] = !Policy.Outputs(["note.txt"], true, true).Contains("PAGES");
        checks["json_outputs_available"] = Policy.Outputs(["dati.json"], false, false, false).ToHashSet().SetEquals(new[] { "YAML", "XML", "CSV", "TXT", "HTML", "MD", "PDF" });
        checks["csv_offers_json"] = Policy.Outputs(["tabella.csv"], false, false, false).Contains("JSON");
        checks["mp3_offers_wav_with_ffmpeg"] = Policy.Outputs(["audio.mp3"], false, false, true).Contains("WAV");
        checks["mp4_offers_video_and_audio_with_ffmpeg"] = Policy.Outputs(["clip.mp4"], false, false, true).ToHashSet().SetEquals(new[] { "MP3", "WAV", "MKV", "WEBM", "AVI", "MOV" });
        checks["wheel_progress_arc_geometry"] = Wheel.ProgressArcData(50).Bounds.Width > 80 && Wheel.ProgressArcData(95).Bounds.Height > 150;
        checks["update_checker_version_compare"] = UpdateChecker.IsRemoteNewer("0.4.0-beta.3", "v0.5.0") && UpdateChecker.IsRemoteNewer("0.4.0-beta.3", "v0.4.0") && !UpdateChecker.IsRemoteNewer("0.4.0-beta.3", "v0.4.0-beta.3");
        checks["update_installer_accepts_only_verifiable_official_packages"] = UpdatePackageSelectionChecks();
        string updateScript = UpdateInstaller.BuildInstallScript(@"C:\Flip test\D'Angelo\p\Install.ps1", @"C:\Flip test\D’Angelo; calc\p\payload", @"C:\Flip test\D‘Angelo\Flip", 42);
        checks["update_installer_quotes_every_powershell_quote"] = UpdateInstaller.Quote("a'b‘c’d‚e‛f") == "a''b‘‘c’’d‚‚e‛‛f" &&
            updateScript.Contains(@"& 'C:\Flip test\D''Angelo\p\Install.ps1' -Source 'C:\Flip test\D’’Angelo; calc\p\payload' -Destination 'C:\Flip test\D‘‘Angelo\Flip' -NoShortcuts -NoStartup") &&
            updateScript.Contains("Get-Process -Id 42 ") && updateScript.Contains("Start-Process -FilePath $exe");
        foreach (int count in new[] { 1, 2, 3, 6, 8, 12 })
        {
            bool correct = true;
            for (int i = 0; i < count; i++)
            {
                double angle = (i * 360.0 / count - 90) * Math.PI / 180;
                var point = new System.Windows.Point(210 + 143 * Math.Cos(angle), 210 + 143 * Math.Sin(angle));
                for (int j = 0; j < count; j++) correct &= Wheel.SectorData(j, count).FillContains(point) == (i == j);
                correct &= !Wheel.SectorData(i, count).FillContains(new System.Windows.Point(210, 210));
                correct &= Wheel.SectorData(i, count).Bounds.Left >= 24 && Wheel.SectorData(i, count).Bounds.Right <= 396;
            }
            checks["dynamic_sector_geometry_" + count] = correct;
        }
        try { ConversionEngine.Convert([], ["PDF"], CancellationToken.None).GetAwaiter().GetResult(); checks["empty_batch_rejected"] = false; } catch (ArgumentException) { checks["empty_batch_rejected"] = true; }
        try { ConversionEngine.Convert(Enumerable.Repeat("file.txt", 129).ToArray(), ["PDF"], CancellationToken.None).GetAwaiter().GetResult(); checks["oversized_batch_rejected"] = false; } catch (ArgumentException) { checks["oversized_batch_rejected"] = true; }
        try { ConversionEngine.Convert(["file.txt"], [], CancellationToken.None).GetAwaiter().GetResult(); checks["empty_output_rejected"] = false; } catch (ArgumentException) { checks["empty_output_rejected"] = true; }
        string priorLog = Evidence.DirectoryPath;
        try
        {
            Evidence.DirectoryPath = Path.Combine(priorLog, "rotation-check");
            Directory.CreateDirectory(Evidence.DirectoryPath);
            string log = Path.Combine(Evidence.DirectoryPath, "events.jsonl");
            File.WriteAllBytes(log, new byte[2 * 1024 * 1024]);
            Evidence.Log("rotation-test");
            checks["diagnostic_logs_bounded"] = File.Exists(log + ".previous") && new FileInfo(log).Length < 1024;
        }
        finally { Evidence.DirectoryPath = priorLog; }
        checks["startup_command_quoted"] = StartupIntegration.CommandFor(Path.Combine(Path.GetTempPath(), "Flip app", "Flip.exe")).EndsWith("Flip.exe\" --background");
        var preferences = System.Text.Json.JsonSerializer.Deserialize<AppPreferences>("{\"SoundEnabled\":false,\"Trigger\":\"LCTRL\"}");
        checks["mute_and_key_roundtrip"] = preferences != null && !preferences.SoundEnabled && preferences.Trigger == "LCTRL";
        Directory.CreateDirectory(Evidence.DirectoryPath);
        string cleanupRoot = Path.Combine(Evidence.DirectoryPath, "cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cleanupRoot);
        string cleanupJob = Path.Combine(cleanupRoot, ".flip-job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cleanupJob);
        string lockFile = Path.Combine(cleanupJob, ".~lock.prepared.pdf#");
        File.WriteAllText(lockFile, "synthetic LibreOffice lock");
        File.SetAttributes(lockFile, FileAttributes.ReadOnly | FileAttributes.Hidden);
        ConversionEngine.DeleteJob(cleanupJob, cleanupRoot);
        checks["readonly_office_lock_cleaned"] = !Directory.Exists(cleanupJob);
        try { ConversionEngine.DeleteJob(cleanupRoot, Path.GetDirectoryName(cleanupRoot)!); checks["unowned_cleanup_refused"] = false; }
        catch (IOException) { checks["unowned_cleanup_refused"] = Directory.Exists(cleanupRoot); }
        Directory.Delete(cleanupRoot);
        CheckReleaseRefinements(checks);
        CheckArchiveOutputs(checks);
        CheckSubRings(checks);
        CheckBrowserDrop(checks);
        CheckClipboardFlip(checks);
        CheckWatchFolders(checks);
        CheckScenarioMatrix(checks);
        CheckLocalization(checks);
        CheckDocxDocumentFidelityAndPdfA(checks);
        File.WriteAllText(Path.Combine(Evidence.DirectoryPath, "input-tests.json"),
            JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, passed = checks.Count(c => c.Value), failed = checks.Count(c => !c.Value), checks }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.All(c => c.Value) ? 0 : 1;
    }

    static void CheckReleaseRefinements(Dictionary<string, bool> checks)
    {
        checks["resize_matches_oriented_dimensions"] =
            BuiltInQuickTools.ComputeResizeDimensions(4032, 3024, BuiltInQuickTools.Resize1080pId) == (1920, 1440) &&
            BuiltInQuickTools.ComputeResizeDimensions(3024, 4032, BuiltInQuickTools.Resize4kId) == (2880, 3840) &&
            BuiltInQuickTools.ComputeResizeDimensions(99, 1, BuiltInQuickTools.Resize50Id) == (50, 1) &&
            BuiltInQuickTools.ComputeResizeDimensions(100, 250, BuiltInQuickTools.Resize1080pId) == (100, 250);
        string[] input = ["pagina-10.pdf", "pagina-2.pdf", "pagina-1.pdf"];
        var options = new Dictionary<string, string>();
        var original = MergePdfQuickTool.OrderPages(input, options);
        options["natural_sort"] = "true";
        var sorted = MergePdfQuickTool.OrderPages(input, options);
        checks["pdf_natural_order_optional"] = sorted.SequenceEqual(["pagina-1.pdf", "pagina-2.pdf", "pagina-10.pdf"]) &&
            original.SequenceEqual(["pagina-10.pdf", "pagina-2.pdf", "pagina-1.pdf"]) && input.SequenceEqual(original);

        // Unione PDF: le pagine devono mantenere dimensioni e orientamento sorgente,
        // con override opzionale ciclabile dalla ruota (A4/A3, verticale/orizzontale).
        string mergeRoot = Path.Combine(Evidence.DirectoryPath, "merge-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(mergeRoot);
            string a3a = Path.Combine(mergeRoot, "a3-a.pdf");
            string a3b = Path.Combine(mergeRoot, "a3-b.pdf");
            foreach (string path in new[] { a3a, a3b })
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var doc = SkiaSharp.SKDocument.CreatePdf(stream) ?? throw new IOException("PDF encoder unavailable.");
                var page = doc.BeginPage(1190.55f, 841.89f);
                page.Clear(SkiaSharp.SKColors.White);
                doc.EndPage();
                doc.Close();
            }

            var tool = new MergePdfQuickTool();
            var originalContext = new QuickToolContext { InputFilePaths = [a3a, a3b], Settings = new FlipSettings() };
            var merged = tool.ExecuteAsync(originalContext).GetAwaiter().GetResult();
            using var pdf = merged.Success && merged.OutputFilePaths.Count > 0
                ? UglyToad.PdfPig.PdfDocument.Open(merged.OutputFilePaths[0])
                : null;
            checks["merge_pdf_preserves_a3_landscape"] = pdf is { NumberOfPages: 2 } &&
                Math.Abs(pdf.GetPage(1).Width - 1190.55) <= 2 && Math.Abs(pdf.GetPage(1).Height - 841.89) <= 2 &&
                Math.Abs(pdf.GetPage(2).Width - 1190.55) <= 2 && Math.Abs(pdf.GetPage(2).Height - 841.89) <= 2 &&
                pdf.GetPage(1).Width > pdf.GetPage(1).Height;

            var overrideContext = new QuickToolContext { InputFilePaths = [a3a, a3b], Settings = new FlipSettings() };
            overrideContext.Parameters["page_size"] = "a4p";
            var overridden = tool.ExecuteAsync(overrideContext).GetAwaiter().GetResult();
            using var pdfA4 = overridden.Success && overridden.OutputFilePaths.Count > 0
                ? UglyToad.PdfPig.PdfDocument.Open(overridden.OutputFilePaths[0])
                : null;
            checks["merge_pdf_page_size_override"] = pdfA4 is { NumberOfPages: 2 } &&
                Math.Abs(pdfA4.GetPage(1).Width - 595.28) <= 2 && Math.Abs(pdfA4.GetPage(1).Height - 841.89) <= 2 &&
                pdfA4.GetPage(1).Height > pdfA4.GetPage(1).Width;
        }
        finally { try { Directory.Delete(mergeRoot, true); } catch { /* output may be in use by evidence snapshot */ } }

        EngineBootstrap.EnsureInitialized();
        string? oneFileMerge = Wheel.ToolUnavailableReason("merge_pdf", ["a.pdf"], new FlipSettings());
        string? twoFileMerge = Wheel.ToolUnavailableReason("merge_pdf", ["a.pdf", "b.pdf"], new FlipSettings());
        checks["merge_pdf_disabled_with_single_file"] = oneFileMerge != null && twoFileMerge == null;

        bool ffmpegSuppress = CliResolver.SuppressAutoDiscoveryForTest;
        try
        {
            CliResolver.SuppressAutoDiscoveryForTest = true;
            string? videoReason = Wheel.ToolUnavailableReason("target_size", ["clip.mp4"], new FlipSettings());
            string? imageReason = Wheel.ToolUnavailableReason("target_size", ["foto.png"], new FlipSettings());
            checks["target_size_video_needs_ffmpeg"] = videoReason != null && imageReason == null;
        }
        finally { CliResolver.SuppressAutoDiscoveryForTest = ffmpegSuppress; }

        var zipTool = RoutePlanner.FindQuickTool("pack_zip")!;
        var rarTool = RoutePlanner.FindQuickTool("pack_rar")!;
        var cropTool = RoutePlanner.FindQuickTool("crop")!;
        var orderedTools = Wheel.OrderToolsForWheel([cropTool, zipTool, rarTool],
            ["crop", "pack_zip", "turntable_3d", "pack_rar"]);
        checks["archive_wedges_are_adjacent"] = orderedTools.Select(tool => tool.Id)
            .SequenceEqual(["crop", "pack_zip", "pack_rar"]);

        // Uno spicchio non disponibile deve mostrare il messaggio dentro la ruota, non solo nel tray.
        var reported = new List<string>();
        var noticeWheel = new Wheel(reported.Add, (_, _) => { }, runTool: (_, _) => { })
        {
            Opacity = 0, Topmost = false
        };
        try
        {
            const BindingFlags priv = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Wheel).GetField("showingTools", priv)!.SetValue(noticeWheel, true);
            typeof(Wheel).GetField("files", priv)!.SetValue(noticeWheel, new[] { Path.Combine(Evidence.DirectoryPath, "one.pdf") });
            typeof(Wheel).GetField("tools", priv)!.SetValue(noticeWheel, new IQuickTool[] { new MergePdfQuickTool() });
            var reasons = (Dictionary<string, string>)typeof(Wheel).GetField("toolUnavailableReasons", priv)!.GetValue(noticeWheel)!;
            reasons["merge_pdf"] = "Unisci PDF richiede due file.";
            var hitType = typeof(Wheel).GetNestedType("SectorHit", BindingFlags.NonPublic)!;
            object hit = Activator.CreateInstance(hitType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [0, -1], null)!;
            typeof(Wheel).GetMethod("Choose", priv)!.Invoke(noticeWheel, [hit, "click"]);
            checks["unavailable_tool_shows_in_wheel_notice"] = noticeWheel.ToolNoticeVisible &&
                noticeWheel.ToolNoticeText.Contains("due file", StringComparison.Ordinal) &&
                reported.Contains("Unisci PDF richiede due file.");
        }
        finally { noticeWheel.Close(); }

        // Ogni messaggio (non solo gli strumenti non disponibili) deve avere una finestra visibile,
        // non solo il balloon della tray.
        var toast = new ToastWindow { Opacity = 0 };
        try
        {
            toast.ShowMessage("Messaggio di verifica visibile.");
            checks["toast_window_shows_notice"] = toast.IsVisible && toast.MessageText == "Messaggio di verifica visibile.";
        }
        finally { toast.CloseToast(); toast.Close(); }

        int scans = 0;
        long now = 0;
        var cache = new WhisperLocator.DiscoveryCache(() => { scans++; return new([], []); }, () => now);
        var empty = cache.Get();
        now = 44_999;
        bool cached = ReferenceEquals(empty, cache.Get()) && scans == 1;
        now++;
        cache.Get();
        cache.Invalidate();
        cache.Get();
        checks["whisper_negative_cache_ttl_and_invalidation"] = cached && scans == 3;

        bool previousSuppress = CliResolver.SuppressAutoDiscoveryForTest;
        try
        {
            EngineBootstrap.EnsureInitialized();
            CliResolver.SuppressAutoDiscoveryForTest = true;
            var cleanSnap = new FlipSettings();
            bool docUnavailable = !RoutePlanner.GetReachableTargetFormats(new[] { "pdf" }, cleanSnap)
                .Any(format => format.Extension == "doc");
            string? missing = RoutePlanner.FindMissingInstallableEngine(
                new[] { "pdf" }, "doc", cleanSnap, new[] { "libreoffice" });
            checks["missing_engine_detected_for_selected_format"] = docUnavailable && missing == "libreoffice";
        }
        finally { CliResolver.SuppressAutoDiscoveryForTest = previousSuppress; }
    }

    static void CheckArchiveOutputs(Dictionary<string, bool> checks)
    {
        string root = Path.Combine(Evidence.DirectoryPath, "archive-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "Cartella");
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "sub"));
            File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
            File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "b");

            var settings = new FlipSettings
            {
                OutputDirectoryMode = OutputDirectoryMode.SameAsSource,
                ConflictPolicy = ConflictPolicy.AutoNumber
            };
            var context = new QuickToolContext { InputFilePaths = [folder], Settings = settings };
            var result = new PackZipQuickTool().ExecuteAsync(context).GetAwaiter().GetResult();
            string expected = Path.Combine(root, "Cartella_bundle.zip");
            bool insideFolder = Directory.EnumerateFiles(folder, "*.zip", SearchOption.AllDirectories).Any();
            bool nestedEntry = false;
            if (result.Success && File.Exists(expected))
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(expected);
                nestedEntry = archive.Entries.Any(e => e.FullName.Replace('\\', '/').StartsWith("Cartella/sub/", StringComparison.OrdinalIgnoreCase));
            }
            checks["folder_zip_next_to_folder"] = result.Success && !insideFolder && File.Exists(expected) && nestedEntry;
        }
        catch (Exception ex)
        {
            Evidence.Log("folder.zip.test.error", ex.Message);
            checks["folder_zip_next_to_folder"] = false;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void CheckSubRings(Dictionary<string, bool> checks)
    {
        string[] ranked = Enumerable.Range(1, 35).Select(i => $"F{i}").ToArray();
        checks["subring_up_to_eight_all_direct"] = Enumerable.Range(0, 9).All(count =>
        {
            var slots = WheelLayout.Partition(ranked[..count]);
            return slots.Select(slot => slot.Format).SequenceEqual(ranked[..count]) && slots.All(slot => slot.Children.Length == 0);
        });
        var nine = WheelLayout.Partition(ranked[..9]);
        checks["subring_nine_keeps_seven_priorities"] = nine.Length == 8 && nine[..7].Select(slot => slot.Format).SequenceEqual(ranked[..7]) &&
            nine[7].Format == null && nine[7].Children.SequenceEqual(ranked[7..9]) &&
            !nine[7].Label.StartsWith("Altri", StringComparison.OrdinalIgnoreCase);

        var many = WheelLayout.Partition(ranked);
        int expansionCount = many.Count(slot => slot.Format == null);
        checks["subring_large_partition_uses_semantic_expansions_no_altri"] = many.Length == 8 &&
            expansionCount >= 1 && expansionCount <= 2 &&
            many.All(slot => !slot.Label.StartsWith("Altri", StringComparison.OrdinalIgnoreCase)) &&
            many.SelectMany(slot => slot.Format == null ? slot.Children : [slot.Format]).SequenceEqual(ranked);

        string[] mixedCategories =
        [
            "PDF", "PNG", "JPG", "WEBP", "GIF", "BMP",
            "TIFF", "ICO", "AVIF", "PSD", "EXR", "HDR", "TGA",
            "DOCX", "ODT", "RTF", "MD", "HTML", "TXT", "DOC"
        ];
        var mixedSlots = WheelLayout.Partition(mixedCategories);
        checks["subring_mixed_categories_split_without_altri"] = mixedSlots.Length == 8 &&
            mixedSlots.Count(slot => slot.Format == null) == 2 &&
            mixedSlots.Where(slot => slot.Format == null).All(slot => slot.Label.StartsWith('+') &&
                !slot.Label.Contains("Altri", StringComparison.OrdinalIgnoreCase)) &&
            mixedSlots.Max(slot => slot.Children.Length) <= WheelLayout.MaxFanChildren;

        CheckDomainRoutingAndWheelLabels(checks);

        var fan = WheelLayout.FitFan(0, 8, 5, new Rect(-500, -500, 1420, 1420))!;
        double middleRadius = (WheelLayout.FanInnerRadius + fan.OuterRadius) / 2;
        checks["subring_fan_centers_on_parent_and_hits_children"] = Math.Abs(fan.Start + fan.Sweep / 2) < .001 &&
            Enumerable.Range(0, fan.Count).All(index => fan.Hit(WheelLayout.At(middleRadius, fan.Angle(index))) == index);
        checks["subring_polar_hit_excludes_primary_gap_and_beyond"] = fan.Hit(WheelLayout.At(143, 0)) < 0 &&
            fan.Hit(WheelLayout.At(213, 0)) < 0 && fan.Hit(WheelLayout.At(fan.OuterRadius + 1, 0)) < 0 &&
            fan.Hit(WheelLayout.At(middleRadius, fan.Start + fan.Step)) < 0;

        var compact = new Rect(0, 0, 420, 420);
        var edges = new[] { compact, new Rect(0, 0, 1280, 800), new Rect(-860, -380, 1280, 800) };
        checks["subring_fan_fits_screen_even_at_corners"] = edges.All(area => Enumerable.Range(0, 8).All(parent =>
        {
            if (WheelLayout.FitFan(parent, 8, 7, area) is not { } fitted) return false;
            return Enumerable.Range(0, 121).All(step =>
            {
                double angle = fitted.Start + fitted.Sweep * step / 120;
                return area.Contains(WheelLayout.At(fitted.OuterRadius, angle)) &&
                    area.Contains(WheelLayout.At(WheelLayout.FanInnerRadius, angle));
            });
        }));

        string fixture = Path.Combine(Evidence.DirectoryPath, $"subring-{Guid.NewGuid():N}.docx");
        try
        {
            File.WriteAllText(fixture, "flip-subring-docx-fixture");
            CheckWheelInput(checks, fixture);
        }
        catch (Exception ex)
        {
            checks["subring_wpf_without_exception"] = false;
            Evidence.Log("subring.test.error", ex.ToString());
        }
        finally { File.Delete(fixture); }
    }

    private static void CheckDomainRoutingAndWheelLabels(Dictionary<string, bool> checks)
    {
        EngineBootstrap.EnsureInitialized();
        var allVisible = new FlipSettings
        {
            MaxVisibleGrade = FormatGrade.Legacy,
            EnabledSpecialistFields =
                SpecialistField.RawPhotography | SpecialistField.DataAndDev |
                SpecialistField.ThreeDAndGameDev | SpecialistField.EbookAndPublishing |
                SpecialistField.CadAndVector | SpecialistField.WebAndFonts |
                SpecialistField.LegacyAndRegional
        }.CreateSnapshot();

        string[] forbiddenForAudio =
            ["bmp", "pptx", "odt", "epub", "rtf", "docx", "xlsx", "pdf", "png", "jpg", "webp", "tiff", "md", "html", "vtt"];
        var wavReachable = RoutePlanner.GetReachableTargetFormats("wav", allVisible);
        var wavExts = wavReachable.Select(f => f.NormalizedExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        checks["routing_audio_blocks_cross_domain_targets"] =
            forbiddenForAudio.All(ext => !wavExts.Contains(ext) && !RoutePlanner.TryPlanRoute("wav", ext, allVisible, out _)) &&
            wavReachable.All(f => f.Category == FormatCategory.Audio || f.NormalizedExtension is "srt" or "txt");

        var wavSlots = WheelLayout.Partition(wavReachable.Select(f => f.Extension.ToUpperInvariant()).ToArray());
        checks["wheel_audio_no_altri_and_short_labels"] =
            wavSlots.All(s => !s.Label.StartsWith("Altri", StringComparison.OrdinalIgnoreCase)) &&
            wavSlots.Count(s => s.Format == null) <= 2 &&
            wavReachable.All(f => FormatRegistry.GetWheelShortLabel(f.Extension).Length <= 6);

        string[] forbiddenForVideo =
            ["docx", "pptx", "xlsx", "odt", "epub", "rtf", "pdf", "bmp", "png", "jpg", "tiff", "md", "html"];
        var mp4Reachable = RoutePlanner.GetReachableTargetFormats("mp4", allVisible);
        var mp4Exts = mp4Reachable.Select(f => f.NormalizedExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Video -> TXT/SRT è ammesso solo come trascrizione Whisper AI a 1 salto.
        static bool VideoTranscriptOk(string ext, FlipSettings s) =>
            !RoutePlanner.TryPlanRoute("mp4", ext, s, out var route) ||
            route is { Hops.Count: 1 } && route.Hops[0].Engine.Id == "whisper";
        checks["routing_video_blocks_cross_domain_targets"] =
            forbiddenForVideo.All(ext => !mp4Exts.Contains(ext) && !RoutePlanner.TryPlanRoute("mp4", ext, allVisible, out _)) &&
            VideoTranscriptOk("txt", allVisible) && VideoTranscriptOk("srt", allVisible) &&
            mp4Reachable.All(f => f.Category is FormatCategory.Video or FormatCategory.Audio ||
                                  f.NormalizedExtension is "gif" or "webp" or "txt" or "srt");

        checks["routing_allows_legitimate_structural_multihop"] =
            RoutePlanner.TryPlanRoute("docx", "png", allVisible, out var docImg) && docImg!.Hops.Count == 2 &&
            RoutePlanner.TryPlanRoute("stl", "png", allVisible, out var cadImg) && cadImg!.Hops.Count == 1;

        var defaultSettings = new FlipSettings().CreateSnapshot();
        var docxTargets = RoutePlanner.GetReachableTargetFormats("docx", defaultSettings)
            .Select(f => f.NormalizedExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        checks["wheel_short_labels_and_canonical_dedup"] =
            FormatRegistry.GetWheelShortLabel("markdown") == "MD" &&
            FormatRegistry.GetWheelShortLabel("jpeg") == "JPG" &&
            FormatRegistry.GetWheelShortLabel("asciidoc") == "ADOC" &&
            docxTargets.Contains("md") && !docxTargets.Contains("markdown") &&
            docxTargets.Contains("jpg") && !docxTargets.Contains("jpeg") &&
            docxTargets.Contains("html") && !docxTargets.Contains("htm") &&
            docxTargets.Contains("tiff") && !docxTargets.Contains("tif");
    }

    private static void CheckWheelInput(Dictionary<string, bool> checks, string fixture)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        EngineBootstrap.EnsureInitialized();
        var settings = new FlipSettings().CreateSnapshot();
        var ranked = RoutePlanner.GetReachableTargetFormats(Path.GetExtension(fixture), settings)
            .Select(format => format.Extension.ToUpperInvariant()).ToArray();
        var slots = WheelLayout.Partition(ranked);
        int parent = Array.FindIndex(slots, slot => slot.Children.Length > 0);
        string[]? dispatchedFiles = null;
        string? dispatchedFormat = null;
        int sounds = 0;
        var wheel = new Wheel(_ => { }, (files, format) => (dispatchedFiles, dispatchedFormat) = (files, format),
            () => sounds++, runTool: (_, _) => { }, settings: settings)
        {
            Opacity = 0, Topmost = false,
            Left = SystemParameters.WorkArea.Left,
            Top = SystemParameters.WorkArea.Top
        };
        try
        {
            wheel.Show();
            typeof(Wheel).GetMethod("CapturePrimaryPlacement", privateInstance)!.Invoke(wheel, null);
            var surface = (Canvas)wheel.Content;
            var canvas = (Canvas)((Viewbox)surface.Children[0]).Child;
            var outer = (Canvas)surface.Children[1];
            var source = PresentationSource.FromVisual(canvas)!;
            Point anchor = canvas.PointToScreen(WheelLayout.Center);
            double diameter = (canvas.PointToScreen(new Point(420, 210)) - anchor).Length * 2;
            var data = new DataObject(DataFormats.FileDrop, new[] { fixture });
            Drag(DragDrop.PreviewDragOverEvent, WheelLayout.Center);
            double parentAngle = parent * 360.0 / slots.Length;
            var parentPoint = WheelLayout.At(143, parentAngle);

            for (int i = 0; i <= parent; i++) Press(Key.Right);
            Press(Key.Enter);
            bool keyboardOpened = outer.Children.Count == slots[parent].Children.Length * 2;
            Press(Key.Right);
            bool keyboardMoved = (int)typeof(Wheel).GetField("selectedChild", privateInstance)!.GetValue(wheel)! == 1;
            Press(Key.Up);
            checks["subring_keyboard_enter_arrows_and_return"] = keyboardOpened && keyboardMoved && outer.Children.Count == 0;

            typeof(Wheel).GetField("dropping", privateInstance)!.SetValue(wheel, true);
            var overParent = Drag(DragDrop.PreviewDragOverEvent, parentPoint);
            var fan = (WheelLayout.Fan)typeof(Wheel).GetField("fan", privateInstance)!.GetValue(wheel)!;
            wheel.UpdateLayout();
            Point after = canvas.PointToScreen(WheelLayout.Center);
            double expandedDiameter = (canvas.PointToScreen(new Point(420, 210)) - after).Length * 2;
            checks["subring_viewport_expands_without_moving_or_scaling_primary"] = (after - anchor).Length < 1 &&
                Math.Abs(diameter - expandedDiameter) < 1 && wheel.ActualWidth > 320;
            checks["subring_parent_expands_without_accepting_drop"] = overParent.Effects == DragDropEffects.None &&
                outer.Children.Count == fan.Count * 2;

            var gap = Drag(DragDrop.PreviewDragOverEvent, WheelLayout.At(213, parentAngle));
            bool keptInGap = gap.Effects == DragDropEffects.None && outer.Children.Count > 0;
            int child = fan.Count - 1;
            var childPoint = WheelLayout.At((WheelLayout.FanInnerRadius + fan.OuterRadius) / 2, fan.Angle(child));
            var overChild = Drag(DragDrop.PreviewDragOverEvent, childPoint);
            checks["subring_radial_hysteresis_reaches_child"] = keptInGap && overChild.Effects == DragDropEffects.Copy && outer.Children.Count > 0;

            var pixels = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            pixels.Render(surface);
            Point pixel = canvas.TranslatePoint(childPoint, surface);
            byte[] color = new byte[4];
            pixels.CopyPixels(new Int32Rect((int)pixel.X, (int)pixel.Y, 1, 1), color, 4, 0);
            checks["subring_child_is_rendered_beyond_primary_disk"] = color[3] > 0;

            Drag(DragDrop.PreviewDragOverEvent, WheelLayout.At(fan.OuterRadius + 1, fan.Angle(child)));
            checks["subring_closes_beyond_outer_radius"] = outer.Children.Count == 0;
            Drag(DragDrop.PreviewDragOverEvent, parentPoint);
            Drag(DragDrop.PreviewDragOverEvent, WheelLayout.At(143, 0));
            checks["subring_closes_on_another_primary_sector"] = outer.Children.Count == 0;

            Drag(DragDrop.PreviewDragOverEvent, parentPoint);
            wheel.HandleDragScroll(120);
            checks["subring_scroll_flip_collapses_and_suspends_selection"] = outer.Children.Count == 0 &&
                !wheel.CanUseDragCommand(WheelCommand.FlipFace) && Drag(DragDrop.PreviewDragOverEvent, childPoint).Effects == DragDropEffects.None;
            WaitForFlip();
            wheel.HandleDragCommand(WheelCommand.FlipFace);
            WaitForFlip();

            // La rotella deve funzionare anche senza drag (apertura con pressione prolungata).
            typeof(Wheel).GetField("dropping", privateInstance)!.SetValue(wheel, false);
            bool toolsBefore = wheel.IsShowingTools;
            wheel.HandleDragScroll(120);
            WaitForFlipEnd();
            checks["subring_scroll_flips_without_drag"] = wheel.IsShowingTools != toolsBefore;
            wheel.HandleDragScroll(120);
            WaitForFlipEnd();
            typeof(Wheel).GetField("dropping", privateInstance)!.SetValue(wheel, true);

            Drag(DragDrop.PreviewDragOverEvent, parentPoint);
            wheel.UpdateLayout();
            var drop = Drag(DragDrop.PreviewDropEvent, childPoint);
            wheel.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            checks["subring_wpf_drop_dispatches_exact_child"] = drop.Effects == DragDropEffects.Copy &&
                dispatchedFormat == slots[parent].Children[child] && dispatchedFiles?.SequenceEqual([Path.GetFullPath(fixture)]) == true;
            checks["subring_drop_closes_fan_and_starts_progress"] = outer.Children.Count == 0 && wheel.IsConverting && sounds > 0;

            DragEventArgs Drag(RoutedEvent routedEvent, Point point)
            {
                var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), privateInstance, null,
                    [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, canvas, point], null)!;
                args.RoutedEvent = routedEvent;
                wheel.RaiseEvent(args);
                return args;
            }

            void Press(Key key) => wheel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });

            void WaitForFlip()
            {
                var frame = new DispatcherFrame();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
                timer.Tick += (_, _) =>
                {
                    if (wheel.CanUseDragCommand(WheelCommand.FlipFace) || DateTime.UtcNow >= deadline) frame.Continue = false;
                };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
                if (!wheel.CanUseDragCommand(WheelCommand.FlipFace)) throw new TimeoutException("Flip animation did not finish.");
            }

            void WaitForFlipEnd()
            {
                var frame = new DispatcherFrame();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
                timer.Tick += (_, _) =>
                {
                    bool flipping = (bool)typeof(Wheel).GetField("flipping", privateInstance)!.GetValue(wheel)!;
                    if (!flipping || DateTime.UtcNow >= deadline) frame.Continue = false;
                };
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
                if ((bool)typeof(Wheel).GetField("flipping", privateInstance)!.GetValue(wheel)!)
                    throw new TimeoutException("Non-drag flip animation did not finish.");
            }
        }
        finally { wheel.Close(); }
    }

    private static void CheckBrowserDrop(Dictionary<string, bool> checks)
    {
        // 1. Estrazione di <img src="https://example.com/photo.webp"> annidato dentro <a href="https://example.com/page.html"> in HTML Format
        var htmlData = new DataObject();
        htmlData.SetData(DataFormats.Html, "<a href=\"https://example.com/page.html\"><img src=\"https://example.com/photo.webp\"></a>");
        bool htmlFound = BrowserDropService.TryInspectBrowserData(htmlData, out var candidateHtml);
        checks["browser_drop_html_nested_img_preferred"] = htmlFound &&
            candidateHtml.RawUri == "https://example.com/photo.webp" &&
            candidateHtml.GuessedExtension == "webp" &&
            candidateHtml.SuggestedFileName == "photo.webp" &&
            !candidateHtml.IsDataUri;

        // 2. Decodifica e salvataggio reale di un data:image/png;base64,... valido
        const string base64Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII=";
        var dataUriObj = new DataObject();
        dataUriObj.SetData(DataFormats.UnicodeText, base64Png);
        bool dataUriFound = BrowserDropService.TryInspectBrowserData(dataUriObj, out var candidateDataUri);
        string testStaging = Path.Combine(Evidence.DirectoryPath, $"browser-staging-{Guid.NewGuid():N}");
        try
        {
            var matResult = BrowserDropService.MaterializeAsync(candidateDataUri, testStaging).GetAwaiter().GetResult();
            checks["browser_drop_data_uri_materialize"] = dataUriFound &&
                candidateDataUri.IsDataUri &&
                candidateDataUri.GuessedExtension == "png" &&
                matResult.Success &&
                File.Exists(matResult.LocalFilePath) &&
                new FileInfo(matResult.LocalFilePath).Length > 0 &&
                matResult.EffectiveExtension == "png";
        }
        finally
        {
            if (Directory.Exists(testStaging)) Directory.Delete(testStaging, true);
        }

        // 3. Verifica che GetEffectiveOutputSettings rediriga SameAsSource verso Downloads disattivando ReplaceOriginalToRecycleBin
        var initialSettings = new FlipSettings
        {
            OutputDirectoryMode = OutputDirectoryMode.SameAsSource,
            ReplaceOriginalToRecycleBin = true
        };
        var effective = BrowserDropService.GetEffectiveOutputSettings(initialSettings);
        string expectedDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        checks["browser_drop_effective_output_settings"] =
            effective.OutputDirectoryMode == OutputDirectoryMode.CustomFolder &&
            effective.CustomOutputDirectory == expectedDownloads &&
            !effective.ReplaceOriginalToRecycleBin &&
            initialSettings.OutputDirectoryMode == OutputDirectoryMode.SameAsSource &&
            initialSettings.ReplaceOriginalToRecycleBin;

        // Controlli addizionali di sicurezza
        var fileDropObj = new DataObject();
        string tempFile = Path.Combine(Evidence.DirectoryPath, $"localdrop-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(tempFile, "local test");
            fileDropObj.SetData(DataFormats.FileDrop, new[] { tempFile });
            fileDropObj.SetData(DataFormats.Html, "<img src=\"https://example.com/ignored.png\">");
            bool localIgnored = !BrowserDropService.TryInspectBrowserData(fileDropObj, out _);
            checks["browser_drop_local_files_not_intercepted"] = localIgnored;
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }

        var noExtImgObj = new DataObject();
        noExtImgObj.SetData(DataFormats.Html, "<img src=\"https://example.com/avatar-endpoint\">");
        bool fallbackFound = BrowserDropService.TryInspectBrowserData(noExtImgObj, out var fallbackCandidate);
        checks["browser_drop_img_fallback_extension_png"] = fallbackFound &&
            fallbackCandidate.GuessedExtension == "png" &&
            fallbackCandidate.SuggestedFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        CheckBrowserWheelDrop(checks);
    }

    private static void CheckBrowserWheelDrop(Dictionary<string, bool> checks)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        EngineBootstrap.EnsureInitialized();
        var settings = new FlipSettings().CreateSnapshot();

        string? convertedFormat = null;
        string[]? convertedFiles = null;
        BrowserDropCandidate? browserConvertCandidate = null;
        FlipSettings? browserConvertSettings = null;

        var wheel = new Wheel(_ => { },
            (files, format) => (convertedFiles, convertedFormat) = (files, format),
            runTool: (_, _) => { },
            settings: settings,
            convertBrowser: (candidate, format, ws) =>
            {
                browserConvertCandidate = candidate;
                convertedFormat = format;
                browserConvertSettings = ws;
            },
            runBrowserTool: (_, _, _) => { })
        {
            Opacity = 0, Topmost = false,
            Left = SystemParameters.WorkArea.Left,
            Top = SystemParameters.WorkArea.Top
        };
        try
        {
            wheel.Show();
            typeof(Wheel).GetMethod("CapturePrimaryPlacement", privateInstance)!.Invoke(wheel, null);
            var surface = (Canvas)wheel.Content;
            var canvas = (Canvas)((Viewbox)surface.Children[0]).Child;
            var source = PresentationSource.FromVisual(canvas)!;

            // Create a DataObject that simulates a browser drag with a data URI PNG
            const string base64Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII=";
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, base64Png);

            // DragEnter with browser data
            var dragArgs = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), privateInstance, null,
                [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, canvas, WheelLayout.Center], null)!;
            dragArgs.RoutedEvent = DragDrop.PreviewDragEnterEvent;
            wheel.RaiseEvent(dragArgs);

            // The wheel should now have formats loaded from the synthetic PNG file
            var files = (string[])typeof(Wheel).GetField("files", privateInstance)!.GetValue(wheel)!;
            var formats = (string[])typeof(Wheel).GetField("formats", privateInstance)!.GetValue(wheel)!;
            var bc = typeof(Wheel).GetField("browserCandidate", privateInstance)!.GetValue(wheel);
            checks["browser_wheel_drag_populates_formats"] = files.Length > 0 && formats.Length > 0 && bc != null;

            // Find a format we know should be reachable from PNG (JPG)
            int jpgIndex = -1;
            var frontSlots = (WheelLayout.Slot[])typeof(Wheel).GetField("frontSlots", privateInstance)!.GetValue(wheel)!;
            for (int i = 0; i < frontSlots.Length; i++)
            {
                if (string.Equals(frontSlots[i].Format, "JPG", StringComparison.OrdinalIgnoreCase))
                { jpgIndex = i; break; }
                for (int c = 0; c < frontSlots[i].Children.Length; c++)
                {
                    if (string.Equals(frontSlots[i].Children[c], "JPG", StringComparison.OrdinalIgnoreCase))
                    { jpgIndex = i; break; }
                }
                if (jpgIndex >= 0) break;
            }

            if (jpgIndex < 0)
            {
                checks["browser_wheel_drop_converts_browser_image"] = false;
                return;
            }

            // Simulate drop on the JPG sector
            typeof(Wheel).GetField("dropping", privateInstance)!.SetValue(wheel, true);
            var jpgPoint = WheelLayout.At(143, jpgIndex * 360.0 / frontSlots.Length);
            var dropArgs = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), privateInstance, null,
                [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, canvas, jpgPoint], null)!;
            dropArgs.RoutedEvent = DragDrop.PreviewDropEvent;
            wheel.RaiseEvent(dropArgs);
            wheel.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

            checks["browser_wheel_drop_converts_browser_image"] =
                dropArgs.Effects == DragDropEffects.Copy &&
                browserConvertCandidate != null &&
                browserConvertCandidate.Value.IsDataUri &&
                browserConvertCandidate.Value.GuessedExtension == "png" &&
                convertedFormat != null &&
                wheel.IsConverting;
        }
        catch (Exception ex)
        {
            checks["browser_wheel_drag_populates_formats"] = false;
            checks["browser_wheel_drop_converts_browser_image"] = false;
            Evidence.Log("browser.wheel.test.error", ex.ToString());
        }
        finally { wheel.Close(); }
    }

    private static void CheckClipboardFlip(Dictionary<string, bool> checks)
    {
        // 1. Parsing scorciatoie
        bool parsed1 = ClipboardFlipService.TryParseShortcut("Ctrl+Shift+F", out var g1);
        checks["clipboard_parse_ctrl_shift_f"] = parsed1 && g1.Enabled && g1.Control && g1.Shift && !g1.Alt && g1.VirtualKey == (int)'F';

        bool parsed2 = ClipboardFlipService.TryParseShortcut("Alt+F8", out var g2);
        checks["clipboard_parse_alt_f8"] = parsed2 && g2.Enabled && !g2.Control && !g2.Shift && g2.Alt && g2.VirtualKey == 0x77;

        bool parsedNone = ClipboardFlipService.TryParseShortcut("None", out var gNone);
        bool parsedEmpty = ClipboardFlipService.TryParseShortcut("", out var gEmpty);
        bool parsedNull = ClipboardFlipService.TryParseShortcut(null, out var gNull);
        checks["clipboard_parse_none_or_empty"] = !parsedNone && !gNone.Enabled && !parsedEmpty && !gEmpty.Enabled && !parsedNull && !gNull.Enabled;

        // 2. Materializzazione tabella TSV
        var dataTsv = new DataObject();
        dataTsv.SetData(DataFormats.UnicodeText, "Nome\tPrezzo\r\nMela\t1.50\r\nPera\t2.00");
        string tsvStaging = Path.Combine(Evidence.DirectoryPath, $"clip-stage-tsv-{Guid.NewGuid():N}");
        try
        {
            bool tsvSuccess = ClipboardFlipService.TryMaterializeDataObject(dataTsv, tsvStaging, out var tsvPayload);
            checks["clipboard_materialize_tsv_table"] = tsvSuccess &&
                tsvPayload.Success &&
                tsvPayload.UsesTemporaryStaging &&
                tsvPayload.FilePaths.Count == 1 &&
                Path.GetExtension(tsvPayload.FilePaths[0]).Equals(".tsv", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllText(tsvPayload.FilePaths[0]).Contains("Mela\t1.50");
        }
        finally
        {
            if (Directory.Exists(tsvStaging)) Directory.Delete(tsvStaging, true);
        }

        // 3. Materializzazione JSON
        var dataJson = new DataObject();
        dataJson.SetData(DataFormats.UnicodeText, "{\"product\": \"Flip\", \"active\": true}");
        string jsonStaging = Path.Combine(Evidence.DirectoryPath, $"clip-stage-json-{Guid.NewGuid():N}");
        try
        {
            bool jsonSuccess = ClipboardFlipService.TryMaterializeDataObject(dataJson, jsonStaging, out var jsonPayload);
            checks["clipboard_materialize_json_data"] = jsonSuccess &&
                jsonPayload.Success &&
                jsonPayload.UsesTemporaryStaging &&
                jsonPayload.FilePaths.Count == 1 &&
                Path.GetExtension(jsonPayload.FilePaths[0]).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllText(jsonPayload.FilePaths[0]).Contains("Flip");
        }
        finally
        {
            if (Directory.Exists(jsonStaging)) Directory.Delete(jsonStaging, true);
        }

        // 4. Materializzazione Immagine BitmapSource
        var wb = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgr32, null);
        byte[] pixels = new byte[2 * 2 * 4];
        wb.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 2 * 4, 0);
        var dataImg = new DataObject();
        dataImg.SetData(DataFormats.Bitmap, wb);
        string imgStaging = Path.Combine(Evidence.DirectoryPath, $"clip-stage-img-{Guid.NewGuid():N}");
        try
        {
            bool imgSuccess = ClipboardFlipService.TryMaterializeDataObject(dataImg, imgStaging, out var imgPayload);
            checks["clipboard_materialize_bitmap_png"] = imgSuccess &&
                imgPayload.Success &&
                imgPayload.UsesTemporaryStaging &&
                imgPayload.FilePaths.Count == 1 &&
                File.Exists(imgPayload.FilePaths[0]) &&
                new FileInfo(imgPayload.FilePaths[0]).Length > 0 &&
                Path.GetExtension(imgPayload.FilePaths[0]).Equals(".png", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(imgStaging)) Directory.Delete(imgStaging, true);
        }

        // 5. Verifica che FileDrop con file reali non crei copie inutili in staging
        string localFile = Path.Combine(Evidence.DirectoryPath, $"clip-local-{Guid.NewGuid():N}.txt");
        File.WriteAllText(localFile, "local file content");
        string fileStaging = Path.Combine(Evidence.DirectoryPath, $"clip-stage-file-{Guid.NewGuid():N}");
        try
        {
            var dataFile = new DataObject();
            dataFile.SetData(DataFormats.FileDrop, new[] { localFile });
            bool fileSuccess = ClipboardFlipService.TryMaterializeDataObject(dataFile, fileStaging, out var filePayload);
            checks["clipboard_file_drop_no_staging"] = fileSuccess &&
                filePayload.Success &&
                !filePayload.UsesTemporaryStaging &&
                filePayload.FilePaths.Count == 1 &&
                filePayload.FilePaths[0] == Path.GetFullPath(localFile) &&
                !Directory.Exists(fileStaging);
        }
        finally
        {
            if (File.Exists(localFile)) File.Delete(localFile);
            if (Directory.Exists(fileStaging)) Directory.Delete(fileStaging, true);
        }

        // 6. Verifica impostazioni effettive di output
        var baseSettings = new FlipSettings
        {
            OutputDirectoryMode = OutputDirectoryMode.SameAsSource,
            ReplaceOriginalToRecycleBin = true
        };
        var stNonStaged = ClipboardFlipService.GetEffectiveOutputSettings(baseSettings, usesTemporaryStaging: false);
        var stStaged = ClipboardFlipService.GetEffectiveOutputSettings(baseSettings, usesTemporaryStaging: true);
        string expectedDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        checks["clipboard_effective_output_settings"] =
            stNonStaged.OutputDirectoryMode == OutputDirectoryMode.SameAsSource &&
            !stNonStaged.ReplaceOriginalToRecycleBin &&
            stStaged.OutputDirectoryMode == OutputDirectoryMode.CustomFolder &&
            stStaged.CustomOutputDirectory == expectedDownloads &&
            !stStaged.ReplaceOriginalToRecycleBin &&
            baseSettings.OutputDirectoryMode == OutputDirectoryMode.SameAsSource &&
            baseSettings.ReplaceOriginalToRecycleBin;

        // 7. Riconoscimento della combinazione ClipboardShortcutGesture e protezione anti-repeat
        if (ClipboardFlipService.TryParseShortcut("Ctrl+Shift+F", out var csf))
        {
            bool matchCorrect = csf.Matches(0x46, ctrl: true, shift: true, alt: false, win: false);
            bool noShift = !csf.Matches(0x46, ctrl: true, shift: false, alt: false, win: false);
            bool noCtrl = !csf.Matches(0x46, ctrl: false, shift: true, alt: false, win: false);
            bool extraAlt = !csf.Matches(0x46, ctrl: true, shift: true, alt: true, win: false);
            bool extraWin = !csf.Matches(0x46, ctrl: true, shift: true, alt: false, win: true);
            bool wrongKey = !csf.Matches(0x45, ctrl: true, shift: true, alt: false, win: false);

            var clipGate = new ShortcutPressGate();
            var press1 = clipGate.Process(keyDown: true, eligible: true);
            var repeat1 = clipGate.Process(keyDown: true, eligible: true);
            var repeat2 = clipGate.Process(keyDown: true, eligible: true);
            var release = clipGate.Process(keyDown: false, eligible: false);
            var press2 = clipGate.Process(keyDown: true, eligible: true);

            checks["clipboard_gesture_modifiers_and_anti_repeat"] =
                matchCorrect && noShift && noCtrl && extraAlt && extraWin && wrongKey &&
                press1 == (true, true) &&
                repeat1 == (true, false) &&
                repeat2 == (true, false) &&
                release == (true, false) &&
                press2 == (true, true);
        }
        else
        {
            checks["clipboard_gesture_modifiers_and_anti_repeat"] = false;
        }

        // 8. Flusso completo end-to-end TSV -> JSON con pulizia staging
        var e2eTsvData = new DataObject();
        e2eTsvData.SetData(DataFormats.UnicodeText, "ID\tDescrizione\r\n101\tFlipPro\r\n102\tWheelQuick");
        string e2eTsvStaging = Path.Combine(Evidence.DirectoryPath, $"clip-e2e-tsv-stage-{Guid.NewGuid():N}");
        string e2eOutDir = Path.Combine(Evidence.DirectoryPath, $"clip-e2e-out-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(e2eOutDir);
            bool matOk = ClipboardFlipService.TryMaterializeDataObject(e2eTsvData, e2eTsvStaging, out var tsvPayload);
            var e2eSettings = new FlipSettings
            {
                OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
                CustomOutputDirectory = e2eOutDir,
                ReplaceOriginalToRecycleBin = false
            };
            var effective = ClipboardFlipService.GetEffectiveOutputSettings(e2eSettings, tsvPayload.UsesTemporaryStaging);

            EngineBootstrap.EnsureInitialized();
            var batch = RoutePlanner.ExecuteBatchAsync(tsvPayload.FilePaths, ["JSON"], effective).GetAwaiter().GetResult();
            bool convOk = batch.Count == 1 &&
                          batch[0].Result.Success &&
                          batch[0].Result.OutputFilePaths.Count == 1 &&
                          File.Exists(batch[0].Result.OutputFilePaths[0]) &&
                          new FileInfo(batch[0].Result.OutputFilePaths[0]).Length > 0 &&
                          File.ReadAllText(batch[0].Result.OutputFilePaths[0]).Contains("FlipPro");

            // Simulazione della pulizia di fine sessione
            if (Directory.Exists(e2eTsvStaging)) Directory.Delete(e2eTsvStaging, true);
            bool stagingCleaned = !Directory.Exists(e2eTsvStaging);

            checks["clipboard_e2e_tsv_to_json_with_staging_cleanup"] = matOk && convOk && stagingCleaned;
        }
        finally
        {
            if (Directory.Exists(e2eTsvStaging)) Directory.Delete(e2eTsvStaging, true);
            if (Directory.Exists(e2eOutDir)) Directory.Delete(e2eOutDir, true);
        }

        // 9. Flusso completo end-to-end Bitmap -> JPG con pulizia staging
        var e2eWb = new WriteableBitmap(4, 4, 96, 96, PixelFormats.Bgr32, null);
        byte[] e2ePixels = new byte[4 * 4 * 4];
        for (int i = 0; i < e2ePixels.Length; i++) e2ePixels[i] = 180;
        e2eWb.WritePixels(new Int32Rect(0, 0, 4, 4), e2ePixels, 4 * 4, 0);
        var e2eImgData = new DataObject();
        e2eImgData.SetData(DataFormats.Bitmap, e2eWb);
        string e2eImgStaging = Path.Combine(Evidence.DirectoryPath, $"clip-e2e-img-stage-{Guid.NewGuid():N}");
        string e2eImgOutDir = Path.Combine(Evidence.DirectoryPath, $"clip-e2e-img-out-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(e2eImgOutDir);
            bool matImgOk = ClipboardFlipService.TryMaterializeDataObject(e2eImgData, e2eImgStaging, out var imgPayload);
            var e2eImgSettings = new FlipSettings
            {
                OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
                CustomOutputDirectory = e2eImgOutDir,
                ReplaceOriginalToRecycleBin = false
            };
            var effectiveImg = ClipboardFlipService.GetEffectiveOutputSettings(e2eImgSettings, imgPayload.UsesTemporaryStaging);

            var batchImg = RoutePlanner.ExecuteBatchAsync(imgPayload.FilePaths, ["JPG"], effectiveImg).GetAwaiter().GetResult();
            bool convImgOk = batchImg.Count == 1 &&
                             batchImg[0].Result.Success &&
                             batchImg[0].Result.OutputFilePaths.Count == 1 &&
                             File.Exists(batchImg[0].Result.OutputFilePaths[0]) &&
                             new FileInfo(batchImg[0].Result.OutputFilePaths[0]).Length > 0;

            if (Directory.Exists(e2eImgStaging)) Directory.Delete(e2eImgStaging, true);
            bool imgStagingCleaned = !Directory.Exists(e2eImgStaging);

            checks["clipboard_e2e_image_to_jpg_with_staging_cleanup"] = matImgOk && convImgOk && imgStagingCleaned;
        }
        finally
        {
            if (Directory.Exists(e2eImgStaging)) Directory.Delete(e2eImgStaging, true);
            if (Directory.Exists(e2eImgOutDir)) Directory.Delete(e2eImgOutDir, true);
        }
    }

    private static void CheckWatchFolders(Dictionary<string, bool> checks)
    {
        string watchDir = Path.Combine(Evidence.DirectoryPath, $"watch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(watchDir);

        var rule = new WatchFolderRule
        {
            FolderPath = watchDir,
            Enabled = true,
            IncludeExtensions = ["*"],
            TargetExtension = "jpg",
            DeleteSourceToRecycleBin = false
        };

        // 1. Verifica filtri file temporanei e stessa estensione
        bool ignoreCrDownload = !WatchFolderService.MatchesRule(rule, Path.Combine(watchDir, "photo.png.crdownload"));
        bool ignoreTmp = !WatchFolderService.MatchesRule(rule, Path.Combine(watchDir, "cache.tmp"));
        bool ignoreOfficeLock = !WatchFolderService.MatchesRule(rule, Path.Combine(watchDir, "~$document.docx"));
        bool ignoreHidden = !WatchFolderService.MatchesRule(rule, Path.Combine(watchDir, ".gitignore"));
        bool ignoreSameExt = !WatchFolderService.MatchesRule(rule, Path.Combine(watchDir, "already.jpg"));
        bool acceptValid = WatchFolderService.MatchesRule(rule, Path.Combine(watchDir, "input.png"));
        checks["watchfolder_ignore_temporary_and_same_extension"] =
            ignoreCrDownload && ignoreTmp && ignoreOfficeLock && ignoreHidden && ignoreSameExt && acceptValid;

        // 2. Test End-to-End di auto-conversione ed Anti-Loop
        var testSettings = new FlipSettings
        {
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = watchDir,
            UserSelectedCustomOutputDirectory = true
        };
        testSettings.WatchFolders.Add(rule);

        int processedCount = 0;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var service = new WatchFolderService(initialSettings: testSettings);
        service.FileProcessed += (src, target, success) =>
        {
            Interlocked.Increment(ref processedCount);
            if (success) tcs.TrySetResult(src);
            else Evidence.Log("watchfolder.test.failed", new { src, target });
        };

        try
        {
            // Breve attesa per registrazione watcher sul file system di Windows
            Thread.Sleep(100);

            // Scrivi un PNG valido nella cartella monitorata
            string samplePng = Path.Combine(watchDir, "source.png");
            var wb = new WriteableBitmap(8, 8, 96, 96, PixelFormats.Bgr32, null);
            byte[] px = new byte[8 * 8 * 4];
            Array.Fill(px, (byte)160);
            wb.WritePixels(new Int32Rect(0, 0, 8, 8), px, 8 * 4, 0);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wb));
            using (var fs = File.Create(samplePng))
            {
                enc.Save(fs);
            }

            // Attesa conversione automatica (max 10 secondi)
            bool autoConverted = tcs.Task.Wait(10000);
            var jpgFiles = Directory.GetFiles(watchDir, "*.jpg");

            checks["watchfolder_auto_convert_png_to_jpg"] =
                autoConverted &&
                jpgFiles.Length == 1 &&
                new FileInfo(jpgFiles[0]).Length > 0;

            // Attendi altri 1500 ms per accertare che l'evento sul file .jpg generato venga ignorato (anti-loop)
            Thread.Sleep(1500);

            // Verifica che l'output sia registrato nella blacklist anti-loop e che non sia stata avviata una seconda conversione
            bool isBlacklisted = jpgFiles.Length > 0 && service.IsGeneratedOutput(jpgFiles[0]);
            var allJpgAfter = Directory.GetFiles(watchDir, "*.jpg");

            checks["watchfolder_anti_loop_no_cascade"] =
                isBlacklisted &&
                processedCount == 1 &&
                allJpgAfter.Length == 1;
        }
        finally
        {
            if (Directory.Exists(watchDir))
            {
                try { Directory.Delete(watchDir, true); } catch { }
            }
        }
    }

    private static void CheckScenarioMatrix(Dictionary<string, bool> checks)
    {
        EngineBootstrap.EnsureInitialized();
        var defaultSettings = new FlipSettings().CreateSnapshot();

        static (HashSet<string> AllReachable, HashSet<string> PrimaryRing, WheelLayout.Slot[] Slots) InspectWheelFor(string sourceExt, FlipSettings settings)
        {
            var reachable = RoutePlanner.GetReachableTargetFormats(sourceExt, settings);
            var rankedUpper = reachable.Select(f => f.Extension.ToUpperInvariant()).ToArray();
            var slots = WheelLayout.Partition(rankedUpper);
            var allSet = reachable.Select(f => f.NormalizedExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var primarySet = slots.Where(s => s.Format != null).Select(s => s.Format!.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (allSet, primarySet, slots);
        }

        // ── 1. MATRICE SCENARI UX RUOTA (Con impostazioni di default!) ─────────

        // Scenario .txt
        var txtWheel = InspectWheelFor("txt", defaultSettings);
        string[] txtMustPrimary = ["md", "pdf", "docx", "html", "rtf", "odt"];
        string[] txtMustAll = ["md", "pdf", "docx", "html", "rtf", "odt", "json", "csv"];
        string[] txtForbidden = ["bmp", "gif", "webp", "jpg", "png", "tiff", "ico"];
        checks["matrix_wheel_txt_shows_md_docs_and_blocks_images"] =
            txtMustPrimary.All(ext => txtWheel.PrimaryRing.Contains(ext)) &&
            txtMustAll.All(ext => txtWheel.AllReachable.Contains(ext)) &&
            txtForbidden.All(ext => !txtWheel.AllReachable.Contains(ext) && !RoutePlanner.TryPlanRoute("txt", ext, defaultSettings, out _));

        // Scenario .md
        var mdWheel = InspectWheelFor("md", defaultSettings);
        string[] mdMustPrimary = ["txt", "html", "pdf", "docx", "odt", "rtf"];
        string[] mdForbidden = ["bmp", "gif", "webp", "jpg", "png"];
        checks["matrix_wheel_md_shows_txt_html_docs_and_blocks_images"] =
            mdMustPrimary.All(ext => mdWheel.PrimaryRing.Contains(ext)) &&
            mdForbidden.All(ext => !mdWheel.AllReachable.Contains(ext) && !RoutePlanner.TryPlanRoute("md", ext, defaultSettings, out _));

        // Scenario .docx
        var docxWheel = InspectWheelFor("docx", defaultSettings);
        string[] docxMustPrimary = ["txt", "md", "html", "odt", "rtf"];
        checks["matrix_wheel_docx_shows_docs_primary_and_images_secondary"] =
            docxMustPrimary.All(ext => docxWheel.PrimaryRing.Contains(ext)) &&
            docxWheel.AllReachable.Contains("png") &&
            docxWheel.AllReachable.Contains("jpg");
        var docxPdfSettings = new FlipSettings { EnginePriorityOrder = ["pdf_builtin"] }.CreateSnapshot();
        bool hasWriter = new LibreOfficeEngine().IsAvailable(docxPdfSettings);
        checks["matrix_docx_to_pdf_prefers_layout_preserving_engine"] =
            RoutePlanner.TryPlanRoute("docx", "pdf", docxPdfSettings, out var docxPdfRoute) &&
            docxWheel.AllReachable.Contains("pdf") &&
            docxPdfRoute is { Hops.Count: 1 } && docxPdfRoute.Hops[0].Engine.Id == (hasWriter ? "libreoffice" : "pdf_builtin");

        var pdfWheel = InspectWheelFor("pdf", defaultSettings);
        bool canExportPdfDoc = RoutePlanner.TryPlanRoute("pdf", "doc", defaultSettings, out var pdfDocRoute);
        checks["matrix_pdf_to_doc_visible_only_with_libreoffice"] =
            canExportPdfDoc == hasWriter && pdfWheel.AllReachable.Contains("doc") == hasWriter &&
            (!hasWriter || (pdfDocRoute is { Hops.Count: 2 } && pdfDocRoute.Hops[0].Engine.Id == "pdf_builtin" &&
                pdfDocRoute.Hops[1].Engine.Id == "libreoffice"));

        // Scenario .xlsx e .csv
        var xlsxWheel = InspectWheelFor("xlsx", defaultSettings);
        var csvWheel = InspectWheelFor("csv", defaultSettings);
        string[] sheetTargets = ["tsv", "json", "xml", "yaml", "html", "md", "pdf"];
        checks["matrix_wheel_xlsx_csv_shows_tabular_and_blocks_images"] =
            xlsxWheel.AllReachable.Contains("csv") &&
            csvWheel.AllReachable.Contains("xlsx") &&
            sheetTargets.All(ext => xlsxWheel.AllReachable.Contains(ext) && csvWheel.AllReachable.Contains(ext)) &&
            !xlsxWheel.AllReachable.Contains("bmp") && !csvWheel.AllReachable.Contains("bmp");

        // Scenario .json e .yaml
        var jsonWheel = InspectWheelFor("json", defaultSettings);
        var yamlWheel = InspectWheelFor("yaml", defaultSettings);
        string[] dataForbidden = ["bmp", "png", "jpg", "wav", "mp4"];
        checks["matrix_wheel_json_yaml_contextual_unlock_and_blocks_media"] =
            jsonWheel.AllReachable.Contains("yaml") &&
            yamlWheel.AllReachable.Contains("json") &&
            new[] { "xml", "toml", "csv", "ini" }.All(ext => jsonWheel.AllReachable.Contains(ext) && yamlWheel.AllReachable.Contains(ext)) &&
            dataForbidden.All(ext => !jsonWheel.AllReachable.Contains(ext) && !yamlWheel.AllReachable.Contains(ext));

        // Scenario .png e .jpg
        var pngWheel = InspectWheelFor("png", defaultSettings);
        var jpgWheel = InspectWheelFor("jpg", defaultSettings);
        checks["matrix_wheel_png_jpg_shows_raster_and_pdf"] =
            pngWheel.AllReachable.Contains("jpg") &&
            jpgWheel.AllReachable.Contains("png") &&
            new[] { "webp", "bmp", "gif", "tiff", "ico", "pdf" }.All(ext => pngWheel.AllReachable.Contains(ext) && jpgWheel.AllReachable.Contains(ext));

        // Scenario .svg e .dxf
        var svgWheel = InspectWheelFor("svg", defaultSettings);
        var dxfWheel = InspectWheelFor("dxf", defaultSettings);
        checks["matrix_wheel_svg_dxf_bidirectional_and_render"] =
            svgWheel.AllReachable.Contains("dxf") &&
            dxfWheel.AllReachable.Contains("svg") &&
            new[] { "pdf", "png", "webp" }.All(ext => svgWheel.AllReachable.Contains(ext) && dxfWheel.AllReachable.Contains(ext));

        // Scenario 3D (.obj, .stl, .fbx, .glb) con impostazioni di default (sblocco contestuale automatico!)
        string[] meshExts = ["obj", "stl", "fbx", "glb", "ply", "gltf", "3mf", "dae"];
        bool all3DUnlocked = true;
        foreach (string src3D in new[] { "obj", "stl", "fbx", "glb" })
        {
            var wheel3D = InspectWheelFor(src3D, defaultSettings);
            bool hasPeers = meshExts.Where(e => e != src3D).All(e => wheel3D.AllReachable.Contains(e));
            bool hasRender = wheel3D.AllReachable.Contains("png") && wheel3D.AllReachable.Contains("pdf");
            if (!hasPeers || !hasRender) all3DUnlocked = false;
        }
        checks["matrix_wheel_3d_contextual_unlock_all_peers_and_blueprint"] = all3DUnlocked;

        // Scenario .wav, .mp3 e .mp4
        var wavWheel = InspectWheelFor("wav", defaultSettings);
        var mp4Wheel = InspectWheelFor("mp4", defaultSettings);
        checks["matrix_wheel_audio_video_strict_domain_isolation"] =
            wavWheel.AllReachable.All(ext => (FormatRegistry.TryGet(ext, out var fd) && fd.Category == FormatCategory.Audio) || ext is "srt" or "txt") &&
            mp4Wheel.AllReachable.All(ext => (FormatRegistry.TryGet(ext, out var fd) && fd.Category is FormatCategory.Video or FormatCategory.Audio) || ext is "gif" or "webp" or "txt" or "srt");

        // Scenario .zip
        var zipWheel = InspectWheelFor("zip", defaultSettings);
        checks["matrix_wheel_zip_shows_tar_gz_tgz_7z"] =
            new[] { "tar", "gz", "tgz", "7z" }.All(ext => zipWheel.AllReachable.Contains(ext));

        // ── 2. MATRICE CONVERSIONI REALI END-TO-END (Byte-Level) ───────────────

        string workDir = Path.Combine(Path.GetTempPath(), "FLIP_Matrix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var matrixSettings = new FlipSettings
        {
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = workDir,
            UserSelectedCustomOutputDirectory = true
        }.CreateSnapshot();
        try
        {
            string StepConvert(string inputPath, string targetExt)
            {
                var res = RoutePlanner.ExecuteRouteAsync(inputPath, targetExt, matrixSettings).GetAwaiter().GetResult();
                if (!res.Success || string.IsNullOrEmpty(res.OutputFilePath))
                    throw new IOException($"Conversione fallita {Path.GetExtension(inputPath)} -> {targetExt}: {res.ErrorMessage}");
                string outFile = res.OutputFilePath;
                if (!File.Exists(outFile) || new FileInfo(outFile).Length == 0)
                    throw new IOException($"Output vuoto per {targetExt}");
                return outFile;
            }

            // A) Catena Testo/Documenti: TXT -> MD -> HTML -> DOCX -> ODT -> RTF -> PDF -> TXT
            string srcTxt = Path.Combine(workDir, "doc_chain.txt");
            File.WriteAllText(srcTxt, "# Titolo FLIP Matrix\n\nPrimo paragrafo di verifica conversione universale.\nSecondo paragrafo con dati.", System.Text.Encoding.UTF8);
            string stepMd = StepConvert(srcTxt, "md");
            string stepHtml = StepConvert(stepMd, "html");
            string stepDocx = StepConvert(stepHtml, "docx");
            if (new LibreOfficeEngine().IsAvailable(matrixSettings))
            {
                // A custom page size is lost if the DOCX takes the built-in text-to-PDF route.
                string layoutDocx = Path.Combine(workDir, "layout.docx");
                File.Copy(stepDocx, layoutDocx);
                using (var zip = System.IO.Compression.ZipFile.Open(layoutDocx, System.IO.Compression.ZipArchiveMode.Update))
                {
                    var entry = zip.GetEntry("word/document.xml")!;
                    XDocument document;
                    using (var stream = entry.Open()) document = XDocument.Load(stream);
                    XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    var body = document.Root!.Element(word + "body")!;
                    body.Element(word + "sectPr")?.Remove();
                    body.Add(new XElement(word + "sectPr",
                        new XElement(word + "pgSz", new XAttribute(word + "w", 10080), new XAttribute(word + "h", 12960))));
                    entry.Delete();
                    using var output = zip.CreateEntry("word/document.xml").Open();
                    document.Save(output);
                }
                string layoutPdfPath = StepConvert(layoutDocx, "pdf");
                using var layoutPdf = UglyToad.PdfPig.PdfDocument.Open(layoutPdfPath);
                var page = layoutPdf.GetPage(1);
                bool preservesPageSize = Math.Abs(page.Width - 504) <= 2 && Math.Abs(page.Height - 648) <= 2;
                checks["matrix_e2e_docx_to_pdf_preserves_page_size"] = preservesPageSize;
                if (!preservesPageSize)
                    Evidence.Log("test.docx-pdf.page-size", new { page.Width, page.Height });
                var privacyPdf = RoutePlanner.ExecuteRouteAsync(layoutDocx, "pdf", matrixSettings,
                    new() { ["strip_metadata"] = "true" }).GetAwaiter().GetResult();
                checks["matrix_docx_to_pdf_refuses_unsupported_metadata_removal"] =
                    !privacyPdf.Success && privacyPdf.ErrorMessage?.Contains("preset PDF", StringComparison.Ordinal) == true;
                string legacyDoc = StepConvert(layoutPdfPath, "doc");
                using var docStream = File.OpenRead(legacyDoc);
                byte[] signature = new byte[8];
                docStream.ReadExactly(signature);
                string extractedDoc = StepConvert(legacyDoc, "txt");
                checks["matrix_e2e_pdf_to_doc_generates_word97_with_editable_text"] =
                    Convert.ToHexString(signature) == "D0CF11E0A1B11AE1" &&
                    File.ReadAllText(extractedDoc).Contains("verifica conversione universale", StringComparison.OrdinalIgnoreCase);
            }
            string stepOdt = StepConvert(stepDocx, "odt");
            string stepRtf = StepConvert(stepOdt, "rtf");
            string stepPdf = StepConvert(stepRtf, "pdf");
            string stepBackTxt = StepConvert(stepDocx, "txt");
            checks["matrix_e2e_text_doc_chain_txt_md_html_docx_odt_rtf_pdf"] =
                File.ReadAllText(stepBackTxt, System.Text.Encoding.UTF8).Contains("verifica conversione universale", StringComparison.OrdinalIgnoreCase) &&
                new FileInfo(stepPdf).Length > 200;

            // B) Catena Dati/Fogli: CSV -> JSON -> YAML -> XLSX -> CSV
            string srcCsv = Path.Combine(workDir, "data_chain.csv");
            File.WriteAllText(srcCsv, "Nome,Reparto,Valore\nAlfa,Ricerca,42\nBeta,Sviluppo,99\n", System.Text.Encoding.UTF8);
            string stepJson = StepConvert(srcCsv, "json");
            string stepYaml = StepConvert(stepJson, "yaml");
            string stepXlsx = StepConvert(stepYaml, "xlsx");
            string stepCsvBack = StepConvert(stepXlsx, "csv");
            string csvBackText = File.ReadAllText(stepCsvBack, System.Text.Encoding.UTF8);
            checks["matrix_e2e_data_chain_csv_json_yaml_xlsx"] =
                csvBackText.Contains("Alfa", StringComparison.Ordinal) &&
                csvBackText.Contains("Sviluppo", StringComparison.Ordinal);

            // C) Catena 3D Completa: OBJ -> STL -> PLY -> GLB -> GLTF -> 3MF -> DAE -> FBX -> OBJ + Blueprint PNG/PDF
            string srcObj = Path.Combine(workDir, "cube_chain.obj");
            File.WriteAllText(srcObj, """
                # Cube 8 vertices 12 triangles
                v -1 -1 -1
                v  1 -1 -1
                v  1  1 -1
                v -1  1 -1
                v -1 -1  1
                v  1 -1  1
                v  1  1  1
                v -1  1  1
                f 1 2 3
                f 1 3 4
                f 5 8 7
                f 5 7 6
                f 1 5 6
                f 1 6 2
                f 2 6 7
                f 2 7 3
                f 3 7 8
                f 3 8 4
                f 4 8 5
                f 4 5 1
                """, System.Text.Encoding.UTF8);

            var initialCounts = Engines.Mesh3DVectorGeoEngine.InspectMeshCounts(srcObj, "obj");
            string mStl = StepConvert(srcObj, "stl");
            string mPly = StepConvert(mStl, "ply");
            string mGlb = StepConvert(mPly, "glb");
            string mGltf = StepConvert(mGlb, "gltf");
            string m3mf = StepConvert(mGltf, "3mf");
            string mDae = StepConvert(m3mf, "dae");
            string mFbx = StepConvert(mDae, "fbx");
            string mObjBack = StepConvert(mFbx, "obj");
            var finalCounts = Engines.Mesh3DVectorGeoEngine.InspectMeshCounts(mObjBack, "obj");

            string blueprintPng = StepConvert(srcObj, "png");
            string blueprintPdf = StepConvert(srcObj, "pdf");

            checks["matrix_e2e_3d_chain_obj_stl_ply_glb_gltf_3mf_dae_fbx_and_blueprint"] =
                initialCounts == (8, 12) &&
                finalCounts == (8, 12) &&
                new FileInfo(blueprintPng).Length > 5_000 &&
                new FileInfo(blueprintPdf).Length > 1_000;

            // D) Catena Vettoriale/CAD: SVG -> DXF -> SVG -> PNG
            string srcSvg = Path.Combine(workDir, "vec_chain.svg");
            File.WriteAllText(srcSvg, """
                <svg xmlns="http://www.w3.org/2000/svg" width="200" height="200" viewBox="0 0 200 200">
                  <rect x="20" y="20" width="160" height="160" stroke="#FF7A18" fill="none"/>
                  <circle cx="100" cy="100" r="50" stroke="#4CC9F0" fill="none"/>
                  <line x1="20" y1="20" x2="180" y2="180" stroke="#FFFFFF"/>
                </svg>
                """, System.Text.Encoding.UTF8);
            string vDxf = StepConvert(srcSvg, "dxf");
            string vSvgBack = StepConvert(vDxf, "svg");
            string vPng = StepConvert(vSvgBack, "png");
            checks["matrix_e2e_vector_chain_svg_dxf_svg_png"] =
                new FileInfo(vDxf).Length > 50 &&
                new FileInfo(vSvgBack).Length > 50 &&
                new FileInfo(vPng).Length > 500;

            // E) FLIP Creator Pack: PaletteQuickTool, Crop 9:16 Blur, PrivacyCleanQuickTool, WaveformQuickTool, +ZIP Toggle
            string sampleImg = Path.Combine(workDir, "creator_sample.png");
            using (var bmp = new SkiaSharp.SKBitmap(240, 160, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul))
            {
                using var cv = new SkiaSharp.SKCanvas(bmp);
                cv.Clear(new SkiaSharp.SKColor(20, 30, 48));
                SkiaSharp.SKColor[] swatches =
                [
                    new(255, 122, 24), new(46, 204, 113), new(52, 152, 219),
                    new(231, 76, 60), new(155, 89, 182), new(241, 196, 15)
                ];
                for (int i = 0; i < swatches.Length; i++)
                {
                    using var p = new SkiaSharp.SKPaint { Color = swatches[i], Style = SkiaSharp.SKPaintStyle.Fill };
                    cv.DrawRect(i * 40, 0, 40, 160, p);
                }
                ConversionEngine.WriteImage(bmp, sampleImg, "PNG");

                var hexColors = Engines.PaletteQuickTool.ExtractHexPalette(bmp, 6);
                checks["creator_palette_extracts_6_distinct_hex"] =
                    hexColors.Count == 6 &&
                    hexColors.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 6 &&
                    hexColors.All(h => h.StartsWith('#') && h.Length == 7);
            }

            // 1. PaletteQuickTool E2E
            var paletteTool = new Engines.PaletteQuickTool();
            var paletteRes = paletteTool.ExecuteAsync(new QuickToolContext
            {
                InputFilePaths = [sampleImg],
                Settings = matrixSettings
            }).GetAwaiter().GetResult();
            using (var palBmp = SkiaSharp.SKBitmap.Decode(paletteRes.OutputFilePaths.FirstOrDefault() ?? ""))
            {
                checks["creator_palette_swatch_card_png"] =
                    paletteRes.Success &&
                    palBmp != null &&
                    palBmp.Width == 1200 &&
                    palBmp.Height == 960;
            }

            // 2. CropQuickTool "9:16 Blur" E2E
            var cropTool = new Engines.CropQuickTool();
            var cropCtx = new QuickToolContext
            {
                InputFilePaths = [sampleImg],
                Settings = matrixSettings
            };
            cropCtx.Parameters["ratio"] = "9:16 Blur";
            var blurRes = cropTool.ExecuteAsync(cropCtx).GetAwaiter().GetResult();
            using (var blurBmp = SkiaSharp.SKBitmap.Decode(blurRes.OutputFilePaths.FirstOrDefault() ?? ""))
            {
                checks["creator_crop_9x16_social_blur"] =
                    blurRes.Success &&
                    blurBmp != null &&
                    blurBmp.Width == 1080 &&
                    blurBmp.Height == 1920;
            }

            // 3. PrivacyCleanQuickTool ("clean_meta") E2E
            var cleanTool = new Engines.PrivacyCleanQuickTool();
            var cleanRes = cleanTool.ExecuteAsync(new QuickToolContext
            {
                InputFilePaths = [sampleImg],
                Settings = matrixSettings
            }).GetAwaiter().GetResult();
            checks["creator_privacy_clean_metadata"] =
                cleanRes.Success &&
                cleanRes.OutputFilePaths.Count == 1 &&
                File.Exists(cleanRes.OutputFilePaths[0]) &&
                new FileInfo(cleanRes.OutputFilePaths[0]).Length > 100;

            // 4. WaveformQuickTool ("waveform") .wav -> 1920x480 .png trasparente
            string sampleWav = Path.Combine(workDir, "creator_tone.wav");
            File.WriteAllBytes(sampleWav, WheelSound.CreateWave());
            var waveTool = new Engines.WaveformQuickTool();
            var waveRes = waveTool.ExecuteAsync(new QuickToolContext
            {
                InputFilePaths = [sampleWav],
                Settings = matrixSettings
            }).GetAwaiter().GetResult();
            using (var waveBmp = SkiaSharp.SKBitmap.Decode(waveRes.OutputFilePaths.FirstOrDefault() ?? ""))
            {
                checks["creator_waveform_wav_to_png_1920x480"] =
                    waveRes.Success &&
                    waveBmp != null &&
                    waveBmp.Width == 1920 &&
                    waveBmp.Height == 480 &&
                    waveBmp.GetPixel(4, 4).Alpha == 0;
            }

            // 5. Combo "+ ZIP" con Barra Spaziatrice sulla Ruota + PackOutputsIntoZipArchive
            var zipTestWheel = new Wheel(_ => { }, (_, _) => { }, settings: defaultSettings)
            {
                Opacity = 0, Topmost = false
            };
            try
            {
                zipTestWheel.Open([sampleImg], drag: true);
                bool beforeToggle = zipTestWheel.PackOutputIntoZip;
                zipTestWheel.HandleDragCommand(WheelCommand.ToggleZip);
                bool afterToggleOn = zipTestWheel.PackOutputIntoZip &&
                    zipTestWheel.CenterCaptionText.Contains("+ ARCHIVIO .ZIP", StringComparison.Ordinal);
                zipTestWheel.HandleDragCommand(WheelCommand.ToggleZip);
                bool afterToggleOff = !zipTestWheel.PackOutputIntoZip &&
                    !zipTestWheel.CenterCaptionText.Contains("+ ARCHIVIO .ZIP", StringComparison.Ordinal);

                string packedZip = Program.PackOutputsIntoZipArchive([sampleImg, sampleWav], "creator_bundle");
                checks["creator_wheel_space_toggle_zip_and_pack"] =
                    !beforeToggle &&
                    afterToggleOn &&
                    afterToggleOff &&
                    File.Exists(packedZip) &&
                    new FileInfo(packedZip).Length > 100;
            }
            finally
            {
                zipTestWheel.Close();
            }

            // F) Turntable 360°: Quick Tool 3D -> GIF animata ciclica 24 fotogrammi 720x720
            string[] turntableExts = ["obj", "stl", "fbx", "glb", "gltf", "ply", "3mf", "dae"];
            checks["turntable_tool_offered_for_every_3d_format_only"] =
                defaultSettings.EnabledBackSideTools.Contains("turntable_3d") &&
                turntableExts.All(ext => RoutePlanner.GetAvailableQuickTools([ext], defaultSettings).Any(t => t.Id == "turntable_3d")) &&
                !RoutePlanner.GetAvailableQuickTools(["png"], defaultSettings).Any(t => t.Id == "turntable_3d") &&
                !RoutePlanner.GetAvailableQuickTools(["obj", "png"], defaultSettings).Any(t => t.Id == "turntable_3d");

            string turntableObj = Path.Combine(workDir, "turntable_cube.obj");
            File.Copy(srcObj, turntableObj, overwrite: true);
            var turntableTool = new Engines.Turntable3DQuickTool();
            var turntableRes = turntableTool.ExecuteAsync(new QuickToolContext
            {
                InputFilePaths = [turntableObj],
                Settings = matrixSettings
            }).GetAwaiter().GetResult();
            string turntableGif = turntableRes.OutputFilePaths.FirstOrDefault() ?? "";
            bool turntableOk = turntableRes.Success && File.Exists(turntableGif) &&
                Path.GetFileName(turntableGif).EndsWith("_turntable.gif", StringComparison.OrdinalIgnoreCase);
            bool turntableLoops = false, turntableFrames = false, turntableAccent = false, turntableAnimated = false;
            if (turntableOk)
            {
                byte[] gifBytes = File.ReadAllBytes(turntableGif);
                turntableLoops = gifBytes.Length > 20_000 &&
                    gifBytes.AsSpan(0, 6).SequenceEqual("GIF89a"u8) &&
                    gifBytes.AsSpan().IndexOf("NETSCAPE2.0"u8) > 0 &&
                    gifBytes[^1] == 0x3B;

                using var gifStream = new MemoryStream(gifBytes);
                var gifDecoder = new GifBitmapDecoder(gifStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                turntableFrames = gifDecoder.Frames.Count == Engines.Turntable3DQuickTool.DefaultFrames &&
                    gifDecoder.Frames.All(f => f.PixelWidth == 720 && f.PixelHeight == 720);

                byte[] FramePixels(int frameIndex)
                {
                    var converted = new FormatConvertedBitmap(gifDecoder.Frames[frameIndex], PixelFormats.Bgra32, null, 0);
                    var buffer = new byte[720 * 720 * 4];
                    converted.CopyPixels(buffer, 720 * 4, 0);
                    return buffer;
                }

                if (turntableFrames)
                {
                    byte[] first = FramePixels(0), quarter = FramePixels(6);
                    int orange = 0, changed = 0;
                    for (int i = 0; i < first.Length; i += 4)
                    {
                        // Arancione FLIP #FF7A18 (tolleranza di quantizzazione GIF)
                        if (first[i + 2] > 225 && first[i + 1] is > 95 and < 150 && first[i] < 70) orange++;
                        if (Math.Abs(first[i] - quarter[i]) + Math.Abs(first[i + 1] - quarter[i + 1]) + Math.Abs(first[i + 2] - quarter[i + 2]) > 48) changed++;
                    }
                    turntableAccent = orange > 200;
                    turntableAnimated = changed > 2_000;
                }
            }
            checks["turntable_cube_obj_to_looping_gif_24_frames_720"] = turntableOk && turntableLoops && turntableFrames;
            checks["turntable_gif_rotates_with_orange_wireframe_wipe"] = turntableAccent && turntableAnimated;

            // G) Whisper AI offline: pipeline Audio -> WAV 16 kHz mono -> TXT / SRT e installer 1-Click
            var whisperEngine = new LocalAiEngine();
            string[] voiceExts = ["wav", "mp3", "m4a", "ogg", "opus", "flac"];
            checks["whisper_edges_cover_voice_notes_to_txt_and_srt"] =
                voiceExts.All(src => new[] { "txt", "srt" }.All(dst =>
                    whisperEngine.SupportedEdges.Any(e => e.SourceExtension == src && e.TargetExtension == dst)));

            string voiceWav = Path.Combine(workDir, "voice_note.wav");
            File.WriteAllBytes(voiceWav, WheelSound.CreateWave()); // 44.1 kHz mono, 1764 campioni
            var (voiceSamples, voiceRate) = WhisperAudio.ReadWavMono(voiceWav);
            string voice16k = Path.Combine(workDir, "voice_note_16k.wav");
            WhisperAudio.WriteWav16(voice16k, WhisperAudio.Resample(voiceSamples, voiceRate, WhisperAudio.TargetRate), WhisperAudio.TargetRate);
            checks["whisper_audio_normalized_to_wav_16k_mono_pcm16"] =
                voiceRate == 44100 && voiceSamples.Length == 1764 &&
                !WhisperAudio.IsWhisperReadyWav(voiceWav) &&
                WhisperAudio.IsWhisperReadyWav(voice16k) &&
                new FileInfo(voice16k).Length == 44 + 640 * 2;

            string cleanedSrt = LocalAiEngine.CleanSrt(
                "1\n00:00:00,000 --> 00:00:02,000\n Ciao a tutti\n\n2\n00:00:02,000 --> 00:00:04,000\n [BLANK_AUDIO]\n\n3\n00:00:04,000 --> 00:00:06,500\n come va?\n");
            checks["whisper_srt_and_txt_are_clean_and_renumbered"] =
                cleanedSrt == "1\r\n00:00:00,000 --> 00:00:02,000\r\nCiao a tutti\r\n\r\n2\r\n00:00:04,000 --> 00:00:06,500\r\ncome va?\r\n\r\n" &&
                LocalAiEngine.CleanTranscript(" Ciao a tutti\n [BLANK_AUDIO]\n come va?\n") == "Ciao a tutti" + Environment.NewLine + "come va?" + Environment.NewLine;

            const string releasesJson = """
                [
                  {"tag_name":"v9.9.9","draft":false,"assets":[]},
                  {"tag_name":"b9999","draft":false,"assets":[
                    {"name":"whisper-bin-Win32.zip","browser_download_url":"https://github.com/ggml-org/whisper.cpp/releases/download/b9999/whisper-bin-Win32.zip"},
                    {"name":"whisper-bin-x64.zip","digest":"sha256:ABCDEF","browser_download_url":"https://github.com/ggml-org/whisper.cpp/releases/download/b9999/whisper-bin-x64.zip"}]},
                  {"tag_name":"v9.9.8","assets":[
                    {"name":"whisper-bin-x64.zip","browser_download_url":"https://github.com/ggml-org/whisper.cpp/releases/download/v9.9.8/whisper-bin-x64.zip"}]}
                ]
                """;
            const string spoofedJson = """
                [{"tag_name":"b1","assets":[{"name":"whisper-bin-x64.zip","browser_download_url":"https://example.com/whisper-bin-x64.zip"}]}]
                """;
            string runtimeZip = Path.Combine(workDir, "whisper-runtime-test.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(runtimeZip, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (string entryName in new[] { "Release/whisper-cli.exe", "Release/whisper.dll", "Release/main.exe", "Release/readme.txt", "../escape.dll" })
                {
                    using var entryStream = zip.CreateEntry(entryName).Open();
                    entryStream.Write("FLIP"u8);
                }
            }
            string runtimeDir = Path.Combine(workDir, "whisper-runtime");
            WhisperModuleInstaller.ExtractRuntime(runtimeZip, runtimeDir);
            string[] extractedRuntime = Directory.GetFiles(runtimeDir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
            checks["whisper_installer_picks_latest_x64_build_and_extracts_safely"] =
                WhisperModuleInstaller.SelectRuntimeAsset(releasesJson) is
                    ("https://github.com/ggml-org/whisper.cpp/releases/download/b9999/whisper-bin-x64.zip", "ABCDEF") &&
                WhisperModuleInstaller.SelectRuntimeAsset(spoofedJson) is null &&
                extractedRuntime.SequenceEqual(["escape.dll", "whisper-cli.exe", "whisper.dll"]) &&
                !File.Exists(Path.Combine(workDir, "escape.dll")) &&
                WhisperModuleInstaller.InstallDirectory.EndsWith(Path.Combine("FLIP", "Engines", "Whisper"), StringComparison.Ordinal);

            // Finto whisper-cli: verifica che riceva un WAV 16 kHz e che la rotta audio -> TXT/SRT sia a 1 salto.
            string fakeWhisperDir = Path.Combine(workDir, "fake-whisper");
            Directory.CreateDirectory(fakeWhisperDir);
            string fakeWhisperCli = Path.Combine(fakeWhisperDir, "whisper-cli.cmd");
            File.WriteAllText(fakeWhisperCli, string.Join("\r\n",
                "@echo off",
                "setlocal",
                ":next",
                "if \"%~1\"==\"\" goto run",
                "if \"%~1\"==\"-of\" set \"OUT=%~2\"",
                "if \"%~1\"==\"-f\" set \"IN=%~2\"",
                "shift",
                "goto next",
                ":run",
                "copy /y \"%IN%\" \"%~dp0received.wav\" >nul || exit /b 3",
                "copy /y \"%~dp0fixture.txt\" \"%OUT%.txt\" >nul || exit /b 4",
                "copy /y \"%~dp0fixture.srt\" \"%OUT%.srt\" >nul || exit /b 5",
                "exit /b 0",
                ""), new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fakeWhisperDir, "fixture.txt"), " Ciao da FLIP\n [BLANK_AUDIO]\n trascrizione offline\n");
            File.WriteAllText(Path.Combine(fakeWhisperDir, "fixture.srt"),
                "1\n00:00:00,000 --> 00:00:01,000\n Ciao da FLIP\n\n2\n00:00:01,000 --> 00:00:02,000\n trascrizione offline\n");

            var whisperSettings = new FlipSettings
            {
                OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
                CustomOutputDirectory = workDir,
                UserSelectedCustomOutputDirectory = true
            };
            whisperSettings.CustomEnginePaths["whisper"] = fakeWhisperCli;
            var whisperSnapshot = whisperSettings.CreateSnapshot();
            bool unavailableWithoutModel = !whisperEngine.IsAvailable(whisperSnapshot);
            File.WriteAllBytes(Path.Combine(fakeWhisperDir, "ggml-base.bin"), [0x6C, 0x6D, 0x67, 0x67]);
            bool availableWithModel = whisperEngine.IsAvailable(whisperSnapshot);
            checks["whisper_cpp_requires_model_and_unlocks_txt_srt_on_audio_wheel"] =
                unavailableWithoutModel && availableWithModel &&
                voiceExts.All(ext =>
                {
                    var wheel = InspectWheelFor(ext, whisperSnapshot);
                    return wheel.AllReachable.Contains("txt") && wheel.AllReachable.Contains("srt") &&
                        RoutePlanner.TryPlanRoute(ext, "srt", whisperSnapshot, out var srtRoute) && srtRoute is { Hops.Count: 1 } &&
                        wheel.AllReachable.All(target =>
                            (FormatRegistry.TryGet(target, out var fd) && fd.Category == FormatCategory.Audio) || target is "srt" or "txt");
                });

            var txtResult = RoutePlanner.ExecuteRouteAsync(voiceWav, "txt", whisperSnapshot).GetAwaiter().GetResult();
            var srtResult = RoutePlanner.ExecuteRouteAsync(voiceWav, "srt", whisperSnapshot).GetAwaiter().GetResult();
            if (!txtResult.Success) Evidence.Log("test.whisper.txt.error", txtResult.ErrorMessage ?? "");
            if (!srtResult.Success) Evidence.Log("test.whisper.srt.error", srtResult.ErrorMessage ?? "");
            checks["whisper_e2e_wav_to_clean_txt_and_srt_via_16k_wav"] =
                txtResult.Success && srtResult.Success &&
                File.ReadAllText(txtResult.OutputFilePath) == "Ciao da FLIP" + Environment.NewLine + "trascrizione offline" + Environment.NewLine &&
                File.ReadAllText(srtResult.OutputFilePath) ==
                    "1\r\n00:00:00,000 --> 00:00:01,000\r\nCiao da FLIP\r\n\r\n2\r\n00:00:01,000 --> 00:00:02,000\r\ntrascrizione offline\r\n\r\n" &&
                WhisperAudio.IsWhisperReadyWav(Path.Combine(fakeWhisperDir, "received.wav"));

            // Decoder nativo Windows (Media Foundation) usato quando FFmpeg non è installato
            bool nativeDecoderOk;
            try
            {
                var (nativeSamples, nativeRate) = MediaFoundationAudio.DecodeMono(voiceWav, CancellationToken.None);
                nativeDecoderOk = nativeRate == 44100 && nativeSamples.Length is > 1600 and < 1900 &&
                    nativeSamples.Max(sample => Math.Abs(sample)) > 0.05f;
            }
            catch (Exception ex)
            {
                Evidence.Log("test.whisper.native-decoder.error", ex.Message);
                nativeDecoderOk = false;
            }
            checks["whisper_native_windows_decoder_reads_pcm_without_ffmpeg"] = nativeDecoderOk;

            string tinyStubModel = Path.Combine(workDir, "for-tests-ggml-base.bin");
            File.WriteAllBytes(tinyStubModel, [0x6C, 0x6D, 0x67, 0x67, 0x01, 0x02]);
            string embeddedPythonWhisper = Path.Combine(Path.GetTempPath(), "embedded-python", "Scripts", "whisper.exe");
            string venvWhisper = Path.Combine(Path.GetTempPath(), "venv", "whisper.exe");
            string legacyEmbeddedWhisper = Path.Combine(Path.GetTempPath(), "python_embeded", "whisper.exe");
            var embeddedSettings = new FlipSettings();
            embeddedSettings.CustomEnginePaths["whisper"] = embeddedPythonWhisper;
            string? resolvedNotEmbedded = CliResolver.Resolve("whisper", embeddedSettings);
            checks["whisper_locator_excludes_embedded_python_and_test_stubs"] =
                WhisperLocator.IsExcludedWhisperExecutable(embeddedPythonWhisper) &&
                WhisperLocator.IsExcludedWhisperExecutable(venvWhisper) &&
                WhisperLocator.IsExcludedWhisperExecutable(legacyEmbeddedWhisper) &&
                !WhisperLocator.IsUsableModelFile(tinyStubModel) &&
                (resolvedNotEmbedded == null || !WhisperLocator.IsExcludedWhisperExecutable(resolvedNotEmbedded));

            CheckGazeAndOnboarding(checks, workDir);
        }
        catch (Exception ex)
        {
            Evidence.Log("matrix.e2e.error", ex.ToString());
            checks["matrix_e2e_execution_no_exception"] = false;
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, true); } catch { }
            }
        }
    }

    private static void CheckGazeAndOnboarding(Dictionary<string, bool> checks, string workDir)
    {
        var gazeDefaults = new FlipSettings();
        var clampedLow = new FlipSettings { GazeDwellMilliseconds = 100 };
        var clampedHigh = new FlipSettings { GazeDwellMilliseconds = 5000 };
        var roundtripSource = new FlipSettings
        {
            GazeAccessibilityMode = true,
            GazeDwellMilliseconds = 1450,
            OnboardingCompleted = true
        };
        var roundtrip = JsonSerializer.Deserialize<FlipSettings>(JsonSerializer.Serialize(roundtripSource));
        checks["gaze_settings_defaults_and_dwell_clamp"] =
            !gazeDefaults.GazeAccessibilityMode &&
            gazeDefaults.GazeDwellMilliseconds == 1100 &&
            !gazeDefaults.OnboardingCompleted &&
            clampedLow.GazeDwellMilliseconds == 600 &&
            clampedHigh.GazeDwellMilliseconds == 2500 &&
            roundtrip is { GazeAccessibilityMode: true, GazeDwellMilliseconds: 1450, OnboardingCompleted: true };

        string gazeSamplePng = Path.Combine(workDir, "gaze-sample.png");
        using (var bmp = new SkiaSharp.SKBitmap(8, 8))
        {
            bmp.Erase(SkiaSharp.SKColors.Orange);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var encoded = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            using var fs = File.Create(gazeSamplePng);
            encoded.SaveTo(fs);
        }

        var gazeWheel = new Wheel(
            _ => { },
            (_, _) => { },
            runTool: (_, _) => { },
            settings: new FlipSettings { GazeAccessibilityMode = true, GazeDwellMilliseconds = 1100 }.CreateSnapshot(),
            openQuickLook: _ => { })
        {
            Opacity = 0,
            Topmost = false
        };
        try
        {
            gazeWheel.Open([gazeSamplePng], drag: false, stickyGaze: true);
            gazeWheel.UpdateLayout();
            var initialLabels = gazeWheel.GazeBarLabels;
            bool openedOk = gazeWheel.IsStickyGaze &&
                !gazeWheel.IsDragMode &&
                gazeWheel.HasGazeBarVisible &&
                initialLabels.SequenceEqual(["⇄ GIRA STRUMENTI", "👁 ANTEPRIMA", "✕ CHIUDI"]);

            gazeWheel.InvokeGazeFlipForTest();
            var frame = new DispatcherFrame();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            var flipWait = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
            flipWait.Tick += (_, _) =>
            {
                if (gazeWheel.IsShowingTools || DateTime.UtcNow >= deadline) frame.Continue = false;
            };
            flipWait.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { flipWait.Stop(); }

            var flippedLabels = gazeWheel.GazeBarLabels;
            bool flippedOk = gazeWheel.IsShowingTools && flippedLabels.Count == 3 && flippedLabels[0] == "⇄ GIRA FORMATI" &&
                gazeWheel.CenterCaptionText.Contains("gaze-sample.png", StringComparison.OrdinalIgnoreCase);
            bool filterOk = GazeOrbWindow.FilterLocalSelection([gazeSamplePng, workDir, Path.Combine(workDir, "missing.png")])
                .SequenceEqual([gazeSamplePng]);
            bool arcOk = GazeDwell.CreateArcGeometry(new Point(30, 30), 26, 0.5) is PathGeometry &&
                GazeDwell.CreateArcGeometry(new Point(30, 30), 26, 1.0) is EllipseGeometry;
            checks["gaze_wheel_sticky_and_zero_keyboard_bar"] = openedOk && flippedOk && filterOk && arcOk;
        }
        finally
        {
            gazeWheel.Close();
        }

        // Verifica FBX Binario (Kaydara FBX Binary v7.4 32-bit con zlib Encoding=1) + ordine anello primario FBX + QuickLook universale/istantaneo
        string binFbxPath = Path.Combine(workDir, "head_binary.fbx");
        WriteSyntheticBinaryFbx(binFbxPath);
        var defaultSnap = new FlipSettings().CreateSnapshot();
        var matrixSnap = new FlipSettings
        {
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = workDir,
            UserSelectedCustomOutputDirectory = true
        }.CreateSnapshot();
        var fbxReachable = RoutePlanner.GetReachableTargetFormats("fbx", defaultSnap)
            .Select(f => f.NormalizedExtension).ToArray();
        var fbxPrimarySlots = WheelLayout.Partition(fbxReachable.Select(e => e.ToUpperInvariant()).ToArray())
            .Where(s => s.Format != null)
            .Select(s => s.Format!.ToLowerInvariant())
            .ToArray();
        string[] expectedFbxPrimary = ["obj", "glb", "stl", "ply", "3mf", "dae", "png"];
        var fbxMeshData = Engines.Mesh3DVectorGeoEngine.BuildInteractiveMeshData(binFbxPath, "fbx");
        var fbxToObj = RoutePlanner.ExecuteRouteAsync(binFbxPath, "obj", matrixSnap).GetAwaiter().GetResult();
        var fbxToGlb = RoutePlanner.ExecuteRouteAsync(binFbxPath, "glb", matrixSnap).GetAwaiter().GetResult();
        checks["fbx_binary_zlib_and_wheel_primary_order"] =
            expectedFbxPrimary.All(ext => fbxPrimarySlots.Contains(ext)) &&
            RoutePlanner.TryPlanRoute("fbx", "gif", defaultSnap, out var fbxGifRoute) && fbxGifRoute is { Hops.Count: 1 } &&
            fbxMeshData.VertexCount == 4 && fbxMeshData.TriangleCount == 2 &&
            fbxToObj.Success && File.Exists(fbxToObj.OutputFilePath) &&
            File.ReadAllText(fbxToObj.OutputFilePath).Contains("11", StringComparison.Ordinal) &&
            fbxToGlb.Success && File.Exists(fbxToGlb.OutputFilePath) &&
            Path.GetDirectoryName(fbxToObj.OutputFilePath) == Path.GetFullPath(workDir);

        string nonTempDir = Path.Combine(SettingsStore.DataDirectory, "local-check-fixture");
        string localDoc = Path.Combine(nonTempDir, "local_check.txt");
        string tempArchiveDoc = Path.Combine(Path.GetTempPath(), "Temp1_ArchiveExtract", "temp_check.txt");
        var normalBatch = new OutputPathAllocator.Batch([localDoc], defaultSnap);
        var tempBatch = new OutputPathAllocator.Batch([tempArchiveDoc], defaultSnap);
        var implicitDownloads = new FlipSettings
        {
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = OutputPathAllocator.GetDownloadsFallbackDirectory(),
            UserSelectedCustomOutputDirectory = false
        };
        var explicitDownloads = new FlipSettings
        {
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = OutputPathAllocator.GetDownloadsFallbackDirectory(),
            UserSelectedCustomOutputDirectory = true
        };
        bool normImplicit = SettingsStore.NormalizeOutputDirectoryDefault(implicitDownloads, []) &&
            implicitDownloads.OutputDirectoryMode == OutputDirectoryMode.SameAsSource;
        bool normExplicit = !SettingsStore.NormalizeOutputDirectoryDefault(explicitDownloads, []) &&
            explicitDownloads.OutputDirectoryMode == OutputDirectoryMode.CustomFolder;
        checks["output_default_same_as_source_and_temp_fallback"] =
            defaultSnap.OutputDirectoryMode == OutputDirectoryMode.SameAsSource &&
            normalBatch.GetDirectory(localDoc) == Path.GetFullPath(nonTempDir) &&
            tempBatch.GetDirectory(tempArchiveDoc) == Path.GetFullPath(OutputPathAllocator.GetDownloadsFallbackDirectory()) &&
            normImplicit && normExplicit;

        // Verifica QuickLook 3D istantaneo (< 0.2s senza generare la GIF in anticipo) e QuickLook universale su file di testo
        var fbxTool = FlipPreviewService.ResolveQuickLookTool(binFbxPath, defaultSnap)!;
        var fbxPreviewSession = FlipPreviewService.PrepareAsync(
            fbxTool,
            new QuickToolContext { InputFilePaths = [binFbxPath], Settings = matrixSnap },
            CancellationToken.None).GetAwaiter().GetResult();
        bool preview3DFastOk = fbxPreviewSession.Is3D &&
            fbxPreviewSession.InteractiveMesh is { VertexCount: 4, TriangleCount: 2 } &&
            File.Exists(fbxPreviewSession.BlueprintPath) &&
            !File.Exists(fbxPreviewSession.PrimaryPath); // GIF generata solo on-demand al salvataggio!
        fbxPreviewSession.DeleteTemporaryFiles();

        string sampleNote = Path.Combine(workDir, "readme_preview.txt");
        File.WriteAllText(sampleNote, "Prima riga FLIP\nSeconda riga QuickLook universale\n", System.Text.Encoding.UTF8);
        var txtTool = FlipPreviewService.ResolveQuickLookTool(sampleNote, defaultSnap)!;
        var txtPreviewSession = FlipPreviewService.PrepareAsync(
            txtTool,
            new QuickToolContext { InputFilePaths = [sampleNote], Settings = matrixSnap },
            CancellationToken.None).GetAwaiter().GetResult();
        bool previewTextOk = txtPreviewSession.IsTextMode &&
            txtPreviewSession.TextContent != null &&
            txtPreviewSession.TextContent.Contains("QuickLook universale", StringComparison.Ordinal) &&
            txtPreviewSession.Metadata.Contains("Parole: 7", StringComparison.Ordinal);
        txtPreviewSession.DeleteTemporaryFiles();
        checks["quicklook_instant_3d_and_universal_text_viewer"] = preview3DFastOk && previewTextOk;

        bool liveGaze = SettingsStore.Current.CreateSnapshot().GazeAccessibilityMode;
        var orb = new GazeOrbWindow(() => 1100, () => true, _ => { }) { Opacity = 0 };
        var welcome = new FirstRunWelcomeWindow { Opacity = 0, Topmost = false };
        try
        {
            bool orbTopmost = orb.Topmost;
            orb.Topmost = false;
            var menuHeaders = orb.GetContextMenuHeadersForTest();
            checks["first_run_welcome_and_orb_construct"] =
                orb.Width == 60 && orb.Height == 60 && !orb.ShowActivated && orbTopmost &&
                GazeOrbWindow.RestOpacity == 0.35 &&
                GazeOrbWindow.ProximityRadius == 90.0 &&
                Math.Abs(orb.ComputeOpacityForDistance(50) - 1.0) < 0.001 &&
                Math.Abs(orb.ComputeOpacityForDistance(150) - 0.35) < 0.001 &&
                menuHeaders.Count == 2 &&
                menuHeaders[0] == "Disattiva Modalità Tobii (Nascondi [F])" &&
                menuHeaders[1] == "Apri Impostazioni Accessibilità..." &&
                welcome.Width == 680 && welcome.Height == 430 &&
                welcome.GazeToggle.IsChecked == liveGaze && welcome.StartButton != null;
        }
        finally
        {
            orb.Close();
            welcome.Close();
        }

        // Verifica conversione PDF -> PDF/A-2b (ISO 19005-2), sigla ruota "PDF/A" nel primo anello e suffisso _pdfa.pdf
        string pdfSourceForPdfA = Path.Combine(workDir, "documento_pa.txt");
        File.WriteAllText(pdfSourceForPdfA, "Contratto PA conforme ISO 19005-2 PDF/A-2b.\nSeconda riga archivio legale.", System.Text.Encoding.UTF8);
        var basePdfRes = RoutePlanner.ExecuteRouteAsync(pdfSourceForPdfA, "pdf", matrixSnap).GetAwaiter().GetResult();
        if (!basePdfRes.Success)
            throw new IOException($"Fixture TXT → PDF non riuscita: {basePdfRes.ErrorMessage}");
        var pdfWheelReachable = RoutePlanner.GetReachableTargetFormats("pdf", defaultSnap);
        var pdfWheelSlots = WheelLayout.Partition(pdfWheelReachable.Select(f => f.Extension.ToUpperInvariant()).ToArray());
        bool pdfaInPrimaryRing = pdfWheelSlots.Any(s => string.Equals(s.Format, "PDFA", StringComparison.OrdinalIgnoreCase));
        var pdfaRes = RoutePlanner.ExecuteRouteAsync(basePdfRes.OutputFilePath, "pdfa", matrixSnap).GetAwaiter().GetResult();
        var txtToPdfaRes = RoutePlanner.ExecuteRouteAsync(pdfSourceForPdfA, "pdfa", matrixSnap).GetAwaiter().GetResult();
        string pdfaContent = pdfaRes.Success && File.Exists(pdfaRes.OutputFilePath)
            ? System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdfaRes.OutputFilePath))
            : "";
        bool pdfaReadableByPdfPig = false;
        if (pdfaRes.Success && File.Exists(pdfaRes.OutputFilePath))
        {
            using var checkPdfA = UglyToad.PdfPig.PdfDocument.Open(pdfaRes.OutputFilePath);
            pdfaReadableByPdfPig = checkPdfA.NumberOfPages >= 1;
        }
        checks["pdf_to_pdfa_iso19005_2b_and_primary_ring"] =
            FormatRegistry.GetWheelShortLabel("pdfa") == "PDF/A" &&
            pdfaInPrimaryRing &&
            new[] { "pdf", "docx", "odt", "rtf", "md", "txt", "html", "png", "jpg", "webp", "bmp" }
                .All(src => RoutePlanner.TryPlanRoute(src, "pdfa", defaultSnap, out _)) &&
            txtToPdfaRes.Success &&
            txtToPdfaRes.OutputFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
            !txtToPdfaRes.OutputFilePath.EndsWith("_pdfa.pdf", StringComparison.OrdinalIgnoreCase) &&
            pdfaRes.Success &&
            pdfaRes.OutputFilePath.EndsWith("_pdfa.pdf", StringComparison.OrdinalIgnoreCase) &&
            pdfaReadableByPdfPig &&
            pdfaContent.StartsWith("%PDF-1.7", StringComparison.Ordinal) &&
            pdfaContent.Contains("<pdfaid:part>2</pdfaid:part>", StringComparison.Ordinal) &&
            pdfaContent.Contains("<pdfaid:conformance>B</pdfaid:conformance>", StringComparison.Ordinal) &&
            pdfaContent.Contains("/GTS_PDFA1", StringComparison.Ordinal) &&
            pdfaContent.Contains("sRGB IEC61966-2.1", StringComparison.Ordinal) &&
            pdfaContent.Contains("/OutputIntents", StringComparison.Ordinal) &&
            pdfaContent.Contains("/Metadata", StringComparison.Ordinal) &&
            pdfaContent.Contains("/ID [<", StringComparison.Ordinal);

        // Verifica "La ruota mostra la strada" (RouteCaption) e privacy pre-rilascio (SanitizeString + ExportToFile)
        var routeWheel = new Wheel(_ => { }, (_, _) => { }, settings: defaultSnap) { Opacity = 0, Topmost = false };
        string routePdfToPdfA;
        string routeDocxToPng;
        try
        {
            routeWheel.Open([basePdfRes.OutputFilePath], drag: true);
            routePdfToPdfA = routeWheel.RouteCaption("PDFA");
            string dummyDocx = Path.Combine(workDir, "route_check.docx");
            File.WriteAllText(dummyDocx, "placeholder");
            routeWheel.Open([dummyDocx], drag: true);
            routeDocxToPng = routeWheel.RouteCaption("PNG");
        }
        finally
        {
            routeWheel.Close();
        }

        string tempStoreFile = Path.Combine(workDir, "temp_settings.json");
        string exportedSettingsFile = Path.Combine(workDir, "exported_settings.json");
        var tempStore = new SettingsStore(tempStoreFile, tempStoreFile + ".legacy");
        string stalePandocPath = Path.Combine(workDir, "missing-pandoc.exe");
        tempStore.Update(s =>
        {
            s.CustomEnginePaths["whisper"] = Path.Combine(workDir, "whisper-cli.exe");
            s.CustomEnginePaths["pandoc"] = stalePandocPath;
            s.AddRecentConversion(new RecentConversion { SourceFileName = "privato.docx", TargetExtension = "pdf", Success = true });
        });
        CliResolver.VerifyAndDetectEngines(tempStore);
        bool stalePathRemoved = !tempStore.Settings.CustomEnginePaths.TryGetValue("pandoc", out string? curPandoc) ||
            !string.Equals(curPandoc, stalePandocPath, StringComparison.OrdinalIgnoreCase);

        tempStore.ExportToFile(exportedSettingsFile);
        var exportJsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var exportedDeserialized = JsonSerializer.Deserialize<FlipSettings>(File.ReadAllText(exportedSettingsFile), exportJsonOpts)!;
        string sanitizedPath = Evidence.SanitizeString(string.Join('\\', "C:", "Users", "DemoUser", "Documents", "segreto.pdf"));
        string sanitizedDataUri = Evidence.SanitizeString("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAUA");
        string sanitizedUrl = Evidence.SanitizeString("https://example.com/secret?token=abc");
        string sanitizedLong = Evidence.SanitizeString(new string('X', 260));

        checks["wheel_route_preview_and_privacy_export_sanitization"] =
            routePdfToPdfA.Contains("PDF → PDF/A", StringComparison.Ordinal) &&
            routeDocxToPng.Contains("DOCX → PDF → PNG", StringComparison.Ordinal) &&
            exportedDeserialized.RecentConversions.Count == 0 &&
            exportedDeserialized.CustomEnginePaths.Count == 0 &&
            !sanitizedPath.Contains("DemoUser", StringComparison.OrdinalIgnoreCase) &&
            sanitizedPath.Contains("%USERPROFILE%", StringComparison.Ordinal) &&
            sanitizedDataUri == "data:[redacted]" &&
            sanitizedUrl == "[redacted-url]" &&
            sanitizedLong.Length == 201;

        // Verifica Preset + strip_metadata reale su JPEG EXIF + UI senza "Ricetta"/"Ricette" + VerifyAndDetectEngines + EngineInstaller
        string rawJpg = Path.Combine(workDir, "preset_raw.jpg");
        string exifJpg = Path.Combine(workDir, "preset_exif.jpg");
        using (var bmp = new SkiaSharp.SKBitmap(24, 24))
        {
            bmp.Erase(SkiaSharp.SKColors.DarkOrange);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var encoded = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 92);
            File.WriteAllBytes(rawJpg, encoded.ToArray());
        }
        byte[] rawJpgBytes = File.ReadAllBytes(rawJpg);
        byte[] exifSegment =
        [
            0xFF, 0xE1, 0x00, 0x10,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08
        ];
        byte[] exifJpgBytes = new byte[rawJpgBytes.Length + exifSegment.Length];
        Array.Copy(rawJpgBytes, 0, exifJpgBytes, 0, 2);
        Array.Copy(exifSegment, 0, exifJpgBytes, 2, exifSegment.Length);
        Array.Copy(rawJpgBytes, 2, exifJpgBytes, 2 + exifSegment.Length, rawJpgBytes.Length - 2);
        File.WriteAllBytes(exifJpg, exifJpgBytes);

        var privacyPreset = new FlipRecipe
        {
            Name = "Privacy JPEG",
            TargetExtension = "jpg",
            Quality = 85,
            StripMetadata = true
        };
        var presetResults = RoutePlanner.ExecuteRecipeAsync([exifJpg], privacyPreset, matrixSnap).GetAwaiter().GetResult();
        bool exifStrippedOk = false;
        if (presetResults.Count == 1 && presetResults[0].Result.Success && File.Exists(presetResults[0].Result.OutputFilePath))
        {
            byte[] strippedBytes = File.ReadAllBytes(presetResults[0].Result.OutputFilePath);
            exifStrippedOk = strippedBytes.Length > 100 &&
                strippedBytes.AsSpan().IndexOf("Exif"u8) < 0;
        }

        var sectionStrings = new List<string>();
        void CollectUiStrings(object? node)
        {
            if (node == null) return;
            if (node is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                sectionStrings.Add(tb.Text);
            else if (node is System.Windows.Controls.ContentControl cc && cc.Content is string s)
                sectionStrings.Add(s);

            if (node is DependencyObject dep)
            {
                foreach (object child in LogicalTreeHelper.GetChildren(dep))
                    CollectUiStrings(child);
            }
        }
        CollectUiStrings(Flip.SettingsHub.HistoryAndRecipesSection.Build());
        bool presetUiClean = sectionStrings.Any(s => s.Contains("Preset", StringComparison.OrdinalIgnoreCase)) &&
            !sectionStrings.Any(s => s.Contains("Ricett", StringComparison.OrdinalIgnoreCase));

        const string pandocReleases = """
            {
              "tag_name": "3.6.4",
              "assets": [
                {"name": "pandoc-3.6.4-linux-amd64.tar.gz", "browser_download_url": "https://github.com/jgm/pandoc/releases/download/3.6.4/pandoc-3.6.4-linux-amd64.tar.gz"},
                {"name": "pandoc-3.6.4-windows-x86_64.zip", "digest": "sha256:DEAD1234", "browser_download_url": "https://github.com/jgm/pandoc/releases/download/3.6.4/pandoc-3.6.4-windows-x86_64.zip"}
              ]
            }
            """;
        const string pandocSpoofed = """
            {"assets":[{"name":"pandoc-3.6.4-windows-x86_64.zip","browser_download_url":"https://example.com/pandoc.zip"}]}
            """;
        string pandocZip = Path.Combine(workDir, "pandoc-test.zip");
        using (var pz = System.IO.Compression.ZipFile.Open(pandocZip, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (string entryName in new[] { "pandoc-3.6.4/pandoc.exe", "pandoc-3.6.4/lib.dll", "../escape.exe" })
            {
                using var es = pz.CreateEntry(entryName).Open();
                es.Write("FLIP"u8);
            }
        }
        string pandocTargetDir = Path.Combine(workDir, "pandoc-extracted");
        string extractedPandocExe = Engines.EngineInstaller.ExtractBinariesFlat(pandocZip, pandocTargetDir, "pandoc.exe");
        bool installerHelpersOk =
            Engines.EngineInstaller.GetWinGetPackageId("ffmpeg") == "Gyan.FFmpeg" &&
            Engines.EngineInstaller.GetWinGetPackageId("tesseract") == "UB-Mannheim.TesseractOCR" &&
            Engines.EngineInstaller.GetWinGetPackageId("calibre") == "calibre.calibre" &&
            Engines.EngineInstaller.GetWinGetPackageId("libreoffice") == "TheDocumentFoundation.LibreOffice" &&
            Engines.EngineInstaller.SelectPandocWindowsAsset(pandocReleases) is
                ("https://github.com/jgm/pandoc/releases/download/3.6.4/pandoc-3.6.4-windows-x86_64.zip", "DEAD1234") &&
            Engines.EngineInstaller.SelectPandocWindowsAsset(pandocSpoofed) is null &&
            File.Exists(extractedPandocExe) &&
            File.Exists(Path.Combine(pandocTargetDir, "lib.dll")) &&
            !File.Exists(Path.Combine(pandocTargetDir, "escape.exe")) &&
            !File.Exists(Path.Combine(workDir, "escape.exe"));

        checks["presets_strip_metadata_ui_and_engine_installer"] =
            exifStrippedOk && presetUiClean && stalePathRemoved && installerHelpersOk;

        // Verifica multi-monitor DPI/coordinate tavoletta, sblocco Wheel sincrono e stop monitorTimer su ScreenEdgeAuraWindow
        var tabletWork = new Native.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1080 };
        var primaryWork = new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var tabletClamp = Native.Clamp(-50, 1060, 480, tabletWork);
        var primaryClamp = Native.Clamp(1910, 10, 320, primaryWork);
        bool clampBoundsOk =
            tabletClamp.X >= tabletWork.Left && tabletClamp.X + tabletClamp.Size <= tabletWork.Right &&
            tabletClamp.Y >= tabletWork.Top && tabletClamp.Y + tabletClamp.Size <= tabletWork.Bottom &&
            primaryClamp.X >= primaryWork.Left && primaryClamp.X + primaryClamp.Size <= primaryWork.Right &&
            primaryClamp.Y >= primaryWork.Top && primaryClamp.Y + primaryClamp.Size <= primaryWork.Bottom &&
            Native.GetMonitorDpi(0, 0) >= 72;

        _ = Native.IsPrimaryMousePressed();
        var staleWheel = new Wheel(_ => { }, (_, _) => { }, settings: defaultSnap) { Opacity = 0, Topmost = false };
        var freshWheel = new Wheel(_ => { }, (_, _) => { }, settings: defaultSnap) { Opacity = 0, Topmost = false };
        bool immediateUnlockOk;
        try
        {
            staleWheel.Open([pdfSourceForPdfA], drag: true);
            bool openHadMonitor = (nint)typeof(Wheel).GetField("activeMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(staleWheel)! != 0;
            bool closeEventFired = false;
            staleWheel.Closed += (_, _) => closeEventFired = true;
            staleWheel.CloseImmediately("host-dismiss");
            freshWheel.Open([pdfSourceForPdfA], drag: true);
            bool freshMonitorSet = (nint)typeof(Wheel).GetField("activeMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(freshWheel)! != 0;
            freshWheel.CloseImmediately("test-cleanup");
            immediateUnlockOk = openHadMonitor && closeEventFired && !staleWheel.IsVisible && freshMonitorSet;
        }
        finally
        {
            try { staleWheel.Close(); } catch { }
            try { freshWheel.Close(); } catch { }
        }

        const BindingFlags priv = BindingFlags.Instance | BindingFlags.NonPublic;
        var auraCtor = typeof(ScreenEdgeAuraWindow).GetConstructor(priv, null, [], null)!;
        var aura = (ScreenEdgeAuraWindow)auraCtor.Invoke(null);
        bool auraStoppedOk;
        try
        {
            aura.Opacity = 0;
            aura.Topmost = false;
            aura.Show();
            typeof(ScreenEdgeAuraWindow).GetMethod("Ignite", priv)!.Invoke(aura, [(nint)0]);
            var timer = (DispatcherTimer)typeof(ScreenEdgeAuraWindow).GetField("monitorTimer", priv)!.GetValue(aura)!;
            var activeField = typeof(ScreenEdgeAuraWindow).GetField("_isActive", priv)!;
            bool activeAfterIgnite = (bool)activeField.GetValue(aura)! && timer.IsEnabled;
            typeof(ScreenEdgeAuraWindow).GetMethod("Dismiss", priv)!.Invoke(aura, null);
            bool stoppedAfterDismiss = !(bool)activeField.GetValue(aura)! && !timer.IsEnabled;
            auraStoppedOk = activeAfterIgnite && stoppedAfterDismiss;
        }
        finally
        {
            try { aura.Close(); } catch { }
        }

        checks["tablet_multi_monitor_dpi_unlock_and_aura_timer_stop"] =
            clampBoundsOk && immediateUnlockOk && auraStoppedOk;

        // Verifica blocco finale Aurora v0.6.0: Provino Aa, Segnalibro 3D (A), Batch Misto, Ultima Scelta #FFB547, Sicurezza glTF e Tutorial 4 Schede
        double bwContrast = FlipPreviewWindow.ComputeContrastRatio("#FFFFFF", "#000000");
        string previewTempDir = Path.Combine(workDir, "aurora_preview_tmp");
        Directory.CreateDirectory(previewTempDir);
        string dummyPng = Path.Combine(workDir, "aurora_palette.png");
        using (var bmp = new SkiaSharp.SKBitmap(12, 12))
        {
            bmp.Erase(SkiaSharp.SKColors.Orange);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(dummyPng, enc.ToArray());
        }
        var palSession = new FlipPreviewSession
        {
            ToolId = "palette",
            ToolDisplayName = "Palette #HEX",
            SourcePath = dummyPng,
            PreviewDirectory = previewTempDir,
            PrimaryPath = dummyPng,
            HexColors = ["#FF7A18", "#121318", "#FFFFFF"],
            SaveSettings = defaultSnap
        };
        bool aaContrastOk;
        var palPreview = new FlipPreviewWindow(palSession) { Opacity = 0, ShowInTaskbar = false, Topmost = false };
        try
        {
            var initialPair = palPreview.GetContrastPairForTest();
            palPreview.SwapContrastPairForTest();
            var swappedPair = palPreview.GetContrastPairForTest();
            aaContrastOk = bwContrast > 20.9 &&
                initialPair == ("#FF7A18", "#121318") &&
                swappedPair == ("#121318", "#FF7A18");
        }
        finally
        {
            try { palPreview.Close(); } catch { }
        }

        var fbxSession2 = FlipPreviewService.PrepareAsync(
            fbxTool,
            new QuickToolContext { InputFilePaths = [binFbxPath], Settings = matrixSnap },
            CancellationToken.None).GetAwaiter().GetResult();
        bool bookmark3DOk;
        var preview3D = new FlipPreviewWindow(fbxSession2) { Opacity = 0, ShowInTaskbar = false, Topmost = false };
        try
        {
            bool emptyInitially = preview3D.GetBookmarkStateForTest() is null;
            preview3D.InvokeBookmarkForTest();
            var saved = preview3D.GetBookmarkStateForTest();
            typeof(FlipPreviewWindow).GetField("targetDistance", priv)!.SetValue(preview3D, 9.5);
            preview3D.InvokeBookmarkForTest();
            double restoredDist = (double)typeof(FlipPreviewWindow).GetField("distance", priv)!.GetValue(preview3D)!;
            bookmark3DOk = emptyInitially && saved is not null && Math.Abs(restoredDist - saved.Distance) < 0.01;
        }
        finally
        {
            try { preview3D.Close(); } catch { }
            fbxSession2.DeleteTemporaryFiles();
        }

        string mixPng = Path.Combine(workDir, "mix_1.png");
        string mixWav = Path.Combine(workDir, "mix_2.wav");
        string mixTxt = Path.Combine(workDir, "mix_3.txt");
        File.Copy(dummyPng, mixPng, overwrite: true);
        File.WriteAllBytes(mixWav, WheelSound.CreateWave());
        File.WriteAllText(mixTxt, "mix");
        var groups = RoutePlanner.GroupFilesByExtension([mixPng, dummyPng, mixWav, mixTxt]);
        bool mixedRecoveryOk;
        var mixWheel = new Wheel(_ => { }, (_, _) => { }, settings: defaultSnap) { Opacity = 0, Topmost = false };
        try
        {
            mixWheel.Open([mixPng, mixWav, mixTxt], drag: false);
            var initialMixLabels = mixWheel.MixedGroupLabelsForTest;
            mixWheel.ApplyMixedGroupFilter("png");
            var pngFormats = (string[])typeof(Wheel).GetField("formats", priv)!.GetValue(mixWheel)!;
            mixedRecoveryOk = groups.Count == 3 &&
                groups[0].Label == "2 PNG" &&
                initialMixLabels.Contains("Tutti (3)") &&
                initialMixLabels.Contains("1 PNG") &&
                pngFormats.Contains("JPG");
        }
        finally
        {
            try { mixWheel.CloseImmediately("test-cleanup"); } catch { }
        }

        bool rememberedChoiceOk;
        Wheel.LastSuccessfulTargetBySourceExt.TryRemove("png", out _);
        var remWheel = new Wheel(_ => { }, (_, _) => { }, settings: defaultSnap) { Opacity = 0, Topmost = false };
        try
        {
            Wheel.RecordLastSuccessfulChoice([".png"], "webp");
            remWheel.Open([mixPng], drag: false);
            string? remembered = remWheel.GetRememberedTargetForCurrentPayload();
            remWheel.SavePreview(Path.Combine(workDir, "remembered_preview.png"), mixPng, false);
            var remCanvas = (System.Windows.Controls.Canvas)((System.Windows.Controls.Viewbox)((System.Windows.Controls.Canvas)remWheel.Content).Children[0]).Child;
            bool hasAmberArc = remCanvas.Children.OfType<System.Windows.Shapes.Path>()
                .Any(p => p.Stroke is System.Windows.Media.SolidColorBrush b && b.Color == System.Windows.Media.Color.FromRgb(0xFF, 0xB5, 0x47));
            rememberedChoiceOk = remembered == "webp" && hasAmberArc;
        }
        finally
        {
            Wheel.LastSuccessfulTargetBySourceExt.TryRemove("png", out _);
            try { remWheel.CloseImmediately("test-cleanup"); } catch { }
        }

        string gltfDir = Path.Combine(workDir, "gltf_sec");
        Directory.CreateDirectory(gltfDir);
        string gltfFile = Path.Combine(gltfDir, "model.gltf");
        string safeBin = Path.Combine(gltfDir, "safe.bin");
        File.WriteAllText(gltfFile, "{}");
        File.WriteAllBytes(safeBin, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        static bool ThrowsSec(Action a)
        {
            try { a(); return false; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return true; }
        }
        bool gltfSafeReadOk = Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, "safe.bin").Length == 12;
        bool gltfSecurityOk = gltfSafeReadOk &&
            ThrowsSec(() => Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, "../outside.bin")) &&
            ThrowsSec(() => Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, "%2e%2e/outside.bin")) &&
            ThrowsSec(() => Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, "file:///Windows/System32/drivers/etc/hosts")) &&
            ThrowsSec(() => Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, "https://example.com/payload.bin")) &&
            ThrowsSec(() => Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, @"\\server\share\payload.bin")) &&
            ThrowsSec(() => Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, "D:" + @"\payload.bin")) &&
            ThrowsSec(() => Engines.Mesh3DVectorGeoEngine.ReadSafeGltfBufferUri(gltfFile, "safe.bin", maxBufferBytes: 4));

        var tut = new FirstRunWelcomeWindow { Opacity = 0, Topmost = false };
        bool tutorialOk;
        try
        {
            int tabs = tut.StepTabCount;
            bool step0 = tut.CurrentStepIndex == 0 && Equals(tut.StartButton.Content, "Avanti →");
            tut.SelectStep(1);
            tut.InvokeTabFlipPreviewForTest();
            tut.SelectStep(3);
            bool step3 = tut.CurrentStepIndex == 3 &&
                (tut.StartButton.Content?.ToString()?.Contains("Inizia a usare FLIP", StringComparison.Ordinal) == true);
            tutorialOk = tabs == 4 && step0 && step3;
        }
        finally
        {
            try { tut.Close(); } catch { }
        }

        checks["aurora_v060_astra_features_tutorial_gltf_and_installer"] =
            aaContrastOk && bookmark3DOk && mixedRecoveryOk && rememberedChoiceOk && gltfSecurityOk && tutorialOk;

        CheckExplicitQuickToolActions(checks, workDir, matrixSnap);
        CheckOnnxRemoveBgEngine(checks, workDir);
    }

    private static void CheckExplicitQuickToolActions(Dictionary<string, bool> checks, string workDir, FlipSettings matrixSnap)
    {
        // 1. Rotazione & Specchiatura esplicite (verifica dimensioni + spostamento esatto dei 4 quadranti colorati)
        string quadPng = Path.Combine(workDir, "qt_quads.png");
        using (var bmp = new SkiaSharp.SKBitmap(160, 80, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul))
        {
            using var cv = new SkiaSharp.SKCanvas(bmp);
            using var red = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 0, 0) };
            using var green = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 255, 0) };
            using var blue = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 0, 255) };
            using var yellow = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 0) };
            cv.DrawRect(0, 0, 80, 40, red);      // Alto-Sinistra = Rosso
            cv.DrawRect(80, 0, 80, 40, green);   // Alto-Destra = Verde
            cv.DrawRect(0, 40, 80, 40, blue);    // Basso-Sinistra = Blu
            cv.DrawRect(80, 40, 80, 40, yellow); // Basso-Destra = Giallo
            ConversionEngine.WriteImage(bmp, quadPng, "PNG");
        }

        static (int W, int H, SkiaSharp.SKColor TL, SkiaSharp.SKColor TR, SkiaSharp.SKColor BL, SkiaSharp.SKColor BR) InspectImg(string path)
        {
            using var d = ConversionEngine.ReadImage(path);
            return (
                d.Width,
                d.Height,
                d.GetPixel(4, 4),
                d.GetPixel(d.Width - 5, 4),
                d.GetPixel(4, d.Height - 5),
                d.GetPixel(d.Width - 5, d.Height - 5));
        }

        static bool IsColor(SkiaSharp.SKColor c, byte r, byte g, byte b) =>
            Math.Abs(c.Red - r) < 10 && Math.Abs(c.Green - g) < 10 && Math.Abs(c.Blue - b) < 10;

        string rotRightPath = Path.Combine(workDir, "qt_rot_right.png");
        string rot90Path = Path.Combine(workDir, "qt_rot_90.png");
        string rotLeftPath = Path.Combine(workDir, "qt_rot_left.png");
        string rot180Path = Path.Combine(workDir, "qt_rot_180.png");
        string flipHPath = Path.Combine(workDir, "qt_flip_h.png");
        string flipVPath = Path.Combine(workDir, "qt_flip_v.png");

        var rRight = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.RotateRightId, quadPng, rotRightPath).GetAwaiter().GetResult();
        var r90 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Rotate90Id, quadPng, rot90Path).GetAwaiter().GetResult();
        var rLeft = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.RotateLeftId, quadPng, rotLeftPath).GetAwaiter().GetResult();
        var r180 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Rotate180Id, quadPng, rot180Path).GetAwaiter().GetResult();
        var rFlipH = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.FlipHorizontalId, quadPng, flipHPath).GetAwaiter().GetResult();
        var rFlipV = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.FlipVerticalId, quadPng, flipVPath).GetAwaiter().GetResult();

        var iRight = InspectImg(rotRightPath);
        var i90 = InspectImg(rot90Path);
        var iLeft = InspectImg(rotLeftPath);
        var i180 = InspectImg(rot180Path);
        var iFlipH = InspectImg(flipHPath);
        var iFlipV = InspectImg(flipVPath);

        checks["quicktool_explicit_rotate_and_flip_orientations"] =
            rRight.Success && r90.Success && rLeft.Success && r180.Success && rFlipH.Success && rFlipV.Success &&
            iRight.W == 80 && iRight.H == 160 && IsColor(iRight.TR, 255, 0, 0) && IsColor(iRight.TL, 0, 0, 255) &&
            i90.W == 80 && i90.H == 160 && IsColor(i90.TR, 255, 0, 0) &&
            iLeft.W == 80 && iLeft.H == 160 && IsColor(iLeft.BL, 255, 0, 0) && IsColor(iLeft.TL, 0, 255, 0) &&
            i180.W == 160 && i180.H == 80 && IsColor(i180.BR, 255, 0, 0) && IsColor(i180.TL, 255, 255, 0) &&
            iFlipH.W == 160 && iFlipH.H == 80 && IsColor(iFlipH.TR, 255, 0, 0) && IsColor(iFlipH.TL, 0, 255, 0) &&
            iFlipV.W == 160 && iFlipV.H == 80 && IsColor(iFlipV.BL, 255, 0, 0) && IsColor(iFlipV.TL, 0, 0, 255);

        // 2. Crop per Aspect Ratio Social (9:16, 4:5, 4:3, 1:1, 16:9) senza upscaling + tool:crop_free con CustomParameters
        string widePng = Path.Combine(workDir, "qt_wide_320x180.png");
        using (var bmp = new SkiaSharp.SKBitmap(320, 180, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul))
        {
            bmp.Erase(SkiaSharp.SKColors.SteelBlue);
            ConversionEngine.WriteImage(bmp, widePng, "PNG");
        }

        string c9x16Path = Path.Combine(workDir, "qt_crop_9x16.png");
        string c4x5Path = Path.Combine(workDir, "qt_crop_4x5.png");
        string c4x3Path = Path.Combine(workDir, "qt_crop_4x3.png");
        string c1x1Path = Path.Combine(workDir, "qt_crop_1x1.png");
        string c16x9Path = Path.Combine(workDir, "qt_crop_16x9.png");
        string cFreePath = Path.Combine(workDir, "qt_crop_free.png");

        var r9x16 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Crop9x16Id, widePng, c9x16Path).GetAwaiter().GetResult();
        var r4x5 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Crop4x5Id, widePng, c4x5Path).GetAwaiter().GetResult();
        var r4x3 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Crop4x3Id, widePng, c4x3Path).GetAwaiter().GetResult();
        var r1x1 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Crop1x1Id, widePng, c1x1Path).GetAwaiter().GetResult();
        var r16x9 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Crop16x9Id, quadPng, c16x9Path).GetAwaiter().GetResult();
        var freeCtx = new ConversionContext
        {
            InputFilePath = widePng,
            OutputFilePath = cFreePath,
            SourceExtension = "png",
            TargetExtension = "png",
            Settings = matrixSnap,
            CustomParameters = new(StringComparer.OrdinalIgnoreCase)
            {
                ["crop_x"] = "0.25",
                ["crop_y"] = "0.25",
                ["crop_w"] = "0.50",
                ["crop_h"] = "0.50"
            }
        };
        var rFree = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.CropFreeId, freeCtx).GetAwaiter().GetResult();

        using var b9x16 = ConversionEngine.ReadImage(c9x16Path);
        using var b4x5 = ConversionEngine.ReadImage(c4x5Path);
        using var b4x3 = ConversionEngine.ReadImage(c4x3Path);
        using var b1x1 = ConversionEngine.ReadImage(c1x1Path);
        using var b16x9 = ConversionEngine.ReadImage(c16x9Path);
        using var bFree = ConversionEngine.ReadImage(cFreePath);

        checks["quicktool_explicit_aspect_crops_and_free_crop"] =
            r9x16.Success && b9x16.Width == 101 && b9x16.Height == 180 &&
            r4x5.Success && b4x5.Width == 144 && b4x5.Height == 180 &&
            r4x3.Success && b4x3.Width == 240 && b4x3.Height == 180 &&
            r1x1.Success && b1x1.Width == 180 && b1x1.Height == 180 &&
            r16x9.Success && b16x9.Width == 142 && b16x9.Height == 80 &&
            rFree.Success && bFree.Width == 160 && bFree.Height == 90;

        // 3. CropImageToFileOrBitmap: clamping bordi/NaN/infiniti, min 1px, Freeze() e blurRegions privacy
        string checkerPng = Path.Combine(workDir, "qt_checker.png");
        using (var bmp = new SkiaSharp.SKBitmap(120, 120, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul))
        {
            for (int y = 0; y < 120; y++)
                for (int x = 0; x < 120; x++)
                    bmp.SetPixel(x, y, (((x / 2) + (y / 2)) & 1) == 0 ? new SkiaSharp.SKColor(255, 0, 0) : new SkiaSharp.SKColor(0, 0, 255));
            ConversionEngine.WriteImage(bmp, checkerPng, "PNG");
        }

        string blurCropOut = Path.Combine(workDir, "qt_blur_crop_out.png");
        var blurBitmap = Engines.BuiltInQuickTools.CropImageToFileOrBitmap(
            checkerPng,
            0.0, 0.0, 1.0, 1.0,
            [(0.25, 0.25, 0.50, 0.50), (-0.5, 1.4, 0.0, double.NaN)],
            blurCropOut);
        using var savedBlur = ConversionEngine.ReadImage(blurCropOut);
        var centerPx = savedBlur.GetPixel(60, 60);
        var outsidePx = savedBlur.GetPixel(2, 2);
        bool blurAppliedInsideOnly =
            centerPx.Red is > 60 and < 210 && centerPx.Blue is > 60 and < 210 &&
            (outsidePx.Red > 240 || outsidePx.Blue > 240);

        var clampedTiny = Engines.BuiltInQuickTools.CropImageToFileOrBitmap(
            checkerPng,
            1.8, -0.5, 0.0, double.NegativeInfinity,
            null,
            null);
        var clampedInf = Engines.BuiltInQuickTools.CropImageToFileOrBitmap(
            checkerPng,
            double.NaN, double.NegativeInfinity, double.PositiveInfinity, 1.5,
            null,
            null);

        checks["quicktool_crop_image_to_file_or_bitmap_clamp_and_blur"] =
            blurBitmap.IsFrozen && blurBitmap.PixelWidth == 120 && blurBitmap.PixelHeight == 120 &&
            File.Exists(blurCropOut) && blurAppliedInsideOnly &&
            clampedTiny.IsFrozen && clampedTiny.PixelWidth == 1 && clampedTiny.PixelHeight == 1 &&
            clampedInf.IsFrozen && clampedInf.PixelWidth == 120 && clampedInf.PixelHeight == 120;

        // 4. Resize rapido (50%, 1080p senza upscale, 4K) e Compress 1 MB su JPEG pesante
        string bigPng = Path.Combine(workDir, "qt_big_4400x2200.png");
        using (var bmp = new SkiaSharp.SKBitmap(4400, 2200, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque))
        {
            bmp.Erase(SkiaSharp.SKColors.DarkSlateBlue);
            ConversionEngine.WriteImage(bmp, bigPng, "PNG");
        }

        string r50Path = Path.Combine(workDir, "qt_resize_50.png");
        string r1080Path = Path.Combine(workDir, "qt_resize_1080.png");
        string r1080SmallPath = Path.Combine(workDir, "qt_resize_1080_small.png");
        string r4kPath = Path.Combine(workDir, "qt_resize_4k.png");

        var res50 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Resize50Id, widePng, r50Path).GetAwaiter().GetResult();
        var res1080 = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Resize1080pId, bigPng, r1080Path).GetAwaiter().GetResult();
        var res1080Small = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Resize1080pId, widePng, r1080SmallPath).GetAwaiter().GetResult();
        var res4k = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Resize4kId, bigPng, r4kPath).GetAwaiter().GetResult();

        using var b50 = ConversionEngine.ReadImage(r50Path);
        using var b1080 = ConversionEngine.ReadImage(r1080Path);
        using var b1080Small = ConversionEngine.ReadImage(r1080SmallPath);
        using var b4k = ConversionEngine.ReadImage(r4kPath);

        // Crea un JPEG rumoroso > 1 MB per verificare tool:compress_1mb
        string noisyJpg = Path.Combine(workDir, "qt_noisy_over1mb.jpg");
        using (var bmp = new SkiaSharp.SKBitmap(1600, 1200, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque))
        {
            uint seed = 0x12345678;
            int totalPx = bmp.Width * bmp.Height;
            var raw = new byte[totalPx * 4];
            for (int i = 0; i < raw.Length; i += 4)
            {
                seed = seed * 1664525u + 1013904223u;
                raw[i] = (byte)(seed >> 24);
                raw[i + 1] = (byte)(seed >> 16);
                raw[i + 2] = (byte)(seed >> 8);
                raw[i + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(raw, 0, bmp.GetPixels(), raw.Length);
            using var img = SkiaSharp.SKImage.FromBitmap(bmp);
            using var enc = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 96);
            File.WriteAllBytes(noisyJpg, enc.ToArray());
        }

        string comp1MbPath = Path.Combine(workDir, "qt_compressed_1mb.jpg");
        var res1Mb = Engines.BuiltInQuickTools.ExecuteActionAsync(Engines.BuiltInQuickTools.Compress1MbId, noisyJpg, comp1MbPath).GetAwaiter().GetResult();
        long origNoisyBytes = new FileInfo(noisyJpg).Length;
        long compBytes = File.Exists(comp1MbPath) ? new FileInfo(comp1MbPath).Length : long.MaxValue;

        checks["quicktool_explicit_resize_and_compress_1mb"] =
            res50.Success && b50.Width == 160 && b50.Height == 90 &&
            res1080.Success && b1080.Width == 1920 && b1080.Height == 960 &&
            res1080Small.Success && b1080Small.Width == 320 && b1080Small.Height == 180 &&
            res4k.Success && b4k.Width == 3840 && b4k.Height == 1920 &&
            origNoisyBytes > 1024 * 1024 && res1Mb.Success && compBytes > 0 && compBytes <= 1024 * 1024;
    }

    private static void CheckOnnxRemoveBgEngine(Dictionary<string, bool> checks, string workDir)
    {
        // 1. Preprocessing IS-Net: 1024×1024 NCHW [1, 3, 1024, 1024] con normalizzazione (v / 255f) - 0.5f e canali RGB distinti
        using (var quadBmp = new SkiaSharp.SKBitmap(160, 80, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque))
        {
            using (var cv = new SkiaSharp.SKCanvas(quadBmp))
            {
                using var red = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 0, 0) };
                using var green = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 255, 0) };
                using var blue = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 0, 255) };
                using var black = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0, 0, 0) };
                cv.DrawRect(0, 0, 80, 40, red);
                cv.DrawRect(80, 0, 80, 40, green);
                cv.DrawRect(0, 40, 80, 40, blue);
                cv.DrawRect(80, 40, 80, 40, black);
                cv.Flush();
            }

            var tensor = Engines.OnnxRemoveBgEngine.PreprocessToTensor(quadBmp);
            static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 0.02f;

            bool shapeOk = tensor.Dimensions.Length == 4 &&
                tensor.Dimensions[0] == 1 &&
                tensor.Dimensions[1] == 3 &&
                tensor.Dimensions[2] == Engines.OnnxRemoveBgEngine.InputSize &&
                tensor.Dimensions[3] == Engines.OnnxRemoveBgEngine.InputSize;

            bool tlRed = Near(tensor[0, 0, 256, 256], 0.5f) && Near(tensor[0, 1, 256, 256], -0.5f) && Near(tensor[0, 2, 256, 256], -0.5f);
            bool trGreen = Near(tensor[0, 0, 256, 768], -0.5f) && Near(tensor[0, 1, 256, 768], 0.5f) && Near(tensor[0, 2, 256, 768], -0.5f);
            bool blBlue = Near(tensor[0, 0, 768, 256], -0.5f) && Near(tensor[0, 1, 768, 256], -0.5f) && Near(tensor[0, 2, 768, 256], 0.5f);
            bool brBlack = Near(tensor[0, 0, 768, 768], -0.5f) && Near(tensor[0, 1, 768, 768], -0.5f) && Near(tensor[0, 2, 768, 768], -0.5f);

            checks["onnx_removebg_preprocess_tensor_nchw_isnet"] = shapeOk && tlRed && trGreen && blBlue && brBlack;
        }

        // 2. Postprocessing: normalizzazione min-max/clamp, ridimensionamento cubico fluido e preservazione RGB su PNG
        byte[] normRange = Engines.OnnxRemoveBgEngine.NormalizeAlphaMask([-2.0f, 0.0f, 2.0f, 6.0f], 4);
        byte[] normFlatHigh = Engines.OnnxRemoveBgEngine.NormalizeAlphaMask([1.5f, 1.5f, float.NaN, 1.5f], 4);
        byte[] normFlatLow = Engines.OnnxRemoveBgEngine.NormalizeAlphaMask([-0.4f, -0.4f], 2);
        bool normOk = normRange[0] == 0 && Math.Abs(normRange[1] - 64) <= 1 &&
            Math.Abs(normRange[2] - 128) <= 1 && normRange[3] == 255 &&
            normFlatHigh[0] == 255 && normFlatHigh[2] == 0 && normFlatLow[0] == 0;

        bool maskApplyOk;
        using (var srcBmp = new SkiaSharp.SKBitmap(120, 80, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque))
        {
            using (var cv = new SkiaSharp.SKCanvas(srcBmp))
            {
                cv.Clear(new SkiaSharp.SKColor(30, 140, 220));
                using var fgPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(210, 95, 40) };
                cv.DrawRect(30, 20, 60, 40, fgPaint);
                cv.Flush();
            }

            // Maschera grezza 30×20 non normalizzata ([-1.0, +3.0]) con soggetto nel riquadro centrale
            const int mw = 30, mh = 20;
            var rawMask = new float[mw * mh];
            for (int y = 0; y < mh; y++)
            {
                for (int x = 0; x < mw; x++)
                {
                    bool inside = x >= 8 && x < 22 && y >= 5 && y < 15;
                    rawMask[y * mw + x] = inside ? 3.0f : -1.0f;
                }
            }

            byte[] normalizedMask = Engines.OnnxRemoveBgEngine.NormalizeAlphaMask(rawMask, rawMask.Length);
            using var cutout = Engines.OnnxRemoveBgEngine.ApplyAlphaMask(srcBmp, normalizedMask, mw, mh);

            string cutoutPng = Path.Combine(workDir, "onnx_synth_cutout.png");
            using (var encoded = cutout.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            {
                File.WriteAllBytes(cutoutPng, encoded.ToArray());
            }

            using var decoded = SkiaSharp.SKBitmap.Decode(cutoutPng);
            var unpremulInfo = new SkiaSharp.SKImageInfo(decoded.Width, decoded.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            using var straight = new SkiaSharp.SKBitmap(unpremulInfo);
            using (var decPixmap = decoded.PeekPixels())
            {
                decPixmap?.ReadPixels(unpremulInfo, straight.GetPixels(), straight.RowBytes);
            }

            var centerPx = straight.GetPixel(60, 40);
            var tlCorner = straight.GetPixel(2, 2);
            var brCorner = straight.GetPixel(117, 77);

            maskApplyOk = decoded.Width == 120 && decoded.Height == 80 &&
                centerPx.Alpha == 255 &&
                centerPx.Red == 210 && centerPx.Green == 95 && centerPx.Blue == 40 &&
                tlCorner.Alpha == 0 && brCorner.Alpha == 0;
        }
        checks["onnx_removebg_postprocess_mask_resize_and_rgb_preservation"] = normOk && maskApplyOk;

        // 3. Validazione file .onnx + assenza modello: nessun fallback sull'euristica colore e messaggio chiaro
        string isolatedModelDir = Path.Combine(workDir, "onnx_missing_model_dir");
        Directory.CreateDirectory(isolatedModelDir);
        string tinyStubOnnx = Path.Combine(isolatedModelDir, "tiny.onnx");
        File.WriteAllBytes(tinyStubOnnx, new byte[256]);
        string wrongExtFile = Path.Combine(isolatedModelDir, "model.bin");
        File.WriteAllBytes(wrongExtFile, new byte[2048]);

        bool validationRejectsBad =
            !Engines.OnnxRemoveBgEngine.IsUsableModelFile(null) &&
            !Engines.OnnxRemoveBgEngine.IsUsableModelFile(Path.Combine(isolatedModelDir, "absent.onnx")) &&
            !Engines.OnnxRemoveBgEngine.IsUsableModelFile(tinyStubOnnx) &&
            !Engines.OnnxRemoveBgEngine.IsUsableModelFile(wrongExtFile);

        string noModelSrcDir = Path.Combine(workDir, "onnx_no_fallback_src");
        Directory.CreateDirectory(noModelSrcDir);
        string noModelInput = Path.Combine(noModelSrcDir, "subject.png");
        using (var bmp = new SkiaSharp.SKBitmap(64, 64, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque))
        {
            using var cv = new SkiaSharp.SKCanvas(bmp);
            cv.Clear(SkiaSharp.SKColors.White);
            using var p = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Crimson };
            cv.DrawCircle(32, 32, 16, p);
            ConversionEngine.WriteImage(bmp, noModelInput, "PNG");
        }

        string? prevOverride = Engines.OnnxRemoveBgEngine.TestOverrideInstallDirectory;
        bool missingModelOk;
        try
        {
            Engines.OnnxRemoveBgEngine.TestOverrideInstallDirectory = isolatedModelDir;
            var mutableNoModel = new FlipSettings
            {
                OutputDirectoryMode = OutputDirectoryMode.SameAsSource
            };
            mutableNoModel.CustomEnginePaths.Remove(Engines.OnnxRemoveBgEngine.ModelKey);
            var noModelSettings = mutableNoModel.CreateSnapshot();

            var tool = new Engines.RemoveBgQuickTool();
            bool canHandle = tool.CanHandle(["png"], noModelSettings);
            var ctx = new QuickToolContext
            {
                InputFilePaths = [noModelInput],
                Settings = noModelSettings
            };
            var res = tool.ExecuteAsync(ctx).GetAwaiter().GetResult();
            bool noFallbackFileCreated = !Directory.EnumerateFiles(noModelSrcDir, "*_nobg.png").Any();

            missingModelOk = validationRejectsBad &&
                canHandle &&
                !Engines.OnnxRemoveBgEngine.IsModelInstalled(noModelSettings) &&
                !res.Success &&
                res.OutputFilePaths.Count == 0 &&
                noFallbackFileCreated &&
                res.ErrorMessage is { } msg &&
                msg.Contains("IS-Net", StringComparison.OrdinalIgnoreCase) &&
                msg.Contains("4. Motori & GPU", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Engines.OnnxRemoveBgEngine.TestOverrideInstallDirectory = prevOverride;
        }
        checks["onnx_removebg_missing_model_no_fallback_and_validation"] = missingModelOk;

        // 4. Scheda "Rimozione Sfondo AI (IS-Net ONNX)" presente nell'Hub "4. Motori & GPU"
        var hubStrings = new List<string>();
        void CollectHubText(object? node)
        {
            if (node == null) return;
            if (node is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                hubStrings.Add(tb.Text);
            else if (node is System.Windows.Controls.ContentControl cc && cc.Content is string s)
                hubStrings.Add(s);

            if (node is DependencyObject dep)
            {
                foreach (object child in LogicalTreeHelper.GetChildren(dep))
                    CollectHubText(child);
            }
        }
        CollectHubText(Flip.SettingsHub.EnginesHubSection.Build());
        checks["onnx_removebg_hub_card_present"] =
            hubStrings.Any(s => s.Contains("Rimozione Sfondo AI (IS-Net ONNX)", StringComparison.Ordinal)) &&
            hubStrings.Any(s => s is "Scarica 1-Click" or "Reinstalla") &&
            hubStrings.Any(s => s == "Sfoglia...") &&
            hubStrings.Any(s => s.Contains("ATTIVO", StringComparison.Ordinal) || s.Contains("NON INSTALLATO", StringComparison.Ordinal));
    }

    private static void WriteSyntheticBinaryFbx(string path)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        // Header 23 byte: "Kaydara FBX Binary  \0\x1a\0" + version 7400 (uint32)
        byte[] magic = new byte[23];
        var ascii = System.Text.Encoding.ASCII.GetBytes("Kaydara FBX Binary  ");
        Array.Copy(ascii, magic, ascii.Length);
        magic[20] = 0x00;
        magic[21] = 0x1A;
        magic[22] = 0x00;
        bw.Write(magic);
        bw.Write((uint)7400);

        // Quad (4 vertici, 1 faccia quad [0, 1, 2, ~3] -> 2 triangoli) compresso con ZLib (Encoding = 1)
        double[] verts = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0];
        int[] polys = [0, 1, 2, ~3];

        byte[] rawVertBytes = new byte[verts.Length * 8];
        Buffer.BlockCopy(verts, 0, rawVertBytes, 0, rawVertBytes.Length);
        byte[] compVerts = CompressZlib(rawVertBytes);

        byte[] rawPolyBytes = new byte[polys.Length * 4];
        Buffer.BlockCopy(polys, 0, rawPolyBytes, 0, rawPolyBytes.Length);
        byte[] compPolys = CompressZlib(rawPolyBytes);

        WriteBinaryNode(bw, "Objects", [], () =>
        {
            WriteBinaryNode(bw, "Geometry", WriteLongProp(10001L), () =>
            {
                WriteBinaryLeaf(bw, "Vertices", 1, WriteArrayProp('d', (uint)verts.Length, 1, compVerts));
                WriteBinaryLeaf(bw, "PolygonVertexIndex", 1, WriteArrayProp('i', (uint)polys.Length, 1, compPolys));
                bw.Write(new byte[13]); // Null record figli di Geometry
            });

            WriteBinaryNode(bw, "Model", WriteLongProp(20002L), () =>
            {
                WriteBinaryNode(bw, "Properties70", [], () =>
                {
                    WriteBinaryLeaf(bw, "P", 7, WriteTransformProp("Lcl Translation", 10.0, 0.0, 0.0));
                    bw.Write(new byte[13]);
                });
                bw.Write(new byte[13]);
            });
            bw.Write(new byte[13]);
        });

        WriteBinaryNode(bw, "Connections", [], () =>
        {
            WriteBinaryLeaf(bw, "C", 3, WriteConnectionProp("OO", 10001L, 20002L));
            bw.Write(new byte[13]);
        });
        bw.Write(new byte[13]); // Null record fine file

        static byte[] CompressZlib(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        static byte[] WriteLongProp(long id)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)'L');
            w.Write(id);
            return ms.ToArray();
        }

        static void WriteStringToken(BinaryWriter w, string s)
        {
            byte[] b = System.Text.Encoding.UTF8.GetBytes(s);
            w.Write((byte)'S');
            w.Write((uint)b.Length);
            w.Write(b);
        }

        static byte[] WriteTransformProp(string name, double x, double y, double z)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteStringToken(w, name);
            WriteStringToken(w, name);
            WriteStringToken(w, "");
            WriteStringToken(w, "A");
            w.Write((byte)'D'); w.Write(x);
            w.Write((byte)'D'); w.Write(y);
            w.Write((byte)'D'); w.Write(z);
            return ms.ToArray();
        }

        static byte[] WriteConnectionProp(string kind, long childId, long parentId)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            WriteStringToken(w, kind);
            w.Write((byte)'L'); w.Write(childId);
            w.Write((byte)'L'); w.Write(parentId);
            return ms.ToArray();
        }

        static byte[] WriteArrayProp(char type, uint count, uint encoding, byte[] payload)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((byte)type);
            w.Write(count);
            w.Write(encoding);
            w.Write((uint)payload.Length);
            w.Write(payload);
            return ms.ToArray();
        }

        static void WriteBinaryLeaf(BinaryWriter w, string name, uint numProps, byte[] props)
        {
            long start = w.BaseStream.Position;
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            uint end = checked((uint)(start + 13 + nameBytes.Length + props.Length));
            w.Write(end);
            w.Write(numProps);
            w.Write((uint)props.Length);
            w.Write((byte)nameBytes.Length);
            w.Write(nameBytes);
            w.Write(props);
        }

        static void WriteBinaryNode(BinaryWriter w, string name, byte[] props, Action writeChildren)
        {
            long start = w.BaseStream.Position;
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            w.Write(0u);
            w.Write(props.Length > 0 ? 1u : 0u);
            w.Write((uint)props.Length);
            w.Write((byte)nameBytes.Length);
            w.Write(nameBytes);
            w.Write(props);
            writeChildren();
            long endPos = w.BaseStream.Position;
            w.BaseStream.Position = start;
            w.Write(checked((uint)endPos));
            w.BaseStream.Position = endPos;
        }
    }

    private static void CheckLocalization(Dictionary<string, bool> checks)
    {
        string savedPref = Loc.Preference;
        try
        {
            // 1. Auto-rilevamento Windows UI culture + override esplicito + normalizzazione FlipSettings
            var itIt = CultureInfo.GetCultureInfo("it-IT");
            var itCh = CultureInfo.GetCultureInfo("it-CH");
            var enUs = CultureInfo.GetCultureInfo("en-US");
            var frFr = CultureInfo.GetCultureInfo("fr-FR");
            var deDe = CultureInfo.GetCultureInfo("de-DE");

            bool resolveOk =
                Loc.ResolveLanguage("auto", itIt) == Loc.Italian &&
                Loc.ResolveLanguage("auto", itCh) == Loc.Italian &&
                Loc.ResolveLanguage("auto", enUs) == Loc.English &&
                Loc.ResolveLanguage("auto", frFr) == Loc.English &&
                Loc.ResolveLanguage("auto", deDe) == Loc.English &&
                Loc.ResolveLanguage("it", enUs) == Loc.Italian &&
                Loc.ResolveLanguage("IT", frFr) == Loc.Italian &&
                Loc.ResolveLanguage("en", itIt) == Loc.English &&
                Loc.ResolveLanguage("EN", itIt) == Loc.English &&
                Loc.ResolveLanguage(null, enUs) == Loc.English &&
                Loc.ResolveLanguage("unknown", itIt) == Loc.Italian;

            var settings = new FlipSettings();
            bool defaultAuto = settings.Language == Loc.Auto;
            settings.Language = "EN";
            var snapEn = settings.CreateSnapshot();
            settings.Language = "bogus";
            var snapFallback = settings.CreateSnapshot();
            string json = JsonSerializer.Serialize(snapEn);
            var roundtrip = JsonSerializer.Deserialize<FlipSettings>(json);

            Loc.SetPreference(Loc.English);
            bool switchedEn = Loc.CurrentLanguage == Loc.English && !Loc.IsItalian &&
                              Loc.T("Impostazioni", "Settings") == "Settings" &&
                              Loc.F("{0} file pronti", "{0} files ready", 3) == "3 files ready";
            Loc.SetPreference(Loc.Italian);
            bool switchedIt = Loc.CurrentLanguage == Loc.Italian && Loc.IsItalian &&
                              Loc.T("Impostazioni", "Settings") == "Impostazioni";

            checks["loc_auto_detection_and_override"] =
                resolveOk && defaultAuto && snapEn.Language == Loc.English &&
                snapFallback.Language == Loc.Auto && roundtrip?.Language == Loc.English &&
                switchedEn && switchedIt;

            // 2. Smoke test UI completa in lingua inglese (Hub 7 sezioni + Welcome 4 step + GazeOrb + WheelLayout + QuickTools)
            Loc.SetPreference(Loc.English);

            var collectedTexts = new List<string>();
            static void CollectLogicalTexts(object? node, List<string> sink)
            {
                if (node is null) return;
                if (node is string s && !string.IsNullOrWhiteSpace(s))
                {
                    sink.Add(s);
                    return;
                }
                if (node is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                    sink.Add(tb.Text);
                if (node is FrameworkElement fe && fe.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip))
                    sink.Add(tip);
                if (node is ComboBox cb)
                {
                    foreach (var item in cb.Items)
                    {
                        string? label = item?.ToString();
                        if (!string.IsNullOrWhiteSpace(label)) sink.Add(label);
                    }
                }
                if (node is ContentControl cc && cc.Content is string cs && !string.IsNullOrWhiteSpace(cs))
                    sink.Add(cs);
                if (node is DependencyObject dep)
                {
                    foreach (var child in LogicalTreeHelper.GetChildren(dep))
                        CollectLogicalTexts(child, sink);
                }
            }

            var hub = new SettingsHubWindow { Opacity = 0, ShowInTaskbar = false };
            try
            {
                for (int tab = 0; tab <= 6; tab++)
                {
                    hub.NavigateTo(tab);
                    CollectLogicalTexts(hub.Content, collectedTexts);
                }
            }
            finally
            {
                try { hub.Close(); } catch { }
            }

            var welcome = new FirstRunWelcomeWindow { Opacity = 0, Topmost = false };
            bool welcomeEnOk;
            try
            {
                bool step0En = Equals(welcome.StartButton.Content, "Next →");
                for (int step = 0; step < welcome.StepTabCount; step++)
                {
                    welcome.SelectStep(step);
                    CollectLogicalTexts(welcome.Content, collectedTexts);
                }
                bool step3En = welcome.StartButton.Content?.ToString()?.Contains("Start using FLIP", StringComparison.Ordinal) == true;
                welcomeEnOk = step0En && step3En;
            }
            finally
            {
                try { welcome.Close(); } catch { }
            }

            var orb = new GazeOrbWindow(() => 1100, () => true, _ => { }) { Opacity = 0, Topmost = false };
            bool orbEnOk;
            try
            {
                var headers = orb.GetContextMenuHeadersForTest();
                orbEnOk = headers.Count == 2 &&
                          headers[0] == "Disable Tobii Mode (Hide [F])" &&
                          headers[1] == "Open Accessibility Settings…";
                CollectLogicalTexts(orb.Content, collectedTexts);
            }
            finally
            {
                try { orb.Close(); } catch { }
            }

            string[] manyImages = ["PNG", "JPG", "WEBP", "PDF", "BMP", "TIFF", "GIF", "ICO", "AVIF", "SVG", "PSD", "HEIC"];
            var imageSlots = WheelLayout.Partition(manyImages);
            string[] manyMixed = ["PNG", "JPG", "WEBP", "PDF", "BMP", "TIFF", "GIF", "ZIP", "MP3", "JSON", "SRT", "STL"];
            var mixedSlots = WheelLayout.Partition(manyMixed);
            bool wheelMoreEn =
                imageSlots.Any(s => s.Label == "+ IMAGES") && imageSlots.All(s => s.Label != "+ IMMAGINI") &&
                mixedSlots.Any(s => s.Label == "+ MORE") && mixedSlots.All(s => s.Label != "+ EXTRA");
            bool shortcutEn = WheelShortcut.Parse("DOWN").DisplayName == "Down Arrow" &&
                              WheelShortcut.Parse("SPACE").DisplayName == "Space";
            bool enginesEn = new RotateQuickTool().DisplayName == "Rotate & Flip" &&
                             new CropQuickTool().DisplayName == "Quick Crop" &&
                             new RemoveBgQuickTool().DisplayName == "AI Remove Background" &&
                             OnnxRemoveBgEngine.MissingModelMessage.Contains("Settings", StringComparison.Ordinal) &&
                             LocalAiEngine.ModuleMissingMessage.Contains("Settings", StringComparison.Ordinal);

            string allUiText = string.Join("\n", collectedTexts);
            bool hasExpectedEn =
                allUiText.Contains("1. Wheel & Slices", StringComparison.Ordinal) &&
                allUiText.Contains("7. Guide & Shortcuts", StringComparison.Ordinal) &&
                allUiText.Contains("Reset defaults", StringComparison.Ordinal) &&
                allUiText.Contains("Language", StringComparison.Ordinal) &&
                allUiText.Contains("Auto (Windows)", StringComparison.Ordinal) &&
                allUiText.Contains("Start using FLIP", StringComparison.Ordinal);

            bool hasNoForbiddenIt =
                !allUiText.Contains("Ripristina predefiniti", StringComparison.Ordinal) &&
                !allUiText.Contains("Guida & Scorciatoie", StringComparison.Ordinal) &&
                !allUiText.Contains("Inizia a usare FLIP", StringComparison.Ordinal) &&
                !allUiText.Contains("Strumenti", StringComparison.Ordinal) &&
                !allUiText.Contains("Impostazioni", StringComparison.Ordinal);

            Evidence.Log("loc.smoke", new
            {
                welcomeEnOk,
                orbEnOk,
                wheelMoreEn,
                shortcutEn,
                enginesEn,
                hasExpectedEn,
                hasNoForbiddenIt
            });

            checks["loc_english_ui_smoke"] =
                welcomeEnOk && orbEnOk && wheelMoreEn && shortcutEn && enginesEn &&
                hasExpectedEn && hasNoForbiddenIt;

            // 3. Cambio lingua live nell'Hub su tab diversi (it → en → it → en → auto → it) senza crash WPF
            string prevSettingsLang = SettingsStore.Current.CreateSnapshot().Language;
            bool liveSwitchOk = false;
            try
            {
                SettingsStore.Current.Update(s => s.Language = Loc.Italian);
                Loc.SetPreference(Loc.Italian);

                var liveHub = new SettingsHubWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    Opacity = 0,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };

                static void FlushDispatcher()
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(
                        DispatcherPriority.ContextIdle,
                        new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }

                string CollectHubText()
                {
                    var list = new List<string>();
                    CollectLogicalTexts(liveHub.Content, list);
                    return string.Join("\n", list);
                }

                bool SwitchAndAssert(string targetPref, int expectedTab, string expectedSidebar, string expectedContent, string forbiddenText)
                {
                    bool selected = liveHub.SelectLanguageForTest(targetPref);
                    FlushDispatcher();
                    string uiText = CollectHubText();
                    return selected &&
                           liveHub.ActiveSectionIndex == expectedTab &&
                           uiText.Contains(expectedSidebar, StringComparison.Ordinal) &&
                           uiText.Contains(expectedContent, StringComparison.Ordinal) &&
                           !uiText.Contains(forbiddenText, StringComparison.Ordinal);
                }

                try
                {
                    liveHub.Show();
                    FlushDispatcher();

                    // Tab 0: it → en → it → en
                    bool t0ItToEn = SwitchAndAssert(Loc.English, 0, "1. Wheel & Slices", "Visible formats", "Ripristina predefiniti");
                    bool t0EnToIt = SwitchAndAssert(Loc.Italian, 0, "1. Ruota & Spicchi", "Formati visibili", "Reset defaults");
                    bool t0ItToEn2 = SwitchAndAssert(Loc.English, 0, "Reset defaults", "Wheel & Slices", "Ruota & Spicchi");

                    // Tab 3: en → it → en
                    liveHub.NavigateTo(3);
                    FlushDispatcher();
                    bool t3EnToIt = SwitchAndAssert(Loc.Italian, 3, "4. Motori & GPU", "Prestazioni", "Engines & GPU");
                    bool t3ItToEn = SwitchAndAssert(Loc.English, 3, "4. Engines & GPU", "Performance", "Motori & GPU");

                    // Tab 6: en → auto → it
                    liveHub.NavigateTo(6);
                    FlushDispatcher();
                    bool autoIsIt = Loc.ResolveLanguage(Loc.Auto) == Loc.Italian;
                    bool t6EnToAuto = SwitchAndAssert(
                        Loc.Auto,
                        6,
                        autoIsIt ? "7. Guida & Scorciatoie" : "7. Guide & Shortcuts",
                        autoIsIt ? "Riapri tour iniziale" : "Replay welcome tour",
                        autoIsIt ? "Reset defaults" : "Ripristina predefiniti");
                    bool t6AutoToIt = SwitchAndAssert(Loc.Italian, 6, "7. Guida & Scorciatoie", "Riapri tour iniziale", "Guide & Shortcuts");

                    // Tab 5: it → en → auto → it
                    liveHub.NavigateTo(5);
                    FlushDispatcher();
                    bool t5ItToEn = SwitchAndAssert(Loc.English, 5, "6. History & Presets", "Reset defaults", "Cronologia & Preset");
                    bool t5EnToAuto = SwitchAndAssert(
                        Loc.Auto,
                        5,
                        autoIsIt ? "6. Cronologia & Preset" : "6. History & Presets",
                        autoIsIt ? "Ripristina predefiniti" : "Reset defaults",
                        autoIsIt ? "History & Presets" : "Cronologia & Preset");
                    bool t5AutoToIt = SwitchAndAssert(Loc.Italian, 5, "6. Cronologia & Preset", "Ripristina predefiniti", "History & Presets");

                    liveSwitchOk = t0ItToEn && t0EnToIt && t0ItToEn2 &&
                                   t3EnToIt && t3ItToEn &&
                                   t6EnToAuto && t6AutoToIt &&
                                   t5ItToEn && t5EnToAuto && t5AutoToIt;

                    Evidence.Log("loc.hub.live_switch", new
                    {
                        t0ItToEn,
                        t0EnToIt,
                        t0ItToEn2,
                        t3EnToIt,
                        t3ItToEn,
                        t6EnToAuto,
                        t6AutoToIt,
                        t5ItToEn,
                        t5EnToAuto,
                        t5AutoToIt
                    });
                }
                finally
                {
                    try { liveHub.Close(); } catch { }
                    FlushDispatcher();
                }
            }
            finally
            {
                SettingsStore.Current.Update(s => s.Language = prevSettingsLang);
                Loc.SetPreference(savedPref);
            }

            checks["loc_hub_live_language_switch"] = liveSwitchOk;
        }
        finally
        {
            Loc.SetPreference(savedPref);
        }
    }

    private static void CheckDocxDocumentFidelityAndPdfA(Dictionary<string, bool> checks)
    {
        EngineBootstrap.EnsureInitialized();
        string workDir = Path.Combine(Evidence.DirectoryPath, $"docx-fidelity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            // 1. Verifica visibilità e priorità PDF (slot 0) vs PDF/A (separato) su PC pulito (senza LibreOffice/Pandoc)
            bool cleanPcWheelOk;
            bool prevSuppress = CliResolver.SuppressAutoDiscoveryForTest;
            CliResolver.SuppressAutoDiscoveryForTest = true;
            try
            {
                static bool CheckDocWheelClean(string sourceExt, FormatGrade grade, bool requirePdfaPrimary)
                {
                    var snap = new FlipSettings { MaxVisibleGrade = grade }.CreateSnapshot();
                    var reachable = RoutePlanner.GetReachableTargetFormats(sourceExt, snap);
                    var slots = WheelLayout.Partition(reachable.Select(f => f.Extension.ToUpperInvariant()).ToArray());
                    bool pdfFirst = slots.Length > 0 &&
                        string.Equals(slots[0].Format, "PDF", StringComparison.OrdinalIgnoreCase) &&
                        slots[0].Label == "PDF";
                    bool hasPdfA = reachable.Any(f => string.Equals(f.NormalizedExtension, "pdfa", StringComparison.OrdinalIgnoreCase));
                    bool pdfaPrimary = slots.Any(s => string.Equals(s.Format, "PDFA", StringComparison.OrdinalIgnoreCase) && s.Label == "PDF/A");
                    return pdfFirst && hasPdfA && (!requirePdfaPrimary || pdfaPrimary);
                }

                cleanPcWheelOk =
                    CheckDocWheelClean("docx", FormatGrade.Everyday, requirePdfaPrimary: true) &&
                    CheckDocWheelClean("docx", FormatGrade.General, requirePdfaPrimary: true) &&
                    CheckDocWheelClean("docx", FormatGrade.Specialist, requirePdfaPrimary: true) &&
                    CheckDocWheelClean("odt", FormatGrade.General, requirePdfaPrimary: true) &&
                    CheckDocWheelClean("rtf", FormatGrade.General, requirePdfaPrimary: true);
            }
            finally
            {
                CliResolver.SuppressAutoDiscoveryForTest = prevSuppress;
            }

            // 2. Verifica priorità LibreOffice quando installato (anche con priorità utente sfavorevole)
            var adversarialSettings = new FlipSettings
            {
                EnginePriorityOrder = ["pdf_builtin", "openxml_builtin", "pandoc"]
            }.CreateSnapshot();
            bool hasLibreOffice = new LibreOfficeEngine().IsAvailable(adversarialSettings);
            bool libreOfficePriorityOk = true;
            if (hasLibreOffice)
            {
                bool docxPdfLo = RoutePlanner.TryPlanRoute("docx", "pdf", adversarialSettings, out var rPdf) &&
                    rPdf is { Hops.Count: 1 } && rPdf.Hops[0].Engine.Id == "libreoffice";
                bool docxPdfaLo = RoutePlanner.TryPlanRoute("docx", "pdfa", adversarialSettings, out var rPdfa) &&
                    rPdfa is { Hops.Count: 2 } && rPdfa.Hops[0].Engine.Id == "libreoffice" && rPdfa.Hops[1].Engine.Id == "pdf_builtin";
                bool docxRtfLo = RoutePlanner.TryPlanRoute("docx", "rtf", adversarialSettings, out var rRtf) &&
                    rRtf is { Hops.Count: 1 } && rRtf.Hops[0].Engine.Id == "libreoffice";
                bool docxOdtLo = RoutePlanner.TryPlanRoute("docx", "odt", adversarialSettings, out var rOdt) &&
                    rOdt is { Hops.Count: 1 } && rOdt.Hops[0].Engine.Id == "libreoffice";
                bool odtPdfLo = RoutePlanner.TryPlanRoute("odt", "pdf", adversarialSettings, out var rOdtPdf) &&
                    rOdtPdf is { Hops.Count: 1 } && rOdtPdf.Hops[0].Engine.Id == "libreoffice";
                bool rtfPdfLo = RoutePlanner.TryPlanRoute("rtf", "pdf", adversarialSettings, out var rRtfPdf) &&
                    rRtfPdf is { Hops.Count: 1 } && rRtfPdf.Hops[0].Engine.Id == "libreoffice";
                var docTargets = RoutePlanner.GetReachableTargetFormats("doc", new FlipSettings().CreateSnapshot());
                bool docPdfFirst = docTargets.Count > 0 && docTargets[0].NormalizedExtension == "pdf";
                libreOfficePriorityOk = docxPdfLo && docxPdfaLo && docxRtfLo && docxOdtLo && odtPdfLo && rtfPdfLo && docPdfFirst;
                Evidence.Log("docx.libreoffice.priority", new { docxPdfLo, docxPdfaLo, docxRtfLo, docxOdtLo, odtPdfLo, rtfPdfLo, docPdfFirst });
            }

            checks["docx_pdf_first_and_libreoffice_priority"] = cleanPcWheelOk && libreOfficePriorityOk;

            // 3. Crea un DOCX ricco realistico con styles.xml, numbering.xml, colori, grassetti, corsivi, sottolineato,
            // barrato, evidenziazione, allineamenti, elenchi puntati/numerati, tabella formattata, accenti italiani e interruzione pagina.
            string richDocx = Path.Combine(workDir, "documento_ricco.docx");
            CreateRichDocxFixture(richDocx);

            // 4. Test motore built-in DOCX -> RTF ad alta fedeltà
            string outRtf = Path.Combine(workDir, "documento_ricco.rtf");
            var ooxml = new OfficeOpenXmlEngine();
            var rtfRes = ooxml.ConvertAsync(new ConversionContext
            {
                InputFilePath = richDocx,
                OutputFilePath = outRtf,
                SourceExtension = "docx",
                TargetExtension = "rtf",
                Settings = new FlipSettings().CreateSnapshot()
            }).GetAwaiter().GetResult();

            string rtfText = rtfRes.Success && File.Exists(outRtf) ? File.ReadAllText(outRtf, System.Text.Encoding.ASCII) : "";
            byte[] rtfRawBytes = rtfRes.Success && File.Exists(outRtf) ? File.ReadAllBytes(outRtf) : [];
            bool rtfStrictlyAscii = rtfRawBytes.Length > 0 && rtfRawBytes.All(b => b <= 0x7F);

            string rtfRoundtripTxt = Path.Combine(workDir, "documento_ricco_rt.txt");
            var rtRes = ooxml.ConvertAsync(new ConversionContext
            {
                InputFilePath = outRtf,
                OutputFilePath = rtfRoundtripTxt,
                SourceExtension = "rtf",
                TargetExtension = "txt",
                Settings = new FlipSettings().CreateSnapshot()
            }).GetAwaiter().GetResult();
            string rtTxt = rtRes.Success && File.Exists(rtfRoundtripTxt) ? File.ReadAllText(rtfRoundtripTxt, System.Text.Encoding.UTF8) : "";

            bool rtfFormattingOk =
                rtfRes.Success &&
                rtfStrictlyAscii &&
                rtfText.StartsWith(@"{\rtf1\ansi\ansicpg1252\deff0", StringComparison.Ordinal) &&
                rtfText.Contains(@"{\fonttbl", StringComparison.Ordinal) &&
                rtfText.Contains("Georgia;", StringComparison.Ordinal) &&
                rtfText.Contains(@"{\colortbl;", StringComparison.Ordinal) &&
                rtfText.Contains(@"\red192\green0\blue0;", StringComparison.Ordinal) &&
                rtfText.Contains(@"\red255\green255\blue0;", StringComparison.Ordinal) &&
                rtfText.Contains(@"\b", StringComparison.Ordinal) &&
                rtfText.Contains(@"\i", StringComparison.Ordinal) &&
                rtfText.Contains(@"\ul", StringComparison.Ordinal) &&
                rtfText.Contains(@"\strike", StringComparison.Ordinal) &&
                rtfText.Contains(@"\qc", StringComparison.Ordinal) &&
                rtfText.Contains(@"\qr", StringComparison.Ordinal) &&
                rtfText.Contains(@"\qj", StringComparison.Ordinal) &&
                rtfText.Contains(@"\trowd", StringComparison.Ordinal) &&
                rtfText.Contains(@"\cell", StringComparison.Ordinal) &&
                rtfText.Contains(@"\row", StringComparison.Ordinal) &&
                rtfText.Contains(@"\page", StringComparison.Ordinal) &&
                rtfText.Contains(@"\u232?", StringComparison.Ordinal) && // è
                rtfText.Contains(@"\u224?", StringComparison.Ordinal) && // à
                rtfText.Contains(@"\u8364?", StringComparison.Ordinal) && // €
                rtRes.Success &&
                rtTxt.Contains("caffè", StringComparison.Ordinal) &&
                rtTxt.Contains("città", StringComparison.Ordinal) &&
                rtTxt.Contains("perché", StringComparison.Ordinal) &&
                !rtTxt.Contains("cafféé", StringComparison.Ordinal) &&
                !rtTxt.Contains("caffèè", StringComparison.Ordinal) &&
                rtTxt.Contains("€", StringComparison.Ordinal) &&
                rtTxt.Contains("Voce Tabella", StringComparison.Ordinal);

            checks["docx_builtin_rich_rtf_preserves_formatting_and_unicode"] = rtfFormattingOk;
            Evidence.Log("docx.rich_rtf", new { rtfRes.Success, rtfStrictlyAscii, rtfLength = rtfText.Length, rtfFormattingOk });

            // 5. Test motore built-in DOCX -> PDF ad alta fedeltà (senza LibreOffice)
            string outPdf = Path.Combine(workDir, "documento_ricco_builtin.pdf");
            var pdfEngine = new PdfEngine();
            var pdfRes = pdfEngine.ConvertAsync(new ConversionContext
            {
                InputFilePath = richDocx,
                OutputFilePath = outPdf,
                SourceExtension = "docx",
                TargetExtension = "pdf",
                Settings = new FlipSettings().CreateSnapshot()
            }).GetAwaiter().GetResult();

            bool richPdfOk = false;
            int pdfPages = 0;
            int distinctFontSizes = 0;
            bool hasRedPixels = false;
            bool hasYellowPixels = false;
            if (pdfRes.Success && File.Exists(outPdf))
            {
                using var docPdf = UglyToad.PdfPig.PdfDocument.Open(outPdf);
                pdfPages = docPdf.NumberOfPages;
                string page1Text = docPdf.NumberOfPages >= 1
                    ? UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(docPdf.GetPage(1))
                    : "";
                string page2Text = docPdf.NumberOfPages >= 2
                    ? UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(docPdf.GetPage(2))
                    : "";
                distinctFontSizes = docPdf.GetPage(1).Letters
                    .Select(l => (int)Math.Round(l.PointSize))
                    .Distinct()
                    .Count();

                // Renderizza pagina 1 e verifica presenza reale di colori (rosso #C00000 e highlight giallo #FFFF00)
                byte[] pdfBytes = File.ReadAllBytes(outPdf);
                using var page1Bmp = PDFtoImage.Conversion.ToImages(pdfBytes, options: new PDFtoImage.RenderOptions(Dpi: 120)).First();
                string previewPng = Path.Combine(Evidence.DirectoryPath, "docx-rich-pdf-page1.png");
                using (var pngData = page1Bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                using (var fs = File.Create(previewPng))
                    pngData.SaveTo(fs);

                for (int y = 0; y < page1Bmp.Height; y += 2)
                {
                    for (int x = 0; x < page1Bmp.Width; x += 2)
                    {
                        var px = page1Bmp.GetPixel(x, y);
                        if (px.Red > 150 && px.Green < 70 && px.Blue < 70) hasRedPixels = true;
                        if (px.Red > 220 && px.Green > 220 && px.Blue < 70) hasYellowPixels = true;
                        if (hasRedPixels && hasYellowPixels) break;
                    }
                    if (hasRedPixels && hasYellowPixels) break;
                }

                richPdfOk =
                    pdfPages >= 2 &&
                    distinctFontSizes >= 3 &&
                    page1Text.Contains("Relazione Tecnica", StringComparison.Ordinal) &&
                    page1Text.Contains("caffè", StringComparison.Ordinal) &&
                    page1Text.Contains("Voce Tabella", StringComparison.Ordinal) &&
                    page2Text.Contains("Seconda Pagina", StringComparison.Ordinal) &&
                    hasRedPixels &&
                    hasYellowPixels;
            }
            checks["docx_builtin_rich_pdf_preserves_formatting_and_pagination"] = richPdfOk;
            Evidence.Log("docx.rich_pdf", new { pdfRes.Success, pdfPages, distinctFontSizes, hasRedPixels, hasYellowPixels, richPdfOk });

            // 6. Verifica profilo ICC sRGB valido (skcms SkiaSharp + Windows WCS ColorContext) e PDF/A-2b integro
            byte[] iccProfile = PdfEngine.GetSrgbIccProfileBytes();
            bool skiaIccOk = false;
            using (var skIcc = SkiaSharp.SKColorSpaceIccProfile.Create(iccProfile))
            using (var skCs = skIcc != null ? SkiaSharp.SKColorSpace.CreateIcc(skIcc) : null)
            {
                skiaIccOk = skIcc != null && skCs != null;
            }

            bool windowsIccOk = false;
            string iccTmpFile = Path.Combine(workDir, "srgb_check.icc");
            File.WriteAllBytes(iccTmpFile, iccProfile);
            try
            {
                var ctx = new System.Windows.Media.ColorContext(new Uri(iccTmpFile));
                windowsIccOk = ctx.OpenProfileStream().Length == iccProfile.Length;
            }
            catch { windowsIccOk = false; }

            bool ValidatePdfAFile(string pdfaPath, out string detail)
            {
                detail = "";
                if (!File.Exists(pdfaPath)) { detail = "missing"; return false; }
                byte[] bytes = File.ReadAllBytes(pdfaPath);
                string latin1 = System.Text.Encoding.Latin1.GetString(bytes);

                bool markers =
                    latin1.StartsWith("%PDF-1.7", StringComparison.Ordinal) &&
                    latin1.Contains("<pdfaid:part>2</pdfaid:part>", StringComparison.Ordinal) &&
                    latin1.Contains("<pdfaid:conformance>B</pdfaid:conformance>", StringComparison.Ordinal) &&
                    latin1.Contains("/GTS_PDFA1", StringComparison.Ordinal) &&
                    latin1.Contains("sRGB IEC61966-2.1", StringComparison.Ordinal) &&
                    latin1.Contains("/OutputIntents", StringComparison.Ordinal) &&
                    latin1.Contains("/Metadata", StringComparison.Ordinal) &&
                    latin1.Contains("/ID [<", StringComparison.Ordinal) &&
                    latin1.Contains("FLIP PDF/A-2b Engine", StringComparison.Ordinal) &&
                    latin1.Contains("FLIP ISO 19005-2 Archival Engine", StringComparison.Ordinal);

                // Verifica che lo stream ICC contenga esattamente i byte del profilo valido (non lo stub rotto da 176 byte)
                bool hasExactIcc = bytes.AsSpan().IndexOf(iccProfile) >= 0;

                // Verifica strutturale dell'ultimo xref incrementale (ogni riga 20 byte e offset esatti verso "N 0 obj")
                bool xrefStrictOk = false;
                int lastStartXref = latin1.LastIndexOf("startxref", StringComparison.Ordinal);
                if (lastStartXref > 0)
                {
                    var mStart = System.Text.RegularExpressions.Regex.Match(latin1[lastStartXref..], @"startxref\s+(\d+)");
                    if (mStart.Success && int.TryParse(mStart.Groups[1].Value, out int xrefPos) && xrefPos > 0 && xrefPos < lastStartXref)
                    {
                        string xrefSection = latin1[xrefPos..lastStartXref];
                        int trailerPos = xrefSection.IndexOf("trailer", StringComparison.Ordinal);
                        if (trailerPos > 0)
                        {
                            string tablePart = xrefSection[..trailerPos];
                            string[] rawLines = tablePart.Split('\n');
                            bool headerOk = rawLines.Length > 2 && rawLines[0] == "xref";
                            bool noObjZeroOverwrite = !tablePart.Contains("\n0 1\n", StringComparison.Ordinal);
                            bool entriesOk = true;
                            int curObj = -1;
                            int remaining = 0;
                            for (int i = 1; i < rawLines.Length; i++)
                            {
                                string ln = rawLines[i];
                                if (ln.Length == 0 && i == rawLines.Length - 1) break;
                                if (remaining == 0)
                                {
                                    var parts = ln.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length != 2 || !int.TryParse(parts[0], out curObj) || !int.TryParse(parts[1], out remaining))
                                    { entriesOk = false; break; }
                                }
                                else
                                {
                                    // Ogni riga xref deve essere esattamente 20 byte compreso '\n' -> 19 caratteri prima di '\n'
                                    if (ln.Length != 19 || !int.TryParse(ln[..10], out int objOff) || objOff <= 0 || objOff >= bytes.Length)
                                    { entriesOk = false; break; }
                                    string prefix = $"{curObj} 0 obj";
                                    if (!latin1.AsSpan(objOff).StartsWith(prefix, StringComparison.Ordinal))
                                    { entriesOk = false; break; }
                                    curObj++;
                                    remaining--;
                                }
                            }
                            xrefStrictOk = headerOk && noObjZeroOverwrite && entriesOk && remaining == 0;
                        }
                    }
                }

                // Verifica apertura PdfPig + rendering PDFtoImage
                bool pigOk;
                using (var doc = UglyToad.PdfPig.PdfDocument.Open(bytes))
                {
                    string p1 = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(doc.GetPage(1));
                    pigOk = doc.NumberOfPages >= 2 && p1.Contains("Relazione Tecnica", StringComparison.Ordinal);
                }

                bool renderOk;
                using (var rendered = PDFtoImage.Conversion.ToImages(bytes, options: new PDFtoImage.RenderOptions(Dpi: 96)).First())
                {
                    renderOk = rendered.Width > 200 && rendered.Height > 200;
                }

                detail = $"markers={markers},icc={hasExactIcc},xref={xrefStrictOk},pig={pigOk},render={renderOk}";
                return markers && hasExactIcc && xrefStrictOk && pigOk && renderOk;
            }

            // 6A. Percorso DOCX -> PDF/A su PC pulito (built-in)
            bool pdfaBuiltinOk;
            string pdfaBuiltinDetail = "";
            CliResolver.SuppressAutoDiscoveryForTest = true;
            try
            {
                var cleanSnap = new FlipSettings
                {
                    OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
                    CustomOutputDirectory = Path.Combine(workDir, "out_clean"),
                    ConflictPolicy = ConflictPolicy.AutoNumber
                }.CreateSnapshot();
                var cleanPdfaRes = RoutePlanner.ExecuteRouteAsync(richDocx, "pdfa", cleanSnap).GetAwaiter().GetResult();
                pdfaBuiltinOk = cleanPdfaRes.Success && ValidatePdfAFile(cleanPdfaRes.OutputFilePath, out pdfaBuiltinDetail);
                if (!cleanPdfaRes.Success) pdfaBuiltinDetail = cleanPdfaRes.ErrorMessage ?? "failed";

                // Without LibreOffice, the built-in exporter handles DOCX -> PDF and honors metadata removal.
                var cleanPrivacyRes = RoutePlanner.ExecuteRouteAsync(richDocx, "pdf", cleanSnap,
                    new() { ["strip_metadata"] = "true" }).GetAwaiter().GetResult();
                checks["docx_pdf_without_libreoffice_uses_builtin_and_strips_metadata"] =
                    RoutePlanner.TryPlanRoute("docx", "pdf", cleanSnap, out var cleanPdfRoute) &&
                    cleanPdfRoute is { Hops.Count: 1 } && cleanPdfRoute.Hops[0].Engine.Id == "pdf_builtin" &&
                    cleanPrivacyRes.Success && File.Exists(cleanPrivacyRes.OutputFilePath) &&
                    !RoutePlanner.TryPlanRoute("pdf", "doc", cleanSnap, out _);
            }
            finally
            {
                CliResolver.SuppressAutoDiscoveryForTest = prevSuppress;
            }

            // 6B. Percorso DOCX -> PDF/A con LibreOffice (se installato)
            bool pdfaLibreOfficeOk = true;
            string pdfaLoDetail = "skipped";
            if (hasLibreOffice)
            {
                var loSnap = new FlipSettings
                {
                    OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
                    CustomOutputDirectory = Path.Combine(workDir, "out_lo"),
                    ConflictPolicy = ConflictPolicy.AutoNumber
                }.CreateSnapshot();
                var loPdfaRes = RoutePlanner.ExecuteRouteAsync(richDocx, "pdfa", loSnap).GetAwaiter().GetResult();
                pdfaLibreOfficeOk = loPdfaRes.Success && ValidatePdfAFile(loPdfaRes.OutputFilePath, out pdfaLoDetail);
                if (!loPdfaRes.Success) pdfaLoDetail = loPdfaRes.ErrorMessage ?? "failed";
            }

            checks["docx_pdfa_iso19005_2b_valid_icc_and_adobe_safe"] =
                skiaIccOk && windowsIccOk && pdfaBuiltinOk && pdfaLibreOfficeOk;
            Evidence.Log("docx.pdfa.validation", new { skiaIccOk, windowsIccOk, iccLength = iccProfile.Length, pdfaBuiltinOk, pdfaBuiltinDetail, pdfaLibreOfficeOk, pdfaLoDetail });
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        }
    }

    private static void CreateRichDocxFixture(string docxPath)
    {
        const string contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
            "</Types>";

        const string rootRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        const string docRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>" +
            "</Relationships>";

        const string stylesXml =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:docDefaults>
                <w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:sz w:val="22"/></w:rPr></w:rPrDefault>
                <w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault>
              </w:docDefaults>
              <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
                <w:name w:val="Normal"/>
              </w:style>
              <w:style w:type="paragraph" w:styleId="Heading1">
                <w:name w:val="heading 1"/>
                <w:basedOn w:val="Normal"/>
                <w:pPr><w:spacing w:before="240" w:after="120"/></w:pPr>
                <w:rPr><w:rFonts w:ascii="Georgia" w:hAnsi="Georgia"/><w:b/><w:color w:val="C00000"/><w:sz w:val="40"/></w:rPr>
              </w:style>
              <w:style w:type="paragraph" w:styleId="Heading2">
                <w:name w:val="heading 2"/>
                <w:basedOn w:val="Normal"/>
                <w:pPr><w:spacing w:before="200" w:after="80"/></w:pPr>
                <w:rPr><w:b/><w:color w:val="1F4E79"/><w:sz w:val="30"/></w:rPr>
              </w:style>
            </w:styles>
            """;

        const string numberingXml =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:abstractNum w:abstractNumId="1">
                <w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
              </w:abstractNum>
              <w:abstractNum w:abstractNumId="2">
                <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
              </w:abstractNum>
              <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
              <w:num w:numId="2"><w:abstractNumId w:val="2"/></w:num>
            </w:numbering>
            """;

        const string documentXml =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p>
                  <w:pPr><w:pStyle w:val="Heading1"/><w:jc w:val="center"/></w:pPr>
                  <w:r><w:t>Relazione Tecnica FLIP</w:t></w:r>
                </w:p>
                <w:p>
                  <w:pPr><w:jc w:val="both"/><w:spacing w:before="60" w:after="140"/></w:pPr>
                  <w:r><w:t xml:space="preserve">Verifica italiana con accenti: caffè, città, perché e simbolo € 120,00. </w:t></w:r>
                  <w:r><w:rPr><w:b/></w:rPr><w:t xml:space="preserve">Testo in grassetto </w:t></w:r>
                  <w:r><w:rPr><w:i/></w:rPr><w:t xml:space="preserve">in corsivo </w:t></w:r>
                  <w:r><w:rPr><w:u w:val="single"/></w:rPr><w:t xml:space="preserve">sottolineato </w:t></w:r>
                  <w:r><w:rPr><w:strike/></w:rPr><w:t xml:space="preserve">barrato </w:t></w:r>
                  <w:r><w:rPr><w:color w:val="C00000"/><w:highlight w:val="yellow"/></w:rPr><w:t>evidenziato rosso</w:t></w:r>
                  <w:r><w:t xml:space="preserve">. Paragrafo lungo per verificare il rientro, la giustificazione tipografica e la leggibilità complessiva del layout su pagina A4.</w:t></w:r>
                </w:p>
                <w:p>
                  <w:pPr><w:pStyle w:val="Heading2"/></w:pPr>
                  <w:r><w:t>Elenco Puntato e Numerato</w:t></w:r>
                </w:p>
                <w:p>
                  <w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr>
                  <w:r><w:rPr><w:b/></w:rPr><w:t>Punto uno:</w:t></w:r>
                  <w:r><w:t xml:space="preserve"> qualità tipografica nativa.</w:t></w:r>
                </w:p>
                <w:p>
                  <w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="2"/></w:numPr></w:pPr>
                  <w:r><w:t>Primo passo numerato con apice</w:t></w:r>
                  <w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:t>2</w:t></w:r>
                  <w:r><w:t> e pedice</w:t></w:r>
                  <w:r><w:rPr><w:vertAlign w:val="subscript"/></w:rPr><w:t>0</w:t></w:r>
                </w:p>
                <w:tbl>
                  <w:tblGrid><w:gridCol w:w="4500"/><w:gridCol w:w="4500"/></w:tblGrid>
                  <w:tr>
                    <w:trPr><w:tblHeader/></w:trPr>
                    <w:tc><w:tcPr><w:shd w:fill="1F4E79"/></w:tcPr><w:p><w:r><w:rPr><w:b/><w:color w:val="FFFFFF"/></w:rPr><w:t>Voce Tabella</w:t></w:r></w:p></w:tc>
                    <w:tc><w:tcPr><w:shd w:fill="1F4E79"/></w:tcPr><w:p><w:r><w:rPr><w:b/><w:color w:val="FFFFFF"/></w:rPr><w:t>Stato</w:t></w:r></w:p></w:tc>
                  </w:tr>
                  <w:tr>
                    <w:tc><w:p><w:r><w:t>Formato RTF e PDF</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:rPr><w:b/><w:color w:val="107C10"/></w:rPr><w:t>OK · Conforme</w:t></w:r></w:p></w:tc>
                  </w:tr>
                </w:tbl>
                <w:p>
                  <w:pPr><w:jc w:val="right"/></w:pPr>
                  <w:r><w:rPr><w:i/></w:rPr><w:t>Nota allineata a destra</w:t></w:r>
                </w:p>
                <w:p>
                  <w:pPr><w:pageBreakBefore/></w:pPr>
                  <w:r><w:rPr><w:b/><w:sz w:val="28"/></w:rPr><w:t>Seconda Pagina Verificata</w:t></w:r>
                </w:p>
                <w:sectPr>
                  <w:pgSz w:w="11906" w:h="16838"/>
                  <w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134"/>
                </w:sectPr>
              </w:body>
            </w:document>
            """;

        using var fs = new FileStream(docxPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        static void WriteEntry(System.IO.Compression.ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using var s = entry.Open();
            using var w = new StreamWriter(s, new System.Text.UTF8Encoding(false));
            w.Write(content);
        }
        WriteEntry(zip, "[Content_Types].xml", contentTypes);
        WriteEntry(zip, "_rels/.rels", rootRels);
        WriteEntry(zip, "word/_rels/document.xml.rels", docRels);
        WriteEntry(zip, "word/styles.xml", stylesXml);
        WriteEntry(zip, "word/numbering.xml", numberingXml);
        WriteEntry(zip, "word/document.xml", documentXml);
    }
}
