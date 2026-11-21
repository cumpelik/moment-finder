using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using MomentFinder.Analysis;
using MomentFinder.Cloud;
using MomentFinder.Library;

namespace MomentFinder.Sync;

/// <summary>
/// Brings the library's tables in line with its facts after changes from another computer came in: people, who is
/// who, buckets, folders in the cloud. It writes the tables only, never new facts (those came from elsewhere).
/// What cannot be placed yet (a photo not analyzed or not seen yet) waits as a fact and is placed when it comes.
/// </summary>
public sealed class FactApplier
{
    private readonly SqliteConnection _c;
    private readonly IDbTransaction _tx;
    private readonly DeviceInfo _me;
    private readonly string _cacheDir;

    public FactApplier(SqliteConnection c, IDbTransaction tx, DeviceInfo me, string cacheDir)
    {
        _c = c;
        _tx = tx;
        _me = me;
        _cacheDir = cacheDir;
    }

    /// <summary>Something that changed which the application shows: a folder came or went (the scan follows).</summary>
    public bool FoldersChanged { get; private set; }

    /// <summary>Places the facts of these objects, in an order that has what they refer to first.</summary>
    public void Apply(IEnumerable<string> objects)
    {
        var all = objects.Distinct().ToList();
        static int Order(string o) => o.Split(':')[0] switch { "lib" => 0, "f" => 1, "p" => 2, "b" => 3, "m" => 4, "bi" => 5, _ => 6 };
        var media = new HashSet<string>();
        foreach (var obj in all.OrderBy(Order))
        {
            var kind = obj.Split(':')[0];
            switch (kind)
            {
                case "lib": Library(); break;
                case "f": Folder(obj[2..]); break;
                case "p": Person(obj[2..]); break;
                case "b": Bucket(obj[2..]); break;
                case "m": Moved(obj[2..]); break;
                case "bi": BucketItem(obj); break;
                case "d" when Keys.ParseDetection(obj) is { } d: media.Add(d.MediaUid); break;
            }
        }
        foreach (var uid in media)
            if (MediaId(uid) is { } id) PeopleOf(_c, _tx, id);
    }

    private Dictionary<string, string?> Fields(string obj) =>
        _c.Query<(string Field, string? Value)>("SELECT field, value FROM fact WHERE obj=@obj", new { obj }, _tx).ToDictionary(r => r.Field, r => r.Value);

