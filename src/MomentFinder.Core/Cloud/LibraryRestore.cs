using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MomentFinder.Data;
using MomentFinder.Library;

namespace MomentFinder.Cloud;

/// <summary>
/// The library of another computer copied here with its recovery string: on a second computer, or on a new one after
/// the old is gone. The copy is downloaded beside the library and put in its place at the next start, before anything
/// opens the old one. Then it is fitted to this computer: folders that are not here but are backed up are browsed from
/// the cloud (files come into the cache when opened, nothing is downloaded wholesale).
/// <para>
/// The download is the library's, not a window's: it goes on when the window that started it closes, says how far it
/// is to whoever asks (Changed), and goes on after a restart of the application (Resume).
/// </para>
/// </summary>
public sealed class LibraryRestore
{
    /// <summary>One computer's library backup found in the cloud.</summary>
    public sealed record Found(string Folder, string Computer, DateTimeOffset When, long Size)
    {
        /// <summary>The library shared by the computers of the family (not one computer's backup).</summary>
        public bool Shared => Folder.StartsWith(SharedPrefix, StringComparison.Ordinal);

        public string Title => Shared
            ? $"The shared library of {Computer} (summed up {When.LocalDateTime:d. M. yyyy H:mm}, the changes since come along)"
            : $"{Computer}, backed up {When.LocalDateTime:d. M. yyyy H:mm}";
    }

    /// <summary>A Found that is the shared library: its folder is the summary's file.</summary>
    public const string SharedPrefix = "shared:";

    public enum CopyState
    {
        /// <summary>Nothing is being copied.</summary>
        None,
        Copying,
        /// <summary>Downloaded; it takes the library's place at the next start.</summary>
        Ready,
        /// <summary>Stopped by an error; what came is kept for another try.</summary>
        Failed,
    }

    /// <summary>The part being downloaded (1 to Of), how far in percent (-1 while not known) and the numbers in words.</summary>
    public sealed record Step(int Number, int Of, string What, int Percent, string Detail);

    /// <summary>A library put in place at the start: from where, and how its folders were fitted to this computer.</summary>
    public sealed record Restored(string Computer, DateTimeOffset When, int FromCloud, int Away, bool ReplacedOwn, bool Shared = false, int Kept = 0, int OwnFolders = 0);

    /// <summary>What is being downloaded, kept with the download so that it goes on after a restart.</summary>
    private sealed record Job(string Recovery, string Folder, string Computer, DateTimeOffset When, bool Thumbnails);

    private const string JobName = "job.json";
    private const string ReadyName = "ready";
    private const string RestoredKey = "restored";

    private readonly AppPaths _paths;
    private readonly Rclone _rclone;
    private readonly string _config;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Recovery? _recovery;
    private bool _thumbnails;

    public LibraryRestore(AppPaths paths)
    {
        _paths = paths;
        _config = Path.Combine(paths.CacheDir, "restore-rclone.conf");
        _rclone = new Rclone(_config);
        if (ReadJob(paths) is { } job && File.Exists(Path.Combine(PendingDir(paths), ReadyName)))
        {
            State = CopyState.Ready;
            From = new Found(job.Folder, job.Computer, job.When, 0);
        }
    }

    public CopyState State { get; private set; }
    /// <summary>The library being copied or copied.</summary>
    public Found? From { get; private set; }
    public Step? Now { get; private set; }
    /// <summary>Why the copy stopped, when it failed.</summary>
    public string? Error { get; private set; }

    /// <summary>The state, the step or its progress changed; raised on a background thread.</summary>
    public event Action? Changed;

    private static string PendingDir(AppPaths paths) => Path.Combine(paths.DataDir, "restore-pending");
    private static string AdaptMarker(AppPaths paths) => Path.Combine(paths.DataDir, "restore-adapt");

