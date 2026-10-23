using System.IO;
using System.Text.Json;
namespace Flip;

internal static class InputTests
{
    internal static int Run()
    {
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
        File.WriteAllText(Path.Combine(Evidence.DirectoryPath, "input-tests.json"),
            JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, passed = checks.Count(c => c.Value), failed = checks.Count(c => !c.Value), checks }, new JsonSerializerOptions { WriteIndented = true }));
        return checks.All(c => c.Value) ? 0 : 1;
    }
}
