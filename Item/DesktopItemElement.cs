using CustomDesktop.Grid;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CustomDesktop.Item;

/// <summary>
/// One item on the desktop (file, folder, or shortcut).
/// Implements IDesktopElement so the GridLayoutManager can track its cells.
/// </summary>
internal sealed class DesktopItemElement : IDesktopElement
{
    public string       Path        { get; }
    public string       DisplayName { get; }
    public BitmapImage? Icon        { get; set; }

    // IDesktopElement — TopLeft has an internal setter so GridCanvas can
    // position the element during auto-placement (Phase 3) and drag-drop (Phase 5).
    public GridCoordinate TopLeft      { get; set; }
    public int            WidthCells  { get; set; }
    public int            HeightCells { get; set; }

    public DesktopItemElement(string path, GridCoordinate topLeft, int cells = 3)
    {
        Path        = path;
        // Strip .lnk / .url extensions so shortcuts show without the extension.
        var ext = System.IO.Path.GetExtension(path);
        DisplayName = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                      ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileNameWithoutExtension(path)
            : System.IO.Path.GetFileName(path);
        TopLeft     = topLeft;
        WidthCells  = cells;
        HeightCells = cells;
    }
}
