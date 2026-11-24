using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using Flip.Core;

namespace Flip.Engines;

/// <summary>
/// Motore locale di rimozione sfondo AI basato su ONNX Runtime e sul modello open-source IS-Net
/// (<c>isnet-general-use.onnx</c>, licenza Apache-2.0, input 1024×1024 NCHW).
/// Non richiede Python né servizi cloud e memorizza il modello in <c>%APPDATA%\FLIP\Engines\RemoveBg</c>.
/// </summary>
public static class OnnxRemoveBgEngine
{
    public const string ModelKey = "removebg_model";
    public const string ModelFileName = "isnet-general-use.onnx";
    public const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx";
    public const string ModelSha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a";
    public const long ExpectedModelBytes = 178_648_008L;
    public const long MinimumDownloadedModelBytes = 150L * 1024 * 1024;
    public const long MinimumCustomModelBytes = 1L * 1024 * 1024;
    public const int InputSize = 1024;

    public static string MissingModelMessage =>
        Loc.T(
            "Modello AI Rimozione Sfondo (IS-Net) non installato. Scaricalo in 1 clic da Impostazioni → 4. Motori & GPU.",
            "IS-Net AI Background Removal model is not installed. Download it in 1 click from Settings → 4. Engines & GPU.");

    private static readonly SKSamplingOptions CubicSmoothSampling = new(SKCubicResampler.Mitchell);

    private static readonly SemaphoreSlim InferenceGate = new(1, 1);
    private static InferenceSession? _cachedSession;
    private static string? _cachedModelPath;
    private static long _cachedModelLength;
    private static DateTime _cachedModelWriteUtc;
    private static string? _cachedInputName;
    private static int _cacheGeneration;
    private static int _cachedSessionGeneration = -1;

    /// <summary>Override isolato per i test offline (mantiene invariata la directory reale in produzione).</summary>
    internal static string? TestOverrideInstallDirectory { get; set; }

    public static string InstallDirectory =>
        string.IsNullOrWhiteSpace(TestOverrideInstallDirectory)
            ? Path.Combine(SettingsStore.DataDirectory, "Engines", "RemoveBg")
            : TestOverrideInstallDirectory;

    public static string DefaultModelPath => Path.Combine(InstallDirectory, ModelFileName);

