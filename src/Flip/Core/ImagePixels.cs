using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Flip.Core;

/// <summary>Decodifica e scrittura dei pixel condivise dalla ruota e dalla riga di comando.</summary>
internal static class ImagePixels
{
    /// <summary>
    /// Decodifica in sRGB: un profilo ICC (Display P3, Adobe RGB) viene applicato ai pixel,
    /// perché i formati di uscita non lo conservano e i colori cambierebbero.
    /// </summary>
    internal static SKCodecResult Decode(SKCodec codec, out SKBitmap bitmap)
    {
        bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var srgb = SKColorSpace.CreateSrgb();
        var result = codec.GetPixels(bitmap.Info.WithColorSpace(srgb), bitmap.GetPixels());
        // Alcuni profili (CMYK, Lab) non sono convertibili: meglio i pixel originali che nessuna immagine.
        if (result is SKCodecResult.InvalidConversion or SKCodecResult.InvalidParameters)
            result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        return result;
    }

    /// <summary>Copia un fotogramma WPF in una bitmap Skia con alfa premoltiplicato.</summary>
    internal static SKBitmap FromWpf(BitmapSource frame)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        var bitmap = new SKBitmap(frame.PixelWidth, frame.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        converted.CopyPixels(new System.Windows.Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight),
            bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        return bitmap;
    }

    internal static SKBitmap FlattenOnWhite(SKBitmap bitmap)
    {
        var flattened = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(flattened);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions());
        return flattened;
    }

    internal static void SaveWithWpf(SKBitmap bitmap, string path, BitmapEncoder encoder, FileMode mode)
    {
        // I visualizzatori ignorano l'alfa dei BMP: senza sfondo le parti trasparenti diventano nere.
        using SKBitmap? flattened = encoder is BmpBitmapEncoder ? FlattenOnWhite(bitmap) : null;
        SKBitmap source = flattened ?? bitmap;
        // Bgra32 richiede colori non premoltiplicati, altrimenti i bordi semitrasparenti si scuriscono.
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        byte[] pixels = new byte[info.BytesSize];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var view = source.PeekPixels() ?? throw new IOException("Pixel dell'immagine non accessibili.");
            if (!view.ReadPixels(info, pin.AddrOfPinnedObject(), info.RowBytes))
                throw new IOException("Conversione dei pixel non riuscita.");
        }
        finally { pin.Free(); }

        var format = flattened is null ? PixelFormats.Bgra32 : PixelFormats.Bgr32;
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(info.Width, info.Height, 96, 96, format, null, pixels, info.RowBytes)));
        using var file = new FileStream(path, mode, FileAccess.Write, FileShare.None);
        encoder.Save(file);
        file.Flush(true);
    }
}
