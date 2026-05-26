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

        // Drop to the bottom of Z-order immediately and again after WinUI 3
        // raises the window on its initial activation call.
        NativeMethods.SendToBottom(hwnd);
        window.Activated += (_, _) => NativeMethods.SendToBottom(hwnd);
    }
}
