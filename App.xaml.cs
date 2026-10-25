using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tasky;

public partial class App : Application
{
    Mutex? singleInstance;
    // Held directly: WPF clears Application.MainWindow when the window closes during shutdown,
    // before OnExit runs, which used to skip the cleanup that restores the Windows taskbar.
    MainWindow? dock;

    // Launching Tasky while it's running signals this, and the running copy opens its settings.
    readonly EventWaitHandle showSettings = new(false, EventResetMode.AutoReset, @"Local\Tasky.ShowSettings");

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstance = new Mutex(true, @"Local\Tasky.SingleInstance", out bool created);
        if (!created)
        {
            showSettings.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        AutoStart.Repair();
        StartMenuShortcut.Repair();
        ThreadPool.RegisterWaitForSingleObject(showSettings,
            (_, _) => Dispatcher.InvokeAsync(() => dock?.OpenSettings()), null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += (_, a) => { Log.Error(a.Exception, "ui"); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            Log.Error(a.ExceptionObject as Exception, "fatal");
            dock?.Cleanup();
        };
        SessionEnding += (_, _) => dock?.Cleanup();

        Resources["AccentBrush"] = new SolidColorBrush(ReadAccent());

        dock = new MainWindow();
        dock.Closed += (_, _) => dock.Cleanup();
        MainWindow = dock;
        dock.Show();
        if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) dock.OpenSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        dock?.Cleanup();
        base.OnExit(e);
    }

    /// <summary>The Windows accent colour, lightened the way dark-mode Windows does.</summary>
    static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int v)
            {
                uint abgr = unchecked((uint)v);
                byte r = (byte)(abgr & 0xFF), g = (byte)((abgr >> 8) & 0xFF), b = (byte)((abgr >> 16) & 0xFF);
                static byte Lift(byte c) => (byte)(c + (255 - c) * 0.4);
                return Color.FromRgb(Lift(r), Lift(g), Lift(b));
            }
        }
        catch { }
        return Color.FromRgb(0x60, 0xCD, 0xFF);
    }
}
