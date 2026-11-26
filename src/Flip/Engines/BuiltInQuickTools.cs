using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Flip.Core;
using Flip.Routing;

namespace Flip.Engines;

// ══════════════════════════════════════════════════════════════════════════════
//  0. BUILT-IN QUICK TOOLS — AZIONI ESPLICITE (tool:*) & HELPER RASTER CONDIVISI
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Dispatcher e libreria condivisa per le azioni rapide esplicite (<c>tool:*</c>) di rotazione,
/// specchiatura, ritaglio (aspect ratio social e coordinate manuali), sfocatura privacy e ridimensionamento.
/// </summary>
public static class BuiltInQuickTools
{
    public const string RotateRightId = "tool:rotate_right";
    public const string Rotate90Id = "tool:rotate_90";
    public const string RotateLeftId = "tool:rotate_left";
    public const string Rotate180Id = "tool:rotate_180";
    public const string FlipHorizontalId = "tool:flip_h";
    public const string FlipVerticalId = "tool:flip_v";

    public const string Crop1x1Id = "tool:crop_1_1";
    public const string Crop16x9Id = "tool:crop_16_9";
    public const string Crop9x16Id = "tool:crop_9_16";
    public const string Crop4x5Id = "tool:crop_4_5";
    public const string Crop4x3Id = "tool:crop_4_3";
    public const string CropFreeId = "tool:crop_free";

    public const string Resize50Id = "tool:resize_50";
    public const string Resize1080pId = "tool:resize_1080p";
    public const string Resize4kId = "tool:resize_4k";
    public const string Compress1MbId = "tool:compress_1mb";

    private const long OneMegabyteBytes = 1024L * 1024L;

