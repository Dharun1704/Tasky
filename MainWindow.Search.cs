using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Tasky;

/// <summary>
/// The search button, and Windows Search's window. On its own (the button, Win+S), Windows Search
/// keeps its search box at the bottom of its window, where the taskbar's box would be, and draws it
/// over the dock. So the button opens out into a box of the same size and the search window is moved
/// to lay Windows' box exactly over it: what you type appears in the dock, with the results above it.
/// It closes back into a magnifier with the search. Typing in Start turns Start into the same window
/// with its box at the top instead; that is centred and lifted clear of the dock like Start.
/// </summary>
public partial class MainWindow
{
    // Windows' search box as it draws it at 100% scale: 220 x 32, 12 px in from the search window's
    // left edge and centred 24 px above its bottom (the middle of the 48 px taskbar it belongs to).
    const double SearchBoxWidth = 220, SearchBoxHeight = 32, SearchBoxInset = 12, SearchBoxAboveBottom = 24;
    // Search typed into Start: the results panel ends this far above the bottom of its window.
    const double StartSearchMargin = 14;
    const double SearchGlyphSize = 14; // Windows' magnifier in its box; the closed button's is larger
    const double SearchOpenTime = 240;

    bool searchOpen, hintHidden;
    /// <summary>The search window came up with Start, so it's Start's search until it closes.</summary>
    bool startSearch;
    /// <summary>The search window last seen, so its closing can be noticed straight away.</summary>
    IntPtr searchWindow;
    DispatcherTimer? searchShownCheck;
    DateTime searchComingUntil;
    /// <summary>The search window's height when open on its own (0 until seen).</summary>
    int searchHeight;
    readonly ScaleTransform searchGlyphScale = new();
    readonly TranslateTransform searchGlyphShift = new();

