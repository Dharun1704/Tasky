using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Tasky;

/// <summary>One icon on the dock: a pinned app, a running app, or both.</summary>
internal sealed class DockItem(string key, string launchPath) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Lower-cased exe path (or shortcut path) used to match windows to the item.</summary>
    public string Key { get; } = key;
    public string LaunchPath { get; set; } = launchPath;
    public string Name { get; set; } = "";
    public bool IsPinned { get; set; }

    /// <summary>Windows in a stable order (first seen first) so clicking cycles predictably.</summary>
    public List<AppWindow> Windows { get; } = new();
    /// <summary>The group's most recently used window.</summary>
    public IntPtr TopWindow { get; set; }

    // Magnification state, driven by the render loop.
    public double Scale = 1, TargetScale = 1;

    /// <summary>Where the icon comes from (null when it could only be read from a window), and the pixel size loaded.</summary>
    public string? IconPath;
    public int IconPx;

    ImageSource? icon;
    public ImageSource? Icon { get => icon; set => Set(ref icon, value); }

    bool isRunning;
    public bool IsRunning { get => isRunning; set => Set(ref isRunning, value); }

    bool isActive;
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }

    bool isAttention;
    public bool IsAttention { get => isAttention; set => Set(ref isAttention, value); }

    bool isHovered;
    public bool IsHovered { get => isHovered; set => Set(ref isHovered, value); }

    double size;
    public double Size
    {
        get => size;
        set
        {
            if (!Set(ref size, value)) return;
            OnChanged(nameof(SlotWidth));
            OnChanged(nameof(SlotHeight));
        }
    }

    /// <summary>Space between icons (the dock's spacing setting).</summary>
    public static double Spacing { get; set; } = 10;

    /// <summary>
    /// Gap between a resting icon and the pill's two long edges. The pill can be thicker than
    /// icon + 16 (a large clock needs room), so this centres the icons in whatever thickness it has.
    /// Magnified icons grow away from the screen edge, from the same gap on the edge side.
    /// </summary>
    public static double Inset { get; set; } = 8;

    /// <summary>The screen edge the dock is on; decides which way slots run and icons grow.</summary>
    public static DockEdge Edge { get; set; } = DockEdge.Bottom;

    double Along => Size + Spacing;
    double Across => Size + Inset * 2;
    public double SlotWidth => Edge.IsVertical() ? Across : Along;
    public double SlotHeight => Edge.IsVertical() ? Along : Across;
    public System.Windows.Thickness IconMargin => Edge.Margin(0, 0, Inset);
    public System.Windows.Thickness IndicatorMargin => Edge.Margin(0, 0, 2);
    public System.Windows.Thickness HoverMargin => Edge.Margin(1, 1, 2, 4);
    public System.Windows.HorizontalAlignment EdgeH => Edge.EdgeH();
    public System.Windows.VerticalAlignment EdgeV => Edge.EdgeV();
    /// <summary>The running/active bar is drawn lying along a bottom dock; it stands up on a side dock.</summary>
    public System.Windows.Media.Transform IndicatorTransform =>
        Edge.IsVertical() ? new System.Windows.Media.RotateTransform(90) : System.Windows.Media.Transform.Identity;

    /// <summary>Re-measures the slot after <see cref="Spacing"/>, <see cref="Inset"/> or <see cref="Edge"/> changes.</summary>
    public void RefreshSlot()
    {
        foreach (var p in new[] { nameof(SlotWidth), nameof(SlotHeight), nameof(IconMargin), nameof(IndicatorMargin),
                                  nameof(HoverMargin), nameof(EdgeH), nameof(EdgeV), nameof(IndicatorTransform) })
            OnChanged(p);
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
