using System.Runtime.InteropServices;

namespace Tasky;

/// <summary>
/// A bare Win32 window that sits exactly behind the dock's pill and shows acrylic blur.
/// The dock itself can't blur: it's a per-pixel-transparent layered window, and DWM blur
/// would cover its whole transparent rectangle rather than just the pill. Win11's official
/// system backdrops are out too: they turn solid grey on windows that never take focus.
/// </summary>
internal sealed class BackdropWindow
{
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    static readonly WndProc Proc = DefWindowProc; // kept in a field so the GC can't collect the thunk
    static readonly string ClassName = "Tasky.Backdrop";

    Native.RECT placed;
    bool shown;

    public IntPtr Handle { get; }

    public BackdropWindow()
    {
        var hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            hInstance = hInstance,
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);

        Handle = CreateWindowEx(WS_EX_TOPMOST | WS_EX_LAYERED |(uint)Native.WS_EX_TOOLWINDOW | (uint)Native.WS_EX_NOACTIVATE,
            ClassName, "Tasky backdrop", WS_POPUP, -10000, -10000, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        int border = Native.DWMWA_COLOR_NONE;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        SetCorners(8);

        // Black is keyed out, so the window's own content vanishes and only the DWM blur remains.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_COLORKEY);
        Native.EnableAcrylic(Handle);
    }

    /// <summary>
    /// DWM ignores window regions for blur, so the shape can only be one of Windows 11's corner
    /// styles: square, small (4px) or standard (8px). Picks the closest to <paramref name="radius"/>
    /// (DIPs) and returns the radius actually used, for the dock to match its pill to.
    /// </summary>
    public double SetCorners(double radius)
    {
        var (preference, used) = radius switch
        {
            < 2 => (DWMWCP_DONOTROUND, 0.0),
            < 6 => (DWMWCP_ROUNDSMALL, 4.0),
            _ => (Native.DWMWCP_ROUND, 8.0),
        };
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        return used;
    }

    const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUNDSMALL = 3;

    /// <summary>Moves the blur to <paramref name="r"/> (screen pixels).</summary>
    public void Place(Native.RECT r)
    {
        if (Handle == IntPtr.Zero) return;
        SetShown(true);
        if (r.Left == placed.Left && r.Top == placed.Top && r.Right == placed.Right && r.Bottom == placed.Bottom) return;
        placed = r;
        Native.SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public void SetShown(bool show)
    {
        if (Handle == IntPtr.Zero || show == shown) return;
        shown = show;
        Native.ShowWindow(Handle, show ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
    }

    const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000, LWA_COLORKEY = 0x1;
    const int BLACK_BRUSH = 4;

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int obj);
}
