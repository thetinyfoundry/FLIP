using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Flip;

internal sealed class Wheel : Window
{
    readonly Canvas canvas = new() { Width = 420, Height = 420 };
    readonly TextBlock center = new()
    {
        Width = 172,
        MaxHeight = 96,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextAlignment = TextAlignment.Center,
        Foreground = Brushes.White,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        IsHitTestVisible = false
    };
    readonly List<System.Windows.Shapes.Path> sectors = [];
    static readonly string[] preferred =
    [
        "DOCX", "PDF", "HTML", "MD", "TXT", "RTF", "ODT", "EPUB", "FB2", "RST",
        "JSON", "YAML", "XML", "CSV",
        "JPG", "PNG", "WEBP", "BMP", "TIFF", "GIF",
        "MP4", "MP3", "WAV", "MKV", "WEBM", "AVI", "MOV", "FLAC", "OGG", "M4A", "AAC"
    ];
    string[] slots = [];
    string[] files = [], formats = [];
    int selected = -1;
    bool dropping, closing, converting;
    double displayedPercent, targetPercent;
    string activeFormat = "";
    System.Windows.Shapes.Path? progressArc;
    Line? progressRay;
    Ellipse? progressTip;
    DispatcherTimer? progressTimer;
    readonly Action<string> report;
    readonly Action<string[], string>? convert;
    readonly Action? playSound;
    readonly Action? cancelConversion;
    internal bool IsDragMode => dropping;
    internal bool IsConverting => converting;

    internal Wheel(Action<string> report, Action<string[], string>? convert = null, Action? playSound = null, Action? cancelConversion = null)
    {
        this.report = report; this.convert = convert; this.playSound = playSound; this.cancelConversion = cancelConversion;
        Title = "Flip";
        Width = Height = 320; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; AllowDrop = true;
        Content = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        PreviewDragEnter += Drag; PreviewDragOver += Drag;
        PreviewDragLeave += (_, _) =>
        {
            if (converting) return;
            Native.GetCursorPos(out var cursor);
            var point = PointFromScreen(new Point(cursor.X, cursor.Y));
            if (point.X < 0 || point.Y < 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
            {
                Highlight(-1);
                Evidence.Log("ole.leave");
            }
        };
        PreviewDrop += DropFiles;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Dismiss("escape-focused");
            else if (!converting && (e.Key is Key.Right or Key.Down || (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)))
            {
                Highlight((selected + 1) % Math.Max(1, formats.Length));
                e.Handled = true;
            }
            else if (!converting && (e.Key is Key.Left or Key.Up || (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)))
            {
                Highlight((selected - 1 + formats.Length) % Math.Max(1, formats.Length));
                e.Handled = true;
            }
            else if (!converting && e.Key is Key.Enter or Key.Space)
            {
                Choose(selected, "keyboard");
                e.Handled = true;
            }
        };
        MouseMove += (_, e) => { if (!dropping && !converting) Highlight(Hit(e.GetPosition(canvas))); };
        MouseLeftButtonUp += (_, e) => { if (!dropping && !converting) Choose(Hit(e.GetPosition(canvas)), "click"); };
        Closed += (_, _) => progressTimer?.Stop();
    }

    internal void Open(string[] sources, bool drag)
    {
        dropping = drag; converting = false; files = sources; formats = OrderFormats(Policy.Outputs(files)); closing = false;
        Paint(); Show(); Native.Position(new WindowInteropHelper(this).Handle);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        if (!drag) { Activate(); Focus(); }
        Evidence.Log("wheel.open", new { drag, files, formats });
    }

