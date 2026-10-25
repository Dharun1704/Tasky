using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Tasky;

/// <summary>Which apps are on the dock: finding their windows, pins, names, icons and attention.</summary>
public partial class MainWindow
{
    // ---- Tracking windows -------------------------------------------------------------------

    void ScheduleRefresh()
    {
        refreshSoon.Stop();
        refreshSoon.Start();
    }

    void Refresh()
    {
        announced.RemoveWhere(h => !Native.IsWindow(h));
        var windows = WindowEnumerator.GetAppWindows(ownPid, announced);
        TrackForeground();

        var groups = new Dictionary<string, List<AppWindow>>(StringComparer.OrdinalIgnoreCase);
        var groupOrder = new List<string>();
        foreach (var w in windows)
        {
            if (!groups.TryGetValue(w.GroupKey, out var list))
            {
                groups[w.GroupKey] = list = new List<AppWindow>();
                groupOrder.Add(w.GroupKey);
            }
            list.Add(w);
        }

        // Pinned apps first, in pin order; then running apps, keeping the place they already had.
        var desired = new List<DockItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in settings.Pins)
        {
            var key = PinKey(pin);
            if (used.Add(key)) desired.Add(GetOrCreate(key, pin, true, groups.GetValueOrDefault(key)));
        }
        foreach (var item in items)
            if (!item.IsPinned && groups.ContainsKey(item.Key) && used.Add(item.Key)) desired.Add(item);
        // A permission prompt goes first among the running apps (so it can never end up in the overflow
        // menu), even if Windows didn't show us any of its windows: consent.exe running is enough.
        if (consentPid != null || groups.ContainsKey(WindowEnumerator.UacKey))
        {
            var uacWindows = groups.GetValueOrDefault(WindowEnumerator.UacKey) ?? new List<AppWindow>();
            groups[WindowEnumerator.UacKey] = uacWindows;
            if (used.Add(WindowEnumerator.UacKey))
                desired.Insert(desired.Count(d => d.IsPinned), GetOrCreate(WindowEnumerator.UacKey, "", false, uacWindows));
        }
        foreach (var key in groupOrder)
            if (used.Add(key)) desired.Add(GetOrCreate(key, groups[key][0].LaunchPath, false, groups[key]));

        foreach (var item in desired)
        {
            var ws = groups.GetValueOrDefault(item.Key) ?? new List<AppWindow>();
            item.Windows.RemoveAll(old => !ws.Any(w => w.Hwnd == old.Hwnd));
            foreach (var w in ws)
            {
                int i = item.Windows.FindIndex(old => old.Hwnd == w.Hwnd);
                if (i >= 0) item.Windows[i] = w;
                else item.Windows.Add(w);
            }
            item.TopWindow = ws.Count > 0 ? ws[0].Hwnd : IntPtr.Zero;
            bool isUac = item.Key == WindowEnumerator.UacKey || ws.Any(WindowEnumerator.IsUac);
            item.IsRunning = ws.Count > 0 || isUac;
            item.IsActive = ws.Any(w => w.Hwnd == lastForeground);
            if (item.IsActive || !item.IsRunning) item.IsAttention = false;
            else if (isUac) item.IsAttention = true; // waiting for your permission
            if (item.Icon == null && ws.Count > 0) item.Icon = IconLoader.FromWindow(ws[0].Hwnd);
        }

