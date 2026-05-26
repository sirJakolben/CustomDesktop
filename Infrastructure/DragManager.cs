using CustomDesktop.Folder;
using CustomDesktop.Grid;
using CustomDesktop.Item;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace CustomDesktop.Infrastructure;

/// <summary>
/// Manages drag-and-drop of desktop items and folders on the grid canvas.
///
/// Drag lifecycle:
///   1. DesktopItemControl or FolderControl raises DragStartRequested.
///   2. DragManager captures the pointer on the overlay canvas and renders a
///      semi-transparent ghost at the pointer position.
///   3. On PointerMoved the snap indicator updates to the nearest free slot.
///   4. On PointerReleased the drop is committed:
///      - item on item   → create folder at the target slot
///      - item on folder → add to folder
///      - item on free   → move to free slot
///      - folder on free → move folder to free slot
///   5. On PointerCanceled/Lost the drag is aborted.
/// </summary>
internal sealed class DragManager
{
    // ── Wiring (set by GridCanvas after construction) ──────────────────────────
    internal GridLayoutManager       LayoutManager { get; set; } = null!;
    internal GridConfiguration       Config        { get; set; } = null!;
    internal Canvas                  OverlayCanvas { get; set; } = null!;

    /// Called when a drop should create a new folder from two items.
    /// Args: (draggedPath, targetPath, targetSlot)
    internal event Action<string, string, GridCoordinate>?            FolderCreateRequested;

    /// Called when a desktop item drop moves it to a free slot.
    /// Args: (element, newTopLeft)
    internal event Action<DesktopItemElement, GridCoordinate>?        ItemMoved;

    /// Called when a folder drop moves it to a free slot.
    /// Args: (folderModel, newTopLeft)
    internal event Action<FolderModel, GridCoordinate>?               FolderMoved;

    // ── Drag state ─────────────────────────────────────────────────────────────
    private enum DragKind { None, Item, Folder }

    private DragKind            _kind          = DragKind.None;
    private DesktopItemElement? _draggedItem;
    private FolderModel?        _draggedFolder;
    private FolderPopup?        _sourcePopup;   // non-null when dragging out of a folder
    private Border?             _ghost;
    private Border?             _snapIndicator;
    private GridCoordinate?     _snapTarget;
    private GridCoordinate?     _hitFolder;     // occupied slot we're hovering over

    // ── Attach helpers ─────────────────────────────────────────────────────────

    internal void AttachItem(DesktopItemControl control, DesktopItemElement element)
        => control.DragStartRequested += _ => BeginItemDrag(element, control, null);

    internal void AttachItemInPopup(DesktopItemControl control, DesktopItemElement element,
                                    FolderPopup sourcePopup)
        => control.DragStartRequested += _ => BeginItemDrag(element, control, sourcePopup);

    internal void AttachFolder(FolderControl control, FolderModel model)
        => control.DragStartRequested += _ => BeginFolderDrag(model, control);

    // ── Begin drag ─────────────────────────────────────────────────────────────

    private void BeginItemDrag(DesktopItemElement element, UIElement source, FolderPopup? popup)
    {
        _kind         = DragKind.Item;
        _draggedItem  = element;
        _sourcePopup  = popup;
        StartVisuals(source);
    }

    private void BeginFolderDrag(FolderModel model, UIElement source)
    {
        _kind          = DragKind.Folder;
        _draggedFolder = model;
        _sourcePopup   = null;
        StartVisuals(source);
    }

