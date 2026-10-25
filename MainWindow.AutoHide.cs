using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Tasky;

/// <summary>Sliding the dock away and back, staying on top, Windows' own panels, and the Ctrl+Alt+D shortcut.</summary>
public partial class MainWindow
{
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

            // Reveal by pushing the mouse against the dock's screen edge; stay while it's over the dock or
            // its magnified icons.
            var mon = monitor.rcMonitor;
            bool onMonitor = pt.X >= mon.Left && pt.X < mon.Right && pt.Y >= mon.Top && pt.Y < mon.Bottom;
            bool atEdge = EdgeHeld(onMonitor && Edge switch
            {
                DockEdge.Top => pt.Y <= work.Top + 2,
                DockEdge.Left => pt.X <= work.Left + 2,
                DockEdge.Right => pt.X >= work.Right - 3,
                _ => pt.Y >= work.Bottom - 3,
            });
            int reach = (int)((BaseSize * (MaxScale - 1) + LabelRoom) * s);
            var p = pillRect;
            bool overDock = !autoHidden && Edge switch
            {
                DockEdge.Top => pt.X >= p.Left && pt.X < p.Right && pt.Y >= work.Top && pt.Y <= p.Bottom + reach,
                DockEdge.Left => pt.Y >= p.Top && pt.Y < p.Bottom && pt.X >= work.Left && pt.X <= p.Right + reach,
                DockEdge.Right => pt.Y >= p.Top && pt.Y < p.Bottom && pt.X >= p.Left - reach && pt.X <= work.Right,
                _ => pt.X >= p.Left && pt.X < p.Right && pt.Y >= p.Top - reach && pt.Y <= work.Bottom,
            };
            // An app asking for attention (or the UAC prompt) keeps the dock out until it's dealt with.
            hide = !atEdge && !overDock && !attentionShown && DateTime.UtcNow >= revealedByHotkeyUntil && WindowOverlapsDock();
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
            // Windows lays the panel out at the bottom of its window, so it can only be moved to sit
            // over a bottom dock; on other edges it stays in Windows' own spot (bottom right).
            if (!QuickSettings.IsVisible || Edge != DockEdge.Bottom) return;
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
        switch (Edge)
        {
            case DockEdge.Top: area.Bottom += gap; break;
            case DockEdge.Left: area.Right += gap; break;
            case DockEdge.Right: area.Left -= gap; break;
            default: area.Top -= gap; break;
        }
        // Scanning every window is the costliest thing the dock does, so the answer is reused until a
        // window moves, opens, closes or changes focus (or for a second, in case an event was missed).
        if (!overlapDirty && area.Equals(overlapArea) && DateTime.UtcNow - overlapAt < TimeSpan.FromSeconds(1))
            return overlapCached;
        overlapCached = WindowEnumerator.AnyAppWindowIntersects(area, ownPid);
        overlapArea = area;
        overlapAt = DateTime.UtcNow;
        overlapDirty = false;
        return overlapCached;
    }

    bool overlapDirty = true, overlapCached;
    Native.RECT overlapArea;
    DateTime overlapAt;
    WindowEvents? windowEvents;

    void SetAutoHidden(bool hide)
    {
        if (hide == autoHidden) return;
        autoHidden = hide;
        if (hide) SetHovered(null);
        else
        {
            KeepOnTop();
            UpdateClock();  // neither is kept current while the dock is hidden
            UpdateStatus();
        }

        // Slide toward the screen edge, off the screen.
        var anim = new DoubleAnimation(hide ? -Edge.Inward() * (PillHeight + Gap + 4) : 0, Duration(hide ? 220 : 180))
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
        DockSlide.BeginAnimation(Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty, anim);
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
}
