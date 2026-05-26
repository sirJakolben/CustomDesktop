using CustomDesktop.Infrastructure;
using CustomDesktop.Item;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace CustomDesktop.Grid;

internal sealed partial class GridCanvas : UserControl
{
    private readonly GridConfiguration   _config  = GridConfiguration.Load();
    private readonly GridLayoutManager   _manager = new();
    private readonly DesktopItemRepository _repo  = new();

    // Parallel list of (element, control) so Recalculate() can reposition items on resize.
    private readonly List<(DesktopItemElement Element, DesktopItemControl Control)> _items = [];

    public GridCanvas()
    {
        InitializeComponent();
        Loaded      += OnLoaded;
        SizeChanged += (_, _) => Recalculate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Recalculate();
        _ = LoadItemsAsync();  // fire-and-forget; icons fill in asynchronously
    }

    // ── Layout ─────────────────────────────────────────────────────────────────

    private void Recalculate()
    {
        if (ActualWidth is 0 || ActualHeight is 0) return;

        _config.Compute(ActualWidth, ActualHeight);

        double block = _config.DefaultIconCells * _config.CellSize;
        HoverIndicator.Width  = block;
        HoverIndicator.Height = block;

        // Reposition all item controls to match the updated cell size.
        foreach (var (element, control) in _items)
        {
            control.Width  = block;
            control.Height = block;
            var px = _config.GridToPixel(element.TopLeft);
            Canvas.SetLeft(control, px.X);
            Canvas.SetTop(control,  px.Y);
        }
    }

    // ── Item loading ───────────────────────────────────────────────────────────

    private async Task LoadItemsAsync()
    {
        var paths = _repo.GetPaths();

        // ── Phase A: place all item controls on the canvas (UI thread, no awaits) ──
        var placements = new List<(DesktopItemElement Element, DesktopItemControl Control)>(paths.Count);

        foreach (var path in paths)
        {
            var slot = FindNextFreeSlot();
            if (slot is null) break;   // grid is full

            var element = new DesktopItemElement(path, slot.Value, _config.DefaultIconCells);
            _manager.Place(element);

            double block = _config.DefaultIconCells * _config.CellSize;
            var control  = new DesktopItemControl { Width = block, Height = block };
            control.Bind(element);

            var px = _config.GridToPixel(slot.Value);
            Canvas.SetLeft(control, px.X);
            Canvas.SetTop(control,  px.Y);

            LayerCanvas.Children.Add(control);
            _items.Add((element, control));
            placements.Add((element, control));
        }

        // ── Phase B: load icons in parallel, dispatch updates back to UI thread ──
        var dq = DispatcherQueue;
        await Task.WhenAll(placements.Select(async p =>
        {
            var icon = await IconLoader.LoadAsync(p.Element.Path);
            if (icon is null) return;
            p.Element.Icon = icon;
            dq.TryEnqueue(p.Control.UpdateIcon);
        }));
    }

    /// Column-first traversal (top-to-bottom within each column, then next column)
    /// to match the placement order users expect from the standard Windows desktop.
    private GridCoordinate? FindNextFreeSlot()
    {
        if (_config.CellSize is 0) return null;

        int maxCol = _config.HorizontalSlots - _config.DefaultIconCells;
        int maxRow = _config.VerticalSlots   - _config.DefaultIconCells;

        for (int c = 0; c <= maxCol; c++)
            for (int r = 0; r <= maxRow; r++)
            {
                var coord = new GridCoordinate(c, r);
                if (_manager.CanPlace(coord, _config.DefaultIconCells, _config.DefaultIconCells))
                    return coord;
            }
        return null;
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
            // Occupied slot — no highlight per spec.
            HoverIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void LayerCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
        => HoverIndicator.Visibility = Visibility.Collapsed;

    // ── Right-click: forward to native Windows desktop context menu ────────────

    private void LayerCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Only reached when the right-click is NOT on a DesktopItemControl
        // (item controls mark e.Handled = true to prevent bubbling).
        NativeMethods.ForwardDesktopContextMenu();
        e.Handled = true;
    }
}