    internal static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "tif"
    };

    public static IReadOnlyList<string> SupportedActionIds { get; } =
    [
        RotateRightId, Rotate90Id, RotateLeftId, Rotate180Id, FlipHorizontalId, FlipVerticalId,
        Crop1x1Id, Crop16x9Id, Crop9x16Id, Crop4x5Id, Crop4x3Id, CropFreeId,
        Resize50Id, Resize1080pId, Resize4kId, Compress1MbId
    ];

    private static readonly HashSet<string> SupportedActionSet =
        new(SupportedActionIds, StringComparer.OrdinalIgnoreCase);

    internal enum OrientationTransform
    {
        None,
        Rotate90Clockwise,
        Rotate180,
        Rotate270Clockwise,
        FlipHorizontal,
        FlipVertical
    }

    public static bool IsSupportedToolAction(string? toolId) =>
        !string.IsNullOrWhiteSpace(toolId) && SupportedActionSet.Contains(toolId.Trim());

    /// <summary>
    /// Esegue un'azione esplicita <c>tool:*</c> usando <see cref="QuickToolContext"/> e commit atomico via <see cref="OutputPathAllocator"/>.
    /// </summary>
    public static Task<QuickToolResult> ExecuteActionAsync(string toolId, QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            string normalizedId = NormalizeToolId(toolId);
            if (!SupportedActionSet.Contains(normalizedId))
                throw new NotSupportedException(Loc.F("Azione rapida non supportata: {0}", "Unsupported quick action: {0}", toolId));

            string suffix = GetActionSuffix(normalizedId);
            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var job = output.CreateJob(inputPath);
                string ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
                if (!RasterExtensions.Contains(ext))
                    throw new NotSupportedException(Loc.F("Formato immagine non supportato da {0}: .{1}", "Image format not supported by {0}: .{1}", normalizedId, ext));

                string fileName = $"{Path.GetFileNameWithoutExtension(inputPath)}{suffix}.{ext}";
                string stagedPath = job.Stage(fileName);
                ExecuteRasterActionToPath(normalizedId, inputPath, stagedPath, ext, context.Parameters, context.CancellationToken);
                output.Commit(job, stagedPath, fileName);
            }

            return Task.FromResult(output.Complete());
        }
        catch (Exception ex)
        {
            return Task.FromResult(output.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Esegue un'azione esplicita <c>tool:*</c> a partire da un <see cref="ConversionContext"/> (usando <see cref="ConversionContext.CustomParameters"/>).
    /// </summary>
    public static Task<EngineExecutionResult> ExecuteActionAsync(string toolId, ConversionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteActionAsync(
            toolId,
            context.InputFilePath,
            context.OutputFilePath,
            context.CustomParameters,
            context.CancellationToken);
    }

    /// <summary>
    /// Esegue un'azione esplicita <c>tool:*</c> direttamente da <paramref name="sourcePath"/> a <paramref name="outputPath"/>.
    /// </summary>
    public static Task<EngineExecutionResult> ExecuteActionAsync(
        string toolId,
        string sourcePath,
        string outputPath,
        IReadOnlyDictionary<string, string>? customParameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedId = NormalizeToolId(toolId);
            if (!SupportedActionSet.Contains(normalizedId))
                return Task.FromResult(new EngineExecutionResult(false, "", Loc.F("Azione rapida non supportata: {0}", "Unsupported quick action: {0}", toolId)));

            string ext = ResolveTargetExtension(outputPath, sourcePath);
            string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tempPath = Path.Combine(dir ?? Path.GetTempPath(), $".flip-tool-{Guid.NewGuid():N}.{ext}");
            try
            {
                ExecuteRasterActionToPath(normalizedId, sourcePath, tempPath, ext, customParameters, cancellationToken);
                File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }

            long size = new FileInfo(outputPath).Length;
            return Task.FromResult(new EngineExecutionResult(true, outputPath, null, size));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new EngineExecutionResult(false, "", ex.Message));
        }
    }

    /// <summary>
    /// Carica l'immagine rispettando l'orientamento EXIF, applica eventuali regioni normalizzate di sfocatura/pixelate
    /// privacy (<paramref name="blurRegions"/>) e il ritaglio normalizzato (<paramref name="normX"/>, <paramref name="normY"/>,
    /// <paramref name="normW"/>, <paramref name="normH"/>) con clamping rigoroso e dimensione minima di 1 px.
    /// Se <paramref name="outputPath"/> è specificato salva il risultato su disco, e restituisce sempre un
    /// <see cref="BitmapSource"/> congelato (<see cref="System.Windows.Freezable.Freeze"/>) thread-safe.
    /// </summary>
    public static BitmapSource CropImageToFileOrBitmap(
        string sourcePath,
        double normX,
        double normY,
        double normW,
        double normH,
        IReadOnlyList<(double X, double Y, double W, double H)>? blurRegions,
        string? outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(Loc.T("Immagine sorgente non trovata.", "Source image not found."), sourcePath);

        using SKBitmap oriented = LoadOrientedBitmap(sourcePath);
        if (blurRegions is { Count: > 0 })
            ApplyBlurRegions(oriented, blurRegions);

        SKRectI cropRect = ComputeNormalizedCropRect(oriented.Width, oriented.Height, normX, normY, normW, normH);
        bool fullImage = cropRect.Left == 0 && cropRect.Top == 0 &&
                         cropRect.Width == oriented.Width && cropRect.Height == oriented.Height;

        SKBitmap? cropped = fullImage ? null : CropBitmap(oriented, cropRect);
        try
        {
            SKBitmap result = cropped ?? oriented;
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                string ext = ResolveTargetExtension(outputPath, sourcePath);
                SaveBitmapAtomic(result, outputPath, ext);
            }

            return ToFrozenBitmapSource(result);
        }
        finally
        {
            cropped?.Dispose();
        }
    }

    // ── Helper Interni Condivisi (Caricamento EXIF, Trasformazione, Crop, Blur, Resize, Salvataggio) ──

    internal static SKBitmap LoadOrientedBitmap(string sourcePath) =>
        ConversionEngine.ReadImage(sourcePath);

    internal static SKBitmap TransformBitmap(SKBitmap source, OrientationTransform transform)
    {
        ArgumentNullException.ThrowIfNull(source);
        bool swapDims = transform is OrientationTransform.Rotate90Clockwise or OrientationTransform.Rotate270Clockwise;
        int targetW = swapDims ? source.Height : source.Width;
        int targetH = swapDims ? source.Width : source.Height;

        var result = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        switch (transform)
        {
            case OrientationTransform.Rotate90Clockwise:
                canvas.Translate(targetW, 0);
                canvas.RotateDegrees(90);
                break;
            case OrientationTransform.Rotate180:
                canvas.Translate(targetW, targetH);
                canvas.RotateDegrees(180);
                break;
            case OrientationTransform.Rotate270Clockwise:
                canvas.Translate(0, targetH);
                canvas.RotateDegrees(270);
                break;
            case OrientationTransform.FlipHorizontal:
                canvas.Translate(targetW, 0);
                canvas.Scale(-1, 1);
                break;
            case OrientationTransform.FlipVertical:
                canvas.Translate(0, targetH);
                canvas.Scale(1, -1);
                break;
        }

        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions());
        canvas.Flush();
        return result;
    }

    internal static SKRectI ComputeCenteredAspectCropRect(int srcW, int srcH, double targetAspect)
    {
        srcW = Math.Max(1, srcW);
        srcH = Math.Max(1, srcH);
        if (!double.IsFinite(targetAspect) || targetAspect <= 0)
            targetAspect = 1.0;

        int cropW, cropH;
        if ((double)srcW / srcH > targetAspect)
        {
            cropH = srcH;
            cropW = Math.Clamp((int)Math.Round(srcH * targetAspect), 1, srcW);
        }
        else
        {
            cropW = srcW;
            cropH = Math.Clamp((int)Math.Round(srcW / targetAspect), 1, srcH);
        }

        int cropX = Math.Clamp((srcW - cropW) / 2, 0, srcW - cropW);
        int cropY = Math.Clamp((srcH - cropH) / 2, 0, srcH - cropH);
        return SKRectI.Create(cropX, cropY, cropW, cropH);
    }

    internal static SKRectI ComputeNormalizedCropRect(int srcW, int srcH, double normX, double normY, double normW, double normH)
    {
        srcW = Math.Max(1, srcW);
        srcH = Math.Max(1, srcH);

        static (int Offset, int Size) ClampAxis(int length, double rawPos, double rawSize)
        {
            double pos = double.IsNaN(rawPos) ? 0.0 :
                         double.IsNegativeInfinity(rawPos) ? 0.0 :
                         double.IsPositiveInfinity(rawPos) ? 1.0 : rawPos;
            double size = double.IsNaN(rawSize) ? 1.0 :
                          double.IsNegativeInfinity(rawSize) ? 0.0 :
                          double.IsPositiveInfinity(rawSize) ? 1.0 : Math.Max(0.0, rawSize);

            double startNorm = Math.Clamp(pos, 0.0, 1.0);
            double endNorm = Math.Clamp(pos + size, 0.0, 1.0);
            if (endNorm < startNorm) endNorm = startNorm;

            int pxStart = Math.Clamp((int)Math.Floor(startNorm * length), 0, length - 1);
            int pxEnd = Math.Clamp((int)Math.Ceiling(endNorm * length), pxStart + 1, length);
            int pxSize = Math.Clamp(pxEnd - pxStart, 1, length - pxStart);
            return (pxStart, pxSize);
        }

        var (x, w) = ClampAxis(srcW, normX, normW);
        var (y, h) = ClampAxis(srcH, normY, normH);
        return SKRectI.Create(x, y, w, h);
    }

    internal static SKBitmap CropBitmap(SKBitmap source, SKRectI cropRect)
    {
        ArgumentNullException.ThrowIfNull(source);
        int x = Math.Clamp(cropRect.Left, 0, Math.Max(0, source.Width - 1));
        int y = Math.Clamp(cropRect.Top, 0, Math.Max(0, source.Height - 1));
        int w = Math.Clamp(cropRect.Width, 1, Math.Max(1, source.Width - x));
        int h = Math.Clamp(cropRect.Height, 1, Math.Max(1, source.Height - y));

        var cropped = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(cropped);
        canvas.Clear(SKColors.Transparent);
        using var img = SKImage.FromBitmap(source);
        canvas.DrawImage(
            img,
            new SKRect(x, y, x + w, y + h),
            new SKRect(0, 0, w, h),
            new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Flush();
        return cropped;
    }

    internal static void ApplyBlurRegions(
        SKBitmap target,
        IReadOnlyList<(double X, double Y, double W, double H)> blurRegions)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (blurRegions.Count == 0) return;

        using var canvas = new SKCanvas(target);
        foreach (var region in blurRegions)
        {
            SKRectI rect = ComputeNormalizedCropRect(target.Width, target.Height, region.X, region.Y, region.W, region.H);
            int maxSide = Math.Max(rect.Width, rect.Height);
            int block = maxSide <= 4 ? Math.Max(2, maxSide) : Math.Clamp(maxSide / 8, 4, 24);
            int tinyW = Math.Max(1, rect.Width / block);
            int tinyH = Math.Max(1, rect.Height / block);

            using var tiny = new SKBitmap(tinyW, tinyH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var tinyCanvas = new SKCanvas(tiny))
            using (var srcImg = SKImage.FromBitmap(target))
            {
                tinyCanvas.DrawImage(
                    srcImg,
                    new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    new SKRect(0, 0, tinyW, tinyH),
                    new SKSamplingOptions(SKFilterMode.Linear));
                tinyCanvas.Flush();
            }

            canvas.Save();
            canvas.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
            float sigma = Math.Clamp(block * 0.45f, 1.5f, 10f);
            using (var tinyImg = SKImage.FromBitmap(tiny))
            using (var blurFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp))
            using (var blurPaint = new SKPaint { ImageFilter = blurFilter })
            {
                canvas.DrawImage(
                    tinyImg,
                    new SKRect(0, 0, tinyW, tinyH),
                    new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    new SKSamplingOptions(SKFilterMode.Nearest),
                    blurPaint);
            }
            canvas.Restore();
        }
        canvas.Flush();
    }

    internal static SKBitmap ResizeBitmap(SKBitmap source, int targetW, int targetH)
    {
        targetW = Math.Max(1, targetW);
        targetH = Math.Max(1, targetH);
        var resized = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(resized);
        canvas.Clear(SKColors.Transparent);
        using var img = SKImage.FromBitmap(source);
        canvas.DrawImage(
            img,
            new SKRect(0, 0, targetW, targetH),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Flush();
        return resized;
    }

    internal static (int Width, int Height) ComputeResizeDimensions(int srcW, int srcH, string actionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(srcW);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(srcH);
        int longest = Math.Max(srcW, srcH);
        double scale = actionId switch
        {
            Resize50Id => 0.5,
            Resize1080pId => Math.Min(1, 1920d / longest),
            Resize4kId => Math.Min(1, 3840d / longest),
            _ => throw new ArgumentOutOfRangeException(nameof(actionId))
        };
        return (Math.Max(1, (int)Math.Round(srcW * scale)), Math.Max(1, (int)Math.Round(srcH * scale)));
    }

    internal static void CompressImageToTargetBytes(
        string inputPath,
        string outputPath,
        string ext,
        long targetBytes,
        CancellationToken token)
    {
        long origLen = new FileInfo(inputPath).Length;
        if (origLen <= targetBytes && !HasNonDefaultExifOrientation(inputPath))
        {
            File.Copy(inputPath, outputPath, overwrite: false);
            return;
        }

        using var orig = LoadOrientedBitmap(inputPath);
        SKEncodedImageFormat? fmt = ext switch
        {
            "webp" => SKEncodedImageFormat.Webp,
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            _ => null
        };

        int[] qualities = [88, 78, 66, 54, 42, 30, 20];
        SKBitmap current = orig;
        bool owned = false;

        try
        {
            for (int scalePass = 0; scalePass < 5; scalePass++)
            {
                token.ThrowIfCancellationRequested();

                if (fmt is { } encoder)
                {
                    SKBitmap encodeTarget = current;
                    SKBitmap? flattened = null;
                    if (encoder == SKEncodedImageFormat.Jpeg)
                    {
                        flattened = new SKBitmap(current.Width, current.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                        using var c = new SKCanvas(flattened);
                        c.Clear(SKColors.White);
                        c.DrawBitmap(current, 0, 0, new SKSamplingOptions());
                        encodeTarget = flattened;
                    }

                    try
                    {
                        foreach (int q in qualities)
                        {
                            token.ThrowIfCancellationRequested();
                            using var data = encodeTarget.Encode(encoder, q);
                            if (data != null && data.Size <= targetBytes)
                            {
                                using var fs = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                                data.SaveTo(fs);
                                fs.Flush(true);
                                return;
                            }
                        }
                    }
                    finally
                    {
                        flattened?.Dispose();
                    }
                }
                else
                {
                    ConversionEngine.WriteImage(current, outputPath, QuickToolOutput.ImageFormat(ext));
                    if (new FileInfo(outputPath).Length <= targetBytes) return;
                    File.Delete(outputPath);
                }

                if (scalePass == 4) break;
                int nextW = Math.Max(1, (int)Math.Round(current.Width * 0.72));
                int nextH = Math.Max(1, (int)Math.Round(current.Height * 0.72));
                if (nextW == current.Width && nextH == current.Height) break;
                var scaled = ResizeBitmap(current, nextW, nextH);
                if (owned) current.Dispose();
                current = scaled;
                owned = true;
            }

            throw new IOException(Loc.T("Impossibile comprimere l'immagine entro la dimensione richiesta.", "Unable to compress image within the target size."));
        }
        finally
        {
            if (owned) current.Dispose();
        }
    }

    internal static BitmapSource ToFrozenBitmapSource(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        SKBitmap bgra = bitmap;
        bool ownsBgra = false;
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType != SKAlphaType.Premul)
        {
            bgra = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bgra);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions());
            canvas.Flush();
            ownsBgra = true;
        }

        try
        {
            int stride = bgra.RowBytes;
            byte[] pixels = new byte[stride * bgra.Height];
            Marshal.Copy(bgra.GetPixels(), pixels, 0, pixels.Length);
            var source = BitmapSource.Create(
                bgra.Width,
                bgra.Height,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                stride);
            source.Freeze();
            return source;
        }
        finally
        {
            if (ownsBgra) bgra.Dispose();
        }
    }

    internal static void SaveBitmapAtomic(SKBitmap bitmap, string outputPath, string ext)
    {
        string fullOut = Path.GetFullPath(outputPath);
        string? dir = Path.GetDirectoryName(fullOut);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir ?? Path.GetTempPath(), $".flip-img-{Guid.NewGuid():N}.{ext}");
        try
        {
            ConversionEngine.WriteImage(bitmap, tmp, QuickToolOutput.ImageFormat(ext));
            File.Move(tmp, fullOut, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    internal static OrientationTransform ParseOrientationTransform(string rawMode) =>
        rawMode.Trim().ToLowerInvariant() switch
        {
            "90" or "right" or "rotate_right" or "rotate_90" or RotateRightId or Rotate90Id => OrientationTransform.Rotate90Clockwise,
            "180" or "rotate_180" or Rotate180Id => OrientationTransform.Rotate180,
            "270" or "left" or "rotate_left" or "rotate_270" or RotateLeftId => OrientationTransform.Rotate270Clockwise,
            "flip_h" or "flip_horizontal" or "fliph" or FlipHorizontalId => OrientationTransform.FlipHorizontal,
            "flip_v" or "flip_vertical" or "flipv" or FlipVerticalId => OrientationTransform.FlipVertical,
            _ => OrientationTransform.Rotate90Clockwise
        };

    private static void ExecuteRasterActionToPath(
        string normalizedId,
        string sourcePath,
        string stagedPath,
        string ext,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken token)
    {
        if (normalizedId == Compress1MbId)
        {
            CompressImageToTargetBytes(sourcePath, stagedPath, ext, OneMegabyteBytes, token);
            return;
        }

        using SKBitmap oriented = LoadOrientedBitmap(sourcePath);
        token.ThrowIfCancellationRequested();

        if (normalizedId is RotateRightId or Rotate90Id or RotateLeftId or Rotate180Id or FlipHorizontalId or FlipVerticalId)
        {
            using var transformed = TransformBitmap(oriented, ParseOrientationTransform(normalizedId));
            ConversionEngine.WriteImage(transformed, stagedPath, QuickToolOutput.ImageFormat(ext));
            return;
        }

        if (normalizedId is Crop1x1Id or Crop16x9Id or Crop9x16Id or Crop4x5Id or Crop4x3Id)
        {
            double aspect = normalizedId switch
            {
                Crop1x1Id => 1.0,
                Crop16x9Id => 16.0 / 9.0,
                Crop9x16Id => 9.0 / 16.0,
                Crop4x5Id => 4.0 / 5.0,
                Crop4x3Id => 4.0 / 3.0,
                _ => 1.0
            };
            SKRectI rect = ComputeCenteredAspectCropRect(oriented.Width, oriented.Height, aspect);
            using var cropped = CropBitmap(oriented, rect);
            ConversionEngine.WriteImage(cropped, stagedPath, QuickToolOutput.ImageFormat(ext));
            return;
        }

        if (normalizedId == CropFreeId)
        {
            double nx = ReadDouble(parameters, "crop_x", 0.0);
            double ny = ReadDouble(parameters, "crop_y", 0.0);
            double nw = ReadDouble(parameters, "crop_w", 1.0);
            double nh = ReadDouble(parameters, "crop_h", 1.0);
            var blurRegions = ParseBlurRegionsParameter(parameters);
            if (blurRegions is { Count: > 0 })
                ApplyBlurRegions(oriented, blurRegions);

            SKRectI rect = ComputeNormalizedCropRect(oriented.Width, oriented.Height, nx, ny, nw, nh);
            using var cropped = CropBitmap(oriented, rect);
            ConversionEngine.WriteImage(cropped, stagedPath, QuickToolOutput.ImageFormat(ext));
            return;
        }

        if (normalizedId is Resize50Id or Resize1080pId or Resize4kId)
        {
            var size = ComputeResizeDimensions(oriented.Width, oriented.Height, normalizedId);
            using var resized = ResizeBitmap(oriented, size.Width, size.Height);
            ConversionEngine.WriteImage(resized, stagedPath, QuickToolOutput.ImageFormat(ext));
            return;
        }

        throw new NotSupportedException(Loc.F("Azione rapida non supportata: {0}", "Unsupported quick action: {0}", normalizedId));
    }

    private static string NormalizeToolId(string? toolId) =>
        (toolId ?? "").Trim().ToLowerInvariant();

    private static string ResolveTargetExtension(string outputPath, string sourcePath)
    {
        string outExt = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        if (RasterExtensions.Contains(outExt)) return outExt;
        string srcExt = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        return RasterExtensions.Contains(srcExt) ? srcExt : "png";
    }

    private static string GetActionSuffix(string normalizedId) => normalizedId switch
    {
        RotateRightId or Rotate90Id => "_rot90",
        RotateLeftId => "_rot270",
        Rotate180Id => "_rot180",
        FlipHorizontalId => "_fliph",
        FlipVerticalId => "_flipv",
        Crop1x1Id => "_1x1",
        Crop16x9Id => "_16x9",
        Crop9x16Id => "_9x16",
        Crop4x5Id => "_4x5",
        Crop4x3Id => "_4x3",
        CropFreeId => "_cropped",
        Resize50Id => "_50pct",
        Resize1080pId => "_1080p",
        Resize4kId => "_4k",
        Compress1MbId => "_1mb",
        _ => "_edited"
    };

    private static bool HasNonDefaultExifOrientation(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            return codec != null && codec.EncodedOrigin != SKEncodedOrigin.TopLeft;
        }
        catch
        {
            return false;
        }
    }

    internal static double ReadDouble(IReadOnlyDictionary<string, string>? parameters, string key, double fallback)
    {
        if (parameters == null || !parameters.TryGetValue(key, out string? raw)) return fallback;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }

    private static List<(double X, double Y, double W, double H)>? ParseBlurRegionsParameter(
        IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters == null || !parameters.TryGetValue("blur_regions", out string? raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        var list = new List<(double, double, double, double)>();
        foreach (string item in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
            {
                list.Add((x, y, w, h));
            }
        }
        return list.Count > 0 ? list : null;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  1. CROP QUICK TOOL
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Strumento rapido per il ritaglio proporzionale centrato (1:1, 16:9, 9:16, 4:5, 4:3, 3:2, 9:16 Blur)
/// e ritaglio manuale tramite coordinate normalizzate (<c>crop_x</c>, <c>crop_y</c>, <c>crop_w</c>, <c>crop_h</c>).
/// </summary>
public sealed class CropQuickTool : IQuickTool
{
    public string Id => "crop";
    public string DisplayName => Loc.T("Ritaglio Rapido", "Quick Crop");
    public string IconGlyph => "\uE7A8"; // Crop
    public bool SupportsMultipleFiles => false;

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => BuiltInQuickTools.RasterExtensions.Contains(e.TrimStart('.')));
    }

    public Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string ratioStr = context.Parameters.GetValueOrDefault("ratio", "1:1").Trim();
            bool isSocialBlur = ratioStr.Contains("blur", StringComparison.OrdinalIgnoreCase);
            bool isFreeCrop = ratioStr.Equals("free", StringComparison.OrdinalIgnoreCase) ||
                              ratioStr.Equals(BuiltInQuickTools.CropFreeId, StringComparison.OrdinalIgnoreCase) ||
                              context.Parameters.ContainsKey("crop_x") ||
                              context.Parameters.ContainsKey("crop_y") ||
                              context.Parameters.ContainsKey("crop_w") ||
                              context.Parameters.ContainsKey("crop_h");
            double targetAspect = isSocialBlur ? 9.0 / 16.0 : ParseAspectRatio(ratioStr);

            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var job = output.CreateJob(inputPath);

                using var orig = BuiltInQuickTools.LoadOrientedBitmap(inputPath);
                string ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
                string suffix = isSocialBlur ? "_9x16_blur" : "_cropped";
                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}{suffix}.{ext}";
                string outPath = job.Stage(name);

                if (isSocialBlur)
                {
                    using var vertical = RenderSocialBlur9x16(orig);
                    ConversionEngine.WriteImage(vertical, outPath, QuickToolOutput.ImageFormat(ext));
                    output.Commit(job, outPath, name);
                    continue;
                }

                SKRectI cropRect = isFreeCrop
                    ? BuiltInQuickTools.ComputeNormalizedCropRect(
                        orig.Width,
                        orig.Height,
                        BuiltInQuickTools.ReadDouble(context.Parameters, "crop_x", 0.0),
                        BuiltInQuickTools.ReadDouble(context.Parameters, "crop_y", 0.0),
                        BuiltInQuickTools.ReadDouble(context.Parameters, "crop_w", 1.0),
                        BuiltInQuickTools.ReadDouble(context.Parameters, "crop_h", 1.0))
                    : BuiltInQuickTools.ComputeCenteredAspectCropRect(orig.Width, orig.Height, targetAspect);

                using var cropped = BuiltInQuickTools.CropBitmap(orig, cropRect);
                ConversionEngine.WriteImage(cropped, outPath, QuickToolOutput.ImageFormat(ext));
                output.Commit(job, outPath, name);
            }

            return Task.FromResult(output.Complete());
        }
        catch (Exception ex)
        {
            return Task.FromResult(output.Fail(ex.Message));
        }
    }

    private static SKBitmap RenderSocialBlur9x16(SKBitmap orig)
    {
        const int targetW = 1080;
        const int targetH = 1920;
        var canvasBmp = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(canvasBmp);
        canvas.Clear(new SKColor(14, 16, 22));

        using var img = SKImage.FromBitmap(orig);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        // 1. Sfondo scalato a pieno schermo (cover) con sfocatura gaussiana 28x28
        float coverScale = Math.Max((float)targetW / orig.Width, (float)targetH / orig.Height) * 1.08f;
        float bgW = orig.Width * coverScale;
        float bgH = orig.Height * coverScale;
        var bgRect = SKRect.Create((targetW - bgW) / 2f, (targetH - bgH) / 2f, bgW, bgH);

        using (var blurFilter = SKImageFilter.CreateBlur(28f, 28f, SKShaderTileMode.Clamp))
        using (var blurPaint = new SKPaint { ImageFilter = blurFilter, IsAntialias = true })
        {
            canvas.DrawImage(img, bgRect, sampling, blurPaint);
        }

        // Velo scuro leggero per staccare il soggetto in primo piano
        using (var veilPaint = new SKPaint { Color = new SKColor(10, 12, 18, 96), Style = SKPaintStyle.Fill })
        {
            canvas.DrawRect(0, 0, targetW, targetH, veilPaint);
        }

        // 2. Primo piano nitido centrato con bordi puliti
        float fitScale = Math.Min((float)targetW / orig.Width, (float)targetH / orig.Height);
        float fgW = orig.Width * fitScale;
        float fgH = orig.Height * fitScale;
        var fgRect = SKRect.Create((targetW - fgW) / 2f, (targetH - fgH) / 2f, fgW, fgH);

        canvas.DrawImage(img, fgRect, sampling);

        using (var borderPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 48),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        })
        {
            canvas.DrawRect(fgRect, borderPaint);
        }

        canvas.Flush();
        return canvasBmp;
    }

    private static double ParseAspectRatio(string ratio)
    {
        string[] parts = ratio.Split([':', '/', 'x', 'X'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
            h > 0)
        {
            return w / h;
        }
        return 1.0; // fallback 1:1 quadrato
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  2. ROTATE QUICK TOOL
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Strumento rapido per rotazione (90°, 180°, 270°) e specchiatura orizzontale/verticale con normalizzazione EXIF.
/// </summary>
public sealed class RotateQuickTool : IQuickTool
{
    public string Id => "rotate";
    public string DisplayName => Loc.T("Ruota & Specchia", "Rotate & Flip");
    public string IconGlyph => "\uE7AD"; // Rotate
    public bool SupportsMultipleFiles => false;

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => BuiltInQuickTools.RasterExtensions.Contains(e.TrimStart('.')));
    }

    public Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            string mode = context.Parameters.GetValueOrDefault("angle", "90");
            var transform = BuiltInQuickTools.ParseOrientationTransform(mode);

            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var job = output.CreateJob(inputPath);

                using var orig = BuiltInQuickTools.LoadOrientedBitmap(inputPath);
                using var rotated = BuiltInQuickTools.TransformBitmap(orig, transform);

                string ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_rotated.{ext}";
                string outPath = job.Stage(name);

                ConversionEngine.WriteImage(rotated, outPath, QuickToolOutput.ImageFormat(ext));
                output.Commit(job, outPath, name);
            }

            return Task.FromResult(output.Complete());
        }
        catch (Exception ex)
        {
            return Task.FromResult(output.Fail(ex.Message));
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  3. MERGE PDF QUICK TOOL
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Unisce più file trascinati (immagini, testo o PDF) in un unico documento PDF multi-pagina.
/// </summary>
public sealed class MergePdfQuickTool : IQuickTool
{
    public string Id => "merge_pdf";
    public string DisplayName => Loc.T("Unisci in PDF", "Merge to PDF");
    public string IconGlyph => "\uE8A7"; // Merge PDF
    public bool SupportsMultipleFiles => true;

    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "txt", "md", "csv"
    };

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => SupportedExts.Contains(e.TrimStart('.')));
    }

    public Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.InputFilePaths.Count == 0)
            return Task.FromResult(new QuickToolResult(false, [], Loc.T("Nessun file fornito per l'unione in PDF.", "No files provided to merge into PDF.")));

        var output = new QuickToolOutput(context);
        try
        {
            string[] inputs = output.Sources.ToArray();
            using var job = output.CreateJob(inputs);
            string name = $"{Path.GetFileNameWithoutExtension(inputs[0])}_merged.pdf";
            string outPath = job.Stage(name);
            WriteMergedPdf(outPath, OrderPages(inputs, context.Parameters), context);
            ConversionEngine.Validate(outPath, "PDF");
            output.Commit(job, outPath, name);
            return Task.FromResult(output.Complete());
        }
        catch (Exception ex)
        {
            return Task.FromResult(output.Fail(Loc.F("Errore Unione PDF: {0}", "PDF merge error: {0}", ex.Message)));
        }
    }

    internal static string[] OrderPages(string[] sources, IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("natural_sort", out var raw) || !bool.TryParse(raw, out bool enabled) || !enabled)
            return sources;
        var comparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);
        return sources.OrderBy(path => Path.GetFileName(path), comparer).ToArray();
    }

    private readonly record struct PageSizePt(float Width, float Height);

    private static readonly PageSizePt A4Portrait = new(595.28f, 841.89f);
    private static readonly PageSizePt A4Landscape = new(841.89f, 595.28f);
    private static readonly PageSizePt A3Portrait = new(841.89f, 1190.55f);
    private static readonly PageSizePt A3Landscape = new(1190.55f, 841.89f);

    private static PageSizePt ParsePageSize(string? value) => value?.ToLowerInvariant() switch
    {
        "a4p" => A4Portrait,
        "a4l" => A4Landscape,
        "a3p" => A3Portrait,
        "a3l" => A3Landscape,
        _ => A4Portrait
    };

    private static List<PageSizePt> ReadPdfPageSizes(string path)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var sizes = new List<PageSizePt>(pdf.NumberOfPages);
        foreach (var page in pdf.GetPages())
            // Page.Width/Height sono già orientati secondo /Rotate e CropBox.
            sizes.Add(new((float)page.Width, (float)page.Height));
        return sizes;
    }

    private static void WriteMergedPdf(string outPath, string[] inputs, QuickToolContext context)
    {
        using var stream = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var document = SKDocument.CreatePdf(stream) ?? throw new IOException(Loc.T("Encoder PDF non disponibile.", "PDF encoder unavailable."));

        string pageSize = context.Parameters.GetValueOrDefault("page_size", "original");
        bool preserveOriginal = string.Equals(pageSize, "original", StringComparison.OrdinalIgnoreCase);
        var forced = preserveOriginal ? A4Portrait : ParsePageSize(pageSize);

        for (int i = 0; i < inputs.Length; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            string path = inputs[i];

            string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            if (ext == "pdf")
            {
                // Ogni pagina PDF mantiene le proprie dimensioni e il proprio orientamento.
                byte[] pdfBytes = File.ReadAllBytes(path);
                var sizes = preserveOriginal ? ReadPdfPageSizes(path) : [];
                var images = PDFtoImage.Conversion.ToImages(pdfBytes, options: new PDFtoImage.RenderOptions(Dpi: 144));
                int pageIndex = 0;
                foreach (var bmp in images)
                {
                    using (bmp)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        var size = preserveOriginal
                            ? (pageIndex < sizes.Count
                                ? sizes[pageIndex]
                                : new PageSizePt(bmp.Width * 72f / 144f, bmp.Height * 72f / 144f))
                            : forced;
                        pageIndex++;
                        DrawImagePage(document, bmp, size.Width, size.Height, 0);
                    }
                }
            }
            else if (ext is "png" or "jpg" or "jpeg" or "webp" or "bmp" or "gif" or "tiff")
            {
                byte[] imgBytes = File.ReadAllBytes(path);
                using var bmp = SKBitmap.Decode(imgBytes)
                    ?? throw new IOException(Loc.F("Immagine non decodificabile: {0}", "Unable to decode image: {0}", path));
                var size = preserveOriginal ? A4Portrait : forced;
                DrawImagePage(document, bmp, size.Width, size.Height, 30);
            }
            else if (ext is "txt" or "md" or "csv")
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                var size = preserveOriginal ? A4Portrait : forced;
                DrawTextPages(document, text, size.Width, size.Height, 30);
            }

            else
            {
                throw new IOException(Loc.F("Formato non supportato per l'unione PDF: {0}", "Unsupported format for PDF merge: {0}", ext));
            }

            context.Progress?.Report((double)(i + 1) / inputs.Length);
        }

        document.Close();
        stream.Flush(true);
    }

    private static void DrawImagePage(SKDocument document, SKBitmap bitmap, float pageW, float pageH, float margin)
    {
        var canvas = document.BeginPage(pageW, pageH);
        canvas.Clear(SKColors.White);

        float scale = Math.Min((pageW - 2 * margin) / bitmap.Width, (pageH - 2 * margin) / bitmap.Height);
        float w = bitmap.Width * scale, h = bitmap.Height * scale;

        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image,
            new SKRect((pageW - w) / 2, (pageH - h) / 2, (pageW + w) / 2, (pageH + h) / 2),
            new SKSamplingOptions(SKFilterMode.Linear));

        document.EndPage();
    }

    private static void DrawTextPages(SKDocument document, string text, float pageW, float pageH, float margin)
    {
        using var typeface = SKTypeface.FromFamilyName("Consolas");
        using var font = new SKFont(typeface, 10);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        var canvas = document.BeginPage(pageW, pageH);
        canvas.Clear(SKColors.White);
        float y = margin + 15;

        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (y > pageH - margin)
            {
                document.EndPage();
                canvas = document.BeginPage(pageW, pageH);
                canvas.Clear(SKColors.White);
                y = margin + 15;
            }
            canvas.DrawText(line, margin, y, SKTextAlign.Left, font, paint);
            y += 14;
        }

        document.EndPage();
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  4. PACK ZIP QUICK TOOL
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>Helper condiviso dagli strumenti di archiviazione (ZIP e RAR).</summary>
internal static class ArchiveQuickToolSupport
{
    internal readonly record struct Entry(string Path, string EntryName);

