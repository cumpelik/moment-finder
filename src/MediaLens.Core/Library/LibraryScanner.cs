using System.Collections.Concurrent;
using MediaLens.Media;

namespace MediaLens.Library;

/// <summary>Moved: files found again under another path or in another source; they keep everything known about them.</summary>
public sealed record ScanResult(int Added, int Changed, int Missing, int Total, int Moved = 0);

/// <summary>Walks a source and brings the index up to date. Only new and changed files are read.</summary>
public sealed class LibraryScanner
{
    private readonly MediaRepository _repo;
    private readonly Cloud.Rclone? _rclone;

    public LibraryScanner(MediaRepository repo, Cloud.Rclone? rclone = null)
    {
        _repo = repo;
        _rclone = rclone;
    }

    public async Task<ScanResult> ScanAsync(Source source, IProgress<string>? progress, CancellationToken ct)
    {
        if (source.Kind == SourceKinds.Cloud)
            return await ScanCloudAsync(source, progress, ct);
        if (source.Kind != SourceKinds.Local)
            throw new NotSupportedException($"Source kind {source.Kind} is not supported"); 
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
        var newPaths = todo.Where(t => t.IsNew).Select(t => t.Rel).ToHashSet(StringComparer.Ordinal);
        var movedIds = new HashSet<long>();
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

        var missing = known.Values.Where(k => !k.IsMissing && !seen.Contains(k.RelPath) && !movedIds.Contains(k.Id)).Select(k => k.Id).ToList();
        _repo.SetMissing(missing, true);
        _repo.SetLastScan(source.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var added = todo.Count(t => t.IsNew) - movedIds.Count;
        return new ScanResult(added, todo.Count(t => !t.IsNew), missing.Count, seen.Count, movedIds.Count);

        void Flush()
        {
            var batch = new List<ScannedFile>();
            while (scanned.TryTake(out var f)) batch.Add(f);
            lock (_repo)
            {
                // A file under a path not seen before may be a known one that was moved: the same content, and the
                // old place is gone. Its row moves along, with the thumbnail, the people, the buckets, everything.
                var fresh = new List<ScannedFile>();
                foreach (var f in batch)
                {
                    if (newPaths.Contains(f.RelPath) && MovedFrom(source, f, movedIds, seen) is { } old)
                    {
                        _repo.Move(old, source.Id, f.RelPath, f.Mtime);
                        movedIds.Add(old);
                    }
                    else fresh.Add(f);
                }
                _repo.Upsert(source.Id, fresh);
            }
        }
    }

    /// <summary>
    /// The row of the same file at its earlier place, when that place is empty now: marked missing, or (another
    /// source not scanned yet) no file there any more, or in this source a path this scan did not meet (also a
    /// rename that only changes the case of letters). A file still at its old place makes this one a copy.
    /// </summary>
    private long? MovedFrom(Source source, ScannedFile file, HashSet<long> taken, HashSet<string>? seen)
    {
        if (file.QuickHash == null) return null;
        var same = _repo.SameContent(file.QuickHash, file.Size).Where(c => !taken.Contains(c.Id)).ToList();
        var gone = same.FirstOrDefault(c => c.Missing)
                   ?? same.FirstOrDefault(c => c.SourceId == source.Id && seen != null && !seen.Contains(c.RelPath))
                   ?? same.FirstOrDefault(c => c.SourceId != source.Id && c.Kind == SourceKinds.Local && Directory.Exists(c.Root) &&
                                               !File.Exists(Path.Combine(c.Root, c.RelPath.Replace('/', Path.DirectorySeparatorChar))));
        return gone?.Id;
    }

    /// <summary>
    /// A cloud source: the listing says what changed; new and changed files are fetched into the cache in
    /// batches (rclone transfers several at once) and read like local files. The cache is trimmed later.
    /// </summary>
    private async Task<ScanResult> ScanCloudAsync(Source source, IProgress<string>? progress, CancellationToken ct)
    {
        if (_rclone == null || source.Remote == null) throw new InvalidOperationException("Cloud access is not configured");
        progress?.Report($"Listing {source.Name}");
        var remote = await _rclone.ListAsync(source.Remote, ct);
        // Whose the folder is: the backup of another computer's folder is only for looking here.
        if (await Cloud.FolderMarker.ReadAsync(_rclone, source.Remote, ct) is { } marker)
            _repo.SetCloudRole(source.Id, marker.IsBackup, marker.IsBackup ? marker.HomeName : null);
        var known = _repo.KnownFiles(source.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new List<Cloud.RemoteFile>();
        foreach (var f in remote)
        {
            if (FileTypes.KindOf(f.Path) == null || IsIgnored(f.Path)) continue;
            seen.Add(f.Path);
            var mtime = f.ModTime.ToUnixTimeMilliseconds();
            if (known.TryGetValue(f.Path, out var k) && k.Size == f.Size && !k.IsMissing)
            {
                // The same place and size with another time (uploaded again, another computer's copy of the
                // library): not worth fetching and analyzing again.
                if (k.Mtime != mtime) _repo.SetMtime(k.Id, mtime);
                continue;
            }
            todo.Add(f);
        }
        var movedIds = new HashSet<long>();

        var done = 0;
        foreach (var chunk in todo.Chunk(100))
        {
            var list = Path.Combine(source.Root, ".files-" + Guid.NewGuid().ToString("N") + ".txt");
            Directory.CreateDirectory(source.Root);
            await File.WriteAllLinesAsync(list, chunk.Select(f => f.Path), ct);
            try
            {
                await _rclone.RunAsync(["copy", source.Remote, source.Root, "--files-from-raw", list, "--transfers", "6"], ct);
            }
            finally
            {
                File.Delete(list);
            }
            var scanned = new List<ScannedFile>();
            foreach (var f in chunk)
            {
                var local = Path.Combine(source.Root, f.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(local)) continue;
                var info = new FileInfo(local);
                var probed = await MediaProbe.ProbeAsync(local, f.Path, FileTypes.KindOf(f.Path)!.Value, info, ct);
                // The cloud's size and time decide what changed next time, not the cached copy's.
                scanned.Add(probed with { Size = f.Size, Mtime = f.ModTime.ToUnixTimeMilliseconds() });
            }
            // Moved inside the cloud or from a local folder that is gone: the known row moves along.
            var fresh = new List<ScannedFile>();
            foreach (var f in scanned)
            {
                if (!known.ContainsKey(f.RelPath) && MovedFrom(source, f, movedIds, null) is { } old)
                {
                    _repo.Move(old, source.Id, f.RelPath, f.Mtime);
                    movedIds.Add(old);
                }
                else fresh.Add(f);
            }
            _repo.Upsert(source.Id, fresh);
            done += chunk.Length;
            progress?.Report($"Reading {source.Name}: {done}/{todo.Count}");
        }

        var missing = known.Values.Where(k => !k.IsMissing && !seen.Contains(k.RelPath) && !movedIds.Contains(k.Id)).Select(k => k.Id).ToList();
        _repo.SetMissing(missing, true);
        _repo.SetLastScan(source.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var added = todo.Count(t => !known.ContainsKey(t.Path));
        return new ScanResult(added - movedIds.Count, todo.Count - added, missing.Count, seen.Count, movedIds.Count);
    }

    /// <summary>Folders of other applications (thumbnail caches, editors' previews) are not part of the library.</summary>
    public static bool IsIgnored(string rel)
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
