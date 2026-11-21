using System.Globalization;
using Dapper;
using MomentFinder.Data;

namespace MomentFinder.Sync;

/// <summary>
/// A library from before sharing gets its facts once: the names, who is who as the user said it, the buckets and
/// the folders in the cloud as they are. They carry the oldest time, so any change made anywhere later wins.
/// </summary>
public static class FactSeed
{
    private const string Done = "facts_seeded";

    public static void Run(Database db)
    {
        if (db.GetSetting(Done) == "1") return;
        var facts = db.Facts;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var p in c.Query<(string Uid, string Name, bool Hidden, long? Cover)>(
                     "SELECT uid, name, hidden, cover_detection_id FROM person", transaction: tx).ToList())
        {
            var obj = Keys.Person(p.Uid);
            facts.Seed(c, tx, obj, "name", p.Name);
            if (p.Hidden) facts.Seed(c, tx, obj, "hidden", "1");
            if (p.Cover is { } cover && PersonFacts.DetectionKeys(c, tx, [cover]) is [var key]) facts.Seed(c, tx, obj, "cover", key.Key);
        }
        var said = c.Query<(long Detection, string Person, string Verdict)>("""
            SELECT d.id, p.uid, 'yes' FROM detection d JOIN person p ON p.id=d.person_id WHERE d.confirmed=1
            UNION ALL
            SELECT r.detection_id, p.uid, 'no' FROM rejection r JOIN person p ON p.id=r.person_id
            """, transaction: tx).ToList();
        var keys = PersonFacts.DetectionKeys(c, tx, said.Select(s => s.Detection)).ToDictionary(k => k.Id, k => k.Key);
        foreach (var s in said)
            if (keys.TryGetValue(s.Detection, out var key)) facts.Seed(c, tx, key, s.Person, s.Verdict);
        foreach (var b in c.Query<(long Id, string Uid, string Name)>("SELECT id, uid, name FROM bucket", transaction: tx).ToList())
        {
            facts.Seed(c, tx, Keys.Bucket(b.Uid), "name", b.Name);
            foreach (var i in c.Query<(string Media, double Position)>(
                         "SELECT m.uid, i.position FROM bucket_item i JOIN media m ON m.id=i.media_id WHERE i.bucket_id=@Id", new { b.Id }, tx))
            {
                var item = Keys.BucketItem(b.Uid, i.Media);
                facts.Seed(c, tx, item, "in", "1");
                facts.Seed(c, tx, item, "pos", i.Position.ToString("R", CultureInfo.InvariantCulture));
            }
        }
        foreach (var s in c.Query<(long Id, string Uid, bool Protected)>("SELECT id, uid, protected FROM source", transaction: tx).ToList())
        {
            if (SharedFolders.Def(c, tx, s.Id, db.Device) is not { } def) continue;
            facts.Seed(c, tx, Keys.Folder(s.Uid), "def", def.ToJson());
            if (s.Protected) facts.Seed(c, tx, Keys.Folder(s.Uid), "protected", "1");
        }
        if (c.ExecuteScalar<string?>("SELECT value FROM setting WHERE key='read_only_password'", transaction: tx) is { } password)
            facts.Seed(c, tx, Keys.Library, "read_only_password", password);
        c.Execute("INSERT INTO setting(key, value) VALUES(@Done, '1') ON CONFLICT(key) DO UPDATE SET value='1'", new { Done }, tx);
        tx.Commit();
    }
}