    /// <summary>Verifica che un file <c>.onnx</c> esista, non sia un link simbolico e rispetti la soglia minima in byte.</summary>
    public static bool IsUsableModelFile(string? path, long minBytes = MinimumCustomModelBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expanded)) return false;
            if (!expanded.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(expanded);
            if (!info.Exists) return false;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return info.Length >= minBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Risolve il percorso del modello ONNX configurato dall'utente oppure quello predefinito in
    /// <c>%APPDATA%\FLIP\Engines\RemoveBg\isnet-general-use.onnx</c>. Restituisce <c>null</c> se assente.
    /// </summary>
    public static string? ResolveModelPath(FlipSettings? settings = null)
    {
        var snap = settings ?? SettingsStore.Current.CreateSnapshot();
        if (snap.CustomEnginePaths.TryGetValue(ModelKey, out string? configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            string expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (IsUsableModelFile(expanded, MinimumCustomModelBytes))
                return expanded;
        }

        string defaultPath = DefaultModelPath;
        if (IsUsableModelFile(defaultPath, MinimumDownloadedModelBytes))
            return defaultPath;

        if (StoreEdition.GitHubEngines is { } shared &&
            Path.Combine(shared, "RemoveBg", ModelFileName) is var sharedPath &&
            IsUsableModelFile(sharedPath, MinimumDownloadedModelBytes))
            return sharedPath;

        return null;
    }

    public static bool IsModelInstalled(FlipSettings? settings = null) =>
        ResolveModelPath(settings) is not null;

    /// <summary>Rimuove dalle impostazioni un percorso <c>removebg_model</c> obsoleto o non più esistente su disco.</summary>
    public static void CleanupStalePath(SettingsStore? store = null)
    {
        var target = store ?? SettingsStore.Current;
        var snap = target.CreateSnapshot();
        if (!snap.CustomEnginePaths.TryGetValue(ModelKey, out string? configured) ||
            string.IsNullOrWhiteSpace(configured))
            return;

        if (!IsUsableModelFile(configured, MinimumCustomModelBytes))
        {
            try
            {
                target.Update(s => s.CustomEnginePaths.Remove(ModelKey));
                InvalidateSessionCache();
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Invalida la sessione ONNX in cache (la memoria viene liberata subito se nessuna inferenza è in corso).</summary>
    public static void InvalidateSessionCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        if (InferenceGate.Wait(0))
        {
            try
            {
                DisposeCachedSessionCore();
            }
            finally
            {
                InferenceGate.Release();
            }
        }
    }

    /// <summary>
    /// Scarica atomicamente <c>isnet-general-use.onnx</c> su file temporaneo, verifica dimensione minima e SHA-256,
    /// poi lo sposta in <see cref="DefaultModelPath"/> e registra il percorso in <see cref="SettingsStore"/>.
    /// </summary>
    public static async Task<string> DownloadModelAsync(
        IProgress<WhisperInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string targetDir = InstallDirectory;
        Directory.CreateDirectory(targetDir);

        string targetPath = Path.Combine(targetDir, ModelFileName);
        string stagedPath = Path.Combine(targetDir, $"{ModelFileName}.{Guid.NewGuid():N}.download");

        progress?.Report(new(0.01, Loc.T("Connessione al repository ufficiale IS-Net…", "Connecting to official IS-Net repository…")));
        try
        {
            await WhisperModuleInstaller.DownloadAsync(
                ModelUrl,
                stagedPath,
                ModelSha256,
                (done, total) => progress?.Report(new(
                    0.02 + 0.94 * WhisperModuleInstaller.Fraction(done, total),
                    Loc.F("Download modello IS-Net… {0}", "Downloading IS-Net model… {0}", WhisperModuleInstaller.FormatMb(done, total)))),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new(0.98, Loc.T("Verifica integrità e attivazione modello…", "Verifying integrity and activating model…")));
            var info = new FileInfo(stagedPath);
            if (!info.Exists || info.Length < MinimumDownloadedModelBytes)
                throw new IOException(Loc.T(
                    "Il file del modello IS-Net scaricato è incompleto o troppo piccolo.",
                    "The downloaded IS-Net model file is incomplete or too small."));

            File.Move(stagedPath, targetPath, overwrite: true);
        }
        finally
        {
            WhisperModuleInstaller.TryDelete(stagedPath);
        }

        InvalidateSessionCache();
        SettingsStore.Current.Update(s => s.CustomEnginePaths[ModelKey] = targetPath);
        progress?.Report(new(1.0, Loc.T("Attivo (IS-Net ONNX locale)", "Active (local IS-Net ONNX)")));
        return targetPath;
    }

    /// <summary>
    /// Esegue l'inferenza ONNX su un'immagine già orientata EXIF e restituisce un nuovo <see cref="SKBitmap"/>
    /// con i pixel RGB originali preservati e il canale alpha calcolato dal modello IS-Net.
    /// </summary>
    public static SKBitmap RemoveBackground(SKBitmap orientedSource, string modelPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orientedSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (orientedSource.Width <= 0 || orientedSource.Height <= 0)
            throw new ArgumentException(Loc.T("Dimensioni immagine non valide.", "Invalid image dimensions."), nameof(orientedSource));
        if (!IsUsableModelFile(modelPath, MinimumCustomModelBytes))
            throw new FileNotFoundException(MissingModelMessage, modelPath);

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Preprocessing fuori dal lock di inferenza
        DenseTensor<float> inputTensor = PreprocessToTensor(orientedSource);
        cancellationToken.ThrowIfCancellationRequested();

        float[] rawOutput;
        int maskWidth;
        int maskHeight;

        // 2. Sessione ONNX cached + inferenza protetta da gate thread-safe
        InferenceGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (InferenceSession session, string inputName) = GetOrCreateSessionLocked(modelPath);
            var inputs = new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(inputs);
            if (outputs.Count == 0)
                throw new InvalidOperationException(Loc.T("Il modello ONNX non ha prodotto alcun tensore di uscita.", "The ONNX model produced no output tensor."));

            Tensor<float> predTensor = outputs[0].AsTensor<float>();
            (rawOutput, maskWidth, maskHeight) = ExtractPrimaryMaskPlane(predTensor);
        }
        finally
        {
            InferenceGate.Release();
            ScheduleIdleRelease();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Postprocessing: normalizzazione min-max + ridimensionamento fluido + composizione alpha RGB-preserving
        byte[] alphaMask = NormalizeAlphaMask(rawOutput, maskWidth * maskHeight);
        return ApplyAlphaMask(orientedSource, alphaMask, maskWidth, maskHeight);
    }

    /// <summary>
    /// Ridimensiona l'immagine a <c>1024×1024</c> RGB e genera il tensore NCHW <c>[1, 3, 1024, 1024]</c>
    /// con normalizzazione IS-Net <c>(v / 255f) - 0.5f</c>.
    /// </summary>
    internal static DenseTensor<float> PreprocessToTensor(SKBitmap orientedSource)
    {
        ArgumentNullException.ThrowIfNull(orientedSource);
        if (orientedSource.Width <= 0 || orientedSource.Height <= 0)
            throw new ArgumentException(Loc.T("Immagine sorgente vuota.", "Source image is empty."), nameof(orientedSource));

        using var resized = new SKBitmap(InputSize, InputSize, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.White);
            using var srcImage = SKImage.FromBitmap(orientedSource);
            canvas.DrawImage(srcImage, new SKRect(0, 0, InputSize, InputSize), CubicSmoothSampling);
            canvas.Flush();
        }

        const float inv255 = 1f / 255f;
        const int planeSize = InputSize * InputSize;
        var buffer = new float[3 * planeSize];
        ReadOnlySpan<byte> pixels = resized.GetPixelSpan();
        int rowBytes = resized.RowBytes;

        for (int y = 0; y < InputSize; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * rowBytes, InputSize * 4);
            int rowOffset = y * InputSize;
            for (int x = 0; x < InputSize; x++)
            {
                int px = x * 4;
                int idx = rowOffset + x;
                // BGRA8888 -> canali RGB in ordine NCHW (0=R, 1=G, 2=B)
                buffer[idx] = (row[px + 2] * inv255) - 0.5f;
                buffer[planeSize + idx] = (row[px + 1] * inv255) - 0.5f;
                buffer[(2 * planeSize) + idx] = (row[px] * inv255) - 0.5f;
            }
        }

        return new DenseTensor<float>(buffer, [1, 3, InputSize, InputSize]);
    }

    /// <summary>
    /// Normalizza la mappa alpha grezza del modello con min-max scaling e clamp nell'intervallo <c>[0, 1]</c>,
    /// restituendo valori 8-bit <c>[0..255]</c>.
    /// </summary>
    internal static byte[] NormalizeAlphaMask(ReadOnlySpan<float> rawOutput, int expectedCount)
    {
        if (expectedCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedCount));

        int count = Math.Min(expectedCount, rawOutput.Length);
        var mask = new byte[expectedCount];
        if (count == 0) return mask;

        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            float v = rawOutput[i];
            if (!float.IsFinite(v)) continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (!float.IsFinite(min) || !float.IsFinite(max))
            return mask;

        float range = max - min;
        if (range > 1e-5f)
        {
            float invRange = 1f / range;
            for (int i = 0; i < count; i++)
            {
                float v = rawOutput[i];
                float normalized = float.IsFinite(v) ? Math.Clamp((v - min) * invRange, 0f, 1f) : 0f;
                mask[i] = (byte)Math.Clamp((int)MathF.Round(normalized * 255f), 0, 255);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                float v = rawOutput[i];
                float clamped = float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
                mask[i] = (byte)Math.Clamp((int)MathF.Round(clamped * 255f), 0, 255);
            }
        }

        return mask;
    }

    /// <summary>
    /// Ridimensiona la maschera alpha alla risoluzione dell'immagine originale con filtraggio cubico fluido
    /// e la applica preservando integralmente i canali RGB originali senza aloni.
    /// </summary>
    internal static SKBitmap ApplyAlphaMask(SKBitmap originalOriented, ReadOnlySpan<byte> maskBytes, int maskWidth, int maskHeight)
    {
        ArgumentNullException.ThrowIfNull(originalOriented);
        if (originalOriented.Width <= 0 || originalOriented.Height <= 0)
            throw new ArgumentException(Loc.T("Immagine originale vuota.", "Original image is empty."), nameof(originalOriented));
        if (maskWidth <= 0 || maskHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(maskWidth), Loc.T("Dimensioni maschera non valide.", "Invalid mask dimensions."));
        if (maskBytes.Length < maskWidth * maskHeight)
            throw new ArgumentException(Loc.T("Buffer maschera alpha insufficiente per le dimensioni specificate.", "Alpha mask buffer is too small for the specified dimensions."), nameof(maskBytes));

        int origW = originalOriented.Width;
        int origH = originalOriented.Height;

        // 1. Ricampiona la maschera alpha alle dimensioni originali (salta copia se già 1:1)
        byte[] resizedAlpha = ResampleMaskToSize(maskBytes, maskWidth, maskHeight, origW, origH);

        // 2. Legge i pixel sorgente in BGRA8888 non premoltiplicati per preservare fedelmente i colori RGB
        var unpremulInfo = new SKImageInfo(origW, origH, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var result = new SKBitmap(unpremulInfo);
        bool copied;
        using (var srcPixmap = originalOriented.PeekPixels())
        {
            copied = srcPixmap is not null && srcPixmap.ReadPixels(unpremulInfo, result.GetPixels(), result.RowBytes);
        }

        if (!copied)
        {
            // Fallback sicuro via canvas se il bitmap sorgente usa un layout non leggibile direttamente
            using var fallback = new SKBitmap(origW, origH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(fallback))
            {
                c.Clear(SKColors.Transparent);
                c.DrawBitmap(originalOriented, 0, 0, new SKSamplingOptions());
                c.Flush();
            }
            using var fbPixmap = fallback.PeekPixels();
            if (fbPixmap is null || !fbPixmap.ReadPixels(unpremulInfo, result.GetPixels(), result.RowBytes))
            {
                result.Dispose();
                throw new IOException(Loc.T("Impossibile leggere i pixel dell'immagine sorgente.", "Unable to read source image pixels."));
            }
        }

        Span<byte> dstPixels = result.GetPixelSpan();
        int dstRowBytes = result.RowBytes;

        for (int y = 0; y < origH; y++)
        {
            Span<byte> row = dstPixels.Slice(y * dstRowBytes, origW * 4);
            int maskRow = y * origW;
            for (int x = 0; x < origW; x++)
            {
                int px = x * 4;
                byte origAlpha = row[px + 3];
                byte maskAlpha = resizedAlpha[maskRow + x];
                byte finalAlpha = origAlpha == 255
                    ? maskAlpha
                    : (byte)((origAlpha * maskAlpha + 127) / 255);

                if (finalAlpha == 0)
                {
                    row[px] = 0;
                    row[px + 1] = 0;
                    row[px + 2] = 0;
                    row[px + 3] = 0;
                }
                else
                {
                    // I byte RGB (px+2=R, px+1=G, px=B) restano intatti al 100%.
                    row[px + 3] = finalAlpha;
                }
            }
        }

        result.NotifyPixelsChanged();
        return result;
    }

    private static byte[] ResampleMaskToSize(ReadOnlySpan<byte> maskBytes, int maskW, int maskH, int targetW, int targetH)
    {
        if (maskW == targetW && maskH == targetH)
            return maskBytes[..(maskW * maskH)].ToArray();

        using var maskBmp = new SKBitmap(maskW, maskH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Span<byte> maskPixels = maskBmp.GetPixelSpan();
        int maskRowBytes = maskBmp.RowBytes;
        for (int y = 0; y < maskH; y++)
        {
            Span<byte> row = maskPixels.Slice(y * maskRowBytes, maskW * 4);
            int srcRow = y * maskW;
            for (int x = 0; x < maskW; x++)
            {
                byte m = maskBytes[srcRow + x];
                int px = x * 4;
                row[px] = m;
                row[px + 1] = m;
                row[px + 2] = m;
                row[px + 3] = 255;
            }
        }
        maskBmp.NotifyPixelsChanged();

        using var scaledBmp = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(scaledBmp))
        {
            canvas.Clear(SKColors.Black);
            using var maskImage = SKImage.FromBitmap(maskBmp);
            canvas.DrawImage(maskImage, new SKRect(0, 0, targetW, targetH), CubicSmoothSampling);
            canvas.Flush();
        }

        var output = new byte[targetW * targetH];
        ReadOnlySpan<byte> scaledPixels = scaledBmp.GetPixelSpan();
        int scaledRowBytes = scaledBmp.RowBytes;
        for (int y = 0; y < targetH; y++)
        {
            ReadOnlySpan<byte> row = scaledPixels.Slice(y * scaledRowBytes, targetW * 4);
            int dstRow = y * targetW;
            for (int x = 0; x < targetW; x++)
            {
                output[dstRow + x] = row[(x * 4) + 2];
            }
        }
        return output;
    }

    private static (float[] Values, int Width, int Height) ExtractPrimaryMaskPlane(Tensor<float> tensor)
    {
        var dims = tensor.Dimensions;
        if (dims.Length < 2)
            throw new InvalidOperationException(Loc.F("Forma tensore ONNX inattesa: rank {0}.", "Unexpected ONNX tensor shape: rank {0}.", dims.Length));

        int maskH = dims[dims.Length - 2];
        int maskW = dims[dims.Length - 1];
        if (maskW <= 0 || maskH <= 0)
            throw new InvalidOperationException(Loc.F("Dimensioni maschera ONNX non valide: {0}×{1}.", "Invalid ONNX mask dimensions: {0}×{1}.", maskW, maskH));

        int planeCount = checked(maskW * maskH);
        var values = new float[planeCount];

        if (tensor is DenseTensor<float> dense)
        {
            ReadOnlySpan<float> span = dense.Buffer.Span;
            if (span.Length < planeCount)
                throw new InvalidOperationException(Loc.T("Tensore ONNX di uscita più corto del previsto.", "ONNX output tensor is shorter than expected."));
            span[..planeCount].CopyTo(values);
        }
        else
        {
            int copied = 0;
            foreach (float v in tensor)
            {
                values[copied++] = v;
                if (copied >= planeCount) break;
            }
            if (copied < planeCount)
                throw new InvalidOperationException(Loc.T("Tensore ONNX di uscita incompleto.", "Incomplete ONNX output tensor."));
        }

        return (values, maskW, maskH);
    }

    private static (InferenceSession Session, string InputName) GetOrCreateSessionLocked(string modelPath)
    {
        string fullPath = Path.GetFullPath(modelPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException(MissingModelMessage, fullPath);

        int gen = Volatile.Read(ref _cacheGeneration);
        if (_cachedSession is not null &&
            _cachedSessionGeneration == gen &&
            string.Equals(_cachedModelPath, fullPath, StringComparison.OrdinalIgnoreCase) &&
            _cachedModelLength == info.Length &&
            _cachedModelWriteUtc == info.LastWriteTimeUtc &&
            !string.IsNullOrWhiteSpace(_cachedInputName))
        {
            return (_cachedSession, _cachedInputName);
        }

        DisposeCachedSessionCore();

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 8),
            InterOpNumThreads = 1,
            EnableMemoryPattern = true,
            EnableCpuMemArena = true
        };

        var session = new InferenceSession(fullPath, options);
        string? inputName = session.InputMetadata.Keys.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(inputName))
        {
            session.Dispose();
            throw new InvalidOperationException(Loc.T("Il modello ONNX selezionato non espone alcun tensore di ingresso.", "The selected ONNX model exposes no input tensor."));
        }

        _cachedSession = session;
        _cachedModelPath = fullPath;
        _cachedModelLength = info.Length;
        _cachedModelWriteUtc = info.LastWriteTimeUtc;
        _cachedInputName = inputName;
        _cachedSessionGeneration = gen;
        return (session, inputName);
    }

    // Il modello occupa oltre 1 GB: resta pronto per i file successivi e poi libera la memoria.
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(60);
    private static readonly Timer IdleTimer = new(_ => InvalidateSessionCache(), null, Timeout.Infinite, Timeout.Infinite);

    private static void ScheduleIdleRelease() => IdleTimer.Change(IdleLimit, Timeout.InfiniteTimeSpan);

    private static void DisposeCachedSessionCore()
    {
        if (_cachedSession is not null)
        {
            try { _cachedSession.Dispose(); } catch { }
            _cachedSession = null;
        }
        _cachedModelPath = null;
        _cachedModelLength = 0;
        _cachedModelWriteUtc = default;
        _cachedInputName = null;
        _cachedSessionGeneration = -1;
    }
}
