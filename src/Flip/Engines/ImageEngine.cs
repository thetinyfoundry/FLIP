using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

/// <summary>
/// Motore built-in di conversione grafica ad alte prestazioni:
/// PNG, JPG, JPEG, WEBP, BMP, GIF, TIFF/TIF, ICO (multi-size nativo) e WDP/JXR.
/// Supporta parametri custom di qualità (1-100), ridimensionamento (max_dimension) e scala di grigi (grayscale).
/// </summary>
public sealed class ImageEngine : IConversionEngine
{
    public string Id => "image_builtin";
    public string DisplayName => "Imaging & ICO Native Engine";
    public bool IsExternal => false;

    private static readonly string[] ImageFormats =
    [
        "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "tif", "ico", "wdp", "jxr"
    ];

    private readonly List<ConversionEdge> _supportedEdges = [];

    public IReadOnlyList<ConversionEdge> SupportedEdges => _supportedEdges;

    public ImageEngine()
    {
        foreach (var src in ImageFormats)
        {
            foreach (var dst in ImageFormats)
            {
                if (!string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                {
                    _supportedEdges.Add(new ConversionEdge(src, dst, Id, 10));
                }
            }
        }
    }

    public bool IsAvailable(FlipSettings settings) => true;

    public Task<EngineExecutionResult> ConvertAsync(ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string srcPath = context.InputFilePath;
            string dstPath = context.OutputFilePath;

            if (!File.Exists(srcPath))
            {
                return Task.FromResult(new EngineExecutionResult(false, "", $"File immagine non trovato: {srcPath}"));
            }

            string srcExt = (context.SourceExtension ?? Path.GetExtension(srcPath)).TrimStart('.').ToLowerInvariant();
            string dstExt = (context.TargetExtension ?? Path.GetExtension(dstPath)).TrimStart('.').ToLowerInvariant();

            context.Progress?.Report(0.15);

            // 1. Carica l'immagine (SkiaSharp con fallback a WPF BitmapDecoder)
            using SKBitmap rawBitmap = LoadBitmap(srcPath);
            context.Progress?.Report(0.40);
            context.CancellationToken.ThrowIfCancellationRequested();

            // 2. Applica parametri custom (ridimensionamento, scala di grigi)
            using SKBitmap processed = ApplyImageTransformations(rawBitmap, context.CustomParameters);
            context.Progress?.Report(0.65);
            context.CancellationToken.ThrowIfCancellationRequested();

            // 3. Assicura la directory di destinazione
            string? outDir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            if (File.Exists(dstPath))
            {
                File.Delete(dstPath);
            }

            // 4. Scrivi nel formato target
            int quality = 92;
            if (context.CustomParameters.TryGetValue("quality", out var qStr) && int.TryParse(qStr, out int qVal))
            {
                quality = Math.Clamp(qVal, 1, 100);
            }

            WriteTargetImage(processed, dstPath, dstExt, quality);
            context.Progress?.Report(1.0);

            long bytes = File.Exists(dstPath) ? new FileInfo(dstPath).Length : 0;
            return Task.FromResult(new EngineExecutionResult(true, dstPath, null, bytes));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", "Conversione immagine annullata."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", $"Errore conversione immagine: {ex.Message}"));
        }
    }

    // ── Lettura Immagine ──────────────────────────────────────────────────────

    internal static SKBitmap LoadBitmap(string source)
    {
        byte[] bytes = File.ReadAllBytes(source);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);

        if (codec != null)
        {
            if ((long)codec.Info.Width * codec.Info.Height > 60_000_000)
            {
                throw new IOException("Immagine oltre il limite di sicurezza di 60 megapixel.");
            }

            var result = ImagePixels.Decode(codec, out SKBitmap decoded);
            if (result != SKCodecResult.Success)
            {
                decoded.Dispose();
                throw new IOException($"Immagine corrotta o non decodificabile: {result}");
            }

            return FixOrientation(decoded, codec.EncodedOrigin);
        }

        // Fallback su WPF BitmapDecoder (supporta ICO, WDP, BMP, ecc.)
        using var memory = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        var frame = decoder.Frames[0];

        if ((long)frame.PixelWidth * frame.PixelHeight > 60_000_000)
        {
            throw new IOException("Immagine oltre il limite di sicurezza di 60 megapixel.");
        }

        return ImagePixels.FromWpf(frame);
    }

    private static SKBitmap FixOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return source;

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

        using var canvas = new SKCanvas(destination);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions());
        source.Dispose();
        return destination;
    }

    // ── Trasformazioni (Resize, Grayscale) ─────────────────────────────────────

    private static SKBitmap ApplyImageTransformations(SKBitmap source, Dictionary<string, string> parameters)
    {
        SKBitmap current = source;

        // 1. Ridimensionamento proporzionale (max_dimension)
        if (parameters.TryGetValue("max_dimension", out var maxDimStr) && int.TryParse(maxDimStr, out int maxDim) && maxDim > 0)
        {
            if (current.Width > maxDim || current.Height > maxDim)
            {
                float scale = Math.Min((float)maxDim / current.Width, (float)maxDim / current.Height);
                int targetW = Math.Max(1, (int)Math.Round(current.Width * scale));
                int targetH = Math.Max(1, (int)Math.Round(current.Height * scale));

                var resized = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(resized))
                {
                    canvas.DrawBitmap(current, new SKRect(0, 0, targetW, targetH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                }

                if (!ReferenceEquals(current, source)) current.Dispose();
                current = resized;
            }
        }

        // 2. Grayscale
        if (parameters.TryGetValue("grayscale", out var grayStr) &&
            (grayStr.Equals("true", StringComparison.OrdinalIgnoreCase) || grayStr == "1"))
        {
            var grayBitmap = new SKBitmap(current.Width, current.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(grayBitmap))
            using (var paint = new SKPaint())
            {
                // Matrice di luminanza NTSC standard
                paint.ColorFilter = SKColorFilter.CreateColorMatrix(
                [
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0,      0,      0,      1, 0
                ]);
                canvas.DrawBitmap(current, 0, 0, new SKSamplingOptions(), paint);
            }

            if (!ReferenceEquals(current, source)) current.Dispose();
            current = grayBitmap;
        }

        // 3. Rimozione esplicita metadati EXIF / GPS / XMP ("strip_metadata"): copia i soli pixel su una nuova bitmap
        if (parameters.TryGetValue("strip_metadata", out var stripStr) &&
            (stripStr.Equals("true", StringComparison.OrdinalIgnoreCase) || stripStr == "1"))
        {
            var clean = new SKBitmap(current.Width, current.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(clean))
            {
                canvas.Clear(SKColors.Transparent);
                using var img = SKImage.FromBitmap(current);
                canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
                canvas.Flush();
            }

            if (!ReferenceEquals(current, source)) current.Dispose();
            current = clean;
        }

        return current;
    }

    // ── Scrittura Immagine ────────────────────────────────────────────────────

    private static void WriteTargetImage(SKBitmap bitmap, string outputPath, string format, int quality)
    {
        switch (format)
        {
            case "png":
                SaveSkiaImage(bitmap, outputPath, SKEncodedImageFormat.Png, 100);
                break;

            case "jpg" or "jpeg":
                // JPEG non supporta trasparenza: appiattisci su sfondo bianco
                using (var flattened = ImagePixels.FlattenOnWhite(bitmap))
                    SaveSkiaImage(flattened, outputPath, SKEncodedImageFormat.Jpeg, quality);
                break;

            case "webp":
                SaveSkiaImage(bitmap, outputPath, SKEncodedImageFormat.Webp, quality);
                break;

            case "ico":
                SaveMultiSizeIco(bitmap, outputPath);
                break;

            case "bmp":
                SaveWpfImage(bitmap, outputPath, new BmpBitmapEncoder());
                break;

            case "gif":
                SaveWpfImage(bitmap, outputPath, new GifBitmapEncoder());
                break;

            case "tiff" or "tif":
                SaveWpfImage(bitmap, outputPath, new TiffBitmapEncoder());
                break;

            case "wdp" or "jxr":
                SaveWpfImage(bitmap, outputPath, new WmpBitmapEncoder());
                break;

            default:
                throw new NotSupportedException($"Formato immagine di destinazione non supportato: {format}");
        }
    }

    private static void SaveSkiaImage(SKBitmap bitmap, string path, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality)
            ?? throw new IOException($"Impossibile codificare l'immagine in formato {format}.");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
        stream.Flush(true);
    }

    private static void SaveWpfImage(SKBitmap bitmap, string path, BitmapEncoder encoder) =>
        ImagePixels.SaveWithWpf(bitmap, path, encoder, FileMode.Create);

    // ── Generatore Binario Nativo ICO Multi-Size ──────────────────────────────

    /// <summary>
    /// Genera un file .ico Windows conforme con risoluzioni multiple (256, 128, 64, 48, 32, 16)
    /// salvando ciascun frame come PNG compresso all'interno dell'header ICONDIR standard.
    /// </summary>
    private static void SaveMultiSizeIco(SKBitmap source, string path)
    {
        int[] targetSizes = [256, 128, 64, 48, 32, 16];
        var frames = new List<(int Size, byte[] PngBytes)>();

        foreach (int size in targetSizes)
        {
            if (source.Width >= size || source.Height >= size || size <= 64)
            {
                using var scaled = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var canvas = new SKCanvas(scaled))
                {
                    canvas.Clear(SKColors.Transparent);
                    // Le icone sono quadrate: un'immagine rettangolare va centrata, non deformata.
                    float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
                    float w = source.Width * scale, h = source.Height * scale;
                    canvas.DrawBitmap(source, new SKRect((size - w) / 2, (size - h) / 2, (size + w) / 2, (size + h) / 2),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                }

                using var img = SKImage.FromBitmap(scaled);
                using var pngData = img.Encode(SKEncodedImageFormat.Png, 100);
                if (pngData != null)
                {
                    frames.Add((size, pngData.ToArray()));
                }
            }
        }

        if (frames.Count == 0)
        {
            using var img = SKImage.FromBitmap(source);
            using var pngData = img.Encode(SKEncodedImageFormat.Png, 100);
            frames.Add((Math.Min(256, source.Width), pngData.ToArray()));
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs);

        // Header ICONDIR (6 bytes)
        writer.Write((ushort)0); // Reserved = 0
        writer.Write((ushort)1); // Resource type: 1 = Icon
        writer.Write((ushort)frames.Count); // Numero di immagini

        // Offset di inizio dei dati immagine (dopo l'header e tutte le entries)
        int dataOffset = 6 + (frames.Count * 16);

        // Directory Entries (16 bytes per immagine)
        foreach (var (size, bytes) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // Width (0 significa 256)
            writer.Write((byte)(size >= 256 ? 0 : size)); // Height
            writer.Write((byte)0);                       // Color count (0 = >=8bpp)
            writer.Write((byte)0);                       // Reserved = 0
            writer.Write((ushort)1);                     // Color planes = 1
            writer.Write((ushort)32);                    // Bits per pixel = 32
            writer.Write((uint)bytes.Length);            // Dimensione dati immagine
            writer.Write((uint)dataOffset);              // Offset dati immagine nel file
            dataOffset += bytes.Length;
        }

        // Dati effettivi dei frame PNG
        foreach (var (_, bytes) in frames)
        {
            writer.Write(bytes);
        }

        writer.Flush();
        fs.Flush(true);
    }
}
