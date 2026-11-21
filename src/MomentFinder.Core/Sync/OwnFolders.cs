using Dapper;
using MomentFinder.Data;
using MomentFinder.Library;

namespace MomentFinder.Sync;

/// <summary>
/// A computer that joins the shared library keeps its own folders (on its disk, not in the shared library) with
/// everything found in them: the rows, thumbnails, faces and figures with who they are, the crops. Only the search
/// data is made again (it lives in one file per library). Nothing of it is read or analyzed again.
/// </summary>
public static class OwnFolders
{
    /// <summary>Carries the folders of the library this computer had (before) that are on this disk and not in the library now.</summary>
    public static int Carry(AppPaths paths, Database db, string before, string beforeThumbs)
    {
        using var c = db.Open();
        c.Execute("ATTACH DATABASE @before AS b", new { before });
        var carried = 0;
        try
        {
            using var tx = c.BeginTransaction();
            var here = c.Query<string>("SELECT root FROM source WHERE kind=@kind", new { kind = SourceKinds.Local }, tx)
                .Select(Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var persons = new Dictionary<long, long>();
            long Person(long old)
            {
                if (persons.TryGetValue(old, out var id)) return id;
                var (name, hidden) = c.QuerySingle<(string, bool)>("SELECT name, hidden FROM b.person WHERE id=@old", new { old }, tx);
                id = c.ExecuteScalar<long?>("SELECT id FROM person WHERE name=@name", new { name }, tx)
                     ?? c.ExecuteScalar<long>("INSERT INTO person(name, created_at, hidden) VALUES(@name, @now, @hidden) RETURNING id",
                         new { name, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hidden }, tx);
                return persons[old] = id;
            }
            foreach (var s in c.Query<(long Id, string Root, string Name, long AddedAt)>(
                         "SELECT id, root, name, added_at FROM b.source WHERE kind=@kind", new { kind = SourceKinds.Local }, tx).ToList())
            {
                if (!Directory.Exists(s.Root) || here.Contains(Full(s.Root))) continue;
                var source = c.ExecuteScalar<long>("INSERT INTO source(kind, root, name, added_at) VALUES(@kind, @Root, @Name, @AddedAt) RETURNING id",
                    new { kind = SourceKinds.Local, s.Root, s.Name, s.AddedAt }, tx);
                var media = new Dictionary<long, long>();
                foreach (var m in c.Query("SELECT * FROM b.media WHERE source_id=@Id", new { s.Id }, tx).Cast<IDictionary<string, object?>>().ToList())
                {
                    var id = c.ExecuteScalar<long>("""
                        INSERT INTO media(source_id, rel_path, kind, size, mtime, quick_hash, taken_at, width, height, duration_ms, lat, lon, camera,
                                          thumb_state, analysis_version, people_version, missing, taken_from_file)
                        VALUES(@source, @rel_path, @kind, @size, @mtime, @quick_hash, @taken_at, @width, @height, @duration_ms, @lat, @lon, @camera,
                               @thumb_state, 0, @people_version, @missing, @taken_from_file) RETURNING id
                        """, new
                    {
                        source, rel_path = m["rel_path"], kind = m["kind"], size = m["size"], mtime = m["mtime"], quick_hash = m["quick_hash"],
                        taken_at = m["taken_at"], width = m["width"], height = m["height"], duration_ms = m["duration_ms"], lat = m["lat"], lon = m["lon"],
                        camera = m["camera"], thumb_state = m["thumb_state"], people_version = m["people_version"], missing = m["missing"],
                        taken_from_file = m.TryGetValue("taken_from_file", out var tff) ? tff : null,
                    }, tx);
                    var old = (long)m["id"]!;
                    media[old] = id;
                    var thumb = Path.Combine(beforeThumbs, (old / 1000).ToString(), $"{old}.jpg");
                    if (File.Exists(thumb))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(paths.ThumbPath(id))!);
                        File.Copy(thumb, paths.ThumbPath(id), true);
                    }
                    else c.Execute("UPDATE media SET thumb_state=0 WHERE id=@id", new { id }, tx);
                }
                var detections = new Dictionary<long, long>();
                foreach (var (oldMedia, newMedia) in media)
                {
                    foreach (var d in c.Query("SELECT * FROM b.detection WHERE media_id=@oldMedia", new { oldMedia }, tx).Cast<IDictionary<string, object?>>().ToList())
                    {
                        var confirmed = (long)(d["confirmed"] ?? 0L) != 0 && d["person_id"] is long;
                        var id = c.ExecuteScalar<long>("""
                            INSERT INTO detection(media_id, time_ms, kind, x, y, w, h, score, quality, embedding, person_id, assign_source, confidence, confirmed)
                            VALUES(@newMedia, @time_ms, @kind, @x, @y, @w, @h, @score, @quality, @embedding, @person, @source, @confidence, @confirmed) RETURNING id
                            """, new
                        {
                            newMedia, time_ms = d["time_ms"], kind = d["kind"], x = d["x"], y = d["y"], w = d["w"], h = d["h"], score = d["score"],
                            quality = d["quality"], embedding = d["embedding"], person = confirmed ? Person((long)d["person_id"]!) : (long?)null,
                            source = confirmed ? 1 : (int?)null, confidence = confirmed ? 1.0 : (double?)null, confirmed,
                        }, tx);
                        var oldId = (long)d["id"]!;
                        detections[oldId] = id;
                        var crop = Path.Combine(beforeThumbs, "people", (oldId / 1000).ToString(), $"{oldId}.jpg");
                        if (File.Exists(crop))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(paths.PeopleCropPath(id))!);
                            File.Copy(crop, paths.PeopleCropPath(id), true);
                        }
                    }
                }
                foreach (var (oldId, newId) in detections)
                {
                    var (linked, track) = c.QuerySingle<(long?, long?)>("SELECT linked_id, track FROM b.detection WHERE id=@oldId", new { oldId }, tx);
                    c.Execute("UPDATE detection SET linked_id=@l, track=@t WHERE id=@newId", new
                    {
                        newId,
                        l = linked is { } li && detections.TryGetValue(li, out var nl) ? nl : (long?)null,
                        t = track is { } ti && detections.TryGetValue(ti, out var nt) ? nt : (long?)null,
                    }, tx);
                }
                foreach (var (oldMedia, newMedia) in media)
                    foreach (var t in c.Query<(long Track, long TimeMs, double X, double Y, double W, double H)>(
                                 "SELECT track, time_ms, x, y, w, h FROM b.track_box WHERE media_id=@oldMedia", new { oldMedia }, tx).ToList())
                        if (detections.TryGetValue(t.Track, out var track))
                            c.Execute("INSERT INTO track_box(media_id, track, time_ms, x, y, w, h) VALUES(@newMedia, @track, @TimeMs, @X, @Y, @W, @H)",
                                new { newMedia, track, t.TimeMs, t.X, t.Y, t.W, t.H }, tx);
                foreach (var (d, p) in c.Query<(long, long)>("SELECT detection_id, person_id FROM b.rejection", transaction: tx).ToList())
                    if (detections.TryGetValue(d, out var nd))
                        c.Execute("INSERT OR IGNORE INTO rejection(detection_id, person_id) VALUES(@nd, @np)", new { nd, np = Person(p) }, tx);
                carried++;
            }
            tx.Commit();
        }
        finally
        {
            c.Execute("DETACH DATABASE b");
        }
        return carried;
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
