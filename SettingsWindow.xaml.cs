using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
        SourceInitialized += (_, _) =>
        {
            int dark = 1; // dark title bar to match
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        };
        Closed += (_, _) => settings.Save();

        LoadValues();
        Wire();
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
        LoadMonitors();
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"Tasky {version?.ToString(3)}";
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
        RevealDelay.ValueChanged += (_, e) =>
        {
            if (loading) return;
            settings.RevealDelay = (int)e.NewValue;
            Changed(look: false);
        };
        Toggle(ShowHotkey, on => { settings.ShowHotkey = on; dock.ApplyHotkeySetting(); });

        foreach (var (button, theme) in new[] { (ThemeSystem, "System"), (ThemeDark, "Dark"), (ThemeLight, "Light") })
            button.Checked += (_, _) =>
            {
                if (loading) return;
                settings.Theme = theme;
                dock.ApplyThemeSetting();
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
        RoundnessHint.Visibility = settings.Blur ? Visibility.Visible : Visibility.Collapsed;
        OpacityValue.Text = $"{settings.BackgroundOpacity * 100:0}%";
        GapValue.Text = $"{settings.EdgeGap:0} px";
        ClockSizeValue.Text = $"{settings.ClockFontSize:0.#} px";
        SpeedValue.Text = $"{settings.AnimationSpeed:0.0}×";
        RevealValue.Text = settings.RevealDelay == 0 ? "Instant" : $"{settings.RevealDelay} ms";
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

    void OnDone(object sender, RoutedEventArgs e) => Close();

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
