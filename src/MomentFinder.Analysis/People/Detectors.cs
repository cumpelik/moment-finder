using System.Numerics.Tensors;
using MomentFinder.Media;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MomentFinder.Analysis.People;

internal static class Nms
{
    public static List<int> Run(IReadOnlyList<Box> boxes, IReadOnlyList<float> scores, float iouThreshold)
    {
        var order = Enumerable.Range(0, boxes.Count).OrderByDescending(i => scores[i]).ToList();
        var keep = new List<int>();
        var removed = new bool[boxes.Count];
        foreach (var i in order)
        {
            if (removed[i]) continue;
            keep.Add(i);
            foreach (var j in order)
                if (!removed[j] && j != i && boxes[i].Iou(boxes[j]) > iouThreshold)
                    removed[j] = true;
        }
        return keep;
    }

    public static float[] Normalize(ReadOnlySpan<float> v)
    {
        var r = v.ToArray();
        var n = TensorPrimitives.Norm(r);
        if (n > 0) TensorPrimitives.Divide(r, n, r);
        return r;
    }
}

/// <summary>A face with its five landmarks (eyes, nose, mouth corners), in image pixels.</summary>
internal sealed record FaceBox(Box Box, float Score, float[] Landmarks);

/// <summary>
/// InsightFace SCRFD 10G (buffalo_l det_10g): 640x640 letterboxed input, three strides with two anchors
/// per location, outputs scores, box distances and landmark offsets per anchor.
/// </summary>
internal sealed class FaceDetector : IDisposable
{
    private const int Size = 640;
    private static readonly int[] Strides = [8, 16, 32];
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string[] _outputs;

    public FaceDetector(InferenceSession session)
    {
        _session = session;
        _input = session.InputMetadata.Keys.First();
        _outputs = session.OutputMetadata.Keys.ToArray();
    }

    public List<FaceBox> Detect(RgbImage image, float threshold = 0.5f)
    {
        var (boxed, scale) = image.Letterbox(Size, 0);
        var input = new DenseTensor<float>([1, 3, Size, Size]);
        var plane = Size * Size;
        var buffer = input.Buffer.Span;
        for (var p = 0; p < plane; p++)
            for (var c = 0; c < 3; c++)
                buffer[c * plane + p] = (boxed.Data[p * 3 + c] - 127.5f) / 128f;

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
        var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());
        var boxes = new List<Box>();
        var scores = new List<float>();
        var marks = new List<float[]>();
        for (var s = 0; s < Strides.Length; s++)
        {
            var stride = Strides[s];
            var score = byName[_outputs[s]];
            var dist = byName[_outputs[s + 3]];
            var kps = byName[_outputs[s + 6]];
            var cells = Size / stride;
            for (var i = 0; i < score.Length; i++)
            {
                if (score[i] < threshold) continue;
                var cell = i / 2; // two anchors per location
                var cx = (cell % cells) * stride;
                var cy = (cell / cells) * stride;
                var x1 = cx - dist[i * 4] * stride;
                var y1 = cy - dist[i * 4 + 1] * stride;
                var x2 = cx + dist[i * 4 + 2] * stride;
                var y2 = cy + dist[i * 4 + 3] * stride;
                boxes.Add(new Box(x1 / scale, y1 / scale, (x2 - x1) / scale, (y2 - y1) / scale));
                scores.Add(score[i]);
                var lm = new float[10];
                for (var k = 0; k < 5; k++)
                {
                    lm[k * 2] = (cx + kps[i * 10 + k * 2] * stride) / scale;
                    lm[k * 2 + 1] = (cy + kps[i * 10 + k * 2 + 1] * stride) / scale;
                }
                marks.Add(lm);
            }
        }
        return Nms.Run(boxes, scores, 0.4f).Select(i => new FaceBox(boxes[i], scores[i], marks[i])).ToList();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>ArcFace R50 (buffalo_l w600k_r50): a face aligned to the 112x112 template by its landmarks.</summary>
internal sealed class FaceEmbedder : IDisposable
{
    private static readonly float[] Template =
        [38.2946f, 51.6963f, 73.5318f, 51.5014f, 56.0252f, 71.7366f, 41.5493f, 92.3655f, 70.7299f, 92.2041f];

    private readonly InferenceSession _session;
    private readonly string _input;

    public FaceEmbedder(InferenceSession session)
    {
        _session = session;
        _input = session.InputMetadata.Keys.First();
    }

