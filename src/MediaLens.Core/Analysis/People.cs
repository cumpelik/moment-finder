using MediaLens.Media;

namespace MediaLens.Analysis;

public enum DetectionKind
{
    Face = 1,
    Body = 2,
}

/// <summary>Pixel box in the analyzed image.</summary>
public readonly record struct Box(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public float CenterX => X + W / 2;
    public float CenterY => Y + H / 2;

    public float Iou(Box o)
    {
        var ix = Math.Max(0, Math.Min(Right, o.Right) - Math.Max(X, o.X));
        var iy = Math.Max(0, Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y));
        var inter = ix * iy;
        var union = W * H + o.W * o.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public bool Contains(float x, float y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>A face or a whole person found in an image, with its identity embedding and a small crop for the UI.</summary>
public sealed class DetectedObject
{
    public DetectionKind Kind { get; init; }
    public Box Box { get; init; }
    public float Score { get; init; }
    /// <summary>0..1: how much the embedding can be trusted (size, sharpness of the view, frontal face).</summary>
    public float Quality { get; init; }
    /// <summary>L2 normalized, 512 values (ArcFace for faces, OSNet re-identification for bodies).</summary>
    public float[] Embedding { get; init; } = [];
    public RgbImage? Crop { get; init; }
    /// <summary>
    /// Found with low confidence (a person lying in the snow, half hidden): in a video only kept when it continues
    /// a person seen before, never a person of its own.
    /// </summary>
    public bool Weak { get; init; }
}

/// <summary>Faces and people in an image: the input of the identity matching.</summary>
public interface IPeopleModel
{
    /// <summary>Changes when the models change, so stored results are computed again.</summary>
    string ModelId { get; }
    string Device { get; }
    /// <param name="includeWeak">Also people found with low confidence (Weak), for following people through a video.</param>
    IReadOnlyList<DetectedObject> Analyze(RgbImage image, bool includeWeak = false);
}
