namespace CustomDesktop.Item;

/// <summary>
/// Enumerates the user's desktop items from both the personal and public
/// desktop folders — exactly the same sources Windows Explorer uses.
/// </summary>
internal sealed class DesktopItemRepository
{
    // Both locations merged, just like the real Windows desktop.
    private static readonly string[] _roots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    ];

    /// Returns full paths of all desktop items, excluding desktop.ini.
    public IReadOnlyList<string> GetPaths()
    {
        var list = new List<string>();

        foreach (var dir in _roots.Where(Directory.Exists))
        {
            list.AddRange(
                Directory.GetFiles(dir)
                    .Where(f => !f.EndsWith("desktop.ini",
                                    StringComparison.OrdinalIgnoreCase)));
            list.AddRange(Directory.GetDirectories(dir));
        }

        return list.AsReadOnly();
    }
}
