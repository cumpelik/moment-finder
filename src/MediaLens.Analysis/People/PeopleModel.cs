using MediaLens.Media;

namespace MediaLens.Analysis.People;

/// <summary>
/// Faces (SCRFD + ArcFace) and whole people (YOLOX + OSNet) of one image. The face says who it is across
/// years; the body says who it is within a day (same clothes), also when the face is hidden by a helmet and goggles.
/// </summary>
public sealed class PeopleModel : IPeopleModel, IDisposable
{
    private const int MinFace = 20;
    private const int MinBodyHeight = 48;

    private readonly FaceDetector _faces;
    private readonly FaceEmbedder _faceEmbedder;
    private readonly PersonDetector _bodies;
    private readonly BodyEmbedder _bodyEmbedder;
    private readonly object _lock = new();

    public string ModelId => "buffalo_l+yolox_s+osnet_ain_x1_0/1";
    public string Device { get; }

    public PeopleModel(string faceDir, string personDir, string reidDir)
    {
        var (det, device) = OrtSessions.Create(Path.Combine(faceDir, "det_10g.onnx"), true);
        Device = device;
        _faces = new FaceDetector(det);
        _faceEmbedder = new FaceEmbedder(OrtSessions.Create(Path.Combine(faceDir, "w600k_r50.onnx"), true).Session);
        _bodies = new PersonDetector(OrtSessions.Create(Path.Combine(personDir, "yolox_s.onnx"), true).Session);
        _bodyEmbedder = new BodyEmbedder(OrtSessions.Create(Path.Combine(reidDir, "osnet_ain_x1_0_msmt17.onnx"), true).Session);
    }

    public IReadOnlyList<DetectedObject> Analyze(RgbImage image)
    {
        lock (_lock)
        {
            var result = new List<DetectedObject>();
            foreach (var f in _faces.Detect(image))
            {
                if (f.Box.W < MinFace || f.Box.H < MinFace) continue;
                var aligned = FaceEmbedder.Align(image, f.Landmarks);
                var side = Math.Max(f.Box.W, f.Box.H) * 1.6f;
                result.Add(new DetectedObject
                {
                    Kind = DetectionKind.Face,
                    Box = f.Box,
                    Score = f.Score,
                    Quality = f.Score * Math.Min(1f, f.Box.W / 64f) * Frontal(f.Landmarks),
                    Embedding = _faceEmbedder.Embed(aligned),
                    Crop = image.Crop(f.Box.CenterX - side / 2, f.Box.CenterY - side / 2, side, side, 112, 112),
                });
            }

            var bodies = _bodies.Detect(image).Where(b => b.Box.H >= MinBodyHeight).ToList();
            var crops = bodies.Select(b =>
            {
                // A little margin, so arms and skis at the edge of the box stay in.
                var padX = b.Box.W * 0.05f;
                var padY = b.Box.H * 0.03f;
                return image.Crop(b.Box.X - padX, b.Box.Y - padY, b.Box.W + 2 * padX, b.Box.H + 2 * padY, 128, 256);
            }).ToList();
            var embeddings = _bodyEmbedder.Embed(crops);
            for (var i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                result.Add(new DetectedObject
                {
                    Kind = DetectionKind.Body,
                    Box = b.Box,
                    Score = b.Score,
                    Quality = b.Score * Math.Min(1f, b.Box.H / 200f) * (TouchesEdge(b.Box, image) ? 0.6f : 1f),
                    Embedding = embeddings[i],
                    Crop = crops[i],
                });
            }
            return result;
        }
    }

    /// <summary>1 for a face looking at the camera, lower when turned aside (nose far from the middle of the eyes).</summary>
    private static float Frontal(float[] lm)
    {
        var eyeDist = MathF.Abs(lm[2] - lm[0]);
        if (eyeDist < 1) return 0.2f;
        var offset = MathF.Abs(lm[4] - (lm[0] + lm[2]) / 2) / eyeDist;
        return Math.Clamp(1 - offset * 1.5f, 0.2f, 1f);
    }

    private static bool TouchesEdge(Box b, RgbImage image) =>
        b.X <= 2 || b.Y <= 2 || b.Right >= image.Width - 2 || b.Bottom >= image.Height - 2;

    public void Dispose()
    {
        _faces.Dispose();
        _faceEmbedder.Dispose();
        _bodies.Dispose();
        _bodyEmbedder.Dispose();
    }
}
