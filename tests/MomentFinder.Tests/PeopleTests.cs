using Dapper;
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

    /// <summary>
    /// A mother holding her child, both in pink stripes: each face goes with its own body (the child's face is
    /// in the mother's box too, but low in it), and the child's body is not taken for the mother by the clothes.
    /// </summary>
    [Fact]
    public async Task MotherAndChildInOneShirtAreTwoPeople()
    {
        var motherBody = new NewDetection(DetectionKind.Body, 0, 0f, 0.03f, 0.79f, 0.97f, 0.9f, 0.8f, Emb(5, 0.15f), null, null);
        var childBody = new NewDetection(DetectionKind.Body, 0, 0.2f, 0.45f, 0.755f, 0.55f, 0.9f, 0.8f, Emb(5, 0.2f), null, null);
        var motherFace = new NewDetection(DetectionKind.Face, 0, 0.25f, 0.14f, 0.45f, 0.36f, 0.9f, 0.8f, Emb(0, 0.2f), null, null);
        var childFace = new NewDetection(DetectionKind.Face, 0, 0.465f, 0.545f, 0.365f, 0.36f, 0.9f, 0.8f, Emb(3), null, null);
        Assert.Equal(new Dictionary<int, int> { [2] = 0, [3] = 1 },
            FaceLinks.Link(new[] { motherBody, childBody, motherFace, childFace }.Select(d => (d.Kind, new Box(d.X, d.Y, d.W, d.H))).ToList()));

        // A man behind a woman leaning into the picture: her face is in his box too (high and to the side), his
        // box is the smaller one; each face still goes with its own body.
        Assert.Equal(new Dictionary<int, int> { [2] = 0, [3] = 1 }, FaceLinks.Link(new (DetectionKind, Box)[]
        {
            (DetectionKind.Body, new Box(97, 72, 800, 998)), (DetectionKind.Body, new Box(693, 7, 752, 1063)),
            (DetectionKind.Face, new Box(579, 120, 109, 142)), (DetectionKind.Face, new Box(740, 60, 260, 270)),
        }));

        using var lib = new TestLibrary();
        foreach (var (f, t) in new[] { ("a.jpg", "2024-06-01 10:00"), ("b.jpg", "2024-06-01 11:00") })
            File.SetLastWriteTime(lib.AddPhoto(f), DateTime.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        var items = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.FileName, i => i.Id);
        new EventBuilder(lib.Db).Rebuild();
        var repo = new PeopleRepository(lib.Db);
        repo.ReplaceDetections(items["a.jpg"], [Face(Emb(0), link: 1), Body(Emb(5))]);
        // Analyzed before the better pairing: no links at all.
        repo.ReplaceDetections(items["b.jpg"], [motherBody, childBody, motherFace, childFace]);
        var vendy = repo.CreatePerson("Vendy");
        repo.Confirm(repo.DetectionsOfMedia(items["a.jpg"]).Select(d => d.Id), vendy.Id);
        new IdentityService(repo).Recompute();

        var b = repo.DetectionsOfMedia(items["b.jpg"]);
        var (mother, child) = (b.Single(d => d.Kind == DetectionKind.Body && d.X == 0), b.Single(d => d.Kind == DetectionKind.Body && d.X > 0));
        Assert.Equal(mother.Id, b.Single(d => d.Kind == DetectionKind.Face && d.Y < 0.3f).LinkedId);
        Assert.Equal(child.Id, b.Single(d => d.Kind == DetectionKind.Face && d.Y > 0.3f).LinkedId);
        Assert.Equal(vendy.Id, mother.PersonId);
        Assert.Null(child.PersonId);
        Assert.NotEqual(vendy.Id, child.SuggestPersonId);
    }

    /// <summary>A unit vector made of the given axes: its similarity to a plain axis is that axis' weight.</summary>
    private static float[] Mix(params (int Axis, float Weight)[] parts)
    {
        var v = new float[16];
        foreach (var (axis, weight) in parts) v[axis] = weight;
        var n = MathF.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < v.Length; i++) v[i] /= n;
        return v;
    }

    /// <summary>
    /// Twenty alike photos of Greta from one afternoon, all taken for her brother (he has more named pictures, and
    /// her own from other years are less alike). Naming her in one of them names the others: the box of a photo is
    /// the figure, the face on it is hers with it, and a face named on that day decides. The brother, there on the
    /// same day, stays himself.
    /// </summary>
    [Fact]
    public async Task NamingOneOfASeriesNamesTheSeries()
    {
        using var lib = new TestLibrary();
        var times = new Dictionary<string, string>();
        for (var i = 0; i < 4; i++) times[$"david{i}.jpg"] = $"2023-0{i + 1}-10 10:00";
        for (var i = 0; i < 3; i++) times[$"greta_old{i}.jpg"] = $"2022-0{i + 1}-10 10:00";
        for (var i = 0; i < 5; i++) times[$"series/s{i}.jpg"] = $"2024-06-01 10:0{i}";
        times["series/david_too.jpg"] = "2024-06-01 10:30";
        times["elsewhere/alike.jpg"] = "2024-09-01 10:00";
        foreach (var (f, t) in times)
            File.SetLastWriteTime(lib.AddPhoto(f), DateTime.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        var items = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.FileName, i => i.Id);
        new EventBuilder(lib.Db).Rebuild();
        var repo = new PeopleRepository(lib.Db);
        var david = repo.CreatePerson("David");
        var greta = repo.CreatePerson("Greta");
        for (var i = 0; i < 4; i++) repo.Confirm(repo.ReplaceDetections(items[$"david{i}.jpg"], [Face(Mix((1, 1)))]), david.Id);
        for (var i = 0; i < 3; i++) repo.Confirm(repo.ReplaceDetections(items[$"greta_old{i}.jpg"], [Face(Mix((2, 1)))]), greta.Id);
        // Today she looks more like the pictures of her brother (0.66) than like her own older ones (0.56).
        float[] Today(int i) => Mix((1, 0.66f), (2, 0.56f), (3, 0.5f), (6 + i, 0.1f));
        var series = Enumerable.Range(0, 5)
            .Select(i => repo.ReplaceDetections(items[$"s{i}.jpg"], [Face(Today(i), link: 1), Body(Emb(5))])).ToList();
        repo.ReplaceDetections(items["david_too.jpg"], [Face(Mix((1, 1), (12, 0.1f)))]);
        repo.ReplaceDetections(items["alike.jpg"], [Face(Today(7))]);
        var identity = new IdentityService(repo) { AnswerDelay = TimeSpan.Zero };
        identity.Recompute();
        Assert.All(series, s => Assert.All(repo.DetectionsOfMedia(items[$"s{series.IndexOf(s)}.jpg"]), d => Assert.Equal(david.Id, d.PersonId)));

        var answered = new TaskCompletionSource<IdentityService.Reach?>();
        identity.Answered += reach => answered.TrySetResult(reach);
        repo.Confirm([series[0][1]], greta.Id);
        identity.RecomputeSoon();
        var reach = await answered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(reach);

        for (var i = 0; i < 5; i++)
            Assert.All(repo.DetectionsOfMedia(items[$"s{i}.jpg"]), d => Assert.Equal(greta.Id, d.PersonId));
        // Her brother on the same day is closer to his own pictures of other days than to her.
        Assert.Equal(david.Id, repo.DetectionsOfMedia(items["david_too.jpg"]).Single().PersonId);
        // Months later, the same look: the series stands for her now, so she is found there too.
        Assert.Equal(greta.Id, repo.DetectionsOfMedia(items["alike.jpg"]).Single().PersonId);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => items[$"s{i}.jpg"]).Append(items["alike.jpg"]).Order(), reach.Found[greta.Id].Order());
        // The rest of that day's series is settled by the one answer (no guess to confirm); the other day is a guess.
        for (var i = 1; i < 5; i++)
            Assert.Equal(AssignSource.SameDay, repo.DetectionsOfMedia(items[$"s{i}.jpg"]).Single(d => d.Kind == DetectionKind.Face).AssignSource);
        Assert.Equal(AssignSource.Face, repo.DetectionsOfMedia(items["alike.jpg"]).Single().AssignSource);
        Assert.Equal(Enumerable.Range(1, 4).Select(i => items[$"s{i}.jpg"]).Order(), reach.Settled[greta.Id].Order());

        // Photos without a date make no day out: their folder is what belongs together.
        using (var c = lib.Db.Open()) c.Execute("UPDATE media SET event_id=NULL");
        identity.Recompute();
        for (var i = 1; i < 5; i++)
            Assert.All(repo.DetectionsOfMedia(items[$"s{i}.jpg"]), d => Assert.Equal(greta.Id, d.PersonId));
        Assert.Equal(david.Id, repo.DetectionsOfMedia(items["david_too.jpg"]).Single().PersonId);
    }

    /// <summary>
    /// Choosing a name: the known people are offered the most likely first. By the face; without a face by the
    /// clothes of that day; who the user said it is not comes last.
    /// </summary>
    [Fact]
    public async Task NamesAreOfferedMostLikelyFirst()
    {
        using var lib = new TestLibrary();
        foreach (var (f, t) in new[] { ("anna.jpg", "2024-02-10 10:00"), ("helmet.jpg", "2024-02-10 11:00"), ("ben.jpg", "2024-03-10 10:00"),
                     ("who.jpg", "2024-05-10 10:00") })
            File.SetLastWriteTime(lib.AddPhoto(f), DateTime.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        var items = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.FileName, i => i.Id);
        new EventBuilder(lib.Db).Rebuild();
        var repo = new PeopleRepository(lib.Db);
        var anna = repo.CreatePerson("Anna");
        var ben = repo.CreatePerson("Ben");
        repo.Confirm([repo.ReplaceDetections(items["anna.jpg"], [Face(Emb(0), link: 1), Body(Emb(5))])[0]], anna.Id);
        repo.Confirm(repo.ReplaceDetections(items["ben.jpg"], [Face(Emb(1))]), ben.Id);
        var helmet = repo.ReplaceDetections(items["helmet.jpg"], [Body(Emb(5, 0.2f))]);
        var who = repo.ReplaceDetections(items["who.jpg"], [Face(Mix((1, 0.8f), (9, 0.6f)))]);
        var identity = new IdentityService(repo);
        identity.Recompute();

        // A face like Ben's: Ben first and offered with one click, Anna not.
        var byFace = identity.Candidates(who);
        Assert.Equal(["Ben", "Anna"], byFace.Select(c => c.Person.Name));
        Assert.Equal([true, false], byFace.Select(c => c.Likely));
        // No face, but Anna's jacket on the day she was there.
        var byClothes = identity.Candidates(helmet);
        Assert.Equal("Anna", byClothes[0].Person.Name);
        Assert.True(byClothes[0].Likely);
        Assert.False(byClothes[1].Likely);
        // "Not Ben": he comes last and is not offered.
        repo.Reject(who, ben.Id);
        var notBen = identity.Candidates(who);
        Assert.Equal(["Anna", "Ben"], notBen.Select(c => c.Person.Name));
        Assert.False(notBen[1].Likely);
    }

    /// <summary>
    /// A class of children in white shirts, filmed: David is known by his face in one photo of that day. The shirt
    /// names nobody where several children wear it, and of two children on screen at once only one can be David (the
    /// one with the better face), in every frame of the video. A track after his own may be him again.
    /// </summary>
    [Fact]
    public async Task NobodyIsTwiceOnScreenAtOnce()
    {
        using var lib = new TestLibrary();
        foreach (var (f, t) in new[] { ("david.jpg", "2024-05-20 09:00"), ("show.jpg", "2024-05-20 10:00") })
            File.SetLastWriteTime(lib.AddPhoto(f), DateTime.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        var items = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.FileName, i => i.Id);
        new EventBuilder(lib.Db).Rebuild();
        var repo = new PeopleRepository(lib.Db);
        var david = repo.CreatePerson("David");
        repo.Confirm([repo.ReplaceDetections(items["david.jpg"], [Face(Emb(0), link: 1), Body(Emb(5))])[0]], david.Id);
        // The "video": five children, each a track over two frames, all in the same shirt. Track 1 shows David's
        // face in the first frame; track 2 shows a face a little like his in the second. Track 6 comes after track 1.
        NewDetection Child(int track, int time, float x) =>
            new(DetectionKind.Body, time, x, 0.1f, 0.1f, 0.6f, 0.9f, 0.8f, Emb(5, 0.1f + track * 0.01f), track, null);
        NewDetection FaceOn(float[] e, int track, int time, float x, int link) =>
            new(DetectionKind.Face, time, x + 0.02f, 0.12f, 0.05f, 0.08f, 0.9f, 0.8f, e, track, link);
        var show = new List<NewDetection>();
        for (var track = 1; track <= 5; track++)
            foreach (var time in new[] { 0, 1000 })
                show.Add(Child(track, time, track * 0.15f));
        show.Add(FaceOn(Emb(0, 0.15f), 1, 0, 0.15f, 0));
        show.Add(FaceOn(Mix((0, 0.6f), (9, 0.8f)), 2, 1000, 0.30f, 3));
        show.Add(Child(6, 2000, 0.15f));
        show.Add(FaceOn(Emb(0, 0.2f), 6, 2000, 0.15f, 12));
        var ids = repo.ReplaceDetections(items["show.jpg"], show);
        new IdentityService(repo).Recompute();

        var seen = repo.DetectionsOfMedia(items["show.jpg"]).ToDictionary(d => d.Id);
        long?[] Of(int track) => Enumerable.Range(0, show.Count).Where(i => show[i].Track == track).Select(i => seen[ids[i]].PersonId).ToArray();
        Assert.All(Of(1), p => Assert.Equal(david.Id, p));
        for (var track = 2; track <= 5; track++) Assert.All(Of(track), Assert.Null);
        Assert.All(Of(6), p => Assert.Equal(david.Id, p));
    }

    /// <summary>Finds one "face": the white patch of the picture, wherever it is in what it is given.</summary>
    private sealed class WhitePatchModel : IPeopleModel
    {
        public string ModelId => "white-patch";
        public string Device => "test";
        /// <summary>The sizes of the images it was given (the analyzer hands it the upright picture).</summary>
        public List<(int Width, int Height)> Seen { get; } = [];

        public IReadOnlyList<DetectedObject> Analyze(MomentFinder.Media.RgbImage image, bool includeWeak = false)
        {
            Seen.Add((image.Width, image.Height));
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    if (image.Data[(y * image.Width + x) * 3] < 200) continue;
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                }
            if (x1 < 0) return [];
            return [new DetectedObject { Kind = DetectionKind.Face, Box = new Box(x0, y0, x1 - x0 + 1, y1 - y0 + 1), Score = 0.9f, Quality = 0.9f, Embedding = Emb(0) }];
        }
    }

    /// <summary>
    /// A photo lying on its side, turned by the user: the people are looked for on the upright picture, and what is
    /// found is stored where it is in the file. So the name said before the turn stays on the same person, and the
    /// other way round, whichever way it is turned.
    /// </summary>
    [Fact]
    public async Task ATurnedPhotoIsAnalyzedUprightAndStoredInTheFile()
    {
        using var lib = new TestLibrary();
        var path = Path.Combine(lib.MediaDir, "side.png");
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Black, 200, 100))
        {
            // The "face": a white patch near the top right corner of the file.
            image.Draw(new ImageMagick.Drawing.Drawables().FillColor(ImageMagick.MagickColors.White).Rectangle(150, 10, 189, 39));
            image.Write(path);
        }
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        MediaItem Item() => lib.Repo.Query(new MediaFilter()).Single();
        var repo = new PeopleRepository(lib.Db);
        var model = new WhitePatchModel();
        var analyzer = new PeopleAnalyzer(lib.Paths, repo, model);
        var ct = TestContext.Current.CancellationToken;
        void IsThePatch(DetectionRow d)
        {
            Assert.Equal(0.75f, d.X, 0.02f);
            Assert.Equal(0.10f, d.Y, 0.02f);
            Assert.Equal(0.20f, d.W, 0.02f);
            Assert.Equal(0.30f, d.H, 0.02f);
        }

        await analyzer.AnalyzeAsync(Item(), ct);
        var eva = repo.CreatePerson("Eva");
        repo.Confirm(repo.DetectionsOfMedia(Item().Id).Select(d => d.Id), eva.Id);
        foreach (var rotation in new[] { 90, 180, 270 })
        {
            lib.Repo.SetRotation(Item().Id, rotation);
            Assert.Equal(rotation, Item().Rotation);
            using (var c = lib.Db.Open())
                Assert.Equal(0, c.ExecuteScalar<int>("SELECT people_version FROM media"));
            await analyzer.AnalyzeAsync(Item(), ct);
            // Looked at upright (the long side is the height when it is turned on its side)...
            Assert.Equal(rotation == 180 ? (200, 100) : (100, 200), model.Seen[^1]);
            // ...stored in the file, and still Eva.
            var found = repo.DetectionsOfMedia(Item().Id).Single();
            IsThePatch(found);
            Assert.Equal((eva.Id, true), (found.PersonId!.Value, found.Confirmed));
            // On the turned picture the patch is where a turn puts the top right corner.
            var (x, y, _, _) = MomentFinder.Media.QuarterTurn.ToTurned(rotation, found.X, found.Y, found.W, found.H);
            Assert.Equal(rotation switch { 90 => (true, true), 180 => (false, true), _ => (false, false) }, (x > 0.5f, y > 0.5f));
        }
    }

    /// <summary>The same for a video filmed with the camera on its side: its frames are turned before the people are looked for.</summary>
    [Fact]
    public async Task ATurnedVideoIsAnalyzedUprightAndStoredInTheFile()
    {
        Assert.SkipUnless(MomentFinder.Media.FFmpeg.IsAvailable, "ffmpeg is not installed");
        using var lib = new TestLibrary();
        var ct = TestContext.Current.CancellationToken;
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=200x100:r=5:d=3,drawbox=x=150:y=10:w=40:h=30:color=white:t=fill",
                "-pix_fmt", "yuv420p", Path.Combine(lib.MediaDir, "side.mp4"),
            },
        };
        await System.Diagnostics.Process.Start(psi)!.WaitForExitAsync(ct);
        await lib.ScanAsync(lib.AddSource());
        MediaItem Item() => lib.Repo.Query(new MediaFilter()).Single();
        var repo = new PeopleRepository(lib.Db);
        var model = new WhitePatchModel();
        var analyzer = new PeopleAnalyzer(lib.Paths, repo, model);
        foreach (var rotation in new[] { 0, 90, 270 })
        {
            lib.Repo.SetRotation(Item().Id, rotation);
            await analyzer.AnalyzeAsync(Item(), ct);
            Assert.Equal(rotation == 0, model.Seen[^1].Width > model.Seen[^1].Height);
            var found = repo.DetectionsOfMedia(Item().Id);
            Assert.NotEmpty(found);
            Assert.All(found, d =>
            {
                Assert.Equal(0.75f, d.X, 0.03f);
                Assert.Equal(0.10f, d.Y, 0.03f);
                Assert.Equal(0.20f, d.W, 0.04f);
                Assert.Equal(0.30f, d.H, 0.04f);
            });
            Assert.All(repo.TrackPaths(Item().Id), p => Assert.Equal(0.75f, p.X, 0.03f));
        }
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

    /// <summary>The viewer over a whole large library asks who is in thousands of videos at once.</summary>
    [Fact]
    public void PeopleInThousandsOfVideos()
    {
        using var lib = new TestLibrary();
        var source = lib.AddSource();
        var repo = new PeopleRepository(lib.Db);
        var eva = repo.CreatePerson("Eva");
        var tom = repo.CreatePerson("Tom");
        var hidden = repo.CreatePerson("Nobody");
        List<long> ids;
        using (var c = lib.Db.Open())
        using (var tx = c.BeginTransaction())
        {
            ids = Enumerable.Range(0, 3000).Select(i => c.ExecuteScalar<long>(
                "INSERT INTO media(source_id, rel_path, kind, size, mtime, taken_at, duration_ms) VALUES(@id, @name, 2, 1, 1, 1, 20000) RETURNING id",
                new { id = source.Id, name = $"v{i}.mp4" }, tx)).ToList();
            void Seen(long media, long? person, long? suggested) => c.Execute(
                "INSERT INTO detection(media_id, time_ms, kind, x, y, w, h, score, quality, embedding, person_id, suggest_person_id) VALUES(@media, 0, 1, 0, 0, 0.2, 0.5, 1, 1, x'00', @person, @suggested)",
                new { media, person, suggested }, tx);
            // Tom in three videos (one of them only suggested), Eva in one, the hidden one and one outside the list too.
            Seen(ids[10], tom.Id, null);
            Seen(ids[2000], tom.Id, null);
            Seen(ids[2999], null, tom.Id);
            Seen(ids[5], eva.Id, null);
            Seen(ids[6], hidden.Id, null);
            c.Execute("UPDATE person SET hidden=1 WHERE id=@id", new { id = hidden.Id }, tx);
            tx.Commit();
        }
        Assert.Equal([tom.Id, eva.Id], repo.PersonsIn(ids));
        Assert.Equal([eva.Id], repo.PersonsIn(ids.Take(8)));
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

    /// <summary>Scans or a folder put into the cloud: hundreds of undated photos copied within minutes are no day out.</summary>
    [Fact]
    public async Task PhotosCopiedInBulkMakeNoEvent()
    {
        using var lib = new TestLibrary();
        var start = DateTime.Parse("2026-11-21 12:00", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 60; i++) File.SetLastWriteTime(lib.AddPhoto($"scan{i}.jpg"), start.AddSeconds(5 * i));
        File.SetLastWriteTime(lib.AddPhoto("trip1.jpg"), DateTime.Parse("2024-02-10 09:00", System.Globalization.CultureInfo.InvariantCulture));
        File.SetLastWriteTime(lib.AddPhoto("trip2.jpg"), DateTime.Parse("2024-02-10 10:00", System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        Assert.Equal(1, new EventBuilder(lib.Db).Rebuild());
        using var c = lib.Db.Open();
        Assert.Equal(2, Dapper.SqlMapper.ExecuteScalar<int>(c, "SELECT COUNT(*) FROM media WHERE event_id IS NOT NULL"));
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
