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
    /// Gap between a resting icon and the pill's top and bottom edges. The pill can be taller than
    /// icon + 16 (a large clock needs room), so this centres the icons in whatever height it has.
    /// Magnified icons grow upward from the same bottom gap.
    /// </summary>
    public static double Inset { get; set; } = 8;

    public double SlotWidth => Size + Spacing;
    public double SlotHeight => Size + Inset * 2;
    public System.Windows.Thickness IconMargin => new(0, 0, 0, Inset);

    /// <summary>Re-measures the slot after <see cref="Spacing"/> or <see cref="Inset"/> changes.</summary>
    public void RefreshSlot()
    {
        OnChanged(nameof(SlotWidth));
        OnChanged(nameof(SlotHeight));
        OnChanged(nameof(IconMargin));
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
