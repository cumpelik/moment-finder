using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Dapper;
using MediaLens.Analysis;
using MediaLens.Data;

namespace MediaLens.People;

/// <summary>How an identity got to a detection; the order is also the trust order.</summary>
public enum AssignSource
{
    User = 1,
    Face = 2,
    Linked = 3,
    Track = 4,
    Outfit = 5,
}

public sealed class Person
{
    public long Id { get; init; }
    public string Name { get; set; } = "";
    public long CreatedAt { get; init; }
    public long? CoverDetectionId { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Filled by ListPersons: photos and videos with this person.</summary>
    public long MediaCount { get; init; }
}

/// <summary>A detection to store: box normalized to 0..1 of the upright image.</summary>
public sealed record NewDetection(DetectionKind Kind, int TimeMs, float X, float Y, float W, float H, float Score,
    float Quality, float[] Embedding, int? Track, int? LinkedIndex);

/// <summary>Where a person of a video (Track as in NewDetection) is in one analyzed frame.</summary>
public sealed record NewTrackBox(int Track, int TimeMs, float X, float Y, float W, float H);

/// <summary>A stored point of a track's path.</summary>
public sealed record TrackBox(long Track, int TimeMs, float X, float Y, float W, float H);

/// <summary>A stored detection as the identity matching and the UI see it.</summary>
public sealed class DetectionRow
{
    public long Id { get; init; }
    public long MediaId { get; init; }
    public int TimeMs { get; init; }
    public DetectionKind Kind { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float W { get; init; }
    public float H { get; init; }
    public float Score { get; init; }
    public float Quality { get; init; }
    public long? Track { get; init; }
    public long? LinkedId { get; init; }
    public long? Cluster { get; set; }
    public long? PersonId { get; set; }
    public AssignSource? AssignSource { get; set; }
    public float? Confidence { get; set; }
    public bool Confirmed { get; set; }
    public long? SuggestPersonId { get; set; }
    public float? SuggestScore { get; set; }
    public long? EventId { get; init; }
    public long TakenAt { get; init; }
    public float[] Embedding { get; set; } = [];
}

public sealed class PeopleRepository
{
    private readonly Database _db;

    public PeopleRepository(Database db) => _db = db;

    internal static byte[] ToBlob(float[] v)
    {
        var half = new Half[v.Length];
        TensorPrimitives.ConvertToHalf(v, half);
        return MemoryMarshal.AsBytes(half.AsSpan()).ToArray();
    }

    internal static float[] FromBlob(byte[] blob)
    {
        var half = MemoryMarshal.Cast<byte, Half>(blob);
        var v = new float[half.Length];
        TensorPrimitives.ConvertToSingle(half, v);
        return v;
    }

    // ---- persons ----

    public List<Person> ListPersons(bool includeHidden = false)
    {
        using var c = _db.Open();
        return c.Query<Person>($"""
            SELECT p.id, p.name, p.created_at, p.cover_detection_id, p.hidden,
                   (SELECT COUNT(DISTINCT d.media_id) FROM detection d JOIN media m ON m.id=d.media_id
                    WHERE d.person_id=p.id AND m.missing=0) AS MediaCount
            FROM person p {(includeHidden ? "" : "WHERE p.hidden=0")} ORDER BY p.name
            """).ToList();
    }

    public Person? FindPerson(string name)
    {
        using var c = _db.Open();
        return c.QueryFirstOrDefault<Person>("SELECT id, name, created_at, cover_detection_id, hidden FROM person WHERE name=@name", new { name = name.Trim() });
    }

    public Person CreatePerson(string name, bool hidden = false)
    {
        using var c = _db.Open();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = c.ExecuteScalar<long>("INSERT INTO person(name, created_at, hidden) VALUES(@name, @now, @hidden) RETURNING id",
            new { name = name.Trim(), now, hidden });
        return new Person { Id = id, Name = name.Trim(), CreatedAt = now, Hidden = hidden };
    }

    public Person FindOrCreatePerson(string name) => FindPerson(name) ?? CreatePerson(name);

    /// <summary>The hidden person that collects faces of strangers, so they do not come back as unnamed groups.</summary>
    public Person Strangers() => ListPersons(true).FirstOrDefault(p => p.Hidden) ?? CreatePerson("(strangers)", hidden: true);

    public void RenamePerson(long id, string name)
    {
        using var c = _db.Open();
        c.Execute("UPDATE person SET name=@name WHERE id=@id", new { id, name = name.Trim() });
    }

