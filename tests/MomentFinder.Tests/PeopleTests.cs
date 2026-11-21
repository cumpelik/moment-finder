using MomentFinder.Analysis;
using MomentFinder.Library;
using MomentFinder.People;
using MomentFinder.Search;

namespace MomentFinder.Tests;

public class PeopleTests
{
    /// <summary>A unit vector near axis "who", tilted by noise on the last axis.</summary>
    private static float[] Emb(int who, float noise = 0.1f, int dim = 16)
    {
        var v = new float[dim];
        v[who] = 1;
        v[dim - 1] = noise;
        var n = MathF.Sqrt(1 + noise * noise);
        for (var i = 0; i < dim; i++) v[i] /= n;
        return v;
    }

    private static NewDetection Face(float[] e, float quality = 0.8f, int? link = null, int time = 0, int? track = null) =>
        new(DetectionKind.Face, time, 0.4f, 0.1f, 0.1f, 0.1f, 0.9f, quality, e, track, link);

    private static NewDetection Body(float[] e, float quality = 0.8f, int time = 0, int? track = null) =>
        new(DetectionKind.Body, time, 0.3f, 0.1f, 0.3f, 0.8f, 0.9f, quality, e, track, null);

    [Fact]
    public async Task NamesSpreadFromFaceToBodyToOutfitAndTrack()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("day/1_face.jpg");
        lib.AddPhoto("day/2_helmet.jpg");
        lib.AddPhoto("day/3_video.jpg");
        lib.AddPhoto("other/4_face.jpg");
        foreach (var (f, t) in new[] { ("day/1_face.jpg", "2024-02-10 10:00"), ("day/2_helmet.jpg", "2024-02-10 11:00"),
                     ("day/3_video.jpg", "2024-02-10 12:00"), ("other/4_face.jpg", "2025-02-10 12:00") })
            File.SetLastWriteTime(Path.Combine(lib.MediaDir, f), DateTime.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        var items = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.FileName, i => i.Id);
        var repo = new PeopleRepository(lib.Db);
        new EventBuilder(lib.Db).Rebuild();

        // Face 0 with its body (jacket A), a helmet photo with only jacket A, a "video" with a track where
        // only the first frame shows the jacket clearly, and a year later the face again.
        var faceIds = repo.ReplaceDetections(items["1_face.jpg"], [Face(Emb(0)), Body(Emb(5))]);
        repo.ReplaceDetections(items["1_face.jpg"], [Face(Emb(0), link: 1), Body(Emb(5))]);
        faceIds = repo.DetectionsOfMedia(items["1_face.jpg"]).Select(d => d.Id).ToList();
        repo.ReplaceDetections(items["2_helmet.jpg"], [Body(Emb(5, 0.2f)), Body(Emb(7))]);
        repo.ReplaceDetections(items["3_video.jpg"], [Body(Emb(5, 0.15f), time: 0, track: 1), Body(Emb(9), quality: 0.3f, time: 1000, track: 1)]);
        repo.ReplaceDetections(items["4_face.jpg"], [Face(Emb(0, 0.3f))]);

        var tomas = repo.CreatePerson("Tomáš");
        repo.Confirm([faceIds[0]], tomas.Id);
        new IdentityService(repo).Recompute();

        Assert.Equal(tomas.Id, repo.DetectionsOfMedia(items["1_face.jpg"]).Single(d => d.Kind == DetectionKind.Body).PersonId);
        var helmet = repo.DetectionsOfMedia(items["2_helmet.jpg"]);
        Assert.Equal(tomas.Id, helmet[0].PersonId);
        Assert.Equal(AssignSource.Outfit, helmet[0].AssignSource);
        Assert.Null(helmet[1].PersonId); // someone else in another jacket
        Assert.All(repo.DetectionsOfMedia(items["3_video.jpg"]), d => Assert.Equal(tomas.Id, d.PersonId));
        // Another year: the face matches, the clothes would not be used.
        Assert.Equal(AssignSource.Face, repo.DetectionsOfMedia(items["4_face.jpg"]).Single().AssignSource);

        // Search by the name finds all four.
        var search = new SearchService(lib.Repo) { People = [new NamedPerson(tomas.Id, "Tomáš")] };
        Assert.Equal(4, search.Search("tomas", new MediaFilter()).Count);

