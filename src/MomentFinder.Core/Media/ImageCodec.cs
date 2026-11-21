using ImageMagick;
using ImageMagick.Formats;
using MomentFinder.Library;

namespace MomentFinder.Media;

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
        if (Fisheye360.IsDualFisheye(item.FullPath))
        {
            var ok = await Create360ThumbnailAsync(item, tmp, ct);
            if (ok) File.Move(tmp, outPath, true);
            return ok;
        }
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

    /// <summary>A 360 item's thumbnail: the forward view, as it opens.</summary>
    private static async Task<bool> Create360ThumbnailAsync(MediaItem item, string outJpeg, CancellationToken ct)
    {
        RgbImage lens0, lens1;
        Rot3? level = null;
        if (item.IsVideo)
        {
            var dir = Path.Combine(Path.GetTempPath(), "momentfinder-360-" + Guid.NewGuid().ToString("N"));
            try
            {
                var duration = (item.DurationMs ?? 0) / 1000.0;
                var frame = await FFmpeg.ExtractDualFrameAsync(item.FullPath, duration > 0 ? Math.Min(1.0, duration * 0.1) : 0, dir, 768, ct);
                if (frame == null) return false;
                level = Fisheye360.LevelAt(item.FullPath, frame.TimeMs / 1000.0);
                (lens0, lens1) = (RgbImage.Load(frame.Lens0, 768), RgbImage.Load(frame.Lens1, 768));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
        else
            (lens0, lens1) = await Task.Run(() => Fisheye360.SplitSideBySide(RgbImage.Load(item.FullPath, 1536)), ct);
        await Task.Run(() => Fisheye360.RenderView(lens0, lens1, 0, Fisheye360.DefaultPitch, Fisheye360.DefaultFov, (int)ThumbSize, (int)ThumbSize * 3 / 4,
                Fisheye360.ModelOf(item.FullPath), level)
            .SaveJpeg(outJpeg, 82), ct);
        return true;
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
