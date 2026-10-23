using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace Flip;

// Pair down/up and auto-repeat even if the mouse is released or focus changes.
// A key that started as normal typing keeps its key-up; only an explicit
// gesture can later reserve its repeats without leaving the target key stuck.
internal sealed class ShortcutPressGate
{
    bool down, captured, reserveRepeats;
    internal bool IsDown => down;
    internal void Reset() { down = captured = reserveRepeats = false; }
    internal void ReserveRepeatsForGesture() { if (down) reserveRepeats = true; }
    internal (bool Consume, bool Activate) Process(bool keyDown, bool eligible)
    {
        if (keyDown)
        {
            if (down) return (captured || reserveRepeats, false);
            down = true; captured = eligible; return (captured, captured);
        }
        bool consume = captured; down = captured = reserveRepeats = false; return (consume, false);
    }
}

internal sealed class ShortcutOrderGate
{
    bool held, mouseDown, opened;
    int originX, originY;
    internal bool Held => held;
    internal bool KeyDown(bool primaryHeld)
    {
        if (held) return false;
        held = true;
        if (!primaryHeld) { mouseDown = opened = false; return false; }
        opened = true; return true;
    }
    internal void KeyUp() { held = mouseDown = false; }
    internal void MouseDown(int x, int y) { mouseDown = true; originX = x; originY = y; }
    internal void MouseUp() { mouseDown = opened = false; }
    internal bool Move(int x, int y)
    {
        if (!held || !mouseDown || opened || Math.Abs((long)x - originX) < 8 && Math.Abs((long)y - originY) < 8) return false;
        opened = true; return true;
    }
    internal void Reset() { held = mouseDown = opened = false; }
}

internal sealed class ShortcutHook : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct KeyEvent { public uint Key, Scan, Flags, Time; public nuint Extra; }
    delegate nint Callback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookExW(int kind, Callback callback, nint module, uint thread);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);
    readonly Callback callback;
    readonly Func<WheelShortcut> shortcut;
    readonly Func<bool> eligible;
    readonly Action<bool> keyChanged;
    readonly ShortcutPressGate gate = new();
    nint handle;
    internal ShortcutHook(Func<WheelShortcut> shortcut, Func<bool> eligible, Action<bool> keyChanged)
    {
        this.shortcut = shortcut; this.eligible = eligible; this.keyChanged = keyChanged; callback = Process;
        handle = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Scorciatoia non disponibile. Usa il menu di Flip.");
    }
    internal static bool IsExplorerForeground()
    {
        var name = new StringBuilder(256);
        GetClassNameW(GetForegroundWindow(), name, name.Capacity);
        return name.ToString() is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";
    }
    internal static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => Native.GetAsyncKeyState(k) < 0);
    internal void Reset() => gate.Reset();
    internal void ReserveRepeatsForGesture() => gate.ReserveRepeatsForGesture();
    nint Process(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            try
            {
                var key = Marshal.PtrToStructure<KeyEvent>(data);
                ushort rawFlags = (ushort)((key.Flags & 1) != 0 ? 2 : 0);
                if (shortcut().Matches((ushort)key.Key, rawFlags, (ushort)key.Scan))
                {
                    bool down = message is 0x100 or 0x104;
                    bool firstPress = down && !gate.IsDown;
                    var decision = gate.Process(down, down && eligible());
                    // State changes only post to the Dispatcher; no I/O or UI work here.
                    if (firstPress || !down) keyChanged(down);
                    if (decision.Consume) return 1;
                }
            }
            catch { /* Fail open: never disable keyboard input on an unexpected callback error. */ }
        }
        return CallNextHookEx(handle, code, message, data);
    }
    public void Dispose()
    {
        if (handle == 0) return;
        UnhookWindowsHookEx(handle); handle = 0;
        GC.KeepAlive(callback);
    }
}
