using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using MomentFinder.Cloud;
using MomentFinder.Data;

namespace MomentFinder.Sync;

/// <summary>
/// One library on several computers at once. Each computer sends its changes as files of its own into the
/// library's cloud and takes in the files of the others; nobody writes into another's file, so two computers can
/// never overwrite each other. Of two changes of one thing the later counts (FactStore).
/// <code>
/// .medialens/shared/journal/&lt;computer&gt;/&lt;time&gt;.jsonl.gz   the changes of one computer, one file per sending
/// .medialens/shared/devices/&lt;computer&gt;.json              which computers use the library, and when last
/// </code>
/// </summary>
public sealed class LibrarySync : IDisposable
{
    public const string SharedSetting = "shared_library";

    private readonly Database _db;
    private readonly CloudService _cloud;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _round = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private DateTime _deviceWritten;

    public LibrarySync(Database db, CloudService cloud, AppPaths paths)
    {
        _db = db;
        _cloud = cloud;
        _paths = paths;
        Content = new SharedContent(db, cloud, paths, () => Enabled && _cloud.BackupCloud is { } c ? Root(_cloud, c) : null);
    }

    /// <summary>Thumbnails and search data, computed once for all the computers.</summary>
    public SharedContent Content { get; }

    /// <summary>The library is shared through its cloud.</summary>
    public bool Enabled => _db.GetSetting(SharedSetting) == "1" && _cloud.BackupCloud != null;

    /// <summary>Changes of another computer came in: names, buckets, who is who (the views read again).</summary>
    public event Action? Changed;
    /// <summary>A folder came from another computer or went: it is read.</summary>
    public event Action? FoldersChanged;
    /// <summary>How the last round went, in words; raised on a background thread.</summary>
    public event Action<string>? StatusChanged;

