using Dapper;
using MomentFinder.Data;
using MomentFinder.Library;

namespace MomentFinder.Cloud;

public sealed record ImportResult(int MatchedItems, int People, int Detections, int BucketItems);

/// <summary>
/// Brings names, people and buckets from an index backup into this library. Items are matched by content
/// (size and quick hash), so it works after a restore to another folder or on another computer. Detections
/// come with their embeddings: the people analysis does not have to run again for matched items.
/// </summary>
public sealed class IndexImport
{
    private readonly Database _db;

    public IndexImport(Database db) => _db = db;

    public ImportResult Import(string backupPath)
    {
        using var c = _db.Open();
        c.Execute("ATTACH DATABASE @backupPath AS b", new { backupPath });
        try
        {
            using var tx = c.BeginTransaction();
            // backup media id -> local media id
            var map = c.Query<(long From, long To)>("""
                SELECT bm.id, m.id FROM b.media bm
                JOIN media m ON m.quick_hash=bm.quick_hash AND m.size=bm.size AND m.missing=0
                WHERE bm.quick_hash IS NOT NULL
                GROUP BY bm.id
                """, transaction: tx).ToDictionary(r => r.From, r => r.To);

            // people by name
            var personMap = new Dictionary<long, long>();
            var people = 0;
            foreach (var (id, name, hidden) in c.Query<(long, string, long)>("SELECT id, name, hidden FROM b.person", transaction: tx))
            {
                var local = c.ExecuteScalar<long?>("SELECT id FROM person WHERE name=@name", new { name }, tx);
                if (local == null)
                {
                    local = c.ExecuteScalar<long>("INSERT INTO person(name, created_at, hidden) VALUES(@name, @now, @hidden) RETURNING id",
                        new { name, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hidden }, tx);
                    people++;
                }
                personMap[id] = local.Value;
            }

            // detections of items that have none here yet
            var detections = 0;
            var withDetections = c.Query<long>("SELECT DISTINCT media_id FROM detection", transaction: tx).ToHashSet();
            // How far the people analysis is, here and in the backup.
            var versionHere = c.Query<(long Id, int Version)>("SELECT id, people_version FROM media", transaction: tx).ToDictionary(r => r.Id, r => r.Version);
            var versionThere = c.Query<(long Id, int Version)>("SELECT id, people_version FROM b.media", transaction: tx).ToDictionary(r => r.Id, r => r.Version);
            var detMap = new Dictionary<long, long>();
            var confirmedHere = 0;
            foreach (var (from, to) in map)
            {
                if (withDetections.Contains(to))
                {
                    // Analyzed here already: carry over who the user said is who, by matching boxes.
                    var mine = c.Query<(long Id, long Kind, long TimeMs, double X, double Y, double W, double H)>(
                        "SELECT id, kind, time_ms, x, y, w, h FROM detection WHERE media_id=@to AND confirmed=0", new { to }, tx).ToList();
                    foreach (var b in c.Query<(long Kind, long TimeMs, double X, double Y, double W, double H, long PersonId)>(
                                 "SELECT kind, time_ms, x, y, w, h, person_id FROM b.detection WHERE media_id=@from AND confirmed=1 AND person_id IS NOT NULL",
                                 new { from }, tx))
                    {
                        if (!personMap.TryGetValue(b.PersonId, out var person)) continue;
                        var box = new Analysis.Box((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                        var match = mine.Where(m => m.Kind == b.Kind && Math.Abs(m.TimeMs - b.TimeMs) <= 1000)
                            .Select(m => (m.Id, Iou: box.Iou(new Analysis.Box((float)m.X, (float)m.Y, (float)m.W, (float)m.H))))
                            .Where(m => m.Iou > 0.5f).OrderByDescending(m => m.Iou).FirstOrDefault();
                        if (match.Id == 0) continue;
                        confirmedHere += c.Execute("UPDATE detection SET person_id=@person, assign_source=1, confidence=1, confirmed=1 WHERE id=@id",
                            new { person, id = match.Id }, tx);
                    }
                    continue;
                }
                // Analyzed here with nobody found (a landscape, a house), by an analysis as new as the backup's or
                // newer: it stays so. Taking the backup's older state would send the photo to be analyzed again.
                if (versionHere[to] > 0 && versionHere[to] >= versionThere[from]) continue;
                var rows = c.Query("SELECT * FROM b.detection WHERE media_id=@from", new { from }, tx).ToList();
                foreach (var r in rows)
                {
                    var d = (IDictionary<string, object?>)r;
                    long? person = d["person_id"] is long p && personMap.TryGetValue(p, out var lp) ? lp : null;
                    var confirmed = (long)(d["confirmed"] ?? 0L) != 0 && person != null;
                    var newId = c.ExecuteScalar<long>("""
                        INSERT INTO detection(media_id, time_ms, kind, x, y, w, h, score, quality, embedding,
                                              person_id, assign_source, confidence, confirmed)
                        VALUES(@to, @time_ms, @kind, @x, @y, @w, @h, @score, @quality, @embedding,
                               @person, @source, @confidence, @confirmed) RETURNING id
                        """, new
                    {
                        to, time_ms = d["time_ms"], kind = d["kind"], x = d["x"], y = d["y"], w = d["w"], h = d["h"],
                        score = d["score"], quality = d["quality"], embedding = d["embedding"],
                        person = confirmed ? person : null, source = confirmed ? 1 : (int?)null,
                        confidence = confirmed ? 1.0 : (double?)null, confirmed,
                    }, tx);
                    detMap[(long)d["id"]!] = newId;
                    detections++;
                }
                c.Execute("UPDATE media SET people_version=(SELECT people_version FROM b.media WHERE id=@from) WHERE id=@to", new { from, to }, tx);
            }
            // links and tracks point to detection ids of the backup
            foreach (var (oldId, newId) in detMap)
            {
                var (linked, track) = c.QuerySingle<(long?, long?)>("SELECT linked_id, track FROM b.detection WHERE id=@oldId", new { oldId }, tx);
                c.Execute("UPDATE detection SET linked_id=@l, track=@t WHERE id=@newId", new
                {
                    newId,
                    l = linked is { } li && detMap.TryGetValue(li, out var nl) ? nl : (long?)null,
                    t = track is { } ti && detMap.TryGetValue(ti, out var nt) ? nt : (long?)null,
                }, tx);
            }
            foreach (var (d, p) in c.Query<(long, long)>("SELECT detection_id, person_id FROM b.rejection", transaction: tx))
            {
                if (detMap.TryGetValue(d, out var nd) && personMap.TryGetValue(p, out var np))
                    c.Execute("INSERT OR IGNORE INTO rejection(detection_id, person_id) VALUES(@nd, @np)", new { nd, np }, tx);
            }

            // buckets by name, in their order
            var bucketItems = 0;
            foreach (var (id, name) in c.Query<(long, string)>("SELECT id, name FROM b.bucket", transaction: tx))
            {
                var local = c.ExecuteScalar<long?>("SELECT id FROM bucket WHERE name=@name", new { name }, tx)
                            ?? c.ExecuteScalar<long>("INSERT INTO bucket(name, created_at) VALUES(@name, @now) RETURNING id",
                                new { name, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx);
                foreach (var (mediaId, position) in c.Query<(long, double)>("SELECT media_id, position FROM b.bucket_item WHERE bucket_id=@id", new { id }, tx))
                {
                    if (!map.TryGetValue(mediaId, out var to)) continue;
                    bucketItems += c.Execute("INSERT OR IGNORE INTO bucket_item(bucket_id, media_id, position, added_at) VALUES(@local, @to, @position, @now)",
                        new { local, to, position, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx);
                }
            }
            tx.Commit();
            return new ImportResult(map.Count, people, detections + confirmedHere, bucketItems);
        }
        finally
        {
            c.Execute("DETACH DATABASE b");
        }
    }
}
