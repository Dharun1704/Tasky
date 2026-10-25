using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;

namespace Tasky;

public partial class MainWindow : Window
{
    const double MagnifyRange = 2.2;  // how many neighbouring icons the magnify wave reaches
    const double Headroom = 44;       // room above magnified icons for the name label and launch bounce

    readonly Settings settings = Settings.Load();
    readonly ObservableCollection<DockItem> items = new();
    readonly Dictionary<string, DockItem> itemsByKey = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> pinKeys = new(StringComparer.OrdinalIgnoreCase);
    readonly uint ownPid = (uint)Environment.ProcessId;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer refreshSoon = new() { Interval = TimeSpan.FromMilliseconds(60) };
    readonly BackdropWindow backdrop = new();
    readonly DispatcherTimer autoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly TrayHost tray;
    FrameworkElement? trayHover; // dock tray icon under the mouse, for the name label

    IntPtr hwnd, lastForeground;
    uint shellHookMsg, appBarMsg, taskbarCreatedMsg;
    AppBar? appBar;
    DockItem? hovered;
    System.Windows.Media.Effects.Effect? pillShadow;
    bool animating, cleanedUp, hiddenForFullscreen, autoHidden, sliding;
    ContextMenu? activeMenu;

    /// <summary>A menu or the tray popup is showing. Read from their state, not counted from events,
    /// so a missed Closed event can never leave the dock stuck on screen.</summary>
    bool MenuOpen => TrayPopup.IsOpen || activeMenu?.IsOpen == true || PreviewShown;

    // ---- Window previews ----
    PreviewWindow? previews;
    DockItem? previewItem;
    readonly DispatcherTimer previewShow = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer previewHide = new() { Interval = TimeSpan.FromMilliseconds(300) };
    bool PreviewShown => previews?.IsVisible == true;
    Native.RECT pillRect; // where the pill sits on screen when shown, in pixels

    double BaseSize => settings.IconSize;
    double Gap => settings.EdgeGap;                                      // pill to screen edge
    double MaxScale => settings.Magnify ? settings.MagnifyScale : 1;    // icon under the cursor
    double DateFontSize => Math.Round(settings.ClockFontSize * 0.82, 1);

    // Tall enough for the icons, and for the two-line clock (time over date, ~1.33 line height each)
    // inside its capsule, which sits 14 DIPs inside the pill.
    double PillHeight => Math.Max(Math.Max(BaseSize + 16, 46), Math.Ceiling((settings.ClockFontSize + DateFontSize) * 1.33 + 18));

