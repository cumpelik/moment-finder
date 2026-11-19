using MediaLens.Library;
using MediaLens.Vectors;

namespace MediaLens.Tests;

public class FlatVectorIndexTests
{
    private static float[] Unit(int dim, int hot)
    {
        var v = new float[dim];
        v[hot] = 1;
        return v;
    }

    private static long[] AddMedia(TestLibrary lib, int count)
    {
        var source = lib.AddSource();
        for (var i = 0; i < count; i++) lib.AddPhoto($"p{i}.jpg");
        lib.ScanAsync(source).GetAwaiter().GetResult();
        return lib.Repo.Query(new MediaFilter()).Select(m => m.Id).Order().ToArray();
    }

    [Fact]
    public void BestRowPerMediaHighestFirst()
    {
        using var lib = new TestLibrary();
        var ids = AddMedia(lib, 3);
        using var index = new FlatVectorIndex(lib.Db, Path.Combine(lib.Paths.VectorDir, "t.vec"), "m1", 4);
        index.Put(ids[0], [new VectorRow(ids[0], 0, Unit(4, 0))]);
        // A video: three frames, the second one matches.
        index.Put(ids[1], [new VectorRow(ids[1], 0, Unit(4, 2)), new VectorRow(ids[1], 5000, Unit(4, 1)), new VectorRow(ids[1], 9000, Unit(4, 3))]);
        index.Put(ids[2], [new VectorRow(ids[2], 0, [0.6f, 0.8f, 0, 0])]);

        var hits = index.Search(Unit(4, 1), null, 10);
        Assert.Equal([ids[1], ids[2], ids[0]], hits.Select(h => h.MediaId));
        Assert.Equal(5000, hits[0].TimeMs);
        Assert.Equal(1f, hits[0].Score, 2);

        var filtered = index.Search(Unit(4, 1), new HashSet<long> { ids[0] }, 10);
        Assert.Equal([ids[0]], filtered.Select(h => h.MediaId));
    }

    [Fact]
    public void SurvivesReopenAndReplacesOnPut()
    {
        using var lib = new TestLibrary();
        var ids = AddMedia(lib, 2);
        var path = Path.Combine(lib.Paths.VectorDir, "t.vec");
        using (var index = new FlatVectorIndex(lib.Db, path, "m1", 4))
        {
            index.Put(ids[0], [new VectorRow(ids[0], 0, Unit(4, 0))]);
            index.Put(ids[1], [new VectorRow(ids[1], 0, Unit(4, 1))]);
            index.Put(ids[0], [new VectorRow(ids[0], 0, Unit(4, 2))]);
            index.RemoveMedia(ids[1]);
        }
        using (var index = new FlatVectorIndex(lib.Db, path, "m1", 4))
        {
            Assert.Equal(1, index.Count);
            var hit = Assert.Single(index.Search(Unit(4, 2), null, 10));
            Assert.Equal(ids[0], hit.MediaId);
            Assert.Equal(1f, hit.Score, 2);
        }
    }

    [Fact]
    public void AnotherModelStartsOver()
    {
        using var lib = new TestLibrary();
        var ids = AddMedia(lib, 1);
        var path = Path.Combine(lib.Paths.VectorDir, "t.vec");
        using (var index = new FlatVectorIndex(lib.Db, path, "m1", 4))
            index.Put(ids[0], [new VectorRow(ids[0], 0, Unit(4, 0))]);
        var reset = false;
        using (var index = new FlatVectorIndex(lib.Db, path, "m2", 4, () => reset = true))
        {
            Assert.True(reset);
            Assert.Equal(0, index.Count);
        }
    }
}
