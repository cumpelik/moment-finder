namespace MomentFinder.Media;

/// <summary>
/// A picture the user turned (MediaItem.Rotation: 0, 90, 180 or 270 degrees clockwise) because its file lies on
/// its side. The file and its thumbnail stay as they are and are shown turned; everything stored about the picture
/// (the boxes of people, the paths of video tracks) stays in the coordinates of the file. These map a box, given
/// in parts of the width and height, between the file and the turned picture.
/// </summary>
public static class QuarterTurn
{
    /// <summary>Any number of degrees as one of 0, 90, 180, 270.</summary>
    public static int Normal(int degrees) => ((degrees % 360 + 360) % 360 + 45) / 90 % 4 * 90;

    /// <summary>The turned picture has the file's width as its height.</summary>
    public static bool Sideways(int rotation) => rotation is 90 or 270;

    /// <summary>A box of the file, on the turned picture.</summary>
    public static (float X, float Y, float W, float H) ToTurned(int rotation, float x, float y, float w, float h) => rotation switch
    {
        90 => (1 - y - h, x, h, w),
        180 => (1 - x - w, 1 - y - h, w, h),
        270 => (y, 1 - x - w, h, w),
        _ => (x, y, w, h),
    };

    /// <summary>A box of the turned picture, in the file.</summary>
    public static (float X, float Y, float W, float H) ToFile(int rotation, float x, float y, float w, float h) =>
        ToTurned((360 - rotation) % 360, x, y, w, h);
}
