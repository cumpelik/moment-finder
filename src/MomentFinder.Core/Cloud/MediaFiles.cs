using MomentFinder.Library;

namespace MomentFinder.Cloud;

/// <summary>
/// The way to the bytes of an item. A local file is just its path; a cloud item is fetched into the local
/// cache the first time it is needed (thumbnail, analysis, viewing, export) and kept while the cache has room.
/// A cloud item whose twin (the same file) is in a folder on this computer is read from there: a folder and its
/// clone in the cloud, or a folder added to a cloud folder, are not downloaded again.
/// </summary>
public static class MediaFiles
{
    private static Rclone? _rclone;
    private static string? _cacheDir;
    private static readonly SemaphoreSlim Gate = new(3);
    /// <summary>Somebody waits for these (the viewer): they do not queue behind the background work.</summary>
    private static readonly SemaphoreSlim WaitedGate = new(3);
    /// <summary>Files being fetched now: a second request for one waits for the same download.</summary>
    private static readonly Dictionary<string, Task> Fetching = [];

    private static Func<MediaItem, string?>? _localTwin;

    public static void Configure(Rclone rclone, string cacheDir, Func<MediaItem, string?>? localTwin = null)
    {
        _rclone = rclone;
        _cacheDir = cacheDir;
        _localTwin = localTwin;
    }

    /// <summary>Gigabytes the cloud cache may use; the least recently used files go first.</summary>
    public static double CacheLimitGb { get; set; } = 10;

    public static bool IsLocal(MediaItem item) => item.Remote == null || IsCached(item);

    private static bool IsCached(MediaItem item) =>
        new FileInfo(item.FullPath) is { Exists: true } fi && fi.Length == item.Size;

    /// <summary>
    /// Where the item's bytes are on this computer right now: its own file, the copy fetched from the cloud, or its
    /// twin in a local folder. Null when they would have to be downloaded (nothing is downloaded here).
    /// </summary>
    public static string? LocalNow(MediaItem item)
    {
        if (item.Remote == null) return item.FullPath;
        if (IsCached(item))
        {
            // Touch: the cache trims by last use.
            try { File.SetLastWriteTimeUtc(item.FullPath, DateTime.UtcNow); } catch (IOException) { }
            return item.FullPath;
        }
        return _localTwin?.Invoke(item);
    }

    /// <summary>
    /// The local path of the item's bytes, downloaded first when needed. Waited: somebody is looking at the screen
    /// for it (the viewer), so it does not wait for the downloads of the background analysis.
    /// </summary>
    public static async Task<string> LocalPathAsync(MediaItem item, CancellationToken ct, bool waited = false,
        IProgress<Rclone.CopyProgress>? progress = null)
    {
        if (LocalNow(item) is { } here) return here;
        if (_rclone == null) throw new InvalidOperationException("Cloud access is not configured");
        var gate = waited ? WaitedGate : Gate;
        await gate.WaitAsync(ct);
        try
        {
            var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            while (true)
            {
                Task? running;
                lock (Fetching)
                    if (!Fetching.TryAdd(item.FullPath, mine.Task)) running = Fetching[item.FullPath];
                    else running = null;
                if (running == null) break;
                // Already on its way (the analysis fetches it): that download, not a second one into the same file.
                await running.WaitAsync(ct);
                if (IsCached(item)) return item.FullPath;
            }
            try
            {
                if (!IsCached(item))
                {
                    // Somebody waits: several connections at once. The background keeps to one each, so it does
                    // not take the line from the viewer.
                    await _rclone.DownloadAsync(item.Remote!, item.FullPath, progress, ct, waited ? 8 : 1);
                    Fetched?.Invoke(item.Id);
                }
            }
            finally
            {
                lock (Fetching) Fetching.Remove(item.FullPath);
                mine.SetResult();
            }
        }
        finally
        {
            gate.Release();
        }
        return item.FullPath;
    }

    /// <summary>An item of a cloud source is on this computer now (its thumbnail says so); raised on a background thread.</summary>
    public static event Action<long>? Fetched;

    /// <summary>Deletes the least recently used cached cloud files above the limit.</summary>
    public static void TrimCache()
    {
        if (_cacheDir == null || !Directory.Exists(_cacheDir)) return;
        var limit = (long)(CacheLimitGb * 1024 * 1024 * 1024);
        var files = new DirectoryInfo(_cacheDir).EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
            if (total <= limit) continue;
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