    /// <summary>Espande le cartelle in file, conservando le sottocartelle nel nome dell'entry.</summary>
    internal static List<Entry> Expand(QuickToolContext context)
    {
        var entries = new List<Entry>();
        foreach (string input in context.InputFilePaths)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(input))
            {
                string root = Path.GetFullPath(input);
                string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    entries.Add(new(file, Path.Combine(folder, Path.GetRelativePath(root, file)).Replace('\\', '/')));
            }
            else if (File.Exists(input))
            {
                entries.Add(new(input, Path.GetFileName(input)));
            }
        }
        return entries;
    }

    /// <summary>Per una cartella selezionata l'archivio va accanto alla cartella, non dentro.</summary>
    internal static string? DestinationDirectoryFor(QuickToolContext context)
    {
        foreach (string input in context.InputFilePaths)
        {
            try
            {
                if (!Directory.Exists(input)) continue;
                string folder = Path.GetFullPath(input);
                string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
                return string.IsNullOrWhiteSpace(parent) ? null : parent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
        return null;
    }

    internal static string ArchiveLabel(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0) return "archive";
        string first = inputs[0];
        try
        {
            if (Directory.Exists(first))
                return Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)));
            return Path.GetFileNameWithoutExtension(first);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "archive";
        }
    }
}

/// <summary>
/// Comprime tutti i file selezionati in un archivio ZIP pulito.
/// </summary>
public sealed class PackZipQuickTool : IQuickTool
{
    public string Id => "pack_zip";
    public string DisplayName => Loc.T("Impacchetta in ZIP", "Pack to ZIP");
    public string IconGlyph => "\uF012"; // Package / Archive
    public bool SupportsMultipleFiles => true;

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        return inputExtensions.Count > 0;
    }

    public Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.InputFilePaths.Count == 0)
            return Task.FromResult(new QuickToolResult(false, [], Loc.T("Nessun file fornito per la compressione ZIP.", "No files provided for ZIP archive.")));

        QuickToolOutput? output = null;
        try
        {
            var entries = ArchiveQuickToolSupport.Expand(context);
            if (entries.Count == 0)
                return Task.FromResult(new QuickToolResult(false, [], Loc.T("Nessun file trovato da comprimere.", "No files found to compress.")));

            var entryByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                entryByPath[Path.GetFullPath(entry.Path)] = entry.EntryName;

            var expanded = new QuickToolContext
            {
                InputFilePaths = entries.Select(e => e.Path).ToArray(),
                Settings = context.Settings,
                Parameters = context.Parameters,
                Progress = context.Progress,
                CancellationToken = context.CancellationToken
            };
            output = new QuickToolOutput(expanded);

            string[] inputs = output.Sources.ToArray();
            using var job = output.CreateJob(inputs);
            string name = $"{ArchiveQuickToolSupport.ArchiveLabel(context.InputFilePaths)}_bundle.zip";
            string outPath = job.Stage(name);
            using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < inputs.Length; i++)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    string path = inputs[i];
                    string entryName = entryByPath.TryGetValue(path, out var mapped) ? mapped : Path.GetFileName(path);
                    string stem = Path.GetFileNameWithoutExtension(entryName);
                    string ext = Path.GetExtension(entryName);
                    for (int suffix = 1; !entryNames.Add(entryName); suffix++)
                        entryName = $"{stem}_{suffix}{ext}";

                    zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
                    context.Progress?.Report((double)(i + 1) / inputs.Length);
                }
            }

            output.Commit(job, outPath, name, destinationDirectory: ArchiveQuickToolSupport.DestinationDirectoryFor(context));
            return Task.FromResult(output.Complete());
        }
        catch (Exception ex)
        {
            var result = output?.Fail(Loc.F("Errore compressione ZIP: {0}", "ZIP compression error: {0}", ex.Message))
                ?? new QuickToolResult(false, [], Loc.F("Errore compressione ZIP: {0}", "ZIP compression error: {0}", ex.Message));
            return Task.FromResult(result);
        }
    }
}

