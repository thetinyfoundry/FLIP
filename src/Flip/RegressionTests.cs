using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Flip.Core;
using Flip.Engines;
using Flip.Routing;
using SkiaSharp;

namespace Flip;

/// <summary>Un controllo per ogni difetto corretto nella 1.0.0 e successive, così non può tornare senza farsi notare.</summary>
internal static class RegressionTests
{
    internal static void Check(Dictionary<string, bool> checks)
    {
        string root = Path.Combine(Evidence.DirectoryPath, "regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (name, test) in new (string, Func<string, bool>)[]
            {
                ("csv_semicolon_columns_kept", SemicolonCsv),
                ("csv_ansi_accents_kept", AnsiCsv),
                ("csv_duplicate_and_extra_columns_kept", DuplicateColumns),
                ("csv_codes_and_decimals_unchanged", CodesAndDecimals),
                ("csv_to_toml_and_ini_produce_rows", TableToConfig),
                ("xml_mixed_text_and_encoding_kept", XmlText),
                ("jsonl_bom_accepted_and_accents_readable", JsonLines),
                ("ini_root_keys_stay_outside_sections", IniOrder),
                ("xlsx_from_json_keeps_every_key", JsonToXlsx),
                ("xlsx_dates_are_dates", XlsxDates),
                ("html_nested_blocks_kept", HtmlBlocks),
                ("rtf_windows_1252_punctuation", RtfPunctuation),
                ("markdown_front_matter_and_hashtags", MarkdownBlocks),
                ("docx_headings_have_no_markdown_symbols", DocxHeadings),
                ("settings_unknown_value_keeps_the_rest", SettingsSalvage),
                ("settings_corrupt_file_is_backed_up", SettingsCorrupt),
                ("merge_pdf_keeps_text_and_page_size", MergePdf),
                ("clean_pdf_keeps_text_and_drops_author", CleanPdf),
                ("ico_keeps_aspect_ratio", IconRatio),
                ("semitransparent_pixels_not_darkened", Transparency),
                ("tar_global_pax_header_accepted", TarGlobalHeader),
                ("svg_preview_leaves_user_files_alone", PreviewIsolation),
                ("watch_two_rules_and_read_only_files", WatchRules),
                ("undo_restores_recycled_original", UndoRestore),
                ("pdf_to_image_keeps_page_shape", PdfPageShape),
                ("excel_all_sheets_to_pdf", ExcelToPdf),
                ("excel_macro_and_template_to_pdf_and_csv", ExcelVariants),
                ("ods_to_pdf_and_csv", OdsToPdf),
                ("xls_binary_to_pdf_and_csv", XlsToPdf),
                ("xls_saved_as_html_to_pdf", HtmlXls),
                ("xls_shared_strings_split_across_records", XlsSplitStrings),
                ("excel_number_formats", ExcelNumbers),
                ("excel_encrypted_and_empty_explained", ExcelErrors),
                ("excel_to_pdf_offered_without_other_programs", ExcelOffered),
                ("store_imports_settings_without_engine_paths", StoreImport),
                ("store_keeps_github_models", StoreModels),
                ("ffmpeg_checksum_formats_parsed", FfmpegChecksum),
                ("ffmpeg_offers_only_writable_audio", FfmpegFormats),
                ("settings_import_keeps_this_pc_paths", ImportKeepsLocalPaths),
                ("pdfa_without_metadata_drops_author_and_keeps_text", PdfaWithoutMetadata),
                ("network_paths_recognized_without_opening_them", NetworkPaths),
                ("browser_redirect_to_this_pc_blocked", BrowserRedirect),
                ("browser_local_addresses_rules", BrowserAddresses),
                ("browser_hostile_html_does_not_freeze", BrowserHostileHtml),
                ("programs_not_searched_in_working_folder", WorkingFolderPrograms),
                ("archive_bomb_stopped", ArchiveBomb),
                ("gltf_declared_size_not_trusted", GltfBomb),
                ("engine_download_requires_checksum", EngineChecksum),
                ("watch_ignores_links", WatchLinks)
            })
            {
                string work = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                try { checks["fix_" + name] = test(work); }
                catch (Exception ex)
                {
                    checks["fix_" + name] = false;
                    Evidence.Log("regression." + name, ex.ToString());
                }
            }
        }
        finally
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Evidence.Log("regression.cleanup", ex.Message); }
        }
    }

    private static string Convert(IConversionEngine engine, string input, string extension)
    {
        string output = Path.ChangeExtension(input, "out." + extension);
        var result = engine.ConvertAsync(new ConversionContext
        {
            InputFilePath = input,
            OutputFilePath = output,
            SourceExtension = Path.GetExtension(input).TrimStart('.'),
            TargetExtension = extension,
            Settings = new FlipSettings()
        }).GetAwaiter().GetResult();
        return result.Success ? output : throw new IOException(result.ErrorMessage);
    }

    private static string ConvertText(string work, string name, byte[] content, string extension)
    {
        string input = Path.Combine(work, name);
        File.WriteAllBytes(input, content);
        return File.ReadAllText(Convert(new DataSerializationEngine(), input, extension)).ReplaceLineEndings("\n");
    }

    private static string ConvertText(string work, string name, string content, string extension) =>
        ConvertText(work, name, new UTF8Encoding(false).GetBytes(content), extension);

    private static bool SemicolonCsv(string work)
    {
        using var json = JsonDocument.Parse(ConvertText(work, "a.csv", "nome;prezzo\nRossi;1,50\n", "json"));
        return json.RootElement[0].GetProperty("prezzo").GetString() == "1,50" &&
            TextFiles.DetectDelimiter("a,b;c\n1,2;3\n") == ',' && TextFiles.DetectDelimiter("solo\n1\n") == ',' &&
            TextFiles.ReadTable("sep=;\r\na;b\r\n1;2", "csv")[1].SequenceEqual(["1", "2"]);
    }

    private static bool AnsiCsv(string work)
    {
        string json = ConvertText(work, "a.csv", [.. "citt"u8, 0xE0, .. "\nForl"u8, 0xEC, .. "\n"u8], "json");
        return json.Contains("\"città\": \"Forlì\"");
    }

    private static bool DuplicateColumns(string work)
    {
        using var json = JsonDocument.Parse(ConvertText(work, "a.csv", "a,a,b\n1,2,3,4\n", "json"));
        return json.RootElement[0].EnumerateObject().Select(p => p.Name + p.Value.GetRawText()).SequenceEqual(["a1", "a_22", "b3", "col_44"]);
    }

    private static bool CodesAndDecimals(string work)
    {
        string source = "lungo,prezzo,codice,nullo,pollici\n12345678901234567890,1.50,12E5,null,5\"\n";
        using var json = JsonDocument.Parse(ConvertText(work, "a.csv", source, "json"));
        var row = json.RootElement[0];
        return row.GetProperty("lungo").ValueEquals("12345678901234567890") && row.GetProperty("prezzo").GetRawText() == "1.50" &&
            row.GetProperty("codice").ValueEquals("12E5") && row.GetProperty("nullo").ValueEquals("null") &&
            row.GetProperty("pollici").ValueEquals("5\"");
    }

    private static bool TableToConfig(string work) =>
        ConvertText(work, "a.csv", "a,b\n1,x\n", "toml").Contains("items = [{ a = 1, b = \"x\" }]") &&
        ConvertText(work, "b.csv", "a,b\n1,x\n", "ini").StartsWith("[1]\na = 1\nb = x");

    private static bool XmlText(string work)
    {
        string mixed = ConvertText(work, "a.xml", "<r xmlns=\"urn:x\">Ciao <b>tu</b> e noi</r>", "json");
        string latin = ConvertText(work, "b.xml", [.. "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><r>Forl"u8, 0xEC, .. "</r>"u8], "json");
        return mixed.Contains("Ciao e noi") && mixed.Contains("\"b\": \"tu\"") && latin.Contains("Forlì");
    }

    private static bool JsonLines(string work) =>
        ConvertText(work, "a.jsonl", [0xEF, 0xBB, 0xBF, .. "{\"a\":\"perché\"}\n"u8], "json").Contains("perché");

    private static bool IniOrder(string work) =>
        ConvertText(work, "a.json", "{\"s\":{\"x\":1},\"fuori\":2}", "ini").StartsWith("fuori = 2\n[s]\nx = 1");

    private static bool JsonToXlsx(string work)
    {
        string source = Path.Combine(work, "a.json");
        File.WriteAllText(source, "[{\"a\":1},{\"a\":2,\"nuova\":\"si\"}]");
        var office = new OfficeOpenXmlEngine();
        string csv = File.ReadAllText(Convert(office, Convert(office, source, "xlsx"), "csv")).ReplaceLineEndings("\n");
        return csv == "a,nuova\n1,\n2,si\n";
    }

    private static bool XlsxDates(string work)
    {
        string path = Path.Combine(work, "d.xlsx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
            Add("xl/workbook.xml", $"<workbook xmlns=\"{main}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"A\" sheetId=\"2\" r:id=\"rId9\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId9\" Type=\"t\" Target=\"worksheets/sheet7.xml\"/></Relationships>");
            Add("xl/styles.xml", $"<styleSheet xmlns=\"{main}\"><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>");
            Add("xl/worksheets/sheet7.xml", $"<worksheet xmlns=\"{main}\"><sheetData><row r=\"1\"><c r=\"A1\" s=\"1\"><v>45292</v></c><c r=\"B1\"><v>45292</v></c><c r=\"C1\" t=\"b\"><v>1</v></c><c r=\"D1\"><v>0.10000000000000001</v></c></row></sheetData></worksheet>");
        }
        return File.ReadAllText(Convert(new OfficeOpenXmlEngine(), path, "csv")).Trim() == "2024-01-01,45292,TRUE,0.1" &&
            OfficeOpenXmlEngine.IsDateFormat("dd/mm/yyyy") && !OfficeOpenXmlEngine.IsDateFormat("#,##0.00\" mesi\"") && !OfficeOpenXmlEngine.IsDateFormat("[Red]0.00");
    }

    private static bool HtmlBlocks(string work)
    {
        string source = Path.Combine(work, "a.html");
        File.WriteAllText(source, "<body>Inizio<div><div>Uno</div>Due</div><h2>Tre</h2><table><tr><td>x</td><td>y</td></tr></table>Fine</body>");
        string text = File.ReadAllText(Convert(new OfficeOpenXmlEngine(), source, "txt"));
        return new[] { "Inizio", "Uno", "Due", "Tre", "x\ty", "Fine" }.All(text.Contains);
    }

    private static bool RtfPunctuation(string work)
    {
        string source = Path.Combine(work, "a.rtf");
        File.WriteAllText(source, @"{\rtf1\ansi\ansicpg1252 l\'92uomo \'80 5}", Encoding.ASCII);
        return File.ReadAllText(Convert(new OfficeOpenXmlEngine(), source, "txt")).Contains("l’uomo € 5");
    }

    private static bool MarkdownBlocks(string work)
    {
        string source = Path.Combine(work, "a.md");
        File.WriteAllText(source, "---\ntitle: Segreto\n---\n# Titolo\n\n#hashtag\n");
        string text = File.ReadAllText(Convert(new OfficeOpenXmlEngine(), source, "html"));
        return !text.Contains("Segreto") && text.Contains("<h2>Titolo</h2>") && text.Contains("<p>#hashtag</p>") && text.Contains("<title>a</title>");
    }

    private static bool DocxHeadings(string work)
    {
        string source = Path.Combine(work, "a.md");
        File.WriteAllText(source, "## Capitolo\n\ntesto\u0001 ok\n");
        using var zip = ZipFile.OpenRead(Convert(new OfficeOpenXmlEngine(), source, "docx"));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        string xml = reader.ReadToEnd();
        return xml.Contains(">Capitolo<") && !xml.Contains("##") && xml.Contains("testo ok");
    }

    private static bool SettingsSalvage(string work)
    {
        string path = Path.Combine(work, "settings.json");
        File.WriteAllText(path, "{\"TriggerKey\":\"F8\",\"ConflictPolicy\":\"ValoreFuturo\",\"WatchFolders\":[{\"FolderPath\":\"x\"}]}");
        var store = new SettingsStore(path, Path.Combine(work, "legacy.json"));
        bool kept = store.Settings.TriggerKey == "F8" && store.Settings.WatchFolders.Count == 1 && store.Settings.ConflictPolicy == ConflictPolicy.AutoNumber;
        store.Update(settings => settings.SoundEnabled = false);
        return kept && Directory.GetFiles(work, "settings.json.backup-*").Any(file => File.ReadAllText(file).Contains("ValoreFuturo"));
    }

    private static bool SettingsCorrupt(string work)
    {
        string path = Path.Combine(work, "settings.json");
        File.WriteAllText(path, "{\"TriggerKey\":\"F8\"");
        var store = new SettingsStore(path, Path.Combine(work, "legacy.json"));
        store.Update(settings => settings.SoundEnabled = false);
        return Directory.GetFiles(work, "settings.json.backup-*").Any(file => File.ReadAllText(file) == "{\"TriggerKey\":\"F8\"");
    }

    private static string SamplePdf(string work, string name, string text, float width, float height)
    {
        string path = Path.Combine(work, name);
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Author = "Persona Privata", Title = "Titolo Privato" });
        using var font = new SKFont(SKTypeface.Default, 20);
        using var paint = new SKPaint { Color = SKColors.Black };
        document.BeginPage(width, height).DrawText(text, 20, 60, SKTextAlign.Left, font, paint);
        document.EndPage();
        document.Close();
        return path;
    }

    private static string RunTool(IQuickTool tool, string work, params string[] inputs)
    {
        var settings = new FlipSettings { OutputDirectoryMode = OutputDirectoryMode.CustomFolder, CustomOutputDirectory = work };
        var result = tool.ExecuteAsync(new QuickToolContext { InputFilePaths = inputs, Settings = settings }).GetAwaiter().GetResult();
        return result.Success ? result.OutputFilePaths[0] : throw new IOException(result.ErrorMessage);
    }

    private static bool MergePdf(string work)
    {
        string note = Path.Combine(work, "nota.txt");
        File.WriteAllText(note, string.Join(' ', Enumerable.Repeat("parola", 60)) + " ultima");
        string merged = RunTool(new MergePdfQuickTool(), work,
            SamplePdf(work, "a.pdf", "AlfaBeta", 300, 400), SamplePdf(work, "b.pdf", "GammaDelta", 842, 595), note);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(merged);
        return pdf.NumberOfPages == 3 && pdf.GetPage(1).Text.Contains("AlfaBeta") && pdf.GetPage(2).Text.Contains("GammaDelta") &&
            pdf.GetPage(1).Width == 300 && pdf.GetPage(2).Width == 842 && pdf.GetPage(3).Text.Contains("ultima") &&
            pdf.GetPage(3).Letters.All(letter => letter.Location.X < 570);
    }

    private static bool CleanPdf(string work)
    {
        string cleaned = RunTool(new PrivacyCleanQuickTool(), work, SamplePdf(work, "a.pdf", "AlfaBeta", 300, 400));
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(cleaned));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(cleaned);
        return pdf.GetPage(1).Text.Contains("AlfaBeta") && pdf.GetPage(1).Width == 300 &&
            string.IsNullOrWhiteSpace(pdf.Information.Author) && string.IsNullOrWhiteSpace(pdf.Information.Title) &&
            !raw.Contains("Privat") && !raw.Contains("D:20");
    }

    private static string SampleImage(string work, string name, int width, int height, SKColor color)
    {
        string path = Path.Combine(work, name);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static bool IconRatio(string work)
    {
        byte[] icon = File.ReadAllBytes(Convert(new ImageEngine(), SampleImage(work, "a.png", 64, 32, SKColors.Red), "ico"));
        int offset = BitConverter.ToInt32(icon, 6 + 12), length = BitConverter.ToInt32(icon, 6 + 8);
        using var frame = SKBitmap.Decode(icon.AsSpan(offset, length).ToArray());
        return frame.GetPixel(frame.Width / 2, 1).Alpha == 0 && frame.GetPixel(frame.Width / 2, frame.Height / 2).Red > 200;
    }

    private static bool Transparency(string work)
    {
        string source = SampleImage(work, "a.png", 16, 16, new SKColor(200, 100, 50, 128));
        using var tiff = ImageEngine.LoadBitmap(Convert(new ImageEngine(), source, "tiff"));
        using var bmp = ImageEngine.LoadBitmap(Convert(new ImageEngine(), source, "bmp"));
        SKColor pixel = tiff.GetPixel(8, 8), flat = bmp.GetPixel(8, 8);
        return Math.Abs(pixel.Red - 200) <= 3 && Math.Abs(pixel.Alpha - 128) <= 2 && Math.Abs(flat.Red - 227) <= 4;
    }

    private static bool TarGlobalHeader(string work)
    {
        string path = Path.Combine(work, "a.tar.gz");
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "0123" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "a.txt") { DataStream = new MemoryStream("ciao"u8.ToArray()) });
        }
        string output = Path.Combine(work, "a.zip");
        var result = new ArchiveEngine().ConvertAsync(new ConversionContext
        {
            InputFilePath = path, OutputFilePath = output, SourceExtension = "gz", TargetExtension = "zip", Settings = new FlipSettings()
        }).GetAwaiter().GetResult();
        if (!result.Success) throw new IOException(result.ErrorMessage);
        using var zip = ZipFile.OpenRead(output);
        return zip.Entries.Single().FullName == "a.txt";
    }

    private static bool PreviewIsolation(string work)
    {
        string svg = Path.Combine(work, "logo.svg"), png = Path.Combine(work, "logo.png");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\"><rect width=\"20\" height=\"20\" fill=\"red\"/></svg>");
        File.WriteAllText(png, "file dell'utente");
        var settings = new FlipSettings { ConflictPolicy = ConflictPolicy.Overwrite, ReplaceOriginalToRecycleBin = true };
        var tool = FlipPreviewService.ResolveQuickLookTool(svg, settings) ?? throw new IOException("Anteprima non disponibile.");
        var session = FlipPreviewService.PrepareAsync(tool, new QuickToolContext { InputFilePaths = [svg], Settings = settings }, CancellationToken.None).GetAwaiter().GetResult();
        session.DeleteTemporaryFiles();
        return File.Exists(svg) && File.ReadAllText(png) == "file dell'utente" && Directory.GetFiles(work).Length == 2;
    }

    private static bool WatchRules(string work)
    {
        string folder = Directory.CreateDirectory(Path.Combine(work, "in")).FullName, output = Directory.CreateDirectory(Path.Combine(work, "out")).FullName;
        var settings = new FlipSettings { OutputDirectoryMode = OutputDirectoryMode.CustomFolder, CustomOutputDirectory = output };
        settings.WatchFolders.Add(new WatchFolderRule { Enabled = true, FolderPath = folder, TargetExtension = "jpg" });
        settings.WatchFolders.Add(new WatchFolderRule { Enabled = true, FolderPath = folder, TargetExtension = "webp" });
        using var service = new WatchFolderService(initialSettings: settings);
        Thread.Sleep(100);
        string prepared = SampleImage(work, "p.png", 8, 8, SKColors.Blue);
        File.SetAttributes(prepared, FileAttributes.ReadOnly);
        File.Move(prepared, Path.Combine(folder, "p.png"));
        return SpinWait.SpinUntil(() => File.Exists(Path.Combine(output, "p.jpg")) && File.Exists(Path.Combine(output, "p.webp")), 10000);
    }

    private static bool UndoRestore(string work)
    {
        string source = SampleImage(work, "originale.png", 8, 8, SKColors.Green);
        byte[] before = File.ReadAllBytes(source);
        var settings = new FlipSettings { ReplaceOriginalToRecycleBin = true, OutputDirectoryMode = OutputDirectoryMode.CustomFolder, CustomOutputDirectory = work };
        var result = RoutePlanner.ExecuteBatchAsync([source], ["jpg"], settings).GetAwaiter().GetResult().Single().Result;
        if (!result.Success) throw new IOException(result.ErrorMessage);
        // Senza Cestino (sessione non interattiva o disco di rete) l'originale resta al suo posto.
        if (File.Exists(source)) return !string.IsNullOrEmpty(result.ErrorMessage);
        var (restored, missing) = OutputPathAllocator.RestoreOriginals(result.OutputFilePaths);
        return restored == 1 && missing.Length == 0 && File.ReadAllBytes(source).SequenceEqual(before);
    }

    private static bool PdfPageShape(string work)
    {
        string pdf = SamplePdf(work, "largo.pdf", "x", 842, 421);
        using var image = SKBitmap.Decode(Convert(new PdfEngine(), pdf, "png"));
        return Math.Abs((double)image.Width / image.Height - 2) < 0.01 && image.Width is > 2000 and < 3000;
    }

    private const string SheetMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static string SampleWorkbook(string work, string name, string spreadsheetNamespace = SheetMain)
    {
        string path = Path.Combine(work, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string text) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write(text); }
        Add("xl/workbook.xml", $"<workbook xmlns=\"{spreadsheetNamespace}\" xmlns:r=\"urn:r\"><sheets><sheet name=\"Primo\" r:id=\"a\"/><sheet name=\"Segreto\" state=\"hidden\" r:id=\"b\"/><sheet name=\"Terzo\" r:id=\"c\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"a\" Target=\"worksheets/x1.xml\"/><Relationship Id=\"b\" Target=\"worksheets/x2.xml\"/><Relationship Id=\"c\" Target=\"/xl/worksheets/x3.xml\"/></Relationships>");
        Add("xl/styles.xml", $"<styleSheet xmlns=\"{spreadsheetNamespace}\"><numFmts><numFmt numFmtId=\"170\" formatCode=\"#,##0.00\\ &quot;EUR&quot;\"/></numFmts><fonts><font/><font><b/></font></fonts><fills><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFE0CC\"/></patternFill></fill></fills><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"170\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>");
        string Sheet(string rows) => $"<worksheet xmlns=\"{spreadsheetNamespace}\"><sheetData>{rows}</sheetData><mergeCells><mergeCell ref=\"A1:C1\"/></mergeCells></worksheet>";
        string Text(string cell, string value) => $"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{value}</t></is></c>";
        Add("xl/worksheets/x1.xml", Sheet($"<row r=\"1\">{Text("A1", "Titolo unito")}</row><row r=\"2\">{Text("A2", "Nome")}{Text("B2", "Importo")}{Text("C2", "Data")}</row>" +
            $"<row r=\"3\">{Text("A3", "Perché Città")}<c r=\"B3\" s=\"1\"><v>1234.5</v></c><c r=\"C3\" s=\"2\"><v>45292</v></c></row>" +
            string.Concat(Enumerable.Range(4, 120).Select(i => $"<row r=\"{i}\">{Text("A" + i, "Riga " + i)}</row>"))));
        Add("xl/worksheets/x2.xml", Sheet($"<row>{Text("A1", "NASCOSTO")}</row>"));
        Add("xl/worksheets/x3.xml", Sheet($"<row>{Text("A1", "UltimoFoglio")}</row>"));
        return path;
    }

    private static string PdfText(string pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return string.Concat(document.GetPages().Select(page => page.Text)).Replace(" ", "");
    }

    private static bool ExcelToPdf(string work)
    {
        string text = PdfText(Convert(new PdfEngine(), SampleWorkbook(work, "a.xlsx"), "pdf"));
        return text.Contains("Titolounito") && text.Contains("PerchéCittà") && text.Contains("Riga123") && text.Contains("UltimoFoglio") && text.Contains("EUR") &&
            text.Contains(new DateTime(2024, 1, 1).ToString("d", System.Globalization.CultureInfo.CurrentCulture)) && !text.Contains("NASCOSTO");
    }

    private static bool ExcelVariants(string work)
    {
        foreach (string extension in new[] { "xlsm", "xltx" })
        {
            string source = SampleWorkbook(work, "a." + extension);
            if (!PdfText(Convert(new PdfEngine(), source, "pdf")).Contains("UltimoFoglio")) return false;
            if (!File.ReadAllText(Convert(new OfficeOpenXmlEngine(), source, "csv")).Contains("Perché Città,1234.5,2024-01-01")) return false;
        }
        // Excel può salvare anche nella variante Strict, con altri spazi dei nomi.
        string strict = SampleWorkbook(work, "strict.xlsx", "http://purl.oclc.org/ooxml/spreadsheetml/main");
        return PdfText(Convert(new PdfEngine(), strict, "pdf")).Contains("UltimoFoglio") &&
            File.ReadAllText(Convert(new OfficeOpenXmlEngine(), strict, "csv")).Contains("Perché Città,1234.5,2024-01-01");
    }

    private static bool OdsToPdf(string work)
    {
        string path = Path.Combine(work, "a.ods");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open()))
            writer.Write("<o:document-content xmlns:o=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:t=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:x=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><o:body><o:spreadsheet><t:table t:name=\"Uno\"><t:table-row><t:table-cell o:value-type=\"string\"><x:p>Città</x:p></t:table-cell><t:table-cell t:number-columns-repeated=\"3\"/><t:table-cell o:value-type=\"currency\" o:value=\"1250.5\"><x:p>1.250,50 €</x:p></t:table-cell></t:table-row><t:table-row t:number-rows-repeated=\"1048000\"><t:table-cell t:number-columns-repeated=\"16384\"/></t:table-row></t:table></o:spreadsheet></o:body></o:document-content>");
        return PdfText(Convert(new PdfEngine(), path, "pdf")).Contains("1.250,50€") &&
            File.ReadAllText(Convert(new OfficeOpenXmlEngine(), path, "csv")).Trim() == "Città,,,,1250.5";
    }

    private static byte[] Biff(int type, params byte[][] parts)
    {
        byte[] body = parts.SelectMany(part => part).ToArray();
        return [.. BitConverter.GetBytes((ushort)type), .. BitConverter.GetBytes((ushort)body.Length), .. body];
    }

    private static byte[] U16(int value) => BitConverter.GetBytes((ushort)value);

    /// <summary>Cartella XLS minima: stringhe condivise (anche spezzate fra due record), numeri, RK, formula e data.</summary>
    private static byte[] SampleXls(bool splitString)
    {
        byte[] first = Encoding.Unicode.GetBytes("Città"), second = "Gamma"u8.ToArray();
        byte[] table = splitString
            ? [.. Biff(0xFC, BitConverter.GetBytes(2), BitConverter.GetBytes(2), U16(5), [1], first[..4]), .. Biff(0x3C, [1], first[4..], U16(5), [0], second)]
            : Biff(0xFC, BitConverter.GetBytes(2), BitConverter.GetBytes(2), U16(5), [1], first, U16(5), [0], second);
        byte[] Globals(int offset) =>
        [
            .. Biff(0x809, U16(0x600), U16(5), new byte[12]), .. Biff(0x31, new byte[6], U16(400), new byte[8]),
            .. Biff(0xE0, U16(0), U16(0), new byte[16]), .. Biff(0xE0, U16(0), U16(14), new byte[16]),
            .. Biff(0x85, BitConverter.GetBytes(offset), [0, 0, 3, 0], "Uno"u8.ToArray()), .. table, .. Biff(0x0A)
        ];
        byte[] sheet =
        [
            .. Biff(0x809, U16(0x600), U16(0x10), new byte[12]),
            .. Biff(0xFD, U16(0), U16(0), U16(0), BitConverter.GetBytes(0)), .. Biff(0xFD, U16(0), U16(1), U16(0), BitConverter.GetBytes(1)),
            .. Biff(0x203, U16(1), U16(0), U16(0), BitConverter.GetBytes(12.5)), .. Biff(0x27E, U16(1), U16(1), U16(0), BitConverter.GetBytes((4200 << 2) | 3)),
            .. Biff(0x203, U16(1), U16(2), U16(1), BitConverter.GetBytes(45292.0)),
            .. Biff(0x06, U16(2), U16(0), U16(0), [0, 0, 0, 0, 0, 0, 0xFF, 0xFF], new byte[8]), .. Biff(0x207, U16(4), [0], "Esit"u8.ToArray()),
            .. Biff(0xBD, U16(2), U16(1), U16(0), BitConverter.GetBytes((7 << 2) | 2), U16(0), BitConverter.GetBytes((8 << 2) | 2), U16(2)),
            .. Biff(0x0A)
        ];
        byte[] globals = Globals(Globals(0).Length);
        // Oltre 4096 byte il flusso usa i settori normali: il resto è riempimento.
        byte[] stream = new byte[4608];
        globals.CopyTo(stream, 0);
        sheet.CopyTo(stream, globals.Length);

        byte[] file = new byte[512 * 12];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(file, 0);
        void Put(int at, uint value) => BitConverter.GetBytes(value).CopyTo(file, at);
        U16(9).CopyTo(file, 30); U16(6).CopyTo(file, 32);
        Put(44, 1); Put(48, 1); Put(56, 4096); Put(60, 0xFFFFFFFE); Put(68, 0xFFFFFFFE); Put(76, 0);
        for (int i = 1; i < 109; i++) Put(76 + i * 4, 0xFFFFFFFF);
        for (int i = 0; i < 128; i++) Put(512 + i * 4, 0xFFFFFFFF);
        Put(512, 0xFFFFFFFD); Put(516, 0xFFFFFFFE);
        for (int i = 2; i < 11; i++) Put(512 + i * 4, i == 10 ? 0xFFFFFFFE : (uint)(i + 1));
        void Entry(int index, string name, byte type, uint start, uint size)
        {
            int at = 1024 + index * 128;
            Encoding.Unicode.GetBytes(name).CopyTo(file, at);
            U16((name.Length + 1) * 2).CopyTo(file, at + 64);
            file[at + 66] = type;
            Put(at + 116, start); Put(at + 120, size);
        }
        Entry(0, "Root Entry", 5, 0xFFFFFFFE, 0);
        Entry(1, "Workbook", 2, 2, (uint)stream.Length);
        stream.CopyTo(file, 1536);
        return file;
    }

    private static bool XlsToPdf(string work)
    {
        string path = Path.Combine(work, "a.xls");
        File.WriteAllBytes(path, SampleXls(splitString: false));
        string pdf = PdfText(Convert(new PdfEngine(), path, "pdf"));
        string csv = File.ReadAllText(Convert(new OfficeOpenXmlEngine(), path, "csv")).ReplaceLineEndings("\n");
        return pdf.Contains("Città") && pdf.Contains("Gamma") && pdf.Contains("Esit") && csv == "Città,Gamma\n12.5,42,2024-01-01\nEsit,7,8\n";
    }

    private static bool XlsSplitStrings(string work)
    {
        string path = Path.Combine(work, "a.xls");
        File.WriteAllBytes(path, SampleXls(splitString: true));
        return File.ReadAllText(Convert(new OfficeOpenXmlEngine(), path, "csv")).StartsWith("Città,Gamma");
    }

    private static bool HtmlXls(string work)
    {
        string path = Path.Combine(work, "gestionale.xls");
        File.WriteAllText(path, "<html><table><tr><th>Cliente</th><th>Totale</th></tr><tr><td>Rossi &amp; C.</td><td>1.250,50</td></tr></table></html>");
        string text = PdfText(Convert(new PdfEngine(), path, "pdf"));
        return text.Contains("Cliente") && text.Contains("Rossi&C.") && text.Contains("1.250,50");
    }

    private static bool ExcelNumbers(string work)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
        try
        {
            string Show(double value, string code) => ExcelFormat.Display(value, 200, code, false);
            return Show(1234.5, "#,##0.00\\ \"€\"") == "1.234,50 €" && Show(-5, "0.00;[Red](0.00)") == "(5,00)" && Show(0.125, "0.0%") == "12,5%" &&
                Show(1234.5, "[$€-410]\\ #,##0.00") == "€ 1.234,50" && Show(45292.75, "dd/mm/yyyy hh:mm") == "01/01/2024 18:00" && Show(1.5, "[h]:mm") == "36:00" &&
                Show(7, "00000") == "00007" && Show(1234567, "#,##0,") == "1.235" && Show(0.1 + 0.2, "General") == "0,3" && Show(45292, "d mmmm yyyy") == "1 gennaio 2024" &&
                Show(-0.001, "0.00") == "0,00" && Show(3, "0 \"pz\"") == "3 pz";
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    private static bool ExcelErrors(string work)
    {
        static string? Fails(string path)
        {
            try { Convert(new PdfEngine(), path, "pdf"); return null; }
            catch (IOException ex) { return ex.Message; }
        }
        byte[] locked = SampleXls(splitString: false);
        Encoding.Unicode.GetBytes("EncryptedPackage").CopyTo(locked, 1024 + 128);
        U16(34).CopyTo(locked, 1024 + 128 + 64);
        string encrypted = Path.Combine(work, "protetto.xlsx"), damaged = Path.Combine(work, "rotto.xls"), empty = Path.Combine(work, "vuoto.csv");
        File.WriteAllBytes(encrypted, locked);
        File.WriteAllBytes(damaged, SampleXls(splitString: false)[..1400]);
        File.WriteAllText(empty, " \n ");
        return Fails(encrypted)?.Contains("password") == true && Fails(damaged) != null && Fails(empty) != null;
    }

    private static bool ExcelOffered(string work)
    {
        var builtIn = new PdfEngine().SupportedEdges;
        return new[] { "xlsx", "xlsm", "xltx", "xls", "ods", "csv", "tsv" }.All(source =>
            builtIn.Any(edge => edge.SourceExtension == source && edge.TargetExtension == "pdf") &&
            RoutePlanner.GetReachableTargetFormats(source, new FlipSettings()) is var targets &&
            WheelLayout.Partition(targets.Select(format => format.Extension.ToUpperInvariant()).ToArray()).Take(2).Any(slot => slot.Format == "PDF"));
    }

    private static bool StoreImport(string work)
    {
        string source = Path.Combine(work, "github.json"), target = Path.Combine(work, "store", "settings.json");
        File.WriteAllText(source, "{\"TriggerKey\":\"F8\",\"CustomEnginePaths\":{\"ffmpeg\":\"x\",\"whisper\":\"y\",\"calibre\":\"z\"}}");
        StoreEdition.ImportGitHubSettings(target, source);
        string imported = File.ReadAllText(target);
        File.WriteAllText(source, "{}");
        StoreEdition.ImportGitHubSettings(target, source);
        return imported.Contains("F8") && imported.Contains("calibre") && !imported.Contains("ffmpeg") && !imported.Contains("whisper") &&
            File.ReadAllText(target) == imported;
    }

    private static bool StoreModels(string work)
    {
        static string Model(string folder, string name, long bytes)
        {
            string path = Path.Combine(Directory.CreateDirectory(folder).FullName, name);
            using var file = File.Create(path);
            file.Write([0x6C, 0x6D, 0x67, 0x67]);
            file.SetLength(bytes);
            return path;
        }
        string engines = Path.Combine(work, "Engines"), custom = Path.Combine(work, "whisper-cuda");
        string speech = Model(Path.Combine(engines, "Whisper"), "ggml-prova-github.bin", 31L << 20);
        string background = Model(Path.Combine(engines, "RemoveBg"), OnnxRemoveBgEngine.ModelFileName, OnnxRemoveBgEngine.MinimumDownloadedModelBytes);
        string beside = Model(custom, "ggml-base.bin", 31L << 20);
        string source = Path.Combine(work, "github.json"), target = Path.Combine(work, "store", "settings.json");
        File.WriteAllText(source, JsonSerializer.Serialize(new
        {
            CustomEnginePaths = new Dictionary<string, string> { ["whisper"] = Path.Combine(custom, "whisper-cli.exe") }
        }));
        StoreEdition.ImportGitHubSettings(target, source);
        using var imported = JsonDocument.Parse(File.ReadAllText(target));
        var paths = imported.RootElement.GetProperty("CustomEnginePaths");
        string? previousFolder = OnnxRemoveBgEngine.TestOverrideInstallDirectory;
        OnnxRemoveBgEngine.TestOverrideInstallDirectory = Path.Combine(work, "vuota");
        StoreEdition.TestGitHubEngines = engines;
        WhisperLocator.InvalidateCache();
        try
        {
            return paths.GetProperty("whisper_model").GetString() == beside && !paths.TryGetProperty("whisper", out _) &&
                LocalAiEngine.FindModel(null, Path.GetFileName(speech)) == speech &&
                OnnxRemoveBgEngine.ResolveModelPath(new FlipSettings()) == background;
        }
        finally
        {
            StoreEdition.TestGitHubEngines = null;
            OnnxRemoveBgEngine.TestOverrideInstallDirectory = previousFolder;
            WhisperLocator.InvalidateCache();
        }
    }

    private static bool ImportKeepsLocalPaths(string work)
    {
        var store = new SettingsStore(Path.Combine(work, "settings.json"), Path.Combine(work, "legacy.json"));
        string mine = Path.Combine(work, "mio"), watched = Directory.CreateDirectory(Path.Combine(work, "watch")).FullName;
        var rule = new WatchFolderRule { FolderPath = watched, TargetExtension = "jpg" };
        store.Update(s =>
        {
            s.CustomEnginePaths["ffmpeg"] = mine;
            s.CustomOutputDirectory = work;
            s.WatchFolders.Add(rule.Copy());
        });
        string shared = Path.Combine(work, "shared.json");
        File.WriteAllText(shared, JsonSerializer.Serialize(new
        {
            TriggerKey = "F8",
            OutputDirectoryMode = 1,
            CustomOutputDirectory = @"\\server\pub",
            CustomEnginePaths = new Dictionary<string, string> { ["ffmpeg"] = "altro.exe", ["pandoc"] = "altro.exe" },
            WatchFolders = new[] { rule, new WatchFolderRule { FolderPath = work, TargetExtension = "mp3" }, new WatchFolderRule { FolderPath = "//server/pub", TargetExtension = "mp3" } }
        }));
        int disabled = store.ImportFromFile(shared);
        var saved = store.CreateSnapshot();
        return disabled == 1 && saved.TriggerKey == "F8" && saved.CustomEnginePaths.Count == 1 && saved.CustomEnginePaths["FFMPEG"] == mine &&
            saved.CustomOutputDirectory == work && saved.OutputDirectoryMode == OutputDirectoryMode.SameAsSource &&
            saved.WatchFolders.Count == 2 && saved.WatchFolders[0].Enabled && !saved.WatchFolders[1].Enabled;
    }

    private static bool PdfaWithoutMetadata(string work)
    {
        string source = SamplePdf(work, "a.pdf", "AlfaBeta", 300, 400), output = Path.Combine(work, "a.pdfa.pdf");
        var context = new ConversionContext
        {
            InputFilePath = source, OutputFilePath = output, SourceExtension = "pdf", TargetExtension = "pdfa", Settings = new FlipSettings()
        };
        context.CustomParameters["strip_metadata"] = "true";
        var result = new PdfEngine().ConvertAsync(context).GetAwaiter().GetResult();
        if (!result.Success) throw new IOException(result.ErrorMessage);
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(output));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output);
        return Encoding.Latin1.GetString(File.ReadAllBytes(source)).Contains("Privat") && !raw.Contains("Privat") &&
            raw.Contains("pdfaid") && pdf.GetPage(1).Text.Contains("AlfaBeta") && pdf.GetPage(1).Width == 300 && Directory.GetFiles(work).Length == 2;
    }

    private static bool NetworkPaths(string work) =>
        new[] { @"\\server\a.png", "//server/a.png", @"\\?\UNC\server\a.png", @"/\server\a.png" }.All(Policy.IsNetworkPath) &&
        !new[] { work, @"\\?\C:\a.png", "a.png", "", null }.Any(Policy.IsNetworkPath) &&
        Policy.ValidateFilePaths(["//127.0.0.1/x/a.png"]).Length == 0;

    private static bool BrowserRedirect(string work)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        int requests = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    byte[] buffer = new byte[4096];
                    int read = await stream.ReadAsync(buffer);
                    string path = Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1];
                    Interlocked.Increment(ref requests);
                    string answer = path == "/redirect"
                        ? "HTTP/1.1 302 Found\r\nLocation: /image.png\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: 4\r\nConnection: close\r\n\r\nciao";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(answer));
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or IOException or IndexOutOfRangeException) { }
        });
        BrowserMaterializedItem Fetch(string? page) => BrowserDropService.MaterializeAsync(
            new BrowserDropCandidate($"http://127.0.0.1:{port}/redirect", "png", "image.png", false, page), work).GetAwaiter().GetResult();
        // Da una pagina locale (o senza pagina) funziona, reindirizzamento compreso; da un sito pubblico non parte nemmeno la richiesta.
        bool local = Fetch("127.0.0.1").Success && Fetch(null).Success;
        int before = requests;
        return local && before == 4 && !Fetch("example.com").Success && requests == before;
    }

    private static bool BrowserAddresses(string work)
    {
        static bool Allowed(string ip, string target, string start, string? page) => BrowserDropService.IsAllowedAddress(
            System.Net.IPAddress.Parse(ip), new Uri(target), new BrowserDropCandidate(start, "png", "a.png", false, page));
        return Allowed("93.184.216.34", "https://example.com/a", "https://example.com/a", "example.com") &&
            !Allowed("127.0.0.1", "http://127.0.0.1/", "https://example.com/a", "example.com") &&
            !Allowed("127.0.0.1", "http://127.0.0.1/", "https://example.com/a", null) &&
            !Allowed("192.168.1.1", "http://192.168.1.1/", "https://example.com/a", null) &&
            !Allowed("127.0.0.1", "https://example.com/a", "https://example.com/a", "example.com") &&
            !Allowed("169.254.169.254", "https://example.com/a", "https://example.com/a", "example.com") &&
            !Allowed("::ffff:127.0.0.1", "https://example.com/a", "https://example.com/a", null) &&
            !Allowed("::1", "http://[::1]/", "https://example.com/a", "example.com") &&
            Allowed("127.0.0.1", "http://localhost:8188/a", "http://localhost:8188/a", "localhost") &&
            Allowed("192.168.1.10", "http://nas.local/a", "http://nas.local/a", "nas.local") &&
            Allowed("10.0.0.5", "https://intranet.example.com/a", "https://intranet.example.com/a", "intranet.example.com");
    }

    private static bool BrowserHostileHtml(string work)
    {
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.Html, string.Concat(Enumerable.Repeat("<img ", 40000)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool found = BrowserDropService.TryInspectBrowserData(data, out _);
        var document = new System.Windows.DataObject();
        document.SetData(System.Windows.DataFormats.UnicodeText, "data:image/docx;base64,AAAA");
        return !found && clock.ElapsedMilliseconds < 3000 &&
            BrowserDropService.TryInspectBrowserData(document, out var candidate) && candidate.GuessedExtension == "png";
    }

    private static bool WorkingFolderPrograms(string work)
    {
        const string name = "flip-prova-programma";
        File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), Path.Combine(work, name + ".exe"));
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = work;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(name) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            process?.WaitForExit(5000);
            return false;
        }
        catch (System.ComponentModel.Win32Exception) { return true; }
        finally { Environment.CurrentDirectory = previous; }
    }

    private static bool ArchiveBomb(string work)
    {
        var bomb = new ArchiveEngine.Quota(1024, work);
        try
        {
            for (long total = 0; total <= ArchiveEngine.Quota.FreeGrowth; total += 1 << 20) bomb.Write(1 << 20);
            return false;
        }
        catch (IOException) { }
        var large = new ArchiveEngine.Quota(1L << 30, work);
        for (int i = 0; i < 4096; i++) large.Write(1 << 20);
        var many = new ArchiveEngine.Quota(1024, work);
        for (int i = 0; i < ArchiveEngine.Quota.MaxEntries; i++) many.Entry();
        try { many.Entry(); return false; }
        catch (IOException) { return true; }
    }

    private static bool GltfBomb(string work)
    {
        string path = Path.Combine(work, "big.gltf"), output = Path.Combine(work, "big.stl");
        File.WriteAllText(path, "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":12,\"uri\":\"data:application/octet-stream;base64,AAAAAAAAAAAAAAAA\"}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteLength\":12}],\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":150000000,\"type\":\"VEC3\"}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]}");
        long before = GC.GetTotalAllocatedBytes();
        var result = new Mesh3DVectorGeoEngine().ConvertAsync(new ConversionContext
        {
            InputFilePath = path, OutputFilePath = output, SourceExtension = "gltf", TargetExtension = "stl", Settings = new FlipSettings()
        }).GetAwaiter().GetResult();
        return !result.Success && GC.GetTotalAllocatedBytes() - before < 200L << 20;
    }

    private static bool EngineChecksum(string work)
    {
        const string whisper = "[{\"assets\":[{\"name\":\"whisper-bin-x64.zip\",\"browser_download_url\":\"https://github.com/ggml-org/whisper.cpp/releases/download/b1/whisper-bin-x64.zip\"}]}]";
        const string pandoc = "{\"assets\":[{\"name\":\"pandoc-9-windows-x86_64.zip\",\"browser_download_url\":\"https://github.com/jgm/pandoc/releases/download/9/pandoc-9-windows-x86_64.zip\"}]}";
        return WhisperModuleInstaller.SelectRuntimeAsset(whisper) is null && EngineInstaller.SelectPandocWindowsAsset(pandoc) is null;
    }

    private static bool WatchLinks(string work)
    {
        string folder = Directory.CreateDirectory(Path.Combine(work, "in")).FullName, output = Directory.CreateDirectory(Path.Combine(work, "out")).FullName;
        string secret = SampleImage(work, "secret.png", 8, 8, SKColors.Blue);
        try { File.CreateSymbolicLink(Path.Combine(work, "link.png"), secret); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; } // Windows senza permesso di creare collegamenti.
        var settings = new FlipSettings { OutputDirectoryMode = OutputDirectoryMode.CustomFolder, CustomOutputDirectory = output };
        settings.WatchFolders.Add(new WatchFolderRule { Enabled = true, FolderPath = folder, TargetExtension = "jpg" });
        using var service = new WatchFolderService(initialSettings: settings);
        Thread.Sleep(100);
        File.CreateSymbolicLink(Path.Combine(folder, "link.png"), secret);
        File.Copy(secret, Path.Combine(folder, "vero.png"));
        bool converted = SpinWait.SpinUntil(() => File.Exists(Path.Combine(output, "vero.jpg")), 10000);
        Thread.Sleep(1500);
        return converted && !File.Exists(Path.Combine(output, "link.jpg"));
    }

    private static bool FfmpegChecksum(string work)
    {
        string hash = new('a', 64);
        return EngineInstaller.ParseFfmpegChecksum(hash + "\n", "x.zip") == hash &&
            EngineInstaller.ParseFfmpegChecksum($"{new string('b', 64)}  altro.zip\n{hash}  x.zip\n", "x.zip") == hash &&
            EngineInstaller.ParseFfmpegChecksum("<html>", "x.zip") == null;
    }

    private static bool FfmpegFormats(string work)
    {
        var edges = new FfmpegEngine().SupportedEdges;
        return !edges.Any(edge => edge.TargetExtension is "mid" or "midi" or "ape" || edge.SourceExtension is "mid" or "midi") &&
            edges.Any(edge => edge.SourceExtension == "ape" && edge.TargetExtension == "mp3");
    }
}