    /// <summary>Everything of "from" goes to "into" (two names for one person); "from" is deleted.</summary>
    public void MergePersons(long from, long into)
    {
        if (from == into) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE detection SET person_id=@into WHERE person_id=@from", new { from, into }, tx);
        c.Execute("UPDATE detection SET suggest_person_id=NULL, suggest_score=NULL WHERE suggest_person_id=@from", new { from }, tx);
        c.Execute("INSERT OR IGNORE INTO rejection(detection_id, person_id) SELECT detection_id, @into FROM rejection WHERE person_id=@from", new { from, into }, tx);
        c.Execute("DELETE FROM person WHERE id=@from", new { from }, tx);
        tx.Commit();
    }

    public void DeletePerson(long id)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("UPDATE detection SET person_id=NULL, assign_source=NULL, confidence=NULL, confirmed=0 WHERE person_id=@id", new { id }, tx);
        c.Execute("DELETE FROM person WHERE id=@id", new { id }, tx);
        tx.Commit();
    }

    public void SetCover(long personId, long detectionId)
    {
        using var c = _db.Open();
        c.Execute("UPDATE person SET cover_detection_id=@detectionId WHERE id=@personId", new { personId, detectionId });
    }

    // ---- detections ----

    /// <summary>
    /// Replaces the detections of a media item (new analysis). What the user confirmed survives when a new
    /// detection lies at the same place (same frame, boxes overlapping), or a new track passes through it.
    /// </summary>
    public List<long> ReplaceDetections(long mediaId, IReadOnlyList<NewDetection> detections, IReadOnlyList<NewTrackBox>? paths = null)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        var old = c.Query<(long Id, long Kind, long TimeMs, double X, double Y, double W, double H, long PersonId)>(
            "SELECT id, kind, time_ms, x, y, w, h, person_id FROM detection WHERE media_id=@mediaId AND confirmed=1 AND person_id IS NOT NULL",
            new { mediaId }, tx).ToList();
        c.Execute("DELETE FROM detection WHERE media_id=@mediaId", new { mediaId }, tx);
        var ids = new List<long>();
        var carriedOld = new HashSet<long>();
        foreach (var d in detections)
        {
            var carried = old.FirstOrDefault(o => o.Kind == (long)d.Kind && o.TimeMs == d.TimeMs &&
                new Box((float)o.X, (float)o.Y, (float)o.W, (float)o.H).Iou(new Box(d.X, d.Y, d.W, d.H)) > 0.5f);
            if (carried.Id != 0) carriedOld.Add(carried.Id);
            ids.Add(c.ExecuteScalar<long>("""
                INSERT INTO detection(media_id, time_ms, kind, x, y, w, h, score, quality, track, embedding,
                                      person_id, assign_source, confidence, confirmed)
                VALUES(@mediaId, @TimeMs, @Kind, @X, @Y, @W, @H, @Score, @Quality, @track, @embedding,
                       @person, @source, @confidence, @confirmed)
                RETURNING id
                """, new
            {
                mediaId, d.TimeMs, Kind = (int)d.Kind, d.X, d.Y, d.W, d.H, d.Score, d.Quality,
                track = (int?)null, embedding = ToBlob(d.Embedding),
                person = carried.Id != 0 ? carried.PersonId : (long?)null,
                source = carried.Id != 0 ? (int)AssignSource.User : (int?)null,
                confidence = carried.Id != 0 ? 1.0 : (double?)null,
                confirmed = carried.Id != 0,
            }, tx));
        }
        for (var i = 0; i < detections.Count; i++)
        {
            if (detections[i].LinkedIndex is { } li)
                c.Execute("UPDATE detection SET linked_id=@l WHERE id=@id", new { id = ids[i], l = ids[li] }, tx);
        }
        // Tracks are numbered per video; make them unique in the library by the first detection id.
        var firstOfTrack = new Dictionary<int, long>();
        for (var i = 0; i < detections.Count; i++)
        {
            if (detections[i].Track is not { } t) continue;
            if (!firstOfTrack.TryGetValue(t, out var first)) firstOfTrack[t] = first = ids[i];
            c.Execute("UPDATE detection SET track=@first WHERE id=@id", new { first, id = ids[i] }, tx);
        }
        c.Execute("DELETE FROM track_box WHERE media_id=@mediaId", new { mediaId }, tx);
        foreach (var p in paths ?? [])
        {
            if (!firstOfTrack.TryGetValue(p.Track, out var track)) continue;
            c.Execute("INSERT INTO track_box(media_id, track, time_ms, x, y, w, h) VALUES(@mediaId, @track, @TimeMs, @X, @Y, @W, @H)",
                new { mediaId, track, p.TimeMs, p.X, p.Y, p.W, p.H }, tx);
        }
        // A confirmation whose detection is gone: the new track passing through the same place takes it.
        foreach (var o in old.Where(o => !carriedOld.Contains(o.Id)))
        {
            var box = new Box((float)o.X, (float)o.Y, (float)o.W, (float)o.H);
            var hit = (paths ?? []).Where(p => p.TimeMs == o.TimeMs && firstOfTrack.ContainsKey(p.Track))
                .Select(p =>
                {
                    var path = new Box(p.X, p.Y, p.W, p.H);
                    // A confirmed face: the path is its body, the face sits in its upper part.
                    var faceInside = o.Kind == (long)DetectionKind.Face && path.Contains(box.X + box.W / 2, box.Y + box.H / 2) &&
                                     box.Y + box.H / 2 < path.Y + path.H * 0.5f;
                    return (p.Track, Iou: Math.Max(box.Iou(path), faceInside ? 0.6f : 0f));
                })
                .Where(x => x.Iou > 0.5f).OrderByDescending(x => x.Iou).FirstOrDefault();
            if (hit.Iou == 0) continue;
            var target = Enumerable.Range(0, detections.Count).Where(i => detections[i].Track == hit.Track)
                .OrderByDescending(i => (long)detections[i].Kind == o.Kind).ThenByDescending(i => detections[i].Quality).First();
            c.Execute("UPDATE detection SET person_id=@PersonId, assign_source=@source, confidence=1, confirmed=1 WHERE id=@id AND confirmed=0",
                new { o.PersonId, source = (int)AssignSource.User, id = ids[target] }, tx);
        }
        tx.Commit();
        return ids;
    }

