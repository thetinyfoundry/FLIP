using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
namespace Flip;

internal static class ClipboardGestureExtensions
{
    internal static bool Matches(this ClipboardShortcutGesture gesture, int vk, bool ctrl, bool shift, bool alt, bool win = false) =>
        gesture.Enabled && !win && vk == gesture.VirtualKey && ctrl == gesture.Control && shift == gesture.Shift && alt == gesture.Alt;
}

internal enum WheelCommand { FlipFace, NextPreset, ToggleZip, QuickLook }

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

/// <summary>
/// Rileva la pressione prolungata del pulsante del mouse su un file, senza spostamento.
/// La logica è pura (nessun timer, nessuna I/O) così è verificabile nei test.
/// </summary>
internal sealed class HoldToOpenGate
{
    const int MoveTolerance = 8;
    readonly int thresholdMs;
    long startedTick;
    int originX, originY;
    bool armed;

    internal HoldToOpenGate(int thresholdMs) => this.thresholdMs = Math.Clamp(thresholdMs, 1000, 8000);

    internal void MouseDown(int x, int y, long now)
    {
        armed = true;
        startedTick = now;
        originX = x;
        originY = y;
    }

    internal bool IsArmed => armed;
    internal void MouseUp() => armed = false;
    internal void Cancel() => armed = false;

    internal void Move(int x, int y)
    {
        if (armed && (Math.Abs((long)x - originX) >= MoveTolerance || Math.Abs((long)y - originY) >= MoveTolerance))
            armed = false;
    }

    internal bool IsDue(long now) => armed && now - startedTick >= thresholdMs;
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
    readonly Func<WheelCommand, bool> canUseWheelCommand;
    readonly Action<WheelCommand> wheelCommand;
    readonly Func<ClipboardShortcutGesture>? clipboardGesture;
    readonly Action? onClipboardFlip;
    readonly Func<bool>? canClipboardFlip;
    readonly ShortcutPressGate gate = new();
    readonly ShortcutPressGate tabGate = new();
    readonly ShortcutPressGate presetGate = new();
    readonly ShortcutPressGate spaceGate = new();
    readonly ShortcutPressGate zipGate = new();
    readonly ShortcutPressGate clipboardGate = new();
    nint handle;
    internal ShortcutHook(Func<WheelShortcut> shortcut, Func<bool> eligible, Action<bool> keyChanged,
        Func<WheelCommand, bool> canUseWheelCommand, Action<WheelCommand> wheelCommand,
        Func<ClipboardShortcutGesture>? clipboardGesture = null,
        Action? onClipboardFlip = null,
        Func<bool>? canClipboardFlip = null)
    {
        this.shortcut = shortcut; this.eligible = eligible; this.keyChanged = keyChanged;
        this.canUseWheelCommand = canUseWheelCommand; this.wheelCommand = wheelCommand;
        this.clipboardGesture = clipboardGesture;
        this.onClipboardFlip = onClipboardFlip;
        this.canClipboardFlip = canClipboardFlip;
        callback = Process;
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
    internal void Reset()
    {
        gate.Reset();
        tabGate.Reset();
        presetGate.Reset();
        spaceGate.Reset();
        zipGate.Reset();
        clipboardGate.Reset();
    }
    internal void ReserveRepeatsForGesture() => gate.ReserveRepeatsForGesture();
    nint Process(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            try
            {
                var key = Marshal.PtrToStructure<KeyEvent>(data);
                ushort rawFlags = (ushort)((key.Flags & 1) != 0 ? 2 : 0);
                bool down = message is 0x100 or 0x104;

                if (clipboardGesture != null && onClipboardFlip != null)
                {
                    var gesture = clipboardGesture();
                    if (gesture.Enabled && key.Key == (uint)gesture.VirtualKey)
                    {
                        bool ctrlHeld = Native.GetAsyncKeyState(0x11) < 0;
                        bool shiftHeld = Native.GetAsyncKeyState(0x10) < 0;
                        bool altHeld = Native.GetAsyncKeyState(0x12) < 0;
                        bool winHeld = Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0;
                        bool matches = gesture.Matches((int)key.Key, ctrlHeld, shiftHeld, altHeld, winHeld);

                        bool canCapture = down && !clipboardGate.IsDown && matches && (canClipboardFlip == null || canClipboardFlip());
                        if (clipboardGate.IsDown || canCapture)
                        {
                            var decision = clipboardGate.Process(down, canCapture);
                            if (decision.Activate) onClipboardFlip();
                            return decision.Consume ? 1 : CallNextHookEx(handle, code, message, data);
                        }
                    }
                }

                bool isTrigger = shortcut().Matches((ushort)key.Key, rawFlags, (ushort)key.Scan);
                // Tab: gira la ruota · A: preset · Spazio (VK_SPACE): FLIP QuickLook · Z (VK_Z): + ARCHIVIO .ZIP
                var commandGate = key.Key switch { 0x09 => tabGate, 0x41 => presetGate, 0x20 => spaceGate, 0x5A => zipGate, _ => null };
                if (commandGate != null && (!isTrigger || !gate.IsDown))
                {
                    var command = key.Key switch
                    {
                        0x09 => WheelCommand.FlipFace,
                        0x41 => WheelCommand.NextPreset,
                        0x20 => WheelCommand.QuickLook,
                        _ => WheelCommand.ToggleZip
                    };
                    bool canCapture = down && !commandGate.IsDown && !ModifiersHeld() && canUseWheelCommand(command);
                    if (commandGate.IsDown || !isTrigger || canCapture)
                    {
                        var decision = commandGate.Process(down, canCapture);
                        if (decision.Activate) wheelCommand(command);
                        return decision.Consume ? 1 : CallNextHookEx(handle, code, message, data);
                    }
                }

                if (isTrigger)
                {
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
