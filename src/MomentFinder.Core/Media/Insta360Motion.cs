using System.Buffers.Binary;
using System.Numerics;

namespace MomentFinder.Media;

/// <summary>A rotation as a matrix (rows), for turning directions between the leveled frame and the lenses.</summary>
public readonly record struct Rot3(double M00, double M01, double M02, double M10, double M11, double M12, double M20, double M21, double M22)
{
    public static readonly Rot3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public (double X, double Y, double Z) Apply(double x, double y, double z) =>
        (M00 * x + M01 * y + M02 * z, M10 * x + M11 * y + M12 * z, M20 * x + M21 * y + M22 * z);

    public Rot3 Transposed => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);
}

/// <summary>
/// How an Insta360 camera was held through a video, from the gyroscope and accelerometer samples in the file's
/// trailer (1000 a second). Gives the rotation that levels the picture: up is up whatever the angle of the selfie
/// stick, shakes are taken out, and the view follows where the camera points only smoothly.
///
/// The trailer: 32 bytes of magic at the end, before it the trailer size; records end with [format, id, size];
/// record 0 lists the others as [id, format, size, offset from the trailer start]. Record 3 holds the samples
/// (time in microseconds, accelerometer x y z, gyroscope x y z as 16 bit values around 32768), record 4 the time
/// of every frame, record 1 the metadata (when the first frame was taken, the sensors' ranges). On the X4 the
/// accelerometer shows 1024 per g and the gyroscope 2000 degrees a second at full scale.
/// </summary>
public sealed class Insta360Motion
{
    private const string Magic = "8db42d694ccc418790edff439fe026bf";
    private const int StoreEvery = 5; // 200 a second

    private readonly double _t0;
    private readonly double _step;
    private readonly Rot3[] _levelFromLens;

    /// <summary>Not used any more: frames are at their video time from the first frame.</summary>
    public double[] FrameTimes { get; }

    private Insta360Motion(double t0, double step, Rot3[] rotations, double[] frameTimes)
    {
        _t0 = t0;
        _step = step;
        _levelFromLens = rotations;
        FrameTimes = frameTimes;
    }

    /// <summary>From a direction of the leveled world to the camera's own at a moment of the video.</summary>
    public Rot3 LensFromLevel(double seconds)
    {
        var i = (int)Math.Round((seconds - _t0) / _step);
        return _levelFromLens[Math.Clamp(i, 0, _levelFromLens.Length - 1)].Transposed;
    }

    public static Insta360Motion? Load(string path, double gyroDps = 0, float tiltSeconds = TiltSeconds,
        double headingSeconds = HeadingSeconds)
    {
        try
        {
            using var f = File.OpenRead(path);
            var records = Records(f);
            if (!records.TryGetValue(3, out var gyro) || gyro.Size < 20 * 100 || gyro.Size % 20 != 0) return null;
            var data = new byte[gyro.Size];
            f.Seek(gyro.Offset, SeekOrigin.Begin);
            f.ReadExactly(data);
            var n = data.Length / 20;
            long T(int i) => (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(i * 20));
            double V(int i, int c) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 20 + 8 + c * 2)) - 32768.0;
            // Microseconds on the X4; older cameras count milliseconds.
            var unit = (T(n - 1) - T(0)) / (double)(n - 1) > 100 ? 1e-6 : 1e-3;

            // The metadata (protobuf): when the first video frame was taken on the sensors' clock (field 24), and the
            // range of the gyroscope in degrees a second (field 65, its field 2).
            var frameTimes = Array.Empty<double>();
            var first = T(0);
            if (records.TryGetValue(1, out var meta) && meta.Size < 1 << 20)
            {
                var m = new byte[meta.Size];
                f.Seek(meta.Offset, SeekOrigin.Begin);
                f.ReadExactly(m);
                foreach (var (field, value, bytes) in Protobuf(m))
                {
                    if (field == 24 && value > 0) first = value;
                    if (field == 65 && bytes != null)
                        foreach (var (inner, range, _) in Protobuf(bytes))
                            if (inner == 2 && range is > 100 and < 10000 && gyroDps <= 0) gyroDps = range;
                }
            }
            if (gyroDps <= 0) gyroDps = 2000;

            // The sensor's axes in the frame used here (x up, y right, z along the front lens for a camera held
            // upright): (-x, z, y) of the raw values. Found on recordings: this gyroscope mapping keeps the picture
            // still while the camera turns, and with it the accelerometer keeps pointing one way through a video.
            Vector3 Accel(int i) => new((float)-V(i, 0), (float)V(i, 2), (float)V(i, 1));
            Vector3 Gyro(int i) => new((float)-V(i, 3), (float)V(i, 5), (float)V(i, 4));
            // At rest the accelerometer shows 1 g upward.
            Vector3 Up(int i) => Vector3.Normalize(Accel(i));
            var start = Vector3.Zero;
            for (var i = 0; i < Math.Min(n, 300); i++) start += Up(i);
            start = Vector3.Normalize(start);
            // q turns sensor directions into world ones (z up).
            var q = Between(start, Vector3.UnitZ);
            var k = gyroDps / 32768 * Math.PI / 180;
            var stored = new List<Quaternion>();
            for (var i = 0; i < n; i++)
            {
                var dt = i > 0 ? Math.Clamp((T(i) - T(i - 1)) * unit, 0, 0.05) : 0.001;
                var w = Gyro(i) * (float)k;
                // The accelerometer pulls the tilt back slowly (the gyroscope drifts), only while it shows about 1 g:
                // in a turn it shows the turn as well.
                var a = Accel(i);
                var g = a.Length() / 1024;
                if (g is > 0.85f and < 1.15f)
                {
                    var measured = a / a.Length();
                    var predicted = Vector3.Transform(Vector3.UnitZ, Quaternion.Conjugate(q));
                    // Quickly at the start (the first guess may be taken in a turn), then slowly: a carved turn pulls
                    // sideways for seconds, the gyroscope carries the horizon through it.
                    var seconds = Math.Min(tiltSeconds, 0.5f + i * 0.001f * 2);
                    w += Vector3.Cross(measured, predicted) * (1 / seconds);
                }
                var angle = w.Length() * (float)dt;
                if (angle > 1e-9f) q = Quaternion.Normalize(q * Quaternion.CreateFromAxisAngle(w / w.Length(), angle));
                if (i % StoreEvery == 0) stored.Add(q);
            }

