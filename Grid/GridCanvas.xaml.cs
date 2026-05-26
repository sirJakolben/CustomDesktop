using CustomDesktop.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CustomDesktop.Grid;

internal sealed partial class GridCanvas : UserControl
{
    private readonly GridConfiguration _config  = GridConfiguration.Load();
    private readonly GridLayoutManager _manager = new();

    public GridCanvas()
    {
        InitializeComponent();
        Loaded      += (_, _) => Recalculate();
        SizeChanged += (_, _) => Recalculate();
    }

    // ── Layout ─────────────────────────────────────────────────────────────────

    private void Recalculate()
    {
        if (ActualWidth is 0 || ActualHeight is 0) return;

        _config.Compute(ActualWidth, ActualHeight);

        // Pre-size the hover indicator to exactly one element block.
        double block = _config.DefaultIconCells * _config.CellSize;
        HoverIndicator.Width  = block;
        HoverIndicator.Height = block;
    }

    // ── Pointer events ─────────────────────────────────────────────────────────

    private void LayerCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_config.CellSize is 0) return;  // not yet computed

        var pos = e.GetCurrentPoint(LayerCanvas).Position;

        // Hide indicator in top/bottom margin areas — elements cannot go there.
        if (pos.Y < _config.EdgeMarginPx || pos.Y > ActualHeight - _config.EdgeMarginPx)
        {
            HoverIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var topLeft = _config.PixelToSnappedTopLeft(pos);
        bool free   = _manager.CanPlace(topLeft, _config.DefaultIconCells, _config.DefaultIconCells);

        if (free)
        {
            var pixel = _config.GridToPixel(topLeft);
            Canvas.SetLeft(HoverIndicator, pixel.X);
            Canvas.SetTop(HoverIndicator,  pixel.Y);
            HoverIndicator.Visibility = Visibility.Visible;
        }
        else
        {
            // Occupied slot — no highlight shown (per spec).
            HoverIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void LayerCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HoverIndicator.Visibility = Visibility.Collapsed;
    }

    // ── Right-click: forward to native Windows desktop context menu ────────────

    private void LayerCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // All standard Windows desktop options (Display settings, Personalise,
        // New folder, Refresh, …) are preserved by forwarding WM_CONTEXTMENU
        // directly to Progman — no custom items are added at this layer.
        NativeMethods.ForwardDesktopContextMenu();
        e.Handled = true;
    }
}
