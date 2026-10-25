using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Tasky;

internal enum NetworkKind { None, Wifi, Ethernet }

internal sealed record NetworkStatus(NetworkKind Kind, int SignalPercent, string Name, bool Internet);
internal sealed record VolumeStatus(int Percent, bool Muted, bool HasDevice);
internal sealed record BatteryStatus(bool Present, int Percent, bool Charging, bool PluggedIn);

/// <summary>Reads the state behind the dock's wifi, volume and battery icons.</summary>
internal static class StatusMonitor
{
    // ---- Network ------------------------------------------------------------------------------

    public static NetworkStatus Network()
    {
        bool internet = NetworkInterface.GetIsNetworkAvailable();
        try
        {
            if (Wifi() is { } wifi) return wifi with { Internet = internet };
        }
        catch (Exception ex) { Log.Error(ex, "reading wifi"); }

        bool ethernet = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            && n.GetIPProperties().GatewayAddresses.Count > 0);
        return new NetworkStatus(ethernet ? NetworkKind.Ethernet : NetworkKind.None, 0, ethernet ? "Ethernet" : "", internet);
    }

    /// <summary>The connected Wi-Fi network and its signal, from the WLAN API; null when not on Wi-Fi.</summary>
    static NetworkStatus? Wifi()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return null;
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return null;
            try
            {
                int count = Marshal.ReadInt32(list);
                const int itemSize = 16 + 512 + 4; // GUID, 256 WCHAR description, state
                for (int i = 0; i < count; i++)
                {
                    var item = list + 8 + i * itemSize;
                    int state = Marshal.ReadInt32(item + 16 + 512);
                    if (state != 1 /* wlan_interface_state_connected */) continue;

                    var guid = Marshal.PtrToStructure<Guid>(item);
                    if (WlanQueryInterface(handle, ref guid, 7 /* current_connection */, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state, mode, 256-WCHAR profile, then the association
                        // attributes (SSID length+bytes at 520, signal quality at 576).
                        int ssidLength = Math.Clamp(Marshal.ReadInt32(data + 520), 0, 32);
                        var ssid = new byte[ssidLength];
                        Marshal.Copy(data + 524, ssid, 0, ssidLength);
                        int quality = Marshal.ReadInt32(data + 576);
                        return new NetworkStatus(NetworkKind.Wifi, Math.Clamp(quality, 0, 100), System.Text.Encoding.UTF8.GetString(ssid), true);
                    }
                    finally { WlanFreeMemory(data); }
                }
                return null;
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiatedVersion, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr memory);

    // ---- Volume -------------------------------------------------------------------------------

    static IAudioEndpointVolume? endpoint;
    static DateTime endpointAt;

    public static VolumeStatus Volume()
    {
        try
        {
            // The default device can change (headphones plugged in), so look it up again now and then.
            if (endpoint == null || DateTime.UtcNow - endpointAt > TimeSpan.FromSeconds(5))
            {
                endpoint = DefaultEndpoint();
                endpointAt = DateTime.UtcNow;
            }
            if (endpoint == null) return new VolumeStatus(0, false, false);
            endpoint.GetMasterVolumeLevelScalar(out float level);
            endpoint.GetMute(out int muted);
            return new VolumeStatus((int)Math.Round(level * 100), muted != 0, true);
        }
        catch
        {
            endpoint = null;
            return new VolumeStatus(0, false, false);
        }
    }

    static IAudioEndpointVolume? DefaultEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            return device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var volume) == 0 ? (IAudioEndpointVolume)volume : null;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int mute, ref Guid context);
        [PreserveSig] int GetMute(out int mute);
    }

    // ---- Battery ------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static BatteryStatus Battery()
    {
        if (!GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100)
            return new BatteryStatus(false, 0, false, s.ACLineStatus == 1);
        return new BatteryStatus(true, s.BatteryLifePercent, (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1);
    }

    // ---- Glyphs (Segoe Fluent Icons) ----------------------------------------------------------

    /// <summary>Foreground glyph for the network, drawn over a faint full-wifi glyph when on Wi-Fi.</summary>
    public static string NetworkGlyph(NetworkStatus n) => n.Kind switch
    {
        NetworkKind.Wifi when !n.Internet => "\uEB5E",
        NetworkKind.Wifi => n.SignalPercent switch { >= 75 => "\uE701", >= 50 => "\uE874", >= 25 => "\uE873", _ => "\uE872" },
        NetworkKind.Ethernet => n.Internet ? "\uE839" : "\uF384",
        _ => "\uF384",
    };

    public static string VolumeGlyph(VolumeStatus v) =>
        v.Muted || !v.HasDevice ? "\uE74F" : v.Percent switch { 0 => "\uE992", < 34 => "\uE993", < 67 => "\uE994", _ => "\uE995" };

    public static string BatteryGlyph(BatteryStatus b)
    {
        int level = (int)Math.Round(b.Percent / 10.0); // 0..10
        if (b.Charging || (b.PluggedIn && b.Percent < 100))
            return level >= 10 ? "\uE83E" : ((char)(0xE85A + level)).ToString();
        return level >= 10 ? "\uE83F" : ((char)(0xE850 + level)).ToString();
    }

    public static string Describe(NetworkStatus n, VolumeStatus v, BatteryStatus b)
    {
        string net = n.Kind switch
        {
            NetworkKind.Wifi => $"Wi-Fi: {n.Name} ({n.SignalPercent}%){(n.Internet ? "" : ", no internet")}",
            NetworkKind.Ethernet => n.Internet ? "Ethernet" : "Ethernet, no internet",
            _ => "Not connected",
        };
        string vol = !v.HasDevice ? "No audio device" : v.Muted ? "Volume: muted" : $"Volume: {v.Percent}%";
        string bat = !b.Present ? "" : $"  ·  Battery: {b.Percent}%{(b.Charging ? ", charging" : b.PluggedIn ? ", plugged in" : "")}";
        return $"{net}  ·  {vol}{bat}";
    }
}
