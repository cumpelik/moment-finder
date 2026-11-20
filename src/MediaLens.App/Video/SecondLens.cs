using MediaLens.Library;
using MediaLens.Media;

namespace MediaLens.App.Video;

/// <summary>
/// The second lens of 360 videos as files of their own (see FFmpeg.CopyVideoStreamAsync), made when a video is
/// first opened and kept in a size-limited cache; the least recently used go first.
/// </summary>
public static class SecondLens
{
    private const long CacheLimit = 8L << 30;
    private static readonly Dictionary<string, Task<string?>> Running = [];

    public static string? CacheDir { get; set; }

    /// <summary>The file with the second lens, made if needed (several seconds for a long video).</summary>
    public static Task<string?> GetAsync(MediaItem item, string source)
    {
        if (CacheDir == null) return Task.FromResult<string?>(null);
        var info = new FileInfo(source);
        var target = Path.Combine(CacheDir, $"{item.Id}_{info.Length}_{info.LastWriteTimeUtc.Ticks}.mp4");
        lock (Running)
        {
            if (File.Exists(target))
            {
                File.SetLastAccessTimeUtc(target, DateTime.UtcNow);
                return Task.FromResult<string?>(target);
            }
            if (Running.TryGetValue(target, out var running)) return running;
            var task = Task.Run(async () =>
            {
                try
                {
                    var ok = await FFmpeg.CopyVideoStreamAsync(source, 1, target);
                    Trim();
                    return ok ? target : null;
                }
                finally
                {
                    lock (Running) Running.Remove(target);
                }
            });
            Running[target] = task;
            return task;
        }
    }

    private static void Trim()
    {
        if (CacheDir == null || !Directory.Exists(CacheDir)) return;
        var files = new DirectoryInfo(CacheDir).GetFiles("*.mp4").OrderByDescending(f => f.LastAccessTimeUtc).ToList();
        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
            if (total <= CacheLimit) continue;
            try { f.Delete(); } catch (IOException) { }
        }
    }
}
