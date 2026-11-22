using System.IO.Hashing;
using ImageMagick;
using MomentFinder.Library;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MxDirectory = MetadataExtractor.Directory;

namespace MomentFinder.Media;

/// <summary>Reads what the library stores about a file: capture time, size, GPS, camera.</summary>
public static class MediaProbe
{
    static MediaProbe()
    {
        // A description an old program wrote into a photo (XMP in Windows-1252) needs the code pages, which .NET does
        // not carry by itself: without them the metadata reader throws on such a photo.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public static async Task<ScannedFile> ProbeAsync(string fullPath, string relPath, MediaKind kind, FileInfo info, CancellationToken ct)
    {
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var hash = QuickHash(fullPath, info.Length);
        // Fallback for the capture time: the file time in local wall clock, like EXIF.
        var fallbackTaken = ToTakenAt(info.LastWriteTime);

        if (kind == MediaKind.Video)
        {
            var probe = await FFmpeg.ProbeAsync(fullPath, ct);
            var dated = probe?.CreationTime is { } ct2 && ct2.Year > 1971 ? ct2 : (DateTime?)null;
            return new ScannedFile(relPath, kind, info.Length, mtime, hash,
                dated is { } d ? ToTakenAt(d) : PathDate.TakenAt(relPath, fallbackTaken) ?? fallbackTaken,
                probe?.Width, probe?.Height, probe?.DurationMs, probe?.Lat, probe?.Lon, probe?.Camera, TakenFromFile: dated == null);
        }

        DateTime? taken = null;
        int? width = null, height = null;
        double? lat = null, lon = null;
        string? camera = null;
        var orientation = 1;
        try
        {
            IReadOnlyList<MxDirectory> dirs = ImageMetadataReader.ReadMetadata(fullPath);
            var subIfd = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto))
                taken = dto;
            else if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dtd))
                taken = dtd;
            else if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt))
                taken = dt;
            if (dirs.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation() is { IsZero: false } geo)
            {
                lat = geo.Latitude;
                lon = geo.Longitude;
            }
            if (ifd0 != null)
            {
                var make = ifd0.GetString(ExifDirectoryBase.TagMake)?.Trim();
                var model = ifd0.GetString(ExifDirectoryBase.TagModel)?.Trim();
                camera = model != null && make != null && model.StartsWith(make, StringComparison.OrdinalIgnoreCase)
                    ? model
                    : string.Join(' ', new[] { make, model }.Where(x => !string.IsNullOrEmpty(x)));
                if (camera.Length == 0) camera = null;
                if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o)) orientation = o;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // No readable metadata, whatever the reader stumbled on: the photo is in the library all the same, with
            // the file time. (One photo it threw on stopped the reading of its whole folder, at every start.)
        }

        try
        {
            var ping = new MagickImageInfo(fullPath);
            width = (int)ping.Width;
            height = (int)ping.Height;
            if (orientation is >= 5 and <= 8)
                (width, height) = (height, width);
        }
        catch (MagickException)
        {
        }

        return new ScannedFile(relPath, kind, info.Length, mtime, hash,
            taken is { Year: > 1971 } t ? ToTakenAt(t) : PathDate.TakenAt(relPath, fallbackTaken) ?? fallbackTaken, width, height, null, lat, lon, camera,
            TakenFromFile: taken is not { Year: > 1971 });
    }

    public static long ToTakenAt(DateTime wallClock) =>
        (long)(DateTime.SpecifyKind(wallClock, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds;

    /// <summary>
    /// The capture time is the file's own time (the wall clock of its modification time): the photo had no date
    /// of its own. The two differ by the time zone only (whole quarters of an hour).
    /// </summary>
    public static bool IsFileTime(long takenAt, long mtime)
    {
        const long quarter = 15 * 60 * 1000;
        var offset = takenAt - mtime;
        if (Math.Abs(offset) > 14 * 4 * quarter) return false;
        var rest = ((offset % quarter) + quarter) % quarter;
        return Math.Min(rest, quarter - rest) <= 2000;
    }

    /// <summary>
    /// A photo or video without a date of its own. Known since it is kept (fromFile); for what was read before
    /// that: dated by the file's time, of a file in the cloud (its time is when it was uploaded) or of a photo
    /// without a camera.
    /// </summary>
    public static bool Undated(bool? fromFile, long takenAt, long mtime, bool inCloud, bool photo, bool hasCamera) =>
        fromFile ?? (IsFileTime(takenAt, mtime) && (inCloud || photo && !hasCamera));

    /// <summary>Size + first and last 64 KiB: finds the same file after a move or in another source without reading all of it.</summary>
    public static string QuickHash(string path, long length)
    {
        const int chunk = 64 * 1024;
        var hasher = new XxHash3();
        Span<byte> lenBytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(lenBytes, length);
        hasher.Append(lenBytes);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        var buffer = new byte[chunk];
        var n = fs.ReadAtLeast(buffer, (int)Math.Min(chunk, length), throwOnEndOfStream: false);
        hasher.Append(buffer.AsSpan(0, n));
        if (length > 2 * chunk)
        {
            fs.Seek(-chunk, SeekOrigin.End);
            n = fs.ReadAtLeast(buffer, chunk, throwOnEndOfStream: false);
            hasher.Append(buffer.AsSpan(0, n));
        }
        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }
}
