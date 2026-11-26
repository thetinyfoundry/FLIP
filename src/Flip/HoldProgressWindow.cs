using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Flip;

/// <summary>
/// Anello progressivo trasparente e click-through mostrato sotto al cursore mentre si
/// tiene premuto il pulsante per l'apertura della ruota. Riutilizza la geometria
/// circolare già usata dall'indicatore di dwell dello sguardo.
/// </summary>
internal sealed class HoldProgressWindow : Window
{
    const double Size = 64;
    const double Center = Size / 2;
    const double Radius = 22;

    const int GWL_EXSTYLE = -20;
    const nint WS_EX_TRANSPARENT = 0x00000020;
    const nint WS_EX_TOOLWINDOW = 0x00000080;
    const nint WS_EX_LAYERED = 0x00080000;
    const nint WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    static HoldProgressWindow? current;

    readonly Path arc = new()
    {
        Stroke = new SolidColorBrush(GazeDwell.AccentColor),
        StrokeThickness = 3,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Fill = null,
        IsHitTestVisible = false
    };

    HoldProgressWindow()
    {
        Title = "FLIP Hold";
        Width = Size;
        Height = Size;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        var track = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            StrokeThickness = 3,
            Fill = null,
            IsHitTestVisible = false
        };
        var canvas = new Canvas { Width = Size, Height = Size, IsHitTestVisible = false };
        Canvas.SetLeft(track, Center - Radius);
        Canvas.SetTop(track, Center - Radius);
        track.Width = track.Height = Radius * 2;
        canvas.Children.Add(track);
        canvas.Children.Add(arc);
        Content = canvas;
        SetProgress(0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint handle = new WindowInteropHelper(this).Handle;
        nint ex = GetWindowLongPtr(handle, GWL_EXSTYLE);
        SetWindowLongPtr(handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    void SetProgress(double fraction)
    {
        try
        {
            arc.Data = GazeDwell.CreateArcGeometry(new Point(Center, Center), Radius, Math.Clamp(fraction, 0, 1));
        }
        catch (Exception ex)
        {
            Evidence.Log("hold.progress.error", ex.Message);
        }
    }

    void Position()
    {
        try
        {
            nint handle = new WindowInteropHelper(this).Handle;
            if (handle != 0) Native.PlaceCenteredAtCursor(handle, Size);
        }
        catch (Exception ex)
        {
            Evidence.Log("hold.position.error", ex.Message);
        }
    }

    internal static void Begin()
    {
        if (ScreenEdgeAuraWindow.Suppressed) return;
        try
        {
            End();
            current = new HoldProgressWindow();
            current.Show();
            current.Position();
        }
        catch (Exception ex)
        {
            Evidence.Log("hold.progress.begin.error", ex.Message);
            current = null;
        }
    }

    internal static void Update(double fraction)
    {
        if (current is { } window && !window.Dispatcher.HasShutdownStarted)
            window.SetProgress(fraction);
    }

    internal static void End()
    {
        var window = current;
        current = null;
        try
        {
            window?.Close();
        }
        catch (Exception ex)
        {
            Evidence.Log("hold.progress.end.error", ex.Message);
        }
    }
}
