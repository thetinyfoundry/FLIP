using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Flip.Core;

namespace Flip;

/// <summary>
/// Utility condivisa per la selezione a sosta dello sguardo (Dwell-to-Select) sui controlli WPF.
/// Disegna un indicatore arancione progressivo tramite <see cref="Adorner"/> senza alterare Tag/Child del controllo.
/// </summary>
internal static class GazeDwell
{
    internal static readonly Color AccentColor = Color.FromRgb(0xFF, 0x7A, 0x18);

    /// <summary>Costruisce un arco circolare orario per la frazione [0..1].</summary>
    internal static Geometry? CreateArcGeometry(Point center, double radius, double fraction)
    {
        if (fraction <= 0.005 || radius <= 0) return null;
        if (fraction >= 0.998)
            return new EllipseGeometry(center, radius, radius);

        double sweepDeg = Math.Clamp(fraction, 0.005, 0.998) * 360.0;
        double endRad = (sweepDeg - 90.0) * Math.PI / 180.0;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, sweepDeg > 180.0, SweepDirection.Clockwise, true));
        return new PathGeometry([fig]);
    }

    /// <summary>
    /// Collega il supporto Dwell-to-Select a un controllo (es. <see cref="Border"/> pill in QuickLook),
    /// tollerando i micro-movimenti del cursore con isteresi di 8px e richiedendo l'uscita dopo il trigger.
    /// </summary>
    internal static void Attach(
        FrameworkElement element,
        Func<int> getDwellMilliseconds,
        Action onTrigger,
        Func<bool>? canTrigger = null,
        double cornerRadius = 8)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(getDwellMilliseconds);
        ArgumentNullException.ThrowIfNull(onTrigger);

        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
        DwellAdorner? adorner = null;
        AdornerLayer? layer = null;
        long enterTimestamp = 0;
        bool inside = false;
        bool needsReenter = false;
        double progress = 0;

        void RemoveAdorner()
        {
            if (adorner != null && layer != null)
            {
                try { layer.Remove(adorner); } catch { }
            }
            adorner = null;
            layer = null;
        }

        void RenderVisual(double fraction)
        {
            progress = fraction;
            if (fraction <= 0.005)
            {
                if (adorner != null) adorner.Progress = 0;
                return;
            }
            if (adorner == null)
            {
                layer = AdornerLayer.GetAdornerLayer(element);
                if (layer != null)
                {
                    adorner = new DwellAdorner(element, cornerRadius);
                    layer.Add(adorner);
                }
            }
            if (adorner != null)
                adorner.Progress = fraction;
        }

        void Reset(bool clearReenter)
        {
            timer.Stop();
            inside = false;
            enterTimestamp = 0;
            if (clearReenter) needsReenter = false;
            if (progress > 0) RenderVisual(0);
            RemoveAdorner();
        }

        bool IsPointerWithin(double margin)
        {
            if (!element.IsVisible || !element.IsEnabled) return false;
            if (PresentationSource.FromVisual(element) == null) return false;
            if (!Native.GetCursorPos(out var pt)) return false;
            try
            {
                var local = element.PointFromScreen(new Point(pt.X, pt.Y));
                var bounds = new Rect(-margin, -margin, element.ActualWidth + margin * 2, element.ActualHeight + margin * 2);
                return bounds.Contains(local);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        timer.Tick += (_, _) =>
        {
            if (!element.IsVisible || !element.IsEnabled || Mouse.Captured != null)
            {
                Reset(clearReenter: true);
                return;
            }

            if (!IsPointerWithin(margin: 8))
            {
                Reset(clearReenter: true);
                return;
            }

            if (canTrigger != null && !canTrigger())
            {
                enterTimestamp = 0;
                RenderVisual(0);
                return;
            }

            if (needsReenter)
            {
                RenderVisual(0);
                return;
            }

            if (enterTimestamp == 0)
                enterTimestamp = Stopwatch.GetTimestamp();

            double elapsedMs = Stopwatch.GetElapsedTime(enterTimestamp).TotalMilliseconds;
            int dwellMs = Math.Clamp(getDwellMilliseconds(), 600, 2500);
            double fraction = Math.Clamp(elapsedMs / dwellMs, 0, 1);
            RenderVisual(fraction);

            if (fraction >= 1.0)
            {
                needsReenter = true;
                enterTimestamp = 0;
                RenderVisual(0);
                RemoveAdorner();
                onTrigger();
            }
        };

        void EnsureTracking()
        {
            if (inside) return;
            if (!element.IsVisible || !element.IsEnabled) return;
            inside = true;
            if (!timer.IsEnabled) timer.Start();
        }

        element.MouseEnter += (_, _) => EnsureTracking();
        element.MouseMove += (_, _) => EnsureTracking();
        element.Unloaded += (_, _) => Reset(clearReenter: true);
        element.IsVisibleChanged += (_, _) =>
        {
            if (!element.IsVisible) Reset(clearReenter: true);
        };
    }

    private sealed class DwellAdorner : Adorner
    {
        private readonly double _cornerRadius;
        private double _progress;

        private static readonly Brush BarBrush = CreateFrozenBrush(AccentColor);
        private static readonly Pen GlowPen = CreateFrozenPen(Color.FromArgb(190, 0xFF, 0x7A, 0x18), 1.6);

        internal DwellAdorner(UIElement adornedElement, double cornerRadius) : base(adornedElement)
        {
            _cornerRadius = cornerRadius;
            IsHitTestVisible = false;
        }

        internal double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) < 0.004) return;
                _progress = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_progress <= 0.005) return;
            var size = AdornedElement.RenderSize;
            if (size.Width <= 2 || size.Height <= 2) return;

            var outer = new Rect(0.5, 0.5, Math.Max(0, size.Width - 1), Math.Max(0, size.Height - 1));
            drawingContext.DrawRoundedRectangle(null, GlowPen, outer, _cornerRadius, _cornerRadius);

            double pad = 4;
            double maxW = Math.Max(4, size.Width - pad * 2);
            double barW = maxW * Math.Clamp(_progress, 0, 1);
            var barRect = new Rect(pad, Math.Max(1, size.Height - 5), barW, 3.5);
            drawingContext.DrawRoundedRectangle(BarBrush, null, barRect, 1.8, 1.8);
        }

        private static SolidColorBrush CreateFrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Pen CreateFrozenPen(Color c, double thickness)
        {
            var p = new Pen(CreateFrozenBrush(c), thickness);
            p.Freeze();
            return p;
        }
    }
}

