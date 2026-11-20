using MediaLens.Library;

namespace MediaLens.Media;

/// <summary>
/// The playing copies of 360 videos (see FFmpeg.Make360ProxyAsync): made in the background after the analysis, or
/// right away when a video is opened first; kept up to a size limit, the least recently played go first.
/// </summary>
public static class Proxy360
{
    private const long CacheLimit = 20L << 30;
    private static readonly Dictionary<string, Task<string?>> Running = [];

    public static string? Dir { get; set; }

    private static string? PathOf(MediaItem item)
    {
        if (Dir == null || !File.Exists(item.FullPath)) return null;
        var info = new FileInfo(item.FullPath);
        return System.IO.Path.Combine(Dir, $"{item.Id}_{info.Length}_{info.LastWriteTimeUtc.Ticks}.mp4");
    }

    /// <summary>The copy if it is there already.</summary>
    public static string? Ready(MediaItem item)
    {
        if (PathOf(item) is not { } path || !File.Exists(path)) return null;
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
        return path;
    }

    public static bool HasRoom => Dir == null || !Directory.Exists(Dir) ||
        new DirectoryInfo(Dir).GetFiles("*.mp4").Sum(f => f.Length) < CacheLimit * 9 / 10;

    /// <summary>The copy, made if needed (about the video's length with a graphics card, a few times that without).</summary>
    public static Task<string?> GetAsync(MediaItem item, CancellationToken ct = default)
    {
        if (PathOf(item) is not { } target) return Task.FromResult<string?>(null);
        lock (Running)
        {
            if (File.Exists(target)) return Task.FromResult<string?>(Ready(item));
            if (Running.TryGetValue(target, out var running)) return running;
            var task = Task.Run(async () =>
            {
                try
                {
                    var ok = await FFmpeg.Make360ProxyAsync(item.FullPath, target, ct);
                    Trim();
                    return ok ? target : null;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"360 copy of {item.FileName} failed: {e.Message}");
                    return null;
                }
                finally
                {
                    lock (Running) Running.Remove(target);
                }
            }, CancellationToken.None);
            Running[target] = task;
            return task;
        }
    }

    private static void Trim()
    {
        if (Dir == null || !Directory.Exists(Dir)) return;
        long total = 0;
        foreach (var f in new DirectoryInfo(Dir).GetFiles("*.mp4").OrderByDescending(f => f.LastAccessTimeUtc))
        {
            total += f.Length;
            if (total <= CacheLimit) continue;
            try { f.Delete(); } catch (IOException) { }
        }
    }
}
