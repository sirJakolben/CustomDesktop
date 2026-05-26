namespace CustomDesktop.Grid;

/// <summary>
/// Tracks which grid cells are occupied by which elements.
/// Each cell maps 1-to-1 to one element reference — multi-cell elements register
/// all their covered cells pointing to the same IDesktopElement.
/// </summary>
internal sealed class GridLayoutManager
{
    // Every occupied cell → the element that owns it.
    private readonly Dictionary<GridCoordinate, IDesktopElement> _cells = new();

    // ── Queries ────────────────────────────────────────────────────────────────

    public bool IsOccupied(GridCoordinate coord) => _cells.ContainsKey(coord);

    /// Returns true when ALL cells in the (widthCells × heightCells) block
    /// starting at topLeft are unoccupied.
    public bool CanPlace(GridCoordinate topLeft, int widthCells, int heightCells)
    {
        for (int r = topLeft.Row; r < topLeft.Row + heightCells; r++)
            for (int c = topLeft.Col; c < topLeft.Col + widthCells; c++)
                if (_cells.ContainsKey(new GridCoordinate(c, r)))
                    return false;
        return true;
    }

    // ── Mutations ──────────────────────────────────────────────────────────────

    /// Register all cells for an element. Throws if any cell is already taken.
    public void Place(IDesktopElement element)
    {
        for (int r = element.TopLeft.Row; r < element.TopLeft.Row + element.HeightCells; r++)
            for (int c = element.TopLeft.Col; c < element.TopLeft.Col + element.WidthCells; c++)
            {
                var coord = new GridCoordinate(c, r);
                if (_cells.ContainsKey(coord))
                    throw new InvalidOperationException(
                        $"Cell ({c},{r}) is already occupied.");
                _cells[coord] = element;
            }
    }

    /// Remove all cells registered for the given element.
    public void Remove(IDesktopElement element)
    {
        for (int r = element.TopLeft.Row; r < element.TopLeft.Row + element.HeightCells; r++)
            for (int c = element.TopLeft.Col; c < element.TopLeft.Col + element.WidthCells; c++)
                _cells.Remove(new GridCoordinate(c, r));
    }

    /// Move element from its current position to newTopLeft.
    /// Validates that the destination is clear (ignoring the element itself).
    public void Move(IDesktopElement element, GridCoordinate newTopLeft)
    {
        Remove(element);

        if (!CanPlace(newTopLeft, element.WidthCells, element.HeightCells))
        {
            Place(element); // roll back — element.TopLeft still holds the old position
            throw new InvalidOperationException("Target cells are occupied.");
        }

        element.TopLeft = newTopLeft;   // update position BEFORE re-registering cells
        Place(element);
    }
}
