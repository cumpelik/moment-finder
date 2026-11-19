using MediaLens.Analysis;
using MediaLens.Media;
using MediaLens.People;
using MediaLens.Vectors;

namespace MediaLens.Library;

public enum IndexerPhase
{
    Idle,
    Scanning,
    Thumbnails,
    Analysis,
    People,
}

public sealed record IndexerStatus(IndexerPhase Phase, string Message, int Done, int Total, bool Paused);

/// <summary>
/// Background work after a source is added or rescanned: read new files, make thumbnails, run the image analysis.
/// Runs until everything is done, then waits for the next request. Pausing stops between items.
/// </summary>
public sealed class Indexer : IDisposable
{
    /// <summary>Raised when the pipeline changes so old results must be computed again.</summary>
    public const int AnalysisVersion = 1;
    public const int PeopleVersion = 1;

    private readonly AppPaths _paths;
    private readonly MediaRepository _repo;
    private readonly LibraryScanner _scanner;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _scanRequested;
    private volatile bool _paused;
    private Task? _loop;

    private ISceneModel? _sceneModel;
    private IVectorIndex? _sceneIndex;
    private PeopleAnalyzer? _people;
    private IdentityService? _identity;
    private EventBuilder? _events;
    private bool _identityDirty;
    private int _sinceIdentity;

    public event Action<IndexerStatus>? StatusChanged;
    /// <summary>New items in the index or new thumbnails: the grid can refresh.</summary>
    public event Action? LibraryChanged;

    public IndexerStatus Status { get; private set; } = new(IndexerPhase.Idle, "", 0, 0, false);

    public Indexer(AppPaths paths, MediaRepository repo)
    {
        _paths = paths;
        _repo = repo;
        _scanner = new LibraryScanner(repo);
    }

    /// <summary>Turns on the image analysis (after the model is downloaded).</summary>
    public void AttachSceneModel(ISceneModel model, IVectorIndex index)
    {
        _sceneModel = model;
        _sceneIndex = index;
        _wake.Release();
    }

    /// <summary>Turns on the people analysis (faces, bodies) and the identity matching.</summary>
    public void AttachPeople(PeopleAnalyzer analyzer, IdentityService identity, EventBuilder events)
    {
        _people = analyzer;
        _identity = identity;
        _events = events;
        events.Rebuild();
        _identityDirty = true;
        _wake.Release();
    }

    /// <summary>After the user named or corrected someone: match everything again in the background.</summary>
    public void RequestIdentity()
    {
        _identityDirty = true;
        _wake.Release();
    }

    public void Start(bool scanNow)
    {
        _scanRequested = scanNow;
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
        _wake.Release();
    }

    public void RequestScan()
    {
        _scanRequested = true;
        _wake.Release();
    }