/// <summary>
/// Comprime i file selezionati in RAR usando WinRAR, se installato.
/// </summary>
public sealed class PackRarQuickTool : IQuickTool
{
    public string Id => "pack_rar";
    public string DisplayName => "RAR";
    public string IconGlyph => "\uF012"; // Package / Archive
    public bool SupportsMultipleFiles => true;

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return CliResolver.Resolve("winrar", settings) != null;
    }

    public async Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.InputFilePaths.Count == 0)
            return new QuickToolResult(false, [], Loc.T("Nessun file fornito per la compressione RAR.", "No files provided for RAR archive."));

        QuickToolOutput? output = null;
        try
        {
            var entries = ArchiveQuickToolSupport.Expand(context);
            if (entries.Count == 0)
                return new QuickToolResult(false, [], Loc.T("Nessun file trovato da comprimere.", "No files found to compress."));

            string? executable = CliResolver.Resolve("winrar", context.Settings);
            if (executable == null)
                throw new IOException(Loc.T("WinRAR non è installato.", "WinRAR is not installed."));

            var expanded = new QuickToolContext
            {
                InputFilePaths = entries.Select(e => e.Path).ToArray(),
                Settings = context.Settings,
                Parameters = context.Parameters,
                Progress = context.Progress,
                CancellationToken = context.CancellationToken
            };
            output = new QuickToolOutput(expanded);

            string[] inputs = output.Sources.ToArray();
            using var job = output.CreateJob(inputs);
            string name = $"{ArchiveQuickToolSupport.ArchiveLabel(context.InputFilePaths)}_bundle.rar";
            string outPath = job.Stage(name);

            var args = new List<string> { "a", "-ep1", "-r", "-y", outPath };
            args.AddRange(context.InputFilePaths.Select(Path.GetFullPath));
            var result = await CliResolver.RunProcessAsync(executable, args,
                cancellationToken: context.CancellationToken, progress: context.Progress).ConfigureAwait(false);
            string detail = string.Join(" ", new[] { result.StdErr, result.StdOut }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            if (detail.Length > 300) detail = detail[..300];
            if (result.ExitCode > 1 || !File.Exists(outPath) || new FileInfo(outPath).Length == 0)
                throw new IOException(string.IsNullOrWhiteSpace(detail)
                    ? Loc.F("WinRAR ha restituito il codice {0}.", "WinRAR exited with code {0}.", result.ExitCode)
                    : detail);

            context.Progress?.Report(1);
            output.Commit(job, outPath, name, destinationDirectory: ArchiveQuickToolSupport.DestinationDirectoryFor(context));
            return output.Complete();
        }
        catch (Exception ex)
        {
            return (output?.Fail(Loc.F("Errore compressione RAR: {0}", "RAR compression error: {0}", ex.Message))
                ?? new QuickToolResult(false, [], Loc.F("Errore compressione RAR: {0}", "RAR compression error: {0}", ex.Message)));
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  5. TARGET SIZE QUICK TOOL
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Comprime immagini o video per rientrare sotto una soglia specifica di megabyte (es. 8 MB per Discord/Email, 25 MB o 50 MB).
/// </summary>
public sealed class TargetSizeQuickTool : IQuickTool
{
    public string Id => "target_size";
    public string DisplayName => Loc.T("Riduci a Dimensione (MB)", "Compress to Size (MB)");
    public string IconGlyph => "\uE896"; // Compress
    public bool SupportsMultipleFiles => false;

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff"
    };

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "webm", "mov", "avi", "wmv", "flv"
    };

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => ImageExts.Contains(e.TrimStart('.')) || VideoExts.Contains(e.TrimStart('.')));
    }

    public async Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            string mbStr = context.Parameters.GetValueOrDefault("target_mb", "8");
            if (!double.TryParse(mbStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double targetMb)
                || !double.IsFinite(targetMb) || targetMb <= 0 || targetMb >= long.MaxValue / (1024.0 * 1024.0))
                targetMb = 8.0;

            long targetBytes = (long)(targetMb * 1024 * 1024);

            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                string ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
                using var job = output.CreateJob(inputPath);
                string sizeLabel = targetMb.ToString("0.#", CultureInfo.InvariantCulture);
                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_{sizeLabel}mb.{ext}";
                string outPath = job.Stage(name);

                if (ImageExts.Contains(ext))
                {
                    CompressImageToTarget(inputPath, outPath, ext, targetBytes, context.CancellationToken);
                }
                else if (VideoExts.Contains(ext))
                {
                    // Video via FFmpeg engine
                    var ffmpeg = new FfmpegEngine();
                    if (!ffmpeg.IsAvailable(context.Settings))
                        throw new IOException(Loc.T("Per la compressione video serve FFmpeg installato o configurato.", "FFmpeg must be installed or configured for video compression."));

                    var convCtx = new ConversionContext
                    {
                        InputFilePath = inputPath,
                        OutputFilePath = outPath,
                        SourceExtension = ext,
                        TargetExtension = ext,
                        Settings = context.Settings,
                        Progress = context.Progress,
                        CancellationToken = context.CancellationToken
                    };
                    convCtx.CustomParameters["target_mb"] = targetMb.ToString(CultureInfo.InvariantCulture);

                    var res = await ffmpeg.ConvertAsync(convCtx).ConfigureAwait(false);
                    if (!res.Success) throw new IOException(res.ErrorMessage ?? Loc.T("Compressione video fallita.", "Video compression failed."));
                }
                else
                {
                    throw new IOException(Loc.F("Formato non supportato per la compressione: {0}", "Unsupported format for compression: {0}", ext));
                }

                if (new FileInfo(outPath).Length > targetBytes)
                    throw new IOException(Loc.F("Impossibile rientrare nella soglia di {0} MB.", "Unable to fit under the {0} MB limit.", sizeLabel));
                output.Commit(job, outPath, name);
            }

            return output.Complete();
        }
        catch (Exception ex)
        {
            return output.Fail(ex.Message);
        }
    }

    private static void CompressImageToTarget(string inputPath, string outputPath, string ext, long targetBytes, CancellationToken token) =>
        BuiltInQuickTools.CompressImageToTargetBytes(inputPath, outputPath, ext, targetBytes, token);
}

