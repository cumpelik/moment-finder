using Dapper;
using MomentFinder.Buckets;
using MomentFinder.Cloud;
using MomentFinder.Library;
using MomentFinder.Sync;
using Xunit;

namespace MomentFinder.Tests;

/// <summary>Changing a folder that lives in the cloud from the library: uploading into it, moving and deleting in it.</summary>
[Collection("Cloud")]
public class CloudJobsTests
{
    private sealed class World : IDisposable
    {
        public TestLibrary Lib { get; } = new();
        public string CloudDir { get; }
        public CloudService Service { get; }
        public CloudJobs Jobs { get; }
        public CloudInfo Cloud { get; private set; } = null!;
        private readonly bool _ownsCloud;

        public World(string? cloudDir = null)
        {
            _ownsCloud = cloudDir == null;
            CloudDir = cloudDir ?? Path.Combine(Path.GetTempPath(), "momentfinder-jobs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(CloudDir);
            Service = new CloudService(Lib.Paths, Lib.Db, Lib.Repo);
            Jobs = new CloudJobs(Lib.Paths, Lib.Db, Lib.Repo, Service);
        }

        public async Task<Source> FolderAsync(string name, CancellationToken ct)
        {
            Cloud ??= await Service.AddFolderAsync("Home", CloudDir, null, ct, encrypt: false);
            var added = Service.AddCloudSource(Cloud.Id, name);
            return Lib.Repo.ListSources().First(s => s.Id == added.Id);
        }

        /// <summary>A photo put into the cloud by other means.</summary>
        public string InCloud(string rel, string color, int width = 64)
        {
            var made = Lib.AddPhoto(Path.Combine("made", Guid.NewGuid().ToString("N") + ".jpg"), width, color: color);
            var path = Path.Combine(CloudDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(made, path);
            return path;
        }

        public bool Has(string rel) => File.Exists(Path.Combine(CloudDir, rel.Replace('/', Path.DirectorySeparatorChar)));

        public List<string> Shown(long sourceId) =>
            Lib.Repo.Query(new MediaFilter { SourceId = sourceId }).Select(i => i.RelPath).Order(StringComparer.Ordinal).ToList();

        public Task<ScanResult> ScanAsync(Source source, CancellationToken ct) => new LibraryScanner(Lib.Repo, Service.Rclone).ScanAsync(source, null, ct);

        public void Dispose()
        {
            Jobs.Dispose();
            Lib.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!_ownsCloud) return;
            try { Directory.Delete(CloudDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task FilesAndFoldersAreUploadedIntoAFolderInTheCloud()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var w = new World();
        var photos = await w.FolderAsync("Photos", ct);

        // On this computer: a folder with a folder inside and a file that is no photo, and one photo by itself.
        var a = w.Lib.AddPhoto("pick/Ski/a.jpg", color: "blue");
        w.Lib.AddPhoto("pick/Ski/day1/b.jpg", color: "green");
        var same = w.Lib.AddPhoto("pick/Ski/same.jpg", color: "yellow");
        File.WriteAllText(Path.Combine(w.Lib.MediaDir, "pick", "Ski", "notes.txt"), "the trip");
        var single = w.Lib.AddPhoto("pick/single.jpg", color: "white");
        var later = Path.Combine(w.Lib.MediaDir, "pick", "later.jpg");
        // In the cloud already: another photo under the same name, and the very same file.
        var other = w.InCloud("Photos/2024/Ski/a.jpg", "red", width: 96);
        var otherSize = new FileInfo(other).Length;
        Directory.CreateDirectory(Path.Combine(w.CloudDir, "Photos", "2024", "Ski"));
        File.Copy(same, Path.Combine(w.CloudDir, "Photos", "2024", "Ski", "same.jpg"));
        File.SetLastWriteTimeUtc(Path.Combine(w.CloudDir, "Photos", "2024", "Ski", "same.jpg"), File.GetLastWriteTimeUtc(same));

        var progress = new List<JobProgress>();
        w.Jobs.Progress += (_, p) => { lock (progress) progress.Add(p); };
        var id = w.Jobs.Upload(photos.Id, "2024", [Path.Combine(w.Lib.MediaDir, "pick", "Ski"), single, later]);
        Assert.Equal(JobStates.Waiting, w.Jobs.Get(id)!.State);
        await w.Jobs.RunAsync(id, ct);

        // Everything is there, the folder with its name and what is inside; nothing was written over.
        Assert.Equal(otherSize, new FileInfo(other).Length);
        Assert.True(w.Has("Photos/2024/Ski/a (2).jpg"));
        Assert.True(w.Has("Photos/2024/Ski/day1/b.jpg"));
        Assert.True(w.Has("Photos/2024/Ski/notes.txt"));
        Assert.True(w.Has("Photos/2024/single.jpg"));
        Assert.Equal(new FileInfo(a).Length, new FileInfo(Path.Combine(w.CloudDir, "Photos", "2024", "Ski", "a (2).jpg")).Length);
        // The library shows them at once, read from the files here: nothing was downloaded for it.
        Assert.Equal(["2024/Ski/a (2).jpg", "2024/Ski/day1/b.jpg", "2024/Ski/same.jpg", "2024/single.jpg"], w.Shown(photos.Id));
        var item = w.Lib.Repo.Query(new MediaFilter { SourceId = photos.Id }).Single(i => i.RelPath == "2024/single.jpg");
        Assert.Equal(single, w.Lib.Repo.LocalTwinPath(item));
        Assert.True(await Media.ImageCodec.CreateThumbnailAsync(item, w.Lib.Repo.LocalTwinPath(item)!, w.Lib.Paths.ThumbPath(item.Id), ct));
        Assert.False(Directory.Exists(photos.Root) && Directory.EnumerateFiles(photos.Root, "*", SearchOption.AllDirectories).Any());

        // The file that was not there is said, with why; the job waits and tries again by itself.
        var job = w.Jobs.Get(id)!;
        Assert.Equal((JobStates.Waiting, 6, 5, 1, 1), (job.State, job.Total, job.Done, job.Same, job.Failed));
        var failed = Assert.Single(w.Jobs.FailedFiles(id));
        Assert.Equal(later, failed.Origin);
        Assert.Contains("not on this computer", failed.Error);
        Assert.NotEmpty(progress);

        // It is there now: the next try takes it, and only it.
        File.Move(w.Lib.AddPhoto("made/later.jpg", color: "black"), later);
        var before = Directory.GetLastWriteTimeUtc(Path.Combine(w.CloudDir, "Photos", "2024", "Ski", "a (2).jpg"));
        await w.Jobs.RunAsync(id, ct);
        job = w.Jobs.Get(id)!;
        Assert.Equal((JobStates.Done, 6, 6, 0), (job.State, job.Total, job.Done, job.Failed));
        Assert.True(w.Has("Photos/2024/later.jpg"));
        Assert.False(w.Has("Photos/2024/Ski/a (3).jpg"));
        Assert.Equal(before, Directory.GetLastWriteTimeUtc(Path.Combine(w.CloudDir, "Photos", "2024", "Ski", "a (2).jpg")));

        // The same upload once more adds nothing: every file is there already.
        var again = w.Jobs.Upload(photos.Id, "2024", [Path.Combine(w.Lib.MediaDir, "pick", "Ski")]);
        await w.Jobs.RunAsync(again, ct);
        job = w.Jobs.Get(again)!;
        Assert.Equal((JobStates.Done, 4, 4), (job.State, job.Total, job.Same));
        Assert.False(w.Has("Photos/2024/Ski/a (3).jpg"));
        Assert.Equal(5, w.Shown(photos.Id).Count);

        // A scan of the folder finds the photo that was there before, and nothing to read again.
        var scan = await w.ScanAsync(photos, ct);
        Assert.Equal((1, 0, 0), (scan.Added, scan.Changed, scan.Missing));
    }

    [Fact]
    public async Task FilesAndFoldersMoveAndAreDeletedInTheCloud()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var w = new World();
        var photos = await w.FolderAsync("Photos", ct);
        var other = await w.FolderAsync("Other", ct);
        w.InCloud("Photos/2023/x.jpg", "red");
        w.InCloud("Photos/2023/y.jpg", "blue");
        w.InCloud("Photos/2023/Trip/z.jpg", "green");
        w.InCloud("Photos/Best/y.jpg", "yellow", width: 96);
        File.WriteAllText(Path.Combine(w.CloudDir, "Photos", "2023", "Trip", "info.txt"), "the trip");
        await w.ScanAsync(photos, ct);
        var x = w.Lib.Repo.Query(new MediaFilter { SourceId = photos.Id }).Single(i => i.RelPath == "2023/x.jpg");
        var z = w.Lib.Repo.Query(new MediaFilter { SourceId = photos.Id }).Single(i => i.RelPath == "2023/Trip/z.jpg");
        // What is known of a photo hangs on its row: a bucket here.
        var buckets = new BucketService(w.Lib.Db, w.Lib.Repo);
        var bucket = buckets.Create("Book");
        buckets.Add(bucket.Id, [x.Id, z.Id]);
        Assert.True(File.Exists(x.FullPath));

        // Two photos into another folder: one of that name is there, so the moved one goes beside it.
        var move = w.Jobs.Move(photos.Id, ["2023/x.jpg", "2023/y.jpg"], photos.Id, "Best");
        await w.Jobs.RunAsync(move, ct);
        Assert.Equal(JobStates.Done, w.Jobs.Get(move)!.State);
        Assert.True(w.Has("Photos/Best/x.jpg") && w.Has("Photos/Best/y.jpg") && w.Has("Photos/Best/y (2).jpg"));
        Assert.False(w.Has("Photos/2023/x.jpg") || w.Has("Photos/2023/y.jpg"));
        Assert.Equal(["2023/Trip/z.jpg", "Best/x.jpg", "Best/y (2).jpg", "Best/y.jpg"], w.Shown(photos.Id));
        var moved = w.Lib.Repo.Get(x.Id)!;
        Assert.Equal("Best/x.jpg", moved.RelPath);
        Assert.Contains(x.Id, buckets.MediaIds(bucket.Id));
        // The copy fetched to this computer moved along: not fetched again.
        Assert.True(File.Exists(moved.FullPath) && !File.Exists(x.FullPath));

        // A whole folder, with what the library does not show; to another place under another name.
        var folder = w.Jobs.MoveFolder(photos.Id, "2023/Trip", photos.Id, "Archive/Trip 2023");
        await w.Jobs.RunAsync(folder, ct);
        Assert.Equal((JobStates.Done, 2), (w.Jobs.Get(folder)!.State, w.Jobs.Get(folder)!.Total));
        Assert.True(w.Has("Photos/Archive/Trip 2023/z.jpg") && w.Has("Photos/Archive/Trip 2023/info.txt"));
        Assert.False(Directory.Exists(Path.Combine(w.CloudDir, "Photos", "2023", "Trip")));
        Assert.Equal("Archive/Trip 2023/z.jpg", w.Lib.Repo.Get(z.Id)!.RelPath);
        Assert.Throws<InvalidOperationException>(() => w.Jobs.MoveFolder(photos.Id, "Archive", photos.Id, "Archive/Trip 2023/Inside"));

        // Into another folder of the library in the cloud: the row goes with it.
        var across = w.Jobs.Move(photos.Id, ["Best/x.jpg"], other.Id, "");
        await w.Jobs.RunAsync(across, ct);
        Assert.True(w.Has("Other/x.jpg") && !w.Has("Photos/Best/x.jpg"));
        Assert.Equal((other.Id, "x.jpg"), (w.Lib.Repo.Get(x.Id)!.SourceId, w.Lib.Repo.Get(x.Id)!.RelPath));

        // Deleted: gone from the cloud and from the library.
        var delete = w.Jobs.Delete(photos.Id, ["Best/y.jpg"]);
        await w.Jobs.RunAsync(delete, ct);
        Assert.Equal(JobStates.Done, w.Jobs.Get(delete)!.State);
        Assert.False(w.Has("Photos/Best/y.jpg"));
        Assert.True(w.Has("Photos/Best/y (2).jpg"));
        var whole = w.Jobs.DeleteFolder(photos.Id, "Archive");
        await w.Jobs.RunAsync(whole, ct);
        Assert.False(Directory.Exists(Path.Combine(w.CloudDir, "Photos", "Archive")));
        Assert.Equal(["Best/y (2).jpg"], w.Shown(photos.Id));
        // What a scan finds afterwards is what the library shows already.
        var scan = await w.ScanAsync(photos, ct);
        Assert.Equal((0, 0, 0), (scan.Added, scan.Changed, scan.Missing));

        // Read-only: nothing of this starts.
        w.Lib.Repo.ReadOnly.SetPassword("secret");
        w.Lib.Repo.ReadOnly.Protect(photos.Id);
        Assert.Throws<InvalidOperationException>(() => w.Jobs.Delete(photos.Id, ["Best/y (2).jpg"]));
        Assert.Throws<InvalidOperationException>(() => w.Jobs.Upload(photos.Id, "", [w.Lib.MediaDir]));
        Assert.Throws<InvalidOperationException>(() => w.Jobs.Move(other.Id, ["x.jpg"], photos.Id, ""));
        Assert.DoesNotContain(w.Jobs.Changeable(), s => s.Id == photos.Id);
    }

    [Fact]
    public async Task AFolderOfAnotherStorageIsCopiedIntoTheCloudFolder()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var w = new World();
        var photos = await w.FolderAsync("Photos", ct);
        // Another storage, with photos put there by other means.
        var oldDir = Path.Combine(w.Lib.Root, "old-storage");
        var old = await w.Service.AddFolderAsync("Old", oldDir, null, ct, encrypt: false);
        foreach (var (rel, color) in new[] { ("Pictures/2019/a.jpg", "red"), ("Pictures/2019/b.jpg", "blue"), ("Pictures/c.jpg", "green") })
        {
            var made = w.Lib.AddPhoto("made/" + Path.GetFileName(rel), color: color);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(oldDir, rel))!);
            File.Move(made, Path.Combine(oldDir, rel));
        }
        Directory.CreateDirectory(Path.Combine(oldDir, "Pictures", ".thumbnails"));
        File.WriteAllText(Path.Combine(oldDir, "Pictures", ".thumbnails", "t.jpg"), "not a photo of the user");

