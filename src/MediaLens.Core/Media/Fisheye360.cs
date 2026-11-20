namespace MediaLens.Media;

/// <summary>
/// 360 media of dual fisheye cameras (Insta360 .insv / .insp): two circular images, lens 0 looking forward,
/// lens 1 backward. Everything else works in the sphere around the camera: a direction is yaw (degrees, 0 is
/// forward, positive to the right) and pitch (positive up); positions are stored as equirectangular coordinates
/// u = yaw / 360 + 0.5, v = 0.5 - pitch / 180 (both 0..1), so boxes of people are boxes on that map.
/// </summary>
public static class Fisheye360
{
    /// <summary>The field of view of each lens (equidistant fisheye); more than 180, so the lenses overlap.</summary>
    /// <summary>Chosen on Insta360 X4 footage by where the two lenses meet without a shift.</summary>
    public static double LensFov { get; set; } = 197;

    /// <summary>The share of the image size that the fisheye circle takes (its radius over half the side).</summary>
    public static double CircleScale { get; set; } = 1.0;

    public static bool IsDualFisheye(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".insv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".insp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A unit direction: x right, y up, z forward.</summary>
    public static (double X, double Y, double Z) Direction(double yawDeg, double pitchDeg)
    {
        var yaw = yawDeg * Math.PI / 180;
        var pitch = pitchDeg * Math.PI / 180;
        return (Math.Sin(yaw) * Math.Cos(pitch), Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch));
    }

    public static (double U, double V) ToEquirect(double x, double y, double z)
    {
        var yaw = Math.Atan2(x, z) * 180 / Math.PI;
        var pitch = Math.Asin(Math.Clamp(y, -1, 1)) * 180 / Math.PI;
        return (yaw / 360 + 0.5, 0.5 - pitch / 180);
    }

    public static (double Yaw, double Pitch) FromEquirect(double u, double v) => ((u - 0.5) * 360, (0.5 - v) * 180);

    /// <summary>Where a direction is in the lens images: the lens and the position in it (0..1).</summary>
    public static (int Lens, double U, double V) ToLens(double x, double y, double z)
    {
        // The back lens looks the other way: its right is the world's left.
        var lens = z >= 0 ? 0 : 1;
        if (lens == 1) (x, z) = (-x, -z);
        var theta = Math.Acos(Math.Clamp(z, -1, 1));
        var r = theta / (LensFov * Math.PI / 360) * 0.5 * CircleScale;
        var phi = Math.Atan2(y, x);
        return (lens, 0.5 + r * Math.Cos(phi), 0.5 - r * Math.Sin(phi));
    }

    /// <summary>
    /// Both lenses where they overlap: how much of lens 0 to take (1 well inside it, 0 inside lens 1, a short ramp
    /// across the seam so the brightness of the two lenses does not meet in a hard line).
    /// </summary>
    public static double FrontWeight(double z)
    {
        // Narrow: a person close to the camera at the seam is seen from two angles (parallax); a wide blend shows him
        // twice, half transparent. A thin edge only hides the brightness difference of the lenses.
        var band = Math.Min(Math.Sin((LensFov / 2 - 90) * Math.PI / 180), Math.Sin(1.5 * Math.PI / 180));
        return band <= 0 ? (z >= 0 ? 1 : 0) : Math.Clamp(0.5 + z / (2 * band), 0, 1);
    }

    /// <summary>A direction in one lens, given which lens (also behind its center, within its field of view).</summary>
    public static (double U, double V) InLens(int lens, double x, double y, double z)
    {
        if (lens == 1) (x, z) = (-x, -z);
        var theta = Math.Acos(Math.Clamp(z, -1, 1));
        var r = theta / (LensFov * Math.PI / 360) * 0.5 * CircleScale;
        var phi = Math.Atan2(y, x);
        return (0.5 + r * Math.Cos(phi), 0.5 - r * Math.Sin(phi));
    }