// ══════════════════════════════════════════════════════════════════════════════
//  6. REMOVE BG QUICK TOOL (IS-NET ONNX LOCALE)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Strumento rapido per la rimozione dello sfondo con modello AI locale IS-Net via ONNX Runtime.
/// Non utilizza alcuna euristica colore di ripiego: richiede il modello <c>.onnx</c> locale.
/// </summary>
public sealed class RemoveBgQuickTool : IQuickTool
{
    public string Id => "remove_bg";
    public string DisplayName => Loc.T("Rimuovi Sfondo AI", "AI Remove Background");
    public string IconGlyph => "\uEE3F"; // Cutout
    public bool SupportsMultipleFiles => false;

    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "tiff", "tif"
    };

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => SupportedExts.Contains(e.TrimStart('.')));
    }

    public async Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            string? modelPath = OnnxRemoveBgEngine.ResolveModelPath(context.Settings);
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return output.Fail(OnnxRemoveBgEngine.MissingModelMessage);
            }

            int total = output.Sources.Count;
            for (int i = 0; i < total; i++)
            {
                string inputPath = output.Sources[i];
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Progress?.Report(total > 0 ? (double)i / total : 0.0);

                using var job = output.CreateJob(inputPath);
                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_nobg.png";
                string outPath = job.Stage(name);

                await Task.Run(() =>
                {
                    using var orig = BuiltInQuickTools.LoadOrientedBitmap(inputPath);
                    using var cutout = OnnxRemoveBgEngine.RemoveBackground(orig, modelPath, context.CancellationToken);
                    using var data = cutout.Encode(SKEncodedImageFormat.Png, 100)
                        ?? throw new IOException(Loc.T("Encoding PNG trasparente fallito.", "Transparent PNG encoding failed."));
                    using var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    data.SaveTo(fs);
                    fs.Flush(true);
                }, context.CancellationToken).ConfigureAwait(false);

                output.Commit(job, outPath, name);
                context.Progress?.Report(total > 0 ? (double)(i + 1) / total : 1.0);
            }

            return output.Complete();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return output.Fail(ex.Message);
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  7. OCR / EXTRACT TEXT QUICK TOOL
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Strumento rapido per estrarre testo da PDF o immagini salvandolo in formato TXT o Markdown.
/// </summary>
public sealed class OcrQuickTool : IQuickTool
{
    public string Id => "ocr";
    public string DisplayName => Loc.T("Estrai Testo (OCR / Testo Nativo)", "Extract Text (OCR / Native Text)");
    public string IconGlyph => "\uE8D3"; // Text extraction
    public bool SupportsMultipleFiles => false;

