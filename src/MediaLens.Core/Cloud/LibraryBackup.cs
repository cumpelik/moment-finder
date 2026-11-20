using Dapper;
using MediaLens.Data;

namespace MediaLens.Cloud;

/// <summary>
/// The library in the cloud: the index (names, people, buckets, sources, settings), the search vectors, the
/// thumbnails and people crops, and the clouds' settings. Everything a new computer needs to show the library
/// as it was, without analyzing anything again. Originals are the mirrors' business; models and the playing
/// copies of 360 videos are made again.
/// </summary>
public sealed class LibraryBackup
{
    public const string DbName = "library.db";
    public const string VectorsName = "scene.vec";
    public const string CloudsName = "rclone.conf";
    public const string ThumbsName = "thumbs";

    private readonly AppPaths _paths;
    private readonly Database _db;
    private readonly CloudService _cloud;

    public LibraryBackup(AppPaths paths, Database db, CloudService cloud)
    {
        _paths = paths;
        _db = db;
        _cloud = cloud;
    }

    public DateTimeOffset? LastAt =>
        long.TryParse(_db.GetSetting("backup_last_at"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    /// <summary>"OK" or the error of the last run.</summary>
    public string? LastResult => _db.GetSetting("backup_last_result");

    /// <summary>
    /// Uploads this computer's library into its folder of the cloud. Not full: only the index (after a mirror to a
    /// cloud that is not the library's backup).
    /// </summary>
    public async Task RunAsync(CloudInfo cloud, bool full, IProgress<string>? progress, CancellationToken ct)
    {
        var root = CloudService.Join(await _cloud.BackupRootAsync(cloud, ct), _cloud.Device.Folder);
        var tmp = Path.Combine(_paths.CacheDir, "backup");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var result = "OK";
        try
        {
            // The index first as it is now; what is uploaded after it can only hold more than it knows of.
            var db = Path.Combine(tmp, DbName);
            using (var c = _db.Open())
                c.Execute("VACUUM INTO @db", new { db });
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (full)
            {
                var vectors = Path.Combine(_paths.VectorDir, VectorsName);
                if (File.Exists(vectors))
                {
                    progress?.Report("search data");
                    // The running index holds the file open for writing: a copy, with the same time so an unchanged
                    // file is not uploaded again.
                    var copy = Path.Combine(tmp, VectorsName);
                    using (var from = new FileStream(vectors, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var to = File.Create(copy))
                        await from.CopyToAsync(to, ct);
                    File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(vectors));
                    await _cloud.Rclone.UploadAsync(copy, CloudService.Join(root, VectorsName), ct);
                }
                var clouds = Path.Combine(_paths.DataDir, CloudsName);
                if (File.Exists(clouds)) await _cloud.Rclone.UploadAsync(clouds, CloudService.Join(root, CloudsName), ct);
                await _cloud.Rclone.CopyAsync(_paths.ThumbDir, CloudService.Join(root, ThumbsName), [],
                    new Progress<Rclone.CopyProgress>(p => progress?.Report($"thumbnails {p.Percent}%")), ct);
            }
            // Last: a backup with its index in place is whole.
            progress?.Report("names, people and buckets");
            await _cloud.Rclone.UploadAsync(db, CloudService.Join(root, DbName), ct);
        }
        catch (Exception e) when (e is RcloneException or IOException)
        {
            result = "Error: " + e.Message;
            throw new RcloneException(result);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
            if (full)
            {
                _db.SetSetting("backup_last_result", result);
                if (result == "OK") _db.SetSetting("backup_last_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            }
        }
    }
}
