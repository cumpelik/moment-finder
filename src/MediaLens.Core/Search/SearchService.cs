using MediaLens.Analysis;
using MediaLens.Library;
using MediaLens.Vectors;

namespace MediaLens.Search;

/// <summary>A found item; for a video, TimeMs is where the best matching frame is.</summary>
public sealed record SearchHit(MediaItem Item, float Score, int TimeMs);

public sealed class SearchService
{
    private readonly MediaRepository _repo;
    private ISceneModel? _model;
    private IVectorIndex? _index;

    /// <summary>At most this many results of a text search.</summary>
    public int MaxTextResults { get; set; } = 300;

    public SearchService(MediaRepository repo) => _repo = repo;

    /// <summary>Names the search box recognizes; refreshed when people are named or renamed.</summary>
    public IReadOnlyList<NamedPerson> People { get; set; } = [];

    /// <summary>For videos found by a person: where the person appears first.</summary>
    public Func<IReadOnlyList<long>, Dictionary<long, int>>? FirstAppearance { get; set; }

    public void AttachSceneModel(ISceneModel model, IVectorIndex index)
    {
        _model = model;
        _index = index;
    }

    public bool CanSearchText => _model != null && _index != null;

    /// <summary>Translates a phrase into English (the search model understands English far better); null until installed.</summary>
    public Func<string, string>? Translate { get; set; }

    /// <summary>The phrase the search model gets for this text: the English translation when it is available.</summary>
    public string QueryPhrase(string text) => Translate is { } t ? t(text) : text;

    /// <summary>
    /// Filters by the scope (source, bucket) and the filter words; with text, orders by similarity to the text
    /// and keeps the results close to the best one. Without text, newest first (bucket: bucket order).
    /// </summary>
    public List<SearchHit> Search(string? input, MediaFilter scope)
    {
        var query = QueryParser.Parse(input, People);
        var filter = QueryParser.ToFilter(query, scope);
        var items = _repo.Query(filter);
        if (!string.IsNullOrWhiteSpace(query.Text) && !CanSearchText)
        {
            // Without the model the words can only match the file and folder names.
            var words = QueryParser.Normalize(query.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(i =>
            {
                var path = QueryParser.Normalize(i.RelPath.Replace('_', ' '));
                return words.All(w => path.Contains(w, StringComparison.Ordinal));
            }).ToList();
        }
        if (string.IsNullOrWhiteSpace(query.Text) || _model is not { } model || _index is not { } index)
        {
            var starts = filter.PersonIds.Count > 0 && FirstAppearance != null ? FirstAppearance(filter.PersonIds) : [];
            return items.Select(i => new SearchHit(i, 0, i.IsVideo ? starts.GetValueOrDefault(i.Id) : 0)).ToList();
        }

        var byId = items.ToDictionary(i => i.Id);
        var vector = Translate is { } translate ? model.EmbedText(translate(query.Text)) : EmbedQuery(model, query.Text);
        var hits = index.Search(vector, byId.Keys.ToHashSet(), MaxTextResults);
        if (hits.Count == 0) return [];

        // SigLIP similarities of a good match are around 0.1-0.2; what is far below the best is noise.
        var cutoff = hits[0].Score - RelativeCutoff;
        return hits.Where(h => h.Score >= cutoff && h.Score >= AbsoluteFloor)
            .Select(h => new SearchHit(byId[h.MediaId], h.Score, h.TimeMs))
            .ToList();
    }

    /// <summary>
    /// Without the translator: the phrase alone and as a photo caption, averaged. A single Czech word ("dort",
    /// "moře") alone is a weak query, as a caption it works better; still far behind the English translation
    /// (on a labeled sample of 43 photos, precision 0.2 against 0.88 with the translation).
    /// </summary>
    internal static float[] EmbedQuery(ISceneModel model, string text)
    {
        string[] templates = ["{0}", "fotografie: {0}"];
        var sum = new float[model.Dimension];
        foreach (var t in templates)
            System.Numerics.Tensors.TensorPrimitives.Add(sum, model.EmbedText(string.Format(t, text)), sum);
        var norm = System.Numerics.Tensors.TensorPrimitives.Norm(sum);
        System.Numerics.Tensors.TensorPrimitives.Divide(sum, norm, sum);
        return sum;
    }

    /// <summary>
    /// Results this far below the best one are dropped. With English queries a match scores about 0.1 to 0.15
    /// and the rest 0.03 to 0.07; 0.04 kept precision 0.88 and recall 0.80 on the labeled sample.
    /// </summary>
    public float RelativeCutoff { get; set; } = 0.04f;
    public float AbsoluteFloor { get; set; } = 0.02f;
}