    private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "tiff", "tif"
    };

    private readonly object probeLock = new();
    private (string Path, string? DataDirectory) lastProbe;
    private long probeExpires;
    private string? availableLanguage;

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        var extensions = inputExtensions.Select(e => e.TrimStart('.')).ToArray();
        if (!extensions.All(e => IsPdf(e) || RasterExtensions.Contains(e))) return false;
        return extensions.All(IsPdf) || FindTesseract(settings) != null;
    }

    public async Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            string outFmt = context.Parameters.GetValueOrDefault("format", "txt").Trim().ToLowerInvariant();
            if (outFmt != "md") outFmt = "txt";

            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                string ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
                using var job = output.CreateJob(inputPath);
                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_extracted.{outFmt}";
                string outPath = job.Stage(name);

                string extractedText = IsPdf(ext)
                    ? ExtractPdfText(inputPath, outFmt == "md", context.CancellationToken)
                    : await RecognizeImageAsync(inputPath, ext, job, context).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(extractedText))
                    throw new IOException(Loc.F("Nessun testo estraibile trovato in {0}.", "No extractable text found in {0}.", Path.GetFileName(inputPath)));

                await File.WriteAllTextAsync(outPath, extractedText, new UTF8Encoding(false), context.CancellationToken)
                    .ConfigureAwait(false);
                output.Commit(job, outPath, name, allowRecycle: false);
            }

            return output.Complete();
        }
        catch (Exception ex)
        {
            return output.Fail(ex.Message);
        }
    }

    private static bool IsPdf(string extension) => extension.Equals("pdf", StringComparison.OrdinalIgnoreCase);

    private static string ExtractPdfText(string path, bool markdown, CancellationToken cancellationToken)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var text = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string pageText = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);
            if (string.IsNullOrWhiteSpace(pageText)) continue;
            if (markdown) text.AppendLine($"## Pagina {page.Number}").AppendLine();
            text.AppendLine(pageText).AppendLine();
        }
        return text.ToString().Trim();
    }

    private (string Path, string Language)? FindTesseract(FlipSettings settings)
    {
        string? executable = CliResolver.Resolve("tesseract", settings);
        if (executable == null) return null;
        var probeKey = (Path: Path.GetFullPath(executable), DataDirectory: Environment.GetEnvironmentVariable("TESSDATA_PREFIX"));
        lock (probeLock)
        {
            if (lastProbe != probeKey || Environment.TickCount64 >= probeExpires)
            {
                availableLanguage = ProbeLanguageAsync(probeKey.Path, CultureInfo.CurrentUICulture.ThreeLetterISOLanguageName)
                    .GetAwaiter().GetResult();
                lastProbe = probeKey;
                probeExpires = Environment.TickCount64 + 30_000;
            }

            return availableLanguage == null ? null : (probeKey.Path, availableLanguage);
        }
    }

    private static async Task<string?> ProbeLanguageAsync(string executable, string preferred)
    {
        try
        {
            var version = await CliResolver.RunProcessAsync(executable, ["--version"], timeoutSeconds: 2)
                .ConfigureAwait(false);
            if (version.ExitCode != 0 || !version.StdOut.StartsWith("tesseract ", StringComparison.OrdinalIgnoreCase))
                return null;

            var languages = await CliResolver.RunProcessAsync(executable, ["--list-langs"], timeoutSeconds: 2)
                .ConfigureAwait(false);
            if (languages.ExitCode != 0 || !languages.StdOut.StartsWith("List of available languages", StringComparison.Ordinal))
                return null;

            string[] installed = languages.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Where(lang => lang is not ("osd" or "equ"))
                .ToArray();
            string? language = installed.FirstOrDefault(lang => lang == preferred)
                ?? installed.FirstOrDefault(lang => lang == "eng")
                ?? installed.FirstOrDefault();
            if (language == null) return null;

            // Listing the model files does not prove that Tesseract can initialize one.
            var ready = await CliResolver.RunProcessAsync(executable, ["-l", language, "--print-parameters"], timeoutSeconds: 2)
                .ConfigureAwait(false);
            return ready.ExitCode == 0 && ready.StdOut.StartsWith("Tesseract parameters:", StringComparison.Ordinal)
                ? language : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                   or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<string> RecognizeImageAsync(string inputPath, string extension, OutputPathAllocator.Job job,
        QuickToolContext context)
    {
        if (!RasterExtensions.Contains(extension))
            throw new NotSupportedException(Loc.F("Formato OCR non supportato: {0}.", "Unsupported OCR format: {0}.", extension));
        var engine = FindTesseract(context.Settings)
            ?? throw new IOException(Loc.T("Tesseract non è disponibile o non ha dati di lingua per l'OCR.", "Tesseract is unavailable or missing language data for OCR."));

        string imagePath = inputPath;
        if (extension == "webp")
        {
            imagePath = job.Stage("ocr-input.png");
            using var bitmap = ConversionEngine.ReadImage(inputPath);
            ConversionEngine.WriteImage(bitmap, imagePath, "PNG");
        }

        string outputBase = job.Stage("ocr-result");
        var result = await CliResolver.RunProcessAsync(engine.Path, [imagePath, outputBase, "-l", engine.Language],
            cancellationToken: context.CancellationToken, timeoutSeconds: 120).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException(Loc.F("Tesseract non è riuscito a leggere l'immagine: {0}", "Tesseract failed to read the image: {0}", result.StdErr.Trim()));

        string textPath = outputBase + ".txt";
        return File.Exists(textPath)
            ? (await File.ReadAllTextAsync(textPath, new UTF8Encoding(false, true), context.CancellationToken)
                .ConfigureAwait(false)).Trim()
            : "";
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  8. PALETTE #HEX QUICK TOOL
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Estrae i 6 colori dominanti distinti dall'immagine e genera una Swatch Card .png ad alta risoluzione
/// con i codici esadecimali #RRGGBB stampati sotto ogni campione.
/// </summary>
public sealed class PaletteQuickTool : IQuickTool
{
    public string Id => "palette";
    public string DisplayName => "Palette #HEX";
    public string IconGlyph => "\uE790"; // Color / Palette
    public bool SupportsMultipleFiles => false;

    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "tif"
    };

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => SupportedExts.Contains(e.TrimStart('.')));
    }

    public Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var job = output.CreateJob(inputPath);

                using var orig = BuiltInQuickTools.LoadOrientedBitmap(inputPath);

                var palette = ExtractDominantColors(orig, 6);
                using var card = RenderSwatchCard(orig, palette, Path.GetFileName(inputPath));

                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_palette.png";
                string outPath = job.Stage(name);

                using var data = card.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new IOException(Loc.T("Encoding Swatch Card PNG fallito.", "Swatch Card PNG encoding failed."));
                using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    data.SaveTo(fs);
                }

                output.Commit(job, outPath, name, allowRecycle: false);
            }

            return Task.FromResult(output.Complete());
        }
        catch (Exception ex)
        {
            return Task.FromResult(output.Fail(ex.Message));
        }
    }

    internal static IReadOnlyList<string> ExtractHexPalette(SKBitmap bmp, int count = 6) =>
        ExtractDominantColors(bmp, count)
            .Select(c => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}")
            .ToArray();

    internal static List<SKColor> ExtractDominantColors(SKBitmap bmp, int count = 6)
    {
        var buckets = new Dictionary<int, (long SumR, long SumG, long SumB, int Count)>();
        var info = bmp.Info;
        bool bgra = info.ColorType == SKColorType.Bgra8888;
        bool packed = bgra || info.ColorType == SKColorType.Rgba8888;
        using var other = packed ? null : bmp.PeekPixels();
        var pixels = MemoryMarshal.Cast<byte, uint>(bmp.GetPixelSpan());
        int stride = bmp.RowBytes / 4;
        int stepX = Math.Max(1, bmp.Width / 200);
        int stepY = Math.Max(1, bmp.Height / 200);

        for (int y = 0; y < bmp.Height; y += stepY)
        {
            for (int x = 0; x < bmp.Width; x += stepX)
            {
                SKColor px;
                if (packed)
                {
                    uint value = pixels[y * stride + x];
                    if (!bgra) value = (value & 0xff00ff00) | ((value & 0xff) << 16) | ((value >> 16) & 0xff);
                    px = info.AlphaType == SKAlphaType.Premul && (value >> 24) != 255
                        ? (SKColor)(SKPMColor)value : (SKColor)value;
                }
                else px = other!.GetPixelColor(x, y);
                if (px.Alpha < 32) continue;

                int qr = px.Red >> 4;
                int qg = px.Green >> 4;
                int qb = px.Blue >> 4;
                int key = (qr << 8) | (qg << 4) | qb;

                if (buckets.TryGetValue(key, out var acc))
                    buckets[key] = (acc.SumR + px.Red, acc.SumG + px.Green, acc.SumB + px.Blue, acc.Count + 1);
                else
                    buckets[key] = (px.Red, px.Green, px.Blue, 1);
            }
        }

        var candidates = buckets.Values
            .OrderByDescending(b => b.Count)
            .Select(b => new SKColor(
                (byte)Math.Clamp(b.SumR / b.Count, 0, 255),
                (byte)Math.Clamp(b.SumG / b.Count, 0, 255),
                (byte)Math.Clamp(b.SumB / b.Count, 0, 255)))
            .ToList();

        var selected = new List<SKColor>(count);
        float[] thresholds = [52f, 28f, 12f, 1f];

        foreach (float minDistance in thresholds)
        {
            foreach (var c in candidates)
            {
                if (selected.Count >= count) break;
                bool distinct = selected.All(existing => ColorDistance(existing, c) >= minDistance);
                if (distinct) selected.Add(c);
            }
            if (selected.Count >= count) break;
        }

        // Fallback armonico se l'immagine ha meno di 6 tinte distinte
        int seed = 0;
        while (selected.Count < count)
        {
            var baseCol = selected.Count > 0 ? selected[0] : new SKColor(255, 122, 24);
            seed++;
            byte r = (byte)((baseCol.Red + seed * 37) % 256);
            byte g = (byte)((baseCol.Green + seed * 59) % 256);
            byte b = (byte)((baseCol.Blue + seed * 83) % 256);
            var fallback = new SKColor(r, g, b);
            if (selected.All(e => e != fallback))
                selected.Add(fallback);
        }

        return selected;
    }

    private static float ColorDistance(SKColor a, SKColor b)
    {
        float dr = a.Red - b.Red;
        float dg = a.Green - b.Green;
        float db = a.Blue - b.Blue;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static SKBitmap RenderSwatchCard(SKBitmap orig, List<SKColor> palette, string fileName)
    {
        const int width = 1200;
        const int height = 960;
        var card = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(card);
        canvas.Clear(new SKColor(18, 19, 24));

        // Cornice superiore per l'immagine originale
        const float pad = 36f;
        const float previewBoxH = 620f;
        var previewFrame = SKRect.Create(pad, pad, width - pad * 2, previewBoxH);

        using (var frameBg = new SKPaint { Color = new SKColor(26, 29, 38), Style = SKPaintStyle.Fill, IsAntialias = true })
        using (var frameBorder = new SKPaint { Color = new SKColor(46, 51, 66), Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true })
        {
            canvas.DrawRoundRect(previewFrame, 16f, 16f, frameBg);
            canvas.DrawRoundRect(previewFrame, 16f, 16f, frameBorder);
        }

        float innerMargin = 24f;
        float availW = previewFrame.Width - innerMargin * 2;
        float availH = previewFrame.Height - innerMargin * 2;
        float scale = Math.Min(availW / orig.Width, availH / orig.Height);
        float drawW = orig.Width * scale;
        float drawH = orig.Height * scale;
        var imgRect = SKRect.Create(
            previewFrame.MidX - drawW / 2f,
            previewFrame.MidY - drawH / 2f,
            drawW,
            drawH);

        using (var img = SKImage.FromBitmap(orig))
        {
            canvas.DrawImage(img, imgRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        // Fascia inferiore con i 6 campioni colore arrotondati e codice #RRGGBB
        float swatchTop = previewFrame.Bottom + 28f;
        float swatchAreaW = width - pad * 2;
        float gap = 18f;
        int n = palette.Count;
        float swatchW = (swatchAreaW - gap * (n - 1)) / n;
        float swatchH = 165f;

        using var hexFont = new SKFont(SKTypeface.FromFamilyName("Consolas", SKFontStyle.Bold), 22);
        using var subFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 14);
        using var textPaint = new SKPaint { Color = new SKColor(243, 244, 246), IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = new SKColor(255, 122, 24), IsAntialias = true };
        using var swatchBorder = new SKPaint { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

        for (int i = 0; i < n; i++)
        {
            float sx = pad + i * (swatchW + gap);
            var swatchRect = SKRect.Create(sx, swatchTop, swatchW, swatchH);
            using var fillPaint = new SKPaint { Color = palette[i], Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawRoundRect(swatchRect, 14f, 14f, fillPaint);
            canvas.DrawRoundRect(swatchRect, 14f, 14f, swatchBorder);

            string hex = $"#{palette[i].Red:X2}{palette[i].Green:X2}{palette[i].Blue:X2}";
            canvas.DrawText(hex, swatchRect.MidX, swatchRect.Bottom + 34f, SKTextAlign.Center, hexFont, textPaint);
        }

        canvas.DrawText($"FLIP PALETTE #HEX  ·  {fileName}", width / 2f, height - 18f, SKTextAlign.Center, subFont, mutedPaint);
        canvas.Flush();
        return card;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  9. PRIVACY CLEAN QUICK TOOL (ZERO METADATI)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Rimuove al 100% i metadati EXIF, GPS, XMP, fotocamera, autore e commenti da immagini e PDF.
/// </summary>
public sealed class PrivacyCleanQuickTool : IQuickTool
{
    public string Id => "clean_meta";
    public string DisplayName => Loc.T("Zero Metadati", "Strip Metadata");
    public string IconGlyph => "\uE72E"; // Shield / Lock
    public bool SupportsMultipleFiles => false;

    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "webp", "bmp", "gif", "tiff", "tif", "pdf"
    };

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => SupportedExts.Contains(e.TrimStart('.')));
    }

    public Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var job = output.CreateJob(inputPath);

                string ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_clean.{ext}";
                string outPath = job.Stage(name);

                if (ext == "pdf")
                {
                    SanitizePdfMetadata(inputPath, outPath, context.CancellationToken);
                }
                else
                {
                    using var decoded = ConversionEngine.ReadImage(inputPath);
                    using var cleanPixels = new SKBitmap(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using (var canvas = new SKCanvas(cleanPixels))
                    {
                        canvas.Clear(SKColors.Transparent);
                        using var img = SKImage.FromBitmap(decoded);
                        canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
                        canvas.Flush();
                    }
                    ConversionEngine.WriteImage(cleanPixels, outPath, QuickToolOutput.ImageFormat(ext));
                }

                output.Commit(job, outPath, name);
            }

            return Task.FromResult(output.Complete());
        }
        catch (Exception ex)
        {
            return Task.FromResult(output.Fail(ex.Message));
        }
    }

    private static void SanitizePdfMetadata(string inputPath, string outputPath, CancellationToken token)
    {
        byte[] pdfBytes = File.ReadAllBytes(inputPath);
        var metadata = new SKDocumentPdfMetadata
        {
            Title = "",
            Author = "",
            Subject = "",
            Keywords = "",
            Creator = "",
            Producer = ""
        };

        using var outStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var doc = SKDocument.CreatePdf(outStream, metadata)
            ?? throw new IOException(Loc.T("Encoder PDF non disponibile.", "PDF encoder unavailable."));

        var pages = PDFtoImage.Conversion.ToImages(pdfBytes, options: new PDFtoImage.RenderOptions(Dpi: 150, WithAspectRatio: true));
        foreach (var pageBmp in pages)
        {
            using (pageBmp)
            {
                token.ThrowIfCancellationRequested();
                var canvas = doc.BeginPage(pageBmp.Width, pageBmp.Height);
                canvas.Clear(SKColors.White);
                using var img = SKImage.FromBitmap(pageBmp);
                canvas.DrawImage(img, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
                doc.EndPage();
            }
        }

        doc.Close();
        outStream.Flush(true);
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  10. WAVEFORM QUICK TOOL (ONDA SONORA)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Genera un'immagine .png (1920x480) su sfondo trasparente con l'Onda Sonora simmetrica
/// sfumata in arancione FLIP (#FF7A18 -> #FFB347) pronta per il video editing.
/// </summary>
public sealed class WaveformQuickTool : IQuickTool
{
    public string Id => "waveform";
    public string DisplayName => Loc.T("Onda Sonora", "Waveform");
    public string IconGlyph => "\uE9D5"; // Waveform
    public bool SupportsMultipleFiles => false;

    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "wav", "mp3", "flac", "m4a", "ogg", "aac", "opus"
    };

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        var exts = inputExtensions.Select(e => e.TrimStart('.').ToLowerInvariant()).ToArray();
        if (!exts.All(SupportedExts.Contains)) return false;
        return exts.All(e => e == "wav") || CliResolver.Resolve("ffmpeg", settings) != null;
    }

    public async Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            const int barCount = 160;
            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var job = output.CreateJob(inputPath);

                string ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
                string wavPath = inputPath;
                if (ext != "wav")
                {
                    string? ffmpeg = CliResolver.Resolve("ffmpeg", context.Settings)
                        ?? throw new IOException(Loc.T("Per generare l'onda sonora da formati compressi è necessario FFmpeg.", "FFmpeg is required to generate waveforms from compressed audio."));
                    wavPath = job.Stage("decoded_audio.wav");
                    var proc = await CliResolver.RunProcessAsync(
                        ffmpeg,
                        ["-y", "-i", inputPath, "-ac", "1", "-ar", "22050", "-sample_fmt", "s16", wavPath],
                        cancellationToken: context.CancellationToken,
                        timeoutSeconds: 120).ConfigureAwait(false);
                    if (proc.ExitCode != 0 || !File.Exists(wavPath))
                        throw new IOException(Loc.F("Decodifica audio fallita: {0}", "Audio decoding failed: {0}", proc.StdErr.Trim()));
                }

                float[] peaks = ExtractWavPeaks(wavPath, barCount);
                using var waveformBmp = RenderWaveformPng(peaks);

                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_waveform.png";
                string outPath = job.Stage(name);

                using var data = waveformBmp.Encode(SKEncodedImageFormat.Png, 100)
                    ?? throw new IOException(Loc.T("Encoding PNG onda sonora fallito.", "Waveform PNG encoding failed."));
                using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    data.SaveTo(fs);
                }

                output.Commit(job, outPath, name, allowRecycle: false);
            }

            return output.Complete();
        }
        catch (Exception ex)
        {
            return output.Fail(ex.Message);
        }
    }

    private static float[] ExtractWavPeaks(string wavPath, int barCount)
    {
        byte[] bytes = File.ReadAllBytes(wavPath);
        if (bytes.Length < 44 ||
            Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            throw new IOException(Loc.T("File WAV non valido: intestazione RIFF/WAVE assente.", "Invalid WAV file: missing RIFF/WAVE header."));
        }

        int audioFormat = 1;
        int channels = 1;
        int bitsPerSample = 16;
        int dataOffset = -1;
        int dataLength = 0;

        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, pos, 4);
            int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
            pos += 8;
            if (chunkSize < 0 || pos + chunkSize > bytes.Length) break;

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BitConverter.ToUInt16(bytes, pos);
                channels = Math.Max(1, (int)BitConverter.ToUInt16(bytes, pos + 2));
                bitsPerSample = BitConverter.ToUInt16(bytes, pos + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = pos;
                dataLength = chunkSize;
                break;
            }

            pos += chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0 || dataLength <= 0)
            throw new IOException(Loc.T("File WAV privo di chunk 'data'.", "WAV file has no 'data' chunk."));

        int bytesPerSample = Math.Max(1, bitsPerSample / 8);
        int frameSize = bytesPerSample * channels;
        int totalFrames = dataLength / frameSize;
        if (totalFrames <= 0)
            throw new IOException(Loc.T("File WAV senza campioni audio.", "WAV file contains no audio samples."));

        float ReadSample(int frameIdx)
        {
            int offset = dataOffset + frameIdx * frameSize;
            if (offset + bytesPerSample > bytes.Length) return 0f;
            return (audioFormat, bitsPerSample) switch
            {
                (3, 32) => Math.Clamp(MathF.Abs(BitConverter.ToSingle(bytes, offset)), 0f, 1f),
                (_, 8) => MathF.Abs((bytes[offset] - 128) / 128f),
                (_, 16) => MathF.Abs(BitConverter.ToInt16(bytes, offset) / 32768f),
                (_, 24) => MathF.Abs(((bytes[offset] | (bytes[offset + 1] << 8) | ((sbyte)bytes[offset + 2] << 16))) / 8388608f),
                (_, 32) => MathF.Abs(BitConverter.ToInt32(bytes, offset) / 2147483648f),
                _ => 0f
            };
        }

        var peaks = new float[barCount];
        float maxPeak = 0.001f;

        for (int b = 0; b < barCount; b++)
        {
            int startFrame = (int)((long)b * totalFrames / barCount);
            int endFrame = Math.Max(startFrame + 1, (int)((long)(b + 1) * totalFrames / barCount));
            int step = Math.Max(1, (endFrame - startFrame) / 64);

            float localMax = 0f;
            float sumSq = 0f;
            int count = 0;
            for (int f = startFrame; f < endFrame && f < totalFrames; f += step)
            {
                float s = ReadSample(f);
                if (s > localMax) localMax = s;
                sumSq += s * s;
                count++;
            }

            float rms = count > 0 ? MathF.Sqrt(sumSq / count) : 0f;
            float combined = localMax * 0.65f + rms * 0.35f;
            peaks[b] = combined;
            if (combined > maxPeak) maxPeak = combined;
        }

        for (int b = 0; b < barCount; b++)
        {
            peaks[b] = Math.Clamp(peaks[b] / maxPeak, 0.04f, 1.0f);
        }

        return peaks;
    }

    private static SKBitmap RenderWaveformPng(float[] peaks)
    {
        const int width = 1920;
        const int height = 480;
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);

        const float padX = 48f;
        const float maxHalfH = 204f;
        float centerY = height / 2f;
        float availW = width - padX * 2;
        int n = peaks.Length;
        float stepX = availW / n;
        float barW = Math.Max(3f, stepX * 0.62f);
        float radius = barW / 2f;

        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, centerY - maxHalfH),
            new SKPoint(0, centerY + maxHalfH),
            [new SKColor(255, 179, 71), new SKColor(255, 122, 24), new SKColor(255, 179, 71)],
            [0f, 0.5f, 1f],
            SKShaderTileMode.Clamp);

        using var barPaint = new SKPaint
        {
            Shader = shader,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        for (int i = 0; i < n; i++)
        {
            float halfH = Math.Max(6f, peaks[i] * maxHalfH);
            float x = padX + i * stepX + (stepX - barW) / 2f;
            var rect = SKRect.Create(x, centerY - halfH, barW, halfH * 2f);
            canvas.DrawRoundRect(rect, radius, radius, barPaint);
        }

        canvas.Flush();
        return bmp;
    }
}

