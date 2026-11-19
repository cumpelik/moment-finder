namespace MediaLens.Analysis;

/// <summary>Downloads models (resumable, size checked) into AppPaths.ModelDir.</summary>
public sealed class ModelManager
{
    private readonly string _modelDir;
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public ModelManager(string modelDir) => _modelDir = modelDir;

    public string DirOf(ModelInfo model) => Path.Combine(_modelDir, model.Id);

    public bool IsInstalled(ModelInfo model) =>
        model.Files.All(f => new FileInfo(Path.Combine(DirOf(model), f.Name)) is { Exists: true } fi && fi.Length == f.Size);

    /// <summary>Progress reports downloaded and total bytes of the whole model.</summary>
    public async Task DownloadAsync(ModelInfo model, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var dir = DirOf(model);
        Directory.CreateDirectory(dir);
        long before = 0;
        foreach (var file in model.Files)
        {
            var target = Path.Combine(dir, file.Name);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == file.Size)
            {
                before += file.Size;
                progress?.Report((before, model.TotalSize));
                continue;
            }
            var part = target + ".part";
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;

            await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[1 << 20];
                var done = have;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report((before + done, model.TotalSize));
                }
            }
            if (new FileInfo(part).Length != file.Size)
            {
                File.Delete(part);
                throw new IOException($"{file.Name}: unexpected size, the download will start again");
            }
            File.Move(part, target, true);
            before += file.Size;
        }
    }
}
