using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flip;

/// <summary>
/// Notifica visibile in superficie. Il balloon della tray è facile da perdere (focus assist,
/// icona nell'area di overflow): questo riquadro compare in basso a destra sul monitor del cursore,
/// sopra le finestre, senza rubare il focus.
/// </summary>
internal sealed class ToastWindow : Window
{
    const double ToastWidth = 380, EdgeMargin = 24;

    readonly TextBlock text = new()
    {
        Foreground = Brushes.White,
        FontSize = 12.5,
        LineHeight = 17,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 340
    };
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(5000) };
    Action? clickAction;

    public ToastWindow()
    {
        Title = "Flip";
        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 0x12, 0x13, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x18)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Child = text
        };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            if (IsVisible) Hide();
        };
        // Un avviso con azione si apre al clic; gli altri si chiudono e basta.
        MouseLeftButtonUp += (_, _) =>
        {
            var action = clickAction;
            CloseToast();
            action?.Invoke();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.SetWindowNoActivate(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Mostra il messaggio in basso a destra sul monitor del cursore, senza rubare il focus.</summary>
    public void ShowMessage(string message, Action? onClick = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        clickAction = onClick;
        Cursor = onClick != null ? System.Windows.Input.Cursors.Hand : null;
        // Un avviso con azione resta più a lungo: va letto e deciso.
        hideTimer.Interval = TimeSpan.FromMilliseconds(onClick != null ? 9000 : 5000);
        text.Text = message.Length > 250 ? message[..250] : message;
        if (!IsVisible) Show();
        UpdateLayout();
        try
        {
            // Posizione in pixel fisici: Left/Top WPF sono in DIP e sbaglierebbero con la scala > 100%.
            Native.PlaceBottomRightOnCursorMonitor(new WindowInteropHelper(this).Handle, ActualWidth, ActualHeight, EdgeMargin);
        }
        catch (InvalidOperationException ex)
        {
            Evidence.Log("toast.position.error", ex.Message);
        }
        hideTimer.Stop();
        hideTimer.Start();
    }

    internal string MessageText => text.Text;

    public void CloseToast()
    {
        hideTimer.Stop();
        clickAction = null;
        if (IsVisible) Hide();
    }
}
