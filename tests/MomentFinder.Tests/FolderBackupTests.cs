using Dapper;
using MomentFinder.Buckets;
using MomentFinder.Cloud;
using MomentFinder.Library;
using MomentFinder.People;

namespace MomentFinder.Tests;

/// <summary>A local folder backed up to a cloud (a folder stands in for it), moved there, and taken over by another computer.</summary>
[Collection("Cloud")]
public class FolderBackupTests
{
    private static string Folder => $"{DeviceInfo.Safe(Environment.MachineName)}/test";

    private static string InCloud(string cloudDir, string rel) =>
        Path.Combine(cloudDir, DeviceInfo.Safe(Environment.MachineName), "test", rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void MovedFilesAreMovedNotUploaded()
    {
        var t = DateTimeOffset.UtcNow;
        var local = new Dictionary<string, RemoteFile>
        {
            ["same.jpg"] = new("same.jpg", 10, t),
            ["new/place.jpg"] = new("new/place.jpg", 20, t),
            ["fresh.jpg"] = new("fresh.jpg", 30, t),
            ["changed.jpg"] = new("changed.jpg", 41, t),
        };
        var cloud = new[]
        {
            new RemoteFile("same.jpg", 10, t.AddMilliseconds(400)),
            new RemoteFile("old/place.jpg", 20, t),
            new RemoteFile("deleted.jpg", 50, t),
            new RemoteFile("changed.jpg", 40, t),
            new RemoteFile(FolderMarker.FileName, 5, t),
        };
        var plan = FolderSync.Plan(local, cloud);
        Assert.Equal([("old/place.jpg", "new/place.jpg")], plan.Moves);
        Assert.Equal(["changed.jpg", "fresh.jpg"], plan.Upload.Order());
        Assert.Equal(["deleted.jpg"], plan.Gone.Select(g => g.Path));
    }

    [Fact]
    public async Task TheFolderNameIsTheUsersAndNeverMixedWithAnother()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        lib.AddPhoto("a.jpg");
        var source = lib.AddSource();
        var cloudDir = Path.Combine(lib.Root, "cloud");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Backup", cloudDir, null, ct, encrypt: false);
        // Something else lives in "Family" already: the proposal is "Family 2", the name itself is refused.
        Directory.CreateDirectory(Path.Combine(cloudDir, "Family"));
        File.WriteAllText(Path.Combine(cloudDir, "Family", "other.jpg"), "x");
        Assert.Equal("Family 2", await service.FreeFolderAsync(cloud.Id, "Family", reuse: true, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddMirrorAsync(source.Id, cloud.Id, false, ct, target: "Family"));

        var mirror = await service.AddMirrorAsync(source.Id, cloud.Id, false, ct, target: @"Photos\Mine/");
        Assert.Equal("Photos/Mine", mirror.Target);
        await service.MirrorAsync(mirror.Id, null, ct);
        Assert.True(File.Exists(Path.Combine(cloudDir, "Photos", "Mine", "a.jpg")));
        // This computer's own backup may be connected again (nothing uploaded twice); a clone never goes into it.
        service.RemoveMirror(mirror.Id);
        Assert.Equal("Photos/Mine", await service.FreeFolderAsync(cloud.Id, "Photos/Mine", reuse: true, ct));
        Assert.Equal("Photos/Mine 2", await service.FreeFolderAsync(cloud.Id, "Photos/Mine", reuse: false, ct));
    }

    [Fact]
    public async Task BackupFollowsTheFolderAndAsksAboutDeleted()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        // Sizes differ: a moved file is told by its size and time.
        lib.AddPhoto("2024/a.jpg", width: 64, color: "red");
        lib.AddPhoto("2024/b.jpg", width: 96, color: "blue");
        var source = lib.AddSource();
        var cloudDir = Path.Combine(lib.Root, "cloud");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Backup", cloudDir, null, ct, encrypt: false);
        var mirror = await service.AddMirrorAsync(source.Id, cloud.Id, false, ct);

        Assert.Equal(new SyncResult(2, 0, 0), await service.MirrorAsync(mirror.Id, null, ct));
        Assert.True(File.Exists(InCloud(cloudDir, "2024/a.jpg")));
        Assert.True((await FolderMarker.ReadAsync(service.Rclone, mirror.Place, ct))!.IsBackup);

        // Renamed, deleted, added.
        Directory.CreateDirectory(Path.Combine(lib.MediaDir, "2025"));
        File.Move(Path.Combine(lib.MediaDir, "2024", "a.jpg"), Path.Combine(lib.MediaDir, "2025", "a2.jpg"));
        File.Delete(Path.Combine(lib.MediaDir, "2024", "b.jpg"));
        lib.AddPhoto("2025/c.jpg", width: 128, color: "green");
        Assert.Equal(new SyncResult(1, 1, 1), await service.MirrorAsync(mirror.Id, null, ct));
        Assert.True(File.Exists(InCloud(cloudDir, "2025/a2.jpg")));
        Assert.False(File.Exists(InCloud(cloudDir, "2024/a.jpg")));
        Assert.True(File.Exists(InCloud(cloudDir, "2024/b.jpg")));
        Assert.Equal(["2024/b.jpg"], service.Gone(mirror.Id).Select(g => g.Path));
        Assert.Equal(1, service.Mirrors().Single().Gone);

        // Deleted by mistake: it comes back.
        await service.BringBackGoneAsync(mirror.Id, null, ct);
        Assert.True(File.Exists(Path.Combine(lib.MediaDir, "2024", "b.jpg")));
        Assert.Empty(service.Gone(mirror.Id));

        // Deleted on purpose: deleted in the cloud only when the user says so.
        File.Delete(Path.Combine(lib.MediaDir, "2024", "b.jpg"));
        await service.MirrorAsync(mirror.Id, null, ct);
        Assert.True(File.Exists(InCloud(cloudDir, "2024/b.jpg")));
        await service.DeleteGoneAsync(mirror.Id, ct);
        Assert.False(File.Exists(InCloud(cloudDir, "2024/b.jpg")));
        Assert.True(File.Exists(InCloud(cloudDir, "2025/c.jpg")));

        // A folder that is not there (a disk not connected) changes nothing in the cloud.
        Directory.Move(lib.MediaDir, lib.MediaDir + "-away");
        await Assert.ThrowsAsync<RcloneException>(() => service.MirrorAsync(mirror.Id, null, ct));
        Directory.CreateDirectory(lib.MediaDir);
        await Assert.ThrowsAsync<RcloneException>(() => service.MirrorAsync(mirror.Id, null, ct));
        Assert.Empty(service.Gone(mirror.Id));
        Assert.True(File.Exists(InCloud(cloudDir, "2025/c.jpg")));
    }

