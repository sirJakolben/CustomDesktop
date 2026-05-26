namespace CustomDesktop.Item;

/// <summary>
/// Enumerates the user's desktop items from both the personal and public
/// desktop folders — exactly the same sources Windows Explorer uses.
///
/// Also watches both folders for file-system changes and fires debounced
/// events so GridCanvas can react without implementing its own watchers.
/// </summary>
internal sealed class DesktopItemRepository : IDisposable
{
    private static readonly string[] _roots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    ];

    // ── Watcher events ─────────────────────────────────────────────────────────

    /// Fires when a new item appears on the desktop.
    internal event Action<string>? ItemCreated;

    /// Fires when an existing item is removed from the desktop.
    internal event Action<string>? ItemDeleted;

    /// Fires when an item is renamed (or moved within the same folder).
    internal event Action<string /*oldPath*/, string /*newPath*/>? ItemRenamed;

    // ── Internals ──────────────────────────────────────────────────────────────

    private readonly List<FileSystemWatcher>         _watchers = [];
    private readonly Dictionary<string, CancellationTokenSource> _debounce = new(
        StringComparer.OrdinalIgnoreCase);

    // Debounce window — prevents duplicate events from a single copy operation.
    private const int DebounceMs = 300;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// Returns full paths of all current desktop items, excluding desktop.ini.
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

    /// Starts watching both desktop folders.
    /// Must be called from the UI thread; events are posted via the given
    /// DispatcherQueue so handlers can safely update UI.
    internal void StartWatching(Microsoft.UI.Dispatching.DispatcherQueue dq)
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            var w = new FileSystemWatcher(root)
            {
                NotifyFilter         = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents  = true,
            };

            w.Created += (_, e) => DebouncePost(dq, e.FullPath, () =>
                ItemCreated?.Invoke(e.FullPath));

            w.Deleted += (_, e) => DebouncePost(dq, e.FullPath, () =>
                ItemDeleted?.Invoke(e.FullPath));

            w.Renamed += (_, e) => DebouncePost(dq, e.FullPath, () =>
                ItemRenamed?.Invoke(e.OldFullPath, e.FullPath));

            _watchers.Add(w);
        }
    }

    // ── Debounce ───────────────────────────────────────────────────────────────

    private void DebouncePost(Microsoft.UI.Dispatching.DispatcherQueue dq,
                               string key, Action action)
    {
        // Cancel any pending notification for the same path
        lock (_debounce)
        {
            if (_debounce.TryGetValue(key, out var existing))
                existing.Cancel();

            var cts = new CancellationTokenSource();
            _debounce[key] = cts;
            var token = cts.Token;

            Task.Delay(DebounceMs, token).ContinueWith(_ =>
            {
                if (token.IsCancellationRequested) return;
                lock (_debounce) _debounce.Remove(key);
                dq.TryEnqueue(() => action());
            }, TaskContinuationOptions.None);
        }
    }

    // ── Cleanup ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        lock (_debounce)
        {
            foreach (var cts in _debounce.Values) cts.Cancel();
            _debounce.Clear();
        }
    }
}
