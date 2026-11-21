using MomentFinder.Library;

namespace MomentFinder.Cloud;

/// <summary>What a backup run does: files to upload, files to move inside the cloud, files gone from the folder.</summary>
public sealed record SyncPlan(List<string> Upload, List<(string From, string To)> Moves, List<RemoteFile> Gone);

/// <summary>How a backup run ended, for the status and the command line.</summary>
public sealed record SyncResult(int Uploaded, int Moved, int Gone);

/// <summary>
/// The backup of a folder compares the folder with its copy in the cloud. A file at a new place with the same size
/// and time as one that left its old place was moved or renamed: it is moved in the cloud too, not uploaded again.
/// What is only in the cloud was deleted here: it waits for the user's word, nothing is deleted by itself.
/// </summary>
public static class FolderSync
{
    /// <summary>rclone keeps the time of a file; some clouds only to the second.</summary>
    private static readonly TimeSpan SameTime = TimeSpan.FromSeconds(1);

    /// <summary>The same file as far as a listing tells: size, and time to the second.</summary>
    public static bool Same(RemoteFile a, RemoteFile b) => a.Size == b.Size && (a.ModTime - b.ModTime).Duration() <= SameTime;

    /// <summary>The files of a local folder as the backup sees them (the same ones the library skips are skipped).</summary>
    public static Dictionary<string, RemoteFile> ListLocal(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = new Dictionary<string, RemoteFile>(StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(root, "*", options))
        {
            var rel = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (LibraryScanner.IsIgnored(rel)) continue;
            var info = new FileInfo(full);
            files[rel] = new RemoteFile(rel, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
        }
        return files;
    }

    public static SyncPlan Plan(IReadOnlyDictionary<string, RemoteFile> local, IEnumerable<RemoteFile> cloud)
    {
        var inCloud = cloud.Where(f => !LibraryScanner.IsIgnored(f.Path)).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var upload = new List<string>();
        var onlyLocal = new List<RemoteFile>();
        foreach (var f in local.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!inCloud.TryGetValue(f.Path, out var there)) onlyLocal.Add(f);
            else if (there.Size != f.Size || (there.ModTime - f.ModTime).Duration() > SameTime) upload.Add(f.Path);
        }
        var onlyCloud = inCloud.Values.Where(f => !local.ContainsKey(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        // Moved: the same size and time at one new place and one old place. With more candidates the same file name
        // decides; still not clear, it is uploaded (a copy too many is better than a wrong file).
        var moves = new List<(string, string)>();
        var left = onlyCloud.ToHashSet();
        var bySize = onlyCloud.ToLookup(f => f.Size);
        foreach (var f in onlyLocal)
        {
            var same = bySize[f.Size].Where(c => left.Contains(c) && (c.ModTime - f.ModTime).Duration() <= SameTime).ToList();
            if (same.Count > 1)
                same = same.Where(c => string.Equals(Path.GetFileName(c.Path), Path.GetFileName(f.Path), StringComparison.Ordinal)).ToList();
            var twins = onlyLocal.Count(o => o.Size == f.Size && (o.ModTime - f.ModTime).Duration() <= SameTime);
            if (same.Count == 1 && (twins == 1 || Path.GetFileName(same[0].Path) == Path.GetFileName(f.Path)))
            {
                moves.Add((same[0].Path, f.Path));
                left.Remove(same[0]);
            }
            else upload.Add(f.Path);
        }
        return new SyncPlan(upload, moves, onlyCloud.Where(left.Contains).ToList());
    }
}
