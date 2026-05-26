namespace CustomDesktop.Grid;

/// <summary>
/// Identifies a single cell in the grid by column and row index (zero-based).
/// Used as the canonical key in GridLayoutManager.
/// </summary>
internal readonly record struct GridCoordinate(int Col, int Row);
