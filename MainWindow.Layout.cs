using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Tasky;

/// <summary>The dock's size, orientation, position on screen, blur, theme and fitting many apps.</summary>
public partial class MainWindow
{
    // ---- Layout & positioning ------------------------------------------------------------

    void ApplyLayout()
    {
        ApplyOrientation();
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

        var edge = Edge;
        bool vertical = edge.IsVertical();
        // Sizes are written as for a bottom dock: "across" is the thickness, "along" the icons' direction.
        void SetSize(FrameworkElement el, double across, double along)
        {
            el.Width = vertical ? across : along;
            el.Height = vertical ? along : across;
        }

        double button = PillHeight - 8;
        SetSize(StartButton, button, button);
        StartButton.Margin = edge.Margin(0, 0, 4);
        StartIcon.Margin = new Thickness((button - BaseSize * 0.8) / 2); // app icons carry padding, the logo doesn't
        // Status capsule: a slightly inset, rounded group holding Quick Settings and the clock. Its text
        // and glyphs keep Windows' taskbar sizes rather than scaling with the app icons, so small
        // icons don't shrink the clock into illegibility and big ones don't make it shout. On a side
        // dock the capsule turns into a column: glyphs stacked, then the time over a short date.
        double capsule = button - 6;
        var capsuleRadius = new CornerRadius(Math.Min(Pill.CornerRadius.TopLeft, capsule / 2) * 0.8);
        SetSize(StatusCapsule, capsule, double.NaN);
        StatusCapsule.Margin = edge.Margin(4, 2, 7);
        StatusCapsule.CornerRadius = capsuleRadius;
        QuickSettings.CornerRadius = Clock.CornerRadius = capsuleRadius;
        QuickSettings.Padding = vertical ? new Thickness(0, 8, 0, 6) : new Thickness(8, 0, 6, 0);
        foreach (var glyph in new[] { WifiBack, WifiGlyph, VolumeGlyph, BatteryGlyph })
        {
            glyph.FontSize = 15;
            glyph.Margin = vertical ? new Thickness(0, 3, 0, 3) : new Thickness(3, 0, 3, 0);
        }
        SetSize(StatusDivider, capsule * 0.45, 1);
        Clock.Padding = vertical ? new Thickness(0, 7, 0, 8) : new Thickness(9, 0, 10, 0);
        ClockTime.FontSize = settings.ClockFontSize;
        ClockDate.FontSize = DateFontSize;
        ClockTime.HorizontalAlignment = ClockDate.HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        SetSize(SettingsButton, button, Math.Max(30, button * 0.8));
        SetSize(OverflowButton, button, Math.Max(30, BaseSize * 0.8));
        OverflowButton.Margin = edge.Margin(2, 0, 4);
        SettingsButton.Margin = edge.Margin(2, 0, 4);
        SettingsGlyph.FontSize = 16;
        Resources["TrayIcon"] = 16.0;
        Resources["TrayItemWidth"] = vertical ? button : 28.0;
        Resources["TrayItemHeight"] = vertical ? 28.0 : button;
        SetSize(TrayArea, button, double.NaN);
        SetSize(TrayMore, double.NaN, 28);
        TrayArea.Margin = edge.Margin(0, 0, 4);
        TrayMoreGlyph.FontSize = 11;

        double sep = BaseSize * 0.6;
        SetSize(SepLeft, sep, 1);
        SetSize(SepRight, sep, 1);
        SepLeft.Margin = SepRight.Margin = edge.Margin(6, 6, (PillHeight - sep) / 2);
        UpdateClock();

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
        ScheduleFit(); // a bigger icon size or clock may not fit any more
    }

    // ---- Fitting the dock on screen ---------------------------------------------------------

    bool fitPending;

