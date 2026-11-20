using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace MediaLens.Analysis.Translation;

/// <summary>
/// Helsinki-NLP OPUS-MT (Marian, Apache 2.0): translates a search phrase into English, which the search
/// model understands much better than Czech. Source SentencePiece pieces are looked up in the shared
/// vocabulary, the decoder runs greedily on the CPU (a query is a few words, the cache is not worth it).
/// </summary>
public sealed class MarianTranslator : IDisposable
{
    private const int MaxTokens = 48;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly Tokenizer _source;
    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _pieces;
    private readonly int _eos;
    private readonly int _pad;
    private readonly int _unk;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public MarianTranslator(string modelDir)
    {
        (_encoder, _) = OrtSessions.Create(Path.Combine(modelDir, "encoder_model_quantized.onnx"), preferAccelerator: false);
        (_decoder, _) = OrtSessions.Create(Path.Combine(modelDir, "decoder_model_quantized.onnx"), preferAccelerator: false);
        using (var spm = File.OpenRead(Path.Combine(modelDir, "source.spm")))
            _source = SentencePieceTokenizer.Create(spm, false, false);
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(modelDir, "vocab.json")))!;
        _pieces = new string[_vocab.Values.Max() + 1];
        foreach (var (piece, id) in _vocab) _pieces[id] = piece;
        _eos = _vocab["</s>"];
        _pad = _vocab["<pad>"];
        _unk = _vocab["<unk>"];
    }

    public string Translate(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return text;
        lock (_cache)
        {
            if (_cache.TryGetValue(text, out var cached)) return cached;
            var result = Run(text);
            _cache[text] = result;
            return result;
        }
    }

    private string Run(string text)
    {
        var ids = _source.EncodeToTokens(text, out _).Select(t => _vocab.GetValueOrDefault(t.Value, _unk)).Append(_eos).Select(i => (long)i).ToArray();
        var mask = Enumerable.Repeat(1L, ids.Length).ToArray();
        var shape = new[] { 1, ids.Length };

        using var encoded = _encoder.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, shape)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, shape)),
        ]);
        var hidden = encoded[0].AsTensor<float>().ToDenseTensor();

        var output = new List<long> { _pad }; // the decoder starts from the pad token
        for (var step = 0; step < MaxTokens; step++)
        {
            using var decoded = _decoder.Run([
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(output.ToArray(), [1, output.Count])),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", new DenseTensor<long>(mask, shape)),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
            ]);
            var logits = decoded[0].AsTensor<float>();
            var vocabSize = logits.Dimensions[^1];
            var last = output.Count - 1;
            var best = -1;
            var bestScore = float.NegativeInfinity;
            for (var v = 0; v < vocabSize; v++)
            {
                if (v == _pad) continue;
                var s = logits[0, last, v];
                if (s > bestScore) (best, bestScore) = (v, s);
            }
            if (best == _eos) break;
            output.Add(best);
        }

        var sb = new StringBuilder();
        foreach (var id in output.Skip(1))
            if (id < _pieces.Length && _pieces[id] is { } p) sb.Append(p);
        return sb.ToString().Replace('▁', ' ').Trim();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
