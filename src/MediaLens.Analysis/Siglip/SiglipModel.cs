using System.Numerics.Tensors;
using MediaLens.Media;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace MediaLens.Analysis.Siglip;

/// <summary>
/// Google SigLIP 2 (Apache 2.0): images and text of many languages, Czech included, in one space.
/// Vision encoder on the accelerator, the text encoder (one short query at a time) on the CPU.
/// Preprocessing as in the Hugging Face processor: image stretched to 224x224, scaled to [-1, 1];
/// text lower-cased, Gemma SentencePiece, EOS appended, padded to 64 tokens.
/// </summary>
public sealed class SiglipModel : ISceneModel, IDisposable
{
    private const int ImageSize = 224;
    private const int TextLength = 64;
    private const int PadId = 0;

    private readonly InferenceSession _vision;
    private readonly InferenceSession _text;
    private readonly Tokenizer _tokenizer;
    private readonly string _visionInput;
    private readonly string _visionOutput;
    private readonly string _textInput;
    private readonly string _textOutput;
    private readonly object _visionLock = new();

    public string ModelId => ModelCatalog.Siglip2Base.Id;
    public int Dimension { get; }
    public string Device { get; }

    public SiglipModel(string modelDir)
    {
        (_vision, Device) = OrtSessions.Create(Path.Combine(modelDir, "vision_model.onnx"), preferAccelerator: true);
        (_text, _) = OrtSessions.Create(Path.Combine(modelDir, "text_model_quantized.onnx"), preferAccelerator: false);
        using (var proto = File.OpenRead(Path.Combine(modelDir, "tokenizer.model")))
            _tokenizer = SentencePieceTokenizer.Create(proto, false, true); // no BOS, EOS

        _visionInput = _vision.InputMetadata.Keys.First();
        _visionOutput = PickEmbeddingOutput(_vision);
        _textInput = _text.InputMetadata.Keys.First(k => k.Contains("input_ids", StringComparison.Ordinal));
        _textOutput = PickEmbeddingOutput(_text);
        Dimension = _vision.OutputMetadata[_visionOutput].Dimensions[^1];
    }

    /// <summary>The pooled embedding (pooler_output), not the per-patch hidden states.</summary>
    private static string PickEmbeddingOutput(InferenceSession s) =>
        s.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("pooler", StringComparison.Ordinal) || k.Contains("embeds", StringComparison.Ordinal))
        ?? s.OutputMetadata.First(kv => kv.Value.Dimensions.Length == 2).Key;

    public float[][] EmbedImages(IReadOnlyList<string> imagePaths)
    {
        var n = imagePaths.Count;
        if (n == 0) return [];
        var pixels = new float[n * 3 * ImageSize * ImageSize];
        var plane = ImageSize * ImageSize;
        Parallel.For(0, n, i =>
        {
            byte[] rgb;
            try
            {
                rgb = ImageCodec.DecodeRgbStretched(imagePaths[i], ImageSize, ImageSize);
            }
            catch (ImageMagick.MagickException)
            {
                rgb = new byte[plane * 3]; // unreadable: a black image, it will not match anything
            }
            var offset = i * 3 * plane;
            for (var p = 0; p < plane; p++)
            {
                pixels[offset + p] = rgb[p * 3] / 127.5f - 1f;
                pixels[offset + plane + p] = rgb[p * 3 + 1] / 127.5f - 1f;
                pixels[offset + 2 * plane + p] = rgb[p * 3 + 2] / 127.5f - 1f;
            }
        });

        var input = new DenseTensor<float>(pixels, [n, 3, ImageSize, ImageSize]);
        lock (_visionLock)
        {
            using var results = _vision.Run([NamedOnnxValue.CreateFromTensor(_visionInput, input)], [_visionOutput]);
            return SplitNormalized(results[0].AsTensor<float>(), n);
        }
    }

    public float[] EmbedText(string text)
    {
        var ids = _tokenizer.EncodeToIds(text.Trim().ToLowerInvariant());
        var tokens = new long[TextLength];
        Array.Fill(tokens, PadId);
        var count = Math.Min(ids.Count, TextLength);
        for (var i = 0; i < count; i++) tokens[i] = ids[i];
        if (ids.Count > TextLength) tokens[^1] = ids[^1]; // keep the EOS of a long text
        var input = new DenseTensor<long>(tokens, [1, TextLength]);
        lock (_text)
        {
            using var results = _text.Run([NamedOnnxValue.CreateFromTensor(_textInput, input)], [_textOutput]);
            return SplitNormalized(results[0].AsTensor<float>(), 1)[0];
        }
    }

    private static float[][] SplitNormalized(Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> output, int n)
    {
        var flat = output.ToArray();
        var dim = flat.Length / n;
        var result = new float[n][];
        for (var i = 0; i < n; i++)
        {
            var v = flat.AsSpan(i * dim, dim).ToArray();
            var norm = TensorPrimitives.Norm(v);
            if (norm > 0) TensorPrimitives.Divide(v, norm, v);
            result[i] = v;
        }
        return result;
    }

    public void Dispose()
    {
        _vision.Dispose();
        _text.Dispose();
    }
}
