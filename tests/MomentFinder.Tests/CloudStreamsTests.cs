using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using MomentFinder.Cloud;
using MomentFinder.Library;

namespace MomentFinder.Tests;

/// <summary>A video of a cloud folder read in parts through the gate on this computer, without downloading it.</summary>
[Collection("Cloud")]
public class CloudStreamsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileInTheCloudIsReadInPartsWithoutDownloadingIt(bool encrypted)
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        using var streams = service.Streams;
        var cloud = await service.AddFolderAsync("Home", Path.Combine(lib.Root, "cloud"), encrypted ? "correct horse battery" : null, ct, encrypt: encrypted);
        // A name with a space and letters that have to be escaped in an address.
        const string rel = "2024/Léto u moře/first day #1.mp4";
        var bytes = RandomNumberGenerator.GetBytes(3_000_000);
        var local = Path.Combine(lib.Root, "video.bin");
        await File.WriteAllBytesAsync(local, bytes, ct);
        await service.Rclone.UploadAsync(local, CloudService.Join(cloud.Remote, "Videos/" + rel), ct);
        var source = service.AddCloudSource(cloud.Id, "Videos");
        var item = new MediaItem { Id = 1, SourceId = source.Id, RelPath = rel, Kind = MediaKind.Video, Size = bytes.Length, SourceRoot = source.Root, SourceRemote = source.Remote };

        Assert.Null(MediaFiles.LocalNow(item));
        var url = await streams.UrlAsync(item, ct);
        Assert.NotNull(url);
        Assert.StartsWith("http://127.0.0.1:", url);

        using var http = new HttpClient();
        // A part from the middle, as a player asks for it after a jump.
        using var request = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(1_000_000, 1_099_999) } };
        using var part = await http.SendAsync(request, ct);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(bytes[1_000_000..1_100_000], await part.Content.ReadAsByteArrayAsync(ct));
        Assert.Equal(bytes, await http.GetByteArrayAsync(url, ct));
        // Another file of the folder comes from the same gate; nothing was put into the library's own cache.
        Assert.StartsWith(url[..url.LastIndexOf("/2024/", StringComparison.Ordinal)],
            await streams.UrlAsync(new MediaItem { RelPath = "other.mp4", SourceRoot = source.Root, SourceRemote = source.Remote }, ct));
        Assert.False(File.Exists(item.FullPath));
        // Without the path nobody else knows, nothing is served.
        var port = new Uri(url).Port;
        using var guess = await http.GetAsync($"http://127.0.0.1:{port}/Videos/", ct);
        Assert.False(guess.IsSuccessStatusCode);

        // Closed with the application: nothing listens there any more.
        streams.Dispose();
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
                await Task.Delay(100, ct);
            }
        });
        Assert.Null(await streams.UrlAsync(item, ct));
    }

    [Fact]
    public async Task AFolderThatCannotBeServedGivesNoAddress()
    {
        Assert.SkipUnless(Rclone.IsAvailable, "rclone is not installed");
        var ct = TestContext.Current.CancellationToken;
        using var lib = new TestLibrary();
        var service = new CloudService(lib.Paths, lib.Db, lib.Repo);
        using var streams = service.Streams;
        streams.StartTimeout = TimeSpan.FromSeconds(3);
        // A storage rclone does not know: the gate does not start, the caller downloads the file as before.
        Assert.Null(await streams.UrlAsync(new MediaItem { RelPath = "a.mp4", SourceRoot = lib.Root, SourceRemote = "nowhere:bucket/videos" }, ct));
        // A local file has no address at all.
        Assert.Null(await streams.UrlAsync(new MediaItem { RelPath = "a.mp4", SourceRoot = lib.Root }, ct));
    }
}
