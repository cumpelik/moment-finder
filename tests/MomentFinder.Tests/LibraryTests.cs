using MomentFinder.Buckets;
using MomentFinder.Library;
using MomentFinder.Media;
using MomentFinder.Search;

namespace MomentFinder.Tests;

public class LibraryTests
{
    [Fact]
    public async Task ScanFindsNewChangedAndMissingFiles()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("2023/a.jpg");
        lib.AddPhoto("2023/b.jpg");
        lib.AddPhoto(".thumbnails/x.jpg"); // another application's cache
        File.WriteAllText(Path.Combine(lib.MediaDir, "notes.txt"), "not media");
        var source = lib.AddSource();

        var first = await lib.ScanAsync(source);
        Assert.Equal(2, first.Added);
        Assert.Equal(2, first.Total);

        var again = await lib.ScanAsync(source);
        Assert.Equal(0, again.Added + again.Changed + again.Missing);

        lib.AddPhoto("2023/a.jpg", 80, 60);
        File.SetLastWriteTimeUtc(Path.Combine(lib.MediaDir, "2023/a.jpg"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(lib.MediaDir, "2023/b.jpg"));
        var third = await lib.ScanAsync(source);
        Assert.Equal(1, third.Changed);
        Assert.Equal(1, third.Missing);

        var items = lib.Repo.Query(new MediaFilter());
        var a = Assert.Single(items);
        Assert.Equal("2023/a.jpg", a.RelPath);
        Assert.Equal(80, a.Width);
        Assert.Equal(Path.Combine(lib.MediaDir, "2023", "a.jpg"), a.FullPath);
    }

