using MomentFinder.Analysis;
using MomentFinder.Media;
using MomentFinder.People;
using MomentFinder.Vectors;

namespace MomentFinder.Library;

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
    /// <summary>2: video frames at their real times (they were half an interval later).</summary>
    public const int AnalysisVersion = 2;
    /// <summary>
    /// 2: a video track keeps its first and last detection. 3: the path of every track (track_box).
    /// 4: video frames at their real times (before, half an interval later than stored).
    /// 5: tracks continue through low confidence views (a fall) and changed poses.
    /// </summary>
    public const int PeopleVersion = 6;

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
    /// <summary>The user named or corrected someone (or the app started): matched again soon, not after all the analysis.</summary>
    private bool _identityWanted;
    private DateTime _lastIdentity = DateTime.MinValue;
    private int _sinceIdentity;

    public event Action<IndexerStatus>? StatusChanged;
    /// <summary>New items in the index or new thumbnails: the grid can refresh.</summary>
    public event Action? LibraryChanged;

    public IndexerStatus Status { get; private set; } = new(IndexerPhase.Idle, "", 0, 0, false);

    public Indexer(AppPaths paths, MediaRepository repo, Cloud.Rclone? rclone = null)
    {
        _paths = paths;
        _repo = repo;
        _scanner = new LibraryScanner(repo, rclone) { BatchRead = source => MomentFinder.Sync.Catalog.Publish(repo.Db, source.Id) };
        _twins = new AnalysisTwin(paths, repo.Db);
    }

    private readonly AnalysisTwin _twins;

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
        _identityWanted = true;
        _wake.Release();
    }

    /// <summary>After the user named or corrected someone: match everything again in the background.</summary>
    public void RequestIdentity()
    {
        _identityDirty = true;
        _identityWanted = true;
        _wake.Release();
    }

    public void Start(bool scanNow)
    {
        _scanRequested = scanNow;
        _loop ??= Task.Run(() => LoopAsync(_stop.Token));
        _wake.Release();
    }

    /// <summary>A source the user's own changes in the cloud are working in now (uploads, moves): not scanned meanwhile.</summary>
    public Func<long, bool>? Busy { get; set; }

    /// <summary>The source being scanned now (its window and its place in the library show how far).</summary>
    public long? ScanningSourceId { get; private set; }

    public void RequestScan()
    {
        _scanRequested = true;
        _wake.Release();
    }

    private readonly HashSet<long> _scanSources = [];
    private readonly HashSet<long> _scanLater = [];

    /// <summary>Scans one source (a folder that changed); the others are not listed again.</summary>
    public void RequestScan(long sourceId)
    {
        lock (_scanSources) _scanSources.Add(sourceId);
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
            // A 360 copy takes minutes, the first reading of a folder in the cloud hours: pausing stops them now
            // (they go on later from where they were).
            if (value)
            {
                try
                {
                    _proxyCts?.Cancel();
                    _scanCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Just finished by itself.
                }
            }
            Report(Status with { Paused = value });
            if (!value) _wake.Release();
        }
    }

    private void Report(IndexerStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Waited for before any work: in a shared library, the first exchange with the other computers (what they made
    /// already is taken, not downloaded and computed again here).
    /// </summary>
    public Func<CancellationToken, Task>? BeforeWork { get; set; }

    private async Task LoopAsync(CancellationToken ct)
    {
        if (BeforeWork != null)
        {
            Report(new IndexerStatus(IndexerPhase.Scanning, "Taking in what the other computers of the library did", 0, 0, false));
            try
            {
                await BeforeWork(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Not answered: the work goes on, what is missing is made here.
            }
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await StepAsync(ct))
                {
                    Report(new IndexerStatus(IndexerPhase.Idle, _paused ? "Paused: press the button on the left to go on" : "Everything is ready", 0, 0, _paused));
                    // Everything is analyzed: cloud files fetched for it may leave the cache.
                    Cloud.MediaFiles.TrimCache();
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
        // What the other computers of the library made already comes first: a whole library of it is here in minutes
        // and can be looked at and searched then. Reading new files from the cloud may take hours.
        if (await SharedBatchAsync(ct)) return true;
        if (_scanRequested)
        {
            _scanRequested = false;
            lock (_scanSources) _scanSources.Clear();
            await ScanAllAsync(null, ct);
            return true;
        }
        long[] only;
        lock (_scanSources)
        {
            // Left out of a scan while the user's changes were working in them: read now that those are done.
            _scanSources.UnionWith(_scanLater.Where(id => Busy?.Invoke(id) != true).ToList());
            _scanLater.ExceptWith(_scanSources);
            only = [.. _scanSources];
            _scanSources.Clear();
        }
        if (only.Length > 0)
        {
            await ScanAllAsync(only, ct);
            return true;
        }
        // Named or corrected by the user: matched again between the batches of a long analysis (at most twice a
        // minute, a review gives many answers in a row), so the names show while the library is still being analyzed.
        var wanted = _identityWanted && DateTime.UtcNow - _lastIdentity > TimeSpan.FromSeconds(30);
        if (!wanted && (TwinBatch() || await ThumbnailBatchAsync(ct) || await AnalysisBatchAsync(ct) || await PeopleBatchAsync(ct)))
            return true;
        if (_identityDirty && _identity != null)
        {
            _identityDirty = false;
            _identityWanted = false;
            _sinceIdentity = 0;
            Report(new IndexerStatus(IndexerPhase.People, "Working out who is who", 0, 0, false));
            await Task.Run(() => _identity.Recompute(), ct);
            _lastIdentity = DateTime.UtcNow;
            return true;
        }
        if (Proxies360 && await Proxy360BatchAsync(ct)) return true;
        return false;
    }

    /// <summary>
    /// Thumbnails and search data another computer of the shared library made already: taken instead of computed
    /// (set by the host when the library is shared).
    /// </summary>
    public Func<CancellationToken, Task<bool>>? SharedContentBatch { get; set; }

    /// <summary>
    /// A batch of what another computer made is being fetched, and so many photos still wait for it: said while it
    /// comes (it takes a while), with how far it is. Thousands of photos come in minutes.
    /// </summary>
    public void SharedContentComing(int left)
    {
        var total = (int)_repo.Stats(AnalysisVersion).Total;
        Report(new IndexerStatus(IndexerPhase.Thumbnails, "Taking thumbnails and search data the other computers of the library made already",
            Math.Max(0, total - left), total, false));
    }

    private async Task<bool> SharedBatchAsync(CancellationToken ct)
    {
        if (SharedContentBatch == null) return false;
        try
        {
            if (!await SharedContentBatch(ct)) return false;
            LibraryChanged?.Invoke();
            return true;
        }
        catch (Cloud.RcloneException)
        {
            return false;
        }
    }

    /// <summary>The app makes light playing copies of 360 videos when everything else is done (not the command line).</summary>
    public bool Proxies360 { get; set; }

    private readonly HashSet<long> _proxyFailed = [];
    private CancellationTokenSource? _proxyCts;
    private CancellationTokenSource? _scanCts;

    /// <summary>One 360 video without its playing copy, the newest first, while the copies have room.</summary>
    private async Task<bool> Proxy360BatchAsync(CancellationToken ct)
    {
        if (!Proxy360.HasRoom) return false;
        var all360 = _repo.Query(new MediaFilter { Kind = MediaKind.Video })
            .Where(v => Fisheye360.IsDualFisheye(v.RelPath) && Cloud.MediaFiles.IsLocal(v)).ToList();
        var videos = all360.Where(v => !_proxyFailed.Contains(v.Id) && Proxy360.Ready(v) == null).ToList();
        if (videos.Count == 0) return false;
        Report(new IndexerStatus(IndexerPhase.Analysis, "Preparing 360 videos for smooth playing", all360.Count - videos.Count, all360.Count, false));
        using var cts = _proxyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var made = await Proxy360.GetAsync(videos[0], cts.Token);
        _proxyCts = null;
        if (cts.IsCancellationRequested) return !_paused;
        if (made == null) _proxyFailed.Add(videos[0].Id);
        return true;
    }

    /// <summary>For the command line: does all the work in the calling task (do not combine with Start).</summary>
    public async Task RunUntilIdleAsync(bool scan, CancellationToken ct)
    {
        _scanRequested = scan;
        while (await StepAsync(ct)) { }
    }

    /// <summary>All sources, or only these.</summary>
    private async Task ScanAllAsync(long[]? only, CancellationToken ct)
    {
        using var cts = _scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await ScanSourcesAsync(only, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Paused in the middle: asked for again, and it goes on from where it was (what was read is known).
            if (only == null) _scanRequested = true;
            else lock (_scanSources) _scanSources.UnionWith(only);
        }
        finally
        {
            _scanCts = null;
            if (_events != null)
            {
                _events.Rebuild();
                _identityDirty = true;
            }
        }
    }

    private async Task ScanSourcesAsync(long[]? only, CancellationToken ct)
    {
        foreach (var source in _repo.ListSources().Where(s => s.Enabled && (only == null || only.Contains(s.Id))))
        {
            // Another computer's backup: which files it has comes from that computer (the catalog), the same as on
            // every computer, without downloading each of them to find out.
            if (MomentFinder.Sync.Catalog.KnownFromOthers(_repo.Db, source)) continue;
            // Files are being put into it or moved in it right now: it is read when that is done.
            if (Busy?.Invoke(source.Id) == true)
            {
                lock (_scanSources) _scanLater.Add(source.Id);
                continue;
            }
            ScanningSourceId = source.Id;
            Report(new IndexerStatus(IndexerPhase.Scanning, $"Looking for new photos and videos in {source.Name}", 0, 0, false));
            // What a long scan found so far shows while it runs (a cloud folder of thousands of photos takes a while).
            var progress = new Progress<string>(m =>
            {
                var count = System.Text.RegularExpressions.Regex.Match(m, @"(\d+)/(\d+)$");
                Report(count.Success
                    ? new IndexerStatus(IndexerPhase.Scanning, m[..count.Index].TrimEnd(' ', ':'), int.Parse(count.Groups[1].Value), int.Parse(count.Groups[2].Value), false)
                    : new IndexerStatus(IndexerPhase.Scanning, m, 0, 0, false));
                LibraryChanged?.Invoke();
            });
            try
            {
                await _scanner.ScanAsync(source, progress, ct);
                // A folder that lives in the cloud: the other computers learn what was found in it.
                if (source.Kind == SourceKinds.Cloud) MomentFinder.Sync.Catalog.Publish(_repo.Db, source.Id);
            }
            catch (DirectoryNotFoundException)
            {
                // Disconnected disk: its items stay, just are not updated.
            }
            catch (Exception e) when (e is Cloud.RcloneException or IOException)
            {
                // One folder the cloud did not answer for: the others are read anyway, this one at the next scan.
                Report(new IndexerStatus(IndexerPhase.Scanning, $"{source.Name} could not be read now ({e.Message}); it is tried again later", 0, 0, false));
            }
            finally
            {
                ScanningSourceId = null;
            }
            LibraryChanged?.Invoke();
        }
    }

    /// <summary>Files known elsewhere in the library get their twin's thumbnail and analysis, without reading them.</summary>
    private bool TwinBatch()
    {
        var done = _twins.CopyBatch(_sceneIndex, 200);
        if (done == 0) return false;
        Report(new IndexerStatus(IndexerPhase.Thumbnails, $"Copies of files it knows already: {done}", 0, 0, false));
        LibraryChanged?.Invoke();
        return true;
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
                Report(new IndexerStatus(IndexerPhase.Thumbnails, "Making thumbnails", n, (int)stats.Total, false));
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
                // Where the file is on this computer: a cloud item's fetched copy, or the file it was uploaded from.
                ok = await ImageCodec.CreateThumbnailAsync(item, await Cloud.MediaFiles.LocalPathAsync(item, ct), path, ct);
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

        // 360 photos: views all around, like the frames of a 360 video.
        foreach (var photo in batch.Where(i => !i.IsVideo && Fisheye360.IsDualFisheye(i.FullPath)))
        {
            var local = await Cloud.MediaFiles.LocalPathAsync(photo, ct);
            var (lens0, lens1) = await Task.Run(() => Fisheye360.SplitSideBySide(RgbImage.Load(local, 2048)), ct);
            await Embed360Async(photo, local, [(0, lens0, lens1)], model, index, ct);
            _repo.SetAnalysisVersion(photo.Id, AnalysisVersion);
            Report(new IndexerStatus(IndexerPhase.Analysis, "Learning what is in the pictures, for the search", ++done, (int)stats.Total, false));
        }
        var photos = batch.Where(i => !i.IsVideo && !Fisheye360.IsDualFisheye(i.FullPath)).ToList();
        if (photos.Count > 0)
        {
            var vectors = await Task.Run(() => model.EmbedImages(photos.Select(p => _paths.ThumbPath(p.Id)).ToList()), ct);
            for (var i = 0; i < photos.Count; i++)
            {
                index.Put(photos[i].Id, [new VectorRow(photos[i].Id, 0, vectors[i])]);
                _repo.SetAnalysisVersion(photos[i].Id, AnalysisVersion);
            }
            done += photos.Count;
            Report(new IndexerStatus(IndexerPhase.Analysis, "Learning what is in the pictures, for the search", done, (int)stats.Total, false));
        }

        foreach (var video in batch.Where(i => i.IsVideo))
        {
            if (_paused) break;
            await AnalyzeVideoAsync(video, model, index, ct);
            _repo.SetAnalysisVersion(video.Id, AnalysisVersion);
            Report(new IndexerStatus(IndexerPhase.Analysis, "Learning what is in the pictures, for the search", ++done, (int)stats.Total, false));
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
            Report(new IndexerStatus(IndexerPhase.People, "Looking for people in the pictures", ++done, (int)stats.Total, false));
            // Match from time to time, so names spread while a large library is still being analyzed.
            if (++_sinceIdentity >= 300) _identityDirty = true;
        }
        _identityDirty = true;
        if (_sinceIdentity >= 300 && _identity != null)
        {
            _sinceIdentity = 0;
            _identityDirty = false;
            _identityWanted = false;
            await Task.Run(() => _identity.Recompute(), ct);
            _lastIdentity = DateTime.UtcNow;
        }
        return true;
    }

    /// <summary>A 360 item for the search: four views around at every moment, each one a row (the best one counts).</summary>
    private async Task Embed360Async(MediaItem item, string local, List<(int TimeMs, RgbImage Lens0, RgbImage Lens1)> moments, ISceneModel model,
        IVectorIndex index, CancellationToken ct)
    {
        var dir = Path.Combine(_paths.CacheDir, "views", item.Id.ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var files = new List<(int TimeMs, string Path)>();
            foreach (var (time, lens0, lens1) in moments)
                foreach (var (yaw, view) in await Task.Run(() => Fisheye360.ViewsAround(lens0, lens1, 4, 320, 320, 100, Fisheye360.ModelOf(local),
                             item.IsVideo ? Fisheye360.LevelAt(local, time / 1000.0) : null).ToList(), ct))
                {
                    var file = Path.Combine(dir, $"{time}_{(int)yaw}.jpg");
                    view.SaveJpeg(file, 85);
                    files.Add((time, file));
                }
            var rows = new List<VectorRow>();
            foreach (var chunk in files.Chunk(8))
            {
                var vectors = await Task.Run(() => model.EmbedImages(chunk.Select(c => c.Path).ToList()), ct);
                for (var i = 0; i < chunk.Length; i++) rows.Add(new VectorRow(item.Id, chunk[i].TimeMs, vectors[i]));
            }
            if (rows.Count > 0) index.Put(item.Id, rows);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task AnalyzeVideoAsync(MediaItem video, ISceneModel model, IVectorIndex index, CancellationToken ct)
    {
        var duration = (video.DurationMs ?? 0) / 1000.0;
        // About 40 frames per video, at least every 30 s, at most every 2 s.
        var interval = Math.Clamp(duration / 40, 2, 30);
        var dir = Path.Combine(_paths.CacheDir, "frames", video.Id.ToString());
        if (Fisheye360.IsDualFisheye(video.FullPath))
        {
            try
            {
                var local = await Cloud.MediaFiles.LocalPathAsync(video, ct);
                var dual = duration > 0 ? await FFmpeg.ExtractDualFramesAsync(local, interval, 60, dir, 512, ct) : [];
                var moments = new List<(int, RgbImage, RgbImage)>();
                foreach (var f in dual) moments.Add((f.TimeMs, RgbImage.Load(f.Lens0, 512), RgbImage.Load(f.Lens1, 512)));
                await Embed360Async(video, local, moments, model, index, ct);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return;
        }
        try
        {
            var frames = duration > 0
                ? await FFmpeg.ExtractFramesAsync(await Cloud.MediaFiles.LocalPathAsync(video, ct), interval, 60, dir, 256, ct)
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
