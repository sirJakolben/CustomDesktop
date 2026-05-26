using System.Runtime.InteropServices;
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

        PInvoke.GetCursorPos(out var pt);
        int px = (int)pt.X;
        int py = (int)pt.Y;

        var lParam = new LPARAM(unchecked((nint)((py << 16) | (px & 0xFFFF))));
        PInvoke.PostMessage(progman, WM_CONTEXTMENU, default, lParam);
    }

    // ── Window subclassing ────────────────────────────────────────────────────
    // EXCEPTION to CsWin32 rule: SetWindowSubclass and friends are in comctl32.dll
    // which is not emitted by the Roslyn source generator for this project
    // configuration (no comctl32.dll.g.cs in output). Direct DllImport is the
    // only viable alternative. Documented here per CLAUDE.md / Phase 5.

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint SUBCLASSPROC(nint hwnd, uint uMsg,
                                        nuint wParam, nint lParam,
                                        nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(
        nint hwnd, SUBCLASSPROC pfnSubclass,
        nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(
        nint hwnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = false)]
    internal static extern nint DefSubclassProc(
        nint hwnd, uint uMsg, nuint wParam, nint lParam);

    // ── File Properties dialog ────────────────────────────────────────────────
    // EXCEPTION to CsWin32 rule: ShellExecuteExW is silently skipped by the
    // Roslyn source generator (HINSTANCE return type conflict). Direct DllImport
    // is the only viable alternative. See CLAUDE.md / Phase 5.

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_NOASYNC      = 0x00000100;
    private const int  SW_SHOW               = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int    cbSize;
        public uint   fMask;
        public nint   hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int    nShow;
        public nint   hInstApp;
        public nint   lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public nint   hkeyClass;
        public uint   dwHotKey;
        public nint   hIconOrMonitor;
        public nint   hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    /// Shows the native Windows Properties dialog for the given file/folder path.
    internal static void ShowFileProperties(string path, nint ownerHwnd)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize    = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask     = SEE_MASK_INVOKEIDLIST | SEE_MASK_NOASYNC,
            hwnd      = ownerHwnd,
            lpVerb    = "properties",
            lpFile    = path,
            nShow     = SW_SHOW,
        };
        ShellExecuteExW(ref info);
    }
}
