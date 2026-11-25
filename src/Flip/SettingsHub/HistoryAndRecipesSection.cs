using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Flip.Core;

namespace Flip.SettingsHub;

/// <summary>
/// Costruisce l'interfaccia della Sezione 6: Cronologia, Preset &amp; Macro e Backup.
/// </summary>
public static class HistoryAndRecipesSection
{
    public static UIElement Build()
    {
        var settings = SettingsStore.Current.Settings;
        var root = new StackPanel();

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Cronologia & Preset", "History & Presets"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Statistiche, preset riutilizzabili e backup.", "Stats, reusable presets, and settings backup."),
            FontSize = 13,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 20)
        });

        // ── Card 1: Statistiche & Cronologia Recente ─────────────────────────
        var cardHistory = HubUi.CreateCard(
            Loc.T("Statistiche", "Stats"),
            Loc.T("Conversioni e spazio risparmiato. Solo in locale.", "Conversions and space saved. Stored locally only."));
        var stackHistory = (StackPanel)cardHistory.Child;

        // Banner Statistiche
        long totalBytesSaved = settings.TotalBytesSaved();
        double savedMb = totalBytesSaved / (1024.0 * 1024.0);
        int totalConversions = settings.RecentConversions.Count;
        int successfulConversions = settings.RecentConversions.Count(c => c.Success);

        var bannerBorder = new Border
        {
            Background = HubUi.InputBg,
            BorderBrush = HubUi.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 14)
        };

        var bannerGrid = new Grid();
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Spazio Risparmiato
        var mbStack = new StackPanel();
        mbStack.Children.Add(new TextBlock
        {
            Text = $"{savedMb:+0.0;-0.0;0.0} MB",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = savedMb >= 0 ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) : HubUi.Danger
        });
        mbStack.Children.Add(new TextBlock
        {
            Text = Loc.T("Spazio risparmiato", "Space saved"),
            FontSize = 11,
            Foreground = HubUi.TextSecondary
        });
        Grid.SetColumn(mbStack, 0);
        bannerGrid.Children.Add(mbStack);

        // Conversioni Totali
        var countStack = new StackPanel();
        countStack.Children.Add(new TextBlock
        {
            Text = $"{successfulConversions} / {totalConversions}",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary
        });
        countStack.Children.Add(new TextBlock
        {
            Text = Loc.T("Conversioni Riuscite", "Successful conversions"),
            FontSize = 11,
            Foreground = HubUi.TextSecondary
        });
        Grid.SetColumn(countStack, 1);
        bannerGrid.Children.Add(countStack);

        // Pulsante Svuota Cronologia
        var historyListPanel = new StackPanel();
        var btnClearHistory = HubUi.CreateButton(Loc.T("Svuota cronologia", "Clear history"), false, () =>
        {
            if (SettingsHubWindow.Confirm(
                Loc.T("Svuota cronologia", "Clear History"),
                Loc.T("Azzerare la cronologia delle conversioni?", "Clear your local conversion history?"),
                Loc.T("Svuota", "Clear")))
            {
                SettingsStore.Current.Update(s => s.RecentConversions.Clear());
                historyListPanel.Children.Clear();
                mbStack.Children.OfType<TextBlock>().First().Text = "0.0 MB";
                countStack.Children.OfType<TextBlock>().First().Text = "0 / 0";
            }
        });
        btnClearHistory.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(btnClearHistory, 2);
        bannerGrid.Children.Add(btnClearHistory);

        bannerBorder.Child = bannerGrid;
        stackHistory.Children.Add(bannerBorder);

        // Lista Ultime Conversioni (ultime 15 visibili)
        var recentItems = settings.RecentConversions.AsEnumerable().Reverse().Take(15).ToList();
        if (recentItems.Count == 0)
        {
            historyListPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("Nessuna conversione recente.", "No recent conversions yet."),
                FontSize = 12,
                Foreground = HubUi.TextMuted,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        else
        {
            foreach (var conv in recentItems)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

                // Data/Ora
                var timeBlock = new TextBlock
                {
                    Text = conv.TimestampUtc.ToLocalTime().ToString("dd/MM HH:mm:ss"),
                    FontSize = 11,
                    Foreground = HubUi.TextMuted
                };
                Grid.SetColumn(timeBlock, 0);
                row.Children.Add(timeBlock);

                // File Name -> Target
                var nameBlock = new TextBlock
                {
                    Text = $"{conv.SourceFileName} ➔ .{conv.TargetExtension}",
                    FontSize = 12,
                    Foreground = HubUi.TextPrimary,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(nameBlock, 1);
                row.Children.Add(nameBlock);

                // Durata
                var durBlock = new TextBlock
                {
                    Text = $"{conv.DurationMs} ms",
                    FontSize = 11,
                    Foreground = HubUi.TextSecondary,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(durBlock, 2);
                row.Children.Add(durBlock);

                // Esito
                var statusBlock = new TextBlock
                {
                    Text = conv.Success ? "✓ OK" : Loc.T("✗ FALLITA", "✗ FAILED"),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = conv.Success ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) : HubUi.Danger,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(statusBlock, 3);
                row.Children.Add(statusBlock);

                historyListPanel.Children.Add(row);
            }
        }

        stackHistory.Children.Add(historyListPanel);
        root.Children.Add(cardHistory);

        // ── Card 2: I tuoi Preset & Macro ────────────────────────────────────
        var cardRecipes = HubUi.CreateCard(
            Loc.T("Preset", "Presets"),
            Loc.T("Formato, dimensione, qualità e metadati in un clic.", "Format, size, quality, and metadata rules in one click."));
        var stackRecipes = (StackPanel)cardRecipes.Child;

        var recipesPanel = new StackPanel();

        void RenderRecipes()
        {
            recipesPanel.Children.Clear();
            if (settings.Recipes.Count == 0)
            {
                recipesPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Nessun preset salvato.", "No saved presets yet."),
                    FontSize = 12,
                    Foreground = HubUi.TextMuted,
                    Margin = new Thickness(0, 4, 0, 10)
                });
            }
            else
            {
                foreach (var r in settings.Recipes)
                {
                    recipesPanel.Children.Add(CreateRecipeCard(r, settings, RenderRecipes));
                }
            }
        }

        RenderRecipes();
        stackRecipes.Children.Add(recipesPanel);

        var btnAddRecipe = HubUi.CreateButton(Loc.T("+ Nuovo preset", "+ New preset"), true, () =>
        {
            var recipe = new FlipRecipe
            {
                Name = $"Preset #{settings.Recipes.Count + 1}",
                TargetExtension = "jpg",
                MaxDimensionPx = 1920,
                Quality = 85,
                StripMetadata = true,
                TargetMaxMegabytes = 0,
                Enabled = true
            };
            SettingsStore.Current.Update(s => s.Recipes.Add(recipe));
            RenderRecipes();
        });
        btnAddRecipe.Margin = new Thickness(0, 10, 0, 0);
        btnAddRecipe.HorizontalAlignment = HorizontalAlignment.Left;
        stackRecipes.Children.Add(btnAddRecipe);

        root.Children.Add(cardRecipes);

        // ── Card 3: Backup & Portabilità Configurazione ─────────────────────
        var cardBackup = HubUi.CreateCard(
            "Backup",
            Loc.T("Spicchi, tasti, cartelle e preset in un file JSON.", "Export or import slices, shortcuts, folders, and presets as JSON."));
        var stackBackup = (StackPanel)cardBackup.Child;
        var backupStatus = new TextBlock { FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        void ShowBackupStatus(string text, bool ok)
        {
            backupStatus.Text = text;
            backupStatus.Foreground = ok ? HubUi.Success : HubUi.Danger;
            backupStatus.Visibility = Visibility.Visible;
        }

        var backupGrid = new Grid();
        backupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        backupGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var btnExport = HubUi.CreateButton(Loc.T("Esporta…", "Export…"), false, () =>
        {
            var dialog = new SaveFileDialog
            {
                Title = Loc.T("FLIP · Esporta configurazione", "FLIP · Export settings"),
                Filter = Loc.T("File JSON (*.json)|*.json|Tutti i file (*.*)|*.*", "JSON files (*.json)|*.json|All files (*.*)|*.*"),
                FileName = $"flip_settings_{DateTime.Now:yyyyMMdd}.json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    SettingsStore.Current.ExportToFile(dialog.FileName);
                    ShowBackupStatus(Loc.F("✓ Esportata · {0}", "✓ Exported · {0}", System.IO.Path.GetFileName(dialog.FileName)), ok: true);
                }
                catch (Exception ex)
                {
                    ShowBackupStatus(Loc.F("Esportazione non riuscita · {0}", "Export failed · {0}", ex.Message), ok: false);
                }
            }
        });
        btnExport.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(btnExport, 0);
        backupGrid.Children.Add(btnExport);

        var btnImport = HubUi.CreateButton(Loc.T("Importa…", "Import…"), false, () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("FLIP · Importa configurazione", "FLIP · Import settings"),
                Filter = Loc.T("File JSON (*.json)|*.json|Tutti i file (*.*)|*.*", "JSON files (*.json)|*.json|All files (*.*)|*.*")
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    int disabled = SettingsStore.Current.ImportFromFile(dialog.FileName);
                    Loc.SetPreference(SettingsStore.Current.CreateSnapshot().Language);
                    SettingsHubWindow.RefreshLanguage();
                    ShowBackupStatus(Loc.F("✓ Importata · {0}", "✓ Imported · {0}", System.IO.Path.GetFileName(dialog.FileName)) +
                        (disabled > 0
                            ? Loc.F(" · {0} cartelle monitorate importate spente: controllale e attivale", " · {0} watch folders imported as off: review and turn them on", disabled)
                            : ""), ok: true);
                }
                catch (Exception ex)
                {
                    ShowBackupStatus(Loc.F("Importazione non riuscita · {0}", "Import failed · {0}", ex.Message), ok: false);
                }
            }
        });
        btnImport.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(btnImport, 1);
        backupGrid.Children.Add(btnImport);

        stackBackup.Children.Add(backupGrid);
        stackBackup.Children.Add(backupStatus);
        root.Children.Add(cardBackup);

        return root;
    }

    private static Border CreateRecipeCard(FlipRecipe recipe, FlipSettings settings, Action onModified)
    {
        var border = new Border
        {
            Background = HubUi.InputBg,
            BorderBrush = HubUi.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel();

        // Nome e Cancella
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var txtName = HubUi.CreateTextBox(recipe.Name, val =>
        {
            recipe.Name = val;
            SettingsStore.Current.Save();
        });
        txtName.FontWeight = FontWeights.SemiBold;
        Grid.SetColumn(txtName, 0);
        headerGrid.Children.Add(txtName);

        var btnDel = HubUi.CreateButton(Loc.T("Elimina", "Delete"), false, () =>
        {
            SettingsStore.Current.Update(s => s.Recipes.Remove(recipe));
            onModified();
        });
        btnDel.Foreground = HubUi.Danger;
        btnDel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(btnDel, 1);
        headerGrid.Children.Add(btnDel);
        stack.Children.Add(headerGrid);

        // Parametri: Target Ext, Max Dim, Quality
        var paramsGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Target Ext
        var p1 = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        p1.Children.Add(new TextBlock { Text = Loc.T("Formato:", "Format:"), FontSize = 11, Foreground = HubUi.TextSecondary });
        var txtExt = HubUi.CreateTextBox(recipe.TargetExtension, val =>
        {
            recipe.TargetExtension = val.TrimStart('.').ToLowerInvariant();
            SettingsStore.Current.Save();
        });
        p1.Children.Add(txtExt);
        Grid.SetColumn(p1, 0);
        paramsGrid.Children.Add(p1);

        // Max Dimension Px
        var p2 = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        p2.Children.Add(new TextBlock { Text = Loc.T("Max Lato (px):", "Max edge (px):"), FontSize = 11, Foreground = HubUi.TextSecondary });
        var txtDim = HubUi.CreateTextBox(recipe.MaxDimensionPx.ToString(), val =>
        {
            if (int.TryParse(val, out int px)) { recipe.MaxDimensionPx = px; SettingsStore.Current.Save(); }
        });
        p2.Children.Add(txtDim);
        Grid.SetColumn(p2, 1);
        paramsGrid.Children.Add(p2);

        // Quality
        var p3 = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        p3.Children.Add(new TextBlock { Text = Loc.T("Qualità (1-100):", "Quality (1–100):"), FontSize = 11, Foreground = HubUi.TextSecondary });
        var txtQ = HubUi.CreateTextBox(recipe.Quality.ToString(), val =>
        {
            if (int.TryParse(val, out int q)) { recipe.Quality = q; SettingsStore.Current.Save(); }
        });
        p3.Children.Add(txtQ);
        Grid.SetColumn(p3, 2);
        paramsGrid.Children.Add(p3);

        stack.Children.Add(paramsGrid);

        // Strip Metadata toggle + Auto-apply toggle
        var toggleStrip = HubUi.CreateToggle(
            Loc.T("Rimuovi metadati", "Strip metadata"),
            "EXIF, GPS & XMP.",
            recipe.StripMetadata,
            val => { recipe.StripMetadata = val; SettingsStore.Current.Save(); });
        toggleStrip.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(toggleStrip);

        var toggleEnabled = HubUi.CreateToggle(
            Loc.T("Applica in automatico", "Auto-apply"),
            Loc.T("Usato dalla ruota per questo formato.", "Applied automatically by the wheel for this format."),
            recipe.Enabled,
            val => { recipe.Enabled = val; SettingsStore.Current.Save(); });
        toggleEnabled.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(toggleEnabled);

        // Azioni Preset (Salva + Applica su file...) + stato esecuzione
        var presetStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = HubUi.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var actionsRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var btnSave = HubUi.CreateButton(Loc.T("Salva Preset", "Save Preset"), false, () =>
        {
            recipe.Name = txtName.Text.Trim();
            recipe.TargetExtension = txtExt.Text.Trim().TrimStart('.').ToLowerInvariant();
            if (int.TryParse(txtDim.Text, out int px)) recipe.MaxDimensionPx = px;
            if (int.TryParse(txtQ.Text, out int q)) recipe.Quality = q;
            SettingsStore.Current.Save();
            presetStatus.Text = Loc.F("✓ Preset \"{0}\" salvato su disco.", "✓ Preset \"{0}\" saved.", recipe.Name);
            presetStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            presetStatus.Visibility = Visibility.Visible;
        });
        btnSave.Margin = new Thickness(0, 0, 8, 0);
        actionsRow.Children.Add(btnSave);

        Button? btnRun = null;
        btnRun = HubUi.CreateButton(Loc.T("▶ Applica su file...", "▶ Run on files…"), true, async () =>
        {
            var picker = new OpenFileDialog
            {
                Title = Loc.F("Applica Preset \"{0}\" (.{1})", "Run Preset \"{0}\" (.{1})", recipe.Name, recipe.TargetExtension),
                Multiselect = true
            };
            if (picker.ShowDialog() != true || picker.FileNames.Length == 0) return;

            btnRun!.IsEnabled = false;
            presetStatus.Text = Loc.T("Preset in esecuzione…", "Running preset…");
            presetStatus.Foreground = HubUi.Accent;
            presetStatus.Visibility = Visibility.Visible;

            try
            {
                var snapshot = SettingsStore.Current.CreateSnapshot();
                var batch = await Task.Run(() =>
                    Flip.Routing.RoutePlanner.ExecuteRecipeAsync(picker.FileNames, recipe, snapshot));
                int okCount = batch.Count(r => r.Result.Success);
                if (okCount > 0)
                {
                    presetStatus.Text = Loc.F("✓ {0}/{1} file salvati in .{2}", "✓ {0}/{1} files saved as .{2}", okCount, batch.Count, recipe.TargetExtension);
                    presetStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
                    SettingsHubWindow.RefreshHistory();
                }
                else
                {
                    string err = batch.FirstOrDefault(r => !r.Result.Success)?.Result.ErrorMessage ?? Loc.T("Nessun output prodotto.", "No output produced.");
                    presetStatus.Text = Loc.F("✗ Errore: {0}", "✗ Error: {0}", err);
                    presetStatus.Foreground = HubUi.Danger;
                }
            }
            catch (Exception ex)
            {
                presetStatus.Text = Loc.F("✗ Errore: {0}", "✗ Error: {0}", ex.Message);
                presetStatus.Foreground = HubUi.Danger;
            }
            finally
            {
                btnRun!.IsEnabled = true;
            }
        });
        actionsRow.Children.Add(btnRun);

        stack.Children.Add(actionsRow);
        stack.Children.Add(presetStatus);

        border.Child = stack;
        return border;
    }
}