/// <summary>
/// Bollino circolare flottante sul bordo destro per l'accessibilità Tobii Eye Tracker / Senza Mani.
/// Apre la Ruota in modalità "Sticky" al completamento della sosta dello sguardo (<see cref="FlipSettings.GazeDwellMilliseconds"/>),
/// leggendo i file selezionati da Esplora File, Desktop o Appunti senza rubare il focus attivo.
/// </summary>
internal sealed class GazeOrbWindow : Window
{
    internal const double OrbSize = 60.0;
    internal const double RestOpacity = 0.35;
    internal const double ProximityRadius = 90.0;
    private static readonly Point OrbCenter = new(OrbSize / 2, OrbSize / 2);
    private const double EnterRadius = 30.0;
    private const double LeaveRadius = 34.0; // tolleranza micro-movimenti oculari
    private const double DragThresholdPx = 6.0;

    private readonly Func<int> _getDwellMs;
    private readonly Func<bool> _canActivate;
    private readonly Action<string[]> _onActivated;

    private readonly System.Windows.Shapes.Path _progressArc;
    private readonly Ellipse _circle;
    private readonly DispatcherTimer _dwellTimer;
    private readonly DispatcherTimer _shellTrackerTimer;
    private readonly DispatcherTimer _proximityTimer;

    private static nint _lastActiveShellHwnd;
    private static string _lastActiveShellClass = "";
    private static uint _initialClipboardSeq = Native.GetClipboardSequenceNumber();
    private static uint _lastObservedClipboardSeq = _initialClipboardSeq;
    private static long _lastClipboardChangeTick;

