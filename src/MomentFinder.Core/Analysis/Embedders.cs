namespace MomentFinder.Analysis;

/// <summary>Image and text in one embedding space (SigLIP): text search over photos and video frames.</summary>
public interface ISceneModel
{
    string ModelId { get; }
    int Dimension { get; }
    /// <summary>Which hardware runs the model, for the status bar (DirectML, CoreML, CUDA, CPU).</summary>
    string Device { get; }
    /// <summary>L2 normalized embeddings of image files (thumbnails or extracted frames), in the same order.</summary>
    float[][] EmbedImages(IReadOnlyList<string> imagePaths);
    /// <summary>L2 normalized embedding of a search phrase.</summary>
    float[] EmbedText(string text);
}
