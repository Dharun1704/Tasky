using System.Diagnostics;
using System.IO;

namespace Tasky;

/// <param name="Aumid">App User Model ID: for packaged (Store/MSIX) apps and installed web apps.</param>
/// <param name="Shortcut">A Start menu shortcut that stands for the app, when its exe can't name itself (games).</param>
internal sealed record AppWindow(IntPtr Hwnd, string Title, string ExePath, string? Aumid, string? Shortcut = null)
{
    /// <summary>
    /// What windows are grouped by, and what a pin of this app matches. A window whose program can't
    /// be identified at all (a system process such as the UAC prompt) is grouped on its own.
    /// </summary>
    public string GroupKey => KeyOverride ?? Aumid ?? Shortcut ?? (ExePath.Length > 0 ? ExePath : WindowKeyPrefix + Hwnd.ToInt64());

    /// <summary>Set for windows grouped by something other than their app (the permission prompt).</summary>
    public string? KeyOverride { get; init; }

    /// <summary>What to start for a new window: packaged apps through the Apps folder, games through their shortcut.</summary>
    public string LaunchPath => Aumid != null ? AppsFolder + Aumid : Shortcut ?? ExePath;

    public const string AppsFolder = @"shell:AppsFolder\";
    public const string WindowKeyPrefix = "window:";
}

