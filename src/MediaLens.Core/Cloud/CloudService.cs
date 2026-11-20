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
}

public sealed record S3Settings(string Provider, string Endpoint, string Region, string AccessKey, string SecretKey, string Bucket);

/// <summary>
/// Clouds, mirrors and cloud sources. A cloud is an rclone remote wrapped in a crypt remote: this computer
/// encrypts contents and names before the upload, with a key the application makes itself (it travels in the
/// recovery string), so the provider only ever stores noise. A mirror copies a local source into the cloud (new
/// and changed files, never deleting there), under a folder named after this computer. The library itself is
/// backed up there too (LibraryBackup), so another computer gets everything back from the recovery string.
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
            SELECT m.id, m.source_id, m.cloud_id, m.target, m.enabled, m.last_run_at, m.last_result,
                   s.name AS SourceName, s.root AS SourceRoot, c.name AS CloudName
            FROM mirror m JOIN source s ON s.id=m.source_id JOIN cloud c ON c.id=m.cloud_id ORDER BY s.name
            """).ToList();
    }

    private static string NewRemoteName() => "ml" + Guid.NewGuid().ToString("N")[..8];

    public async Task<CloudInfo> AddS3Async(string name, S3Settings s3, string? password, CancellationToken ct)
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
        return await FinishAsync(name, "s3", remote, $"{bucket}/{HomeFolder}", password, ct);
    }

    /// <summary>Opens the browser for the Microsoft sign-in, then finds the user's drive.</summary>
    public async Task<CloudInfo> AddOneDriveAsync(string name, string? password, CancellationToken ct)
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
        return await FinishAsync(name, "onedrive", remote, $"{remote}:{HomeFolder}", password, ct);
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
    public async Task<CloudInfo> AddGoogleDriveAsync(string name, string? password, CancellationToken ct)
    {
        var token = await Rclone.AuthorizeAsync("drive", ct);
        var remote = NewRemoteName();
        await Rclone.CreateRemoteAsync(remote, "drive", new Dictionary<string, string>
        {
            ["scope"] = "drive",
            ["token"] = token,
        }, ct);
        return await FinishAsync(name, "drive", remote, $"{remote}:{HomeFolder}", password, ct);
    }

    /// <summary>A folder as the "cloud": a NAS, an external disk, or the folder of a sync client.</summary>
    public async Task<CloudInfo> AddFolderAsync(string name, string folder, string? password, CancellationToken ct)
    {
        var remote = NewRemoteName();
        await Rclone.CreateRemoteAsync(remote, "local", new Dictionary<string, string>(), ct);
        return await FinishAsync(name, "folder", remote, $"{remote}:{folder.Replace('\\', '/')}", password, ct);
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

    /// <summary>
    /// Where the library backups of a cloud are. A cloud from before encryption was always on stores its mirrors
    /// readable; the library backup (it holds the keys of every cloud) still goes through an encrypted layer of its own.
    /// </summary>
    public async Task<string> BackupRootAsync(CloudInfo cloud, CancellationToken ct)
    {
        if (cloud.Encrypted) return Join(cloud.Remote, IndexFolder);
        var layer = cloud.Remote.Split(':')[0][..10] + "b";
        if (!(await Rclone.DumpAsync(ct)).ContainsKey(layer))
            await Rclone.CreateRemoteAsync(layer, "crypt", CryptOver(Join(cloud.Remote, IndexFolder), NewKey()), ct);
        return layer + ":";
    }

    /// <summary>What another computer needs to find this library in the cloud: see RecoveryString.</summary>
    public async Task<string> RecoveryStringAsync(CloudInfo cloud, CancellationToken ct)
    {
        var root = await BackupRootAsync(cloud, ct);
        var prefix = cloud.Remote.Split(':')[0][..10];
        var remotes = (await Rclone.DumpAsync(ct)).Where(r => r.Key == prefix || r.Key == prefix + "c" || r.Key == prefix + "b")
            .ToDictionary(r => r.Key, r => r.Value);
        return RecoveryString.Encode(new Recovery(cloud.Name, root, remotes));
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

    private async Task<CloudInfo> FinishAsync(string name, string kind, string baseRemote, string basePath, string? password, CancellationToken ct)
    {
        var remote = basePath;
        // Always encrypted; a password is only given to read a cloud encrypted with one (an older setup, plain rclone).
        var generated = string.IsNullOrEmpty(password);
        password = generated ? NewKey() : password;
        {
            var crypt = baseRemote + "c";
            await Rclone.CreateRemoteAsync(crypt, "crypt", new Dictionary<string, string>
            {
                ["remote"] = basePath,
                ["password"] = password,
                ["filename_encryption"] = "standard",
                ["directory_name_encryption"] = "true",
            }, ct);
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
            new { name, kind, remote, encrypted = password is { Length: > 0 }, now });
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
        foreach (var r in new[] { baseRemote + "c", baseRemote + "b", baseRemote })
        {
            try { await Rclone.DeleteRemoteAsync(r, ct); } catch (RcloneException) { }
        }
    }

    /// <summary>Forgets the cloud here; nothing is deleted in the cloud itself.</summary>
    public async Task RemoveCloudAsync(long id, CancellationToken ct)
    {
        var cloud = Clouds().FirstOrDefault(x => x.Id == id);
        if (cloud == null) return;
        // "mlXXXXXXXX" is the remote, "mlXXXXXXXXc" its crypt wrapper.
        await DeleteRemotesAsync(cloud.Remote.Split(':')[0][..10], ct);
        using var c = _db.Open();
        c.Execute("DELETE FROM cloud WHERE id=@id", new { id });
    }

    public MirrorInfo AddMirror(long sourceId, long cloudId)
    {
        var source = _repo.ListSources().First(s => s.Id == sourceId);
        var taken = Mirrors().Where(m => m.CloudId == cloudId).Select(m => m.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Under this computer's name: two computers on one cloud keep their folders apart.
        var folder = $"{Device.MirrorFolder}/{Sanitize(source.Name)}";
        var target = folder;
        for (var n = 2; taken.Contains(target); n++) target = $"{folder} {n}";
        using var c = _db.Open();
        var id = c.ExecuteScalar<long>("INSERT INTO mirror(source_id, cloud_id, target) VALUES(@sourceId, @cloudId, @target) RETURNING id",
            new { sourceId, cloudId, target });
        return Mirrors().First(m => m.Id == id);
    }

    private static string Sanitize(string name) => DeviceInfo.Safe(name);

    public void RemoveMirror(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM mirror WHERE id=@id", new { id });
    }

    /// <summary>Copies new and changed files of the source to the cloud, then the encrypted index.</summary>
    public async Task MirrorAsync(long mirrorId, IProgress<Rclone.CopyProgress>? progress, CancellationToken ct)
    {
        var mirror = Mirrors().First(m => m.Id == mirrorId);
        var cloud = Clouds().First(x => x.Id == mirror.CloudId);
        var target = Join(cloud.Remote, mirror.Target);
        string result;
        try
        {
            await Rclone.CopyAsync(mirror.SourceRoot, target,
                ["--exclude", ".*/**", "--exclude", ".*", "--exclude", "@eaDir/**", "--exclude", "$RECYCLE.BIN/**", "--exclude", "#recycle/**"],
                progress, ct);
            // Recorded before the library is backed up, so the backup knows this folder is in the cloud.
            Done("OK");
            // The library follows, into the cloud chosen for it (which may be another one, or none).
            if (BackupCloud is { } home) await Backup.RunAsync(home, full: true, null, ct);
            result = "OK";
        }
        catch (Exception e) when (e is RcloneException or IOException)
        {
            result = "Error: " + e.Message;
        }
        Done(result);
        if (result != "OK") throw new RcloneException(result);

        void Done(string outcome)
        {
            using var c = _db.Open();
            c.Execute("UPDATE mirror SET last_run_at=@now, last_result=@outcome WHERE id=@mirrorId",
                new { mirrorId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), outcome });
        }
    }

    /// <summary>
    /// The folders in a cloud (mirrors made here or on another computer). A folder named after a computer that
    /// backs its library up here holds that computer's mirrors: those are listed, as "computer/folder".
    /// </summary>
    public async Task<List<string>> FoldersAsync(long cloudId, CancellationToken ct)
    {
        var cloud = Clouds().First(x => x.Id == cloudId);
        var computers = (await Rclone.DirsAsync(await BackupRootAsync(cloud, ct), ct))
            .Select(d => d.LastIndexOf('-') is > 0 and var dash ? d[..dash] : d).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = new List<string>();
        foreach (var top in await Rclone.DirsAsync(cloud.Remote, ct))
        {
            if (top == IndexFolder) continue;
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
            var root = await BackupRootAsync(cloud, ct);
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