    /// <summary>
    /// Similarity transform (scale, rotation, shift) from the landmarks to the template, least squares in
    /// complex numbers: template = a * landmarks + b. The face is sampled through the inverse.
    /// </summary>
    public static RgbImage Align(RgbImage image, float[] landmarks)
    {
        double sx = 0, sy = 0, dx = 0, dy = 0;
        for (var k = 0; k < 5; k++)
        {
            sx += landmarks[k * 2]; sy += landmarks[k * 2 + 1];
            dx += Template[k * 2]; dy += Template[k * 2 + 1];
        }
        sx /= 5; sy /= 5; dx /= 5; dy /= 5;
        double nr = 0, ni = 0, den = 0;
        for (var k = 0; k < 5; k++)
        {
            var px = landmarks[k * 2] - sx; var py = landmarks[k * 2 + 1] - sy;
            var qx = Template[k * 2] - dx; var qy = Template[k * 2 + 1] - dy;
            // (q) * conj(p)
            nr += qx * px + qy * py;
            ni += qy * px - qx * py;
            den += px * px + py * py;
        }
        var ar = nr / den;
        var ai = ni / den;
        var br = dx - (ar * sx - ai * sy);
        var bi = dy - (ar * sy + ai * sx);
        // inverse: src = (z - b) / a
        var mag = ar * ar + ai * ai;
        var pr = ar / mag;
        var pi = -ai / mag;
        var tx = -(pr * br - pi * bi);
        var ty = -(pr * bi + pi * br);
        return image.Warp(112, 112, (float)pr, (float)-pi, (float)tx, (float)pi, (float)pr, (float)ty);
    }

    public float[] Embed(RgbImage aligned)
    {
        var input = new DenseTensor<float>([1, 3, 112, 112]);
        var plane = 112 * 112;
        var buffer = input.Buffer.Span;
        for (var p = 0; p < plane; p++)
            for (var c = 0; c < 3; c++)
                buffer[c * plane + p] = (aligned.Data[p * 3 + c] - 127.5f) / 127.5f;
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
        return Nms.Normalize(results[0].AsTensor<float>().ToArray());
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>YOLOX-s (COCO, Apache 2.0), only the person class: letterbox with 114, BGR 0-255, grid decoding.</summary>
internal sealed class PersonDetector : IDisposable
{
    private const int Size = 640;
    private static readonly int[] Strides = [8, 16, 32];
    private readonly InferenceSession _session;
    private readonly string _input;

    public PersonDetector(InferenceSession session)
    {
        _session = session;
        _input = session.InputMetadata.Keys.First();
    }

    public List<(Box Box, float Score)> Detect(RgbImage image, float threshold = 0.45f)
    {
        var (boxed, scale) = image.Letterbox(Size, 114);
        var input = new DenseTensor<float>([1, 3, Size, Size]);
        var plane = Size * Size;
        var buffer = input.Buffer.Span;
        for (var p = 0; p < plane; p++)
        {
            buffer[p] = boxed.Data[p * 3 + 2];
            buffer[plane + p] = boxed.Data[p * 3 + 1];
            buffer[2 * plane + p] = boxed.Data[p * 3];
        }
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
        var o = results[0].AsTensor<float>().ToArray();
        const int stride85 = 85;
        var boxes = new List<Box>();
        var scores = new List<float>();
        var row = 0;
        foreach (var stride in Strides)
        {
            var cells = Size / stride;
            for (var gy = 0; gy < cells; gy++)
            for (var gx = 0; gx < cells; gx++, row++)
            {
                var b = row * stride85;
                var score = o[b + 4] * o[b + 5]; // objectness * person
                if (score < threshold) continue;
                var cx = (o[b] + gx) * stride;
                var cy = (o[b + 1] + gy) * stride;
                var w = MathF.Exp(o[b + 2]) * stride;
                var h = MathF.Exp(o[b + 3]) * stride;
                boxes.Add(new Box((cx - w / 2) / scale, (cy - h / 2) / scale, w / scale, h / scale));
                scores.Add(score);
            }
        }
        return Nms.Run(boxes, scores, 0.45f).Select(i => (boxes[i], scores[i])).ToList();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>OSNet-AIN x1.0 (MSMT17, MIT): appearance of a whole person (clothes, build) for re-identification.</summary>
internal sealed class BodyEmbedder : IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];
    private readonly InferenceSession _session;
    private readonly string _input;

    public BodyEmbedder(InferenceSession session)
    {
        _session = session;
        _input = session.InputMetadata.Keys.First();
    }

    public float[][] Embed(IReadOnlyList<RgbImage> crops)
    {
        if (crops.Count == 0) return [];
        const int w = 128, h = 256;
        var plane = w * h;
        var input = new DenseTensor<float>([crops.Count, 3, h, w]);
        var buffer = input.Buffer.Span;
        for (var n = 0; n < crops.Count; n++)
        {
            var data = crops[n].Data;
            var offset = n * 3 * plane;
            for (var p = 0; p < plane; p++)
                for (var c = 0; c < 3; c++)
                    buffer[offset + c * plane + p] = (data[p * 3 + c] / 255f - Mean[c]) / Std[c];
        }
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
        var flat = results[0].AsTensor<float>().ToArray();
        var dim = flat.Length / crops.Count;
        return Enumerable.Range(0, crops.Count).Select(n => Nms.Normalize(flat.AsSpan(n * dim, dim))).ToArray();
    }

    public void Dispose() => _session.Dispose();
}
