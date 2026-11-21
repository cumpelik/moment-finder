using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MomentFinder.Data;
using MomentFinder.Library;

namespace MomentFinder.Sync;

/// <summary>
/// What is in each folder in the cloud, the same for every computer of the library: the computer that puts the
/// files there (the backup of its folder) or reads them there (a folder that lives in the cloud) says which files
/// they are, with their date, size and place. The others take that instead of downloading every file to find out,
/// and never show a photo that is not in the cloud yet.
/// </summary>
public static class Catalog
{
    /// <summary>One file as the others learn it (short names: there are many of them).</summary>
    public sealed record FileFact(string S, string P, int K, long Size, long Mtime, string? Q, long Taken, int? W, int? H,
        long? D, double? Lat, double? Lon, string? Cam, bool? Tff);

    private sealed class Row
    {
        public string? Uid { get; init; }
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
        public bool? TakenFromFile { get; init; }
        public bool Missing { get; init; }
    }

    /// <summary>
    /// Says which files of the folder are in the cloud now (inCloud: their paths; null when the folder lives there
    /// and the library's rows are what was found in it) and which are gone. Only what differs from what was said
    /// before goes out. Returns how many files were said; nothing for a folder that is not shared.
    /// </summary>
    public static int Publish(Database db, long sourceId, IReadOnlySet<string>? inCloud = null)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var folder = c.ExecuteScalar<string?>("SELECT uid FROM source WHERE id=@sourceId", new { sourceId }, tx);
        if (folder == null || FactStore.Get(c, tx, Keys.Folder(folder), "def") == null || FactStore.Get(c, tx, Keys.Folder(folder), "deleted") == "1")
            return 0;
        var said = c.Query<(string Obj, string Field, string? Value)>("""
            SELECT f.obj, f.field, f.value FROM media m JOIN fact f ON f.obj='m:'||m.uid WHERE m.source_id=@sourceId AND f.field IN ('file','gone')
            """, new { sourceId }, tx).ToDictionary(r => (r.Obj, r.Field), r => r.Value);
        var rows = c.Query<Row>("""
            SELECT uid AS Uid, rel_path AS RelPath, kind AS Kind, size AS Size, mtime AS Mtime, quick_hash AS QuickHash, taken_at AS TakenAt,
                   width AS Width, height AS Height, duration_ms AS DurationMs, lat AS Lat, lon AS Lon, camera AS Camera,
                   taken_from_file AS TakenFromFile, missing AS Missing
            FROM media WHERE source_id=@sourceId
            """, new { sourceId }, tx).ToList();
        var count = 0;
        foreach (var r in rows)
        {
            if (r.Uid == null) continue;
            var obj = Keys.Media(r.Uid);
            var wasGone = said.GetValueOrDefault((obj, "gone")) == "1";
            if (r.Missing)
            {
                if (said.ContainsKey((obj, "file")) && !wasGone)
                {
                    db.Facts.Set(c, tx, obj, "gone", "1");
                    count++;
                }
                continue;
            }
            // Read here but not in the cloud yet (the backup did not take it): the others do not see it yet.
            if (inCloud != null && !inCloud.Contains(r.RelPath)) continue;
            var json = JsonSerializer.Serialize(new FileFact(folder, r.RelPath, r.Kind, r.Size, r.Mtime, r.QuickHash, r.TakenAt, r.Width, r.Height,
                r.DurationMs, r.Lat, r.Lon, r.Camera, r.TakenFromFile));
            if (said.GetValueOrDefault((obj, "file")) != json)
            {
                db.Facts.Set(c, tx, obj, "file", json);
                count++;
            }
            if (wasGone) db.Facts.Set(c, tx, obj, "gone", "0");
        }
        tx.Commit();
        return count;
    }

    /// <summary>
    /// A file another computer said is in a folder (or is gone): its row in this library, when the folder is one this
    /// computer sees from the cloud. Its own folders it reads itself. Returns the row of a file new here.
    /// </summary>
    public static long? Place(SqliteConnection c, IDbTransaction tx, string mediaUid)
    {
        var obj = Keys.Media(mediaUid);
        var f = FileOf(FactStore.Get(c, tx, obj, "file"));
        if (f == null) return null;
        var source = c.QueryFirstOrDefault<(long Id, string Kind)?>("SELECT id, kind FROM source WHERE uid=@S", new { f.S }, tx);
        if (source is not { } s || s.Kind != SourceKinds.Cloud) return null;
        var missing = FactStore.Get(c, tx, obj, "gone") == "1";
        var existing = c.ExecuteScalar<long?>("SELECT id FROM media WHERE source_id=@Id AND rel_path=@P", new { s.Id, f.P }, tx);
        if (existing is { } id)
        {
            // A changed file is read again (thumbnail, analysis); the same one only follows the facts.
            c.Execute("""
                UPDATE media SET kind=@K, mtime=@Mtime, taken_at=@Taken, width=@W, height=@H, duration_ms=@D, lat=@Lat, lon=@Lon,
                       camera=@Cam, taken_from_file=@Tff, missing=@missing,
                       thumb_state=CASE WHEN size<>@Size OR ifnull(quick_hash,'')<>ifnull(@Q,'') THEN 0 ELSE thumb_state END,
                       analysis_version=CASE WHEN size<>@Size OR ifnull(quick_hash,'')<>ifnull(@Q,'') THEN 0 ELSE analysis_version END,
                       size=@Size, quick_hash=@Q
                WHERE id=@id
                """, new { f.K, f.Mtime, f.Taken, f.W, f.H, f.D, f.Lat, f.Lon, f.Cam, f.Tff, missing, f.Size, f.Q, id }, tx);
            return null;
        }
        if (missing) return null;
        return c.ExecuteScalar<long>("""
            INSERT INTO media(source_id, rel_path, kind, size, mtime, quick_hash, taken_at, width, height, duration_ms, lat, lon, camera, taken_from_file)
            VALUES(@Id, @P, @K, @Size, @Mtime, @Q, @Taken, @W, @H, @D, @Lat, @Lon, @Cam, @Tff) RETURNING id
            """, new { s.Id, f.P, f.K, f.Size, f.Mtime, f.Q, f.Taken, f.W, f.H, f.D, f.Lat, f.Lon, f.Cam, f.Tff }, tx);
    }

    /// <summary>The files said of a folder (it just came to this computer): placed all at once.</summary>
    public static List<long> PlaceFolder(SqliteConnection c, IDbTransaction tx, string folderUid)
    {
        var placed = new List<long>();
        foreach (var obj in c.Query<string>("SELECT obj FROM fact WHERE field='file' AND json_extract(value, '$.S')=@folderUid",
                     new { folderUid }, tx).ToList())
            if (Place(c, tx, obj[2..]) is { } id) placed.Add(id);
        return placed;
    }

    private static FileFact? FileOf(string? json)
    {
        try
        {
            return json is { Length: > 0 } ? JsonSerializer.Deserialize<FileFact>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The folder's files are known from the others (it is another computer's backup): reading it here would only cost.</summary>
    public static bool KnownFromOthers(Database db, Source source)
    {
        if (source.Kind != SourceKinds.Cloud || db.GetSetting(LibrarySync.SharedSetting) != "1") return false;
        using var c = db.Open();
        var uid = c.ExecuteScalar<string?>("SELECT uid FROM source WHERE id=@Id", new { source.Id });
        return uid != null && FolderDef.FromJson(FactStore.Get(c, null, Keys.Folder(uid), "def")) is { Mode: FolderDef.Backup } def && def.Owner != db.Device.Id;
    }
}
