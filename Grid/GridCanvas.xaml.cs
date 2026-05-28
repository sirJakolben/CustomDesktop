using CustomDesktop.Folder;
using CustomDesktop.Infrastructure;
using CustomDesktop.Item;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace CustomDesktop.Grid;

internal sealed partial class GridCanvas : UserControl
{
    private readonly GridConfiguration     _config  = GridConfiguration.Load();
    private readonly GridLayoutManager     _manager = new();
    private readonly DesktopItemRepository _repo    = new();
    private readonly DragManager           _drag    = new();
    private readonly ResizeManager         _resize  = new();

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

    // ── Rubber-band selection state ────────────────────────────────────────────
    private bool   _selectionActive;
    private Point  _selectionOrigin;
    private const double SelectionThresholdPx = 5.0;

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

        // Wire ResizeManager
        _resize.LayoutManager = _manager;
        _resize.Config        = _config;
        _resize.LayerCanvas   = LayerCanvas;
        _resize.OverlayCanvas = DragOverlayCanvas;
        _resize.ElementResized += OnElementResized;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _resize.Initialize();
        Recalculate();
        _ = LoadItemsAsync();
        _repo.StartWatching(DispatcherQueue);
        _repo.ItemCreated       += OnItemCreated;
        _repo.ItemDeleted       += OnItemDeleted;
        _repo.ItemRenamed       += OnItemRenamed;
        _repo.DirectoryCreated  += OnDirectoryCreated;
        _repo.DirectoryDeleted  += OnDirectoryDeleted;
        _repo.DirectoryRenamed  += OnDirectoryRenamed;
    }

    // ── Layout ─────────────────────────────────────────────────────────────────

    private void Recalculate()
    {
        if (ActualWidth is 0 || ActualHeight is 0) return;

        _config.Compute(ActualWidth, ActualHeight);

        foreach (var (element, control) in _items)
        {
            double w = element.WidthCells  * _config.CellSize;
            double h = element.HeightCells * _config.CellSize;
            control.Width  = w;
            control.Height = h;
            var px = _config.GridToPixel(element.TopLeft);
            Canvas.SetLeft(control, px.X);
            Canvas.SetTop(control,  px.Y);
        }

        foreach (var (model, control) in _folders)
        {
            double w = model.WidthCells  * _config.CellSize;
            double h = model.HeightCells * _config.CellSize;
            control.Width  = w;
            control.Height = h;
            var px = _config.GridToPixel(model.TopLeft);
            Canvas.SetLeft(control, px.X);
            Canvas.SetTop(control,  px.Y);
        }
    }

    /// Called from MainWindow when Ctrl+/- hotkeys fire.
    internal void AdjustVerticalSlots(int delta)
    {
        // Minimum: at least DefaultIconCells vertical slots so one element fits
        int minSlots = Math.Max(1, _config.DefaultIconCells);
        int newSlots = Math.Max(minSlots, _config.VerticalSlots + delta);
        if (newSlots == _config.VerticalSlots) return;

        _config.VerticalSlots = newSlots;
        _config.Save();
        Recalculate();
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

    /// Restore from persisted layout, then auto-place newly-discovered items.
    private async Task RestoreLayoutAsync(LayoutPersistence.LayoutData layout)
    {
        var placements = new List<(DesktopItemElement Element, DesktopItemControl Control)>();

        // ── Restore filesystem-backed folders first ────────────────────────────
        var fsSavedDirs = new HashSet<string>(
            layout.Folders
                .Where(f => f.DirectoryPath is not null)
                .Select(f => f.DirectoryPath!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pf in layout.Folders)
        {
            var folder = LayoutPersistence.ToFolder(pf, _config.DefaultIconCells);
            if (!_manager.CanPlace(folder.TopLeft, folder.WidthCells, folder.HeightCells))
                continue;

            _manager.Place(folder);
            _folderIcons[folder] = Enumerable.Repeat<BitmapImage?>(null, folder.ItemPaths.Count).ToList();

            var control = CreateFolderControl(folder);
            _folders.Add((folder, control));
            WireFolderControl(control, folder);
        }

        // ── Restore items at saved coordinates ─────────────────────────────────
        var restoredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pi in layout.Items)
        {
            var coord = new GridCoordinate(pi.Col, pi.Row);
            if (!_manager.CanPlace(coord, pi.WidthCells > 0 ? pi.WidthCells : _config.DefaultIconCells,
                                          pi.HeightCells > 0 ? pi.HeightCells : _config.DefaultIconCells))
                continue;

            var element = LayoutPersistence.ToElement(pi, _config.DefaultIconCells);
            _manager.Place(element);
            restoredPaths.Add(pi.Path);

            var (control, _) = CreateItemControl(element);
            placements.Add((element, control));
        }

        // ── Auto-place new items not in the saved layout ────────────────────────
        var allPaths = _repo.GetPaths();
        var folderPaths = new HashSet<string>(_folders.SelectMany(f => f.Model.ItemPaths),
            StringComparer.OrdinalIgnoreCase);

        foreach (var path in allPaths)
        {
            if (restoredPaths.Contains(path) || folderPaths.Contains(path)) continue;

            var slot = FindNextFreeSlot();
            if (slot is null) break;

            var element = new DesktopItemElement(path, slot.Value, _config.DefaultIconCells);
            _manager.Place(element);
            var (control, _) = CreateItemControl(element);
            placements.Add((element, control));
        }

        // ── Auto-place new filesystem directories not yet in the layout ─────────
        var allDirs = _repo.GetDirectoryPaths();
        foreach (var dirPath in allDirs)
        {
            if (fsSavedDirs.Contains(dirPath)) continue;  // already restored above

            var slot = FindNextFreeSlot();
            if (slot is null) break;

            await PlaceFilesystemFolderAsync(dirPath, slot.Value);
        }

        // ── Async icon loads ────────────────────────────────────────────────────
        await LoadIconsAsync(placements);

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
            var (control, _) = CreateItemControl(element);
            placements.Add((element, control));
        }

        // Place filesystem directories as folders
        foreach (var dirPath in _repo.GetDirectoryPaths())
        {
            var slot = FindNextFreeSlot();
            if (slot is null) break;
            await PlaceFilesystemFolderAsync(dirPath, slot.Value);
        }

        await LoadIconsAsync(placements);

        foreach (var (folder, control) in _folders.ToList())
            await LoadFolderIconsAsync(folder, control);
    }

    // ── Filesystem-folder placement ────────────────────────────────────────────

    /// Creates a FolderModel backed by a real directory and places it on the grid.
    private async Task PlaceFilesystemFolderAsync(string dirPath, GridCoordinate slot)
    {
        if (!_manager.CanPlace(slot, _config.DefaultIconCells, _config.DefaultIconCells)) return;

        var dirName = System.IO.Path.GetFileName(dirPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));

        // Populate ItemPaths with files inside the directory
        var contents = Directory.Exists(dirPath)
            ? Directory.GetFiles(dirPath)
                .Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];

        var folder = new FolderModel(slot, _config.DefaultIconCells, dirName)
        {
            DirectoryPath = dirPath,
        };
        folder.ItemPaths.AddRange(contents);

        _manager.Place(folder);
        _folderIcons[folder] = Enumerable.Repeat<BitmapImage?>(null, contents.Count).ToList();

        var control = CreateFolderControl(folder);
        _folders.Add((folder, control));
        WireFolderControl(control, folder);

        await LoadFolderIconsAsync(folder, control);
        SaveLayout();
    }

    // ── Control factory helpers ────────────────────────────────────────────────

    private (DesktopItemControl Control, Point Pixel) CreateItemControl(DesktopItemElement element)
    {
        double w = element.WidthCells  * _config.CellSize;
        double h = element.HeightCells * _config.CellSize;
        var control = new DesktopItemControl { Width = w, Height = h };
        control.OwnerHwnd = OwnerHwnd;
        control.Bind(element);

        var px = _config.GridToPixel(element.TopLeft);
        Canvas.SetLeft(control, px.X);
        Canvas.SetTop(control,  px.Y);

        LayerCanvas.Children.Add(control);
        _items.Add((element, control));
        _drag.AttachItem(control, element);
        _resize.Attach(element, control);

        return (control, px);
    }

    private FolderControl CreateFolderControl(FolderModel folder)
    {
        double w = folder.WidthCells  * _config.CellSize;
        double h = folder.HeightCells * _config.CellSize;
        var control = new FolderControl { Width = w, Height = h };
        control.Bind(folder);

        var px = _config.GridToPixel(folder.TopLeft);
        Canvas.SetLeft(control, px.X);
        Canvas.SetTop(control,  px.Y);

        LayerCanvas.Children.Add(control);
        return control;
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

    // ── Resize handler ─────────────────────────────────────────────────────────

    private void OnElementResized(IDesktopElement element)
    {
        // Update control size and position
        if (element is DesktopItemElement itemElem)
        {
            var entry = _items.FirstOrDefault(i => i.Element == itemElem);
            if (entry.Control is not null)
            {
                entry.Control.Width  = element.WidthCells  * _config.CellSize;
                entry.Control.Height = element.HeightCells * _config.CellSize;
                var px = _config.GridToPixel(element.TopLeft);
                Canvas.SetLeft(entry.Control, px.X);
                Canvas.SetTop(entry.Control,  px.Y);
            }
        }
        else if (element is FolderModel folderModel)
        {
            var entry = _folders.FirstOrDefault(f => f.Model == folderModel);
            if (entry.Control is not null)
            {
                entry.Control.Width  = element.WidthCells  * _config.CellSize;
                entry.Control.Height = element.HeightCells * _config.CellSize;
                var px = _config.GridToPixel(element.TopLeft);
                Canvas.SetLeft(entry.Control, px.X);
                Canvas.SetTop(entry.Control,  px.Y);
            }
        }
        SaveLayout();
    }

    // ── Folder: create from two items ─────────────────────────────────────────

    private async Task CreateFolderFromItems(DesktopItemElement dragged, GridCoordinate targetCoord)
    {
        var targetEntry = _items.FirstOrDefault(t => t.Element.TopLeft == targetCoord);
        if (targetEntry.Element is null) return;

        var slot = targetEntry.Element.TopLeft;

        RemoveItem(dragged);
        RemoveItem(targetEntry.Element);

        // Create a real directory on the desktop for this folder
        string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string dirName     = GetUniqueDirectoryName(desktopPath, "Neuer Ordner");
        string dirPath     = System.IO.Path.Combine(desktopPath, dirName);

        try { Directory.CreateDirectory(dirPath); } catch { dirPath = null!; }

        string folderName = dirName;
        var folder = new FolderModel(slot, _config.DefaultIconCells, folderName)
        {
            DirectoryPath = string.IsNullOrEmpty(dirPath) ? null : dirPath,
        };

        // Move files into the directory if it was created
        string movedPath1 = dragged.Path;
        string movedPath2 = targetEntry.Element.Path;

        if (!string.IsNullOrEmpty(dirPath))
        {
            movedPath1 = MoveFileToDir(dragged.Path,              dirPath);
            movedPath2 = MoveFileToDir(targetEntry.Element.Path,  dirPath);
        }

        folder.ItemPaths.Add(movedPath1);
        folder.ItemPaths.Add(movedPath2);

        _manager.Place(folder);
        _folderIcons[folder] = [null, null];

        var control = CreateFolderControl(folder);
        _folders.Add((folder, control));
        WireFolderControl(control, folder);

        await LoadFolderIconsAsync(folder, control);
        SaveLayout();
    }

    private static string GetUniqueDirectoryName(string parent, string baseName)
    {
        string name = baseName;
        int i = 2;
        while (Directory.Exists(System.IO.Path.Combine(parent, name)))
            name = $"{baseName} ({i++})";
        return name;
    }

    private static string MoveFileToDir(string srcPath, string targetDir)
    {
        try
        {
            string fileName = System.IO.Path.GetFileName(srcPath);
            string dst      = System.IO.Path.Combine(targetDir, fileName);
            File.Move(srcPath, dst, overwrite: false);
            return dst;
        }
        catch { return srcPath; /* if move fails, keep original path */ }
    }

    private async Task LoadFolderIconsAsync(FolderModel folder, FolderControl control)
    {
        if (!_folderIcons.ContainsKey(folder))
            _folderIcons[folder] = new List<BitmapImage?>(
                Enumerable.Repeat<BitmapImage?>(null, folder.ItemPaths.Count));

        var icons = _folderIcons[folder];
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
        string newPath = dragged.Path;

        // Move file into directory if folder is filesystem-backed
        if (folder.DirectoryPath is not null)
            newPath = MoveFileToDir(dragged.Path, folder.DirectoryPath);

        RemoveItem(dragged);
        folder.ItemPaths.Add(newPath);

        var entry = _folders.FirstOrDefault(f => f.Model == folder);
        if (entry.Control is not null)
            await LoadFolderIconsAsync(folder, entry.Control);

        SaveLayout();
    }

    // ── Folder: remove item (drag out) ─────────────────────────────────────────

    private void RemoveItemFromFolder(string path, FolderModel folder)
    {
        // Move file back to desktop if folder is filesystem-backed
        if (folder.DirectoryPath is not null)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            try
            {
                string fileName = System.IO.Path.GetFileName(path);
                string dst      = System.IO.Path.Combine(desktopPath, fileName);
                if (!File.Exists(dst)) File.Move(path, dst);
            }
            catch { /* best-effort */ }
        }

        folder.ItemPaths.Remove(path);

        if (folder.ItemPaths.Count >= 2)
        {
            var entry = _folders.FirstOrDefault(f => f.Model == folder);
            if (entry.Control is not null && _folderIcons.TryGetValue(folder, out var icons))
                entry.Control.SetPreviews(icons);
            return;
        }

        if (folder.ItemPaths.Count == 1)
        {
            var lastPath = folder.ItemPaths[0];
            var slot     = folder.TopLeft;
            RemoveFolder(folder);

            var element = new DesktopItemElement(lastPath, slot, _config.DefaultIconCells);
            _manager.Place(element);
            var (control, _) = CreateItemControl(element);
            _ = LoadSingleItemIconAsync(element, control);
            SaveLayout();
        }
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
        _resize.Detach(folder);

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
        _resize.Attach(folder, control);
    }

    private void OnFolderOpenRequested(FolderControl control)
    {
        if (control.Model is null) return;
        var folder = control.Model;

        if (_openPopup is { IsOpen: true })
        {
            _openPopup.Close();
            _openPopup = null;
            return;
        }

        _openPopup = new FolderPopup();
        _openPopup.ItemDragStartRequested += OnItemDragStartFromPopup;

        var icons  = _folderIcons.TryGetValue(folder, out var cached) ? cached : new List<BitmapImage?>();
        var px     = _config.GridToPixel(folder.TopLeft);
        double block = _config.DefaultIconCells * _config.CellSize;

        _openPopup.Show(folder, block, new Point(px.X, px.Y), XamlRoot, icons);
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
        _manager.Move(element, newSlot);
        var control = _items.FirstOrDefault(i => i.Element == element).Control;
        if (control is null) return;

        control.Width  = element.WidthCells  * _config.CellSize;
        control.Height = element.HeightCells * _config.CellSize;
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

    private void OnFolderCreateRequested(string draggedPath, string ignored, GridCoordinate targetCoord)
    {
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
            _ = AddItemToFolder(draggedEntry.Element, folderEntry.Model);
        else
            _ = CreateFolderFromItems(draggedEntry.Element, targetCoord);
    }

    private void OnItemDragStartFromPopup(string path, FolderPopup sourcePopup)
    {
        var folderEntry = _folders.FirstOrDefault(f => f.Model.ItemPaths.Contains(path));
        if (folderEntry.Model is null) return;

        RemoveItemFromFolder(path, folderEntry.Model);
        sourcePopup.Close();

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
        _resize.Detach(element);
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
        if (path.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        if (_items.Any(i => string.Equals(i.Element.Path, path, StringComparison.OrdinalIgnoreCase))) return;

        var slot = FindNextFreeSlot();
        if (slot is null) return;

        var element = new DesktopItemElement(path, slot.Value, _config.DefaultIconCells);
        _manager.Place(element);
        var (control, _) = CreateItemControl(element);
        _ = LoadSingleItemIconAsync(element, control);
        SaveLayout();
    }

    private void OnItemDeleted(string path)
    {
        var entry = _items.FirstOrDefault(i =>
            string.Equals(i.Element.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry.Element is not null)
        {
            RemoveItem(entry.Element);
            SaveLayout();
            return;
        }

        var folderEntry = _folders.FirstOrDefault(f =>
            f.Model.ItemPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        if (folderEntry.Model is null) return;

        RemoveItemFromFolder(path, folderEntry.Model);
        SaveLayout();
    }

    private void OnItemRenamed(string oldPath, string newPath)
    {
        var entry = _items.FirstOrDefault(i =>
            string.Equals(i.Element.Path, oldPath, StringComparison.OrdinalIgnoreCase));
        if (entry.Element is not null)
        {
            var slot  = entry.Element.TopLeft;
            int cells = entry.Element.WidthCells;
            RemoveItem(entry.Element);

            var newElement = new DesktopItemElement(newPath, slot, cells);
            _manager.Place(newElement);
            var (control, _) = CreateItemControl(newElement);
            _ = LoadSingleItemIconAsync(newElement, control);
            SaveLayout();
            return;
        }

        var folderEntry = _folders.FirstOrDefault(f =>
            f.Model.ItemPaths.Any(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase)));
        if (folderEntry.Model is null) return;

        int idx = folderEntry.Model.ItemPaths.FindIndex(p =>
            string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) { folderEntry.Model.ItemPaths[idx] = newPath; SaveLayout(); }
    }

    // ── Directory (filesystem folder) FS handlers ──────────────────────────────

    private void OnDirectoryCreated(string dirPath)
    {
        // Already tracked?
        if (_folders.Any(f => string.Equals(f.Model.DirectoryPath, dirPath,
                StringComparison.OrdinalIgnoreCase))) return;

        var slot = FindNextFreeSlot();
        if (slot is null) return;

        _ = PlaceFilesystemFolderAsync(dirPath, slot.Value);
    }

    private void OnDirectoryDeleted(string dirPath)
    {
        var entry = _folders.FirstOrDefault(f =>
            string.Equals(f.Model.DirectoryPath, dirPath, StringComparison.OrdinalIgnoreCase));
        if (entry.Model is null) return;
        RemoveFolder(entry.Model);
        SaveLayout();
    }

    private void OnDirectoryRenamed(string oldPath, string newPath)
    {
        var entry = _folders.FirstOrDefault(f =>
            string.Equals(f.Model.DirectoryPath, oldPath, StringComparison.OrdinalIgnoreCase));
        if (entry.Model is null) return;

        entry.Model.DirectoryPath = newPath;
        entry.Model.Name = System.IO.Path.GetFileName(
            newPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));

        var ctrl = _folders.FirstOrDefault(f => f.Model == entry.Model).Control;
        ctrl?.Bind(entry.Model);   // refresh displayed name

        SaveLayout();
    }

    // ── Slot finding ───────────────────────────────────────────────────────────

    private GridCoordinate? FindNextFreeSlot()
    {
        if (_config.CellSize is 0) return null;

        int cells  = _config.DefaultIconCells;
        int maxCol = _config.HorizontalSlots - cells;
        int maxRow = _config.VerticalSlots   - cells;

        for (int c = 0; c <= maxCol; c++)
            for (int r = 0; r <= maxRow; r++)
            {
                var coord = new GridCoordinate(c, r);
                if (_manager.CanPlace(coord, cells, cells))
                    return coord;
            }
        return null;
    }

    // ── Rubber-band selection (pointer events) ─────────────────────────────────

    private void LayerCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(LayerCanvas);
        // Only start rubber band on left- or right-button, NOT on stylus/touch for now
        if (!pt.Properties.IsLeftButtonPressed && !pt.Properties.IsRightButtonPressed) return;

        _selectionActive = false;
        _selectionOrigin = pt.Position;
        LayerCanvas.CapturePointer(e.Pointer);
        e.Handled = false;  // don't consume — items also handle PointerPressed
    }

    private void LayerCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt  = e.GetCurrentPoint(LayerCanvas);
        var pos = pt.Position;

        // Show rubber band once drag threshold exceeded
        if (!_selectionActive)
        {
            double dx = pos.X - _selectionOrigin.X;
            double dy = pos.Y - _selectionOrigin.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < SelectionThresholdPx) return;
            _selectionActive = true;
        }

        if (!_selectionActive) return;

        // Grid-align the selection rectangle
        var tl = SnapToGrid(new Point(Math.Min(pos.X, _selectionOrigin.X),
                                      Math.Min(pos.Y, _selectionOrigin.Y)));
        var br = SnapToGridEnd(new Point(Math.Max(pos.X, _selectionOrigin.X),
                                         Math.Max(pos.Y, _selectionOrigin.Y)));

        double rectX = tl.X;
        double rectY = tl.Y;
        double rectW = Math.Max(0, br.X - tl.X);
        double rectH = Math.Max(0, br.Y - tl.Y);

        Canvas.SetLeft(SelectionBox, rectX);
        Canvas.SetTop(SelectionBox,  rectY);
        SelectionBox.Width     = rectW;
        SelectionBox.Height    = rectH;
        SelectionBox.Visibility = Visibility.Visible;
    }

    private void LayerCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _selectionActive = false;
        SelectionBox.Visibility = Visibility.Collapsed;
        LayerCanvas.ReleasePointerCapture(e.Pointer);
        // RMB context menu is handled by LayerCanvas_RightTapped (fires only when no drag occurred)
    }

    private void LayerCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _selectionActive = false;
        SelectionBox.Visibility = Visibility.Collapsed;
        LayerCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void LayerCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_selectionActive) return;
        SelectionBox.Visibility = Visibility.Collapsed;
    }

    private void LayerCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // RightTapped fires for a tap (not a drag). Forward to Progman.
        NativeMethods.ForwardDesktopContextMenu();
        e.Handled = true;
    }

    // ── Grid-snap helpers for rubber band ─────────────────────────────────────

    private Point SnapToGrid(Point px)
    {
        var coord = _config.PixelToSnappedTopLeft(px);
        return _config.GridToPixel(coord);
    }

    private Point SnapToGridEnd(Point px)
    {
        // Snap the bottom-right to the nearest cell boundary beyond the cursor
        if (_config.CellSize <= 0) return px;

        double marginAdjustedY = px.Y - _config.EdgeMarginPx;
        int col = (int)Math.Ceiling(px.X               / _config.CellSize);
        int row = (int)Math.Ceiling(marginAdjustedY     / _config.CellSize);

        col = Math.Max(0, Math.Min(col, _config.HorizontalSlots));
        row = Math.Max(0, Math.Min(row, _config.VerticalSlots));

        return new Point(col * _config.CellSize,
                         _config.EdgeMarginPx + row * _config.CellSize);
    }
}
