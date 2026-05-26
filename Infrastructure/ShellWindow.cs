using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace CustomDesktop.Infrastructure;

internal static class ShellWindow
{
    /// Applies all shell window settings and returns the HWND for further use.
    internal static nint Configure(Window window)
    {
        nint hwnd = WindowNative.GetWindowHandle(window);

        // Extend content into the title bar area so no system chrome is visible.
        window.ExtendsContentIntoTitleBar = true;

        // Apply shell-specific Win32 extended styles.
        NativeMethods.ApplyShellExStyles(hwnd);

        // Cover the primary display work area (excludes taskbar).
        var workArea = DisplayArea.Primary.WorkArea;
        window.AppWindow.MoveAndResize(workArea);

        // SendToBottom is called when the window is first activated (via App.Activate()).
        window.Activated += (_, _) => NativeMethods.SendToBottom(hwnd);

        return hwnd;
    }

    /// Re-reads the primary work area and repositions/resizes the window to fill it.
    /// Called from SystemEventBridge on WM_SETTINGCHANGE / WM_DISPLAYCHANGE.
    internal static void ResetToWorkArea(Window window, nint hwnd)
    {
        var workArea = DisplayArea.Primary.WorkArea;
        window.AppWindow.MoveAndResize(workArea);
        NativeMethods.SendToBottom(hwnd);
    }
}
