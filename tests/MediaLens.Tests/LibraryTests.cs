using MediaLens.Buckets;
using MediaLens.Library;
using MediaLens.Media;
using MediaLens.Search;

namespace MediaLens.Tests;

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
        Assert.True(await ImageCodec.CreateThumbnailAsync(item, thumb, TestContext.Current.CancellationToken));
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
}
