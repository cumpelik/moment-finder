namespace MediaLens.Analysis;

public sealed record ModelFile(string Name, string Url, long Size);

/// <summary>A model is a folder of files under AppPaths.ModelDir, downloaded once; everything runs offline afterwards.</summary>
public sealed record ModelInfo(string Id, string Title, string License, IReadOnlyList<ModelFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

public static class ModelCatalog
{
    private const string Siglip2Repo = "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/";

    public static readonly ModelInfo Siglip2Base = new(
        "siglip2-base-patch16-224",
        "SigLIP 2 base (search by description)",
        "Apache-2.0",
        [
            new("vision_model.onnx", Siglip2Repo + "onnx/vision_model.onnx", 371_807_752),
            new("text_model_quantized.onnx", Siglip2Repo + "onnx/text_model_quantized.onnx", 283_438_275),
            new("tokenizer.model", Siglip2Repo + "tokenizer.model", 4_241_003),
        ]);
}
