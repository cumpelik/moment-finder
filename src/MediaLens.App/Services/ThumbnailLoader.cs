using Avalonia.Media.Imaging;
using MediaLens.Library;
using MediaLens.Media;

namespace MediaLens.App.Services;

/// <summary>
/// Loads thumbnails for the tiles on screen. A tile scrolled away cancels its load; an item the indexer
/// did not get to yet gets its thumbnail made right away.
/// </summary>
public sealed class ThumbnailLoader
{
    /// <summary>Decoded width: sharp at the largest tile size, small enough for thousands of tiles.</summary>
    private const int DecodeWidth = 360;

    private readonly Indexer _indexer;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(Math.Max(2, Environment.ProcessorCount / 2));

    public ThumbnailLoader(Indexer indexer, AppPaths paths)
    {
        _indexer = indexer;
        _paths = paths;
    }

    /// <summary>A video found by a search shows the matching frame (atMs), not its usual first-second frame.</summary>
    public async Task<Bitmap?> LoadAsync(MediaItem item, int atMs, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = item.IsVideo && atMs > 0
                ? await HitFrameAsync(item, atMs, ct)
                : await _indexer.EnsureThumbnailAsync(item, ct);
            if (path == null) return null;
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.MediumQuality);
            }, ct);
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // An undecodable thumbnail leaves the tile empty instead of failing the grid.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> HitFrameAsync(MediaItem item, int atMs, CancellationToken ct)
    {
        // A 360 frame would be a fisheye circle: the thumbnail (forward view) stands for it.
        if (Media.Fisheye360.IsDualFisheye(item.FullPath)) return await _indexer.EnsureThumbnailAsync(item, ct);
        var path = Path.Combine(_paths.CacheDir, "hits", $"{item.Id}_{atMs}.jpg");
        if (File.Exists(path)) return path;
        var tmp = Path.ChangeExtension(path, Guid.NewGuid().ToString("N") + ".jpg");
        if (await FFmpeg.ExtractFrameAsync(await Cloud.MediaFiles.LocalPathAsync(item, ct), atMs / 1000.0, tmp, (int)ImageCodec.ThumbSize, ct))
        {
            File.Move(tmp, path, true);
            return path;
        }
        return await _indexer.EnsureThumbnailAsync(item, ct);
    }
}
