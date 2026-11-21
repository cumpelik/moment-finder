using Dapper;
using MomentFinder.Data;
using MomentFinder.Vectors;

namespace MomentFinder.Library;

/// <summary>
/// A file that is already in the library elsewhere (the same content: quick hash and size) gets what is known about
/// its twin instead of being read and analyzed again: the thumbnail, the search data, the people with their crops
/// and paths, the playing copy of a 360 video. A folder moved to the cloud and kept on the computer too is the
/// usual case; plain duplicates in two folders gain the same way.
/// </summary>
public sealed class AnalysisTwin
{
    private readonly AppPaths _paths;
    private readonly Database _db;
    private readonly HashSet<long> _tried = [];

    public AnalysisTwin(AppPaths paths, Database db)
    {
        _paths = paths;
        _db = db;
    }

    private sealed class Pair
    {
        public long Id { get; init; }
        public long TwinId { get; init; }
    }

    private sealed class TwinState
    {
        public int AnalysisVersion { get; init; }
        public int PeopleVersion { get; init; }
        public long? EventId { get; init; }
    }

    /// <summary>Gives up to limit items without a thumbnail what their twins have; the number of items done.</summary>
    public int CopyBatch(IVectorIndex? index, int limit)
    {
        List<Pair> pairs;
        using (var c = _db.Open())
        {
            pairs = c.Query<Pair>("""
                SELECT id, twin_id FROM (
                    SELECT m.id AS id,
                           (SELECT t.id FROM media t
                            WHERE t.quick_hash=m.quick_hash AND t.size=m.size AND t.id<>m.id AND t.thumb_state=1 AND t.missing=0
                            ORDER BY t.people_version DESC, t.analysis_version DESC, t.id LIMIT 1) AS twin_id
                    FROM media m WHERE m.thumb_state=0 AND m.missing=0 AND m.quick_hash IS NOT NULL)
                WHERE twin_id IS NOT NULL
                """).Where(p => !_tried.Contains(p.Id)).Take(limit).ToList();
        }
        var done = 0;
        foreach (var p in pairs)
        {
            _tried.Add(p.Id);
            if (Copy(p.Id, p.TwinId, index)) done++;
        }
        return done;
    }

    private bool Copy(long id, long twinId, IVectorIndex? index)
    {
        var thumb = _paths.ThumbPath(twinId);
        if (!File.Exists(thumb)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ThumbPath(id))!);
        File.Copy(thumb, _paths.ThumbPath(id), true);

        using var c = _db.Open();
        var twin = c.QuerySingle<TwinState>("SELECT analysis_version, people_version, event_id FROM media WHERE id=@twinId", new { twinId });
        var analysis = 0;
        if (twin.AnalysisVersion > 0 && index != null && index.RowsOf(twinId) is { Count: > 0 } rows)
        {
            index.Put(id, rows.Select(r => r with { MediaId = id }).ToList());
            analysis = twin.AnalysisVersion;
        }

        var people = 0;
        if (twin.PeopleVersion > 0)
        {
            using var tx = c.BeginTransaction();
            c.Execute("DELETE FROM detection WHERE media_id=@id; DELETE FROM track_box WHERE media_id=@id;", new { id }, tx);
            var newIds = new Dictionary<long, long>();
            var detections = c.Query<(long Id, long? LinkedId)>("SELECT id, linked_id FROM detection WHERE media_id=@twinId ORDER BY id",
                new { twinId }, tx).ToList();
            foreach (var (old, _) in detections)
            {
                newIds[old] = c.ExecuteScalar<long>("""
                    INSERT INTO detection(media_id, time_ms, kind, x, y, w, h, score, quality, track, linked_id, embedding, cluster,
                                          person_id, assign_source, confidence, confirmed, suggest_person_id, suggest_score)
                    SELECT @id, time_ms, kind, x, y, w, h, score, quality, track, linked_id, embedding, cluster,
                           person_id, assign_source, confidence, confirmed, suggest_person_id, suggest_score
                    FROM detection WHERE id=@old RETURNING id
                    """, new { id, old }, tx);
            }
            foreach (var (old, linked) in detections)
            {
                var now = newIds[old];
                // A face and its body point at each other: the copies at the copies.
                if (linked is { } l)
                    c.Execute("UPDATE detection SET linked_id=@to WHERE id=@now", new { now, to = newIds.TryGetValue(l, out var n) ? n : (long?)null }, tx);
                c.Execute("INSERT OR IGNORE INTO rejection(detection_id, person_id) SELECT @now, person_id FROM rejection WHERE detection_id=@old",
                    new { now, old }, tx);
            }
            c.Execute("INSERT INTO track_box(media_id, track, time_ms, x, y, w, h) SELECT @id, track, time_ms, x, y, w, h FROM track_box WHERE media_id=@twinId",
                new { id, twinId }, tx);
            tx.Commit();
            foreach (var (old, now) in newIds)
            {
                var crop = _paths.PeopleCropPath(old);
                if (!File.Exists(crop)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(_paths.PeopleCropPath(now))!);
                File.Copy(crop, _paths.PeopleCropPath(now), true);
            }
            people = twin.PeopleVersion;
        }

        c.Execute("UPDATE media SET thumb_state=1, analysis_version=@analysis, people_version=@people, event_id=@EventId WHERE id=@id",
            new { id, analysis, people, twin.EventId });
        CopyProxy360(twinId, id);
        return true;
    }

    /// <summary>The playing copy of a 360 video is named after the item; the twin's serves this one as well.</summary>
    private static void CopyProxy360(long twinId, long id)
    {
        if (Media.Proxy360.Dir is not { } dir || !Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, $"{twinId}_*_e2.mp4"))
        {
            var target = Path.Combine(dir, id + Path.GetFileName(file)[twinId.ToString().Length..]);
            if (!File.Exists(target)) File.Copy(file, target);
        }
    }
}