    private void Library()
    {
        if (FactStore.Get(_c, _tx, Keys.Library, "read_only_password") is { } password)
            _c.Execute("INSERT INTO setting(key, value) VALUES('read_only_password', @password) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                new { password }, _tx);
    }

    // ---- folders ----

    private void Folder(string uid)
    {
        var f = Fields(Keys.Folder(uid));
        var def = FolderDef.FromJson(f.GetValueOrDefault("def"));
        var local = _c.QueryFirstOrDefault<(long Id, string Kind)?>("SELECT id, kind FROM source WHERE uid=@uid", new { uid }, _tx);
        if (f.GetValueOrDefault("deleted") == "1")
        {
            if (local is { } gone)
            {
                _c.Execute("DELETE FROM source WHERE id=@Id", new { gone.Id }, _tx);
                FoldersChanged = true;
            }
            return;
        }
        if (def == null) return;
        var isProtected = f.GetValueOrDefault("protected") == "1";
        if (local is { } here)
        {
            _c.Execute("UPDATE source SET protected=@isProtected WHERE id=@Id", new { isProtected, here.Id }, _tx);
            // How this computer reaches a folder that is not its own follows the folder (cloned, taken home elsewhere).
            if (here.Kind == SourceKinds.Cloud && def.Owner != _me.Id)
                _c.Execute("""
                    UPDATE source SET name=@Name, remote=@Remote, read_only=@readOnly, home_name=@OwnerName, owner=@Owner WHERE id=@Id
                    """, new { def.Name, def.Remote, readOnly = def.Mode == FolderDef.Backup, def.OwnerName, def.Owner, here.Id }, _tx);
            return;
        }
        // This computer's own folder is never made from another's word.
        if (def.Owner == _me.Id && def.Mode == FolderDef.Backup) return;
        // The storage it is in: its remotes all start with the storage's own name ("ml" and 8 characters).
        var prefix = def.Remote[..Math.Min(10, def.Remote.Length)];
        var cloudId = _c.Query<(long Id, string Remote)>("SELECT id, remote FROM cloud", transaction: _tx)
            .Where(cl => cl.Remote.StartsWith(prefix, StringComparison.Ordinal)).Select(cl => (long?)cl.Id).FirstOrDefault();
        var id = _c.ExecuteScalar<long>("""
            INSERT INTO source(kind, root, name, added_at, remote, cloud_id, read_only, home_name, owner, protected, uid)
            VALUES(@kind, '', @Name, @now, @Remote, @cloudId, @readOnly, @OwnerName, @Owner, @isProtected, @uid) RETURNING id
            """, new
        {
            kind = SourceKinds.Cloud, def.Name, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), def.Remote, cloudId,
            readOnly = def.Mode == FolderDef.Backup, def.OwnerName, def.Owner, isProtected, uid,
        }, _tx);
        var root = Path.Combine(_cacheDir, "cloud", id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        _c.Execute("UPDATE source SET root=@root WHERE id=@id", new { root, id }, _tx);
        FoldersChanged = true;
    }

    // ---- people ----

    /// <summary>The person a key stands for now (merged into another, perhaps more than once).</summary>
    private string Resolve(string personUid)
    {
        for (var i = 0; i < 20; i++)
        {
            var into = FactStore.Get(_c, _tx, Keys.Person(personUid), "into");
            if (into == null) return personUid;
            personUid = into;
        }
        return personUid;
    }

    private long? PersonId(string uid) => _c.ExecuteScalar<long?>("SELECT id FROM person WHERE uid=@uid", new { uid }, _tx);

    private void Person(string uid)
    {
        var f = Fields(Keys.Person(uid));
        var local = PersonId(uid);
        if (f.GetValueOrDefault("into") is { } intoRaw)
        {
            var into = Resolve(intoRaw);
            _c.Execute("INSERT OR REPLACE INTO person_alias(from_uid, into_uid) VALUES(@uid, @into)", new { uid, into }, _tx);
            if (local is not { } from) return;
            Person(into);
            if (PersonId(into) is { } target)
            {
                _c.Execute("UPDATE detection SET person_id=@target WHERE person_id=@from", new { from, target }, _tx);
                _c.Execute("UPDATE detection SET suggest_person_id=NULL, suggest_score=NULL WHERE suggest_person_id=@from", new { from }, _tx);
                _c.Execute("INSERT OR IGNORE INTO rejection(detection_id, person_id) SELECT detection_id, @target FROM rejection WHERE person_id=@from",
                    new { from, target }, _tx);
            }
            _c.Execute("DELETE FROM person WHERE id=@from", new { from }, _tx);
            return;
        }
        if (f.GetValueOrDefault("deleted") == "1")
        {
            if (local is { } gone)
            {
                _c.Execute("UPDATE detection SET person_id=NULL, assign_source=NULL, confidence=NULL, confirmed=0 WHERE person_id=@gone", new { gone }, _tx);
                _c.Execute("DELETE FROM person WHERE id=@gone", new { gone }, _tx);
            }
            return;
        }
        if (f.GetValueOrDefault("name") is not { } name) return;
        var hidden = f.GetValueOrDefault("hidden") == "1";
        var id = local ?? _c.ExecuteScalar<long>("INSERT INTO person(name, created_at, hidden, uid) VALUES(@name, @now, @hidden, @uid) RETURNING id",
            new { name, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hidden, uid }, _tx);
        _c.Execute("UPDATE person SET name=@name, hidden=@hidden WHERE id=@id", new { name, hidden, id }, _tx);
        if (f.GetValueOrDefault("cover") is { } cover && Keys.ParseDetection(cover) is { } d && MediaId(d.MediaUid) is { } media &&
            Match(_c, _tx, media, d.Kind, d.TimeMs, new Box(d.X, d.Y, d.W, d.H)) is { } detection)
            _c.Execute("UPDATE person SET cover_detection_id=@detection WHERE id=@id", new { detection, id }, _tx);
    }

    // ---- who is who ----

    /// <summary>The photo a key stands for here (moved since, perhaps).</summary>
    private long? MediaId(string uid) => MediaId(_c, _tx, uid);

    internal static long? MediaId(SqliteConnection c, IDbTransaction? tx, string uid)
    {
        for (var i = 0; i < 20; i++)
        {
            if (c.ExecuteScalar<long?>("SELECT id FROM media WHERE uid=@uid", new { uid }, tx) is { } id) return id;
            if (FactStore.Get(c, tx, Keys.Media(uid), "moved") is not { } moved) return null;
            uid = moved;
        }
        return null;
    }

    private static long? Match(SqliteConnection c, IDbTransaction? tx, long mediaId, int kind, long timeMs, Box box) =>
        c.Query<(long Id, double X, double Y, double W, double H)>(
                "SELECT id, x, y, w, h FROM detection WHERE media_id=@mediaId AND kind=@kind AND time_ms=@timeMs", new { mediaId, kind, timeMs }, tx)
            .Select(d => (d.Id, Iou: box.Iou(new Box((float)d.X, (float)d.Y, (float)d.W, (float)d.H))))
            .Where(d => d.Iou > 0.5f).OrderByDescending(d => d.Iou).Select(d => (long?)d.Id).FirstOrDefault();

    /// <summary>
    /// Who is (not) on each face and figure of a photo, by what anybody said of it: for each person the latest word
    /// counts, and of the persons it is "yes" for, the latest. A detection nobody said anything of is left alone (its
    /// confirmation may have been carried over by a track or the look, see ReplaceDetections).
    /// </summary>
    public static void PeopleOf(SqliteConnection c, IDbTransaction? tx, long mediaId)
    {
        var uid = c.ExecuteScalar<string?>("SELECT uid FROM media WHERE id=@mediaId", new { mediaId }, tx);
        if (uid == null) return;
        // Under its old keys too, from before it was moved.
        var uids = new List<string> { uid };
        uids.AddRange(c.Query<string>("SELECT substr(obj, 3) FROM fact WHERE field='moved' AND value=@uid AND obj LIKE 'm:%'", new { uid }, tx));
        var said = new List<(string Key, string Person, string Verdict, string Hlc)>();
        foreach (var u in uids)
            said.AddRange(c.Query<(string, string, string, string)>(
                "SELECT obj, field, value, hlc FROM fact WHERE obj >= @from AND obj < @to AND value IS NOT NULL",
                new { from = $"d:{u}:", to = $"d:{u};" }, tx));
        if (said.Count == 0) return;
        var detections = c.Query<(long Id, int Kind, long TimeMs, double X, double Y, double W, double H, long? PersonId, bool Confirmed)>(
            "SELECT id, kind, time_ms, x, y, w, h, person_id, confirmed FROM detection WHERE media_id=@mediaId", new { mediaId }, tx).ToList();
        var persons = c.Query<(string Uid, long Id)>("SELECT uid, id FROM person WHERE uid IS NOT NULL", transaction: tx).ToDictionary(p => p.Uid, p => p.Id);
        var aliases = c.Query<(string From, string Into)>("SELECT from_uid, into_uid FROM person_alias", transaction: tx).ToDictionary(a => a.From, a => a.Into);
        string Final(string p)
        {
            for (var i = 0; i < 20 && aliases.TryGetValue(p, out var next); i++) p = next;
            return p;
        }
        foreach (var d in detections)
        {
            var box = new Box((float)d.X, (float)d.Y, (float)d.W, (float)d.H);
            var mine = said.Where(s => Keys.ParseDetection(s.Key) is { } k && k.Kind == d.Kind && k.TimeMs == d.TimeMs &&
                                       box.Iou(new Box(k.X, k.Y, k.W, k.H)) > 0.5f).ToList();
            if (mine.Count == 0) continue;
            var latest = mine.GroupBy(s => Final(s.Person))
                .Select(g => g.MaxBy(s => s.Hlc, StringComparer.Ordinal) with { Person = g.Key })
                .Where(s => persons.ContainsKey(s.Person)).ToList();
            var yes = latest.Where(s => s.Verdict == PersonFacts.Yes).OrderByDescending(s => s.Hlc, StringComparer.Ordinal).FirstOrDefault();
            if (yes.Person != null)
                c.Execute("""
                    UPDATE detection SET person_id=@person, assign_source=1, confidence=1, confirmed=1, suggest_person_id=NULL, suggest_score=NULL
                    WHERE id=@Id
                    """, new { person = persons[yes.Person], d.Id }, tx);
            else if (d.Confirmed)
                c.Execute("UPDATE detection SET person_id=NULL, assign_source=NULL, confidence=NULL, confirmed=0 WHERE id=@Id", new { d.Id }, tx);
            c.Execute("DELETE FROM rejection WHERE detection_id=@Id", new { d.Id }, tx);
            foreach (var no in latest.Where(s => s.Verdict == PersonFacts.No))
                c.Execute("INSERT OR IGNORE INTO rejection(detection_id, person_id) VALUES(@Id, @person)", new { d.Id, person = persons[no.Person] }, tx);
        }
    }

    // ---- buckets ----

    private long? BucketId(string uid) => _c.ExecuteScalar<long?>("SELECT id FROM bucket WHERE uid=@uid", new { uid }, _tx);

    private void Bucket(string uid)
    {
        var f = Fields(Keys.Bucket(uid));
        var local = BucketId(uid);
        if (f.GetValueOrDefault("deleted") == "1")
        {
            if (local is { } gone) _c.Execute("DELETE FROM bucket WHERE id=@gone", new { gone }, _tx);
            return;
        }
        if (f.GetValueOrDefault("name") is not { } name) return;
        if (local is { } id)
        {
            _c.Execute("UPDATE bucket SET name=@name WHERE id=@id", new { name = FreeName(name, id), id }, _tx);
            return;
        }
        _c.Execute("INSERT INTO bucket(name, created_at, uid) VALUES(@name, @now, @uid)",
            new { name = FreeName(name, null), now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), uid }, _tx);
        // Its items that came before it.
        foreach (var item in _c.Query<string>("SELECT DISTINCT obj FROM fact WHERE obj LIKE @prefix", new { prefix = $"bi:{uid}:%" }, _tx).ToList())
            BucketItem(item);
    }

