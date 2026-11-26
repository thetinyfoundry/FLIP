using System.Runtime.InteropServices;

namespace Flip;

internal static class Native
{
    private const int VkLeftButton = 0x01, VkRightButton = 0x02, SmSwapButton = 23;
    private const ushort LeftButtonDown = 0x0001, LeftButtonUp = 0x0002;
    private const ushort RightButtonDown = 0x0004, RightButtonUp = 0x0008;

    internal static volatile bool AcceptSecondaryDragButton = false;

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Device { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] internal struct Header { public uint Type, Size; public nint Device, Param; }
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, Extra; }

    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    internal static bool ButtonsSwapped => GetSystemMetrics(SmSwapButton) != 0;
    internal static int PrimaryVirtualKey(bool swapped) => swapped ? VkRightButton : VkLeftButton;

    internal static bool IsDragButtonHeld
    {
        get
        {
            bool swapped = ButtonsSwapped;
            return GetAsyncKeyState(PrimaryVirtualKey(swapped)) < 0 ||
                (AcceptSecondaryDragButton && GetAsyncKeyState(PrimaryVirtualKey(!swapped)) < 0);
        }
    }

    internal static bool IsPrimaryMousePressed() =>
        (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0 ||
        (GetAsyncKeyState(VkRightButton) & 0x8000) != 0;

    internal static bool PrimaryDown(ushort flags, bool swapped) =>
        MatchesDragButton(flags, swapped, LeftButtonDown, RightButtonDown);

    internal static bool PrimaryUp(ushort flags, bool swapped) =>
        MatchesDragButton(flags, swapped, LeftButtonUp, RightButtonUp);

    private static bool MatchesDragButton(ushort flags, bool swapped, ushort left, ushort right)
    {
        int mask = AcceptSecondaryDragButton ? left | right : (swapped ? right : left);
        return (flags & mask) != 0;
    }
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    [DllImport("user32.dll")] internal static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetTopWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint hwnd, System.Text.StringBuilder name, int capacity);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint newLong);

    internal static void SetWindowNoActivate(nint hwnd)
    {
        if (hwnd == 0) return;
        const int GWL_EXSTYLE = -20;
        const nint WS_EX_TOOLWINDOW = 0x00000080;
        const nint WS_EX_NOACTIVATE = 0x08000000;
        nint ex = GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    internal static (int X, int Y, int Size) Clamp(int x, int y, int size, Rect area)
    {
        size = Math.Min(size, Math.Min(area.Right - area.Left, area.Bottom - area.Top));
        return (Math.Clamp(x - size / 2, area.Left, area.Right - size),
            Math.Clamp(y - size / 2, area.Top, area.Bottom - size), size);
    }

    internal readonly record struct PlacementResult(int X, int Y, int Size, nint Monitor, MonitorInfo Info);

    internal static uint GetMonitorDpi(nint monitor, nint hwndFallback = 0)
    {
        try
        {
            if (monitor != 0 && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX >= 72)
                return dpiX;
        }
        catch { }

        if (hwndFallback != 0)
        {
            uint winDpi = GetDpiForWindow(hwndFallback);
            if (winDpi >= 72) return winDpi;
        }
        return 96;
    }

    internal static PlacementResult PlaceNearCursor(
        nint hwnd,
        bool centered = false,
        int verticalBiasDip = -48,
        double dipSize = 320.0,
        double? physicalScale = null)
    {
        GetCursorPos(out var point);
        nint monitor = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Monitor lookup failed.");

        uint monitorDpi = GetMonitorDpi(monitor, hwnd);
        double scale = physicalScale is > 0.25 and < 8.0 ? physicalScale.Value : monitorDpi / 96.0;
        int anchorX = centered ? (info.Work.Left + info.Work.Right) / 2 : point.X;
        int anchorY = centered
            ? (info.Work.Top + info.Work.Bottom) / 2 + (int)Math.Round(verticalBiasDip * scale)
            : point.Y;
        int sizePx = Math.Max(1, (int)Math.Round(dipSize * scale));
        var placement = Clamp(anchorX, anchorY, sizePx, info.Work);
        if (!SetWindowPos(hwnd, new nint(-1), placement.X, placement.Y, placement.Size, placement.Size, 0x10))
            throw new InvalidOperationException("Window placement failed.");

        // Dopo lo spostamento sul monitor di destinazione, verifica se il DPI effettivo dell'HWND differisce.
        if (physicalScale == null)
        {
            uint actualDpi = GetDpiForWindow(hwnd);
            if (actualDpi >= 72 && actualDpi != monitorDpi)
            {
                double actualScale = actualDpi / 96.0;
                int adjustedY = centered
                    ? (info.Work.Top + info.Work.Bottom) / 2 + (int)Math.Round(verticalBiasDip * actualScale)
                    : point.Y;
                int adjustedSize = Math.Max(1, (int)Math.Round(dipSize * actualScale));
                var adjusted = Clamp(anchorX, adjustedY, adjustedSize, info.Work);
                if (adjusted != placement)
                {
                    placement = adjusted;
                    if (!SetWindowPos(hwnd, new nint(-1), placement.X, placement.Y, placement.Size, placement.Size, 0x10))
                        throw new InvalidOperationException("Window placement failed.");
                }
            }
        }

        Evidence.Log(centered ? "placement.centered" : "placement", new { point, anchorX, anchorY, monitorDpi, scale, placement, work = info.Work });
        return new PlacementResult(placement.X, placement.Y, placement.Size, monitor, info);
    }

    /// <summary>
    /// Posiziona una finestra in basso a destra nell'area di lavoro del monitor del cursore, in pixel fisici
    /// (le dimensioni arrivano in DIP e vengono scalate col DPI di quel monitor).
    /// </summary>
    internal static void PlaceBottomRightOnCursorMonitor(nint hwnd, double widthDip, double heightDip, double marginDip)
    {
        GetCursorPos(out var point);
        nint monitor = MonitorFromPoint(point, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Monitor lookup failed.");

        double scale = GetMonitorDpi(monitor, hwnd) / 96.0;
        int width = Math.Max(1, (int)Math.Round(widthDip * scale));
        int height = Math.Max(1, (int)Math.Round(heightDip * scale));
        int margin = (int)Math.Round(marginDip * scale);
        int x = Math.Max(info.Work.Left, info.Work.Right - width - margin);
        int y = Math.Max(info.Work.Top, info.Work.Bottom - height - margin);
        // SWP_NOACTIVATE (0x10): la notifica non ruba mai il focus.
        if (!SetWindowPos(hwnd, new nint(-1), x, y, width, height, 0x10))
            throw new InvalidOperationException("Window placement failed.");
    }

    internal static void Position(nint hwnd) => _ = PlaceNearCursor(hwnd, centered: false);

    internal static void PositionCentered(nint hwnd, int verticalBiasDip = -48) =>
        _ = PlaceNearCursor(hwnd, centered: true, verticalBiasDip: verticalBiasDip);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    internal static void EnableDarkMode(nint hwnd)
    {
        try
        {
            int trueValue = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueValue, sizeof(int));
        }
        catch { }
    }
}
