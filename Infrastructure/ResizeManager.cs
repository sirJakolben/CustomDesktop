using CustomDesktop.Grid;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace CustomDesktop.Infrastructure;

/// <summary>
/// Manages per-element resize handles on the desktop grid.
///
/// Interaction:
///   - While Ctrl is held and the pointer moves over an element, four blue
///     corner handles (Ellipse) appear on the DragOverlayCanvas.
///   - Dragging a corner handle resizes the element in grid-cell increments.
///   - Releasing the pointer commits the resize and saves the layout.
///   - Moving the pointer away or releasing Ctrl hides the handles.
///
/// Wiring: GridCanvas calls Attach() for each item/folder.
/// ResizeManager subscribes to PointerMoved on the LayerCanvas; GridCanvas passes
/// the OverlayCanvas so handles are painted above all other content.
/// </summary>
internal sealed class ResizeManager
{
    // ── Wiring (set by GridCanvas after construction) ──────────────────────────
    internal GridLayoutManager LayoutManager { get; set; } = null!;
    internal GridConfiguration Config        { get; set; } = null!;
    internal Canvas            LayerCanvas   { get; set; } = null!;
    internal Canvas            OverlayCanvas { get; set; } = null!;

    /// Raised after a successful resize so GridCanvas can update control size
    /// and call SaveLayout().
    internal event Action<IDesktopElement>? ElementResized;

    // ── Registered elements ────────────────────────────────────────────────────
    // Maps each element to its corresponding UI control (for bounds calculation)
    private readonly List<(IDesktopElement Element, UIElement Control)> _elements = [];

    // ── Handle visuals ─────────────────────────────────────────────────────────
    private const double HandleSize    = 14.0;
    private const double HandleRadius  =  7.0;

    private readonly Ellipse[] _handles = new Ellipse[4]; // TL, TR, BL, BR
    private IDesktopElement?   _activeElement;
    private bool               _handlesVisible;

    // ── Resize drag state ──────────────────────────────────────────────────────
    private enum Corner { None, TopLeft, TopRight, BottomLeft, BottomRight }
    private Corner    _draggingCorner = Corner.None;
    private bool      _isDragging;

    // Grid state at drag start
    private GridCoordinate _startTopLeft;
    private int            _startWidth;
    private int            _startHeight;

    // ── Public API ─────────────────────────────────────────────────────────────

    internal void Attach(IDesktopElement element, UIElement control)
        => _elements.Add((element, control));

    internal void Detach(IDesktopElement element)
    {
        _elements.RemoveAll(e => e.Element == element);
        if (_activeElement == element)
            HideHandles();
    }