// ══════════════════════════════════════════════════════════════════════════════
//  11. 3D TURNTABLE 360° QUICK TOOL (Modello 3D → GIF animata ciclica)
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Strumento rapido che genera una GIF animata 720x720 con il modello 3D in rotazione di 360°:
/// metà Shaded (luce studio lambertiana) e metà Wireframe arancione FLIP, griglia di base e badge poligoni/vertici.
/// </summary>
public sealed class Turntable3DQuickTool : IQuickTool
{
    public string Id => "turntable_3d";
    public string DisplayName => "Turntable 360°";
    public string IconGlyph => ""; // Rotate
    public bool SupportsMultipleFiles => false;

    internal const int DefaultFrames = 24;
    internal const int DefaultSize = 720;
    internal const int DefaultFps = 20;

    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "stl", "fbx", "glb", "gltf", "ply", "3mf", "dae"
    };

    public bool CanHandle(IReadOnlyList<string> inputExtensions, FlipSettings settings)
    {
        if (!settings.EnabledBackSideTools.Contains(Id, StringComparer.OrdinalIgnoreCase)) return false;
        if (inputExtensions.Count == 0) return false;
        return inputExtensions.All(e => SupportedExts.Contains(e.TrimStart('.')));
    }

    public async Task<QuickToolResult> ExecuteAsync(QuickToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var output = new QuickToolOutput(context);

        try
        {
            int frames = ReadInt(context, "frames", DefaultFrames, 8, 120);
            int size = ReadInt(context, "size", DefaultSize, 240, 1080);
            int fps = ReadInt(context, "fps", DefaultFps, 5, 50);
            context.Parameters.TryGetValue("up", out string? upAxis);

            int total = output.Sources.Count, index = 0;
            foreach (string inputPath in output.Sources)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                using var job = output.CreateJob(inputPath);

                string name = $"{Path.GetFileNameWithoutExtension(inputPath)}_turntable.gif";
                string outPath = job.Stage(name);
                int current = index++;

                await Task.Run(() =>
                {
                    var rendered = new int[frames][];
                    Mesh3DVectorGeoEngine.RenderTurntableFrames(
                        inputPath, Path.GetExtension(inputPath), frames, size,
                        (frame, pixels) =>
                        {
                            rendered[frame] = pixels;
                            context.Progress?.Report((current + 0.8 * (frame + 1) / frames) / total);
                        },
                        context.CancellationToken, upAxis);
                    AnimatedGifWriter.Write(outPath, size, size, rendered, fps, context.CancellationToken);
                }, context.CancellationToken).ConfigureAwait(false);

                output.Commit(job, outPath, name, allowRecycle: false);
                context.Progress?.Report((double)index / total);
            }

            return output.Complete();
        }
        catch (OperationCanceledException)
        {
            return output.Fail(Loc.T("Turntable 360° annullato.", "Turntable 360° cancelled."));
        }
        catch (Exception ex)
        {
            return output.Fail(ex.Message);
        }
    }

    private static int ReadInt(QuickToolContext context, string key, int fallback, int min, int max) =>
        context.Parameters.TryGetValue(key, out string? raw) &&
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
}