        // Apps that don't fit on screen even at the smallest size: the last ones (running, not pinned)
        // move to the overflow menu.
        int hidden = Math.Clamp(overflowCount, 0, Math.Max(0, desired.Count - 1));
        overflowItems.Clear();
        overflowItems.AddRange(desired.Skip(desired.Count - hidden));
        OverflowButton.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        Sync(desired.Take(desired.Count - hidden).ToList());
        ApplyAttention();
        ScheduleFit();
        foreach (var key in itemsByKey.Keys.Where(k => !used.Contains(k)).ToList())
        {
            if (itemsByKey[key].IconPath is { } path) IconLoader.Forget(path); // the app left the dock
            itemsByKey.Remove(key);
        }
    }

    DockItem GetOrCreate(string key, string launchPath, bool pinned, List<AppWindow>? windows)
    {
        if (!itemsByKey.TryGetValue(key, out var item))
        {
            var first = windows is { Count: > 0 } ? windows[0] : null;
            if (key == WindowEnumerator.UacKey || (first != null && WindowEnumerator.IsUac(first)))
            {
                // The UAC prompt's program can't be read; show it the way the taskbar does.
                item = new DockItem(key, "") { Name = "User Account Control", Size = BaseSize, Icon = IconLoader.Shield() };
            }
            else if (launchPath.Length == 0)
            {
                // A window whose program can't be identified: name and icon come from the window.
                item = new DockItem(key, "") { Name = first?.Title ?? "", Size = BaseSize, Icon = first != null ? IconLoader.FromWindow(first.Hwnd) : null };
            }
            else
            {
                // An exe without a name of its own is better known by its window title.
                string name = first != null && launchPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                              && !ExeNames.HasName(launchPath) && first.Title.Length > 0
                    ? first.Title
                    : DisplayName(launchPath);
                item = new DockItem(key, launchPath) { Name = name, Size = BaseSize, IconPath = launchPath };
                UpdateIcon(item);
                if (item.Icon != null) PreloadIcons(item);
                else
                {
                    item.IconPath = null;
                    item.Icon = first != null ? IconLoader.FromWindow(first.Hwnd) : null;
                }
            }
            itemsByKey[key] = item;
        }
        item.IsPinned = pinned;
        if (pinned) item.LaunchPath = launchPath;
        return item;
    }

    /// <summary>Swaps in the icon rendered for the size the item is currently drawn at.</summary>
    void UpdateIcon(DockItem item)
    {
        if (item.IconPath == null) return;
        int px = IconLoader.Bucket(item.Size * VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (px == item.IconPx) return;
        item.IconPx = px;
        item.Icon = IconLoader.FromPath(item.IconPath, px) ?? item.Icon;
    }

    /// <summary>Loads every magnified size in idle time so the first hover doesn't stutter.</summary>
    void PreloadIcons(DockItem item)
    {
        if (item.IconPath is not { } path) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int from = IconLoader.Bucket(BaseSize * s), to = IconLoader.Bucket(BaseSize * MaxScale * s);
        Dispatcher.InvokeAsync(() =>
        {
            for (int px = from; px <= to; px += IconLoader.Step) IconLoader.FromPath(path, px);
            IconLoader.ReleaseSource(path); // every size it can be drawn at is cached now
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Moves/inserts/removes so existing icons keep their visuals and animation state.</summary>
    void Sync(List<DockItem> desired)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], desired[i])) continue;
            int old = items.IndexOf(desired[i]);
            if (old >= 0) items.Move(old, i);
            else items.Insert(i, desired[i]);
        }
        while (items.Count > desired.Count) items.RemoveAt(items.Count - 1);
    }

    void TrackForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid != ownPid) lastForeground = fg;
    }

    const int HSHELL_WINDOWCREATED = 1, HSHELL_WINDOWDESTROYED = 2;
    readonly HashSet<IntPtr> announced = new();
    /// <summary>Windows that flashed for attention since the last refresh (they may not be on the dock yet).</summary>
    readonly HashSet<IntPtr> pendingAttention = new();

    /// <summary>
    /// Marks apps whose window flashed (or that are asking for elevation) and brings a hidden dock
    /// out for them: with the Windows taskbar hidden, the dock is the only place they'd show.
    /// </summary>
    void ApplyAttention()
    {
        foreach (var h in pendingAttention)
            foreach (var item in items.Concat(overflowItems))
                if (h != lastForeground && item.Windows.Any(w => w.Hwnd == h)) item.IsAttention = true;
        pendingAttention.Clear();

        bool wanted = items.Concat(overflowItems).Any(i => i.IsAttention);
        if (wanted && !attentionShown) SetAutoHidden(false);
        attentionShown = wanted;
    }

    bool attentionShown;

    bool IsFullscreenForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || Native.IsZoomed(fg) || IsShellUi(fg)) return false;
        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == ownPid) return false;
        if (Native.GetClass(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var m = DockMonitor().rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>
    /// Pins match windows by exe path; a shortcut pin matches the exe it points at,
    /// and a packaged-app pin (shell:AppsFolder\AUMID) matches by AUMID.
    /// </summary>
    string PinKey(string pin)
    {
        if (pinKeys.TryGetValue(pin, out var key)) return key;
        key = pin;
        if (pin.StartsWith(AppWindow.AppsFolder, StringComparison.OrdinalIgnoreCase))
            key = pin[AppWindow.AppsFolder.Length..];
        else if (pin.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ResolveShortcut(pin) is { Arguments: "" } target
            && target.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            key = target.Path; // a shortcut with arguments (a game through its launcher) stays its own app
        return pinKeys[pin] = key;
    }

    static (string Path, string Arguments)? ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = shortcut.TargetPath;
                string arguments = shortcut.Arguments ?? "";
                return string.IsNullOrEmpty(target) ? null : (target, arguments.Trim());
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    /// <summary>
    /// Descriptions that name the framework an app is built on rather than the app: Electron apps
    /// (GitHub Desktop, Discord…) often ship with "Electron" as their file description.
    /// </summary>
    static readonly HashSet<string> FrameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Electron", "Chromium", "Google Chrome", "CefSharp", "NW.js", "Node.js", "Tauri App",
        "Java(TM) Platform SE binary", "OpenJDK Platform binary", "Python", "Qt",
    };

    static string DisplayName(string path)
    {
        try
        {
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                // The file description is what the taskbar shows; fall back to the product name when the
                // description is just the framework's placeholder.
                foreach (var name in new[] { info.FileDescription, info.ProductName })
                    if (!string.IsNullOrWhiteSpace(name) && !FrameworkNames.Contains(name.Trim())) return name.Trim();
            }
        }
        catch { }
        return Native.GetShellDisplayName(path) ?? Path.GetFileNameWithoutExtension(path);
    }
}
