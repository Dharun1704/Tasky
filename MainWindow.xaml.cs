using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Tasky;

public partial class MainWindow : Window
{
    const double MagnifyRange = 2.2;  // how many neighbouring icons the magnify wave reaches
    const double LabelRoom = 44;      // room beyond magnified icons for the name label and launch bounce

    DockEdge Edge => DockEdges.Parse(settings.Position);
    bool Vertical => Edge.IsVertical();
    // Beside a side dock the name label sits next to the icon, so the window needs room for its width.
    double Headroom => Vertical ? 260 : LabelRoom;

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

    /// <summary>The icon size in use: the setting, or smaller while that many apps wouldn't fit on screen.</summary>
    double BaseSize => Math.Min(settings.IconSize, fitSize);
    double fitSize = double.MaxValue;
    /// <summary>How many running apps (from the end) don't fit even at the smallest size; they go in the overflow menu.</summary>
    int overflowCount;
    readonly List<DockItem> overflowItems = new();
    const double MinFitSize = 24;
    double Gap => settings.EdgeGap;                                      // pill to screen edge
    double MaxScale => settings.Magnify ? settings.MagnifyScale : 1;    // icon under the cursor
    double DateFontSize => Math.Round(settings.ClockFontSize * 0.82, 1);

    // Tall enough for the icons, and for the two-line clock (time over date, ~1.33 line height each)
    // inside its capsule, which sits 14 DIPs inside the pill.
    // On a side dock the clock's width matters instead: time and short date side by side in a column.
    // ("PillHeight" is the dock's thickness across, whichever edge it's on.)
    double PillHeight => Vertical
        ? Math.Max(Math.Max(BaseSize + 16, 46), Math.Ceiling(ClockTextWidth() + 24))
        : Math.Max(Math.Max(BaseSize + 16, 46), Math.Ceiling((settings.ClockFontSize + DateFontSize) * 1.33 + 18));

    /// <summary>Width of the widest time ("23:58") and short date ("28 Sep") at the clock's sizes.</summary>
    double ClockTextWidth()
    {
        double Measure(string text, double size, FontWeight weight) => new FormattedText(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal), size,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;
        var sample = new DateTime(2000, 9, 28, 23, 58, 0);
        return Math.Max(Measure(sample.ToString("t"), settings.ClockFontSize, FontWeights.SemiBold),
                        Measure(sample.ToString(ShortDateFormat), DateFontSize, FontWeights.Normal));
    }

    const string ShortDateFormat = "d MMM";

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

        windowEvents = new WindowEvents();
        windowEvents.LayoutChanged += () => overlapDirty = true;
        windowEvents.WindowsChanged += ScheduleRefresh;
        // Network and power changes arrive as events; the status icons only poll the volume.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.InvokeAsync(() => statusDirty = true);
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => Dispatcher.InvokeAsync(() => statusDirty = true);
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, _) => Dispatcher.InvokeAsync(() => statusDirty = true);

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
            // Windows announces each window it gives a taskbar button, including hidden ones such as
            // the background UAC prompt; remember those so they reach the dock too.
            switch ((int)wParam)
            {
                case HSHELL_WINDOWCREATED: announced.Add(lParam); break;
                case HSHELL_WINDOWDESTROYED: announced.Remove(lParam); break;
                case Native.HSHELL_FLASH:
                    announced.Add(lParam);
                    pendingAttention.Add(lParam);
                    break;
            }
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

    // ---- The heartbeat --------------------------------------------------------------------

    int ticks;
    /// <summary>consent.exe while a permission prompt is open (see <see cref="ConsentWatcher"/>).</summary>
    uint? consentPid;

    /// <summary>
    /// Once a second. Most work is event-driven (windows opening, closing, moving; network and power
    /// changes); this checks for a permission prompt, keeps the clock and volume current, and re-checks
    /// the app list every 5 seconds in case an event was missed. Nothing visual is updated while the
    /// dock is hidden.
    /// </summary>
    void OnTick()
    {
        ticks++;
        // A permission prompt opening or closing: checked every second whether or not the dock is visible.
        var consent = ConsentWatcher.Find();
        if (consent != consentPid)
        {
            consentPid = WindowEnumerator.ConsentPid = consent;
            Refresh();
        }
        bool visible = !autoHidden && !hiddenForFullscreen;
        if (visible)
        {
            UpdateClock();
            UpdateStatus();
        }
        if (ticks % 5 == 0) Refresh();
        // Over a fullscreen app the dock stays away, so the mouse-edge check can idle.
        autoHideTimer.Interval = TimeSpan.FromMilliseconds(hiddenForFullscreen ? 500 : 120);

        // Explorer sometimes brings its taskbar back (e.g. after display changes).
        if (settings.HideTaskbar && WindowsTaskbar.IsVisible) WindowsTaskbar.SetVisible(false);

        // A permission prompt is the one thing worth interrupting a fullscreen game or video for.
        bool fullscreen = IsFullscreenForeground() && consentPid == null;
        if (fullscreen != hiddenForFullscreen)
        {
            hiddenForFullscreen = fullscreen;
            Root.IsHitTestVisible = !fullscreen;
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(fullscreen ? 0 : 1, Duration(180)));
            SyncBackdrop();
        }
        if (!autoHidden) KeepOnTop();
    }

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
            windowEvents?.Dispose();
            previews?.Close();
        }
        catch (Exception ex) { Log.Error(ex, "cleanup"); }
    }
}