    /// <summary>The direction through a pixel of a perspective view (yaw, pitch, horizontal field of view).</summary>
    public static (double X, double Y, double Z) ViewRay(double yawDeg, double pitchDeg, double hfovDeg, int width, int height, double px, double py)
    {
        var f = width / 2.0 / Math.Tan(hfovDeg * Math.PI / 360);
        // Camera space: x right, y up, z forward; then pitch (around x) and yaw (around y).
        double cx = px - width / 2.0, cy = height / 2.0 - py, cz = f;
        var n = Math.Sqrt(cx * cx + cy * cy + cz * cz);
        cx /= n; cy /= n; cz /= n;
        var p = pitchDeg * Math.PI / 180;
        var y1 = cy * Math.Cos(p) + cz * Math.Sin(p);
        var z1 = -cy * Math.Sin(p) + cz * Math.Cos(p);
        var yw = yawDeg * Math.PI / 180;
        return (cx * Math.Cos(yw) + z1 * Math.Sin(yw), y1, -cx * Math.Sin(yw) + z1 * Math.Cos(yw));
    }

    /// <summary>Where a direction falls in a perspective view (pixels), or null when it is behind the viewer.</summary>
    public static (double X, double Y)? ToView(double yawDeg, double pitchDeg, double hfovDeg, int width, int height, double x, double y, double z)
    {
        var yw = yawDeg * Math.PI / 180;
        var cx = x * Math.Cos(yw) - z * Math.Sin(yw);
        var z1 = x * Math.Sin(yw) + z * Math.Cos(yw);
        var p = pitchDeg * Math.PI / 180;
        var cy = y * Math.Cos(p) - z1 * Math.Sin(p);
        var cz = y * Math.Sin(p) + z1 * Math.Cos(p);
        if (cz <= 0.01) return null;
        var f = width / 2.0 / Math.Tan(hfovDeg * Math.PI / 360);
        return (width / 2.0 + cx / cz * f, height / 2.0 - cy / cz * f);
    }

    /// <summary>How much a direction faces the view (the cosine of the angle to its center).</summary>
    public static double Facing(double yawDeg, double pitchDeg, double x, double y, double z)
    {
        var (vx, vy, vz) = Direction(yawDeg, pitchDeg);
        return x * vx + y * vy + z * vz;
    }

    /// <summary>A perspective view rendered from the two lens images (bilinear).</summary>
    public static RgbImage RenderView(RgbImage lens0, RgbImage lens1, double yawDeg, double pitchDeg, double hfovDeg, int width, int height)
    {
        var data = new byte[width * height * 3];
        Parallel.For(0, height, j =>
        {
            for (var i = 0; i < width; i++)
            {
                var (x, y, z) = ViewRay(yawDeg, pitchDeg, hfovDeg, width, height, i + 0.5, j + 0.5);
                var w = FrontWeight(z);
                var o = (j * width + i) * 3;
                var (u0, v0) = InLens(0, x, y, z);
                var (u1, v1) = InLens(1, x, y, z);
                for (var c = 0; c < 3; c++)
                {
                    var a = w > 0 ? lens0.Sample((float)(u0 * lens0.Width - 0.5), (float)(v0 * lens0.Height - 0.5), c) : 0;
                    var b = w < 1 ? lens1.Sample((float)(u1 * lens1.Width - 0.5), (float)(v1 * lens1.Height - 0.5), c) : 0;
                    data[o + c] = (byte)Math.Round(a * w + b * (1 - w));
                }
            }
        });
        return new RgbImage(data, width, height);
    }

    /// <summary>A 360 photo (.insp): both lens circles side by side in one image.</summary>
    public static (RgbImage Lens0, RgbImage Lens1) SplitSideBySide(RgbImage image)
    {
        var half = image.Width / 2;
        return (image.Crop(0, 0, half, image.Height, half, image.Height), image.Crop(half, 0, half, image.Height, half, image.Height));
    }

    /// <summary>The view a 360 item is shown in at first: forward, a little down (where people are), wide.</summary>
    public const double DefaultPitch = -10;
    public const double DefaultFov = 100;

    /// <summary>
    /// Perspective views all around for the analysis: the models expect ordinary pictures, the fisheye or the
    /// equirectangular map would bend people out of shape. Neighbours overlap, so nobody is cut in half everywhere.
    /// </summary>
    public static IEnumerable<(double Yaw, RgbImage View)> ViewsAround(RgbImage lens0, RgbImage lens1, int count, int width, int height, double hfov)
    {
        for (var k = 0; k < count; k++)
        {
            var yaw = 360.0 * k / count;
            yield return (yaw, RenderView(lens0, lens1, yaw, DefaultPitch, hfov, width, height));
        }
    }

