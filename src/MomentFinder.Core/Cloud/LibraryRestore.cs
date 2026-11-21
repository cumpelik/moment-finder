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
        public string Title => $"{Computer}, backed up {When.LocalDateTime:d. M. yyyy H:mm}";
    }

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
    public sealed record Restored(string Computer, DateTimeOffset When, int FromCloud, int Away, bool ReplacedOwn);

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
        var files = await _rclone.ListAsync(recovery.BackupRoot, 2, ct);
        return files.Where(f => f.Path.Contains('/') && Path.GetFileName(f.Path) == LibraryBackup.DbName)
            .Select(f =>
            {
                var folder = f.Path[..f.Path.IndexOf('/')];
                return new Found(folder, folder.LastIndexOf('-') is > 0 and var dash ? folder[..dash] : folder, f.ModTime, f.Size);
            })
            .OrderByDescending(f => f.When).ToList();
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
        File.WriteAllText(AdaptMarker(paths), JsonSerializer.Serialize(new Marker(withThumbs, job?.Computer ?? "", job?.When ?? DateTimeOffset.Now, replacedOwn)));
        return true;
    }

    private sealed record Marker(bool Thumbs, string Computer, DateTimeOffset When, bool ReplacedOwn);

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
                    continue;
                }
                var mirror = c.Query<(string Target, string Remote, string? Own, long CloudId)>("""
                    SELECT m.target, c.remote, m.remote, m.cloud_id FROM mirror m JOIN cloud c ON c.id=m.cloud_id
                    WHERE m.source_id=@Id AND m.last_run_at IS NOT NULL ORDER BY m.last_run_at DESC LIMIT 1
                    """, new { s.Id }, tx).FirstOrDefault();
                if (mirror.Target == null)
                {
                    away++;
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
            // The backup of the library is that computer's: this one does not upload its copy again (all the
            // thumbnails once more) until the user says so, in Settings.
            c.Execute("DELETE FROM setting WHERE key IN ('backup_cloud_id', 'backup_last_at', 'backup_last_result', 'backup_tried_at')", transaction: tx);
            tx.Commit();
        }
        foreach (var s in new MediaRepository(db).ListSources().Where(s => s.Kind == SourceKinds.Cloud)) Directory.CreateDirectory(s.Root);
        db.SetSetting(RestoredKey, JsonSerializer.Serialize(new Restored(marker.Computer, marker.When, fromCloud, away, marker.ReplacedOwn)));
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
