using CustomDesktop.Folder;
using CustomDesktop.Infrastructure;
using CustomDesktop.Item;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CustomDesktop.Grid;

internal sealed partial class GridCanvas : UserControl
{
    private readonly GridConfiguration    _config  = GridConfiguration.Load();
    private readonly GridLayoutManager    _manager = new();
    private readonly DesktopItemRepository _repo   = new();
    private readonly DragManager          _drag    = new();

    // Desktop items
    private readonly List<(DesktopItemElement Element, DesktopItemControl Control)> _items = [];

    // Folders
    private readonly List<(FolderModel Model, FolderControl Control)> _folders = [];

    // Cached icons per folder (index matches FolderModel.ItemPaths order)
    private readonly Dictionary<FolderModel, List<BitmapImage?>> _folderIcons = [];

    // Currently open folder popup (only one at a time)
    private FolderPopup? _openPopup;

    // Owner HWND passed in from MainWindow — used for Properties dialog.
    internal nint OwnerHwnd { get; set; }

    public GridCanvas()
    {
        InitializeComponent();
        Loaded      += OnLoaded;
        SizeChanged += (_, _) => Recalculate();

        // Wire DragManager
        _drag.LayoutManager = _manager;
        _drag.Config        = _config;
        _drag.OverlayCanvas = DragOverlayCanvas;

        _drag.ItemMoved             += OnItemMoved;
        _drag.FolderMoved           += OnFolderMoved;
        _drag.FolderCreateRequested += OnFolderCreateRequested;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Recalculate();
        _ = LoadItemsAsync();
        _repo.StartWatching(DispatcherQueue);
        _repo.ItemCreated += OnItemCreated;
        _repo.ItemDeleted += OnItemDeleted;
        _repo.ItemRenamed += OnItemRenamed;
    }

    // ── Layout ─────────────────────────────────────────────────────────────────

    private void Recalculate()
    {
        if (ActualWidth is 0 || ActualHeight is 0) return;

        _config.Compute(ActualWidth, ActualHeight);

        double block = _config.DefaultIconCells * _config.CellSize;
        HoverIndicator.Width  = block;
        HoverIndicator.Height = block;

        foreach (var (element, control) in _items)
        {
            control.Width  = block;
            control.Height = block;
            var px = _config.GridToPixel(element.TopLeft);
            Canvas.SetLeft(control, px.X);
            Canvas.SetTop(control,  px.Y);
        }

        foreach (var (model, control) in _folders)
        {
            control.Width  = block;
            control.Height = block;
            var px = _config.GridToPixel(model.TopLeft);
            Canvas.SetLeft(control, px.X);
            Canvas.SetTop(control,  px.Y);
        }
    }

    // ── Item loading ───────────────────────────────────────────────────────────

    private async Task LoadItemsAsync()
    {
        var layout = LayoutPersistence.TryLoad();

        if (layout is not null)
            await RestoreLayoutAsync(layout);
        else
            await AutoPlaceItemsAsync();
    }

    /// Restore from persisted layout: placed items at saved coordinates,
    /// then auto-place any newly-discovered items that aren't in the layout.
    private async Task RestoreLayoutAsync(LayoutPersistence.LayoutData layout)
    {
        var placements = new List<(DesktopItemElement Element, DesktopItemControl Control)>();

        // ── Restore folders first (they occupy slots) ──
        foreach (var pf in layout.Folders)
        {
            var folder = LayoutPersistence.ToFolder(pf, _config.DefaultIconCells);
            if (!_manager.CanPlace(folder.TopLeft, folder.WidthCells, folder.HeightCells))
                continue; // overlap — skip

            _manager.Place(folder);
            _folderIcons[folder] = Enumerable.Repeat<BitmapImage?>(null, folder.ItemPaths.Count).ToList();

            double block  = _config.DefaultIconCells * _config.CellSize;
            var control   = new FolderControl { Width = block, Height = block };
            control.Bind(folder);

            var px = _config.GridToPixel(folder.TopLeft);
            Canvas.SetLeft(control, px.X);
            Canvas.SetTop(control,  px.Y);

            LayerCanvas.Children.Add(control);
            _folders.Add((folder, control));
            WireFolderControl(control, folder);
        }

        // ── Restore items at saved coordinates ──
        var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pi in layout.Items)
        {
            var coord = new GridCoordinate(pi.Col, pi.Row);
            if (!_manager.CanPlace(coord, _config.DefaultIconCells, _config.DefaultIconCells))
                continue; // overlap — skip

            var element = LayoutPersistence.ToElement(pi, _config.DefaultIconCells);
            _manager.Place(element);
            restoredPaths.Add(pi.Path);

            var (control, px) = CreateItemControl(element);
            placements.Add((element, control));
        }

