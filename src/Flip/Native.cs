using System.Runtime.InteropServices;

namespace Flip;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Device { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] internal struct Header { public uint Type, Size; public nint Device, Param; }
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, Extra; }

    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    internal static bool ButtonsSwapped => GetSystemMetrics(23) != 0; // SM_SWAPBUTTON
    internal static int PrimaryVirtualKey(bool swapped) => swapped ? 2 : 1;
    internal static bool PrimaryDown(ushort flags, bool swapped) => (flags & (swapped ? 4 : 1)) != 0;
    internal static bool PrimaryUp(ushort flags, bool swapped) => (flags & (swapped ? 8 : 2)) != 0;
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    [DllImport("user32.dll")] internal static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    internal static (int X, int Y, int Size) Clamp(int x, int y, int size, Rect area)
    {
        size = Math.Min(size, Math.Min(area.Right - area.Left, area.Bottom - area.Top));
        return (Math.Clamp(x - size / 2, area.Left, area.Right - size),
            Math.Clamp(y - size / 2, area.Top, area.Bottom - size), size);
    }

    internal static void Position(nint hwnd)
    {
        GetCursorPos(out var point);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(point, 2), ref info)) throw new InvalidOperationException("Monitor lookup failed.");
        // First move to destination monitor, then read actual HWND DPI (physical pixels).
        SetWindowPos(hwnd, new nint(-1), point.X, point.Y, 0, 0, 0x11);
        var dpi = GetDpiForWindow(hwnd);
        var placement = Clamp(point.X, point.Y, (int)Math.Round(320 * dpi / 96.0), info.Work);
        if (!SetWindowPos(hwnd, new nint(-1), placement.X, placement.Y, placement.Size, placement.Size, 0x10))
            throw new InvalidOperationException("Window placement failed.");
        Evidence.Log("placement", new { point, dpi, placement, work = info.Work });
    }
}
