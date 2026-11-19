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

    /// <summary>InsightFace buffalo_l (as mirrored by Immich). Weights for non-commercial research use only.</summary>
    public static readonly ModelInfo Faces = new(
        "buffalo_l",
        "InsightFace buffalo_l (face detection and recognition)",
        "non-commercial",
        [
            new("det_10g.onnx", "https://huggingface.co/immich-app/buffalo_l/resolve/main/detection/model.onnx", 16_923_827),
            new("w600k_r50.onnx", "https://huggingface.co/immich-app/buffalo_l/resolve/main/recognition/model.onnx", 174_383_860),
        ]);

    public static readonly ModelInfo Persons = new(
        "yolox_s",
        "YOLOX-s (person detection)",
        "Apache-2.0",
        [new("yolox_s.onnx", "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_s.onnx", 35_858_002)]);

    public static readonly ModelInfo Reid = new(
        "osnet_ain_x1_0",
        "OSNet-AIN x1.0 MSMT17 (recognizing people by clothes)",
        "MIT",
        [new("osnet_ain_x1_0_msmt17.onnx", "https://huggingface.co/mprib/osnet-ain-x1-0-msmt17-onnx/resolve/main/osnet_ain_x1_0_msmt17.onnx", 8_742_442)]);

    public static readonly IReadOnlyList<ModelInfo> PeopleModels = [Faces, Persons, Reid];
    public static readonly IReadOnlyList<ModelInfo> All = [Siglip2Base, Faces, Persons, Reid];
}