    void Paint()
    {
        canvas.Children.Clear(); sectors.Clear(); selected = -1;
        slots = formats.Length > 0 ? OrderFormats(formats) : ["DOCX", "WEBP", "PDF", "MD", "HTML", "EPUB", "JPG", "PNG"];
        var disk = new Ellipse
        {
            Width = 414,
            Height = 414,
            Fill = new SolidColorBrush(Color.FromArgb(214, 24, 26, 28)),
            Stroke = new SolidColorBrush(Color.FromArgb(145, 128, 131, 124)),
            StrokeThickness = 1.4
        };
        Canvas.SetLeft(disk, 3); Canvas.SetTop(disk, 3); canvas.Children.Add(disk);
        // Unknown payload cannot honestly expose compatible formats before OLE DragEnter.
        int n = slots.Length;
        for (int i = 0; i < n; i++)
        {
            double span = 360.0 / n, mid = i * span;
            Point At(double r, double deg) => new(210 + r * Math.Cos((deg - 90) * Math.PI / 180), 210 + r * Math.Sin((deg - 90) * Math.PI / 180));
            var path = new System.Windows.Shapes.Path
            {
                Data = SectorData(i, n),
                Fill = Rest(),
                Stroke = new SolidColorBrush(Color.FromArgb(140, 130, 133, 124)),
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round
            };
            bool enabled = formats.Contains(slots[i]);
            path.Opacity = enabled ? 1 : .38;
            sectors.Add(path); canvas.Children.Add(path);
            var pos = At(143, mid);
            var label = new TextBlock
            {
                Text = slots[i],
                Opacity = enabled ? 1 : .32,
                FontSize = n > 10 ? 16 : 19,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Width = 68,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, pos.X - 34); Canvas.SetTop(label, pos.Y - 12); canvas.Children.Add(label);
        }
        center.Text = (files.Length == 0 ? "Trascina qui il file" : files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} file")
            + "\n\nFlip\nEsc per annullare";
        if (files.Length > 0 && formats.Length == 0) center.Text = Policy.AvailabilityMessage(files);
        center.ToolTip = files.Length > 0 ? string.Join("\n", files) : "Avvia un trascinamento di file locali";
        Canvas.SetLeft(center, 124); Canvas.SetTop(center, 166); canvas.Children.Add(center);
    }

    internal static string[] OrderFormats(IEnumerable<string> outputs) => preferred.Where(outputs.Contains).Concat(outputs.Except(preferred)).ToArray();
    static Brush Rest() => new LinearGradientBrush(Color.FromArgb(200, 73, 77, 76), Color.FromArgb(190, 44, 48, 48), 90);