        // "Not Tomáš" on the helmet photo sticks.
        repo.Reject([helmet[0].Id], tomas.Id);
        new IdentityService(repo).Recompute();
        Assert.Null(repo.DetectionsOfMedia(items["2_helmet.jpg"])[0].PersonId);
    }

    [Fact]
    public async Task UnknownFacesFormGroups()
    {
        using var lib = new TestLibrary();
        for (var i = 0; i < 4; i++) lib.AddPhoto($"p{i}.jpg");
        await lib.ScanAsync(lib.AddSource());
        var ids = lib.Repo.Query(new MediaFilter()).Select(i => i.Id).Order().ToList();
        var repo = new PeopleRepository(lib.Db);
        repo.ReplaceDetections(ids[0], [Face(Emb(1))]);
        repo.ReplaceDetections(ids[1], [Face(Emb(1, 0.2f))]);
        repo.ReplaceDetections(ids[2], [Face(Emb(2))]);
        repo.ReplaceDetections(ids[3], [Face(Emb(2, 0.2f)), Face(Emb(3))]);
        new IdentityService(repo).Recompute();
        var groups = repo.UnnamedClusters();
        Assert.Equal(2, groups.Count); // a face seen once is not a group yet
        Assert.All(groups, g => Assert.Equal(2, g.Media));
    }

    /// <summary>A skier falls: lying, the detector is unsure and the look changes, but it is the same track.</summary>
    [Fact]
    public void TrackerFollowsAFall()
    {
        DetectedObject B(int who, float x, float y, float w, float h, bool weak = false, float noise = 0.1f) => new()
        {
            Kind = DetectionKind.Body, Box = new Box(x, y, w, h), Score = weak ? 0.25f : 0.9f, Quality = 0.9f,
            Embedding = Emb(who, noise), Weak = weak,
        };
        var tracks = VideoTracker.Track([
            [B(1, 100, 50, 50, 120)],
            [B(1, 110, 120, 120, 50, weak: true, noise: 1.2f)], // lying, low confidence, looks different
            [],                                                 // not found at all
            [B(1, 130, 60, 50, 120)],                           // up again
            [B(7, 600, 50, 50, 120, weak: true)],               // a weak shape elsewhere starts nobody
        ]);
        Assert.Equal(tracks[0][0], tracks[1][0]);
        Assert.Equal(tracks[0][0], tracks[3][0]);
        Assert.Equal(0, tracks[4][0]);
    }

    /// <summary>Skiers without a face: one card per person and day, not per video track; naming the card names all of it.</summary>
    [Fact]
    public async Task FiguresWithoutFaceAreOfferedAsOnePersonPerDay()
    {
        using var lib = new TestLibrary();
        string[] files = ["v1.jpg", "v2.jpg", "v3.jpg"];
        foreach (var f in files)
            File.SetLastWriteTime(lib.AddPhoto(f), DateTime.Parse("2024-02-10 10:00", System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        var ids = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.FileName, i => i.Id);
        var repo = new PeopleRepository(lib.Db);
        new EventBuilder(lib.Db).Rebuild();
        // Jacket 5 in two tracks of one video and in another video; jacket 7 once; a blurry figure alone.
        repo.ReplaceDetections(ids["v1.jpg"], [Body(Emb(5), time: 0, track: 1), Body(Emb(5, 0.15f), time: 2000, track: 1),
            Body(Emb(5, 0.2f), time: 9000, track: 2), Body(Emb(7), time: 0, track: 3)]);
        repo.ReplaceDetections(ids["v2.jpg"], [Body(Emb(5, 0.1f), time: 4000, track: 1)]);
        repo.ReplaceDetections(ids["v3.jpg"], [Body(Emb(9), quality: 0.1f)]);
        new IdentityService(repo).Recompute();

        var figures = repo.UnnamedFigures(new HashSet<long>());
        Assert.Equal(2, figures.Count);
        Assert.Equal(2, figures[0].Media);
        Assert.Equal(3, figures[0].Tracks);

        var eva = repo.CreatePerson("Eva");
        repo.Confirm(repo.FigureDetectionIds(figures[0].GroupKey), eva.Id);
        new IdentityService(repo).Recompute();
        Assert.Equal(2, repo.ListPersons().Single().MediaCount);
        Assert.Single(repo.UnnamedFigures(new HashSet<long>()));
        Assert.Empty(repo.UnnamedClusters()); // figure groups are not groups of faces
    }

    /// <summary>A new analysis keeps other moments of a track: the name goes with the track passing the same place.</summary>
    [Fact]
    public async Task ConfirmedNameSurvivesReanalysisThroughTheTrackPath()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("v.jpg");
        await lib.ScanAsync(lib.AddSource());
        var id = lib.Repo.Query(new MediaFilter()).Single().Id;
        var repo = new PeopleRepository(lib.Db);
        var first = repo.ReplaceDetections(id, [Body(Emb(5), time: 3000, track: 0)], [new NewTrackBox(0, 3000, 0.3f, 0.1f, 0.3f, 0.8f)]);
        repo.Confirm(first, repo.CreatePerson("Greta").Id);

        // Now the track keeps the frames at 1 s and 6 s; at 3 s only its path is stored.
        repo.ReplaceDetections(id, [Body(Emb(5), time: 1000, track: 0), Body(Emb(5), time: 6000, track: 0)],
            [new NewTrackBox(0, 1000, 0.2f, 0.1f, 0.3f, 0.8f), new NewTrackBox(0, 3000, 0.31f, 0.1f, 0.3f, 0.8f), new NewTrackBox(0, 6000, 0.4f, 0.1f, 0.3f, 0.8f)]);
        var dets = repo.DetectionsOfMedia(id);
        Assert.Single(dets, d => d.Confirmed && d.PersonId != null);
        Assert.Equal(3, repo.TrackPaths(id).Count);
        new IdentityService(repo).Recompute();
        Assert.All(repo.DetectionsOfMedia(id), d => Assert.NotNull(d.PersonId));
    }

    /// <summary>
    /// An older analysis stored video frames half an interval earlier than they really were: a name confirmed then
    /// goes to the new track that is at that place at the real time.
    /// </summary>
    [Fact]
    public async Task ConfirmedNameSurvivesTheFrameTimeFix()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("v.jpg");
        await lib.ScanAsync(lib.AddSource());
        var id = lib.Repo.Query(new MediaFilter()).Single().Id;
        var repo = new PeopleRepository(lib.Db);
        // Old: stored at 2 s, really at 2.5 s, where the skier (moving right 0.1 per second) was at x = 0.25.
        NewDetection At(int time, float x) => new(DetectionKind.Body, time, x, 0.2f, 0.1f, 0.5f, 0.9f, 0.8f, Emb(5), 0, null);
        var old = repo.ReplaceDetections(id, [At(2000, 0.25f)], [new NewTrackBox(0, 2000, 0.25f, 0.2f, 0.1f, 0.5f)]);
        repo.Confirm(old, repo.CreatePerson("Eva").Id);
        // New, at the real times: 2 s at x = 0.2, 3 s at x = 0.3.
        repo.ReplaceDetections(id, [At(2000, 0.2f), At(3000, 0.3f)],
            [new NewTrackBox(0, 2000, 0.2f, 0.2f, 0.1f, 0.5f), new NewTrackBox(0, 3000, 0.3f, 0.2f, 0.1f, 0.5f)], oldTimeShiftMs: 500);
        Assert.Single(repo.DetectionsOfMedia(id), d => d.Confirmed && d.PersonId != null);
    }

    /// <summary>Aggregates over no rows have no column types; every query must still work on a new library.</summary>
    [Fact]
    public void EmptyLibraryQueries()
    {
        using var lib = new TestLibrary();
        var repo = new PeopleRepository(lib.Db);
        Assert.Empty(repo.UnnamedClusters());
        Assert.Empty(repo.Suggestions(10));
        Assert.Equal(0, repo.SuggestionCount());
        Assert.Empty(repo.ListPersons());
        Assert.Empty(repo.FirstAppearance([1]));
        Assert.Empty(repo.Rejections());
        Assert.Empty(repo.LoadAll());
        Assert.Empty(lib.Repo.CountBySource());
        Assert.Equal(0, lib.Repo.Stats(1).Total);
        Assert.Empty(new Buckets.BucketService(lib.Db, lib.Repo).List());
        Assert.Empty(new Cloud.CloudService(lib.Paths, lib.Db, lib.Repo).Mirrors());
        Assert.Equal(0, new EventBuilder(lib.Db).Rebuild());
        new IdentityService(repo).Recompute();
    }

    [Fact]
    public void NamesInTheQuery()
    {
        var people = new[] { new NamedPerson(1, "Tomáš Novák"), new NamedPerson(2, "Eva"), new NamedPerson(3, "Tomáš Dvořák") };
        var q = QueryParser.Parse("tomas novak eva lyže 2023", people);
        Assert.Equal([1L, 2L], q.PersonIds.Order());
        Assert.Equal("lyže", q.Text);
        // "Tomáš" alone is ambiguous: stays text.
        Assert.Empty(QueryParser.Parse("Tomáš", people).PersonIds);
    }

    [Fact]
    public async Task EventsSplitByTimeGap()
    {
        using var lib = new TestLibrary();
        string[] times = ["2024-02-10 09:00", "2024-02-10 12:00", "2024-02-10 23:30", "2024-02-11 08:00"];
        for (var i = 0; i < times.Length; i++)
        {
            var path = lib.AddPhoto($"e{i}.jpg");
            File.SetLastWriteTime(path, DateTime.Parse(times[i], System.Globalization.CultureInfo.InvariantCulture));
        }
        await lib.ScanAsync(lib.AddSource());
        Assert.Equal(3, new EventBuilder(lib.Db).Rebuild());
    }

    [Fact]
    public void TrackerFollowsAppearanceAcrossFrames()
    {
        DetectedObject B(int who, float x) => new()
        {
            Kind = DetectionKind.Body, Box = new Box(x, 10, 50, 120), Score = 0.9f, Quality = 0.9f, Embedding = Emb(who),
        };
        // Two skiers swap sides between frames (fast motion): appearance keeps them apart.
        var tracks = VideoTracker.Track([[B(1, 0), B(2, 300)], [B(2, 0), B(1, 300)], [B(1, 150)]]);
        Assert.Equal(tracks[0][0], tracks[1][1]);
        Assert.Equal(tracks[0][1], tracks[1][0]);
        Assert.Equal(tracks[0][0], tracks[2][0]);
    }
}