    public MainWindow()
    {
        Theme.Apply(settings.Theme);
        InitializeComponent();
        tray = new TrayHost(settings);
        Apps.ItemsSource = items;
        tick.Tick += (_, _) => OnTick();
        previewShow.Tick += (_, _) =>
        {
            previewShow.Stop();
            if (hovered is { IsRunning: true } item) ShowPreview(item);
        };
        previewHide.Tick += (_, _) =>
        {
            previewHide.Stop();
            if (previews?.IsMouseOver != true && hovered == null) HidePreview();
        };
        autoHideTimer.Tick += (_, _) => UpdateAutoHide();
        refreshSoon.Tick += (_, _) => { refreshSoon.Stop(); Refresh(); };
        SourceInitialized += OnSourceInitialized;
        LayoutUpdated += (_, _) => SyncBackdrop();
        LocationChanged += (_, _) => SyncBackdrop();
        new WindowInteropHelper(this).Owner = backdrop.Handle; // owned windows always stay above their owner
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;

        // Never take focus away from the app the user is working in, and stay out of Alt+Tab.
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));

        shellHookMsg = Native.RegisterWindowMessage("SHELLHOOK");
        appBarMsg = Native.RegisterWindowMessage("Tasky.AppBar");
        taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        appBar = new AppBar(appBarMsg);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        Native.RegisterShellHookWindow(hwnd);
        ApplyHotkeySetting();

        try
        {
            ApplyTaskbar();
            ApplyLayout();
            ApplyTray();
            Refresh();
            UpdateClock();
            UpdateStatus();
        }
        catch (Exception error) { Log.Error(error, "startup"); }
        tick.Start();
        autoHideTimer.Start();
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint m = (uint)msg;
        if (m == shellHookMsg)
        {
            if ((int)wParam == Native.HSHELL_FLASH) MarkAttention(lParam);
            ScheduleRefresh();
        }
        else if (m == appBarMsg)
        {
            if ((int)wParam == Native.ABN_POSCHANGED) Dispatcher.InvokeAsync(Reposition);
        }
        else if (m == taskbarCreatedMsg)
        {
            // Explorer restarted: it shows its taskbar again and forgets our app bar.
            appBar?.Forget();
            Dispatcher.InvokeAsync(() => { ApplyTaskbar(); Reposition(); });
        }
        else if (m == WM_HOTKEY && (int)wParam == HotkeyId)
        {
            OnHotkey();
        }
        else if (m == Native.WM_SETTINGCHANGE && lParam != IntPtr.Zero && settings.Theme == "System"
                 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Dispatcher.InvokeAsync(ApplyThemeSetting); // Windows switched between light and dark
        }
        else if (m == Native.WM_DISPLAYCHANGE || m == Native.WM_DPICHANGED ||
                 (m == Native.WM_SETTINGCHANGE && (int)wParam == Native.SPI_SETWORKAREA && !settings.ReserveSpace))
        {
            Dispatcher.InvokeAsync(Reposition);
        }
        return IntPtr.Zero;
    }

    // ---- Layout & positioning ------------------------------------------------------------

    void ApplyLayout()
    {
        Pill.Height = PillHeight;
        // With blur the pill wraps the blur window's DWM corners (+1 for the border it sits inside).
        double radius = Math.Min(settings.CornerRadius, PillHeight / 2);
        if (settings.Blur) radius = backdrop.SetCorners(radius) is var used && used > 0 ? used + 1 : 0;
        Pill.CornerRadius = new CornerRadius(radius);
        DockItem.Spacing = settings.IconSpacing;
        DockItem.Inset = (PillHeight - BaseSize) / 2;
        // Over blur the tint can be lighter and still read as the same Solid/Glass/Clear level.
        // The shadow is dropped too: it renders under the whole translucent pill and muddies the glass.
        PillBrush.Color = (Color)Application.Current.Resources["T.PillColor"];
        PillBrush.Opacity = settings.BackgroundOpacity * (settings.Blur ? 0.5 : 1);
        pillShadow ??= Pill.Effect;
        Pill.Effect = settings.Blur ? null : pillShadow;

        double button = PillHeight - 8;
        StartButton.Width = StartButton.Height = button;
        StartButton.Margin = new Thickness(0, 0, 0, 4);
        StartIcon.Margin = new Thickness((button - BaseSize * 0.8) / 2); // app icons carry padding, the logo doesn't
        // Status capsule: a slightly inset, rounded group holding Quick Settings and the clock. Its text
        // and glyphs keep Windows' taskbar sizes rather than scaling with the app icons, so small
        // icons don't shrink the clock into illegibility and big ones don't make it shout.
        double capsule = button - 6;
        var capsuleRadius = new CornerRadius(Math.Min(Pill.CornerRadius.TopLeft, capsule / 2) * 0.8);
        StatusCapsule.Height = capsule;
        StatusCapsule.Margin = new Thickness(4, 0, 2, 7);
        StatusCapsule.CornerRadius = capsuleRadius;
        QuickSettings.CornerRadius = Clock.CornerRadius = capsuleRadius;
        QuickSettings.Padding = new Thickness(8, 0, 6, 0);
        foreach (var glyph in new[] { WifiBack, WifiGlyph, VolumeGlyph, BatteryGlyph })
        {
            glyph.FontSize = 15;
            glyph.Margin = new Thickness(3, 0, 3, 0);
        }
        StatusDivider.Height = capsule * 0.45;
        Clock.Padding = new Thickness(9, 0, 10, 0);
        ClockTime.FontSize = settings.ClockFontSize;
        ClockDate.FontSize = DateFontSize;
        SettingsButton.Width = Math.Max(30, button * 0.8);
        SettingsButton.Height = button;
        SettingsButton.Margin = new Thickness(2, 0, 0, 4);
        SettingsGlyph.FontSize = 16;
        Resources["TraySlot"] = 28.0;
        Resources["TrayIcon"] = 16.0;
        TrayArea.Height = button;
        TrayArea.Margin = new Thickness(0, 0, 0, 4);
        TrayMoreGlyph.FontSize = 11;
        // Desktops have no battery; don't show an icon for one.

        double sep = BaseSize * 0.6;
        SepLeft.Height = SepRight.Height = sep;
        SepLeft.Margin = SepRight.Margin = new Thickness(6, 0, 6, (PillHeight - sep) / 2);

        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        IconLoader.Trim(IconLoader.Bucket(BaseSize * dpiScale), IconLoader.Bucket(BaseSize * MaxScale * dpiScale));
        foreach (var item in items)
        {
            item.Scale = item.TargetScale = 1;
            item.Size = BaseSize;
            item.IconPx = 0; // its cached bitmap may just have been trimmed
            item.RefreshSlot();
            UpdateIcon(item);
            PreloadIcons(item);
        }
        Reposition();
    }

    void Reposition()
    {
        if (hwnd == IntPtr.Zero || appBar == null) return;

        var monitor = DockMonitor();
        double s = VisualTreeHelper.GetDpi(this).DpiScaleY;

        int bottom;
        if (settings.ReserveSpace)
        {
            bottom = appBar.Reserve(hwnd, monitor.rcMonitor, (int)Math.Ceiling((PillHeight + Gap * 2) * s));
        }
        else
        {
            appBar.Release();
            bottom = DockMonitor().rcWork.Bottom;
        }

        // The window spans the monitor width; everything outside the pill is fully
        // transparent, which a layered window lets clicks fall through.
        double heightDip = PillHeight - BaseSize + BaseSize * MaxScale + Headroom; // room for the biggest magnified icon
        Width = monitor.rcMonitor.Width / s;
        Height = heightDip;
        Left = monitor.rcMonitor.Left / s;
        Top = (bottom - Gap * s) / s - heightDip;
    }

    /// <summary>Keeps the blur window exactly under the pill, which resizes as icons magnify.</summary>
    void SyncBackdrop()
    {
        if (PresentationSource.FromVisual(Pill) == null || Pill.ActualWidth <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = Pill.PointToScreen(new Point(0, 0)); // device pixels, includes the auto-hide slide
        int inset = (int)Math.Ceiling(dpi.DpiScaleX);     // stay inside the pill's 1px border
        var r = new Native.RECT
        {
            Left = (int)Math.Round(origin.X) + inset,
            Top = (int)Math.Round(origin.Y) + inset,
            Right = (int)Math.Round(origin.X + Pill.ActualWidth * dpi.DpiScaleX) - inset,
            Bottom = (int)Math.Round(origin.Y + Pill.ActualHeight * dpi.DpiScaleY) - inset,
        };
        if (!sliding && !autoHidden)
        {
            pillRect = r;
            tray.SetPlacement(r);
        }

        // While sliding away, the pill is clipped by the bottom of the dock window; clip the blur to match.
        var windowBottom = PointToScreen(new Point(0, ActualHeight)).Y;
        r.Bottom = Math.Min(r.Bottom, (int)Math.Round(windowBottom) - inset);

        if (!settings.Blur || hiddenForFullscreen || (autoHidden && !sliding) || r.Bottom - r.Top <= 0)
        {
            backdrop.SetShown(false);
            return;
        }
        backdrop.Place(r);
    }

    // ---- Auto-hide ----------------------------------------------------------------------------

    void UpdateAutoHide()
    {
        DismissPopupsIfNeeded();
        bool hide = false;
        // Stays up while settings is open, so changes can be seen as they're made.
        if (!settings.ReserveSpace && settings.AutoHide && !MenuOpen && settingsWindow == null && pillRect.Width > 0)
        {
            Native.GetCursorPos(out var pt);
            var monitor = DockMonitor();
            var work = monitor.rcWork;
            double s = VisualTreeHelper.GetDpi(this).DpiScaleY;

            // Reveal by pushing the mouse against the bottom edge; stay while it's over the dock or its magnified icons.
            bool atEdge = EdgeHeld(pt.Y >= work.Bottom - 3 && pt.Y <= monitor.rcMonitor.Bottom
                                   && pt.X >= monitor.rcMonitor.Left && pt.X < monitor.rcMonitor.Right);
            int reach = (int)((BaseSize * (MaxScale - 1) + Headroom) * s);
            bool overDock = !autoHidden && pt.X >= pillRect.Left && pt.X < pillRect.Right
                            && pt.Y >= pillRect.Top - reach && pt.Y <= work.Bottom;
            hide = !atEdge && !overDock && DateTime.UtcNow >= revealedByHotkeyUntil && WindowOverlapsDock();
        }
        SetAutoHidden(HideAfterDelay(hide));
        AlignShellFlyout();
    }

    static readonly HashSet<string> ShellUiProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "ShellHost",
    };
    readonly Dictionary<uint, string> processNames = new();

    string ProcessName(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (!processNames.TryGetValue(pid, out var name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = ""; }
            if (processNames.Count > 256) processNames.Clear();
            processNames[pid] = name;
        }
        return name;
    }

    /// <summary>
    /// Start, Search, Quick Settings and the notification center. Their windows cover the whole
    /// screen, so without this they'd read as a fullscreen app or an overlapping window and hide the
    /// dock, exactly when the user pressed the Windows key and expects to see it.
    /// </summary>
    bool IsShellUi(IntPtr window) => window != IntPtr.Zero && ShellUiProcesses.Contains(ProcessName(window));

    /// <summary>
    /// Windows opens Quick Settings at the right end of its (hidden) taskbar. Move it so it opens
    /// straight above the dock's wifi/volume button.
    /// </summary>
    void AlignShellFlyout()
    {
        if (autoHidden || sliding || pillRect.Width <= 0) return;
        var fg = Native.GetForegroundWindow();
        if (!Native.GetWindowRect(fg, out var r)) return;
        var monitor = DockMonitor().rcMonitor;
        int x, y, height;

        if (Native.GetClass(fg) == "ControlCenterWindow")
        {
            if (!QuickSettings.IsVisible) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            double center = QuickSettings.PointToScreen(new Point(0, 0)).X + QuickSettings.ActualWidth * dpi.DpiScaleX / 2;
            x = Math.Clamp((int)(center - r.Width / 2.0), monitor.Left, monitor.Right - r.Width);

            // The panel sits at the bottom of a transparent, screen-height window, laid out from the
            // window's top, so shortening the window would crop it. Keep its height (screen minus
            // Windows' 48px taskbar, restored if an older Tasky shortened it) and end it just above
            // the dock; only the empty top runs off-screen.
            height = Math.Max(r.Height, monitor.Height - 48);
            y = pillRect.Top - (int)(Gap * dpi.DpiScaleY) - height;
        }
        else if (Native.GetClass(fg) == "Windows.UI.Core.CoreWindow" && ProcessName(fg) == "ShellExperienceHost")
        {
            // The notification center doesn't re-lay out when moved or resized, so it's left at its
            // default spot: a full-height column at the right edge, clear of the centred dock. This
            // also puts it back if an older Tasky moved or shortened it.
            x = monitor.Right - r.Width;
            y = monitor.Top;
            height = monitor.Height;
        }
        else return;

        if (Math.Abs(r.Left - x) <= 1 && Math.Abs(r.Top - y) <= 1 && Math.Abs(r.Height - height) <= 1) return;
        Native.SetWindowPos(fg, IntPtr.Zero, x, y, r.Width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Windows can slide a monitor-sized foreground window (any maximized window once the taskbar is
    /// hidden) above topmost windows while leaving them flagged topmost. Put the dock back on top.
    /// </summary>
    void KeepOnTop()
    {
        // Not while a menu or the tray popup is open: the dock window would cover the part of it above the pill.
        if (hiddenForFullscreen || hwnd == IntPtr.Zero || MenuOpen) return;
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(backdrop.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    /// <summary>
    /// Any app window counts, not just the focused one: focus often sits on an invisible shell window
    /// (after a Quick Settings panel closes, say), which would otherwise keep the dock up over a maximized app.
    /// </summary>
    bool WindowOverlapsDock()
    {
        if (IsShellUi(Native.GetForegroundWindow())) return false; // Start & co. reveal the dock
        int gap = (int)(Gap * VisualTreeHelper.GetDpi(this).DpiScaleY);
        var area = pillRect;
        area.Top -= gap;
        return WindowEnumerator.AnyAppWindowIntersects(area, ownPid);
    }

    void SetAutoHidden(bool hide)
    {
        if (hide == autoHidden) return;
        autoHidden = hide;
        if (hide) SetHovered(null);
        else KeepOnTop();

        var anim = new DoubleAnimation(hide ? PillHeight + Gap + 4 : 0, Duration(hide ? 220 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            sliding = false;
            CompositionTarget.Rendering -= OnSlideFrame;
            SyncBackdrop();
        };
        if (!sliding)
        {
            sliding = true;
            CompositionTarget.Rendering += OnSlideFrame;
        }
        DockSlide.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    void OnSlideFrame(object? sender, EventArgs e) => SyncBackdrop();

    /// <summary>An animation length scaled by the animation-speed setting.</summary>
    TimeSpan Duration(double ms) => TimeSpan.FromMilliseconds(ms / Math.Clamp(settings.AnimationSpeed, 0.25, 4));

    DateTime? hideWantedSince;

    /// <summary>Holds a hide back until it has been wanted for the hide delay (showing is never delayed here).</summary>
    bool HideAfterDelay(bool hide)
    {
        if (!hide || autoHidden)
        {
            hideWantedSince = null;
            return hide;
        }
        hideWantedSince ??= DateTime.UtcNow;
        return DateTime.UtcNow - hideWantedSince.Value >= TimeSpan.FromMilliseconds(settings.HideDelay);
    }

    DateTime? edgeSince;

    /// <summary>True once the mouse has rested at the screen edge for the reveal delay.</summary>
    bool EdgeHeld(bool atEdge)
    {
        if (!atEdge)
        {
            edgeSince = null;
            return false;
        }
        edgeSince ??= DateTime.UtcNow;
        return DateTime.UtcNow - edgeSince.Value >= TimeSpan.FromMilliseconds(settings.RevealDelay);
    }

    // ---- Keyboard shortcut: Ctrl+Alt+D shows the dock ----

    const int HotkeyId = 0x7A51;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, WM_HOTKEY = 0x0312;
    DateTime revealedByHotkeyUntil;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    public void ApplyHotkeySetting()
    {
        if (hwnd == IntPtr.Zero) return;
        UnregisterHotKey(hwnd, HotkeyId);
        if (settings.ShowHotkey && !RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'D'))
            Log.Error(null, "Ctrl+Alt+D is already taken by another app");
    }

    /// <summary>Shows a hidden dock for a few seconds (it stays while the mouse is on it); hides it again if shown.</summary>
    void OnHotkey()
    {
        if (!autoHidden && DateTime.UtcNow < revealedByHotkeyUntil)
        {
            revealedByHotkeyUntil = default;
            return; // the auto-hide timer slides it away
        }
        revealedByHotkeyUntil = DateTime.UtcNow.AddSeconds(4);
        SetAutoHidden(false);
        KeepOnTop();
    }

    // ---- Theme ----

    public void ApplyThemeSetting()
    {
        Theme.Apply(settings.Theme);
        ApplyLayout();
        SyncBackdrop();
    }

    /// <summary>The display the dock lives on: the one chosen in settings, or the main one if that's gone.</summary>
    Native.MONITORINFO DockMonitor()
    {
        IntPtr mon = IntPtr.Zero;
        if (settings.Monitor is { } device)
            mon = Native.Monitors().FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase)).Handle;
        if (mon == IntPtr.Zero) mon = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref info);
        return info;
    }

    public void ApplyMonitorSetting()
    {
        appBar?.Release(); // the reservation belongs to the old display
        Reposition();
        SyncBackdrop();
    }

    // ---- Tracking windows -------------------------------------------------------------------

    void ScheduleRefresh()
    {
        refreshSoon.Stop();
        refreshSoon.Start();
    }

    void OnTick()
    {
        UpdateClock();
        UpdateStatus();
        Refresh();

        // Explorer sometimes brings its taskbar back (e.g. after display changes).
        if (settings.HideTaskbar && WindowsTaskbar.IsVisible) WindowsTaskbar.SetVisible(false);

        bool fullscreen = IsFullscreenForeground();
        if (fullscreen != hiddenForFullscreen)
        {
            hiddenForFullscreen = fullscreen;
            Root.IsHitTestVisible = !fullscreen;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(fullscreen ? 0 : 1, Duration(180)));
            SyncBackdrop();
        }
        if (!autoHidden) KeepOnTop();
    }

    void Refresh()
    {
        var windows = WindowEnumerator.GetAppWindows(ownPid);
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
            item.IsRunning = ws.Count > 0;
            item.IsActive = ws.Any(w => w.Hwnd == lastForeground);
            if (item.IsActive || !item.IsRunning) item.IsAttention = false;
            if (item.Icon == null && ws.Count > 0) item.Icon = IconLoader.FromWindow(ws[0].Hwnd);
        }

        Sync(desired);
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
            item = new DockItem(key, launchPath) { Name = DisplayName(launchPath), Size = BaseSize, IconPath = launchPath };
            UpdateIcon(item);
            if (item.Icon != null) PreloadIcons(item);
            else
            {
                item.IconPath = null;
                item.Icon = windows is { Count: > 0 } ? IconLoader.FromWindow(windows[0].Hwnd) : null;
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

    void MarkAttention(IntPtr h)
    {
        foreach (var item in items)
            if (h != lastForeground && item.Windows.Any(w => w.Hwnd == h)) item.IsAttention = true;
    }

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
        else if (pin.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ResolveShortcut(pin) is { } target
            && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            key = target;
        return pinKeys[pin] = key;
    }

    static string? ResolveShortcut(string lnk)
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
                return string.IsNullOrEmpty(target) ? null : target;
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

    // ---- Magnification ----------------------------------------------------------------------

    void OnAppsMouseMove(object sender, MouseEventArgs e)
    {
        if (dragItem != null && e.LeftButton == MouseButtonState.Pressed && DragApp(e)) return;

        int index = -1;
        double fraction = 0.5;
        for (int i = 0; i < items.Count; i++)
        {
            if (Apps.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var p = e.GetPosition(c);
            if (p.X >= 0 && p.X < c.ActualWidth)
            {
                index = i;
                fraction = p.X / c.ActualWidth;
                break;
            }
        }

        SetHovered(index >= 0 ? items[index] : null);
        if (index < 0) return;

        // A cosine wave centred on the cursor, measured in "icons" so it doesn't depend on size.
        double cursor = index + fraction;
        for (int i = 0; i < items.Count; i++)
        {
            double t = Math.Max(0, 1 - Math.Abs(i + 0.5 - cursor) / MagnifyRange);
            items[i].TargetScale = 1 + (MaxScale - 1) * (1 - Math.Cos(Math.PI * t)) / 2;
        }
        StartAnimating();
    }

    void OnAppsMouseLeave(object sender, MouseEventArgs e)
    {
        SetHovered(null);
        foreach (var item in items) item.TargetScale = 1;
        StartAnimating();
    }

    void StartAnimating()
    {
        if (animating) return;
        animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        bool done = true;
        foreach (var item in items)
        {
            double diff = item.TargetScale - item.Scale;
            if (Math.Abs(diff) < 0.002) item.Scale = item.TargetScale;
            else
            {
                item.Scale += diff * (1 - Math.Pow(1 - 0.28, settings.AnimationSpeed)); // 0.28 per frame at 1x
                done = false;
            }
            item.Size = Math.Round(BaseSize * item.Scale); // whole pixels keep icons off half-pixel blur
            UpdateIcon(item);
        }
        UpdateLabel();

        if (done)
        {
            CompositionTarget.Rendering -= OnFrame;
            animating = false;
        }
    }

    void SetHovered(DockItem? item)
    {
        if (hovered == item) return;
        if (hovered != null) hovered.IsHovered = false;
        hovered = item;
        if (item != null)
        {
            item.IsHovered = true;
            NameLabelText.Text = item.Name;
        }

        // Previews: shown after a short hover on a running app, then follow the cursor across icons.
        previewShow.Stop();
        if (item is { IsRunning: true })
        {
            previewHide.Stop();
            if (PreviewShown) ShowPreview(item);
            else previewShow.Start();
        }
        else if (PreviewShown)
        {
            previewHide.Stop();
            previewHide.Start();
        }
        UpdateLabel();
    }

    void ShowPreview(DockItem item)
    {
        if (Apps.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c || item.Windows.Count == 0) return;
        if (previews == null)
        {
            previews = new PreviewWindow();
            previews.MouseEnter += (_, _) => previewHide.Stop();
            previews.MouseLeave += (_, _) => { previewHide.Stop(); previewHide.Start(); };
            previews.WindowChosen += h =>
            {
                HidePreview();
                Activate(h);
                lastForeground = h;
                ScheduleRefresh();
            };
            previews.WindowClosed += h =>
            {
                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                // Give the app a moment to close, then show what's left (or nothing).
                var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                later.Tick += (_, _) =>
                {
                    later.Stop();
                    Refresh();
                    if (previewItem is { IsRunning: true } p) ShowPreview(p);
                    else HidePreview();
                };
                later.Start();
            };
        }
        previewItem = item;
        var center = c.PointToScreen(new Point(c.ActualWidth / 2, 0));
        previews.ShowFor(item.Windows, item.Icon, new Point(center.X, pillRect.Top), hwnd);
        UpdateLabel();
    }

    void HidePreview()
    {
        previewShow.Stop();
        previewHide.Stop();
        previewItem = null;
        previews?.HidePreviews();
        UpdateLabel();
    }

    void UpdateLabel()
    {
        var c = hovered != null ? Apps.ItemContainerGenerator.ContainerFromItem(hovered) as FrameworkElement : trayHover;
        if (c == null || (PreviewShown && hovered != null)) // the preview names the windows itself
        {
            NameLabel.Visibility = Visibility.Collapsed;
            return;
        }
        NameLabel.Visibility = Visibility.Visible;
        NameLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var top = c.TranslatePoint(new Point(c.ActualWidth / 2, 0), Root);
        Canvas.SetLeft(NameLabel, top.X - NameLabel.DesiredSize.Width / 2);
        Canvas.SetTop(NameLabel, top.Y - NameLabel.DesiredSize.Height - 2);
    }

    // ---- Drag to reorder pinned apps ----------------------------------------------------------

    const double DragThreshold = 6;
    DockItem? dragItem;
    Point dragStart;
    bool dragging;

    void OnAppsMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as DockItem;
        dragStart = e.GetPosition(Apps);
        dragging = false;
    }

    /// <summary>Handles a move while an icon is held; true once it has become a drag.</summary>
    bool DragApp(MouseEventArgs e)
    {
        var p = e.GetPosition(Apps);
        if (!dragging)
        {
            if (Math.Abs(p.X - dragStart.X) < DragThreshold) return false;
            dragging = true;
            Mouse.Capture(Apps, CaptureMode.SubTree);
            HidePreview();
            SetHovered(null);
            foreach (var item in items) item.TargetScale = 1; // magnifying while dragging just jitters
            StartAnimating();
            if (Apps.ItemContainerGenerator.ContainerFromItem(dragItem) is UIElement held) held.Opacity = 0.55;
        }

        // Swap places with whichever pinned icon is under the cursor.
        for (int i = 0; i < items.Count; i++)
        {
            if (Apps.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            double x = e.GetPosition(c).X;
            if (x < 0 || x >= c.ActualWidth) continue;
            var target = items[i];
            if (target != dragItem) MoveApp(dragItem!, target);
            break;
        }
        return true;
    }

    int PinIndex(DockItem item) => settings.Pins.FindIndex(p => PinKey(p).Equals(item.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Moves <paramref name="item"/> to where <paramref name="target"/> is. Pinned apps always come
    /// first, so: pinned among pinned reorders the pins; running among running reorders them for as
    /// long as they stay open; a running app dropped among the pinned ones gets pinned right there.
    /// </summary>
    void MoveApp(DockItem item, DockItem target)
    {
        if (item.IsPinned && target.IsPinned)
        {
            int from = PinIndex(item), to = PinIndex(target);
            if (from < 0 || to < 0) return;
            var pin = settings.Pins[from];
            settings.Pins.RemoveAt(from);
            settings.Pins.Insert(to, pin);
        }
        else if (!item.IsPinned && target.IsPinned)
        {
            int to = PinIndex(target);
            if (to < 0) return;
            settings.Pins.Insert(to, item.LaunchPath);
            item.IsPinned = true;
        }
        else if (item.IsPinned && !target.IsPinned)
        {
            return; // pinned apps stay in front of the running-only ones
        }
        items.Move(items.IndexOf(item), items.IndexOf(target));
    }

    void OnAppsLostCapture(object sender, MouseEventArgs e)
    {
        if (dragging && Mouse.LeftButton != MouseButtonState.Pressed) EndAppDrag();
    }

    /// <summary>Ends a drag; true if there was one (so the release isn't also a click).</summary>
    bool EndAppDrag()
    {
        bool wasDragging = dragging;
        if (dragging)
        {
            if (Apps.ItemContainerGenerator.ContainerFromItem(dragItem) is UIElement held) held.Opacity = 1;
            Mouse.Capture(null);
            settings.Save();
        }
        dragging = false;
        dragItem = null;
        return wasDragging;
    }

    // ---- Clicks -----------------------------------------------------------------------------

    void OnAppsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && EndAppDrag())
        {
            e.Handled = true;
            return;
        }
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DockItem item) return;
        e.Handled = true;
        switch (e.ChangedButton)
        {
            case MouseButton.Left: OnItemClick(item); break;
            case MouseButton.Middle: Launch(item); break;
            case MouseButton.Right: HidePreview(); ShowItemMenu(item, e.OriginalSource as UIElement); break;
        }
    }

    void OnItemClick(DockItem item)
    {
        HidePreview();
        if (!item.IsRunning)
        {
            Launch(item);
            return;
        }

        TrackForeground();
        IntPtr target;
        if (item.Windows.Count == 1)
        {
            var h = item.Windows[0].Hwnd;
            if (h == lastForeground && !Native.IsIconic(h))
            {
                Native.ShowWindow(h, Native.SW_MINIMIZE);
                ScheduleRefresh();
                return;
            }
            target = h;
        }
        else
        {
            // Several windows: cycle through them in a stable order.
            int i = item.Windows.FindIndex(w => w.Hwnd == lastForeground);
            target = i >= 0 ? item.Windows[(i + 1) % item.Windows.Count].Hwnd : item.TopWindow;
        }

        Activate(target);
        lastForeground = target;
        ScheduleRefresh();
    }

    static void Activate(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        if (!Native.SetForegroundWindow(h))
        {
            // Tapping Alt satisfies the foreground-lock rules when Windows refuses the switch.
            Native.PressKeys(Native.VK_MENU);
            Native.SetForegroundWindow(h);
        }
    }

    void Launch(DockItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.LaunchPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(item.LaunchPath) ?? "",
            });
            Bounce(item);
        }
        catch (Exception ex) { Log.Error(ex, $"launching {item.LaunchPath}"); }
    }

    void Bounce(DockItem item)
    {
        if (Apps.ItemContainerGenerator.ContainerFromItem(item) is not UIElement c) return;
        var t = new TranslateTransform();
        c.RenderTransform = t;
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -14, TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    void OnStartClick(object sender, MouseButtonEventArgs e) => Native.PressKeys(Native.VK_LWIN);

    void OnClockClick(object sender, MouseButtonEventArgs e) => Native.PressKeys(Native.VK_LWIN, Native.VK_N);

    void OnQuickSettingsClick(object sender, MouseButtonEventArgs e) => Native.PressKeys(Native.VK_LWIN, Native.VK_A);

    void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("t");
        ClockDate.Text = now.ToString("d");
    }

    // ---- Live status icons ------------------------------------------------------------------

    NetworkStatus network = new(NetworkKind.None, 0, "", false);
    BatteryStatus battery = new(false, 0, false, false);
    VolumeStatus volume = new(0, false, false);
    int statusTicks;

    /// <summary>Volume every second (it changes from keyboards and apps); network and battery every 5.</summary>
    void UpdateStatus()
    {
        if (statusTicks++ % 5 == 0)
        {
            network = StatusMonitor.Network();
            battery = StatusMonitor.Battery();
        }
        volume = StatusMonitor.Volume();

        WifiGlyph.Text = StatusMonitor.NetworkGlyph(network);
        WifiBack.Visibility = network.Kind == NetworkKind.Wifi && network.Internet ? Visibility.Visible : Visibility.Collapsed;
        VolumeGlyph.Text = StatusMonitor.VolumeGlyph(volume);
        BatteryGlyph.Visibility = battery.Present ? Visibility.Visible : Visibility.Collapsed;
        BatteryGlyph.Text = StatusMonitor.BatteryGlyph(battery);
        if (ReferenceEquals(trayHover, QuickSettings)) NameLabelText.Text = StatusMonitor.Describe(network, volume, battery);
    }

    void OnStatusMouseEnter(object sender, MouseEventArgs e)
    {
        trayHover = QuickSettings;
        NameLabelText.Text = StatusMonitor.Describe(network, volume, battery);
        UpdateLabel();
    }

    void OnStatusMouseLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(trayHover, QuickSettings)) trayHover = null;
        UpdateLabel();
    }

    // ---- System tray ------------------------------------------------------------------------

    void ApplyTray()
    {
        if (settings.ShowTray && tray.Start())
        {
            TrayPinned.ItemsSource = tray.Pinned;
            TrayAll.ItemsSource = tray.All;
            TrayArea.Visibility = Visibility.Visible;
        }
        else
        {
            TrayPopup.IsOpen = false;
            TrayPinned.ItemsSource = TrayAll.ItemsSource = null;
            tray.Stop();
            TrayArea.Visibility = Visibility.Collapsed;
        }
    }

    static NotifyIcon? TrayIconOf(object sender) => (sender as FrameworkElement)?.DataContext as NotifyIcon;

    bool clickAfterDismiss; // the press closed a shell panel; send the whole click once it's gone

    // A left press on a tray icon pinned to the dock is held back until it's clear whether it's a click
    // (sent to the app on release) or a drag (reorders the pinned icons, and the app never sees it).
    NotifyIcon? trayPress;
    FrameworkElement? trayPressElement;
    Point trayPressStart;
    bool trayDragging;

    bool InDock(object sender) => sender is FrameworkElement el && TrayPinned.IsAncestorOf(el);

    void OnTrayMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TrayIconOf(sender) is not { } icon) return;
        e.Handled = true;

        // An open Quick Settings / notification panel keeps every app's flyout behind it (and swallows
        // the activation), so close it first, the way clicking the real taskbar does.
        clickAfterDismiss = IsShellUi(Native.GetForegroundWindow());
        if (clickAfterDismiss) Native.PressKeys(Native.VK_ESCAPE);

        if (e.ChangedButton == MouseButton.Left && InDock(sender))
        {
            trayPress = icon;
            trayPressElement = (FrameworkElement)sender;
            trayPressStart = e.GetPosition(TrayPinned);
            trayDragging = false;
            return;
        }
        if (!clickAfterDismiss) icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), Native.GetDoubleClickTime());
    }

    void OnTrayMouseUp(object sender, MouseButtonEventArgs e)
    {
        var icon = TrayIconOf(sender);
        bool heldPress = e.ChangedButton == MouseButton.Left && trayPress != null;
        if (heldPress)
        {
            icon = trayPress;
            bool wasDrag = trayDragging;
            EndTrayDrag();
            if (wasDrag)
            {
                e.Handled = true;
                return;
            }
        }
        if (icon == null) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Left) TrayPopup.IsOpen = false; // like Windows' overflow, get out of the app's way

        var button = e.ChangedButton;
        uint pos = MouseHelper.GetCursorPositionParam();
        int doubleClick = Native.GetDoubleClickTime();
        if (!clickAfterDismiss)
        {
            if (heldPress) icon.IconMouseDown(button, pos, doubleClick);
            icon.IconMouseUp(button, pos, doubleClick);
            return;
        }
        clickAfterDismiss = false;
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            icon.IconMouseDown(button, pos, doubleClick);
            icon.IconMouseUp(button, pos, doubleClick);
        };
        later.Start();
    }

    void OnTrayMouseMove(object sender, MouseEventArgs e)
    {
        if (trayPress != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var p = e.GetPosition(TrayPinned);
            if (!trayDragging && Math.Abs(p.X - trayPressStart.X) >= DragThreshold)
            {
                trayDragging = true;
                trayPressElement?.CaptureMouse();
                if (trayPressElement != null) trayPressElement.Opacity = 0.55;
                trayHover = null;
                UpdateLabel();
            }
            if (trayDragging)
            {
                // Swap places with the pinned tray icon under the cursor.
                for (int i = 0; i < TrayPinned.Items.Count; i++)
                {
                    if (TrayPinned.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
                    double x = e.GetPosition(c).X;
                    if (x < 0 || x >= c.ActualWidth) continue;
                    if (TrayPinned.Items[i] is NotifyIcon target && target != trayPress) tray.MovePin(trayPress, target);
                    break;
                }
                return;
            }
        }
        TrayIconOf(sender)?.IconMouseMove(MouseHelper.GetCursorPositionParam());
    }

    void EndTrayDrag()
    {
        if (trayDragging)
        {
            trayPressElement?.ReleaseMouseCapture();
            tray.SavePins();
        }
        // Containers are recycled when the view re-sorts, so restore every one's opacity.
        for (int i = 0; i < TrayPinned.Items.Count; i++)
            if (TrayPinned.ItemContainerGenerator.ContainerFromIndex(i) is UIElement c)
                foreach (var child in SelfAndDescendants(c)) child.Opacity = 1;
        trayPress = null;
        trayPressElement = null;
        trayDragging = false;
    }

    static IEnumerable<UIElement> SelfAndDescendants(UIElement root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (VisualTreeHelper.GetChild(root, i) is UIElement child)
                foreach (var d in SelfAndDescendants(child)) yield return d;
    }

    void OnTrayMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement el || TrayIconOf(sender) is not { } icon) return;

        // Apps use this to place their own tooltips and flyouts.
        var dpi = VisualTreeHelper.GetDpi(el);
        var origin = el.PointToScreen(new Point(0, 0));
        icon.Placement = new ManagedShell.Interop.NativeMethods.Rect
        {
            Left = (int)origin.X,
            Top = (int)origin.Y,
            Right = (int)(origin.X + el.ActualWidth * dpi.DpiScaleX),
            Bottom = (int)(origin.Y + el.ActualHeight * dpi.DpiScaleY),
        };
        icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());

        if (DockHost.IsAncestorOf(el) && !string.IsNullOrWhiteSpace(icon.Title))
        {
            trayHover = el;
            NameLabelText.Text = icon.Title;
            UpdateLabel();
        }
    }

    void OnTrayMouseLeave(object sender, MouseEventArgs e)
    {
        TrayIconOf(sender)?.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        if (ReferenceEquals(sender, trayHover))
        {
            trayHover = null;
            UpdateLabel();
        }
    }

    // Swallow the press so it never reaches the app's icon underneath.
    void OnTrayPinDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void OnTrayPinClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (TrayIconOf(sender) is { } icon) tray.TogglePin(icon);
    }

    IntPtr foregroundAtPopupOpen;

    void OnTrayMoreClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        TrayPopup.IsOpen = !TrayPopup.IsOpen;
    }

    /// <summary>Notes what's in front and forgets clicks made so far, as a popup or menu opens.</summary>
    void PopupOpening()
    {
        foregroundAtPopupOpen = Native.GetForegroundWindow();
        Native.GetAsyncKeyState(0x01); // clear the "pressed since last call" bits left by the opening click
        Native.GetAsyncKeyState(0x02);
    }

    /// <summary>
    /// The tray popup and right-click menus are closed by hand rather than by WPF's light dismiss,
    /// which doesn't fire reliably for a dock that never takes focus: on a click anywhere outside them
    /// (and the arrow that toggles the tray popup), or when the user switches apps. Polled from the
    /// auto-hide timer.
    /// </summary>
    void DismissPopupsIfNeeded()
    {
        if (!MenuOpen) return;

        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint pid);
        bool switchedApps = fg != foregroundAtPopupOpen && pid != ownPid;
        bool clicked = (Native.GetAsyncKeyState(0x01) & 0x8001) != 0 | (Native.GetAsyncKeyState(0x02) & 0x8001) != 0;
        Native.GetCursorPos(out var pt);

        if (TrayPopup.IsOpen && TrayPopup.Child is FrameworkElement content
            && (switchedApps || (clicked && !IsOver(content, pt) && !IsOver(TrayMore, pt))))
            TrayPopup.IsOpen = false;

        if (activeMenu is { IsOpen: true } menu && (switchedApps || (clicked && !IsOver(menu, pt))))
            menu.IsOpen = false;
    }

    static bool IsOver(FrameworkElement el, Native.POINT pt)
    {
        if (PresentationSource.FromVisual(el) == null) return false;
        var dpi = VisualTreeHelper.GetDpi(el);
        var o = el.PointToScreen(new Point(0, 0));
        return pt.X >= o.X && pt.Y >= o.Y && pt.X < o.X + el.ActualWidth * dpi.DpiScaleX && pt.Y < o.Y + el.ActualHeight * dpi.DpiScaleY;
    }

    void OnTrayPopupOpened(object? sender, EventArgs e)
    {
        PopupOpening();
        TrayEmpty.Visibility = TrayAll.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayMoreGlyph.Text = "\uE70D"; // chevron down while open
    }

    void OnTrayPopupClosed(object? sender, EventArgs e)
    {
        TrayMoreGlyph.Text = "\uE70E";
    }

    // ---- Menus ------------------------------------------------------------------------------

    /// <summary>The ID Windows keeps the app's recent documents under.</summary>
    string AppIdFor(DockItem item)
    {
        if (item.LaunchPath.StartsWith(AppWindow.AppsFolder, StringComparison.OrdinalIgnoreCase))
            return item.LaunchPath[AppWindow.AppsFolder.Length..];
        foreach (var w in item.Windows)
            if ((w.Aumid ?? Native.GetWindowAppUserModelId(w.Hwnd)) is { Length: > 0 } explicitId) return explicitId;
        var exe = item.Windows.FirstOrDefault()?.ExePath ?? item.Key;
        return JumpList.ImplicitAppId(exe);
    }

    /// <summary>Opens a recent document with the app it was recent in (not just the default handler).</summary>
    void OpenRecent(DockItem item, RecentItem recent)
    {
        try
        {
            var exe = item.Windows.FirstOrDefault()?.ExePath ?? item.LaunchPath;
            var start = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) && File.Exists(recent.Path)
                ? new ProcessStartInfo(exe, $"\"{recent.Path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(recent.Path) { UseShellExecute = true };
            Process.Start(start);
        }
        catch (Exception ex) { Log.Error(ex, $"opening {recent.Path}"); }
    }

    void ShowItemMenu(DockItem item, UIElement? target)
    {
        var menu = new ContextMenu();

        var recent = JumpList.Recent(AppIdFor(item), 6);
        if (recent.Count > 0)
        {
            menu.Items.Add(Section("Recent"));
            foreach (var r in recent)
            {
                var entry = Item(FriendlyName(r), () => OpenRecent(item, r), icon: RecentIcon(r));
                entry.ToolTip = r.Path;
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Separator());
        }

        if (item.Windows.Count > 0)
        {
            menu.Items.Add(Section(item.Name));
            foreach (var w in item.Windows)
            {
                var h = w.Hwnd;
                menu.Items.Add(Item(Native.GetText(h), () => Activate(h), icon: MenuImage(item.Icon), active: h == lastForeground));
            }
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(item.IsRunning
            ? Item("New window", () => Launch(item), icon: Glyph(""))
            : Item($"Open {item.Name}", () => Launch(item), icon: MenuImage(item.Icon) ?? (object)Glyph("")));
        menu.Items.Add(item.IsPinned
            ? Item("Unpin from dock", () => TogglePin(item), icon: Glyph(""))
            : Item("Pin to dock", () => TogglePin(item), icon: Glyph("")));
        if (item.IsRunning)
        {
            menu.Items.Add(Item(item.Windows.Count > 1 ? "Close all windows" : "Close window", () =>
            {
                foreach (var w in item.Windows) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                ScheduleRefresh();
            }, icon: Glyph("")));
        }
        Open(menu, target);
    }

    void OnDockRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowDockMenu(sender as UIElement);
    }

    SettingsWindow? settingsWindow;

    void OnSettingsClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSettings();
    }

    public void OpenSettings()
    {
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(settings, this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    // Called by the settings window as its controls change.
    public void ApplyLook()
    {
        ApplyLayout();
        SyncBackdrop();
    }

    public void ApplyModeSetting()
    {
        SetAutoHidden(false);
        Reposition();
    }

    public void ApplyTraySetting() => ApplyTray();

    public void ApplyTaskbarSetting()
    {
        ApplyTaskbar();
        Reposition();
    }

    void ShowDockMenu(UIElement? target)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Dock settings…", OpenSettings, icon: Glyph("\uE713")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Section("When windows reach the dock"));

        foreach (var (label, reserve, autoHide) in new[]
                 {
                     ("Keep them above the dock", true, settings.AutoHide),
                     ("Hide the dock", false, true),
                     ("Show the dock over them", false, false),
                 })
        {
            bool current = settings.ReserveSpace ? reserve : !reserve && settings.AutoHide == autoHide;
            menu.Items.Add(Item(label, () =>
            {
                settings.ReserveSpace = reserve;
                settings.AutoHide = autoHide;
                settings.Save();
                ApplyModeSetting();
                settingsWindow?.Reload();
            }, current));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show tray icons",
            () => { settings.ShowTray = !settings.ShowTray; settings.Save(); ApplyTray(); settingsWindow?.Reload(); }, settings.ShowTray));
        menu.Items.Add(Item("Hide Windows taskbar",
            () => { settings.HideTaskbar = !settings.HideTaskbar; settings.Save(); ApplyTaskbarSetting(); settingsWindow?.Reload(); },
            settings.HideTaskbar));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit Tasky", () => Application.Current.Shutdown(), icon: Glyph("\uE7E8")));

        Open(menu, target);
    }

    void Open(ContextMenu menu, UIElement? target)
    {
        if (activeMenu is { IsOpen: true }) activeMenu.IsOpen = false; // one menu at a time
        activeMenu = menu; // keeps an auto-hiding dock on screen while it's up
        menu.Opened += (_, _) => PopupOpening();
        menu.PlacementTarget = target ?? Pill;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>A menu row: <paramref name="icon"/> is a glyph or image; <paramref name="active"/> marks the window in front.</summary>
    static MenuItem Item(string header, Action onClick, bool isChecked = false, object? icon = null, bool active = false)
    {
        var item = new MenuItem
        {
            Header = new TextBlock { Text = header, TextTrimming = TextTrimming.CharacterEllipsis },
            IsChecked = isChecked,
            Icon = icon,
            Tag = active ? "Active" : null,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    static MenuItem Section(string title) => new() { Header = title, Style = (Style)Application.Current.FindResource("MenuHeader") };

    /// <summary>A 16 px icon from Segoe Fluent Icons for a menu row.</summary>
    static TextBlock Glyph(string code) => new()
    {
        Text = code,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Image? MenuImage(ImageSource? source)
    {
        if (source == null) return null;
        var image = new Image { Source = source, Width = 16, Height = 16, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>"youtube.com/shorts/abc" for a web address; the file or folder name otherwise.</summary>
    static string FriendlyName(RecentItem r)
    {
        if (Uri.TryCreate(r.Path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return (host + uri.AbsolutePath).TrimEnd('/');
        }
        return r.Name;
    }

    static object RecentIcon(RecentItem r)
    {
        if (r.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Glyph(""); // globe
        return MenuImage(IconLoader.Small(r.Path)) ?? (object)Glyph(""); // the file's own icon, else a page
    }

    void TogglePin(DockItem item)
    {
        if (item.IsPinned)
        {
            settings.Pins.RemoveAll(p => string.Equals(PinKey(p), item.Key, StringComparison.OrdinalIgnoreCase));
            item.IsPinned = false;
        }
        else
        {
            settings.Pins.Add(item.LaunchPath);
        }
        settings.Save();
        Refresh();
    }

    // ---- Drag an .exe or shortcut onto the dock to pin it -----------------------------------

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedApps(e).Any() ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        foreach (var path in DroppedApps(e))
            if (!settings.Pins.Any(p => PinKey(p).Equals(PinKey(path), StringComparison.OrdinalIgnoreCase)))
                settings.Pins.Add(path);
        settings.Save();
        Refresh();
    }

    static IEnumerable<string> DroppedApps(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();

    // ---- Windows taskbar & shutdown ---------------------------------------------------------

    void ApplyTaskbar()
    {
        if (settings.HideTaskbar)
        {
            if (!settings.TaskbarHiddenByUs)
            {
                settings.TaskbarWasAutoHide = WindowsTaskbar.IsAutoHide;
                settings.TaskbarHiddenByUs = true;
                settings.Save();
            }
            WindowsTaskbar.IsAutoHide = true;
            WindowsTaskbar.SetVisible(false);
        }
        else if (settings.TaskbarHiddenByUs)
        {
            RestoreTaskbar();
        }
    }

    void RestoreTaskbar()
    {
        WindowsTaskbar.SetVisible(true);
        WindowsTaskbar.IsAutoHide = settings.TaskbarWasAutoHide;
        settings.TaskbarHiddenByUs = false;
        settings.Save();
    }

    /// <summary>Puts the Windows taskbar and work area back. Safe to call more than once.</summary>
    public void Cleanup()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        try
        {
            tray.Stop(); // first, so apps re-add their icons to Explorer's tray
            if (settings.TaskbarHiddenByUs) RestoreTaskbar();
            appBar?.Release();
            if (hwnd != IntPtr.Zero) Native.DeregisterShellHookWindow(hwnd);
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotkeyId);
            previews?.Close();
        }
        catch (Exception ex) { Log.Error(ex, "cleanup"); }
    }
}