    /// <summary>New thumbnails and analyses are processed without a scan.</summary>
    public void Wake() => _wake.Release();

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Report(Status with { Paused = value });
            if (!value) _wake.Release();
        }
    }

    private void Report(IndexerStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await StepAsync(ct))
                {
                    Report(new IndexerStatus(IndexerPhase.Idle, _paused ? "Paused" : "Up to date", 0, 0, _paused));
                    await _wake.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Report(new IndexerStatus(IndexerPhase.Idle, "Error: " + e.Message, 0, 0, _paused));
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /// <summary>One unit of work; false when there is nothing to do.</summary>
    private async Task<bool> StepAsync(CancellationToken ct)
    {
        if (_paused) return false;
        if (_scanRequested)
        {
            _scanRequested = false;
            await ScanAllAsync(ct);
            return true;
        }
        if (await ThumbnailBatchAsync(ct) || await AnalysisBatchAsync(ct) || await PeopleBatchAsync(ct))
            return true;
        if (_identityDirty && _identity != null)
        {
            _identityDirty = false;
            _sinceIdentity = 0;
            Report(new IndexerStatus(IndexerPhase.People, "Matching people", 0, 0, false));
            await Task.Run(() => _identity.Recompute(), ct);
            return true;
        }
        return false;
    }

    /// <summary>For the command line: does all the work in the calling task (do not combine with Start).</summary>
    public async Task RunUntilIdleAsync(bool scan, CancellationToken ct)
    {
        _scanRequested = scan;
        while (await StepAsync(ct)) { }
    }

    private async Task ScanAllAsync(CancellationToken ct)
    {
        try
        {
            await ScanSourcesAsync(ct);
        }
        finally
        {
            if (_events != null)
            {
                _events.Rebuild();
                _identityDirty = true;
            }
        }
    }

    private async Task ScanSourcesAsync(CancellationToken ct)
    {
        foreach (var source in _repo.ListSources().Where(s => s.Enabled))
        {
            Report(new IndexerStatus(IndexerPhase.Scanning, $"Scanning {source.Name}", 0, 0, false));
            var progress = new Progress<string>(m => Report(new IndexerStatus(IndexerPhase.Scanning, m, 0, 0, false)));
            try
            {
                await _scanner.ScanAsync(source, progress, ct);
            }
            catch (DirectoryNotFoundException)
            {
                // Disconnected disk: its items stay, just are not updated.
            }
            LibraryChanged?.Invoke();
        }
    }

    private async Task<bool> ThumbnailBatchAsync(CancellationToken ct)
    {
        var batch = _repo.PendingThumbs(64);
        if (batch.Count == 0) return false;
        var stats = _repo.Stats(AnalysisVersion);
        var done = (int)stats.Thumbnailed;
        await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = ct },
            async (item, token) =>
            {
                await EnsureThumbnailAsync(item, token, regenerate: true);
                var n = Interlocked.Increment(ref done);
                Report(new IndexerStatus(IndexerPhase.Thumbnails, "Thumbnails", n, (int)stats.Total, false));
            });
        LibraryChanged?.Invoke();
        return true;
    }

    private readonly SemaphoreSlim _thumbGate = new(Math.Max(2, Environment.ProcessorCount / 2));

    /// <summary>Also called by the grid for an item it shows before the indexer got to it.</summary>
    public async Task<string?> EnsureThumbnailAsync(MediaItem item, CancellationToken ct, bool regenerate = false)
    {
        var path = _paths.ThumbPath(item.Id);
        if (!regenerate && File.Exists(path)) return path;
        await _thumbGate.WaitAsync(ct);
        try
        {
            if (!regenerate && File.Exists(path)) return path;
            bool ok;
            try
            {
                ok = await ImageCodec.CreateThumbnailAsync(item, path, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ok = false;
            }
            _repo.SetThumbState(item.Id, ok ? ThumbStates.Ready : ThumbStates.Failed);
            return ok ? path : null;
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    private async Task<bool> AnalysisBatchAsync(CancellationToken ct)
    {
        if (_sceneModel is not { } model || _sceneIndex is not { } index) return false;
        var batch = _repo.PendingAnalysis(AnalysisVersion, 16);
        if (batch.Count == 0) return false;
        var stats = _repo.Stats(AnalysisVersion);
        var done = (int)stats.Analyzed;
        var device = model.Device;

        var photos = batch.Where(i => !i.IsVideo).ToList();
        if (photos.Count > 0)
        {
            var vectors = await Task.Run(() => model.EmbedImages(photos.Select(p => _paths.ThumbPath(p.Id)).ToList()), ct);
            for (var i = 0; i < photos.Count; i++)
            {
                index.Put(photos[i].Id, [new VectorRow(photos[i].Id, 0, vectors[i])]);
                _repo.SetAnalysisVersion(photos[i].Id, AnalysisVersion);
            }
            done += photos.Count;
            Report(new IndexerStatus(IndexerPhase.Analysis, $"Analyzing ({device})", done, (int)stats.Total, false));
        }

        foreach (var video in batch.Where(i => i.IsVideo))
        {
            if (_paused) break;
            await AnalyzeVideoAsync(video, model, index, ct);
            _repo.SetAnalysisVersion(video.Id, AnalysisVersion);
            Report(new IndexerStatus(IndexerPhase.Analysis, $"Analyzing ({device})", ++done, (int)stats.Total, false));
        }
        return true;
    }

    private async Task<bool> PeopleBatchAsync(CancellationToken ct)
    {
        if (_people is not { } people) return false;
        var batch = _repo.PendingPeople(PeopleVersion, 8);
        if (batch.Count == 0) return false;
        var stats = _repo.Stats(AnalysisVersion);
        var done = (int)stats.PeopleAnalyzed;
        foreach (var item in batch)
        {
            if (_paused) break;
            try
            {
                await people.AnalyzeAsync(item, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Unreadable file: it stays without people, the rest goes on.
            }
            _repo.SetPeopleVersion(item.Id, PeopleVersion);
            Report(new IndexerStatus(IndexerPhase.People, $"Looking for people ({people.Model.Device})", ++done, (int)stats.Total, false));
            // Match from time to time, so names spread while a large library is still being analyzed.
            if (++_sinceIdentity >= 300) _identityDirty = true;
        }
        _identityDirty = true;
        if (_sinceIdentity >= 300 && _identity != null)
        {
            _sinceIdentity = 0;
            _identityDirty = false;
            await Task.Run(() => _identity.Recompute(), ct);
        }
        return true;
    }

    private async Task AnalyzeVideoAsync(MediaItem video, ISceneModel model, IVectorIndex index, CancellationToken ct)
    {
        var duration = (video.DurationMs ?? 0) / 1000.0;
        // About 40 frames per video, at least every 30 s, at most every 2 s.
        var interval = Math.Clamp(duration / 40, 2, 30);
        var dir = Path.Combine(_paths.CacheDir, "frames", video.Id.ToString());
        try
        {
            var frames = duration > 0
                ? await FFmpeg.ExtractFramesAsync(video.FullPath, interval, 60, dir, 256, ct)
                : [];
            if (frames.Length == 0)
            {
                // Unknown length or no keyframes found: the thumbnail stands for the video.
                var thumb = _paths.ThumbPath(video.Id);
                if (File.Exists(thumb))
                    index.Put(video.Id, [new VectorRow(video.Id, 0, (await Task.Run(() => model.EmbedImages([thumb]), ct))[0])]);
                return;
            }
            var rows = new List<VectorRow>();
            foreach (var chunk in frames.Chunk(8))
            {
                var vectors = await Task.Run(() => model.EmbedImages(chunk), ct);
                for (var i = 0; i < chunk.Length; i++)
                {
                    var n = int.Parse(Path.GetFileNameWithoutExtension(chunk[i])) - 1;
                    rows.Add(new VectorRow(video.Id, (int)(n * interval * 1000), vectors[i]));
                }
            }
            index.Put(video.Id, rows);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _stop.Dispose();
    }
}
