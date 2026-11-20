using Dapper;
using MediaLens.Data;
using MediaLens.Library;

namespace MediaLens.Cloud;

/// <summary>
/// A library brought back from the cloud with a recovery string, on a new computer or a second one. The backup is
/// downloaded beside the library and put in its place at the next start, before anything opens the old one. Then
/// it is fitted to this computer: folders that are not here but were mirrored are browsed from the cloud (files
/// come into the cache when opened, nothing is downloaded wholesale).
/// </summary>
public sealed class LibraryRestore
{
    /// <summary>One computer's library backup found in the cloud.</summary>
    public sealed record Found(string Folder, string Computer, DateTimeOffset When, long Size)
    {
        public string Title => $"{Computer}, backed up {When.LocalDateTime:d. M. yyyy H:mm}";
    }

    private readonly AppPaths _paths;
    private readonly Rclone _rclone;
    private readonly string _config;

    public LibraryRestore(AppPaths paths)
    {
        _paths = paths;
        _config = Path.Combine(paths.CacheDir, "restore-rclone.conf");
        _rclone = new Rclone(_config);
    }

    private static string PendingDir(AppPaths paths) => Path.Combine(paths.DataDir, "restore-pending");
    private static string AdaptMarker(AppPaths paths) => Path.Combine(paths.DataDir, "restore-adapt");

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

    /// <summary>Downloads a backup beside the library; the next start puts it in place (ApplyPending).</summary>
    public async Task DownloadAsync(Recovery recovery, Found backup, bool thumbnails, IProgress<string>? progress, CancellationToken ct)
    {
        Rclone.WriteConfig(_config, recovery.Remotes);
        var pending = PendingDir(_paths);
        if (Directory.Exists(pending)) Directory.Delete(pending, true);
        Directory.CreateDirectory(pending);
        var root = CloudService.Join(recovery.BackupRoot, backup.Folder);
        var files = (await _rclone.ListAsync(root, 1, ct)).Select(f => f.Path).ToHashSet();
        progress?.Report("names, people and buckets");
        await _rclone.DownloadAsync(CloudService.Join(root, LibraryBackup.DbName), Path.Combine(pending, LibraryBackup.DbName), ct);
        if (files.Contains(LibraryBackup.VectorsName))
        {
            progress?.Report("search data");
            await _rclone.DownloadAsync(CloudService.Join(root, LibraryBackup.VectorsName), Path.Combine(pending, LibraryBackup.VectorsName), ct);
        }
        // The clouds as that computer knew them; a backup without them still has the one of the recovery string.
        if (files.Contains(LibraryBackup.CloudsName))
            await _rclone.DownloadAsync(CloudService.Join(root, LibraryBackup.CloudsName), Path.Combine(pending, LibraryBackup.CloudsName), ct);
        else
            File.Copy(_config, Path.Combine(pending, LibraryBackup.CloudsName), true);
        if (thumbnails)
            await _rclone.CopyAsync(CloudService.Join(root, LibraryBackup.ThumbsName), Path.Combine(pending, LibraryBackup.ThumbsName), ["--transfers", "8"],
                new Progress<Rclone.CopyProgress>(p => progress?.Report($"thumbnails {p.Percent}%")), ct);
        File.WriteAllText(Path.Combine(pending, "ready"), thumbnails ? "thumbs" : "");
        File.Delete(_config);
    }

    /// <summary>
    /// At the start, before the library is opened: a downloaded backup takes the library's place. What was here is
    /// kept beside it (library.before-restore.db, thumbs.before-restore). True when a library was put in place.
    /// </summary>
    public static bool ApplyPending(AppPaths paths)
    {
        var pending = PendingDir(paths);
        if (!Directory.Exists(pending)) return false;
        var ready = Path.Combine(pending, "ready");
        if (!File.Exists(ready))
        {
            // A download that did not finish.
            Directory.Delete(pending, true);
            return false;
        }
        var withThumbs = Directory.Exists(Path.Combine(pending, LibraryBackup.ThumbsName));
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
        File.WriteAllText(AdaptMarker(paths), withThumbs ? "thumbs" : "");
        return true;
    }

    /// <summary>
    /// After ApplyPending, with the restored library open: its sources are fitted to this computer. A local folder
    /// that is not here and was mirrored becomes a cloud source at the mirror's place: the same files under the same
    /// paths, so thumbnails, people and search work at once and the originals come from the cloud when opened.
    /// </summary>
    public static void AdaptToThisComputer(AppPaths paths, Database db)
    {
        var marker = AdaptMarker(paths);
        if (!File.Exists(marker)) return;
        var withThumbs = File.ReadAllText(marker) == "thumbs";
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
                if (Directory.Exists(s.Root)) continue;
                var mirror = c.Query<(string Target, string Remote)>("""
                    SELECT m.target, c.remote FROM mirror m JOIN cloud c ON c.id=m.cloud_id
                    WHERE m.source_id=@Id AND m.last_run_at IS NOT NULL ORDER BY m.last_run_at DESC LIMIT 1
                    """, new { s.Id }, tx).FirstOrDefault();
                if (mirror.Target == null) continue;
                c.Execute("UPDATE source SET kind=@kind, root=@cache, remote=@remote WHERE id=@Id",
                    new { kind = SourceKinds.Cloud, cache, remote = CloudService.Join(mirror.Remote, mirror.Target), s.Id }, tx);
                // This computer does not have the folder: nothing to mirror from here.
                c.Execute("DELETE FROM mirror WHERE source_id=@Id", new { s.Id }, tx);
            }
            if (!withThumbs) c.Execute("UPDATE media SET thumb_state=0", transaction: tx);
            if (!File.Exists(Path.Combine(paths.VectorDir, LibraryBackup.VectorsName)))
            {
                c.Execute("DELETE FROM scene_vec", transaction: tx);
                c.Execute("UPDATE media SET analysis_version=0", transaction: tx);
            }
            // When the library was backed up is that computer's story, not this one's.
            c.Execute("DELETE FROM setting WHERE key IN ('backup_last_at', 'backup_last_result')", transaction: tx);
            tx.Commit();
        }
        foreach (var s in new MediaRepository(db).ListSources().Where(s => s.Kind == SourceKinds.Cloud)) Directory.CreateDirectory(s.Root);
        File.Delete(marker);
    }
}