    /// <summary>
    /// A box of a perspective view (pixels) as a box on the equirectangular map (0..1): the outline sampled and
    /// enclosed. A box across the back seam keeps u above 1 for its right part.
    /// </summary>
    public static (float U, float V, float W, float H) ViewBoxToEquirect(double yawDeg, double pitchDeg, double hfovDeg, int width, int height,
        float bx, float by, float bw, float bh)
    {
        var points = new List<(double U, double V)>();
        const int steps = 8;
        for (var k = 0; k <= steps; k++)
        {
            var t = (float)k / steps;
            foreach (var (px, py) in new[] { (bx + bw * t, by), (bx + bw * t, by + bh), (bx, by + bh * t), (bx + bw, by + bh * t) })
            {
                var (x, y, z) = ViewRay(yawDeg, pitchDeg, hfovDeg, width, height, px, py);
                points.Add(ToEquirect(x, y, z));
            }
        }
        // Across the seam behind the camera: the left part continues past 1.
        if (points.Max(p => p.U) - points.Min(p => p.U) > 0.5)
            points = points.Select(p => p.U < 0.5 ? (p.U + 1, p.V) : p).ToList();
        var u0 = points.Min(p => p.U);
        var v0 = points.Min(p => p.V);
        var w = points.Max(p => p.U) - u0;
        // One way to write a box across the seam: its center on the map (it may start below 0 or end above 1).
        if (u0 + w / 2 >= 1) u0 -= 1;
        return ((float)u0, (float)v0, (float)w, (float)(points.Max(p => p.V) - v0));
    }

    /// <summary>The overlap of two map boxes, also across the seam behind the camera (u wraps around).</summary>
    public static float WrappedIou(Analysis.Box a, Analysis.Box b, float mapWidth) =>
        Math.Max(a.Iou(b), Math.Max(a.Iou(b with { X = b.X + mapWidth }), a.Iou(b with { X = b.X - mapWidth })));

    /// <summary>
    /// A box of the equirectangular map seen in a perspective view (pixels), or null when its center is not in
    /// front of the viewer.
    /// </summary>
    public static (double X, double Y, double W, double H)? EquirectBoxToView(double yawDeg, double pitchDeg, double hfovDeg, int width, int height,
        double u, double v, double w, double h)
    {
        var (cyaw, cpitch) = FromEquirect(u + w / 2, v + h / 2);
        var (cx, cy, cz) = Direction(cyaw, cpitch);
        if (ToView(yawDeg, pitchDeg, hfovDeg, width, height, cx, cy, cz) == null) return null;
        var xs = new List<double>();
        var ys = new List<double>();
        const int steps = 6;
        for (var k = 0; k <= steps; k++)
        {
            var t = (double)k / steps;
            foreach (var (pu, pv) in new[] { (u + w * t, v), (u + w * t, v + h), (u, v + h * t), (u + w, v + h * t) })
            {
                var (yaw, pitch) = FromEquirect(pu, pv);
                var (x, y, z) = Direction(yaw, pitch);
                // Only points well in front: near 90 degrees to the side the projection runs away.
                if (Facing(yawDeg, pitchDeg, x, y, z) < 0.2 || ToView(yawDeg, pitchDeg, hfovDeg, width, height, x, y, z) is not { } p) continue;
                xs.Add(p.X);
                ys.Add(p.Y);
            }
        }
        if (xs.Count == 0) return null;
        // Points far to the side of the view shoot off to huge values: the box ends at the edge of the picture.
        double Clamp(double value, double size) => Math.Clamp(value, -0.25 * size, 1.25 * size);
        var (x0, x1) = (Clamp(xs.Min(), width), Clamp(xs.Max(), width));
        var (y0, y1) = (Clamp(ys.Min(), height), Clamp(ys.Max(), height));
        if (x1 <= 0 || y1 <= 0 || x0 >= width || y0 >= height || x1 - x0 < 2 || y1 - y0 < 2) return null;
        return (x0, y0, x1 - x0, y1 - y0);
    }
}
