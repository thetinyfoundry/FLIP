using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Flip.Core;
using Flip.Engines;
using Flip.Routing;

namespace Flip;

internal sealed class Wheel : Window
{
    readonly Canvas canvas = new() { Width = WheelLayout.Size, Height = WheelLayout.Size };
    readonly Canvas surface = new();
    readonly Canvas outerRing = new() { IsHitTestVisible = false };
    readonly Canvas rememberedFanLayer = new() { IsHitTestVisible = false };
    readonly Canvas dwellOverlay = new() { IsHitTestVisible = false };
    readonly Border gazeBar = new() { Visibility = Visibility.Collapsed };
    readonly Border mixedGroupBar = new() { Visibility = Visibility.Collapsed };
    readonly WrapPanel mixedGroupPanel = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    readonly List<(Border Pill, string? ExtKey)> mixedGroupPills = [];
    readonly Border[] gazeButtons = new Border[3];
    readonly Border[] gazeButtonProgress = new Border[3];
    readonly TextBlock[] gazeButtonLabels = new TextBlock[3];
    readonly bool[] gazeBarCompletedUntilLeave = new bool[3];
    const double GazeBarWidth = 486;
    const double GazeBarHeight = 52;
    const double GazeBarTopOffset = 382;
    readonly Viewbox primaryView;
    readonly DispatcherTimer leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    readonly ScaleTransform faceScale = new(1, 1);
    readonly TranslateTransform faceShift = new(0, 0);
    readonly TransformGroup faceTransform = new();
    readonly TranslateTransform sheenShift = new(0, -FlipSheenHeight);
    readonly Canvas flipSheen;
    const double FlipSheenHeight = 74;
    const int HalfFlipMilliseconds = 120;
    const double PrimaryDipSize = 320.0;
    const double HoverShift = 3.5;
    static readonly Brush SectorStroke = Freeze(new SolidColorBrush(Color.FromArgb(140, 130, 133, 124)));
    static readonly Brush ExpandedParentStroke = Freeze(new SolidColorBrush(Color.FromArgb(230, 0xFF, 0x7A, 0x18)));
    private sealed record PrimaryCascadeWedge(
        System.Windows.Shapes.Path Sector,
        TextBlock Label,
        System.Windows.Shapes.Path? Marker,
        double SectorOpacity,
        double LabelOpacity,
        ScaleTransform SectorScale,
        ScaleTransform LabelScale,
        ScaleTransform? MarkerScale);

    const double OpenCascadeInitialScale = 0.88;
    const int OpenCascadeStepMs = 19;
    const int OpenCascadeDurationMs = 120;
    bool playOpenCascade;
    readonly List<PrimaryCascadeWedge> primaryCascadeWedges = [];
    int openCascadeToken;
    EventHandler? openCascadeRenderHandler;
    bool openedFromHost;
    bool aligningDpi;
    nint activeMonitor;
    Rect reservedPixels = Rect.Empty;
    Int32Rect lastWindowRect;
    IReadOnlyDictionary<string, ConversionRoute> primaryRoutes = System.Collections.Immutable.ImmutableDictionary<string, ConversionRoute>.Empty;
    readonly Dictionary<string, string> routeCaptionCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly string[] specialEngineKeys = ["ffmpeg", "pandoc", "libreoffice", "calibre", "whisper", "tesseract", "local_ai"];
    static readonly Color FlipAccent = Color.FromRgb(0xFF, 0x7A, 0x18);
    static readonly Color RememberedTargetColor = Color.FromRgb(0xFF, 0xB5, 0x47);
    static readonly Brush RememberedTargetBrush = Freeze(new SolidColorBrush(RememberedTargetColor));
    internal static readonly ConcurrentDictionary<string, string> LastSuccessfulTargetBySourceExt = new(StringComparer.OrdinalIgnoreCase);
    readonly Rectangle cropOutline = new() { Stroke = ExpandedParentStroke, StrokeThickness = 1.2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly ConcurrentDictionary<string, Lazy<(int W, int H)?>> imageSizes = new(StringComparer.OrdinalIgnoreCase);
    (int W, int H)? resizeSource;
    readonly TextBlock center = new()
    {
        Width = 150,
        MaxHeight = 92,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextAlignment = TextAlignment.Center,
        Foreground = Brushes.White,
        FontSize = 12.5,
        LineHeight = 17,
        TextWrapping = TextWrapping.Wrap,
        IsHitTestVisible = false
    };
    readonly TextBlock toolNoticeText = new()
    {
        MaxWidth = 268,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.White,
        FontSize = 12,
        LineHeight = 16,
        IsHitTestVisible = false
    };
    readonly Border toolNotice = new()
    {
        Width = 292,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
        Background = new SolidColorBrush(Color.FromArgb(238, 20, 22, 25)),
        BorderBrush = new SolidColorBrush(FlipAccent),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 8, 12, 8)
    };
    readonly DispatcherTimer toolNoticeTimer = new() { Interval = TimeSpan.FromMilliseconds(3800) };
    const double ToolNoticeLeft = 14, ToolNoticeTop = 132;
    /// <summary>Pillola "Formati · Strumenti" nella parte bassa del cerchio centrale (coordinate canvas, raggio ≤ 88 &lt; 98 degli spicchi).</summary>
    internal static readonly Rect FacePillRect = new(150, 250, 120, 22);
    const double CenterTextLeft = 135, CenterTextWidth = 150, CenterTextTop = 152;
    Border? facePill;
    readonly DispatcherTimer facePillTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    bool facePillArmed = true;
    bool quickLookReady;
    readonly Action<string>? openQuickLook;
    readonly List<System.Windows.Shapes.Path> sectors = [];
    readonly List<System.Windows.Shapes.Path> outerSectors = [];
    static readonly string[] preferred =
    [
        "DOCX", "PDF", "HTML", "MD", "TXT", "RTF", "ODT", "EPUB", "FB2", "RST",
        "JSON", "YAML", "XML", "CSV",
        "JPG", "PNG", "WEBP", "BMP", "TIFF", "GIF",
        "MP4", "MP3", "WAV", "MKV", "WEBM", "AVI", "MOV", "FLAC", "OGG", "M4A", "AAC"
    ];
    string[] slots = [];
    WheelLayout.Slot[] frontSlots = [];
    WheelLayout.Fan? fan;
    int expanded = -1, selectedChild = -1;
    Rect primaryPixels, workPixels;
    Rect workOnCanvas = new(-300, -300, 1020, 1020);
    double pixelsPerUnit;
    Matrix fromDevice;
    nint windowHandle;
    string[] files = [], formats = [];
    string[] allPayloadFiles = [];
    IReadOnlyList<RoutePlanner.FileExtensionGroup> mixedGroups = [];
    string? activeMixedGroupExt;
    string[] activeJobSourceExts = [];
    string activeJobTargetExt = "";
    string[] sourceExtensions = [];
    string[]? toolLookupPayload;
    IQuickTool[] tools = [];
    bool showingTools, flipping, toolLookupPending;
    BrowserDropCandidate? browserCandidate;
    string[]? previousDragPaths;
    string[] validatedDragFiles = [];
    int scrollDelta;
    readonly Dictionary<string, int> toolPresets = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> toolUnavailableReasons = new(StringComparer.OrdinalIgnoreCase);

    static bool TryGetPreset(string toolId, out (string Key, string[] Values, string[] Labels) preset)
    {
        switch (toolId.ToLowerInvariant())
        {
            case "crop":
                preset = ("ratio", ["1:1", "16:9", "4:3", "3:2", "9:16", "9:16 Blur"], ["1:1", "16:9", "4:3", "3:2", "9:16", "9:16 Blur"]);
                return true;
            case "rotate":
                preset = ("angle", ["90", "180", "270", "flip_h"], ["90°", "180°", "270°", Loc.T("Specchia", "Flip")]);
                return true;
            case "target_size":
                preset = ("target_mb", ["8", "25", "50"], ["8 MB", "25 MB", "50 MB"]);
                return true;
            case "merge_pdf":
                preset = ("page_size", ["original", "a4p", "a4l", "a3p", "a3l"],
                    [Loc.T("Originale", "Original"), "A4 · P", "A4 · L", "A3 · P", "A3 · L"]);
                return true;
            default:
                preset = default;
                return false;
        }
    }

    // ── Sub-Ring a ventaglio sulla faccia STRUMENTI (solo immagini raster, mai in test) ──────────────
    sealed record ToolAction(string Id, string Label, string Description);
    sealed record ToolGroup(string ParentId, string Label, string Title, string DefaultActionId, ToolAction[] Actions);
    const string ResizeGroupId = "resize";

    static ToolGroup? GetToolGroup(string toolId) => toolId.ToLowerInvariant() switch
    {
        "rotate" => new("rotate", Loc.T("Ruota", "Rotate"), Loc.T("Ruota", "Rotate"), BuiltInQuickTools.RotateRightId,
        [
            new(BuiltInQuickTools.RotateLeftId, Loc.T("90° SX", "90° L"), Loc.T("Ruota 90° a sinistra", "Rotate 90° left")),
            new(BuiltInQuickTools.RotateRightId, Loc.T("90° DX", "90° R"), Loc.T("Ruota 90° a destra", "Rotate 90° right")),
            new(BuiltInQuickTools.Rotate180Id, "180°", Loc.T("Ruota 180°", "Rotate 180°")),
            new(BuiltInQuickTools.FlipHorizontalId, Loc.T("Oriz.", "Horiz."), Loc.T("Specchia in orizzontale", "Flip horizontal")),
            new(BuiltInQuickTools.FlipVerticalId, "Vert.", Loc.T("Specchia in verticale", "Flip vertical"))
        ]),
        "crop" => new("crop", Loc.T("Ritaglia", "Crop"), Loc.T("Ritaglia", "Crop"), FlipPreviewWindow.CropInteractiveToolId,
        [
            new(FlipPreviewWindow.CropInteractiveToolId, Loc.T("Manuale", "Manual"), Loc.T("Ritaglio manuale", "Manual crop")),
            new(BuiltInQuickTools.Crop9x16Id, "9:16", Loc.T("Ritaglio 9:16 · Reel", "Crop 9:16 · Reel")),
            new(BuiltInQuickTools.Crop4x5Id, "4:5", Loc.T("Ritaglio 4:5 · Post", "Crop 4:5 · Post")),
            new(BuiltInQuickTools.Crop1x1Id, "1:1", Loc.T("Ritaglio quadrato 1:1", "Square crop 1:1")),
            new(BuiltInQuickTools.Crop16x9Id, "16:9", Loc.T("Ritaglio 16:9 · Video", "Crop 16:9 · Video"))
        ]),
        ResizeGroupId => new(ResizeGroupId, Loc.T("Scala", "Scale"), Loc.T("Scala", "Scale"), BuiltInQuickTools.Resize50Id,
        [
            new(BuiltInQuickTools.Resize50Id, "50%", Loc.T("Scala al 50%", "Scale to 50%")),
            new(BuiltInQuickTools.Resize1080pId, "1080p", Loc.T("Scala a 1080p", "Scale to 1080p")),
            new(BuiltInQuickTools.Compress1MbId, "< 1 MB", Loc.T("Comprimi sotto 1 MB", "Compress under 1 MB"))
        ]),
        _ => null
    };
    bool toolGroupsActive;

    /// <summary>Adattatore IQuickTool per le azioni esplicite <c>tool:*</c>: esegue <see cref="BuiltInQuickTools.ExecuteActionAsync(string, QuickToolContext)"/>.</summary>
    internal sealed class ToolActionQuickTool(string id, string displayName, string actionId) : IQuickTool
    {
        public string Id => id;
        public string DisplayName => displayName;
        public string ActionId => actionId;
        public string IconGlyph => "";
        public bool SupportsMultipleFiles => true;
        public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings) =>
            inputExtensions.Count > 0 && inputExtensions.All(ext => BuiltInQuickTools.RasterExtensions.Contains(ext.TrimStart('.')));
        public Task<QuickToolResult> ExecuteAsync(QuickToolContext context) =>
            string.Equals(actionId, FlipPreviewWindow.CropInteractiveToolId, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(new QuickToolResult(false, [], Loc.T("L'editor di ritaglio manuale si apre dalla ruota.", "Open the manual crop editor from the wheel.")))
                : BuiltInQuickTools.ExecuteActionAsync(actionId, context);
    }