        var asked = new List<long>();
        w.Jobs.ScanWanted += asked.Add;
        var id = w.Jobs.Import(photos.Id, "From the old storage", CloudService.Join(old.Remote, "Pictures"), "Pictures of Old");
        await w.Jobs.RunAsync(id, ct);
        var job = w.Jobs.Get(id)!;
        Assert.Equal((JobStates.Done, 3, 3), (job.State, job.Total, job.Done));
        Assert.True(w.Has("Photos/From the old storage/2019/a.jpg") && w.Has("Photos/From the old storage/c.jpg"));
        Assert.False(Directory.Exists(Path.Combine(w.CloudDir, "Photos", "From the old storage", ".thumbnails")));
        // The files did not pass the library: the folder is read to find them.
        Assert.Equal([photos.Id], asked);
        await w.ScanAsync(photos, ct);
        Assert.Equal(["From the old storage/2019/a.jpg", "From the old storage/2019/b.jpg", "From the old storage/c.jpg"], w.Shown(photos.Id));
        // The other storage is as it was.
        Assert.True(File.Exists(Path.Combine(oldDir, "Pictures", "c.jpg")));

        // Single files of a folder there, by name: only those, straight into the folder.
        var some = w.Jobs.ImportFiles(photos.Id, "Picked", CloudService.Join(old.Remote, "Pictures/2019"), ["b.jpg", "gone.jpg"], "Pictures/2019 of Old");
        await w.Jobs.RunAsync(some, ct);
        job = w.Jobs.Get(some)!;
        Assert.Equal((2, 1, 1), (job.Total, job.Done, job.Failed));
        Assert.True(w.Has("Photos/Picked/b.jpg") && !w.Has("Photos/Picked/a.jpg"));
        Assert.Equal("gone.jpg", Assert.Single(w.Jobs.FailedFiles(some)).Origin);
        // A folder of the library's own storage is never copied into itself.
        Assert.Throws<InvalidOperationException>(() => w.Jobs.Import(photos.Id, "Inside", photos.Remote!, "itself"));
    }