/// <summary>
/// Encoder GIF89a animato e ciclico: palette globale a 256 colori (median cut) e compressione LZW.
/// </summary>
internal static class AnimatedGifWriter
{
    private const int BinBits = 6;
    private const int BinCount = 1 << (BinBits * 3);

    /// <summary>Scrive i fotogrammi (pixel 0xAARRGGBB, opachi) come GIF in loop infinito.</summary>
    internal static void Write(string path, int width, int height, IReadOnlyList<int[]> frames, int fps, CancellationToken cancellationToken = default)
    {
        if (frames.Count == 0) throw new IOException(Loc.T("Nessun fotogramma da salvare nella GIF.", "No frames to save into GIF."));
        if (width is < 1 or > ushort.MaxValue || height is < 1 or > ushort.MaxValue)
            throw new IOException(Loc.T("Dimensioni GIF non valide.", "Invalid GIF dimensions."));
        if (frames.Any(frame => frame is null || frame.Length != width * height))
            throw new IOException(Loc.T("Fotogramma GIF con dimensioni incoerenti.", "GIF frame has inconsistent dimensions."));

        var (palette, lookup) = BuildPalette(frames);
        // Il ritardo GIF è in centesimi di secondo; sotto i 2 cs molti player rallentano a 10 fps.
        int delay = Math.Clamp((int)Math.Round(100.0 / Math.Clamp(fps, 1, 50)), 2, 100);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("GIF89a"u8);
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((byte)0xF7); // palette globale da 256 colori, 8 bit per canale
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(palette);

        // Estensione NETSCAPE2.0: loop infinito
        writer.Write([0x21, 0xFF, 0x0B]);
        writer.Write("NETSCAPE2.0"u8);
        writer.Write([0x03, 0x01, 0x00, 0x00, 0x00]);

        var indices = new byte[width * height];
        foreach (int[] frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < indices.Length; i++) indices[i] = lookup[Bin(frame[i])];

            writer.Write([0x21, 0xF9, 0x04, 0x04]); // Graphic Control: nessuna trasparenza, non rimuovere
            writer.Write((ushort)delay);
            writer.Write([0x00, 0x00]);
            writer.Write((byte)0x2C);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write((byte)0);
            WriteLzw(writer, indices);
        }

        writer.Write((byte)0x3B);
    }

    private static int Bin(int argb) =>
        (((argb >> 16) & 0xFF) >> (8 - BinBits) << (BinBits * 2)) |
        (((argb >> 8) & 0xFF) >> (8 - BinBits) << BinBits) |
        ((argb & 0xFF) >> (8 - BinBits));

    private sealed class ColorBox(int[] bins, int start, int end)
    {
        public readonly int[] Bins = bins;
        public readonly int Start = start;
        public readonly int End = end; // esclusivo
        public long Population;
        public int Axis;
        public int Range;
    }

    private static (byte[] Palette, byte[] Lookup) BuildPalette(IReadOnlyList<int[]> frames)
    {
        var count = new long[BinCount];
        var sumR = new long[BinCount];
        var sumG = new long[BinCount];
        var sumB = new long[BinCount];
        foreach (int[] frame in frames)
        {
            foreach (int pixel in frame)
            {
                int bin = Bin(pixel);
                count[bin]++;
                sumR[bin] += (pixel >> 16) & 0xFF;
                sumG[bin] += (pixel >> 8) & 0xFF;
                sumB[bin] += pixel & 0xFF;
            }
        }

        int[] used = Enumerable.Range(0, BinCount).Where(bin => count[bin] > 0).ToArray();
        int mask = (1 << BinBits) - 1;
        int Channel(int bin, int axis) => (bin >> (BinBits * (2 - axis))) & mask;

        void Measure(ColorBox box)
        {
            Span<int> min = [int.MaxValue, int.MaxValue, int.MaxValue];
            Span<int> max = [int.MinValue, int.MinValue, int.MinValue];
            box.Population = 0;
            for (int i = box.Start; i < box.End; i++)
            {
                int bin = box.Bins[i];
                box.Population += count[bin];
                for (int axis = 0; axis < 3; axis++)
                {
                    int value = Channel(bin, axis);
                    if (value < min[axis]) min[axis] = value;
                    if (value > max[axis]) max[axis] = value;
                }
            }
            box.Axis = 0;
            box.Range = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                if (max[axis] - min[axis] <= box.Range) continue;
                box.Range = max[axis] - min[axis];
                box.Axis = axis;
            }
        }

        var boxes = new List<ColorBox> { new(used, 0, used.Length) };
        Measure(boxes[0]);
        while (boxes.Count < 256)
        {
            // Divide il box più esteso, pesato per popolazione: preserva anche i colori rari ma distinti (accento arancione).
            ColorBox? target = null;
            double best = 0;
            foreach (var box in boxes)
            {
                if (box.End - box.Start < 2) continue;
                double score = (box.Range + 0.5) * Math.Sqrt(box.Population);
                if (score > best) { best = score; target = box; }
            }
            if (target is null) break;

            int axisToSplit = target.Axis;
            Array.Sort(target.Bins, target.Start, target.End - target.Start,
                Comparer<int>.Create((a, b) => Channel(a, axisToSplit).CompareTo(Channel(b, axisToSplit))));
            long half = target.Population / 2, running = 0;
            int split = target.Start + 1;
            for (int i = target.Start; i < target.End - 1; i++)
            {
                running += count[target.Bins[i]];
                split = i + 1;
                if (running >= half) break;
            }

            var left = new ColorBox(target.Bins, target.Start, split);
            var right = new ColorBox(target.Bins, split, target.End);
            Measure(left);
            Measure(right);
            boxes.Remove(target);
            boxes.Add(left);
            boxes.Add(right);
        }

        var palette = new byte[256 * 3];
        var lookup = new byte[BinCount];
        for (int index = 0; index < boxes.Count; index++)
        {
            var box = boxes[index];
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = box.Start; i < box.End; i++)
            {
                int bin = box.Bins[i];
                r += sumR[bin]; g += sumG[bin]; b += sumB[bin]; n += count[bin];
                lookup[bin] = (byte)index;
            }
            if (n == 0) continue;
            palette[index * 3] = (byte)(r / n);
            palette[index * 3 + 1] = (byte)(g / n);
            palette[index * 3 + 2] = (byte)(b / n);
        }
        return (palette, lookup);
    }

    /// <summary>LZW GIF a lunghezza di codice variabile (9-12 bit) con dizionario a hash aperto.</summary>
    private static void WriteLzw(BinaryWriter writer, byte[] indices)
    {
        const int minCodeSize = 8, clearCode = 256, endCode = 257, maxCodes = 4096, hashSize = 8191;
        writer.Write((byte)minCodeSize);

        var hashKeys = new int[hashSize];
        var hashCodes = new int[hashSize];
        var block = new byte[255];
        int blockLength = 0, bitBuffer = 0, bitCount = 0;
        int codeSize = minCodeSize + 1, nextCode = endCode + 1;

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block[blockLength++] = (byte)bitBuffer;
                bitBuffer >>= 8;
                bitCount -= 8;
                if (blockLength == 255)
                {
                    writer.Write((byte)255);
                    writer.Write(block, 0, 255);
                    blockLength = 0;
                }
            }
        }

        Array.Fill(hashKeys, -1);
        Emit(clearCode);
        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int symbol = indices[i];
            int key = (prefix << 8) | symbol;
            int slot = (int)((uint)(key * 0x9E3779B1) % hashSize);
            int found = -1;
            while (hashKeys[slot] != -1)
            {
                if (hashKeys[slot] == key) { found = hashCodes[slot]; break; }
                if (++slot == hashSize) slot = 0;
            }

            if (found >= 0)
            {
                prefix = found;
                continue;
            }

            Emit(prefix);
            if (nextCode < maxCodes)
            {
                hashKeys[slot] = key;
                hashCodes[slot] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12) codeSize++;
            }
            else
            {
                Emit(clearCode);
                Array.Fill(hashKeys, -1);
                codeSize = minCodeSize + 1;
                nextCode = endCode + 1;
            }
            prefix = symbol;
        }

        Emit(prefix);
        Emit(endCode);
        if (bitCount > 0) block[blockLength++] = (byte)bitBuffer;
        if (blockLength > 0)
        {
            writer.Write((byte)blockLength);
            writer.Write(block, 0, blockLength);
        }
        writer.Write((byte)0);
    }
}

internal sealed class QuickToolOutput(QuickToolContext context)
{
    private OutputPathAllocator.Batch? batch;
    private readonly List<string> outputs = [];
    private readonly List<string> warnings = [];

    private OutputPathAllocator.Batch Batch => batch ??= new(context.InputFilePaths, context.Settings);
    internal IReadOnlyList<string> Sources => Batch.Sources;
    internal OutputPathAllocator.Job CreateJob(params string[] inputs) => Batch.CreateJob(inputs);

    internal void Commit(OutputPathAllocator.Job job, string staged, string name, bool allowRecycle = true, string? destinationDirectory = null)
    {
        var result = job.CommitOutputAndApplyRules([new(staged, name)], context.CancellationToken, allowRecycle, destinationDirectory);
        outputs.AddRange(result.Outputs.Select(item => item.Path));
        if (result.Warning is not null) warnings.Add(result.Warning);
        if (result.Error is not null) throw new IOException(result.Error);
    }

    internal QuickToolResult Complete() =>
        new(outputs.Count > 0, outputs, warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings));

    internal QuickToolResult Fail(string message) =>
        new(false, outputs, string.Join(Environment.NewLine, warnings.Append(message)));

    internal static string ImageFormat(string extension) => extension switch
    {
        "jpeg" => "JPG",
        "tif" => "TIFF",
        _ => extension.ToUpperInvariant()
    };
}