    /// <summary>
    /// Only a bottom dock opens out, and only with the taskbar search set to "Search box": with
    /// the other settings Windows puts its box at the top of the search window instead.
    /// </summary>
    bool SearchOpensOut => Edge == DockEdge.Bottom &&
        Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search",
                                          "SearchboxTaskbarMode", null) is 2;

    double SearchOpenWidth(double button) => SearchBoxWidth + button - SearchBoxHeight;
    /// <summary>The closed magnifier, sized like the dock's other icons rather than Windows' small one.</summary>
    double ClosedGlyphScale => Math.Round(BaseSize * 0.6) / SearchGlyphSize;
    /// <summary>
    /// The magnifier's circle sits up and to the left of its glyph, the handle trailing down-right,
    /// so centring the glyph makes it look off centre. The closed button nudges it down and right so
    /// the circle reads as centred; open, it sits exactly where Windows draws its own.
    /// </summary>
    double ClosedGlyphShift => Math.Round(BaseSize * 0.6 * 0.08);

    /// <summary>Sizes the button for the dock (from <see cref="ApplyLayout"/>), open or closed.</summary>
    void LayOutSearch(double button)
    {
        SearchButton.Visibility = settings.ShowSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchButton.Width = SearchButton.Height = button;
        SearchButton.Margin = Edge.Margin(0, 0, 4); // centred across the pill, like Start
        // The magnifier is centred in the button; opening out keeps it where it is and grows the box rightwards.
        SearchBox.Margin = new Thickness((button - SearchBoxHeight) / 2, 0, 0, 0);
        SearchBox.Width = SearchBox.Height = SearchBoxHeight;
        SearchFill.CornerRadius = new CornerRadius(SearchBoxHeight / 2);
        SearchGlyph.FontSize = SearchGlyphSize;
        SearchGlyph.Margin = new Thickness((SearchBoxHeight - SearchGlyphSize) / 2, 0, 0, 0);
        SearchGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
        SearchGlyph.RenderTransform = new TransformGroup { Children = { searchGlyphScale, searchGlyphShift } };
        searchGlyphScale.ScaleX = searchGlyphScale.ScaleY = ClosedGlyphScale;
        searchGlyphShift.X = searchGlyphShift.Y = ClosedGlyphShift;
        SearchText.Opacity = 0;
        if (searchOpen && (!settings.ShowSearch || !SearchOpensOut)) SetSearchOpen(false);
        else if (searchOpen) SetSearchOpen(true, animate: false); // to the new size
    }

    public void ApplySearchSetting()
    {
        ApplyLayout();
        ScheduleFit();
    }

    void OnSearchClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HidePreview();
        if (searchOpen)
        {
            Native.PressKeys(Native.VK_ESCAPE); // a second click closes it, as on the taskbar
            return;
        }
        // From Start, Win+S switches straight over to search; closing Start first would play its
        // closing animation and then search's opening one.
        if (ShellWindow("StartMenuExperienceHost", evenCloaked: false) != IntPtr.Zero)
        {
            TrayPopup.IsOpen = false;
            if (activeMenu is { IsOpen: true }) activeMenu.IsOpen = false;
            OpenSearch();
        }
        else AfterPanelsClose(OpenSearch);
    }

    void OpenSearch()
    {
        // Search on its own from here on, even though Windows turns Start's search window
        // straight into it without hiding it in between.
        startSearch = false;
        if (SearchOpensOut)
        {
            SetSearchOpen(true);
            searchComingUntil = DateTime.UtcNow.AddSeconds(1.5);
            SettleSearch();
        }
        PressShellKeys(Native.VK_LWIN, Native.VK_S);
    }

    DispatcherTimer? searchSettle;
    DateTime settleUntil;

    /// <summary>
    /// Windows moves its search window to its own spot as it opens, a frame or two after showing it.
    /// For a moment, follow every move of it as it happens, and check every 10 ms rather than every
    /// 120, so the window is back on the dock's box before Windows' spot is drawn more than a frame.
    /// </summary>
    void SettleSearch()
    {
        settleUntil = DateTime.UtcNow.AddMilliseconds(700);
        var search = ShellWindow("SearchHost", evenCloaked: true);
        if (search != IntPtr.Zero && windowEvents != null)
        {
            Native.GetWindowThreadProcessId(search, out uint pid);
            windowEvents.WatchMoves(pid);
        }
        if (searchSettle == null)
        {
            searchSettle = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
            searchSettle.Tick += (_, _) =>
            {
                if (DateTime.UtcNow < settleUntil && searchOpen)
                {
                    TrackSearch();
                    return;
                }
                searchSettle.Stop();
                windowEvents?.WatchMoves(0);
            };
        }
        searchSettle.Start();
    }

    /// <summary>
    /// The search window was uncloaked. It comes up with Start too, and the two can arrive in either
    /// order, so it's looked at a moment later, once Start would be showing.
    /// </summary>
    void OnSearchShown()
    {
        if (searchShownCheck == null)
        {
            searchShownCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            searchShownCheck.Tick += (_, _) =>
            {
                searchShownCheck.Stop();
                TrackSearch(justShown: true);
            };
        }
        searchShownCheck.Stop();
        searchShownCheck.Start();
    }

    /// <summary>
    /// Keeps the search window in place while it's open (from the auto-hide check and window events):
    /// opens the button out for search on its own, lifts and centres Start's search, and closes the
    /// button up when the search goes.
    /// </summary>
    void TrackSearch(bool justShown = false)
    {
        var search = ShellWindow("SearchHost", evenCloaked: false);
        if (search == IntPtr.Zero)
        {
            if (DateTime.UtcNow < searchComingUntil) return; // opened by the button, not up yet
            startSearch = false;
            if (searchOpen) SetSearchOpen(false);
            return;
        }
        searchWindow = search;
        if (ShellWindow("StartMenuExperienceHost", evenCloaked: false) != IntPtr.Zero)
        {
            if (DateTime.UtcNow < searchComingUntil)
            {
                // The button's search, taking over from Start, which is still on its way out.
                if (searchOpen) AlignSearch(search, fromStart: true);
                return;
            }
            startSearch = true; // laid over Start; AlignStartMenu moves it with Start
            if (searchOpen) SetSearchOpen(false);
            return;
        }
        searchComingUntil = DateTime.MinValue; // it's up
        if (startSearch)
        {
            AlignStartSearch(search);
            return;
        }
        if (justShown && settings.ShowSearch && SearchOpensOut) SetSearchOpen(true);
        if (searchOpen) AlignSearch(search);
    }

    void SetSearchOpen(bool open, bool animate = true)
    {
        if (open == searchOpen && animate) return;
        searchOpen = open;
        double button = PillHeight - 8;
        var time = animate ? Duration(SearchOpenTime) : TimeSpan.Zero;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        void Animate(IAnimatable target, DependencyProperty property, double to)
        {
            var animation = new DoubleAnimation(to, time) { EasingFunction = ease };
            // Once closed, hand the value back to the layout, which sets the same closed size.
            if (!open) animation.Completed += (_, _) => { if (!searchOpen) target.BeginAnimation(property, null); };
            target.BeginAnimation(property, animation);
        }
        Animate(SearchButton, WidthProperty, open ? SearchOpenWidth(button) : button);
        Animate(SearchBox, WidthProperty, open ? SearchBoxWidth : SearchBoxHeight);
        Animate(SearchFill, OpacityProperty, open ? 1 : 0);
        Animate(SearchText, OpacityProperty, open ? 1 : 0);
        // The magnifier shrinks to the size of Windows' own, which takes its place.
        double scale = open ? 1 : ClosedGlyphScale;
        Animate(searchGlyphScale, ScaleTransform.ScaleXProperty, scale);
        Animate(searchGlyphScale, ScaleTransform.ScaleYProperty, scale);
        Animate(searchGlyphShift, TranslateTransform.XProperty, open ? 0 : ClosedGlyphShift);
        Animate(searchGlyphShift, TranslateTransform.YProperty, open ? 0 : ClosedGlyphShift);
        if (!open)
        {
            hintHidden = false;
            Animate(SearchHint, OpacityProperty, 1);
        }
        // The box isn't a button while open: no hover highlight around it.
        if (open) SearchButton.Background = Brushes.Transparent;
        else SearchButton.ClearValue(BackgroundProperty);
        if (!animate) SyncBackdrop();
    }

    /// <summary>
    /// Moves the search window so its box sits on the dock's box, where that box ends up once fully
    /// open (the pill grows both ways as it opens), then fades out the dock's own magnifier and
    /// "Search", which Windows draws in the same places.
    /// </summary>
    void AlignSearch(IntPtr search, bool fromStart = false)
    {
        if (pillRect.Width <= 0 || !Native.GetWindowRect(search, out var r)) return;
        var monitor = DockMonitor().rcMonitor;
        if (r.Right <= monitor.Left || r.Left >= monitor.Right) return; // open on another screen
        // Taking over from Start, Windows makes the window taller (by its taskbar's height) a frame
        // or two later, which would drop the box below the dock for a moment. Give it the height
        // search had last time straight away, so that resize changes nothing.
        if (!fromStart) searchHeight = r.Height;
        else if (searchHeight > 0) r.Top = r.Bottom - searchHeight;

        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double grow = SearchOpenWidth(PillHeight - 8) - SearchButton.ActualWidth;
        double pillLeft = (pillRect.Left + pillRect.Right) / 2.0 - (Pill.ActualWidth + grow) * s / 2;
        double boxLeft = pillLeft + SearchBox.TranslatePoint(new Point(0, 0), Pill).X * s;
        int x = (int)Math.Round(boxLeft - SearchBoxInset * s);
        int bottom = (int)Math.Round((pillRect.Top + pillRect.Bottom) / 2.0 + SearchBoxAboveBottom * s);
        MoveSearch(search, r, x, bottom);
        if (!hintHidden)
        {
            hintHidden = true;
            SearchHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Duration(120)));
        }
    }

    /// <summary>
    /// Start's search: typing in Start replaces Start with this window's results. Windows places it
    /// for its own taskbar, so with the taskbar gone the results would cover the dock, off centre.
    /// Centre it as Start is and end the results where Start's panel ends. Done as soon as Start
    /// opens (from <see cref="AlignStartMenu"/>), while the window is still empty.
    /// </summary>
    void AlignStartSearch(IntPtr search)
    {
        if (pillRect.Width <= 0 || !Native.GetWindowRect(search, out var r)) return;
        var monitor = DockMonitor().rcMonitor;
        if (r.Right <= monitor.Left || r.Left >= monitor.Right) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int x = (monitor.Left + monitor.Right - r.Width) / 2;
        MoveSearch(search, r, x, StartPanelBottom() + (int)Math.Round(StartSearchMargin * s));
    }

    /// <summary>Moves the search window to end at <paramref name="bottom"/>, at the height <paramref name="r"/> gives.</summary>
    static void MoveSearch(IntPtr search, Native.RECT r, int x, int bottom)
    {
        Native.GetWindowRect(search, out var now);
        if (Math.Abs(now.Left - x) <= 1 && Math.Abs(now.Bottom - bottom) <= 1 && now.Height == r.Height) return;
        Native.SetWindowPos(search, IntPtr.Zero, x, bottom - r.Height, r.Width, r.Height,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
}
