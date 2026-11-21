namespace MomentFinder;

/// <summary>Where the library keeps its data. One library per user; MOMENTFINDER_DATA overrides it (tests, CLI, portable use).</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string DbPath => Path.Combine(DataDir, "library.db");
    public string ThumbDir => Path.Combine(DataDir, "thumbs");
    public string VectorDir => Path.Combine(DataDir, "vectors");
    public string CacheDir => Path.Combine(DataDir, "cache");

    /// <summary>Models are shared by all libraries of the user, so a test library does not download them again.</summary>
    public string ModelDir { get; }

    public AppPaths(string? dataDir = null, string? modelDir = null)
    {
        var root = DefaultRoot();
        DataDir = dataDir ?? Environment.GetEnvironmentVariable("MOMENTFINDER_DATA") ?? root;
        ModelDir = modelDir ?? Environment.GetEnvironmentVariable("MOMENTFINDER_MODELS") ?? Path.Combine(root, "models");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(VectorDir);
        Directory.CreateDirectory(CacheDir);
        Media.Proxy360.Dir = Path.Combine(CacheDir, "360");
        Directory.CreateDirectory(ModelDir);
    }

    /// <summary>
    /// The user's data folder. The application was called MediaLens first: its folder becomes this one at the first
    /// start, and while that cannot happen (the old version still running) the old folder is used as it is.
    /// </summary>
    private static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        var root = Path.Combine(local, "MomentFinder");
        var before = Path.Combine(local, "MediaLens");
        if (Directory.Exists(root) || !Directory.Exists(before)) return root;
        try
        {
            Directory.Move(before, root);
            return root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return before;
        }
    }

    public string ThumbPath(long mediaId) => Path.Combine(ThumbDir, (mediaId / 1000).ToString(), mediaId + ".jpg");

    /// <summary>The crop of a face or a person, shown in the people screens.</summary>
    public string PeopleCropPath(long detectionId) => Path.Combine(ThumbDir, "people", (detectionId / 1000).ToString(), detectionId + ".jpg");
}