        // ── Auto-place items not in the saved layout ──
        var allPaths = _repo.GetPaths();
        // Exclude paths already in folders
        var folderPaths = new HashSet<string>(_folders.SelectMany(f => f.Model.ItemPaths),
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in allPaths)
        {
            if (restoredPaths.Contains(path) || folderPaths.Contains(path)) continue;

            var slot = FindNextFreeSlot();
            if (slot is null) break;

            var element = new DesktopItemElement(path, slot.Value, _config.DefaultIconCells);
            _manager.Place(element);
            var (control, px) = CreateItemControl(element);
            placements.Add((element, control));
        }

        // ── Async icon load for all items ──
        await LoadIconsAsync(placements);

        // ── Async icon load for all folders ──
        foreach (var (folder, control) in _folders.ToList())
            await LoadFolderIconsAsync(folder, control);
    }

    /// Original auto-placement: column-first, all items at first free slot.
    private async Task AutoPlaceItemsAsync()
    {
        var paths = _repo.GetPaths();
        var placements = new List<(DesktopItemElement Element, DesktopItemControl Control)>(paths.Count);

        foreach (var path in paths)
        {
            var slot = FindNextFreeSlot();
            if (slot is null) break;

            var element = new DesktopItemElement(path, slot.Value, _config.DefaultIconCells);
            _manager.Place(element);
            var (control, px) = CreateItemControl(element);
            placements.Add((element, control));
        }

        await LoadIconsAsync(placements);
    }

    /// Constructs and registers a DesktopItemControl for the given element.
    private (DesktopItemControl Control, Windows.Foundation.Point Pixel) CreateItemControl(
        DesktopItemElement element)
    {
        double block = _config.DefaultIconCells * _config.CellSize;
        var control  = new DesktopItemControl { Width = block, Height = block };
        control.OwnerHwnd = OwnerHwnd;
        control.Bind(element);

        var px = _config.GridToPixel(element.TopLeft);
        Canvas.SetLeft(control, px.X);
        Canvas.SetTop(control,  px.Y);

        LayerCanvas.Children.Add(control);
        _items.Add((element, control));
        _drag.AttachItem(control, element);

        return (control, px);
    }

    private async Task LoadIconsAsync(
        IReadOnlyList<(DesktopItemElement Element, DesktopItemControl Control)> placements)
    {
        var dq = DispatcherQueue;
        await Task.WhenAll(placements.Select(async p =>
        {
            var icon = await IconLoader.LoadAsync(p.Element.Path);
            if (icon is null) return;
            p.Element.Icon = icon;
            dq.TryEnqueue(p.Control.UpdateIcon);
        }));
    }

    // ── Save layout helper ─────────────────────────────────────────────────────

    private void SaveLayout() =>
        LayoutPersistence.Save(_items, _folders);

    // ── Folder: create from two items ─────────────────────────────────────────

    /// Creates a folder from a dragged item dropped onto another item.
    /// targetCoord is the grid cell of the target element.
    private async Task CreateFolderFromItems(DesktopItemElement dragged, GridCoordinate targetCoord)
    {
        // Find the item at targetCoord
        var targetEntry = _items.FirstOrDefault(t =>
            t.Element.TopLeft == targetCoord);

        if (targetEntry.Element is null) return;   // nothing found — abort

        var slot = targetEntry.Element.TopLeft;

        // Remove both items from grid
        RemoveItem(dragged);
        RemoveItem(targetEntry.Element);

        // Create folder at the freed slot
        var folder = new FolderModel(slot, _config.DefaultIconCells);
        folder.ItemPaths.Add(dragged.Path);
        folder.ItemPaths.Add(targetEntry.Element.Path);

        _manager.Place(folder);
        _folderIcons[folder] = [null, null];

        double block = _config.DefaultIconCells * _config.CellSize;
        var control  = new FolderControl { Width = block, Height = block };
        control.Bind(folder);

        var px = _config.GridToPixel(slot);
        Canvas.SetLeft(control, px.X);
        Canvas.SetTop(control,  px.Y);

        LayerCanvas.Children.Add(control);
        _folders.Add((folder, control));

        WireFolderControl(control, folder);

        // Load icons async
        await LoadFolderIconsAsync(folder, control);
        SaveLayout();
    }

    private async Task LoadFolderIconsAsync(FolderModel folder, FolderControl control)
    {
        if (!_folderIcons.ContainsKey(folder))
            _folderIcons[folder] = new List<BitmapImage?>(
                Enumerable.Repeat<BitmapImage?>(null, folder.ItemPaths.Count));

        var icons = _folderIcons[folder];

        // Ensure list has enough slots
        while (icons.Count < folder.ItemPaths.Count) icons.Add(null);

        var dq = DispatcherQueue;
        await Task.WhenAll(folder.ItemPaths.Select(async (path, i) =>
        {
            var icon = await IconLoader.LoadAsync(path);
            if (icon is null) return;
            icons[i] = icon;
            dq.TryEnqueue(() =>
            {
                control.SetPreviews(icons);
                _openPopup?.Refresh(folder, icons);
            });
        }));
    }

    // ── Folder: add item ───────────────────────────────────────────────────────

    private async Task AddItemToFolder(DesktopItemElement dragged, FolderModel folder)
    {
        RemoveItem(dragged);
        folder.ItemPaths.Add(dragged.Path);

        // Find the control
        var entry = _folders.FirstOrDefault(f => f.Model == folder);
        if (entry.Control is not null)
            await LoadFolderIconsAsync(folder, entry.Control);

        SaveLayout();
    }

    // ── Folder: remove item (drag out) ─────────────────────────────────────────

    private void RemoveItemFromFolder(string path, FolderModel folder)
    {
        folder.ItemPaths.Remove(path);

        if (folder.ItemPaths.Count >= 2)
        {
            // Update previews
            var entry = _folders.FirstOrDefault(f => f.Model == folder);
            if (entry.Control is not null && _folderIcons.TryGetValue(folder, out var icons))
                entry.Control.SetPreviews(icons);
            return;
        }

        if (folder.ItemPaths.Count == 1)
        {
            // Dissolve folder — place last item at folder's slot
            var lastPath = folder.ItemPaths[0];
            var slot     = folder.TopLeft;

            RemoveFolder(folder);

            var element = new DesktopItemElement(lastPath, slot, _config.DefaultIconCells);
            _manager.Place(element);
            var (control, _px) = CreateItemControl(element);
            _ = LoadSingleItemIconAsync(element, control);
            SaveLayout();
        }
        // count == 0 → folder was just emptied by drag-out; also dissolve
        else if (folder.ItemPaths.Count == 0)
        {
            RemoveFolder(folder);
            SaveLayout();
        }
    }

    private async Task LoadSingleItemIconAsync(DesktopItemElement element, DesktopItemControl control)
    {
        var icon = await IconLoader.LoadAsync(element.Path);
        if (icon is null) return;
        element.Icon = icon;
        DispatcherQueue.TryEnqueue(control.UpdateIcon);
    }

    // ── Folder: delete ─────────────────────────────────────────────────────────

    private void RemoveFolder(FolderModel folder)
    {
        _manager.Remove(folder);
        _folderIcons.Remove(folder);

        var entry = _folders.FirstOrDefault(f => f.Model == folder);
        if (entry.Control is not null)
        {
            LayerCanvas.Children.Remove(entry.Control);
            _folders.Remove(entry);
        }

        _openPopup?.Close();
        _openPopup = null;
    }

    // ── Folder control wiring ─────────────────────────────────────────────────

    private void WireFolderControl(FolderControl control, FolderModel folder)
    {
        control.OpenRequested   += OnFolderOpenRequested;
        control.DeleteRequested += OnFolderDeleteRequested;
        _drag.AttachFolder(control, folder);
    }

    private void OnFolderOpenRequested(FolderControl control)
    {
        if (control.Model is null) return;
        var folder = control.Model;

        // Toggle: close if already open for this folder
        if (_openPopup is { IsOpen: true })
        {
            _openPopup.Close();
            _openPopup = null;
            return;
        }

        _openPopup = new FolderPopup();
        _openPopup.ItemDragStartRequested += OnItemDragStartFromPopup;

        var icons  = _folderIcons.TryGetValue(folder, out var cached) ? cached
                     : new List<BitmapImage?>();
        var px     = _config.GridToPixel(folder.TopLeft);
        double block = _config.DefaultIconCells * _config.CellSize;

        _openPopup.Show(folder, block, new Windows.Foundation.Point(px.X, px.Y),
                        XamlRoot, icons);
    }

    private void OnFolderDeleteRequested(FolderControl control)
    {
        if (control.Model is null) return;
        RemoveFolder(control.Model);
        SaveLayout();
    }

    // ── Drag event handlers ────────────────────────────────────────────────────

    private void OnItemMoved(DesktopItemElement element, GridCoordinate newSlot)
    {
        _manager.Move(element, newSlot);   // updates element.TopLeft internally
        var control = _items.FirstOrDefault(i => i.Element == element).Control;
        if (control is null) return;

        double block = _config.DefaultIconCells * _config.CellSize;
        var px = _config.GridToPixel(newSlot);
        Canvas.SetLeft(control, px.X);
        Canvas.SetTop(control,  px.Y);
        SaveLayout();
    }

    private void OnFolderMoved(FolderModel folder, GridCoordinate newSlot)
    {
        _manager.Move(folder, newSlot);
        var control = _folders.FirstOrDefault(f => f.Model == folder).Control;
        if (control is null) return;

        var px = _config.GridToPixel(newSlot);
        Canvas.SetLeft(control, px.X);
        Canvas.SetTop(control,  px.Y);
        SaveLayout();
    }

    /// Drag dropped onto an occupied cell — determine whether it's a folder or item.
    private void OnFolderCreateRequested(string draggedPath, string ignored, GridCoordinate targetCoord)
    {
        // Is the target cell owned by a folder?
        var folderEntry = _folders.FirstOrDefault(f =>
        {
            for (int r = f.Model.TopLeft.Row; r < f.Model.TopLeft.Row + f.Model.HeightCells; r++)
                for (int c = f.Model.TopLeft.Col; c < f.Model.TopLeft.Col + f.Model.WidthCells; c++)
                    if (new GridCoordinate(c, r) == targetCoord) return true;
            return false;
        });

        var draggedEntry = _items.FirstOrDefault(i => i.Element.Path == draggedPath);
        if (draggedEntry.Element is null) return;

        if (folderEntry.Model is not null)
        {
            // Dropped on a folder → add to folder
            _ = AddItemToFolder(draggedEntry.Element, folderEntry.Model);
        }
        else
        {
            // Dropped on another item → create folder
            _ = CreateFolderFromItems(draggedEntry.Element, targetCoord);
        }
    }

    private void OnItemDragStartFromPopup(string path, FolderPopup sourcePopup)
    {
        // Find the folder that owns this popup item
        var folderEntry = _folders.FirstOrDefault(f => f.Model.ItemPaths.Contains(path));
        if (folderEntry.Model is null) return;

        // Remove from folder immediately; if only 1 left it dissolves
        RemoveItemFromFolder(path, folderEntry.Model);
        sourcePopup.Close();

        // Create a temporary item element at a free slot and begin dragging it
        var slot = FindNextFreeSlot();
        if (slot is null) return;

        var element = new DesktopItemElement(path, slot.Value, _config.DefaultIconCells);
        _manager.Place(element);
        CreateItemControl(element);
        SaveLayout();
    }

    // ── Remove helpers ─────────────────────────────────────────────────────────

    private void RemoveItem(DesktopItemElement element)
    {
        _manager.Remove(element);
        var entry = _items.FirstOrDefault(i => i.Element == element);
        if (entry.Control is not null)
        {
            LayerCanvas.Children.Remove(entry.Control);
            _items.Remove(entry);
        }
    }

    // ── FileSystemWatcher handlers ─────────────────────────────────────────────

    private void OnItemCreated(string path)
    {
        // Ignore desktop.ini
        if (path.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;

        // Already tracked (e.g., copy-in triggered duplicate event)?
        if (_items.Any(i => string.Equals(i.Element.Path, path,
                StringComparison.OrdinalIgnoreCase))) return;

        var slot = FindNextFreeSlot();
        if (slot is null) return;

        var element = new DesktopItemElement(path, slot.Value, _config.DefaultIconCells);
        _manager.Place(element);
        var (control, _px) = CreateItemControl(element);
        _ = LoadSingleItemIconAsync(element, control);
        SaveLayout();
    }

    private void OnItemDeleted(string path)
    {
        // Check standalone items
        var entry = _items.FirstOrDefault(i =>
            string.Equals(i.Element.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry.Element is not null)
        {
            RemoveItem(entry.Element);
            SaveLayout();
            return;
        }

        // Check inside folders
        var folderEntry = _folders.FirstOrDefault(f =>
            f.Model.ItemPaths.Any(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        if (folderEntry.Model is null) return;

        RemoveItemFromFolder(path, folderEntry.Model);
        SaveLayout();
    }

    private void OnItemRenamed(string oldPath, string newPath)
    {
        // Update standalone item
        var entry = _items.FirstOrDefault(i =>
            string.Equals(i.Element.Path, oldPath, StringComparison.OrdinalIgnoreCase));
        if (entry.Element is not null)
        {
            // DesktopItemElement.Path is get-only; replace with a new element at the same slot
            var slot  = entry.Element.TopLeft;
            var cells = entry.Element.WidthCells;

            RemoveItem(entry.Element);

            var newElement = new DesktopItemElement(newPath, slot, cells);
            _manager.Place(newElement);
            var (control, _px) = CreateItemControl(newElement);
            _ = LoadSingleItemIconAsync(newElement, control);
            SaveLayout();
            return;
        }

        // Update path inside a folder
        var folderEntry = _folders.FirstOrDefault(f =>
            f.Model.ItemPaths.Any(p =>
                string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase)));
        if (folderEntry.Model is null) return;

        int idx = folderEntry.Model.ItemPaths.FindIndex(p =>
            string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            folderEntry.Model.ItemPaths[idx] = newPath;
            SaveLayout();
        }
    }

    // ── Slot finding ───────────────────────────────────────────────────────────

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
        if (_config.CellSize is 0) return;

        var pos = e.GetCurrentPoint(LayerCanvas).Position;

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
            HoverIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void LayerCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
        => HoverIndicator.Visibility = Visibility.Collapsed;

    private void LayerCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        NativeMethods.ForwardDesktopContextMenu();
        e.Handled = true;
    }
}
