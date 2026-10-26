using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tasky;

/// <summary>Right-click menus, the settings window, pinning, and dropping apps onto the dock.</summary>
public partial class MainWindow
{
    // ---- Menus ------------------------------------------------------------------------------

    /// <summary>The ID Windows keeps the app's recent documents under.</summary>
    string AppIdFor(DockItem item)
    {
        if (item.LaunchPath.StartsWith(AppWindow.AppsFolder, StringComparison.OrdinalIgnoreCase))
            return item.LaunchPath[AppWindow.AppsFolder.Length..];
        foreach (var w in item.Windows)
            if ((w.Aumid ?? Native.GetWindowAppUserModelId(w.Hwnd)) is { Length: > 0 } explicitId) return explicitId;
        var exe = item.Windows.FirstOrDefault()?.ExePath ?? item.Key;
        return JumpList.ImplicitAppId(exe);
    }

    /// <summary>Opens a recent document with the app it was recent in (not just the default handler).</summary>
    void OpenRecent(DockItem item, RecentItem recent)
    {
        try
        {
            var exe = item.Windows.FirstOrDefault()?.ExePath ?? item.LaunchPath;
            var start = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) && File.Exists(recent.Path)
                ? new ProcessStartInfo(exe, $"\"{recent.Path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(recent.Path) { UseShellExecute = true };
            Process.Start(start);
        }
        catch (Exception ex) { Log.Error(ex, $"opening {recent.Path}"); }
    }

    void ShowItemMenu(DockItem item, UIElement? target)
    {
        var menu = new ContextMenu();

        var recent = JumpList.Recent(AppIdFor(item), 6);
        if (recent.Count > 0)
        {
            menu.Items.Add(Section("Recent"));
            foreach (var r in recent)
            {
                var entry = Item(FriendlyName(r), () => OpenRecent(item, r), icon: RecentIcon(r));
                entry.ToolTip = r.Path;
                menu.Items.Add(entry);
            }
            menu.Items.Add(new Separator());
        }

        if (item.Windows.Count > 0)
        {
            menu.Items.Add(Section(item.Name));
            foreach (var w in item.Windows)
            {
                var h = w.Hwnd;
                menu.Items.Add(Item(Native.GetText(h), () => Activate(h), icon: MenuImage(item.Icon), active: h == lastForeground));
            }
            menu.Items.Add(new Separator());
        }

        if (item.CanLaunch)
        {
            menu.Items.Add(item.IsRunning
                ? Item("New window", () => Launch(item), icon: Glyph("\uE710"))
                : Item($"Open {item.Name}", () => Launch(item), icon: MenuImage(item.Icon) ?? (object)Glyph("\uE8A7")));
            menu.Items.Add(item.IsPinned
                ? Item("Unpin from dock", () => TogglePin(item), icon: Glyph("\uE77A"))
                : Item("Pin to dock", () => TogglePin(item), icon: Glyph("\uE718")));
        }
        if (item.IsRunning)
        {
            menu.Items.Add(Item(item.Windows.Count > 1 ? "Close all windows" : "Close window", () =>
            {
                foreach (var w in item.Windows) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                ScheduleRefresh();
            }, icon: Glyph("\uE711")));
        }
        Open(menu, target);
    }

