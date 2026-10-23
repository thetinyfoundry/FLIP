using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace Flip;

internal sealed class ShortcutSettingsWindow : Window
{
    readonly TextBlock current = new() { FontSize = 22, Margin = new Thickness(0, 16, 0, 14) };
    readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) };
    WheelShortcut selected;
    bool recording;
    internal bool Accepted { get; private set; }
    internal string SelectedShortcut => selected.Id;
    internal string SelectedLanguage { get; private set; }
    internal ShortcutSettingsWindow(WheelShortcut value, AppPreferences preferences)
    {
        bool it = UiText.IsItalian(preferences);
        SelectedLanguage = preferences.Language;
        selected = value; Title = UiText.Get(preferences, "Impostazioni Flip", "Flip Settings");
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = UiText.Get(preferences, "Scorciatoia della ruota", "Wheel shortcut"), FontSize = 18 });
        var language = new ComboBox { Width = 180, Margin = new Thickness(0, 8, 0, 4) };
        language.Items.Add(new ComboBoxItem { Content = "Automatico / Automatic", Tag = "auto" });
        language.Items.Add(new ComboBoxItem { Content = "Italiano", Tag = "it" });
        language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        language.SelectedIndex = preferences.Language switch { "it" => 1, "en" => 2, _ => 0 };
        language.SelectionChanged += (_, _) => { if (language.SelectedItem is ComboBoxItem item) SelectedLanguage = (string)item.Tag; };
        panel.Children.Add(language);
        panel.Children.Add(new TextBlock { Text = UiText.Get(preferences, "Puoi trascinare e poi premere il tasto, oppure tenerlo premuto e iniziare a trascinare. Quando appare la ruota puoi rilasciare il tasto.", "You can start dragging and then press the key, or hold the key and start dragging. Release the key once the wheel appears."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(current); Refresh();
        var presets = new WrapPanel();
        foreach (string id in new[] { "DOWN", "LEFT", "RIGHT", "UP", "SPACE", "OEM102" })
        {
            var choice = WheelShortcut.Parse(id);
            var button = new Button { Content = choice.Name, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => { selected = choice; recording = false; feedback.Text = ""; Refresh(); };
            presets.Children.Add(button);
        }
        panel.Children.Add(presets);
        var capture = new Button { Content = UiText.Get(preferences, "Scegli un altro tasto…", "Choose another key…"), Padding = new Thickness(8), Margin = new Thickness(0, 10, 0, 0) };
        capture.Click += (_, _) => { recording = true; feedback.Text = UiText.Get(preferences, "Premi un tasto singolo. Esc annulla la registrazione.", "Press one key. Esc cancels recording."); };
        panel.Children.Add(capture); panel.Children.Add(feedback);
        panel.Children.Add(new TextBlock { Text = UiText.Get(preferences, "Fuori dal gesto il tasto funziona normalmente. Ctrl, Alt, Shift, Fn e i tasti di sistema non sono selezionabili. Il tic della ruota resta controllabile dal menu vicino all’orologio.", "Outside the gesture, the key works normally. Ctrl, Alt, Shift, Fn and system keys cannot be selected. The wheel tick remains available from the tray menu."), TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = UiText.Get(preferences, "Annulla", "Cancel"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = UiText.Get(preferences, "Salva", "Save"), Padding = new Thickness(16, 6, 16, 6) };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => { Accepted = true; Close(); };
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions);
        PreviewKeyDown += (_, e) =>
        {
            if (!recording) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } return; }
            e.Handled = true;
            if (e.Key == Key.Escape) { recording = false; feedback.Text = UiText.Get(preferences, "Registrazione annullata.", "Recording cancelled."); return; }
            try
            {
                if (Keyboard.Modifiers != ModifierKeys.None) throw new ArgumentException(UiText.Get(preferences, "Premi un solo tasto, senza Ctrl, Alt o Shift.", "Press one key without Ctrl, Alt or Shift."));
                selected = WheelShortcut.FromVirtualKey((ushort)KeyInterop.VirtualKeyFromKey(e.Key));
                recording = false; feedback.Text = UiText.Get(preferences, "Premi Salva per applicare la scelta.", "Press Save to apply the choice."); Refresh();
            }
            catch (ArgumentException ex) { feedback.Text = ex.Message; }
        };
    }
    void Refresh() => current.Text = selected.Name;
}
