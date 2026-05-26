using System.Diagnostics;
using CustomDesktop.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace CustomDesktop.Item;

internal sealed partial class DesktopItemControl : UserControl
{
    private DesktopItemElement? _item;

    // Set by GridCanvas after the control is added to the visual tree,
    // so ShowFileProperties can pass the owner HWND to the shell.
    internal nint OwnerHwnd { get; set; }

    // Raised when the user holds the pointer down long enough to start a drag.
    internal event Action<DesktopItemControl>? DragStartRequested;

    // Drag-detection state
    private bool   _pointerDown;
    private global::Windows.Foundation.Point _pointerDownPos;
    private const double DragThresholdPx = 8.0;

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

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null) return;
        NativeMethods.ShowFileProperties(_item.Path, OwnerHwnd);
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

    // ── Drag detection ───────────────────────────────────────────────────────────

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown    = true;
        _pointerDownPos = e.GetCurrentPoint(RootGrid).Position;
        RootGrid.CapturePointer(e.Pointer);
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        var pos = e.GetCurrentPoint(RootGrid).Position;
        double dx = pos.X - _pointerDownPos.X;
        double dy = pos.Y - _pointerDownPos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= DragThresholdPx)
        {
            _pointerDown = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
            DragStartRequested?.Invoke(this);
        }
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown = false;
        RootGrid.ReleasePointerCapture(e.Pointer);
    }

    private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown = false;
    }
}
