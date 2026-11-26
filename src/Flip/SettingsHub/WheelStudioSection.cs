using System.Windows;
using System.Windows.Controls;
using Flip.Core;

namespace Flip.SettingsHub;

/// <summary>
/// Costruisce l'interfaccia della Sezione 1: Ruota &amp; Spicchi (gradi di visibilità, pacchetti specialistici, spicchi pinnati, retro della ruota).
/// </summary>
public static class WheelStudioSection
{
    private sealed record GradeChoice(FormatGrade Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CategoryChoice(FormatCategory Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static string CategoryLabel(FormatCategory category) => category switch
    {
        FormatCategory.RasterAndRaw => Loc.T("Immagini & RAW", "Images & RAW"),
        FormatCategory.Vector => Loc.T("Vettoriali", "Vector"),
        FormatCategory.DocumentAndEmail => Loc.T("Documenti & Email", "Documents & Email"),
        FormatCategory.Spreadsheet => Loc.T("Fogli di calcolo", "Spreadsheets"),
        FormatCategory.Presentation => Loc.T("Presentazioni", "Presentations"),
        FormatCategory.Pdf => "PDF",
        FormatCategory.TextAndMarkup => Loc.T("Testo & Markup", "Text & Markup"),
        FormatCategory.EbookAndComic => Loc.T("Ebook & Fumetti", "Ebooks & Comics"),
        FormatCategory.Audio => "Audio",
        FormatCategory.Video => "Video",
        FormatCategory.Subtitle => Loc.T("Sottotitoli", "Subtitles"),
        FormatCategory.DataAndConfig => Loc.T("Dati & Config", "Data & Config"),
        FormatCategory.ArchiveAndDisk => Loc.T("Archivi & Dischi", "Archives & Disk Images"),
        FormatCategory.Font => Loc.T("Font", "Fonts"),
        FormatCategory.Cad => "CAD",
        FormatCategory.Model3D => Loc.T("Modelli 3D", "3D Models"),
        FormatCategory.Geospatial => Loc.T("Geospaziale", "Geospatial"),
        FormatCategory.Hwp => Loc.T("HWP & Regionali", "HWP & Regional"),
        _ => category.ToString()
    };

    public static UIElement Build()
    {
        var settings = SettingsStore.Current.Settings;
        var root = new StackPanel();

        // Titolo Sezione
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Ruota & Spicchi", "Wheel & Slices"),
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = HubUi.TextPrimary,
            Margin = new Thickness(0, 0, 0, 4)
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("Formati visibili, pacchetti e strumenti della ruota.", "Visible formats, specialist packs, and quick tools."),
            FontSize = 13,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 20)
        });

        // ── Card 1: Grado di Visibilità & Contatore ──────────────────────────
        var cardGrade = HubUi.CreateCard(
            Loc.T("Formati visibili", "Visible formats"),
            Loc.T("Quanti formati mostrare nella ruota.", "How many target formats appear on the wheel."));
        var stackGrade = (StackPanel)cardGrade.Child;

