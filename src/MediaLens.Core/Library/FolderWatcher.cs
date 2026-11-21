namespace MediaLens.Library;

/// <summary>
/// Watches the folders of the library that are on this computer. Once a folder has been quiet for a while after a
/// change (files copied in, moved, renamed, deleted), it says so once: the folder is scanned and its backup follows.
/// Waiting for quiet keeps a copy in progress from being read half done. A folder that appears (a disk connected)
/// counts as changed. What a watcher can miss (a full buffer, some network folders) the hourly comparison finds.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMinutes(2);

    private readonly MediaRepository _repo;
    private readonly TimeSpan _quiet;
    private readonly Dictionary<long, Watch> _watches = [];
    /// <summary>Folders known to be away: when one comes back, it is scanned.</summary>
    private readonly HashSet<long> _away = [];
    private readonly Timer _look;
    private bool _disposed;

    /// <summary>A folder (by its source id) changed and is quiet now. Raised on a background thread.</summary>
    public event Action<long>? FolderChanged;

    public FolderWatcher(MediaRepository repo, TimeSpan? quiet = null, TimeSpan? lookEvery = null)
    {
        _repo = repo;
        _quiet = quiet ?? DefaultQuiet;
        var every = lookEvery ?? TimeSpan.FromMinutes(1);
        _look = new Timer(_ => Refresh(), null, every, every);
        Refresh();
    }

    /// <summary>Follows the sources: a folder added, removed, located elsewhere, connected or disconnected.</summary>
    public void Refresh()
    {
        List<Source> sources;
        try
        {
            sources = _repo.ListSources().Where(s => s.Enabled && s.Kind == SourceKinds.Local).ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            return;
        }
        var present = sources.Select(s => (s.Id, s.Root, Here: Directory.Exists(s.Root))).ToList();
        lock (_watches)
        {
            if (_disposed) return;
            foreach (var id in _watches.Keys.Where(id => !present.Any(p => p.Id == id && p.Here)).ToList())
            {
                _watches[id].Dispose();
                _watches.Remove(id);
            }
            _away.IntersectWith(present.Select(p => p.Id));
            foreach (var (id, root, here) in present)
            {
                if (!here)
                {
                    _away.Add(id);
                    continue;
                }
                if (_watches.TryGetValue(id, out var watch) && watch.Root == root && !watch.Broken) continue;
                if (watch != null)
                {
                    watch.Dispose();
                    _watches.Remove(id);
                }
                var fresh = new Watch(this, id, root);
                _watches[id] = fresh;
                // Connected again: what changed meanwhile is read. A folder here from the start is scanned at start.
                if (_away.Remove(id)) fresh.Touch();
            }
        }
    }

    private void Changed(long id) => FolderChanged?.Invoke(id);

    public void Dispose()
    {
        lock (_watches)
        {
            _disposed = true;
            _look.Dispose();
            foreach (var w in _watches.Values) w.Dispose();
            _watches.Clear();
        }
    }

    private sealed class Watch : IDisposable
    {
        private readonly FolderWatcher _owner;
        private readonly long _id;
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _quiet;

        public string Root { get; }
        /// <summary>The watcher gave up (or never started): made again at the next look.</summary>
        public bool Broken { get; private set; }

        public Watch(FolderWatcher owner, long id, string root)
        {
            _owner = owner;
            _id = id;
            Root = root;
            _quiet = new Timer(_ => _owner.Changed(_id));
            try
            {
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                _watcher.Created += OnEvent;
                _watcher.Changed += OnEvent;
                _watcher.Deleted += OnEvent;
                _watcher.Renamed += (_, e) =>
                {
                    if (Counts(e.OldFullPath) || Counts(e.FullPath)) Touch();
                };
                // A full buffer or the folder gone: something changed, and the watcher is made again.
                _watcher.Error += (_, _) =>
                {
                    Broken = true;
                    Touch();
                };
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Too many folders watched (Linux) or a folder that cannot be watched: the hourly comparison stays.
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            if (Counts(e.FullPath)) Touch();
        }

        /// <summary>Hidden and system parts (dot files, the recycle bin, NAS thumbnails) are no change of the library.</summary>
        private bool Counts(string fullPath)
        {
            var rel = Path.GetRelativePath(Root, fullPath).Replace('\\', '/');
            return rel != "." && !LibraryScanner.IsIgnored(rel);
        }

        /// <summary>Each change starts the wait for quiet again.</summary>
        public void Touch() => _quiet.Change(_owner._quiet, Timeout.InfiniteTimeSpan);

        public void Dispose()
        {
            _watcher?.Dispose();
            _quiet.Dispose();
        }
    }
}
