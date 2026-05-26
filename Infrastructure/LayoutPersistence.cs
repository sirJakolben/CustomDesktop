using System.Text.Json;
using System.Text.Json.Serialization;
using CustomDesktop.Folder;
using CustomDesktop.Grid;
using CustomDesktop.Item;
using Windows.UI;
// ReSharper disable UnusedTypeParameter

namespace CustomDesktop.Infrastructure;

/// <summary>
/// Persists and restores the desktop layout: item positions and folder state.
/// Stored in LocalFolder/layout.json alongside grid.json.
/// On load, paths that no longer exist on disk are silently dropped.
/// </summary>
internal static class LayoutPersistence
{
    // ── DTOs ───────────────────────────────────────────────────────────────────

    internal sealed class PersistedItem
    {
        public string Path { get; set; } = string.Empty;
        public int    Col  { get; set; }
        public int    Row  { get; set; }
    }

    internal sealed class PersistedFolder
    {
        public string        Name      { get; set; } = "Folder";
        public byte          ColorA    { get; set; } = 0xFF;
        public byte          ColorR    { get; set; }
        public byte          ColorG    { get; set; }
        public byte          ColorB    { get; set; }
        public int           Col       { get; set; }
        public int           Row       { get; set; }
        public List<string>  ItemPaths { get; set; } = [];
    }

    internal sealed class LayoutData
    {
        public int                  SchemaVersion { get; set; } = 1;
        public List<PersistedItem>  Items         { get; set; } = [];
        public List<PersistedFolder> Folders      { get; set; } = [];
    }

    // ── Path ───────────────────────────────────────────────────────────────────

    private static string LayoutPath =>
        System.IO.Path.Combine(
            Windows.Storage.ApplicationData.Current.LocalFolder.Path,
            "layout.json");

    private static readonly JsonSerializerOptions _options =
        new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ── Save ───────────────────────────────────────────────────────────────────

    internal static void Save<TItemControl, TFolderControl>(
        IEnumerable<(DesktopItemElement Element, TItemControl _Control)> items,
        IEnumerable<(FolderModel Model, TFolderControl _Control)> folders)
    {
        try
        {
            var data = new LayoutData
            {
                Items = items.Select(i => new PersistedItem
                {
                    Path = i.Element.Path,
                    Col  = i.Element.TopLeft.Col,
                    Row  = i.Element.TopLeft.Row,
                }).ToList(),

                Folders = folders.Select(f => new PersistedFolder
                {
                    Name      = f.Model.Name,
                    ColorA    = f.Model.BackgroundColor.A,
                    ColorR    = f.Model.BackgroundColor.R,
                    ColorG    = f.Model.BackgroundColor.G,
                    ColorB    = f.Model.BackgroundColor.B,
                    Col       = f.Model.TopLeft.Col,
                    Row       = f.Model.TopLeft.Row,
                    ItemPaths = [.. f.Model.ItemPaths],
                }).ToList(),
            };

            System.IO.File.WriteAllText(LayoutPath, JsonSerializer.Serialize(data, _options));
        }
        catch { /* non-critical */ }
    }

    // ── Load ───────────────────────────────────────────────────────────────────

    internal static LayoutData? TryLoad()
    {
        try
        {
            if (!System.IO.File.Exists(LayoutPath)) return null;
            var json = System.IO.File.ReadAllText(LayoutPath);
            var data = JsonSerializer.Deserialize<LayoutData>(json, _options);
            if (data is null) return null;

            // Strip items where the file/folder no longer exists on disk.
            data.Items   = data.Items  .Where(i => System.IO.Path.Exists(i.Path)).ToList();
            data.Folders = data.Folders
                .Select(f =>
                {
                    f.ItemPaths = f.ItemPaths.Where(System.IO.Path.Exists).ToList();
                    return f;
                })
                .Where(f => f.ItemPaths.Count >= 2)  // dissolve under-populated folders
                .ToList();

            return data;
        }
        catch { return null; }
    }

    // ── Helpers to reconstruct domain objects ──────────────────────────────────

    internal static DesktopItemElement ToElement(PersistedItem p, int cells)
        => new(p.Path, new GridCoordinate(p.Col, p.Row), cells);

    internal static FolderModel ToFolder(PersistedFolder p, int cells)
    {
        var f = new FolderModel(new GridCoordinate(p.Col, p.Row), cells, p.Name)
        {
            BackgroundColor = Color.FromArgb(p.ColorA, p.ColorR, p.ColorG, p.ColorB),
        };
        f.ItemPaths.AddRange(p.ItemPaths);
        return f;
    }
}