        var counterText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = HubUi.Accent,
            Margin = new Thickness(0, 8, 0, 10)
        };

        int totalRegisteredCount = 0;
        foreach (FormatCategory cat in Enum.GetValues<FormatCategory>())
        {
            totalRegisteredCount += FormatRegistry.GetByCategory(cat).Count;
        }

        void UpdateCounter(FormatGrade grade, SpecialistField fields)
        {
            int visibleCount = 0;
            foreach (FormatCategory cat in Enum.GetValues<FormatCategory>())
            {
                visibleCount += FormatRegistry.GetVisibleTargets(cat, grade, fields).Count();
            }
            counterText.Text = Loc.F("{0} di {1} formati visibili", "{0} of {1} formats visible", visibleCount, totalRegisteredCount);
        }

        GradeChoice[] gradeChoices =
        [
            new(FormatGrade.Everyday, Loc.T("Essenziali", "Essential")),
            new(FormatGrade.General, Loc.T("Estesi · consigliato", "Extended · recommended")),
            new(FormatGrade.Specialist, Loc.T("Studio & Pro", "Studio & Pro")),
            new(FormatGrade.Legacy, Loc.T("Tutti", "All"))
        ];
        GradeChoice selectedGrade = gradeChoices.FirstOrDefault(g => g.Value == settings.MaxVisibleGrade) ?? gradeChoices[1];

        var comboGrade = HubUi.CreateComboBox(
            gradeChoices,
            selectedGrade,
            selected =>
            {
                SettingsStore.Current.Update(s => s.MaxVisibleGrade = selected.Value);
                UpdateCounter(selected.Value, SettingsStore.Current.Settings.EnabledSpecialistFields);
            });

        stackGrade.Children.Add(comboGrade);
        stackGrade.Children.Add(counterText);
        UpdateCounter(settings.MaxVisibleGrade, settings.EnabledSpecialistFields);
        root.Children.Add(cardGrade);

        // ── Card 1b: Effetti Visivi ─────────────────────────────────────────
        var cardEffects = HubUi.CreateCard(
            Loc.T("Effetti visivi", "Visual effects"),
            Loc.T("Feedback luminoso durante l'uso.", "Ambient visual feedback while converting."));
        var stackEffects = (StackPanel)cardEffects.Child;
        stackEffects.Children.Add(HubUi.CreateToggle(
            Loc.T("Aura ai bordi dello schermo", "Screen edge aura"),
            Loc.T("Bagliore arancione durante ruota e conversioni. Non blocca i clic.", "Subtle orange edge glow while active. Never blocks clicks."),
            settings.EnableScreenEdgeAura,
            active => SettingsStore.Current.Update(s => s.EnableScreenEdgeAura = active)));
        root.Children.Add(cardEffects);

        // ── Card 2: Pacchetti Specialistici ─────────────────────────────────
        var cardSpecialist = HubUi.CreateCard(
            Loc.T("Pacchetti specialistici", "Specialist packs"),
            Loc.T("Formati avanzati per settori specifici.", "Unlock domain-specific formats when needed."));
        var stackSpecialist = (StackPanel)cardSpecialist.Child;

        var fieldsGrid = new Grid();
        fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftCol = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        var rightCol = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };

        (SpecialistField Field, string Name, string Desc)[] packages =
        [
            (SpecialistField.RawPhotography, Loc.T("Fotografia RAW", "RAW Photography"), "CR2, NEF, ARW, DNG, RAF, RW2, PEF"),
            (SpecialistField.ThreeDAndGameDev, "3D & GameDev", "OBJ, STL, GLB, GLTF, FBX, DAE, PLY, 3MF"),
            (SpecialistField.CadAndVector, Loc.T("CAD & Ingegneria", "CAD & Engineering"), "DXF, DWG, STEP, IGES, SVG, EPS"),
            (SpecialistField.DataAndDev, Loc.T("Dati & Config", "Data & Config"), "JSON, YAML, TOML, XML, CSV, TSV, INI, SQL"),
            (SpecialistField.EbookAndPublishing, Loc.T("Ebook & Editoria", "Ebooks & Publishing"), "EPUB, MOBI, AZW3, CBR, CBZ, FB2"),
            (SpecialistField.WebAndFonts, Loc.T("Web & Font", "Web & Fonts"), "WOFF, WOFF2, TTF, OTF, EOT, AVIF, WebP"),
            (SpecialistField.LegacyAndRegional, Loc.T("Legacy & Regionali", "Legacy & Regional"), Loc.T("HWP, WPD, DBF, PCX, TGA e formati storici", "HWP, WPD, DBF, PCX, TGA, and legacy formats"))
        ];

        for (int i = 0; i < packages.Length; i++)
        {
            var p = packages[i];
            bool isChecked = (settings.EnabledSpecialistFields & p.Field) != 0;

            var toggle = HubUi.CreateToggle(p.Name, p.Desc, isChecked, active =>
            {
                SettingsStore.Current.Update(s =>
                {
                    if (active) s.EnabledSpecialistFields |= p.Field;
                    else s.EnabledSpecialistFields &= ~p.Field;
                });
                UpdateCounter(SettingsStore.Current.Settings.MaxVisibleGrade, SettingsStore.Current.Settings.EnabledSpecialistFields);
            });

            if (i % 2 == 0) leftCol.Children.Add(toggle);
            else rightCol.Children.Add(toggle);
        }

        Grid.SetColumn(leftCol, 0);
        Grid.SetColumn(rightCol, 1);
        fieldsGrid.Children.Add(leftCol);
        fieldsGrid.Children.Add(rightCol);
        stackSpecialist.Children.Add(fieldsGrid);
        root.Children.Add(cardSpecialist);

        // ── Card 3: Pinned Formats Primo Anello ──────────────────────────────
        var cardPinned = HubUi.CreateCard(
            Loc.T("Spicchi preferiti", "Pinned slices"),
            Loc.T("Formati mostrati per primi, per categoria.", "Prioritized formats in the inner ring per category."));
        var stackPinned = (StackPanel)cardPinned.Child;

        var catComboLabel = new TextBlock
        {
            Text = Loc.T("Categoria", "Category"),
            FontSize = 12,
            Foreground = HubUi.TextSecondary,
            Margin = new Thickness(0, 0, 0, 4)
        };
        stackPinned.Children.Add(catComboLabel);

        var txtPinned = new TextBox
        {
            Background = HubUi.InputBg,
            Foreground = HubUi.TextPrimary,
            CaretBrush = HubUi.TextPrimary,
            BorderBrush = HubUi.CardBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0)
        };

        FormatCategory currentCategory = FormatCategory.RasterAndRaw;
        bool isPopulating = false;
        List<string> displayedFormats = [];

        static List<string> ParsePinnedFormats(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.TrimStart('.').ToLowerInvariant())
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct()
                .ToList();

        void LoadPinnedForCat(FormatCategory cat)
        {
            string catKey = cat.ToString();
            var stored = SettingsStore.Current.CreateSnapshot().PinnedWheelFormatsByCategory;
            IEnumerable<string> formats = stored.TryGetValue(catKey, out var list)
                ? list
                : FormatRegistry.GetByCategory(cat)
                    .Where(d => d.Grade == FormatGrade.Everyday)
                    .Select(d => d.Extension)
                    .Take(6);
            string text = string.Join(", ", formats);
            isPopulating = true;
            try
            {
                displayedFormats = ParsePinnedFormats(text);
                txtPinned.Text = text;
            }
            finally
            {
                isPopulating = false;
            }
        }

        CategoryChoice[] categoryChoices = Enum.GetValues<FormatCategory>()
            .Select(cat => new CategoryChoice(cat, CategoryLabel(cat)))
            .ToArray();
        CategoryChoice selectedCat = categoryChoices.FirstOrDefault(c => c.Value == currentCategory) ?? categoryChoices[0];

        var comboCat = HubUi.CreateComboBox(
            categoryChoices,
            selectedCat,
            choice =>
            {
                currentCategory = choice.Value;
                LoadPinnedForCat(choice.Value);
            });

        stackPinned.Children.Add(comboCat);

        var tipPinned = new TextBlock
        {
            Text = Loc.T("Estensioni separate da virgola · es. png, jpg, webp", "Comma-separated extensions · e.g. png, jpg, webp"),
            FontSize = 11,
            Foreground = HubUi.TextMuted,
            Margin = new Thickness(0, 10, 0, 2)
        };
        stackPinned.Children.Add(tipPinned);

        txtPinned.TextChanged += (_, _) =>
        {
            if (isPopulating) return;
            var formats = ParsePinnedFormats(txtPinned.Text);
            if (formats.SequenceEqual(displayedFormats)) return;

            SettingsStore.Current.Update(s => s.PinnedWheelFormatsByCategory[currentCategory.ToString()] = formats);
            displayedFormats = formats;
        };

        LoadPinnedForCat(currentCategory);
        stackPinned.Children.Add(txtPinned);
        root.Children.Add(cardPinned);

        // ── Card 4: Strumenti Retro della Ruota (Quick Tools) ───────────────
        var cardBack = HubUi.CreateCard(
            Loc.T("Strumenti", "Tools"),
            Loc.T("Strumenti sulla faccia Strumenti · Tab o rotellina.", "Quick tools on the Tools face · press Tab or scroll."));
        var stackBack = (StackPanel)cardBack.Child;

        (string Id, string Name, string Desc)[] backTools =
        [
            ("crop", Loc.T("Ritaglia", "Crop"), Loc.T("Manuale, 9:16, 4:5, 1:1, 16:9 e 9:16 sfocato", "Manual, 9:16, 4:5, 1:1, 16:9, and blurred 9:16")),
            ("rotate", Loc.T("Ruota e specchia", "Rotate & flip"), Loc.T("90°, 180° e ribaltamento", "90°, 180°, and mirror flips")),
            ("merge_pdf", Loc.T("Unisci in PDF", "Merge PDF"), Loc.T("Dimensioni originali; con A scegli A4/A3", "Original size; press A for A4/A3")),
            ("pack_zip", Loc.T("Comprimi in ZIP", "Pack ZIP"), Loc.T("Un archivio con i file selezionati", "Archive selected files into one ZIP")),
            ("pack_rar", "RAR", Loc.T("Richiede WinRAR installato", "Requires WinRAR to be installed")),
            ("target_size", Loc.T("Peso massimo", "Target size"), Loc.T("Comprime entro 8, 25 o 50 MB", "Compress under 8, 25, or 50 MB")),
            ("remove_bg", Loc.T("Rimuovi sfondo", "Remove background"), Loc.T("Scontorno con IA locale", "Local AI background removal")),
            ("ocr", Loc.T("Estrai testo", "Extract text"), Loc.T("OCR nativo di Windows", "Local OCR text extraction")),
            ("palette", Loc.T("Palette colori", "Color palette"), Loc.T("6 colori dominanti in una card PNG", "6 dominant colors in a PNG swatch card")),
            ("clean_meta", Loc.T("Rimuovi metadati", "Strip metadata"), Loc.T("EXIF, GPS e XMP da immagini e PDF", "Remove EXIF, GPS, and XMP from images and PDFs")),
            ("waveform", Loc.T("Forma d'onda", "Waveform"), Loc.T("PNG trasparente 1920×480 dell'audio", "Transparent 1920×480 audio waveform PNG")),
            ("turntable_3d", Loc.T("Turntable 3D", "3D Turntable"), Loc.T("GIF 360° del modello, 720×720", "360° spinning model GIF, 720×720"))
        ];

        foreach (var tool in backTools)
        {
            bool isEnabled = settings.EnabledBackSideTools.Contains(tool.Id, StringComparer.OrdinalIgnoreCase);
            var toggle = HubUi.CreateToggle(tool.Name, tool.Desc, isEnabled, active =>
            {
                SettingsStore.Current.Update(s =>
                {
                    if (active && !s.EnabledBackSideTools.Contains(tool.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        s.EnabledBackSideTools.Add(tool.Id);
                    }
                    else if (!active)
                    {
                        s.EnabledBackSideTools.RemoveAll(id => string.Equals(id, tool.Id, StringComparison.OrdinalIgnoreCase));
                    }
                });
            });
            stackBack.Children.Add(toggle);
        }

        root.Children.Add(cardBack);

        return root;
    }
}
