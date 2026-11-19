namespace MediaLens;

/// <summary>Where the library keeps its data. One library per user; MEDIALENS_DATA overrides it (tests, CLI, portable use).</summary>
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
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create), "MediaLens");
        DataDir = dataDir ?? Environment.GetEnvironmentVariable("MEDIALENS_DATA") ?? root;
        ModelDir = modelDir ?? Environment.GetEnvironmentVariable("MEDIALENS_MODELS") ?? Path.Combine(root, "models");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ThumbDir);
        Directory.CreateDirectory(VectorDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(ModelDir);
    }

    public string ThumbPath(long mediaId) => Path.Combine(ThumbDir, (mediaId / 1000).ToString(), mediaId + ".jpg");

    /// <summary>The crop of a face or a person, shown in the people screens.</summary>
    public string PeopleCropPath(long detectionId) => Path.Combine(ThumbDir, "people", (detectionId / 1000).ToString(), detectionId + ".jpg");
}
