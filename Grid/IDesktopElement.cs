namespace CustomDesktop.Grid;

/// <summary>
/// Anything that can be placed on the grid: icons, folders, widgets.
/// Implemented in Phase 3 (icons) and Phase 4 (folders).
/// </summary>
internal interface IDesktopElement
{
    GridCoordinate TopLeft    { get; set; }
    int            WidthCells { get; }
    int            HeightCells { get; }
}
