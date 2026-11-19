using System.Collections.Concurrent;
using MediaLens.Media;

namespace MediaLens.Library;

public sealed record ScanResult(int Added, int Changed, int Missing, int Total);

/// <summary>Walks a source and brings the index up to date. Only new and changed files are read.</summary>
public sealed class LibraryScanner
{
    private readonly MediaRepository _repo;

    public LibraryScanner(MediaRepository repo) => _repo = repo;

    public async Task<ScanResult> ScanAsync(Source source, IProgress<string>? progress, CancellationToken ct)
    {
        if (source.Kind != SourceKinds.Local)
            throw new NotSupportedException($"Source kind {source.Kind} is not supported yet");
        if (!Directory.Exists(source.Root))
            throw new DirectoryNotFoundException(source.Root);

        var known = _repo.KnownFiles(source.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new List<(string Full, string Rel, MediaKind Kind, FileInfo Info, bool IsNew)>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var full in Directory.EnumerateFiles(source.Root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (FileTypes.KindOf(full) is not { } kind) continue;
            var rel = Path.GetRelativePath(source.Root, full).Replace(Path.DirectorySeparatorChar, '/');
            if (IsIgnored(rel)) continue;
            seen.Add(rel);
            var info = new FileInfo(full);
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            if (known.TryGetValue(rel, out var k) && k.Size == info.Length && k.Mtime == mtime && !k.IsMissing)
                continue;
            todo.Add((full, rel, kind, info, !known.ContainsKey(rel)));
        }

        // Files that came back unchanged (a reconnected disk) only lose the missing flag.
        var returned = todo.Where(t => known.TryGetValue(t.Rel, out var k) && k.IsMissing && k.Size == t.Info.Length &&
                                       k.Mtime == new DateTimeOffset(t.Info.LastWriteTimeUtc).ToUnixTimeMilliseconds())
            .ToList();
        _repo.SetMissing(returned.Select(t => known[t.Rel].Id), false);
        todo = todo.Except(returned).ToList();

        var scanned = new ConcurrentBag<ScannedFile>();
        var done = 0;
        await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (t, token) =>
            {
                try
                {
                    scanned.Add(await MediaProbe.ProbeAsync(t.Full, t.Rel, t.Kind, t.Info, token));
                }
                catch (IOException)
                {
                    // Locked or vanished meanwhile; the next scan tries again.
                }
                catch (UnauthorizedAccessException)
                {
                }
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0) progress?.Report($"Reading {source.Name}: {n}/{todo.Count}");
                // Save in batches, so a large first scan shows up in the grid while it runs.
                if (scanned.Count >= 200)
                    Flush();
            });
        Flush();

        var missing = known.Values.Where(k => !k.IsMissing && !seen.Contains(k.RelPath)).Select(k => k.Id).ToList();
        _repo.SetMissing(missing, true);
        _repo.SetLastScan(source.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var added = todo.Count(t => t.IsNew);
        return new ScanResult(added, todo.Count - added, missing.Count, seen.Count);

        void Flush()
        {
            var batch = new List<ScannedFile>();
            while (scanned.TryTake(out var f)) batch.Add(f);
            lock (_repo) _repo.Upsert(source.Id, batch);
        }
    }

    /// <summary>Folders of other applications (thumbnail caches, editors' previews) are not part of the library.</summary>
    private static bool IsIgnored(string rel)
    {
        foreach (var part in rel.Split('/'))
        {
            if (part.StartsWith('.') || part.StartsWith("@ea", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("#recycle", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
