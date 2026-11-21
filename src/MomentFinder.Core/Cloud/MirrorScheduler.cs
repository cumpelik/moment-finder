using MomentFinder.Data;

namespace MomentFinder.Cloud;

/// <summary>
/// Keeps the backups of folders and of the library current: after a change in a folder, every hour and on request
/// each backed up folder is compared with its copy in the cloud, every few hours the library itself goes to its cloud. One at a time, so the upload does not choke the connection.
/// </summary>
public sealed class MirrorScheduler : IDisposable
{
    private readonly CloudService _cloud;
    private readonly Database _db;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private readonly HashSet<long> _requested = [];
    private bool _backupRequested;

    /// <summary>Text for the status bar; empty when nothing runs.</summary>
    public event Action<string>? StatusChanged;
    public event Action? MirrorsChanged;
    /// <summary>
    /// How far the copy of a folder is: a clone shows it at its place in the sidebar, a folder being added to a cloud
    /// folder at that one. The key is MirrorKey or MergeKey.
    /// </summary>
    public event Action<string, Rclone.CopyProgress>? Copying;

    public static string MirrorKey(long mirrorId) => $"mirror:{mirrorId}";
    public static string MergeKey(long sourceId) => $"merge:{sourceId}";
    /// <summary>A backup found the folder changed, or a folder cloned to the cloud: the library scans again.</summary>
    public event Action? FoldersChanged;

    public MirrorScheduler(CloudService cloud, Database db)
    {
        _cloud = cloud;
        _db = db;
    }

