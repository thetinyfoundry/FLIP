using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Flip.Core;
using Flip.Engines;

namespace Flip;

/// <summary>
/// Differenze del pacchetto Microsoft Store. Windows isola registro e AppData dei pacchetti, quindi avvio automatico,
/// menu contestuale, dati e aggiornamenti usano le vie previste da MSIX. Nella versione GitHub non cambia nulla.
/// </summary>
internal static class StoreEdition
{
    internal const string StartupTaskId = "FlipStartup";
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>Nome della famiglia del pacchetto, oppure null per la versione GitHub.</summary>
    internal static string? PackageFamily { get; } = ReadPackageFamily();

    internal static bool IsPackaged => PackageFamily is not null;

    /// <summary>Cartella dati non virtualizzata: i motori scaricati devono avere un percorso reale da cui essere avviati.</summary>
    internal static string? LocalState => PackageFamily is null ? null : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", PackageFamily, "LocalState", "FLIP");

    /// <summary>
    /// Motore incluso nel pacchetto: è coperto dalla firma dello Store, mentre uno scaricato a parte può essere bloccato da Smart App Control.
    /// </summary>
    internal static string? BundledEngine(string engineId)
    {
        if (!IsPackaged) return null;
        string? relative = engineId switch { "ffmpeg" => @"tools\ffmpeg.exe", "whisper" => @"tools\whisper\whisper-cli.exe", _ => null };
        if (relative == null) return null;
        string path = Path.Combine(AppContext.BaseDirectory, relative);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Cartella dei motori scaricati dalla versione GitHub: l'edizione Store li riusa senza copiarli.</summary>
    internal static string? GitHubEngines => TestGitHubEngines ?? (IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLIP", "Engines")
        : null);

    /// <summary>Solo per i test: cartella dei motori della versione GitHub.</summary>
    internal static string? TestGitHubEngines { get; set; }

    private static string ContextMenuMarker => Path.Combine(LocalState!, "context-menu.off");

    internal static bool ContextMenuEnabled
    {
        get => !File.Exists(ContextMenuMarker);
        set
        {
            Directory.CreateDirectory(LocalState!);
            if (value) File.Delete(ContextMenuMarker);
            else File.WriteAllText(ContextMenuMarker, "");
        }
    }

    /// <summary>Al primo avvio riprende le preferenze della versione GitHub, se presente, senza modificarla.</summary>
    internal static void ImportGitHubSettings(string packagedSettings, string githubSettings)
    {
        try
        {
            if (File.Exists(packagedSettings) || !File.Exists(githubSettings)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(packagedSettings)!);
            if (JsonNode.Parse(File.ReadAllText(githubSettings)) is not JsonObject settings) return;
            // I percorsi rilevati dalla versione GitHub farebbero preferire motori non firmati a quelli inclusi.
            if (settings["CustomEnginePaths"] is JsonObject engines)
            {
                // Il modello accanto al vecchio motore resta valido: senza, l'utente dovrebbe scaricarlo di nuovo.
                if (!engines.ContainsKey("whisper_model") && engines["whisper"] is JsonValue whisper &&
                    whisper.TryGetValue(out string? executable) && LocalAiEngine.FindModelBeside(executable) is { } model)
                    engines["whisper_model"] = model;
                engines.Remove("ffmpeg");
                engines.Remove("whisper");
            }
            File.WriteAllText(packagedSettings, settings.ToJsonString(TextFiles.ReadableJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            Evidence.Log("store.settings.import.error", ex.Message);
        }
    }

    /// <summary>Gli aggiornamenti arrivano dallo Store: FLIP può solo aprire la pagina.</summary>
    internal static void OpenStoreUpdates() =>
        Process.Start(new ProcessStartInfo("ms-windows-store://downloadsandupdates") { UseShellExecute = true })?.Dispose();

    internal static bool StartupEnabled => Startup(task =>
    {
        Marshal.ThrowExceptionForHR(task.GetState(out int state));
        return state is 2 or 4;
    });

    internal static void SetStartup(bool enabled) => Startup(task =>
    {
        if (!enabled)
        {
            Marshal.ThrowExceptionForHR(task.Disable());
            return true;
        }
        Marshal.ThrowExceptionForHR(task.RequestEnableAsync(out nint operation));
        int state = WaitResult(operation, (IAsyncOperationEnum pending, out int value) => pending.GetResults(out value));
        // 1: disattivato dall'utente in Gestione attività; 3: vietato dai criteri.
        if (state is 1 or 3)
            throw new IOException(Loc.T(
                "Windows ha disattivato l'avvio di FLIP. Riattivalo in Impostazioni > App > Avvio.",
                "Windows has turned off FLIP startup. Turn it back on in Settings > Apps > Startup."));
        return true;
    });

    private static string? ReadPackageFamily()
    {
        try
        {
            uint length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) == AppModelErrorNoPackage || length == 0) return null;
            var name = new StringBuilder((int)length);
            return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    // Chiamate dirette all'API StartupTask di Windows: le proiezioni WinRT aggiungerebbero 25 MB anche alla versione GitHub.
    private static T Startup<T>(Func<IStartupTask, T> use)
    {
        // Le API asincrone di WinRT non si completano mentre il thread dell'interfaccia le attende.
        try { return Task.Run(() => Call(use)).GetAwaiter().GetResult(); }
        // Il menu e le impostazioni gestiscono già IOException: un errore di Windows non deve chiudere FLIP.
        catch (Exception ex) when (ex is not IOException) { throw new IOException(ex.Message, ex); }

        static T Call(Func<IStartupTask, T> use)
        {
            const string name = "Windows.ApplicationModel.StartupTask";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out nint className));
            nint factory = 0, operation, taskName = 0;
            try
            {
                Guid statics = typeof(IStartupTaskStatics).GUID;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref statics, out factory));
                Marshal.ThrowExceptionForHR(WindowsCreateString(StartupTaskId, StartupTaskId.Length, out taskName));
                var activation = (IStartupTaskStatics)Marshal.GetObjectForIUnknown(factory);
                Marshal.ThrowExceptionForHR(activation.GetAsync(taskName, out operation));
            }
            finally
            {
                if (factory != 0) Marshal.Release(factory);
                _ = WindowsDeleteString(className);
                if (taskName != 0) _ = WindowsDeleteString(taskName);
            }

            nint task = WaitResult(operation, (IAsyncOperationObject pending, out nint value) => pending.GetResults(out value));
            try { return use((IStartupTask)Marshal.GetObjectForIUnknown(task)); }
            finally { Marshal.Release(task); }
        }
    }

    private delegate int ReadResult<TOperation, TValue>(TOperation operation, out TValue value);

    private static TValue WaitResult<TOperation, TValue>(nint operation, ReadResult<TOperation, TValue> read) where TOperation : class
    {
        try
        {
            object instance = Marshal.GetObjectForIUnknown(operation);
            var info = (IAsyncInfo)instance;
            var limit = Stopwatch.StartNew();
            int status;
            while (true)
            {
                Marshal.ThrowExceptionForHR(info.GetStatus(out status));
                if (status != 0) break;
                if (limit.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException(Loc.T("Windows non ha risposto.", "Windows did not respond."));
                Thread.Sleep(15);
            }
            if (status != 1)
            {
                Marshal.ThrowExceptionForHR(info.GetErrorCode(out int error));
                throw new IOException($"StartupTask 0x{error:X8}");
            }
            // L'interfaccia è generica: il suo identificatore dipende dal tipo del risultato.
            Guid id = typeof(TOperation).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(operation, in id, out nint typed));
            try
            {
                var pending = (TOperation)Marshal.GetTypedObjectForIUnknown(typed, typeof(TOperation));
                Marshal.ThrowExceptionForHR(read(pending, out TValue value));
                return value;
            }
            finally { Marshal.Release(typed); }
        }
        finally { Marshal.Release(operation); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? name);
    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint className, ref Guid iid, out nint factory);

    // I primi tre metodi di ogni interfaccia sono quelli di IInspectable.
    [ComImport, Guid("EE5B60BD-A148-41A7-B26E-E8B88A1E62F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStartupTaskStatics
    {
        void Inspectable1(); void Inspectable2(); void Inspectable3();
        [PreserveSig] int GetForCurrentPackageAsync(out nint operation);
        [PreserveSig] int GetAsync(nint taskId, out nint operation);
    }

    [ComImport, Guid("F75C23C8-B5F2-4F6C-88DD-36CB1D599D17"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IStartupTask
    {
        void Inspectable1(); void Inspectable2(); void Inspectable3();
        [PreserveSig] int RequestEnableAsync(out nint operation);
        [PreserveSig] int Disable();
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("00000036-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAsyncInfo
    {
        void Inspectable1(); void Inspectable2(); void Inspectable3();
        [PreserveSig] int GetId(out uint id);
        [PreserveSig] int GetStatus(out int status);
        [PreserveSig] int GetErrorCode(out int error);
    }

    // IAsyncOperation<StartupTask>
    [ComImport, Guid("CBEC7A4E-A046-5330-873D-0FCE228792FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAsyncOperationObject
    {
        void Inspectable1(); void Inspectable2(); void Inspectable3();
        [PreserveSig] int SetCompleted(nint handler);
        [PreserveSig] int GetCompleted(out nint handler);
        [PreserveSig] int GetResults(out nint result);
    }

    // IAsyncOperation<StartupTaskState>
    [ComImport, Guid("5239A934-80E2-518F-B819-1F316F379A3F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAsyncOperationEnum
    {
        void Inspectable1(); void Inspectable2(); void Inspectable3();
        [PreserveSig] int SetCompleted(nint handler);
        [PreserveSig] int GetCompleted(out nint handler);
        [PreserveSig] int GetResults(out int result);
    }
}
