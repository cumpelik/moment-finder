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
/// Clouds, mirrors and cloud sources. A cloud is an rclone remote; with a password it is wrapped in a crypt
/// remote so the provider only ever stores encrypted contents and names. A mirror copies a local source
/// into the cloud (new and changed files, never deleting there) together with an encrypted copy of the
/// index, so a restore brings back names, people and buckets without analyzing everything again.
/// </summary>
public sealed class CloudService
{
    public const string IndexFolder = ".medialens";

    private readonly AppPaths _paths;
    private readonly Database _db;
    private readonly MediaRepository _repo;

    public Rclone Rclone { get; }

    public CloudService(AppPaths paths, Database db, MediaRepository repo)
    {
        _paths = paths;
        _db = db;
        _repo = repo;
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
        return await FinishAsync(name, "s3", remote, $"{remote}:{s3.Bucket.Trim('/')}/MediaLens", password, ct);
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
        return await FinishAsync(name, "onedrive", remote, $"{remote}:MediaLens", password, ct);
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
        return await FinishAsync(name, "drive", remote, $"{remote}:MediaLens", password, ct);
    }

    /// <summary>A folder as the "cloud": a NAS, an external disk, or the folder of a sync client.</summary>
    public async Task<CloudInfo> AddFolderAsync(string name, string folder, string? password, CancellationToken ct)
    {
        var remote = NewRemoteName();
        await Rclone.CreateRemoteAsync(remote, "local", new Dictionary<string, string>(), ct);
        return await FinishAsync(name, "folder", remote, $"{remote}:{folder.Replace('\\', '/')}", password, ct);
    }

    private async Task<CloudInfo> FinishAsync(string name, string kind, string baseRemote, string basePath, string? password, CancellationToken ct)
    {
        var remote = basePath;
        if (!string.IsNullOrEmpty(password))
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
                throw new RcloneException("Wrong password: the cloud folder holds data encrypted with another password.");
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
        foreach (var r in new[] { baseRemote + "c", baseRemote })
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
        var target = Sanitize(source.Name);
        for (var n = 2; taken.Contains(target); n++) target = $"{Sanitize(source.Name)} {n}";
        using var c = _db.Open();
        var id = c.ExecuteScalar<long>("INSERT INTO mirror(source_id, cloud_id, target) VALUES(@sourceId, @cloudId, @target) RETURNING id",
            new { sourceId, cloudId, target });
        return Mirrors().First(m => m.Id == id);
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch)).Trim();

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
            await BackupIndexAsync(cloud, ct);
            result = "OK";
        }
        catch (Exception e) when (e is RcloneException or IOException)
        {
            result = "Error: " + e.Message;
        }
        using (var c = _db.Open())
            c.Execute("UPDATE mirror SET last_run_at=@now, last_result=@result WHERE id=@mirrorId",
                new { mirrorId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), result });
        if (result != "OK") throw new RcloneException(result);
    }

    /// <summary>A consistent copy of the index (VACUUM INTO), uploaded next to the mirrored folders.</summary>
    private async Task BackupIndexAsync(CloudInfo cloud, CancellationToken ct)
    {
        var tmp = Path.Combine(_paths.CacheDir, "index-backup.db");
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var c = _db.Open())
            c.Execute("VACUUM INTO @tmp", new { tmp });
        try
        {
            await Rclone.UploadAsync(tmp, Join(cloud.Remote, IndexFolder + "/library.db"), ct);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(tmp);
        }
    }

    /// <summary>The folders in a cloud (mirrors made here or on another computer).</summary>
    public async Task<List<string>> FoldersAsync(long cloudId, CancellationToken ct)
    {
        var cloud = Clouds().First(x => x.Id == cloudId);
        var json = await Rclone.RunAsync(["lsjson", "--dirs-only", "--max-depth", "1", cloud.Remote], ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("Path").GetString()!)
            .Where(p => p != IndexFolder).Order(StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>The index backup of a cloud, downloaded for an import (null when the cloud has none).</summary>
    public async Task<string?> DownloadIndexAsync(long cloudId, CancellationToken ct)
    {
        var cloud = Clouds().First(x => x.Id == cloudId);
        var local = Path.Combine(_paths.CacheDir, $"index-{cloudId}.db");
        try
        {
            await Rclone.DownloadAsync(Join(cloud.Remote, IndexFolder + "/library.db"), local, ct);
            return local;
        }
        catch (RcloneException)
        {
            return null;
        }
    }
}