    /// <summary>I gruppi a ventaglio esistono solo fuori dai test, su payload locale di sole immagini raster.</summary>
    bool ToolGroupsAllowed(string[] payload) =>
        !ScreenEdgeAuraWindow.Suppressed && browserCandidate is null && payload.Length > 0 &&
        payload.All(path => BuiltInQuickTools.RasterExtensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.')));

    ToolGroup? ToolGroupFor(int index) =>
        toolGroupsActive && showingTools && browserCandidate is null && (uint)index < (uint)tools.Length
            ? GetToolGroup(tools[index].Id)
            : null;

    IQuickTool[] AugmentTools(IQuickTool[] available, string[] payload)
    {
        if (!ToolGroupsAllowed(payload) || available.Any(t => t.Id.Equals(ResizeGroupId, StringComparison.OrdinalIgnoreCase)))
            return available;
        var list = available.ToList();
        int anchor = list.FindIndex(t => t.Id.Equals("crop", StringComparison.OrdinalIgnoreCase));
        if (anchor < 0) anchor = list.FindIndex(t => t.Id.Equals("rotate", StringComparison.OrdinalIgnoreCase));
        list.Insert(anchor < 0 ? list.Count : anchor + 1, new ToolActionQuickTool(ResizeGroupId, Loc.T("Scala", "Scale"), BuiltInQuickTools.Resize50Id));
        return list.ToArray();
    }

    /// <summary>Spicchio strumenti → IQuickTool reale: figlio del ventaglio, azione predefinita del padre o strumento originale.</summary>
    IQuickTool ResolveTool(SectorHit sector, out bool isGroupAction)
    {
        isGroupAction = false;
        var tool = tools[sector.Primary];
        if (ToolGroupFor(sector.Primary) is not { } group) return tool;
        isGroupAction = true;
        var action = sector.Child >= 0 && sector.Child < group.Actions.Length
            ? group.Actions[sector.Child]
            : group.Actions.FirstOrDefault(a => a.Id == group.DefaultActionId) ?? group.Actions[0];
        return new ToolActionQuickTool(action.Id, action.Description, action.Id);
    }

    /// <summary>
    /// Motivo per cui uno strumento rapido non è utilizzabile con la selezione corrente.
    /// Una ragione non null rende lo spicchio semi-trasparente e, se cliccato, mostra il messaggio.
    /// </summary>
    internal static string? ToolUnavailableReason(string toolId, IReadOnlyList<string> files, FlipSettings settings)
    {
        switch (toolId.ToLowerInvariant())
        {
            case "merge_pdf":
                return files.Count < 2
                    ? Loc.T("Per unire in PDF seleziona almeno due file.", "Select at least two files to merge into PDF.")
                    : null;
            case "target_size":
                if (files.Any(path => VideoToolExtensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.'))) &&
                    CliResolver.Resolve("ffmpeg", settings) == null)
                    return Loc.T("Per comprimere video serve FFmpeg installato o configurato.", "FFmpeg must be installed or configured for video compression.");
                return null;
            case "waveform":
                if (files.Any(path => !System.IO.Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) &&
                    CliResolver.Resolve("ffmpeg", settings) == null)
                    return Loc.T("Per l'onda sonora da formati compressi serve FFmpeg: installalo da Hub › Motori & GPU.",
                        "Waveforms from compressed audio need FFmpeg: install it from Hub › Engines & GPU.");
                return null;
            case "ocr":
                if (files.Any(path => !System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) &&
                    (RoutePlanner.FindQuickTool("ocr") as OcrQuickTool)?.FindTesseract(settings) == null)
                    return Loc.T("Per l'OCR sulle immagini serve Tesseract con la lingua: installalo da Hub › Motori & GPU.",
                        "Image OCR needs Tesseract with language data: install it from Hub › Engines & GPU.");
                return null;
            case "remove_bg":
                return OnnxRemoveBgEngine.IsModelInstalled(settings) ? null : OnnxRemoveBgEngine.MissingModelMessage;
            default:
                return null;
        }
    }

    private static readonly HashSet<string> VideoToolExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "webm", "mov", "avi", "wmv", "flv" };

    /// <summary>Ordina gli strumenti come nelle impostazioni, tenendo ZIP e RAR sempre vicini.</summary>
    internal static IQuickTool[] OrderToolsForWheel(IEnumerable<IQuickTool> found, IReadOnlyList<string> preferredOrder)
    {
        var order = preferredOrder.ToList();
        var list = found.OrderBy(tool =>
        {
            int index = order.FindIndex(id => id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }).ToList();
        int zip = list.FindIndex(tool => tool.Id.Equals("pack_zip", StringComparison.OrdinalIgnoreCase));
        int rar = list.FindIndex(tool => tool.Id.Equals("pack_rar", StringComparison.OrdinalIgnoreCase));
        if (zip >= 0 && rar >= 0 && rar != zip + 1)
        {
            var rarTool = list[rar];
            list.RemoveAt(rar);
            zip = list.FindIndex(tool => tool.Id.Equals("pack_zip", StringComparison.OrdinalIgnoreCase));
            list.Insert(zip + 1, rarTool);
        }
        return list.ToArray();
    }

    int WedgeCount => showingTools ? tools.Length : frontSlots.Length;

    int ChildCount(int index) => showingTools
        ? ToolGroupFor(index)?.Actions.Length ?? 0
        : (uint)index < (uint)frontSlots.Length ? frontSlots[index].Children.Length : 0;
    int selected = -1;
    bool dropping, closing, converting, packOutputIntoZip, payloadHasFolder;
    // Il rilascio del click tenuto premuto che ha aperto la ruota non deve scegliere uno spicchio,
    // qualunque sia la sua durata: si ignora ogni rilascio finché la ruota non vede una nuova pressione.
    bool awaitingFreshPress;
    internal void IgnoreReleaseUntilNextPress() => awaitingFreshPress = true;
    internal bool PackOutputIntoZip
    {
        get => packOutputIntoZip;
        set
        {
            if (packOutputIntoZip == value) return;
            packOutputIntoZip = value;
            if (!showingTools && !converting) UpdateFrontCenter();
        }
    }
    internal string CenterCaptionText => center.Text;
    internal string ToolNoticeText => toolNoticeText.Text;
    internal bool ToolNoticeVisible => toolNotice.Visibility == Visibility.Visible;


    /// <summary>Messaggio visibile dentro la ruota, oltre alla notifica di sistema, quando uno strumento non è utilizzabile.</summary>
    void ShowToolNotice(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        toolNotice.BeginAnimation(UIElement.OpacityProperty, null);
        toolNotice.Opacity = 1;
        toolNoticeText.Text = message;
        toolNotice.Visibility = Visibility.Visible;
        toolNoticeTimer.Stop();
        toolNoticeTimer.Start();
    }
    double displayedPercent, targetPercent;
    string activeFormat = "";
    System.Windows.Shapes.Path? progressArc;
    Line? progressRay;
    Ellipse? progressTip;
    DispatcherTimer? progressTimer;
    readonly Action<string> report;
    readonly Action<string[], string>? convert;
    readonly Action<IQuickTool, QuickToolContext>? runTool;
    readonly Action<BrowserDropCandidate, string, FlipSettings>? convertBrowser;
    readonly Action<BrowserDropCandidate, IQuickTool, QuickToolContext>? runBrowserTool;
    readonly FlipSettings settings;
    readonly Action? playSound;
    readonly Action? cancelConversion;
    internal bool IsDragMode => dropping;
    internal bool IsConverting => converting;
    bool stickyGaze;
    internal bool IsStickyGaze => stickyGaze;
    internal bool IsGazeMode => !dropping && (stickyGaze || settings.GazeAccessibilityMode);
    internal bool IsShowingTools => showingTools;
    internal bool HasGazeBarVisible => gazeBar.Visibility == Visibility.Visible;
    internal IReadOnlyList<string> GazeBarLabels => gazeButtonLabels.Select(label => label.Text).ToArray();
    internal void RequestFlipFace()
    {
        if (CanFlipFace) FlipFace();
    }
    internal void InvokeGazeFlipForTest() => RequestFlipFace();

    readonly DispatcherTimer gazePollTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    enum GazeTargetKind { None, Sector, BarButton }
    readonly record struct GazeTarget(GazeTargetKind Kind, int Primary, int Child)
    {
        public static readonly GazeTarget Empty = new(GazeTargetKind.None, -1, -1);
    }
    GazeTarget activeGazeTarget = GazeTarget.Empty;
    GazeTarget completedGazeSector = GazeTarget.Empty;
    long gazeLastSeenTick;
    long gazeLastUpdateTick;
    double gazeAccumulatedMs;
    const double GazeGraceMs = 120;

    int SelectableCount => showingTools ? tools.Length : files.Length > 0 ? frontSlots.Length : 0;
    readonly record struct SectorHit(int Primary, int Child = -1);

    internal Wheel(Action<string> report, Action<string[], string>? convert = null, Action? playSound = null,
        Action? cancelConversion = null, Action<IQuickTool, QuickToolContext>? runTool = null, FlipSettings? settings = null,
        Action<BrowserDropCandidate, string, FlipSettings>? convertBrowser = null,
        Action<BrowserDropCandidate, IQuickTool, QuickToolContext>? runBrowserTool = null,
        Action<string>? openQuickLook = null)
    {
        this.openQuickLook = openQuickLook;
        this.report = report; this.convert = convert; this.playSound = playSound; this.cancelConversion = cancelConversion;
        this.runTool = runTool; this.convertBrowser = convertBrowser; this.runBrowserTool = runBrowserTool;
        this.settings = (settings ?? new FlipSettings()).CreateSnapshot();
        Title = "Flip";
        Width = Height = 320; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; AllowDrop = true;
        faceTransform.Children.Add(faceScale);
        faceTransform.Children.Add(faceShift);
        canvas.RenderTransform = faceTransform;
        canvas.RenderTransformOrigin = new Point(0.5, 0.5);
        flipSheen = CreateFlipSheen();
        primaryView = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Width = 320, Height = 320 };
        BuildGazeBar();
        BuildMixedGroupBar();
        surface.Children.Add(primaryView);
        surface.Children.Add(outerRing);
        surface.Children.Add(dwellOverlay);
        surface.Children.Add(gazeBar);
        surface.Children.Add(rememberedFanLayer);
        surface.Children.Add(mixedGroupBar);
        toolNotice.Child = toolNoticeText;
        Canvas.SetLeft(toolNotice, ToolNoticeLeft);
        Canvas.SetTop(toolNotice, ToolNoticeTop);
        surface.Children.Add(toolNotice);
        toolNoticeTimer.Tick += (_, _) =>
        {
            toolNoticeTimer.Stop();
            toolNotice.BeginAnimation(UIElement.OpacityProperty, null);
            toolNotice.Opacity = 1;
            toolNotice.Visibility = Visibility.Collapsed;
        };
        Content = surface;
        gazePollTimer.Tick += (_, _) => OnGazePollTick();
        leaveTimer.Tick += (_, _) => TrackLeave();
        facePillTimer.Tick += (_, _) =>
        {
            facePillTimer.Stop();
            if (closing || converting || !dropping || !facePillArmed) return;
            if (CanUseDragCommand(WheelCommand.FlipFace))
            {
                facePillArmed = false;
                HandleDragCommand(WheelCommand.FlipFace);
            }
        };
        PreviewDragEnter += Drag; PreviewDragOver += Drag;
        PreviewDragLeave += (_, _) =>
        {
            previousDragPaths = null;
            TrackLeave();
        };
        PreviewDrop += DropFiles;
        // La rotella passa tra formati e strumenti anche quando la ruota non è in modalità drag
        // (apertura con pressione prolungata, gaze o clic sulla pillola). In drag mode il delta
        // arriva dall'input raw di Program.cs, che continua a gestirlo durante il trascinamento.
        PreviewMouseWheel += (_, e) =>
        {
            if (!IsDragMode)
                HandleDragScroll((short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue));
            e.Handled = true;
        };
        IsKeyboardFocusWithinChanged += (_, e) => keyboardFocused = (bool)e.NewValue;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Dismiss("escape-focused");
            else if (!converting && !flipping && (e.Key is Key.Right or Key.Down || (!dropping && e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)))
            {
                MoveSelection(1);
                e.Handled = true;
            }
            else if (!converting && !flipping && (e.Key is Key.Left or Key.Up || (!dropping && e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)))
            {
                if (e.Key == Key.Up && expanded >= 0)
                {
                    SetExpanded(-1);
                    UpdateFrontCenter();
                }
                else MoveSelection(-1);
                e.Handled = true;
            }
            else if (!dropping && e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.None)
            {
                HandleKeyCommand(WheelCommand.NextPreset);
                e.Handled = true;
            }
            else if (!dropping && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.None)
            {
                HandleKeyCommand(WheelCommand.ToggleZip);
                e.Handled = true;
            }
            else if (!converting && !flipping && e.Key == Key.Space && CanPreviewInGazeBar)
            {
                TriggerQuickLook("keyboard-space");
                e.Handled = true;
            }
            // Come durante il drag: Spazio apre l'anteprima rapida. Invio resta la scelta dello spicchio.
            else if (!dropping && e.Key == Key.Space && CanUseCommand(WheelCommand.QuickLook))
            {
                TriggerQuickLook("keyboard-space");
                e.Handled = true;
            }
            else if (!converting && e.Key is Key.Enter or Key.Space)
            {
                if (selected >= 0) Choose(new(selected, selectedChild), "keyboard");
                e.Handled = true;
            }
        };
        MouseMove += (_, e) => { if (!dropping && !converting) TrackPointer(e.GetPosition(canvas)); };
        PreviewMouseDown += (_, _) => awaitingFreshPress = false;
        MouseLeftButtonUp += (_, e) =>
        {
            if (awaitingFreshPress || dropping || converting) return;
            var point = e.GetPosition(canvas);
            if (IsOverFacePill(point))
            {
                if (CanFlipFace) FlipFace();
                return;
            }
            Choose(Hit(point), "click");
        };
        PreviewMouseRightButtonUp += (_, _) =>
        {
            if (awaitingFreshPress || dropping || converting) return;
            Dismiss("right-click");
        };
        MouseLeave += (_, _) => { if (!dropping) TrackLeave(); };
        Closed += (_, _) => { closing = true; CancelPendingOpenCascade(); gazePollTimer.Stop(); ClearGazeVisual(); progressTimer?.Stop(); leaveTimer.Stop(); facePillTimer.Stop(); toolNoticeTimer.Stop(); };
    }