    [Fact]
    public async Task AStoppedJobKeepsWhatIsDoneAndGoesOnWhenAsked()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var w = new World();
        var photos = await w.FolderAsync("Photos", ct);
        w.Lib.AddPhoto("pick/a.jpg", color: "blue");
        var id = w.Jobs.Upload(photos.Id, "", [Path.Combine(w.Lib.MediaDir, "pick")]);
        w.Jobs.Stop(id);
        Assert.Equal(JobStates.Stopped, w.Jobs.Get(id)!.State);
        await w.Jobs.RunAsync(id, ct);
        Assert.False(w.Has("Photos/pick/a.jpg"));
        w.Jobs.Retry(id);
        await w.Jobs.RunAsync(id, ct);
        Assert.Equal(JobStates.Done, w.Jobs.Get(id)!.State);
        Assert.True(w.Has("Photos/pick/a.jpg"));
        w.Jobs.ClearDone();
        Assert.Empty(w.Jobs.List());

        // A storage this computer has no access to: said so, the job waits for the user.
        var broken = w.Jobs.Upload(photos.Id, "", [Path.Combine(w.Lib.MediaDir, "pick")]);
        var gone = photos.Remote!.Replace(":", "gone:");
        using (var c = w.Lib.Db.Open()) c.Execute("UPDATE source SET remote=@gone WHERE id=@Id", new { gone, photos.Id });
        await w.Jobs.RunAsync(broken, ct);
        var job = w.Jobs.Get(broken)!;
        Assert.Equal(JobStates.Failed, job.State);
        Assert.Contains("not connected", job.Message);
    }

    [Fact]
    public async Task TheOtherComputersFollowWhatWasUploadedMovedAndDeleted()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var a = new World();
        using var b = new World(a.CloudDir);
        var photos = await a.FolderAsync("Photos", ct);
        a.Service.BackupCloud = a.Cloud;
        a.Lib.Db.SetSetting(LibrarySync.SharedSetting, "1");
        b.Service.BackupCloud = await b.Service.AddFolderAsync("Home", b.CloudDir, null, ct, encrypt: false);
        b.Lib.Db.SetSetting(LibrarySync.SharedSetting, "1");
        // It joined with the recovery string: it reaches the storage the same way.
        var remotes = await b.Service.Rclone.DumpAsync(ct);
        foreach (var (name, settings) in await a.Service.Rclone.DumpAsync(ct)) remotes.TryAdd(name, settings);
        Rclone.WriteConfig(Path.Combine(b.Lib.Paths.DataDir, "rclone.conf"), remotes);
        var syncA = new LibrarySync(a.Lib.Db, a.Service, a.Lib.Paths);
        var syncB = new LibrarySync(b.Lib.Db, b.Service, b.Lib.Paths);
        async Task BothAsync()
        {
            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);
            await syncA.RunAsync(ct);
            Assert.Null(syncA.LastError);
            Assert.Null(syncB.LastError);
        }

        a.Lib.AddPhoto("pick/Ski/a.jpg", color: "blue");
        a.Lib.AddPhoto("pick/Ski/b.jpg", color: "green");
        await a.Jobs.RunAsync(a.Jobs.Upload(photos.Id, "", [Path.Combine(a.Lib.MediaDir, "pick", "Ski")]), ct);
        await BothAsync();
        // The other computer has the folder and what was uploaded into it, without reading the cloud itself.
        var seen = Assert.Single(b.Lib.Repo.ListSources());
        Assert.Equal(["Ski/a.jpg", "Ski/b.jpg"], b.Shown(seen.Id));
        var theirs = b.Lib.Repo.Query(new MediaFilter { SourceId = seen.Id }).Single(i => i.RelPath == "Ski/a.jpg");
        // What that computer found in the photo since hangs on its row.
        b.Lib.Repo.SetThumbState(theirs.Id, ThumbStates.Ready);
        b.Lib.Repo.SetPeopleVersion(theirs.Id, Indexer.PeopleVersion);

        // Moved here: there the same row is at the new place, nothing of it is made again.
        await a.Jobs.RunAsync(a.Jobs.Move(photos.Id, ["Ski/a.jpg"], photos.Id, "Best"), ct);
        await BothAsync();
        Assert.Equal(["Best/a.jpg", "Ski/b.jpg"], b.Shown(seen.Id));
        var followed = b.Lib.Repo.Get(theirs.Id)!;
        Assert.Equal(("Best/a.jpg", ThumbStates.Ready), (followed.RelPath, followed.ThumbState));
        using (var c = b.Lib.Db.Open())
            Assert.Equal(2, c.ExecuteScalar<long>("SELECT COUNT(*) FROM media WHERE source_id=@Id", new { seen.Id }));

        // The other computer changes the folder too: a whole folder moved there, a photo deleted.
        seen = b.Lib.Repo.ListSources().Single();
        await b.Jobs.RunAsync(b.Jobs.MoveFolder(seen.Id, "Ski", seen.Id, "2024/Ski"), ct);
        await b.Jobs.RunAsync(b.Jobs.Delete(seen.Id, ["Best/a.jpg"]), ct);
        Assert.All(b.Jobs.List(), j => Assert.Equal(JobStates.Done, j.State));
        await syncB.RunAsync(ct);
        await BothAsync();
        Assert.Equal(["2024/Ski/b.jpg"], a.Shown(photos.Id));
        Assert.Equal(["2024/Ski/b.jpg"], b.Shown(seen.Id));
        Assert.True(a.Has("Photos/2024/Ski/b.jpg") && !a.Has("Photos/Best/a.jpg"));
        // A scan on either finds what the library shows.
        var scan = await a.ScanAsync(photos, ct);
        Assert.Equal((0, 0, 0), (scan.Added, scan.Changed, scan.Missing));
    }
}
