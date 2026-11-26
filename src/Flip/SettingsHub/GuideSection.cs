using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Flip.Core;

namespace Flip.SettingsHub;

/// <summary>
/// Sezione 7: scorciatoie essenziali come keycap, sei capacità chiave di FLIP e accesso al tour iniziale.
/// </summary>
public static class GuideSection
{
    private static readonly SolidColorBrush KeyBg = Frozen(0x1E, 0x21, 0x2B);
    private static readonly SolidColorBrush KeyBorder = Frozen(0x3A, 0x3F, 0x4E);

    public static UIElement Build()
    {
        var settings = SettingsStore.Current.CreateSnapshot();
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Guida & Scorciatoie", "Guide & Shortcuts"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary,
            Padding = new Thickness(0, 2, 0, 0),
            Margin = new Thickness(0, 4, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Gesti essenziali e cosa può fare FLIP.", "Core gestures and what FLIP can do."),
            FontSize = 13,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 18)
        });

        // ── Scorciatoie ─────────────────────────────────────────────────────
        var cardKeys = HubUi.CreateCard(Loc.T("Scorciatoie", "Shortcuts"));
        var stackKeys = (StackPanel)cardKeys.Child;
        string trigger = KeyChoice.Describe(string.IsNullOrWhiteSpace(settings.TriggerKey) ? "Down" : settings.TriggerKey);
        string clipboard = string.IsNullOrWhiteSpace(settings.EnableClipboardFlipShortcut) ? "Ctrl+Shift+F" : settings.EnableClipboardFlipShortcut.Trim();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string[] Keys, string Separator, string Action)[] shortcuts =
        [
            ([Loc.T("Trascina", "Drag"), trigger], "+", Loc.T("Apri la ruota", "Open the wheel")),
            (["Tab", Loc.T("Rotellina", "Scroll")], "/", Loc.T("Passa da Formati a Strumenti", "Flip between Formats and Tools")),
            ([Loc.T("Spazio", "Space")], "", Loc.T("Anteprima rapida · 3D, PDF, immagini, audio", "QuickLook · 3D, PDF, images, audio")),
            ([clipboard], "", Loc.T("Converti dagli appunti", "Convert from clipboard")),
            (["A"], "", Loc.T("Salva o richiama l'inquadratura 3D", "Save or recall 3D camera angle")),
            (["Ctrl+S", "Ctrl+C", "P"], "·", Loc.T("Salva, copia o oscura nel ritaglio", "Save, copy, or blur in crop editor")),
            (["Esc"], "", Loc.T("Chiudi ruota o editor", "Close wheel or editor"))
        ];
        for (int i = 0; i < shortcuts.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            for (int k = 0; k < shortcuts[i].Keys.Length; k++)
            {
                if (k > 0 && shortcuts[i].Separator.Length > 0)
                {
                    keys.Children.Add(new TextBlock
                    {
                        Text = shortcuts[i].Separator,
                        FontSize = 11,
                        Foreground = HubUi.TextSecondary,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    });
                }
                keys.Children.Add(KeyCap(shortcuts[i].Keys[k]));
            }
            Grid.SetRow(keys, i);
            grid.Children.Add(keys);

            var action = new TextBlock
            {
                Text = shortcuts[i].Action,
                FontSize = 13,
                Foreground = HubUi.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);

            if (i < shortcuts.Length - 1)
            {
                var divider = new Border
                {
                    Height = 1,
                    Background = HubUi.CardBorder,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Opacity = 0.6
                };
                Grid.SetRow(divider, i);
                Grid.SetColumnSpan(divider, 2);
                grid.Children.Add(divider);
            }
        }
        stackKeys.Children.Add(grid);
        root.Children.Add(cardKeys);

        // ── Cosa può fare FLIP (2 × 3) ──────────────────────────────────────
        var cardFeatures = HubUi.CreateCard(Loc.T("Cosa può fare FLIP", "What FLIP can do"));
        var stackFeatures = (StackPanel)cardFeatures.Child;
        var features = new UniformGrid { Columns = 2, Rows = 3 };
        (string Glyph, string Title, string Line)[] items =
        [
            ("⇄", Loc.T("Conversioni & PDF/A-2b", "Conversions & PDF/A-2b"),
                Loc.T("Immagini, audio, video, 3D, documenti, archivi e PDF/A-2b.", "Images, audio, video, 3D, docs, archives, and PDF/A-2b.")),
            ("✂", Loc.T("Immagini & Privacy", "Images & Privacy"),
                Loc.T("Ritaglio 9:16/manuale, ruota, scala, sfondo AI, oscura aree, EXIF.", "9:16 or free crop, rotate, scale, AI cutout, privacy blur, EXIF.")),
            ("◈", Loc.T("QuickLook 3D · PDF · Palette", "QuickLook 3D · PDF · Palette"),
                Loc.T("Vista 3D con Turntable 360°, copia testo PDF, palette CSS e \"Aa\".", "3D viewer & 360° Turntable, PDF text copy, CSS palette & \"Aa\" contrast.")),
            ("◉", Loc.T("Audio AI & OCR locali", "Local Audio AI & OCR"),
                Loc.T("Trascrizione Whisper (SRT/TXT) e OCR immagini, 100% offline.", "Whisper transcription (SRT/TXT) and image OCR, 100% offline.")),
            ("⚡", Loc.T("Automazione & Batch", "Automation & Batch"),
                Loc.T("Cartelle monitorate, Preset riutilizzabili, Unisci PDF e batch.", "Watch folders, reusable Presets, PDF merge, and batch jobs.")),
            ("◎", Loc.T("Browser, Appunti & Tobii", "Browser, Clipboard & Tobii"),
                Loc.T("Drag da browser, conversione appunti e controllo con lo sguardo.", "Browser drag-drop, clipboard flip, and hands-free eye tracking."))
        ];
        for (int i = 0; i < items.Length; i++)
        {
            int row = i / 2;
            int col = i % 2;
            var tile = new Border
            {
                Background = HubUi.InputBg,
                BorderBrush = HubUi.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(col == 0 ? 0 : 5, row == 0 ? 0 : 5, col == 0 ? 5 : 0, row == 2 ? 0 : 5)
            };
            var tileGrid = new Grid();
            tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tileGrid.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Background = Frozen(0x3B, 0x24, 0x14),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = items[i].Glyph,
                    FontSize = 12,
                    Foreground = HubUi.Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = items[i].Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = HubUi.TextPrimary
            });
            text.Children.Add(new TextBlock
            {
                Text = items[i].Line,
                FontSize = 11.2,
                Foreground = HubUi.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 15,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(text, 1);
            tileGrid.Children.Add(text);
            tile.Child = tileGrid;
            features.Children.Add(tile);
        }
        stackFeatures.Children.Add(features);
        root.Children.Add(cardFeatures);
        root.Children.Add(StoreEdition.IsPackaged ? BuildStoreUpdatesCard() : BuildUpdatesCard());
        root.Children.Add(BuildSupportCard());

        var tour = HubUi.CreateButton(Loc.T("Riapri tour iniziale", "Replay welcome tour"), false, FirstRunWelcomeWindow.ShowSingleton);
        tour.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(tour);
        return root;
    }

    private static bool _checkOnOpen;

    /// <summary>Opens the Updates card from the tray and checks right away: the user has just asked for it.</summary>
    internal static void OpenUpdates()
    {
        _checkOnOpen = true;
        SettingsHubWindow.ShowSingleton(SettingsHubWindow.GuideSectionIndex, null, null, null);
    }

    /// <summary>The Store installs updates on its own; this only opens its page.</summary>
    private static Border BuildStoreUpdatesCard()
    {
        var card = HubUi.CreateCard(Loc.T("Aggiornamenti", "Updates"),
            Loc.F("Versione installata: {0}. Si aggiorna da Microsoft Store.", "Installed version: {0}. Updated by the Microsoft Store.", UpdateChecker.CurrentVersion));
        var button = HubUi.CreateButton(Loc.T("Apri Microsoft Store", "Open Microsoft Store"), false, () =>
        {
            try { StoreEdition.OpenStoreUpdates(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Evidence.Log("store.open.error", ex.Message); }
        });
        button.HorizontalAlignment = HorizontalAlignment.Left;
        ((StackPanel)card.Child).Children.Add(button);
        return card;
    }

    /// <summary>Voluntary support on an external page: it unlocks nothing and FLIP never sees payment data.</summary>
    private static Border BuildSupportCard()
    {
        var card = HubUi.CreateCard(Loc.T("Sostieni FLIP", "Support FLIP"),
            Loc.T("FLIP è gratuito e completo. Se vuoi, puoi offrirmi un caffè: è un contributo volontario e non sblocca funzioni. Si apre il browser su Buy Me a Coffee, che gestisce il pagamento.",
                "FLIP is free and complete. If you like, you can buy me a coffee: it is voluntary and unlocks nothing. Your browser opens Buy Me a Coffee, which handles the payment."));
        var button = HubUi.CreateButton(Loc.T("Offrimi un caffè", "Buy me a coffee"), true, () =>
        {
            try { UpdateChecker.OpenBrowser(UpdateChecker.SupportUrl); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Evidence.Log("support.open.error", ex.Message); }
        });
        button.HorizontalAlignment = HorizontalAlignment.Left;
        ((StackPanel)card.Child).Children.Add(button);
        return card;
    }

    /// <summary>Check GitHub on request, then download, verify and install the latest stable release.</summary>
    private static Border BuildUpdatesCard()
    {
        var card = HubUi.CreateCard(Loc.T("Aggiornamenti", "Updates"));
        var stack = (StackPanel)card.Child;
        stack.Children.Add(new TextBlock
        {
            Text = Loc.F("Versione installata: {0}", "Installed version: {0}", UpdateChecker.CurrentVersion),
            FontSize = 13,
            Foreground = HubUi.TextPrimary,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var actionGrid = new Grid();
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var mainButton = HubUi.CreateButton(Loc.T("Cerca aggiornamenti", "Check for updates"), isPrimary: true);
        UpdatePackage? found = null;
        var notesButton = HubUi.CreateButton(Loc.T("Note di rilascio", "Release notes"), false,
            () => UpdateChecker.OpenBrowser(found?.HtmlUrl ?? UpdateChecker.ReleasesPageUrl));
        notesButton.Margin = new Thickness(8, 0, 0, 0);
        notesButton.Visibility = Visibility.Collapsed;
        buttons.Children.Add(mainButton);
        buttons.Children.Add(notesButton);
        Grid.SetColumn(buttons, 0);

        var progressFill = new Border { Background = HubUi.Accent, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var progressTrack = new Border
        {
            Background = HubUi.BgDark,
            BorderBrush = HubUi.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Height = 10,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = progressFill
        };
        Grid.SetColumn(progressTrack, 1);
        var percentText = new TextBlock
        {
            FontSize = 11,
            Foreground = HubUi.TextSecondary,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(percentText, 2);
        actionGrid.Children.Add(buttons);
        actionGrid.Children.Add(progressTrack);
        actionGrid.Children.Add(percentText);
        stack.Children.Add(actionGrid);

        var status = new TextBlock
        {
            Text = Loc.T("FLIP contatta GitHub solo quando premi il pulsante. Il pacchetto viene verificato con SHA256 prima dell'installazione.",
                "FLIP contacts GitHub only when you press the button. The package is verified with SHA256 before it is installed."),
            FontSize = 11.5,
            Foreground = HubUi.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(status);

        double fraction = 0;
        void RenderProgress() => progressFill.Width = Math.Max(0, progressTrack.ActualWidth - 2) * Math.Clamp(fraction, 0, 1);
        progressTrack.SizeChanged += (_, _) => RenderProgress();
        void SetStatus(string text, Brush brush) { status.Text = text; status.Foreground = brush; }
        void ShowProgress(bool visible)
        {
            progressTrack.Visibility = percentText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            fraction = 0; percentText.Text = "0%"; RenderProgress();
        }

        CancellationTokenSource? running = null;
        string? blockReason = UpdateInstaller.SelfUpdateBlockReason();

        async Task CheckAsync()
        {
            mainButton.IsEnabled = false;
            SetStatus(Loc.T("Controllo su GitHub…", "Checking GitHub…"), HubUi.TextSecondary);
            try
            {
                found = await Task.Run(() => UpdateInstaller.FindNewerAsync(CancellationToken.None));
                if (found == null)
                {
                    SetStatus(Loc.T("✓ FLIP è aggiornato.", "✓ FLIP is up to date."), HubUi.Success);
                    return;
                }
                notesButton.Visibility = Visibility.Visible;
                if (blockReason != null)
                {
                    mainButton.Content = Loc.T("Apri GitHub", "Open GitHub");
                    SetStatus(Loc.F("Disponibile {0}. {1}", "{0} is available. {1}", found.Name, blockReason), HubUi.TextSecondary);
                }
                else
                {
                    mainButton.Content = Loc.F("Scarica e installa {0}", "Download and install {0}", found.TagName);
                    SetStatus(Loc.F("Disponibile {0}.", "{0} is available.", found.Name), HubUi.Accent);
                }
            }
            catch (Exception ex)
            {
                Evidence.Log("update.check.error", ex.Message);
                SetStatus(Loc.F("Impossibile verificare gli aggiornamenti · {0}", "Could not check for updates · {0}", ex.Message), HubUi.Danger);
            }
            finally
            {
                mainButton.IsEnabled = true;
            }
        }

        async Task InstallAsync(UpdatePackage package)
        {
            if (blockReason != null) { UpdateChecker.OpenBrowser(package.HtmlUrl); return; }
            if (UpdateInstaller.Busy)
            {
                SetStatus(Loc.T("Un aggiornamento è già in corso.", "An update is already in progress."), HubUi.TextSecondary);
                return;
            }
            if (!SettingsHubWindow.Confirm(
                    Loc.T("Installa aggiornamento", "Install update"),
                    Loc.F(
                        "FLIP scaricherà {0} da GitHub, verificherà il pacchetto e poi si chiuderà per installarlo.\n\nImpostazioni, collegamenti e avvio automatico restano invariati. La versione attuale viene salvata come backup.\n\nUna finestra di PowerShell mostrerà l'installazione, poi FLIP si riavvierà.",
                        "FLIP will download {0} from GitHub, verify the package and then exit to install it.\n\nSettings, shortcuts and startup stay as they are. The current version is kept as a backup.\n\nA PowerShell window shows the installation, then FLIP restarts.",
                        package.TagName),
                    Loc.T("Installa", "Install")))
                return;

            using var cts = new CancellationTokenSource();
            running = cts;
            UpdateInstaller.Busy = true;
            mainButton.Content = Loc.T("Annulla", "Cancel");
            ShowProgress(true);
            SetStatus(Loc.F("Download di {0}…", "Downloading {0}…", package.ZipName), HubUi.TextSecondary);
            var progress = new Progress<double>(value =>
            {
                if (!ReferenceEquals(running, cts)) return;
                fraction = value; RenderProgress();
                percentText.Text = $"{Math.Clamp(value, 0, 1) * 100:F0}%";
            });

            try
            {
                string packageDir = await Task.Run(() => UpdateInstaller.DownloadAndVerifyAsync(package, progress, cts.Token));
                while (UpdateInstaller.ConversionRunning?.Invoke() == true)
                {
                    SetStatus(Loc.T("✓ Pacchetto verificato. Attendo la fine della conversione in corso…",
                        "✓ Package verified. Waiting for the current conversion to finish…"), HubUi.TextSecondary);
                    await Task.Delay(500, cts.Token);
                }
                cts.Token.ThrowIfCancellationRequested();
                running = null;
                mainButton.IsEnabled = false;
                SetStatus(Loc.T("✓ Pacchetto verificato. FLIP si chiude per installare l'aggiornamento…",
                    "✓ Package verified. FLIP is closing to install the update…"), HubUi.Success);
                UpdateInstaller.LaunchInstallerAndExit(packageDir);
            }
            catch (Exception ex)
            {
                bool canceled = ex is OperationCanceledException || cts.IsCancellationRequested;
                running = null;
                UpdateInstaller.Busy = false;
                UpdateInstaller.CleanupStagedUpdates(null);
                ShowProgress(false);
                mainButton.IsEnabled = true;
                mainButton.Content = Loc.F("Scarica e installa {0}", "Download and install {0}", package.TagName);
                if (!canceled) Evidence.Log("update.install.error", ex.Message);
                SetStatus(canceled
                        ? Loc.T("Annullato.", "Canceled.")
                        : Loc.F("Aggiornamento non riuscito · {0}", "Update failed · {0}", ex.Message),
                    canceled ? HubUi.TextSecondary : HubUi.Danger);
            }
        }

        mainButton.Click += async (_, _) =>
        {
            if (running != null) { running.Cancel(); return; }
            if (found == null) await CheckAsync();
            else await InstallAsync(found);
        };
        // Leaving the section or closing the Hub removes the Cancel button: stop instead of exiting FLIP unexpectedly.
        card.Unloaded += (_, _) => running?.Cancel();
        card.Loaded += async (_, _) =>
        {
            if (!_checkOnOpen) return;
            _checkOnOpen = false;
            card.BringIntoView();
            await CheckAsync();
        };
        return card;
    }

    private static Border KeyCap(string text) => new()
    {
        Background = KeyBg,
        BorderBrush = KeyBorder,
        BorderThickness = new Thickness(1, 1, 1, 2),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(8, 2, 8, 3),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = HubUi.TextPrimary,
            FontFamily = new FontFamily("Segoe UI")
        }
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