    void OnDockRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowDockMenu(sender as UIElement);
    }

    SettingsWindow? settingsWindow;

    void OnSettingsClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSettings();
    }

    public void OpenSettings()
    {
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(settings, this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    // Called by the settings window as its controls change.
    public void ApplyLook()
    {
        ApplyLayout();
        SyncBackdrop();
    }

    public void ApplyModeSetting()
    {
        SetAutoHidden(false);
        Reposition();
    }

    public void ApplyTraySetting() => ApplyTray();

    public void ApplyTaskbarSetting()
    {
        ApplyTaskbar();
        Reposition();
    }

    void ShowDockMenu(UIElement? target)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Dock settings…", OpenSettings, icon: Glyph("\uE713")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Section("When windows reach the dock"));

        foreach (var (label, reserve, autoHide) in new[]
                 {
                     ("Keep them above the dock", true, settings.AutoHide),
                     ("Hide the dock", false, true),
                     ("Show the dock over them", false, false),
                 })
        {
            bool current = settings.ReserveSpace ? reserve : !reserve && settings.AutoHide == autoHide;
            menu.Items.Add(Item(label, () =>
            {
                settings.ReserveSpace = reserve;
                settings.AutoHide = autoHide;
                settings.Save();
                ApplyModeSetting();
                settingsWindow?.Reload();
            }, current));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show tray icons",
            () => { settings.ShowTray = !settings.ShowTray; settings.Save(); ApplyTray(); settingsWindow?.Reload(); }, settings.ShowTray));
        menu.Items.Add(Item("Hide Windows taskbar",
            () => { settings.HideTaskbar = !settings.HideTaskbar; settings.Save(); ApplyTaskbarSetting(); settingsWindow?.Reload(); },
            settings.HideTaskbar));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit Tasky", () => Application.Current.Shutdown(), icon: Glyph("\uE7E8")));

        Open(menu, target);
    }

    void Open(ContextMenu menu, UIElement? target)
    {
        if (activeMenu is { IsOpen: true }) activeMenu.IsOpen = false; // one menu at a time
        activeMenu = menu; // keeps an auto-hiding dock on screen while it's up
        menu.Opened += (_, _) => PopupOpening();
        target ??= Pill;
        menu.PlacementTarget = target;
        menu.Placement = MenuPlacement;
        var offset = ClearOfDock(target, MenuShadowRoom);
        menu.HorizontalOffset = offset.X;
        menu.VerticalOffset = offset.Y;
        // Start would stay in front of it, so it's closed first.
        AfterPanelsClose(() =>
        {
            if (activeMenu == menu) menu.IsOpen = true;
        });
    }

    /// <summary>A menu row: <paramref name="icon"/> is a glyph or image; <paramref name="active"/> marks the window in front.</summary>
    static MenuItem Item(string header, Action onClick, bool isChecked = false, object? icon = null, bool active = false)
    {
        var item = new MenuItem
        {
            Header = new TextBlock { Text = header, TextTrimming = TextTrimming.CharacterEllipsis },
            IsChecked = isChecked,
            Icon = icon,
            Tag = active ? "Active" : null,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    static MenuItem Section(string title) => new() { Header = title, Style = (Style)Application.Current.FindResource("MenuHeader") };

    /// <summary>A 16 px icon from Segoe Fluent Icons for a menu row.</summary>
    static TextBlock Glyph(string code) => new()
    {
        Text = code,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Image? MenuImage(ImageSource? source)
    {
        if (source == null) return null;
        var image = new Image { Source = source, Width = 16, Height = 16, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    /// <summary>"youtube.com/shorts/abc" for a web address; the file or folder name otherwise.</summary>
    static string FriendlyName(RecentItem r)
    {
        if (Uri.TryCreate(r.Path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            return (host + uri.AbsolutePath).TrimEnd('/');
        }
        return r.Name;
    }

    static object RecentIcon(RecentItem r)
    {
        if (r.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Glyph("\uE774"); // globe
        return MenuImage(IconLoader.Small(r.Path)) ?? (object)Glyph("\uE8A5"); // the file's own icon, else a page
    }

    void TogglePin(DockItem item)
    {
        if (item.IsPinned)
        {
            settings.Pins.RemoveAll(p => string.Equals(PinKey(p), item.Key, StringComparison.OrdinalIgnoreCase));
            item.IsPinned = false;
        }
        else
        {
            settings.Pins.Add(item.LaunchPath);
        }
        settings.Save();
        Refresh();
    }

    // ---- Drag an .exe or shortcut onto the dock to pin it -----------------------------------

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedApps(e).Any() ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        foreach (var path in DroppedApps(e))
            if (!settings.Pins.Any(p => PinKey(p).Equals(PinKey(path), StringComparison.OrdinalIgnoreCase)))
                settings.Pins.Add(path);
        settings.Save();
        Refresh();
    }

    static IEnumerable<string> DroppedApps(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();
}
