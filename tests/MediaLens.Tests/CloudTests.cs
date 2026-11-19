using MediaLens.Cloud;
using MediaLens.Library;
using MediaLens.People;

namespace MediaLens.Tests;

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
        var mirror = service.AddMirror(source.Id, cloud.Id);
        await service.MirrorAsync(mirror.Id, null, ct);

        var stored = Directory.GetFiles(cloudDir, "*", SearchOption.AllDirectories);
        Assert.True(stored.Length >= 3); // two photos and the index
        Assert.DoesNotContain(stored, f => f.Contains("IMG_", StringComparison.Ordinal) || f.Contains("library", StringComparison.Ordinal));

        // Another computer: same folder, same password.
        using var other = new TestLibrary();
        var service2 = new CloudService(other.Paths, other.Db, other.Repo);
        await Assert.ThrowsAsync<RcloneException>(() => service2.AddFolderAsync("Wrong", cloudDir, "wrong password!", ct));
        var cloud2 = await service2.AddFolderAsync("Backup", cloudDir, "correct horse battery", ct);
        Assert.Equal(["test"], await service2.FoldersAsync(cloud2.Id, ct));

        var restored = Path.Combine(other.Root, "restored");
        await service2.RestoreAsync(cloud2.Id, "test", restored, null, ct);
        Assert.Equal(File.ReadAllBytes(Path.Combine(lib.MediaDir, "2024", "IMG_0001.jpg")),
            File.ReadAllBytes(Path.Combine(restored, "2024", "IMG_0001.jpg")));

        // Browse the cloud folder as a source, then import the names.
        var cloudSource = service2.AddCloudSource(cloud2.Id, "test");
        var scan = await new LibraryScanner(other.Repo, service2.Rclone).ScanAsync(other.Repo.ListSources().Single(s => s.Id == cloudSource.Id), null, ct);
        Assert.Equal(2, scan.Added);
        var index = await service2.DownloadIndexAsync(cloud2.Id, ct);
        var result = new IndexImport(other.Db).Import(index!);
        Assert.Equal(2, result.MatchedItems);
        var eva = new PeopleRepository(other.Db).ListPersons().Single();
        Assert.Equal("Eva", eva.Name);
        Assert.Equal(1, eva.MediaCount);
    }
}
