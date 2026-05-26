using CustomDesktop.Infrastructure;
using CustomDesktop.Spikes;
using Microsoft.UI.Xaml;

namespace CustomDesktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShellWindow.Configure(this);

#if DEBUG
        Spike_01_WindowHierarchy.Run();
#endif
    }
}