/// <summary>Finds the windows that belong on a taskbar (roughly the Alt+Tab rules) and works out which app each is.</summary>
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

    /// <summary>Exe path and package ID per process, looked up once instead of on every refresh.</summary>
    static readonly Dictionary<uint, (string? Path, string? Aumid)> ProcessInfo = new();

    /// <summary>The app ID each window declares, with when it was read (an empty answer is asked again later).</summary>
    static readonly Dictionary<IntPtr, (string? Id, DateTime At)> WindowIds = new();

    /// <summary>
    /// App windows in z-order, topmost (most recently used) first. <paramref name="announced"/> are
    /// windows Windows told us belong on the taskbar (via the shell hook) even though they're hidden:
    /// the background UAC prompt is one.
    /// </summary>
    public static List<AppWindow> GetAppWindows(uint ownPid, IReadOnlySet<IntPtr> announced)
    {
        var list = new List<AppWindow>();
        var seenPids = new HashSet<uint>();
        Native.EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h, ownPid, announced)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            seenPids.Add(pid);
            list.Add(Identify(h, pid));
            return true;
        }, IntPtr.Zero);

        foreach (var gone in FrameContent.Keys.Where(k => !Native.IsWindow(k)).ToList()) FrameContent.Remove(gone);
        foreach (var gone in WindowIds.Keys.Where(k => !Native.IsWindow(k)).ToList()) WindowIds.Remove(gone);
        foreach (var gone in ProcessInfo.Keys.Where(p => !seenPids.Contains(p)).ToList()) ProcessInfo.Remove(gone);
        return list;
    }

    /// <summary>consent.exe while a permission prompt is open (set by the dock from <see cref="ConsentWatcher"/>).</summary>
    public static uint? ConsentPid { get; set; }

    /// <summary>The one dock item that stands for the permission prompt, however Windows shows it.</summary>
    public const string UacKey = "uac:consent";

    static AppWindow Identify(IntPtr h, uint pid)
    {
        var title = Native.GetText(h);
        if (pid == ConsentPid) return new AppWindow(h, title, "", null) { KeyOverride = UacKey };
        var (path, aumid) = Process(pid);

        // Store apps are hosted by ApplicationFrameHost; the real app owns a child window.
        if (path != null && path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var content = FrameChild(h, pid);
            if (content is { } c)
            {
                (path, aumid) = Process(c);
                if (aumid != null) FrameContent[h] = (path ?? "", aumid);
            }
            else if (FrameContent.TryGetValue(h, out var seen)) (path, aumid) = seen;
            aumid ??= WindowId(h); // frames carry the app's ID too
        }

        // Installed web apps (Netflix, YouTube...) run inside the browser's process but declare their
        // own app ID on their window. Browser windows declare one too ("MSEdge"), so only IDs that
        // name an installed app count: a package's "family!app" or a Chromium web app's "_crx_".
        if (WindowId(h) is { } windowId && (windowId.Contains('!') || windowId.Contains("_crx_", StringComparison.OrdinalIgnoreCase)))
            aumid = windowId;

        // Games and the like whose exe has no name: use the Start menu shortcut named like the window.
        string? shortcut = null;
        if (aumid == null && path != null && !ExeNames.HasName(path)) shortcut = Shortcuts.Named(title);

        return new AppWindow(h, title, path ?? "", aumid, shortcut);
    }

    static (string? Path, string? Aumid) Process(uint pid)
    {
        if (!ProcessInfo.TryGetValue(pid, out var info))
            ProcessInfo[pid] = info = (Native.GetProcessPath(pid), Native.GetAppUserModelId(pid));
        return info;
    }

    static string? WindowId(IntPtr h)
    {
        if (WindowIds.TryGetValue(h, out var known) && (known.Id != null || DateTime.UtcNow - known.At < TimeSpan.FromSeconds(10)))
            return known.Id;
        var id = Native.GetWindowAppUserModelId(h);
        WindowIds[h] = (id, DateTime.UtcNow);
        return id;
    }

    static uint? FrameChild(IntPtr frame, uint framePid)
    {
        uint? found = null;
        Native.EnumChildWindows(frame, (child, _) =>
        {
            Native.GetWindowThreadProcessId(child, out uint childPid);
            if (childPid == framePid) return true;
            found = childPid;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Whether any app window (not minimized, on this desktop) has its visible frame inside <paramref name="area"/>.</summary>
    public static bool AnyAppWindowIntersects(Native.RECT area, uint ownPid)
    {
        bool found = false;
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsIconic(h) || !IsAppWindow(h, ownPid, None)) return true;
            // The visible frame, without the invisible resize borders GetWindowRect includes.
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, 16) != 0
                && !Native.GetWindowRect(h, out r))
                return true;
            found = r.Left < area.Right && r.Right > area.Left && r.Top < area.Bottom && r.Bottom > area.Top;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    static readonly HashSet<IntPtr> None = new();

    static bool IsAppWindow(IntPtr h, uint ownPid, IReadOnlySet<IntPtr> announced)
    {
        if (!Native.IsWindowVisible(h) && !announced.Contains(h)) return false;

        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow && !announced.Contains(h))
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

    /// <summary>
    /// The User Account Control prompt. When an app that isn't in front asks for permission, Windows
    /// shows only a taskbar button for it (a hidden window of consent.exe) until it's clicked.
    /// </summary>
    public static bool IsUac(AppWindow w) =>
        w.KeyOverride == UacKey
        || w.ExePath.EndsWith(@"\consent.exe", StringComparison.OrdinalIgnoreCase)
        || Native.GetClass(w.Hwnd).Contains("Secure UAP", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Whether an exe carries a name of its own (file description or product name).</summary>
internal static class ExeNames
{
    static readonly Dictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasName(string exePath)
    {
        if (Known.TryGetValue(exePath, out var has)) return has;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            has = !string.IsNullOrWhiteSpace(info.FileDescription) || !string.IsNullOrWhiteSpace(info.ProductName);
        }
        catch { has = true; } // unreadable: leave it to the usual naming
        return Known[exePath] = has;
    }
}

/// <summary>Start menu shortcuts by name, re-read every few minutes as apps are installed.</summary>
internal static class Shortcuts
{
    static Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
    static DateTime builtAt;

    public static string? Named(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (DateTime.UtcNow - builtAt > TimeSpan.FromMinutes(5)) Build();
        return byName.GetValueOrDefault(title.Trim());
    }

    static void Build()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(Environment.GetFolderPath(root), "*.lnk", SearchOption.AllDirectories))
                    index.TryAdd(Path.GetFileNameWithoutExtension(lnk), lnk);
            }
            catch (Exception ex) { Log.Error(ex, "reading Start menu shortcuts"); }
        }
        byName = index;
        builtAt = DateTime.UtcNow;
    }
}
