using MomentFinder.Library;

namespace MomentFinder.Media;

/// <summary>
/// The playing copies of 360 videos (see FFmpeg.Make360ProxyAsync): made in the background after the analysis, or
/// right away when a video is opened first; kept up to a size limit, the least recently played go first. A copy being
/// made shows its progress and can be stopped.
/// </summary>
public static class Proxy360
{
    private const long CacheLimit = 20L << 30;

    private sealed class Job
    {
        public required Task<string?> Task;
        public required CancellationTokenSource Cancel;
        public double Progress;
        public DateTime Started = DateTime.UtcNow;
    }

    private static readonly Dictionary<string, Job> Running = [];

    public static string? Dir { get; set; }

    private static string? PathOf(MediaItem item)
    {
        if (Dir == null || !File.Exists(item.FullPath)) return null;
        var info = new FileInfo(item.FullPath);
        return Path.Combine(Dir, $"{item.Id}_{info.Length}_{info.LastWriteTimeUtc.Ticks}_e2.mp4");
    }

    /// <summary>The copy if it is there already.</summary>
    public static string? Ready(MediaItem item)
    {
        if (PathOf(item) is not { } path || !File.Exists(path)) return null;
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
        return path;
    }

    /// <summary>A copy started or ended (made, stopped, failed): what the thumbnails say about it may have changed.</summary>
    public static event Action? Changed;

    /// <summary>For the thumbnail's badge: null no copy yet, false being made now, true ready to play smoothly.</summary>
    public static bool? State(MediaItem item)
    {
        if (PathOf(item) is not { } path) return null;
        if (File.Exists(path)) return true;
        lock (Running) return Running.ContainsKey(path) ? false : null;
    }

    public static bool HasRoom => Dir == null || !Directory.Exists(Dir) ||
        new DirectoryInfo(Dir).GetFiles("*.mp4").Sum(f => f.Length) < CacheLimit * 9 / 10;

    /// <summary>How far the copy of this item is (0..1) and the time left, while it is being made.</summary>
    public static (double Done, TimeSpan? Left)? Progress(MediaItem item)
    {
        if (PathOf(item) is not { } target) return null;
        lock (Running)
        {
            if (!Running.TryGetValue(target, out var job)) return null;
            var elapsed = DateTime.UtcNow - job.Started;
            TimeSpan? left = job.Progress > 0.02 && elapsed.TotalSeconds > 3
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - job.Progress) / job.Progress) : null;
            return (job.Progress, left);
        }
    }

    /// <summary>Stops making this copy (the user does not want to wait, or the background work is paused).</summary>
    public static void Stop(MediaItem item)
    {
        if (PathOf(item) is not { } target) return;
        lock (Running)
            if (Running.TryGetValue(target, out var job)) job.Cancel.Cancel();
    }

    /// <summary>The copy, made if needed; null when it cannot be made or was stopped.</summary>
    public static Task<string?> GetAsync(MediaItem item, CancellationToken ct = default)
    {
        if (PathOf(item) is not { } target) return Task.FromResult<string?>(null);
        lock (Running)
        {
            if (File.Exists(target)) return Task.FromResult(Ready(item));
            if (Running.TryGetValue(target, out var running))
            {
                ct.Register(running.Cancel.Cancel);
                return running.Task;
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Job? job = null;
            var progress = new Progress<double>(p => { if (job != null) job.Progress = p; });
            var task = Task.Run(async () =>
            {
                try
                {
                    var model = Fisheye360.ModelOf(item.FullPath);
                    var motion = Fisheye360.MotionOf(item.FullPath);
                    var seconds = (item.DurationMs ?? 0) / 1000.0;
                    var fps = await FFmpeg.FrameRateAsync(item.FullPath, cts.Token);
                    var ok = await FFmpeg.Make360ProxyAsync(item.FullPath, target, (int)(seconds * fps), fps,
                        (frame, lenses, picture) =>
                        {
                            var at = frame / fps;
                            Fisheye360.RenderEquirect(lenses, FFmpeg.Proxy360Lens, picture, FFmpeg.Proxy360Width, FFmpeg.Proxy360Height,
                                model, motion?.LensFromLevel(at) ?? Rot3.Identity);
                        }, progress, cts.Token);
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
                    cts.Dispose();
                    Changed?.Invoke();
                }
            }, CancellationToken.None);
            job = new Job { Task = task, Cancel = cts };
            Running[target] = job;
            Changed?.Invoke();
            return task;
        }
    }

    private static void Trim()
    {
        if (Dir == null || !Directory.Exists(Dir)) return;
        // Copies of an earlier kind (both lenses side by side) are not played any more.
        foreach (var old in new DirectoryInfo(Dir).GetFiles("*.mp4").Where(f => !f.Name.EndsWith("_e2.mp4", StringComparison.Ordinal)))
            try { old.Delete(); } catch (IOException) { }
        long total = 0;
        foreach (var f in new DirectoryInfo(Dir).GetFiles("*.mp4").OrderByDescending(f => f.LastAccessTimeUtc))
        {
            total += f.Length;
            if (total <= CacheLimit) continue;
            try { f.Delete(); } catch (IOException) { }
        }
    }
}
