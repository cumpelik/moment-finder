using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.Analysis;
using MediaLens.App.Services;
using MediaLens.Buckets;
using MediaLens.Library;
using MediaLens.Search;

namespace MediaLens.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibraryHost _host;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _refreshThrottle;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;
    private int _anchorIndex = -1;
    private bool _refreshPending;
    private bool _starting = true;

    public ThumbnailLoader Thumbnails { get; }

    public ObservableCollection<object> SidebarItems { get; } = [];
    public ObservableCollection<Bucket> Buckets { get; } = [];

    [ObservableProperty] private object? _selectedSidebarItem;
    [ObservableProperty] private ObservableCollection<TileViewModel> _tiles = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private Bucket? _activeBucket;
    [ObservableProperty] private double _tileSize = 180;

    [ObservableProperty] private string _indexerText = "";
    [ObservableProperty] private double _indexerProgress;
    [ObservableProperty] private bool _indexerBusy;
    [ObservableProperty] private bool _indexerPaused;

    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private bool _canDownloadModel;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private double _downloadProgress;

    [ObservableProperty] private int _slideSeconds = 5;
    [ObservableProperty] private bool _videoHighlightsOnly;

    /// <summary>The view opens the player window.</summary>
    public event Action<PlayerViewModel>? PlayerRequested;
    /// <summary>A new search or scope (not a refresh of the same one): the view scrolls to the top.</summary>
    public event Action? ResultsReset;

    public MainViewModel(LibraryHost host)
    {
        _host = host;
        Thumbnails = new ThumbnailLoader(host.Indexer, host.Paths);
        _searchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _searchDebounce!.Stop();
            _ = RunSearchAsync(keepSelection: false);
        });
        _refreshThrottle = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _refreshThrottle!.Stop();
            if (!_refreshPending) return;
            _refreshPending = false;
            RefreshSidebarCounts();
            // A text search is not repeated by itself: its results would jump around while the user looks at them.
            if (string.IsNullOrWhiteSpace(QueryParser.Parse(SearchText).Text))
                _ = RunSearchAsync(keepSelection: true);
        });

        if (int.TryParse(host.Db.GetSetting("slide_seconds"), CultureInfo.InvariantCulture, out var s)) _slideSeconds = s;
        _videoHighlightsOnly = host.Db.GetSetting("video_highlights") == "1";
        if (double.TryParse(host.Db.GetSetting("tile_size"), CultureInfo.InvariantCulture, out var ts)) _tileSize = ts;

        host.Indexer.StatusChanged += st => Dispatcher.UIThread.Post(() => OnIndexerStatus(st));
        host.Indexer.LibraryChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = true;
            if (!_refreshThrottle.IsEnabled) _refreshThrottle.Start();
        });
    }

    public void Start()
    {
        RebuildSidebar();
        SelectedSidebarItem = SidebarItems.OfType<SidebarEntry>().First();
        if (long.TryParse(_host.Db.GetSetting("active_bucket"), out var activeId))
            ActiveBucket = Buckets.FirstOrDefault(b => b.Id == activeId);
        _starting = false;
        _ = RunSearchAsync(keepSelection: false);
        _host.Indexer.Start(scanNow: true);
        _ = LoadModelAsync();
    }

    // ---- sidebar ----

    private void RebuildSidebar()
    {
        // Clearing the lists makes the bound controls push null into the selections; restore them afterwards.
        _rebuilding = true;
        var selected = _scope;
        var activeId = ActiveBucket?.Id;
        SidebarItems.Clear();
        var stats = _host.Repo.Stats(Indexer.AnalysisVersion);
        SidebarItems.Add(new SidebarEntry(SidebarKind.All, 0, "All photos and videos", stats.Total.ToString("N0"),
            $"{stats.Photos:N0} photos, {stats.Videos:N0} videos"));
        SidebarItems.Add(new SidebarHeader("SOURCES", "Add a folder", SidebarKind.Source));
        var sources = _host.Repo.ListSources();
        var counts = _host.Repo.CountBySource();
        foreach (var s in sources)
            SidebarItems.Add(new SidebarEntry(SidebarKind.Source, s.Id, s.Name, counts.GetValueOrDefault(s.Id).ToString("N0"), s.Root));
        IsLibraryEmpty = sources.Count == 0;
        SidebarItems.Add(new SidebarHeader("BUCKETS", "New bucket", SidebarKind.Bucket));
        Buckets.Clear();
        foreach (var b in _host.Buckets.List())
        {
            Buckets.Add(b);
            SidebarItems.Add(new SidebarEntry(SidebarKind.Bucket, b.Id, b.Name, b.Count.ToString("N0"), b.Name));
        }
        var restored = selected == null ? null : SidebarItems.OfType<SidebarEntry>().FirstOrDefault(e => e.Kind == selected.Kind && e.Id == selected.Id);
        ActiveBucket = activeId is { } id ? Buckets.FirstOrDefault(b => b.Id == id) : null;
        _rebuilding = false;
        // A removed scope falls back to the whole library (and searches again).
        SelectedSidebarItem = restored ?? SidebarItems.OfType<SidebarEntry>().First();
    }

    private void RefreshSidebarCounts()
    {
        // Rebuilding keeps the selection; the counts are cheap queries.
        RebuildSidebar();
    }

    partial void OnSelectedSidebarItemChanged(object? oldValue, object? newValue)
    {
        if (_rebuilding) return;
        if (newValue is SidebarHeader || newValue == null)
        {
            // Headers are not scopes; stay where we were.
            var scope = _scope;
            Dispatcher.UIThread.Post(() => SelectedSidebarItem = SidebarItems.OfType<SidebarEntry>().FirstOrDefault(e => e.Kind == scope?.Kind && e.Id == scope.Id));
            return;
        }
        if (newValue is not SidebarEntry entry) return;
        var same = _scope != null && _scope.Kind == entry.Kind && _scope.Id == entry.Id;
        _scope = entry;
        OnPropertyChanged(nameof(IsBucketScope));
        if (_starting || same) return;
        // A bucket opens whole and in its own order; a search left from before would hide part of it.
        if (entry.Kind == SidebarKind.Bucket && SearchText.Length > 0)
        {
            SearchText = "";
            _searchDebounce.Stop();
        }
        _ = RunSearchAsync(keepSelection: false);
    }

    private SidebarEntry? _scope;
    private bool _rebuilding;

    public SidebarEntry? CurrentScope => _scope;
    public bool IsBucketScope => CurrentScope?.Kind == SidebarKind.Bucket;

    [ObservableProperty] private bool _isLibraryEmpty;

    private MediaFilter ScopeFilter() => CurrentScope switch
    {
        { Kind: SidebarKind.Source } e => new MediaFilter { SourceId = e.Id },
        { Kind: SidebarKind.Bucket } e => new MediaFilter { BucketId = e.Id },
        _ => new MediaFilter(),
    };

    public void AddSource(string folder)
    {
        var name = new DirectoryInfo(folder).Name;
        if (_host.Repo.ListSources().Any(s => string.Equals(s.Root, folder, StringComparison.Ordinal)))
            return;
        var source = _host.Repo.AddSource(SourceKinds.Local, folder, string.IsNullOrEmpty(name) ? folder : name);
        RebuildSidebar();
        SelectedSidebarItem = SidebarItems.OfType<SidebarEntry>().First(e => e.Kind == SidebarKind.Source && e.Id == source.Id);
        _host.Indexer.RequestScan();
    }

    public void RemoveSource(SidebarEntry entry)
    {
        _host.Repo.RemoveSource(entry.Id);
        RebuildSidebar();
        if (SelectedSidebarItem is SidebarEntry { Kind: SidebarKind.Source } e && e.Id == entry.Id)
            SelectedSidebarItem = SidebarItems.OfType<SidebarEntry>().First();
        _ = RunSearchAsync(keepSelection: false);
    }

    [RelayCommand]
    private void Rescan() => _host.Indexer.RequestScan();

    public Bucket CreateBucket(string name)
    {
        var bucket = _host.Buckets.Create(UniqueBucketName(name));
        RebuildSidebar();
        ActiveBucket = Buckets.First(b => b.Id == bucket.Id);
        return bucket;
    }

    private string UniqueBucketName(string name)
    {
        var existing = _host.Buckets.List().Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name.Trim();
        for (var n = 2; existing.Contains(candidate); n++) candidate = $"{name.Trim()} {n}";
        return candidate;
    }

    public void RenameBucket(SidebarEntry entry, string name)
    {
        _host.Buckets.Rename(entry.Id, UniqueBucketName(name));
        RebuildSidebar();
    }

    public void DeleteBucket(SidebarEntry entry)
    {
        _host.Buckets.Delete(entry.Id);
        if (ActiveBucket?.Id == entry.Id) ActiveBucket = null;
        RebuildSidebar();
        _ = RunSearchAsync(keepSelection: false);
    }

    partial void OnActiveBucketChanged(Bucket? value)
    {
        if (_rebuilding) return;
        if (!_starting) _host.Db.SetSetting("active_bucket", value?.Id.ToString());
        MarkBucketMembership();
    }

    private void MarkBucketMembership()
    {
        var ids = ActiveBucket is { } b ? _host.Buckets.MediaIds(b.Id) : [];
        foreach (var t in Tiles) t.InBucket = ids.Contains(t.Item.Id);
    }

    /// <summary>B: the selection goes to the active bucket (a new one is made when there is none).</summary>
    [RelayCommand]
    private void AddSelectionToBucket()
    {
        var items = SelectedTiles().Select(t => t.Item.Id).ToList();
        if (items.Count == 0) return;
        AddToBucket(items);
    }

    public void AddToBucket(IReadOnlyList<long> mediaIds)
    {
        if (ActiveBucket == null)
        {
            if (Buckets.Count > 0) ActiveBucket = Buckets[0];
            else CreateBucket("Photo book");
        }
        var bucketId = ActiveBucket!.Id;
        _host.Buckets.Add(bucketId, mediaIds);
        RebuildSidebar();
        MarkBucketMembership();
        if (CurrentScope is { Kind: SidebarKind.Bucket } s && s.Id == bucketId)
            _ = RunSearchAsync(keepSelection: true);
    }

    public bool IsInActiveBucket(long mediaId) => ActiveBucket is { } b && _host.Buckets.MediaIds(b.Id).Contains(mediaId);

    /// <summary>Delete in a bucket view: takes the selection out of the bucket (the files stay).</summary>
    [RelayCommand]
    private void RemoveSelectionFromBucket()
    {
        if (CurrentScope is not { Kind: SidebarKind.Bucket } scope) return;
        var ids = SelectedTiles().Select(t => t.Item.Id).ToList();
        if (ids.Count == 0) return;
        _host.Buckets.Remove(scope.Id, ids);
        RebuildSidebar();
        _ = RunSearchAsync(keepSelection: false);
    }

    /// <summary>Ctrl+Left / Ctrl+Right in a bucket view: moves the selection within the bucket order.</summary>
    public void MoveSelectionInBucket(int delta)
    {
        if (CurrentScope is not { Kind: SidebarKind.Bucket } scope) return;
        var list = Tiles.ToList();
        var selected = list.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0) return;
        var ordered = delta < 0 ? selected : Enumerable.Reverse(selected).ToList();
        foreach (var t in ordered)
        {
            var i = list.IndexOf(t);
            var j = i + delta;
            if (j < 0 || j >= list.Count || list[j].IsSelected) continue;
            (list[i], list[j]) = (list[j], list[i]);
        }
        _host.Buckets.Reorder(scope.Id, list.Select(t => t.Item.Id).ToList());
        Tiles = new ObservableCollection<TileViewModel>(list);
        _anchorIndex = list.IndexOf(selected[0]);
    }

    public async Task<ExportResult?> ExportBucketAsync(string targetDir, IProgress<(int Done, int Total)> progress)
    {
        if (CurrentScope is not { Kind: SidebarKind.Bucket } scope) return null;
        return await _host.Buckets.ExportAsync(scope.Id, targetDir, new ExportOptions(), progress, CancellationToken.None);
    }

    // ---- search and results ----

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    [RelayCommand]
    private Task SearchNow()
    {
        _searchDebounce.Stop();
        return RunSearchAsync(keepSelection: false);
    }

    private async Task RunSearchAsync(bool keepSelection)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var text = SearchText;
        var scope = ScopeFilter();
        var hasText = !string.IsNullOrWhiteSpace(QueryParser.Parse(text).Text);
        if (hasText) ResultSummary = "Searching...";
        List<SearchHit> hits;
        try
        {
            hits = await Task.Run(() => _host.Search.Search(text, scope), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;

        var selectedIds = keepSelection ? SelectedTiles().Select(t => t.Item.Id).ToHashSet() : [];
        // The same items in the same order: keep the tiles (and their loaded thumbnails).
        if (keepSelection && hits.Count == Tiles.Count && hits.Select(h => h.Item.Id).SequenceEqual(Tiles.Select(t => t.Item.Id)))
        {
            UpdateSummary(hits.Count, hasText);
            return;
        }
        var tiles = hits.Select(h => new TileViewModel(h.Item, h.TimeMs) { IsSelected = selectedIds.Contains(h.Item.Id) });
        Tiles = new ObservableCollection<TileViewModel>(tiles);
        _anchorIndex = -1;
        MarkBucketMembership();
        UpdateSummary(hits.Count, hasText);
        if (!keepSelection) ResultsReset?.Invoke();
    }

    private void UpdateSummary(int count, bool hasText)
    {
        var text = Format.Count(count, "item", "items");
        if (hasText && !_host.Search.CanSearchText)
            text += " (search by description needs the AI model, see below)";
        else if (hasText)
            text += " best matches";
        ResultSummary = text;
    }

    public IEnumerable<TileViewModel> SelectedTiles() => Tiles.Where(t => t.IsSelected);

    /// <summary>Click: select one; Ctrl: toggle; Shift: range from the last click.</summary>
    public void Select(TileViewModel tile, bool toggle, bool range)
    {
        var index = Tiles.IndexOf(tile);
        if (index < 0) return;
        if (range && _anchorIndex >= 0)
        {
            var (a, b) = (Math.Min(_anchorIndex, index), Math.Max(_anchorIndex, index));
            if (!toggle)
                foreach (var t in Tiles) t.IsSelected = false;
            for (var i = a; i <= b; i++) Tiles[i].IsSelected = true;
            return;
        }
        if (toggle)
            tile.IsSelected = !tile.IsSelected;
        else
        {
            foreach (var t in Tiles) t.IsSelected = false;
            tile.IsSelected = true;
        }
        _anchorIndex = index;
    }

    public void SelectAll()
    {
        foreach (var t in Tiles) t.IsSelected = true;
    }

    /// <summary>Keyboard navigation; returns the newly selected tile so the view can scroll to it.</summary>
    public TileViewModel? MoveSelection(int delta, bool extend)
    {
        if (Tiles.Count == 0) return null;
        var from = _anchorIndex >= 0 ? _anchorIndex : -1;
        var to = Math.Clamp(from < 0 ? 0 : from + delta, 0, Tiles.Count - 1);
        if (!extend)
            foreach (var t in Tiles) t.IsSelected = false;
        Tiles[to].IsSelected = true;
        _anchorIndex = to;
        return Tiles[to];
    }

    public TileViewModel? AnchorTile => _anchorIndex >= 0 && _anchorIndex < Tiles.Count ? Tiles[_anchorIndex] : null;

    // ---- playback ----

    /// <summary>Opens the viewer on one item; arrows go through the whole result.</summary>
    public void Open(TileViewModel tile)
    {
        var index = Tiles.IndexOf(tile);
        if (index < 0) return;
        PlayerRequested?.Invoke(CreatePlayer(Tiles.ToList(), index, autoPlay: false));
    }

    /// <summary>Plays the selection when there is more than one selected item, otherwise the whole result.</summary>
    [RelayCommand]
    private void PlayAll() => StartSlideshow(shuffle: false);

    [RelayCommand]
    private void Shuffle() => StartSlideshow(shuffle: true);

    private void StartSlideshow(bool shuffle)
    {
        var selected = SelectedTiles().ToList();
        var list = selected.Count > 1 ? selected : Tiles.ToList();
        if (list.Count == 0) return;
        if (shuffle) list = list.OrderBy(_ => Random.Shared.Next()).ToList();
        PlayerRequested?.Invoke(CreatePlayer(list, 0, autoPlay: true));
    }

    private PlayerViewModel CreatePlayer(List<TileViewModel> tiles, int index, bool autoPlay) =>
        new(tiles.Select(t => new PlaylistEntry(t.Item, t.StartMs)).ToList(), index, autoPlay,
            new PlayerSettings(SlideSeconds, VideoHighlightsOnly), this);

    partial void OnSlideSecondsChanged(int value) => _host.Db.SetSetting("slide_seconds", value.ToString(CultureInfo.InvariantCulture));
    partial void OnVideoHighlightsOnlyChanged(bool value) => _host.Db.SetSetting("video_highlights", value ? "1" : "0");
    partial void OnTileSizeChanged(double value) => _host.Db.SetSetting("tile_size", value.ToString(CultureInfo.InvariantCulture));

    // ---- background work ----

    private void OnIndexerStatus(IndexerStatus st)
    {
        IndexerPaused = st.Paused;
        IndexerBusy = st.Phase != IndexerPhase.Idle;
        IndexerProgress = st.Total > 0 ? 100.0 * st.Done / st.Total : 0;
        IndexerText = st.Total > 0 ? $"{st.Message} {st.Done:N0} / {st.Total:N0}" : st.Message;
    }

    [RelayCommand]
    private void TogglePause() => _host.Indexer.Paused = !_host.Indexer.Paused;

    private async Task LoadModelAsync()
    {
        var model = ModelCatalog.Siglip2Base;
        if (!_host.SceneModelInstalled)
        {
            ModelText = $"Search by description is off: the AI model is not downloaded yet ({model.TotalSize / 1048576} MB, once, then everything runs offline).";
            CanDownloadModel = true;
            return;
        }
        ModelText = "Loading the AI model...";
        try
        {
            await Task.Run(_host.TryLoadSceneModel);
            ModelText = $"AI search ready ({_host.SceneModel!.Device})";
            CanDownloadModel = false;
            if (!string.IsNullOrWhiteSpace(QueryParser.Parse(SearchText).Text))
                await RunSearchAsync(keepSelection: false);
        }
        catch (Exception e)
        {
            ModelText = "The AI model cannot start: " + e.Message;
        }
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        if (IsDownloadingModel)
        {
            _downloadCts?.Cancel();
            return;
        }
        var model = ModelCatalog.Siglip2Base;
        _downloadCts = new CancellationTokenSource();
        IsDownloadingModel = true;
        try
        {
            await _host.Models.DownloadAsync(model, new Progress<(long Done, long Total)>(p =>
            {
                DownloadProgress = 100.0 * p.Done / p.Total;
                ModelText = $"Downloading the AI model: {p.Done / 1048576} / {p.Total / 1048576} MB";
            }), _downloadCts.Token);
            await LoadModelAsync();
        }
        catch (OperationCanceledException)
        {
            ModelText = "Download stopped; it continues where it ended next time.";
        }
        catch (Exception e)
        {
            ModelText = "Download failed: " + e.Message;
        }
        finally
        {
            IsDownloadingModel = false;
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _downloadCts?.Cancel();
    }
}
