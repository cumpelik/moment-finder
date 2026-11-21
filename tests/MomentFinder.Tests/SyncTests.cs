using Dapper;
using MomentFinder.Buckets;
using MomentFinder.Library;
using MomentFinder.People;
using MomentFinder.Sync;
using Xunit;

namespace MomentFinder.Tests;

/// <summary>One library on several computers: what the people decide is kept as facts under keys that mean the same everywhere.</summary>
public class SyncTests
{
    private static List<Change> Outbox(TestLibrary lib)
    {
        using var c = lib.Db.Open();
        return c.Query<Change>("SELECT obj AS Obj, field AS Field, value AS Value, hlc AS Hlc FROM outbox ORDER BY seq").ToList();
    }

    private static string? Fact(TestLibrary lib, string obj, string field)
    {
        using var c = lib.Db.Open();
        return FactStore.Get(c, null, obj, field);
    }

    private static string Uid(TestLibrary lib, string table, long id)
    {
        using var c = lib.Db.Open();
        return c.ExecuteScalar<string>($"SELECT uid FROM {table} WHERE id=@id", new { id })!;
    }

    [Fact]
    public async Task EveryDecisionIsAFactWithASharedKey()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("2024/a.jpg");
        lib.AddPhoto("2024/b.jpg");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var items = lib.Repo.Query(new MediaFilter()).OrderBy(i => i.RelPath).ToList();
        // A photo's key is made of its folder and its place: another computer reading the folder finds the same.
        Assert.Equal(Keys.MediaUid(Uid(lib, "source", source.Id), "2024/a.jpg"), Uid(lib, "media", items[0].Id));

        var people = new PeopleRepository(lib.Db);
        var face = people.ReplaceDetections(items[0].Id,
            [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.2f, 0.3f, 0.4f, 0.9f, 0.9f, new float[16], null, null)])[0];
        var eva = people.CreatePerson("Eva");
        var evaKey = Keys.Person(Uid(lib, "person", eva.Id));
        Assert.Equal("Eva", Fact(lib, evaKey, "name"));
        var detection = Keys.Detection(Uid(lib, "media", items[0].Id), (int)Analysis.DetectionKind.Face, 0, 0.1, 0.2, 0.3, 0.4);

        people.Confirm([face], eva.Id);
        Assert.Equal(PersonFacts.Yes, Fact(lib, detection, Uid(lib, "person", eva.Id)));
        people.Reject([face], eva.Id);
        Assert.Equal(PersonFacts.No, Fact(lib, detection, Uid(lib, "person", eva.Id)));
        people.RenamePerson(eva.Id, "Eva Nová");
        Assert.Equal("Eva Nová", Fact(lib, evaKey, "name"));

        var buckets = new BucketService(lib.Db, lib.Repo);
        var book = buckets.Create("Book");
        var bookKey = Keys.Bucket(Uid(lib, "bucket", book.Id));
        buckets.Add(book.Id, [items[0].Id, items[1].Id]);
        var itemB = Keys.BucketItem(Uid(lib, "bucket", book.Id), Uid(lib, "media", items[1].Id));
        Assert.Equal(("1", "2"), (Fact(lib, itemB, "in"), Fact(lib, itemB, "pos")));
        buckets.Reorder(book.Id, [items[1].Id, items[0].Id]);
        Assert.Equal("1", Fact(lib, itemB, "pos"));
        buckets.Remove(book.Id, [items[1].Id]);
        Assert.Equal("0", Fact(lib, itemB, "in"));
        buckets.Delete(book.Id);
        Assert.Equal("1", Fact(lib, bookKey, "deleted"));

        // Everything waits to be sent, in order, each with a later time than the one before.
        var sent = Outbox(lib);
        Assert.True(sent.Count >= 12);
        Assert.Equal(sent.Select(s => s.Hlc).Order(StringComparer.Ordinal), sent.Select(s => s.Hlc));
        // A folder only on this disk is nobody else's business.
        Assert.DoesNotContain(sent, s => s.Obj.StartsWith("f:", StringComparison.Ordinal));
    }

    [Fact]
    public void MergedPersonIsFoundUnderTheNewKey()
    {
        using var lib = new TestLibrary();
        var people = new PeopleRepository(lib.Db);
        var eva = people.CreatePerson("Eva");
        var evicka = people.CreatePerson("Evička");
        var from = Uid(lib, "person", evicka.Id);
        people.MergePersons(evicka.Id, eva.Id);
        Assert.Equal(Uid(lib, "person", eva.Id), Fact(lib, Keys.Person(from), "into"));
        using var c = lib.Db.Open();
        Assert.Equal(Uid(lib, "person", eva.Id), c.ExecuteScalar<string>("SELECT into_uid FROM person_alias WHERE from_uid=@from", new { from }));
    }

    [Fact]
    public async Task ALibraryFromBeforeSharingKeepsWhatWasDecided()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("a.jpg");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var item = lib.Repo.Query(new MediaFilter()).Single();
        var people = new PeopleRepository(lib.Db);
        var face = people.ReplaceDetections(item.Id,
            [new NewDetection(Analysis.DetectionKind.Face, 0, 0.1f, 0.2f, 0.3f, 0.4f, 0.9f, 0.9f, new float[16], null, null)])[0];
        var eva = people.CreatePerson("Eva");
        people.Confirm([face], eva.Id);
        // As if the library came from a version before sharing: no facts yet.
        using (var c = lib.Db.Open())
            c.Execute("DELETE FROM fact; DELETE FROM outbox; DELETE FROM setting WHERE key='facts_seeded';");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var reopened = new Data.Database(lib.Paths.DbPath);
        using var r = reopened.Open();
        var evaUid = r.ExecuteScalar<string>("SELECT uid FROM person WHERE id=@Id", new { eva.Id })!;
        Assert.Equal("Eva", FactStore.Get(r, null, Keys.Person(evaUid), "name"));
        var detection = Keys.Detection(r.ExecuteScalar<string>("SELECT uid FROM media")!, (int)Analysis.DetectionKind.Face, 0, 0.1, 0.2, 0.3, 0.4);
        Assert.Equal(PersonFacts.Yes, FactStore.Get(r, null, detection, evaUid));
        // The oldest time: whatever anybody changes later wins; and nothing of it is sent (the first snapshot carries it).
        Assert.StartsWith("000000000000.0000.", r.ExecuteScalar<string>("SELECT MIN(hlc) FROM fact"));
        Assert.Equal(0, r.ExecuteScalar<long>("SELECT COUNT(*) FROM outbox"));
    }
}
