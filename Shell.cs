using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tasky;

/// <summary>
/// SHAppBarMessage, but always delivered to Explorer. The real one sends to the first Shell_TrayWnd
/// in z-order, which is our tray service's window while the tray is on. That window forwards to
/// Explorer, but the shared memory carrying positions was created for our process, so Explorer
/// can't read it and QUERYPOS/SETPOS silently do nothing. Here the memory is made for Explorer.
/// </summary>
internal static class ExplorerAppBar
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA3264 // the 32/64-bit-neutral layout the shell uses on the wire
    {
        public uint cbSize, hWnd, uCallbackMessage, uEdge;
        public Native.RECT rc;
        public long lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARMSGDATA3264 // "V3": 64 bytes, with a 64-bit shared-memory handle at offset 48
    {
        public APPBARDATA3264 abd;
        public uint dwMessage, dwPadding1;
        public long hSharedMemory;
        public uint dwSourceProcessId, dwPadding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    // Exported from shlwapi by ordinal only.
    [DllImport("shlwapi.dll", EntryPoint = "#7")] static extern IntPtr SHAllocShared(ref APPBARDATA3264 data, uint size, uint processId);
    [DllImport("shlwapi.dll", EntryPoint = "#8")] static extern IntPtr SHLockShared(IntPtr handle, uint processId);
    [DllImport("shlwapi.dll", EntryPoint = "#9")] static extern bool SHUnlockShared(IntPtr view);
    [DllImport("shlwapi.dll", EntryPoint = "#10")] static extern bool SHFreeShared(IntPtr handle, uint processId);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);

    const uint WM_COPYDATA = 0x004A, ABM_GETTASKBARPOS = 5;

    public static UIntPtr Send(uint message, ref Native.APPBARDATA abd)
    {
        var tray = WindowsTaskbar.Tray;
        if (tray == IntPtr.Zero) return Native.SHAppBarMessage(message, ref abd); // no Explorer: let the shell try
        Native.GetWindowThreadProcessId(tray, out uint explorerPid);

        var msg = new APPBARMSGDATA3264
        {
            abd = new APPBARDATA3264
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA3264>(),
                hWnd = (uint)abd.hWnd.ToInt64(),
                uCallbackMessage = abd.uCallbackMessage,
                uEdge = abd.uEdge,
                rc = abd.rc,
                lParam = abd.lParam.ToInt64(),
            },
            dwMessage = message,
            dwSourceProcessId = (uint)Environment.ProcessId,
        };

        bool needsShared = message is Native.ABM_QUERYPOS or Native.ABM_SETPOS or ABM_GETTASKBARPOS;
        // The handle is made for Explorer's process, the one that has to read and fill it.
        uint owner = explorerPid;
        IntPtr shared = needsShared ? SHAllocShared(ref msg.abd, msg.abd.cbSize, owner) : IntPtr.Zero;
        msg.hSharedMemory = shared.ToInt64();

        int size = Marshal.SizeOf<APPBARMSGDATA3264>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(msg, buffer, false);
            var cds = new COPYDATASTRUCT { dwData = IntPtr.Zero, cbData = size, lpData = buffer };
            SendMessageTimeout(tray, WM_COPYDATA, abd.hWnd, ref cds, Native.SMTO_ABORTIFHUNG, 2000, out var result);

            if (shared != IntPtr.Zero)
            {
                var view = SHLockShared(shared, owner);
                if (view != IntPtr.Zero)
                {
                    abd.rc = Marshal.PtrToStructure<APPBARDATA3264>(view).rc; // Explorer's adjusted rectangle
                    SHUnlockShared(view);
                }
            }
            return (UIntPtr)(ulong)result.ToInt64();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (shared != IntPtr.Zero) SHFreeShared(shared, owner);
        }
    }
}

/// <summary>Registers the dock as an app bar so maximized windows stop above it.</summary>
internal sealed class AppBar(uint callbackMessage)
{
    IntPtr hwnd;
    bool registered;

    /// <summary>Reserves a <paramref name="thickness"/> px strip along one edge of the monitor; returns the strip.</summary>
    public Native.RECT Reserve(IntPtr window, Native.RECT monitor, int thickness, DockEdge edge)
    {
        hwnd = window;
        var abd = Data();
        if (!registered)
        {
            abd.uCallbackMessage = callbackMessage;
            ExplorerAppBar.Send(Native.ABM_NEW, ref abd);
            registered = true;
        }

        abd.uEdge = edge.AppBarEdge();
        abd.rc = monitor;
        Strip(ref abd.rc, edge, thickness);
        ExplorerAppBar.Send(Native.ABM_QUERYPOS, ref abd); // moves the strip clear of the Windows taskbar if it's showing
        Strip(ref abd.rc, edge, thickness);
        ExplorerAppBar.Send(Native.ABM_SETPOS, ref abd);
        return abd.rc;
    }

