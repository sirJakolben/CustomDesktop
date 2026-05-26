using CustomDesktop.Item;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace CustomDesktop.Folder;

/// <summary>
/// Expanded overlay for a folder.
/// Floats above the desktop canvas — no title bar, no close button.
///
/// Layout rules (per spec):
///   ≤ 4 items  → 2 rows
///   5-18 items → 3 rows
///   > 18 items → 3 rows + horizontal ScrollViewer
///
/// Icons are the same block size as regular desktop items (DefaultIconCells × CellSize).
/// </summary>
internal sealed class FolderPopup
{
    private readonly Popup      _popup      = new();
    private readonly StackPanel _itemsPanel = new() { Orientation = Orientation.Horizontal };

    /// Fires when the user starts a drag on an item inside this popup.
    /// (string path, FolderPopup source)
    internal event Action<string, FolderPopup>? ItemDragStartRequested;

    private double      _blockSize;
    private FolderModel? _model;

    internal FolderPopup()
    {
        BuildVisualTree();
    }

    // ── Visual tree ──────────────────────────────────────────────────────────────

    private void BuildVisualTree()
    {
        var scroll = new ScrollViewer
        {
            HorizontalScrollMode          = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode            = ScrollMode.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
            Content                       = _itemsPanel,
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background   = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E)),
            Padding      = new Thickness(12),
            MinWidth     = 120,
            Child        = scroll,
        };

        var root = new Microsoft.UI.Xaml.Controls.Grid();
        root.Children.Add(border);

        _popup.Child                 = root;
        _popup.IsLightDismissEnabled = true;
    }

    // ── Show / hide ──────────────────────────────────────────────────────────────

    internal void Show(FolderModel model, double blockSize,
                       Point anchorPixel, XamlRoot xamlRoot,
                       IReadOnlyList<BitmapImage?> icons)
    {
        _model     = model;
        _blockSize = blockSize;

        RebuildItems(model, icons);

        _popup.XamlRoot         = xamlRoot;
        _popup.HorizontalOffset = anchorPixel.X;
        _popup.VerticalOffset   = anchorPixel.Y - EstimatedPopupHeight(model.ItemPaths.Count);
        _popup.IsOpen           = true;
    }

    internal void Close() => _popup.IsOpen = false;
    internal bool IsOpen  => _popup.IsOpen;

    // ── Item grid ────────────────────────────────────────────────────────────────

    private void RebuildItems(FolderModel model, IReadOnlyList<BitmapImage?> icons)
    {
        _itemsPanel.Children.Clear();

        int count = model.ItemPaths.Count;
        if (count == 0) return;

        int rows = count <= 4 ? 2 : 3;
        int cols = (int)Math.Ceiling((double)count / rows);

        for (int col = 0; col < cols; col++)
        {
            var colPanel = new StackPanel { Orientation = Orientation.Vertical };

            for (int row = 0; row < rows; row++)
            {
                int idx = col * rows + row;
                if (idx >= count) break;

                var icon = idx < icons.Count ? icons[idx] : null;
                colPanel.Children.Add(CreateItemControl(model.ItemPaths[idx], icon));
            }

            _itemsPanel.Children.Add(colPanel);
        }
    }

    private DesktopItemControl CreateItemControl(string path, BitmapImage? icon)
    {
        var element = new DesktopItemElement(path, default);
        element.Icon = icon;

        var control = new DesktopItemControl
        {
            Width  = _blockSize,
            Height = _blockSize,
        };
        control.Bind(element);
        if (icon is not null)
            control.UpdateIcon();

        // Wire drag-out event
        control.DragStartRequested += c =>
            ItemDragStartRequested?.Invoke(path, this);

        return control;
    }

    private double EstimatedPopupHeight(int count)
    {
        int rows = count <= 4 ? 2 : 3;
        return rows * _blockSize + 48;   // 48 = padding + margin buffer
    }

    // ── Refresh ──────────────────────────────────────────────────────────────────

    /// Rebuild the item grid with new icons (call after async icon load completes).
    internal void Refresh(FolderModel model, IReadOnlyList<BitmapImage?> icons)
    {
        _model = model;
        RebuildItems(model, icons);
    }
}
