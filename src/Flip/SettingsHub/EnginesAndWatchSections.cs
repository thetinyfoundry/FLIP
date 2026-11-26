using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Flip.Core;
using Flip.Engines;
using Flip.Routing;

namespace Flip.SettingsHub;

/// <summary>
/// Costruisce l'interfaccia della Sezione 4: Motori & Accelerazione GPU (prestazioni, rilevamento motori esterni, priorità).
/// </summary>
public static class EnginesHubSection
{
    private record ExternalEngineMeta(string Key, string DisplayName, string Purpose, string ExecutableName, string InstallHint);

    private static ExternalEngineMeta[] GetKnownEngines() =>
    [
        new("ffmpeg", "FFmpeg", Loc.T("Audio e video", "Audio & video"), "ffmpeg.exe", Loc.T("Installazione automatica in background.", "Automatic background install.")),
        new("pandoc", "Pandoc", Loc.T("Documenti e Markdown", "Documents & Markdown"), "pandoc.exe", Loc.T("Installazione automatica in background.", "Automatic background install.")),
        new("libreoffice", "LibreOffice", Loc.T("Office, PDF e formati storici", "Office, PDF & legacy docs"), "soffice.com", Loc.T("Installazione automatica in background.", "Automatic background install.")),
        new("calibre", "Calibre", "Ebook", "ebook-convert.exe", Loc.T("Installazione automatica in background.", "Automatic background install.")),
        new("whisper", "Whisper", Loc.T("Trascrizione audio locale", "Local audio transcription"), "whisper-cli.exe", Loc.T("Motore e modello restano sul tuo PC.", "Engine and model stay 100% local.")),
        new("tesseract", "Tesseract", "OCR", "tesseract.exe", Loc.T("Installazione automatica in background.", "Automatic background install."))
    ];

