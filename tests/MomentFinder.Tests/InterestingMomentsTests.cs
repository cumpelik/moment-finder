using Dapper;
using MomentFinder.Library;
using MomentFinder.People;

namespace MomentFinder.Tests;

/// <summary>A named person, big, for a few seconds: a moment worth watching; small, short or nameless: none.</summary>
public class InterestingMomentsTests
{
    [Fact]
    public void BigForAFewSecondsIsAMoment()
    {
        // One look a second: big from 2 s to 6 s, then far away for two looks, then big again only for a single look.
        (int, double)[] path = [(1000, 0.1), (2000, 0.3), (3000, 0.4), (4000, 0.5), (5000, 0.5), (6000, 0.3), (7000, 0.05), (8000, 0.05), (9000, 0.6)];
        var runs = InterestingMoments.Runs(path, 1000, 7).ToList();
        var run = Assert.Single(runs);
        Assert.Equal((1500, 6500), (run.StartMs, run.EndMs));
        Assert.Equal([7L], run.PersonIds);
        // Five seconds, on average at 0.8 of full size.
        Assert.Equal(5 * 0.8, run.Score, 3);
    }

    [Fact]
    public void OneSmallerLookDoesNotBreakAMoment()
    {
        (int, double)[] path = [(0, 0.3), (1000, 0.3), (2000, 0.12), (3000, 0.3), (4000, 0.3)];
        Assert.Equal((0, 4500), InterestingMoments.Runs(path, 1000, 1).Select(r => (r.StartMs, r.EndMs)).Single());
    }

    [Fact]
    public void AGapInThePathSplitsIt()
    {
        (int, double)[] path = [(0, 0.5), (1000, 0.5), (2000, 0.5), (9000, 0.5), (10000, 0.5), (11000, 0.5)];
        Assert.Equal(2, InterestingMoments.Runs(path, 1000, 1).Count());
    }

    [Fact]
    public void TwoPeopleTogetherAreOneMoment()
    {
        var merged = InterestingMoments.Merge([new Moment(2000, 6000, 3, [1]), new Moment(4000, 9000, 4, [2]), new Moment(30000, 33000, 1, [1])], 40000);
        Assert.Equal(2, merged.Count);
        // The best one counts fully, the other one in part: a group is not worth the sum of its people.
        Assert.Equal((1000, 10000), (merged[0].StartMs, merged[0].EndMs));
        Assert.Equal(4 + 0.3 * 3, merged[0].Score, 3);
        Assert.Equal([1L, 2L], merged[0].PersonIds.Order());
    }

    [Fact]
    public void OnlyNamedPeopleCount()
    {
        using var lib = new TestLibrary();
        var source = lib.AddSource();
        var people = new PeopleRepository(lib.Db);
        var eva = people.CreatePerson("Eva");
        using (var c = lib.Db.Open())
        {
            long Video(string name) => c.ExecuteScalar<long>("""
                INSERT INTO media(source_id, rel_path, kind, size, mtime, taken_at, duration_ms) VALUES(@id, @name, 2, 1, 1, 1, 20000) RETURNING id
                """, new { id = source.Id, name });
            void Track(long media, long track, long? person)
            {
                c.Execute("INSERT INTO detection(media_id, time_ms, kind, x, y, w, h, score, quality, track, embedding, person_id) VALUES(@media, 0, 1, 0, 0, 0.2, 0.5, 1, 1, @track, x'00', @person)",
                    new { media, track, person });
                for (var t = 0; t <= 5000; t += 1000)
                    c.Execute("INSERT INTO track_box(media_id, track, time_ms, x, y, w, h) VALUES(@media, @track, @t, 0, 0, 0.2, 0.5)", new { media, track, t });
            }
            Track(Video("named.mp4"), 100, eva.Id);
            Track(Video("nameless.mp4"), 200, null);
        }
        var found = Assert.Single(InterestingMoments.Find(lib.Db));
        Assert.Equal("named.mp4", lib.Repo.Query(new MediaFilter { Ids = [found.MediaId] }).Single().RelPath);
    }

    [Fact]
    public void AVideoInTheLibraryTwiceIsThereOnceFromThisComputer()
    {
        using var lib = new TestLibrary();
        var cloud = lib.Repo.AddSource(SourceKinds.Cloud, Path.Combine(lib.Root, "cache"), "Store", "nowhere:bucket");
        var local = lib.AddSource();
        var eva = new PeopleRepository(lib.Db).CreatePerson("Eva");
        using (var c = lib.Db.Open())
            foreach (var (source, track) in new[] { (cloud.Id, 100L), (local.Id, 200L) })
            {
                var media = c.ExecuteScalar<long>("""
                    INSERT INTO media(source_id, rel_path, kind, size, mtime, quick_hash, taken_at, duration_ms) VALUES(@source, 'ski.mp4', 2, 5, 1, 'abc', 1, 20000) RETURNING id
                    """, new { source });
                c.Execute("INSERT INTO detection(media_id, time_ms, kind, x, y, w, h, score, quality, track, embedding, person_id) VALUES(@media, 0, 1, 0, 0, 0.2, 0.5, 1, 1, @track, x'00', @person)",
                    new { media, track, person = eva.Id });
                for (var t = 0; t <= 5000; t += 1000)
                    c.Execute("INSERT INTO track_box(media_id, track, time_ms, x, y, w, h) VALUES(@media, @track, @t, 0, 0, 0.2, 0.5)", new { media, track, t });
            }
        var found = Assert.Single(InterestingMoments.Find(lib.Db));
        Assert.Equal(local.Id, lib.Repo.Query(new MediaFilter { Ids = [found.MediaId] }).Single().SourceId);
    }
}
