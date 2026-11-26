using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Microsoft.Win32;
using Flip.Core;

namespace Flip.SettingsHub;

/// <summary>
/// Costruisce l'interfaccia della Sezione 2: Trigger &amp; OS (tasto attivazione, drag button, flip tab, accessibilità sguardo, clipboard shortcut, avvio Windows, context menu).
/// </summary>
public static class TriggerAndOsSection
{
    private static readonly Lazy<Style> DarkSliderStyle = new(() => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="Slider">
            <Setter Property="Stylus.IsPressAndHoldEnabled" Value="False"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Slider">
                        <Grid VerticalAlignment="Center" MinHeight="24">
                            <Track x:Name="PART_Track">
                                <Track.DecreaseRepeatButton>
                                    <RepeatButton Command="{x:Static Slider.DecreaseLarge}" Focusable="False" OverridesDefaultStyle="True">
                                        <RepeatButton.Template>
                                            <ControlTemplate TargetType="RepeatButton">
                                                <Border Height="5" CornerRadius="2.5" Background="#FF7A18"/>
                                            </ControlTemplate>
                                        </RepeatButton.Template>
                                    </RepeatButton>
                                </Track.DecreaseRepeatButton>
                                <Track.IncreaseRepeatButton>
                                    <RepeatButton Command="{x:Static Slider.IncreaseLarge}" Focusable="False" OverridesDefaultStyle="True">
                                        <RepeatButton.Template>
                                            <ControlTemplate TargetType="RepeatButton">
                                                <Border Height="5" CornerRadius="2.5" Background="#2E3240"/>
                                            </ControlTemplate>
                                        </RepeatButton.Template>
                                    </RepeatButton>
                                </Track.IncreaseRepeatButton>
                                <Track.Thumb>
                                    <Thumb Width="16" Height="16" Cursor="Hand" OverridesDefaultStyle="True">
                                        <Thumb.Template>
                                            <ControlTemplate TargetType="Thumb">
                                                <Ellipse x:Name="Dot" Width="16" Height="16" Fill="#FF7A18" Stroke="#F3F4F6" StrokeThickness="1.5"/>
                                                <ControlTemplate.Triggers>
                                                    <Trigger Property="IsMouseOver" Value="True">
                                                        <Setter TargetName="Dot" Property="Fill" Value="#FF6B00"/>
                                                        <Setter TargetName="Dot" Property="StrokeThickness" Value="2"/>
                                                    </Trigger>
                                                </ControlTemplate.Triggers>
                                            </ControlTemplate>
                                        </Thumb.Template>
                                    </Thumb>
                                </Track.Thumb>
                            </Track>
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """));

    public static UIElement Build()
    {
        var store = SettingsStore.Current;
        var settings = store.Settings;
        var root = new StackPanel();

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Attivazione & Input", "Activation & Input"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Gesto di apertura, scorciatoie e integrazione con Windows.", "Trigger gesture, shortcuts, and Windows integration."),
            FontSize = 13,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 20)
        });

        // ── Card 1: Gesto di Attivazione della Ruota ─────────────────────────
        var cardTrigger = HubUi.CreateCard(
            Loc.T("Apertura della ruota", "Wheel trigger"),
            Loc.T("Tasto da premere mentre trascini un file.", "Key to press while dragging a file."));
        var stackTrigger = (StackPanel)cardTrigger.Child;

        // Etichette leggibili localizzate; nelle impostazioni resta salvato il nome tecnico del tasto.
        KeyChoice[] triggerKeys = [.. new[] { "Down", "Up", "Left", "Right", "Space", "Oem102", "Control", "Alt", "Shift", "MiddleClick" }
            .Select(key => new KeyChoice(key, KeyChoice.Describe(key)))];
        KeyChoice currentTrigger = triggerKeys.FirstOrDefault(key =>
            string.Equals(key.Value, settings.TriggerKey, StringComparison.OrdinalIgnoreCase))
            ?? new KeyChoice(settings.TriggerKey, KeyChoice.Describe(settings.TriggerKey));
        if (!triggerKeys.Contains(currentTrigger))
        {
            triggerKeys = [.. triggerKeys, currentTrigger];
        }

        var comboTrigger = HubUi.CreateComboBox(
            triggerKeys,
            currentTrigger,
            selected => SettingsStore.Current.Update(s => s.TriggerKey = selected.Value));
        stackTrigger.Children.Add(comboTrigger);

        var toggleDrag = HubUi.CreateToggle(
            Loc.T("Solo durante il trascinamento", "Only while dragging"),
            Loc.T("Evita aperture accidentali.", "Prevents accidental triggers."),
            settings.RequireDragButton,
            active => SettingsStore.Current.Update(s => s.RequireDragButton = active));
        stackTrigger.Children.Add(toggleDrag);

        var toggleSecondaryButton = HubUi.CreateToggle(
            Loc.T("Accetta anche il tasto destro", "Allow right-click drag"),
            Loc.T("Utile con mouse verticali.", "Handy for vertical mice and custom grips."),
            settings.AcceptSecondaryDragButton,
            active =>
            {
                store.Update(s => s.AcceptSecondaryDragButton = active);
                Program.SyncDragButtonPreference(store, EventArgs.Empty);
            });
        stackTrigger.Children.Add(toggleSecondaryButton);

        var toggleFlip = HubUi.CreateToggle(
            Loc.T("Gira la ruota con Tab o rotellina", "Flip wheel with Tab or scroll"),
            Loc.T("Passa da Formati a Strumenti.", "Switch between Formats and Tools."),
            settings.EnableFlipWithTabOrScroll,
            active => SettingsStore.Current.Update(s => s.EnableFlipWithTabOrScroll = active));
        stackTrigger.Children.Add(toggleFlip);

        var toggleHold = HubUi.CreateToggle(
            Loc.T("Apri tenendo premuto il click su un file", "Hold click on a file to open"),
            Loc.T("Tieni premuto il pulsante su un file selezionato in Esplora File o sul Desktop per aprire la ruota, senza premere tasti. Per uscire: tasto destro o Esc.",
                 "Hold the mouse button on a selected file in File Explorer or on the Desktop to open the wheel, without pressing any key. To close it: right-click or Esc."),
            settings.HoldToOpenEnabled,
            active => store.Update(s => s.HoldToOpenEnabled = active));
        stackTrigger.Children.Add(toggleHold);

        var holdHeader = new Grid { Margin = new Thickness(0, 14, 0, 6) };
        holdHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        holdHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        holdHeader.Children.Add(new TextBlock
        {
            Text = Loc.T("Tempo di pressione", "Hold time"),
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        });
        int initialHold = Math.Clamp(settings.HoldToOpenMilliseconds, 1000, 8000);
        var holdValueText = new TextBlock
        {
            Text = $"{initialHold} ms",
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.Accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(holdValueText, 1);
        holdHeader.Children.Add(holdValueText);
        stackTrigger.Children.Add(holdHeader);

        var holdSlider = new Slider
        {
            Style = DarkSliderStyle.Value,
            Minimum = 1000,
            Maximum = 8000,
            TickFrequency = 250,
            IsSnapToTickEnabled = true,
            SmallChange = 250,
            LargeChange = 1000,
            Value = initialHold
        };
        bool syncingHoldSlider = false;
        holdSlider.ValueChanged += (_, e) =>
        {
            int nextMs = Math.Clamp((int)Math.Round(e.NewValue), 1000, 8000);
            holdValueText.Text = $"{nextMs} ms";
            if (syncingHoldSlider) return;
            if (store.CreateSnapshot().HoldToOpenMilliseconds != nextMs)
                store.Update(s => s.HoldToOpenMilliseconds = nextMs);
        };
        stackTrigger.Children.Add(holdSlider);

        var holdPresetRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (ms, caption) in new (int Ms, string Caption)[]
        {
            (2000, Loc.T("Breve · 2000 ms", "Short · 2000 ms")),
            (3000, Loc.T("Standard · 3000 ms", "Standard · 3000 ms")),
            (4000, Loc.T("Comodo · 4000 ms", "Relaxed · 4000 ms")),
            (5000, Loc.T("Lungo · 5000 ms", "Long · 5000 ms"))
        })
        {
            var presetBtn = HubUi.CreateButton(caption, false, () =>
            {
                syncingHoldSlider = true;
                try { holdSlider.Value = ms; }
                finally { syncingHoldSlider = false; }
                holdValueText.Text = $"{ms} ms";
                store.Update(s => s.HoldToOpenMilliseconds = ms);
            });
            presetBtn.Padding = new Thickness(10, 4, 10, 4);
            presetBtn.FontSize = 11;
            presetBtn.Margin = new Thickness(0, 0, 6, 4);
            holdPresetRow.Children.Add(presetBtn);
        }
        stackTrigger.Children.Add(holdPresetRow);

        root.Children.Add(cardTrigger);

        // ── Card 2: Clipboard & Integrazione Sistema ────────────────────────
        var cardSystem = HubUi.CreateCard(
            Loc.T("Sistema", "System"),
            Loc.T("Avvio, appunti ed Esplora file.", "Startup, clipboard, and File Explorer."));
        var stackSystem = (StackPanel)cardSystem.Child;

        var clipLabel = new TextBlock
        {
            Text = Loc.T("Scorciatoia per convertire dagli appunti", "Clipboard Flip shortcut"),
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 4)
        };
        stackSystem.Children.Add(clipLabel);

        var txtClip = HubUi.CreateTextBox(
            settings.EnableClipboardFlipShortcut,
            text => SettingsStore.Current.Update(s => s.EnableClipboardFlipShortcut = text));
        txtClip.Margin = new Thickness(0, 0, 0, 12);
        stackSystem.Children.Add(txtClip);

        var toggleAutoStart = HubUi.CreateToggle(
            Loc.T("Avvia FLIP con Windows", "Launch FLIP at startup"),
            Loc.T("Parte in background nell'area di notifica.", "Starts quietly in the system tray."),
            settings.StartWithWindows,
            active =>
            {
                SettingsStore.Current.Update(s => s.StartWithWindows = active);
                try { StartupIntegration.SetEnabled(active); } catch { }
            });
        stackSystem.Children.Add(toggleAutoStart);

        var toggleContextMenu = HubUi.CreateToggle(
            Loc.T("Voce \"Converti con FLIP\" nel menu contestuale", "Add \"Convert with FLIP\" to context menu"),
            Loc.T("Clic destro su un file in Esplora file.", "Right-click any file in File Explorer."),
            settings.EnableExplorerContextMenu,
            active => SettingsStore.Current.Update(s => s.EnableExplorerContextMenu = active));
        stackSystem.Children.Add(toggleContextMenu);

        root.Children.Add(cardSystem);

        // ── Card 3: Accessibilità Tobii Eye Tracker / Senza Mani ────────────
        var cardGaze = HubUi.CreateCard(
            Loc.T("Accessibilità Tobii", "Tobii Accessibility"),
            Loc.T("Usa FLIP con lo sguardo, senza mani.", "Hands-free control with eye tracking."));
        var stackGaze = (StackPanel)cardGaze.Child;

        var toggleGaze = HubUi.CreateToggle(
            Loc.T("Attiva modalità Tobii", "Enable Tobii gaze mode"),
            Loc.T("Bollino [F] sul bordo destro e selezione a sosta.", "Edge orb [F] on the right side with dwell-to-select."),
            settings.GazeAccessibilityMode,
            active => store.Update(s => s.GazeAccessibilityMode = active));
        stackGaze.Children.Add(toggleGaze);

        var dwellHeader = new Grid { Margin = new Thickness(0, 14, 0, 6) };
        dwellHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dwellHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        dwellHeader.Children.Add(new TextBlock
        {
            Text = Loc.T("Tempo di sosta", "Dwell time"),
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        });

        int initialDwell = Math.Clamp(settings.GazeDwellMilliseconds, 600, 2500);
        var dwellValueText = new TextBlock
        {
            Text = $"{initialDwell} ms",
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.Accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dwellValueText, 1);
        dwellHeader.Children.Add(dwellValueText);
        stackGaze.Children.Add(dwellHeader);

        var dwellSlider = new Slider
        {
            Style = DarkSliderStyle.Value,
            Minimum = 600,
            Maximum = 2500,
            TickFrequency = 50,
            IsSnapToTickEnabled = true,
            SmallChange = 50,
            LargeChange = 200,
            Value = initialDwell
        };

        bool syncingSlider = false;
        dwellSlider.ValueChanged += (_, e) =>
        {
            int nextMs = Math.Clamp((int)Math.Round(e.NewValue), 600, 2500);
            dwellValueText.Text = $"{nextMs} ms";
            if (syncingSlider) return;
            if (store.CreateSnapshot().GazeDwellMilliseconds != nextMs)
            {
                store.Update(s => s.GazeDwellMilliseconds = nextMs);
            }
        };
        stackGaze.Children.Add(dwellSlider);

        var presetRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (ms, caption) in new (int Ms, string Caption)[]
        {
            (750, Loc.T("Rapido · 750 ms", "Fast · 750 ms")),
            (1100, Loc.T("Standard · 1100 ms", "Standard · 1100 ms")),
            (1600, Loc.T("Comodo · 1600 ms", "Relaxed · 1600 ms")),
            (2200, Loc.T("Esteso · 2200 ms", "Extended · 2200 ms"))
        })
        {
            var presetBtn = HubUi.CreateButton(caption, false, () =>
            {
                syncingSlider = true;
                try { dwellSlider.Value = ms; }
                finally { syncingSlider = false; }
                dwellValueText.Text = $"{ms} ms";
                store.Update(s => s.GazeDwellMilliseconds = ms);
            });
            presetBtn.Padding = new Thickness(10, 4, 10, 4);
            presetBtn.FontSize = 11;
            presetBtn.Margin = new Thickness(0, 0, 6, 4);
            presetRow.Children.Add(presetBtn);
        }
        stackGaze.Children.Add(presetRow);

        root.Children.Add(cardGaze);

        return root;
    }
}

/// <summary>
/// Costruisce l'interfaccia della Sezione 3: Regole di Output (cartella output, batch subfolder, conflict policy, cestino, smaller).
/// </summary>
public static class OutputRulesSection
{
    public static UIElement Build()
    {
        var settings = SettingsStore.Current.Settings;
        var root = new StackPanel();

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Destinazione File", "Output Rules"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Dove salvare i file convertiti e come proteggere gli originali.", "Where converted files go and how originals are protected."),
            FontSize = 13,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 20)
        });

        // ── Card 1: Cartella di Destinazione ────────────────────────────────
        var cardDir = HubUi.CreateCard(
            Loc.T("Cartella di destinazione", "Output folder"),
            Loc.T("Predefinito: accanto al file originale.", "Default: next to the original file."));
        var stackDir = (StackPanel)cardDir.Child;

        var dirOptions = new (OutputDirectoryMode Mode, string Label)[]
        {
            (OutputDirectoryMode.SameAsSource, Loc.T("Accanto all'originale", "Next to original")),
            (OutputDirectoryMode.CustomFolder, Loc.T("Cartella personalizzata", "Custom folder")),
            (OutputDirectoryMode.SubfolderOnBatch, Loc.T("Sottocartella per i batch", "Subfolder for batches"))
        };

        // Selettore cartella custom con OpenFolderDialog
        var customDirPanel = new Grid
        {
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = settings.OutputDirectoryMode == OutputDirectoryMode.CustomFolder ? Visibility.Visible : Visibility.Collapsed
        };
        customDirPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customDirPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var comboDirMode = HubUi.CreateComboBox(
            dirOptions.Select(o => o.Label).ToArray(),
            dirOptions.FirstOrDefault(o => o.Mode == settings.OutputDirectoryMode).Label ?? dirOptions[0].Label,
            selectedLabel =>
            {
                var mode = dirOptions.FirstOrDefault(o => o.Label == selectedLabel).Mode;
                customDirPanel.Visibility = mode == OutputDirectoryMode.CustomFolder ? Visibility.Visible : Visibility.Collapsed;
                SettingsStore.Current.Update(s =>
                {
                    s.OutputDirectoryMode = mode;
                    if (mode == OutputDirectoryMode.SameAsSource)
                        s.UserSelectedCustomOutputDirectory = false;
                });
            });
        stackDir.Children.Add(comboDirMode);

        var txtCustomDir = HubUi.CreateTextBox(
            settings.CustomOutputDirectory,
            text => SettingsStore.Current.Update(s =>
            {
                s.CustomOutputDirectory = text;
                s.UserSelectedCustomOutputDirectory = !string.IsNullOrWhiteSpace(text);
            }));
        Grid.SetColumn(txtCustomDir, 0);
        customDirPanel.Children.Add(txtCustomDir);

        var btnBrowse = HubUi.CreateButton(Loc.T("Sfoglia...", "Browse…"), isPrimary: false, () =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = Loc.T("FLIP · Cartella di destinazione", "FLIP · Output folder"),
                InitialDirectory = string.IsNullOrWhiteSpace(settings.CustomOutputDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : settings.CustomOutputDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                txtCustomDir.Text = dialog.FolderName;
                SettingsStore.Current.Update(s =>
                {
                    s.CustomOutputDirectory = dialog.FolderName;
                    s.UserSelectedCustomOutputDirectory = true;
                });
            }
        });
        btnBrowse.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(btnBrowse, 1);
        customDirPanel.Children.Add(btnBrowse);

        stackDir.Children.Add(customDirPanel);

        // Batch Subfolder Threshold
        var thresholdLabel = new TextBlock
        {
            Text = Loc.T("File minimi per creare la sottocartella", "Minimum files to create a batch subfolder"),
            FontSize = 11,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 12, 0, 4)
        };
        stackDir.Children.Add(thresholdLabel);

        var txtThreshold = HubUi.CreateTextBox(
            settings.BatchSubfolderThreshold.ToString(),
            text =>
            {
                if (int.TryParse(text, out int val) && val > 0 &&
                    val != SettingsStore.Current.Settings.BatchSubfolderThreshold)
                {
                    SettingsStore.Current.Update(s => s.BatchSubfolderThreshold = val);
                }
            });
        stackDir.Children.Add(txtThreshold);

        root.Children.Add(cardDir);

        // ── Card 2: Conflitti & Sovrascrittura ───────────────────────────────
        var cardConflict = HubUi.CreateCard(
            Loc.T("File con lo stesso nome", "Naming conflicts"),
            Loc.T("Cosa fare se il file esiste già.", "Action when the target file already exists."));
        var stackConflict = (StackPanel)cardConflict.Child;

        ConflictChoice[] conflictChoices =
        [
            new(ConflictPolicy.AutoNumber, Loc.T("Aggiungi un numero · foto_1.png", "Auto-number · photo_1.png")),
            new(ConflictPolicy.Overwrite, Loc.T("Sovrascrivi", "Overwrite")),
            new(ConflictPolicy.Skip, Loc.T("Salta", "Skip"))
        ];
        var comboConflict = HubUi.CreateComboBox(
            conflictChoices,
            conflictChoices.FirstOrDefault(c => c.Value == settings.ConflictPolicy) ?? conflictChoices[0],
            selected => SettingsStore.Current.Update(s => s.ConflictPolicy = selected.Value));
        stackConflict.Children.Add(comboConflict);

        root.Children.Add(cardConflict);

        // ── Card 3: Sicurezza File Originali & Ottimizzazione ────────────────
        var cardSafety = HubUi.CreateCard(
            Loc.T("Originali e spazio", "Originals & storage"),
            Loc.T("Regole di sicurezza sui file.", "Safety rules for source files."));
        var stackSafety = (StackPanel)cardSafety.Child;

        var toggleRecycle = HubUi.CreateToggle(
            Loc.T("Sposta l'originale nel Cestino", "Move original to Recycle Bin"),
            Loc.T("Solo dopo una conversione riuscita. Recuperabile.", "Only after a verified conversion. Recoverable."),
            settings.ReplaceOriginalToRecycleBin,
            active => SettingsStore.Current.Update(s => s.ReplaceOriginalToRecycleBin = active));
        stackSafety.Children.Add(toggleRecycle);

        var toggleSmaller = HubUi.CreateToggle(
            Loc.T("Salva solo se più leggero", "Keep only if smaller"),
            Loc.T("Altrimenti tiene l'originale.", "Discards the output if it ends up larger."),
            settings.KeepOnlyIfSmaller,
            active => SettingsStore.Current.Update(s => s.KeepOnlyIfSmaller = active));
        stackSafety.Children.Add(toggleSmaller);

        root.Children.Add(cardSafety);

        return root;
    }
}

/// <summary>Voce di combo: valore salvato nelle impostazioni + etichetta leggibile.</summary>
internal sealed record KeyChoice(string Value, string Label)
{
    public override string ToString() => Label;

    internal static string Describe(string key) => key.Trim().ToUpperInvariant() switch
    {
        "DOWN" => Loc.T("Freccia Giù", "Down Arrow"),
        "UP" => Loc.T("Freccia Su", "Up Arrow"),
        "LEFT" => Loc.T("Freccia Sinistra", "Left Arrow"),
        "RIGHT" => Loc.T("Freccia Destra", "Right Arrow"),
        "SPACE" => Loc.T("Spazio", "Space"),
        "OEM102" => Loc.T("Tasto < >", "< > Key"),
        "CONTROL" => "Ctrl",
        "ALT" => "Alt",
        "SHIFT" => Loc.T("Maiusc", "Shift"),
        "MIDDLECLICK" => Loc.T("Clic centrale", "Middle Click"),
        _ => key
    };
}

internal sealed record ConflictChoice(ConflictPolicy Value, string Label)
{
    public override string ToString() => Label;
}