    /// <summary>A file moved to another folder or source is the same item: its id and what hangs on it stay.</summary>
    [Fact]
    public async Task APhotoWithADescriptionInAnOldEncodingIsReadLikeTheOthers()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("2015/good.jpg");
        // What an old program left in a photo: a description (XMP) with stray bytes in its padding, which the
        // metadata reader then tries to read as Windows-1252.
        var path = lib.AddPhoto("2015/old.jpg", width: 80);
        var jpeg = File.ReadAllBytes(path);
        var xmp = System.Text.Encoding.Latin1.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" dc:source=\"x\"/></rdf:RDF></x:xmpmeta>" +
            "      \0   0      !\u00a0    $\u00a0  $     \0 \0     ");
        byte[] header = [0xFF, 0xE1, (byte)((xmp.Length + 2) >> 8), (byte)(xmp.Length + 2)];
        File.WriteAllBytes(path, [.. jpeg[..2], .. header, .. xmp, .. jpeg[2..]]);

        var scan = await lib.ScanAsync(lib.AddSource());

        // It stopped the reading of the whole folder: none of the photos beside it came into the library.
        Assert.Equal(2, scan.Added);
        Assert.Equal(80, lib.Repo.Query(new MediaFilter()).Single(i => i.FileName == "old.jpg").Width);
    }

    [Fact]
    public async Task MovedFilesKeepTheirRow()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("2023/a.jpg", color: "red");
        lib.AddPhoto("2023/b.jpg", color: "blue");
        lib.AddPhoto("2023/c.jpg", color: "green");
        var source = lib.AddSource();
        await lib.ScanAsync(source);
        var ids = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.FileName, i => i.Id);
        var buckets = new BucketService(lib.Db, lib.Repo);
        var bucket = buckets.Create("Book");
        buckets.Add(bucket.Id, [ids["a.jpg"]]);

        // Into another folder, a rename, and a copy (the original stays).
        Directory.CreateDirectory(Path.Combine(lib.MediaDir, "trip"));
        File.Move(Path.Combine(lib.MediaDir, "2023/a.jpg"), Path.Combine(lib.MediaDir, "trip/a.jpg"));
        File.Move(Path.Combine(lib.MediaDir, "2023/b.jpg"), Path.Combine(lib.MediaDir, "2023/B_renamed.jpg"));
        File.Copy(Path.Combine(lib.MediaDir, "2023/c.jpg"), Path.Combine(lib.MediaDir, "trip/c_copy.jpg"));
        var scan = await lib.ScanAsync(source);
        Assert.Equal((1, 0, 0, 2), (scan.Added, scan.Changed, scan.Missing, scan.Moved));
        var after = lib.Repo.Query(new MediaFilter()).ToDictionary(i => i.RelPath, i => i.Id);
        Assert.Equal(ids["a.jpg"], after["trip/a.jpg"]);
        Assert.Equal(ids["b.jpg"], after["2023/B_renamed.jpg"]);
        Assert.NotEqual(ids["c.jpg"], after["trip/c_copy.jpg"]);
        Assert.Equal(4, after.Count);
        Assert.Equal("trip/a.jpg", Assert.Single(lib.Repo.Query(new MediaFilter { BucketId = bucket.Id })).RelPath);

        // To another source, whichever of the two is scanned first.
        var otherDir = Path.Combine(lib.Root, "other");
        Directory.CreateDirectory(otherDir);
        var other = lib.Repo.AddSource(SourceKinds.Local, otherDir, "other");
        File.Move(Path.Combine(lib.MediaDir, "trip/a.jpg"), Path.Combine(otherDir, "a.jpg"));
        Assert.Equal(1, (await lib.ScanAsync(other)).Moved);
        Assert.Equal(0, (await lib.ScanAsync(source)).Missing);
        File.Move(Path.Combine(otherDir, "a.jpg"), Path.Combine(lib.MediaDir, "back.jpg"));
        Assert.Equal(1, (await lib.ScanAsync(other)).Missing);
        Assert.Equal(1, (await lib.ScanAsync(source)).Moved);
        Assert.Equal(ids["a.jpg"], lib.Repo.Query(new MediaFilter()).Single(i => i.RelPath == "back.jpg").Id);
    }

    [Fact]
    public async Task VideoMetadataAndThumbnail()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg is not installed");
        using var lib = new TestLibrary();
        var video = Path.Combine(lib.MediaDir, "clip.mp4");
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=3",
                "-metadata", "creation_time=2021-07-04T12:00:00Z", "-pix_fmt", "yuv420p", video,
            },
        };
        await System.Diagnostics.Process.Start(psi)!.WaitForExitAsync(TestContext.Current.CancellationToken);
        var source = lib.AddSource();
        await lib.ScanAsync(source);

        var item = Assert.Single(lib.Repo.Query(new MediaFilter()));
        Assert.Equal(MediaKind.Video, item.Kind);
        Assert.Equal(320, item.Width);
        Assert.InRange(item.DurationMs ?? 0, 2900, 3100);
        Assert.Equal(2021, item.Taken.Year);

        var thumb = lib.Paths.ThumbPath(item.Id);
        Assert.True(await ImageCodec.CreateThumbnailAsync(item, item.FullPath, thumb, TestContext.Current.CancellationToken));
        Assert.True(new FileInfo(thumb).Length > 0);
    }

    [Fact]
    public void Iso6709Location()
    {
        Assert.Equal((50.0755, 14.4378), FFmpeg.ParseIso6709("+50.0755+014.4378+235.000/"));
        Assert.Equal((-33.8688, -151.2093), FFmpeg.ParseIso6709("-33.8688-151.2093/"));
        Assert.Equal((null, null), FFmpeg.ParseIso6709(""));
    }

    [Fact]
    public async Task SearchWithoutModelFiltersByKindAndScope()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("Pláž 2024/a.jpg");
        lib.AddPhoto("b_lyze.jpg");
        await lib.ScanAsync(lib.AddSource());
        var search = new SearchService(lib.Repo);
        Assert.Equal(2, search.Search("fotky", new MediaFilter()).Count);
        Assert.Empty(search.Search("video", new MediaFilter()));
        // A phrase without the model: the words match file and folder names, without diacritics.
        Assert.Empty(search.Search("pes na pláži", new MediaFilter()));
        Assert.Equal("a.jpg", search.Search("plaz", new MediaFilter()).Single().Item.FileName);
        Assert.Equal("b_lyze.jpg", search.Search("lyže", new MediaFilter()).Single().Item.FileName);
    }

    [Fact]
    public async Task DateFilterCountsYearsAndMonths()
    {
        using var lib = new TestLibrary();
        foreach (var (f, t) in new[] { ("a.jpg", "2023-07-14 10:00"), ("b.jpg", "2024-02-10 23:30"), ("c.jpg", "2024-02-11 08:00"), ("d.jpg", "2024-12-31 18:00") })
            File.SetLastWriteTime(lib.AddPhoto(f), DateTime.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
        await lib.ScanAsync(lib.AddSource());
        Assert.Equal([(2024, 0, 3L), (2023, 0, 1L)], lib.Repo.DateCounts());
        Assert.Equal([(2024, 2, 2L), (2024, 12, 1L)], lib.Repo.DateCounts(2024));
        // The last evening of the year still belongs to it (local time, no time zone shift).
        var dec31 = Media.MediaProbe.ToTakenAt(new DateTime(2024, 12, 31));
        Assert.Single(lib.Repo.Query(new MediaFilter { From = dec31, To = dec31 + 86_400_000 }));
        // Several months that are not next to each other: July 2023 and December 2024, not February.
        (long, long) Month(int y, int m) => (Media.MediaProbe.ToTakenAt(new DateTime(y, m, 1)), Media.MediaProbe.ToTakenAt(new DateTime(y, m, 1).AddMonths(1)));
        Assert.Equal(["d.jpg", "a.jpg"], lib.Repo.Query(new MediaFilter { Spans = [Month(2023, 7), Month(2024, 12)] }).Select(i => i.FileName));
    }

    /// <summary>360: a box found in a perspective view goes onto the map and comes back to the same place in that view.</summary>
    [Fact]
    public void Fisheye360BoxesRoundTrip()
    {
        var (u, v, w, h) = Media.Fisheye360.ViewBoxToEquirect(120, -10, 100, 800, 600, 300, 200, 100, 250);
        var back = Media.Fisheye360.EquirectBoxToView(120, -10, 100, 800, 600, u, v, w, h)!.Value;
        // A box of a box: it comes back a little larger (the map bends the outline), never shifted.
        Assert.InRange(back.X, 280, 302);
        Assert.InRange(back.Y, 180, 202);
        Assert.InRange(back.W, 98, 130);
        Assert.InRange(back.H, 248, 285);
        // Behind the viewer: not in the view at all.
        Assert.Null(Media.Fisheye360.EquirectBoxToView(-60, -10, 100, 800, 600, u, v, w, h));
        // A box across the seam behind the camera keeps its center on the map.
        var (su, _, sw, _) = Media.Fisheye360.ViewBoxToEquirect(180, -10, 100, 800, 600, 350, 200, 100, 250);
        Assert.InRange(su + sw / 2, 0, 1);
        // The camera's calibration from the end of an Insta360 file.
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, new string('x', 1000) + "v1.4.8_build1*o2_2893.080_4000.850_2990.760_0.054_0.822_89.788_2886.650_12000.740_3014.510_-0.066_0.930_90.213_16000_6000_10958");
            var cal = Media.Fisheye360.ReadCalibration(file)!;
            Assert.InRange(cal.F, 0.300, 0.302);
            Assert.InRange(cal.Roll1, 0.42, 0.43);
            Assert.InRange(cal.Cx1, 0.497, 0.498);
        }
        finally
        {
            File.Delete(file);
        }
        // Straight ahead is lens 0, straight behind lens 1, both in the middle of their circle.
        Assert.Equal((0, 0.5, 0.5), Media.Fisheye360.ToLens(0, 0, 1));
        Assert.Equal((1, 0.5, 0.5), Media.Fisheye360.ToLens(0, 0, -1));
    }

    [Fact]
    public void PlaceNamesNearestVillage()
    {
        var dir = Directory.CreateTempSubdirectory("places").FullName;
        try
        {
            var zip = Path.Combine(dir, "cities.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            using (var w = new StreamWriter(archive.CreateEntry("cities1000.txt").Open()))
            {
                w.WriteLine("1\tSölden\tSoelden\t\t46.96667\t11.0\tP\tPPL\tAT");
                w.WriteLine("2\tLängenfeld\tLaengenfeld\t\t47.07386\t10.96909\tP\tPPL\tAT");
            }
            var places = new Places.PlaceNames(zip);
            Assert.Equal("Sölden", places.Name(46.94, 11.02));
            Assert.Null(places.Name(50.08, 14.42)); // Prague: nothing within 25 km in this list
            Assert.Equal("Sölden", places.MostCommon([(46.95, 11.0), (46.97, 11.01), (47.07, 10.97)]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task BucketKeepsOrderAndExportsNumberedCopies()
    {
        using var lib = new TestLibrary();
        lib.AddPhoto("x/IMG_1.jpg", color: "red");
        lib.AddPhoto("y/IMG_1.jpg", 70, 50, "blue"); // same name in another folder
        lib.AddPhoto("z/IMG_2.jpg", color: "green");
        await lib.ScanAsync(lib.AddSource());
        var items = lib.Repo.Query(new MediaFilter()).OrderBy(i => i.RelPath).ToList();
        var buckets = new BucketService(lib.Db, lib.Repo);
        var bucket = buckets.Create("Fotokniha 2025");

        Assert.Equal(3, buckets.Add(bucket.Id, [items[2].Id, items[0].Id, items[1].Id]));
        Assert.Equal(0, buckets.Add(bucket.Id, [items[0].Id])); // already there
        buckets.Reorder(bucket.Id, [items[0].Id, items[2].Id, items[1].Id]);
        Assert.Equal([items[0].Id, items[2].Id, items[1].Id],
            lib.Repo.Query(new MediaFilter { BucketId = bucket.Id }).Select(i => i.Id));

        var target = Path.Combine(lib.Root, "export");
        var result = await buckets.ExportAsync(bucket.Id, target, new ExportOptions(), null, TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Copied);
        Assert.Equal(["001_IMG_1.jpg", "002_IMG_2.jpg", "003_IMG_1.jpg"],
            Directory.GetFiles(target).Select(Path.GetFileName).Order());

        // Again: nothing is copied twice.
        var again = await buckets.ExportAsync(bucket.Id, target, new ExportOptions(), null, TestContext.Current.CancellationToken);
        Assert.Equal(0, again.Copied);
        Assert.Equal(3, again.Skipped);

        // Without numbers the two IMG_1.jpg must not overwrite each other, nor a foreign file of that name.
        var plain = Path.Combine(lib.Root, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "IMG_2.jpg"), "someone else's file");
        await buckets.ExportAsync(bucket.Id, plain, new ExportOptions(NumberByOrder: false), null, TestContext.Current.CancellationToken);
        Assert.Equal(["IMG_1 (2).jpg", "IMG_1.jpg", "IMG_2 (2).jpg", "IMG_2.jpg"],
            Directory.GetFiles(plain).Select(Path.GetFileName).Order());
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(plain, "IMG_2.jpg")));
    }

    [Fact]
    public void APreferenceOfTheWindowDoesNotWaitForAnotherWrite()
    {
        using var lib = new TestLibrary();
        lib.Db.SetSetting("group_by", "Folder");

        // Something writes to the library for long (the people matching, the other computers' changes).
        using var busy = lib.Db.Open();
        using var writing = busy.BeginTransaction();
        var asked = System.Diagnostics.Stopwatch.StartNew();
        lib.Db.SetSettingSoon("group_by", "Day");
        lib.Db.SetSettingSoon("group_by", "Month");
        Assert.True(asked.ElapsedMilliseconds < 1000, $"the window waited {asked.ElapsedMilliseconds} ms");
        // Known at once, although not written yet.
        Assert.Equal("Month", lib.Db.GetSetting("group_by"));
        Assert.Equal("Folder", Dapper.SqlMapper.ExecuteScalar<string>(busy, "SELECT value FROM setting WHERE key='group_by'", transaction: writing));

        writing.Commit();
        lib.Db.FlushSettings();
        Assert.Equal("Month", Dapper.SqlMapper.ExecuteScalar<string>(busy, "SELECT value FROM setting WHERE key='group_by'"));
        Assert.Equal("Month", lib.Db.GetSetting("group_by"));
    }
}
