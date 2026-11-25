using System.IO;
using Microsoft.Win32;
using Flip.Core;
namespace Flip;

internal static class ShellIntegration
{
    const string Key = @"Software\Classes\*\shell\Flip.Convert";
    internal static bool Enabled
    {
        get
        {
            if (StoreEdition.IsPackaged) return StoreEdition.ContextMenuEnabled;
            try
            {
                using var existing = Registry.CurrentUser.OpenSubKey(Key);
                return existing != null;
            }
            catch { return false; }
        }
    }
    internal static void Apply(bool remove)
    {
        if (StoreEdition.IsPackaged) { StoreEdition.ContextMenuEnabled = !remove; return; }
        string exe = Environment.ProcessPath ?? throw new IOException(Loc.T("Percorso eseguibile assente.", "Executable path unavailable."));
        if (!Path.GetFileName(exe).Equals("Flip.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Loc.T("Usare Flip.exe, non dotnet Flip.dll, per configurare il menu.", "Use Flip.exe, not dotnet Flip.dll, to configure the context menu."));
        string command = $"\"{exe}\" --files \"%1\"";
        using (var existing = Registry.CurrentUser.OpenSubKey(Key))
        {
            using var c = existing?.OpenSubKey("command");
            if (existing != null && c?.GetValue("") as string != command)
                throw new IOException(Loc.T("Una chiave Flip diversa esiste già: è stata preservata.", "A different FLIP registry key already exists and was preserved."));
        }
        if (remove) Registry.CurrentUser.DeleteSubKeyTree(Key, false);
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("", Loc.T("Converti con Flip", "Convert with FLIP"));
            key.SetValue("MultiSelectModel", "Single");
            key.SetValue("Icon", $"\"{exe}\",0");
            using var c = key.CreateSubKey("command"); c.SetValue("", command);
        }
        Evidence.Log(remove ? "shell.remove" : "shell.install", exe);
    }
}