    private void StartVisuals(UIElement source)
    {
        // Ghost: semi-transparent white rectangle the same block size
        double block = Config.DefaultIconCells * Config.CellSize;

        _ghost = new Border
        {
            Width            = block,
            Height           = block,
            Background       = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            CornerRadius     = new CornerRadius(12),
            IsHitTestVisible = false,
        };
        Canvas.SetZIndex(_ghost, 200);  // above everything

        _snapIndicator = new Border
        {
            Width            = block,
            Height           = block,
            Background       = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x00, 0xAA, 0xFF)),
            CornerRadius     = new CornerRadius(12),
            IsHitTestVisible = false,
            Visibility       = Visibility.Collapsed,
        };
        Canvas.SetZIndex(_snapIndicator, 199);

        OverlayCanvas.Children.Add(_snapIndicator);
        OverlayCanvas.Children.Add(_ghost);

        // Hook pointer events on the overlay so movement is captured globally
        OverlayCanvas.IsHitTestVisible = true;
        OverlayCanvas.PointerMoved    += OnPointerMoved;
        OverlayCanvas.PointerReleased += OnPointerReleased;
        OverlayCanvas.PointerCanceled += OnPointerCanceled;
    }

    // ── Pointer tracking ───────────────────────────────────────────────────────

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_kind == DragKind.None) return;

        var pos   = e.GetCurrentPoint(OverlayCanvas).Position;
        double block = Config.DefaultIconCells * Config.CellSize;

        // Move ghost to cursor
        Canvas.SetLeft(_ghost!, pos.X - block / 2);
        Canvas.SetTop(_ghost!,  pos.Y - block / 2);

        // Compute snap target
        var snapped = Config.PixelToSnappedTopLeft(pos);

        bool isFree = LayoutManager.CanPlace(snapped,
            Config.DefaultIconCells, Config.DefaultIconCells);

        if (isFree)
        {
            _snapTarget = snapped;
            _hitFolder  = null;
            var px = Config.GridToPixel(snapped);
            Canvas.SetLeft(_snapIndicator!, px.X);
            Canvas.SetTop(_snapIndicator!,  px.Y);
            _snapIndicator!.Visibility = Visibility.Visible;
        }
        else
        {
            _snapTarget = null;
            _hitFolder  = snapped;
            _snapIndicator!.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        => CommitDrop();

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        => CancelDrag();

    // ── Drop commit ────────────────────────────────────────────────────────────

    private void CommitDrop()
    {
        if (_kind == DragKind.None) { Cleanup(); return; }

        if (_kind == DragKind.Item && _draggedItem is not null)
        {
            if (_snapTarget.HasValue)
            {
                // Free slot — move the item
                ItemMoved?.Invoke(_draggedItem, _snapTarget.Value);
            }
            else if (_hitFolder.HasValue)
            {
                // Dropped onto an occupied cell — let GridCanvas decide
                // (it knows whether that cell belongs to a folder or another item)
                FolderCreateRequested?.Invoke(_draggedItem.Path,
                    _draggedItem.Path,          // placeholder; GridCanvas resolves target
                    _hitFolder.Value);
            }
        }
        else if (_kind == DragKind.Folder && _draggedFolder is not null)
        {
            if (_snapTarget.HasValue)
                FolderMoved?.Invoke(_draggedFolder, _snapTarget.Value);
        }

        Cleanup();
    }

    private void CancelDrag() => Cleanup();

    private void Cleanup()
    {
        _kind          = DragKind.None;
        _draggedItem   = null;
        _draggedFolder = null;
        _sourcePopup   = null;
        _snapTarget    = null;
        _hitFolder     = null;

        if (_ghost           is not null) { OverlayCanvas.Children.Remove(_ghost);         _ghost         = null; }
        if (_snapIndicator   is not null) { OverlayCanvas.Children.Remove(_snapIndicator); _snapIndicator = null; }

        OverlayCanvas.IsHitTestVisible = false;
        OverlayCanvas.PointerMoved    -= OnPointerMoved;
        OverlayCanvas.PointerReleased -= OnPointerReleased;
        OverlayCanvas.PointerCanceled -= OnPointerCanceled;
    }

    // ── Source-popup accessor ──────────────────────────────────────────────────

    internal FolderPopup? SourcePopup => _sourcePopup;
}
