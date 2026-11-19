using ImageMagick;
using MediaLens.Data;
using MediaLens.Library;

namespace MediaLens.Tests;

/// <summary>A throwaway library and a folder of generated media, deleted after the test.</summary>
internal sealed class TestLibrary : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "medialens-test-" + Guid.NewGuid().ToString("N"));
    public string MediaDir => Path.Combine(Root, "media");
    public AppPaths Paths { get; }
    public Database Db { get; }
    public MediaRepository Repo { get; }

    public TestLibrary()
    {
        Directory.CreateDirectory(MediaDir);
        Paths = new AppPaths(Path.Combine(Root, "data"), Path.Combine(Root, "models"));
        Db = new Database(Paths.DbPath);
        Repo = new MediaRepository(Db);
    }

    public string AddPhoto(string relPath, int width = 64, int height = 48, string color = "red")
    {
        var path = Path.Combine(MediaDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(new MagickColor(color), (uint)width, (uint)height);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    public Source AddSource() => Repo.AddSource(SourceKinds.Local, MediaDir, "test");

    public async Task<ScanResult> ScanAsync(Source source) =>
        await new LibraryScanner(Repo).ScanAsync(source, null, CancellationToken.None);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
