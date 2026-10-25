namespace Tasky;

/// <param name="Aumid">App User Model ID for packaged (Store/MSIX) apps, whose exe has no usable icon.</param>
internal sealed record AppWindow(IntPtr Hwnd, string Title, string ExePath, string? Aumid)
{
    /// <summary>What windows are grouped by, and what a pin of this app matches.</summary>
    public string GroupKey => Aumid ?? ExePath;

    /// <summary>A packaged app must be launched through the Apps folder, not its exe.</summary>
    public string LaunchPath => Aumid != null ? AppsFolder + Aumid : ExePath;

    public const string AppsFolder = @"shell:AppsFolder\";
}

/// <summary>Finds the windows that belong on a taskbar (roughly the Alt+Tab rules).</summary>
internal static class WindowEnumerator
{
    static readonly HashSet<string> ExcludedClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>
    /// What each Store-app frame was hosting when we last saw its content. A minimized Store app's
    /// content detaches from the frame, which would otherwise show up as "Application Frame Host".
    /// </summary>
    static readonly Dictionary<IntPtr, (string Path, string? Aumid)> FrameContent = new();

    /// <summary>App windows in z-order, topmost (most recently used) first.</summary>
    public static List<AppWindow> GetAppWindows(uint ownPid)
    {
        var list = new List<AppWindow>();
        Native.EnumWindows((h, _) =>
        {
            if (IsAppWindow(h, ownPid) && GetProcess(h) is var (pid, path, isFrame))
            {
                var aumid = Native.GetAppUserModelId(pid);
                if (isFrame)
                {
                    if (aumid != null) FrameContent[h] = (path, aumid);
                    else if (FrameContent.TryGetValue(h, out var seen)) (path, aumid) = seen;
                    else aumid = Native.GetWindowAppUserModelId(h); // frames carry the app's ID too
                }
                list.Add(new AppWindow(h, Native.GetText(h), path, aumid));
            }
            return true;
        }, IntPtr.Zero);

        foreach (var gone in FrameContent.Keys.Where(k => !Native.IsWindow(k)).ToList()) FrameContent.Remove(gone);
        return list;
    }

    /// <summary>Whether any app window (not minimized, on this desktop) has its visible frame inside <paramref name="area"/>.</summary>
    public static bool AnyAppWindowIntersects(Native.RECT area, uint ownPid)
    {
        bool found = false;
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsIconic(h) || !IsAppWindow(h, ownPid)) return true;
            // The visible frame, without the invisible resize borders GetWindowRect includes.
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, 16) != 0
                && !Native.GetWindowRect(h, out r))
                return true;
            found = r.Left < area.Right && r.Right > area.Left && r.Top < area.Bottom && r.Bottom > area.Top;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    static bool IsAppWindow(IntPtr h, uint ownPid)
    {
        if (!Native.IsWindowVisible(h)) return false;

        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
        }

        // Cloaked windows are suspended UWP frames or live on another virtual desktop.
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        if (Native.GetWindowTextLength(h) == 0) return false;
        if (ExcludedClasses.Contains(Native.GetClass(h))) return false;

        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid != ownPid;
    }

    /// <summary>The process that owns the window's content, its exe path, and whether it's a Store-app frame.</summary>
    static (uint Pid, string Path, bool IsFrame)? GetProcess(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        var path = Native.GetProcessPath(pid);
        if (path == null) return null;
        uint owner = pid;

        // Store apps are hosted by ApplicationFrameHost; the real app owns a child window.
        bool isFrame = path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        if (isFrame)
        {
            Native.EnumChildWindows(h, (child, _) =>
            {
                Native.GetWindowThreadProcessId(child, out uint childPid);
                if (childPid != pid && Native.GetProcessPath(childPid) is { } real)
                {
                    path = real;
                    owner = childPid;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }
        return (owner, path, isFrame);
    }
}