    [Fact]
    public async Task EncryptedFolderHidesNamesAndContents()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        lib.AddPhoto("2024/IMG_0001.jpg");
        var source = lib.AddSource();
        var cloudDir = Path.Combine(lib.Root, "cloud");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Backup", cloudDir, null, ct, encrypt: false);
        var mirror = await service.AddMirrorAsync(source.Id, cloud.Id, true, ct);
        Assert.True(mirror.Encrypted);
        await service.MirrorAsync(mirror.Id, null, ct);

        var folder = Path.Combine(cloudDir, DeviceInfo.Safe(Environment.MachineName), "test");
        var stored = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).ToList();
        Assert.Equal(2, stored.Count); // the photo and the marker
        Assert.DoesNotContain(stored, f => f.Contains("IMG_", StringComparison.Ordinal) || f.Contains("2024", StringComparison.Ordinal) ||
                                           f.Contains("medialens", StringComparison.Ordinal));
        Assert.Equal("test", (await FolderMarker.ReadAsync(service.Rclone, mirror.Place, ct))!.Name);
    }

    [Fact]
    public async Task AFolderIsAddedToACloudFolderNeverWritingOver()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        var a = lib.AddPhoto("2024/a.jpg", width: 64, color: "red");
        lib.AddPhoto("2024/b.jpg", width: 80, color: "blue");
        lib.AddPhoto("c.jpg", width: 96, color: "green");
        var source = lib.AddSource();
        await lib.ScanAsync(source);

        // The cloud folder has a.jpg already (the same file) and another b.jpg.
        var cloudDir = Path.Combine(lib.Root, "cloud");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Store", cloudDir, null, ct, encrypt: false);
        var family = Path.Combine(cloudDir, "Family", "test", "2024");
        Directory.CreateDirectory(family);
        File.Copy(a, Path.Combine(family, "a.jpg"));
        File.SetLastWriteTimeUtc(Path.Combine(family, "a.jpg"), File.GetLastWriteTimeUtc(a));
        File.WriteAllText(Path.Combine(family, "b.jpg"), "another photo");
        var target = lib.Repo.AddSource(SourceKinds.Cloud, Path.Combine(lib.Root, "cache"), "Family (Store)", CloudService.Join(cloud.Remote, "Family"));
        Assert.Contains(service.MergeTargets(), t => t.Id == target.Id);

        service.RequestMerge(source.Id, target.Id, "test");
        Assert.Equal(new MergeResult(1, 1, 1), await service.MergeAsync(source.Id, null, ct));
        Assert.Equal("another photo", File.ReadAllText(Path.Combine(family, "b.jpg")));
        Assert.True(File.Exists(Path.Combine(family, "b (2).jpg")));
        Assert.True(File.Exists(Path.Combine(cloudDir, "Family", "test", "c.jpg")));
        Assert.Empty(service.Merges());
        // The new files in the cloud folder have the rows of their originals (their twins give the rest).
        var rows = lib.Repo.Query(new MediaFilter { SourceId = target.Id }).Select(i => i.RelPath).Order().ToList();
        Assert.Equal(["test/2024/a.jpg", "test/2024/b (2).jpg", "test/c.jpg"], rows);
        Assert.Equal(3, lib.Repo.Query(new MediaFilter { SourceId = source.Id }).Count);

        // Again (a break, or added twice): nothing new is copied.
        service.RequestMerge(source.Id, target.Id, "test");
        Assert.Equal(new MergeResult(0, 0, 3), await service.MergeAsync(source.Id, null, ct));
    }

    [Fact]
    public async Task ACloudFileWithATwinHereIsNotDownloaded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        var photo = lib.AddPhoto("a.jpg");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        _ = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = lib.Repo.AddSource(SourceKinds.Cloud, Path.Combine(lib.Root, "cache"), "Store", "nowhere:bucket/photos");
        using (var c = lib.Db.Open())
            c.Execute("""
                INSERT INTO media(source_id, rel_path, kind, size, mtime, quick_hash, taken_at)
                SELECT @cloud, rel_path, kind, size, mtime, quick_hash, taken_at FROM media WHERE source_id=@local
                """, new { cloud = cloud.Id, local = source.Id });
        var inCloud = lib.Repo.Query(new MediaFilter { SourceId = cloud.Id }).Single();
        // The remote does not exist: a download would fail. The file here is used.
        Assert.Equal(photo, await MediaFiles.LocalPathAsync(inCloud, ct));
    }

    [Fact]
    public async Task MovedToTheCloudKeepsEverything()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        lib.AddPhoto("2024/a.jpg", color: "red");
        lib.AddPhoto("2024/b.jpg", color: "blue");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var indexer = new Indexer(lib.Paths, lib.Repo);
        await indexer.RunUntilIdleAsync(false, ct);
        var items = lib.Repo.Query(new MediaFilter()).OrderBy(i => i.RelPath).ToList();
        Assert.All(items, i => Assert.Equal(ThumbStates.Ready, i.ThumbState));
        var people = new PeopleRepository(lib.Db);
        var face = people.ReplaceDetections(items[0].Id,
            [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.1f, 0.2f, 0.2f, 0.9f, 0.9f, new float[16], null, null)])[0];
        var eva = people.CreatePerson("Eva");
        people.Confirm([face], eva.Id);
        using (var c = lib.Db.Open()) c.Execute("UPDATE media SET people_version=@v", new { v = Indexer.PeopleVersion });
        var buckets = new BucketService(lib.Db, lib.Repo);
        buckets.Add(buckets.Create("Book").Id, [items[1].Id]);

        var cloudDir = Path.Combine(lib.Root, "cloud");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Store", cloudDir, null, ct, encrypt: false);
        var mirror = await service.AddMirrorAsync(source.Id, cloud.Id, false, ct);
        var localId = await service.MoveToCloudAsync(mirror.Id, null, ct);

        // The source lives in the cloud now, with everything it knew; the folder here is a source of its own.
        var sources = lib.Repo.ListSources();
        var moved = sources.Single(s => s.Id == source.Id);
        Assert.Equal(SourceKinds.Cloud, moved.Kind);
        Assert.Equal("test (Store)", moved.Name);
        Assert.False(moved.ReadOnly);
        Assert.Empty(service.Mirrors());
        Assert.Equal(FolderMarker.Shared, (await FolderMarker.ReadAsync(service.Rclone, moved.Remote!, ct))!.Mode);
        Assert.Equal(1, buckets.List().Single().Count);
        var kept = sources.Single(s => s.Id == localId);
        Assert.Equal(lib.MediaDir, kept.Root);

        // The folder's files get their twins' thumbnails and people without being read.
        var twins = new AnalysisTwin(lib.Paths, lib.Db);
        Assert.Equal(2, twins.CopyBatch(null, 100));
        var copies = lib.Repo.Query(new MediaFilter { SourceId = localId }).OrderBy(i => i.RelPath).ToList();
        Assert.All(copies, i => Assert.Equal(ThumbStates.Ready, i.ThumbState));
        Assert.All(copies, i => Assert.True(File.Exists(lib.Paths.ThumbPath(i.Id))));
        Assert.Equal(eva.Id, people.DetectionsOfMedia(copies[0].Id).Single().PersonId);
        Assert.Equal(2, people.ListPersons().Single().MediaCount);

        // Scanning the cloud source fetches nothing: the files there are the ones it knows.
        var scan = await new LibraryScanner(lib.Repo, service.Rclone).ScanAsync(moved, null, ct);
        Assert.Equal((0, 0, 0), (scan.Added, scan.Changed, scan.Missing));
        Assert.Empty(Directory.GetFiles(moved.Root, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnotherComputerTakesTheBackupOver()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        lib.AddPhoto("2024/a.jpg", color: "red");
        lib.AddPhoto("2024/b.jpg", color: "blue");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var cloudDir = Path.Combine(lib.Root, "cloud");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Store", cloudDir, null, ct, encrypt: false);
        var mirror = await service.AddMirrorAsync(source.Id, cloud.Id, false, ct);
        await service.MirrorAsync(mirror.Id, null, ct);

        // The other computer sees the backup, only for looking.
        using var other = new TestLibrary();
        var service2 = new CloudService(other.Paths, other.Db, other.Repo);
        var cloud2 = await service2.AddFolderAsync("Store", cloudDir, null, ct, encrypt: false);
        var added = service2.AddCloudSource(cloud2.Id, Folder);
        var scanner = new LibraryScanner(other.Repo, service2.Rclone);
        Assert.Equal(2, (await scanner.ScanAsync(other.Repo.ListSources().Single(), null, ct)).Added);
        var seen = other.Repo.ListSources().Single();
        Assert.True(seen.ReadOnly);
        Assert.Equal(Environment.MachineName, seen.HomeName);
        var ids = other.Repo.Query(new MediaFilter()).Select(i => i.Id).Order().ToList();

        // It becomes this computer's folder: downloaded, nothing new to read, backed up from here.
        var home = Path.Combine(other.Root, "photos");
        await service2.MakeHomeAsync(added.Id, home, null, ct);
        var local = other.Repo.ListSources().Single();
        Assert.Equal(SourceKinds.Local, local.Kind);
        Assert.Equal(home, local.Root);
        Assert.True(File.Exists(Path.Combine(home, "2024", "a.jpg")));
        var rescan = await scanner.ScanAsync(local, null, ct);
        Assert.Equal((0, 0, 0), (rescan.Added, rescan.Changed, rescan.Missing));
        Assert.Equal(ids, other.Repo.Query(new MediaFilter()).Select(i => i.Id).Order().ToList());
        var mirror2 = service2.Mirrors().Single();
        Assert.Equal(new SyncResult(0, 0, 0), await service2.MirrorAsync(mirror2.Id, null, ct));
        Assert.Equal(service2.Device.Id, (await FolderMarker.ReadAsync(service2.Rclone, mirror2.Place, ct))!.Home);

        // The first computer must not change it any more.
        var stopped = await Assert.ThrowsAsync<RcloneException>(() => service.MirrorAsync(mirror.Id, null, ct));
        Assert.Contains("took this backup over", stopped.Message);
        Assert.False(service.Mirrors().Single().Enabled);
    }
}
