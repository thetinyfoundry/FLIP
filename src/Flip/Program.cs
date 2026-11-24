using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Flip.Core;
using Flip.Engines;
using Flip.Routing;
using Flip.SettingsHub;
using Forms = System.Windows.Forms;
namespace Flip;

internal static class Program
{
    internal const string PipeName = "Flip.Local." + nameof(Flip) + "." + "v2";
    internal static string ReleaseTitle { get; } =
        $"{typeof(Program).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "FLIP"} v{UpdateChecker.CurrentVersion}";
    private static readonly Lock MouseSettingsLock = new();

    internal static void InitializeConversionServices(SettingsStore store)
    {
        EngineBootstrap.EnsureInitialized();
        store.SettingsChanged += SyncDragButtonPreference;
        SyncDragButtonPreference(store, EventArgs.Empty);
        _ = Task.Run(() => CliResolver.AutoDetectAndPersistEngines(store));
    }

    internal static void SyncDragButtonPreference(object? sender, EventArgs _)
    {
        if (sender is not SettingsStore store) return;
        lock (MouseSettingsLock)
        {
            Native.AcceptSecondaryDragButton = store.Settings.AcceptSecondaryDragButton;
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            int evidence = Array.IndexOf(args, "--evidence"); if (evidence >= 0) Evidence.DirectoryPath = Path.GetFullPath(OptionValue(args, "--evidence"));
            Evidence.Enabled = evidence >= 0 || args.Contains("--test-input") || args.Contains("--test-conversions");
            if (evidence >= 0 || args.Contains("--preview-wheel") || args.Contains("--preview-hub") || args.Contains("--export-sound")) Directory.CreateDirectory(Evidence.DirectoryPath);
            if (args.Contains("--enable-startup") || args.Contains("--disable-startup")) { StartupIntegration.SetEnabled(args.Contains("--enable-startup")); return 0; }
            if (args.Contains("--install-shortcuts") || args.Contains("--remove-shortcuts")) { StartupIntegration.Shortcuts(args.Contains("--remove-shortcuts")); return 0; }
            if (args.Contains("--export-sound")) { File.WriteAllBytes(Path.Combine(Evidence.DirectoryPath, "flip-tick.wav"), WheelSound.CreateWave()); return 0; }
            ScreenEdgeAuraWindow.Suppressed = args.Contains("--test-input") || args.Contains("--test-conversions") ||
                args.Contains("--preview-wheel") || args.Contains("--preview-hub") || args.Contains("--convert");
            if (!args.Contains("--test-input") && !args.Contains("--test-conversions"))
            {
                try { Loc.SetPreference(SettingsStore.Current.CreateSnapshot().Language); } catch { }
            }
            if (args.Contains("--preview-wheel")) { var preview = new Wheel(_ => { }, (_, _) => { }); preview.SavePreview(Path.Combine(Evidence.DirectoryPath, "wheel-controls.png"), "documento.txt", false, args.Contains("--mixed-preview") ? ["immagine.png", "video.mp4"] : null); preview.Close(); return 0; }
            if (args.Contains("--preview-hub"))
            {
                var app = new Application();
                int tab = 0;
                int tabIdx = Array.IndexOf(args, "--tab");
                if (tabIdx >= 0 && tabIdx + 1 < args.Length && int.TryParse(args[tabIdx + 1], out int t)) tab = t;
                int langIdx = Array.IndexOf(args, "--lang");
                if (langIdx >= 0 && langIdx + 1 < args.Length) Loc.SetPreference(args[langIdx + 1]);
                var hub = new SettingsHubWindow();
                hub.NavigateTo(Math.Clamp(tab, 0, SettingsHubWindow.GuideSectionIndex));
                var content = (UIElement)hub.Content;
                content.Measure(new Size(840, 620));
                content.Arrange(new Rect(0, 0, 840, 620));
                content.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(840, 620, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(content);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                string outPath = Path.Combine(Evidence.DirectoryPath, $"hub-preview-tab{tab}.png");
                using (var fs = File.Create(outPath)) encoder.Save(fs);
                hub.Close();
                return 0;
            }
            if (args.Contains("--test-input") || args.Contains("--test-conversions"))
            {
                // La cronologia generata dalle suite appartiene ai dati di prova.
                string stateDir = Path.Combine(Evidence.DirectoryPath, "test-state", Guid.NewGuid().ToString("N"));
                SettingsStore.UseTestDirectory(stateDir);
                Environment.SetEnvironmentVariable("CALIBRE_CONFIG_DIRECTORY", Path.Combine(stateDir, "calibre-config"));
                Environment.SetEnvironmentVariable("CALIBRE_CACHE_DIRECTORY", Path.Combine(stateDir, "calibre-cache"));
                return args.Contains("--test-input") ? InputTests.Run() : ConversionTests.Run().GetAwaiter().GetResult();
            }
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
            if (args.Contains("--stop")) return Send("exit");
            if (args.Contains("--preview-sound")) return Send("sound-test");
            if (args.Contains("--set-trigger"))
            {
                var choice = WheelShortcut.Parse(OptionValue(args, "--set-trigger"));
                using var check = OpenInstanceMutex(false, out bool absent);
                if (check is null) return 1;
                if (!absent) return Send("trigger:" + choice.Id);
                var settings = AppPreferences.Load(); settings.Trigger = choice.Id; settings.Save(); return 0;
            }
            using var mutex = OpenInstanceMutex(true, out bool fresh, quiet: args.Contains("--background"));
            if (mutex is null) return 1;
            if (!fresh)
            {
                if (args.Contains("--background")) return 0;
                int p = Array.IndexOf(args, "--files");
                return Send(args.Contains("--settings") ? "settings" : p >= 0 ? (args.Contains("--multi") ? "multi:" : "") + JsonSerializer.Serialize(FileArguments(args)) : "select");
            }
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
        if (files.Length == 0) throw new ArgumentException(Loc.T("Manca un file dopo --files.", "Missing file path after --files."));
        return files;
    }
    internal static string OptionValue(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(Loc.F("Manca il valore per {0}.", "Missing value for {0}.", option));
        return args[index + 1];
    }
    internal static string PackOutputsIntoZipArchive(IReadOnlyList<string> outputs, string? preferredStem = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        string[] valid = outputs.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (valid.Length == 0)
            throw new IOException(Loc.T("Nessun file convertito disponibile per l'impacchettamento ZIP.", "No converted files available to pack into ZIP."));

        string destDir = Path.GetDirectoryName(valid[0])!;
        string stem = string.IsNullOrWhiteSpace(preferredStem)
            ? (valid.Length == 1 ? Path.GetFileNameWithoutExtension(valid[0]) : $"{Path.GetFileNameWithoutExtension(valid[0])}_batch")
            : preferredStem;

        string zipPath = Path.Combine(destDir, $"{stem}_converted.zip");
        for (int i = 1; File.Exists(zipPath) || Directory.Exists(zipPath); i++)
        {
            zipPath = Path.Combine(destDir, $"{stem}_converted_{i}.zip");
        }

        string tempZip = Path.Combine(destDir, $".flip-zip-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tempZip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in valid)
                {
                    string entryName = Path.GetFileName(file);
                    string eStem = Path.GetFileNameWithoutExtension(entryName);
                    string eExt = Path.GetExtension(entryName);
                    for (int suffix = 1; !usedNames.Add(entryName); suffix++)
                        entryName = $"{eStem}_{suffix}{eExt}";

                    zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }

            File.Move(tempZip, zipPath, overwrite: false);
            return zipPath;
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    static Mutex? OpenInstanceMutex(bool initiallyOwned, out bool createdNew, bool quiet = false)
    {
        try { return new Mutex(initiallyOwned, "Local\\Flip.Desktop.v2", out createdNew); }
        catch (UnauthorizedAccessException ex)
        {
            createdNew = false;
            ReportInstanceAccessDenied(ex, quiet);
            return null;
        }
    }

    static void ReportInstanceAccessDenied(UnauthorizedAccessException exception, bool quiet)
    {
        Evidence.Log("instance.access-denied", exception.ToString());
        if (quiet) return;

        MessageBox.Show(Loc.T(
            "Impossibile comunicare con l’istanza di FLIP già aperta. Potrebbe essere in esecuzione come amministratore. Chiudi FLIP dall’area di notifica vicino all’orologio e riaprilo normalmente.",
            "Could not communicate with the running FLIP instance. It may be running as administrator. Exit FLIP from the notification area next to the clock and open it normally."),
            "FLIP", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static int Send(string message)
    {
        try { using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); pipe.Connect(1500); using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true }; writer.WriteLine(message); }
        catch (TimeoutException) { if (message != "exit") MessageBox.Show(Loc.T("Flip è già in avvio. Attendi un istante e usa l'icona nell'area di notifica.", "FLIP is already starting. Wait a moment and use the system tray icon."), "Flip"); }
        catch (UnauthorizedAccessException ex)
        {
            ReportInstanceAccessDenied(ex, quiet: message == "exit");
            return 1;
        }
        return 0;
    }
}
internal sealed class Host : Window, IDisposable
{
    readonly Application app;
    readonly string[] args;
    readonly Forms.NotifyIcon tray;
    readonly Forms.ToolStripItem openFlipItem;
    readonly Forms.ToolStripItem quickConvertItem;
    readonly Forms.ToolStripItem multiFileItem;
    readonly Forms.ToolStripItem clipboardFlipItem;
    readonly Forms.ToolStripItem historyItem;
    readonly Forms.ToolStripItem latestFolderItem;
    readonly Forms.ToolStripItem undoLastItem;
    readonly Forms.ToolStripMenuItem gestureItem;
    readonly Forms.ToolStripMenuItem soundItem;
    readonly Forms.ToolStripItem testSoundItem;
    readonly Forms.ToolStripMenuItem startupItem;
    readonly Forms.ToolStripItem welcomeTourItem;
    readonly Forms.ToolStripItem checkUpdatesItem;
    readonly Forms.ToolStripItem cancelItem;
    readonly Forms.ToolStripItem exitItem;
    readonly CancellationTokenSource lifetime = new();
    readonly SettingsStore settingsStore = SettingsStore.Current;
    readonly AppPreferences preferences = AppPreferences.Load();
    readonly WheelSound sound;
    readonly DispatcherTimer release = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromMilliseconds(400) };
    long dragOpenedTick;
    long dragReleasedTick;
    ShortcutHook? shortcutHook;
    readonly ShortcutOrderGate shortcutOrder = new();
    bool syncingTray;
    bool settingsOpen => SettingsHubWindow.IsSingletonOpen;
    HwndSource? source; nint hwnd; Wheel? wheel;
    bool raw, mouseListening, disposed, busy, exiting;
    WheelShortcut wheelShortcut = WheelShortcut.Parse(WheelShortcut.Default);
    string triggerName => wheelShortcut.DisplayName;
    Task? work; CancellationTokenSource? job;
    WatchFolderService? watchFolderService;
    string? lastFolder;
    string[] lastGeneratedOutputs = [];
    FlipPreviewWindow? activePreview;
    GazeOrbWindow? gazeOrb;
    internal Host(Application app, string[] args)
    {
        this.app = app; this.args = args; Title = "Flip";
        Loc.SetPreference(preferences.Language);
        Width = 1; Height = 1; Left = -12000; Top = -12000; ShowInTaskbar = false; ShowActivated = false; WindowStyle = WindowStyle.None;
        bool migratedTrigger = false;
        try { wheelShortcut = WheelShortcut.Parse(preferences.Trigger); }
        catch (ArgumentException) { wheelShortcut = WheelShortcut.Parse(WheelShortcut.Default); preferences.Trigger = WheelShortcut.Default; migratedTrigger = true; }
        sound = new WheelSound(preferences.SoundEnabled);
        int key = Array.IndexOf(args, "--trigger"); if (key >= 0) SetTrigger(Program.OptionValue(args, "--trigger"));
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Flip.ico")), Text = Program.ReleaseTitle, Visible = true };
        var menu = new Forms.ContextMenuStrip
        {
            BackColor = System.Drawing.Color.FromArgb(26, 28, 35),
            ForeColor = System.Drawing.Color.FromArgb(243, 244, 246),
            Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors()) { RoundedEdges = false },
            ShowImageMargin = false
        };
        openFlipItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(ShowSettings));
        quickConvertItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(QuickSelect));
        multiFileItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(() => ShowPanel([])));
        clipboardFlipItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(TriggerClipboardFlip));
        historyItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(ShowHistory));
        latestFolderItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(OpenLatestFolder));
        undoLastItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(UndoLastConversion));
        menu.Items.Add(new Forms.ToolStripSeparator());
        gestureItem = new Forms.ToolStripMenuItem("") { Checked = preferences.GestureEnabled, CheckOnClick = true };
        gestureItem.CheckedChanged += (_, _) =>
        {
            if (!syncingTray) UpdateTrayPreference(s => s.GestureEnabled = gestureItem.Checked);
        };
        menu.Items.Add(gestureItem);
        soundItem = new Forms.ToolStripMenuItem("") { Checked = preferences.SoundEnabled, CheckOnClick = true };
        soundItem.CheckedChanged += (_, _) => { if (!syncingTray) UpdateTrayPreference(s => s.SoundEnabled = soundItem.Checked); };
        menu.Items.Add(soundItem);
        testSoundItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(sound.Play));
        startupItem = new Forms.ToolStripMenuItem("") { CheckOnClick = true };
        bool changingStartup = false;
        startupItem.CheckedChanged += (_, _) =>
        {
            if (changingStartup) return;
            try
            {
                StartupIntegration.SetEnabled(startupItem.Checked);
                UpdateTrayPreference(s => s.StartWithWindows = startupItem.Checked);
            }
            catch (Exception ex)
            {
                changingStartup = true;
                startupItem.Checked = !startupItem.Checked;
                changingStartup = false;
                Toast(ex.Message);
            }
        };
        menu.Opening += (_, _) =>
        {
            ApplyTrayLocalization();
            changingStartup = true;
            try { startupItem.Checked = StartupIntegration.Enabled; startupItem.Enabled = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                startupItem.Enabled = false;
                Evidence.Log("startup.read.error", ex.Message);
            }
            finally { changingStartup = false; }
            latestFolderItem.Enabled = (lastFolder != null && Directory.Exists(lastFolder)) ||
                settingsStore.CreateSnapshot().RecentConversions.Any(item => item.Success);
            undoLastItem.Enabled = !busy && lastGeneratedOutputs.Any(File.Exists);
        };
        menu.Items.Add(startupItem);
        welcomeTourItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(FirstRunWelcomeWindow.ShowSingleton));
        checkUpdatesItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(() => _ = CheckUpdatesAsync()));
        cancelItem = menu.Items.Add("", null, (_, _) => job?.Cancel());
        exitItem = menu.Items.Add("", null, (_, _) => Dispatcher.BeginInvoke(Exit));
        ApplyTrayLocalization();
        foreach (Forms.ToolStripItem item in menu.Items) item.ForeColor = menu.ForeColor;
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowSettings);
        tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(OpenLatestFolder);
        if (migratedTrigger) SavePreferences();
        SourceInitialized += (_, _) => Initialize();
        release.Tick += (_, _) =>
        {
            release.Stop();
            if (wheel is { IsDragMode: true } currentWheel && !currentWheel.IsConverting && !Native.IsPrimaryMousePressed())
                DismissWheel(false, resetInputState: true);
        };
        watchdog.Tick += (_, _) => OnWatchdogTick();
    }

    void ApplyTrayLocalization()
    {
        string triggerLabel = KeyChoice.Describe(wheelShortcut.Id);
        openFlipItem.Text = Loc.T("Apri FLIP", "Open FLIP");
        quickConvertItem.Text = Loc.T("Conversione rapida…", "Quick convert…");
        multiFileItem.Text = Loc.T("Più formati / più file…", "Batch convert…");
        clipboardFlipItem.Text = Loc.T("Converti dagli Appunti (Clipboard Flip)", "Convert from Clipboard (Clipboard Flip)");
        historyItem.Text = Loc.T("Cronologia conversioni", "Conversion history");
        latestFolderItem.Text = Loc.T("Apri ultima cartella", "Open last folder");
        undoLastItem.Text = Loc.T("Annulla ultima conversione", "Undo last conversion");
        gestureItem.Text = Loc.F("Gesto {0} attivo", "{0} trigger enabled", triggerLabel);
        soundItem.Text = Loc.T("Suono al cambio di settore", "Wheel tick sound");
        testSoundItem.Text = Loc.T("Prova il suono", "Test sound");
        startupItem.Text = Loc.T("Avvia con Windows", "Launch at startup");
        welcomeTourItem.Text = Loc.T("Guida Rapida di Benvenuto", "Welcome Tour");
        checkUpdatesItem.Text = StoreEdition.IsPackaged
            ? Loc.T("Aggiornamenti in Microsoft Store…", "Updates in Microsoft Store…")
            : Loc.T("Verifica aggiornamenti su GitHub…", "Check for updates on GitHub…");
        cancelItem.Text = Loc.T("Annulla conversione", "Cancel conversion");
        exitItem.Text = Loc.T("Esci da Flip", "Quit FLIP");
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
        settingsStore.Update(s => s.TriggerKey = next.Id);
        Evidence.Log("shortcut.changed", new { next.Id, next.Name });
    }

    void UpdateTrayPreference(Action<FlipSettings> update)
    {
        try { settingsStore.Update(update); }
        catch (Exception ex)
        {
            Toast(Loc.F("Impossibile salvare le preferenze: {0}", "Could not save preferences: {0}", ex.Message));
            SyncHostPreferences();
        }
    }

    void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(SyncHostPreferences);

    void SyncHostPreferences()
    {
        if (disposed || exiting) return;
        var current = settingsStore.ReadPreferences();
        Loc.SetPreference(current.Language);
        syncingTray = true;
        try
        {
            try
            {
                var trigger = WheelShortcut.Parse(current.TriggerKey);
                if (trigger != wheelShortcut) SetTrigger(trigger.Id);
            }
            catch (ArgumentException ex) { Evidence.Log("shortcut.invalid-setting", ex.Message); }
            ApplyTrayLocalization();
            gestureItem.Checked = current.GestureEnabled;
            soundItem.Checked = sound.Enabled = current.SoundEnabled;
            if (current.GestureEnabled && !raw) RegisterRaw();
            else if (!current.GestureEnabled && raw)
            {
                RemoveRaw();
                if (wheel?.IsDragMode == true) wheel.Dismiss("gesture-disabled");
            }
            SyncGazeAccessibility();
        }
        finally { syncingTray = false; }
    }

    void SyncGazeAccessibility()
    {
        if (disposed || exiting) return;
        bool enabled = settingsStore.CreateSnapshot().GazeAccessibilityMode;
        if (enabled)
        {
            if (gazeOrb == null)
            {
                gazeOrb = new GazeOrbWindow(
                    () => Math.Clamp(settingsStore.CreateSnapshot().GazeDwellMilliseconds, 600, 2500),
                    () => !exiting && !disposed && !busy && wheel == null,
                    OpenStickyFromGaze);
                gazeOrb.Closed += (_, _) =>
                {
                    if (!exiting && !disposed) gazeOrb = null;
                };
            }
            if (!gazeOrb.IsVisible)
            {
                gazeOrb.Show();
                gazeOrb.PositionOnRightEdge();
            }
            gazeOrb.SetSuspended(wheel != null || busy);
        }
        else if (gazeOrb != null)
        {
            var orb = gazeOrb;
            gazeOrb = null;
            orb.Close();
        }
    }

    void ShowSettings() => ShowHub();
    void ShowHistory() => ShowHub(SettingsHubWindow.HistorySection);

    void ShowHub(int? section = null, string[]? batchFiles = null)
    {
        if (exiting) return;
        shortcutOrder.Reset();
        if (wheel?.IsConverting == false) wheel.Dismiss("settings");
        try { SettingsHubWindow.ShowSingleton(section, StartHubBatch, CancelJob, batchFiles); }
        catch (Exception ex)
        {
            Evidence.Log("hub.open.error", ex.ToString());
            Toast(Loc.F("Impossibile aprire l'Hub: {0}", "Could not open FLIP Hub: {0}", ex.Message));
        }
    }

    void Initialize()
    {
        Program.InitializeConversionServices(settingsStore);
        FirstRunWelcomeWindow.TrayNotifier = Toast;
        hwnd = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(hwnd); source.AddHook(Messages);
        if (preferences.GestureEnabled) RegisterRaw();
        settingsStore.SettingsChanged += OnSettingsChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        SyncGazeAccessibility();
        watchFolderService = new WatchFolderService(settingsStore);
        Evidence.Log("host.start", new { pid = Environment.ProcessId, buttonsSwapped = Native.ButtonsSwapped, wheelShortcut.Name, DocumentTools.Pandoc, DocumentTools.Office, MediaTools.Ffmpeg });
        _ = Listen();
        UpdateInstaller.RequestExit = () => Dispatcher.BeginInvoke(Exit);
        UpdateInstaller.ConversionRunning = () => busy || watchFolderService?.IsConverting == true;
        // After an in-app update the installer may still be finishing; remove its downloaded package a little later.
        _ = Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token).ContinueWith(t => { if (!t.IsCanceled && !UpdateInstaller.Busy) UpdateInstaller.CleanupStagedUpdates(null); }, TaskScheduler.Default);
        if (args.Contains("--measure-idle")) MeasureIdle();
        Dispatcher.BeginInvoke(() =>
        {
            int p = Array.IndexOf(args, "--files");
            if (p >= 0) { if (args.Contains("--multi")) ShowPanel(Program.FileArguments(args)); else Open(Program.FileArguments(args), false); }
            else if (args.Contains("--settings")) ShowSettings();
            else if (args.Contains("--ui")) ShowSettings();
            else if (!args.Contains("--background"))
                Toast(Loc.F("{0} è pronto. Trascina un file e premi {1}.", "{0} is ready. Drag a file and press {1}.", Program.ReleaseTitle, KeyChoice.Describe(wheelShortcut.Id)));

            if (p < 0 && !args.Contains("--measure-idle") && !args.Contains("--background") &&
                !settingsStore.CreateSnapshot().OnboardingCompleted)
            {
                FirstRunWelcomeWindow.ShowSingleton();
            }
        });
    }

    void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!disposed && !exiting) ApplyTrayLocalization();
    });

    async Task CheckUpdatesAsync()
    {
        try
        {
            if (StoreEdition.IsPackaged) { StoreEdition.OpenStoreUpdates(); return; }
            var info = await UpdateChecker.CheckLatestAsync(lifetime.Token);
            Evidence.Log("update.check", info);
            if (info.IsNewer)
            {
                bool canInstall = UpdateInstaller.SelfUpdateBlockReason() == null;
                bool answer = SettingsHubWindow.Confirm(
                    Loc.T("Aggiornamento disponibile", "Update available"),
                    canInstall
                        ? Loc.F(
                            "È disponibile una nuova versione di Flip su GitHub!\n\nVersione installata: {0}\nNuova versione: {1} ({2})\n\nVuoi aprire la sezione Aggiornamenti per installarla?",
                            "A new version of FLIP is available on GitHub.\n\nInstalled: {0}\nLatest: {1} ({2})\n\nOpen the Updates section to install it?",
                            info.CurrentVersion, info.TagName, info.Name)
                        : Loc.F(
                            "È disponibile una nuova versione di Flip su GitHub!\n\nVersione installata: {0}\nNuova versione: {1} ({2})\n\nVuoi aprire la pagina di download?",
                            "A new version of FLIP is available on GitHub.\n\nInstalled: {0}\nLatest: {1} ({2})\n\nOpen the download page?",
                            info.CurrentVersion, info.TagName, info.Name),
                    canInstall ? Loc.T("Aggiorna…", "Update…") : Loc.T("Apri GitHub", "Open GitHub"));
                if (answer && canInstall) GuideSection.OpenUpdates();
                else if (answer) UpdateChecker.OpenBrowser(info.HtmlUrl);
            }
            else
            {
                Toast(Loc.F(
                    "Flip è aggiornato ({0}). Ultima release GitHub: {1}.",
                    "FLIP is up to date ({0}). Latest GitHub release: {1}.",
                    info.CurrentVersion, info.TagName));
            }
        }
        catch (Exception ex)
        {
            Evidence.Log("update.error", ex.Message);
            Toast(StoreEdition.IsPackaged
                ? Loc.T("Impossibile aprire Microsoft Store.", "Could not open the Microsoft Store.")
                : Loc.T("Impossibile verificare gli aggiornamenti su GitHub.", "Could not check GitHub updates."));
        }
    }
    void MeasureIdle()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; var process = Process.GetCurrentProcess(); TimeSpan cpu = default; long tick = 0; int sample = 0;
        timer.Tick += (_, _) => { if (sample++ == 0) { cpu = process.TotalProcessorTime; tick = Stopwatch.GetTimestamp(); timer.Interval = TimeSpan.FromSeconds(10); return; } timer.Stop(); process.Refresh(); Evidence.Log("idle.measurement", new { seconds = Stopwatch.GetElapsedTime(tick).TotalSeconds, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, threads = process.Threads.Count, handles = process.HandleCount, mouseListening, busy, panelOpen = settingsOpen, wheelOpen = wheel != null }); Exit(); }; timer.Start();
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
                await Dispatcher.InvokeAsync(() => { if (message == "exit") Exit(); else if (message == "sound-test") sound.Play(); else if (message is "settings" or "select") ShowSettings(); else if (message == "clipboard") TriggerClipboardFlip(); else if (message?.StartsWith("trigger:") == true) ApplyShortcut(message[8..]); else if (message?.StartsWith("multi:") == true) ShowPanel(JsonSerializer.Deserialize<string[]>(message[6..]) ?? []); else if (message != null) Open(JsonSerializer.Deserialize<string[]>(message) ?? [], false); });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Evidence.Log("ipc.error", ex.Message); if (!lifetime.IsCancellationRequested) await Task.Delay(300, lifetime.Token); }
        }
    }
    internal void CancelJob() => job?.Cancel();
    void SavePreferences() { try { preferences.Save(); } catch (Exception ex) { Toast(Loc.F("Impossibile salvare le preferenze: {0}", "Could not save preferences: {0}", ex.Message)); } }
    void QuickSelect()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Flip — scegli file da convertire", "FLIP — choose files to convert"),
            Multiselect = true,
            Filter = Loc.T("Tutti i file|*.*", "All files|*.*")
        };
        if (dialog.ShowDialog() == true) Open(dialog.FileNames, false);
    }
    void ShowPanel(string[] files) => ShowHub(SettingsHubWindow.HistorySection, files);

    void OpenStickyFromGaze(string[] selectedFiles)
    {
        if (exiting || disposed) return;
        if (busy) { Toast(Loc.T("Conversione in corso. Attendi il completamento.", "A conversion is already running. Please wait.")); return; }
        if (selectedFiles.Length == 0)
        {
            Toast(Loc.T(
                "Nessun file locale selezionato: seleziona un file su Desktop/Esplora File o copialo negli Appunti e ripunta la sfera FLIP.",
                "No local file selected: select a file in Explorer or copy it to the clipboard, then look at the FLIP orb again."));
            return;
        }
        Open(selectedFiles, dragging: false, stickyGaze: true);
    }

    void DismissWheel(bool allowCommit = false, bool resetInputState = false)
    {
        var current = wheel;
        if (current == null || current.IsConverting || busy) return;
        release.Stop();
        watchdog.Stop();
        dragReleasedTick = 0;
        if (resetInputState)
        {
            shortcutOrder.Reset();
            SetMouseListening(false);
        }
        if (allowCommit && current.IsVisible)
        {
            current.Dismiss("watchdog-commit");
        }
        else
        {
            if (ReferenceEquals(wheel, current)) wheel = null;
            current.CloseImmediately("host-dismiss");
        }
        gazeOrb?.SetSuspended(busy);
        UpdateScreenEdgeAura();
    }

    void OnWatchdogTick()
    {
        var current = wheel;
        if (current == null || !current.IsDragMode)
        {
            watchdog.Stop();
            dragReleasedTick = 0;
            return;
        }
        if (current.IsConverting || busy) return;

        long now = Environment.TickCount64;
        if (!Native.IsPrimaryMousePressed())
        {
            if (dragReleasedTick == 0) dragReleasedTick = now;
            if (now - dragReleasedTick >= 500 || !current.IsVisible)
            {
                DismissWheel(false, resetInputState: true);
                return;
            }
        }
        else
        {
            dragReleasedTick = 0;
        }

        if (now - dragOpenedTick >= 20_000)
            DismissWheel(false, resetInputState: true);
    }

    void Open(string[] files, bool dragging, FlipSettings? customSettings = null, Action? onWheelClosed = null, bool stickyGaze = false)
    {
        if (exiting || busy) { if (busy) Toast(Loc.T("Conversione in corso. Attendi il completamento.", "A conversion is already running. Please wait.")); return; }
        if (wheel != null)
        {
            if (wheel.IsConverting) { Toast(Loc.T("Conversione in corso. Attendi il completamento.", "A conversion is already running. Please wait.")); return; }
            DismissWheel(false);
        }
        if (!dragging)
        {
            files = Policy.ReadFiles(new DataObject(DataFormats.FileDrop, files));
            if (files.Length == 0) { Toast(Loc.T("Seleziona file locali disponibili.", "Select available local files.")); return; }
        }
        var snapshot = (customSettings ?? settingsStore.CreateSnapshot()).CreateSnapshot();
        var createdWheel = new Wheel(Toast, (paths, format) => StartRoutedJob(paths, format, snapshot), sound.Play, CancelJob,
            StartQuickTool, snapshot,
            convertBrowser: (candidate, format, wheelSettings) => StartBrowserRoutedJob(candidate, format, wheelSettings),
            runBrowserTool: (candidate, tool, context) => StartBrowserQuickTool(candidate, tool, context),
            openQuickLook: StartQuickLook);
        wheel = createdWheel;
        createdWheel.Closed += (_, _) =>
        {
            onWheelClosed?.Invoke();
            if (!ReferenceEquals(wheel, createdWheel)) return;
            wheel = null; release.Stop(); watchdog.Stop(); dragReleasedTick = 0; shortcutOrder.Reset(); SetMouseListening(false);
            gazeOrb?.SetSuspended(busy);
            UpdateScreenEdgeAura();
        };
        gazeOrb?.SetSuspended(true);
        if (dragging) SetMouseListening(true);
        createdWheel.Open(files, dragging, stickyGaze);
        if (dragging)
        {
            dragOpenedTick = Environment.TickCount64;
            dragReleasedTick = 0;
            watchdog.Start();
        }
        UpdateScreenEdgeAura();
    }

    /// <summary>Aura ai bordi del monitor: attiva mentre la ruota è aperta o un job è in corso (mai nei test).</summary>
    void UpdateScreenEdgeAura()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateScreenEdgeAura());
            return;
        }
        var activeWheel = wheel;
        bool wheelOpen = activeWheel?.IsVisible == true;
        bool active = !exiting && !disposed && settingsStore.Settings.EnableScreenEdgeAura && (wheelOpen || busy);
        nint keepBelow = wheelOpen ? new WindowInteropHelper(activeWheel!).Handle : 0;
        ScreenEdgeAuraWindow.SetActive(active, keepBelow);
    }

    internal void TriggerClipboardFlip()
    {
        if (exiting || busy)
        {
            if (busy) Toast(Loc.T("Conversione in corso. Attendi il completamento.", "A conversion is already running. Please wait."));
            return;
        }
        if (wheel != null)
        {
            if (wheel.IsConverting) { Toast(Loc.T("Conversione in corso. Attendi il completamento.", "A conversion is already running. Please wait.")); return; }
            DismissWheel(false);
        }

        string stagingDir = Path.Combine(Path.GetTempPath(), "FLIP", "ClipboardStaging", Guid.NewGuid().ToString("N"));
        if (!ClipboardFlipService.TryMaterializeClipboard(stagingDir, out var payload) || !payload.Success || payload.FilePaths.Count == 0)
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            Toast(string.IsNullOrWhiteSpace(payload.ErrorMessage)
                ? Loc.T("Nessun contenuto convertibile trovato negli appunti.", "No convertible content found in the clipboard.")
                : Loc.F("Appunti non convertibili: {0}", "Could not convert clipboard: {0}", payload.ErrorMessage));
            return;
        }

        var effectiveSnapshot = ClipboardFlipService.GetEffectiveOutputSettings(settingsStore.CreateSnapshot(), payload.UsesTemporaryStaging);

        void CleanupStaging()
        {
            if (payload.UsesTemporaryStaging)
            {
                try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
            }
        }

        Open(payload.FilePaths.ToArray(), dragging: false, customSettings: effectiveSnapshot, onWheelClosed: CleanupStaging);
        Toast(Loc.F("Appunti caricati: {0}", "Clipboard loaded: {0}", payload.Description));
    }
    void StartHubBatch(string[] files, string[] formats, FlipSettings snapshot, Action<string> status)
    {
        if (busy || exiting) { status(Loc.T("Una conversione è già in corso.", "A conversion is already running.")); return; }
        var currentFiles = Policy.ReadFiles(new DataObject(DataFormats.FileDrop, files));
        var targets = RoutePlanner.GetReachableTargetFormats(currentFiles.Select(path => Path.GetExtension(path)).ToArray(), snapshot)
            .Select(format => format.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currentFiles.Length == 0 || formats.Length == 0 || !formats.All(targets.Contains))
        {
            status(Loc.T("Almeno un file o formato non è più disponibile. Controlla la selezione.", "One or more selected files or formats are no longer available. Check your selection."));
            return;
        }
        status(Loc.T("Conversione avviata…", "Conversion started…"));
        StartJob(token => RunRoutedJob(currentFiles, formats, snapshot, token, status));
    }

    void StartJob(Func<CancellationToken, Task> run)
    {
        busy = true; job = new CancellationTokenSource();
        release.Stop(); watchdog.Stop();
        UpdateScreenEdgeAura();
        work = run(job.Token);
    }

    void FinishJob()
    {
        busy = false;
        job?.Dispose();
        job = null;
        gazeOrb?.SetSuspended(wheel != null);
        UpdateScreenEdgeAura();
    }

    void StartRoutedJob(string[] files, string format, FlipSettings snapshot)
    {
        if (busy || exiting)
        {
            wheel?.CompleteConversion(false);
            return;
        }
        StartJob(token => RunRoutedJob(files, [format], snapshot, token));
    }

    async Task RunRoutedJob(string[] files, string[] formats, FlipSettings snapshot, CancellationToken token, Action<string>? status = null)
    {
        var activeWheel = status == null ? wheel : null;
        bool packZip = activeWheel?.PackOutputIntoZip == true;
        try
        {
            var progress = new Progress<double>(fraction =>
            {
                if (activeWheel != null) activeWheel.SetConversionProgress((int)Math.Round(fraction * 100));
                else if (busy && !token.IsCancellationRequested) status?.Invoke(Loc.F("Conversione in corso ({0:P0})", "Converting ({0:P0})", fraction));
            });
            var batch = await Task.Run(() => RoutePlanner.ExecuteBatchAsync(files, formats, snapshot,
                progress: progress, ct: token), token);
            var results = batch.Select(item => new ConversionResult(item.SourcePath, item.TargetExtension,
                item.Result.OutputFilePaths.ToArray(), item.Result.Success ? null : item.Result.ErrorMessage ?? Loc.T("Conversione non riuscita.", "Conversion failed."))).ToList();
            var outputs = results.SelectMany(result => result.Outputs).ToArray();
            if (outputs.Length > 0)
            {
                lastFolder = Path.GetDirectoryName(outputs[0]);
                lastGeneratedOutputs = outputs;
            }

            if (token.IsCancellationRequested)
            {
                activeWheel?.Dismiss("cancelled");
                Report(Loc.F("Conversione annullata. File creati: {0}.", "Conversion canceled. Files created: {0}.", outputs.Length));
                return;
            }

            string? zipCreated = null;
            if (packZip && outputs.Length > 0)
            {
                zipCreated = Program.PackOutputsIntoZipArchive(outputs);
                lastGeneratedOutputs = [.. outputs, zipCreated];
            }

            int failures = results.Count(result => result.Error != null);
            activeWheel?.CompleteConversion(failures == 0 && outputs.Length > 0,
                results.FirstOrDefault(result => result.Error != null)?.Error);
            string summary = Loc.F("File creati: {0}.", "Files created: {0}.", outputs.Length);
            if (zipCreated != null) summary += Loc.F(" Archivio ZIP: {0}.", " ZIP archive: {0}.", Path.GetFileName(zipCreated));
            if (failures > 0) summary += Loc.F(" Conversioni non riuscite: {0}. {1}", " Failed conversions: {0}. {1}", failures, results.First(result => result.Error != null).Error!);
            var warning = batch.FirstOrDefault(item => item.Result.Success && !string.IsNullOrWhiteSpace(item.Result.ErrorMessage));
            if (warning != null) summary += " " + warning.Result.ErrorMessage;
            Report(summary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            activeWheel?.Dismiss("cancelled");
            Report(Loc.T("Conversione annullata.", "Conversion canceled."));
        }
        catch (Exception ex)
        {
            activeWheel?.CompleteConversion(false, ex.Message);
            Report(ex.Message);
            Evidence.Log("route-job.error", ex.ToString());
        }
        finally { FinishJob(); }

        void Report(string text)
        {
            status?.Invoke(text);
            Toast(text);
            try { SettingsHubWindow.RefreshHistory(); }
            catch (Exception ex) { Evidence.Log("history.refresh.error", ex.Message); }
        }
    }

    void StartQuickTool(IQuickTool tool, QuickToolContext context)
    {
        if (busy || exiting)
        {
            wheel?.CompleteConversion(false);
            return;
        }
        StartJob(token => RunQuickTool(tool, context, token));
    }

    void StartBrowserRoutedJob(BrowserDropCandidate candidate, string format, FlipSettings wheelSettings)
    {
        if (busy || exiting)
        {
            wheel?.CompleteConversion(false);
            return;
        }
        StartJob(token => RunBrowserRoutedJob(candidate, format, wheelSettings, token));
    }

    async Task RunBrowserRoutedJob(BrowserDropCandidate candidate, string format, FlipSettings wheelSettings, CancellationToken token)
    {
        var activeWheel = wheel;
        bool packZip = activeWheel?.PackOutputIntoZip == true;
        string stagingDir = Path.Combine(Path.GetTempPath(), "FLIP", "BrowserStaging", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDir);
            var downloadProgress = new Progress<double>(pct =>
                activeWheel?.SetConversionProgress((int)Math.Round(pct * 0.4)));
            var materialized = await Task.Run(() =>
                BrowserDropService.MaterializeAsync(candidate, stagingDir, downloadProgress, token), token);
            if (!materialized.Success)
            {
                activeWheel?.CompleteConversion(false);
                Toast(Loc.F("Download non riuscito: {0}", "Download failed: {0}", materialized.ErrorMessage ?? Loc.T("Errore sconosciuto.", "Unknown error.")));
                return;
            }

            var effectiveSettings = BrowserDropService.GetEffectiveOutputSettings(wheelSettings);
            string effectiveExt = materialized.EffectiveExtension.TrimStart('.').ToLowerInvariant();
            var reachable = RoutePlanner.GetReachableTargetFormats(effectiveExt, effectiveSettings);
            if (!reachable.Any(f => string.Equals(f.Extension, format, StringComparison.OrdinalIgnoreCase)))
            {
                activeWheel?.CompleteConversion(false);
                Toast(Loc.F(
                    "Il formato {0} non è raggiungibile dal tipo effettivo ({1}) scaricato dal browser.",
                    "Format {0} is not reachable from the actual type ({1}) downloaded from the browser.",
                    format, effectiveExt.ToUpperInvariant()));
                return;
            }

            var convProgress = new Progress<double>(fraction =>
                activeWheel?.SetConversionProgress(40 + (int)Math.Round(fraction * 60)));
            var batch = await Task.Run(() => RoutePlanner.ExecuteBatchAsync(
                [materialized.LocalFilePath], [format], effectiveSettings,
                progress: convProgress, ct: token), token);
            var results = batch.Select(item => new ConversionResult(item.SourcePath, format,
                item.Result.OutputFilePaths.ToArray(),
                item.Result.Success ? null : item.Result.ErrorMessage ?? Loc.T("Conversione non riuscita.", "Conversion failed."))).ToList();
            var outputs = results.SelectMany(r => r.Outputs).ToArray();
            if (outputs.Length > 0)
            {
                lastFolder = Path.GetDirectoryName(outputs[0]);
                lastGeneratedOutputs = outputs;
            }

            if (token.IsCancellationRequested)
            {
                activeWheel?.Dismiss("cancelled");
                Toast(Loc.T("Conversione annullata.", "Conversion canceled."));
                return;
            }

            string? zipCreated = null;
            if (packZip && outputs.Length > 0)
            {
                zipCreated = Program.PackOutputsIntoZipArchive(outputs);
                lastGeneratedOutputs = [.. outputs, zipCreated];
            }

            int failures = results.Count(r => r.Error != null);
            activeWheel?.CompleteConversion(failures == 0 && outputs.Length > 0);
            string summary = Loc.F("File dal browser: {0} creati.", "Browser drop: {0} created.", outputs.Length);
            if (zipCreated != null) summary += Loc.F(" Archivio ZIP: {0}.", " ZIP archive: {0}.", Path.GetFileName(zipCreated));
            if (failures > 0) summary += Loc.F(" Errori: {0}. {1}", " Errors: {0}. {1}", failures, results.First(r => r.Error != null).Error!);
            Toast(summary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            activeWheel?.Dismiss("cancelled");
            Toast(Loc.T("Download/conversione annullata.", "Download/conversion canceled."));
        }
        catch (Exception ex)
        {
            activeWheel?.CompleteConversion(false);
            Toast(ex.Message);
            Evidence.Log("browser-route-job.error", ex.ToString());
        }
        finally
        {
            FinishJob();
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
        }
    }

    void StartBrowserQuickTool(BrowserDropCandidate candidate, IQuickTool tool, QuickToolContext context)
    {
        if (busy || exiting)
        {
            wheel?.CompleteConversion(false);
            return;
        }
        StartJob(token => RunBrowserQuickTool(candidate, tool, context, token));
    }

    async Task RunBrowserQuickTool(BrowserDropCandidate candidate, IQuickTool tool, QuickToolContext context, CancellationToken token)
    {
        var activeWheel = wheel;
        string stagingDir = Path.Combine(Path.GetTempPath(), "FLIP", "BrowserStaging", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stagingDir);
            var downloadProgress = new Progress<double>(pct =>
                activeWheel?.SetConversionProgress((int)Math.Round(pct * 0.4)));
            var materialized = await Task.Run(() =>
                BrowserDropService.MaterializeAsync(candidate, stagingDir, downloadProgress, token), token);
            if (!materialized.Success)
            {
                activeWheel?.CompleteConversion(false);
                Toast(Loc.F("Download non riuscito: {0}", "Download failed: {0}", materialized.ErrorMessage ?? Loc.T("Errore sconosciuto.", "Unknown error.")));
                return;
            }

            context.InputFilePaths = [materialized.LocalFilePath];
            context.Settings = BrowserDropService.GetEffectiveOutputSettings(context.Settings);
            context.CancellationToken = token;
            context.Progress = new Progress<double>(fraction =>
            {
                if (double.IsFinite(fraction))
                    activeWheel?.SetConversionProgress(40 + (int)Math.Round(Math.Clamp(fraction, 0, 1) * 60));
            });
            var result = await Task.Run(() => tool.ExecuteAsync(context), token);
            int count = result.OutputFilePaths.Count;
            if (count > 0)
            {
                lastFolder = Path.GetDirectoryName(result.OutputFilePaths[0]);
                lastGeneratedOutputs = result.OutputFilePaths.ToArray();
            }

            if (token.IsCancellationRequested)
                activeWheel?.Dismiss("cancelled");
            else
                activeWheel?.CompleteConversion(result.Success && count > 0);

            string summary = token.IsCancellationRequested ? Loc.T("Operazione annullata.", "Operation canceled.") :
                result.Success ? Loc.F("{0}: operazione completata.", "{0}: completed.", tool.DisplayName) :
                Loc.F("{0}: operazione non riuscita.", "{0}: failed.", tool.DisplayName);
            if (count > 0) summary += Loc.F(" File creati: {0}.", " Files created: {0}.", count);
            if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                summary += " " + result.ErrorMessage;
            Toast(summary);
        }
        catch (OperationCanceledException)
        {
            activeWheel?.Dismiss("cancelled");
            Toast(Loc.T("Operazione annullata.", "Operation canceled."));
        }
        catch (Exception ex)
        {
            activeWheel?.CompleteConversion(false);
            Toast(ex.Message);
            Evidence.Log("browser-quick-tool.error", new { tool.Id, ex.Message });
        }
        finally
        {
            FinishJob();
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
        }
    }

    async Task RunQuickTool(IQuickTool tool, QuickToolContext context, CancellationToken token)
    {
        try
        {
            var activeWheel = wheel;
            context.CancellationToken = token;
            context.Progress = new Progress<double>(fraction =>
            {
                if (double.IsFinite(fraction))
                    activeWheel?.SetConversionProgress((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100));
            });
            if (tool is Wheel.ToolActionQuickTool { ActionId: FlipPreviewWindow.CropInteractiveToolId } && context.InputFilePaths.Count > 0)
            {
                // ✂ Manuale: apre FlipPreviewWindow direttamente nell'editor Ritaglia & Oscura sul primo file.
                activeWheel?.CompleteConversion(true);
                ShowCropEditor(context.InputFilePaths[0], context.Settings);
                return;
            }
            if (FlipPreviewService.Supports(tool, context))
            {
                // FLIP QuickLook: anteprima in %TEMP%\FLIP\Preview, nessun file nella cartella dell'utente finché non si salva.
                var session = await FlipPreviewService.PrepareAsync(tool, context, token);
                if (token.IsCancellationRequested || exiting || disposed)
                {
                    session.DeleteTemporaryFiles();
                    activeWheel?.Dismiss("cancelled");
                    Toast(Loc.T("Operazione annullata.", "Operation canceled."));
                    return;
                }
                activeWheel?.CompleteConversion(true);
                ShowPreview(session);
                return;
            }
            var result = await Task.Run(() => tool.ExecuteAsync(context), token);
            int count = result.OutputFilePaths.Count;
            if (count > 0)
            {
                lastFolder = Path.GetDirectoryName(result.OutputFilePaths[0]);
                lastGeneratedOutputs = result.OutputFilePaths.ToArray();
            }
            if (token.IsCancellationRequested)
                activeWheel?.Dismiss("cancelled");
            else
                activeWheel?.CompleteConversion(result.Success && count > 0, result.ErrorMessage);

            string summary = token.IsCancellationRequested ? Loc.T("Operazione annullata.", "Operation canceled.") :
                result.Success ? Loc.F("{0}: operazione completata.", "{0}: completed.", tool.DisplayName) :
                Loc.F("{0}: operazione non riuscita.", "{0}: failed.", tool.DisplayName);
            if (count > 0) summary += Loc.F(" File creati: {0}.", " Files created: {0}.", count);
            if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                summary += " " + result.ErrorMessage;
            Toast(summary);
        }
        catch (OperationCanceledException)
        {
            wheel?.Dismiss("cancelled");
            Toast(Loc.T("Operazione annullata.", "Operation canceled."));
        }
        catch (Exception ex)
        {
            wheel?.CompleteConversion(false, ex.Message);
            Toast(ex.Message);
            Evidence.Log("quick-tool.error", new { tool.Id, ex.Message });
        }
        finally { FinishJob(); }
    }
    /// <summary>Spazio durante il drag sulla ruota: FLIP QuickLook immediato sul file trascinato.</summary>
    void StartQuickLook(string path)
    {
        if (busy || exiting || disposed)
        {
            Toast(Loc.T("Attendi la fine dell'operazione in corso.", "Please wait for the current operation to finish."));
            return;
        }
        var snapshot = settingsStore.CreateSnapshot();
        var tool = FlipPreviewService.ResolveQuickLookTool(path, snapshot);
        if (tool == null || !File.Exists(path))
        {
            Toast(Loc.T("Anteprima FLIP QuickLook non disponibile per questo file.", "FLIP QuickLook is not available for this file."));
            return;
        }
        var context = new QuickToolContext { InputFilePaths = [path], Settings = snapshot };
        StartJob(token => RunQuickLook(tool, context, token));
    }

    async Task RunQuickLook(IQuickTool tool, QuickToolContext context, CancellationToken token)
    {
        try
        {
            Evidence.Log("quicklook.start", new { tool.Id, files = context.InputFilePaths });
            var session = await FlipPreviewService.PrepareAsync(tool, context, token);
            if (token.IsCancellationRequested || exiting || disposed)
            {
                session.DeleteTemporaryFiles();
                return;
            }
            ShowPreview(session);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Evidence.Log("quicklook.cancelled", tool.Id);
        }
        catch (Exception ex)
        {
            Toast(Loc.F("Anteprima non riuscita: {0}", "QuickLook failed: {0}", ex.Message));
            Evidence.Log("quicklook.error", new { tool.Id, ex.Message });
        }
        finally { FinishJob(); }
    }

    void ShowPreview(FlipPreviewSession session)
    {
        if (disposed || exiting)
        {
            session.DeleteTemporaryFiles();
            return;
        }
        activePreview?.Close();
        try
        {
            var window = new FlipPreviewWindow(session, OnPreviewSaved);
            activePreview = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(activePreview, window)) activePreview = null;
            };
            window.Show();
            Evidence.Log("preview.opened", new { session.ToolId, source = session.SourcePath });
        }
        catch (Exception ex)
        {
            if (activePreview != null && !activePreview.IsVisible) activePreview = null;
            session.DeleteTemporaryFiles();
            Toast(Loc.F("Anteprima non disponibile: {0}", "Preview unavailable: {0}", ex.Message));
            Evidence.Log("preview.open.error", ex.ToString());
        }
    }

    void ShowCropEditor(string sourcePath, FlipSettings settings)
    {
        if (disposed || exiting) return;
        activePreview?.Close();
        try
        {
            var window = FlipPreviewWindow.CreateCropEditor(sourcePath, settings, OnPreviewSaved);
            activePreview = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(activePreview, window)) activePreview = null;
            };
            window.Show();
            Evidence.Log("crop-editor.opened", new { source = sourcePath });
        }
        catch (Exception ex)
        {
            if (activePreview != null && !activePreview.IsVisible) activePreview = null;
            Toast(Loc.F("Editor di ritaglio non disponibile: {0}", "Crop editor unavailable: {0}", ex.Message));
            Evidence.Log("crop-editor.open.error", ex.ToString());
        }
    }

    void OnPreviewSaved(IReadOnlyList<string> saved)
    {
        if (saved.Count == 0) return;
        lastFolder = Path.GetDirectoryName(saved[0]);
        lastGeneratedOutputs = saved.ToArray();
        Toast(Loc.F("File salvato: {0}.", "Saved: {0}.", string.Join(", ", saved.Select(path => Path.GetFileName(path)))));
        try { SettingsHubWindow.RefreshHistory(); }
        catch (Exception ex) { Evidence.Log("history.refresh.error", ex.Message); }
    }

    void UndoLastConversion()
    {
        try
        {
            var existing = lastGeneratedOutputs.Where(File.Exists).ToArray();
            if (existing.Length == 0)
            {
                Toast(Loc.T("Nessun file dell'ultima conversione disponibile da annullare.", "No files from the last conversion to undo."));
                return;
            }
            var (restored, missing) = OutputPathAllocator.RestoreOriginals(existing);
            if (missing.Length > 0)
            {
                // Senza l'originale il file convertito è l'unica copia: non va tolto.
                Toast(Loc.F("Annullamento interrotto: l'originale {0} non è più nel Cestino, quindi i file convertiti restano al loro posto.",
                    "Undo stopped: the original {0} is no longer in the Recycle Bin, so the converted files stay in place.", Path.GetFileName(missing[0])));
                return;
            }
            int count = OutputPathAllocator.MoveToRecycleBin(existing);
            lastGeneratedOutputs = [];
            Evidence.Log("undo.last-conversion", new { count, restored, files = existing });
            Toast(Loc.F("Ultima conversione annullata: {0} file spostati nel Cestino.", "Undid last conversion: moved {0} file(s) to Recycle Bin.", count) +
                (restored > 0 ? " " + Loc.F("Originali ripristinati: {0}.", "Originals restored: {0}.", restored) : ""));
        }
        catch (Exception ex)
        {
            Evidence.Log("undo.last-conversion.error", ex.Message);
            Toast(Loc.F("Impossibile annullare l'ultima conversione: {0}", "Could not undo last conversion: {0}", ex.Message));
        }
    }
    void OpenLatestFolder()
    {
        if (lastFolder != null && TryOpenFolder(lastFolder)) return;
        ShowHistory();
        if (settingsStore.CreateSnapshot().RecentConversions.Any(item => item.Success))
            Toast(Loc.T("Il percorso non è disponibile. Consulta la cronologia delle conversioni.", "Folder unavailable. Check your conversion history."));
    }

    internal static void OpenFolder(string folder) => TryOpenFolder(folder);

    static bool TryOpenFolder(string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return false;
            string fullPath = Path.GetFullPath(folder);
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(fullPath);
            using var process = Process.Start(start);
            if (process == null) return false;
            Evidence.Log("folder.open.request", fullPath);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Evidence.Log("folder.open.error", ex.Message);
            return false;
        }
    }

    internal void Toast(string text) { if (disposed) return; tray.BalloonTipTitle = "Flip"; tray.BalloonTipText = text.Length > 250 ? text[..250] : text; tray.ShowBalloonTip(5000); Evidence.Log("notice", text); }
    async void Exit()
    {
        if (exiting) return; exiting = true; job?.Cancel(); wheel?.Dismiss("exit"); activePreview?.Close();
        FirstRunWelcomeWindow.CloseSingleton(); gazeOrb?.Close(); gazeOrb = null; RemoveRaw();
        ScreenEdgeAuraWindow.SetActive(false);
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
                    () => raw && !busy && !exiting && !settingsOpen && Native.IsDragButtonHeld
                        && !ShortcutHook.ModifiersHeld() && (wheel?.IsDragMode == true || ShortcutHook.IsExplorerForeground() || Native.IsDragButtonHeld),
                    down => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ShortcutKeyChanged(down)),
                    command => raw && !busy && !exiting && !settingsOpen &&
                        wheel?.CanUseDragCommand(command) == true && Native.IsDragButtonHeld,
                    command =>
                    {
                        var activeWheel = wheel;
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            if (ReferenceEquals(wheel, activeWheel)) activeWheel?.HandleDragCommand(command);
                        });
                    },
                    clipboardGesture: () =>
                    {
                        string shortcutStr = settingsStore.Settings.EnableClipboardFlipShortcut;
                        return ClipboardFlipService.TryParseShortcut(shortcutStr, out var g) ? g : default;
                    },
                    onClipboardFlip: () =>
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => TriggerClipboardFlip());
                    },
                    canClipboardFlip: () => raw && !busy && !exiting && !settingsOpen && !Native.IsDragButtonHeld);
                Evidence.Log("shortcut.hook.registered", new { triggerName, scope = "Drag anywhere or Explorer/Desktop" });
            }
            catch (Exception ex) { RemoveRaw(); gestureItem.Checked = false; Toast(ex.Message); return; }
        }
        if (!raw) Toast(Loc.T("Gesto non disponibile: usa il menu dell'icona.", "Gesture unavailable: use the tray icon menu."));
    }
    void ActivateWheelShortcut()
    {
        bool held = Native.IsDragButtonHeld;
        if (!held || settingsOpen || !raw || exiting) return;
        SetMouseListening(true);
        Evidence.Log("wheel.shortcut", new { wheelShortcut.Name, held });
        shortcutHook?.ReserveRepeatsForGesture();
        var initialSelection = GazeOrbWindow.CaptureSelectedFiles(allowClipboardFallback: false);
        Open(initialSelection, true);
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
            || !(wheel?.IsDragMode == true || ShortcutHook.IsExplorerForeground() || Native.IsDragButtonHeld)) return;
        bool primaryHeld = Native.IsDragButtonHeld;
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
                if ((key.Flags & 1) == 0)
                {
                    if (key.VKey == 0x1B)
                    {
                        shortcutOrder.Reset();
                        if (wheel == null) SetMouseListening(false);
                        Dispatcher.BeginInvoke(() => wheel?.Dismiss("escape"));
                    }
                    else if (key.VKey == 0x20 && wheel is { IsDragMode: false, IsStickyGaze: true, IsVisible: true, IsConverting: false })
                    {
                        Dispatcher.BeginInvoke(() => wheel?.TriggerQuickLook("raw-space"));
                    }
                    else if (key.VKey == 0x09 && wheel is { IsDragMode: false, IsStickyGaze: true, IsVisible: true, IsConverting: false })
                    {
                        Dispatcher.BeginInvoke(() => wheel?.RequestFlipFace());
                    }
                }
                // Trigger keys are handled once by ShortcutHook and consumed only
                // for Explorer/Desktop drags. Raw keyboard input still observes Escape.
            }
            else if (header.Type == 0)
            {
                if (size < hs + 8) return 0;
                ushort flags = unchecked((ushort)Marshal.ReadInt16(data, 4));
                const ushort mouseWheel = 0x0400;
                if ((flags & mouseWheel) != 0 && Native.IsDragButtonHeld)
                    wheel?.HandleDragScroll(Marshal.ReadInt16(data, 6));
                bool swapped = Native.ButtonsSwapped;
                Native.GetCursorPos(out var cursor);
                if (Native.PrimaryDown(flags, swapped)) shortcutOrder.MouseDown(cursor.X, cursor.Y);
                if (Native.PrimaryUp(flags, swapped)) { shortcutOrder.MouseUp(); if (wheel?.IsDragMode == true) { release.Stop(); release.Start(); } else if (!shortcutOrder.Held) SetMouseListening(false); }
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
        watchFolderService?.Dispose(); watchFolderService = null;
        settingsStore.SettingsChanged -= Program.SyncDragButtonPreference;
        settingsStore.SettingsChanged -= OnSettingsChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        source?.RemoveHook(Messages);
        FirstRunWelcomeWindow.TrayNotifier = null;
        SettingsHubWindow.CloseSingleton();
        FirstRunWelcomeWindow.CloseSingleton();
        gazeOrb?.Close(); gazeOrb = null;
        wheel?.Close(); sound.Dispose(); tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); lifetime.Dispose(); Evidence.Log("host.stop");
    }

    sealed class TrayColors : Forms.ProfessionalColorTable
    {
        static readonly System.Drawing.Color Surface = System.Drawing.Color.FromArgb(26, 28, 35);
        static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(46, 50, 64);
        static readonly System.Drawing.Color Highlight = System.Drawing.Color.FromArgb(55, 59, 79);
        public override System.Drawing.Color ToolStripDropDownBackground => Surface;
        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color MenuItemBorder => Highlight;
        public override System.Drawing.Color MenuItemSelected => Highlight;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Highlight;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Highlight;
        public override System.Drawing.Color SeparatorDark => Border;
        public override System.Drawing.Color SeparatorLight => Surface;
        public override System.Drawing.Color CheckBackground => Highlight;
        public override System.Drawing.Color CheckSelectedBackground => Highlight;
        public override System.Drawing.Color CheckPressedBackground => Highlight;
    }
}
