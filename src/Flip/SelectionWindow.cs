using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Flip;

internal sealed class SelectionWindow : Window
{
    readonly Host host;
    readonly ListBox filesList = new() { Height = 110, Margin = new Thickness(0, 10, 0, 12), Background = new SolidColorBrush(Color.FromRgb(36, 40, 44)), Foreground = Brushes.White };
    readonly WrapPanel formatsPanel = new() { Margin = new Thickness(0, 8, 0, 8) };
    readonly TextBox results = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 185, Margin = new Thickness(0, 12, 0, 0), Background = new SolidColorBrush(Color.FromRgb(36, 40, 44)), Foreground = Brushes.White };
    readonly TextBlock progress = new() { Text = "Scegli i file e uno o più formati.", TextWrapping = TextWrapping.Wrap };
    readonly Button run = new() { Content = "Converti", Padding = new Thickness(24, 9, 24, 9), IsEnabled = false };
    readonly List<CheckBox> boxes = [];
    readonly Button browse = new() { Content = "Scegli file…", Padding = new Thickness(15, 8, 15, 8), HorizontalAlignment = HorizontalAlignment.Left };
    bool busy;
    string[] paths = [];
    internal SelectionWindow(Host host)
    {
        this.host = host; Title = "Flip — conversione multipla"; Width = 760; Height = 680; MinWidth = 570; MinHeight = 570; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 32)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(26) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Flip", FontSize = 30, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Converti localmente. Le copie saranno salvate accanto agli originali.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        browse.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Tutti i file|*.*" }; if (picker.ShowDialog(this) == true) SetFiles(picker.FileNames); };
        panel.Children.Add(browse); panel.Children.Add(filesList);
        panel.Children.Add(new TextBlock { Text = "Formati di uscita", FontSize = 17, FontWeight = FontWeights.SemiBold }); panel.Children.Add(formatsPanel);
        panel.Children.Add(new TextBlock { Text = "PDF → documenti: solo testo estraibile, senza OCR o ricostruzione dell'impaginazione. PDF → immagini: una copia per pagina. GIF in ingresso: primo fotogramma.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; run.Click += (_, _) => host.Start(paths, boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToArray()); actions.Children.Add(run);
        var cancel = new Button { Content = "Annulla lavoro", Padding = new Thickness(15, 9, 15, 9), Margin = new Thickness(10, 0, 0, 0) }; cancel.Click += (_, _) => host.CancelJob(); actions.Children.Add(cancel);
        panel.Children.Add(actions); panel.Children.Add(progress); panel.Children.Add(results);
        var folder = new Button { Content = "Apri cartella del primo file", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 7, 12, 7) };
        folder.Click += (_, _) => { if (paths.Length > 0) Host.OpenFolder(Path.GetDirectoryName(paths[0])!); }; panel.Children.Add(folder);
    }
    internal void SetFiles(string[] files)
    {
        if (busy) { progress.Text = "Attendi il completamento prima di cambiare i file."; return; }
        paths = Policy.ReadFiles(new DataObject(DataFormats.FileDrop, files)); filesList.ItemsSource = paths.Select(Path.GetFileName).ToArray();
        boxes.Clear(); formatsPanel.Children.Clear();
        foreach (string format in Policy.Outputs(paths))
        {
            var check = new CheckBox { Content = format, Foreground = Brushes.White, Margin = new Thickness(0, 7, 22, 7), FontSize = 15 };
            check.Checked += (_, _) => UpdateRun(); check.Unchecked += (_, _) => UpdateRun(); boxes.Add(check); formatsPanel.Children.Add(check);
        }
        progress.Text = paths.Length == 0 ? "Nessun file locale disponibile." : boxes.Count == 0 ? "Nessuna conversione comune disponibile. Seleziona meno file o un formato supportato." : (paths.Length == 1 ? "1 file selezionato." : $"{paths.Length} file selezionati.") + " Scegli una o più uscite.";
        if (boxes.Count > 0) boxes[0].IsChecked = true; UpdateRun();
    }
    void UpdateRun() => run.IsEnabled = !busy && paths.Length > 0 && boxes.Any(b => b.IsChecked == true);
    internal void SetBusy(bool value) { busy = value; browse.IsEnabled = filesList.IsEnabled = formatsPanel.IsEnabled = !value; UpdateRun(); if (value) progress.Text = "Conversione in corso…"; }
    internal void SetProgress(string text) => progress.Text = text;
    internal void SetResults(List<ConversionResult> values)
    {
        results.Text = string.Join(Environment.NewLine + Environment.NewLine, values.Select(r => Path.GetFileName(r.Source) + " → " + r.Format + Environment.NewLine + (r.Error ?? string.Join(Environment.NewLine, r.Outputs))));
        progress.Text = $"{values.SelectMany(r => r.Outputs).Count()} file creati, {values.Count(r => r.Error != null)} errori.";
    }
}
