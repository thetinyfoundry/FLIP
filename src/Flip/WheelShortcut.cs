namespace Flip;
// Single non-modifier key: no Windows copy/move/link modifiers are required.
internal readonly record struct WheelShortcut(string Name, ushort VirtualKey)
{
    internal const string Default = "DOWN";
    internal string Id => VirtualKey == 0xE2 ? "OEM102" : "VK:" + VirtualKey.ToString("X2");
    internal static WheelShortcut Parse(string? value) => (value ?? "").Trim().ToUpperInvariant().Replace(" ", "") switch
    {
        "OEM102" or "OEM_102" or "VK_OEM_102" or "<>" or "<" or ">" => new("<>", 0xE2),
        "DOWN" => FromVirtualKey(0x28),
        "UP" => FromVirtualKey(0x26),
        "LEFT" => FromVirtualKey(0x25),
        "RIGHT" => FromVirtualKey(0x27),
        "SPACE" => FromVirtualKey(0x20),
        var s when s.StartsWith("VK:", StringComparison.Ordinal) && ushort.TryParse(s[3..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort key) => FromVirtualKey(key),
        var s when s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]) => FromVirtualKey(s[0]),
        var s when s.StartsWith("F", StringComparison.Ordinal) && int.TryParse(s[1..], out int f) && f is >= 1 and <= 11 => FromVirtualKey((ushort)(0x6F + f)),
        _ => throw new ArgumentException("Scegli un tasto singolo: una freccia, Spazio, una lettera o un tasto funzione. Ctrl, Alt, Shift e Fn non sono utilizzabili da soli.")
    };
    internal static WheelShortcut FromVirtualKey(ushort key)
    {
        string name = key switch
        {
            0x20 => "Spazio", 0x25 => "Freccia sinistra", 0x26 => "Freccia su", 0x27 => "Freccia destra", 0x28 => "Freccia giù",
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x70 and <= 0x7A => "F" + (key - 0x6F),
            0xE2 => "<>",
            >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF => ((System.Windows.Input.Key)System.Windows.Input.KeyInterop.KeyFromVirtualKey(key)).ToString(),
            _ => throw new ArgumentException("Questo tasto è riservato. Scegli una freccia, Spazio, una lettera o F1–F11, senza modificatori.")
        };
        return new(name, key);
    }
    internal bool Matches(ushort key, ushort flags, ushort scanCode = 0) =>
        VirtualKey == 0xE2 ? (flags & 6) == 0 && (scanCode != 0 ? scanCode == 0x56 : key == VirtualKey) : key == VirtualKey;
}