    /// <summary>The paths of the people of a video, by track and time.</summary>
    public List<TrackBox> TrackPaths(long mediaId)
    {
        using var c = _db.Open();
        return c.Query<(long Track, long TimeMs, double X, double Y, double W, double H)>(
                "SELECT track, time_ms, x, y, w, h FROM track_box WHERE media_id=@mediaId ORDER BY track, time_ms", new { mediaId })
            .Select(r => new TrackBox(r.Track, (int)r.TimeMs, (float)r.X, (float)r.Y, (float)r.W, (float)r.H)).ToList();
    }

    /// <summary>Who is on which item (named, not hidden people), for grouping by person.</summary>
    public Dictionary<long, List<long>> PersonsByMedia()
    {
        using var c = _db.Open();
        return c.Query<(long MediaId, long PersonId)>("""
                SELECT DISTINCT d.media_id, d.person_id FROM detection d JOIN person p ON p.id=d.person_id AND p.hidden=0
                """)
            .GroupBy(r => r.MediaId).ToDictionary(g => g.Key, g => g.Select(r => r.PersonId).ToList());
    }

    /// <summary>The named people seen in these media, for choosing whose moments to play.</summary>
    public List<long> PersonsIn(IEnumerable<long> mediaIds)
    {
        var ids = mediaIds.ToList();
        if (ids.Count == 0) return [];
        using var c = _db.Open();
        return c.Query<long>("""
            SELECT d.person_id FROM detection d JOIN person p ON p.id=d.person_id AND p.hidden=0
            WHERE d.media_id IN @ids GROUP BY d.person_id ORDER BY COUNT(DISTINCT d.media_id) DESC
            """, new { ids }).ToList();
    }