    /// <summary>Narrows <paramref name="rc"/> to the strip of <paramref name="thickness"/> on its <paramref name="edge"/> side.</summary>
    static void Strip(ref Native.RECT rc, DockEdge edge, int thickness)
    {
        switch (edge)
        {
            case DockEdge.Top: rc.Bottom = rc.Top + thickness; break;
            case DockEdge.Left: rc.Right = rc.Left + thickness; break;
            case DockEdge.Right: rc.Left = rc.Right - thickness; break;
            default: rc.Top = rc.Bottom - thickness; break;
        }
    }

    public void Release()
    {
        if (!registered) return;
        var abd = Data();
        ExplorerAppBar.Send(Native.ABM_REMOVE, ref abd);
        registered = false;
    }

    /// <summary>Explorer forgets app bars when it restarts.</summary>
    public void Forget() => registered = false;

    Native.APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = hwnd };
}

/// <summary>Controls the real Windows taskbar on the primary monitor.</summary>
internal static class WindowsTaskbar
{
    /// <summary>
    /// Explorer's taskbar. Not simply FindWindow: our own tray service registers a window of the same
    /// class and keeps it first in z-order so apps' tray icons reach the dock.
    /// </summary>
    public static IntPtr Tray
    {
        get
        {
            uint own = (uint)Environment.ProcessId;
            for (var h = Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); h != IntPtr.Zero;
                 h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null))
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid != own) return h;
            }
            return IntPtr.Zero;
        }
    }

    public static bool IsVisible => Native.IsWindowVisible(Tray);

    public static bool IsAutoHide
    {
        get
        {
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Tray };
            return ((ulong)ExplorerAppBar.Send(Native.ABM_GETSTATE, ref abd) & Native.ABS_AUTOHIDE) != 0;
        }
        set
        {
            var abd = new Native.APPBARDATA
            {
                cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Tray,
                lParam = value ? Native.ABS_AUTOHIDE : Native.ABS_ALWAYSONTOP,
            };
            ExplorerAppBar.Send(Native.ABM_SETSTATE, ref abd);
        }
    }

    public static void SetVisible(bool visible)
    {
        var tray = Tray;
        if (tray != IntPtr.Zero) Native.ShowWindow(tray, visible ? Native.SW_SHOWNA : Native.SW_HIDE);
    }

    /// <summary>
    /// Records "always show" as the taskbar's saved setting, which Explorer reads when it starts, without
    /// changing the running taskbar. After a restart (or a crash or power cut) the taskbar is there
    /// until Tasky starts and hides it again, rather than auto-hidden with nothing in its place.
    /// </summary>
    public static void SaveAlwaysShown()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", writable: true);
            if (key?.GetValue("Settings") is not byte[] data || data.Length <= 8 || (data[8] & Native.ABS_AUTOHIDE) == 0) return;
            data[8] &= unchecked((byte)~Native.ABS_AUTOHIDE);
            key.SetValue("Settings", data, RegistryValueKind.Binary);
        }
        catch (Exception ex) { Log.Error(ex, "saving the taskbar setting"); }
    }
}

/// <summary>A Tasky shortcut in the Start menu's program list.</summary>
internal static class StartMenuShortcut
{
    static string LinkPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Tasky.lnk");

    public static bool Exists => System.IO.File.Exists(LinkPath);

    public static void Create()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(LinkPath);
                link.TargetPath = Environment.ProcessPath;
                link.WorkingDirectory = System.IO.Path.GetDirectoryName(Environment.ProcessPath);
                link.Description = "A floating dock for Windows 11";
                link.Save();
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception ex) { Log.Error(ex, "creating Start menu shortcut"); }
    }

    public static void Remove()
    {
        try { System.IO.File.Delete(LinkPath); }
        catch (Exception ex) { Log.Error(ex, "removing Start menu shortcut"); }
    }

    /// <summary>Re-points an existing shortcut at this exe after a move or a publish, like AutoStart.Repair.</summary>
    public static void Repair()
    {
        if (Exists && !(Environment.ProcessPath ?? "").Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)) Create();
    }
}

internal static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Points an existing startup entry at this exe when the one it names is gone or is a development
    /// (bin\Debug) build, e.g. after publishing. A dev build never takes the entry over from a real install.
    /// </summary>
    public static void Repair()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue("Tasky") is not string registered) return;
            string current = Environment.ProcessPath ?? "";
            string target = registered.Trim('"');
            bool stale = !System.IO.File.Exists(target) || target.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase);
            bool currentIsInstall = !current.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
            if (stale && currentIsInstall && !string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
                key.SetValue("Tasky", $"\"{current}\"");
        }
        catch (Exception ex) { Log.Error(ex, "repairing startup entry"); }
    }

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Tasky") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Tasky", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Tasky", false);
        }
    }
}
