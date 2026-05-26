using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Foundation;

namespace CustomDesktop.Grid;

/// <summary>
/// All user-configurable grid settings plus the values computed from them at runtime.
///
/// Design rules (from spec):
///   - Grid is 3x denser than the standard Windows icon grid.
///     Standard Windows grid ≈ 96 px per cell → our cell ≈ 32 px.
///     This emerges naturally from VerticalSlots = 20 on a 1080p display.
///   - Each element occupies DefaultIconCells × DefaultIconCells cells (default 3×3).
///   - Top and bottom edges have equal padding = EdgeMarginFraction × WorkAreaHeight.
///   - HorizontalSlots is computed, never stored — it adapts to any screen width.
/// </summary>
internal sealed class GridConfiguration
{
    // ── Persisted settings ─────────────────────────────────────────────────────

    /// Number of vertical grid slots. Controls density.
    /// Default 20 → ~32 px/cell on 1080p → 3x denser than standard 96 px Windows grid.
    public int VerticalSlots { get; set; } = 20;

    /// Equal top/bottom padding as a fraction of work-area height. Default = 1 %.
    public double EdgeMarginFraction { get; set; } = 0.01;

    /// Default side length of a desktop element in grid cells.
    /// Each icon/folder/widget occupies (DefaultIconCells × DefaultIconCells) cells.
    public int DefaultIconCells { get; set; } = 3;

    // ── Computed at runtime (never serialised) ─────────────────────────────────

    [JsonIgnore] public double CellSize        { get; private set; }
    [JsonIgnore] public int    HorizontalSlots { get; private set; }
    [JsonIgnore] public double EdgeMarginPx    { get; private set; }

    // ── Computation ────────────────────────────────────────────────────────────

    /// Call once when the window size is known (and again on resize).
    public void Compute(double workAreaWidth, double workAreaHeight)
    {
        EdgeMarginPx    = workAreaHeight * EdgeMarginFraction;
        double usable   = workAreaHeight - 2 * EdgeMarginPx;
        CellSize        = usable / VerticalSlots;
        HorizontalSlots = (int)Math.Floor(workAreaWidth / CellSize);
    }

    // ── Coordinate helpers ─────────────────────────────────────────────────────

    /// Grid cell → top-left pixel position on the canvas.
    public Point GridToPixel(GridCoordinate coord) =>
        new(coord.Col * CellSize,
            EdgeMarginPx + coord.Row * CellSize);

    /// Pixel position → snapped top-left grid cell for a DefaultIconCells block.
    /// Clamps so the block always fits fully within the grid.
    public GridCoordinate PixelToSnappedTopLeft(Point pixel)
    {
        int col = (int)Math.Floor(pixel.X / CellSize);
        int row = (int)Math.Floor((pixel.Y - EdgeMarginPx) / CellSize);

        col = Math.Clamp(col, 0, Math.Max(0, HorizontalSlots - DefaultIconCells));
        row = Math.Clamp(row, 0, Math.Max(0, VerticalSlots   - DefaultIconCells));

        return new GridCoordinate(col, row);
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    private static string ConfigPath =>
        Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            "grid.json");

    public static GridConfiguration Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<GridConfiguration>(json) ?? new();
            }
        }
        catch { /* fall through to defaults on any I/O or parse error */ }
        return new();
    }

    public void Save()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, options));
        }
        catch { /* non-critical — next save will retry */ }
    }
}
