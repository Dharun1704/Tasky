using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;

namespace Tasky;

/// <summary>The system tray: pinned tray icons on the dock, the overflow popup, and passing clicks to apps.</summary>
public partial class MainWindow
{
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
            if (!trayDragging && Math.Abs(Edge.Along(p) - Edge.Along(trayPressStart)) >= DragThreshold)
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
                    double along = Edge.Along(e.GetPosition(c));
                    if (along < 0 || along >= Edge.AlongLength(c)) continue;
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
        if (TrayPopup.IsOpen)
        {
            TrayPopup.IsOpen = false;
            return;
        }
        var offset = ClearOfDock(TrayMore, TrayPopupShadowRoom);
        TrayPopup.HorizontalOffset = offset.X;
        TrayPopup.VerticalOffset = offset.Y;
        AfterPanelsClose(() => TrayPopup.IsOpen = true); // Start would stay in front of it
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
}
