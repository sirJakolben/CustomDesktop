using CustomDesktop.Infrastructure;
using CustomDesktop.Spikes;
using Microsoft.UI.Xaml;

namespace CustomDesktop;

public sealed partial class MainWindow : Window
{
    private SystemEventBridge? _bridge;
    private nint _hwnd;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd   = ShellWindow.Configure(this);
        _bridge = new SystemEventBridge(_hwnd);

        // Re-cover the work area on any display/work-area change.
        _bridge.WorkAreaChanged += () => ShellWindow.ResetToWorkArea(this, _hwnd);
        _bridge.DisplayChanged  += () => ShellWindow.ResetToWorkArea(this, _hwnd);

        // Pass HWND to GridCanvas so it can show native file Properties dialogs.
        DesktopGrid.OwnerHwnd = _hwnd;

        Closed += (_, _) => _bridge.Dispose();

#if DEBUG
        Spike_01_WindowHierarchy.Run();
#endif
    }
}
