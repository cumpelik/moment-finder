using Dapper;
using MomentFinder.Buckets;
using MomentFinder.Cloud;
using MomentFinder.Library;
using MomentFinder.People;
using MomentFinder.Sync;
using Xunit;

namespace MomentFinder.Tests;

/// <summary>One library on several computers: what the people decide is kept as facts under keys that mean the same everywhere.</summary>
[Collection("Cloud")]
public class SyncTests
{
    private static List<Change> Outbox(TestLibrary lib)
    {
        using var c = lib.Db.Open();
        return c.Query<Change>("SELECT obj AS Obj, field AS Field, value AS Value, hlc AS Hlc FROM outbox ORDER BY seq").ToList();
    }

    private static string? Fact(TestLibrary lib, string obj, string field)
    {
        using var c = lib.Db.Open();
        return FactStore.Get(c, null, obj, field);
    }

    private static string Uid(TestLibrary lib, string table, long id)
    {
        using var c = lib.Db.Open();
        return c.ExecuteScalar<string>($"SELECT uid FROM {table} WHERE id=@id", new { id })!;
    }

    [Fact]
    public async Task EveryDecisionIsAFactWithASharedKey()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("2024/a.jpg");
        lib.AddPhoto("2024/b.jpg");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var items = lib.Repo.Query(new MediaFilter()).OrderBy(i => i.RelPath).ToList();
        // A photo's key is made of its folder and its place: another computer reading the folder finds the same.
        Assert.Equal(Keys.MediaUid(Uid(lib, "source", source.Id), "2024/a.jpg"), Uid(lib, "media", items[0].Id));

