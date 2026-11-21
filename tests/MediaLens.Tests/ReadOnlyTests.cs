using MediaLens.Cloud;
using MediaLens.Library;

namespace MediaLens.Tests;

/// <summary>A read-only source is not changed from MediaLens until read-only is turned off with the password.</summary>
[Collection("Cloud")]
public class ReadOnlyTests
{
    [Fact]
    public void ReadOnlyIsTurnedOffOnlyWithThePassword()
    {
        using var lib = new TestLibrary();
        var source = lib.AddSource();
        var locks = lib.Repo.ReadOnly;
        Assert.Throws<InvalidOperationException>(() => locks.Protect(source.Id));
        locks.SetPassword("tajne");
        locks.Protect(source.Id);
        Assert.True(locks.IsLocked(source.Id));
        Assert.Throws<InvalidOperationException>(() => lib.Repo.RemoveSource(source.Id));

        Assert.False(locks.TurnOff(source.Id, "spatne"));
        Assert.True(locks.IsLocked(source.Id));
        Assert.True(locks.TurnOff(source.Id, "tajne"));
        Assert.False(locks.IsLocked(source.Id));
        locks.TurnOn(source.Id);
        Assert.True(locks.IsLocked(source.Id));

        // Another start of the application: read-only again, the password still the same.
        var again = new ReadOnlyLock(lib.Db);
        Assert.True(again.IsLocked(source.Id));
        Assert.True(again.Check("tajne"));
        Assert.Throws<InvalidOperationException>(() => again.Unprotect(source.Id));
        Assert.True(again.TurnOff(source.Id, "tajne"));
        again.Unprotect(source.Id);
        Assert.False(new ReadOnlyLock(lib.Db).IsLocked(source.Id));
    }

    [Fact]
    public async Task AFolderOfTheStorageBecomesASourceAndReadOnlyKeepsItSafe()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        var storage = Path.Combine(lib.Root, "storage");
        Directory.CreateDirectory(Path.Combine(storage, "Photos", "2024"));
        Directory.CreateDirectory(Path.Combine(storage, "media-lens-xyz"));
        File.WriteAllText(Path.Combine(storage, "Photos", "2024", "a.jpg"), "x");
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        var cloud = await service.AddFolderAsync("Store", storage, null, ct, encrypt: false);

        var root = await service.StorageRootAsync(cloud, ct);
        Assert.Equal(["Photos"], await service.FoldersAsync(root, ct));
        Assert.Equal(["2024"], await service.FoldersAsync(CloudService.Join(root, "Photos"), ct));
        var source = service.AddCloudSourceAt(cloud.Id, CloudService.Join(root, "Photos"), "Photos (Store)");
        Assert.Equal(cloud.Id, source.CloudId);
        Assert.Contains(service.MergeTargets(), t => t.Id == source.Id);

        lib.Repo.ReadOnly.SetPassword("tajne");
        lib.Repo.ReadOnly.Protect(source.Id);
        // Nothing can be added to it, nor can it get a home here.
        Assert.DoesNotContain(service.MergeTargets(), t => t.Id == source.Id);
        Assert.Throws<InvalidOperationException>(() => service.RequestMerge(lib.AddSource().Id, source.Id, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MakeHomeAsync(source.Id, Path.Combine(lib.Root, "home"), null, ct));
    }
}
