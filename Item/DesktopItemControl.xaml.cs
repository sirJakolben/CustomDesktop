using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace CustomDesktop.Item;

internal sealed partial class DesktopItemControl : UserControl
{
    private DesktopItemElement? _item;

    public DesktopItemControl() => InitializeComponent();

    // ── Binding ─────────────────────────────────────────────────────────────────

    /// Bind to an element. Call before adding the control to the canvas.
    internal void Bind(DesktopItemElement item)
    {
        _item          = item;
        NameLabel.Text = item.DisplayName;
        if (item.Icon is not null)
            IconImage.Source = item.Icon;
    }

    /// Called by GridCanvas once the async icon load completes.
    internal void UpdateIcon()
    {
        if (_item?.Icon is not null)
            IconImage.Source = _item.Icon;
    }

    // ── Pointer events ──────────────────────────────────────────────────────────

    private void RootGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        Launch();
        e.Handled = true;  // prevent event reaching LayerCanvas
    }

    // Marks the event as handled so it does NOT bubble to LayerCanvas_RightTapped
    // (which would forward to Progman). The ContextFlyout handles the display.
    private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
        => e.Handled = true;

    // ── Context menu actions ────────────────────────────────────────────────────

    private void Open_Click(object sender, RoutedEventArgs e) => Launch();

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        // Passes /select so Explorer opens the parent folder with the file highlighted.
        Process.Start(new ProcessStartInfo
        {
            FileName        = "explorer.exe",
            Arguments       = $"/select,\"{_item.Path}\"",
            UseShellExecute = true,
        });
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        var package = new DataPackage();
        package.SetText(_item.Path);
        Clipboard.SetContent(package);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private void Launch()
    {
        if (_item is null) return;
        try
        {
            // UseShellExecute = true tells .NET to use the shell verb "open",
            // which handles .lnk shortcuts, file associations, UAC elevation, etc.
            Process.Start(new ProcessStartInfo(_item.Path)
            {
                UseShellExecute = true,
            });
        }
        catch { /* silently ignore — user will see nothing happen; Phase 5 adds error UI */ }
    }
}
