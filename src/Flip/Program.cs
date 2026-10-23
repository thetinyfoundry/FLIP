using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
namespace Flip;

internal static class Program
{
    internal const string PipeName = "Flip.Local." + nameof(Flip) + "." + "v2";
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            int evidence = Array.IndexOf(args, "--evidence"); if (evidence >= 0) Evidence.DirectoryPath = Path.GetFullPath(OptionValue(args, "--evidence"));
            if (evidence >= 0 || args.Contains("--preview-wheel") || args.Contains("--export-sound")) Directory.CreateDirectory(Evidence.DirectoryPath);
            if (args.Contains("--enable-startup") || args.Contains("--disable-startup")) { StartupIntegration.SetEnabled(args.Contains("--enable-startup")); return 0; }
            if (args.Contains("--install-shortcuts") || args.Contains("--remove-shortcuts")) { StartupIntegration.Shortcuts(args.Contains("--remove-shortcuts")); return 0; }
            if (args.Contains("--export-sound")) { File.WriteAllBytes(Path.Combine(Evidence.DirectoryPath, "flip-tick.wav"), WheelSound.CreateWave()); return 0; }
            if (args.Contains("--preview-wheel")) { var preview = new Wheel(_ => { }, (_, _) => { }); preview.SavePreview(Path.Combine(Evidence.DirectoryPath, "wheel-controls.png"), "documento.txt", false, args.Contains("--mixed-preview") ? ["immagine.png", "video.mp4"] : null); preview.Close(); return 0; }
            if (args.Contains("--test-input")) return InputTests.Run();
            if (args.Contains("--test-conversions")) return ConversionTests.Run().GetAwaiter().GetResult();
            if (args.Contains("--install-context-menu") || args.Contains("--remove-context-menu")) { ShellIntegration.Apply(args.Contains("--remove-context-menu")); return 0; }
            if (args.Contains("--convert"))
            {
                int f = Array.IndexOf(args, "--convert"), p = Array.IndexOf(args, "--files");
                if (p < 0 || f < 0 || f + 1 >= args.Length || p + 1 >= args.Length || args[f + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--convert PNG,PDF --files file1 file2");
                var results = ConversionEngine.Convert(FileArguments(args).Select(Path.GetFullPath).ToArray(), args[f + 1].Split(','), CancellationToken.None).GetAwaiter().GetResult();
                string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }); Console.WriteLine(json);
                int report = Array.IndexOf(args, "--report"); if (report >= 0) File.WriteAllText(OptionValue(args, "--report"), json);
                return results.Any(r => r.Error != null) ? 1 : 0;
            }
            if (args.Contains("--stop")) { Send("exit"); return 0; }
            if (args.Contains("--preview-sound")) { Send("sound-test"); return 0; }
            if (args.Contains("--set-trigger"))
            {
                var choice = WheelShortcut.Parse(OptionValue(args, "--set-trigger"));
                using var check = new Mutex(false, "Local\\Flip.Desktop.v2", out bool absent);
                if (!absent) { Send("trigger:" + choice.Id); return 0; }
                var settings = AppPreferences.Load(); settings.Trigger = choice.Id; settings.Save(); return 0;
            }
            using var mutex = new Mutex(true, "Local\\Flip.Desktop.v2", out bool fresh);
            if (!fresh) { if (args.Contains("--background")) return 0; int p = Array.IndexOf(args, "--files"); Send(args.Contains("--settings") ? "settings" : p >= 0 ? (args.Contains("--multi") ? "multi:" : "") + JsonSerializer.Serialize(FileArguments(args)) : "select"); return 0; }
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using var host = new Host(app, args); app.MainWindow = host; new WindowInteropHelper(host).EnsureHandle();
                app.Run(); return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex) { Evidence.Log("fatal", ex.ToString()); if (args.Contains("--convert") || args.Contains("--test-input") || args.Contains("--test-conversions")) { Console.Error.WriteLine(ex.Message); return 1; } MessageBox.Show(ex.Message, "Flip", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
    }
    internal static string[] FileArguments(string[] args)
    {
        int index = Array.IndexOf(args, "--files");
        string[] files = index < 0 ? [] : args.Skip(index + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (files.Length == 0) throw new ArgumentException("Manca un file dopo --files.");
        return files;
    }
    internal static string OptionValue(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Manca il valore per " + option + ".");
        return args[index + 1];
    }
    static void Send(string message)
    {
        try { using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); pipe.Connect(1500); using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true }; writer.WriteLine(message); }
        catch (TimeoutException) { if (message != "exit") MessageBox.Show("Flip è già in avvio. Attendi un istante e usa l'icona nell'area di notifica.", "Flip"); }
    }
}
internal sealed class Host : Window, IDisposable
{
    readonly Application app;
    readonly string[] args;
    readonly Forms.NotifyIcon tray;
    readonly Forms.ToolStripMenuItem gestureItem;
    readonly CancellationTokenSource lifetime = new();
    readonly AppPreferences preferences = AppPreferences.Load();
    readonly WheelSound sound;
    readonly DispatcherTimer release = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromSeconds(20) };
    ShortcutHook? shortcutHook;
    readonly ShortcutOrderGate shortcutOrder = new();
    ShortcutSettingsWindow? settingsWindow;
    bool settingsOpen;
    HwndSource? source; nint hwnd; Wheel? wheel; SelectionWindow? panel;
    bool raw, mouseListening, disposed, busy, exiting;
    WheelShortcut wheelShortcut = WheelShortcut.Parse(WheelShortcut.Default);
    string triggerName => wheelShortcut.Name;
    Task? work; CancellationTokenSource? job;
    string? lastFolder;
    internal Host(Application app, string[] args)
    {
        this.app = app; this.args = args; Title = "Flip";
        Width = 1; Height = 1; Left = -12000; Top = -12000; ShowInTaskbar = false; ShowActivated = false; WindowStyle = WindowStyle.None;
        bool migratedTrigger = false;
        try { wheelShortcut = WheelShortcut.Parse(preferences.Trigger); }
        catch (ArgumentException) { wheelShortcut = WheelShortcut.Parse(WheelShortcut.Default); preferences.Trigger = WheelShortcut.Default; migratedTrigger = true; }
        sound = new WheelSound(preferences.SoundEnabled);
        int key = Array.IndexOf(args, "--trigger"); if (key >= 0) SetTrigger(Program.OptionValue(args, "--trigger"));
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Flip.ico")), Text = "Flip — convertitore locale", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Conversione rapida…", null, (_, _) => Dispatcher.BeginInvoke(QuickSelect));
        menu.Items.Add("Più formati / più file…", null, (_, _) => Dispatcher.BeginInvoke(() => ShowPanel([])));
        menu.Items.Add("Apri ultima cartella", null, (_, _) => { if (lastFolder != null) OpenFolder(lastFolder); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        gestureItem = new Forms.ToolStripMenuItem("Gesto " + triggerName + " attivo") { Checked = true, CheckOnClick = true };
        gestureItem.CheckedChanged += (_, _) => { if (gestureItem.Checked) { RegisterRaw(); } else { RemoveRaw(); wheel?.Dismiss("gesture-disabled"); } };
        menu.Items.Add(gestureItem);
        menu.Items.Add("Impostazioni…", null, (_, _) => Dispatcher.BeginInvoke(ShowSettings));
        var soundItem = new Forms.ToolStripMenuItem("Suono al cambio di settore") { Checked = preferences.SoundEnabled, CheckOnClick = true };
        soundItem.CheckedChanged += (_, _) => { sound.Enabled = preferences.SoundEnabled = soundItem.Checked; SavePreferences(); };
        menu.Items.Add(soundItem);
        menu.Items.Add("Prova il suono", null, (_, _) => Dispatcher.BeginInvoke(sound.Play));
        var startupItem = new Forms.ToolStripMenuItem("Avvia con Windows") { Checked = StartupIntegration.Enabled, CheckOnClick = true };
        bool changingStartup = false;
        startupItem.CheckedChanged += (_, _) => { if (changingStartup) return; try { StartupIntegration.SetEnabled(startupItem.Checked); } catch (Exception ex) { changingStartup = true; startupItem.Checked = StartupIntegration.Enabled; changingStartup = false; Toast(ex.Message); } };
        menu.Opening += (_, _) => { changingStartup = true; startupItem.Checked = StartupIntegration.Enabled; changingStartup = false; };
        menu.Items.Add(startupItem);
        menu.Items.Add("Annulla conversione", null, (_, _) => job?.Cancel());
        menu.Items.Add("Esci da Flip", null, (_, _) => Dispatcher.BeginInvoke(Exit));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => ShowPanel([]));
        tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() => { if (lastFolder != null) OpenFolder(lastFolder); });
        if (migratedTrigger) SavePreferences();
        SourceInitialized += (_, _) => Initialize();
        release.Tick += (_, _) => { release.Stop(); wheel?.Dismiss("primary-button-up"); };
        watchdog.Tick += (_, _) => { watchdog.Stop(); wheel?.Dismiss("watchdog"); };
    }
    void SetTrigger(string value)
    {
        wheelShortcut = WheelShortcut.Parse(value);
        shortcutHook?.Reset();
        shortcutOrder.Reset();
        if (wheel == null) SetMouseListening(false);
    }
    void ApplyShortcut(string value)
    {
        var next = WheelShortcut.Parse(value);
        string previous = preferences.Trigger;
        preferences.Trigger = next.Id;
        try { preferences.Save(); }
        catch { preferences.Trigger = previous; throw; }
        SetTrigger(next.Id);
        gestureItem.Text = "Gesto " + triggerName + " attivo";
        Evidence.Log("shortcut.changed", new { next.Id, next.Name });
    }
    void ShowSettings()
    {
        if (settingsOpen) { settingsWindow?.Activate(); return; }
        settingsOpen = true; shortcutOrder.Reset(); wheel?.Dismiss("settings");
        try
        {
            var dialog = new ShortcutSettingsWindow(wheelShortcut, preferences);
            settingsWindow = dialog;
            dialog.Closed += (_, _) =>
            {
                settingsOpen = false; settingsWindow = null;
                if (dialog.Accepted && !exiting)
                    try { preferences.Language = dialog.SelectedLanguage; ApplyShortcut(dialog.SelectedShortcut); Toast(UiText.Get(preferences, "Scorciatoia della ruota: ", "Wheel shortcut: ") + triggerName); }
                    catch (Exception ex) { Toast("Impossibile cambiare la scorciatoia: " + ex.Message); }
            };
            dialog.Show(); dialog.Activate();
        }
        catch (Exception ex) { settingsOpen = false; settingsWindow = null; Toast("Impossibile aprire le impostazioni: " + ex.Message); }
    }
    void Initialize()
    {
        hwnd = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(hwnd); source.AddHook(Messages);
        RegisterRaw();
        Evidence.Log("host.start", new { pid = Environment.ProcessId, buttonsSwapped = Native.ButtonsSwapped, wheelShortcut.Name, DocumentTools.Pandoc, DocumentTools.Office });
        _ = Listen();
        if (args.Contains("--measure-idle")) MeasureIdle();
        Dispatcher.BeginInvoke(() =>
        {
            int p = Array.IndexOf(args, "--files");
            if (p >= 0) { if (args.Contains("--multi")) ShowPanel(Program.FileArguments(args)); else Open(Program.FileArguments(args), false); }
            else if (args.Contains("--settings")) ShowSettings();
            else if (args.Contains("--ui")) ShowPanel([]);
            else if (!args.Contains("--background")) Toast("Flip è pronto. Trascina un file e premi " + triggerName + ". Per più uscite usa il menu dell'icona.");
        });
    }
    void MeasureIdle()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; var process = Process.GetCurrentProcess(); TimeSpan cpu = default; long tick = 0; int sample = 0;
        timer.Tick += (_, _) => { if (sample++ == 0) { cpu = process.TotalProcessorTime; tick = Stopwatch.GetTimestamp(); timer.Interval = TimeSpan.FromSeconds(10); return; } timer.Stop(); process.Refresh(); Evidence.Log("idle.measurement", new { seconds = Stopwatch.GetElapsedTime(tick).TotalSeconds, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, threads = process.Threads.Count, handles = process.HandleCount, mouseListening, busy, panelOpen = panel != null, wheelOpen = wheel != null }); Exit(); }; timer.Start();
    }
    async Task Listen()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8); string? message = await reader.ReadLineAsync(lifetime.Token);
                await Dispatcher.InvokeAsync(() => { if (message == "exit") Exit(); else if (message == "sound-test") sound.Play(); else if (message == "settings") ShowSettings(); else if (message?.StartsWith("trigger:") == true) ApplyShortcut(message[8..]); else if (message == "select") ShowPanel([]); else if (message?.StartsWith("multi:") == true) ShowPanel(JsonSerializer.Deserialize<string[]>(message[6..]) ?? []); else if (message != null) Open(JsonSerializer.Deserialize<string[]>(message) ?? [], false); });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Evidence.Log("ipc.error", ex.Message); if (!lifetime.IsCancellationRequested) await Task.Delay(300, lifetime.Token); }
        }
    }
    internal void CancelJob() => job?.Cancel();
    void SavePreferences() { try { preferences.Save(); } catch (Exception ex) { Toast("Impossibile salvare le preferenze: " + ex.Message); } }
    void QuickSelect()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Flip — scegli file da convertire", Multiselect = true, Filter = "Tutti i file|*.*" };
        if (dialog.ShowDialog() == true) Open(dialog.FileNames, false);
    }
    internal void ShowPanel(string[] files)
    {
        wheel?.Dismiss("multi-format");
        if (panel == null) { panel = new SelectionWindow(this); panel.Closed += (_, _) => panel = null; }
        if (files.Length > 0) panel.SetFiles(files); panel.Show(); panel.Activate();
    }
    void Open(string[] files, bool dragging)
    {
        if (exiting || busy) { if (busy) Toast("Conversione in corso. Attendi il completamento."); return; }
        if (wheel?.IsVisible == true) return;
        if (!dragging)
        {
            files = Policy.ReadFiles(new DataObject(DataFormats.FileDrop, files));
            if (files.Length == 0) { Toast("Seleziona file locali disponibili."); return; }
            if (Policy.Outputs(files).Length == 0) { Toast(Policy.AvailabilityMessage(files)); return; }
        }
        wheel = new Wheel(Toast, (paths, format) => Start(paths, [format]), sound.Play);
        wheel.Closed += (_, _) => { wheel = null; release.Stop(); watchdog.Stop(); shortcutOrder.Reset(); SetMouseListening(false); };
        SetMouseListening(true); wheel.Open(files, dragging); watchdog.Start();
    }
    internal void Start(string[] files, string[] formats)
    {
        if (busy) { Toast("Una conversione è già in corso."); return; }
        if (files.Length == 0 || formats.Length == 0) { Toast("Seleziona almeno un file e un formato."); return; }
        busy = true; job = new CancellationTokenSource();
        panel?.SetBusy(true); work = RunJob(files, formats, job.Token);
    }
    async Task RunJob(string[] files, string[] formats, CancellationToken token)
    {
        try
        {
            var results = await Task.Run(() => ConversionEngine.Convert(files, formats, token, message => Dispatcher.BeginInvoke(() => panel?.SetProgress(message))), token);
            var outputs = results.SelectMany(r => r.Outputs).ToArray(); var errors = results.Where(r => r.Error != null).ToArray();
            if (outputs.Length > 0) lastFolder = Path.GetDirectoryName(outputs[0]);
            string summary = $"{outputs.Length} file creati" + (errors.Length > 0 ? $", {errors.Length} conversioni non riuscite." : ".");
            panel?.SetResults(results); var folders = outputs.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); Toast(summary + (folders.Length == 1 ? " Destinazione: " + folders[0] : folders.Length > 1 ? " Ogni copia è nella cartella del rispettivo originale." : ""));
            if (errors.Length > 0 && panel == null) { ShowPanel(files); panel?.SetResults(results); }
        }
        catch (OperationCanceledException) { Toast("Conversione annullata. Gli originali sono intatti."); panel?.SetProgress("Annullata. Le conversioni già completate restano salvate."); }
        catch (Exception ex) { Toast(ex.Message); Evidence.Log("job.error", ex.ToString()); }
        finally { busy = false; panel?.SetBusy(false); job?.Dispose(); job = null; }
    }
    internal static void OpenFolder(string folder) { Evidence.Log("folder.open.request", folder); var p = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; p.ArgumentList.Add(folder); Process.Start(p); }
    internal void Toast(string text) { if (disposed) return; tray.BalloonTipTitle = "Flip"; tray.BalloonTipText = text.Length > 250 ? text[..250] : text; tray.ShowBalloonTip(5000); Evidence.Log("notice", text); }
    async void Exit()
    {
        if (exiting) return; exiting = true; job?.Cancel(); wheel?.Dismiss("exit"); RemoveRaw();
        if (work != null) try { await work; } catch { }
        Dispose(); app.Shutdown();
    }
    void RegisterRaw()
    {
        if (hwnd == 0) return;
        var devices = new[] { new Native.Device { Page = 1, Usage = 6, Flags = 0x100, Target = hwnd } };
        raw = Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Native.Device>()); Evidence.Log("raw.register", new { raw, triggerName });
        if (raw && shortcutHook == null)
        {
            try
            {
                shortcutHook = new ShortcutHook(() => wheelShortcut,
                    () => raw && !busy && !exiting && !settingsOpen && Native.GetAsyncKeyState(Native.PrimaryVirtualKey(Native.ButtonsSwapped)) < 0
                        && !ShortcutHook.ModifiersHeld() && (wheel?.IsDragMode == true || ShortcutHook.IsExplorerForeground()),
                    down => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ShortcutKeyChanged(down)));
                Evidence.Log("shortcut.hook.registered", new { triggerName, scope = "Explorer/Desktop drag only" });
            }
            catch (Exception ex) { RemoveRaw(); gestureItem.Checked = false; Toast(ex.Message); return; }
        }
        if (!raw) Toast("Gesto non disponibile: usa il menu dell'icona.");
    }
    void ActivateWheelShortcut()
    {

        bool swapped = Native.ButtonsSwapped, held = Native.GetAsyncKeyState(Native.PrimaryVirtualKey(swapped)) < 0;
        if (!held || settingsOpen || !raw || exiting) return;
        SetMouseListening(true);
        Evidence.Log("wheel.shortcut", new { wheelShortcut.Name, held, swapped });
        shortcutHook?.ReserveRepeatsForGesture();
        Open([], true);
    }
    void ShortcutKeyChanged(bool down)
    {
        if (!down)
        {
            shortcutOrder.KeyUp();
            if (wheel == null) SetMouseListening(false);
            return;
        }
        if (!raw || busy || exiting || settingsOpen || ShortcutHook.ModifiersHeld()
            || !(wheel?.IsDragMode == true || ShortcutHook.IsExplorerForeground())) return;
        bool primaryHeld = Native.GetAsyncKeyState(Native.PrimaryVirtualKey(Native.ButtonsSwapped)) < 0;
        SetMouseListening(true);
        if (shortcutOrder.KeyDown(primaryHeld)) ActivateWheelShortcut();
    }
    void SetMouseListening(bool value)
    {
        if (mouseListening == value) return;
        var devices = new[] { new Native.Device { Page = 1, Usage = 2, Flags = value ? 0x100u : 1u, Target = value ? hwnd : 0 } };
        if (Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<Native.Device>())) mouseListening = value;
    }
    void RemoveRaw()
    {
        shortcutHook?.Dispose(); shortcutHook = null;
        shortcutOrder.Reset();
        if (!raw) return; var devices = new[] { new Native.Device { Page = 1, Usage = 6, Flags = 1 }, new Native.Device { Page = 1, Usage = 2, Flags = 1 } };
        Native.RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<Native.Device>()); raw = false; mouseListening = false;
    }
    nint Messages(nint h, int message, nint wp, nint lp, ref bool handled)
    {
        if (message != 0xFF || !raw) return 0;
        uint size = 0, hs = (uint)Marshal.SizeOf<Native.Header>();
        if (Native.GetRawInputData(lp, 0x10000003, 0, ref size, hs) == uint.MaxValue || size < hs || size > 4096) return 0;
        nint memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(lp, 0x10000003, memory, ref size, hs) == uint.MaxValue) return 0;
            var header = Marshal.PtrToStructure<Native.Header>(memory); nint data = memory + (int)hs;
            if (header.Type == 1)
            {
                if (size < hs + Marshal.SizeOf<Native.Keyboard>()) return 0;
                var key = Marshal.PtrToStructure<Native.Keyboard>(data);
                if ((key.Flags & 1) == 0 && key.VKey == 0x1B) { shortcutOrder.Reset(); if (wheel == null) SetMouseListening(false); Dispatcher.BeginInvoke(() => wheel?.Dismiss("escape")); }
                // Trigger keys are handled once by ShortcutHook and consumed only
                // for Explorer/Desktop drags. Raw keyboard input still observes Escape.
            }
            else if (header.Type == 0)
            {
                if (size < hs + 8) return 0;
                ushort flags = unchecked((ushort)Marshal.ReadInt16(data, 4));
                bool swapped = Native.ButtonsSwapped; Native.GetCursorPos(out var cursor);
                if (Native.PrimaryDown(flags, swapped)) shortcutOrder.MouseDown(cursor.X, cursor.Y);
                if (Native.PrimaryUp(flags, swapped)) { shortcutOrder.MouseUp(); if (wheel != null) { release.Stop(); release.Start(); } else if (!shortcutOrder.Held) SetMouseListening(false); }
                else if (shortcutOrder.Move(cursor.X, cursor.Y)) ActivateWheelShortcut();
            }
        }
        catch (Exception ex) { Evidence.Log("input.error", ex.Message); }
        finally { Marshal.FreeHGlobal(memory); }
        return 0;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; lifetime.Cancel(); job?.Cancel(); release.Stop(); watchdog.Stop(); RemoveRaw();
        source?.RemoveHook(Messages);
        settingsWindow?.Close(); wheel?.Close(); panel?.Close(); sound.Dispose(); tray.Visible = false; tray.Dispose(); lifetime.Dispose(); Evidence.Log("host.stop");
    }
}