            // Where the front lens points on the ground plane, followed smoothly (forward and backward, so without lag).
            var heading = new double[stored.Count];
            double last = 0, turns = 0;
            for (var i = 0; i < stored.Count; i++)
            {
                var forward = Vector3.Transform(Vector3.UnitZ, stored[i]);
                var h = forward.X * forward.X + forward.Y * forward.Y > 0.09 ? Math.Atan2(forward.Y, forward.X) : last - turns;
                // Unwrapped, so the smoothing does not jump at 180 degrees.
                while (h + turns - last > Math.PI) turns -= 2 * Math.PI;
                while (h + turns - last < -Math.PI) turns += 2 * Math.PI;
                heading[i] = last = h + turns;
            }
            var step = (T(n - 1) - T(0)) * unit / (n - 1) * StoreEvery;
            var alpha = Math.Min(1, step / headingSeconds);
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 1; i < heading.Length; i++) heading[i] = heading[i - 1] + (heading[i] - heading[i - 1]) * alpha;
                Array.Reverse(heading);
            }

            var rotations = new Rot3[stored.Count];
            for (var i = 0; i < stored.Count; i++)
            {
                var (sin, cos) = Math.SinCos(heading[i]);
                var right = new Vector3((float)sin, (float)-cos, 0);
                var forward = new Vector3((float)cos, (float)sin, 0);
                // A lens direction (x right, y up, z forward) is (y, x, z) for the sensor.
                Vector3 Level(Vector3 sensor)
                {
                    var world = Vector3.Transform(sensor, stored[i]);
                    return new Vector3(Vector3.Dot(world, right), world.Z, Vector3.Dot(world, forward));
                }
                var (cx, cy, cz) = (Level(Vector3.UnitY), Level(Vector3.UnitX), Level(Vector3.UnitZ));
                rotations[i] = new Rot3(cx.X, cy.X, cz.X, cx.Y, cy.Y, cz.Y, cx.Z, cy.Z, cz.Z);
            }
            return new Insta360Motion((T(0) - first) * unit, step, rotations, frameTimes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>How fast the accelerometer corrects the tilt, and how smoothly the view follows the camera's heading.</summary>
    private const float TiltSeconds = 15;
    private const double HeadingSeconds = 0.8;

    private static Quaternion Between(Vector3 from, Vector3 to)
    {
        var axis = Vector3.Cross(from, to);
        var dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (axis.Length() < 1e-6f) return dot > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(dot));
    }

    /// <summary>The top-level fields of a protobuf message: numbers as values, nested messages and strings as bytes.</summary>
    private static IEnumerable<(int Field, long Value, byte[]? Bytes)> Protobuf(byte[] data)
    {
        var i = 0;
        long Varint()
        {
            long v = 0;
            for (var shift = 0; i < data.Length && shift < 64; shift += 7)
            {
                var b = data[i++];
                v |= (long)(b & 0x7F) << shift;
                if (b < 0x80) break;
            }
            return v;
        }
        while (i < data.Length)
        {
            var key = Varint();
            var field = (int)(key >> 3);
            switch (key & 7)
            {
                case 0: yield return (field, Varint(), null); break;
                case 1: i += 8; break;
                case 5: i += 4; break;
                case 2:
                    var length = (int)Varint();
                    if (length < 0 || i + length > data.Length) yield break;
                    yield return (field, 0, data.AsSpan(i, length).ToArray());
                    i += length;
                    break;
                default: yield break;
            }
        }
    }

    private static Dictionary<int, (long Offset, int Size)> Records(FileStream f)
    {
        var result = new Dictionary<int, (long, int)>();
        if (f.Length < 200) return result;
        var foot = new byte[78];
        f.Seek(-78, SeekOrigin.End);
        f.ReadExactly(foot);
        if (System.Text.Encoding.ASCII.GetString(foot, 46, 32) != Magic) return result;
        long trailerSize = BinaryPrimitives.ReadUInt32LittleEndian(foot.AsSpan(38));
        var trailerStart = f.Length - trailerSize;
        // The last record: [format, id, size] right before the 72 byte footer.
        int id = foot[1], size = (int)BinaryPrimitives.ReadUInt32LittleEndian(foot.AsSpan(2));
        var end = f.Length - 78;
        if (id == 0 && size is > 0 and < 100_000)
        {
            var index = new byte[size];
            f.Seek(end - size, SeekOrigin.Begin);
            f.ReadExactly(index);
            for (var i = 0; i + 10 <= index.Length; i += 10)
            {
                var entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i + 2));
                long offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i + 6));
                if (entrySize > 0 && trailerStart + offset + entrySize <= f.Length) result[index[i]] = (trailerStart + offset, entrySize);
            }
            return result;
        }
        // Older files: the records follow each other backwards.
        var header = new byte[6];
        while (end - 6 > trailerStart && result.Count < 64)
        {
            f.Seek(end, SeekOrigin.Begin);
            f.ReadExactly(header);
            id = header[1];
            size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2));
            if (size <= 0 || end - size < trailerStart) break;
            result[id] = (end - size, size);
            end -= size + 6;
        }
        return result;
    }
}