    internal void Open(string[] sources, bool drag, bool stickyGaze = false)
    {
        imageSizes.Clear();
        previousDragPaths = null;
        this.stickyGaze = stickyGaze;
        openedFromHost = true;
        dropping = drag; converting = false; closing = false; packOutputIntoZip = false; scrollDelta = 0; awaitingFreshPress = false;
        gazeBar.Visibility = Visibility.Collapsed;
        if (!ScreenEdgeAuraWindow.Suppressed && playSound?.Target is WheelSound wheelSound)
            wheelSound.WarmUp();
        SetPayload(sources);
        // Cascata oraria d'ingresso preparata in Paint() e avviata dopo il primo frame renderizzato (mai nei test).
        playOpenCascade = !ScreenEdgeAuraWindow.Suppressed;
        Paint(); Show();
        playOpenCascade = false;
        PlaceOnCursorMonitor(centered: stickyGaze, verticalBiasDip: IsGazeMode ? -46 : 0, forcePlace: true);
        UpdateGazeBarState();
        if (IsGazeMode)
        {
            ReserveFanViewport();
            ResizeViewport(null, forcePlace: true);
            ResetGazeDwell();
            gazePollTimer.Start();
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        ScheduleOpenCascade();
        if (!drag && !stickyGaze) { Activate(); Focus(); }
        Evidence.Log("wheel.open", new { drag, stickyGaze, gaze = IsGazeMode, files, formats });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (closing || !IsVisible) return;
        RealignAfterDpiChange(newDpi);
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!closing && IsVisible) RealignAfterDpiChange(newDpi);
        }, DispatcherPriority.Render);
    }

    internal bool CanUseDragCommand(WheelCommand command) => dropping && CanUseCommand(command);

    /// <summary>Comandi disponibili sulla ruota aperta, sia durante il drag sia senza (pressione prolungata, vassoio).</summary>
    bool CanUseCommand(WheelCommand command)
    {
        if (converting || closing || flipping) return false;
        return command switch
        {
            WheelCommand.FlipFace => settings.EnableFlipWithTabOrScroll && runTool != null,
            WheelCommand.NextPreset => showingTools && selected >= 0 && selected < tools.Length && TryGetPreset(tools[selected].Id, out _) && ToolGroupFor(selected) is null,
            WheelCommand.ToggleZip => !showingTools,
            WheelCommand.QuickLook => quickLookReady && openQuickLook != null && browserCandidate is null && files.Length >= 1,
            _ => false
        };
    }

    internal void TriggerQuickLook(string via)
    {
        if (converting || closing || flipping || openQuickLook == null || files.Length == 0) return;
        string file = files[0];
        if (!File.Exists(file)) return;
        Evidence.Log("wheel.quicklook", new { via, file });
        Dismiss("quicklook");
        var callback = openQuickLook;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => callback(file));
    }

    internal void HandleDragCommand(WheelCommand command)
    {
        if (CanUseDragCommand(command)) ExecuteCommand(command);
    }

    /// <summary>Spazio, A, Z e Tab sulla ruota aperta senza drag (pressione prolungata, vassoio).</summary>
    internal void HandleKeyCommand(WheelCommand command)
    {
        if (!dropping && CanUseCommand(command)) ExecuteCommand(command);
    }

    /// <summary>
    /// Per l'hook di tastiera (altro thread): la ruota senza drag non ha il focus, che è rimasto a Esplora File
    /// dopo la pressione prolungata. Solo in quel caso l'hook gestisce e consuma i tasti della ruota.
    /// </summary>
    internal bool AcceptsHookKeyCommand(WheelCommand command) => !dropping && !keyboardFocused && CanUseCommand(command);
    volatile bool keyboardFocused;

    void ExecuteCommand(WheelCommand command)
    {
        if (command == WheelCommand.QuickLook)
        {
            // Spazio durante il drag: chiude la ruota e apre FLIP QuickLook sul file trascinato.
            TriggerQuickLook("drag-space");
            return;
        }
        if (command == WheelCommand.ToggleZip)
        {
            PackOutputIntoZip = !PackOutputIntoZip;
            playSound?.Invoke();
            return;
        }
        if (command == WheelCommand.NextPreset)
        {
            string id = tools[selected].Id;
            if (TryGetPreset(id, out var preset))
                toolPresets[id] = (toolPresets.GetValueOrDefault(id) + 1) % preset.Values.Length;
            UpdateToolsCenter();
            playSound?.Invoke();
            return;
        }

        FlipFace();
    }

    // Una cartella offre solo gli archivi: la faccia Formati sarebbe vuota.
    bool CanFlipFace => !converting && !closing && !flipping && settings.EnableFlipWithTabOrScroll && runTool != null &&
                        !payloadHasFolder;

    /// <summary>
    /// Gira la ruota di 180° tra Formati e Quick Tools (Tab, rotellina, clic o hover sulla pillola) con un ribaltamento 3D
    /// verticale dall'alto verso il basso: ScaleY 1 → 0 → 1 con leggera compressione prospettica su ScaleX (1 → 0.92 → 1)
    /// e una fascia di riflesso luminoso che scorre sulla ruota. La faccia viene scambiata esattamente a ScaleY = 0.
    /// </summary>
    void FlipFace()
    {
        if (!CanFlipFace) return;
        bool nextShowsTools = !showingTools;
        SetExpanded(-1);
        flipping = true;
        scrollDelta = 0;
        canvas.IsHitTestVisible = false;
        if (nextShowsTools) LoadTools();
        StartFlipSheen();

        var half = TimeSpan.FromMilliseconds(HalfFlipMilliseconds);
        var closeEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var closeFace = new DoubleAnimation(1, 0, half) { EasingFunction = closeEase };
        closeFace.Completed += (_, _) =>
        {
            if (closing) return;
            showingTools = nextShowsTools;
            if (showingTools) LoadTools();
            Paint();
            UpdateGazeBarState();
            ResetGazeDwell();
            var openEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
            var openFace = new DoubleAnimation(0, 1, half) { EasingFunction = openEase };
            openFace.Completed += (_, _) =>
            {
                faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                faceShift.BeginAnimation(TranslateTransform.YProperty, null);
                DetachFlipSheen();
                flipping = false;
                canvas.IsHitTestVisible = true;
                RefreshDragHighlight();
            };
            var cubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, half) { EasingFunction = cubicOut });
            faceShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-9, 0, half) { EasingFunction = cubicOut });
            faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, openFace);
        };
        faceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.92, half) { EasingFunction = closeEase });
        faceShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 9, half) { EasingFunction = closeEase });
        faceScale.BeginAnimation(ScaleTransform.ScaleYProperty, closeFace);
    }

    /// <summary>Fascia di riflesso arancione/bianca ritagliata sul disco della ruota, usata durante il ribaltamento.</summary>
    Canvas CreateFlipSheen()
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, FlipAccent.R, FlipAccent.G, FlipAccent.B), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, FlipAccent.R, FlipAccent.G, FlipAccent.B), 0.34));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, 0xFF, 0xF4, 0xE6), 0.5));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, FlipAccent.R, FlipAccent.G, FlipAccent.B), 0.66));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, FlipAccent.R, FlipAccent.G, FlipAccent.B), 1));
        var band = new Rectangle
        {
            Width = WheelLayout.Size,
            Height = FlipSheenHeight,
            Fill = gradient,
            RenderTransform = sheenShift,
            IsHitTestVisible = false
        };
        var host = new Canvas
        {
            Width = WheelLayout.Size,
            Height = WheelLayout.Size,
            IsHitTestVisible = false,
            Clip = new EllipseGeometry(WheelLayout.Center, 207, 207)
        };
        host.Children.Add(band);
        return host;
    }

    void StartFlipSheen()
    {
        AttachFlipSheen();
        var sweep = new DoubleAnimation(-FlipSheenHeight, WheelLayout.Size, TimeSpan.FromMilliseconds(HalfFlipMilliseconds * 2))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        sheenShift.BeginAnimation(TranslateTransform.YProperty, sweep);
    }

    void AttachFlipSheen()
    {
        if (flipSheen.Parent is Panel parent) parent.Children.Remove(flipSheen);
        canvas.Children.Add(flipSheen);
    }

    void DetachFlipSheen()
    {
        sheenShift.BeginAnimation(TranslateTransform.YProperty, null);
        sheenShift.Y = -FlipSheenHeight;
        if (flipSheen.Parent is Panel parent) parent.Children.Remove(flipSheen);
    }

    internal void HandleDragScroll(short delta)
    {
        if (!CanFlipFace) return;
        scrollDelta += delta;
        if (Math.Abs(scrollDelta) >= 120)
            RequestFlipFace();
    }

    void SetPayload(string[] incoming)
    {
        allPayloadFiles = incoming;
        activeMixedGroupExt = null;
        // Con una cartella si offrono solo gli archivi: niente suddivisione per tipo di file.
        mixedGroups = ContainsDirectory(incoming) ? [] : RoutePlanner.GroupFilesByExtension(incoming);
        ApplyPayloadSubset(incoming);
    }

    internal void ApplyMixedGroupFilter(string? extKey)
    {
        if (allPayloadFiles.Length == 0) return;
        activeMixedGroupExt = string.IsNullOrWhiteSpace(extKey) ? null : extKey;
        string[] filtered = activeMixedGroupExt == null
            ? allPayloadFiles
            : mixedGroups.FirstOrDefault(g => string.Equals(g.Extension, activeMixedGroupExt, StringComparison.OrdinalIgnoreCase))?.Files
              ?? allPayloadFiles;
        ApplyPayloadSubset(filtered);
        Paint();
        RefreshDragHighlight();
    }

    internal IReadOnlyList<string> MixedGroupLabelsForTest =>
        mixedGroupPills.Select(p => (p.Pill.Child as TextBlock)?.Text ?? "").ToArray();

    void ApplyPayloadSubset(string[] activeFiles)
    {
        files = activeFiles;
        resizeSource = null;
        quickLookReady = false;
        try
        {
            quickLookReady = openQuickLook != null && activeFiles.Length >= 1 && File.Exists(activeFiles[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Evidence.Log("wheel.quicklook.lookup.error", ex.Message);
        }
        var extensions = GetSourceExtensions(activeFiles);
        bool hasFolder = ContainsDirectory(activeFiles);
        payloadHasFolder = hasFolder;
        if (!sourceExtensions.SequenceEqual(extensions) || hasFolder)
        {
            sourceExtensions = extensions;
            routeCaptionCache.Clear();
            formats = hasFolder
                ? []
                : RoutePlanner.PlanWheelFormats(extensions, settings, out primaryRoutes)
                    .Select(format => format.Extension.ToUpperInvariant()).ToArray();
        }
        if (hasFolder) showingTools = true;
        toolLookupPayload = null;
        toolLookupPending = false;
        tools = [];
        RefreshMixedGroupBar();
        UpdateGazeBarState();
        if (showingTools) LoadTools();
    }

    void BuildMixedGroupBar()
    {
        mixedGroupBar.Background = new SolidColorBrush(Color.FromArgb(242, 0x12, 0x13, 0x18));
        mixedGroupBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x39));
        mixedGroupBar.BorderThickness = new Thickness(1);
        mixedGroupBar.CornerRadius = new CornerRadius(14);
        mixedGroupBar.Padding = new Thickness(6, 5, 6, 5);
        mixedGroupBar.MaxWidth = 520;
        mixedGroupBar.Child = mixedGroupPanel;
    }

    void RefreshMixedGroupBar()
    {
        mixedGroupPanel.Children.Clear();
        mixedGroupPills.Clear();
        bool show = !converting && !showingTools && allPayloadFiles.Length > 1 &&
            mixedGroups.Count >= 2 && (formats.Length == 0 || activeMixedGroupExt != null);
        if (!show)
        {
            mixedGroupBar.Visibility = Visibility.Collapsed;
            return;
        }

        void AddGroupPill(string text, string? extKey, bool selected)
        {
            var pill = new Border
            {
                Background = new SolidColorBrush(selected ? Color.FromRgb(0x2B, 0x1D, 0x14) : Color.FromRgb(0x1E, 0x21, 0x2B)),
                BorderBrush = new SolidColorBrush(selected ? FlipAccent : Color.FromRgb(0x2E, 0x33, 0x42)),
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11.5,
                    FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold,
                    Foreground = Brushes.White
                }
            };
            pill.PreviewMouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (converting || closing) return;
                ApplyMixedGroupFilter(extKey);
                playSound?.Invoke();
            };
            mixedGroupPills.Add((pill, extKey));
            mixedGroupPanel.Children.Add(pill);
        }

        AddGroupPill(Loc.F("Tutti ({0})", "All ({0})", allPayloadFiles.Length), null, activeMixedGroupExt == null);
        foreach (var g in mixedGroups)
        {
            bool selected = string.Equals(activeMixedGroupExt, g.Extension, StringComparison.OrdinalIgnoreCase);
            AddGroupPill(g.Label, g.Extension, selected);
        }
        mixedGroupBar.Visibility = Visibility.Visible;
    }

    static string[] GetSourceExtensions(string[] paths) => paths.Select(path => System.IO.Path.GetExtension(path).ToLowerInvariant())
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Le cartelle non hanno conversioni: vengono offerti solo gli archivi (ZIP e, se disponibile, RAR).</summary>
    static bool ContainsDirectory(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            try { if (Directory.Exists(path)) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return false;
    }

    async void LoadTools()
    {
        var payload = files;
        if (payload.Length == 0 || ReferenceEquals(toolLookupPayload, payload)) return;
        toolLookupPayload = payload;
        toolLookupPending = true;
        try
        {
            bool hasFolder = ContainsDirectory(payload);
            bool readSize = !hasFolder && ToolGroupsAllowed(payload);
            var (available, size, reasons) = await Task.Run(() =>
            {
                // Keep extensionless files in the intersection. ZIP can handle them, raster tools cannot.
                string[] extensions = payload.Select(path =>
                    System.IO.Path.GetExtension(path) is { Length: > 0 } extension ? extension : "*").ToArray();
                var ordered = settings.EnabledBackSideTools;
                // Le cartelle rispettano gli strumenti scelti dall'utente: se ZIP è disattivato resta solo RAR.
                var lookupSettings = settings;
                var size = readSize ? imageSizes.GetOrAdd(payload[0], static path => new(() => ReadOrientedSize(path))).Value : null;
                // Onda sonora e OCR senza il loro motore: restano visibili, sbiaditi, con il motivo al clic.
                var available = RoutePlanner.GetAvailableQuickTools(extensions, lookupSettings).ToList();
                foreach (var dimmed in new IQuickTool?[] { RoutePlanner.FindQuickTool("waveform"), RoutePlanner.FindQuickTool("ocr") })
                {
                    bool supported = dimmed switch
                    {
                        WaveformQuickTool wave => wave.SupportsWithoutEngine(extensions, lookupSettings),
                        OcrQuickTool ocr => ocr.SupportsWithoutEngine(extensions, lookupSettings),
                        _ => false
                    };
                    if (supported && !available.Any(tool => tool.Id == dimmed!.Id)) available.Add(dimmed!);
                }
                var found = OrderToolsForWheel(available, ordered);
                if (hasFolder) found = found.Where(t => t.Id is "pack_zip" or "pack_rar").ToArray();
                var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tool in found)
                {
                    string? reason = ToolUnavailableReason(tool.Id, payload, lookupSettings);
                    if (reason != null) reasons[tool.Id] = reason;
                }
                return (found, size, reasons);
            }).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (closing || converting || !ReferenceEquals(files, payload)) return;
                toolLookupPending = false;
                resizeSource = size;
                toolUnavailableReasons.Clear();
                foreach (var reason in reasons) toolUnavailableReasons[reason.Key] = reason.Value;
                tools = hasFolder ? available : AugmentTools(available, payload);
                UpdateGazeBarState();
                if (showingTools)
                {
                    Paint();
                    RefreshDragHighlight();
                }
            });
        }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted)
        {
            // The wheel's dispatcher no longer accepts UI updates.
        }
        catch (Exception ex)
        {
            Evidence.Log("quick-tools.lookup.error", ex.Message);
            if (!Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (closing || !ReferenceEquals(files, payload)) return;
                    toolLookupPending = false;
                    if (showingTools) UpdateToolsCenter();
                });
            }
        }
    }

    void RefreshDragHighlight()
    {
        if (!closing && !flipping && dropping && IsVisible && Native.GetCursorPos(out var cursor))
            TrackPointer(canvas.PointFromScreen(new Point(cursor.X, cursor.Y)));
    }

    string ToolLabel(string id) => id switch
    {
        "crop" => "Crop", "rotate" => Loc.T("Ruota", "Rotate"), "merge_pdf" => Loc.T("Unisci PDF", "Merge PDF"), "pack_zip" => "ZIP", "pack_rar" => "RAR",
        "target_size" => "Target", "remove_bg" => Loc.T("Sfondo", "Bg"), "ocr" => "OCR",
        "palette" => "Palette", "clean_meta" => "Privacy", "waveform" => Loc.T("Onda", "Wave"),
        "turntable_3d" => "360°",
        _ => tools.First(t => t.Id == id).DisplayName
    };

    string ActiveFileHeader()
    {
        if (files.Length == 0) return "";
        string first = System.IO.Path.GetFileName(files[0]);
        if (first.Length > 24) first = first[..21] + "…";
        return files.Length == 1 ? first : Loc.F("{0} file ({1})", "{0} files ({1})", files.Length, first);
    }

    static (int W, int H)? ReadOrientedSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec != null) return (int)codec.EncodedOrigin >= 5
                ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
            using var fallback = File.OpenRead(path);
            var frame = BitmapDecoder.Create(fallback, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException or COMException)
        { return null; }
    }

    void ShowCropOutline(string? action)
    {
        double ratio = action switch
        {
            BuiltInQuickTools.Crop1x1Id => 1, BuiltInQuickTools.Crop16x9Id => 16d / 9,
            BuiltInQuickTools.Crop4x5Id => 4d / 5, BuiltInQuickTools.Crop9x16Id => 9d / 16, _ => 0
        };
        cropOutline.Visibility = ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetTop(center, ratio > 0 ? 178 : CenterTextTop);
        center.MaxHeight = ratio > 0 ? 68 : 92;
        if (ratio <= 0) return;
        cropOutline.Width = Math.Min(30, 20 * ratio);
        cropOutline.Height = cropOutline.Width / ratio;
        Canvas.SetLeft(cropOutline, 210 - cropOutline.Width / 2);
        Canvas.SetTop(cropOutline, 158 - cropOutline.Height / 2);
    }

    void UpdateToolsCenter()
    {
        ShowCropOutline(null);
        string fileHeader = ActiveFileHeader();
        string prefix = fileHeader.Length > 0 ? fileHeader + "\n" : "";
        if (ToolGroupFor(selected) is { } group)
        {
            // Centro asciutto: file, azione sotto il puntatore (o predefinita del padre) e, se utile, dimensioni finali.
            var action = selectedChild >= 0 && selectedChild < group.Actions.Length
                ? group.Actions[selectedChild]
                : group.Actions.FirstOrDefault(a => a.Id == group.DefaultActionId) ?? group.Actions[0];
            if (selectedChild >= 0) ShowCropOutline(action.Id);
            string detail = "";
            if (resizeSource is { } src && action.Id is BuiltInQuickTools.Resize50Id or BuiltInQuickTools.Resize1080pId)
            {
                var dst = BuiltInQuickTools.ComputeResizeDimensions(src.W, src.H, action.Id);
                detail = dst == src ? Loc.T("\nGià entro il limite", "\nAlready within limit") : $"\n{src.W}×{src.H} → {dst.Width}×{dst.Height}";
            }
            center.Text = $"{prefix}{action.Description}{detail}";
            return;
        }
        if (selected >= 0 && selected < tools.Length)
        {
            var tool = tools[selected];
            string preset = TryGetPreset(tool.Id, out var options)
                ? $"\n{options.Labels[toolPresets.GetValueOrDefault(tool.Id)]} · A"
                : "";
            center.Text = $"{prefix}{tool.DisplayName}{preset}";
            return;
        }
        center.Text = files.Length == 0 ? Loc.T("Trascina qui un file", "Drop a file here") :
            toolLookupPending ? prefix + Loc.T("Verifico gli strumenti…", "Checking tools…") :
            tools.Length == 0 ? prefix + Loc.T("Nessuno strumento", "No tools available") : prefix + Loc.T("Scegli uno strumento", "Choose a tool");
    }

    void UpdateFrontCenter()
    {
        ShowCropOutline(null);
        string zipBadge = packOutputIntoZip ? Loc.T("\n+ ARCHIVIO .ZIP", "\n+ .ZIP ARCHIVE") : "";
        string fileHeader = ActiveFileHeader();
        string prefix = fileHeader.Length > 0 ? fileHeader + "\n" : "";
        if (expanded >= 0)
        {
            var group = frontSlots[expanded];
            center.Text = prefix + (selectedChild >= 0
                ? TargetCaption(group.Children[selectedChild])
                : group.Label) + zipBadge;
            return;
        }
        if (selected >= 0)
        {
            if (IsParent(selected))
            {
                center.Text = prefix + frontSlots[selected].Label + zipBadge;
                return;
            }
            if ((uint)selected < (uint)frontSlots.Length && frontSlots[selected].Format is { } fmt)
            {
                center.Text = prefix + TargetCaption(fmt) + zipBadge;
                return;
            }
        }
        center.Text = files.Length == 0
            ? Loc.T("Trascina qui un file", "Drop a file here") + zipBadge
            : formats.Length == 0
                ? (mixedGroups.Count >= 2
                    ? Loc.F("{0} file misti\n", "{0} mixed files\n", allPayloadFiles.Length) + string.Join(" · ", mixedGroups.Take(3).Select(g => g.Label))
                    : prefix + Loc.T("Nessun formato comune", "No shared format"))
                : fileHeader + zipBadge;
    }

    /// <summary>Riga formato + rotta: "WEBP · WebP Image" e sotto "PNG → WEBP"; senza rotta nota diventa "→ WEBP".</summary>
    string TargetCaption(string extension)
    {
        string route = RouteCaption(extension);
        string caption = FormatCenterCaption(extension);
        return route.Length > 0 ? caption + route : "→ " + caption;
    }

    /// <summary>
    /// "La ruota mostra la strada": mostra sotto il nome del formato i passaggi reali calcolati da RoutePlanner.TryPlanRoute
    /// (es. "DOCX → PDF → PNG", "WAV → TXT · Whisper AI" oppure "PDF → PDF/A"). Cache per sessione di ruota.
    /// </summary>
    internal string RouteCaption(string target)
    {
        if (sourceExtensions.Length < 1 || string.IsNullOrWhiteSpace(target)) return "";
        string source = sourceExtensions[0].TrimStart('.');
        if (source.Length == 0) return "";
        string key = source + "|" + target;
        if (routeCaptionCache.TryGetValue(key, out var cached)) return cached;

        string caption = "";
        if (primaryRoutes.TryGetValue(target.Trim().TrimStart('.').ToLowerInvariant(), out var route) && route is { Hops.Count: > 0 })
        {
            string chain = FormatRegistry.GetWheelShortLabel(source) +
                string.Concat(route.Hops.Select(hop => " → " + FormatRegistry.GetWheelShortLabel(hop.Edge.TargetExtension)));

            if (route.Hops.Count == 1 && route.Hops[0].Engine is { } engine &&
                specialEngineKeys.Any(id => engine.Id.Contains(id, StringComparison.OrdinalIgnoreCase)))
            {
                string suffix = engine.Id.Equals("whisper", StringComparison.OrdinalIgnoreCase)
                    ? " · Whisper AI"
                    : $" · via {engine.DisplayName}";
                caption = $"\n{chain}{suffix}";
            }
            else
            {
                caption = $"\n{chain}";
            }
        }
        routeCaptionCache[key] = caption;
        return caption;
    }

    static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    static Vector Radial(double degrees)
    {
        double radians = (degrees - 90) * Math.PI / 180;
        return new Vector(Math.Cos(radians), Math.Sin(radians));
    }

    /// <summary>Ingresso "uno alla volta": dissolvenza + traslazione radiale da -distance a 0 con sfasamento.</summary>
    static void PlayRadialEntrance(UIElement element, Vector direction, double distance, double targetOpacity,
        TimeSpan delay, TimeSpan fadeDuration, TimeSpan moveDuration, IEasingFunction fadeEase, IEasingFunction moveEase)
    {
        var shift = new TranslateTransform(-direction.X * distance, -direction.Y * distance);
        element.RenderTransform = shift;
        element.Opacity = 0;
        var fade = new DoubleAnimation(targetOpacity, fadeDuration) { BeginTime = delay, EasingFunction = fadeEase };
        fade.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = targetOpacity;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        var moveX = new DoubleAnimation(0, moveDuration) { BeginTime = delay, EasingFunction = moveEase };
        var moveY = new DoubleAnimation(0, moveDuration) { BeginTime = delay, EasingFunction = moveEase };
        moveY.Completed += (_, _) =>
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.X = 0;
            shift.Y = 0;
        };
        shift.BeginAnimation(TranslateTransform.XProperty, moveX);
        shift.BeginAnimation(TranslateTransform.YProperty, moveY);
    }

    static string FormatCenterCaption(string extension)
    {
        string shortSigil = Flip.Core.FormatRegistry.GetWheelShortLabel(extension);
        var def = Flip.Core.FormatRegistry.Find(extension);
        return def is { DisplayName.Length: > 0 and <= 18 } d &&
            !string.Equals(d.DisplayName, shortSigil, StringComparison.OrdinalIgnoreCase)
            ? $"{shortSigil} · {d.DisplayName}"
            : shortSigil;
    }

    void CancelPendingOpenCascade()
    {
        openCascadeToken++;
        if (openCascadeRenderHandler != null)
        {
            CompositionTarget.Rendering -= openCascadeRenderHandler;
            openCascadeRenderHandler = null;
        }
        primaryCascadeWedges.Clear();
    }

    void ScheduleOpenCascade()
    {
        if (ScreenEdgeAuraWindow.Suppressed || primaryCascadeWedges.Count == 0 || closing || converting)
            return;

        int token = ++openCascadeToken;
        if (openCascadeRenderHandler != null)
        {
            CompositionTarget.Rendering -= openCascadeRenderHandler;
            openCascadeRenderHandler = null;
        }

        // Attende Loaded e il primo frame compositato prima di avviare la cascata oraria sugli spicchi primari.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (token != openCascadeToken || closing || converting || !IsVisible || primaryCascadeWedges.Count == 0)
                return;

            EventHandler? handler = null;
            handler = (_, _) =>
            {
                if (handler != null)
                {
                    CompositionTarget.Rendering -= handler;
                    if (ReferenceEquals(openCascadeRenderHandler, handler))
                        openCascadeRenderHandler = null;
                }
                if (token != openCascadeToken || closing || converting || !IsVisible)
                    return;
                StartPrimaryOpenCascade(token);
            };
            openCascadeRenderHandler = handler;
            CompositionTarget.Rendering += handler;
        });
    }

    void StartPrimaryOpenCascade(int token)
    {
        var duration = TimeSpan.FromMilliseconds(OpenCascadeDurationMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < primaryCascadeWedges.Count; i++)
        {
            var wedge = primaryCascadeWedges[i];
            if (i == selected)
            {
                RevealPrimaryWedgeImmediately(i);
                continue;
            }

            var delay = TimeSpan.FromMilliseconds(i * OpenCascadeStepMs);
            AnimatePrimaryCascadeElement(wedge.Sector, wedge.SectorScale, wedge.SectorOpacity, delay, duration, ease, token);
            AnimatePrimaryCascadeElement(wedge.Label, wedge.LabelScale, wedge.LabelOpacity, delay, duration, ease, token);
            if (wedge.Marker != null && wedge.MarkerScale != null)
            {
                AnimatePrimaryCascadeElement(wedge.Marker, wedge.MarkerScale, 1.0, delay, duration, ease, token);
            }
        }
    }

    void AnimatePrimaryCascadeElement(UIElement element, ScaleTransform scale, double targetOpacity,
        TimeSpan delay, TimeSpan duration, IEasingFunction ease, int token)
    {
        var fade = new DoubleAnimation(0, targetOpacity, duration) { BeginTime = delay, EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (token != openCascadeToken) return;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = targetOpacity;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        var scaleX = new DoubleAnimation(OpenCascadeInitialScale, 1.0, duration) { BeginTime = delay, EasingFunction = ease };
        var scaleY = new DoubleAnimation(OpenCascadeInitialScale, 1.0, duration) { BeginTime = delay, EasingFunction = ease };
        scaleY.Completed += (_, _) =>
        {
            if (token != openCascadeToken) return;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    void RevealPrimaryWedgeImmediately(int index)
    {
        if (index < 0 || index >= primaryCascadeWedges.Count) return;
        var wedge = primaryCascadeWedges[index];
        SnapCascadeElement(wedge.Sector, wedge.SectorScale, wedge.SectorOpacity);
        SnapCascadeElement(wedge.Label, wedge.LabelScale, wedge.LabelOpacity);
        if (wedge.Marker != null && wedge.MarkerScale != null)
        {
            SnapCascadeElement(wedge.Marker, wedge.MarkerScale, 1.0);
        }
    }

    void RevealAllPrimaryWedgesImmediately()
    {
        for (int i = 0; i < primaryCascadeWedges.Count; i++)
            RevealPrimaryWedgeImmediately(i);
    }

    static void SnapCascadeElement(UIElement element, ScaleTransform scale, double targetOpacity)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = targetOpacity;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1.0;
        scale.ScaleY = 1.0;
    }

    void Paint()
    {
        CancelPendingOpenCascade();
        SetExpanded(-1);
        canvas.Children.Clear(); sectors.Clear(); selected = -1; selectedChild = -1;
        bool prepareOpenCascade = playOpenCascade && !ScreenEdgeAuraWindow.Suppressed;
        toolGroupsActive = showingTools && ToolGroupsAllowed(files);
        frontSlots = showingTools ? [] : WheelLayout.Partition(formats);
        slots = showingTools ? tools.Select(tool => tool.Id).ToArray() :
            files.Length > 0 ? frontSlots.Select(slot => slot.Label).ToArray() : ["DOCX", "WEBP", "PDF", "MD", "HTML", "EPUB", "JPG", "PNG"];
        var disk = new Ellipse
        {
            Width = 414,
            Height = 414,
            Fill = new SolidColorBrush(Color.FromArgb(214, 24, 26, 28)),
            Stroke = new SolidColorBrush(Color.FromArgb(145, 128, 131, 124)),
            StrokeThickness = 1.4
        };
        Canvas.SetLeft(disk, 3); Canvas.SetTop(disk, 3); canvas.Children.Add(disk);
        string? rememberedTarget = GetRememberedTargetForCurrentPayload();
        // Unknown payload cannot honestly expose compatible formats before OLE DragEnter.
        int n = slots.Length;
        for (int i = 0; i < n; i++)
        {
            double span = 360.0 / n, mid = i * span;
            var radial = Radial(mid);
            var path = new System.Windows.Shapes.Path
            {
                Data = SectorData(i, n),
                Fill = RestBrush,
                Stroke = SectorStroke,
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round,
                Tag = radial
            };
            bool enabled = i < SelectableCount;
            bool toolReady = !showingTools || !toolUnavailableReasons.ContainsKey(slots[i]);
            path.Opacity = enabled ? (toolReady ? 1 : .34) : .38;
            sectors.Add(path); canvas.Children.Add(path);
            System.Windows.Shapes.Path? marker = null;
            if (!showingTools && enabled && rememberedTarget != null && SlotMatchesRememberedTarget(i, rememberedTarget))
            {
                double halfSpan = Math.Min(11.0, (360.0 / n) * 0.24);
                marker = new System.Windows.Shapes.Path
                {
                    Data = RememberedArcData(mid - halfSpan, mid + halfSpan, 189.0),
                    Stroke = RememberedTargetBrush,
                    StrokeThickness = 3.2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                    Effect = new DropShadowEffect { Color = RememberedTargetColor, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.85 }
                };
                canvas.Children.Add(marker);
            }
            var pos = WheelLayout.At(PrimaryLabelRadius, mid);
            var toolGroup = ToolGroupFor(i);
            string caption = toolGroup is { } grouped ? grouped.Label + " ›" : showingTools ? ToolLabel(slots[i]) : slots[i];
            double baseSize = toolGroup is not null ? 16.0
                : !showingTools && caption.StartsWith('+') ? 13.0
                : !showingTools && caption.Length >= 5 ? 15.5 : 18.0;
            // Spicchio primario: corona 98…185. L'etichetta (orizzontale) deve stare nel riquadro ruotato con margine.
            double labelHalfSpan = (span / 2 - Math.Min(2, span * .06)) * Math.PI / 180;
            double tangential = 2 * (PrimaryLabelRadius - 22) * Math.Sin(labelHalfSpan) - 10;
            double fontSize = FitLabelFontSize(caption, baseSize, mid, tangential, PrimaryLabelDepth, 9);
            var label = new TextBlock
            {
                Text = caption,
                Opacity = enabled ? (toolReady ? 1 : .28) : .32,
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var labelSize = label.DesiredSize;
            double labelLeft = pos.X - labelSize.Width / 2;
            double labelTop = pos.Y - labelSize.Height / 2;
            Canvas.SetLeft(label, labelLeft); Canvas.SetTop(label, labelTop); canvas.Children.Add(label);
            if (prepareOpenCascade)
            {
                // Inizializza gli spicchi a scala 0.88 e opacità 0; la cascata oraria partirà al primo frame renderizzato.
                double sectorOpacity = path.Opacity;
                double labelOpacity = label.Opacity;
                var sectorScale = new ScaleTransform(OpenCascadeInitialScale, OpenCascadeInitialScale, WheelLayout.Center.X, WheelLayout.Center.Y);
                var sectorTransform = new TransformGroup();
                sectorTransform.Children.Add(sectorScale);
                sectorTransform.Children.Add(new TranslateTransform());
                path.RenderTransform = sectorTransform;
                path.Opacity = 0;

                var labelScale = new ScaleTransform(OpenCascadeInitialScale, OpenCascadeInitialScale, WheelLayout.Center.X - labelLeft, WheelLayout.Center.Y - labelTop);
                label.RenderTransform = labelScale;
                label.Opacity = 0;

                ScaleTransform? markerScale = null;
                if (marker != null)
                {
                    markerScale = new ScaleTransform(OpenCascadeInitialScale, OpenCascadeInitialScale, WheelLayout.Center.X, WheelLayout.Center.Y);
                    marker.RenderTransform = markerScale;
                    marker.Opacity = 0;
                }

                primaryCascadeWedges.Add(new PrimaryCascadeWedge(path, label, marker, sectorOpacity, labelOpacity, sectorScale, labelScale, markerScale));
            }
        }
        playOpenCascade = false;
        if (showingTools) UpdateToolsCenter();
        else UpdateFrontCenter();
        center.ToolTip = files.Length > 0 ? string.Join("\n", files) : Loc.T("Avvia un trascinamento di file locali", "Drag local files to convert");
        Canvas.SetLeft(center, CenterTextLeft); canvas.Children.Add(center);
        canvas.Children.Add(cropOutline);
        PaintFacePill();
        if (flipping) AttachFlipSheen();
        // Nuovo payload: riserva subito lo spazio dei sub-ring e garantisce il posizionamento sul monitor del cursore.
        if ((!showingTools || toolGroupsActive) && pixelsPerUnit > 0)
        {
            if (openedFromHost && HasCursorMonitorChanged())
            {
                PlaceOnCursorMonitor(centered: stickyGaze, verticalBiasDip: IsGazeMode ? -46 : 0, forcePlace: true);
            }
            else if (ReserveFanViewport())
            {
                ResizeViewport(null, forcePlace: openedFromHost);
            }
        }
    }

    /// <summary>
    /// Calcola una volta l'area (in pixel) che può servire ai sub-ring dei padri "+ CATEGORIA". La finestra viene
    /// dimensionata subito su quest'area: aprire/chiudere un sub-ring non sposta più la ruota (niente sbalzo).
    /// Restituisce true se l'area riservata è cambiata.
    /// </summary>
    bool ReserveFanViewport()
    {
        Rect next = Rect.Empty;
        if (!ScreenEdgeAuraWindow.Suppressed && pixelsPerUnit > 0 && (!showingTools || toolGroupsActive))
        {
            double outer = 0;
            int wedges = WedgeCount;
            for (int i = 0; i < wedges; i++)
            {
                int children = ChildCount(i);
                if (children == 0) continue;
                var candidate = WheelLayout.FitFan(i, wedges, children, workOnCanvas);
                if (candidate != null) outer = Math.Max(outer, candidate.OuterRadius);
            }
            if (outer > 0)
            {
                double radius = (outer + 4) * pixelsPerUnit;
                Point anchor = new(primaryPixels.Left + primaryPixels.Width / 2, primaryPixels.Top + primaryPixels.Height / 2);
                next = new Rect(anchor.X - radius, anchor.Y - radius, 2 * radius, 2 * radius);
                next.Intersect(workPixels);
            }
        }
        if (next == reservedPixels) return false;
        reservedPixels = next;
        return true;
    }

    void PaintFacePill()
    {
        facePill = null;
        if (runTool == null || !settings.EnableFlipWithTabOrScroll) return;
        var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x18));
        var idle = new SolidColorBrush(Color.FromArgb(150, 0xF3, 0xF4, 0xF6));
        var title = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        title.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Formati", "Formats")) { Foreground = showingTools ? idle : accent });
        title.Inlines.Add(new System.Windows.Documents.Run("  ·  ") { Foreground = idle });
        title.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Strumenti", "Tools")) { Foreground = showingTools ? accent : idle });
        facePill = new Border
        {
            Width = FacePillRect.Width,
            Height = FacePillRect.Height,
            CornerRadius = new CornerRadius(FacePillRect.Height / 2),
            Background = new SolidColorBrush(Color.FromArgb(235, 0x12, 0x13, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 0xFF, 0x7A, 0x18)),
            BorderThickness = new Thickness(1),
            Child = title,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(facePill, FacePillRect.Left);
        Canvas.SetTop(facePill, FacePillRect.Top);
        canvas.Children.Add(facePill);
    }

    const double PrimaryLabelRadius = 141.5, PrimaryLabelDepth = 185 - 98 - 16;

    /// <summary>
    /// Riduce il corpo di un'etichetta orizzontale finché il suo riquadro, proiettato sugli assi radiale e tangenziale
    /// dello spicchio all'angolo <paramref name="angleDeg"/>, sta dentro <paramref name="tangential"/> × <paramref name="radial"/>.
    /// </summary>
    static double FitLabelFontSize(string text, double baseSize, double angleDeg, double tangential, double radial, double minSize)
    {
        if (string.IsNullOrEmpty(text) || tangential <= 0 || radial <= 0) return baseSize;
        var probe = new TextBlock { Text = text, FontSize = baseSize, FontWeight = FontWeights.Bold };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = probe.DesiredSize.Width, h = probe.DesiredSize.Height * 0.78;
        double radians = angleDeg * Math.PI / 180, s = Math.Abs(Math.Sin(radians)), c = Math.Abs(Math.Cos(radians));
        double radialExtent = w * s + h * c, tangentialExtent = w * c + h * s;
        double scale = Math.Min(1, Math.Min(radial / Math.Max(1, radialExtent), tangential / Math.Max(1, tangentialExtent)));
        return Math.Max(minSize, Math.Floor(baseSize * scale * 2) / 2);
    }

    bool IsOverFacePill(Point point) =>
        facePill is { Visibility: Visibility.Visible } && !converting && FacePillRect.Contains(point);

    void UpdateFacePillHover(Point point)
    {
        bool over = IsOverFacePill(point);
        if (facePill is { } pill)
        {
            pill.Background = new SolidColorBrush(over ? Color.FromArgb(245, 0x2A, 0x2E, 0x39) : Color.FromArgb(235, 0x12, 0x13, 0x18));
            pill.BorderThickness = new Thickness(over ? 1.6 : 1);
        }
        if (!over)
        {
            facePillTimer.Stop();
            facePillArmed = true;
            return;
        }
        // Durante il drag il clic non è possibile: basta sostare sulla pillola per girare la ruota.
        if (dropping && facePillArmed && !facePillTimer.IsEnabled) facePillTimer.Start();
    }

    bool HasCursorMonitorChanged()
    {
        if (activeMonitor == 0) return false;
        Native.GetCursorPos(out var cursor);
        nint monitor = Native.MonitorFromPoint(cursor, 2);
        return monitor != 0 && monitor != activeMonitor;
    }

    double? ResolveCursorMonitorScale(nint targetMonitor)
    {
        if (targetMonitor != 0 && targetMonitor == Native.MonitorFromWindow(windowHandle, 2) &&
            PresentationSource.FromVisual(this)?.CompositionTarget is { } target &&
            target.TransformToDevice.M11 > 0)
        {
            return target.TransformToDevice.Transform(new Vector(PrimaryDipSize, PrimaryDipSize)).X / PrimaryDipSize;
        }
        return Native.GetMonitorDpi(targetMonitor, windowHandle) / 96.0;
    }

    void RealignAfterDpiChange(DpiScale newDpi)
    {
        if (aligningDpi || closing || !IsVisible) return;
        aligningDpi = true;
        try
        {
            windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle == 0) return;

            var source = PresentationSource.FromVisual(this);
            Matrix toDevice = source?.CompositionTarget?.TransformToDevice
                ?? new Matrix(newDpi.DpiScaleX, 0, 0, newDpi.DpiScaleY, 0, 0);
            double convertedPx = toDevice.Transform(new Vector(PrimaryDipSize, PrimaryDipSize)).X;
            double physicalScale = convertedPx > 0 ? convertedPx / PrimaryDipSize : newDpi.DpiScaleX;
            PlaceOnCursorMonitor(centered: stickyGaze, verticalBiasDip: IsGazeMode ? -46 : 0, forcePlace: true, physicalScale: physicalScale);
        }
        finally
        {
            aligningDpi = false;
        }
    }

    void PlaceOnCursorMonitor(bool centered, int verticalBiasDip, bool forcePlace, double? physicalScale = null)
    {
        windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == 0) return;

        Native.GetCursorPos(out var cursor);
        nint cursorMonitor = Native.MonitorFromPoint(cursor, 2);
        double? scale = physicalScale ?? ResolveCursorMonitorScale(cursorMonitor);
        var placed = Native.PlaceNearCursor(
            windowHandle,
            centered: centered,
            verticalBiasDip: verticalBiasDip,
            dipSize: PrimaryDipSize,
            physicalScale: scale);
        ApplyPlacementMetrics(placed);
        ReserveFanViewport();
        ResizeViewport(fan, forcePlace: forcePlace);
    }

    void ApplyPlacementMetrics(Native.PlacementResult placed)
    {
        lastWindowRect = default;
        reservedPixels = Rect.Empty;
        activeMonitor = placed.Monitor;
        primaryView.Width = primaryView.Height = PrimaryDipSize;
        outerRing.RenderTransform = rememberedFanLayer.RenderTransform = dwellOverlay.RenderTransform = PlacementScale;
        UpdateLayout();

        fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        primaryPixels = new Rect(placed.X, placed.Y, placed.Size, placed.Size);
        pixelsPerUnit = primaryPixels.Width / WheelLayout.Size;
        workPixels = new Rect(
            placed.Info.Work.Left,
            placed.Info.Work.Top,
            placed.Info.Work.Right - placed.Info.Work.Left,
            placed.Info.Work.Bottom - placed.Info.Work.Top);
        workOnCanvas = new Rect(
            (workPixels.Left - primaryPixels.Left) / pixelsPerUnit,
            (workPixels.Top - primaryPixels.Top) / pixelsPerUnit,
            workPixels.Width / pixelsPerUnit,
            workPixels.Height / pixelsPerUnit);
    }

    void CapturePrimaryPlacement()
    {
        windowHandle = new WindowInteropHelper(this).Handle;
        lastWindowRect = default;
        UpdateLayout();
        primaryView.Width = primaryView.Height = PrimaryDipSize;
        outerRing.RenderTransform = rememberedFanLayer.RenderTransform = dwellOverlay.RenderTransform = PlacementScale;
        UpdateLayout();

        var origin = canvas.PointToScreen(new Point(0, 0));
        var corner = canvas.PointToScreen(new Point(WheelLayout.Size, WheelLayout.Size));
        primaryPixels = new Rect(origin, corner);
        pixelsPerUnit = primaryPixels.Width / WheelLayout.Size;
        fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

        var anchor = canvas.PointToScreen(WheelLayout.Center);
        var point = new Native.Point { X = (int)Math.Round(anchor.X), Y = (int)Math.Round(anchor.Y) };
        nint monitor = Native.MonitorFromPoint(point, 2);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("Monitor lookup failed.");
        activeMonitor = monitor;

        workPixels = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        workOnCanvas = new Rect((workPixels.Left - origin.X) / pixelsPerUnit, (workPixels.Top - origin.Y) / pixelsPerUnit,
            workPixels.Width / pixelsPerUnit, workPixels.Height / pixelsPerUnit);
    }

    bool ResizeViewport(WheelLayout.Fan? next, bool forcePlace = false)
    {
        if (pixelsPerUnit <= 0) return true;
        if (openedFromHost && HasCursorMonitorChanged() && !aligningDpi)
        {
            Native.GetCursorPos(out var cursor);
            nint cursorMonitor = Native.MonitorFromPoint(cursor, 2);
            var placed = Native.PlaceNearCursor(
                windowHandle,
                centered: stickyGaze,
                verticalBiasDip: IsGazeMode ? -46 : 0,
                dipSize: PrimaryDipSize,
                physicalScale: ResolveCursorMonitorScale(cursorMonitor));
            ApplyPlacementMetrics(placed);
            ReserveFanViewport();
            forcePlace = true;
        }

        Rect viewport = primaryPixels;
        if (!reservedPixels.IsEmpty) viewport.Union(reservedPixels);
        if (next != null)
        {
            // Reserve the radial path to the fan, including the transparent gap and any inward turn at a screen edge.
            double radius = (next.OuterRadius + 4) * pixelsPerUnit;
            Point anchor = new(primaryPixels.Left + primaryPixels.Width / 2, primaryPixels.Top + primaryPixels.Height / 2);
            viewport.Union(new Rect(anchor.X - radius, anchor.Y - radius, 2 * radius, 2 * radius));
            viewport.Intersect(workPixels);
        }

        Rect? barPixels = null;
        if (HasGazeBarVisible)
        {
            double dipScale = primaryView.Width > 0 ? primaryPixels.Width / primaryView.Width : 1.0;
            double barW = GazeBarWidth * dipScale;
            double barH = GazeBarHeight * dipScale;
            double minLeft = workPixels.Left + 4;
            double maxLeft = Math.Max(minLeft, workPixels.Right - barW - 4);
            double minTop = workPixels.Top + 4;
            double maxTop = Math.Max(minTop, workPixels.Bottom - barH - 4);
            double barLeft = Math.Clamp((primaryPixels.Left + primaryPixels.Right) / 2 - barW / 2, minLeft, maxLeft);
            double barTop = Math.Clamp(primaryPixels.Top + GazeBarTopOffset * dipScale, minTop, maxTop);
            Rect clampedBar = new(barLeft, barTop, barW, barH);
            clampedBar.Intersect(workPixels);
            if (clampedBar.Width > 0 && clampedBar.Height > 0)
            {
                viewport.Union(clampedBar);
                barPixels = clampedBar;
            }
        }

        Rect? mixedBarPixels = null;
        if (mixedGroupBar.Visibility == Visibility.Visible)
        {
            double dipScale = primaryView.Width > 0 ? primaryPixels.Width / primaryView.Width : 1.0;
            mixedGroupBar.Measure(new Size(540, 200));
            double desiredW = Math.Max(220, mixedGroupBar.DesiredSize.Width) * dipScale;
            double desiredH = Math.Max(34, mixedGroupBar.DesiredSize.Height) * dipScale;
            double minLeft = workPixels.Left + 4;
            double maxLeft = Math.Max(minLeft, workPixels.Right - desiredW - 4);
            double barLeft = Math.Clamp((primaryPixels.Left + primaryPixels.Right) / 2 - desiredW / 2, minLeft, maxLeft);
            double preferredTop = primaryPixels.Bottom - 6 * dipScale;
            if (barPixels is { } gb && preferredTop + desiredH + 4 > gb.Top)
                preferredTop = primaryPixels.Top - desiredH + 6 * dipScale;
            else if (preferredTop + desiredH + 4 > workPixels.Bottom)
                preferredTop = primaryPixels.Top - desiredH + 6 * dipScale;
            double barTop = Math.Clamp(preferredTop, workPixels.Top + 4, Math.Max(workPixels.Top + 4, workPixels.Bottom - desiredH - 4));
            Rect clampedMixed = new(barLeft, barTop, desiredW, desiredH);
            clampedMixed.Intersect(workPixels);
            if (clampedMixed.Width > 0 && clampedMixed.Height > 0)
            {
                viewport.Union(clampedMixed);
                mixedBarPixels = clampedMixed;
            }
        }

        viewport.Intersect(workPixels);
        int left = (int)Math.Floor(viewport.Left), top = (int)Math.Floor(viewport.Top);
        int width = Math.Max(1, (int)Math.Ceiling(viewport.Right) - left), height = Math.Max(1, (int)Math.Ceiling(viewport.Bottom) - top);
        var windowRect = new Int32Rect(left, top, width, height);
        // Su cambio monitor o apertura iniziale forza sempre SetWindowPos; sullo stesso monitor evita ridimensionamenti inutili.
        if (forcePlace || windowRect != lastWindowRect)
        {
            nint insertAfter = forcePlace ? new nint(-1) : 0;
            uint flags = forcePlace ? 0x10u : 0x14u;
            if (!Native.SetWindowPos(windowHandle, insertAfter, left, top, width, height, flags)) return false;
            lastWindowRect = windowRect;
        }
        var offset = fromDevice.Transform(new Vector(primaryPixels.Left - left, primaryPixels.Top - top));
        Canvas.SetLeft(primaryView, offset.X); Canvas.SetTop(primaryView, offset.Y);
        Canvas.SetLeft(outerRing, offset.X); Canvas.SetTop(outerRing, offset.Y);
        Canvas.SetLeft(rememberedFanLayer, offset.X); Canvas.SetTop(rememberedFanLayer, offset.Y);
        Canvas.SetLeft(dwellOverlay, offset.X); Canvas.SetTop(dwellOverlay, offset.Y);
        Canvas.SetLeft(toolNotice, offset.X + ToolNoticeLeft); Canvas.SetTop(toolNotice, offset.Y + ToolNoticeTop);
        if (barPixels is { } bp)
        {
            var barOffset = fromDevice.Transform(new Vector(bp.Left - left, bp.Top - top));
            Canvas.SetLeft(gazeBar, barOffset.X);
            Canvas.SetTop(gazeBar, barOffset.Y);
        }
        if (mixedBarPixels is { } mp)
        {
            var mixedOffset = fromDevice.Transform(new Vector(mp.Left - left, mp.Top - top));
            Canvas.SetLeft(mixedGroupBar, mixedOffset.X);
            Canvas.SetTop(mixedGroupBar, mixedOffset.Y);
        }
        return true;
    }

    void SetExpanded(int index)
    {
        if (index == expanded) return;
        var next = index < 0 ? null : WheelLayout.FitFan(index, WedgeCount, ChildCount(index), workOnCanvas);
        if (!ResizeViewport(next) && next != null) return;

        expanded = next is null ? -1 : index;
        fan = next;
        leaveTimer.Stop();
        selectedChild = -1;
        outerRing.Children.Clear();
        outerSectors.Clear();
        rememberedFanLayer.Children.Clear();
        // La ruota principale resta ferma: cambia solo il bordo dello spicchio padre "+ CATEGORIA".
        for (int i = 0; i < sectors.Count; i++)
        {
            bool isParent = next != null && i == index;
            sectors[i].Stroke = isParent ? ExpandedParentStroke : SectorStroke;
            sectors[i].StrokeThickness = isParent ? 2 : 1.4;
        }
        if (next == null) return;

        bool cascade = !ScreenEdgeAuraWindow.Suppressed;
        double radius = (WheelLayout.FanInnerRadius + next.OuterRadius) / 2;
        for (int child = 0; child < next.Count; child++)
        {
            double angle = next.Angle(child);
            double half = next.Step / 2 - next.Gap;
            var radial = Radial(angle);
            var path = new System.Windows.Shapes.Path
            {
                Data = FanSectorData(angle - half, angle + half, next.OuterRadius),
                Fill = RestBrush,
                Stroke = new SolidColorBrush(Color.FromArgb(180, 130, 133, 124)),
                StrokeThickness = 1.4,
                Tag = radial
            };
            outerSectors.Add(path);
            outerRing.Children.Add(path);
            var toolGroup = ToolGroupFor(index);
            string childExt = toolGroup is { } group ? group.Actions[child].Id : frontSlots[index].Children[child];
            if (toolGroup is null && GetRememberedTargetForCurrentPayload() is { } remTarget &&
                string.Equals(childExt, remTarget, StringComparison.OrdinalIgnoreCase))
            {
                double remSpan = Math.Min(9.0, half * 0.55);
                rememberedFanLayer.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = RememberedArcData(angle - remSpan, angle + remSpan, next.OuterRadius + 3.5),
                    Stroke = RememberedTargetBrush,
                    StrokeThickness = 3.0,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                });
            }
            string caption = toolGroup is { } actions ? actions.Actions[child].Label : Flip.Core.FormatRegistry.GetWheelShortLabel(childExt);
            // Corona esterna FanInnerRadius…OuterRadius: corda misurata sul bordo interno (il più stretto) con padding.
            double fanTangential = 2 * (WheelLayout.FanInnerRadius + 6) * Math.Sin(half * Math.PI / 180) - 8;
            double fanRadial = next.OuterRadius - WheelLayout.FanInnerRadius - 12;
            var label = new TextBlock
            {
                Text = caption, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                FontSize = FitLabelFontSize(caption, toolGroup is null ? 15 : 13.5, angle, fanTangential, fanRadial, 7)
            };
            Point position = WheelLayout.At(radius, angle);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = label.DesiredSize;
            Canvas.SetLeft(label, Math.Clamp(position.X - size.Width / 2,
                workOnCanvas.Left + 2, workOnCanvas.Right - size.Width - 2));
            Canvas.SetTop(label, Math.Clamp(position.Y - size.Height / 2,
                workOnCanvas.Top + 2, workOnCanvas.Bottom - size.Height - 2));
            outerRing.Children.Add(label);
            if (cascade)
            {
                // "Uno alla volta": ogni figlio esce da sotto lo spicchio padre con 22 ms di sfasamento.
                var delay = TimeSpan.FromMilliseconds(child * 22);
                var fade = TimeSpan.FromMilliseconds(150);
                var move = TimeSpan.FromMilliseconds(170);
                PlayRadialEntrance(path, radial, 14, 1, delay, fade, move,
                    new CubicEase { EasingMode = EasingMode.EaseOut }, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });
                PlayRadialEntrance(label, radial, 14, 1, delay, fade, move,
                    new CubicEase { EasingMode = EasingMode.EaseOut }, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 });
            }
        }
    }

    static Geometry FanSectorData(double start, double end, double outer)
    {
        const double inner = WheelLayout.FanInnerRadius;
        var figure = new PathFigure { StartPoint = WheelLayout.At(outer, start), IsClosed = true };
        figure.Segments.Add(new ArcSegment(WheelLayout.At(outer, end), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(WheelLayout.At(inner, end), true));
        figure.Segments.Add(new ArcSegment(WheelLayout.At(inner, start), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true));
        return new PathGeometry([figure]);
    }

    internal static string[] OrderFormats(IEnumerable<string> outputs) => preferred.Where(outputs.Contains).Concat(outputs.Except(preferred)).ToArray();
    static readonly ScaleTransform PlacementScale = Freeze(new ScaleTransform(PrimaryDipSize / WheelLayout.Size, PrimaryDipSize / WheelLayout.Size));
    static readonly LinearGradientBrush RestBrush = Freeze(new LinearGradientBrush(Color.FromArgb(200, 73, 77, 76), Color.FromArgb(190, 44, 48, 48), 90));
    static readonly LinearGradientBrush HoverBrush = Freeze(new LinearGradientBrush(Color.FromRgb(255, 112, 12), Color.FromRgb(255, 112, 12), 90));
    static readonly ConcurrentDictionary<(int Count, int Index), PathGeometry> PrimarySectors = new();

    internal static Geometry SectorData(int index, int count = 8) =>
        PrimarySectors.GetOrAdd((count, index), static key => Freeze(BuildPrimarySector(key.Index, key.Count)));

    static PathGeometry BuildPrimarySector(int index, int count)
    {
        double span = 360.0 / count, gap = Math.Min(2, span * .06);
        double start = index * span - span / 2 + gap, end = (index + 1) * span - span / 2 - gap;
        double outerCorner = Math.Min(2.2, (end - start) / 3), innerCorner = Math.Min(4, (end - start) / 3);
        Point At(double r, double deg) => new(210 + r * Math.Cos((deg - 90) * Math.PI / 180), 210 + r * Math.Sin((deg - 90) * Math.PI / 180));
        var fig = new PathFigure { StartPoint = At(178, start), IsClosed = true };
        fig.Segments.Add(new QuadraticBezierSegment(At(185, start), At(185, start + outerCorner), true));
        fig.Segments.Add(new ArcSegment(At(185, end - outerCorner), new Size(185, 185), 0, end - start > 180, SweepDirection.Clockwise, true));
        fig.Segments.Add(new QuadraticBezierSegment(At(185, end), At(178, end), true));
        fig.Segments.Add(new LineSegment(At(105, end), true));
        fig.Segments.Add(new QuadraticBezierSegment(At(98, end), At(98, end - innerCorner), true));
        fig.Segments.Add(new ArcSegment(At(98, start + innerCorner), new Size(98, 98), 0, end - start > 180, SweepDirection.Counterclockwise, true));
        fig.Segments.Add(new QuadraticBezierSegment(At(98, start), At(105, start), true));
        return new PathGeometry([fig]);
    }

    internal static Geometry ProgressArcData(double percent, double radius = 91)
    {
        double clamped = Math.Clamp(percent, 0.8, 99.9);
        double deg = clamped * 3.6 - 90.0;
        double rad = deg * Math.PI / 180.0;
        Point start = new(210, 210 - radius);
        Point end = new(210 + radius * Math.Cos(rad), 210 + radius * Math.Sin(rad));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, clamped > 50.0, SweepDirection.Clockwise, true));
        return new PathGeometry([fig]);
    }

    void StartProgressVisual(string chosenFormat, string? displayName = null)
    {
        RevealAllPrimaryWedgesImmediately();
        CancelPendingOpenCascade();
        ShowCropOutline(null);
        gazePollTimer.Stop();
        ClearGazeVisual();
        gazeBar.Visibility = Visibility.Collapsed;
        SetExpanded(-1);
        ResizeViewport(null);
        converting = true;
        dropping = false;
        AllowDrop = false;
        activeFormat = displayName ?? chosenFormat;
        facePillTimer.Stop();
        if (facePill != null) facePill.Visibility = Visibility.Collapsed;
        displayedPercent = 4;
        targetPercent = 12;

        for (int i = 0; i < sectors.Count; i++)
        {
            bool isTarget = selected == i;
            sectors[i].Fill = isTarget ? HoverBrush : RestBrush;
            sectors[i].BeginAnimation(OpacityProperty, null);
            sectors[i].Opacity = isTarget ? 1.0 : 0.22;
        }

        var trackRing = new Ellipse
        {
            Width = 182,
            Height = 182,
            Stroke = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            StrokeThickness = 5,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(trackRing, 119);
        Canvas.SetTop(trackRing, 119);
        canvas.Children.Add(trackRing);

        progressRay = new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(170, 255, 165, 60)),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        canvas.Children.Add(progressRay);

        progressArc = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 136, 24)),
            StrokeThickness = 5.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        canvas.Children.Add(progressArc);

        progressTip = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromRgb(255, 210, 130)),
            IsHitTestVisible = false
        };
        // Bagliore vivo sulla punta dell'arco che avanza: respira tra blur 12 e 26.
        var tipGlow = new DropShadowEffect { Color = FlipAccent, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.95 };
        tipGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(12, 26, TimeSpan.FromMilliseconds(520))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        progressTip.Effect = tipGlow;
        canvas.Children.Add(progressTip);

        UpdateProgressVisual();

        progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        progressTimer.Tick += (_, _) =>
        {
            if (!converting || closing) return;
            if (displayedPercent < targetPercent)
                displayedPercent = Math.Min(targetPercent, displayedPercent + Math.Max(0.8, (targetPercent - displayedPercent) * 0.28));
            else if (displayedPercent < 96)
                displayedPercent += (96 - displayedPercent) * 0.022;
            UpdateProgressVisual();
        };
        progressTimer.Start();
    }

    void UpdateProgressVisual()
    {
        if (progressArc == null || progressRay == null || progressTip == null) return;
        double pct = Math.Clamp(displayedPercent, 1, 99.9);
        progressArc.Data = ProgressArcData(pct, 91);

        double rad = (pct * 3.6 - 90.0) * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        progressRay.X1 = 210 + 66 * cos;
        progressRay.Y1 = 210 + 66 * sin;
        progressRay.X2 = 210 + 91 * cos;
        progressRay.Y2 = 210 + 91 * sin;

        Canvas.SetLeft(progressTip, 210 + 91 * cos - 4.5);
        Canvas.SetTop(progressTip, 210 + 91 * sin - 4.5);

        string sourceLabel = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : Loc.F("{0} file", "{0} files", files.Length);
        if (sourceLabel.Length > 20) sourceLabel = sourceLabel[..19] + "…";
        int shownPct = Math.Clamp((int)Math.Round(displayedPercent), 1, 99);
        center.Text = Loc.F("{0}\n→ {1}  ({2}%)\nConversione in corso…\nEsc per annullare", "{0}\n→ {1}  ({2}%)\nConverting…\nEsc to cancel", sourceLabel, activeFormat, shownPct);
    }

    internal static void RecordLastSuccessfulChoice(IEnumerable<string> sourceExts, string targetExt)
    {
        string target = FormatRegistry.CanonicalExtension(targetExt);
        if (string.IsNullOrEmpty(target)) return;
        foreach (string rawExt in sourceExts)
        {
            string normSrc = rawExt.Trim().TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(normSrc))
                LastSuccessfulTargetBySourceExt[normSrc] = target;
        }
    }

    internal string? GetRememberedTargetForCurrentPayload()
    {
        if (showingTools || files.Length == 0 || sourceExtensions.Length == 0 || formats.Length == 0)
            return null;

        string? shared = null;
        foreach (string ext in sourceExtensions)
        {
            string normSrc = ext.Trim().TrimStart('.').ToLowerInvariant();
            if (!LastSuccessfulTargetBySourceExt.TryGetValue(normSrc, out string? remembered))
                return null;
            if (shared == null) shared = remembered;
            else if (!string.Equals(shared, remembered, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        if (string.IsNullOrEmpty(shared)) return null;
        return formats.Any(f => string.Equals(f, shared, StringComparison.OrdinalIgnoreCase)) ? shared : null;
    }

    bool SlotMatchesRememberedTarget(int slotIndex, string rememberedTarget)
    {
        if ((uint)slotIndex >= (uint)frontSlots.Length) return false;
        var slot = frontSlots[slotIndex];
        if (!string.IsNullOrEmpty(slot.Format) &&
            string.Equals(slot.Format, rememberedTarget, StringComparison.OrdinalIgnoreCase))
            return true;
        return slot.Children.Any(c => string.Equals(c, rememberedTarget, StringComparison.OrdinalIgnoreCase));
    }

    static Geometry RememberedArcData(double startDeg, double endDeg, double radius)
    {
        Point At(double deg) => new(
            210 + radius * Math.Cos((deg - 90) * Math.PI / 180),
            210 + radius * Math.Sin((deg - 90) * Math.PI / 180));
        var fig = new PathFigure { StartPoint = At(startDeg), IsClosed = false };
        fig.Segments.Add(new ArcSegment(At(endDeg), new Size(radius, radius), 0, endDeg - startDeg > 180, SweepDirection.Clockwise, true));
        return new PathGeometry([fig]);
    }

    internal void SetConversionProgress(int percent, string? step = null)
    {
        if (!converting || closing || targetPercent >= 100) return;
        targetPercent = Math.Clamp(percent, (int)targetPercent, 99);
        if (targetPercent > displayedPercent)
            displayedPercent = Math.Max(displayedPercent, targetPercent - 8);
        UpdateProgressVisual();
    }

    internal void CompleteConversion(bool success, string? message = null)
    {
        if (closing) return;
        if (success && !showingTools && activeJobSourceExts.Length > 0 && !string.IsNullOrWhiteSpace(activeJobTargetExt))
            RecordLastSuccessfulChoice(activeJobSourceExts, activeJobTargetExt);
        activeJobSourceExts = [];
        activeJobTargetExt = "";

        progressTimer?.Stop();
        displayedPercent = 100;
        targetPercent = 100;
        if (progressArc != null && progressRay != null && progressTip != null)
        {
            var color = success ? Color.FromRgb(46, 204, 113) : Color.FromRgb(231, 76, 60);
            var brush = new SolidColorBrush(color);
            progressArc.Stroke = brush;
            progressArc.Data = ProgressArcData(99.9, 91);
            progressRay.Stroke = brush;
            progressTip.Fill = brush;
            if (progressTip.Effect is DropShadowEffect { IsFrozen: false } tipGlow) tipGlow.Color = color;
        }
        ScreenEdgeAuraWindow.Pulse(success);
        string sourceLabel = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : Loc.F("{0} file", "{0} files", files.Length);
        if (sourceLabel.Length > 20) sourceLabel = sourceLabel[..19] + "…";
        string? detail = success || string.IsNullOrWhiteSpace(message) ? null : message.Trim().ReplaceLineEndings(" ");
        if (detail is { Length: > 110 }) detail = detail[..109] + "…";
        center.Text = success
            ? Loc.F("{0}\n✓ {1} (100%)\nCompletato!", "{0}\n✓ {1} (100%)\nDone!", sourceLabel, activeFormat)
            : detail is null
                ? Loc.F("{0}\n✕ {1}\nErrore conversione", "{0}\n✕ {1}\nConversion failed", sourceLabel, activeFormat)
                : $"✕ {activeFormat}\n{detail}";

        // Un errore esplicito resta leggibile al centro della ruota prima della chiusura automatica.
        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(detail is null ? 360 : 2600) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            converting = false;
            Dismiss("conversion-completed");
        };
        closeTimer.Start();
    }

    internal void SavePreview(string path, string source, bool hover, string[]? sources = null)
    {
        EngineBootstrap.EnsureInitialized();
        SetPayload(sources ?? [source]); Paint(); if (hover && formats.Length > 0) Highlight(0);
        canvas.Measure(new Size(420, 420)); canvas.Arrange(new Rect(0, 0, 420, 420)); canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(420, 420, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    SectorHit? Hit(Point point)
    {
        if (flipping || closing) return null;
        if (fan?.Hit(point) is >= 0 and var child) return new(expanded, child);
        if (IsOverFacePill(point)) return null;
        for (int i = 0; i < SelectableCount; i++)
            if (sectors[i].Data.FillContains(point)) return new(i);
        return null;
    }

    SectorHit? TrackPointer(Point point)
    {
        if (converting || closing) return null;
        ScreenEdgeAuraWindow.SyncCursorMonitor(windowHandle);
        UpdateFacePillHover(point);
        if (flipping) return null;
        var hit = Hit(point);
        if (hit is { } sector)
        {
            if (sector.Child < 0)
            {
                if (!IsGazeMode) SetExpanded(IsParent(sector.Primary) ? sector.Primary : -1);
                else if (expanded >= 0 && sector.Primary != expanded) SetExpanded(-1);
            }
            Highlight(sector.Primary, sector.Child);
        }
        else if (fan is { } current && WheelLayout.Radius(point) <= current.OuterRadius)
            Highlight(expanded);
        else
        {
            SetExpanded(-1);
            Highlight(-1);
        }
        return hit;
    }

    void TrackLeave()
    {
        if (IsGazeMode)
        {
            leaveTimer.Stop();
            return;
        }
        if (converting || flipping || closing || !Native.GetCursorPos(out var cursor))
        {
            leaveTimer.Stop();
            return;
        }
        var screen = new Point(cursor.X, cursor.Y);
        var point = PointFromScreen(screen);
        if (point.X >= 0 && point.Y >= 0 && point.X < ActualWidth && point.Y < ActualHeight)
        {
            leaveTimer.Stop();
            return;
        }
        // Near a monitor seam, the pointer can leave our HWND before it leaves the fan's radius.
        if (fan is { } current && WheelLayout.Radius(canvas.PointFromScreen(screen)) <= current.OuterRadius)
        {
            leaveTimer.Start();
            return;
        }
        leaveTimer.Stop();
        SetExpanded(-1);
        Highlight(-1);
        if (dropping) Evidence.Log("ole.leave");
    }

    bool IsParent(int index) => showingTools ? ToolGroupFor(index) is not null : frontSlots[index].Children.Length > 0;

    string? FrontFormat(SectorHit hit) => hit.Child < 0
        ? frontSlots[hit.Primary].Format : frontSlots[hit.Primary].Children[hit.Child];

    void MoveSelection(int direction)
    {
        if (SelectableCount == 0) return;
        if (fan != null && selectedChild >= 0)
        {
            Highlight(expanded, (selectedChild + direction + fan.Count) % fan.Count);
            return;
        }

        int next = selected < 0 ? (direction > 0 ? 0 : SelectableCount - 1) :
            (selected + direction + SelectableCount) % SelectableCount;
        SetExpanded(-1);
        Highlight(next);
    }

    internal void SelectForTest(int index, int child = -1) => Highlight(index, child);

    void Highlight(int index, int child = -1)
    {
        if (converting || flipping) return;
        if (index < 0 || index >= SelectableCount) index = -1;
        if (selected == index && selectedChild == child) return;
        if (index >= 0) RevealPrimaryWedgeImmediately(index);
        var accent = HoverBrush;
        for (int i = 0; i < sectors.Count; i++)
        {
            sectors[i].Fill = i == index ? accent : RestBrush;
            ApplyWedgeHoverVisuals(sectors[i], i == index && child < 0);
        }
        for (int i = 0; i < outerSectors.Count; i++)
        {
            outerSectors[i].Fill = i == child ? accent : RestBrush;
            ApplyWedgeHoverVisuals(outerSectors[i], i == child);
        }
        int previousSelected = selected;
        int previousChild = selectedChild;
        selected = index;
        selectedChild = child;
        if (showingTools) UpdateToolsCenter();
        else UpdateFrontCenter();
        if (index >= 0 && (previousSelected != index || previousChild != child))
            playSound?.Invoke();
    }

    /// <summary>
    /// Hover direzionale: lo spicchio attivo scivola di 3.5 px lungo la propria bisettrice (110 ms) con bagliore
    /// arancione morbido. Nessun ingrandimento centrato sulla ruota, quindi nessuno sbalzo dei vicini.
    /// </summary>
    static void ApplyWedgeHoverVisuals(System.Windows.Shapes.Path path, bool active)
    {
        if (path.Tag is not Vector direction) return;
        TranslateTransform? shift = path.RenderTransform switch
        {
            TranslateTransform { IsFrozen: false } direct => direct,
            TransformGroup { IsFrozen: false } group => group.Children.OfType<TranslateTransform>().FirstOrDefault(t => !t.IsFrozen),
            _ => null
        };
        if (shift is null)
        {
            if (!active) { path.Effect = null; return; }
            shift = new TranslateTransform();
            if (path.RenderTransform is TransformGroup { IsFrozen: false } existingGroup)
                existingGroup.Children.Add(shift);
            else
                path.RenderTransform = shift;
        }
        path.Effect = active
            ? new DropShadowEffect { Color = FlipAccent, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.65 }
            : null;
        double distance = active ? HoverShift : 0;
        double toX = direction.X * distance, toY = direction.Y * distance;
        if (!active && !shift.HasAnimatedProperties && shift.X == toX && shift.Y == toY) return;
        var duration = TimeSpan.FromMilliseconds(110);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(toX, duration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(toY, duration) { EasingFunction = ease });
    }

    void Drag(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (converting) return;
        try
        {
            var rawPaths = Policy.RawFilePaths(e.Data);
            if (previousDragPaths is null || !rawPaths.AsSpan().SequenceEqual(previousDragPaths))
            {
                previousDragPaths = null;
                validatedDragFiles = Policy.ValidateDropPaths(rawPaths);
                previousDragPaths = rawPaths.ToArray();
            }
            var incoming = validatedDragFiles;
            if (incoming.Length == 0 && BrowserDropService.TryInspectBrowserData(e.Data, out var candidate))
            {
                browserCandidate = candidate;
                string syntheticPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), candidate.SuggestedFileName);
                incoming = [syntheticPath];
            }
            else
            {
                browserCandidate = null;
            }
            if (!allPayloadFiles.SequenceEqual(incoming))
            {
                bool initialDragPayload = allPayloadFiles.Length == 0 && incoming.Length > 0 && !ScreenEdgeAuraWindow.Suppressed;
                SetPayload(incoming);
                playOpenCascade = initialDragPayload;
                Paint();
                playOpenCascade = false;
                if (initialDragPayload) ScheduleOpenCascade();
                Evidence.Log("ole.payload", new { files, formats, browser = browserCandidate?.RawUri });
            }
            if (mixedGroupBar.Visibility == Visibility.Visible)
            {
                Point onSurface = e.GetPosition(surface);
                foreach (var (pill, extKey) in mixedGroupPills)
                {
                    if (pill.ActualWidth <= 0 || pill.ActualHeight <= 0) continue;
                    Point topLeft = pill.TranslatePoint(new Point(0, 0), surface);
                    Rect rect = new(topLeft.X, topLeft.Y, pill.ActualWidth, pill.ActualHeight);
                    if (rect.Contains(onSurface) && !string.Equals(extKey, activeMixedGroupExt, StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyMixedGroupFilter(extKey);
                        playSound?.Invoke();
                        break;
                    }
                }
            }
            var hit = TrackPointer(e.GetPosition(canvas));
            e.Effects = Policy.Effect(e.AllowedEffects, hit is { } sector && (showingTools || FrontFormat(sector) != null));
        }
        catch (Exception ex) { Evidence.Log("ole.error", ex.Message); }
    }

    void DropFiles(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (converting) return;
        if (flipping) { Dismiss("drop-during-flip"); return; }
        try
        {
            var hit = Hit(e.GetPosition(canvas));
            var incoming = Policy.ValidateDropPaths(Policy.RawFilePaths(e.Data));
            bool isBrowserDrop = false;
            if (incoming.Length == 0 && browserCandidate is { } bc)
            {
                isBrowserDrop = true;
                incoming = files;
            }
            else if (!isBrowserDrop && activeMixedGroupExt != null && allPayloadFiles.SequenceEqual(incoming))
            {
                incoming = files;
            }
            if (hit is not { } sector || incoming.Length == 0 || Policy.Effect(e.AllowedEffects, true) != DragDropEffects.Copy)
            {
                Dismiss("rejected-drop");
                return;
            }

            if (!isBrowserDrop && showingTools && !files.SequenceEqual(incoming)) { Dismiss("quick-tool-payload-changed"); return; }
            if (!isBrowserDrop && !showingTools && (FrontFormat(sector) is not { } target ||
                !RoutePlanner.GetReachableTargetFormats(GetSourceExtensions(incoming), settings)
                    .Any(format => string.Equals(format.Extension, target, StringComparison.OrdinalIgnoreCase))))
            {
                if (FrontFormat(sector) is { } unavailable)
                    report(Loc.F("Il formato {0} non è disponibile per tutti i file rilasciati.", "Format {0} is not available for all dropped files.", unavailable));
                Dismiss("format-unavailable-at-drop");
                return;
            }

            files = incoming;
            e.Effects = DragDropEffects.Copy;

            if (isBrowserDrop && browserCandidate is { } dropCandidate)
            {
                ChooseBrowser(sector, dropCandidate, "ole.drop.browser");
            }
            else
            {
                browserCandidate = null;
                Choose(sector, "ole.drop");
            }
        }
        catch (Exception ex) { report(Loc.F("Rilascio rifiutato: {0}", "Drop rejected: {0}", ex.Message)); Dismiss("drop-error"); }
    }

    void ChooseBrowser(SectorHit sector, BrowserDropCandidate candidate, string via)
    {
        if (converting || flipping || closing || sector.Primary >= SelectableCount) return;
        int index = sector.Primary;
        if (showingTools)
        {
            if (runBrowserTool == null) { Dismiss("no-browser-tool-handler"); return; }
            var tool = ResolveTool(sector, out bool groupAction);
            if (toolUnavailableReasons.TryGetValue(tool.Id, out string? browserReason))
            {
                report(browserReason);
                ShowToolNotice(browserReason);
                playSound?.Invoke();
                Evidence.Log("browser.quick-tool.unavailable", new { via, tool.Id, browserReason });
                return;
            }
            var context = new QuickToolContext { InputFilePaths = files.ToArray(), Settings = settings };
            if (!groupAction && TryGetPreset(tool.Id, out var options))
                context.Parameters[options.Key] = options.Values[toolPresets.GetValueOrDefault(tool.Id)];
            Highlight(index, groupAction ? sector.Child : -1);
            Evidence.Log("browser.quick-tool.selection", new { via, candidate.RawUri, tool.Id });
            StartProgressVisual(tool.Id, tool.DisplayName);
            Dispatcher.BeginInvoke(() =>
            {
                if (!closing && converting) runBrowserTool(candidate, tool, context);
            });
            return;
        }

        var output = FrontFormat(sector);
        if (output == null)
        {
            SetExpanded(index);
            return;
        }
        if (convertBrowser == null) { Dismiss("no-browser-convert-handler"); return; }
        Evidence.Log("browser.selection", new { via, candidate.RawUri, output });
        activeJobSourceExts = GetSourceExtensions(files);
        activeJobTargetExt = output;
        Highlight(index, sector.Child);
        StartProgressVisual(output);
        Dispatcher.BeginInvoke(() =>
        {
            if (!closing && converting) convertBrowser(candidate, output, settings);
        });
    }

    void Choose(SectorHit? hit, string via)
    {
        if (converting || flipping || closing || hit is not { } sector || sector.Primary >= SelectableCount) return;
        int index = sector.Primary;
        var selectedFiles = files.ToArray();
        if (showingTools)
        {
            if (runTool == null) return;
            // Sguardo: il primo dwell sul padre apre il ventaglio (l'azione predefinita è sempre tra i figli).
            if (sector.Child < 0 && IsGazeMode && IsParent(index) && expanded != index)
            {
                SetExpanded(index);
                Highlight(index);
                return;
            }
            var tool = ResolveTool(sector, out bool groupAction);
            if (toolUnavailableReasons.TryGetValue(tool.Id, out string? reason))
            {
                report(reason);
                ShowToolNotice(reason);
                playSound?.Invoke();
                Evidence.Log("quick-tool.unavailable", new { via, files, tool.Id, reason });
                return;
            }
            var context = new QuickToolContext { InputFilePaths = selectedFiles, Settings = settings };
            if (!groupAction && TryGetPreset(tool.Id, out var options))
                context.Parameters[options.Key] = options.Values[toolPresets.GetValueOrDefault(tool.Id)];
            Highlight(index, groupAction ? sector.Child : -1);
            Evidence.Log("quick-tool.selection", new { via, files, tool.Id, context.Parameters });
            StartProgressVisual(tool.Id, tool.DisplayName);
            Dispatcher.BeginInvoke(() =>
            {
                if (!closing && converting) runTool(tool, context);
            });
            return;
        }

        var output = FrontFormat(sector);
        if (output == null)
        {
            SetExpanded(index);
            Highlight(index, via == "keyboard" && fan != null ? 0 : -1);
            return;
        }
        Evidence.Log("selection", new { via, files, output, effect = "Copy" });
        if (convert != null)
        {
            activeJobSourceExts = GetSourceExtensions(selectedFiles);
            activeJobTargetExt = output;
            Highlight(index, sector.Child);
            StartProgressVisual(output);
            Dispatcher.BeginInvoke(() =>
            {
                if (!closing && converting) convert(selectedFiles, output);
            });
        }
        else
        {
            Dismiss("selection");
        }
    }

    internal void Dismiss(string reason)
    {
        if (closing) return;
        if (converting)
        {
            if (reason is "primary-button-up" or "watchdog") return;
            if (reason is "escape" or "escape-focused") cancelConversion?.Invoke();
            converting = false;
            activeJobSourceExts = [];
            activeJobTargetExt = "";
            progressTimer?.Stop();
        }
        if (!IsVisible)
        {
            CloseImmediately(reason);
            return;
        }
        CancelPendingOpenCascade();
        gazePollTimer.Stop();
        ClearGazeVisual();
        closing = true; Evidence.Log("wheel.close", reason);
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(85));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }

    internal void CloseImmediately(string reason)
    {
        if (closing)
        {
            CancelPendingOpenCascade();
            try { BeginAnimation(OpacityProperty, null); Opacity = 0; Close(); } catch (InvalidOperationException) { }
            return;
        }
        CancelPendingOpenCascade();
        converting = false;
        activeJobSourceExts = [];
        activeJobTargetExt = "";
        progressTimer?.Stop();
        gazePollTimer.Stop();
        facePillTimer.Stop();
        leaveTimer.Stop();
        ClearGazeVisual();
        closing = true;
        Evidence.Log("wheel.close.immediate", reason);
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        try { Close(); } catch (InvalidOperationException) { }
    }

    void BuildGazeBar()
    {
        gazeBar.Width = GazeBarWidth;
        gazeBar.Height = GazeBarHeight;
        gazeBar.Background = new SolidColorBrush(Color.FromArgb(242, 0x12, 0x13, 0x18));
        gazeBar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x39));
        gazeBar.BorderThickness = new Thickness(1);
        gazeBar.CornerRadius = new CornerRadius(14);
        gazeBar.Padding = new Thickness(7);

        var grid = new Grid();
        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string[] initialLabels = [Loc.T("⇄ GIRA STRUMENTI", "⇄ FLIP TOOLS"), Loc.T("👁 ANTEPRIMA", "👁 PREVIEW"), Loc.T("✕ CHIUDI", "✕ CLOSE")];
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            var label = new TextBlock
            {
                Text = initialLabels[i],
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var progress = new Border
            {
                Height = 3,
                Width = 0,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(GazeDwell.AccentColor),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 1),
                IsHitTestVisible = false
            };

            var inner = new Grid();
            inner.Children.Add(label);
            inner.Children.Add(progress);

            var button = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x42)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 2 ? 0 : 4, 0),
                Child = inner
            };
            button.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                TriggerGazeBarButton(idx, "click");
            };
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!gazeBarCompletedUntilLeave[idx])
                    TriggerGazeBarButton(idx, "click");
            };

            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            gazeButtons[i] = button;
            gazeButtonProgress[i] = progress;
            gazeButtonLabels[i] = label;
        }

        gazeBar.Child = grid;
    }

    bool CanPreviewInGazeBar =>
        !converting && !closing && !flipping && quickLookReady && openQuickLook != null && browserCandidate is null && files.Length >= 1;

    bool IsGazeBarButtonEnabled(int index) => index switch
    {
        0 => CanFlipFace,
        1 => CanPreviewInGazeBar,
        2 => !converting && !closing,
        _ => false
    };

    void UpdateGazeBarState()
    {
        gazeBar.Visibility = IsGazeMode && !converting ? Visibility.Visible : Visibility.Collapsed;
        gazeButtonLabels[0].Text = showingTools ? Loc.T("⇄ GIRA FORMATI", "⇄ FLIP FORMATS") : Loc.T("⇄ GIRA STRUMENTI", "⇄ FLIP TOOLS");
        gazeButtonLabels[1].Text = Loc.T("👁 ANTEPRIMA", "👁 PREVIEW");
        gazeButtonLabels[2].Text = Loc.T("✕ CHIUDI", "✕ CLOSE");
        for (int i = 0; i < 3; i++)
        {
            bool enabled = IsGazeBarButtonEnabled(i);
            gazeButtons[i].Opacity = enabled ? 1.0 : 0.42;
            gazeButtons[i].Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
            if (!enabled) gazeButtonProgress[i].Width = 0;
        }
    }

    void TriggerGazeBarButton(int index, string via)
    {
        if (!IsGazeBarButtonEnabled(index)) return;
        gazeBarCompletedUntilLeave[index] = true;
        ResetGazeDwell();

        switch (index)
        {
            case 0:
                FlipFace();
                break;
            case 1:
                TriggerQuickLook(via);
                break;
            case 2:
                Dismiss("gaze-close");
                break;
        }
    }

    bool IsSectorDwellable(SectorHit sector)
    {
        if (sector.Primary < 0 || sector.Primary >= SelectableCount) return false;
        if (showingTools) return sector.Child >= 0 || !IsParent(sector.Primary) || sector.Primary != expanded;
        if (sector.Child >= 0) return true;
        if (IsParent(sector.Primary)) return sector.Primary != expanded;
        return FrontFormat(sector) != null;
    }

    void ResetGazeDwell()
    {
        activeGazeTarget = GazeTarget.Empty;
        gazeLastSeenTick = 0;
        gazeLastUpdateTick = 0;
        gazeAccumulatedMs = 0;
        ClearGazeVisual();
    }

    void ClearGazeVisual()
    {
        dwellOverlay.Children.Clear();
        for (int i = 0; i < 3; i++)
        {
            gazeButtonProgress[i].Width = 0;
            gazeButtons[i].Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x23));
            gazeButtons[i].BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x42));
            gazeButtons[i].BorderThickness = new Thickness(1);
        }
    }

    void OnGazePollTick()
    {
        if (closing || converting || flipping || !IsVisible || !IsGazeMode)
        {
            ClearGazeVisual();
            return;
        }
        if (!Native.GetCursorPos(out var cursor)) return;

        Point screen = new(cursor.X, cursor.Y);
        GazeTarget candidate = GazeTarget.Empty;
        int hoveredButton = -1;

        if (HasGazeBarVisible)
        {
            for (int i = 0; i < 3; i++)
            {
                Point local = gazeButtons[i].PointFromScreen(screen);
                bool inside = local.X >= -4 && local.Y >= -4 &&
                              local.X <= gazeButtons[i].ActualWidth + 4 &&
                              local.Y <= gazeButtons[i].ActualHeight + 4;
                if (inside)
                {
                    hoveredButton = i;
                    if (IsGazeBarButtonEnabled(i) && !gazeBarCompletedUntilLeave[i])
                        candidate = new GazeTarget(GazeTargetKind.BarButton, i, -1);
                    break;
                }
                gazeBarCompletedUntilLeave[i] = false;
            }
        }

        if (hoveredButton < 0)
        {
            Point canvasPoint = canvas.PointFromScreen(screen);
            var hit = TrackPointer(canvasPoint);
            if (hit is { } sector && IsSectorDwellable(sector))
            {
                var sectorTarget = new GazeTarget(GazeTargetKind.Sector, sector.Primary, sector.Child);
                if (sectorTarget != completedGazeSector)
                    candidate = sectorTarget;
            }
            else
            {
                completedGazeSector = GazeTarget.Empty;
            }
        }

        for (int i = 0; i < 3; i++)
        {
            bool activeHover = i == hoveredButton && IsGazeBarButtonEnabled(i);
            gazeButtons[i].Background = new SolidColorBrush(activeHover ? Color.FromRgb(0x25, 0x28, 0x34) : Color.FromRgb(0x1A, 0x1C, 0x23));
            gazeButtons[i].BorderBrush = new SolidColorBrush(activeHover ? GazeDwell.AccentColor : Color.FromRgb(0x2E, 0x33, 0x42));
            gazeButtons[i].BorderThickness = new Thickness(activeHover ? 1.3 : 1);
            if (candidate.Kind != GazeTargetKind.BarButton || candidate.Primary != i)
                gazeButtonProgress[i].Width = 0;
        }

        long now = Stopwatch.GetTimestamp();
        if (candidate.Kind == GazeTargetKind.None)
        {
            if (activeGazeTarget.Kind != GazeTargetKind.None)
            {
                double missedMs = (now - gazeLastSeenTick) * 1000.0 / Stopwatch.Frequency;
                if (missedMs >= GazeGraceMs) ResetGazeDwell();
            }
            return;
        }

        if (candidate != activeGazeTarget)
        {
            activeGazeTarget = candidate;
            gazeLastSeenTick = now;
            gazeLastUpdateTick = now;
            gazeAccumulatedMs = 0;
        }
        else
        {
            double deltaMs = (now - gazeLastUpdateTick) * 1000.0 / Stopwatch.Frequency;
            gazeAccumulatedMs += Math.Clamp(deltaMs, 0, 80);
            gazeLastSeenTick = now;
            gazeLastUpdateTick = now;
        }

        double dwellMs = Math.Clamp(settings.GazeDwellMilliseconds, 600, 2500);
        double progress = Math.Clamp(gazeAccumulatedMs / dwellMs, 0, 1);

        if (activeGazeTarget.Kind == GazeTargetKind.BarButton)
        {
            dwellOverlay.Children.Clear();
            int btn = activeGazeTarget.Primary;
            gazeButtonProgress[btn].Width = Math.Max(0, gazeButtons[btn].ActualWidth - 4) * progress;
            if (progress >= 1.0)
                TriggerGazeBarButton(btn, "gaze-dwell");
            return;
        }

        DrawGazeSectorArc(activeGazeTarget, progress);
        if (progress >= 1.0)
        {
            var chosen = new SectorHit(activeGazeTarget.Primary, activeGazeTarget.Child);
            if (!IsParent(chosen.Primary) || chosen.Child >= 0)
                completedGazeSector = activeGazeTarget;
            ResetGazeDwell();
            Choose(chosen, "gaze-dwell");
        }
    }

    void DrawGazeSectorArc(GazeTarget target, double progress)
    {
        dwellOverlay.Children.Clear();
        Point centerPt;
        double arcRadius;
        if (target.Child >= 0 && fan is { } currentFan)
        {
            double r = (WheelLayout.FanInnerRadius + currentFan.OuterRadius) / 2;
            centerPt = WheelLayout.At(r, currentFan.Angle(target.Child));
            arcRadius = Math.Clamp((currentFan.OuterRadius - WheelLayout.FanInnerRadius) * 0.42, 16, 26);
        }
        else if (target.Primary >= 0 && sectors.Count > 0)
        {
            double span = 360.0 / sectors.Count;
            centerPt = WheelLayout.At(143, target.Primary * span);
            arcRadius = 28;
        }
        else return;

        var track = new Ellipse
        {
            Width = arcRadius * 2,
            Height = arcRadius * 2,
            Stroke = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)),
            StrokeThickness = 2.6,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(track, centerPt.X - arcRadius);
        Canvas.SetTop(track, centerPt.Y - arcRadius);
        dwellOverlay.Children.Add(track);

        if (GazeDwell.CreateArcGeometry(centerPt, arcRadius, progress) is { } arcGeom)
        {
            dwellOverlay.Children.Add(new System.Windows.Shapes.Path
            {
                Data = arcGeom,
                Stroke = new SolidColorBrush(GazeDwell.AccentColor),
                StrokeThickness = 3.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false
            });
        }
    }
}