    private static Job? ReadJob(AppPaths paths)
    {
        var path = Path.Combine(PendingDir(paths), JobName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Job>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>The library backups the recovery string leads to, the newest first.</summary>
    public async Task<List<Found>> FindAsync(Recovery recovery, CancellationToken ct)
    {
        Rclone.WriteConfig(_config, recovery.Remotes);
        var found = new List<Found>();
        // The shared library first: joining it is what a second computer of the family wants.
        if ((await MomentFinder.Sync.LibrarySync.SnapshotsAsync(_rclone, recovery.BackupRoot, ct)).LastOrDefault() is { } snapshot)
        {
            var devices = await _rclone.ListAsync(CloudService.Join(recovery.BackupRoot, "shared/devices"), 1, ct);
            var names = new List<string>();
            foreach (var d in devices)
                if (await _rclone.ReadTextAsync(CloudService.Join(recovery.BackupRoot, "shared/devices/" + d.Path), ct) is { } json &&
                    JsonSerializer.Deserialize<JsonElement>(json).TryGetProperty("Name", out var name))
                    names.Add(name.GetString() ?? "");
            found.Add(new Found(SharedPrefix + snapshot.Path, names.Count > 0 ? string.Join(", ", names.Order()) : "the family", snapshot.ModTime, snapshot.Size));
        }
        var files = await _rclone.ListAsync(recovery.BackupRoot, 2, ct);
        found.AddRange(files.Where(f => f.Path.Contains('/') && Path.GetFileName(f.Path) == LibraryBackup.DbName && !f.Path.StartsWith("shared/", StringComparison.Ordinal))
            .Select(f =>
            {
                var folder = f.Path[..f.Path.IndexOf('/')];
                return new Found(folder, folder.LastIndexOf('-') is > 0 and var dash ? folder[..dash] : folder, f.ModTime, f.Size);
            })
            .OrderByDescending(f => f.When));
        return found;
    }

    // ---- the copy as a job of the library ----

    /// <summary>Starts copying the library in the background (see State, Now, Changed).</summary>
    public void Start(Recovery recovery, Found backup, bool thumbnails)
    {
        CancellationToken ct;
        lock (_lock)
        {
            if (State == CopyState.Copying) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
            _recovery = recovery;
            _thumbnails = thumbnails;
            State = CopyState.Copying;
            From = backup;
            Now = null;
            Error = null;
        }
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadAsync(recovery, backup, thumbnails, new Callback<Step>(step =>
                {
                    Now = step;
                    Changed?.Invoke();
                }), ct);
                State = CopyState.Ready;
            }
            catch (OperationCanceledException)
            {
                DeletePending();
                State = CopyState.None;
                From = null;
            }
            catch (Exception e)
            {
                // Whatever it is, the copy must not stay "copying" forever: it says why and can be tried again.
                Error = e.Message;
                State = CopyState.Failed;
            }
            Now = null;
            Changed?.Invoke();
        });
    }

    /// <summary>A copy that the application's end interrupted goes on where it stopped.</summary>
    public bool Resume()
    {
        if (State != CopyState.None || ReadJob(_paths) is not { } job || RecoveryString.Parse(job.Recovery) is not { } recovery) return false;
        Start(recovery, new Found(job.Folder, job.Computer, job.When, 0), job.Thumbnails);
        return true;
    }

    /// <summary>Stops the copy; what came is deleted.</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>Another try after a failure; what came already is not downloaded again.</summary>
    public void Retry()
    {
        if (State == CopyState.Failed && _recovery != null && From != null) Start(_recovery, From, _thumbnails);
    }

    /// <summary>A copied or failed library is not wanted: it is deleted and this computer keeps its own.</summary>
    public void Discard()
    {
        if (State is not (CopyState.Ready or CopyState.Failed)) return;
        DeletePending();
        State = CopyState.None;
        From = null;
        Error = null;
        Changed?.Invoke();
    }

    private void DeletePending()
    {
        var pending = PendingDir(_paths);
        try
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Downloads a backup beside the library; the next start puts it in place (ApplyPending). A download of the same
    /// backup that stopped goes on: the files that came whole are not downloaded again.
    /// </summary>
    public async Task DownloadAsync(Recovery recovery, Found backup, bool thumbnails, IProgress<Step>? progress, CancellationToken ct)
    {
        Rclone.WriteConfig(_config, recovery.Remotes);
        var pending = PendingDir(_paths);
        var job = new Job(RecoveryString.Encode(recovery), backup.Folder, backup.Computer, backup.When, thumbnails);
        if (ReadJob(_paths) is not { } before || before.Folder != job.Folder || before.When != job.When)
        {
            if (Directory.Exists(pending)) Directory.Delete(pending, true);
        }
        Directory.CreateDirectory(pending);
        File.Delete(Path.Combine(pending, ReadyName));
        // Beside the library on this computer, where its own clouds' keys are too.
        File.WriteAllText(Path.Combine(pending, JobName), JsonSerializer.Serialize(job));

        if (backup.Shared)
        {
            await DownloadSharedAsync(recovery, backup, pending, progress, ct);
            return;
        }
        var root = CloudService.Join(recovery.BackupRoot, backup.Folder);
        var files = (await _rclone.ListAsync(root, 1, ct)).Select(f => f.Path).ToHashSet();
        var steps = thumbnails ? 3 : 2;

        IProgress<Rclone.CopyProgress> Part(int number, string what)
        {
            progress?.Report(new Step(number, steps, what, -1, ""));
            return new Callback<Rclone.CopyProgress>(p => progress?.Report(new Step(number, steps, what, p.Percent, p.Text)));
        }

        async Task FileAsync(string name, int number, string what)
        {
            var local = Path.Combine(pending, name);
            if (File.Exists(local)) return;
            await _rclone.DownloadAsync(CloudService.Join(root, name), local, Part(number, what), ct);
        }

        await FileAsync(LibraryBackup.DbName, 1, "the names, people and buckets");
        if (files.Contains(LibraryBackup.VectorsName)) await FileAsync(LibraryBackup.VectorsName, 2, "the search data");
        // The clouds as that computer knew them (kept encrypted); a backup without them still has the one of the
        // recovery string.
        try
        {
            await _rclone.DownloadAsync(CloudService.Join(recovery.Keys, $"{backup.Folder}/{LibraryBackup.CloudsName}"),
                Path.Combine(pending, LibraryBackup.CloudsName), ct);
        }
        catch (RcloneException)
        {
            File.Copy(_config, Path.Combine(pending, LibraryBackup.CloudsName), true);
        }
        if (thumbnails)
            await _rclone.CopyAsync(CloudService.Join(root, LibraryBackup.ThumbsName), Path.Combine(pending, LibraryBackup.ThumbsName),
                ["--transfers", "8"], Part(3, "the thumbnails"), ct);
        File.WriteAllText(Path.Combine(pending, ReadyName), thumbnails ? "thumbs" : "");
        File.Delete(_config);
    }

    /// <summary>
    /// The shared library: its newest summary, and the access to its storages as every computer of it gave them.
    /// Thumbnails and search data are not downloaded here: they come from the shared content as they are needed.
    /// </summary>
    private async Task DownloadSharedAsync(Recovery recovery, Found backup, string pending, IProgress<Step>? progress, CancellationToken ct)
    {
        var local = Path.Combine(pending, LibraryBackup.DbName);
        if (!File.Exists(local))
        {
            var gz = local + ".gz";
            var part = new Callback<Rclone.CopyProgress>(p => progress?.Report(new Step(1, 2, "the shared library", p.Percent, p.Text)));
            progress?.Report(new Step(1, 2, "the shared library", -1, ""));
            await _rclone.DownloadAsync(CloudService.Join(recovery.BackupRoot, "shared/snapshot/" + backup.Folder[SharedPrefix.Length..]), gz, part, ct);
            await using (var from = new System.IO.Compression.GZipStream(File.OpenRead(gz), System.IO.Compression.CompressionMode.Decompress))
            await using (var to = File.Create(local + ".part"))
                await from.CopyToAsync(to, ct);
            File.Move(local + ".part", local, true);
            File.Delete(gz);
        }
        progress?.Report(new Step(2, 2, "the access to the storages", -1, ""));
        var remotes = new Dictionary<string, Dictionary<string, string>>(recovery.Remotes);
        var keys = CloudService.Join(recovery.Keys, "shared");
        foreach (var f in await _rclone.ListAsync(keys, 1, ct))
            if (await _rclone.ReadTextAsync(CloudService.Join(keys, f.Path), ct) is { } json)
                foreach (var (name, settings) in JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json) ?? [])
                    remotes.TryAdd(name, settings);
        Rclone.WriteConfig(Path.Combine(pending, LibraryBackup.CloudsName), remotes);
        File.WriteAllText(Path.Combine(pending, ReadyName), "shared");
        File.Delete(_config);
    }

    /// <summary>
    /// At the start, before the library is opened: a downloaded backup takes the library's place. What was here is
    /// kept beside it (library.before-restore.db, thumbs.before-restore). True when a library was put in place.
    /// A download that has not finished stays for Resume.
    /// </summary>
    public static bool ApplyPending(AppPaths paths)
    {
        var pending = PendingDir(paths);
        if (!Directory.Exists(pending)) return false;
        var job = ReadJob(paths);
        var ready = Path.Combine(pending, ReadyName);
        if (!File.Exists(ready))
        {
            // Without its job (an older version) it cannot go on.
            if (job == null) Directory.Delete(pending, true);
            return false;
        }
        var withThumbs = Directory.Exists(Path.Combine(pending, LibraryBackup.ThumbsName));
        var shared = File.ReadAllText(ready) == "shared";
        var replacedOwn = File.Exists(paths.DbPath) && HasFolders(paths.DbPath);
        SqliteConnection.ClearAllPools();
        if (File.Exists(paths.DbPath)) File.Move(paths.DbPath, Path.Combine(paths.DataDir, "library.before-restore.db"), true);
        foreach (var journal in new[] { paths.DbPath + "-wal", paths.DbPath + "-shm" }) File.Delete(journal);
        File.Move(Path.Combine(pending, LibraryBackup.DbName), paths.DbPath);

        var vectors = Path.Combine(paths.VectorDir, LibraryBackup.VectorsName);
        File.Delete(vectors);
        if (File.Exists(Path.Combine(pending, LibraryBackup.VectorsName))) File.Move(Path.Combine(pending, LibraryBackup.VectorsName), vectors);
        File.Move(Path.Combine(pending, LibraryBackup.CloudsName), Path.Combine(paths.DataDir, LibraryBackup.CloudsName), true);

        // Thumbnails are named by the ids of the library they belong to: the old ones never fit the new library.
        var oldThumbs = Path.Combine(paths.DataDir, "thumbs.before-restore");
        if (Directory.Exists(oldThumbs)) Directory.Delete(oldThumbs, true);
        if (Directory.Exists(paths.ThumbDir)) Directory.Move(paths.ThumbDir, oldThumbs);
        if (withThumbs) Directory.Move(Path.Combine(pending, LibraryBackup.ThumbsName), paths.ThumbDir);
        else Directory.CreateDirectory(paths.ThumbDir);
        // The files of cloud sources fetched for the old library sit under its source ids.
        var cache = Path.Combine(paths.CacheDir, "cloud");
        if (Directory.Exists(cache)) Directory.Delete(cache, true);

        Directory.Delete(pending, true);
        File.WriteAllText(AdaptMarker(paths), JsonSerializer.Serialize(new Marker(withThumbs, job?.Computer ?? "", job?.When ?? DateTimeOffset.Now, replacedOwn, shared)));
        return true;
    }

    private sealed record Marker(bool Thumbs, string Computer, DateTimeOffset When, bool ReplacedOwn, bool Shared = false);

    /// <summary>The library had folders of its own (one never used has none: nothing is lost by replacing it).</summary>
    private static bool HasFolders(string db)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            return c.ExecuteScalar<long>("SELECT COUNT(*) FROM source") > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// After ApplyPending, with the restored library open: its sources are fitted to this computer. A local folder
    /// that is not here and was mirrored becomes a cloud source at the mirror's place: the same files under the same
    /// paths, so thumbnails, people and search work at once and the originals come from the cloud when opened.
    /// </summary>
    public static void AdaptToThisComputer(AppPaths paths, Database db)
    {
        var markerPath = AdaptMarker(paths);
        if (!File.Exists(markerPath)) return;
        var text = File.ReadAllText(markerPath);
        Marker marker;
        try
        {
            // An older version wrote only "thumbs" or nothing.
            marker = text.StartsWith('{') ? JsonSerializer.Deserialize<Marker>(text)! : new Marker(text == "thumbs", "", DateTimeOffset.Now, false);
        }
        catch (JsonException)
        {
            marker = new Marker(false, "", DateTimeOffset.Now, false);
        }
        int fromCloud = 0, away = 0;
        using (var c = db.Open())
        using (var tx = c.BeginTransaction())
        {
            // What the other computer was doing in the cloud is its work, and where it uploaded files from are its disks.
            c.Execute("DELETE FROM cloud_job; DELETE FROM upload_origin;", transaction: tx);
            var sources = c.Query<(long Id, string Kind, string Root)>("SELECT id, kind, root FROM source", transaction: tx).ToList();
            foreach (var s in sources)
            {
                var cache = Path.Combine(paths.CacheDir, "cloud", s.Id.ToString());
                if (s.Kind == SourceKinds.Cloud)
                {
                    c.Execute("UPDATE source SET root=@cache WHERE id=@Id", new { cache, s.Id }, tx);
                    continue;
                }
                if (Directory.Exists(s.Root))
                {
                    // A folder at the same place here (both computers have D:\Photos): read from here, but its backup is
                    // the other computer's (this folder may hold other files): not uploaded from here.
                    c.Execute("DELETE FROM mirror WHERE source_id=@Id", new { s.Id }, tx);
                    if (marker.Shared)
                    {
                        // And it is this computer's own folder, not the other's: a key of its own, and of its photos.
                        c.Execute("UPDATE source SET uid=lower(hex(randomblob(16))) WHERE id=@Id", new { s.Id }, tx);
                        c.Execute("UPDATE media SET uid=media_uid((SELECT uid FROM source WHERE id=@Id), rel_path) WHERE source_id=@Id", new { s.Id }, tx);
                    }
                    continue;
                }
                var mirror = c.Query<(string Target, string Remote, string? Own, long CloudId)>("""
                    SELECT m.target, c.remote, m.remote, m.cloud_id FROM mirror m JOIN cloud c ON c.id=m.cloud_id
                    WHERE m.source_id=@Id AND m.last_run_at IS NOT NULL ORDER BY m.last_run_at DESC LIMIT 1
                    """, new { s.Id }, tx).FirstOrDefault();
                if (mirror.Target == null)
                {
                    // In the shared library a folder only on the other computer's disk is that computer's own business.
                    if (marker.Shared) c.Execute("DELETE FROM source WHERE id=@Id", new { s.Id }, tx);
                    else away++;
                    continue;
                }
                // The backup of the other computer's folder: only for looking here, until this computer takes it over.
                c.Execute("UPDATE source SET kind=@kind, root=@cache, remote=@remote, cloud_id=@CloudId, read_only=1 WHERE id=@Id",
                    new { kind = SourceKinds.Cloud, cache, remote = mirror.Own ?? CloudService.Join(mirror.Remote, mirror.Target), mirror.CloudId, s.Id }, tx);
                // This computer does not have the folder: nothing to mirror from here.
                c.Execute("DELETE FROM mirror WHERE source_id=@Id", new { s.Id }, tx);
                fromCloud++;
            }
            if (!marker.Thumbs) c.Execute("UPDATE media SET thumb_state=0", transaction: tx);
            if (!File.Exists(Path.Combine(paths.VectorDir, LibraryBackup.VectorsName)))
            {
                c.Execute("DELETE FROM scene_vec", transaction: tx);
                c.Execute("UPDATE media SET analysis_version=0", transaction: tx);
            }
            if (marker.Shared)
            {
                // The shared library: its cloud stays this computer's too. Where the computer that summed it up was
                // in sending its own changes is its business; this one sends from the start.
                // Its own backup from before (if it had one) is its own to remove, after its own few days.
                c.Execute("""
                    DELETE FROM setting WHERE key IN ('backup_last_at', 'backup_last_result', 'backup_tried_at', 'journal_last_file',
                                                      'shared_since', 'own_backup_removed')
                    """, transaction: tx);
                c.Execute("DELETE FROM outbox", transaction: tx);
            }
            else
            {
                // The backup of the library is that computer's: this one does not upload its copy again (all the
                // thumbnails once more) until the user says so, in Settings.
                c.Execute("DELETE FROM setting WHERE key IN ('backup_cloud_id', 'backup_last_at', 'backup_last_result', 'backup_tried_at')", transaction: tx);
            }
            tx.Commit();
        }
        foreach (var s in new MediaRepository(db).ListSources().Where(s => s.Kind == SourceKinds.Cloud)) Directory.CreateDirectory(s.Root);
        // What was named and put in buckets on this computer before it joined stays, and goes to the others.
        int kept = 0, own = 0;
        var before = Path.Combine(paths.DataDir, "library.before-restore.db");
        if (marker.Shared && marker.ReplacedOwn && File.Exists(before))
        {
            // Its own folders with all that was found in them, then its names and buckets of the shared photos.
            own = MomentFinder.Sync.OwnFolders.Carry(paths, db, before, Path.Combine(paths.DataDir, "thumbs.before-restore"));
            new IndexImport(db).Import(before);
            kept = MomentFinder.Sync.FactSeed.Reconcile(db);
        }
        db.SetSetting(RestoredKey, JsonSerializer.Serialize(new Restored(marker.Computer, marker.When, fromCloud, away, marker.ReplacedOwn, marker.Shared, kept, own)));
        File.Delete(markerPath);
    }

    /// <summary>The library was put in place at this start (or one before), until ForgetRestored: the application says so once.</summary>
    public static Restored? LastRestored(Database db)
    {
        try
        {
            return db.GetSetting(RestoredKey) is { Length: > 0 } json ? JsonSerializer.Deserialize<Restored>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void ForgetRestored(Database db)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM setting WHERE key=@RestoredKey", new { RestoredKey });
    }

    /// <summary>Progress reported at once, on the reporting thread (Progress&lt;T&gt; would post it somewhere, out of order).</summary>
    private sealed class Callback<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