    /// <summary>Two computers named a bucket alike: names are unique, the second gets " 2".</summary>
    private string FreeName(string name, long? self)
    {
        var candidate = name;
        for (var n = 2; _c.ExecuteScalar<long?>("SELECT id FROM bucket WHERE name=@candidate AND id<>@self", new { candidate, self = self ?? -1 }, _tx) != null; n++)
            candidate = $"{name} {n}";
        return candidate;
    }

    private void BucketItem(string obj)
    {
        var parts = obj.Split(':');
        if (parts.Length != 3 || BucketId(parts[1]) is not { } bucket || MediaId(parts[2]) is not { } media) return;
        var f = Fields(obj);
        if (f.GetValueOrDefault("in") != "1")
        {
            _c.Execute("DELETE FROM bucket_item WHERE bucket_id=@bucket AND media_id=@media", new { bucket, media }, _tx);
            return;
        }
        var position = double.TryParse(f.GetValueOrDefault("pos"), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
        _c.Execute("""
            INSERT INTO bucket_item(bucket_id, media_id, position, added_at) VALUES(@bucket, @media, @position, @now)
            ON CONFLICT(bucket_id, media_id) DO UPDATE SET position=excluded.position
            """, new { bucket, media, position, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, _tx);
    }

    // ---- photos ----

    /// <summary>A photo moved: what was said of it under the old key (buckets, who is who) follows it.</summary>
    private void Moved(string uid)
    {
        if (FactStore.Get(_c, _tx, Keys.Media(uid), "moved") is not { } now || MediaId(now) is not { } media) return;
        foreach (var item in _c.Query<string>("SELECT DISTINCT obj FROM fact WHERE obj LIKE @pattern", new { pattern = $"bi:%:{uid}" }, _tx).ToList())
        {
            var parts = item.Split(':');
            var moved = Keys.BucketItem(parts[1], now);
            foreach (var (field, value) in Fields(item))
                _c.Execute("INSERT OR IGNORE INTO fact(obj, field, value, hlc) SELECT @moved, field, value, hlc FROM fact WHERE obj=@item AND field=@field",
                    new { moved, item, field }, _tx);
            BucketItem(moved);
        }
        PeopleOf(_c, _tx, media);
    }

    /// <summary>The photos whose facts wait for them (just added to the library: by a scan, from another computer).</summary>
    public static void Arrived(SqliteConnection c, IDbTransaction? tx, IEnumerable<long> mediaIds)
    {
        foreach (var id in mediaIds)
        {
            if (c.ExecuteScalar<string?>("SELECT uid FROM media WHERE id=@id", new { id }, tx) is not { } uid) continue;
            foreach (var item in c.Query<(string Obj, string? Value, string? Pos)>("""
                         SELECT i.obj, i.value, (SELECT value FROM fact p WHERE p.obj=i.obj AND p.field='pos')
                         FROM fact i WHERE i.field='in' AND i.obj LIKE @pattern
                         """, new { pattern = $"bi:%:{uid}" }, tx).ToList())
            {
                var bucket = c.ExecuteScalar<long?>("SELECT id FROM bucket WHERE uid=@b", new { b = item.Obj.Split(':')[1] }, tx);
                if (bucket == null || item.Value != "1") continue;
                var position = double.TryParse(item.Pos, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
                c.Execute("INSERT OR IGNORE INTO bucket_item(bucket_id, media_id, position, added_at) VALUES(@bucket, @id, @position, @now)",
                    new { bucket, id, position, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, tx);
            }
            PeopleOf(c, tx, id);
        }
    }
}
