namespace CustomDesktop.Infrastructure;

/// <summary>
/// Hooks into the Win32 message pump of the main window to intercept shell
/// broadcast messages.
///
/// Uses SetWindowSubclass (comctl32) — see NativeMethods.cs for the DllImport
/// exemption note.
///
/// All C# events fire on the UI (message-pump) thread — callers need not
/// marshal to the dispatcher.
/// </summary>
internal sealed class SystemEventBridge : IDisposable
{
    // Windows broadcast messages we care about
    private const uint WM_SETTINGCHANGE = 0x001A;   // taskbar, work-area, DPI, themes
    private const uint WM_DISPLAYCHANGE = 0x007E;   // resolution / monitor layout

    // Unique subclass ID — arbitrary non-zero value
    private const nuint SubclassId = 0x4355_0001;

    /// Fired when the work area changes (taskbar resize, DPI, theme switch).
    internal event Action? WorkAreaChanged;

    /// Fired when the display configuration changes (resolution, monitor add/remove).
    internal event Action? DisplayChanged;

    private readonly nint _hwnd;

    // Must be kept alive for the lifetime of the subclass.
    // If GC'd the native function pointer becomes dangling → crash.
    private NativeMethods.SUBCLASSPROC? _proc;

    internal SystemEventBridge(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = new NativeMethods.SUBCLASSPROC(WndProc);
        NativeMethods.SetWindowSubclass(hwnd, _proc, SubclassId, 0);
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
        }
        return NativeMethods.DefSubclassProc(hwnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_proc is null) return;
        NativeMethods.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        _proc = null;
    }
}
