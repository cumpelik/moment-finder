using System.Text;
using Dapper;
using MediaLens.Data;

namespace MediaLens.Library;

public sealed record MediaFilter
{
    public long? SourceId { get; init; }
    public MediaKind? Kind { get; init; }
    /// <summary>Inclusive, TakenAt milliseconds.</summary>
    public long? From { get; init; }
    /// <summary>Exclusive, TakenAt milliseconds.</summary>
    public long? To { get; init; }
    public long? BucketId { get; init; }
    /// <summary>All of these people are on the item.</summary>
    public IReadOnlyList<long> PersonIds { get; init; } = [];
}

/// <summary>What the scanner found on disk for one file.</summary>
public sealed record ScannedFile(
    string RelPath, MediaKind Kind, long Size, long Mtime, string? QuickHash, long TakenAt,
    int? Width, int? Height, long? DurationMs, double? Lat, double? Lon, string? Camera);

public sealed record KnownFile(long Id, string RelPath, long Size, long Mtime, long Missing)
{
    public bool IsMissing => Missing != 0;
}

public sealed class MediaRepository
{
    private readonly Database _db;

    static MediaRepository() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public MediaRepository(Database db) => _db = db;

    private const string MediaColumns =
        "m.id, m.source_id, m.rel_path, m.kind, m.size, m.mtime, m.quick_hash, m.taken_at, m.width, m.height, " +
        "m.duration_ms, m.lat, m.lon, m.camera, m.thumb_state, m.analysis_version";

    // ---- sources ----

    public Source AddSource(string kind, string root, string name)
    {
        using var c = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = c.ExecuteScalar<long>(
            "INSERT INTO source(kind,root,name,added_at) VALUES(@kind,@root,@name,@now) RETURNING id",
            new { kind, root, name, now });
        return new Source { Id = id, Kind = kind, Root = root, Name = name, AddedAt = now };
    }

    public List<Source> ListSources()
    {
        using var c = _db.Open();
        return c.Query<Source>("SELECT * FROM source ORDER BY name COLLATE NOCASE").ToList();
    }

    public Dictionary<long, long> CountBySource()
    {
        using var c = _db.Open();
        return c.Query<(long SourceId, long Count)>("SELECT source_id, COUNT(*) FROM media WHERE missing=0 GROUP BY source_id")
            .ToDictionary(r => r.SourceId, r => r.Count);
    }

    public void RemoveSource(long id)
    {
        using var c = _db.Open();
        c.Execute("DELETE FROM source WHERE id=@id", new { id });
    }

    public void SetLastScan(long sourceId, long at)
    {
        using var c = _db.Open();
        c.Execute("UPDATE source SET last_scan_at=@at WHERE id=@sourceId", new { sourceId, at });
    }

    // ---- scanning ----

    public Dictionary<string, KnownFile> KnownFiles(long sourceId)
    {
        using var c = _db.Open();
        return c.Query<KnownFile>("SELECT id, rel_path, size, mtime, missing FROM media WHERE source_id=@sourceId",
                new { sourceId })
            .ToDictionary(f => f.RelPath, StringComparer.Ordinal);
    }

