using System.Windows.Data;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;

namespace Tasky;

/// <summary>
/// Owns the notification area (system tray). ManagedShell registers its own hidden Shell_TrayWnd
/// ahead of Explorer's, so apps' Shell_NotifyIcon calls arrive here; it forwards everything on to
/// Explorer so Explorer's (hidden) tray stays in sync, and on startup asks apps to re-add their icons.
/// </summary>
internal sealed class TrayHost(Settings settings)
{
    TrayService? service;
    Native.RECT placed;
    DockEdge placedEdge;

    public NotificationArea? Area { get; private set; }

    /// <summary>App icons pinned to the dock, in pin order.</summary>
    public ListCollectionView? Pinned { get; private set; }
    /// <summary>Every app icon, for the overflow popup.</summary>
    public ListCollectionView? All { get; private set; }

    /// <summary>
    /// Explorer's volume, network and power icons are left out: the dock's Quick Settings button
    /// already covers them, and they don't respond while Explorer's taskbar is hidden.
    /// </summary>
    static readonly HashSet<Guid> QuickSettingsIcons = new[]
    {
        NotificationArea.VOLUME_GUID, NotificationArea.NETWORK_GUID, NotificationArea.POWER_GUID,
    }.Select(g => new Guid(g)).ToHashSet();

    static bool IsAppIcon(object o) => o is NotifyIcon icon && !icon.IsHidden && !QuickSettingsIcons.Contains(icon.GUID);

    ListCollectionView View(Predicate<object> filter, params string[] liveProperties)
    {
        var view = new ListCollectionView(Area!.TrayIcons) { Filter = filter, IsLiveFiltering = true };
        foreach (var p in liveProperties) view.LiveFilteringProperties.Add(p);
        return view;
    }

    static TrayHost()
    {
        ManagedShell.Common.Logging.ShellLogger.Severity = ManagedShell.Common.Logging.LogSeverity.Warning;
        ManagedShell.Common.Logging.ShellLogger.Log += (_, e) => Log.Error(e.Exception, $"tray {e.SeverityString}: {e.Message}");
    }

    /// <summary>
    /// What a pin is saved under. ManagedShell's own identifier includes the tooltip, which apps change
    /// all the time (OneDrive's goes from "Backed up" to "Syncing"), so its pins wouldn't survive a restart.
    /// </summary>
    static string PinKey(NotifyIcon icon) => icon.GUID != Guid.Empty ? icon.GUID.ToString() : $"{icon.Path}:{icon.UID}";

    /// <summary>Pinned icons' keys, in dock order (the user's order, not ManagedShell's PinOrder).</summary>
    readonly List<string> pins = new();

    int PinIndex(NotifyIcon icon)
    {
        int i = pins.FindIndex(k => string.Equals(k, PinKey(icon), StringComparison.OrdinalIgnoreCase));
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>Pins an icon that was pinned last time, as soon as its app adds it.</summary>
    void RestorePin(NotifyIcon icon)
    {
        if (!icon.IsPinned && PinIndex(icon) != int.MaxValue) icon.Pin();
    }

    sealed class ByPinOrder(TrayHost host) : System.Collections.IComparer
    {
        public int Compare(object? a, object? b) =>
            a is NotifyIcon x && b is NotifyIcon y ? host.PinIndex(x).CompareTo(host.PinIndex(y)) : 0;
    }

    /// <summary>Moves a pinned icon to where <paramref name="target"/> is (drag to reorder).</summary>
    public void MovePin(NotifyIcon icon, NotifyIcon target)
    {
        int from = PinIndex(icon), to = PinIndex(target);
        if (from == int.MaxValue || to == int.MaxValue || from == to) return;
        var key = pins[from];
        pins.RemoveAt(from);
        pins.Insert(to, key);
        Pinned?.Refresh();
    }

    public void SavePins()
    {
        settings.TrayPins = pins.ToList();
        settings.Save();
    }

    public bool Start()
    {
        if (Area != null) return true;
        try
        {
            service = new TrayService();
            pins.Clear();
            pins.AddRange((settings.TrayPins ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase));
            // Pinning is ours (see PinKey), and ManagedShell's defaults are Explorer's system icons anyway.
            Area = new NotificationArea(Array.Empty<string>(), service, new ExplorerTrayService());
            // Other apps' app-bar messages now reach our tray window first. Leave every one unhandled so
            // it's forwarded to Explorer, which owns the work area. (Forwarded position messages don't
            // work, see ExplorerAppBar; Tasky sends its own straight to Explorer.)
            Area.SetAppBarMessageCallback(PassAppBarToExplorer);
            Area.Initialize();
            if (!Area.IsFailed)
            {
                Pinned = View(o => IsAppIcon(o) && ((NotifyIcon)o).IsPinned, nameof(NotifyIcon.IsPinned), nameof(NotifyIcon.IsHidden));
                Pinned.CustomSort = new ByPinOrder(this);
                All = View(IsAppIcon, nameof(NotifyIcon.IsHidden));

                foreach (var icon in Area.TrayIcons.ToList()) RestorePin(icon);
                Area.TrayIcons.CollectionChanged += (_, e) =>
                {
                    // Deferred: pinning while the collection is still raising its change event re-enters it.
                    foreach (var icon in e.NewItems?.OfType<NotifyIcon>() ?? [])
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => RestorePin(icon));
                };
                return true;
            }
            Log.Error(null, "tray service failed to start");
        }
        catch (Exception ex) { Log.Error(ex, "starting tray"); }
        Stop();
        return false;
    }

    static IntPtr PassAppBarToExplorer(NativeMethods.APPBARMSGDATAV3 amd, ref bool handled)
    {
        handled = false;
        return IntPtr.Zero;
    }

    /// <summary>Hands the tray back to Explorer (apps are told to re-add their icons there).</summary>
    public void Stop()
    {
        try { Area?.Dispose(); } catch (Exception ex) { Log.Error(ex, "stopping tray"); }
        try { service?.Dispose(); } catch { }
        Area = null;
        Pinned = All = null;
        service = null;
        placed = default;
    }

    public void TogglePin(NotifyIcon icon)
    {
        if (Area == null) return;
        var key = PinKey(icon);
        if (icon.IsPinned)
        {
            icon.Unpin();
            pins.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            pins.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            pins.Add(key); // newly pinned icons go at the end
            icon.Pin();
        }
        Pinned?.Refresh();
        SavePins();
    }

    /// <summary>Tells apps where the tray is (screen pixels) so their menus and flyouts open beside the dock.</summary>
    public void SetPlacement(Native.RECT r, DockEdge dockEdge)
    {
        if (Area == null || (dockEdge == placedEdge && r.Left == placed.Left && r.Top == placed.Top && r.Right == placed.Right && r.Bottom == placed.Bottom)) return;
        placed = r;
        placedEdge = dockEdge;
        Area.SetTrayHostSizeData(new TrayHostSizeData
        {
            edge = (NativeMethods.ABEdge)dockEdge.AppBarEdge(),
            rc = new NativeMethods.Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom },
        });
    }
}
