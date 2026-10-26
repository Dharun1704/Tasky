using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Tasky;

/// <summary>Dock settings. Every control applies to the dock as it changes; saving is debounced.</summary>
public partial class SettingsWindow : Window
{
    readonly Settings settings;
    readonly MainWindow dock;
    readonly DispatcherTimer save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer relayout = new() { Interval = TimeSpan.FromMilliseconds(30) };
    bool loading;

    internal SettingsWindow(Settings settings, MainWindow dock)
    {
        this.settings = settings;
        this.dock = dock;
        InitializeComponent();

        save.Tick += (_, _) => { save.Stop(); settings.Save(); };
        // Sliders fire on every pixel of a drag; re-laying out the dock at most every 30 ms keeps it smooth.
        relayout.Tick += (_, _) => { relayout.Stop(); dock.ApplyLook(); };
        SourceInitialized += (_, _) => UseMica();
        Closed += (_, _) => settings.Save();

        ApplyTheme();
        LoadValues();
        Wire();
        WireNavigation();
    }

    // ---- Look ---------------------------------------------------------------------------------

    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref Margins margins);
    struct Margins { public int Left, Right, Top, Bottom; }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_MAINWINDOW = 2;
    bool mica;

    /// <summary>
    /// Windows 11's Mica material behind the whole window, title bar included, like Windows' own
    /// Settings: the frame is extended over the client area and WPF draws on a transparent
    /// background. Older Windows keeps a plain background.
    /// </summary>
    void UseMica()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetDarkTitleBar(hwnd);
        int type = DWMSBT_MAINWINDOW;
        if (Native.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) != 0) return;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return;
        HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        mica = true;
        ApplyTheme();
    }

    void SetDarkTitleBar(IntPtr hwnd)
    {
        int dark = Theme.IsLight ? 0 : 1; // also picks the dark or light Mica
        Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    /// <summary>The window's colours ("S.*"), following the dock's dark/light theme.</summary>
    void ApplyTheme()
    {
        bool light = Theme.IsLight;
        void Set(string key, uint argb) => Resources[key] = Brush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        // The app's accent is lifted for dark backgrounds (see App.ReadAccent); light ones get Windows' own.
        var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
        static byte Unlift(byte lifted) => (byte)Math.Clamp((lifted - 102) / 0.6, 0, 255);
        Resources["S.Accent"] = Brush(light ? Color.FromRgb(Unlift(accent.R), Unlift(accent.G), Unlift(accent.B)) : accent);

        if (light)
        {
            Set("S.Window", mica ? 0x00000000 : 0xFFF3F3F3);
            Set("S.Fg", 0xE4000000);
            Set("S.Sub", 0x9E000000);
            Set("S.Card", 0xB3FFFFFF);
            Set("S.CardStroke", 0x0F000000);
            Set("S.NavHover", 0x09000000);
            Set("S.NavSelected", 0x0F000000);
            Set("S.SliderRest", 0x72000000);
            Set("S.Thumb", 0xFFFFFFFF);
            Set("S.ToggleStroke", 0x9E000000);
            Set("S.ToggleOff", 0x05000000);
            Set("S.OnAccent", 0xFFFFFFFF);
            Set("S.SegmentsBg", 0x08000000);
            Set("S.SegmentOn", 0xFFFFFFFF);
            Set("S.Button", 0xB3FFFFFF);
            Set("S.ButtonHover", 0xF2F6F6F6);
            Set("S.Scroll", 0x72000000);
        }
        else
        {
            Set("S.Window", mica ? 0x00000000 : 0xFF202020);
            Set("S.Fg", 0xFFFFFFFF);
            Set("S.Sub", 0xC5FFFFFF);
            Set("S.Card", 0x0DFFFFFF);
            Set("S.CardStroke", 0x17FFFFFF);
            Set("S.NavHover", 0x0DFFFFFF);
            Set("S.NavSelected", 0x17FFFFFF);
            Set("S.SliderRest", 0x8BFFFFFF);
            Set("S.Thumb", 0xFF454545);
            Set("S.ToggleStroke", 0xCCFFFFFF);
            Set("S.ToggleOff", 0x0AFFFFFF);
            Set("S.OnAccent", 0xFF000000);
            Set("S.SegmentsBg", 0x0AFFFFFF);
            Set("S.SegmentOn", 0x2EFFFFFF);
            Set("S.Button", 0x0FFFFFFF);
            Set("S.ButtonHover", 0x1AFFFFFF);
            Set("S.Scroll", 0x8BFFFFFF);
        }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetDarkTitleBar(hwnd);
    }

    static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>The sidebar shows one page at a time.</summary>
    void WireNavigation()
    {
        foreach (var (nav, page) in new[] { (NavAppearance, PageAppearance), (NavDock, PageDock), (NavBehavior, PageBehavior),
                                            (NavSystem, PageSystem), (NavAbout, PageAbout) })
            nav.Checked += (_, _) =>
            {
                foreach (var p in new[] { PageAppearance, PageDock, PageBehavior, PageSystem, PageAbout })
                    p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
                Scroller.ScrollToTop();
            };
        NavAppearance.IsChecked = true;
    }

    /// <summary>Picks up changes made from the dock's right-click menu.</summary>
    public void Reload() => LoadValues();

    void LoadValues()
    {
        loading = true;
        IconSize.Value = settings.IconSize;
        Spacing.Value = settings.IconSpacing;
        Magnify.IsChecked = settings.Magnify;
        MagnifyScale.Value = settings.MagnifyScale;
        Roundness.Value = settings.CornerRadius;
        BackgroundOpacity.Value = Math.Round(settings.BackgroundOpacity * 100);
        Blur.IsChecked = settings.Blur;
        ClockSize.Value = settings.ClockFontSize;
        EdgeGap.Value = settings.EdgeGap;
        AnimationSpeed.Value = settings.AnimationSpeed;
        RevealDelay.Value = settings.RevealDelay;
        HideDelay.Value = settings.HideDelay;
        ShowHotkey.IsChecked = settings.ShowHotkey;
        ThemeSystem.IsChecked = settings.Theme is not ("Dark" or "Light");
        ThemeDark.IsChecked = settings.Theme == "Dark";
        ThemeLight.IsChecked = settings.Theme == "Light";
        ModeReserve.IsChecked = settings.ReserveSpace;
        ModeAutoHide.IsChecked = !settings.ReserveSpace && settings.AutoHide;
        ModeAlways.IsChecked = !settings.ReserveSpace && !settings.AutoHide;
        ShowTray.IsChecked = settings.ShowTray;
        HideTaskbar.IsChecked = settings.HideTaskbar;
        AutoStart.IsChecked = Tasky.AutoStart.Enabled;
        StartMenu.IsChecked = StartMenuShortcut.Exists;
        var edge = DockEdges.Parse(settings.Position);
        EdgeBottom.IsChecked = edge == DockEdge.Bottom;
        EdgeTop.IsChecked = edge == DockEdge.Top;
        EdgeLeft.IsChecked = edge == DockEdge.Left;
        EdgeRight.IsChecked = edge == DockEdge.Right;
        LoadMonitors();
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"Tasky {version?.ToString(3)}";
        SidebarVersion.Text = $"Version {version?.ToString(3)}";
        loading = false;
        UpdateLabels();
    }

    /// <summary>One choice per display, e.g. "Display 2 · 1920 × 1080".</summary>
    void LoadMonitors()
    {
        MonitorChoices.Children.Clear();
        var monitors = Native.Monitors();
        string? current = monitors.Any(m => string.Equals(m.Device, settings.Monitor, StringComparison.OrdinalIgnoreCase))
            ? settings.Monitor : null;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            string number = new string(m.Device.Where(char.IsDigit).ToArray());
            var choice = new RadioButton
            {
                GroupName = "Monitor",
                Style = (Style)FindResource("Choice"),
                Content = $"Display {(number.Length > 0 ? number : (i + 1).ToString())} · {m.Bounds.Width} × {m.Bounds.Height}{(m.Primary ? " (main)" : "")}",
                IsChecked = current == null ? m.Primary : string.Equals(m.Device, current, StringComparison.OrdinalIgnoreCase),
            };
            string? device = m.Primary ? null : m.Device; // the main display is stored as null, so it follows a main-display change
            choice.Checked += (_, _) =>
            {
                if (loading) return;
                settings.Monitor = device;
                dock.ApplyMonitorSetting();
                Changed(look: false);
            };
            MonitorChoices.Children.Add(choice);
        }
    }

    void Wire()
    {
        void Look(Slider slider, Action<double> apply) => slider.ValueChanged += (_, e) =>
        {
            if (loading) return;
            apply(e.NewValue);
            Changed(look: true);
        };
        Look(IconSize, v => settings.IconSize = (int)v);
        Look(Spacing, v => settings.IconSpacing = v);
        Look(MagnifyScale, v => settings.MagnifyScale = Math.Round(v, 2));
        Look(Roundness, v => settings.CornerRadius = v);
        Look(BackgroundOpacity, v => settings.BackgroundOpacity = v / 100);
        Look(EdgeGap, v => settings.EdgeGap = v);
        Look(ClockSize, v => settings.ClockFontSize = v);
        Look(AnimationSpeed, v => settings.AnimationSpeed = Math.Round(v, 1));
        HideDelay.ValueChanged += (_, e) =>
        {
            if (loading) return;
            settings.HideDelay = (int)e.NewValue;
            Changed(look: false);
        };
        RevealDelay.ValueChanged += (_, e) =>
        {
            if (loading) return;
            settings.RevealDelay = (int)e.NewValue;
            Changed(look: false);
        };
        Toggle(ShowHotkey, on => { settings.ShowHotkey = on; dock.ApplyHotkeySetting(); });

        foreach (var (button, edge) in new[] { (EdgeBottom, DockEdge.Bottom), (EdgeTop, DockEdge.Top), (EdgeLeft, DockEdge.Left), (EdgeRight, DockEdge.Right) })
            button.Checked += (_, _) =>
            {
                if (loading) return;
                settings.Position = edge.ToString();
                dock.ApplyPositionSetting();
                Changed(look: false);
            };

        foreach (var (button, theme) in new[] { (ThemeSystem, "System"), (ThemeDark, "Dark"), (ThemeLight, "Light") })
            button.Checked += (_, _) =>
            {
                if (loading) return;
                settings.Theme = theme;
                dock.ApplyThemeSetting();
                ApplyTheme();
                Changed(look: false);
            };

        Toggle(Magnify, on => settings.Magnify = on, look: true);
        Toggle(Blur, on => settings.Blur = on, look: true);
        Toggle(ShowTray, on => { settings.ShowTray = on; dock.ApplyTraySetting(); });
        Toggle(HideTaskbar, on => { settings.HideTaskbar = on; dock.ApplyTaskbarSetting(); });
        Toggle(AutoStart, on => Tasky.AutoStart.Enabled = on);
        Toggle(StartMenu, on => { if (on) StartMenuShortcut.Create(); else StartMenuShortcut.Remove(); });

        Mode(ModeReserve, reserve: true, autoHide: settings.AutoHide);
        Mode(ModeAutoHide, reserve: false, autoHide: true);
        Mode(ModeAlways, reserve: false, autoHide: false);
    }

    void Toggle(CheckBox box, Action<bool> apply, bool look = false)
    {
        void Handler(object s, RoutedEventArgs e)
        {
            if (loading) return;
            apply(box.IsChecked == true);
            Changed(look);
        }
        box.Checked += Handler;
        box.Unchecked += Handler;
    }

    void Mode(RadioButton button, bool reserve, bool autoHide) => button.Checked += (_, _) =>
    {
        if (loading) return;
        settings.ReserveSpace = reserve;
        if (!reserve) settings.AutoHide = autoHide;
        dock.ApplyModeSetting();
        Changed(look: false);
    };

    void Changed(bool look)
    {
        UpdateLabels();
        if (look)
        {
            relayout.Stop();
            relayout.Start();
        }
        save.Stop();
        save.Start();
    }

    void UpdateLabels()
    {
        IconSizeValue.Text = $"{settings.IconSize} px";
        SpacingValue.Text = $"{settings.IconSpacing:0} px";
        MagnifyValue.Text = $"{settings.MagnifyScale:0.00}×";
        MagnifyScale.IsEnabled = settings.Magnify;
        RoundnessValue.Text = $"{settings.CornerRadius:0} px";
        RoundnessRow.Description = settings.Blur
            ? "Blur limits this to 0, 4 or 8 px. Turn blur off for any roundness."
            : "How round the dock's corners are.";
        OpacityValue.Text = $"{settings.BackgroundOpacity * 100:0}%";
        GapValue.Text = $"{settings.EdgeGap:0} px";
        ClockSizeValue.Text = $"{settings.ClockFontSize:0.#} px";
        SpeedValue.Text = $"{settings.AnimationSpeed:0.0}×";
        RevealValue.Text = settings.RevealDelay == 0 ? "Instant" : $"{settings.RevealDelay} ms";
        HideValue.Text = settings.HideDelay == 0 ? "Instant" : $"{settings.HideDelay / 1000.0:0.0} s";
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        var defaults = new Settings();
        settings.IconSize = defaults.IconSize;
        settings.IconSpacing = defaults.IconSpacing;
        settings.Magnify = defaults.Magnify;
        settings.MagnifyScale = defaults.MagnifyScale;
        settings.CornerRadius = defaults.CornerRadius;
        settings.BackgroundOpacity = defaults.BackgroundOpacity;
        settings.Blur = defaults.Blur;
        settings.EdgeGap = defaults.EdgeGap;
        settings.AnimationSpeed = defaults.AnimationSpeed;
        if (settings.Theme != defaults.Theme)
        {
            settings.Theme = defaults.Theme;
            dock.ApplyThemeSetting();
        }
        settings.ClockFontSize = defaults.ClockFontSize;
        LoadValues();
        dock.ApplyLook();
        settings.Save();
    }

    void OnGitHub(object sender, RoutedEventArgs e) => Open("https://github.com/Dharun1704/Tasky");

    void OnDataFolder(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Settings.Folder);
        Open(Settings.Folder);
    }

    static void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error(ex, $"opening {target}"); }
    }
}
