using System.Net.Http.Headers;
using System.Text.Json;
using Dapper;
using MediaLens.Data;
using MediaLens.Library;

namespace MediaLens.Cloud;

public sealed class CloudInfo
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>s3, onedrive, drive, folder</summary>
    public string Kind { get; init; } = "";
    /// <summary>The rclone path everything of this cloud goes under (through the crypt remote when encrypted).</summary>
    public string Remote { get; init; } = "";
    public bool Encrypted { get; init; }
    public long CreatedAt { get; init; }

    public string KindTitle => Kind switch
    {
        "s3" => "S3",
        "onedrive" => "OneDrive",
        "drive" => "Google Drive",
        _ => "Folder",
    };
}

public sealed class MirrorInfo
{
    public long Id { get; init; }
    public long SourceId { get; init; }
    public long CloudId { get; init; }
    public string Target { get; init; } = "";
    public bool Enabled { get; init; }
    public long? LastRunAt { get; init; }
    public string? LastResult { get; init; }
    public string SourceName { get; init; } = "";
    public string SourceRoot { get; init; } = "";
    public string CloudName { get; init; } = "";
    public string CloudRemote { get; init; } = "";
    /// <summary>The folder's own crypt remote when it is encrypted by itself; null: the place under the cloud.</summary>
    public string? Remote { get; init; }
    public bool Encrypted { get; init; }
    /// <summary>Files deleted from the folder that are still in the cloud, waiting for the user's word.</summary>
    public long Gone { get; init; }
    public long GoneBytes { get; init; }

    /// <summary>The rclone path of the copy in the cloud.</summary>
    public string Place => Remote ?? CloudService.Join(CloudRemote, Target);

    public bool LastOk => LastResult == "OK";
}

public sealed record S3Settings(string Provider, string Endpoint, string Region, string AccessKey, string SecretKey, string Bucket);

/// <summary>
/// Clouds, mirrors and cloud sources. A cloud is an rclone remote, plain or wrapped in a crypt remote (this
/// computer then encrypts contents and names before the upload, with a key the application makes itself, so
/// the provider only stores noise). A mirror copies a local source into the cloud (new and changed files, never
/// deleting there), under a folder named after this computer. The library itself is backed up to the cloud
/// chosen for it (LibraryBackup): index, search data and thumbnails as they are, the access keys of the clouds
/// always encrypted. Another computer gets everything back from the recovery string.
/// </summary>
public sealed class CloudService
{
    public const string IndexFolder = ".medialens";

    private readonly AppPaths _paths;
    private readonly Database _db;
    private readonly MediaRepository _repo;

    public Rclone Rclone { get; }
    public DeviceInfo Device { get; }
    public LibraryBackup Backup { get; }

    /// <summary>
    /// What tells this library's place in a bucket from another installation's: made once, restored with the
    /// library, so a computer set up from the recovery string writes to the same place.
    /// </summary>
    public string LibraryId
    {
        get
        {
            if (_db.GetSetting("library_id") is { Length: > 0 } id) return id;
            id = Guid.NewGuid().ToString("N")[..8];
            _db.SetSetting("library_id", id);
            return id;
        }
    }

    private string HomeFolder => "media-lens-" + LibraryId;

    public CloudService(AppPaths paths, Database db, MediaRepository repo)
    {
        _paths = paths;
        _db = db;
        _repo = repo;
        Device = DeviceInfo.Load(paths.DataDir);
        Backup = new LibraryBackup(paths, db, this);
        Rclone = new Rclone(Path.Combine(paths.DataDir, "rclone.conf"));
        MediaFiles.Configure(Rclone, Path.Combine(paths.CacheDir, "cloud"));
    }

    public static string Join(string remote, string path)
    {
        path = path.Trim('/');
        if (path.Length == 0) return remote;
        return remote.EndsWith(':') || remote.EndsWith('/') ? remote + path : remote + "/" + path;
    }

    public List<CloudInfo> Clouds()
    {
        using var c = _db.Open();
        return c.Query<CloudInfo>("SELECT * FROM cloud ORDER BY name").ToList();
    }