        var people = new PeopleRepository(lib.Db);
        var face = people.ReplaceDetections(items[0].Id,
            [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.2f, 0.3f, 0.4f, 0.9f, 0.9f, new float[16], null, null)])[0];
        var eva = people.CreatePerson("Eva");
        var evaKey = Keys.Person(Uid(lib, "person", eva.Id));
        Assert.Equal("Eva", Fact(lib, evaKey, "name"));
        var detection = Keys.Detection(Uid(lib, "media", items[0].Id), (int)Analysis.DetectionKind.Face, 0, 0.1, 0.2, 0.3, 0.4);

        people.Confirm([face], eva.Id);
        Assert.Equal(PersonFacts.Yes, Fact(lib, detection, Uid(lib, "person", eva.Id)));
        people.Reject([face], eva.Id);
        Assert.Equal(PersonFacts.No, Fact(lib, detection, Uid(lib, "person", eva.Id)));
        people.RenamePerson(eva.Id, "Eva Nová");
        Assert.Equal("Eva Nová", Fact(lib, evaKey, "name"));

        var buckets = new BucketService(lib.Db, lib.Repo);
        var book = buckets.Create("Book");
        var bookKey = Keys.Bucket(Uid(lib, "bucket", book.Id));
        buckets.Add(book.Id, [items[0].Id, items[1].Id]);
        var itemB = Keys.BucketItem(Uid(lib, "bucket", book.Id), Uid(lib, "media", items[1].Id));
        Assert.Equal(("1", "2"), (Fact(lib, itemB, "in"), Fact(lib, itemB, "pos")));
        buckets.Reorder(book.Id, [items[1].Id, items[0].Id]);
        Assert.Equal("1", Fact(lib, itemB, "pos"));
        buckets.Remove(book.Id, [items[1].Id]);
        Assert.Equal("0", Fact(lib, itemB, "in"));
        buckets.Delete(book.Id);
        Assert.Equal("1", Fact(lib, bookKey, "deleted"));

        // Everything waits to be sent, in order, each with a later time than the one before.
        var sent = Outbox(lib);
        Assert.True(sent.Count >= 12);
        Assert.Equal(sent.Select(s => s.Hlc).Order(StringComparer.Ordinal), sent.Select(s => s.Hlc));
        // A folder only on this disk is nobody else's business.
        Assert.DoesNotContain(sent, s => s.Obj.StartsWith("f:", StringComparison.Ordinal));
    }

    [Fact]
    public void MergedPersonIsFoundUnderTheNewKey()
    {
        using var lib = new TestLibrary();
        var people = new PeopleRepository(lib.Db);
        var eva = people.CreatePerson("Eva");
        var evicka = people.CreatePerson("Evička");
        var from = Uid(lib, "person", evicka.Id);
        people.MergePersons(evicka.Id, eva.Id);
        Assert.Equal(Uid(lib, "person", eva.Id), Fact(lib, Keys.Person(from), "into"));
        using var c = lib.Db.Open();
        Assert.Equal(Uid(lib, "person", eva.Id), c.ExecuteScalar<string>("SELECT into_uid FROM person_alias WHERE from_uid=@from", new { from }));
    }

    [Fact]
    public async Task ALibraryFromBeforeSharingKeepsWhatWasDecided()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("a.jpg");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var item = lib.Repo.Query(new MediaFilter()).Single();
        var people = new PeopleRepository(lib.Db);
        var face = people.ReplaceDetections(item.Id,
            [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.2f, 0.3f, 0.4f, 0.9f, 0.9f, new float[16], null, null)])[0];
        var eva = people.CreatePerson("Eva");
        people.Confirm([face], eva.Id);
        // As if the library came from a version before sharing: no facts yet.
        using (var c = lib.Db.Open())
            c.Execute("DELETE FROM fact; DELETE FROM outbox; DELETE FROM setting WHERE key='facts_seeded';");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var reopened = new Data.Database(lib.Paths.DbPath);
        using var r = reopened.Open();
        var evaUid = r.ExecuteScalar<string>("SELECT uid FROM person WHERE id=@Id", new { eva.Id })!;
        Assert.Equal("Eva", FactStore.Get(r, null, Keys.Person(evaUid), "name"));
        var detection = Keys.Detection(r.ExecuteScalar<string>("SELECT uid FROM media")!, (int)Analysis.DetectionKind.Face, 0, 0.1, 0.2, 0.3, 0.4);
        Assert.Equal(PersonFacts.Yes, FactStore.Get(r, null, detection, evaUid));
        // The oldest time: whatever anybody changes later wins; and nothing of it is sent (the first snapshot carries it).
        Assert.StartsWith("000000000000.0000.", r.ExecuteScalar<string>("SELECT MIN(hlc) FROM fact"));
        Assert.Equal(0, r.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox"));
    }

    /// <summary>A computer of a shared library: its cloud is the folder both see.</summary>
    private static async Task<LibrarySync> SharedAsync(TestLibrary lib, string cloudDir, CancellationToken ct)
    {
        var service = new Cloud.CloudService(lib.Paths, lib.Db, lib.Repo);
        service.BackupCloud = await service.AddFolderAsync("Home", cloudDir, null, ct, encrypt: false);
        lib.Db.SetSetting(LibrarySync.SharedSetting, "1");
        return new LibrarySync(lib.Db, service, lib.Paths);
    }

    private static List<long> Faces(TestLibrary lib, long mediaId) =>
        new PeopleRepository(lib.Db).ReplaceDetections(mediaId,
            [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.2f, 0.3f, 0.4f, 0.9f, 0.9f, new float[16], null, null)]);

    [Fact]
    public async Task TwoComputersShareNamesBucketsAndWhoIsWho()
    {
        Assert.SkipUnless(Cloud.Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        var cloudDir = Path.Combine(Path.GetTempPath(), "momentfinder-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloudDir);
        try
        {
            using var a = new TestLibrary();
            using var b = new TestLibrary();
            foreach (var lib in new[] { a, b })
            {
                lib.AddPhoto("a.jpg", color: "red");
                lib.AddPhoto("b.jpg", color: "blue");
            }
            var sa = a.AddSource();
            var sb = b.AddSource();
            // One folder both see (as a folder in the cloud is): the same key, so the same keys of its photos.
            using (var c = b.Db.Open()) c.Execute("UPDATE source SET uid=@uid WHERE id=@Id", new { uid = Uid(a, "source", sa.Id), sb.Id });
            await a.ScanAsync(sa);
            await b.ScanAsync(sb);
            long Item(TestLibrary lib, string name) => lib.Repo.Query(new MediaFilter()).Single(i => i.FileName == name).Id;
            var faceA = Faces(a, Item(a, "a.jpg"))[0];
            var faceB = Faces(b, Item(b, "a.jpg"))[0];
            var syncA = await SharedAsync(a, cloudDir, ct);
            var syncB = await SharedAsync(b, cloudDir, ct);
            var peopleA = new PeopleRepository(a.Db);
            var peopleB = new PeopleRepository(b.Db);

            // Named on one computer: the other knows the person, who is on the face, and the bucket in its order.
            var eva = peopleA.CreatePerson("Eva");
            peopleA.Confirm([faceA], eva.Id);
            var bucketsA = new BucketService(a.Db, a.Repo);
            var book = bucketsA.Create("Book");
            bucketsA.Add(book.Id, [Item(a, "b.jpg"), Item(a, "a.jpg")]);
            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);
            var evaB = peopleB.FindPerson("Eva")!;
            var detB = peopleB.DetectionsOfMedia(Item(b, "a.jpg")).Single();
            Assert.Equal((evaB.Id, true), (detB.PersonId!.Value, detB.Confirmed));
            var bucketsB = new BucketService(b.Db, b.Repo);
            var bookB = bucketsB.Find("Book")!;
            Assert.Equal([Item(b, "b.jpg"), Item(b, "a.jpg")], b.Repo.Query(new MediaFilter { BucketId = bookB.Id }).Select(i => i.Id));

            // Corrected on the other one: "not Eva", and her new name, come back.
            peopleB.Reject([faceB], evaB.Id);
            peopleB.RenamePerson(evaB.Id, "Eva K.");
            await syncB.RunAsync(ct);
            await syncA.RunAsync(ct);
            var detA = peopleA.DetectionsOfMedia(Item(a, "a.jpg")).Single();
            Assert.False(detA.Confirmed);
            Assert.Equal("Eva K.", peopleA.ListPersons().Single().Name);
            using (var c = a.Db.Open())
                Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM rejection WHERE detection_id=@faceA AND person_id=@Id", new { faceA, eva.Id }));
            // A new analysis of the photo keeps the "not Eva" (it used to drop it).
            var again = Faces(a, Item(a, "a.jpg"))[0];
            using (var c = a.Db.Open())
                Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM rejection WHERE detection_id=@again", new { again }));

            // Both rename at once: the later name counts on both.
            peopleA.RenamePerson(eva.Id, "Eva A");
            // Within one millisecond neither is later (the computer decides then): this one is clearly later.
            await Task.Delay(20, ct);
            peopleB.RenamePerson(evaB.Id, "Eva B");
            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);
            await syncA.RunAsync(ct);
            Assert.Equal("Eva B", peopleA.ListPersons().Single().Name);
            Assert.Equal("Eva B", peopleB.ListPersons().Single().Name);

            // Said of a photo the other computer has not analyzed yet: it waits, and is there when it is analyzed.
            using (var c = b.Db.Open()) c.Execute("DELETE FROM detection WHERE media_id=@id", new { id = Item(b, "b.jpg") });
            var faceBb = Faces(a, Item(a, "b.jpg"))[0];
            peopleA.Confirm([faceBb], eva.Id);
            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);
            var later = Faces(b, Item(b, "b.jpg"))[0];
            Assert.Equal(evaB.Id, peopleB.DetectionsOfMedia(Item(b, "b.jpg")).Single(d => d.Id == later).PersonId);

            // Nothing is taken in twice, nothing is lost: one more round changes nothing.
            await syncB.RunAsync(ct);
            Assert.Null(syncB.LastError);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(cloudDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task EveryComputerSeesTheSameFilesOfAFolderInTheCloud()
    {
        Assert.SkipUnless(Cloud.Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        var cloudDir = Path.Combine(Path.GetTempPath(), "momentfinder-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloudDir);
        try
        {
            using var a = new TestLibrary();
            using var b = new TestLibrary();
            a.AddPhoto("2024/a.jpg", color: "red");
            a.AddPhoto("2024/b.jpg", color: "blue");
            var folder = a.AddSource();
            await a.ScanAsync(folder);
            var syncA = await SharedAsync(a, cloudDir, ct);
            var syncB = await SharedAsync(b, cloudDir, ct);
            var cloudA = new Cloud.CloudService(a.Paths, a.Db, a.Repo);
            var mirror = await cloudA.AddMirrorAsync(folder.Id, cloudA.BackupCloud!.Id, false, ct);
            // A photo read here but not backed up yet: the other computer must not see it (it could not open it).
            a.AddPhoto("2024/c.jpg", color: "green");
            await a.ScanAsync(folder);
            File.Move(Path.Combine(a.MediaDir, "2024/c.jpg"), Path.Combine(a.Root, "c.jpg"));
            await cloudA.MirrorAsync(mirror.Id, null, ct);
            File.Move(Path.Combine(a.Root, "c.jpg"), Path.Combine(a.MediaDir, "2024/c.jpg"));

            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);
            // The folder is there, from the cloud, only for looking; with its files as the first computer said them.
            var seen = b.Repo.ListSources().Single();
            Assert.Equal((SourceKinds.Cloud, true), (seen.Kind, seen.ReadOnly));
            Assert.True(Catalog.KnownFromOthers(b.Db, seen));
            Assert.Equal(["2024/a.jpg", "2024/b.jpg"], b.Repo.Query(new MediaFilter()).Select(i => i.RelPath).Order());
            var taken = b.Repo.Query(new MediaFilter()).Single(i => i.RelPath == "2024/a.jpg");
            var original = a.Repo.Query(new MediaFilter()).Single(i => i.RelPath == "2024/a.jpg");
            Assert.Equal((original.Size, original.TakenAt), (taken.Size, taken.TakenAt));

            // Backed up now, and one deleted: the other computer follows the cloud.
            File.Delete(Path.Combine(a.MediaDir, "2024/a.jpg"));
            await a.ScanAsync(folder);
            await cloudA.MirrorAsync(mirror.Id, null, ct);
            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);
            Assert.Equal(["2024/b.jpg", "2024/c.jpg"], b.Repo.Query(new MediaFilter()).Select(i => i.RelPath).Order());

            // Removed from the library on one computer: removed on every one (the files stay where they are).
            b.Repo.RemoveSource(seen.Id);
            await syncB.RunAsync(ct);
            await syncA.RunAsync(ct);
            Assert.Empty(a.Repo.ListSources());
            Assert.True(File.Exists(Path.Combine(a.MediaDir, "2024/b.jpg")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(cloudDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ThumbnailsAndSearchDataAreMadeOnceForAllComputers()
    {
        Assert.SkipUnless(Cloud.Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        var cloudDir = Path.Combine(Path.GetTempPath(), "momentfinder-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloudDir);
        try
        {
            using var a = new TestLibrary();
            using var b = new TestLibrary();
            a.AddPhoto("a.jpg", color: "red");
            a.AddPhoto("b.jpg", color: "blue");
            var folder = a.AddSource();
            await a.ScanAsync(folder);
            var syncA = await SharedAsync(a, cloudDir, ct);
            var syncB = await SharedAsync(b, cloudDir, ct);
            var cloudA = new Cloud.CloudService(a.Paths, a.Db, a.Repo);
            await cloudA.MirrorAsync((await cloudA.AddMirrorAsync(folder.Id, cloudA.BackupCloud!.Id, false, ct)).Id, null, ct);

            // The first computer made the thumbnails and the search data.
            using var sceneA = new Vectors.FlatVectorIndex(a.Db, Path.Combine(a.Paths.VectorDir, "scene.vec"), "test-model", 4, () => { });
            using var sceneB = new Vectors.FlatVectorIndex(b.Db, Path.Combine(b.Paths.VectorDir, "scene.vec"), "test-model", 4, () => { });
            syncA.Content.Scene = sceneA;
            syncB.Content.Scene = sceneB;
            foreach (var item in a.Repo.Query(new MediaFilter()))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(a.Paths.ThumbPath(item.Id))!);
                File.WriteAllText(a.Paths.ThumbPath(item.Id), "thumb of " + item.FileName);
                a.Repo.SetThumbState(item.Id, ThumbStates.Ready);
                sceneA.Put(item.Id, [new Vectors.VectorRow(item.Id, 0, [1, 0, 0, item.Id])]);
                a.Repo.SetAnalysisVersion(item.Id, Indexer.AnalysisVersion);
            }
            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);

            // The other one takes them, without the photos.
            Assert.True(await syncB.Content.TakeAsync(ct));
            await syncB.Content.TakeAsync(ct);
            foreach (var item in b.Repo.Query(new MediaFilter()))
            {
                Assert.Equal("thumb of " + item.FileName, File.ReadAllText(b.Paths.ThumbPath(item.Id)));
                var original = a.Repo.Query(new MediaFilter()).Single(i => i.RelPath == item.RelPath);
                Assert.Equal(original.Id, sceneB.RowsOf(item.Id).Single().Vector[3]);
            }
            using (var c = b.Db.Open())
                Assert.Equal(0, c.ExecuteScalar<long>("SELECT COUNT(*) FROM media WHERE thumb_state<>1 OR analysis_version<@v", new { v = Indexer.AnalysisVersion }));
            Assert.Empty(Directory.GetFiles(Path.Combine(b.Paths.CacheDir, "cloud"), "*", SearchOption.AllDirectories));
            // Stored once: the second computer gives nothing more.
            Assert.Equal(0, await syncB.Content.GiveAsync(ct));
            Assert.Equal(4, Directory.GetFiles(Path.Combine(cloudDir, ".medialens", "shared", "content"), "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(cloudDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task AComputerJoinsTheSharedLibraryAndKeepsItsOwnWork()
    {
        Assert.SkipUnless(Cloud.Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        var cloudDir = Path.Combine(Path.GetTempPath(), "momentfinder-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloudDir);
        try
        {
            using var a = new TestLibrary();
            using var b = new TestLibrary();
            // The first computer: a folder backed up to the cloud, one only on its disk, a name, a bucket.
            a.AddPhoto("shared/a.jpg", color: "red");
            a.AddPhoto("shared/b.jpg", color: "blue");
            a.AddPhoto("private/p.jpg", color: "yellow");
            var shared = a.Repo.AddSource(SourceKinds.Local, Path.Combine(a.MediaDir, "shared"), "Shared");
            var mine = a.Repo.AddSource(SourceKinds.Local, Path.Combine(a.MediaDir, "private"), "Mine");
            await new LibraryScanner(a.Repo).ScanAsync(shared, null, ct);
            await new LibraryScanner(a.Repo).ScanAsync(mine, null, ct);
            var syncA = await SharedAsync(a, cloudDir, ct);
            a.Db.SetSetting(LibrarySync.SharedSetting, null);
            var cloudA = new Cloud.CloudService(a.Paths, a.Db, a.Repo);
            await cloudA.MirrorAsync((await cloudA.AddMirrorAsync(shared.Id, cloudA.BackupCloud!.Id, false, ct)).Id, null, ct);
            var photoA = a.Repo.Query(new MediaFilter()).Single(i => i.FileName == "a.jpg");
            var peopleA = new PeopleRepository(a.Db);
            peopleA.Confirm([Faces(a, photoA.Id)[0]], peopleA.CreatePerson("Eva").Id);
            foreach (var item in a.Repo.Query(new MediaFilter()))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(a.Paths.ThumbPath(item.Id))!);
                File.WriteAllText(a.Paths.ThumbPath(item.Id), "thumb of " + item.FileName);
                a.Repo.SetThumbState(item.Id, ThumbStates.Ready);
            }

            // The second computer has a library of its own: a folder on its disk, analyzed and named.
            b.AddPhoto("own/o.jpg", color: "green");
            var own = b.Repo.AddSource(SourceKinds.Local, Path.Combine(b.MediaDir, "own"), "Own");
            await new LibraryScanner(b.Repo).ScanAsync(own, null, ct);
            var photoB = b.Repo.Query(new MediaFilter()).Single();
            var peopleB = new PeopleRepository(b.Db);
            var otaFace = Faces(b, photoB.Id)[0];
            peopleB.Confirm([otaFace], peopleB.CreatePerson("Ota").Id);
            Directory.CreateDirectory(Path.GetDirectoryName(b.Paths.ThumbPath(photoB.Id))!);
            File.WriteAllText(b.Paths.ThumbPath(photoB.Id), "thumb of o.jpg");
            b.Repo.SetThumbState(photoB.Id, ThumbStates.Ready);
            Directory.CreateDirectory(Path.GetDirectoryName(b.Paths.PeopleCropPath(otaFace))!);
            File.WriteAllText(b.Paths.PeopleCropPath(otaFace), "crop of Ota");

            // The first one shares its library; it gives its thumbnails.
            Assert.False(await syncA.SharedLibraryExistsAsync(ct));
            await syncA.ShareAsync(null, ct);
            Assert.True(await syncA.SharedLibraryExistsAsync(ct));
            await syncA.RunAsync(ct);

            // The second joins it with the recovery string.
            var recovery = RecoveryString.Parse(await cloudA.RecoveryStringAsync(cloudA.BackupCloud!, ct))!;
            var restore = new Cloud.LibraryRestore(b.Paths);
            var found = await restore.FindAsync(recovery, ct);
            Assert.True(found[0].Shared);
            await restore.DownloadAsync(recovery, found[0], thumbnails: false, null, ct);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.True(Cloud.LibraryRestore.ApplyPending(b.Paths));
            var db = new Data.Database(b.Paths.DbPath);
            // Another computer: the first one's folders are not on its disk.
            var away = a.MediaDir + "-elsewhere";
            Directory.Move(a.MediaDir, away);
            try
            {
                Cloud.LibraryRestore.AdaptToThisComputer(b.Paths, db);
            }
            finally
            {
                Directory.Move(away, a.MediaDir);
            }
            var repo = new MediaRepository(db);

            // The shared folder from the cloud, the first computer's own folder not at all, its own folder with all its work.
            var sources = repo.ListSources().ToDictionary(s => s.Name);
            Assert.Equal(["Own", "Shared"], sources.Keys.Order());
            Assert.Equal((SourceKinds.Cloud, true), (sources["Shared"].Kind, sources["Shared"].ReadOnly));
            var people = new PeopleRepository(db);
            var ownPhoto = repo.Query(new MediaFilter { SourceId = sources["Own"].Id }).Single();
            var ownFace = people.DetectionsOfMedia(ownPhoto.Id).Single();
            Assert.Equal(("Ota", true), (people.ListPersons().Single(p => p.Id == ownFace.PersonId).Name, ownFace.Confirmed));
            Assert.Equal("thumb of o.jpg", File.ReadAllText(b.Paths.ThumbPath(ownPhoto.Id)));
            Assert.Equal("crop of Ota", File.ReadAllText(b.Paths.PeopleCropPath(ownFace.Id)));
            var restored = Cloud.LibraryRestore.LastRestored(db)!;
            Assert.Equal((true, 1), (restored.Shared, restored.OwnFolders));
            // Eva is known with her photo; the photos come with their thumbnails from the shared content.
            var sharedPhoto = repo.Query(new MediaFilter()).Single(i => i.FileName == "a.jpg");
            Assert.Equal("Eva", people.ListPersons().Single(p => p.Id == people.DetectionsOfMedia(sharedPhoto.Id).Single().PersonId).Name);
            var cloudB = new Cloud.CloudService(b.Paths, db, repo);
            var syncB = new LibrarySync(db, cloudB, b.Paths);
            Assert.True(syncB.Enabled);
            // The changes made after the summary (the thumbnails given) come in with the first round.
            await syncB.RunAsync(ct);
            Assert.True(await syncB.Content.TakeAsync(ct));
            Assert.Equal("thumb of a.jpg", File.ReadAllText(b.Paths.ThumbPath(sharedPhoto.Id)));

            // From now on they share: Ota (named on the second one) reaches the first, a new name of Eva the second.
            await syncB.RunAsync(ct);
            await syncA.RunAsync(ct);
            Assert.Contains("Ota", peopleA.ListPersons().Select(p => p.Name));
            peopleA.RenamePerson(peopleA.FindPerson("Eva")!.Id, "Eva K.");
            await syncA.RunAsync(ct);
            await syncB.RunAsync(ct);
            Assert.Contains("Eva K.", people.ListPersons().Select(p => p.Name));
            Assert.Null(syncB.LastError);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(cloudDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheBackupOfEachComputerFromBeforeGoesOnceTheLibraryIsShared()
    {
        Assert.SkipUnless(Cloud.Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        var cloudDir = Path.Combine(Path.GetTempPath(), "momentfinder-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloudDir);
        try
        {
            using var a = new TestLibrary();
            a.AddPhoto("a.jpg");
            await a.ScanAsync(a.AddSource());
            var sync = await SharedAsync(a, cloudDir, ct);
            a.Db.SetSetting(LibrarySync.SharedSetting, null);
            var cloud = new Cloud.CloudService(a.Paths, a.Db, a.Repo);
            await cloud.Backup.RunAsync(cloud.BackupCloud!, full: true, null, ct);
            var own = Path.Combine(cloudDir, ".medialens", cloud.Device.Folder);
            Assert.True(File.Exists(Path.Combine(own, "library.db")));
            await sync.ShareAsync(null, ct);
            using var scene = new Vectors.FlatVectorIndex(a.Db, Path.Combine(a.Paths.VectorDir, "scene.vec"), "test-model", 4, () => { });
            sync.Content.Scene = scene;

            // Kept for a few days, in case.
            await sync.RunAsync(ct);
            Assert.True(Directory.Exists(own));
            sync.KeepOwnBackup = TimeSpan.Zero;
            await sync.RunAsync(ct);
            Assert.False(Directory.Exists(own));
            Assert.Null(sync.LastError);
            Assert.True(await sync.SharedLibraryExistsAsync(ct));

            // Of the summaries the newest two stay.
            for (var i = 0; i < 3; i++)
            {
                a.Db.SetSetting("snapshot_at", "0");
                await Task.Delay(5, ct);
                await sync.RunAsync(ct);
            }
            Assert.Equal(2, (await LibrarySync.SnapshotsAsync(cloud.Rclone, cloud.BackupRoot(cloud.BackupCloud!), ct)).Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(cloudDir, true); } catch (IOException) { }
        }
    }
}
