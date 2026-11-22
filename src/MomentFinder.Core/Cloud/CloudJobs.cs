using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Dapper;
using MomentFinder.Data;
using MomentFinder.Library;
using MomentFinder.Media;
using MomentFinder.Sync;

namespace MomentFinder.Cloud;

public static class JobKinds
{
    /// <summary>Files and folders of this computer into a folder that lives in the cloud.</summary>
    public const string Upload = "upload";
    /// <summary>A folder of another storage (another bucket, a OneDrive) into a folder that lives in the cloud.</summary>
    public const string Import = "import";
    public const string Move = "move";
    public const string Delete = "delete";
}

public static class JobStates
{
    /// <summary>In the queue, or between two tries.</summary>
    public const string Waiting = "waiting";
    public const string Running = "running";
    public const string Done = "done";
    /// <summary>Some files did not go, also when tried again: it waits for the user.</summary>
    public const string Failed = "failed";
    /// <summary>Stopped by the user; what was done stays done.</summary>
    public const string Stopped = "stopped";
}

public static class JobFileStates
{
    public const int Waiting = 0;
    public const int Done = 1;
    public const int Failed = 2;
    /// <summary>The same file was there already: nothing to do.</summary>
    public const int Same = 3;
}

/// <summary>What a job does and where, as it was asked for.</summary>
public sealed record JobSpec
{
    /// <summary>Upload: files and folders on this computer (a folder goes with its name and everything in it).</summary>
    public List<string>? Paths { get; init; }
    /// <summary>Import: the folder in the other storage (an rclone path); what is in it goes into Folder.</summary>
    public string? Remote { get; init; }
    /// <summary>Move, delete: the source the files are in.</summary>
    public long? From { get; init; }
    /// <summary>Move, delete: a whole folder of it (with everything in it, not only photos and videos)...</summary>
    public string? FromFolder { get; init; }
    /// <summary>...or these files. Import: only these files of Remote (by name), not everything in it.</summary>
    public List<string>? Files { get; init; }
    /// <summary>Where it goes inside the target ("" is its top); for a moved folder, where the folder itself is afterwards.</summary>
    public string Folder { get; init; } = "";
}

public sealed class CloudJob
{
    public long Id { get; init; }
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Spec { get; init; } = "";
    public long TargetId { get; init; }
    public string TargetName { get; init; } = "";
    public string State { get; init; } = "";
    public bool Prepared { get; init; }
    public long CreatedAt { get; init; }
    public long? FinishedAt { get; init; }
    public int Attempts { get; init; }
    public long? TriedAt { get; init; }
    public string? Message { get; init; }
    public int Total { get; init; }
    /// <summary>Done now or there already.</summary>
    public int Done { get; init; }
    public int Same { get; init; }
    public int Failed { get; init; }
    public long Bytes { get; init; }
    public long DoneBytes { get; init; }

    /// <summary>Still to be worked on (now or at the next try).</summary>
    public bool IsOpen => State is JobStates.Waiting or JobStates.Running;
}

public sealed class CloudJobFile
{
    public long Seq { get; init; }
    public string Origin { get; init; } = "";
    public string Target { get; set; } = "";
    public long Size { get; set; }
    public long Mtime { get; set; }
    public int State { get; init; }
    public string? Error { get; init; }
}

/// <summary>How far a running job is.</summary>
public sealed record JobProgress(int Percent, string Text);

/// <summary>
/// The changes the user asks for in folders that live in the cloud: uploading files and folders into them, copying a
/// folder from another storage, moving and deleting files and whole folders. Each is a job in the library, done in
/// the background one after another, file by file: a break (the application closed, the connection lost) goes on
/// where it stopped, a file that did not go is kept with its reason and tried again, and what still does not go
/// waits for the user in the list. Nothing in the cloud is ever written over: a file that is there already is left
/// (the same one) or the new one goes beside it as "name (2).jpg".
/// </summary>
public sealed class CloudJobs : IDisposable
{
    private readonly AppPaths _paths;
    private readonly Database _db;
    private readonly MediaRepository _repo;
    private readonly CloudService _cloud;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<long> _stopRequested = [];
    private Task? _loop;
    private long? _runningId;
    private long? _runningFrom;
    private long? _runningTarget;
    private CancellationTokenSource? _running;
    private volatile bool _paused;

    /// <summary>A job was added, changed its state or went away; raised on a background thread.</summary>
    public event Action? Changed;
    /// <summary>How far the running job is; raised on a background thread.</summary>
    public event Action<long, JobProgress>? Progress;
    /// <summary>Photos came into the library, moved or went: the views read again, new ones get their thumbnails.</summary>
    public event Action? LibraryChanged;
    /// <summary>Files came into a folder without passing this computer (from another storage): the folder is read.</summary>
    public event Action<long>? ScanWanted;

    public CloudJobs(AppPaths paths, Database db, MediaRepository repo, CloudService cloud)
    {
        _paths = paths;
        _db = db;
        _repo = repo;
        _cloud = cloud;
    }

    private Rclone Rclone => _cloud.Rclone;

    // ---- asking ----

    /// <summary>The folders in a cloud that can be changed from here now: living there, not another computer's backup, not read-only.</summary>
    public List<Source> Changeable() =>
        _repo.ListSources().Where(s => s.Kind == SourceKinds.Cloud && !s.ReadOnly && s.Remote != null && !_repo.ReadOnly.IsLocked(s.Id)).ToList();