    /// Call once after construction; subscribes to LayerCanvas events.
    internal void Initialize()
    {
        // Create the 4 handles but don't add to canvas yet
        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Ellipse
            {
                Width            = HandleSize,
                Height           = HandleSize,
                Fill             = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x8F, 0xFF)),
                Stroke           = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                StrokeThickness  = 2,
                IsHitTestVisible = true,
                Visibility       = Visibility.Collapsed,
            };
            Canvas.SetZIndex(_handles[i], 300);
        }

        LayerCanvas.PointerMoved += OnLayerPointerMoved;
        LayerCanvas.PointerExited += OnLayerPointerExited;
    }

    // ── Hover detection ────────────────────────────────────────────────────────

    private void OnLayerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Only show handles when Ctrl is held
        bool ctrlHeld = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);

        if (!ctrlHeld)
        {
            if (_handlesVisible && !_isDragging) HideHandles();
            return;
        }

        if (_isDragging) return;   // don't re-evaluate while dragging

        var pos = e.GetCurrentPoint(LayerCanvas).Position;
        var hit = HitTestElement(pos);

        if (hit is null)
        {
            if (_handlesVisible) HideHandles();
            return;
        }

        if (hit != _activeElement)
        {
            _activeElement = hit;
            ShowHandles(hit);
        }
        else
        {
            // Refresh positions (element may have moved)
            PositionHandles(hit);
        }
    }

    private void OnLayerPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) HideHandles();
    }

    // ── Hit test ───────────────────────────────────────────────────────────────

    private IDesktopElement? HitTestElement(Point pos)
    {
        foreach (var (element, _) in _elements)
        {
            var px = Config.GridToPixel(element.TopLeft);
            double w = element.WidthCells  * Config.CellSize;
            double h = element.HeightCells * Config.CellSize;

            if (pos.X >= px.X && pos.X <= px.X + w &&
                pos.Y >= px.Y && pos.Y <= px.Y + h)
                return element;
        }
        return null;
    }

    // ── Handle show/hide ───────────────────────────────────────────────────────

    private void ShowHandles(IDesktopElement element)
    {
        // Add to overlay canvas if not already there
        foreach (var h in _handles)
        {
            if (!OverlayCanvas.Children.Contains(h))
                OverlayCanvas.Children.Add(h);
            h.Visibility = Visibility.Visible;
        }

        PositionHandles(element);
        _handlesVisible = true;

        // Wire handle drag events
        for (int i = 0; i < 4; i++)
        {
            int idx = i;  // capture for lambda
            _handles[i].PointerPressed  += (s, e) => OnHandlePressed(s, e, (Corner)(idx + 1));
            _handles[i].PointerMoved    += OnHandleMoved;
            _handles[i].PointerReleased += OnHandleReleased;
            _handles[i].PointerCanceled += OnHandleCanceled;
        }
    }

    private void HideHandles()
    {
        foreach (var h in _handles)
        {
            h.Visibility = Visibility.Collapsed;
            h.PointerPressed  -= null!;
            h.PointerMoved    -= OnHandleMoved;
            h.PointerReleased -= OnHandleReleased;
            h.PointerCanceled -= OnHandleCanceled;
        }
        _handlesVisible = false;
        _activeElement  = null;
    }

    private void PositionHandles(IDesktopElement element)
    {
        var px = Config.GridToPixel(element.TopLeft);
        double w  = element.WidthCells  * Config.CellSize;
        double h  = element.HeightCells * Config.CellSize;
        double hr = HandleRadius;

        // TL=0, TR=1, BL=2, BR=3
        double[] xs = [px.X - hr,     px.X + w - hr, px.X - hr,     px.X + w - hr];
        double[] ys = [px.Y - hr,     px.Y - hr,     px.Y + h - hr, px.Y + h - hr];

        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_handles[i], xs[i]);
            Canvas.SetTop(_handles[i],  ys[i]);
        }
    }

    // ── Handle drag ────────────────────────────────────────────────────────────

    private void OnHandlePressed(object sender, PointerRoutedEventArgs e, Corner corner)
    {
        if (_activeElement is null) return;

        _isDragging     = true;
        _draggingCorner = corner;
        _startTopLeft   = _activeElement.TopLeft;
        _startWidth     = _activeElement.WidthCells;
        _startHeight    = _activeElement.HeightCells;

        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _activeElement is null) return;

        var pos     = e.GetCurrentPoint(LayerCanvas).Position;
        var gridPos = Config.PixelToSnappedTopLeft(pos);

        // Calculate new TopLeft + size based on which corner is dragged
        GridCoordinate newTopLeft = _startTopLeft;
        int newW = _startWidth;
        int newH = _startHeight;

        int minCells = 1;

        switch (_draggingCorner)
        {
            case Corner.BottomRight:
                // Bottom-right: only size changes, TopLeft stays fixed
                newW = Math.Max(minCells, gridPos.Col - _startTopLeft.Col + 1);
                newH = Math.Max(minCells, gridPos.Row - _startTopLeft.Row + 1);
                break;

            case Corner.BottomLeft:
                // Left edge moves, width shrinks/grows, TopLeft.Col changes
                {
                    int newCol  = Math.Min(gridPos.Col, _startTopLeft.Col + _startWidth - minCells);
                    newW        = _startTopLeft.Col + _startWidth - newCol;
                    newH        = Math.Max(minCells, gridPos.Row - _startTopLeft.Row + 1);
                    newTopLeft  = new GridCoordinate(newCol, _startTopLeft.Row);
                }
                break;

            case Corner.TopRight:
                // Top edge moves, height shrinks/grows, TopLeft.Row changes
                {
                    int newRow  = Math.Min(gridPos.Row, _startTopLeft.Row + _startHeight - minCells);
                    newH        = _startTopLeft.Row + _startHeight - newRow;
                    newW        = Math.Max(minCells, gridPos.Col - _startTopLeft.Col + 1);
                    newTopLeft  = new GridCoordinate(_startTopLeft.Col, newRow);
                }
                break;

            case Corner.TopLeft:
                // Both axes move
                {
                    int newCol  = Math.Min(gridPos.Col, _startTopLeft.Col + _startWidth  - minCells);
                    int newRow  = Math.Min(gridPos.Row, _startTopLeft.Row + _startHeight - minCells);
                    newW        = _startTopLeft.Col + _startWidth  - newCol;
                    newH        = _startTopLeft.Row + _startHeight - newRow;
                    newTopLeft  = new GridCoordinate(newCol, newRow);
                }
                break;
        }

        // Clamp to grid bounds
        newW = Math.Min(newW, Config.HorizontalSlots - newTopLeft.Col);
        newH = Math.Min(newH, Config.VerticalSlots   - newTopLeft.Row);

        // Preview: just reposition handles without committing to the layout
        // (we only commit on release to avoid costly layout thrashing)
        var previewBounds = (newTopLeft, newW, newH);
        PositionHandlesPreview(previewBounds);

        e.Handled = true;
    }

    private void PositionHandlesPreview((GridCoordinate tl, int w, int h) bounds)
    {
        var px = Config.GridToPixel(bounds.tl);
        double pw  = bounds.w * Config.CellSize;
        double ph  = bounds.h * Config.CellSize;
        double hr  = HandleRadius;

        double[] xs = [px.X - hr, px.X + pw - hr, px.X - hr, px.X + pw - hr];
        double[] ys = [px.Y - hr, px.Y - hr, px.Y + ph - hr, px.Y + ph - hr];

        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_handles[i], xs[i]);
            Canvas.SetTop(_handles[i],  ys[i]);
        }
    }

    private void OnHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _activeElement is null) { ResetDrag(); return; }

        var pos     = e.GetCurrentPoint(LayerCanvas).Position;
        var gridPos = Config.PixelToSnappedTopLeft(pos);

        GridCoordinate newTopLeft = _startTopLeft;
        int newW = _startWidth;
        int newH = _startHeight;
        int minCells = 1;

        switch (_draggingCorner)
        {
            case Corner.BottomRight:
                newW = Math.Max(minCells, gridPos.Col - _startTopLeft.Col + 1);
                newH = Math.Max(minCells, gridPos.Row - _startTopLeft.Row + 1);
                break;
            case Corner.BottomLeft:
                {
                    int newCol  = Math.Min(gridPos.Col, _startTopLeft.Col + _startWidth - minCells);
                    newW        = _startTopLeft.Col + _startWidth - newCol;
                    newH        = Math.Max(minCells, gridPos.Row - _startTopLeft.Row + 1);
                    newTopLeft  = new GridCoordinate(newCol, _startTopLeft.Row);
                }
                break;
            case Corner.TopRight:
                {
                    int newRow  = Math.Min(gridPos.Row, _startTopLeft.Row + _startHeight - minCells);
                    newH        = _startTopLeft.Row + _startHeight - newRow;
                    newW        = Math.Max(minCells, gridPos.Col - _startTopLeft.Col + 1);
                    newTopLeft  = new GridCoordinate(_startTopLeft.Col, newRow);
                }
                break;
            case Corner.TopLeft:
                {
                    int newCol  = Math.Min(gridPos.Col, _startTopLeft.Col + _startWidth  - minCells);
                    int newRow  = Math.Min(gridPos.Row, _startTopLeft.Row + _startHeight - minCells);
                    newW        = _startTopLeft.Col + _startWidth  - newCol;
                    newH        = _startTopLeft.Row + _startHeight - newRow;
                    newTopLeft  = new GridCoordinate(newCol, newRow);
                }
                break;
        }

        // Clamp to grid bounds
        newW = Math.Max(minCells, Math.Min(newW, Config.HorizontalSlots - newTopLeft.Col));
        newH = Math.Max(minCells, Math.Min(newH, Config.VerticalSlots   - newTopLeft.Row));

        // Commit: update layout manager
        try
        {
            if (newTopLeft == _startTopLeft)
                LayoutManager.Resize(_activeElement, newW, newH);
            else
                LayoutManager.ResizeAndMove(_activeElement, newTopLeft, newW, newH);

            ElementResized?.Invoke(_activeElement);
        }
        catch
        {
            // Overlap — rollback to start dimensions (already in element from RemoveAndRollback)
            PositionHandles(_activeElement);
        }

        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        ResetDrag();
        e.Handled = true;
    }

    private void OnHandleCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_activeElement is not null) PositionHandles(_activeElement);
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        ResetDrag();
    }

    private void ResetDrag()
    {
        _isDragging     = false;
        _draggingCorner = Corner.None;
    }
}
