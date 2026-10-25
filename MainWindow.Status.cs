using System.Windows;
using System.Windows.Input;

namespace Tasky;

/// <summary>The clock and the live Wi-Fi, volume and battery icons.</summary>
public partial class MainWindow
{
    void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("t");
        ClockDate.Text = now.ToString(Vertical ? ShortDateFormat : "d"); // a full date is too wide for a side dock
    }

    // ---- Live status icons ------------------------------------------------------------------

    NetworkStatus network = new(NetworkKind.None, 0, "", false);
    BatteryStatus battery = new(false, 0, false, false);
    VolumeStatus volume = new(0, false, false);
    int statusTicks;
    bool statusDirty = true;

    /// <summary>
    /// Volume every call (it changes from keyboards and apps, with no cheap event); network and battery
    /// when Windows reports a change, or every 30 seconds for signal strength and charge.
    /// </summary>
    void UpdateStatus()
    {
        if (statusDirty || statusTicks++ % 30 == 0)
        {
            network = StatusMonitor.Network();
            battery = StatusMonitor.Battery();
            statusDirty = false;
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
}