    private Source Checked(long sourceId)
    {
        var source = _repo.ListSources().FirstOrDefault(s => s.Id == sourceId) ?? throw new InvalidOperationException("The folder is not in the library any more.");
        if (source.Kind != SourceKinds.Cloud || source.Remote == null)
            throw new InvalidOperationException($"\"{source.Name}\" is a folder on a computer: change it there, MomentFinder follows.");
        if (source.ReadOnly)
            throw new InvalidOperationException($"\"{source.Name}\" is the backup of a folder on {source.HomeName ?? "another computer"}: only that computer changes it.");
        _repo.ReadOnly.ThrowIfLocked(sourceId, source.Name);
        return source;
    }

    private static string Place(Source source, string folder) => folder.Length == 0 ? source.Name : $"{source.Name} / {folder.Replace("/", " / ")}";

    /// <summary>Files and folders of this computer go into a folder of the cloud source (a folder with its name and all inside).</summary>
    public long Upload(long targetId, string folder, IReadOnlyList<string> paths)
    {
        var target = Checked(targetId);
        if (paths.Count == 0) throw new InvalidOperationException("Nothing was chosen.");
        folder = CloudService.CleanFolder(folder);
        var folders = paths.Count(Directory.Exists);
        var what = paths.Count == 1 ? $"\"{Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}\""
            : folders == 0 ? $"{paths.Count:N0} files"
            : folders == paths.Count ? $"{folders:N0} folders"
            : $"{folders:N0} {(folders == 1 ? "folder" : "folders")} and {paths.Count - folders:N0} {(paths.Count - folders == 1 ? "file" : "files")}";
        return Add(JobKinds.Upload, $"Upload {what} to {Place(target, folder)}", targetId, new JobSpec { Paths = paths.ToList(), Folder = folder });
    }

