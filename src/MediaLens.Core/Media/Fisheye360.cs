namespace MediaLens.Media;

/// <summary>
/// 360 media of dual fisheye cameras (Insta360 .insv / .insp): two circular images, lens 0 looking forward,
/// lens 1 backward. Everything else works in the sphere around the camera: a direction is yaw (degrees, 0 is
/// forward, positive to the right) and pitch (positive up); positions are stored as equirectangular coordinates
/// u = yaw / 360 + 0.5, v = 0.5 - pitch / 180 (both 0..1), so boxes of people are boxes on that map.
/// </summary>
public static class Fisheye360
{
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
    public static (int Lens, double U, double V) ToLens(double x, double y, double z, LensModel? model = null)
    {
        var lens = z >= 0 ? 0 : 1;
        var (u, v) = InLens(lens, x, y, z, model);
        return (lens, u, v);
    }

    /// <summary>
    /// Both lenses where they overlap: how much of lens 0 to take (1 well inside it, 0 inside lens 1, a short ramp
    /// across the seam so the brightness of the two lenses does not meet in a hard line). Narrow: a person close
    /// to the camera at the seam is seen from two angles (parallax); a wide blend shows them twice, half transparent.
    /// </summary>
    public static double FrontWeight(double z) => Math.Clamp(0.5 + z / (2 * SeamBand), 0, 1);

    private static readonly double SeamBand = Math.Sin(1.5 * Math.PI / 180);

