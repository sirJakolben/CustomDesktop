using CustomDesktop.Grid;
using Microsoft.UI;
using Windows.UI;

namespace CustomDesktop.Folder;

/// <summary>
/// Data model for an app-folder: a named, coloured container holding 2+ desktop items.
/// Occupies a 3×3 block on the grid (same as a regular item).
/// </summary>
internal sealed class FolderModel : Grid.IDesktopElement
{
    // ── IDesktopElement ────────────────────────────────────────────────────────
    public GridCoordinate TopLeft      { get; set; }
    public int            WidthCells  { get; set; }
    public int            HeightCells { get; set; }

    // ── Folder data ────────────────────────────────────────────────────────────
    public string     Name            { get; set; }
    public Color      BackgroundColor { get; set; }

    /// Ordered list of file-system paths contained in this folder.
    public List<string> ItemPaths { get; } = [];

    /// When non-null: this folder is backed by a real filesystem directory.
    /// null = virtual folder (tracked only in layout.json).
    public string? DirectoryPath { get; set; }

    // ── Colour presets ─────────────────────────────────────────────────────────
    /// Eight built-in presets offered in the colour picker.
    public static readonly Color[] Presets =
    [
        Color.FromArgb(0xFF, 0x60, 0x8B, 0xD0),   // steel blue
        Color.FromArgb(0xFF, 0x5A, 0xB0, 0x5A),   // forest green
        Color.FromArgb(0xFF, 0xD0, 0x5A, 0x5A),   // soft red
        Color.FromArgb(0xFF, 0xD0, 0xA0, 0x40),   // amber
        Color.FromArgb(0xFF, 0x9B, 0x59, 0xB6),   // violet
        Color.FromArgb(0xFF, 0x1A, 0xBC, 0x9C),   // teal
        Color.FromArgb(0xFF, 0xE6, 0x7E, 0x22),   // orange
        Color.FromArgb(0xFF, 0x7F, 0x8C, 0x8D),   // slate
    ];

    public FolderModel(GridCoordinate topLeft, int cells, string name = "Folder")
    {
        TopLeft     = topLeft;
        WidthCells  = cells;
        HeightCells = cells;
        Name        = name;
        BackgroundColor = Presets[0];
    }

    /// Convenience: is this folder backed by a real directory?
    public bool IsFilesystemFolder => DirectoryPath is not null;
}
