using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Tasky;

/// <summary>
/// Live window previews above a dock icon, drawn by DWM thumbnails (what the Windows taskbar uses).
/// A normal, non-layered window: DWM can't draw thumbnails into a per-pixel-transparent one.
/// </summary>
internal sealed class PreviewWindow : Window
{
    const double MaxThumbWidth = 220, MaxThumbHeight = 140, CardPadding = 6;

    readonly StackPanel cards = new() { Orientation = Orientation.Horizontal };
    readonly List<(IntPtr Thumb, FrameworkElement Area, IntPtr Source)> thumbs = new();
    readonly Dictionary<IntPtr, FrameworkElement> cardOf = new();
    IntPtr hwnd;
    Point anchor;
    DockEdge edge;

    public event Action<IntPtr>? WindowChosen;
    public event Action<IntPtr>? WindowClosed;

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true; // sized and placed by Place, in one step
        SetResourceReference(BackgroundProperty, "T.PreviewBg");
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        UseLayoutRounding = true;
        Content = new Border { Padding = new Thickness(3), Child = cards };
        SetResourceReference(ForegroundProperty, "T.Fg"); // titles and the close glyph inherit it
        Left = Top = -10000;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            int corner = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        };
        LayoutUpdated += (_, _) => PlaceThumbnails();
    }

    /// <summary>DWM's border and shadow to match the dock's theme.</summary>
    void ApplyFrame()
    {
        int dark = Theme.IsLight ? 0 : 1, border = Theme.IsLight ? 0x00D8D8D8 : 0x00444444; // COLORREF is 0x00BBGGRR
        Native.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>
    /// Shows previews of <paramref name="windows"/> next to <paramref name="anchor"/> (screen px): the
    /// point on the dock's screen-facing side level with the icon. They open away from <paramref name="edge"/>.
    /// </summary>
    public void ShowFor(IReadOnlyList<AppWindow> windows, ImageSource? icon, Point anchor, DockEdge edge, IntPtr owner)
    {
        ClearThumbs();
        cards.Children.Clear();
        if (hwnd == IntPtr.Zero)
        {
            new WindowInteropHelper(this).Owner = owner;
            Show(); // creates the window (off-screen) so thumbnails have somewhere to draw
        }
        ApplyFrame();

        foreach (var w in windows) cards.Children.Add(MakeCard(w, icon));
        this.anchor = anchor;
        this.edge = edge;
        Place();
        if (!IsVisible) Show();
    }

    /// <summary>
    /// Takes a window's card out after its close button was clicked: it fades, then the others close
    /// up around the icon in one move, with the other thumbnails left running. False when it was the
    /// last one.
    /// </summary>
    public bool Remove(IntPtr window)
    {
        if (!cardOf.Remove(window, out var card)) return cardOf.Count > 0;
        int i = thumbs.FindIndex(t => t.Source == window);
        var thumb = i >= 0 ? thumbs[i].Thumb : IntPtr.Zero;
        if (i >= 0) thumbs.RemoveAt(i);
        if (cardOf.Count == 0)
        {
            if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb);
            return false;
        }

        // DWM draws the thumbnail over the card, so the two fade together.
        card.IsHitTestVisible = false;
        var started = DateTime.UtcNow;
        var fade = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        fade.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / FadeMs);
            card.Opacity = 1 - t;
            if (thumb != IntPtr.Zero) SetOpacity(thumb, (byte)(255 * (1 - t)));
            if (t < 1) return;
            fade.Stop();
            if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb);
            cards.Children.Remove(card);
            if (IsVisible) Place();
        };
        fade.Start();
        return true;
    }

    const double FadeMs = 120;

    /// <summary>Sizes the window to its cards and puts it next to the icon, in a single move.</summary>
    void Place()
    {
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dpi = VisualTreeHelper.GetDpi(this);
        double width = Math.Ceiling(content.DesiredSize.Width * dpi.DpiScaleX), height = Math.Ceiling(content.DesiredSize.Height * dpi.DpiScaleY);
        var mon = Native.MonitorFromPoint(new Native.POINT { X = (int)anchor.X, Y = (int)anchor.Y }, 2 /* NEAREST */);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref info);
        var screen = info.rcMonitor;
        double ClampX(double v) => Math.Clamp(v, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - width - 8));
        double ClampY(double v) => Math.Clamp(v, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - height - 8));
        var (x, y) = edge switch
        {
            DockEdge.Top => (ClampX(anchor.X - width / 2), anchor.Y + 10),
            DockEdge.Left => (anchor.X + 10, ClampY(anchor.Y - height / 2)),
            DockEdge.Right => (anchor.X - width - 10, ClampY(anchor.Y - height / 2)),
            _ => (ClampX(anchor.X - width / 2), anchor.Y - height - 10),
        };
        Native.SetWindowPos(hwnd, IntPtr.Zero, (int)x, (int)y, (int)width, (int)height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        UpdateLayout();
        PlaceThumbnails();
    }

    public void HidePreviews()
    {
        ClearThumbs();
        cards.Children.Clear();
        if (IsVisible) Hide();
    }

    void SetOpacity(IntPtr thumb, byte opacity)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_OPACITY, opacity = opacity };
        DwmUpdateThumbnailProperties(thumb, ref props);
    }

    FrameworkElement MakeCard(AppWindow w, ImageSource? icon)
    {
        // Minimized windows included: DWM keeps the last image of a minimized window, which is what
        // the Windows taskbar shows for them too.
        IntPtr thumb = IntPtr.Zero;
        bool hasThumb = hwnd != IntPtr.Zero && DwmRegisterThumbnail(hwnd, w.Hwnd, out thumb) == 0;
        var source = WindowSize(w.Hwnd, hasThumb ? thumb : IntPtr.Zero);
        // DWM reports a caption-bar-sized source when it has no image to give; show the app icon then.
        bool usable = hasThumb && source.Width >= 120 && source.Height >= 80;
        if (hasThumb && !usable) DwmUnregisterThumbnail(thumb);
        var size = Fit(usable ? source : new Size(16, 10));

        var area = new Grid { Width = size.Width, Height = size.Height };
        area.SetResourceReference(Panel.BackgroundProperty, "T.CapsuleBg");
        if (!usable && icon != null)
            area.Children.Add(new Image { Source = icon, Width = 40, Height = 40, Opacity = 0.9 });

        var title = new TextBlock
        {
            Text = Native.GetText(w.Hwnd),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0),
        };
        // Like the Windows taskbar's, the close button only shows on the card under the mouse.
        var close = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Opacity = 0,
            Child = new TextBlock
            {
                Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; WindowClosed?.Invoke(w.Hwnd); };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4), Height = 20 };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        var card = new Border
        {
            Padding = new Thickness(CardPadding),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Width = size.Width + CardPadding * 2,
            Child = new StackPanel { Children = { header, area } },
            Cursor = Cursors.Hand,
        };
        card.MouseEnter += (_, _) =>
        {
            card.SetResourceReference(Border.BackgroundProperty, "T.MenuHover");
            close.Opacity = 1;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = Brushes.Transparent;
            close.Opacity = 0;
        };
        card.MouseLeftButtonUp += (_, _) => WindowChosen?.Invoke(w.Hwnd);

        if (usable) thumbs.Add((thumb, area, w.Hwnd));
        cardOf[w.Hwnd] = card;
        return card;
    }

    /// <summary>The window's size in pixels: what DWM will draw, else its on-screen (or restored) frame.</summary>
    static Size WindowSize(IntPtr window, IntPtr thumb)
    {
        if (thumb != IntPtr.Zero && DwmQueryThumbnailSourceSize(thumb, out var s) == 0 && s.cx > 0 && s.cy > 0)
            return new Size(s.cx, s.cy);
        if (Native.DwmGetWindowAttribute(window, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, 16) == 0 && r.Width > 0 && r.Height > 0)
            return new Size(r.Width, r.Height);
        return new Size(16, 10);
    }

    /// <summary>Scales a window's shape to fit the preview box (in DIPs).</summary>
    static Size Fit(Size source)
    {
        double scale = Math.Min(MaxThumbWidth / source.Width, MaxThumbHeight / source.Height);
        return new Size(Math.Round(source.Width * scale), Math.Round(source.Height * scale));
    }

    void PlaceThumbnails()
    {
        if (hwnd == IntPtr.Zero || thumbs.Count == 0 || !IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var (thumb, area, _) in thumbs)
        {
            if (!area.IsVisible) continue;
            var topLeft = area.TranslatePoint(new Point(0, 0), this);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new Native.RECT
                {
                    Left = (int)Math.Round(topLeft.X * dpi.DpiScaleX),
                    Top = (int)Math.Round(topLeft.Y * dpi.DpiScaleY),
                    Right = (int)Math.Round((topLeft.X + area.ActualWidth) * dpi.DpiScaleX),
                    Bottom = (int)Math.Round((topLeft.Y + area.ActualHeight) * dpi.DpiScaleY),
                },
                opacity = 255,
                fVisible = 1,
                fSourceClientAreaOnly = 0,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void ClearThumbs()
    {
        foreach (var (thumb, _, _) in thumbs) DwmUnregisterThumbnail(thumb);
        thumbs.Clear();
        cardOf.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearThumbs();
        base.OnClosed(e);
    }

    const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public Native.RECT rcDestination;
        public Native.RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out Native.SIZE size);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
}
