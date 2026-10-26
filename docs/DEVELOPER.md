# Tasky developer guide

This guide is for someone who knows C++ and the Win32 API but hasn't used C# or WPF. Part 1 maps the C# and WPF ideas used in Tasky onto what you already know. Part 2 explains how Tasky is put together and how each piece works. Part 3 covers building, debugging and common changes.

**Contents**

- [Part 1: C# and WPF for a C++ developer](#part-1-c-and-wpf-for-a-c-developer)
- [Part 2: How Tasky works](#part-2-how-tasky-works)
- [Part 3: Working on Tasky](#part-3-working-on-tasky)

---

# Part 1: C# and WPF for a C++ developer

## The toolchain

| C++ | C# / .NET |
| --- | --- |
| CMakeLists.txt / .vcxproj | `Tasky.csproj` (MSBuild XML: target framework, packages, build settings) |
| vcpkg / Conan | NuGet. Tasky uses one package, `ManagedShell`, listed in the .csproj |
| Compiler + linker | `dotnet build`. Produces IL (bytecode) in `Tasky.dll`, plus a small `Tasky.exe` launcher |
| Runtime | The .NET runtime JIT-compiles IL to machine code. `dotnet publish` with ReadyToRun precompiles most of it |
| Headers / translation units | None. Every `.cs` file in the folder is compiled together; any type can use any other |

```bash
dotnet build                               # debug build into bin\Debug\net10.0-windows\
dotnet run                                 # build and start
dotnet publish -c Release -o publish       # single-file release build into publish\
```

## The language

**Files, namespaces, imports.** `namespace Tasky;` at the top of a file puts everything in it in that namespace. `using System.Windows;` is like `using namespace` and is how you reach types without their full name. There are no `#include`s and no forward declarations.

**Classes and structs.**

- `class`: always a reference type. Instances live on the garbage-collected heap, variables are references (think `std::shared_ptr` without the counting), and you never `delete`.
- `struct`: a value type, copied on assignment like a C++ struct. Tasky uses structs mostly to mirror Win32 structures (`RECT`, `MONITORINFO`).
- `record`: a class with value-style equality and a one-line declaration, e.g. `record AppWindow(IntPtr Hwnd, string Title, ...)`, which generates the constructor, read-only properties and `Equals`.
- `static class`: a class with only static members, like a C++ namespace of free functions (`Native`, `IconLoader`).
- `sealed`: like `final`. `internal`: visible inside this assembly only (most Tasky types).
- **Primary constructors:** `class AppBar(uint callbackMessage)` declares constructor parameters that the whole class body can use, like captured members.

**Partial classes.** `public partial class MainWindow` can be split across several files, and the compiler merges them into one class. Tasky's main window is split by area (`MainWindow.Layout.cs`, `MainWindow.Tray.cs`, ...). It's like defining a class's members across several `.cpp` files, but fields and methods can be declared in any part.

**Properties.** `public int IconSize { get; set; } = 36;` is a field with generated getter and setter, initialised to 36. `double Gap => settings.EdgeGap;` is a read-only property computed on every access (an inline getter). `=>` on a method means "the body is this one expression".

**Nullable types.** `string?` means "may be null"; the compiler warns if you dereference it without checking. `int?` is `std::optional<int>`. Operators you'll see: `a ?? b` (a if not null, else b), `a?.B` (null if a is null), `x!` (assert not null).

**Memory and resources.**

- The GC frees managed objects. Native handles (HICON, HBITMAP, COM pointers) are not managed, so Tasky frees them explicitly: `DestroyIcon`, `DeleteObject`, `Marshal.ReleaseComObject`.
- `using var key = Registry...;` disposes `key` at the end of the scope, like RAII. `IDisposable.Dispose()` is the destructor-like cleanup method.
- **Keep callbacks alive:** if you hand a C# function to Win32 as a callback (window procedure, WinEvent hook), keep the delegate in a field. Otherwise the GC can collect it while Windows still calls it (see `BackdropWindow.Proc`, `WindowEvents.proc`).

**Delegates, lambdas, events.**

- `Action` / `Func<T>` are like `std::function<void()>` / `std::function<T()>`.
- Lambdas: `(_, _) => Refresh()`, where `_` means "ignored parameter".
- Events are multicast callbacks: `timer.Tick += handler;` subscribes, `-=` unsubscribes. Like a signal/slot list.

**LINQ** is `<algorithm>` and ranges with method syntax: `items.Where(i => i.IsPinned).Select(i => i.Key).ToList()`, `list.Any(...)`, `FirstOrDefault(...)`. It's lazy until `ToList()`, `Count()`, `Any()` and so on.

**Pattern matching** is used a lot:

```csharp
if (sender is FrameworkElement el) ...                 // dynamic_cast + bind if it succeeds
if (item is { IsRunning: true }) ...                   // non-null and IsRunning == true
var x = edge switch { DockEdge.Top => 1, _ => 0 };     // switch as an expression
if (ResolveShortcut(pin) is { Arguments: "" } target)  // property pattern + bind
```

**Other syntax:**

- String interpolation: `$"{count} px"`.
- Verbatim strings: `@"C:\path"`, where backslashes aren't escapes.
- `\uE713`: a Unicode escape. Tasky's icon glyphs from the Segoe Fluent Icons font are written this way.
- Tuples: `var (x, y) = (1, 2);`, `(string Path, string Arguments)? ResolveShortcut(...)`.

**Exceptions** work like C++'s: `try { } catch (Exception ex) { } finally { }`. Tasky catches around anything that talks to Windows, logs the failure, and carries on; a dock must not crash.

## Calling Win32 and COM (interop)

This is the part closest to C++, and Tasky uses it heavily.

```csharp
[DllImport("user32.dll")]
public static extern bool SetForegroundWindow(IntPtr hWnd);
```

- `[DllImport]` declares a function exported by a DLL, like a prototype the runtime binds at first call (P/Invoke). `extern` means "body is native".
- `IntPtr` is a pointer-sized integer, used for `HWND`, `HICON`, `HANDLE` and `void*`.
- `[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }` mirrors a C struct with the same layout. Fields that Tasky never reads still have to be there to keep offsets right.
- `out` / `ref` parameters are pointers to the caller's variable: `GetWindowRect(h, out var r)`.
- `Marshal` copies between managed and native memory: `Marshal.PtrToStringUni`, `Marshal.AllocHGlobal` / `FreeHGlobal`, `Marshal.SizeOf<T>()`.
- **COM:** `[ComImport, Guid("..."), InterfaceType(InterfaceIsIUnknown)] interface IShellItem { ... }` declares a COM interface. Methods must be listed in vtable order, including ones Tasky never calls, up to the last one it uses. `[PreserveSig]` returns the raw HRESULT instead of throwing.
- **`dynamic`** calls COM automation objects (IDispatch) late-bound, the way VBScript would. Tasky uses it for `WScript.Shell` to read and write `.lnk` shortcuts.

Almost all of Tasky's Win32 declarations are in `Native.cs`. A few live next to the only code that uses them.

## WPF for a Win32 developer

WPF is Microsoft's retained-mode UI framework. You describe a tree of elements; WPF lays it out, renders it with DirectX, and handles input.

- **XAML** (`MainWindow.xaml`) is XML describing the element tree, like a much richer `.rc` dialog template. `x:Name="Pill"` creates a field `Pill` you can use from C#. The C# half of a window is its *code-behind* (`MainWindow.xaml.cs`), and both halves are parts of the same partial class.
- **Layout:** each element is measured, then arranged. Panels decide placement: `Grid` overlays children, `StackPanel` lines them up, `Canvas` uses absolute coordinates. `HorizontalAlignment` and `VerticalAlignment` place a child inside the space its parent gives it.
- **DIPs, not pixels:** sizes are in device-independent pixels (1/96 inch). At 150% scaling, 1 DIP = 1.5 px. Win32 calls use pixels, so Tasky multiplies or divides by `VisualTreeHelper.GetDpi(this).DpiScaleX` at the boundary.
- **Data binding:** `Width="{Binding SlotWidth}"` reads `SlotWidth` from the element's `DataContext` object. When that object raises `PropertyChanged` (`INotifyPropertyChanged`), WPF re-reads it and updates the UI. `DockItem` works this way: change `item.Size`, and the icon resizes.
- **Lists:** `ItemsControl` + `ItemsSource` + `DataTemplate`. Give it a list of `DockItem`s and a template, and it creates one visual per item. `ObservableCollection<T>` tells it when items are added, removed or moved.
- **Styles, templates and triggers:** a `Style` sets properties on every element of a type, and a `ControlTemplate` replaces a control's visuals entirely (Tasky's menus are restyled this way in `App.xaml`, the settings window's sliders and toggles in `SettingsWindow.xaml`). Triggers change properties when a condition holds, e.g. hover.
- **Resources:** named values in a dictionary. `{StaticResource X}` is looked up once; `{DynamicResource X}` re-reads it when it changes, which is how the theme switches live (`Theme.cs` swaps the `T.*` brushes).
- **The UI thread:** WPF objects may only be touched on the thread that created them, which runs a Win32 message loop.
  - `DispatcherTimer` ticks on that thread, like `SetTimer`.
  - `Dispatcher.InvokeAsync(action, priority)` is `PostMessage` with a closure: it queues work for that thread.
  - `CompositionTarget.Rendering` fires once per rendered frame; Tasky uses it for animations.
- **The HWND underneath:** every WPF `Window` has a real HWND. `new WindowInteropHelper(this).Handle` gets it, and `HwndSource.AddHook(WndProc)` sees its raw messages, like subclassing. Tasky's `WndProc` handles the shell hook, app-bar notifications, the hotkey and setting changes.
- **Transparency:** `AllowsTransparency="True"` makes the window a layered window (`WS_EX_LAYERED`, per-pixel alpha). Clicks pass through pixels whose alpha is 0, which is why the dock window can span the whole screen width yet only the pill and icons catch the mouse.
- **Popups and menus:** `ContextMenu` and `Popup` are separate top-level windows WPF creates for you.

---

# Part 2: How Tasky works

## The windows involved

| Window | What it is |
| --- | --- |
| **Dock** (`MainWindow`) | A transparent, topmost, layered WPF window as wide as the screen (or as tall, for a side dock). It never takes focus (`WS_EX_NOACTIVATE`) and stays out of Alt+Tab (`WS_EX_TOOLWINDOW`). Holds the pill, the icons, the tray, the clock and the name label. |
| **Blur backdrop** (`BackdropWindow`) | A plain Win32 window placed exactly under the pill, with acrylic blur. It exists because DWM blur on the layered dock window would blur the whole transparent rectangle, not just the pill. The dock is *owned* by it, which keeps the dock above it in z-order. |
| **Previews** (`PreviewWindow`) | Shows live DWM thumbnails of an app's windows on hover. A normal, non-layered window, because DWM can't draw thumbnails into a layered one. |
| **Settings** (`SettingsWindow`) | A Windows 11 Settings-style window (Mica backdrop, sidebar pages, one card per setting); changes apply to the dock as you make them. |
| **Tray** (ManagedShell's `TrayService`) | A hidden window with the class name `Shell_TrayWnd`, kept ahead of Explorer's. Apps' `Shell_NotifyIcon` calls find it first, so their tray icons come to Tasky. |
| **Explorer's taskbar** | Still running. Tasky hides it when "Hide the Windows taskbar" is on, and restores it on exit. |

## Start-up

1. **`App.OnStartup`** (`App.xaml.cs`):
   - Takes a named mutex so only one Tasky runs. A second launch signals the first one (named event `Local\Tasky.ShowSettings`) to open its settings, then exits.
   - Repairs the "start with Windows" entry and the Start menu shortcut if they point at an old exe.
   - Installs crash handlers that log the error and restore the Windows taskbar.
   - Creates `MainWindow`.
2. **`MainWindow` constructor** (`MainWindow.xaml.cs`): loads settings, applies the theme, builds the XAML, and wires timers and events.
3. **`OnSourceInitialized`** runs once the HWND exists:
   - Sets the extended window styles.
   - Registers for shell-hook messages, the app-bar callback, `TaskbarCreated` (Explorer restarted) and the Ctrl+Alt+D hotkey.
   - Starts the window-event hooks and the network and power listeners.
   - Then: `ApplyTaskbar` → `ApplyLayout` → `ApplyTray` → `Refresh` → first clock and status update → start timers.

## What keeps it running

Tasky is mostly event-driven, which is what keeps its CPU use low.

| Source | Where | Triggers |
| --- | --- | --- |
| Shell hook (`RegisterShellHookWindow`) | `WndProc` | Windows created or destroyed, flashing → `ScheduleRefresh` |
| WinEvent hooks (`SetWinEventHook`) | `WindowEvents.cs` | Foreground change, minimize, show/hide/destroy, cloak (virtual desktops) → refresh. Move/resize end → "what overlaps the dock may have changed" |
| `refreshSoon` timer (60 ms, one-shot) | `ScheduleRefresh` | Debounces bursts of events into one `Refresh()` |
| `tick` timer (1 s) | `OnTick` | Permission-prompt check, clock and volume (only while the dock is visible), a fallback `Refresh` every 5 s, fullscreen detection, staying on top |
| `autoHideTimer` (120 ms; 500 ms over fullscreen) | `UpdateAutoHide` | Mouse at the screen edge, mouse over the dock, overlap check (cached), closing popups on outside clicks |
| `CompositionTarget.Rendering` | `OnFrame`, `OnSlideFrame` | Per-frame animation, only while something is animating |
| Network, power and setting changes | `NetworkChange`, `SystemEvents`, `WM_SETTINGCHANGE` | Refresh the status icons, follow the Windows theme, reposition |

## The app list (`MainWindow.Apps.cs`, `WindowEnumerator.cs`)

`Refresh()` rebuilds what's on the dock:

1. **`WindowEnumerator.GetAppWindows`** walks top-level windows (`EnumWindows`) and keeps the ones the taskbar would show, roughly the Alt+Tab rules:
   - visible, or announced to us via the shell hook (e.g. the background UAC prompt);
   - not owned, not a tool window, not `WS_EX_NOACTIVATE` (unless `WS_EX_APPWINDOW`);
   - not cloaked (suspended Store apps, other virtual desktops);
   - has a title, isn't a shell class, isn't Tasky.
2. **`Identify`** decides which app each window belongs to, producing an `AppWindow` with a `GroupKey`:
   - **Store apps:** they run inside `ApplicationFrameHost`; the real app owns a child window, so its process is used. Minimized Store apps detach that child, so the last one seen is remembered.
   - **Installed web apps (Netflix...):** they run inside `msedge.exe` but declare their own App User Model ID (AUMID) on the window. IDs containing `!` (packages) or `_crx_` (Chromium web apps) are used as the group; a plain browser window's ID (`MSEdge`) isn't.
   - **Games with nameless exes (Valorant):** if the exe has no file description or product name, a Start menu shortcut named like the window title stands for it.
   - **Windows whose program can't be read:** grouped on their own (`window:<hwnd>`).
   - **The permission prompt:** windows of `consent.exe` all go to one `uac:consent` group.
   - Process path and AUMID are cached per process, and window AUMIDs per window, so a refresh doesn't reopen every process.
3. **Grouping and order:** pinned apps first, in pin order (`settings.Pins`, matched through `PinKey`); then the permission prompt if there is one; then running apps, in the order they first appeared.
4. **`GetOrCreate`** returns the existing `DockItem` for a key or creates one, with its name (`DisplayName`: file description, then product name, skipping placeholders like "Electron") and icon.
5. Each item's window list, running, active and attention state are updated. Then **overflow** splits off apps that don't fit, and **`Sync`** turns the visible list into the minimal moves, inserts and removes on the `ObservableCollection`, so existing icons keep their visuals and animation state.

**`DockItem`** (`DockItem.cs`) is the view model for one icon: key, launch path, name, icon, windows, flags, and the current size. The XAML template binds to its properties.

## Layout and screen edges (`MainWindow.Layout.cs`, `DockEdge.cs`)

- **The structure** (in `MainWindow.xaml`): `Root` (Grid) → `DockHost` (Grid, slides for auto-hide) → `Pill` (the background band) plus `DockRow`, a StackPanel holding Start, a separator, the `Apps` list, the overflow button, a separator, the tray, the status capsule and the settings button. The name label sits in a `Canvas` above everything.
- **Edges:** `DockEdge` is Bottom, Top, Left or Right, and `DockEdges` holds the rules: alignment against the edge, margins written "as for a bottom dock" and rotated, the axis along the dock, and which way is inward. `ApplyOrientation` turns the rows vertical for side docks and swaps the items panel template.
- **Thickness:** `PillHeight` is the dock's thickness whichever edge it's on: tall enough for the icons, and for the two-line clock (horizontal) or the clock's width (vertical).
- **`ApplyLayout`** sizes every fixed element from the icon size and edge, then calls `Reposition`.
- **`Reposition`** places the window: it spans the monitor along the dock, and its depth across fits the largest magnified icon plus the label. With "keep windows above the dock", `AppBar.Reserve` registers an app bar so Windows shrinks the work area.
- **`SyncBackdrop`** runs after every layout pass. It moves the blur window to exactly under the pill (in pixels), clipping it while the dock slides, and records `pillRect`, which auto-hide and menu placement use.
- **Fitting many apps (`FitToScreen`):** after each refresh it adds up the fixed parts of the row, child by child, because WPF caps a panel's measured size at the window size. If the apps don't fit, it lowers the icon size (`fitSize`, down to 24 px). If even that doesn't fit, it sets `overflowCount`, and `Refresh` moves the last running apps into the "…" menu. It grows back only with some room to spare, so the dock doesn't flip between two sizes.

## Magnification, previews, dragging, clicks (`MainWindow.Interaction.cs`)

- **Magnification:** on mouse move, find the icon under the cursor and the cursor's fraction across it, giving a position in "icon units". Each icon's target scale is `1 + (max - 1) * (1 - cos(pi * t)) / 2`, where `t` falls off linearly over 2.2 icons. `OnFrame` eases each icon's scale toward its target every frame and stops when they've all arrived.
- **Icons:** `IconLoader.FromPath` gets a 256 px icon from the shell (`IShellItemImageFactory`) once, then makes each displayed size by area-averaging (`Downscale`). It caches sizes in 4 px steps and frees the 256 px original after preloading. Scaling at draw time looked jagged, and asking Windows for odd sizes gave hard-edged icons.
- **Name label:** placed beside the hovered icon's slot, on the screen-facing side.
- **Previews:** after a 450 ms hover on a running app, `PreviewWindow` registers a DWM thumbnail per window (`DwmRegisterThumbnail`), including minimized ones, since DWM keeps their last image. Clicking a card switches to the window. × (shown on the card under the mouse) closes it: the card fades and the rest re-centre in one move (`Remove`, `Place`), and if the app is still open 400 ms later (a "Save changes?" prompt) the preview steps aside.
- **Dragging:** a press on an icon that moves more than 6 px along the dock becomes a drag. Pinned among pinned reorders the pins; running among running reorders them; a running app dropped among pinned ones gets pinned there.
- **Clicks:** left-click runs `ActivateOrLaunch`:
  - not running → launch;
  - one window → bring it forward, or minimize it if it's already in front;
  - several windows → cycle through them.

  Middle-click opens a new window; right-click opens the menu. **`AfterPanelsClose`** first closes Start, Search, Quick Settings or the notification center if one is open (Escape, then act 200 ms later), because they stay in front and make Windows refuse to switch apps.
- **`Activate`:** restores the window if minimized, then `SetForegroundWindow`. If Windows refuses, a tapped Alt key satisfies the foreground-lock rules.

## Auto-hide and staying on screen (`MainWindow.AutoHide.cs`)

- **Modes:** `ReserveSpace` (an app bar; windows stop above the dock), `AutoHide` (slide away when a window overlaps), or neither (always on top of windows).
- **`UpdateAutoHide`:** the dock hides when all of these hold:
  - the mouse isn't at the screen edge (after the reveal delay) and isn't over the dock;
  - no menu or preview is open, no app wants attention, and Ctrl+Alt+D hasn't just revealed it;
  - an app window overlaps the pill.

  Hiding waits for the hide delay.
- **`WindowOverlapsDock`:** enumerating every window is the costliest check the dock makes, so its answer is cached until a window event marks it stale, or for at most a second.
- **`SetAutoHidden`:** animates `DockSlide` off the screen edge. The blur window follows frame by frame.
- **Fullscreen:** over a fullscreen app (not just maximized), the whole dock fades out, unless a permission prompt is open.
- **`KeepOnTop`:** re-asserts `HWND_TOPMOST`, because a monitor-sized foreground window can end up above "topmost" windows once the taskbar is hidden.
- **`AlignShellFlyout`:** moves Quick Settings above the dock's status icons (bottom dock only; Windows lays that panel out from its window's top), and puts the notification center back in its default place.
- **`AlignStartMenu`:** Start is drawn at the bottom of a full-width window that ends just above Windows' hidden 48 px taskbar, so a big dock would cover it. When that window uncloaks, it and the search-box window over it are each lifted by the same amount from their default spots, so Start ends a dock gap above the pill (bottom dock only). `ClearOfDock` does the same for Tasky's own menus and tray popup, which also close Start before opening (it would stay in front of them).
- **Shell UI:** `IsShellUi` recognises Start, Search, Quick Settings and the notification center by process name, so they reveal the dock instead of counting as overlapping windows.

## The system tray (`TrayHost.cs`, `MainWindow.Tray.cs`)

- **ManagedShell's `TrayService`** creates the hidden `Shell_TrayWnd` and keeps it first in z-order, so apps' tray messages reach Tasky. It forwards everything to Explorer, so Explorer's (hidden) tray stays in sync. On start it broadcasts `TaskbarCreated`, which makes running apps re-add their icons.
- **`NotificationArea`** holds the icons. `TrayHost` exposes two filtered views of them, `Pinned` (in your order) and `All`. Windows' own volume, network and power icons are left out, since the status capsule covers them.
- **Pins** are saved under the icon's GUID, or "exe path:icon ID". ManagedShell's own identifier includes the tooltip, which apps change constantly.
- **Clicks** are forwarded with `IconMouseDown` / `IconMouseUp`. On a pinned icon, a left press is held back until it's clear whether it's a click or a drag.
- **The popup** (`TrayPopup`) is closed by hand, on an outside click or app switch, because WPF's automatic dismissal doesn't fire reliably for a window that never takes focus.
- **App bars:** because Tasky's tray window is now the first `Shell_TrayWnd`, `SHAppBarMessage` calls would reach it instead of Explorer. Forwarding doesn't work for position messages: the shared memory they use belongs to the wrong process. So **`ExplorerAppBar`** (`Shell.cs`) builds the message itself and sends it straight to Explorer's taskbar, with shared memory created for Explorer's process.

## The Windows taskbar (`Shell.cs`, `MainWindow.xaml.cs`)

- **Hiding:** `ApplyTaskbar` sets Explorer's taskbar to auto-hide and hides its window. Before the first change it records the taskbar's original state in settings (`TaskbarHiddenByUs`, `TaskbarWasAutoHide`). It then saves "always show" as Explorer's stored setting (`SaveAlwaysShown`, the auto-hide bit in `StuckRects3`), which Explorer only reads when it starts. So after a restart, crash or power cut the taskbar is there until Tasky starts.
- **Restoring:** `Cleanup` puts the taskbar back and gives the tray back. It runs on Exit, window close, Windows session end and unhandled exceptions, and the next launch repairs things if Tasky was killed. At session end the taskbar is left on "always show" instead of the user's own setting, and `TaskbarHiddenByUs` stays set, so it shows at the next sign-in until Tasky starts and hides it again.
- **Lookup:** `WindowsTaskbar.Tray` finds Explorer's `Shell_TrayWnd` while skipping Tasky's own.

## Status icons and clock (`MainWindow.Status.cs`, `StatusMonitor.cs`)

- **Wi-Fi:** read through the WLAN API (`WlanQueryInterface`): connected network and signal quality. Other connection kinds come from `NetworkInterface`.
- **Volume:** read through Core Audio (`IMMDeviceEnumerator` → `IAudioEndpointVolume`): level and mute. The default device is looked up again every 5 s, in case headphones were plugged in.
- **Battery:** `GetSystemPowerStatus`.
- **Updates:** volume each second while the dock is visible; network and battery when Windows reports a change, or every 30 s. The glyphs come from Segoe Fluent Icons.
- **Clock:** a two-line clock. On side docks the date is shortened ("26 Oct").

## The permission prompt (`ConsentWatcher.cs`)

Windows' User Account Control prompt runs as `consent.exe` exactly while a prompt is open or waiting.

- **Detection:** once a second, `ConsentWatcher.Find` makes one `NtQuerySystemInformation` call into a reused buffer and looks for `consent.exe` in this session.
- **While found:** a "User Account Control" item with a shield and attention bar is placed first among running apps, the dock comes out (even over fullscreen apps), and clicking it activates one of consent's windows.
- **Why not just watch for its window:** Windows doesn't reliably tell a non-Explorer shell about the prompt's window, so the process is the dependable signal.

## Menus, settings, pinning (`MainWindow.Menus.cs`, `SettingsWindow.*`, `JumpList.cs`, `Shell.cs`)

- **App menus** list recent documents, then the app's windows, then New window, Pin/Unpin and Close.
- **Recent documents** come from `IApplicationDocumentLists` for the app's AUMID. Unpackaged apps use the implicit AUMID Windows derives from the exe path (`JumpList.ImplicitAppId`).
- **The dock menu** has Dock settings, the three modes, Show tray icons, Hide Windows taskbar and Exit.
- **The settings window** writes to the shared `Settings` object and calls the dock's `Apply*` methods. Slider changes are throttled to one re-layout per 30 ms, and saving to disk is debounced to 400 ms. Each setting is a `SettingRow` card (icon, title, description, control on the right). The sidebar shows one page at a time. Colours are window resources named `S.*`, set in `ApplyTheme` from the dock's dark/light theme. Mica comes from `DWMWA_SYSTEMBACKDROP_TYPE` with the frame extended over a transparent client area.
- **Pins** are stored as paths: an exe, a `.lnk`, or `shell:AppsFolder\<AUMID>` for packaged and web apps.
  - A shortcut normally stands for the exe it targets.
  - A shortcut *with arguments* (a game through its launcher) stays its own app.
- **Start with Windows** writes `HKCU\...\Run`. **The Start menu shortcut** is created through `WScript.Shell`.

## Themes (`Theme.cs`, `App.xaml`)

The dock's colours are application resources named `T.*` (`T.Fg`, `T.MenuBg`, ...). XAML uses them through `DynamicResource`, so `Theme.Apply("Light")` restyles everything in place. "Match Windows" reads `SystemUsesLightTheme` and follows changes via `WM_SETTINGCHANGE` "ImmersiveColorSet". The accent colour comes from the DWM registry key, lightened the way dark-mode Windows does it.

## Settings file

Settings live in `%APPDATA%\Tasky\settings.json`, which `Settings.cs` reads and writes as JSON. Any missing key takes its default.

| Key | Meaning | Default |
| --- | --- | --- |
| `Pins` | Pinned apps (exe, `.lnk` or `shell:AppsFolder\...` paths), in order | Explorer, Edge |
| `IconSize`, `IconSpacing` | Icon size and gap, DIPs | 36, 10 |
| `Magnify`, `MagnifyScale` | Magnify on hover, and by how much | true, 1.6 |
| `CornerRadius`, `BackgroundOpacity`, `Blur` | Look of the pill | 18, 0.7, true |
| `ClockFontSize`, `EdgeGap` | Clock size; gap to the screen edge | 14, 8 |
| `ReserveSpace`, `AutoHide` | Dock mode (see auto-hide) | true, true |
| `HideTaskbar`, `ShowTray` | Hide Explorer's taskbar; take over the tray | false, true |
| `Monitor`, `Position` | Display device name (null = main); `Bottom`/`Top`/`Left`/`Right` | null, Bottom |
| `Theme` | `System`, `Dark` or `Light` | System |
| `RevealDelay`, `HideDelay`, `AnimationSpeed` | Edge reveal delay, hide delay (ms); animation speed multiplier | 0, 0, 1.0 |
| `ShowHotkey` | Ctrl+Alt+D shows the dock | true |
| `TrayPins` | Pinned tray icons, in order | none |
| `TaskbarHiddenByUs`, `TaskbarWasAutoHide` | Crash recovery for the Windows taskbar | false |

Errors go to `%APPDATA%\Tasky\tasky.log`.

## File guide

| File | Contents |
| --- | --- |
| `App.xaml` | Global styles: menus, tooltips, the dock buttons' hover |
| `App.xaml.cs` | Start-up: single instance, crash handlers, accent colour, creating the dock |
| `MainWindow.xaml` | The dock's visual tree: pill, Start, app list template, overflow button, tray, status capsule, settings button, tray popup, name label |
| `MainWindow.xaml.cs` | The dock's fields, constructor, start-up, `WndProc`, the 1 s heartbeat, taskbar hiding and `Cleanup` |
| `MainWindow.Layout.cs` | Sizes, orientation, positioning, blur backdrop, fitting/overflow, theme and monitor changes |
| `MainWindow.AutoHide.cs` | Auto-hide, edge reveal, delays, overlap check, staying on top, shell panels, Ctrl+Alt+D |
| `MainWindow.Apps.cs` | `Refresh`, dock items, icons, pins and names, attention, fullscreen check |
| `MainWindow.Interaction.cs` | Magnification, name label, previews, dragging, clicks, launching, activating |
| `MainWindow.Status.cs` | Clock and live status icons |
| `MainWindow.Tray.cs` | Tray icons on the dock and in the popup: clicks, drags, pins, popup dismissal |
| `MainWindow.Menus.cs` | Right-click menus, recent files, settings window entry points, pin toggling, drag-and-drop pinning |
| `DockItem.cs` | One dock icon's state (bindable) |
| `DockEdge.cs` | Screen-edge layout rules |
| `WindowEnumerator.cs` | Which windows belong on the dock and which app each is (`AppWindow`, `ExeNames`, `Shortcuts`) |
| `WindowEvents.cs` | WinEvent hooks for window changes |
| `ConsentWatcher.cs` | Detects the UAC prompt's process |
| `IconLoader.cs` | App icons at every size, window icons, file icons for menus, the shield |
| `PreviewWindow.cs` | Live window previews |
| `BackdropWindow.cs` | The blur window |
| `TrayHost.cs` | ManagedShell tray setup, filtered views, pins |
| `StatusMonitor.cs` | Wi-Fi, volume and battery state and glyphs |
| `JumpList.cs` | Recent documents per app |
| `Theme.cs` | Dark and light colours |
| `Shell.cs` | `ExplorerAppBar`, `AppBar`, `WindowsTaskbar`, `StartMenuShortcut`, `AutoStart` |
| `Native.cs` | Win32, DWM and shell declarations and small helpers |
| `Settings.cs` | Settings model, JSON load and save, and `Log` |
| `SettingsWindow.xaml(.cs)` | The settings window: its control styles, pages and theme colours |
| `SettingRow.cs` | One settings card: icon, title, description and a control |
| `app.manifest` | Per-monitor DPI awareness and the Windows 10/11 compatibility ID |
| `Assets/` | The app icon: `Tasky.ico` (every size, a flat version for 16-24 px), `Tasky.png`, their source images, and `make-icon.ps1`, which rebuilds both from the sources |
| `Tasky.csproj` | Build settings: WPF, .NET 10, single-file ReadyToRun publish, memory-saving GC settings, ManagedShell |

---

# Part 3: Working on Tasky

## Setup

- **SDK:** install the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).
- **Editor:** any. Visual Studio 2022 gives you the XAML designer and a debugger (open `Tasky.csproj`); VS Code works with the C# Dev Kit extension.
- **Run:** exit the installed Tasky first (only one copy runs), then `dotnet run`. To start with the settings window open, run `dotnet run -- --settings`.

## Debugging

- **Breakpoints:** in Visual Studio, F5 starts Tasky under the debugger. Breakpoints in timer or event handlers pause the whole dock, which is fine.
- **Logging:** `Log.Error(ex, "context")` appends to `%APPDATA%\Tasky\tasky.log`. It's used for real errors; for a quick investigation, add temporary `Log.Error(null, $"...")` lines and remove them after.
- **If the taskbar disappears while debugging** (you stopped the debugger, so `Cleanup` didn't run): start and exit Tasky, or restart Windows Explorer from Task Manager.
- **Inspecting windows:** Spy++ (ships with Visual Studio) or Accessibility Insights show class names, styles and owners, which is what most of `WindowEnumerator` reasons about.

## Conventions

- **Comments** explain *why* something is done, especially anything that works around Windows behaviour; that's most of the non-obvious code.
- **Talking to Windows:** wrap calls that can fail in try/catch and log, and keep going. Free native handles and COM objects you get.
- **Icon glyphs:** write them as `\uE713` escapes, never as raw characters. Raw private-use characters are easy to corrupt with a wrong-encoding save.
- **Line endings:** files are UTF-8 with Windows line endings; `.gitattributes` enforces that.
- **Performance:** prefer events to polling. Anything that runs on a timer should be cheap, and should skip work while the dock is hidden.
- **Commits:** plain messages, no co-author lines.

## Common changes

**Adding a setting:**

1. Add a property with a default to `Settings.cs`. JSON picks it up automatically, and old files get the default.
2. Add a `SettingRow` to a page in `SettingsWindow.xaml` (copy a nearby slider or toggle row).
3. In `SettingsWindow.xaml.cs`: load it in `LoadValues`, hook it up in `Wire` (`Look(...)` for things that change the dock's look, `Toggle(...)` for switches), and add a label in `UpdateLabels` if it shows a value.
4. Use it in `MainWindow`. If it affects layout, `ApplyLayout()` picks it up via the `Look` path.

**Adding a button to the dock:**

1. Add a `Border` with `Style="{StaticResource DockButton}"` to `DockRow` in `MainWindow.xaml`, and a click handler.
2. Size it in `ApplyLayout` with `SetSize(...)` and an `edge.Margin(...)`.
3. Add it to the `HugEdge` list in `ApplyOrientation`, so it lines up on every edge.

**Changing which windows appear:** `WindowEnumerator.IsAppWindow` (filtering) and `Identify` (grouping). Test with Store apps, web apps, minimized windows and a second virtual desktop.

## Releasing

```bash
dotnet publish -c Release -o publish
```

This gives a single `publish\Tasky.exe`, which needs the .NET 10 Desktop Runtime. Bump `<Version>` in `Tasky.csproj`; it appears in Settings → About.

## Known limitations (and why)

- **Undocumented behaviour:** the blur, tray takeover, hiding Explorer's taskbar and moving Start and Quick Settings all rely on Windows behaviour Microsoft doesn't document. Insider builds can break them.
- **Flashing apps:** on current Windows 11 builds, Windows doesn't send "flashing" to non-Explorer shells, so apps flashing for attention can't bring the dock out. The UAC prompt is handled separately (see above).
- **Recent documents only:** apps' custom jump-list tasks are only readable by Explorer.
- **App bars:** while Tasky's tray is on, other apps that reserve screen space can't, because their messages reach Tasky's tray window first.
- **Elevated apps:** tray icons of apps running as administrator only respond if Tasky also runs as administrator.
- **One dock:** a single dock, on one display.