    internal static Geometry SectorData(int index, int count = 8)
    {
        double span = 360.0 / count, gap = Math.Min(2, span * .06);
        double start = index * span - span / 2 + gap, end = (index + 1) * span - span / 2 - gap;
        Point At(double r, double deg) => new(210 + r * Math.Cos((deg - 90) * Math.PI / 180), 210 + r * Math.Sin((deg - 90) * Math.PI / 180));
        var fig = new PathFigure { StartPoint = At(178, start), IsClosed = true };
        fig.Segments.Add(new QuadraticBezierSegment(At(185, start), At(185, start + 2.2), true));
        fig.Segments.Add(new ArcSegment(At(185, end - 2.2), new Size(185, 185), 0, end - start > 180, SweepDirection.Clockwise, true));
        fig.Segments.Add(new QuadraticBezierSegment(At(185, end), At(178, end), true));
        fig.Segments.Add(new LineSegment(At(105, end), true));
        fig.Segments.Add(new QuadraticBezierSegment(At(98, end), At(98, end - 4), true));
        fig.Segments.Add(new ArcSegment(At(98, start + 4), new Size(98, 98), 0, end - start > 180, SweepDirection.Counterclockwise, true));
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

    void StartProgressVisual(string chosenFormat)
    {
        converting = true;
        dropping = false;
        AllowDrop = false;
        activeFormat = chosenFormat;
        displayedPercent = 4;
        targetPercent = 12;

        for (int i = 0; i < sectors.Count; i++)
        {
            bool isTarget = slots[i] == chosenFormat;
            sectors[i].Fill = isTarget ? new SolidColorBrush(Color.FromRgb(255, 112, 12)) : Rest();
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
        canvas.Children.Add(progressTip);

        UpdateProgressVisual();

        progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        progressTimer.Tick += (_, _) =>
        {
            if (!converting || closing) return;
            if (displayedPercent < targetPercent)
                displayedPercent = Math.Min(targetPercent, displayedPercent + Math.Max(0.8, (targetPercent - displayedPercent) * 0.28));
            else if (displayedPercent < 88)
                displayedPercent += (88 - displayedPercent) * 0.025;
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

        string sourceLabel = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} file";
        if (sourceLabel.Length > 20) sourceLabel = sourceLabel[..19] + "…";
        int shownPct = Math.Clamp((int)Math.Round(displayedPercent), 1, 99);
        center.Text = $"{sourceLabel}\n→ {activeFormat}  ({shownPct}%)\nConversione in corso…\nEsc per annullare";
    }

    internal void SetConversionProgress(int percent, string? step = null)
    {
        if (!converting || closing) return;
        targetPercent = Math.Clamp(percent, (int)targetPercent, 99);
        if (targetPercent > displayedPercent)
            displayedPercent = Math.Max(displayedPercent, targetPercent - 8);
        UpdateProgressVisual();
    }

    internal void CompleteConversion(bool success)
    {
        if (closing) return;
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
        }
        string sourceLabel = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Length} file";
        if (sourceLabel.Length > 20) sourceLabel = sourceLabel[..19] + "…";
        center.Text = success
            ? $"{sourceLabel}\n✓ {activeFormat} (100%)\nCompletato!"
            : $"{sourceLabel}\n✕ {activeFormat}\nErrore conversione";

        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
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
        files = sources ?? [source]; formats = OrderFormats(Policy.Outputs(files)); Paint(); if (hover && formats.Length > 0) Highlight(0);
        canvas.Measure(new Size(420, 420)); canvas.Arrange(new Rect(0, 0, 420, 420)); canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(420, 420, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    int Hit(Point point)
    {
        for (int i = 0; i < sectors.Count; i++)
            if (formats.Contains(slots[i]) && sectors[i].Data.FillContains(point)) return Array.IndexOf(formats, slots[i]);
        return -1;
    }

    void Highlight(int index)
    {
        if (converting) return;
        if (index < 0 || index >= formats.Length) index = -1;
        if (selected == index) return;
        for (int i = 0; i < sectors.Count; i++) sectors[i].Fill = index >= 0 && slots[i] == formats[index] ? new SolidColorBrush(Color.FromRgb(255, 112, 12)) : Rest();
        selected = index;
        if (index >= 0) playSound?.Invoke();
    }

    void Drag(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (converting) return;
        try
        {
            var incoming = Policy.ReadFiles(e.Data);
            if (!files.SequenceEqual(incoming))
            {
                files = incoming; formats = OrderFormats(Policy.Outputs(files)); Paint();
                Evidence.Log("ole.payload", new { files, formats, availability = Policy.AvailabilityMessage(files) });
            }
            var index = Hit(e.GetPosition(canvas));
            Highlight(index);
            e.Effects = Policy.Effect(e.AllowedEffects, index >= 0 && files.Length > 0);
        }
        catch (Exception ex) { Evidence.Log("ole.error", ex.Message); }
    }

    void DropFiles(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (converting) return;
        try
        {
            var incoming = Policy.ReadFiles(e.Data);
            // Revalidate at the commit boundary; never trust cached DragOver data.
            if (!files.SequenceEqual(incoming)) { files = incoming; formats = OrderFormats(Policy.Outputs(files)); }
            var index = Hit(e.GetPosition(canvas));
            e.Effects = Policy.Effect(e.AllowedEffects, index >= 0 && files.Length > 0);
            if (e.Effects == DragDropEffects.Copy) Choose(index, "ole.drop");
            else Dismiss("rejected-drop");
        }
        catch (Exception ex) { report("Drop rejected: " + ex.Message); Dismiss("drop-error"); }
    }

    void Choose(int index, string via)
    {
        if (converting || index < 0 || index >= formats.Length) return;
        var selectedFiles = files.ToArray(); var output = formats[index];
        Evidence.Log("selection", new { via, files, output, effect = "Copy" });
        if (convert != null)
        {
            StartProgressVisual(output);
            Dispatcher.BeginInvoke(() => convert(selectedFiles, output));
        }
        else
        {
            Dismiss("selection");
        }
    }

    internal void Dismiss(string reason)
    {
        if (closing || !IsVisible) return;
        if (converting)
        {
            if (reason is "primary-button-up" or "watchdog") return;
            if (reason is "escape" or "escape-focused") cancelConversion?.Invoke();
            converting = false;
            progressTimer?.Stop();
        }
        closing = true; Evidence.Log("wheel.close", reason);
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(85));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }
}
