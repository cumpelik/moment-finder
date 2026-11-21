using MediaLens.Library;

namespace MediaLens.Tests;

/// <summary>A folder that changes is told once it has been quiet; hidden files and a quiet folder are not.</summary>
public class FolderWatcherTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private static async Task<List<long>> CollectAsync(FolderWatcher watcher, Action act, TimeSpan wait)
    {
        var told = new List<long>();
        watcher.FolderChanged += id => { lock (told) told.Add(id); };
        act();
        await Task.Delay(wait, TestContext.Current.CancellationToken);
        lock (told) return [.. told];
    }

    [Fact]
    public async Task ChangesAreToldOnceWhenTheFolderIsQuiet()
    {
        using var lib = new TestLibrary();
        var source = lib.AddSource();
        using var watcher = new FolderWatcher(lib.Repo, Quiet);
        var told = await CollectAsync(watcher, () =>
        {
            // A copy of several files with short pauses: one change, after the last one.
            for (var i = 0; i < 4; i++)
            {
                lib.AddPhoto($"2025/p{i}.jpg");
                Thread.Sleep(150);
            }
            File.Move(Path.Combine(lib.MediaDir, "2025", "p0.jpg"), Path.Combine(lib.MediaDir, "moved.jpg"));
            File.Delete(Path.Combine(lib.MediaDir, "2025", "p1.jpg"));
        }, Quiet * 4);
        Assert.Equal([source.Id], told);
    }

    [Fact]
    public async Task HiddenFilesAreNoChange()
    {
        using var lib = new TestLibrary();
        lib.AddSource();
        using var watcher = new FolderWatcher(lib.Repo, Quiet);
        var told = await CollectAsync(watcher, () =>
        {
            File.WriteAllText(Path.Combine(lib.MediaDir, ".hidden"), "x");
            Directory.CreateDirectory(Path.Combine(lib.MediaDir, "@eaDir"));
            File.WriteAllText(Path.Combine(lib.MediaDir, "@eaDir", "thumb.jpg"), "x");
        }, Quiet * 3);
        Assert.Empty(told);
    }

    [Fact]
    public async Task AFolderThatComesBackIsTold()
    {
        using var lib = new TestLibrary();
        var source = lib.AddSource();
        var away = lib.MediaDir + "-away";
        Directory.Move(lib.MediaDir, away);
        using var watcher = new FolderWatcher(lib.Repo, Quiet);
        var told = await CollectAsync(watcher, () =>
        {
            // The disk is connected again: the next look finds it.
            Directory.Move(away, lib.MediaDir);
            watcher.Refresh();
        }, Quiet * 3);
        Assert.Equal([source.Id], told);
    }
}
