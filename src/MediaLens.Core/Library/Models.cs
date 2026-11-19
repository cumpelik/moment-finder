namespace MediaLens.Library;

public enum MediaKind
{
    Photo = 1,
    Video = 2,
}

public static class SourceKinds
{
    public const string Local = "local";
}

public sealed class Source
{
    public long Id { get; init; }
    public string Kind { get; init; } = SourceKinds.Local;
    public string Root { get; init; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long AddedAt { get; init; }
    public long? LastScanAt { get; set; }
}

/// <summary>One photo or video. TakenAt is the wall clock of the camera (EXIF has no time zone), stored as if it were UTC.</summary>
public sealed class MediaItem
{
    public long Id { get; init; }
    public long SourceId { get; init; }
    public string RelPath { get; init; } = "";
    public MediaKind Kind { get; init; }
    public long Size { get; init; }
    public long Mtime { get; init; }
    public string? QuickHash { get; init; }
    public long TakenAt { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? DurationMs { get; init; }
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public string? Camera { get; init; }
    public int ThumbState { get; init; }
    public int AnalysisVersion { get; init; }

    /// <summary>Filled by the repository from the source root.</summary>
    public string FullPath { get; set; } = "";

    public string FileName => Path.GetFileName(RelPath);
    public DateTime Taken => DateTime.UnixEpoch.AddMilliseconds(TakenAt);
    public bool IsVideo => Kind == MediaKind.Video;
}

public static class ThumbStates
{
    public const int Pending = 0;
    public const int Ready = 1;
    public const int Failed = 2;
}

public static class FileTypes
{
    public static readonly HashSet<string> Photo = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".avif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf",
    };

    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".mts", ".m2ts", ".3gp", ".webm", ".wmv", ".mpg", ".mpeg",
    };

    public static MediaKind? KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (Photo.Contains(ext)) return MediaKind.Photo;
        if (Video.Contains(ext)) return MediaKind.Video;
        return null;
    }
}
