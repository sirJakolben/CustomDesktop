// Spike 01 — Shell Window Hierarchy
// Goal:   Verify EnumWindows + GetClassName can identify Progman and WorkerW.
// Pass:   Debug output shows at least one "Progman" entry with the correct hwnd.
// Status: PENDING (run in DEBUG and check Output window in VS)

using System.Diagnostics;
using Windows.Win32;

namespace CustomDesktop.Spikes;

internal static class Spike_01_WindowHierarchy
{
    internal static void Run()
    {
        Debug.WriteLine("[Spike01] Enumerating top-level windows...");

        // The CsWin32 Span<char> overloads are marked unsafe internally (they use fixed),
        // so the actual P/Invoke calls must live in a separate unsafe method, not a lambda.
        PInvoke.EnumWindows((hwnd, _) => { LogIfShellWindow(hwnd); return true; }, default);

        Debug.WriteLine("[Spike01] Done.");
    }

    private static unsafe void LogIfShellWindow(Windows.Win32.Foundation.HWND hwnd)
    {
        Span<char> textBuf  = stackalloc char[256];
        Span<char> classBuf = stackalloc char[256];

        int textLen  = PInvoke.GetWindowText(hwnd, textBuf);
        int classLen = PInvoke.GetClassName(hwnd, classBuf);

        string title     = new string(textBuf[..textLen]);
        string className = new string(classBuf[..classLen]);

        if (className is "Progman" or "WorkerW")
        {
            nint handle = (nint)hwnd.Value; // HWND.Value is void* in CsWin32
            Debug.WriteLine($"[Spike01] FOUND {className} '{title}' hwnd=0x{handle:X}");
        }
    }
}
