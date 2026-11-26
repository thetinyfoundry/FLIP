using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flip;

/// <summary>
/// Notifica visibile in superficie. Il balloon della tray è facile da perdere (focus assist,
/// icona nell'area di overflow): questo riquadro compare sempre in alto sopra le finestre.
/// </summary>
internal sealed class ToastWindow : Window
{
    readonly TextBlock text = new()
    {
        Foreground = Brushes.White,
        FontSize = 12.5,
        LineHeight = 17,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 340
    };
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(5000) };

    public ToastWindow()
    {
        Title = "Flip";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 20, 22, 25)),
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
    }

    /// <summary>Mostra il messaggio in basso a destra, senza rubare il focus.</summary>
    public void ShowMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        text.Text = message.Length > 250 ? message[..250] : message;
        if (!IsVisible) Show();
        UpdateLayout();
        var work = System.Windows.Forms.SystemInformation.WorkingArea;
        Left = work.Right - ActualWidth - 24;
        Top = work.Bottom - ActualHeight - 24;
        hideTimer.Stop();
        hideTimer.Start();
    }

    internal string MessageText => text.Text;

    public void CloseToast()
    {
        hideTimer.Stop();
        if (IsVisible) Hide();
    }
}
