using System.Runtime.InteropServices;

namespace Tasky;

/// <summary>
/// Notifies when top-level windows appear, disappear, minimize, finish moving, change focus or
/// switch virtual desktop, so the dock can react instead of scanning every window on a timer.
/// The callbacks arrive on the UI thread (hooks are "out of context", delivered via its message loop).
/// </summary>
internal sealed class WindowEvents : IDisposable
{
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MOVESIZEEND = 0x000B,
               EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017,
               EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003,
               EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    const uint WINEVENT_OUTOFCONTEXT = 0x0;
    const int OBJID_WINDOW = 0, CHILDID_SELF = 0;

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

    readonly WinEventProc proc; // kept in a field so the GC can't collect the callback thunk
    readonly List<IntPtr> hooks = new();

    /// <summary>A window came, went, minimized/restored or changed desktop: the app list may differ.</summary>
    public event Action? WindowsChanged;
    /// <summary>A window moved, resized or changed focus: what overlaps the dock may differ.</summary>
    public event Action? LayoutChanged;
    /// <summary>A top-level window was just shown or uncloaked (Explorer bringing its taskbar back, Start opening).</summary>
    public event Action<IntPtr>? WindowShown;

    public WindowEvents()
    {
        proc = OnEvent;
        foreach (var (min, max) in new[]
                 {
                     (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
                     (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND),
                     (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                     (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE), // also covers EVENT_OBJECT_SHOW
                     (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
                 })
        {
            var hook = SetWinEventHook(min, max, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (hook != IntPtr.Zero) hooks.Add(hook);
        }
    }

    void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Only whole top-level windows: not carets, menus' items or controls inside windows.
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        if (GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;

        if (evt is EVENT_OBJECT_SHOW or EVENT_OBJECT_UNCLOAKED) WindowShown?.Invoke(hwnd);
        LayoutChanged?.Invoke();
        if (evt is EVENT_SYSTEM_MOVESIZEEND) return;
        WindowsChanged?.Invoke();
    }

    public void Dispose()
    {
        foreach (var h in hooks) UnhookWinEvent(h);
        hooks.Clear();
    }
}
