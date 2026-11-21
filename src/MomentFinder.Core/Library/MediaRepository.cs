using System.Text;
using Dapper;
using MomentFinder.Data;

namespace MomentFinder.Library;

public sealed record MediaFilter
{
    public long? SourceId { get; init; }
    public MediaKind? Kind { get; init; }
    /// <summary>Inclusive, TakenAt milliseconds.</summary>
    public long? From { get; init; }
    /// <summary>Exclusive, TakenAt milliseconds.</summary>
    public long? To { get; init; }
    /// <summary>Any of these spans (from inclusive, to exclusive, TakenAt milliseconds): several months or years.</summary>
    public IReadOnlyList<(long From, long To)> Spans { get; init; } = [];
    public long? BucketId { get; init; }
    /// <summary>All of these people are on the item.</summary>
    public IReadOnlyList<long> PersonIds { get; init; } = [];
    /// <summary>Only these items.</summary>
    public IReadOnlyCollection<long>? Ids { get; init; }
    /// <summary>Videos with moments worth watching (InterestingMoments), the best first, each from its best moment.</summary>
    public bool BestMoments { get; init; }
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

    public MediaRepository(Database db)
    {
        _db = db;
        ReadOnly = new ReadOnlyLock(db);
    }

    /// <summary>Which sources are read-only now.</summary>
    public ReadOnlyLock ReadOnly { get; }

    public Database Db => _db;

    private const string MediaColumns =
        "m.id, m.source_id, m.rel_path, m.kind, m.size, m.mtime, m.quick_hash, m.taken_at, m.width, m.height, " +
        "m.duration_ms, m.lat, m.lon, m.camera, m.thumb_state, m.analysis_version";

    // ---- sources ----

    public Source AddSource(string kind, string root, string name, string? remote = null)
    {
        using var c = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = c.ExecuteScalar<long>(
            "INSERT INTO source(kind,root,name,added_at,remote) VALUES(@kind,@root,@name,@now,@remote) RETURNING id",
            new { kind, root, name, now, remote });
        return new Source { Id = id, Kind = kind, Root = root, Name = name, AddedAt = now, Remote = remote };
    }

    public void SetSourceRoot(long id, string root)
    {
        using var c = _db.Open();
        c.Execute("UPDATE source SET root=@root WHERE id=@id", new { id, root });
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
        ReadOnly.ThrowIfLocked(id);
        using var c = _db.Open();
        c.Execute("DELETE FROM source WHERE id=@id", new { id });
    }

