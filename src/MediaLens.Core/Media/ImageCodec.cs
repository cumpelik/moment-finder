using ImageMagick;
using ImageMagick.Formats;
using MediaLens.Library;

namespace MediaLens.Media;

/// <summary>Raw BGRA pixels, ready for a bitmap of the UI.</summary>
public sealed record DecodedImage(byte[] Bgra, int Width, int Height);

public static class ImageCodec
{
    /// <summary>The longest side of a thumbnail. Also the input of the image analysis, so the original is decoded only once.</summary>
    public const uint ThumbSize = 480;

    public static async Task<bool> CreateThumbnailAsync(MediaItem item, string outPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        // Written aside and moved, so a reader never sees half a file and two writers do not collide.
        var tmp = Path.ChangeExtension(outPath, Guid.NewGuid().ToString("N") + ".jpg");
        if (item.IsVideo)
        {
            var duration = (item.DurationMs ?? 0) / 1000.0;
            var at = duration > 0 ? Math.Min(1.0, duration * 0.1) : 0;
            var ok = await FFmpeg.ExtractFrameAsync(item.FullPath, at, tmp, (int)ThumbSize, ct) ||
                     (at > 0 && await FFmpeg.ExtractFrameAsync(item.FullPath, 0, tmp, (int)ThumbSize, ct));
            if (ok) File.Move(tmp, outPath, true);
            return ok;
        }

        return await Task.Run(() =>
        {
            using var image = ReadFirstFrame(item.FullPath, (int)ThumbSize * 2);
            image.AutoOrient();
            image.Thumbnail(new MagickGeometry(ThumbSize, ThumbSize));
            image.Strip();
            image.Quality = 82;
            image.Write(tmp, MagickFormat.Jpeg);
            File.Move(tmp, outPath, true);
            return true;
        }, ct);
    }

    /// <summary>The photo turned upright and scaled down to fit the box (never up).</summary>
    public static DecodedImage DecodeForDisplay(string path, int maxWidth, int maxHeight)
    {
        using var image = ReadFirstFrame(path, Math.Max(maxWidth, maxHeight));
        image.AutoOrient();
        if (image.Width > maxWidth || image.Height > maxHeight)
            image.Resize(new MagickGeometry((uint)maxWidth, (uint)maxHeight));
        image.Alpha(AlphaOption.Remove);
        return new DecodedImage(image.ToByteArray(MagickFormat.Bgra), (int)image.Width, (int)image.Height);
    }

    /// <summary>RGB bytes of the image stretched to exactly width x height (the model input of SigLIP is not cropped).</summary>
    public static byte[] DecodeRgbStretched(string path, int width, int height)
    {
        using var image = ReadFirstFrame(path, Math.Max(width, height) * 2);
        image.AutoOrient();
        image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        image.Alpha(AlphaOption.Remove);
        return image.ToByteArray(MagickFormat.Rgb);
    }

    private static MagickImage ReadFirstFrame(string path, int hintSize)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        // A JPEG decodes at 1/2, 1/4 or 1/8 directly - much faster for a thumbnail.
        settings.SetDefines(new JpegReadDefines { Size = new MagickGeometry((uint)hintSize, (uint)hintSize) });
        return new MagickImage(path, settings);
    }
}
