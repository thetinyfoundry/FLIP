using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
namespace Flip;

internal static class StartupIntegration
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Flip.RadialConverter";
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "Flip.exe");
    internal static string CommandFor(string executable) => "\"" + Path.GetFullPath(executable) + "\" --background";
    internal static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return string.Equals(key?.GetValue(ValueName) as string, CommandFor(Executable), StringComparison.OrdinalIgnoreCase); }
    }
    internal static void SetEnabled(bool enabled)
    {
        string expected = CommandFor(Executable);
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        string? existing = key.GetValue(ValueName) as string;
        if (existing != null && !string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Esiste un avvio Flip con un percorso diverso. Rimuovilo dalla precedente installazione prima di sostituirlo.");
        if (enabled)
        {
            if (!File.Exists(Executable)) throw new FileNotFoundException("Pubblica Flip.exe prima di configurare l'avvio automatico.", Executable);
            key.SetValue(ValueName, expected, RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, false);
        Evidence.Log("startup.changed", new { enabled, command = expected });
    }
    internal static void Shortcuts(bool remove)
    {
        string[] locations = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Flip.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"Flip.lnk")];
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows Script Host non disponibile per creare i collegamenti.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            foreach (string location in locations)
            {
                if (remove && !File.Exists(location)) continue;
                dynamic link = shell.CreateShortcut(location);
                try
                {
                    if (File.Exists(location) && !string.Equals((string)link.TargetPath, Executable, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Esiste già un collegamento Flip con destinazione diversa: " + location);
                    if (remove) { File.Delete(location); continue; }
                    link.TargetPath = Executable; link.Arguments = "--ui"; link.WorkingDirectory = AppContext.BaseDirectory;
                    link.IconLocation = Path.Combine(AppContext.BaseDirectory, "Flip.ico"); link.Description = "Flip · convertitore locale di file"; link.Save();
                }
                finally { Marshal.FinalReleaseComObject(link); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        Evidence.Log("shortcuts.changed", new { remove, locations });
    }
}