    /// <summary>Inserts new files and updates changed ones; a changed file gets a new thumbnail and a new analysis.</summary>
    public void Upsert(long sourceId, IReadOnlyCollection<ScannedFile> files)
    {
        if (files.Count == 0) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var f in files)
        {
            c.Execute("""
                INSERT INTO media(source_id, rel_path, kind, size, mtime, quick_hash, taken_at, width, height,
                                  duration_ms, lat, lon, camera)
                VALUES(@sourceId, @RelPath, @Kind, @Size, @Mtime, @QuickHash, @TakenAt, @Width, @Height,
                       @DurationMs, @Lat, @Lon, @Camera)
                ON CONFLICT(source_id, rel_path) DO UPDATE SET
                    kind=excluded.kind, size=excluded.size, mtime=excluded.mtime, quick_hash=excluded.quick_hash,
                    taken_at=excluded.taken_at, width=excluded.width, height=excluded.height,
                    duration_ms=excluded.duration_ms, lat=excluded.lat, lon=excluded.lon, camera=excluded.camera,
                    thumb_state=0, analysis_version=0, missing=0
                """,
                new
                {
                    sourceId, f.RelPath, Kind = (int)f.Kind, f.Size, f.Mtime, f.QuickHash, f.TakenAt, f.Width, f.Height,
                    f.DurationMs, f.Lat, f.Lon, f.Camera,
                }, tx);
        }
        tx.Commit();
    }

    public void SetMissing(IEnumerable<long> ids, bool missing)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
            c.Execute("UPDATE media SET missing=@missing WHERE id=@id", new { id, missing }, tx);
        tx.Commit();
    }

    // ---- queries ----

    public List<MediaItem> Query(MediaFilter filter, int limit = int.MaxValue)
    {
        var sql = new StringBuilder($"SELECT {MediaColumns}, s.root AS SourceRoot FROM media m JOIN source s ON s.id=m.source_id");
        var p = new DynamicParameters();
        if (filter.BucketId is { } bucketId)
        {
            sql.Append(" JOIN bucket_item b ON b.media_id=m.id AND b.bucket_id=@bucketId");
            p.Add("bucketId", bucketId);
        }
        sql.Append(" WHERE m.missing=0 AND s.enabled=1");
        if (filter.SourceId is { } sourceId) { sql.Append(" AND m.source_id=@sourceId"); p.Add("sourceId", sourceId); }
        if (filter.Kind is { } kind) { sql.Append(" AND m.kind=@kind"); p.Add("kind", (int)kind); }
        if (filter.From is { } from) { sql.Append(" AND m.taken_at>=@from"); p.Add("from", from); }
        if (filter.To is { } to) { sql.Append(" AND m.taken_at<@to"); p.Add("to", to); }
        for (var i = 0; i < filter.PersonIds.Count; i++)
        {
            sql.Append($" AND m.id IN (SELECT media_id FROM detection WHERE person_id=@person{i})");
            p.Add("person" + i, filter.PersonIds[i]);
        }
        sql.Append(filter.BucketId is null ? " ORDER BY m.taken_at DESC, m.id DESC" : " ORDER BY b.position, b.added_at");
        if (limit != int.MaxValue) { sql.Append(" LIMIT @limit"); p.Add("limit", limit); }

        using var c = _db.Open();
        return c.Query<MediaRow>(sql.ToString(), p).Select(r => r.ToItem()).ToList();
    }

    public Dictionary<long, MediaItem> Get(IEnumerable<long> ids)
    {
        using var c = _db.Open();
        var result = new Dictionary<long, MediaItem>();
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            foreach (var r in c.Query<MediaRow>(
                         $"SELECT {MediaColumns}, s.root AS SourceRoot FROM media m JOIN source s ON s.id=m.source_id WHERE m.id IN @chunk AND m.missing=0",
                         new { chunk }))
                result[r.Id] = r.ToItem();
        }
        return result;
    }

    public MediaItem? Get(long id) => Get([id]).GetValueOrDefault(id);

    public List<MediaItem> PendingThumbs(int limit)
    {
        using var c = _db.Open();
        return c.Query<MediaRow>(
                $"SELECT {MediaColumns}, s.root AS SourceRoot FROM media m JOIN source s ON s.id=m.source_id " +
                "WHERE m.thumb_state=0 AND m.missing=0 AND s.enabled=1 ORDER BY m.taken_at DESC LIMIT @limit", new { limit })
            .Select(r => r.ToItem()).ToList();
    }

    public void SetThumbState(long id, int state)
    {
        using var c = _db.Open();
        c.Execute("UPDATE media SET thumb_state=@state WHERE id=@id", new { id, state });
    }

    /// <summary>Items with a thumbnail whose analysis is older than the current pipeline.</summary>
    public List<MediaItem> PendingAnalysis(int version, int limit)
    {
        using var c = _db.Open();
        return c.Query<MediaRow>(
                $"SELECT {MediaColumns}, s.root AS SourceRoot FROM media m JOIN source s ON s.id=m.source_id " +
                "WHERE m.analysis_version<@version AND m.thumb_state=1 AND m.missing=0 AND s.enabled=1 " +
                "ORDER BY m.taken_at DESC LIMIT @limit", new { version, limit })
            .Select(r => r.ToItem()).ToList();
    }

    public List<MediaItem> PendingPeople(int version, int limit)
    {
        using var c = _db.Open();
        return c.Query<MediaRow>(
                $"SELECT {MediaColumns}, s.root AS SourceRoot FROM media m JOIN source s ON s.id=m.source_id " +
                "WHERE m.people_version<@version AND m.thumb_state=1 AND m.missing=0 AND s.enabled=1 " +
                "ORDER BY m.taken_at DESC LIMIT @limit", new { version, limit })
            .Select(r => r.ToItem()).ToList();
    }

    public void SetPeopleVersion(long id, int version)
    {
        using var c = _db.Open();
        c.Execute("UPDATE media SET people_version=@version WHERE id=@id", new { id, version });
    }

    public void SetAnalysisVersion(long id, int version)
    {
        using var c = _db.Open();
        c.Execute("UPDATE media SET analysis_version=@version WHERE id=@id", new { id, version });
    }

    public LibraryStats Stats(int analysisVersion)
    {
        using var c = _db.Open();
        return c.QuerySingle<LibraryStats>("""
            SELECT COUNT(*) AS Total,
                   COALESCE(SUM(m.kind=1),0) AS Photos,
                   COALESCE(SUM(m.kind=2),0) AS Videos,
                   COALESCE(SUM(m.thumb_state<>0),0) AS Thumbnailed,
                   COALESCE(SUM(m.analysis_version>=@analysisVersion),0) AS Analyzed,
                   COALESCE(SUM(m.people_version>=@peopleVersion),0) AS PeopleAnalyzed
            FROM media m JOIN source s ON s.id=m.source_id WHERE m.missing=0 AND s.enabled=1
            """, new { analysisVersion, peopleVersion = Indexer.PeopleVersion });
    }

    private sealed class MediaRow
    {
        public long Id { get; init; }
        public long SourceId { get; init; }
        public string RelPath { get; init; } = "";
        public int Kind { get; init; }
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
        public string SourceRoot { get; init; } = "";

        public MediaItem ToItem() => new()
        {
            Id = Id, SourceId = SourceId, RelPath = RelPath, Kind = (MediaKind)Kind, Size = Size, Mtime = Mtime,
            QuickHash = QuickHash, TakenAt = TakenAt, Width = Width, Height = Height, DurationMs = DurationMs,
            Lat = Lat, Lon = Lon, Camera = Camera, ThumbState = ThumbState, AnalysisVersion = AnalysisVersion,
            FullPath = Path.Combine(SourceRoot, RelPath.Replace('/', Path.DirectorySeparatorChar)),
        };
    }
}

public sealed class LibraryStats
{
    public long Total { get; init; }
    public long Photos { get; init; }
    public long Videos { get; init; }
    public long Thumbnailed { get; init; }
    public long Analyzed { get; init; }
    public long PeopleAnalyzed { get; init; }
}
