using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Tasky;

/// <summary>Mouse on the app icons: magnification, name label, window previews, dragging, clicking and launching.</summary>
public partial class MainWindow
{
    // ---- Magnification ----------------------------------------------------------------------

    void OnAppsMouseMove(object sender, MouseEventArgs e)
    {
        if (dragItem != null && e.LeftButton == MouseButtonState.Pressed && DragApp(e)) return;

        int index = -1;
        double fraction = 0.5;
        for (int i = 0; i < items.Count; i++)
        {
            if (Apps.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            double along = Edge.Along(e.GetPosition(c)), length = Edge.AlongLength(c);
            if (along >= 0 && along < length)
            {
                index = i;
                fraction = along / length;
                break;
            }
        }

        SetHovered(index >= 0 ? items[index] : null);
        if (index < 0) return;

        // A cosine wave centred on the cursor, measured in "icons" so it doesn't depend on size.
        double cursor = index + fraction;
        for (int i = 0; i < items.Count; i++)
        {
            double t = Math.Max(0, 1 - Math.Abs(i + 0.5 - cursor) / MagnifyRange);
            items[i].TargetScale = 1 + (MaxScale - 1) * (1 - Math.Cos(Math.PI * t)) / 2;
        }
        StartAnimating();
    }

    void OnAppsMouseLeave(object sender, MouseEventArgs e)
    {
        SetHovered(null);
        foreach (var item in items) item.TargetScale = 1;
        StartAnimating();
    }

    void StartAnimating()
    {
        if (animating) return;
        animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        bool done = true;
        List<DockItem>? gone = null;
        foreach (var item in items)
        {
            double diff = item.TargetScale - item.Scale;
            if (Math.Abs(diff) < 0.002) item.Scale = item.TargetScale;
            else
            {
                item.Scale += diff * (1 - Math.Pow(1 - 0.28, settings.AnimationSpeed)); // 0.28 per frame at 1x
                done = false;
            }

            // Leaving: shrink away over LeaveTime, easing in and out; back again: grow in.
            if (item.LeavingSince is { } since)
            {
                double t = Math.Min(1, (DateTime.UtcNow - since).TotalMilliseconds / Duration(LeaveTime).TotalMilliseconds);
                item.Presence = (1 + Math.Cos(Math.PI * t)) / 2;
                if (t >= 1) (gone ??= new()).Add(item);
                else done = false;
            }
            else if (item.Presence < 1)
            {
                item.Presence = Math.Min(1, item.Presence + 0.12 * settings.AnimationSpeed);
                done = false;
            }

            item.Size = Math.Round(BaseSize * item.Scale * item.Presence); // whole pixels keep icons off half-pixel blur
            if (item.Presence >= 1) UpdateIcon(item); // not while shrinking away or growing back: no new bitmaps for passing sizes
        }
        if (gone != null)
            foreach (var item in gone) items.Remove(item);
        UpdateLabel();

        if (done)
        {
            CompositionTarget.Rendering -= OnFrame;
            animating = false;
        }
    }

    void SetHovered(DockItem? item)
    {
        if (hovered == item) return;
        if (hovered != null) hovered.IsHovered = false;
        hovered = item;
        if (item != null)
        {
            item.IsHovered = true;
            NameLabelText.Text = item.Name;
        }

        // Previews: shown after a short hover on a running app, then follow the cursor across icons.
        previewShow.Stop();
        if (item is { IsRunning: true })
        {
            previewHide.Stop();
            if (PreviewShown) ShowPreview(item);
            else previewShow.Start();
        }
        else if (PreviewShown)
        {
            previewHide.Stop();
            previewHide.Start();
        }
        UpdateLabel();
    }

    void ShowPreview(DockItem item)
    {
        if (Apps.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c || item.Windows.Count == 0) return;
        if (previews == null)
        {
            previews = new PreviewWindow();
            previews.MouseEnter += (_, _) => previewHide.Stop();
            previews.MouseLeave += (_, _) => { previewHide.Stop(); previewHide.Start(); };
            previews.WindowChosen += h =>
            {
                HidePreview();
                AfterPanelsClose(() =>
                {
                    Activate(h);
                    lastForeground = h;
                    ScheduleRefresh();
                });
            };
            previews.WindowClosed += h =>
            {
                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                var shownFor = previewItem;
                if (!previews.Remove(h)) HidePreview(); // its card goes at once; the dock catches up as the window closes
                // An app that asks first ("Save changes?") is still open a moment later: get out of the way of
                // its question (unless the preview has since moved on to another app).
                var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                later.Tick += (_, _) =>
                {
                    later.Stop();
                    if (previewItem == shownFor && Native.IsWindow(h) && Native.IsWindowVisible(h)) HidePreview();
                };
                later.Start();
            };
        }
        previewItem = item;
        var center = c.PointToScreen(new Point(c.ActualWidth / 2, c.ActualHeight / 2));
        var anchor = Edge switch
        {
            DockEdge.Top => new Point(center.X, pillRect.Bottom),
            DockEdge.Left => new Point(pillRect.Right, center.Y),
            DockEdge.Right => new Point(pillRect.Left, center.Y),
            _ => new Point(center.X, pillRect.Top),
        };
        previews.ShowFor(item.Windows, item.Icon, anchor, Edge, hwnd);
        UpdateLabel();
    }

    void HidePreview()
    {
        previewShow.Stop();
        previewHide.Stop();
        previewItem = null;
        previews?.HidePreviews();
        UpdateLabel();
    }

    void UpdateLabel()
    {
        var c = hovered != null ? Apps.ItemContainerGenerator.ContainerFromItem(hovered) as FrameworkElement : trayHover;
        if (c == null || (PreviewShown && hovered != null)) // the preview names the windows itself
        {
            NameLabel.Visibility = Visibility.Collapsed;
            return;
        }
        NameLabel.Visibility = Visibility.Visible;
        NameLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // An app's container stretches across the whole row; the slot inside it is the icon's own size.
        if (c is ContentPresenter && VisualTreeHelper.GetChildrenCount(c) > 0 && VisualTreeHelper.GetChild(c, 0) is FrameworkElement slot) c = slot;
        var r = new Rect(c.TranslatePoint(new Point(0, 0), Root), new Size(c.ActualWidth, c.ActualHeight));
        double w = NameLabel.DesiredSize.Width, h = NameLabel.DesiredSize.Height;
        var (x, y) = Edge switch
        {
            DockEdge.Top => (r.Left + r.Width / 2 - w / 2, r.Bottom + 2),
            DockEdge.Left => (r.Right + 4, r.Top + r.Height / 2 - h / 2),
            DockEdge.Right => (r.Left - w - 4, r.Top + r.Height / 2 - h / 2),
            _ => (r.Left + r.Width / 2 - w / 2, r.Top - h - 2),
        };
        Canvas.SetLeft(NameLabel, x);
        Canvas.SetTop(NameLabel, y);
    }

    // ---- Drag to reorder pinned apps ----------------------------------------------------------

    const double DragThreshold = 6;
    const double LeaveTime = 220; // ms an icon takes to leave the dock, at 1x animation speed
    DockItem? dragItem;
    Point dragStart;
    bool dragging;

    void OnAppsMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragItem = (e.OriginalSource as FrameworkElement)?.DataContext as DockItem;
        dragStart = e.GetPosition(Apps);
        dragging = false;
    }