    public List<MirrorInfo> Mirrors()
    {
        using var c = _db.Open();
        return c.Query<MirrorInfo>("""
            SELECT m.id, m.source_id, m.cloud_id, m.target, m.enabled, m.last_run_at, m.last_result, m.remote, m.encrypted,
                   s.name AS SourceName, s.root AS SourceRoot, c.name AS CloudName, c.remote AS CloudRemote,
                   (SELECT COUNT(*) FROM mirror_gone g WHERE g.mirror_id=m.id) AS Gone,
                   (SELECT COALESCE(SUM(size), 0) FROM mirror_gone g WHERE g.mirror_id=m.id) AS GoneBytes
            FROM mirror m JOIN source s ON s.id=m.source_id JOIN cloud c ON c.id=m.cloud_id ORDER BY s.name
            """).ToList();
    }

    private static string NewRemoteName() => "ml" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The four ways to add a cloud share this: encrypted with the given password, or with a key made here when
    /// there is none; with encrypt false the files are stored as they are (readable at the provider).
    /// </summary>
    public async Task<CloudInfo> AddS3Async(string name, S3Settings s3, string? password, CancellationToken ct, bool encrypt = true)
    {
        var remote = NewRemoteName();
        var parameters = new Dictionary<string, string>
        {
            ["provider"] = string.IsNullOrWhiteSpace(s3.Provider) ? "AWS" : s3.Provider,
            ["access_key_id"] = s3.AccessKey,
            ["secret_access_key"] = s3.SecretKey,
        };
        if (!string.IsNullOrWhiteSpace(s3.Region)) parameters["region"] = s3.Region;
        if (!string.IsNullOrWhiteSpace(s3.Endpoint)) parameters["endpoint"] = s3.Endpoint;
        await Rclone.CreateRemoteAsync(remote, "s3", parameters, ct);
        // IDrive e2 has an address per account and region; rclone asks IDrive for it by the access key, so nobody
        // has to look it up. No address back: IDrive does not know the key.
        if (parameters["provider"] == "IDrive" && !(await Rclone.DumpAsync(ct)).GetValueOrDefault(remote, []).ContainsKey("endpoint"))
        {
            await DeleteRemotesAsync(remote, CancellationToken.None);
            throw new RcloneException("IDrive e2 does not know this access key. Copy it again from IDrive e2, Access Keys.");
        }
        // The bucket: used as it is when the key can read it (a key for one bucket only may not create buckets, and
        // must never be made to try), otherwise made. From then on rclone is told not to check or create it again.
        var bucket = $"{remote}:{s3.Bucket.Trim('/')}";
        try
        {
            try
            {
                await Rclone.RunAsync(["lsjson", "--max-depth", "1", bucket], ct);
            }
            catch (RcloneException e) when (!new[] { "SignatureDoesNotMatch", "InvalidAccessKeyId", "RequestError", "no such host", "dial tcp" }
                                               .Any(part => e.Message.Contains(part, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    await Rclone.RunAsync(["mkdir", bucket], ct);
                }
                catch (RcloneException)
                {
                    throw new RcloneException($"The bucket \"{s3.Bucket.Trim('/')}\" is not there (or this key may not use it), and the key is not allowed " +
                                              "to make it. Make the bucket at the provider, or use a key that may create buckets.");
                }
            }
            await Rclone.RunAsync(["config", "update", remote, "no_check_bucket", "true", "--non-interactive"], ct);
        }
        catch
        {
            await DeleteRemotesAsync(remote, CancellationToken.None);
            throw;
        }
        return await FinishAsync(name, "s3", remote, $"{bucket}/{HomeFolder}", password, encrypt, ct);
    }

    /// <summary>Opens the browser for the Microsoft sign-in, then finds the user's drive.</summary>
    public async Task<CloudInfo> AddOneDriveAsync(string name, string? password, CancellationToken ct, bool encrypt = true)
    {
        var token = await Rclone.AuthorizeAsync("onedrive", ct);
        var (driveId, driveType) = await OneDriveOfAsync(token, ct);
        var remote = NewRemoteName();
        await Rclone.CreateRemoteAsync(remote, "onedrive", new Dictionary<string, string>
        {
            ["token"] = token,
            ["drive_id"] = driveId,
            ["drive_type"] = driveType,
        }, ct);
        return await FinishAsync(name, "onedrive", remote, $"{remote}:{HomeFolder}", password, encrypt, ct);
    }

    private static async Task<(string Id, string Type)> OneDriveOfAsync(string tokenJson, CancellationToken ct)
    {
        using var token = JsonDocument.Parse(tokenJson);
        var accessToken = token.RootElement.GetProperty("access_token").GetString();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var doc = JsonDocument.Parse(await http.GetStringAsync("https://graph.microsoft.com/v1.0/me/drive", ct));
        return (doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("driveType").GetString()!);
    }

    /// <summary>Opens the browser for the Google sign-in.</summary>
    public async Task<CloudInfo> AddGoogleDriveAsync(string name, string? password, CancellationToken ct, bool encrypt = true)
    {
        var token = await Rclone.AuthorizeAsync("drive", ct);
        var remote = NewRemoteName();
        await Rclone.CreateRemoteAsync(remote, "drive", new Dictionary<string, string>
        {
            ["scope"] = "drive",
            ["token"] = token,
        }, ct);
        return await FinishAsync(name, "drive", remote, $"{remote}:{HomeFolder}", password, encrypt, ct);
    }

    /// <summary>A folder as the "cloud": a NAS, an external disk, or the folder of a sync client.</summary>
    public async Task<CloudInfo> AddFolderAsync(string name, string folder, string? password, CancellationToken ct, bool encrypt = true)
    {
        var remote = NewRemoteName();
        await Rclone.CreateRemoteAsync(remote, "local", new Dictionary<string, string>(), ct);
        return await FinishAsync(name, "folder", remote, $"{remote}:{folder.Replace('\\', '/')}", password, encrypt, ct);
    }

    /// <summary>An encryption key nobody has to think up or remember: it is part of the recovery string.</summary>
    private static string NewKey() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

    private Dictionary<string, string> CryptOver(string path, string key) => new()
    {
        ["remote"] = path,
        ["password"] = key,
        ["filename_encryption"] = "standard",
        ["directory_name_encryption"] = "true",
    };

    /// <summary>Where the library backups of a cloud are, a folder for each computer.</summary>
    public string BackupRoot(CloudInfo cloud) => Join(cloud.Remote, IndexFolder);

    /// <summary>
    /// Where the access keys of the clouds are backed up (rclone.conf of each computer): always encrypted. In an
    /// encrypted cloud that is the backup itself; in a plain one an encrypted folder of its own beside it, with a
    /// key made here that travels in the recovery string.
    /// </summary>
    public async Task<string> KeysRootAsync(CloudInfo cloud, CancellationToken ct)
    {
        if (cloud.Encrypted) return BackupRoot(cloud);
        var layer = cloud.Remote.Split(':')[0][..10] + "k";
        if (!(await Rclone.DumpAsync(ct)).ContainsKey(layer))
            await Rclone.CreateRemoteAsync(layer, "crypt", CryptOver(Join(cloud.Remote, IndexFolder + "-keys"), NewKey()), ct);
        return layer + ":";
    }

    /// <summary>What another computer needs to find this library in the cloud: see RecoveryString.</summary>
    public async Task<string> RecoveryStringAsync(CloudInfo cloud, CancellationToken ct)
    {
        var keys = await KeysRootAsync(cloud, ct);
        var prefix = cloud.Remote.Split(':')[0][..10];
        var remotes = (await Rclone.DumpAsync(ct)).Where(r => r.Key == prefix || r.Key == prefix + "c" || r.Key == prefix + "k")
            .ToDictionary(r => r.Key, r => r.Value);
        return RecoveryString.Encode(new Recovery(cloud.Name, BackupRoot(cloud), remotes, keys));
    }

    /// <summary>
    /// The cloud the library itself (settings, names, search data, thumbnails) is backed up to. Chosen by the user,
    /// possibly another one than the photos and videos go to; adding a cloud does not choose it. Null: not backed up.
    /// </summary>
    public CloudInfo? BackupCloud
    {
        get => _db.GetSetting("backup_cloud_id") is { Length: > 0 } id ? Clouds().FirstOrDefault(c => c.Id.ToString() == id) : null;
        set => _db.SetSetting("backup_cloud_id", value?.Id.ToString());
    }

    private async Task<CloudInfo> FinishAsync(string name, string kind, string baseRemote, string basePath, string? password, bool encrypt,
        CancellationToken ct)
    {
        var remote = basePath;
        // A password is only given to read a cloud encrypted with one (an older setup, plain rclone).
        var generated = string.IsNullOrEmpty(password);
        if (encrypt)
        {
            var crypt = baseRemote + "c";
            await Rclone.CreateRemoteAsync(crypt, "crypt", CryptOver(basePath, generated ? NewKey() : password!), ct);
            remote = crypt + ":";
        }
        try
        {
            await Rclone.TestAsync(remote, ct);
            if (remote != basePath && await CountAsync(basePath, ct) > 0 && await CountAsync(remote, ct) == 0)
                throw new RcloneException(generated
                    ? "This place already holds a library encrypted with another key. Bring it back with its recovery string, or use another bucket or folder."
                    : "Wrong password: the cloud folder holds data encrypted with another password.");
        }
        catch
        {
            await DeleteRemotesAsync(baseRemote, CancellationToken.None);
            throw;
        }
        using var c = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = c.ExecuteScalar<long>(
            "INSERT INTO cloud(name, kind, remote, encrypted, created_at) VALUES(@name, @kind, @remote, @encrypted, @now) RETURNING id",
            new { name, kind, remote, encrypted = encrypt, now });
        return Clouds().First(x => x.Id == id);
    }

    private async Task<int> CountAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Rclone.RunAsync(["lsjson", "--max-depth", "1", remotePath], ct));
            return doc.RootElement.GetArrayLength();
        }
        catch (RcloneException)
        {
            return 0;
        }
    }

    private async Task DeleteRemotesAsync(string baseRemote, CancellationToken ct)
    {
        foreach (var r in new[] { baseRemote + "c", baseRemote + "k", baseRemote })
        {
            try { await Rclone.DeleteRemoteAsync(r, ct); } catch (RcloneException) { }
        }
    }

    /// <summary>Forgets the cloud here; nothing is deleted in the cloud itself.</summary>
    public async Task RemoveCloudAsync(long id, CancellationToken ct)
    {
        var cloud = Clouds().FirstOrDefault(x => x.Id == id);
        if (cloud == null) return;
        // "mlXXXXXXXX" is the remote, the others start with it: its crypt wrapper (c), the keys (k), encrypted folders (f).
        var prefix = Prefix(cloud);
        foreach (var name in (await Rclone.DumpAsync(ct)).Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderDescending())
        {
            try { await Rclone.DeleteRemoteAsync(name, ct); } catch (RcloneException) { }
        }
        using var c = _db.Open();
        c.Execute("DELETE FROM cloud WHERE id=@id", new { id });
    }

    private static string Prefix(CloudInfo cloud) => cloud.Remote.Split(':')[0][..10];

    /// <summary>A folder backed up there or a source living there: the cloud is still needed.</summary>
    public bool CloudInUse(long id)
    {
        var cloud = Clouds().FirstOrDefault(x => x.Id == id);
        if (cloud == null) return false;
        var prefix = Prefix(cloud);
        return Mirrors().Any(m => m.CloudId == id) ||
               _repo.ListSources().Any(s => s.CloudId == id || s.Remote?.StartsWith(prefix, StringComparison.Ordinal) == true);
    }

    /// <summary>The cloud a cloud source is in.</summary>
    public CloudInfo? CloudOf(Source source) =>
        source.Remote == null ? null
        : Clouds().FirstOrDefault(c => c.Id == source.CloudId) ?? Clouds().FirstOrDefault(c => source.Remote.StartsWith(Prefix(c), StringComparison.Ordinal));

    /// <summary>
    /// Connects a local folder with a cloud as its backup: under this computer's name (two computers on one cloud keep
    /// their folders apart). Encrypted: the folder gets a crypt remote of its own with a key made here (it travels
    /// with the library's backup); the folder's name stays readable, the names and contents inside do not.
    /// </summary>
    public async Task<MirrorInfo> AddMirrorAsync(long sourceId, long cloudId, bool encrypt, CancellationToken ct, bool clone = false)
    {
        var source = _repo.ListSources().First(s => s.Id == sourceId);
        var cloud = Clouds().First(x => x.Id == cloudId);
        var taken = Mirrors().Where(m => m.CloudId == cloudId).Select(m => m.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A folder of that name from before is used again only when it is this computer's plain backup (or empty).
        var there = (await Rclone.DirsAsync(Join(cloud.Remote, Device.MirrorFolder), ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folder = $"{Device.MirrorFolder}/{Sanitize(source.Name)}";
        var target = folder;
        for (var n = 2; taken.Contains(target) || there.Contains(target[(Device.MirrorFolder.Length + 1)..]) && !await ReusableAsync(Join(cloud.Remote, target)); n++)
            target = $"{folder} {n}";
        string? remote = null;
        var encrypted = cloud.Encrypted;
        if (encrypt && !cloud.Encrypted)
        {
            remote = Prefix(cloud) + "f" + Guid.NewGuid().ToString("N")[..6];
            await Rclone.CreateRemoteAsync(remote, "crypt", CryptOver(Join(cloud.Remote, target), NewKey()), ct);
            remote += ":";
            encrypted = true;
        }
        using var c = _db.Open();
        var id = c.ExecuteScalar<long>("""
            INSERT INTO mirror(source_id, cloud_id, target, remote, encrypted) VALUES(@sourceId, @cloudId, @target, @remote, @encrypted) RETURNING id
            """, new { sourceId, cloudId, target, remote, encrypted });
        return Mirrors().First(m => m.Id == id);

        async Task<bool> ReusableAsync(string place)
        {
            // Under this computer's name without a marker: an older copy made here. A clone starts in a folder of its
            // own: files of an older copy would wait there as deleted from the folder.
            if (encrypt || clone) return false;
            var marker = await FolderMarker.ReadAsync(Rclone, place, ct);
            return marker == null || marker.IsBackup && marker.Home == Device.Id;
        }
    }

    private static string Sanitize(string name) => DeviceInfo.Safe(name);

    /// <summary>
    /// Stops backing the folder up; the copy stays in the cloud as it is. An encrypted folder keeps its key in the
    /// library, so the copy can still be read.
    /// </summary>
    public void RemoveMirror(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM mirror WHERE id=@id", new { id });
    }

    /// <summary>
    /// Brings the folder's copy in the cloud up to date (see FolderSync): moves what was moved, uploads what is new or
    /// changed, notes what was deleted here (deleted in the cloud only when the user says so). Then the library.
    /// </summary>
    public async Task<SyncResult> MirrorAsync(long mirrorId, IProgress<Rclone.CopyProgress>? progress, CancellationToken ct)
    {
        var mirror = Mirrors().First(m => m.Id == mirrorId);
        var place = mirror.Place;
        string result;
        var done = new SyncResult(0, 0, 0);
        try
        {
            if (!Directory.Exists(mirror.SourceRoot))
                throw new IOException("The folder is not on this computer now (a disk that is not connected?): nothing was backed up.");
            var marker = await FolderMarker.ReadAsync(Rclone, place, ct);
            if (marker != null && (!marker.IsBackup || marker.Home != Device.Id))
            {
                // Another computer made the copy its own, or it was moved to the cloud: this folder must not change it.
                var why = marker.IsBackup
                    ? $"Stopped: {marker.HomeName} took this backup over, its folder decides what is in the cloud now."
                    : "Stopped: the folder in the cloud lives on its own now (moved to the cloud).";
                using (var c = _db.Open()) c.Execute("UPDATE mirror SET enabled=0 WHERE id=@mirrorId", new { mirrorId });
                Done(why);
                throw new RcloneException(why);
            }
            var local = FolderSync.ListLocal(mirror.SourceRoot);
            var inCloud = await Rclone.ListAsync(place, -1, ct);
            if (local.Count == 0 && inCloud.Any(f => !LibraryScanner.IsIgnored(f.Path)))
                throw new IOException("The folder is empty on this computer: nothing was changed in the cloud. If it is on a disk, connect the disk.");
            var plan = FolderSync.Plan(local, inCloud);

            var moved = 0;
            await Parallel.ForEachAsync(plan.Moves, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (m, token) =>
            {
                await Rclone.MoveFileAsync(Join(place, m.From), Join(place, m.To), token);
                var n = Interlocked.Increment(ref moved);
                progress?.Report(new Rclone.CopyProgress(0, 0, n * 100 / plan.Moves.Count, $"moved {n} of {plan.Moves.Count} in the cloud"));
            });
            await Rclone.CopyFilesAsync(mirror.SourceRoot, place, plan.Upload, progress, ct);
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                c.Execute("DELETE FROM mirror_gone WHERE mirror_id=@mirrorId", new { mirrorId }, tx);
                foreach (var f in plan.Gone)
                    c.Execute("INSERT INTO mirror_gone(mirror_id, path, size) VALUES(@mirrorId, @Path, @Size)", new { mirrorId, f.Path, f.Size }, tx);
                tx.Commit();
            }
            var mine = new FolderMarker(FolderMarker.Backup, Device.Id, Device.Name, mirror.SourceName);
            if (marker != mine) await mine.WriteAsync(Rclone, place, ct);
            done = new SyncResult(plan.Upload.Count, plan.Moves.Count, plan.Gone.Count);
            // Recorded before the library is backed up, so the backup knows this folder is in the cloud.
            Done("OK");
            // The library follows, into the cloud chosen for it (which may be another one, or none).
            if (BackupCloud is { } home) await Backup.RunAsync(home, full: true, null, ct);
            result = "OK";
        }
        catch (Exception e) when (e is RcloneException or IOException)
        {
            if (e.Message.StartsWith("Stopped:", StringComparison.Ordinal)) throw;
            result = "Error: " + e.Message;
        }
        Done(result);
        if (result != "OK") throw new RcloneException(result);
        return done;

        void Done(string outcome)
        {
            using var c = _db.Open();
            c.Execute("UPDATE mirror SET last_run_at=@now, last_result=@outcome WHERE id=@mirrorId",
                new { mirrorId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), outcome });
        }
    }

    /// <summary>The files deleted from the folder that are still in its copy in the cloud.</summary>
    public List<RemoteFile> Gone(long mirrorId)
    {
        using var c = _db.Open();
        return c.Query<(string Path, long Size)>("SELECT path, size FROM mirror_gone WHERE mirror_id=@mirrorId ORDER BY path", new { mirrorId })
            .Select(g => new RemoteFile(g.Path, g.Size, default)).ToList();
    }

    /// <summary>The user said so: what was deleted from the folder is deleted in the cloud too (not what came back since).</summary>
    public async Task DeleteGoneAsync(long mirrorId, CancellationToken ct)
    {
        var mirror = Mirrors().First(m => m.Id == mirrorId);
        var gone = Gone(mirrorId).Select(g => g.Path)
            .Where(p => !File.Exists(Path.Combine(mirror.SourceRoot, p.Replace('/', Path.DirectorySeparatorChar)))).ToList();
        await Rclone.DeleteFilesAsync(mirror.Place, gone, ct);
        using var c = _db.Open();
        c.Execute("DELETE FROM mirror_gone WHERE mirror_id=@mirrorId", new { mirrorId });
    }

    /// <summary>Deleted by mistake: the files come back from the cloud into the folder.</summary>
    public async Task BringBackGoneAsync(long mirrorId, IProgress<Rclone.CopyProgress>? progress, CancellationToken ct)
    {
        var mirror = Mirrors().First(m => m.Id == mirrorId);
        await Rclone.CopyFilesAsync(mirror.Place, mirror.SourceRoot, Gone(mirrorId).Select(g => g.Path).ToList(), progress, ct);
        using var c = _db.Open();
        c.Execute("DELETE FROM mirror_gone WHERE mirror_id=@mirrorId", new { mirrorId });
    }

    /// <summary>
    /// The user wants the folder cloned to the cloud: done by the scheduler once the backup is complete (which may take
    /// hours the first time) and nothing deleted waits for a decision.
    /// </summary>
    public void RequestMoveToCloud(long mirrorId, bool move) => _db.SetSetting($"move_to_cloud_{mirrorId}", move ? "1" : null);

    public bool MoveRequested(long mirrorId) => _db.GetSetting($"move_to_cloud_{mirrorId}") == "1";

    /// <summary>
    /// The name of a folder's clone: the folder and the cloud, or its folder in the cloud ("Photos 2") when a source of
    /// that name is there already (a clone made before).
    /// </summary>
    public string CloneName(string sourceName, MirrorInfo mirror)
    {
        var name = $"{sourceName} ({mirror.CloudName})";
        return _repo.ListSources().Any(s => s.Name == name) ? $"{mirror.Target[(mirror.Target.LastIndexOf('/') + 1)..]} ({mirror.CloudName})" : name;
    }

    /// <summary>
    /// The folder is cloned to the cloud (nothing on this computer is deleted): after a last backup, the copy in the cloud becomes a source of its own that lives
    /// there (everything known about the files stays with it: names, buckets). The folder on this computer stays in
    /// the library as another source, its files get what is known from their twins without being read again.
    /// Returns the id of the source of the local folder.
    /// </summary>
    public async Task<long> MoveToCloudAsync(long mirrorId, IProgress<Rclone.CopyProgress>? progress, CancellationToken ct)
    {
        await MirrorAsync(mirrorId, progress, ct);
        var mirror = Mirrors().First(m => m.Id == mirrorId);
        if (mirror.Gone > 0)
            throw new InvalidOperationException($"First decide about the {mirror.Gone} files deleted from the folder that are still in the cloud.");
        var source = _repo.ListSources().First(s => s.Id == mirror.SourceId);
        var cloud = Clouds().First(x => x.Id == mirror.CloudId);
        // The library knows the folder as it is now (moved and deleted files), so both sources start from the same.
        await new LibraryScanner(_repo, Rclone).ScanAsync(source, null, ct);
        await new FolderMarker(FolderMarker.Shared, Device.Id, Device.Name, source.Name).WriteAsync(Rclone, mirror.Place, ct);
        var cache = Path.Combine(_paths.CacheDir, "cloud", source.Id.ToString());
        var name = CloneName(source.Name, mirror);
        long localId;
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            localId = c.ExecuteScalar<long>("INSERT INTO source(kind, root, name, added_at) VALUES(@kind, @Root, @Name, @now) RETURNING id",
                new { kind = SourceKinds.Local, source.Root, source.Name, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx);
            c.Execute("""
                INSERT INTO media(source_id, rel_path, kind, size, mtime, quick_hash, taken_at, width, height, duration_ms, lat, lon, camera, event_id)
                SELECT @localId, rel_path, kind, size, mtime, quick_hash, taken_at, width, height, duration_ms, lat, lon, camera, event_id
                FROM media WHERE source_id=@Id AND missing=0
                """, new { localId, source.Id }, tx);
            c.Execute("""
                UPDATE source SET kind=@kind, root=@cache, remote=@remote, cloud_id=@cloudId, name=@name, read_only=0, home_name=NULL WHERE id=@Id
                """, new { kind = SourceKinds.Cloud, cache, remote = mirror.Place, cloudId = cloud.Id, name, source.Id }, tx);
            c.Execute("DELETE FROM mirror WHERE id=@mirrorId", new { mirrorId }, tx);
            tx.Commit();
        }
        Directory.CreateDirectory(cache);
        return localId;
    }

    /// <summary>
    /// A folder in the cloud gets a home on this computer: downloaded into the folder (files already there are not
    /// downloaded again), the source becomes that local folder (everything known stays), and its copy in the cloud is
    /// its backup from now on, this computer's. For a folder moved to the cloud, and after losing a computer. A chosen
    /// folder that holds other things gets it as a folder of its own inside. Returns where it is.
    /// </summary>
    public async Task<string> MakeHomeAsync(long sourceId, string folder, IProgress<Rclone.CopyProgress>? progress, CancellationToken ct)
    {
        var source = _repo.ListSources().First(s => s.Id == sourceId);
        if (source.Kind != SourceKinds.Cloud || source.Remote is not { } remote) throw new InvalidOperationException("This is not a folder in a cloud.");
        var cloud = CloudOf(source) ?? throw new InvalidOperationException("The cloud of this folder is not connected.");
        var name = source.Name.EndsWith($" ({cloud.Name})", StringComparison.Ordinal) ? source.Name[..^(cloud.Name.Length + 3)] : source.Name;
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() &&
            !string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), name, StringComparison.OrdinalIgnoreCase))
            folder = Path.Combine(folder, Sanitize(name));
        Directory.CreateDirectory(folder);
        await Rclone.CopyAsync(remote, folder, ["--exclude", ".*", "--exclude", ".*/**"], progress, ct);
        var target = remote.StartsWith(cloud.Remote, StringComparison.Ordinal) ? remote[cloud.Remote.Length..].Trim('/') : name;
        var encrypted = cloud.Encrypted || !remote.StartsWith(cloud.Remote, StringComparison.Ordinal);
        var full = Path.GetFullPath(folder);
        using (var c = _db.Open())
        using (var tx = c.BeginTransaction())
        {
            // The same folder kept as a source of its own (after the move to the cloud): this one takes its place.
            foreach (var other in _repo.ListSources().Where(s => s.Id != sourceId && s.Kind == SourceKinds.Local &&
                                                                 string.Equals(Path.GetFullPath(s.Root), full, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
                c.Execute("DELETE FROM source WHERE id=@Id", new { other.Id }, tx);
            c.Execute("""
                UPDATE source SET kind=@kind, root=@full, remote=NULL, cloud_id=NULL, read_only=0, home_name=NULL, name=@name WHERE id=@sourceId
                """, new { kind = SourceKinds.Local, full, name, sourceId }, tx);
            c.Execute("""
                INSERT INTO mirror(source_id, cloud_id, target, remote, encrypted) VALUES(@sourceId, @cloudId, @target, @mine, @encrypted)
                """, new { sourceId, cloudId = cloud.Id, target, mine = remote == Join(cloud.Remote, target) ? null : remote, encrypted }, tx);
            tx.Commit();
        }
        await new FolderMarker(FolderMarker.Backup, Device.Id, Device.Name, name).WriteAsync(Rclone, remote, ct);
        var cache = Path.Combine(_paths.CacheDir, "cloud", sourceId.ToString());
        try { if (Directory.Exists(cache)) Directory.Delete(cache, true); } catch (IOException) { }
        return full;
    }

    /// <summary>
    /// The folders in a cloud (mirrors made here or on another computer). A folder named after a computer that
    /// backs its library up here holds that computer's mirrors: those are listed, as "computer/folder".
    /// </summary>
    public async Task<List<string>> FoldersAsync(long cloudId, CancellationToken ct)
    {
        var cloud = Clouds().First(x => x.Id == cloudId);
        var computers = (await Rclone.DirsAsync(BackupRoot(cloud), ct))
            .Select(d => d.LastIndexOf('-') is > 0 and var dash ? d[..dash] : d).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (var top in await Rclone.DirsAsync(cloud.Remote, ct))
        {
            if (top.StartsWith(IndexFolder, StringComparison.Ordinal)) continue;
            if (!computers.Contains(top)) folders.Add(top);
            else folders.AddRange((await Rclone.DirsAsync(Join(cloud.Remote, top), ct)).Select(sub => $"{top}/{sub}"));
        }
        return folders.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Downloads a folder of the cloud into a local folder (after losing a disk).</summary>
    public Task RestoreAsync(long cloudId, string folder, string localDir, IProgress<Rclone.CopyProgress>? progress, CancellationToken ct)
    {
        var cloud = Clouds().First(x => x.Id == cloudId);
        Directory.CreateDirectory(localDir);
        return Rclone.CopyAsync(Join(cloud.Remote, folder), localDir, [], progress, ct);
    }

    /// <summary>A folder of the cloud as a source: browsed and analyzed without keeping a full local copy.</summary>
    public Source AddCloudSource(long cloudId, string folder)
    {
        var cloud = Clouds().First(x => x.Id == cloudId);
        var source = _repo.AddSource(SourceKinds.Cloud, "", $"{folder} ({cloud.Name})", Join(cloud.Remote, folder));
        var root = Path.Combine(_paths.CacheDir, "cloud", source.Id.ToString());
        Directory.CreateDirectory(root);
        _repo.SetSourceRoot(source.Id, root);
        return source;
    }

    /// <summary>The newest index backup of a cloud (of any computer), downloaded for an import; null when there is none.</summary>
    public async Task<string?> DownloadIndexAsync(long cloudId, CancellationToken ct)
    {
        var cloud = Clouds().First(x => x.Id == cloudId);
        var local = Path.Combine(_paths.CacheDir, $"index-{cloudId}.db");
        try
        {
            var root = BackupRoot(cloud);
            var newest = (await Rclone.ListAsync(root, 2, ct)).Where(f => Path.GetFileName(f.Path) == LibraryBackup.DbName)
                .OrderByDescending(f => f.ModTime).FirstOrDefault();
            // Backups made before every computer had its own folder.
            var file = newest != null ? Join(root, newest.Path) : Join(cloud.Remote, IndexFolder + "/" + LibraryBackup.DbName);
            await Rclone.DownloadAsync(file, local, ct);
            return local;
        }
        catch (RcloneException)
        {
            return null;
        }
    }
}