    public DateTimeOffset? LastSync { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Nothing goes out or comes in while paused (the pause button pauses all background work).</summary>
    public bool Paused { get; set; }

    private static string Root(CloudService cloud, CloudInfo c) => CloudService.Join(cloud.BackupRoot(c), "shared");

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>A change was made here: it goes out soon, not at the next minute.</summary>
    public void Wake() => _wake.Release();

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Paused && Enabled) await RunAsync(ct);
                // A change made here wakes it; a little later, so a burst of clicks goes as one file.
                if (await _wake.WaitAsync(TimeSpan.FromSeconds(60), ct)) await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>One round: what was changed here goes out, what the others changed comes in.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_cloud.BackupCloud is not { } cloud) return;
        await _round.WaitAsync(ct);
        try
        {
            var root = Root(_cloud, cloud);
            await PushAsync(root, ct);
            // What this computer computed for shared photos goes up; the facts saying so go out with the next changes.
            if (await Content.GiveAsync(ct) > 0) await PushAsync(root, ct);
            var taken = await PullAsync(root, ct);
            if (DateTime.UtcNow - _deviceWritten > TimeSpan.FromMinutes(10))
            {
                await WriteDeviceAsync(root, ct);
                await ExchangeRemotesAsync(cloud, ct);
            }
            // A summary now and then: a computer that joins starts from it, not from every change ever made.
            if (!long.TryParse(_db.GetSetting("snapshot_at"), out var at) ||
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - at > TimeSpan.FromDays(7).TotalMilliseconds)
                await WriteSnapshotAsync(root, ct);
            LastSync = DateTimeOffset.UtcNow;
            LastError = null;
            StatusChanged?.Invoke(taken > 0 ? $"Took in {taken:N0} changes from the other computers" : "");
        }
        catch (Exception e) when (e is RcloneException or IOException or JsonException)
        {
            LastError = e.Message;
            StatusChanged?.Invoke("The changes could not be exchanged with the other computers: " + e.Message);
        }
        finally
        {
            _round.Release();
        }
    }

    // ---- out ----

    private sealed record Line(string O, string F, string? V, string H);

    private async Task PushAsync(string root, CancellationToken ct)
    {
        var me = _db.Device.Id;
        while (true)
        {
            List<(long Seq, string Obj, string Field, string? Value, string Hlc)> rows;
            using (var c = _db.Open())
                rows = c.Query<(long, string, string, string?, string)>("SELECT seq, obj, field, value, hlc FROM outbox ORDER BY seq LIMIT 5000").ToList();
            if (rows.Count == 0) return;
            var file = Path.Combine(_paths.CacheDir, $"journal-{Guid.NewGuid():N}.jsonl.gz");
            try
            {
                await using (var gz = new GZipStream(File.Create(file), CompressionLevel.Optimal))
                await using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
                    foreach (var r in rows)
                        await w.WriteLineAsync(JsonSerializer.Serialize(new Line(r.Obj, r.Field, r.Value, r.Hlc)));
                // Named by the time of its first change: the files of one computer sort in the order they were made.
                var name = $"{rows[0].Hlc}.jsonl.gz";
                await _cloud.Rclone.UploadAsync(file, CloudService.Join(root, $"journal/{me}/{name}"), ct);
                _db.SetSetting("journal_last_file", name);
            }
            finally
            {
                File.Delete(file);
            }
            var last = rows[^1].Seq;
            using (var c = _db.Open()) c.Execute("DELETE FROM outbox WHERE seq<=@last", new { last });
        }
    }

    // ---- the library as a whole ----

    /// <summary>The summaries of the shared library in the cloud, the newest last; none when it is not shared yet.</summary>
    public static async Task<List<RemoteFile>> SnapshotsAsync(Rclone rclone, string backupRoot, CancellationToken ct) =>
        (await rclone.ListAsync(CloudService.Join(backupRoot, "shared/snapshot"), 1, ct))
        .Where(f => f.Path.EndsWith(".db.gz", StringComparison.Ordinal)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

    /// <summary>The library's cloud has a shared library already (this one, or another computer's to join).</summary>
    public async Task<bool> SharedLibraryExistsAsync(CancellationToken ct) =>
        _cloud.BackupCloud is { } cloud && (await SnapshotsAsync(_cloud.Rclone, _cloud.BackupRoot(cloud), ct)).Count > 0;

    /// <summary>
    /// This library becomes the shared library of its cloud: the other computers of the family join it. What it
    /// knows goes up as it is (a summary of the whole library, which files of its folders are in the cloud), nothing
    /// is computed again. Its own backup in the cloud stops: the shared library is the backup now.
    /// </summary>
    public async Task ShareAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (_cloud.BackupCloud is not { } cloud) throw new InvalidOperationException("Choose the library's cloud first.");
        if (await SharedLibraryExistsAsync(ct)) throw new InvalidOperationException("The cloud has a shared library already: join it instead.");
        var root = Root(_cloud, cloud);
        await _round.WaitAsync(ct);
        try
        {
            _db.SetSetting(SharedSetting, "1");
            foreach (var source in new MomentFinder.Library.MediaRepository(_db).ListSources())
            {
                using (var c = _db.Open()) SharedFolders.Publish(c, null, _db.Facts, _db.Device, source.Id);
                // Which files of a backed up folder are in the cloud: what the cloud has, not what was read here.
                var mirror = _cloud.Mirrors().FirstOrDefault(m => m.SourceId == source.Id && m.Enabled);
                if (mirror != null)
                {
                    progress?.Report($"Which photos of {source.Name} are in the cloud");
                    var there = (await _cloud.Rclone.ListAsync(mirror.Place, -1, ct)).Select(f => f.Path).ToHashSet();
                    Catalog.Publish(_db, source.Id, there);
                }
                else if (source.Kind == MomentFinder.Library.SourceKinds.Cloud) Catalog.Publish(_db, source.Id);
            }
            progress?.Report("The whole library");
            await WriteSnapshotAsync(root, ct);
            await ExchangeRemotesAsync(cloud, ct);
            await WriteDeviceAsync(root, ct);
        }
        finally
        {
            _round.Release();
        }
    }

    /// <summary>
    /// A summary of the whole library as it is here (all its facts and what was computed), for a computer that joins:
    /// it takes it and then only the changes made after it. Named by time, so the newest sorts last.
    /// </summary>
    private async Task WriteSnapshotAsync(string root, CancellationToken ct)
    {
        await PushAsync(root, ct);
        var me = _db.Device.Id;
        var lastFile = _db.GetSetting("journal_last_file");
        var db = Path.Combine(_paths.CacheDir, $"snapshot-{Guid.NewGuid():N}.db");
        var gz = db + ".gz";
        try
        {
            using (var c = _db.Open()) c.Execute("VACUUM INTO @db", new { db });
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var s = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
            {
                s.Open();
                // What this computer sent is in it; what it took in from the others too, up to where it is.
                s.Execute("DELETE FROM outbox");
                s.Execute("""
                    INSERT INTO sync_seen(device, seq, last_file, last_seen) VALUES(@me, 0, @lastFile, @now)
                    ON CONFLICT(device) DO UPDATE SET last_file=excluded.last_file
                    """, new { me, lastFile, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            }
            await using (var from = File.OpenRead(db))
            await using (var to = new GZipStream(File.Create(gz), CompressionLevel.Optimal))
                await from.CopyToAsync(to, ct);
            await _cloud.Rclone.UploadAsync(gz, CloudService.Join(root, $"snapshot/{_db.Facts.Now()}.db.gz"), ct);
            _db.SetSetting("snapshot_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        }
        finally
        {
            File.Delete(db);
            File.Delete(gz);
        }
    }

    /// <summary>
    /// The access to the storages: a storage added on one computer (and the key of a folder encrypted there) reaches
    /// the others, always encrypted in the cloud. Each computer gives its own, takes what it does not have.
    /// </summary>
    private async Task ExchangeRemotesAsync(CloudInfo cloud, CancellationToken ct)
    {
        var keys = CloudService.Join(await _cloud.KeysRootAsync(cloud, ct), "shared");
        var mine = await _cloud.Rclone.DumpAsync(ct);
        var file = Path.Combine(_paths.CacheDir, $"remotes-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(mine), ct);
            await _cloud.Rclone.UploadAsync(file, CloudService.Join(keys, $"{_db.Device.Id}.json"), ct);
            var added = false;
            foreach (var other in await _cloud.Rclone.ListAsync(keys, 1, ct))
            {
                if (other.Path == $"{_db.Device.Id}.json" || await _cloud.Rclone.ReadTextAsync(CloudService.Join(keys, other.Path), ct) is not { } json) continue;
                var theirs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? [];
                foreach (var (name, settings) in theirs)
                    if (mine.TryAdd(name, settings)) added = true;
            }
            if (added) Rclone.WriteConfig(Path.Combine(_paths.DataDir, "rclone.conf"), mine);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private async Task WriteDeviceAsync(string root, CancellationToken ct)
    {
        var me = _db.Device;
        var file = Path.Combine(_paths.CacheDir, $"device-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { me.Id, me.Name, LastSeen = DateTimeOffset.UtcNow }), ct);
            await _cloud.Rclone.UploadAsync(file, CloudService.Join(root, $"devices/{me.Id}.json"), ct);
            _deviceWritten = DateTime.UtcNow;
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- in ----

    private async Task<int> PullAsync(string root, CancellationToken ct)
    {
        var me = _db.Device.Id;
        var files = await _cloud.Rclone.ListAsync(CloudService.Join(root, "journal"), 2, ct);
        Dictionary<string, string?> seen;
        using (var c = _db.Open())
            seen = c.Query<(string Device, string? Last)>("SELECT device, last_file FROM sync_seen").ToDictionary(s => s.Device, s => s.Last);
        var waiting = files.Select(f => f.Path.Split('/')).Where(p => p.Length == 2 && p[0] != me && p[1].EndsWith(".jsonl.gz", StringComparison.Ordinal))
            .Where(p => !seen.TryGetValue(p[0], out var last) || last == null || string.CompareOrdinal(p[1], last) > 0)
            .OrderBy(p => p[1], StringComparer.Ordinal).ToList();
        var taken = 0;
        var foldersChanged = false;
        foreach (var p in waiting)
        {
            var local = Path.Combine(_paths.CacheDir, $"journal-in-{Guid.NewGuid():N}.jsonl.gz");
            try
            {
                await _cloud.Rclone.DownloadAsync(CloudService.Join(root, $"journal/{p[0]}/{p[1]}"), local, ct);
                var changes = new List<Change>();
                await using (var gz = new GZipStream(File.OpenRead(local), CompressionMode.Decompress))
                using (var r = new StreamReader(gz, Encoding.UTF8))
                    while (await r.ReadLineAsync(ct) is { } line)
                        if (line.Length > 0 && JsonSerializer.Deserialize<Line>(line) is { } l)
                            changes.Add(new Change(l.O, l.F, l.V, l.H));
                foldersChanged |= Take(p[0], p[1], changes);
                taken += changes.Count;
            }
            finally
            {
                File.Delete(local);
            }
        }
        if (taken > 0) Changed?.Invoke();
        if (foldersChanged) FoldersChanged?.Invoke();
        return taken;
    }

    /// <summary>One file of another computer's changes, taken in whole or not at all. True when folders came or went.</summary>
    public bool Take(string device, string fileName, IReadOnlyList<Change> changes)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var kept = new List<string>();
        foreach (var change in changes)
            if (_db.Facts.Apply(c, tx, change)) kept.Add(change.Obj);
        var applier = new FactApplier(c, tx, _db.Device, _paths.CacheDir);
        applier.Apply(kept);
        c.Execute("""
            INSERT INTO sync_seen(device, seq, last_file, last_seen) VALUES(@device, @count, @fileName, @now)
            ON CONFLICT(device) DO UPDATE SET seq=seq+@count, last_file=excluded.last_file, last_seen=excluded.last_seen
            """, new { device, count = changes.Count, fileName, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx);
        tx.Commit();
        return applier.FoldersChanged;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
    }
}