    private long _dwellStartStamp;
    private bool _needsReenter;
    private bool _suspended;
    private bool _leftPressed;
    private bool _isDraggingVertically;
    private double _dragStartScreenY;
    private double _dragStartWindowTop;
    private double _targetOpacity = RestOpacity;
    private double? _dockedTop;

    internal GazeOrbWindow(
        Func<int> getDwellMs,
        Func<bool> canActivate,
        Action<string[]> onDwellActivated)
    {
        _getDwellMs = getDwellMs ?? throw new ArgumentNullException(nameof(getDwellMs));
        _canActivate = canActivate ?? throw new ArgumentNullException(nameof(canActivate));
        _onActivated = onDwellActivated ?? throw new ArgumentNullException(nameof(onDwellActivated));

        Title = "FLIP Gaze Orb";
        Width = OrbSize;
        Height = OrbSize;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Opacity = RestOpacity;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ToolTip = GetOrbTooltip();

        var canvas = new Canvas { Width = OrbSize, Height = OrbSize };

        _circle = new Ellipse
        {
            Width = 58,
            Height = 58,
            Fill = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x18)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x39)),
            StrokeThickness = 2
        };
        Canvas.SetLeft(_circle, 1);
        Canvas.SetTop(_circle, 1);
        canvas.Children.Add(_circle);

        _progressArc = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x18)),
            StrokeThickness = 3.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        canvas.Children.Add(_progressArc);

        var logo = new TextBlock
        {
            Text = "F",
            Width = OrbSize,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Black,
            FontSize = 25,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x18)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(logo, 0);
        Canvas.SetTop(logo, 13);
        canvas.Children.Add(logo);

        Content = canvas;

        _dwellTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _dwellTimer.Tick += OnDwellTick;

        _shellTrackerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _shellTrackerTimer.Tick += (_, _) => TrackForegroundShellAndClipboard();
        _shellTrackerTimer.Start();
        TrackForegroundShellAndClipboard();

        _proximityTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _proximityTimer.Tick += (_, _) => UpdateAutoDockOpacity();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _proximityTimer.Start();
            else _proximityTimer.Stop();
        };

        MouseEnter += (_, _) =>
        {
            TrackForegroundShellAndClipboard();
            UpdateAutoDockOpacity();
            StartDwellIfReady();
        };
        MouseMove += (_, e) =>
        {
            if (_leftPressed && e.LeftButton == MouseButtonState.Pressed && Native.GetCursorPos(out var cur))
            {
                double currentY = ToDipY(cur.Y);
                double deltaY = currentY - _dragStartScreenY;
                if (!_isDraggingVertically && Math.Abs(deltaY) >= DragThresholdPx)
                {
                    _isDraggingVertically = true;
                    ResetDwell(clearReenter: true);
                    ApplyOpacityAnimation(1.0);
                }

                if (_isDraggingVertically)
                {
                    var work = SystemParameters.WorkArea;
                    Left = work.Right - Width - 2;
                    double nextTop = Math.Clamp(_dragStartWindowTop + deltaY, work.Top + 8, work.Bottom - Height - 8);
                    Top = nextTop;
                    _dockedTop = nextTop;
                    return;
                }
            }

            StartDwellIfReady();
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            TrackForegroundShellAndClipboard();
            _leftPressed = true;
            _isDraggingVertically = false;
            _dragStartWindowTop = Top;
            _dragStartScreenY = Native.GetCursorPos(out var cur) ? ToDipY(cur.Y) : Top;
            CaptureMouse();
            ApplyOpacityAnimation(1.0);
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_leftPressed) return;
            _leftPressed = false;
            if (IsMouseCaptured) ReleaseMouseCapture();

            if (_isDraggingVertically)
            {
                _isDraggingVertically = false;
                UpdateAutoDockOpacity();
                e.Handled = true;
                return;
            }

            e.Handled = true;
            TrackForegroundShellAndClipboard();
            TriggerSelectionNow();
        };
        LostMouseCapture += (_, _) =>
        {
            _leftPressed = false;
            _isDraggingVertically = false;
        };

        ContextMenu = BuildDarkContextMenu();
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ResetDwell(clearReenter: true);
            ApplyOpacityAnimation(1.0);
            ToolTip = GetOrbTooltip();
            ContextMenu = BuildDarkContextMenu();
            if (ContextMenu != null)
            {
                ContextMenu.PlacementTarget = this;
                ContextMenu.IsOpen = true;
            }
        };

        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            Native.SetWindowNoActivate(handle);
            if (HwndSource.FromHwnd(handle) is { } hwndSource)
            {
                hwndSource.AddHook((nint _, int msg, nint _, nint _, ref bool handled) =>
                {
                    const int WM_MOUSEACTIVATE = 0x0021;
                    const int MA_NOACTIVATE = 3;
                    if (msg == WM_MOUSEACTIVATE)
                    {
                        handled = true;
                        return new nint(MA_NOACTIVATE);
                    }
                    return 0;
                });
            }
            PositionOnRightEdge();
        };
        Closed += (_, _) =>
        {
            _dwellTimer.Stop();
            _shellTrackerTimer.Stop();
            _proximityTimer.Stop();
        };
    }

    internal static void TrackForegroundShellAndClipboard()
    {
        uint seq = Native.GetClipboardSequenceNumber();
        if (seq != _lastObservedClipboardSeq)
        {
            _lastObservedClipboardSeq = seq;
            _lastClipboardChangeTick = Environment.TickCount64;
        }

        nint fg = Native.GetForegroundWindow();
        if (fg == 0) return;
        nint fgRoot = Native.GetAncestor(fg, 2);
        if (fgRoot == 0) fgRoot = fg;
        string cls = GetWindowClass(fgRoot);
        if (cls is "Progman" or "WorkerW" or "CabinetWClass" or "ExploreWClass")
        {
            _lastActiveShellHwnd = fgRoot;
            _lastActiveShellClass = cls;
        }
    }

    internal void PositionOnRightEdge()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 2;
        double defaultTop = work.Top + (work.Height - Height) / 2.0;
        Top = Math.Clamp(_dockedTop ?? defaultTop, work.Top + 8, work.Bottom - Height - 8);
    }

    internal double ComputeOpacityForDistance(double distanceFromCenter)
    {
        if (_suspended) return 0.28;
        if (_isDraggingVertically || ContextMenu?.IsOpen == true) return 1.0;
        return distanceFromCenter <= ProximityRadius ? 1.0 : RestOpacity;
    }

    private double ToDipY(int screenY)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice is { } fromDevice)
            return fromDevice.Transform(new Point(0, screenY)).Y;
        return screenY;
    }

    private void UpdateAutoDockOpacity()
    {
        if (!IsVisible || PresentationSource.FromVisual(this) == null) return;
        if (_suspended)
        {
            ApplyOpacityAnimation(0.28);
            return;
        }
        if (_isDraggingVertically || ContextMenu?.IsOpen == true)
        {
            ApplyOpacityAnimation(1.0);
            return;
        }
        if (!Native.GetCursorPos(out var pt)) return;
        try
        {
            Point local = PointFromScreen(new Point(pt.X, pt.Y));
            double dist = (local - OrbCenter).Length;
            ApplyOpacityAnimation(ComputeOpacityForDistance(dist));
        }
        catch (InvalidOperationException) { }
    }

    private void ApplyOpacityAnimation(double nextOpacity)
    {
        if (Math.Abs(_targetOpacity - nextOpacity) < 0.01) return;
        _targetOpacity = nextOpacity;
        BeginAnimation(OpacityProperty, new DoubleAnimation(nextOpacity, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    internal IReadOnlyList<string> GetContextMenuHeadersForTest() =>
        ContextMenu is null
            ? []
            : ContextMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToArray();

    internal void DisableGazeModeFromMenu(SettingsStore? store = null)
    {
        (store ?? SettingsStore.Current).Update(s => s.GazeAccessibilityMode = false);
        Close();
    }

    private static string GetOrbTooltip() => Loc.T(
        "FLIP Senza Mani · Fissa o clicca per aprire la Ruota · Trascina in verticale per spostare · Tasto destro per opzioni",
        "FLIP Hands-Free · Dwell or click to open the Wheel · Drag vertically to move · Right-click for options");

    private ContextMenu BuildDarkContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1F)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x39)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        var hideItem = new MenuItem
        {
            Header = Loc.T("Disattiva Modalità Tobii (Nascondi [F])", "Disable Tobii Mode (Hide [F])"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 6, 8, 6)
        };
        hideItem.Click += (_, _) => DisableGazeModeFromMenu();

        var settingsItem = new MenuItem
        {
            Header = Loc.T("Apri Impostazioni Accessibilità...", "Open Accessibility Settings…"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x18)),
            FontWeight = FontWeights.SemiBold,
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 6, 8, 6)
        };
        settingsItem.Click += (_, _) =>
        {
            ResetDwell(clearReenter: true);
            Flip.SettingsHub.SettingsHubWindow.ShowSingleton(1, null, null, null);
        };

        menu.Items.Add(hideItem);
        menu.Items.Add(new Separator
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x39)),
            Margin = new Thickness(4, 2, 4, 2)
        });
        menu.Items.Add(settingsItem);
        menu.Closed += (_, _) => UpdateAutoDockOpacity();
        return menu;
    }

    internal void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended)
        {
            ResetDwell(clearReenter: false);
            ApplyOpacityAnimation(0.28);
        }
        else
        {
            UpdateAutoDockOpacity();
        }
    }

    private void StartDwellIfReady()
    {
        if (_suspended || !_dwellTimer.IsEnabled)
        {
            if (!_suspended) _dwellTimer.Start();
        }
    }

    private void OnDwellTick(object? sender, EventArgs e)
    {
        if (_suspended || !IsVisible || PresentationSource.FromVisual(this) == null)
        {
            ResetDwell(clearReenter: true);
            return;
        }

        if (!Native.GetCursorPos(out var pt))
        {
            ResetDwell(clearReenter: true);
            return;
        }

        Point local;
        try
        {
            local = PointFromScreen(new Point(pt.X, pt.Y));
        }
        catch (InvalidOperationException)
        {
            ResetDwell(clearReenter: true);
            return;
        }

        double dist = (local - OrbCenter).Length;
        double toleranceRadius = _dwellStartStamp > 0 || _needsReenter ? LeaveRadius : EnterRadius;
        if (dist > toleranceRadius)
        {
            ResetDwell(clearReenter: true);
            return;
        }

        if (!_canActivate() || _needsReenter)
        {
            _dwellStartStamp = 0;
            UpdateProgressVisual(0);
            return;
        }

        if (_dwellStartStamp == 0)
        {
            _dwellStartStamp = Stopwatch.GetTimestamp();
            _circle.Stroke = new SolidColorBrush(Color.FromRgb(0x43, 0x4A, 0x5C));
        }

        double elapsedMs = Stopwatch.GetElapsedTime(_dwellStartStamp).TotalMilliseconds;
        int dwellMs = Math.Clamp(_getDwellMs(), 600, 2500);
        double fraction = Math.Clamp(elapsedMs / dwellMs, 0, 1);
        UpdateProgressVisual(fraction);

        if (fraction >= 1.0)
        {
            TriggerSelectionNow();
        }
    }

    private void TriggerSelectionNow()
    {
        if (_suspended || !_canActivate()) return;
        _needsReenter = true;
        _dwellStartStamp = 0;
        UpdateProgressVisual(0);
        _circle.Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x39));

        string[] files = CaptureSelectedFiles();
        Evidence.Log("gaze.orb.activate", new { count = files.Length });
        _onActivated(files);
    }

    private void ResetDwell(bool clearReenter)
    {
        _dwellTimer.Stop();
        _dwellStartStamp = 0;
        if (clearReenter) _needsReenter = false;
        UpdateProgressVisual(0);
        _circle.Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x39));
    }

    private void UpdateProgressVisual(double fraction)
    {
        _progressArc.Data = GazeDwell.CreateArcGeometry(OrbCenter, 27.0, fraction);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  LETTURA SELEZIONE DA ESPLORA FILE / DESKTOP (COM) / APPUNTI
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object? Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
        void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, ref object pvarLoc);
        void OnActivated(int lCookie, bool fActive);
        [return: MarshalAs(UnmanagedType.IDispatch)] object? FindWindowSW(ref object pvarloc, ref object pvarlocRoot, int swClass, out int pHWND, int swfwOptions);
        void OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
        void ProcessAttachDetach(bool fAttach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out nint ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
        [PreserveSig] int InsertMenusSB(nint hmenuShared, nint lpMenuWidths);
        [PreserveSig] int SetMenuSB(nint hmenuShared, nint holemenuRes, nint hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(nint hmenuShared);
        [PreserveSig] int SetStatusTextSB(nint pszStatusText);
        [PreserveSig] int EnableModelessSB(bool fEnable);
        [PreserveSig] int TranslateAcceleratorSB(nint pmsg, ushort wID);
        [PreserveSig] int BrowseObject(nint pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out nint ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out nint phwnd);
        [PreserveSig] int SendControlMsg(uint id, uint uMsg, nint wParam, nint lParam, out nint pret);
        [PreserveSig] int QueryActiveShellView(out nint ppshv);
        [PreserveSig] int OnViewWindowActive(nint pshv);
        [PreserveSig] int SetToolbarItems(nint lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        [PreserveSig] int GetWindow(out nint phwnd);
    }

    /// <summary>Filtra una lista di percorsi selezionati applicando le regole locali di <see cref="Policy.ReadFiles"/>.</summary>
    internal static string[] FilterLocalSelection(IEnumerable<string?> candidatePaths, bool allowFolders = false)
    {
        var filesOnly = new List<string>();
        var folders = new List<string>();
        foreach (string? raw in candidatePaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                if (!System.IO.Path.IsPathFullyQualified(raw)) continue;
                if (Directory.Exists(raw))
                {
                    if (allowFolders) folders.Add(raw);
                }
                else if (File.Exists(raw))
                {
                    var attrs = File.GetAttributes(raw);
                    if ((attrs & FileAttributes.Directory) == 0)
                        filesOnly.Add(raw);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        var accepted = new List<string>(folders);
        if (filesOnly.Count > 0)
        {
            var data = new DataObject(DataFormats.FileDrop, filesOnly.ToArray());
            accepted.AddRange(Policy.ReadFiles(data));
        }
        return accepted.ToArray();
    }

    /// <summary>
    /// Legge i file attualmente selezionati nella finestra in primo piano (Desktop o Esplora File)
    /// o in fallback dalla lista FileDrop copiata di recente negli Appunti.
    /// </summary>
    internal static string[] CaptureSelectedFiles(bool allowClipboardFallback = true, bool allowFolders = false)
    {
        TrackForegroundShellAndClipboard();

        nint fg = Native.GetForegroundWindow();
        nint fgRoot = fg != 0 ? Native.GetAncestor(fg, 2) : 0; // GA_ROOT
        if (fgRoot == 0) fgRoot = fg;
        string fgClass = GetWindowClass(fgRoot);

        // Se la finestra in primo piano è FLIP stesso o la barra di sistema, usa l'ultima finestra Shell attiva visibile.
        if (fgClass is not ("Progman" or "WorkerW" or "CabinetWClass" or "ExploreWClass") &&
            _lastActiveShellHwnd != 0 &&
            Native.IsWindowVisible(_lastActiveShellHwnd) &&
            !Native.IsIconic(_lastActiveShellHwnd))
        {
            fgRoot = _lastActiveShellHwnd;
            fgClass = _lastActiveShellClass;
        }

        bool fgIsDesktop = fgClass is "Progman" or "WorkerW";
        bool fgIsExplorer = fgClass is "CabinetWClass" or "ExploreWClass";

        IShellWindows? shellWindows = null;
        try
        {
            var swType = Type.GetTypeFromCLSID(ClsidShellWindows);
            if (swType != null)
            {
                shellWindows = Activator.CreateInstance(swType) as IShellWindows;
            }

            if (shellWindows != null)
            {
                // 1. Se il Desktop è in primo piano, leggi PRIMA la selezione del Desktop
                //    così una finestra di Esplora File rimasta aperta dietro non ruba mai la selezione!
                if (fgIsDesktop)
                {
                    var desktopFiles = ReadDesktopSelection(shellWindows, allowFolders);
                    if (desktopFiles.Length > 0) return desktopFiles;
                }

                // 2. Se Esplora File è in primo piano, leggi PRIMA la selezione di quell'HWND specifico.
                if (fgIsExplorer)
                {
                    var activeExplorerFiles = ReadExplorerSelectionForHwnd(shellWindows, fgRoot, allowFolders);
                    if (activeExplorerFiles.Length > 0) return activeExplorerFiles;
                }

                // 3. Se non era in primo piano Esplora File, controlla prima il Desktop.
                if (!fgIsDesktop && !fgIsExplorer)
                {
                    var desktopFiles = ReadDesktopSelection(shellWindows, allowFolders);
                    if (desktopFiles.Length > 0) return desktopFiles;
                }

                // 4. Fallback: scorri le finestre di Esplora File in Z-order visibili e non ridotte a icona.
                foreach (nint explorerHwnd in EnumerateVisibleExplorerWindowsZOrder())
                {
                    if (explorerHwnd == fgRoot) continue;
                    var files = ReadExplorerSelectionForHwnd(shellWindows, explorerHwnd, allowFolders);
                    if (files.Length > 0) return files;
                }

                // 5. Fallback sul Desktop se l'Explorer attivo era vuoto ma c'è un file selezionato sul Desktop.
                if (fgIsExplorer)
                {
                    var desktopFiles = ReadDesktopSelection(shellWindows, allowFolders);
                    if (desktopFiles.Length > 0) return desktopFiles;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Evidence.Log("gaze.selection.com-error", ex.Message);
        }
        finally
        {
            SafeReleaseCom(shellWindows);
        }

        // 6. Ultimo fallback: FileDrop negli Appunti SOLO se copiato negli ultimi 12 secondi.
        bool isRecentClipboard = _lastClipboardChangeTick > 0 &&
            (Environment.TickCount64 - _lastClipboardChangeTick) <= 12_000;
        if (allowClipboardFallback && isRecentClipboard)
        {
            try
            {
                if (Clipboard.ContainsFileDropList() && Clipboard.GetDataObject() is { } clipObj)
                    return Policy.ReadFiles(clipObj);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                Evidence.Log("gaze.selection.clipboard-error", ex.Message);
            }
        }

        return [];
    }

    private static string[] ReadDesktopSelection(IShellWindows shellWindows, bool allowFolders = false)
    {
        object missing = Type.Missing;
        object? desktopWin = null;
        try
        {
            const int SwcDesktop = 0x08;
            const int SwfoNeedDispatch = 0x01;
            desktopWin = shellWindows.FindWindowSW(ref missing, ref missing, SwcDesktop, out _, SwfoNeedDispatch);
            return desktopWin != null ? ExtractSelectedFilesFromShellWindow(desktopWin, allowFolders) : [];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return [];
        }
        finally
        {
            SafeReleaseCom(desktopWin);
        }
    }

    private static string[] ReadExplorerSelectionForHwnd(IShellWindows shellWindows, nint targetHwnd, bool allowFolders = false)
    {
        var matchingTabs = new List<object>();
        try
        {
            int count = shellWindows.Count;
            for (int i = 0; i < count; i++)
            {
                object? winObj = null;
                try
                {
                    winObj = shellWindows.Item(i);
                    if (winObj == null) continue;
                    dynamic win = winObj;
                    nint winHwnd = new((long)win.HWND);
                    if (winHwnd == targetHwnd)
                    {
                        matchingTabs.Add(winObj);
                        winObj = null; // trasferita ownership alla lista
                    }
                }
                catch { }
                finally
                {
                    if (winObj != null) SafeReleaseCom(winObj);
                }
            }

            if (matchingTabs.Count == 0) return [];

            // Su Windows 11 con schede multiple in Explorer, preferisci il tab la cui ShellView è visibile.
            object? visibleTab = null;
            if (matchingTabs.Count > 1)
            {
                visibleTab = matchingTabs.FirstOrDefault(tab =>
                {
                    nint viewHwnd = TryGetShellViewHwnd(tab);
                    return viewHwnd != 0 && Native.IsWindowVisible(viewHwnd);
                });
            }

            if (visibleTab != null)
            {
                var files = ExtractSelectedFilesFromShellWindow(visibleTab, allowFolders);
                if (files.Length > 0) return files;
            }

            foreach (object tab in matchingTabs)
            {
                if (ReferenceEquals(tab, visibleTab)) continue;
                var files = ExtractSelectedFilesFromShellWindow(tab, allowFolders);
                if (files.Length > 0) return files;
            }

            return [];
        }
        finally
        {
            foreach (object tab in matchingTabs) SafeReleaseCom(tab);
        }
    }

    private static nint TryGetShellViewHwnd(object shellWindow)
    {
        nint pBrowser = 0;
        nint pView = 0;
        try
        {
            if (shellWindow is not IComServiceProvider sp) return 0;
            Guid sid = IidIShellBrowser;
            Guid iid = IidIShellBrowser;
            if (sp.QueryService(ref sid, ref iid, out pBrowser) < 0 || pBrowser == 0) return 0;

            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
            if (browser.QueryActiveShellView(out pView) < 0 || pView == 0) return 0;

            var view = (IShellView)Marshal.GetObjectForIUnknown(pView);
            return view.GetWindow(out nint h) >= 0 ? h : 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (pView != 0) Marshal.Release(pView);
            if (pBrowser != 0) Marshal.Release(pBrowser);
        }
    }

    private static string[] ExtractSelectedFilesFromShellWindow(object winObj, bool allowFolders = false)
    {
        object? doc = null;
        object? selected = null;
        var candidates = new List<string>();
        try
        {
            dynamic win = winObj;
            doc = win.Document;
            if (doc == null) return [];
            selected = ((dynamic)doc).SelectedItems();
            if (selected == null) return [];

            dynamic sel = selected;
            int count = (int)sel.Count;
            for (int i = 0; i < count && i < 128; i++)
            {
                object? item = null;
                try
                {
                    item = sel.Item(i);
                    if (item == null) continue;
                    string? path = ((dynamic)item).Path as string;
                    if (!string.IsNullOrWhiteSpace(path)) candidates.Add(path);
                }
                catch { }
                finally
                {
                    SafeReleaseCom(item);
                }
            }
        }
        catch { }
        finally
        {
            SafeReleaseCom(selected);
            SafeReleaseCom(doc);
        }

        return FilterLocalSelection(candidates, allowFolders);
    }

    private static IEnumerable<nint> EnumerateVisibleExplorerWindowsZOrder()
    {
        const uint GwHwndNext = 2;
        nint current = Native.GetTopWindow(0);
        int guard = 0;
        while (current != 0 && guard++ < 512)
        {
            if (Native.IsWindowVisible(current) && !Native.IsIconic(current))
            {
                string cls = GetWindowClass(current);
                if (cls is "CabinetWClass" or "ExploreWClass")
                    yield return current;
            }
            current = Native.GetWindow(current, GwHwndNext);
        }
    }

    private static string GetWindowClass(nint hwnd)
    {
        if (hwnd == 0) return "";
        var sb = new StringBuilder(128);
        int len = Native.GetClassNameW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
    }

    private static void SafeReleaseCom(object? obj)
    {
        if (obj != null && Marshal.IsComObject(obj))
        {
            try { Marshal.ReleaseComObject(obj); } catch { }
        }
    }
}