    /// <summary>Handles a move while an icon is held; true once it has become a drag.</summary>
    bool DragApp(MouseEventArgs e)
    {
        var p = e.GetPosition(Apps);
        if (!dragging)
        {
            if (Math.Abs(Edge.Along(p) - Edge.Along(dragStart)) < DragThreshold) return false;
            dragging = true;
            Mouse.Capture(Apps, CaptureMode.SubTree);
            HidePreview();
            SetHovered(null);
            foreach (var item in items) item.TargetScale = 1; // magnifying while dragging just jitters
            StartAnimating();
            if (Apps.ItemContainerGenerator.ContainerFromItem(dragItem) is UIElement held) held.Opacity = 0.55;
        }

        // Swap places with whichever pinned icon is under the cursor.
        for (int i = 0; i < items.Count; i++)
        {
            if (Apps.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            double along = Edge.Along(e.GetPosition(c));
            if (along < 0 || along >= Edge.AlongLength(c)) continue;
            var target = items[i];
            if (target != dragItem) MoveApp(dragItem!, target);
            break;
        }
        return true;
    }

    int PinIndex(DockItem item) => settings.Pins.FindIndex(p => PinKey(p).Equals(item.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Moves <paramref name="item"/> to where <paramref name="target"/> is. Pinned apps always come
    /// first, so: pinned among pinned reorders the pins; running among running reorders them for as
    /// long as they stay open; a running app dropped among the pinned ones gets pinned right there.
    /// </summary>
    void MoveApp(DockItem item, DockItem target)
    {
        if (item.IsPinned && target.IsPinned)
        {
            int from = PinIndex(item), to = PinIndex(target);
            if (from < 0 || to < 0) return;
            var pin = settings.Pins[from];
            settings.Pins.RemoveAt(from);
            settings.Pins.Insert(to, pin);
        }
        else if (!item.IsPinned && target.IsPinned)
        {
            if (!item.CanLaunch) return; // nothing to pin
            int to = PinIndex(target);
            if (to < 0) return;
            settings.Pins.Insert(to, item.LaunchPath);
            item.IsPinned = true;
        }
        else if (item.IsPinned && !target.IsPinned)
        {
            return; // pinned apps stay in front of the running-only ones
        }
        items.Move(items.IndexOf(item), items.IndexOf(target));
    }

    void OnAppsLostCapture(object sender, MouseEventArgs e)
    {
        if (dragging && Mouse.LeftButton != MouseButtonState.Pressed) EndAppDrag();
    }

    /// <summary>Ends a drag; true if there was one (so the release isn't also a click).</summary>
    bool EndAppDrag()
    {
        bool wasDragging = dragging;
        if (dragging)
        {
            if (Apps.ItemContainerGenerator.ContainerFromItem(dragItem) is UIElement held) held.Opacity = 1;
            Mouse.Capture(null);
            settings.Save();
        }
        dragging = false;
        dragItem = null;
        return wasDragging;
    }

    // ---- Clicks -----------------------------------------------------------------------------

    void OnAppsMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && EndAppDrag())
        {
            e.Handled = true;
            return;
        }
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DockItem item) return;
        e.Handled = true;
        switch (e.ChangedButton)
        {
            case MouseButton.Left: OnItemClick(item); break;
            case MouseButton.Middle: AfterPanelsClose(() => Launch(item)); break;
            case MouseButton.Right: HidePreview(); ShowItemMenu(item, e.OriginalSource as UIElement); break;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/>, closing anything open over the screen first. Start, Search,
    /// Quick Settings and the notification center stay in front of whatever opens and make Windows
    /// refuse to switch apps; clicking the real taskbar closes them first, and so does this.
    /// </summary>
    void AfterPanelsClose(Action action)
    {
        TrayPopup.IsOpen = false;
        if (activeMenu is { IsOpen: true }) activeMenu.IsOpen = false;
        if (!IsShellUi(Native.GetForegroundWindow()))
        {
            action();
            return;
        }
        Native.PressKeys(Native.VK_ESCAPE);
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            action();
        };
        later.Start();
    }

    void OnItemClick(DockItem item)
    {
        HidePreview();
        AfterPanelsClose(() => ActivateOrLaunch(item));
    }

    void ActivateOrLaunch(DockItem item)
    {
        // The permission prompt: bring up whichever of consent.exe's windows Windows is holding back.
        if (item.Key == WindowEnumerator.UacKey && consentPid is { } consent)
        {
            var prompt = ConsentWatcher.Windows(consent).FirstOrDefault(h => Native.GetWindowTextLength(h) > 0);
            if (prompt != IntPtr.Zero) Activate(prompt);
            return;
        }

        if (!item.IsRunning)
        {
            Launch(item);
            return;
        }

        TrackForeground();
        IntPtr target;
        if (item.Windows.Count == 1)
        {
            var h = item.Windows[0].Hwnd;
            if (h == lastForeground && !Native.IsIconic(h))
            {
                Native.ShowWindow(h, Native.SW_MINIMIZE);
                ScheduleRefresh();
                return;
            }
            target = h;
        }
        else
        {
            // Several windows: cycle through them in a stable order.
            int i = item.Windows.FindIndex(w => w.Hwnd == lastForeground);
            target = i >= 0 ? item.Windows[(i + 1) % item.Windows.Count].Hwnd : item.TopWindow;
        }

        Activate(target);
        lastForeground = target;
        ScheduleRefresh();
    }

    static void Activate(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        if (!Native.SetForegroundWindow(h))
        {
            // Tapping Alt satisfies the foreground-lock rules when Windows refuses the switch.
            Native.PressKeys(Native.VK_MENU);
            Native.SetForegroundWindow(h);
        }
    }

    void Launch(DockItem item)
    {
        if (!item.CanLaunch) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.LaunchPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(item.LaunchPath) ?? "",
            });
            Bounce(item);
        }
        catch (Exception ex) { Log.Error(ex, $"launching {item.LaunchPath}"); }
    }

    void Bounce(DockItem item)
    {
        if (Apps.ItemContainerGenerator.ContainerFromItem(item) is not UIElement c) return;
        var t = new TranslateTransform();
        c.RenderTransform = t;
        // Hop away from the screen edge.
        t.BeginAnimation(Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty,
            new DoubleAnimation(0, 14 * Edge.Inward(), TimeSpan.FromMilliseconds(260))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    void OnStartClick(object sender, MouseButtonEventArgs e) => PressShellKeys(Native.VK_LWIN);

    void OnClockClick(object sender, MouseButtonEventArgs e) => PressShellKeys(Native.VK_LWIN, Native.VK_N);

    void OnQuickSettingsClick(object sender, MouseButtonEventArgs e) => PressShellKeys(Native.VK_LWIN, Native.VK_A);

    static readonly bool selfElevated = Native.IsElevated((uint)Environment.ProcessId);

    /// <summary>
    /// Presses a Windows shortcut (Win, Win+S...) for a dock button. Windows drops key presses that an
    /// ordinary program makes while an app running as administrator (Task Manager, say) has the focus,
    /// so in that case the dock takes the focus first, as clicking the real taskbar would.
    /// </summary>
    void PressShellKeys(params byte[] keys)
    {
        var foreground = Native.GetForegroundWindow();
        if (!selfElevated && foreground != IntPtr.Zero && foreground != hwnd)
        {
            Native.GetWindowThreadProcessId(foreground, out uint pid);
            if (pid != ownPid && Native.IsElevated(pid)) Native.SetForegroundWindow(hwnd);
        }
        Native.PressKeys(keys);
    }
}