    /// <summary>What the folder's marker in the cloud says: the backup of another computer's folder is only for looking.</summary>
    public void SetCloudRole(long sourceId, bool readOnly, string? homeName)
    {
        using var c = _db.Open();
        c.Execute("UPDATE source SET read_only=@readOnly, home_name=@homeName WHERE id=@sourceId", new { sourceId, readOnly, homeName });
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

    /// <summary>A row with the same content as a scanned file, and where it is (was).</summary>
    public sealed class SameFile
    {
        public long Id { get; init; }
        public long SourceId { get; init; }
        public string RelPath { get; init; } = "";
        public bool Missing { get; init; }
        public string Kind { get; init; } = "";
        public string Root { get; init; } = "";
    }

    public List<SameFile> SameContent(string quickHash, long size)
    {
        using var c = _db.Open();
        return c.Query<SameFile>("""
            SELECT m.id, m.source_id, m.rel_path, m.missing, s.kind, s.root
            FROM media m JOIN source s ON s.id=m.source_id
            WHERE m.quick_hash=@quickHash AND m.size=@size ORDER BY m.id
            """, new { quickHash, size }).ToList();
    }

    /// <summary>The file is at another place now: the row follows, with all that hangs on its id.</summary>
    public void Move(long id, long sourceId, string relPath, long mtime)
    {
        using var c = _db.Open();
        c.Execute("UPDATE media SET source_id=@sourceId, rel_path=@relPath, mtime=@mtime, missing=0 WHERE id=@id",
            new { id, sourceId, relPath, mtime });
    }

    public void SetMtime(long id, long mtime)
    {
        using var c = _db.Open();
        c.Execute("UPDATE media SET mtime=@mtime WHERE id=@id", new { id, mtime });
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

    /// <summary>The same file (quick hash and size) in a folder on this computer, if it is there now.</summary>
    public string? LocalTwinPath(MediaItem item)
    {
        if (item.QuickHash == null) return null;
        using var c = _db.Open();
        var twins = c.Query<(string Root, string RelPath)>("""
            SELECT s.root, m.rel_path FROM media m JOIN source s ON s.id=m.source_id
            WHERE m.quick_hash=@QuickHash AND m.size=@Size AND m.missing=0 AND s.kind='local' AND s.enabled=1
            """, new { item.QuickHash, item.Size });
        return twins.Select(t => Path.Combine(t.Root, t.RelPath.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(path => new FileInfo(path) is { Exists: true } f && f.Length == item.Size);
    }

    public List<MediaItem> Query(MediaFilter filter, int limit = int.MaxValue)
    {
        var sql = new StringBuilder($"SELECT {MediaColumns}, s.root AS SourceRoot, s.remote AS SourceRemote FROM media m JOIN source s ON s.id=m.source_id");
        var p = new DynamicParameters();
        if (filter.BucketId is { } bucketId)
        {
            sql.Append(" JOIN bucket_item b ON b.media_id=m.id AND b.bucket_id=@bucketId");
            p.Add("bucketId", bucketId);
        }
        sql.Append(" WHERE m.missing=0 AND s.enabled=1");
        if (filter.SourceId is { } sourceId) { sql.Append(" AND m.source_id=@sourceId"); p.Add("sourceId", sourceId); }
        if (filter.Ids is { } ids) { sql.Append(" AND m.id IN @ids"); p.Add("ids", ids); }
        if (filter.Kind is { } kind) { sql.Append(" AND m.kind=@kind"); p.Add("kind", (int)kind); }
        if (filter.From is { } from) { sql.Append(" AND m.taken_at>=@from"); p.Add("from", from); }
        if (filter.To is { } to) { sql.Append(" AND m.taken_at<@to"); p.Add("to", to); }
        if (filter.Spans.Count > 0)
        {
            sql.Append(" AND (");
            for (var i = 0; i < filter.Spans.Count; i++)
            {
                sql.Append(i == 0 ? "" : " OR ").Append($"(m.taken_at>=@spanFrom{i} AND m.taken_at<@spanTo{i})");
                p.Add("spanFrom" + i, filter.Spans[i].From);
                p.Add("spanTo" + i, filter.Spans[i].To);
            }
            sql.Append(')');
        }
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
                         $"SELECT {MediaColumns}, s.root AS SourceRoot, s.remote AS SourceRemote FROM media m JOIN source s ON s.id=m.source_id WHERE m.id IN @chunk AND m.missing=0",
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
                $"SELECT {MediaColumns}, s.root AS SourceRoot, s.remote AS SourceRemote FROM media m JOIN source s ON s.id=m.source_id " +
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
                $"SELECT {MediaColumns}, s.root AS SourceRoot, s.remote AS SourceRemote FROM media m JOIN source s ON s.id=m.source_id " +
                "WHERE m.analysis_version<@version AND m.thumb_state=1 AND m.missing=0 AND s.enabled=1 " +
                "ORDER BY m.taken_at DESC LIMIT @limit", new { version, limit })
            .Select(r => r.ToItem()).ToList();
    }

    public List<MediaItem> PendingPeople(int version, int limit)
    {
        using var c = _db.Open();
        return c.Query<MediaRow>(
                $"SELECT {MediaColumns}, s.root AS SourceRoot, s.remote AS SourceRemote FROM media m JOIN source s ON s.id=m.source_id " +
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

    /// <summary>How many items each year has (newest first), for the date filter; month 0 counts whole years.</summary>
    public List<(int Year, int Month, long Count)> DateCounts(int? year = null)
    {
        using var c = _db.Open();
        var part = year == null ? "0" : "CAST(strftime('%m', m.taken_at / 1000, 'unixepoch') AS INTEGER)";
        return c.Query<(long Year, long Month, long Count)>($"""
            SELECT CAST(strftime('%Y', m.taken_at / 1000, 'unixepoch') AS INTEGER) AS y, {part} AS mo, COUNT(*)
            FROM media m JOIN source s ON s.id=m.source_id
            WHERE m.missing=0 AND s.enabled=1 {(year == null ? "" : "AND strftime('%Y', m.taken_at / 1000, 'unixepoch')=@y")}
            GROUP BY y, mo ORDER BY y DESC, mo
            """, new { y = year?.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) })
            .Select(r => ((int)r.Year, (int)r.Month, r.Count)).ToList();
    }

    /// <summary>The GPS positions of the items of these events (days out), for naming where they were.</summary>
    public Dictionary<long, List<(double Lat, double Lon)>> EventPositions(IEnumerable<long> eventIds)
    {
        var ids = eventIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        using var c = _db.Open();
        return c.Query<(long EventId, double Lat, double Lon)>(
                "SELECT event_id, lat, lon FROM media WHERE event_id IN @ids AND lat IS NOT NULL AND lon IS NOT NULL AND missing=0", new { ids })
            .GroupBy(r => r.EventId).ToDictionary(g => g.Key, g => g.Select(r => (r.Lat, r.Lon)).ToList());
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
        public string? SourceRemote { get; init; }

        public MediaItem ToItem() => new()
        {
            Id = Id, SourceId = SourceId, RelPath = RelPath, Kind = (MediaKind)Kind, Size = Size, Mtime = Mtime,
            QuickHash = QuickHash, TakenAt = TakenAt, Width = Width, Height = Height, DurationMs = DurationMs,
            Lat = Lat, Lon = Lon, Camera = Camera, ThumbState = ThumbState, AnalysisVersion = AnalysisVersion,
            FullPath = Path.Combine(SourceRoot, RelPath.Replace('/', Path.DirectorySeparatorChar)),
            Remote = SourceRemote == null ? null : Cloud.CloudService.Join(SourceRemote, RelPath),
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
