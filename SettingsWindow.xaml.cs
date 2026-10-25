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
        ModeReserve.IsChecked = settings.ReserveSpace;
        ModeAutoHide.IsChecked = !settings.ReserveSpace && settings.AutoHide;
        ModeAlways.IsChecked = !settings.ReserveSpace && !settings.AutoHide;
        ShowTray.IsChecked = settings.ShowTray;
        HideTaskbar.IsChecked = settings.HideTaskbar;
        AutoStart.IsChecked = Tasky.AutoStart.Enabled;
        loading = false;
        UpdateLabels();
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

        Toggle(Magnify, on => settings.Magnify = on, look: true);
        Toggle(Blur, on => settings.Blur = on, look: true);
        Toggle(ShowTray, on => { settings.ShowTray = on; dock.ApplyTraySetting(); });
        Toggle(HideTaskbar, on => { settings.HideTaskbar = on; dock.ApplyTaskbarSetting(); });
        Toggle(AutoStart, on => Tasky.AutoStart.Enabled = on);

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
        settings.ClockFontSize = defaults.ClockFontSize;
        LoadValues();
        dock.ApplyLook();
        settings.Save();
    }

    void OnDone(object sender, RoutedEventArgs e) => Close();
}
