using Dapper;
using MediaLens.Data;
using MediaLens.Library;

namespace MediaLens.Buckets;

public sealed class Bucket
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public long CreatedAt { get; init; }
    public long Count { get; init; }
}

public sealed record ExportOptions(bool NumberByOrder = true, bool Overwrite = false);

public sealed record ExportResult(int Copied, int Skipped, List<string> Errors);

/// <summary>Named baskets of items (a photo book, a slideshow for a party) with their own order.</summary>
public sealed class BucketService
{
    private readonly Database _db;
    private readonly MediaRepository _repo;

    public BucketService(Database db, MediaRepository repo)
    {
        _db = db;
        _repo = repo;
    }

    public List<Bucket> List()
    {
        using var c = _db.Open();
        return c.Query<Bucket>("""
            SELECT b.id AS Id, b.name AS Name, b.created_at AS CreatedAt,
                   (SELECT COUNT(*) FROM bucket_item i JOIN media m ON m.id=i.media_id WHERE i.bucket_id=b.id AND m.missing=0) AS Count
            FROM bucket b ORDER BY b.created_at DESC
            """).ToList();
    }

    public Bucket Create(string name)
    {
        using var c = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = c.ExecuteScalar<long>("INSERT INTO bucket(name, created_at) VALUES(@name, @now) RETURNING id", new { name = name.Trim(), now });
        return new Bucket { Id = id, Name = name.Trim(), CreatedAt = now };
    }

    public Bucket? Find(string name)
    {
        using var c = _db.Open();
        var id = c.ExecuteScalar<long?>("SELECT id FROM bucket WHERE name=@name", new { name });
        return id is { } i ? List().FirstOrDefault(b => b.Id == i) : null;
    }

    public void Rename(long id, string name)
    {
        using var c = _db.Open();
        c.Execute("UPDATE bucket SET name=@name WHERE id=@id", new { id, name = name.Trim() });
    }

    public void Delete(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM bucket WHERE id=@id", new { id });
    }

    /// <summary>Appends at the end, in the given order; items already in the bucket keep their place.</summary>
    public int Add(long bucketId, IEnumerable<long> mediaIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var position = c.ExecuteScalar<double?>("SELECT MAX(position) FROM bucket_item WHERE bucket_id=@bucketId", new { bucketId }, tx) ?? 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var added = 0;
        foreach (var mediaId in mediaIds)
        {
            added += c.Execute("INSERT OR IGNORE INTO bucket_item(bucket_id, media_id, position, added_at) VALUES(@bucketId, @mediaId, @position, @now)",
                new { bucketId, mediaId, position = ++position, now }, tx);
        }
        tx.Commit();
        return added;
    }

    public void Remove(long bucketId, IEnumerable<long> mediaIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var mediaId in mediaIds)
            c.Execute("DELETE FROM bucket_item WHERE bucket_id=@bucketId AND media_id=@mediaId", new { bucketId, mediaId }, tx);
        tx.Commit();
    }

    public HashSet<long> MediaIds(long bucketId)
    {
        using var c = _db.Open();
        return c.Query<long>("SELECT media_id FROM bucket_item WHERE bucket_id=@bucketId", new { bucketId }).ToHashSet();
    }

    /// <summary>Saves a new order of the whole bucket (after moving items around).</summary>
    public void Reorder(long bucketId, IReadOnlyList<long> orderedMediaIds)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        for (var i = 0; i < orderedMediaIds.Count; i++)
            c.Execute("UPDATE bucket_item SET position=@p WHERE bucket_id=@bucketId AND media_id=@m",
                new { bucketId, m = orderedMediaIds[i], p = (double)(i + 1) }, tx);
        tx.Commit();
    }

    /// <summary>
    /// Copies the originals to a folder in bucket order. NumberByOrder prefixes 001_, 002_... so a photo book
    /// service or a file browser keeps the order. A name taken by another file gets " (2)".
    /// </summary>
    public async Task<ExportResult> ExportAsync(long bucketId, string targetDir, ExportOptions options,
        IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        var items = _repo.Query(new MediaFilter { BucketId = bucketId });
        var digits = Math.Max(3, items.Count.ToString().Length);
        int copied = 0, skipped = 0;
        var errors = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var name = options.NumberByOrder ? $"{(i + 1).ToString().PadLeft(digits, '0')}_{item.FileName}" : item.FileName;
            var target = UniqueTarget(targetDir, name, item.Size, used, options.Overwrite);
            try
            {
                if (!options.Overwrite && File.Exists(target))
                {
                    skipped++;
                }
                else
                {
                    await CopyAsync(await Cloud.MediaFiles.LocalPathAsync(item, ct), target, ct);
                    File.SetLastWriteTimeUtc(target, DateTimeOffset.FromUnixTimeMilliseconds(item.Mtime).UtcDateTime);
                    copied++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Cloud.RcloneException)
            {
                errors.Add($"{item.FullPath}: {e.Message}");
            }
            progress?.Report((i + 1, items.Count));
        }
        return new ExportResult(copied, skipped, errors);
    }

    /// <summary>
    /// A name not used by this export. Without Overwrite, an existing file of the same size is taken as
    /// this item from an earlier export (skipped); one of another size is someone else's file and is kept.
    /// </summary>
    private static string UniqueTarget(string dir, string name, long size, HashSet<string> used, bool overwrite)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var n = 2; used.Contains(candidate) || (!overwrite && IsOtherFile(Path.Combine(dir, candidate), size)); n++)
            candidate = $"{stem} ({n}){ext}";
        used.Add(candidate);
        return Path.Combine(dir, candidate);
    }

    private static bool IsOtherFile(string path, long size) => File.Exists(path) && new FileInfo(path).Length != size;

    private static async Task CopyAsync(string source, string target, CancellationToken ct)
    {
        var tmp = target + ".part";
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true))
        await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            await input.CopyToAsync(output, ct);
        File.Move(tmp, target, true);
    }
}
