using System.IO;
using Microsoft.Win32;
namespace Flip;

internal static class ShellIntegration
{
    const string Key = @"Software\Classes\*\shell\Flip.Convert";
    internal static void Apply(bool remove)
    {
        string exe = Environment.ProcessPath ?? throw new IOException("Percorso eseguibile assente.");
        if (!Path.GetFileName(exe).Equals("Flip.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Usare Flip.exe, non dotnet Flip.dll, per configurare il menu.");
        string command = $"\"{exe}\" --files \"%1\"";
        using (var existing = Registry.CurrentUser.OpenSubKey(Key))
        {
            using var c = existing?.OpenSubKey("command");
            if (existing != null && c?.GetValue("") as string != command) throw new IOException("Una chiave Flip diversa esiste già: è stata preservata.");
        }
        if (remove) Registry.CurrentUser.DeleteSubKeyTree(Key, false);
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue("", "Converti con Flip"); key.SetValue("MultiSelectModel", "Single"); key.SetValue("Icon", exe + ",0");
            using var c = key.CreateSubKey("command"); c.SetValue("", command);
        }
        Evidence.Log(remove ? "shell.remove" : "shell.install", exe);
    }
}
