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

    public static async Task<string> LocalPathAsync(MediaItem item, CancellationToken ct)
    {
        if (item.Remote == null) return item.FullPath;
        if (IsCached(item))
        {
            // Touch: the cache trims by last use.
            try { File.SetLastWriteTimeUtc(item.FullPath, DateTime.UtcNow); } catch (IOException) { }
            return item.FullPath;
        }
        if (_localTwin?.Invoke(item) is { } twin) return twin;
        if (_rclone == null) throw new InvalidOperationException("Cloud access is not configured");
        await Gate.WaitAsync(ct);
        try
        {
            if (!IsCached(item)) await _rclone.DownloadAsync(item.Remote, item.FullPath, ct);
        }
        finally
        {
            Gate.Release();
        }
        return item.FullPath;
    }

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
