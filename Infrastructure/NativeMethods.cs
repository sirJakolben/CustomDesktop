using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace CustomDesktop.Infrastructure;

internal static class NativeMethods
{
    // Removes the WS_EX_APPWINDOW flag so the window never gets a taskbar button,
    // and adds WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE so it is invisible to Alt+Tab
    // and never steals keyboard focus on click.
    private const int  WS_EX_TOOLWINDOW  = 0x00000080;
    private const int  WS_EX_NOACTIVATE  = unchecked((int)0x08000000);
    private const int  WS_EX_APPWINDOW   = unchecked((int)0x00040000);
    private const uint WM_CONTEXTMENU    = 0x007B;

    internal static void ApplyShellExStyles(nint hwnd)
    {
        var h = new HWND(hwnd);
        int exStyle = PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        exStyle &= ~WS_EX_APPWINDOW;
        PInvoke.SetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle);
    }

    // Places the window at the very bottom of the Z-order (above wallpaper,
    // below every other window) without moving or resizing it.
    internal static void SendToBottom(nint hwnd)
    {
        PInvoke.SetWindowPos(
            new HWND(hwnd),
            new HWND(new nint(1)), // HWND_BOTTOM
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
        );
    }

    // Forwards a right-click to Progman so Windows shows its native desktop
    // context menu (Display settings, Personalise, New folder, etc.).
    internal static unsafe void ForwardDesktopContextMenu()
    {
        HWND progman;
        fixed (char* cls = "Progman")
            progman = PInvoke.FindWindow(new PWSTR(cls), default);

        if (progman == default) return;

        // CsWin32 friendly overload returns Windows.Foundation.Point (double X/Y)
        PInvoke.GetCursorPos(out var pt);
        int px = (int)pt.X;
        int py = (int)pt.Y;

        // MAKELPARAM(x, y): pack screen coords into LPARAM
        var lParam = new LPARAM(unchecked((nint)((py << 16) | (px & 0xFFFF))));
        PInvoke.PostMessage(progman, WM_CONTEXTMENU, default, lParam);
    }
}