    /// <summary>All detections with their media's event and time, for the identity matching.</summary>
    public List<DetectionRow> LoadAll()
    {
        using var c = _db.Open();
        var rows = new List<DetectionRow>();
        using var reader = c.ExecuteReader("""
            SELECT d.id, d.media_id, d.time_ms, d.kind, d.x, d.y, d.w, d.h, d.score, d.quality, d.track, d.linked_id,
                   d.cluster, d.person_id, d.assign_source, d.confidence, d.confirmed, d.suggest_person_id, d.suggest_score,
                   m.event_id, m.taken_at, d.embedding
            FROM detection d JOIN media m ON m.id=d.media_id WHERE m.missing=0
            """);
        while (reader.Read())
        {
            rows.Add(new DetectionRow
            {
                Id = reader.GetInt64(0), MediaId = reader.GetInt64(1), TimeMs = reader.GetInt32(2),
                Kind = (DetectionKind)reader.GetInt32(3), X = reader.GetFloat(4), Y = reader.GetFloat(5),
                W = reader.GetFloat(6), H = reader.GetFloat(7), Score = reader.GetFloat(8), Quality = reader.GetFloat(9),
                Track = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                LinkedId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                Cluster = reader.IsDBNull(12) ? null : reader.GetInt64(12),
                PersonId = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                AssignSource = reader.IsDBNull(14) ? null : (AssignSource)reader.GetInt32(14),
                Confidence = reader.IsDBNull(15) ? null : reader.GetFloat(15),
                Confirmed = reader.GetInt32(16) != 0,
                SuggestPersonId = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                SuggestScore = reader.IsDBNull(18) ? null : reader.GetFloat(18),
                EventId = reader.IsDBNull(19) ? null : reader.GetInt64(19),
                TakenAt = reader.GetInt64(20),
                Embedding = FromBlob((byte[])reader.GetValue(21)),
            });
        }
        return rows;
    }

    public Dictionary<long, HashSet<long>> Rejections()
    {
        using var c = _db.Open();
        var result = new Dictionary<long, HashSet<long>>();
        foreach (var (d, p) in c.Query<(long, long)>("SELECT detection_id, person_id FROM rejection"))
        {
            if (!result.TryGetValue(d, out var set)) result[d] = set = [];
            set.Add(p);
        }
        return result;
    }

