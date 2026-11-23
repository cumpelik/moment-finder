using Dapper;
using ImageMagick;
using MomentFinder.Data;

namespace MomentFinder.People;

/// <summary>
/// The picture of a face or a figure for the galleries. A library joined from the shared one has the analysis but
/// not the crops (they stay on the computer that analyzed): a photo's crop is then cut from its thumbnail.
/// </summary>
public static class CropFallback
{
    public static string Ensure(AppPaths paths, Database db, long detectionId)
    {
        var path = paths.PeopleCropPath(detectionId);
        if (File.Exists(path)) return path;
        using var c = db.Open();
        var d = c.QueryFirstOrDefault<(long MediaId, int Kind, double X, double Y, double W, double H, string RelPath, int Rotation)?>("""
            SELECT d.media_id, m.kind, d.x, d.y, d.w, d.h, m.rel_path, m.rotation FROM detection d JOIN media m ON m.id=d.media_id WHERE d.id=@detectionId
            """, new { detectionId });
        // A video's thumbnail is another moment, a 360 one another view: no crop rather than a wrong one.
        if (d is not { } det || det.Kind != (int)Library.MediaKind.Photo || Media.Fisheye360.IsDualFisheye(det.RelPath)) return path;
        var thumb = paths.ThumbPath(det.MediaId);
        if (!File.Exists(thumb)) return path;
        try
        {
            using var image = new MagickImage(thumb);
            var x = (int)Math.Clamp(det.X * image.Width, 0, image.Width - 1);
            var y = (int)Math.Clamp(det.Y * image.Height, 0, image.Height - 1);
            var w = (uint)Math.Clamp(det.W * image.Width, 1, image.Width - x);
            var h = (uint)Math.Clamp(det.H * image.Height, 1, image.Height - y);
            image.Crop(new MagickGeometry(x, y, w, h));
            image.ResetPage();
            // A photo the user turned: the box and the thumbnail are the file's, the person is shown upright.
            if (det.Rotation != 0) image.Rotate(det.Rotation);
            image.Quality = 82;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written aside and moved: a card shown twice quickly cuts it twice at once, and nobody reads half a file.
            var part = $"{path}.{Guid.NewGuid():N}.part";
            image.Write(part, MagickFormat.Jpeg);
            File.Move(part, path, true);
        }
        catch (Exception e) when (e is MagickException or IOException or UnauthorizedAccessException)
        {
        }
        return path;
    }
}