    /// <summary>A direction in one lens, given which lens (also behind its center, within its field of view).</summary>
    public static (double U, double V) InLens(int lens, double x, double y, double z, LensModel? model = null)
    {
        var m = model ?? LensModel.Default;
        var (cx, cy) = (m.Cx0, m.Cy0);
        if (lens == 1)
        {
            // The back lens looks the other way (its right is the world's left), turned a little against the front one.
            (x, z) = (-x, -z);
            var roll = m.Roll1 * Math.PI / 180;
            (x, y) = (x * Math.Cos(roll) - y * Math.Sin(roll), x * Math.Sin(roll) + y * Math.Cos(roll));
            (cx, cy) = (m.Cx1, m.Cy1);
        }
        var theta = Math.Acos(Math.Clamp(z, -1, 1));
        var r = m.F * theta;
        var phi = Math.Atan2(y, x);
        return (cx + r * Math.Cos(phi), cy - r * Math.Sin(phi));
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
    public static RgbImage RenderView(RgbImage lens0, RgbImage lens1, double yawDeg, double pitchDeg, double hfovDeg, int width, int height,
        LensModel? model = null, Rot3? lensFromLevel = null)
    {
        var data = new byte[width * height * 3];
        Parallel.For(0, height, j =>
        {
            for (var i = 0; i < width; i++)
            {
                var (x, y, z) = ViewRay(yawDeg, pitchDeg, hfovDeg, width, height, i + 0.5, j + 0.5);
                // The view is in the leveled world; the lenses are wherever the camera was held.
                if (lensFromLevel is { } rot) (x, y, z) = rot.Apply(x, y, z);
                var w = FrontWeight(z);
                var o = (j * width + i) * 3;
                var (u0, v0) = InLens(0, x, y, z, model);
                var (u1, v1) = InLens(1, x, y, z, model);
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
    public static IEnumerable<(double Yaw, RgbImage View)> ViewsAround(RgbImage lens0, RgbImage lens1, int count, int width, int height, double hfov,
        LensModel? model = null, Rot3? lensFromLevel = null)
    {
        for (var k = 0; k < count; k++)
        {
            var yaw = 360.0 * k / count;
            yield return (yaw, RenderView(lens0, lens1, yaw, DefaultPitch, hfov, width, height, model, lensFromLevel));
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Insta360Motion?> Motions = new();

    /// <summary>How the camera was held through a video (null: not recorded, the picture stays as the camera saw it).</summary>
    public static Insta360Motion? MotionOf(string path) => Motions.GetOrAdd(path, p => Insta360Motion.Load(p));

    /// <summary>The rotation that levels a file's picture at a moment.</summary>
    public static Rot3? LevelAt(string path, double seconds) => MotionOf(path)?.LensFromLevel(seconds);

    /// <summary>
    /// Both lenses (side by side, BGRA) as one leveled equirectangular picture (BGRA): the playing copy of a 360 video.
    /// The lens positions are computed on a grid of 8 pixels and interpolated, the pixels sampled bilinearly.
    /// </summary>
    public static void RenderEquirect(byte[] sideBySide, int lensSize, byte[] output, int width, int height, LensModel model, Rot3 lensFromLevel)
    {
        const int cell = 8;
        int gw = width / cell + 2, gh = height / cell + 2;
        var grid = new float[gw * gh * 5];
        Parallel.For(0, gh, gj =>
        {
            for (var gi = 0; gi < gw; gi++)
            {
                var (yaw, pitch) = FromEquirect((gi * cell + 0.5) / width, (gj * cell + 0.5) / height);
                var (x, y, z) = Direction(yaw, pitch);
                (x, y, z) = lensFromLevel.Apply(x, y, z);
                var (u0, v0) = InLens(0, x, y, z, model);
                var (u1, v1) = InLens(1, x, y, z, model);
                var o = (gj * gw + gi) * 5;
                grid[o] = (float)(u0 * lensSize);
                grid[o + 1] = (float)(v0 * lensSize);
                grid[o + 2] = (float)(u1 * lensSize);
                grid[o + 3] = (float)(v1 * lensSize);
                grid[o + 4] = (float)z;
            }
        });
        var stride = lensSize * 2 * 4;
        var band = (float)SeamBand;
        Parallel.For(0, height, j =>
        {
            var gj = j / cell;
            var fy = (j % cell) / (float)cell;
            for (var i = 0; i < width; i++)
            {
                var gi = i / cell;
                var fx = (i % cell) / (float)cell;
                var a = (gj * gw + gi) * 5;
                var b = a + gw * 5;
                float G(int c) => (grid[a + c] * (1 - fx) + grid[a + 5 + c] * fx) * (1 - fy) + (grid[b + c] * (1 - fx) + grid[b + 5 + c] * fx) * fy;
                var z = G(4);
                var weight = Math.Clamp(0.5f + z / (2 * band), 0, 1);
                var o = (j * width + i) * 4;
                if (weight >= 1) Sample(sideBySide, stride, lensSize, 0, G(0), G(1), output, o, 1);
                else if (weight <= 0) Sample(sideBySide, stride, lensSize, lensSize, G(2), G(3), output, o, 1);
                else
                {
                    Sample(sideBySide, stride, lensSize, 0, G(0), G(1), output, o, 1);
                    Sample(sideBySide, stride, lensSize, lensSize, G(2), G(3), output, o, 1 - weight);
                }
                output[o + 3] = 255;
            }
        });
    }

    /// <summary>Bilinear sample of one lens (at xOffset in the side by side frame) into output; share below 1 mixes with what is there.</summary>
    private static void Sample(byte[] src, int stride, int lensSize, int xOffset, float u, float v, byte[] output, int o, float share)
    {
        u = Math.Clamp(u - 0.5f, 0, lensSize - 1.001f);
        v = Math.Clamp(v - 0.5f, 0, lensSize - 1.001f);
        int x0 = (int)u, y0 = (int)v;
        float fx = u - x0, fy = v - y0;
        var p = y0 * stride + (xOffset + x0) * 4;
        for (var c = 0; c < 3; c++)
        {
            var value = (src[p + c] * (1 - fx) + src[p + 4 + c] * fx) * (1 - fy) + (src[p + stride + c] * (1 - fx) + src[p + stride + 4 + c] * fx) * fy;
            output[o + c] = (byte)(share >= 1 ? value : output[o + c] * (1 - share) + value * share);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LensModel> Calibrations = new();

    /// <summary>The lens model of a file: the camera's own calibration from the file, or the default.</summary>
    public static LensModel ModelOf(string path) => Calibrations.GetOrAdd(path, p => ReadCalibration(p) ?? LensModel.Default);

    /// <summary>
    /// Insta360 writes each camera's lens calibration into the end of the file: "2_" then for each lens the radius,
    /// center x and y, and three angles, then the sensor size (both lenses side by side), in sensor pixels. The
    /// video frame is the square of the sensor height around each lens, turned a quarter (sensor y is frame x).
    /// </summary>
    public static LensModel? ReadCalibration(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var length = Math.Min(f.Length, 512 * 1024);
            f.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            f.ReadExactly(buffer);
            var text = System.Text.Encoding.Latin1.GetString(buffer);
            const string n = @"(-?\d+\.\d+)";
            var match = System.Text.RegularExpressions.Regex.Match(text,
                $@"(?<![\d.])2_{n}_{n}_{n}_{n}_{n}_{n}_{n}_{n}_{n}_{n}_{n}_{n}_(\d+)_(\d+)");
            if (!match.Success) return null;
            double G(int i) => double.Parse(match.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            var (width, height) = (G(13), G(14));
            if (width <= 0 || height <= 0) return null;
            // Center offsets of each lens from the middle of its half of the sensor, as a share of the frame side.
            var (dx0, dy0) = ((G(2) - width / 4) / height, (G(3) - height / 2) / height);
            var (dx1, dy1) = ((G(8) - width * 3 / 4) / height, (G(9) - height / 2) / height);
            return new LensModel(0.5 - dy0, 0.5 + dx0, 0.5 - dy1, 0.5 + dx1,
                // The radius is where the field of view reaches 91.7 degrees (fitted on X4 footage).
                G(1) / height / RadiusAngle, G(12) - G(6));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private const double RadiusAngle = 1.6006;
}

/// <summary>
/// A dual fisheye camera: the lens centers in their frames (0..1), the scale (frame sides per radian from the center,
/// equidistant fisheye) and how much the back lens is turned against the front one (degrees).
/// </summary>
public sealed record LensModel(double Cx0, double Cy0, double Cx1, double Cy1, double F, double Roll1)
{
    /// <summary>Insta360 X4 without its calibration: measured where the lenses overlap (190 degrees each).</summary>
    public static readonly LensModel Default = new(0.5, 0.5, 0.5, 0.5, 0.30126, 0);
}