    /// <summary>Checks the fit once the current layout pass has finished.</summary>
    void ScheduleFit()
    {
        if (fitPending) return;
        fitPending = true;
        Dispatcher.InvokeAsync(() => { fitPending = false; FitToScreen(); }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// When the dock would be longer than the screen, shrinks the app icons (down to 24 px; they
    /// still magnify on hover), and past that moves the last running apps into the overflow menu.
    /// It grows back as apps close.
    /// </summary>
    void FitToScreen()
    {
        if (hwnd == IntPtr.Zero || !DockRow.IsLoaded) return;
        var edge = Edge;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var m = DockMonitor().rcMonitor;
        double available = (edge.IsVertical() ? m.Height : m.Width) / s - 32; // a little air at both ends

        double Along(Size size) => edge.IsVertical() ? size.Height : size.Width;
        double margins = edge.IsVertical() ? DockRow.Margin.Top + DockRow.Margin.Bottom : DockRow.Margin.Left + DockRow.Margin.Right;
        // Everything but the app icons (Start, tray, clock...), which doesn't grow with icon count. Summed
        // child by child: the row's own measured size is capped at the window, exactly when it overflows.
        double fixedPart = margins + DockRow.Children.OfType<FrameworkElement>()
            .Where(c => c != Apps && c.Visibility != Visibility.Collapsed)
            .Sum(c => Along(c.DesiredSize));
        if (OverflowButton.Visibility != Visibility.Visible) fixedPart += Math.Max(30, BaseSize * 0.8) + 2; // room for it, should it appear
        int count = items.Count + overflowItems.Count;
        double spacing = settings.IconSpacing;
        double magnifyScale = settings.Magnify ? settings.MagnifyScale : 1;

        // The row's length at a given icon size, at rest plus what a magnified wave adds under the cursor.
        double Needed(double size, int icons) => fixedPart + icons * (size + spacing) + (magnifyScale - 1) * size * 2.2;

        double newFit = double.MaxValue;
        int newOverflow = 0;
        if (Needed(settings.IconSize, count) > available)
        {
            newFit = settings.IconSize;
            while (newFit > MinFitSize && Needed(newFit, count) > available) newFit -= 2;
            newFit = Math.Max(MinFitSize, newFit);
            if (Needed(newFit, count) > available)
            {
                int fits = (int)Math.Floor((available - fixedPart - (magnifyScale - 1) * newFit * 2.2) / (newFit + spacing));
                newOverflow = Math.Max(1, count - Math.Max(1, fits));
            }
        }
        // Growing back needs some room to spare, so the dock doesn't flip between two sizes.
        if (newFit > BaseSize && fitSize != double.MaxValue && Needed(Math.Min(newFit, settings.IconSize), count) > available - 24)
            newFit = fitSize;

        bool sizeChanged = !newFit.Equals(fitSize);
        bool overflowChanged = newOverflow != overflowCount;
        fitSize = newFit;
        overflowCount = newOverflow;
        if (sizeChanged) ApplyLayout();
        if (overflowChanged) Refresh();
    }

    void OnOverflowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HidePreview();
        var menu = new ContextMenu();
        menu.Items.Add(Section("More apps"));
        foreach (var item in overflowItems)
        {
            var it = item;
            menu.Items.Add(Item(it.Name, () => OnItemClick(it), icon: MenuImage(it.Icon), active: it.IsActive));
        }
        Open(menu, OverflowButton);
    }

    DockEdge? appliedEdge;

    /// <summary>Turns the dock's rows and alignment to match its screen edge.</summary>
    void ApplyOrientation()
    {
        var edge = Edge;
        bool vertical = edge.IsVertical();
        var orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        DockHost.HorizontalAlignment = edge.EdgeH();
        DockHost.VerticalAlignment = edge.EdgeV();
        DockRow.Orientation = TrayArea.Orientation = StatusRow.Orientation = QuickGlyphs.Orientation = orientation;
        DockRow.Margin = vertical ? new Thickness(0, 8, 0, 8) : new Thickness(8, 0, 8, 0);
        edge.HugEdge(DockRow);
        foreach (var el in new FrameworkElement[] { StartButton, SepLeft, Apps, OverflowButton, SepRight, TrayArea, StatusCapsule, SettingsButton })
            edge.HugEdge(el);

        // The pill is a band of the dock's thickness against the edge, as long as the row of icons.
        edge.HugEdge(Pill);
        Pill.Width = vertical ? PillHeight : double.NaN;
        Pill.Height = vertical ? double.NaN : PillHeight;

        DockItem.Edge = edge;
        if (appliedEdge == edge) return;

        Apps.ItemsPanel = StackPanelTemplate(orientation);
        TrayPinned.ItemsPanel = StackPanelTemplate(orientation);
        TrayPopup.Placement = MenuPlacement;

        // A slide left over from the old edge would point the wrong way.
        DockSlide.BeginAnimation(TranslateTransform.XProperty, null);
        DockSlide.BeginAnimation(TranslateTransform.YProperty, null);
        DockSlide.X = DockSlide.Y = 0;
        autoHidden = sliding = false;
        appliedEdge = edge;
    }

    static ItemsPanelTemplate StackPanelTemplate(Orientation orientation)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, orientation);
        return new ItemsPanelTemplate(panel);
    }

    /// <summary>Menus and popups open on the screen side of the dock.</summary>
    PlacementMode MenuPlacement => Edge switch
    {
        DockEdge.Top => PlacementMode.Bottom,
        DockEdge.Left => PlacementMode.Right,
        DockEdge.Right => PlacementMode.Left,
        _ => PlacementMode.Top,
    };

    // Transparent room around the visible box of a menu and of the tray popup, for the shadow
    // (the Margin in their templates).
    const double MenuShadowRoom = 12, TrayPopupShadowRoom = 10;
    const double MenuGap = 8; // between the pill and a menu

    /// <summary>
    /// The offset that moves a menu placed against <paramref name="target"/> (a button inside the pill)
    /// out to sit <see cref="MenuGap"/> clear of the pill itself, whatever the dock's size and edge.
    /// </summary>
    Vector ClearOfDock(UIElement target, double shadowRoom)
    {
        var at = target.TranslatePoint(new Point(0, 0), Pill);
        var size = target.RenderSize;
        double away = MenuGap - shadowRoom;
        return Edge switch
        {
            DockEdge.Top => new Vector(0, Pill.ActualHeight - (at.Y + size.Height) + away),
            DockEdge.Left => new Vector(Pill.ActualWidth - (at.X + size.Width) + away, 0),
            DockEdge.Right => new Vector(-at.X - away, 0),
            _ => new Vector(0, -at.Y - away),
        };
    }

    void Reposition()
    {
        if (hwnd == IntPtr.Zero || appBar == null) return;

        var monitor = DockMonitor();
        var edge = Edge;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleY;

        // Where the dock's band may sit: the reserved strip, or the work area (beside a visible taskbar).
        Native.RECT area;
        if (settings.ReserveSpace)
        {
            area = appBar.Reserve(hwnd, monitor.rcMonitor, (int)Math.Ceiling((PillHeight + Gap * 2) * s), edge);
        }
        else
        {
            appBar.Release();
            area = DockMonitor().rcWork;
        }

        // The window spans the monitor along the dock and is deep enough across for the biggest
        // magnified icon plus its label; everything outside the pill is fully transparent, which a
        // layered window lets clicks fall through.
        double depth = PillHeight - BaseSize + BaseSize * MaxScale + Headroom;
        double gap = Gap * s;
        var m = monitor.rcMonitor;
        switch (edge)
        {
            case DockEdge.Top:
                (Left, Top, Width, Height) = (m.Left / s, (area.Top + gap) / s, m.Width / s, depth);
                break;
            case DockEdge.Left:
                (Left, Top, Width, Height) = ((area.Left + gap) / s, m.Top / s, depth, m.Height / s);
                break;
            case DockEdge.Right:
                (Left, Top, Width, Height) = ((area.Right - gap) / s - depth, m.Top / s, depth, m.Height / s);
                break;
            default:
                (Left, Top, Width, Height) = (m.Left / s, (area.Bottom - gap) / s - depth, m.Width / s, depth);
                break;
        }
    }

    public void ApplyPositionSetting()
    {
        appBar?.Release(); // the reservation belongs to the old edge
        ApplyLayout();
        SyncBackdrop();
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
            tray.SetPlacement(r, Edge);
        }

        // While sliding away, the pill is clipped by the edge of the dock window; clip the blur to match.
        var windowTopLeft = PointToScreen(new Point(0, 0));
        var windowBottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
        r.Left = Math.Max(r.Left, (int)Math.Round(windowTopLeft.X) + inset);
        r.Top = Math.Max(r.Top, (int)Math.Round(windowTopLeft.Y) + inset);
        r.Right = Math.Min(r.Right, (int)Math.Round(windowBottomRight.X) - inset);
        r.Bottom = Math.Min(r.Bottom, (int)Math.Round(windowBottomRight.Y) - inset);

        if (!settings.Blur || hiddenForFullscreen || (autoHidden && !sliding) || r.Bottom - r.Top <= 0 || r.Right - r.Left <= 0)
        {
            backdrop.SetShown(false);
            return;
        }
        backdrop.Place(r);
    }

    // ---- Theme ----

    public void ApplyThemeSetting()
    {
        Theme.Apply(settings.Theme);
        ApplyLayout();
        SyncBackdrop();
        settingsWindow?.ApplyTheme(); // it follows the dock's theme
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
}