    public static UIElement Build()
    {
        CliResolver.VerifyAndDetectEngines(SettingsStore.Current);
        OnnxRemoveBgEngine.CleanupStalePath(SettingsStore.Current);
        var settings = SettingsStore.Current.Settings;
        var root = new StackPanel();
        var knownEngines = GetKnownEngines();

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Motori & GPU", "Engines & GPU"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Prestazioni e motori per i formati avanzati.", "Performance and engines for advanced formats."),
            FontSize = 13,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 20)
        });

        // ── Card 1: Prestazioni & GPU ───────────────────────────────────────
        var cardPerf = HubUi.CreateCard(
            Loc.T("Prestazioni", "Performance"),
            Loc.T("Conversioni in parallelo ed encoder GPU.", "Parallel jobs and GPU hardware encoder."));
        var stackPerf = (StackPanel)cardPerf.Child;

        var perfGrid = new Grid();
        perfGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        perfGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Parallel Jobs
        var leftStack = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        leftStack.Children.Add(new TextBlock
        {
            Text = Loc.T("Conversioni in parallelo", "Parallel conversions"),
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 4)
        });

        int[] parallelOptions = [1, 2, 4, 6, 8];
        var comboParallel = HubUi.CreateComboBox(
            parallelOptions,
            settings.MaxParallelJobs,
            val => SettingsStore.Current.Update(s => s.MaxParallelJobs = val));
        leftStack.Children.Add(comboParallel);
        Grid.SetColumn(leftStack, 0);
        perfGrid.Children.Add(leftStack);

        // Hardware Acceleration
        var rightStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        rightStack.Children.Add(new TextBlock
        {
            Text = Loc.T("Encoder GPU", "GPU encoder"),
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 4)
        });

        GpuChoice[] gpuChoices =
        [
            new(HardwareAcceleration.Auto, Loc.T("Automatico", "Automatic")),
            new(HardwareAcceleration.Nvenc, "NVIDIA NVENC"),
            new(HardwareAcceleration.Amf, "AMD AMF"),
            new(HardwareAcceleration.Qsv, "Intel Quick Sync"),
            new(HardwareAcceleration.CpuOnly, Loc.T("Solo CPU", "CPU only"))
        ];
        var comboGpu = HubUi.CreateComboBox(
            gpuChoices,
            gpuChoices.FirstOrDefault(g => g.Value == settings.HardwareAcceleration) ?? gpuChoices[0],
            val => SettingsStore.Current.Update(s => s.HardwareAcceleration = val.Value));
        rightStack.Children.Add(comboGpu);
        Grid.SetColumn(rightStack, 1);
        perfGrid.Children.Add(rightStack);

        stackPerf.Children.Add(perfGrid);
        root.Children.Add(cardPerf);

        // ── Card 2: Rimozione Sfondo AI (IS-Net ONNX) ───────────────────────
        var (cardRemoveBg, syncRemoveBgCard) = CreateRemoveBgAiCard();
        root.Children.Add(cardRemoveBg);

        // ── Card 3: Catalogo Motori Esterni ─────────────────────────────────
        var cardEngines = HubUi.CreateCard(
            Loc.T("Motori esterni", "External engines"),
            Loc.T("Rileva o installa ogni motore con un clic.", "Detect or install each engine in one click."));
        var stackEngines = (StackPanel)cardEngines.Child;

        var verifyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var verifyStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var rowRefreshers = new List<Action> { syncRemoveBgCard };
        var btnVerifyAll = HubUi.CreateButton(Loc.T("↻ Verifica motori", "↻ Verify engines"), false, () =>
        {
            CliResolver.VerifyAndDetectEngines(SettingsStore.Current);
            OnnxRemoveBgEngine.CleanupStalePath(SettingsStore.Current);
            foreach (var refresh in rowRefreshers) refresh();
            // Conta solo i motori elencati qui: WinRAR è rilevato ma non ha una riga nell'Hub.
            var snapshot = SettingsStore.Current.CreateSnapshot();
            int activeCount = knownEngines.Count(meta => CliResolver.Resolve(meta.Key, snapshot) != null);
            verifyStatus.Text = Loc.F("{0} di {1} motori pronti", "{0} of {1} engines ready", activeCount, knownEngines.Length);
            verifyStatus.Foreground = activeCount > 0 ? HubUi.Success : HubUi.TextSecondary;
        });
        verifyRow.Children.Add(btnVerifyAll);
        verifyRow.Children.Add(verifyStatus);
        stackEngines.Children.Add(verifyRow);

        foreach (var meta in knownEngines)
        {
            var (rowBorder, syncRow) = CreateEngineRow(meta);
            rowRefreshers.Add(syncRow);
            stackEngines.Children.Add(rowBorder);
        }

        root.Children.Add(cardEngines);

        // ── Card 3: Priorità Motori ─────────────────────────────────────────
        var cardPriority = HubUi.CreateCard(
            Loc.T("Priorità motori", "Engine priority"),
            Loc.T("A parità di percorso, vince il primo della lista.", "When multiple engines match, the top one wins."));
        var stackPriority = (StackPanel)cardPriority.Child;

        var priorityListPanel = new StackPanel();

        void RefreshPriorityList()
        {
            priorityListPanel.Children.Clear();
            var list = settings.EnginePriorityOrder;
            if (list.Count == 0)
            {
                list = ["image_builtin", "data_builtin", "pdf_builtin", "ffmpeg", "pandoc", "libreoffice", "calibre", "whisper"];
                settings.EnginePriorityOrder = [.. list];
            }

            for (int i = 0; i < list.Count; i++)
            {
                int index = i;
                string engineId = list[i];

                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var numBlock = new TextBlock
                {
                    Text = $"{index + 1}.",
                    FontWeight = FontWeights.Bold,
                    Foreground = HubUi.Accent,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(numBlock, 0);
                row.Children.Add(numBlock);

                var nameBlock = new TextBlock
                {
                    Text = EngineLabel(engineId),
                    FontSize = 13,
                    Foreground = HubUi.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(nameBlock, 1);
                row.Children.Add(nameBlock);

                var btnStack = new StackPanel { Orientation = Orientation.Horizontal };
                if (index > 0)
                {
                    var upBtn = HubUi.CreateButton("▲", false, () =>
                    {
                        SettingsStore.Current.Update(s =>
                        {
                            (s.EnginePriorityOrder[index - 1], s.EnginePriorityOrder[index]) =
                                (s.EnginePriorityOrder[index], s.EnginePriorityOrder[index - 1]);
                        });
                        RefreshPriorityList();
                    });
                    upBtn.Padding = new Thickness(8, 2, 8, 2);
                    upBtn.Margin = new Thickness(0, 0, 4, 0);
                    btnStack.Children.Add(upBtn);
                }

                if (index < list.Count - 1)
                {
                    var downBtn = HubUi.CreateButton("▼", false, () =>
                    {
                        SettingsStore.Current.Update(s =>
                        {
                            (s.EnginePriorityOrder[index + 1], s.EnginePriorityOrder[index]) =
                                (s.EnginePriorityOrder[index], s.EnginePriorityOrder[index + 1]);
                        });
                        RefreshPriorityList();
                    });
                    downBtn.Padding = new Thickness(8, 2, 8, 2);
                    btnStack.Children.Add(downBtn);
                }

                Grid.SetColumn(btnStack, 2);
                row.Children.Add(btnStack);
                priorityListPanel.Children.Add(row);
            }
        }

        RefreshPriorityList();
        stackPriority.Children.Add(priorityListPanel);
        root.Children.Add(cardPriority);

        return root;
    }

    private static (Border Card, Action SyncFromDisk) CreateRemoveBgAiCard()
    {
        var card = HubUi.CreateCard(
            Loc.T("Rimozione Sfondo AI (IS-Net ONNX)", "AI Background Removal (IS-Net ONNX)"),
            Loc.T("Scontorno locale ad alta precisione senza Python né cloud (Apache-2.0, ~170 MB).", "High-precision local cutouts with no Python or cloud (Apache-2.0, ~170 MB)."));
        var stack = (StackPanel)card.Child;

        var rowBorder = new Border
        {
            Background = HubUi.InputBg,
            BorderBrush = HubUi.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12)
        };
        var rowStack = new StackPanel();

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var modelInfo = new TextBlock
        {
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(modelInfo, 0);
        topGrid.Children.Add(modelInfo);

        var statusBadge = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(statusBadge, 1);
        topGrid.Children.Add(statusBadge);
        rowStack.Children.Add(topGrid);

        var pathGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var feedback = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        void ShowFeedback(string text, bool ok)
        {
            feedback.Text = text;
            feedback.Foreground = ok ? HubUi.Success : HubUi.Accent;
            feedback.Visibility = Visibility.Visible;
        }

        CancellationTokenSource? running = null;

        var installButton = HubUi.CreateButton(Loc.T("Scarica 1-Click", "1-Click Download"), isPrimary: true);
        installButton.HorizontalAlignment = HorizontalAlignment.Left;

        void RefreshStatus()
        {
            string? resolved = OnnxRemoveBgEngine.ResolveModelPath();
            bool active = !string.IsNullOrWhiteSpace(resolved);

            statusBadge.Text = active ? Loc.T("● ATTIVO", "● ACTIVE") : Loc.T("○ NON INSTALLATO", "○ NOT INSTALLED");
            statusBadge.Foreground = active ? HubUi.Success : HubUi.TextMuted;
            statusBadge.ToolTip = resolved;

            modelInfo.Text = active
                ? Loc.F("Modello attivo: {0} (1024×1024)", "Active model: {0} (1024×1024)", Path.GetFileName(resolved))
                : Loc.F("Modello richiesto: {0} (1024×1024)", "Required model: {0} (1024×1024)", OnnxRemoveBgEngine.ModelFileName);
            modelInfo.ToolTip = resolved;

            if (running == null)
            {
                installButton.Content = active ? Loc.T("Reinstalla", "Reinstall") : Loc.T("Scarica 1-Click", "1-Click Download");
                installButton.Tag = active ? null : "Primary";
                installButton.Background = active ? HubUi.CardBg : HubUi.Accent;
                installButton.Foreground = active ? HubUi.TextPrimary : Brushes.White;
                installButton.BorderBrush = active ? HubUi.CardBorder : HubUi.AccentHover;
            }
        }

        var txtPath = HubUi.CreateTextBox(OnnxRemoveBgEngine.ResolveModelPath() ?? "", val =>
        {
            string trimmed = val.Trim().Trim('"');
            SettingsStore.Current.Update(s =>
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                    s.CustomEnginePaths.Remove(OnnxRemoveBgEngine.ModelKey);
                else
                    s.CustomEnginePaths[OnnxRemoveBgEngine.ModelKey] = trimmed;
            });
            OnnxRemoveBgEngine.InvalidateSessionCache();
            RefreshStatus();
        });
        Grid.SetColumn(txtPath, 0);
        pathGrid.Children.Add(txtPath);

        void SelectPath(string path)
        {
            if (string.Equals(txtPath.Text, path, StringComparison.Ordinal))
                RefreshStatus();
            else
                txtPath.Text = path;
        }

        var btnBrowse = HubUi.CreateButton(Loc.T("Sfoglia...", "Browse…"), false, () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.T("FLIP · Modello ONNX Rimozione Sfondo", "FLIP · Background Removal ONNX Model"),
                Filter = Loc.T("Modelli ONNX (*.onnx)|*.onnx|Tutti i file (*.*)|*.*", "ONNX models (*.onnx)|*.onnx|All files (*.*)|*.*"),
                FileName = OnnxRemoveBgEngine.ModelFileName
            };

            if (dialog.ShowDialog() == true)
            {
                if (!OnnxRemoveBgEngine.IsUsableModelFile(dialog.FileName))
                {
                    ShowFeedback(Loc.T("File non valido: seleziona un modello .onnx valido (almeno 1 MB).", "Invalid file: select a valid .onnx model (at least 1 MB)."), ok: false);
                    return;
                }

                SelectPath(dialog.FileName);
                ShowFeedback(Loc.F("Modello collegato: {0}", "Model linked: {0}", Path.GetFileName(dialog.FileName)), ok: true);
            }
        });
        btnBrowse.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(btnBrowse, 1);
        pathGrid.Children.Add(btnBrowse);

        rowStack.Children.Add(pathGrid);
        rowStack.Children.Add(feedback);

        // Pannello Scarica 1-Click con progresso reale (MB e fase)
        var actionPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var actionGrid = new Grid();
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(installButton, 0);

        var progressFill = new Border
        {
            Background = HubUi.Accent,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
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
            FontWeight = FontWeights.SemiBold,
            Foreground = HubUi.Accent,
            MinWidth = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(percentText, 2);

        var statusText = new TextBlock
        {
            Text = Loc.T("Scarica in %APPDATA%\\FLIP\\Engines\\RemoveBg con verifica integrità SHA-256.", "Downloads to %APPDATA%\\FLIP\\Engines\\RemoveBg with SHA-256 verification."),
            FontSize = 11,
            Foreground = HubUi.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        double fraction = 0;
        void RenderProgress()
        {
            double available = Math.Max(0, progressTrack.ActualWidth - 2);
            progressFill.Width = available * Math.Clamp(fraction, 0, 1);
        }
        progressTrack.SizeChanged += (_, _) => RenderProgress();

        void ResetInstallUi()
        {
            progressTrack.Visibility = Visibility.Collapsed;
            percentText.Visibility = Visibility.Collapsed;
            RefreshStatus();
        }

        installButton.Click += async (_, _) =>
        {
            if (running != null)
            {
                running.Cancel();
                return;
            }

            using var cts = new CancellationTokenSource();
            running = cts;
            installButton.Content = Loc.T("Annulla", "Cancel");
            progressTrack.Visibility = Visibility.Visible;
            percentText.Visibility = Visibility.Visible;
            percentText.Text = "0%";
            statusText.Text = Loc.T("Preparazione download IS-Net…", "Preparing IS-Net download…");
            statusText.Foreground = HubUi.TextSecondary;
            fraction = 0;
            RenderProgress();

            var progress = new Progress<WhisperInstallProgress>(update =>
            {
                if (!ReferenceEquals(running, cts)) return;
                fraction = update.Fraction;
                RenderProgress();
                percentText.Text = $"{Math.Clamp(update.Fraction, 0, 1) * 100:F0}%";
                statusText.Text = update.Status.Trim().TrimStart('●', ' ');
            });

            try
            {
                string modelPath = await Task.Run(() => OnnxRemoveBgEngine.DownloadModelAsync(progress, cts.Token));
                running = null;
                statusText.Text = Loc.F("✓ Installato · {0}", "✓ Installed · {0}", modelPath);
                statusText.Foreground = HubUi.Success;
                ResetInstallUi();
                SelectPath(modelPath);
            }
            catch (OperationCanceledException)
            {
                running = null;
                statusText.Text = Loc.T("Download annullato.", "Download canceled.");
                statusText.Foreground = HubUi.TextSecondary;
                ResetInstallUi();
            }
            catch (Exception ex)
            {
                running = null;
                Evidence.Log("removebg.install.error", ex.Message);
                statusText.Text = Loc.F("Download non riuscito · {0}", "Download failed · {0}", ex.Message);
                statusText.Foreground = HubUi.Danger;
                ResetInstallUi();
            }
        };

        actionGrid.Children.Add(installButton);
        actionGrid.Children.Add(progressTrack);
        actionGrid.Children.Add(percentText);
        actionPanel.Children.Add(actionGrid);
        actionPanel.Children.Add(statusText);
        rowStack.Children.Add(actionPanel);

        rowBorder.Child = rowStack;
        stack.Children.Add(rowBorder);

        void SyncFromDisk()
        {
            string fresh = OnnxRemoveBgEngine.ResolveModelPath() ?? "";
            if (!string.Equals(txtPath.Text, fresh, StringComparison.Ordinal))
                txtPath.Text = fresh;
            else
                RefreshStatus();
        }

        RefreshStatus();
        return (card, SyncFromDisk);
    }

    private static (Border Row, Action SyncFromDisk) CreateEngineRow(ExternalEngineMeta meta)
    {
        var rowBorder = new Border
        {
            Background = HubUi.InputBg,
            BorderBrush = HubUi.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var rowStack = new StackPanel();

        // Header: Nome e Badge di Stato
        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        titleBlock.Inlines.Add(new System.Windows.Documents.Run(meta.DisplayName) { FontWeight = FontWeights.SemiBold, Foreground = HubUi.TextPrimary });
        titleBlock.Inlines.Add(new System.Windows.Documents.Run("  ·  " + meta.Purpose) { Foreground = HubUi.TextSecondary, FontSize = 12 });
        Grid.SetColumn(titleBlock, 0);
        topGrid.Children.Add(titleBlock);

        var statusBadge = new TextBlock
        {
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(statusBadge, 1);
        topGrid.Children.Add(statusBadge);
        rowStack.Children.Add(topGrid);

        // Motore mancante: cosa si guadagna installandolo. La ruota mostra solo ciò che è già disponibile.
        var addsFormats = new TextBlock
        {
            FontSize = 11.5,
            Foreground = HubUi.Accent,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        rowStack.Children.Add(addsFormats);
        void RefreshAddedFormats()
        {
            const int shown = 8;
            var added = CliResolver.Resolve(meta.Key) == null
                ? RoutePlanner.GetFormatsAddedByEngine(meta.Key, SettingsStore.Current.CreateSnapshot())
                : [];
            addsFormats.Visibility = added.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (added.Count == 0) return;
            string list = string.Join(", ", added.Take(shown)) + (added.Count > shown ? $" +{added.Count - shown}" : "");
            addsFormats.Text = Loc.F("Installandolo la ruota offre anche: {0}", "Installing it adds to the wheel: {0}", list);
            addsFormats.ToolTip = string.Join(", ", added);
        }

        // Path input + Pulsanti
        var pathGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var feedback = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        void ShowFeedback(string text, bool ok)
        {
            feedback.Text = text;
            feedback.Foreground = ok ? HubUi.Success : HubUi.Accent;
            feedback.Visibility = Visibility.Visible;
        }

        Action? afterBadgeRefresh = null;
        void RefreshBadge()
        {
            UpdateEngineBadge(statusBadge, meta.Key);
            RefreshAddedFormats();
            afterBadgeRefresh?.Invoke();
        }

        string ResolveInitialPath()
        {
            var snap = SettingsStore.Current.CreateSnapshot();
            if (snap.CustomEnginePaths.TryGetValue(meta.Key, out var p) && !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return p;
            return CliResolver.Resolve(meta.Key) ?? "";
        }

        var txtPath = HubUi.CreateTextBox(ResolveInitialPath(), val =>
        {
            SettingsStore.Current.Update(s =>
            {
                // Il percorso del motore incluso cambia a ogni aggiornamento dello Store: non va salvato.
                if (string.IsNullOrWhiteSpace(val) || string.Equals(val, StoreEdition.BundledEngine(meta.Key), StringComparison.OrdinalIgnoreCase))
                    s.CustomEnginePaths.Remove(meta.Key);
                else s.CustomEnginePaths[meta.Key] = val;
            });
            if (meta.Key == "whisper") WhisperLocator.InvalidateCache();
            RefreshBadge();
        });
        Grid.SetColumn(txtPath, 0);
        pathGrid.Children.Add(txtPath);

        void SelectPath(string path)
        {
            if (txtPath.Text == path) RefreshBadge();
            else txtPath.Text = path;
        }

        var btnBrowse = HubUi.CreateButton(Loc.T("Sfoglia...", "Browse…"), false, () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = Loc.F("FLIP · Eseguibile {0}", "FLIP · {0} executable", meta.DisplayName),
                Filter = Loc.F("{0}|{0}|Eseguibili (*.exe, *.com)|*.exe;*.com|Tutti i file (*.*)|*.*", "{0}|{0}|Executables (*.exe, *.com)|*.exe;*.com|All files (*.*)|*.*", meta.ExecutableName),
                FileName = meta.ExecutableName
            };

            if (dialog.ShowDialog() == true)
            {
                SelectPath(dialog.FileName);
                if (meta.Key == "whisper" && TryAttachWhisperModel(dialog.FileName) is { } attached)
                {
                    RefreshBadge();
                    ShowFeedback(Loc.F("Modello collegato: {0}", "Model linked: {0}", Path.GetFileName(attached)), ok: true);
                }
            }
        });
        btnBrowse.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(btnBrowse, 1);
        pathGrid.Children.Add(btnBrowse);

        var btnDetect = HubUi.CreateButton(Loc.T("Rileva", "Detect"), false, () =>
        {
            WhisperLocator.InvalidateCache();
            string? found = CliResolver.Resolve(meta.Key, new FlipSettings());
            if (!string.IsNullOrEmpty(found) && File.Exists(found))
            {
                SelectPath(found);
                if (meta.Key == "whisper") TryAttachWhisperModel(found);
                RefreshBadge();
                ShowFeedback(Loc.F("Trovato: {0}", "Found: {0}", found), ok: true);
            }
            else
            {
                SelectPath("");
                ShowFeedback(Loc.F("{0} non trovato sul PC.", "{0} not found on this PC.", meta.DisplayName), ok: false);
            }
        });
        btnDetect.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(btnDetect, 2);
        pathGrid.Children.Add(btnDetect);

        rowStack.Children.Add(pathGrid);
        rowStack.Children.Add(feedback);

        TextBlock? whisperDetails = null;
        if (meta.Key == "whisper")
        {
            whisperDetails = new TextBlock
            {
                FontSize = 11,
                Foreground = HubUi.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            rowStack.Children.Add(whisperDetails);

            var btnChooseModel = HubUi.CreateButton(Loc.T("Scegli modello .bin…", "Choose .bin model…"), false, () =>
            {
                var modelDialog = new OpenFileDialog
                {
                    Title = Loc.T("FLIP · Modello Whisper", "FLIP · Whisper Model"),
                    Filter = Loc.T("Modelli Whisper (*.bin)|*.bin|Tutti i file (*.*)|*.*", "Whisper models (*.bin)|*.bin|All files (*.*)|*.*")
                };

                if (modelDialog.ShowDialog() == true)
                {
                    if (!WhisperLocator.IsUsableModelFile(modelDialog.FileName))
                    {
                        ShowFeedback(Loc.T("File non valido: serve un modello ggml-*.bin da almeno 30 MB.", "Invalid file: select a ggml-*.bin model of at least 30 MB."), ok: false);
                        return;
                    }
                    SettingsStore.Current.Update(s => s.CustomEnginePaths["whisper_model"] = modelDialog.FileName);
                    WhisperLocator.InvalidateCache();
                    RefreshBadge();
                    ShowFeedback(Loc.F("Modello collegato: {0}", "Model linked: {0}", Path.GetFileName(modelDialog.FileName)), ok: true);
                }
            });
            btnChooseModel.HorizontalAlignment = HorizontalAlignment.Left;
            btnChooseModel.Margin = new Thickness(0, 8, 0, 0);
            rowStack.Children.Add(btnChooseModel);
        }

        var (installPanel, syncInstallPanel) = CreateEngineInstallPanel(meta, SelectPath);
        rowStack.Children.Add(installPanel);

        afterBadgeRefresh = () =>
        {
            if (whisperDetails != null)
            {
                string? exe = CliResolver.Resolve("whisper");
                string? model = exe != null ? LocalAiEngine.FindModel(exe) : WhisperLocator.FindGlobalModel();
                string notFound = Loc.T("non trovato", "not found");
                string exeText = exe ?? notFound;
                string modelText = model != null ? Path.GetFileName(model) : notFound;
                whisperDetails.Text = Loc.F("Motore: {0}\nModello: {1}", "Engine: {0}\nModel: {1}", exeText, modelText);
                whisperDetails.ToolTip = model;
            }
            syncInstallPanel();
        };

        void SyncFromDisk()
        {
            string fresh = ResolveInitialPath();
            if (!string.Equals(txtPath.Text, fresh, StringComparison.Ordinal))
                txtPath.Text = fresh;
            else
                RefreshBadge();
        }

        RefreshBadge();

        rowBorder.Child = rowStack;
        return (rowBorder, SyncFromDisk);
    }

    /// <summary>
    /// Pannello Installer 1-Click unificato per tutti i 6 motori (whisper, pandoc, ffmpeg, tesseract, calibre, libreoffice).
    /// </summary>
    private static (UIElement Panel, Action Sync) CreateEngineInstallPanel(ExternalEngineMeta meta, Action<string> selectPath)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var actionGrid = new Grid();
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var percentText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = HubUi.Accent,
            MinWidth = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(percentText, 2);

        var progressFill = new Border
        {
            Background = HubUi.Accent,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
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

        var statusText = new TextBlock
        {
            Text = meta.InstallHint,
            FontSize = 11,
            Foreground = HubUi.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        double fraction = 0;
        void RenderProgress()
        {
            double available = Math.Max(0, progressTrack.ActualWidth - 2);
            progressFill.Width = available * Math.Clamp(fraction, 0, 1);
        }
        progressTrack.SizeChanged += (_, _) => RenderProgress();

        CancellationTokenSource? running = null;
        bool IsActiveNow() => meta.Key == "whisper" ? IsWhisperActive() : IsStandardEngineActive(meta.Key);

        var installButton = HubUi.CreateButton(Loc.T("Scarica 1-Click", "1-Click Install"), isPrimary: true);
        installButton.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(installButton, 0);

        void SyncButtonState()
        {
            if (running != null) return;
            bool active = IsActiveNow();
            installButton.Content = active ? Loc.T("Reinstalla", "Reinstall") : Loc.T("Scarica 1-Click", "1-Click Install");
            installButton.Tag = active ? null : "Primary";
            installButton.Background = active ? HubUi.CardBg : HubUi.Accent;
            installButton.Foreground = active ? HubUi.TextPrimary : Brushes.White;
            installButton.BorderBrush = active ? HubUi.CardBorder : HubUi.AccentHover;
        }

        void ResetInstallButton()
        {
            progressTrack.Visibility = Visibility.Collapsed;
            percentText.Visibility = Visibility.Collapsed;
            SyncButtonState();
        }

        installButton.Click += async (_, _) =>
        {
            if (running != null)
            {
                running.Cancel();
                return;
            }

            using var cts = new CancellationTokenSource();
            running = cts;
            installButton.Content = Loc.T("Annulla", "Cancel");
            progressTrack.Visibility = Visibility.Visible;
            percentText.Visibility = Visibility.Visible;
            percentText.Text = "0%";
            statusText.Text = Loc.T("Preparazione…", "Preparing…");
            statusText.Foreground = HubUi.TextSecondary;
            fraction = 0;
            RenderProgress();

            var progress = new Progress<WhisperInstallProgress>(update =>
            {
                if (!ReferenceEquals(running, cts)) return;
                fraction = update.Fraction;
                RenderProgress();
                percentText.Text = $"{Math.Clamp(update.Fraction, 0, 1) * 100:F0}%";
                statusText.Text = update.Status.Trim().TrimStart('●', ' ');
            });

            try
            {
                string executable = await Task.Run(() => EngineInstaller.InstallAsync(meta.Key, progress, cts.Token));
                running = null;
                statusText.Text = Loc.F("✓ Installato · {0}", "✓ Installed · {0}", executable);
                statusText.Foreground = HubUi.Success;
                ResetInstallButton();
                selectPath(executable);
            }
            catch (OperationCanceledException)
            {
                running = null;
                statusText.Text = Loc.T("Annullato.", "Canceled.");
                ResetInstallButton();
            }
            catch (Exception ex)
            {
                running = null;
                Evidence.Log($"{meta.Key}.install.error", ex.Message);
                statusText.Text = Loc.F("Installazione non riuscita · {0}", "Install failed · {0}", ex.Message);
                statusText.Foreground = HubUi.Danger;
                ResetInstallButton();
            }
        };

        actionGrid.Children.Add(installButton);
        actionGrid.Children.Add(progressTrack);
        actionGrid.Children.Add(percentText);
        panel.Children.Add(actionGrid);
        panel.Children.Add(statusText);

        SyncButtonState();
        return (panel, SyncButtonState);
    }

    private sealed record GpuChoice(HardwareAcceleration Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static string EngineLabel(string engineId) => engineId switch
    {
        "image_builtin" => Loc.T("Immagini · integrato", "Images · built-in"),
        "data_builtin" => Loc.T("Dati · integrato", "Data · built-in"),
        "pdf_builtin" => Loc.T("PDF · integrato", "PDF · built-in"),
        "ffmpeg" => "FFmpeg",
        "pandoc" => "Pandoc",
        "libreoffice" => "LibreOffice",
        "calibre" => "Calibre",
        "whisper" => "Whisper",
        "tesseract" => "Tesseract",
        _ => engineId
    };

    /// <summary>
    /// Dopo la scelta manuale di whisper-cli: se nessun modello valido è già collegato, aggancia il primo
    /// <c>*.bin</c> utilizzabile accanto all'eseguibile o in <c>models/</c> (anche una cartella sopra). Restituisce il modello collegato.
    /// </summary>
    private static string? TryAttachWhisperModel(string executablePath)
    {
        var snap = SettingsStore.Current.CreateSnapshot();
        if (snap.CustomEnginePaths.TryGetValue("whisper_model", out var current) &&
            !string.IsNullOrWhiteSpace(current) && File.Exists(current) && WhisperLocator.IsUsableModelFile(current))
            return null;

        string? dir = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(dir)) return null;
        string? parent = Path.GetDirectoryName(dir);
        var candidates = new List<string> { dir, Path.Combine(dir, "models") };
        if (parent != null) candidates.Add(Path.Combine(parent, "models"));
        string? model = null;
        foreach (string folder in candidates)
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                model = Directory.EnumerateFiles(folder, "*.bin", SearchOption.TopDirectoryOnly)
                    .Where(path => WhisperLocator.IsUsableModelFile(path))
                    .OrderByDescending(path => Path.GetFileName(path).StartsWith("ggml-", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => new FileInfo(path).Length)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                model = null;
            }
            if (model != null) break;
        }
        if (model == null) return null;
        SettingsStore.Current.Update(s => s.CustomEnginePaths["whisper_model"] = model);
        WhisperLocator.InvalidateCache();
        return model;
    }

    private static bool IsStandardEngineActive(string engineId)
    {
        string? resolved = CliResolver.Resolve(engineId);
        return !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved);
    }

    private static bool IsWhisperActive()
    {
        string? executable = CliResolver.Resolve("whisper");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return false;
        string? model = LocalAiEngine.FindModel(executable);
        return !string.IsNullOrWhiteSpace(model) && File.Exists(model) && WhisperLocator.IsUsableModelFile(model);
    }

    private static void UpdateEngineBadge(TextBlock badge, string engineId)
    {
        string? resolvedPath = CliResolver.Resolve(engineId);
        if (!string.IsNullOrWhiteSpace(resolvedPath) && !File.Exists(resolvedPath))
            resolvedPath = null;

        badge.ToolTip = resolvedPath;
        if (engineId == "whisper" && resolvedPath is not null)
        {
            bool active = IsWhisperActive();
            badge.Text = active ? Loc.T("● ATTIVO", "● ACTIVE") : Loc.T("◐ MODELLO MANCANTE", "◐ MODEL MISSING");
            badge.Foreground = active ? HubUi.Success : HubUi.Accent;
            return;
        }

        if (resolvedPath is not null)
        {
            badge.Text = Loc.T("● PRONTO", "● READY");
            badge.Foreground = HubUi.Success;
        }
        else
        {
            badge.Text = Loc.T("○ NON INSTALLATO", "○ NOT INSTALLED");
            badge.Foreground = HubUi.TextMuted;
        }
    }

}

/// <summary>
/// Costruisce l'interfaccia della Sezione 5: Cartelle Automatiche (monitoraggio automatico in background).
/// </summary>
public static class WatchFoldersSection
{
    public static UIElement Build()
    {
        CliResolver.VerifyAndDetectEngines(SettingsStore.Current);
        var settings = SettingsStore.Current.Settings;
        var root = new StackPanel();

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Cartelle Automatiche", "Watch Folders"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Converte da sola ogni nuovo file in una cartella.", "Automatically converts every new file dropped into a folder."),
            FontSize = 13,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 20)
        });

        var cardList = HubUi.CreateCard(
            Loc.T("Cartelle monitorate", "Watched folders"),
            Loc.T("Es. Screenshot o Download.", "E.g. Screenshots or Downloads."));
        var stackList = (StackPanel)cardList.Child;

        var rulesPanel = new StackPanel();

        void RenderRules()
        {
            rulesPanel.Children.Clear();

            if (settings.WatchFolders.Count == 0)
            {
                rulesPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Nessuna cartella monitorata.", "No watched folders yet."),
                    FontSize = 12,
                    Foreground = HubUi.TextMuted,
                    Margin = new Thickness(0, 8, 0, 12)
                });
            }
            else
            {
                foreach (var rule in settings.WatchFolders)
                {
                    rulesPanel.Children.Add(CreateRuleCard(rule, settings, RenderRules));
                }
            }
        }

        RenderRules();
        stackList.Children.Add(rulesPanel);

        var btnAdd = HubUi.CreateButton(Loc.T("+ Aggiungi cartella", "+ Add folder"), true, () =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = Loc.T("FLIP · Cartella da monitorare", "FLIP · Folder to watch"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            if (dialog.ShowDialog() == true)
            {
                var newRule = new WatchFolderRule
                {
                    FolderPath = dialog.FolderName,
                    Enabled = true,
                    IncludeExtensions = ["png", "jpg"],
                    TargetExtension = "webp",
                    DeleteSourceToRecycleBin = false
                };

                SettingsStore.Current.Update(s => s.WatchFolders.Add(newRule));
                RenderRules();
            }
        });
        btnAdd.Margin = new Thickness(0, 14, 0, 0);
        btnAdd.HorizontalAlignment = HorizontalAlignment.Left;
        stackList.Children.Add(btnAdd);

        root.Children.Add(cardList);
        return root;
    }

    private static Border CreateRuleCard(WatchFolderRule rule, FlipSettings settings, Action onModified)
    {
        var border = new Border
        {
            Background = HubUi.InputBg,
            BorderBrush = HubUi.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel();

        // Top Row: Path e Toggle Attivo
        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pathBlock = new TextBlock
        {
            Text = $"📁 {rule.FolderPath}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = HubUi.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(pathBlock, 0);
        topGrid.Children.Add(pathBlock);

        var cbActive = new CheckBox
        {
            IsChecked = rule.Enabled,
            Content = new TextBlock { Text = Loc.T("Attiva", "Active"), Foreground = HubUi.TextPrimary, Margin = new Thickness(4, -1, 0, 0) },
            VerticalAlignment = VerticalAlignment.Center
        };
        cbActive.Checked += (_, _) => { SettingsStore.Current.Update(_ => rule.Enabled = true); };
        cbActive.Unchecked += (_, _) => { SettingsStore.Current.Update(_ => rule.Enabled = false); };
        Grid.SetColumn(cbActive, 1);
        topGrid.Children.Add(cbActive);
        stack.Children.Add(topGrid);

        // Parametri di conversione: In -> Out
        var paramsGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        paramsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Include Extensions
        var leftStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        leftStack.Children.Add(new TextBlock { Text = Loc.T("Da · es. png, jpg", "From · e.g. png, jpg"), FontSize = 11, Foreground = HubUi.TextSecondary });
        var txtIn = HubUi.CreateTextBox(string.Join(", ", rule.IncludeExtensions), val =>
        {
            rule.IncludeExtensions = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.TrimStart('.').ToLowerInvariant())
                .ToList();
            SettingsStore.Current.Save();
        });
        leftStack.Children.Add(txtIn);
        Grid.SetColumn(leftStack, 0);
        paramsGrid.Children.Add(leftStack);

        // Target Extension
        var rightStack = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
        rightStack.Children.Add(new TextBlock { Text = Loc.T("A · es. webp", "To · e.g. webp"), FontSize = 11, Foreground = HubUi.TextSecondary });
        var txtOut = HubUi.CreateTextBox(rule.TargetExtension, val =>
        {
            rule.TargetExtension = val.TrimStart('.').ToLowerInvariant();
            SettingsStore.Current.Save();
        });
        rightStack.Children.Add(txtOut);
        Grid.SetColumn(rightStack, 1);
        paramsGrid.Children.Add(rightStack);

        // Pulsante Rimuovi
        var btnDel = HubUi.CreateButton(Loc.T("Elimina", "Delete"), false, () =>
        {
            SettingsStore.Current.Update(s => s.WatchFolders.Remove(rule));
            onModified();
        });
        btnDel.Foreground = HubUi.Danger;
        btnDel.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(btnDel, 2);
        paramsGrid.Children.Add(btnDel);

        stack.Children.Add(paramsGrid);

        // Opzione Cestino
        var toggleRecycle = HubUi.CreateToggle(
            Loc.T("Sposta l'originale nel Cestino dopo la conversione", "Move original to Recycle Bin after conversion"),
            null,
            rule.DeleteSourceToRecycleBin,
            active => { SettingsStore.Current.Update(_ => rule.DeleteSourceToRecycleBin = active); });
        toggleRecycle.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(toggleRecycle);

        border.Child = stack;
        return border;
    }
}

