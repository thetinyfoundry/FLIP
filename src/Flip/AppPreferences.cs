using System.IO;
using System.Text.Json;
namespace Flip;

internal sealed class AppPreferences
{
    public bool SoundEnabled { get; set; } = true;
    public string Trigger { get; set; } = WheelShortcut.Default;
    public string Language { get; set; } = "auto";
    internal static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flip", "settings.json");
    internal static AppPreferences Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Evidence.Log("settings.load.error", ex.Message); return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, SettingsPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
