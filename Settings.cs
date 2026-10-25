using System.IO;
using System.Text.Json;

namespace Tasky;

internal sealed class Settings
{
    public List<string> Pins { get; set; } = new();
    public int IconSize { get; set; } = 36;
    public bool Magnify { get; set; } = true;
    /// <summary>How large the icon under the cursor grows, as a multiple of its size.</summary>
    public double MagnifyScale { get; set; } = 1.6;
    /// <summary>Space between app icons, in DIPs.</summary>
    public double IconSpacing { get; set; } = 10;
    /// <summary>Corner radius of the dock, in DIPs (with blur, snapped to what Windows allows).</summary>
    public double CornerRadius { get; set; } = 18;
    /// <summary>Gap between the dock and the bottom of the screen, in DIPs.</summary>
    public double EdgeGap { get; set; } = 8;
    public double BackgroundOpacity { get; set; } = 0.70;
    /// <summary>Font size of the clock's time, in DIPs; the date is drawn a little smaller.</summary>
    public double ClockFontSize { get; set; } = 14;
    public bool Blur { get; set; } = true;
    public bool ReserveSpace { get; set; } = true;
    /// <summary>When not reserving space: slide the dock away while the active window overlaps it.</summary>
    public bool AutoHide { get; set; } = true;
    public bool HideTaskbar { get; set; }
    public bool ShowTray { get; set; } = true;
    /// <summary>Identifiers of tray icons pinned to the dock; null means ManagedShell's defaults.</summary>
    public List<string>? TrayPins { get; set; }

    // Crash recovery: lets the next launch put the Windows taskbar back the way it was.
    public bool TaskbarHiddenByUs { get; set; }
    public bool TaskbarWasAutoHide { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tasky");
    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? CreateDefault();
        }
        catch (Exception ex) { Log.Error(ex, "loading settings"); }
        return CreateDefault();
    }

    static Settings CreateDefault()
    {
        var s = new Settings();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        s.Pins.Add(Path.Combine(windows, "explorer.exe"));
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft\Edge\Application\msedge.exe");
        if (File.Exists(edge)) s.Pins.Add(edge);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Error(ex, "saving settings"); }
    }
}

internal static class Log
{
    public static void Error(Exception? ex, string context = "")
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "tasky.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}{Environment.NewLine}");
        }
        catch { }
    }
}
