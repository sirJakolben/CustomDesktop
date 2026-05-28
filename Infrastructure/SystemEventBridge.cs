namespace CustomDesktop.Infrastructure;

/// <summary>
/// Hooks into the Win32 message pump of the main window to intercept shell
/// broadcast messages and global hotkeys.
///
/// Uses SetWindowSubclass (comctl32) — see NativeMethods.cs for the DllImport
/// exemption note.
///
/// All C# events fire on the UI (message-pump) thread — callers need not
/// marshal to the dispatcher.
///
/// Hotkey IDs registered via NativeMethods.RegisterHotKey:
///   HotkeyId.ZoomIn  (Ctrl+Plus)  → DensityDecreased (fewer rows = larger cells)
///   HotkeyId.ZoomOut (Ctrl+Minus) → DensityIncreased (more rows = smaller cells)
///   HotkeyId.ZoomInFast  (Ctrl+Shift+Plus)  → DensityDecreased ×5
///   HotkeyId.ZoomOutFast (Ctrl+Shift+Minus) → DensityIncreased ×5
/// </summary>
internal sealed class SystemEventBridge : IDisposable
{
    // Windows broadcast messages we care about
    private const uint WM_SETTINGCHANGE = 0x001A;   // taskbar, work-area, DPI, themes
    private const uint WM_DISPLAYCHANGE = 0x007E;   // resolution / monitor layout

    // Unique subclass ID — arbitrary non-zero value
    private const nuint SubclassId = 0x4355_0001;

    // Virtual key codes (OEM keys)
    private const uint VK_OEM_PLUS  = 0xBB; // '+' / '=' key
    private const uint VK_OEM_MINUS = 0xBD; // '-' / '_' key

    // Hotkey IDs — arbitrary unique integers per HWND
    internal static class HotkeyId
    {
        internal const int ZoomIn      = 1;   // Ctrl+Plus  (−1 vertical slot)
        internal const int ZoomOut     = 2;   // Ctrl+Minus (+1 vertical slot)
        internal const int ZoomInFast  = 3;   // Ctrl+Shift+Plus  (−5)
        internal const int ZoomOutFast = 4;   // Ctrl+Shift+Minus (+5)
    }

    /// Fired when the work area changes (taskbar resize, DPI, theme switch).
    internal event Action? WorkAreaChanged;

    /// Fired when the display configuration changes (resolution, monitor add/remove).
    internal event Action? DisplayChanged;

    /// Fired when vertical slot count should decrease by delta (fewer slots = larger cells).
    /// Ctrl+Plus decrements by 1 (zooming in), Ctrl+Shift+Plus by 5.
    internal event Action<int /*delta*/>? VerticalSlotsDelta;

    private readonly nint _hwnd;

    // Must be kept alive for the lifetime of the subclass.
    // If GC'd the native function pointer becomes dangling → crash.
    private NativeMethods.SUBCLASSPROC? _proc;

    internal SystemEventBridge(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = new NativeMethods.SUBCLASSPROC(WndProc);
        NativeMethods.SetWindowSubclass(hwnd, _proc, SubclassId, 0);

        uint ctrl      = NativeMethods.MOD_CONTROL  | NativeMethods.MOD_NOREPEAT;
        uint ctrlShift = NativeMethods.MOD_CONTROL  | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT;

        // Ctrl+Plus → zoom in (fewer rows)
        NativeMethods.RegisterHotKey(hwnd, HotkeyId.ZoomIn,      ctrl,      VK_OEM_PLUS);
        // Ctrl+Minus → zoom out (more rows)
        NativeMethods.RegisterHotKey(hwnd, HotkeyId.ZoomOut,     ctrl,      VK_OEM_MINUS);
        // Ctrl+Shift+Plus → fast zoom in
        NativeMethods.RegisterHotKey(hwnd, HotkeyId.ZoomInFast,  ctrlShift, VK_OEM_PLUS);
        // Ctrl+Shift+Minus → fast zoom out
        NativeMethods.RegisterHotKey(hwnd, HotkeyId.ZoomOutFast, ctrlShift, VK_OEM_MINUS);
    }

    private nint WndProc(nint hwnd, uint uMsg, nuint wParam, nint lParam,
                         nuint uIdSubclass, nuint dwRefData)
    {
        switch (uMsg)
        {
            case WM_SETTINGCHANGE:
                WorkAreaChanged?.Invoke();
                break;

            case WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke();
                break;

            case NativeMethods.WM_HOTKEY:
                HandleHotkey((int)(uint)wParam);
                break;
        }
        return NativeMethods.DefSubclassProc(hwnd, uMsg, wParam, lParam);
    }

    private void HandleHotkey(int id)
    {
        switch (id)
        {
            // Ctrl+Plus  → fewer vertical slots (cells get bigger → "zoom in")
            case HotkeyId.ZoomIn:      VerticalSlotsDelta?.Invoke(-1); break;
            // Ctrl+Minus → more vertical slots (cells get smaller → "zoom out")
            case HotkeyId.ZoomOut:     VerticalSlotsDelta?.Invoke(+1); break;
            case HotkeyId.ZoomInFast:  VerticalSlotsDelta?.Invoke(-5); break;
            case HotkeyId.ZoomOutFast: VerticalSlotsDelta?.Invoke(+5); break;
        }
    }

    public void Dispose()
    {
        if (_proc is null) return;

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId.ZoomIn);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId.ZoomOut);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId.ZoomInFast);
        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId.ZoomOutFast);

        NativeMethods.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        _proc = null;
    }
}