    public double IntervalHours
    {
        get => double.TryParse(_db.GetSetting("mirror_hours"), System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 6;
        set => _db.SetSetting("mirror_hours", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Folders are compared with the cloud every hour: a listing, cheap when nothing changed.</summary>
    public const double FolderHours = 1;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Runs a mirror now (or all of them with null), also a stopped one.</summary>
    public void RunNow(long? mirrorId = null)
    {
        lock (_requested)
        {
            foreach (var m in _cloud.Mirrors().Where(m => mirrorId == null || m.Id == mirrorId))
                _requested.Add(m.Id);
        }
        _wake.Release();
    }

    private string? _runningKey;
    private CancellationTokenSource? _running;

    /// <summary>Stops the copy of this mirror if it runs now (a clone cancelled, a backup stopped).</summary>
    public void Cancel(long mirrorId)
    {
        lock (_requested)
        {
            _requested.Remove(mirrorId);
            if (_runningKey == MirrorKey(mirrorId)) _running?.Cancel();
        }
    }

    /// <summary>Stops adding this folder to a cloud folder if it runs now.</summary>
    public void CancelMerge(long sourceId)
    {
        lock (_requested)
        {
            if (_runningKey == MergeKey(sourceId)) _running?.Cancel();
        }
    }

    /// <summary>A folder waits to be added to a cloud folder: started now, not at the next round.</summary>
    public void MergeNow() => _wake.Release();

    /// <summary>A folder changed on this computer: its backup follows (not a stopped one, another computer has it).</summary>
    public void FolderChanged(long sourceId)
    {
        if (_cloud.Mirrors().FirstOrDefault(m => m.SourceId == sourceId && m.Enabled) is { } mirror) RunNow(mirror.Id);
        // Being added to a cloud folder (perhaps waiting for its disk): it goes on now.
        if (_cloud.Merges().Any(j => j.SourceId == sourceId)) _wake.Release();
    }

    private volatile bool _paused;

    /// <summary>
    /// Nothing goes to the cloud while paused (the pause button of the status bar pauses all background work): what
    /// runs stops at once and starts again after the pause.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value)
                lock (_requested)
                    _running?.Cancel();
            else _wake.Release();
        }
    }

    /// <summary>Backs the library up now.</summary>
    public void BackupNow()
    {
        _backupRequested = true;
        _wake.Release();
    }

    private async Task BackupAsync(CancellationToken ct)
    {
        if (_cloud.BackupCloud is not { } cloud || !Rclone.IsAvailable) return;
        long.TryParse(_db.GetSetting("backup_tried_at"), out var tried);
        var last = Math.Max(tried, _cloud.Backup.LastAt?.ToUnixTimeMilliseconds() ?? 0);
        var due = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last > IntervalHours * 3600_000;
        if (!due && !_backupRequested) return;
        StatusChanged?.Invoke($"Backing up the library to {cloud.Name}...");
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_requested)
        {
            _runningKey = "backup";
            _running = run;
        }
        try
        {
            await _cloud.Backup.RunAsync(cloud, full: true,
                new Progress<string>(what => StatusChanged?.Invoke($"Backing up the library to {cloud.Name}: {what}")), run.Token);
            _backupRequested = false;
        }
        catch (RcloneException)
        {
            // Recorded as the backup's last result; tried again at the next interval, not every ten minutes.
            _backupRequested = false;
            _db.SetSetting("backup_tried_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Paused: it runs again after the pause.
        }
        finally
        {
            lock (_requested)
            {
                _runningKey = null;
                _running = null;
            }
        }
        MirrorsChanged?.Invoke();
    }

    /// <summary>Folders waiting to be added to a cloud folder; one that cannot go on now (a disk away) waits for the next round.</summary>
    private async Task MergesAsync(CancellationToken ct)
    {
        foreach (var job in _cloud.Merges())
        {
            if (_paused) return;
            var key = MergeKey(job.SourceId);
            StatusChanged?.Invoke($"Adding {job.SourceName} to {job.TargetName}...");
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_requested)
            {
                _runningKey = key;
                _running = run;
            }
            try
            {
                await _cloud.MergeAsync(job.SourceId, new Progress<Rclone.CopyProgress>(p =>
                {
                    StatusChanged?.Invoke($"Adding {job.SourceName} to {job.TargetName}: {p.Percent}% ({p.Text})");
                    Copying?.Invoke(key, p);
                }), run.Token);
                FoldersChanged?.Invoke();
            }
            catch (Exception e) when (e is RcloneException or InvalidOperationException or IOException)
            {
                // Recorded in the job's last result; tried again at the next round.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Stopped by the user.
            }
            finally
            {
                lock (_requested)
                {
                    _runningKey = null;
                    _running = null;
                }
            }
            MirrorsChanged?.Invoke();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_paused)
                {
                    StatusChanged?.Invoke("");
                    await _wake.WaitAsync(TimeSpan.FromMinutes(10), ct);
                    continue;
                }
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var due = _cloud.Mirrors().Where(m => m.Enabled &&
                    (m.LastRunAt == null || now - m.LastRunAt > FolderHours * 3600_000)).Select(m => m.Id).ToHashSet();
                lock (_requested)
                {
                    due.UnionWith(_requested);
                    _requested.Clear();
                }
                foreach (var id in due)
                {
                    if (_paused)
                    {
                        // Asked for: it runs after the pause.
                        lock (_requested) _requested.Add(id);
                        continue;
                    }
                    var mirror = _cloud.Mirrors().FirstOrDefault(m => m.Id == id);
                    if (mirror == null) continue;
                    var doing = _cloud.MoveRequested(id) ? "Cloning" : "Backing up";
                    StatusChanged?.Invoke($"{doing} {mirror.SourceName} to {mirror.CloudName}...");
                    using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    lock (_requested)
                    {
                        _runningKey = MirrorKey(id);
                        _running = run;
                    }
                    try
                    {
                        var result = await _cloud.MirrorAsync(id, new Progress<Rclone.CopyProgress>(p =>
                        {
                            StatusChanged?.Invoke($"{doing} {mirror.SourceName} to {mirror.CloudName}: {p.Percent}% ({p.Text})");
                            Copying?.Invoke(MirrorKey(id), p);
                        }), run.Token);
                        if (result.Moved + result.Gone > 0) FoldersChanged?.Invoke();
                        // All of it is in the cloud now: the folder moves there, if the user asked for it.
                        if (_cloud.MoveRequested(id) && _cloud.Mirrors().FirstOrDefault(m => m.Id == id) is { Gone: 0 })
                        {
                            StatusChanged?.Invoke($"Cloning {mirror.SourceName} to {mirror.CloudName}...");
                            await _cloud.MoveToCloudAsync(id, null, run.Token);
                            _cloud.RequestMoveToCloud(id, false);
                            FoldersChanged?.Invoke();
                        }
                    }
                    catch (Exception e) when (e is RcloneException or InvalidOperationException)
                    {
                        // Recorded in the mirror's last result; tried again at the next interval.
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Cancelled by the user: the next mirror goes on.
                    }
                    finally
                    {
                        lock (_requested)
                        {
                            _runningKey = null;
                            _running = null;
                        }
                    }
                    MirrorsChanged?.Invoke();
                }
                await MergesAsync(ct);
                if (!_paused) await BackupAsync(ct);
                StatusChanged?.Invoke("");
                await _wake.WaitAsync(TimeSpan.FromMinutes(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
    }
}
