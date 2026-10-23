using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
namespace Flip;

internal sealed record ConversionResult(string Source, string Format, string[] Outputs, string? Error);
internal static class ConversionEngine
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    internal static async Task<List<ConversionResult>> Convert(string[] paths, string[] formats, CancellationToken token, Action<string>? progress = null)
    {
        if (paths.Length is 0 or > 128) throw new ArgumentException("Seleziona da 1 a 128 file.");
        if (formats.Length == 0 || formats.All(string.IsNullOrWhiteSpace)) throw new ArgumentException("Seleziona almeno un formato di uscita.");
        await Gate.WaitAsync(token);
        var results = new List<ConversionResult>();
        try
        {
            foreach (string source in paths)
                foreach (string format in formats.Select(f => f.TrimStart('.').ToUpperInvariant()).Distinct())
                {
                    token.ThrowIfCancellationRequested(); progress?.Invoke(Path.GetFileName(source) + " → " + format);
                    try
                    {
                        if (!Policy.Outputs([source]).Contains(format)) throw new IOException("Conversione non disponibile: " + Path.GetExtension(source) + " → " + format);
                        if (!File.Exists(source) || new FileInfo(source).Length > 100L * 1024 * 1024) throw new IOException("File assente o oltre il limite di 100 MiB.");
                        if (Policy.ReadFiles(new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { source })).Length == 0)
                            throw new IOException("Sorgente non locale, non disponibile o non sicura.");
                        ValidateContainer(source);
                        string parent = Path.GetDirectoryName(Path.GetFullPath(source))!, job = Path.Combine(parent, ".flip-job-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(job);
                        try
                        {
                            var staged = new List<string>(); string ext = Path.GetExtension(source).ToLowerInvariant();
                            if (Policy.Images.Contains(ext))
                            {
                                using var bitmap = ReadImage(source);
                                string output = Path.Combine(job, "output." + Extension(format));
                                if (format == "PDF") ImagePdf(bitmap, output); else WriteImage(bitmap, output, format);
                                staged.Add(output);
                            }
                            else if ((ext == ".pdf" || Policy.Documents.Contains(ext)) && format is "PNG" or "JPG" or "WEBP")
                            {
                                byte[] pdf;
                                if (ext == ".pdf") pdf = File.ReadAllBytes(source);
                                else { string intermediate = Path.Combine(job, "document.pdf"); if (ext == ".txt") TextPdf(DocumentTools.ReadText(source), intermediate); else { string copy = Path.Combine(job, "input" + ext); File.Copy(source, copy, false); await DocumentTools.Convert(copy, "PDF", intermediate, job, token); } pdf = File.ReadAllBytes(intermediate); }
                                int pages = PDFtoImage.Conversion.GetPageCount(pdf);
                                if (pages > 200) throw new IOException("PDF oltre il limite di 200 pagine.");
                                int page = 0;
                                foreach (var bitmap in PDFtoImage.Conversion.ToImages(pdf, options: new PDFtoImage.RenderOptions(Dpi: 144, Width: 1800, Height: 2400, WithAspectRatio: true)))
                                {
                                    using (bitmap) { token.ThrowIfCancellationRequested(); string output = Path.Combine(job, $"page-{++page:000}." + Extension(format)); WriteImage(bitmap, output, format); staged.Add(output); }
                                }
                            }
                            else
                            {
                                string output = Path.Combine(job, "output." + Extension(format));
                                if (ext == ".txt" && format == "PDF") TextPdf(DocumentTools.ReadText(source), output);
                                else
                                {
                                    // Tools receive a private copy, never the original path.
                                    string copy = Path.Combine(job, "input" + ext); File.Copy(source, copy, false);
                                    await DocumentTools.Convert(copy, format, output, job, token);
                                }
                                staged.Add(output);
                            }
                            token.ThrowIfCancellationRequested();
                            foreach (var file in staged) Validate(file, format);
                            var committed = new List<string>();
                            try
                            {
                                for (int i = 0; i < staged.Count; i++)
                                {
                                    token.ThrowIfCancellationRequested();
                                    string suffix = staged.Count > 1 || ext == ".pdf" && format is "PNG" or "JPG" or "WEBP" ? $".page-{i + 1:000}" : "";
                                    committed.Add(Commit(staged[i], source, format, suffix));
                                }
                            }
                            catch { foreach (var file in committed) File.Delete(file); throw; }
                            results.Add(new(source, format, committed.ToArray(), null));
                            Evidence.Log("conversion.completed", new { source, format, outputs = committed });
                        }
                        finally { if (Directory.Exists(job)) DeleteJob(job, parent); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { results.Add(new(source, format, [], ex.Message)); Evidence.Log("conversion.failed", new { source, format, error = ex.Message }); }
                }
        }
        finally { Gate.Release(); }
        return results;
    }
    internal static string Extension(string format) => format == "JPG" ? "jpg" : format == "TIFF" ? "tiff" : format.ToLowerInvariant();
    internal static void DeleteJob(string job, string parent)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job));
        string name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(".flip-job-", StringComparison.Ordinal)
            || !Guid.TryParseExact(name[10..], "N", out _))
            throw new IOException("Refusing to clean an unowned temporary directory.");
        void Clean(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to follow a temporary directory link.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to follow a temporary file link.");
                if ((attributes & FileAttributes.Directory) != 0) Clean(entry);
                else
                {
                    // LibreOffice can leave read-only hidden lock files in our private job.
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
            }
            Directory.Delete(directory);
        }
        Clean(full);
    }
    internal static void ValidateContainer(string source)
    {
        if (Path.GetExtension(source).ToLowerInvariant() is not (".docx" or ".epub" or ".odt")) return;
        using var zip = System.IO.Compression.ZipFile.OpenRead(source);
        if (zip.Entries.Count > 4096) throw new IOException("Documento oltre il limite di 4096 elementi interni.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > 256L * 1024 * 1024) throw new IOException("Documento espanso oltre il limite di 256 MiB.");
        }
    }
    static string Commit(string staged, string source, string format, string suffix)
    {
        string stem = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + suffix + ".converted");
        for (int n = 0; n < 10000; n++)
        {
            string result = stem + (n == 0 ? "" : $" ({n})") + "." + Extension(format);
            try { File.Move(staged, result, false); return result; }
            catch (IOException) when (File.Exists(result)) { }
        }
        throw new IOException("Troppi file con lo stesso nome. Nessun file esistente è stato sovrascritto.");
    }
    internal static SKBitmap ReadImage(string source)
    {
        byte[] bytes = File.ReadAllBytes(source);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec != null)
        {
            if ((long)codec.Info.Width * codec.Info.Height > 40_000_000) throw new IOException("Immagine oltre il limite di 40 megapixel.");
            using var decoded = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
            if (result != SKCodecResult.Success) throw new IOException("Immagine corrotta o incompleta: " + result);
            return Orient(decoded, codec.EncodedOrigin);
        }
        using var memory = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0]; if ((long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new IOException("Immagine oltre il limite di 40 megapixel.");
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var bitmap = new SKBitmap(frame.PixelWidth, frame.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        converted.CopyPixels(new System.Windows.Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight), bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        return bitmap;
    }
    static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        bool swap = (int)origin >= 5;
        var destination = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        float w = source.Width, h = source.Height;
        var m = SKMatrix.Identity;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: m.ScaleX = -1; m.TransX = w; break;
            case SKEncodedOrigin.BottomRight: m.ScaleX = -1; m.ScaleY = -1; m.TransX = w; m.TransY = h; break;
            case SKEncodedOrigin.BottomLeft: m.ScaleY = -1; m.TransY = h; break;
            case SKEncodedOrigin.LeftTop: m.ScaleX = 0; m.ScaleY = 0; m.SkewX = 1; m.SkewY = 1; break;
            case SKEncodedOrigin.RightTop: m.ScaleX = 0; m.ScaleY = 0; m.SkewX = -1; m.SkewY = 1; m.TransX = h; break;
            case SKEncodedOrigin.RightBottom: m.ScaleX = 0; m.ScaleY = 0; m.SkewX = -1; m.SkewY = -1; m.TransX = h; m.TransY = w; break;
            case SKEncodedOrigin.LeftBottom: m.ScaleX = 0; m.ScaleY = 0; m.SkewX = 1; m.SkewY = -1; m.TransY = w; break;
        }
        using var canvas = new SKCanvas(destination); canvas.SetMatrix(m); canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions()); return destination;
    }
    internal static void WriteImage(SKBitmap bitmap, string path, string format)
    {
        if (format is "PNG" or "JPG" or "WEBP")
        {
            using var flattened = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(flattened)) { canvas.Clear(SKColors.White); canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions()); }
            using var image = SKImage.FromBitmap(format == "JPG" ? flattened : bitmap);
            using var encoded = image.Encode(format == "PNG" ? SKEncodedImageFormat.Png : format == "JPG" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp, format == "PNG" ? 100 : 92)
                ?? throw new IOException("Encoder immagine non disponibile.");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); encoded.SaveTo(stream); stream.Flush(true);
            return;
        }
        using var bgra = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bgra)) canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions());
        var source = BitmapSource.Create(bgra.Width, bgra.Height, 96, 96, PixelFormats.Bgra32, null, bgra.GetPixels(), bgra.ByteCount, bgra.RowBytes);
        BitmapEncoder encoder = format switch { "BMP" => new BmpBitmapEncoder(), "TIFF" => new TiffBitmapEncoder(), "GIF" => new GifBitmapEncoder(), _ => throw new IOException("Encoder non supportato: " + format) };
        encoder.Frames.Add(BitmapFrame.Create(source)); using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); encoder.Save(file); file.Flush(true);
    }
    internal static void ImagePdf(SKBitmap bitmap, string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var document = SKDocument.CreatePdf(stream) ?? throw new IOException("Encoder PDF non disponibile.");
        float width = 595, height = 842, margin = 30;
        using (var image = SKImage.FromBitmap(bitmap))
        {
            var canvas = document.BeginPage(width, height); canvas.Clear(SKColors.White);
            float scale = Math.Min((width - 2 * margin) / bitmap.Width, (height - 2 * margin) / bitmap.Height);
            float w = bitmap.Width * scale, h = bitmap.Height * scale;
            canvas.DrawImage(image, new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2), new SKSamplingOptions(SKFilterMode.Linear)); document.EndPage();
        }
        document.Close(); stream.Flush(true);
    }
    internal static void TextPdf(string text, string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var document = SKDocument.CreatePdf(stream) ?? throw new IOException("Encoder PDF non disponibile.");
        using var typeface = SKTypeface.FromFamilyName("Consolas");
        using var font = new SKFont(typeface, 11); using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        SKCanvas canvas = document.BeginPage(595, 842); canvas.Clear(SKColors.White); float y = 45; int pages = 1;
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string value = line.Replace("\t", "    "); int offset = 0;
            do
            {
                if (y > 805) { document.EndPage(); if (++pages > 200) throw new IOException("Testo oltre il limite di 200 pagine."); canvas = document.BeginPage(595, 842); canvas.Clear(SKColors.White); y = 45; }
                int count = Math.Min(78, value.Length - offset);
                if (count > 0 && char.IsHighSurrogate(value[offset + count - 1]) && offset + count < value.Length) count--;
                canvas.DrawText(count == 0 ? "" : value.Substring(offset, count), 35, y, SKTextAlign.Left, font, paint); y += 15; offset += count;
            } while (offset < value.Length);
        }
        document.EndPage(); document.Close(); stream.Flush(true);
    }
    internal static void Validate(string path, string format)
    {
        if (new FileInfo(path).Length == 0) throw new IOException("Output vuoto.");
        if (format == "PDF") { using var pdf = UglyToad.PdfPig.PdfDocument.Open(path); if (pdf.NumberOfPages == 0) throw new IOException("PDF senza pagine."); }
        else if (format is "PNG" or "JPG" or "WEBP" or "BMP" or "TIFF" or "GIF") { using var bitmap = ReadImage(path); if (bitmap.Width == 0) throw new IOException("Output immagine non valido."); }
        else if (format is "DOCX" or "EPUB" or "ODT") { using var zip = System.IO.Compression.ZipFile.OpenRead(path); if (zip.Entries.Count == 0) throw new IOException("Documento vuoto."); }
    }
}
