using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace CustomDesktop.Infrastructure;

internal static class ShellWindow
{
    internal static void Configure(Window window)
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
        // The constructor call would be a no-op because the window is not yet in the
        // Z-order. WS_EX_NOACTIVATE prevents user-driven re-activation afterwards,
        // so this single hook is sufficient for the initial placement.
        window.Activated += (_, _) => NativeMethods.SendToBottom(hwnd);
    }
}
