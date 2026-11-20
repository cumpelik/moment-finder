using MediaLens.Cloud;
using MediaLens.Library;
using MediaLens.People;

namespace MediaLens.Tests;

/// <summary>One at a time with the other cloud tests: the cloud files of the app are reached through one shared rclone setup.</summary>
[Collection("Cloud")]
public class CloudTests
{
    [Theory]
    [InlineData("12.5 MiB", 13107200)]
    [InlineData("0 B", 0)]
    [InlineData("1.000 GiB", 1073741824)]
    [InlineData("512 KiB", 524288)]
    public void RcloneSizes(string text, long bytes) => Assert.Equal(bytes, Rclone.ParseSize(text));

    [Fact]
    public void JoinRemotePaths()
    {
        Assert.Equal("mlx:Family", CloudService.Join("mlx:", "Family"));
        Assert.Equal("mlx:bucket/MediaLens/Family/a.jpg", CloudService.Join("mlx:bucket/MediaLens", "Family/a.jpg"));
    }

    /// <summary>
    /// The whole backup story with a folder as the cloud: encrypted mirror, nothing readable in the cloud,
    /// a second library on "another computer" reads it back, and names come back from the index backup.
    /// </summary>
    [Fact]
    public async Task EncryptedMirrorRestoreAndImport()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        lib.AddPhoto("2024/IMG_0001.jpg", color: "red");
        lib.AddPhoto("2024/IMG_0002.jpg", color: "blue");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var people = new PeopleRepository(lib.Db);
        var item = lib.Repo.Query(new MediaFilter()).First(i => i.FileName == "IMG_0001.jpg");
        var faceId = people.ReplaceDetections(item.Id,
            [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.1f, 0.2f, 0.2f, 0.9f, 0.9f, new float[16], null, null)])[0];
        people.Confirm([faceId], people.CreatePerson("Eva").Id);

        var cloudDir = Path.Combine(lib.Root, "cloud");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Backup", cloudDir, "correct horse battery", ct);
        service.BackupCloud = cloud;
        var mirror = await service.AddMirrorAsync(source.Id, cloud.Id, false, ct);
        await service.MirrorAsync(mirror.Id, null, ct);

        var stored = Directory.GetFiles(cloudDir, "*", SearchOption.AllDirectories);
        Assert.True(stored.Length >= 3); // two photos and the index
        Assert.DoesNotContain(stored, f => f.Contains("IMG_", StringComparison.Ordinal) || f.Contains("library", StringComparison.Ordinal));

        // Another computer: same folder, same password.
        using var other = new TestLibrary();
        var service2 = new CloudService(other.Paths, other.Db, other.Repo);
        await Assert.ThrowsAsync<RcloneException>(() => service2.AddFolderAsync("Wrong", cloudDir, "wrong password!", ct));
        var cloud2 = await service2.AddFolderAsync("Backup", cloudDir, "correct horse battery", ct);
        // The mirror sits under the name of the computer that made it.
        var folder = $"{DeviceInfo.Safe(Environment.MachineName)}/test";
        Assert.Equal([folder], await service2.FoldersAsync(cloud2.Id, ct));

        var restored = Path.Combine(other.Root, "restored");
        await service2.RestoreAsync(cloud2.Id, folder, restored, null, ct);
        Assert.Equal(File.ReadAllBytes(Path.Combine(lib.MediaDir, "2024", "IMG_0001.jpg")),
            File.ReadAllBytes(Path.Combine(restored, "2024", "IMG_0001.jpg")));

        // Browse the cloud folder as a source, then import the names.
        var cloudSource = service2.AddCloudSource(cloud2.Id, folder);
        var scan = await new LibraryScanner(other.Repo, service2.Rclone).ScanAsync(other.Repo.ListSources().Single(s => s.Id == cloudSource.Id), null, ct);
        Assert.Equal(2, scan.Added);
        var index = await service2.DownloadIndexAsync(cloud2.Id, ct);
        var result = new IndexImport(other.Db).Import(index!);
        Assert.Equal(2, result.MatchedItems);
        var eva = new PeopleRepository(other.Db).ListPersons().Single();
        Assert.Equal("Eva", eva.Name);
        Assert.Equal(1, eva.MediaCount);
    }

    [Fact]
    public void RecoveryStringRoundTrip()
    {
        var recovery = new Recovery("Family", "ml12345678c:.medialens", new()
        {
            ["ml12345678"] = new() { ["type"] = "s3", ["secret_access_key"] = "s3cr3t/+=", ["endpoint"] = "https://e2.example.com" },
            ["ml12345678c"] = new() { ["type"] = "crypt", ["remote"] = "ml12345678:bucket/media-lens-abc", ["password"] = "obscured" },
        });
        var text = RecoveryString.Encode(recovery);
        Assert.StartsWith("MLR1.", text);
        Assert.DoesNotContain("s3cr3t", text);
        // Pasted from a mail: broken into lines, with spaces around.
        var back = RecoveryString.Parse("  " + text[..30] + "\r\n" + text[30..] + " \n")!;
        Assert.Equal(recovery.BackupRoot, back.BackupRoot);
        Assert.Equal("s3cr3t/+=", back.Remotes["ml12345678"]["secret_access_key"]);
        Assert.Null(RecoveryString.Parse("MLR1.not a string"));
        Assert.Null(RecoveryString.Parse("something else"));
    }

    /// <summary>
    /// Losing the computer: the library is backed up to the cloud, and a new computer with nothing but the recovery
    /// string has it back, with people, thumbnails and search data, and browses the mirrored folder from the cloud.
    /// </summary>
    [Fact]
    public async Task RecoveryStringBringsTheLibraryBack()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        var cloudDir = Path.Combine(Path.GetTempPath(), "medialens-cloud-" + Guid.NewGuid().ToString("N"));
        try
        {
            string recoveryText;
            long itemId;
            byte[] original;
            using (var lib = new TestLibrary())
            {
                original = File.ReadAllBytes(lib.AddPhoto("2024/IMG_0001.jpg", color: "red"));
                lib.AddPhoto("2024/IMG_0002.jpg", color: "blue");
                var source = lib.AddSource();
                await lib.ScanAsync(source);
                var item = lib.Repo.Query(new MediaFilter()).First(i => i.FileName == "IMG_0001.jpg");
                itemId = item.Id;
                var people = new PeopleRepository(lib.Db);
                var face = people.ReplaceDetections(item.Id,
                    [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.1f, 0.2f, 0.2f, 0.9f, 0.9f, new float[16], null, null)])[0];
                people.Confirm([face], people.CreatePerson("Eva").Id);
                Directory.CreateDirectory(Path.GetDirectoryName(lib.Paths.ThumbPath(item.Id))!);
                File.WriteAllText(lib.Paths.ThumbPath(item.Id), "thumbnail");
                File.WriteAllText(Path.Combine(lib.Paths.VectorDir, "scene.vec"), "vectors");
                lib.Db.SetSetting("tile_size", "222");

                var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
                // The library's cloud stores it as it is; a second cloud (for photos) is encrypted, its key made here.
                var cloud = await service.AddFolderAsync("Library", cloudDir, null, ct, encrypt: false);
                Assert.False(cloud.Encrypted);
                var photos = await service.AddFolderAsync("Photos", cloudDir + "-photos", null, ct);
                Assert.True(photos.Encrypted);
                // Adding a cloud does not move the library into it: that is a choice of its own.
                Assert.Null(service.BackupCloud);
                service.BackupCloud = cloud;
                await service.MirrorAsync((await service.AddMirrorAsync(source.Id, cloud.Id, false, ct)).Id, null, ct);
                Assert.Equal("OK", service.Backup.LastResult);
                var stored = Directory.GetFiles(cloudDir, "*", SearchOption.AllDirectories);
                Assert.Contains(stored, f => Path.GetFileName(f) == "library.db");
                Assert.Contains(stored, f => Path.GetFileName(f) == "scene.vec");
                // The clouds' settings hold the keys of every cloud: nowhere readable, by name or by content.
                Assert.DoesNotContain(stored, f => f.Contains("rclone", StringComparison.Ordinal));
                Assert.DoesNotContain(stored, f => new FileInfo(f).Length < 100_000 && File.ReadAllText(f).Contains("type = crypt", StringComparison.Ordinal));
                recoveryText = await service.RecoveryStringAsync(cloud, ct);
            }
            // The old computer is gone, with the folder of photos.

            using var other = new TestLibrary();
            var restore = new LibraryRestore(other.Paths);
            var recovery = RecoveryString.Parse(recoveryText)!;
            var found = Assert.Single(await restore.FindAsync(recovery, ct));
            Assert.Equal(DeviceInfo.Safe(Environment.MachineName), found.Computer);
            await restore.DownloadAsync(recovery, found, thumbnails: true, null, ct);

            // The next start.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.True(LibraryRestore.ApplyPending(other.Paths));
            var db = new Data.Database(other.Paths.DbPath);
            LibraryRestore.AdaptToThisComputer(other.Paths, db);
            var repo = new MediaRepository(db);

            Assert.Equal("222", db.GetSetting("tile_size"));
            Assert.Equal("Eva", new PeopleRepository(db).ListPersons().Single().Name);
            Assert.Equal("thumbnail", File.ReadAllText(other.Paths.ThumbPath(itemId)));
            Assert.Equal("vectors", File.ReadAllText(Path.Combine(other.Paths.VectorDir, "scene.vec")));
            // The folder is not on this computer: it is browsed from the cloud, the same items under the same ids.
            var cloudSource = Assert.Single(repo.ListSources());
            Assert.Equal(SourceKinds.Cloud, cloudSource.Kind);
            var service2 = new CloudService(other.Paths, db, repo);
            Assert.Empty(service2.Mirrors());
            // Both clouds are known again, with their keys.
            Assert.Equal(["Library", "Photos"], service2.Clouds().Select(c => c.Name));
            await service2.Rclone.TestAsync(service2.Clouds().Single(c => c.Name == "Photos").Remote, ct);
            var scan = await new LibraryScanner(repo, service2.Rclone).ScanAsync(cloudSource, null, ct);
            Assert.Equal((0, 0, 0, 2), (scan.Added, scan.Changed, scan.Missing, scan.Total));
            var restored = repo.Query(new MediaFilter()).Single(i => i.Id == itemId);
            Assert.Equal("2024/IMG_0001.jpg", restored.RelPath);
            // Opened: the original comes from the cloud into the cache.
            Assert.Equal(original, File.ReadAllBytes(await MediaFiles.LocalPathAsync(restored, ct)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var dir in new[] { cloudDir, cloudDir + "-photos" })
                try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
