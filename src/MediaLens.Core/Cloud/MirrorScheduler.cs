using MediaLens.Data;

namespace MediaLens.Cloud;

/// <summary>
/// Keeps the mirrors current: every few hours (and on request) each enabled mirror copies what is new.
/// One mirror at a time, so the upload does not choke the connection.
/// </summary>
public sealed class MirrorScheduler : IDisposable
{
    private readonly CloudService _cloud;
    private readonly Database _db;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private readonly HashSet<long> _requested = [];

    /// <summary>Text for the status bar; empty when nothing runs.</summary>
    public event Action<string>? StatusChanged;
    public event Action? MirrorsChanged;

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

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Runs a mirror now (or all of them with null).</summary>
    public void RunNow(long? mirrorId = null)
    {
        lock (_requested)
        {
            foreach (var m in _cloud.Mirrors().Where(m => mirrorId == null || m.Id == mirrorId))
                _requested.Add(m.Id);
        }
        _wake.Release();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var due = _cloud.Mirrors().Where(m => m.Enabled &&
                    (m.LastRunAt == null || now - m.LastRunAt > IntervalHours * 3600_000)).Select(m => m.Id).ToHashSet();
                lock (_requested)
                {
                    due.UnionWith(_requested);
                    _requested.Clear();
                }
                foreach (var id in due)
                {
                    var mirror = _cloud.Mirrors().FirstOrDefault(m => m.Id == id);
                    if (mirror == null) continue;
                    StatusChanged?.Invoke($"Mirroring {mirror.SourceName} to {mirror.CloudName}...");
                    try
                    {
                        await _cloud.MirrorAsync(id, new Progress<Rclone.CopyProgress>(p =>
                            StatusChanged?.Invoke($"Mirroring {mirror.SourceName} to {mirror.CloudName}: {p.Percent}% ({p.Text})")), ct);
                    }
                    catch (RcloneException)
                    {
                        // Recorded in the mirror's last result; tried again at the next interval.
                    }
                    MirrorsChanged?.Invoke();
                }
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