    /// <summary>Writes the automatic results (only rows whose values changed are passed).</summary>
    public void SaveAssignments(IEnumerable<DetectionRow> rows)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var r in rows)
            c.Execute("""
                UPDATE detection SET cluster=@Cluster, person_id=@PersonId, assign_source=@source, confidence=@Confidence,
                       suggest_person_id=@SuggestPersonId, suggest_score=@SuggestScore WHERE id=@Id AND confirmed=0
                """, new { r.Cluster, r.PersonId, source = (int?)r.AssignSource, r.Confidence, r.SuggestPersonId, r.SuggestScore, r.Id }, tx);
        tx.Commit();
    }

    /// <summary>The user says who it is: confirmed, never changed by the automatic matching.</summary>
    public void Confirm(IEnumerable<long> detectionIds, long personId)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var id in detectionIds)
        {
            c.Execute("""
                UPDATE detection SET person_id=@personId, assign_source=1, confidence=1, confirmed=1,
                       suggest_person_id=NULL, suggest_score=NULL WHERE id=@id
                """, new { id, personId }, tx);
            c.Execute("DELETE FROM rejection WHERE detection_id=@id AND person_id=@personId", new { id, personId }, tx);
        }
        tx.Commit();
    }

    /// <summary>"Not this person": the detection is freed and never offered for that person again.</summary>
    public void Reject(IEnumerable<long> detectionIds, long personId)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        foreach (var id in detectionIds)
        {
            c.Execute("INSERT OR IGNORE INTO rejection(detection_id, person_id) VALUES(@id, @personId)", new { id, personId }, tx);
            c.Execute("""
                UPDATE detection SET person_id=CASE WHEN person_id=@personId THEN NULL ELSE person_id END,
                       assign_source=CASE WHEN person_id=@personId THEN NULL ELSE assign_source END,
                       confirmed=CASE WHEN person_id=@personId THEN 0 ELSE confirmed END,
                       suggest_person_id=CASE WHEN suggest_person_id=@personId THEN NULL ELSE suggest_person_id END
                WHERE id=@id
                """, new { id, personId }, tx);
        }
        tx.Commit();
    }

    public List<DetectionRow> DetectionsOfMedia(long mediaId)
    {
        using var c = _db.Open();
        return c.Query<DetectionRow>("""
            SELECT id, media_id, time_ms, kind, x, y, w, h, score, quality, track, linked_id, cluster, person_id,
                   assign_source, confidence, confirmed, suggest_person_id, suggest_score
            FROM detection WHERE media_id=@mediaId ORDER BY time_ms, kind
            """, new { mediaId }).ToList();
    }

    /// <summary>Detections for a gallery of crops: of a person, of an unnamed group, best first.</summary>
    public List<DetectionRow> Detections(long? personId = null, long? cluster = null, DetectionKind? kind = null, int limit = 500)
    {
        using var c = _db.Open();
        var where = new List<string> { "m.missing=0" };
        if (personId != null) where.Add("d.person_id=@personId");
        if (cluster != null) where.Add("d.cluster=@cluster");
        if (kind != null) where.Add("d.kind=@kind");
        return c.Query<DetectionRow>($"""
            SELECT d.id, d.media_id, d.time_ms, d.kind, d.x, d.y, d.w, d.h, d.score, d.quality, d.track, d.linked_id,
                   d.cluster, d.person_id, d.assign_source, d.confidence, d.confirmed, d.suggest_person_id, d.suggest_score,
                   m.taken_at AS TakenAt
            FROM detection d JOIN media m ON m.id=d.media_id
            WHERE {string.Join(" AND ", where)}
            ORDER BY d.confirmed DESC, d.quality DESC LIMIT @limit
            """, new { personId, cluster, kind = (int?)kind, limit }).ToList();
    }

    public sealed class ClusterInfo
    {
        public long Cluster { get; init; }
        public long Faces { get; init; }
        public long Media { get; init; }
        public long CoverId { get; init; }
    }

    /// <summary>Groups of faces nobody named yet, biggest first.</summary>
    public List<ClusterInfo> UnnamedClusters()
    {
        using var c = _db.Open();
        return c.Query<ClusterInfo>("""
            SELECT d.cluster AS Cluster, COUNT(*) AS Faces, COUNT(DISTINCT d.media_id) AS Media,
                   (SELECT d2.id FROM detection d2 WHERE d2.cluster=d.cluster ORDER BY d2.quality DESC LIMIT 1) AS CoverId
            FROM detection d JOIN media m ON m.id=d.media_id
            WHERE d.kind=@face AND d.cluster IS NOT NULL AND d.person_id IS NULL AND m.missing=0
            GROUP BY d.cluster HAVING COUNT(DISTINCT d.media_id) >= 2
            ORDER BY Media DESC
            """, new { face = (int)DetectionKind.Face }).ToList();
    }

    public sealed class FigureInfo
    {
        /// <summary>The group of alike figures (IdentityService.ClusterFigures).</summary>
        public long GroupKey { get; init; }
        public long MediaId { get; init; }
        public long CoverId { get; init; }
        public int TimeMs { get; init; }
        public long Media { get; init; }
        public long Tracks { get; init; }
    }

    /// <summary>
    /// People nobody named whose face does not identify them (helmet and goggles, seen from behind, too small):
    /// groups of alike figures of one event, the clearest first. A group whose face is already among the
    /// groups of faces is left to that group.
    /// </summary>
    public List<FigureInfo> UnnamedFigures(IReadOnlySet<long> faceClusters, int limit = 300)
    {
        using var c = _db.Open();
        var members = c.Query<(long Cluster, long Id, long? Track, long MediaId, int TimeMs, double Quality)>("""
            SELECT d.cluster, d.id, d.track, d.media_id, d.time_ms, d.quality
            FROM detection d JOIN media m ON m.id=d.media_id
            WHERE d.kind=@body AND d.cluster IS NOT NULL AND d.person_id IS NULL AND m.missing=0
            """, new { body = (int)DetectionKind.Body }).ToList();
        var skip = new HashSet<long>();
        if (faceClusters.Count > 0)
            foreach (var (track, linked) in c.Query<(long? Track, long? Linked)>(
                         "SELECT track, linked_id FROM detection WHERE kind=@face AND person_id IS NULL AND cluster IN @faceClusters",
                         new { face = (int)DetectionKind.Face, faceClusters }))
            {
                if (track is { } t) skip.Add(t);
                if (linked is { } l) skip.Add(l);
            }
        return members.GroupBy(m => m.Cluster)
            .Where(g => !g.Any(m => skip.Contains(m.Id) || m.Track is { } t && skip.Contains(t)))
            .Select(g =>
            {
                var cover = g.MaxBy(m => m.Quality);
                return (Quality: cover.Quality, Info: new FigureInfo
                {
                    GroupKey = g.Key, MediaId = cover.MediaId, CoverId = cover.Id, TimeMs = cover.TimeMs,
                    Media = g.Select(m => m.MediaId).Distinct().Count(), Tracks = g.Select(m => m.Track ?? m.Id).Distinct().Count(),
                });
            })
            .OrderByDescending(x => x.Info.Tracks).ThenByDescending(x => x.Quality)
            .Take(limit).Select(x => x.Info).ToList();
    }

    /// <summary>The unnamed detections of a group of figures: its bodies and the faces on them or on their tracks.</summary>
    public List<long> FigureDetectionIds(long groupKey)
    {
        using var c = _db.Open();
        return c.Query<long>("""
            WITH b AS (SELECT id, track FROM detection WHERE kind=@body AND cluster=@groupKey AND person_id IS NULL)
            SELECT id FROM b
            UNION
            SELECT f.id FROM detection f WHERE f.kind=@face AND f.person_id IS NULL
              AND (f.linked_id IN (SELECT id FROM b) OR f.track IN (SELECT track FROM b WHERE track IS NOT NULL))
            """, new { groupKey, body = (int)DetectionKind.Body, face = (int)DetectionKind.Face }).ToList();
    }

    public List<long> ClusterDetectionIds(long cluster)
    {
        using var c = _db.Open();
        return c.Query<long>("SELECT id FROM detection WHERE cluster=@cluster AND person_id IS NULL", new { cluster }).ToList();
    }

    /// <summary>The review queue: open suggestions, most likely first.</summary>
    public List<DetectionRow> Suggestions(int limit)
    {
        using var c = _db.Open();
        return c.Query<DetectionRow>("""
            SELECT d.id, d.media_id, d.time_ms, d.kind, d.x, d.y, d.w, d.h, d.score, d.quality, d.track, d.linked_id,
                   d.cluster, d.person_id, d.assign_source, d.confidence, d.confirmed, d.suggest_person_id, d.suggest_score,
                   m.taken_at AS TakenAt
            FROM detection d JOIN media m ON m.id=d.media_id
            JOIN person p ON p.id=d.suggest_person_id AND p.hidden=0
            WHERE d.suggest_person_id IS NOT NULL AND d.confirmed=0 AND m.missing=0
            ORDER BY d.kind, d.suggest_score DESC LIMIT @limit
            """, new { limit }).ToList();
    }

    public long SuggestionCount()
    {
        using var c = _db.Open();
        return c.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM detection d JOIN media m ON m.id=d.media_id JOIN person p ON p.id=d.suggest_person_id AND p.hidden=0
            WHERE d.suggest_person_id IS NOT NULL AND d.confirmed=0 AND m.missing=0
            """);
    }

    /// <summary>For a video: where each person appears first (playback starts there).</summary>
    /// <summary>
    /// When the people are on screen in each video: the spans of their tracks, widened by the frame interval
    /// (a person is seen at sampled frames only) and merged when they touch.
    /// </summary>
    public Dictionary<long, List<(int StartMs, int EndMs)>> PersonSegments(IEnumerable<long> personIds)
    {
        var ids = personIds.ToList();
        if (ids.Count == 0) return [];
        using var c = _db.Open();
        var spans = c.Query<(long MediaId, long From, long To, long Duration)>("""
            SELECT d.media_id, MIN(d.time_ms), MAX(d.time_ms), COALESCE(m.duration_ms, 0)
            FROM detection d JOIN media m ON m.id=d.media_id
            WHERE m.kind=2 AND d.track IN (SELECT track FROM detection WHERE person_id IN @ids AND track IS NOT NULL)
            GROUP BY d.media_id, d.track
            """, new { ids }).ToList();
        var result = new Dictionary<long, List<(int, int)>>();
        foreach (var video in spans.GroupBy(s => s.MediaId))
        {
            var pad = PeopleAnalyzer.FrameIntervalMs(video.First().Duration);
            var merged = new List<(int Start, int End)>();
            foreach (var s in video.OrderBy(s => s.From))
            {
                var start = (int)Math.Max(0, s.From - pad / 2);
                var end = (int)(s.To + pad);
                if (merged.Count > 0 && start <= merged[^1].End + 1000)
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
                else
                    merged.Add((start, end));
            }
            result[video.Key] = merged;
        }
        return result;
    }

    public Dictionary<long, int> FirstAppearance(IEnumerable<long> personIds)
    {
        var ids = personIds.ToList();
        if (ids.Count == 0) return [];
        using var c = _db.Open();
        return c.Query<(long MediaId, long TimeMs)>(
                "SELECT media_id, MIN(time_ms) FROM detection WHERE person_id IN @ids GROUP BY media_id", new { ids })
            .ToDictionary(r => r.MediaId, r => (int)r.TimeMs);
    }
}