    /// <summary>What is in a folder of another storage goes into a folder of the cloud source.</summary>
    public long Import(long targetId, string folder, string remote, string from)
    {
        var target = Checked(targetId);
        folder = CloudService.CleanFolder(folder);
        var place = CloudService.Join(target.Remote!, folder);
        if (place == remote || place.StartsWith(remote.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("A folder cannot be copied into itself.");
        return Add(JobKinds.Import, $"Copy {from} to {Place(target, folder)}", targetId, new JobSpec { Remote = remote, Folder = folder });
    }

    /// <summary>Single files of a folder of another storage go into a folder of the cloud source.</summary>
    public long ImportFiles(long targetId, string folder, string remote, IReadOnlyList<string> names, string from)
    {
        var target = Checked(targetId);
        if (names.Count == 0) throw new InvalidOperationException("Nothing was chosen.");
        folder = CloudService.CleanFolder(folder);
        var what = names.Count == 1 ? $"\"{names[0]}\"" : $"{names.Count:N0} files";
        return Add(JobKinds.Import, $"Copy {what} from {from} to {Place(target, folder)}", targetId,
            new JobSpec { Remote = remote, Files = names.ToList(), Folder = folder });
    }

    /// <summary>These files of a cloud source go into a folder of the same or another cloud source.</summary>
    public long Move(long fromId, IReadOnlyList<string> files, long targetId, string folder)
    {
        var from = Checked(fromId);
        var target = Checked(targetId);
        if (files.Count == 0) throw new InvalidOperationException("Nothing was chosen.");
        folder = CloudService.CleanFolder(folder);
        var what = files.Count == 1 ? $"\"{Path.GetFileName(files[0])}\"" : $"{files.Count:N0} files";
        return Add(JobKinds.Move, $"Move {what} from {from.Name} to {Place(target, folder)}", targetId,
            new JobSpec { From = fromId, Files = files.ToList(), Folder = folder });
    }

    /// <summary>A whole folder of a cloud source moves (or is renamed): it is at newPath of the target afterwards.</summary>
    public long MoveFolder(long fromId, string folder, long targetId, string newPath)
    {
        var from = Checked(fromId);
        var target = Checked(targetId);
        folder = CloudService.CleanFolder(folder);
        newPath = CloudService.CleanFolder(newPath);
        if (folder.Length == 0) throw new InvalidOperationException("The whole source cannot be moved: move the folders in it.");
        if (newPath.Length == 0) throw new InvalidOperationException("Say where the folder goes.");
        if (fromId == targetId && (newPath == folder || newPath.StartsWith(folder + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException("A folder cannot be moved into itself.");
        return Add(JobKinds.Move, $"Move the folder {Place(from, folder)} to {Place(target, newPath)}", targetId,
            new JobSpec { From = fromId, FromFolder = folder, Folder = newPath });
    }

    /// <summary>These files are deleted in the cloud, for good.</summary>
    public long Delete(long sourceId, IReadOnlyList<string> files)
    {
        var source = Checked(sourceId);
        if (files.Count == 0) throw new InvalidOperationException("Nothing was chosen.");
        var what = files.Count == 1 ? $"\"{Path.GetFileName(files[0])}\"" : $"{files.Count:N0} files";
        return Add(JobKinds.Delete, $"Delete {what} from {source.Name}", sourceId, new JobSpec { From = sourceId, Files = files.ToList() });
    }

    /// <summary>A whole folder is deleted in the cloud with everything in it, for good.</summary>
    public long DeleteFolder(long sourceId, string folder)
    {
        var source = Checked(sourceId);
        folder = CloudService.CleanFolder(folder);
        if (folder.Length == 0) throw new InvalidOperationException("The whole source is not deleted from here: delete the folders in it.");
        return Add(JobKinds.Delete, $"Delete the folder {Place(source, folder)}", sourceId, new JobSpec { From = sourceId, FromFolder = folder });
    }

    private long Add(string kind, string title, long targetId, JobSpec spec)
    {
        long id;
        using (var c = _db.Open())
            id = c.ExecuteScalar<long>("""
                INSERT INTO cloud_job(kind, title, spec, target_id, created_at) VALUES(@kind, @title, @spec, @targetId, @now) RETURNING id
                """, new { kind, title, spec = JsonSerializer.Serialize(spec), targetId, now = Now() });
        Changed?.Invoke();
        _wake.Release();
        return id;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- the list ----

    private const string JobColumns = """
        j.id AS Id, j.kind AS Kind, j.title AS Title, j.spec AS Spec, j.target_id AS TargetId, s.name AS TargetName, j.state AS State,
        j.prepared AS Prepared, j.created_at AS CreatedAt, j.finished_at AS FinishedAt, j.attempts AS Attempts, j.tried_at AS TriedAt,
        j.message AS Message,
        (SELECT COUNT(*) FROM cloud_job_file f WHERE f.job_id=j.id) AS Total,
        (SELECT COUNT(*) FROM cloud_job_file f WHERE f.job_id=j.id AND f.state IN (1, 3)) AS Done,
        (SELECT COUNT(*) FROM cloud_job_file f WHERE f.job_id=j.id AND f.state=3) AS Same,
        (SELECT COUNT(*) FROM cloud_job_file f WHERE f.job_id=j.id AND f.state=2) AS Failed,
        (SELECT COALESCE(SUM(f.size), 0) FROM cloud_job_file f WHERE f.job_id=j.id) AS Bytes,
        (SELECT COALESCE(SUM(f.size), 0) FROM cloud_job_file f WHERE f.job_id=j.id AND f.state IN (1, 3)) AS DoneBytes
        """;

    /// <summary>All jobs, the oldest first.</summary>
    public List<CloudJob> List()
    {
        using var c = _db.Open();
        return c.Query<CloudJob>($"SELECT {JobColumns} FROM cloud_job j JOIN source s ON s.id=j.target_id ORDER BY j.id").ToList();
    }

    public CloudJob? Get(long id)
    {
        using var c = _db.Open();
        return c.QueryFirstOrDefault<CloudJob>($"SELECT {JobColumns} FROM cloud_job j JOIN source s ON s.id=j.target_id WHERE j.id=@id", new { id });
    }

    /// <summary>The files of a job that did not go, each with its reason.</summary>
    public List<CloudJobFile> FailedFiles(long jobId, int limit = 200)
    {
        using var c = _db.Open();
        return c.Query<CloudJobFile>("""
            SELECT seq AS Seq, origin AS Origin, target AS Target, size AS Size, mtime AS Mtime, state AS State, error AS Error
            FROM cloud_job_file WHERE job_id=@jobId AND state=2 ORDER BY seq LIMIT @limit
            """, new { jobId, limit }).ToList();
    }

    private List<CloudJobFile> Files(long jobId, int state)
    {
        using var c = _db.Open();
        return c.Query<CloudJobFile>("""
            SELECT seq AS Seq, origin AS Origin, target AS Target, size AS Size, mtime AS Mtime, state AS State, error AS Error
            FROM cloud_job_file WHERE job_id=@jobId AND state=@state ORDER BY seq
            """, new { jobId, state }).ToList();
    }

    /// <summary>A job is working in this source now (the scan leaves it alone until it is done).</summary>
    public bool IsBusy(long sourceId)
    {
        lock (_gate) return _runningId != null && (_runningTarget == sourceId || _runningFrom == sourceId);
    }

    /// <summary>Stops a job: now when it runs. What was done stays done.</summary>
    public void Stop(long id)
    {
        lock (_gate)
        {
            if (_runningId == id)
            {
                _stopRequested.Add(id);
                _running?.Cancel();
                return;
            }
        }
        Execute("UPDATE cloud_job SET state=@state, finished_at=@now WHERE id=@id AND state=@waiting",
            new { id, state = JobStates.Stopped, now = Now(), waiting = JobStates.Waiting });
        Changed?.Invoke();
    }

    /// <summary>A job that stopped or gave up goes on: what did not go is tried again.</summary>
    public void Retry(long id)
    {
        Execute("UPDATE cloud_job SET state=@state, attempts=0, tried_at=NULL, finished_at=NULL, message=NULL WHERE id=@id AND state<>@running",
            new { id, state = JobStates.Waiting, running = JobStates.Running });
        Changed?.Invoke();
        _wake.Release();
    }

    /// <summary>Takes a job that does not run off the list.</summary>
    public void Remove(long id)
    {
        Execute("DELETE FROM cloud_job WHERE id=@id AND state<>@running", new { id, running = JobStates.Running });
        Changed?.Invoke();
    }

    /// <summary>The jobs that are done go off the list.</summary>
    public void ClearDone()
    {
        Execute("DELETE FROM cloud_job WHERE state=@done", new { done = JobStates.Done });
        Changed?.Invoke();
    }

    private void Execute(string sql, object? args = null)
    {
        using var c = _db.Open();
        c.Execute(sql, args);
    }

    // ---- the background work ----

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Nothing goes to the cloud while paused: what runs stops at once and goes on after the pause.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value)
                lock (_gate)
                    _running?.Cancel();
            else _wake.Release();
        }
    }

    /// <summary>Between two tries of a job that could not go on: soon at first (a short break), then every few minutes.</summary>
    private static TimeSpan Wait(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>The job whose turn it is: the oldest waiting one that is not between two tries.</summary>
    private long? Next()
    {
        var now = Now();
        using var c = _db.Open();
        return c.Query<(long Id, int Attempts, long? TriedAt)>("SELECT id, attempts, tried_at FROM cloud_job WHERE state=@waiting ORDER BY id",
                new { waiting = JobStates.Waiting })
            .Where(j => j.TriedAt is not { } tried || now - tried >= Wait(j.Attempts).TotalMilliseconds).Select(j => (long?)j.Id).FirstOrDefault();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Running when the application ended: they go on.
        Execute("UPDATE cloud_job SET state=@waiting WHERE state=@running", new { waiting = JobStates.Waiting, running = JobStates.Running });
        // Done long ago: off the list by themselves.
        Execute("DELETE FROM cloud_job WHERE state=@done AND finished_at<@before",
            new { done = JobStates.Done, before = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds() });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_paused || !Rclone.IsAvailable || Next() is not { } job)
                {
                    await _wake.WaitAsync(TimeSpan.FromSeconds(20), ct);
                    continue;
                }
                await RunAsync(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Works on one job until it is done, stopped or cannot go on (the loop does this; the command line and the tests call it).</summary>
    public async Task RunAsync(long id, CancellationToken ct)
    {
        if (Get(id) is not { IsOpen: true } job) return;
        var spec = JsonSerializer.Deserialize<JobSpec>(job.Spec) ?? new JobSpec();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _runningId = id;
            _runningTarget = job.TargetId;
            _runningFrom = spec.From;
            _running = run;
        }
        Execute("UPDATE cloud_job SET state=@state, message=NULL WHERE id=@id", new { id, state = JobStates.Running });
        Changed?.Invoke();
        string state;
        string? message = null;
        var tried = false;
        try
        {
            message = await DoAsync(job, spec, run.Token);
            var after = Get(id)!;
            tried = after.Failed > 0 || message != null;
            // A try that did not get through is repeated by itself; files that keep failing wait for the user.
            state = !tried ? JobStates.Done
                : message != null || after.Attempts + 1 < 3 ? JobStates.Waiting
                : JobStates.Failed;
        }
        catch (OperationCanceledException)
        {
            bool stopped;
            lock (_gate) stopped = _stopRequested.Remove(id);
            // Stopped by the user; or paused, or the application ends: then it goes on later.
            state = stopped ? JobStates.Stopped : JobStates.Waiting;
        }
        catch (InvalidOperationException e)
        {
            // Not to be done as things are (read-only, the folder gone from the library): the user has to look.
            state = JobStates.Failed;
            message = e.Message;
        }
        catch (Exception e) when (e is RcloneException or IOException or UnauthorizedAccessException or JsonException)
        {
            state = JobStates.Waiting;
            message = e.Message;
            tried = true;
        }
        finally
        {
            lock (_gate)
            {
                _runningId = _runningTarget = _runningFrom = null;
                _running = null;
            }
        }
        Execute("""
            UPDATE cloud_job SET state=@state, message=@message, attempts=attempts+@tried, tried_at=CASE WHEN @tried=1 THEN @now ELSE tried_at END,
                   finished_at=CASE WHEN @state IN ('done', 'failed', 'stopped') THEN @now ELSE NULL END
            WHERE id=@id
            """, new { id, state, message, tried = tried ? 1 : 0, now = Now() });
        Changed?.Invoke();
        LibraryChanged?.Invoke();
        if (job.Kind == JobKinds.Import && state != JobStates.Stopped) ScanWanted?.Invoke(job.TargetId);
    }

    /// <summary>One try of the job. Returns why it could not get through (to be tried again), or null.</summary>
    private async Task<string?> DoAsync(CloudJob job, JobSpec spec, CancellationToken ct)
    {
        var target = Checked(job.TargetId);
        var from = spec.From is { } fromId && fromId != job.TargetId ? Checked(fromId) : spec.From != null ? target : null;
        var remotes = await Rclone.DumpAsync(ct);
        foreach (var source in from == null ? [target] : new[] { target, from })
            if (!remotes.ContainsKey(source.Remote!.Split(':')[0]))
                throw new InvalidOperationException($"The storage of \"{source.Name}\" is not connected on this computer.");
        if (!job.Prepared)
        {
            Progress?.Invoke(job.Id, new JobProgress(0, "Looking what there is to do"));
            await PrepareAsync(job, spec, target, from, ct);
            Changed?.Invoke();
        }
        // What did not go at the last try is tried again.
        Execute("UPDATE cloud_job_file SET state=0, error=NULL WHERE job_id=@Id AND state=2", new { job.Id });
        var files = Files(job.Id, JobFileStates.Waiting);
        if (files.Count == 0) return null;
        var work = new Work(this, Get(job.Id)!, spec, target, from, ct);
        var error = job.Kind == JobKinds.Delete ? await work.DeleteAsync(files) : await work.PlaceAsync(files);
        // The other computers of the library learn what is in the folders now.
        Catalog.Publish(_db, target.Id);
        if (from != null && from.Id != target.Id) Catalog.Publish(_db, from.Id);
        return error;
    }

    // ---- what there is to do ----

    private async Task PrepareAsync(CloudJob job, JobSpec spec, Source target, Source? from, CancellationToken ct)
    {
        var rows = new List<(string Origin, string Target, long Size, long Mtime)>();
        switch (job.Kind)
        {
            case JobKinds.Upload:
                foreach (var path in spec.Paths ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                    {
                        var name = DeviceInfo.Safe(new DirectoryInfo(path).Name);
                        foreach (var (rel, file) in await Task.Run(() => FolderSync.ListLocal(path), ct))
                            rows.Add((Path.Combine(path, rel.Replace('/', Path.DirectorySeparatorChar)), CloudService.Join(CloudService.Join(spec.Folder, name), rel),
                                file.Size, file.ModTime.ToUnixTimeMilliseconds()));
                    }
                    else
                    {
                        // A file that is not there (any more) stays on the list: it is said so when its turn comes.
                        var info = new FileInfo(path);
                        rows.Add((path, CloudService.Join(spec.Folder, info.Name), info.Exists ? info.Length : 0,
                            info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() : 0));
                    }
                }
                break;
            case JobKinds.Import when spec.Files is { } names:
            {
                // Single files of that folder: what the storage says of them (a name that is not there fails by itself).
                var wanted = names.ToHashSet(StringComparer.Ordinal);
                var there = (await Rclone.ListAsync(spec.Remote!, 1, ct)).Where(f => wanted.Contains(f.Path)).ToDictionary(f => f.Path, StringComparer.Ordinal);
                foreach (var name in names.Distinct())
                    rows.Add((name, CloudService.Join(spec.Folder, name), there.GetValueOrDefault(name)?.Size ?? 0,
                        there.GetValueOrDefault(name)?.ModTime.ToUnixTimeMilliseconds() ?? 0));
                break;
            }
            case JobKinds.Import:
                foreach (var f in await Rclone.ListAsync(spec.Remote!, ct))
                    if (!LibraryScanner.IsIgnored(f.Path))
                        rows.Add((f.Path, CloudService.Join(spec.Folder, f.Path), f.Size, f.ModTime.ToUnixTimeMilliseconds()));
                break;
            case JobKinds.Move or JobKinds.Delete when spec.FromFolder is { } folder:
                // The whole folder as the cloud has it: what the library does not show (other files) goes with it.
                foreach (var f in await Rclone.ListAsync(CloudService.Join(from!.Remote!, folder), ct))
                    rows.Add((CloudService.Join(folder, f.Path), job.Kind == JobKinds.Delete ? "" : CloudService.Join(spec.Folder, f.Path),
                        f.Size, f.ModTime.ToUnixTimeMilliseconds()));
                break;
            case JobKinds.Move or JobKinds.Delete:
            {
                var known = new Dictionary<string, (long Size, long Mtime)>(StringComparer.Ordinal);
                using (var c = _db.Open())
                    foreach (var chunk in (spec.Files ?? []).Distinct().Chunk(500))
                        foreach (var (path, size, mtime) in c.Query<(string, long, long)>(
                                     "SELECT rel_path, size, mtime FROM media WHERE source_id=@Id AND rel_path IN @chunk", new { from!.Id, chunk }))
                            known[path] = (size, mtime);
                foreach (var file in (spec.Files ?? []).Distinct())
                {
                    var to = job.Kind == JobKinds.Delete ? "" : CloudService.Join(spec.Folder, file[(file.LastIndexOf('/') + 1)..]);
                    // In that folder already: nothing to move.
                    if (job.Kind == JobKinds.Move && from!.Id == target.Id && to == file) continue;
                    var (size, mtime) = known.GetValueOrDefault(file);
                    rows.Add((file, to, size, mtime));
                }
                break;
            }
        }
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            c.Execute("DELETE FROM cloud_job_file WHERE job_id=@Id", new { job.Id }, tx);
            var seq = 0;
            foreach (var r in rows)
                c.Execute("INSERT INTO cloud_job_file(job_id, seq, origin, target, size, mtime) VALUES(@Id, @seq, @Origin, @Target, @Size, @Mtime)",
                    new { job.Id, seq = seq++, r.Origin, r.Target, r.Size, r.Mtime }, tx);
            c.Execute("UPDATE cloud_job SET prepared=1 WHERE id=@Id", new { job.Id }, tx);
            tx.Commit();
        }
    }

    // ---- doing it ----

    /// <summary>One try of one job: what it needs to know while it works, and how far it is.</summary>
    private sealed class Work(CloudJobs jobs, CloudJob job, JobSpec spec, Source target, Source? from, CancellationToken ct)
    {
        private readonly Database _db = jobs._db;
        private readonly Rclone _rclone = jobs.Rclone;
        private readonly MediaRepository _repo = jobs._repo;
        private long _doneBytes = job.DoneBytes;
        private int _doneFiles = job.Done;
        private DateTime _said = DateTime.MinValue;
        private DateTime _published = DateTime.UtcNow;

        private string Verb => job.Kind switch
        {
            JobKinds.Upload => "Uploaded",
            JobKinds.Import => "Copied",
            JobKinds.Move => "Moved",
            _ => "Deleted",
        };

        /// <summary>How far the job is; bytes: with what rclone is in the middle of.</summary>
        private void Report(long bytes = 0, double speed = 0, bool always = false)
        {
            if (!always && DateTime.UtcNow - _said < TimeSpan.FromMilliseconds(700)) return;
            _said = DateTime.UtcNow;
            // By size where the bytes pass this computer; a move or a delete inside the cloud by the files.
            var bySize = job.Kind is JobKinds.Upload or JobKinds.Import && job.Bytes > 0;
            var done = Math.Min(job.Bytes, Math.Max(bytes, Interlocked.Read(ref _doneBytes)));
            var percent = bySize ? (int)(done * 100 / job.Bytes) : job.Total == 0 ? 0 : Math.Min(100, _doneFiles * 100 / job.Total);
            var parts = new List<string>();
            if (bySize) parts.Add($"{Rclone.SizeText(done)} of {Rclone.SizeText(job.Bytes)}");
            if (speed > 0) parts.Add($"{Rclone.SizeText((long)speed)}/s");
            parts.Add($"{_doneFiles.ToString("N0", CultureInfo.InvariantCulture)} of {job.Total.ToString("N0", CultureInfo.InvariantCulture)} files");
            jobs.Progress?.Invoke(job.Id, new JobProgress(percent, $"{Verb} {string.Join(", ", parts)}"));
        }

        private void SetFiles(IEnumerable<CloudJobFile> files, int state, Func<CloudJobFile, string?>? error = null)
        {
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var f in files)
                c.Execute("UPDATE cloud_job_file SET state=@state, error=@error, target=@Target, size=@Size, mtime=@Mtime WHERE job_id=@Id AND seq=@Seq",
                    new { state, error = error?.Invoke(f), f.Target, f.Size, f.Mtime, job.Id, f.Seq }, tx);
            tx.Commit();
        }

        /// <summary>"a/IMG_1.jpg", then "a/IMG_1 (2).jpg", "a/IMG_1 (3).jpg"...</summary>
        private static IEnumerable<string> Versions(string rel)
        {
            yield return rel;
            var dot = rel.LastIndexOf('.');
            var slash = rel.LastIndexOf('/');
            var (stem, ext) = dot > slash + 1 ? (rel[..dot], rel[dot..]) : (rel, "");
            for (var n = 2; ; n++) yield return $"{stem} ({n}){ext}";
        }

        /// <summary>
        /// What is in the target where the files go. Only the folders they go into are listed (a folder of its own
        /// that is new costs one question, not a listing of the whole source).
        /// </summary>
        private async Task<Dictionary<string, RemoteFile>> ThereAsync(IEnumerable<string> targets)
        {
            var dirs = targets.Select(t => t.Contains('/') ? t[..t.LastIndexOf('/')] : "").Distinct().ToList();
            var there = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
            // Straight into the top of the source: only what is right there, not the whole source.
            if (dirs.Remove(""))
                foreach (var f in await _rclone.ListAsync(target.Remote!, 1, ct))
                    there.TryAdd(f.Path, f);
            // Of the folders touched, those not inside another one; with folders below it, one is listed with them.
            foreach (var top in dirs.Where(d => !dirs.Any(o => d.StartsWith(o + "/", StringComparison.Ordinal))))
            {
                var deep = dirs.Any(d => d.StartsWith(top + "/", StringComparison.Ordinal));
                foreach (var f in await _rclone.ListAsync(CloudService.Join(target.Remote!, top), deep ? -1 : 1, ct))
                    there.TryAdd(CloudService.Join(top, f.Path), f);
            }
            return there;
        }

        /// <summary>Uploads, copies from another storage, or moves: every file to its place, never over another file.</summary>
        public async Task<string?> PlaceAsync(List<CloudJobFile> files)
        {
            var upload = job.Kind == JobKinds.Upload;
            var move = job.Kind == JobKinds.Move;
            jobs.Progress?.Invoke(job.Id, new JobProgress(0, "Looking what is there already"));
            var there = await ThereAsync(files.Select(f => f.Target));

            var todo = new List<CloudJobFile>();
            var same = new List<CloudJobFile>();
            var away = new List<CloudJobFile>();
            foreach (var f in files)
            {
                if (upload)
                {
                    // As the file is now (it may have changed since it was chosen).
                    var info = new FileInfo(f.Origin);
                    if (!info.Exists)
                    {
                        away.Add(f);
                        continue;
                    }
                    f.Size = info.Length;
                    f.Mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                }
                // Asked for by name from another storage and not found there (rclone would pass it over in silence).
                else if (job.Kind == JobKinds.Import && spec.Files != null && f.Mtime == 0)
                {
                    away.Add(f);
                    continue;
                }
                var me = new RemoteFile(f.Target, f.Size, DateTimeOffset.FromUnixTimeMilliseconds(f.Mtime));
                // There already (from before, or from a try that was cut): under its name or one with a number.
                if (f.Mtime > 0 && Versions(f.Target).TakeWhile(there.ContainsKey).FirstOrDefault(v => FolderSync.Same(there[v], me)) is { } found &&
                    !(move && from!.Id == target.Id && string.Equals(found, f.Origin, StringComparison.OrdinalIgnoreCase)))
                {
                    f.Target = found;
                    same.Add(f);
                    continue;
                }
                var to = Versions(f.Target).First(v => !there.ContainsKey(v));
                there[to] = me;
                f.Target = to;
                todo.Add(f);
            }
            SetFiles(away, JobFileStates.Failed, _ => upload ? "The file is not on this computer any more." : "The file is not in that storage any more.");
            if (same.Count > 0)
            {
                // A moved file that is at its new place already (a try that was cut after the cloud did it): the one
                // left behind goes.
                if (move) await _rclone.BatchAsync("delete", from!.Remote!, null, same.Select(f => f.Origin).ToList(), null, null, ct);
                await RecordAsync(same, JobFileStates.Same);
            }

            // Files that keep their path under a folder on both sides go together, in one run of rclone; a file that
            // gets another name (one of that name is there) goes by itself.
            string? error = null;
            var failed = 0;
            foreach (var group in todo.GroupBy(Bases).OrderBy(g => g.Key.Alone))
            {
                ct.ThrowIfCancellationRequested();
                if (group.Key.Alone)
                {
                    await Parallel.ForEachAsync(group, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (f, token) =>
                    {
                        try
                        {
                            var to = CloudService.Join(target.Remote!, f.Target);
                            if (move) await _rclone.MoveFileAsync(CloudService.Join(from!.Remote!, f.Origin), to, token);
                            else await _rclone.UploadAsync(upload ? f.Origin : CloudService.Join(spec.Remote!, f.Origin), to, token);
                            await RecordAsync([f], JobFileStates.Done);
                        }
                        catch (RcloneException e)
                        {
                            SetFiles([f], JobFileStates.Failed, _ => e.Message);
                            Interlocked.Increment(ref failed);
                        }
                    });
                    continue;
                }
                var byRel = group.ToDictionary(f => Rel(f, group.Key.From), StringComparer.Ordinal);
                var result = await BatchAsync(move ? "move" : "copy", group.Key.From, group.Key.To, byRel);
                failed += result.Failed.Count;
                error ??= result.Error;
            }
            // A folder that moved whole leaves nothing behind (storages with real folders keep the empty ones).
            if (move && spec.FromFolder is { } folder && error == null && failed == 0)
            {
                try
                {
                    await _rclone.RunAsync(["rmdirs", CloudService.Join(from!.Remote!, folder)], ct);
                }
                catch (RcloneException)
                {
                    // No such folder there (a storage without real folders): nothing to tidy.
                }
            }
            Report(always: true);
            return error;
        }

        public async Task<string?> DeleteAsync(List<CloudJobFile> files)
        {
            var byRel = files.ToDictionary(f => f.Origin, StringComparer.Ordinal);
            var result = await BatchAsync("delete", target.Remote!, null, byRel);
            if (spec.FromFolder is { } folder && result.Error == null && result.Failed.Count == 0)
            {
                try
                {
                    await _rclone.RunAsync(["rmdirs", CloudService.Join(target.Remote!, folder)], ct);
                }
                catch (RcloneException)
                {
                }
            }
            Report(always: true);
            return result.Error;
        }

        /// <summary>
        /// One run of rclone over files that keep their names. What it says is done is written down while it runs
        /// (the library shows the photos as they arrive); what failed is kept with its reason.
        /// </summary>
        private async Task<Rclone.BatchResult> BatchAsync(string verb, string fromPath, string? toPath, Dictionary<string, CloudJobFile> byRel)
        {
            var arrived = new ConcurrentQueue<CloudJobFile>();
            var recorded = new HashSet<long>();
            using var ended = new CancellationTokenSource();
            var before = Interlocked.Read(ref _doneBytes);
            var pump = Task.Run(async () =>
            {
                while (true)
                {
                    // Asked before the queue is emptied: what arrived last is not left in it.
                    var last = ended.IsCancellationRequested;
                    var batch = new List<CloudJobFile>();
                    while (batch.Count < 200 && arrived.TryDequeue(out var f)) batch.Add(f);
                    if (batch.Count > 0)
                    {
                        await RecordAsync(batch, JobFileStates.Done);
                        foreach (var f in batch) recorded.Add(f.Seq);
                    }
                    else if (last) return;
                    else await Task.Delay(300);
                }
            });
            Rclone.BatchResult result;
            try
            {
                result = await _rclone.BatchAsync(verb, fromPath, toPath, byRel.Keys, rel =>
                {
                    if (byRel.TryGetValue(rel, out var f)) arrived.Enqueue(f);
                }, stats => Report(before + stats.Bytes, stats.Speed), ct);
            }
            finally
            {
                ended.Cancel();
                await pump;
            }
            // Not named by rclone and the run ended well: they are where they should be (there already).
            if (result.Error == null)
                await RecordAsync(byRel.Where(p => !recorded.Contains(p.Value.Seq) && !result.Failed.ContainsKey(p.Key)).Select(p => p.Value).ToList(), JobFileStates.Done);
            var why = result.Failed.Where(p => byRel.ContainsKey(p.Key) && !recorded.Contains(byRel[p.Key].Seq)).ToDictionary(p => byRel[p.Key].Seq, p => p.Value);
            SetFiles(byRel.Values.Where(f => why.ContainsKey(f.Seq)), JobFileStates.Failed, f => why[f.Seq]);
            return result;
        }

        /// <summary>
        /// The files are in the cloud as asked (or gone from it): the library follows. An uploaded photo gets its
        /// row from the file here (nothing is downloaded to read it), a moved one keeps its row with all that is
        /// known of it, a deleted one is gone from the library.
        /// </summary>
        private async Task RecordAsync(List<CloudJobFile> files, int state)
        {
            if (files.Count == 0) return;
            switch (job.Kind)
            {
                case JobKinds.Upload:
                    await RecordUploadsAsync(files);
                    break;
                case JobKinds.Move:
                    RecordMoves(files);
                    break;
                case JobKinds.Delete:
                    RecordDeletes(files);
                    break;
            }
            SetFiles(files, state);
            Interlocked.Add(ref _doneFiles, files.Count);
            Interlocked.Add(ref _doneBytes, files.Sum(f => f.Size));
            Report();
            jobs.Changed?.Invoke();
            if (job.Kind != JobKinds.Import) jobs.LibraryChanged?.Invoke();
            // A long job: the other computers see what is there so far, now and then.
            if (DateTime.UtcNow - _published > TimeSpan.FromMinutes(5))
            {
                _published = DateTime.UtcNow;
                Catalog.Publish(_db, target.Id);
            }
        }

        private async Task RecordUploadsAsync(List<CloudJobFile> files)
        {
            var media = files.Where(f => FileTypes.KindOf(f.Target) != null && !LibraryScanner.IsIgnored(f.Target)).ToList();
            if (media.Count == 0) return;
            // Known at that place with that size: it stays as it is (its thumbnail and analysis too).
            var known = new Dictionary<string, (long Id, long Size, bool Missing)>(StringComparer.Ordinal);
            using (var c = _db.Open())
                foreach (var chunk in media.Select(f => f.Target).Chunk(500))
                    foreach (var (path, id, size, missing) in c.Query<(string, long, long, bool)>(
                                 "SELECT rel_path, id, size, missing FROM media WHERE source_id=@Id AND rel_path IN @chunk", new { target.Id, chunk }))
                        known[path] = (id, size, missing);
            var scanned = new ConcurrentBag<ScannedFile>();
            await Parallel.ForEachAsync(media.Where(f => !known.TryGetValue(f.Target, out var k) || k.Missing || k.Size != f.Size),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (f, token) =>
                {
                    try
                    {
                        scanned.Add(await MediaProbe.ProbeAsync(f.Origin, f.Target, FileTypes.KindOf(f.Target)!.Value, new FileInfo(f.Origin), token));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Gone or locked right after its upload: the next scan of the folder reads it from the cloud.
                    }
                });
            _repo.Upsert(target.Id, scanned);
            var origins = media.GroupBy(f => f.Target, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Origin, StringComparer.Ordinal);
            foreach (var (path, id) in _repo.IdsOf(target.Id, origins.Keys))
                _repo.SetUploadOrigin(id, origins[path]);
        }

        private void RecordMoves(List<CloudJobFile> files)
        {
            var ids = _repo.IdsOf(from!.Id, files.Select(f => f.Origin));
            foreach (var f in files)
            {
                if (!ids.TryGetValue(f.Origin, out var id)) continue;
                _repo.MoveInCloud(id, target.Id, f.Target);
                // Its copy on this computer (fetched for viewing) moves along.
                MoveCached(Path.Combine(from.Root, f.Origin.Replace('/', Path.DirectorySeparatorChar)),
                    Path.Combine(target.Root, f.Target.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        private static void MoveCached(string cached, string to)
        {
            try
            {
                if (!File.Exists(cached)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(cached, to, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Open in the viewer: it is fetched again at its new place when needed.
            }
        }

        private void RecordDeletes(List<CloudJobFile> files)
        {
            var ids = _repo.IdsOf(target.Id, files.Select(f => f.Origin));
            _repo.SetMissing(ids.Values, true);
            foreach (var f in files)
            {
                try
                {
                    File.Delete(Path.Combine(target.Root, f.Origin.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        // ---- which files go together ----

        /// <summary>The folders a file goes from and to when it keeps its path under them. Alone: it gets another name, it goes by itself.</summary>
        private (string From, string To, bool Alone) Bases(CloudJobFile f)
        {
            var (root, origin) = Origin(f);
            var a = origin.Split('/');
            var b = f.Target.Split('/');
            var k = 0;
            while (k < a.Length && k < b.Length && a[^(k + 1)] == b[^(k + 1)]) k++;
            return k == 0 ? ("", "", true) : (root + string.Join('/', a[..^k]), CloudService.Join(target.Remote!, string.Join('/', b[..^k])), false);
        }

        /// <summary>The file's path under the folder it goes from.</summary>
        private string Rel(CloudJobFile f, string fromBase)
        {
            var (root, origin) = Origin(f);
            return (root + origin)[fromBase.Length..].TrimStart('/');
        }

        /// <summary>Where the file is now: what cannot be cut (the drive, the share, the storage) and its path below that.</summary>
        private (string Root, string Path) Origin(CloudJobFile f)
        {
            if (job.Kind != JobKinds.Upload)
            {
                var remote = job.Kind == JobKinds.Import ? spec.Remote! : from!.Remote!;
                return (remote.EndsWith(':') || remote.EndsWith('/') ? remote : remote + "/", f.Origin);
            }
            var full = Path.GetFullPath(f.Origin);
            var root = Path.GetPathRoot(full) ?? "";
            var rest = full[root.Length..].Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimStart('/');
            root = root.Replace(Path.DirectorySeparatorChar, '/');
            return (root.EndsWith('/') ? root : root + "/", rest);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
    }
}
