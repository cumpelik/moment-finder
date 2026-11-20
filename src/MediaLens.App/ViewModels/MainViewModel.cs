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
    public PeopleViewModel People { get; }

    /// <summary>What the main area shows: the grid of photos, or one of the people screens.</summary>
    public bool IsGridMode => CurrentScope?.Kind is not (SidebarKind.Groups or SidebarKind.Review) && !ShowPersonFaces;
    public bool IsGroupsMode => CurrentScope?.Kind == SidebarKind.Groups;
    public bool IsReviewMode => CurrentScope?.Kind == SidebarKind.Review;
    public bool IsPersonScope => CurrentScope?.Kind == SidebarKind.Person;
    public bool IsPeopleScreen => IsGroupsMode || IsReviewMode;

    /// <summary>In a person's scope: the faces and figures instead of the photos.</summary>
    [ObservableProperty] private bool _showPersonFaces;

    public ObservableCollection<object> SidebarItems { get; } = [];
    public ObservableCollection<Bucket> Buckets { get; } = [];
    public bool HasBuckets => Buckets.Count > 0;

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
    /// <summary>The invitation to download the models, shown above the results until they are installed.</summary>
    [ObservableProperty] private string _aiSetupText = "";
    [ObservableProperty] private string _aiSetupTitle = "";
    /// <summary>Progress or the outcome of the model download, in the banner.</summary>
    [ObservableProperty] private string _downloadText = "";

    [ObservableProperty] private string _mirrorText = "";
    [ObservableProperty] private int _slideSeconds = 5;
    /// <summary>Videos play only the moments of the searched person or the matching part.</summary>
    [ObservableProperty] private bool _videoHighlightsOnly;

    /// <summary>The view opens the player window.</summary>
    /// <summary>InWindow: full screen; otherwise the panel under the thumbnails.</summary>
    public event Action<PlayerViewModel, bool>? PlayerRequested;
    /// <summary>A new search or scope (not a refresh of the same one): the view scrolls to the top.</summary>
    public event Action? ResultsReset;

    public MainViewModel(LibraryHost host)
    {
        _host = host;
        Thumbnails = new ThumbnailLoader(host.Indexer, host.Paths);
        People = new PeopleViewModel(host, () => Dispatcher.UIThread.Post(RebuildSidebar));
        host.Identity.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            RebuildSidebar();
            if (IsPersonScope && !ShowPersonFaces) _ = RunSearchAsync(keepSelection: true);
            if (IsPersonScope && ShowPersonFaces && CurrentScope is { } p) People.LoadPerson(p.Id);
            if (IsGroupsMode) People.LoadClusters();
            // The matching finished after the review opened: the new suggestions are there now.
            if (IsReviewMode && !People.HasReview) People.LoadReview();
        });
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
            if (string.IsNullOrWhiteSpace(QueryParser.Parse(SearchText, _host.Search.People).Text))
                _ = RunSearchAsync(keepSelection: true);
        });

        if (int.TryParse(host.Db.GetSetting("slide_seconds"), CultureInfo.InvariantCulture, out var s)) _slideSeconds = s;
        // Videos found by a person or a description start on those moments unless the user chose whole videos.
        _videoHighlightsOnly = host.Db.GetSetting("video_highlights") != "0";
        _previewMode = host.Db.GetSetting("open_in") switch
        {
            "below" => PreviewMode.Below,
            "right" => PreviewMode.Right,
            _ => host.Db.GetSetting("open_in_panel") == "1" ? PreviewMode.Below : PreviewMode.Window,
        };
        _sidebarVisible = host.Db.GetSetting("sidebar_hidden") != "1";
        if (double.TryParse(host.Db.GetSetting("tile_size"), CultureInfo.InvariantCulture, out var ts)) _tileSize = ts;
        LoadBrowseSettings();

        host.Indexer.StatusChanged += st => Dispatcher.UIThread.Post(() => OnIndexerStatus(st));
        host.Mirrors.StatusChanged += text => Dispatcher.UIThread.Post(() => MirrorText = text);
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
        _host.Mirrors.Start();
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
        var persons = _host.People.ListPersons();
        var clusters = _host.People.UnnamedClusters();
        var groups = clusters.Count + _host.People.UnnamedFigures(clusters.Select(c => c.Cluster).ToHashSet()).Count;
        var suggestions = _host.People.SuggestionCount();
        if (persons.Count > 0 || groups > 0 || suggestions > 0 || _host.PeopleModel != null)
        {
            SidebarItems.Add(new SidebarHeader("PEOPLE", "", SidebarKind.Person));
            SidebarItems.Add(new SidebarEntry(SidebarKind.Groups, 0, "Who is this?", groups.ToString("N0"),
                "People nobody named yet: groups of faces, and people without a visible face (helmets, from behind)"));
            SidebarItems.Add(new SidebarEntry(SidebarKind.Review, 0, "Review suggestions", suggestions.ToString("N0"),
                "The analysis is not sure: confirm or reject, one key per answer"));
            foreach (var p in persons)
                SidebarItems.Add(new SidebarEntry(SidebarKind.Person, p.Id, p.Name, p.MediaCount.ToString("N0"), p.Name));
        }
        SidebarItems.Add(new SidebarHeader("BUCKETS", "New bucket", SidebarKind.Bucket));
        Buckets.Clear();
        foreach (var b in _host.Buckets.List())
        {
            Buckets.Add(b);
            SidebarItems.Add(new SidebarEntry(SidebarKind.Bucket, b.Id, b.Name, b.Count.ToString("N0"), b.Name));
        }
        var restored = selected == null ? null : SidebarItems.OfType<SidebarEntry>().FirstOrDefault(e => e.Kind == selected.Kind && e.Id == selected.Id);
        ActiveBucket = activeId is { } id ? Buckets.FirstOrDefault(b => b.Id == id) : null;
        OnPropertyChanged(nameof(HasBuckets));
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
        OnPropertyChanged(nameof(IsPersonScope));
        if (!same) ShowPersonFaces = false;
        OnPropertyChanged(nameof(IsGridMode));
        OnPropertyChanged(nameof(ShowPreviewPanel));
        OnPropertyChanged(nameof(IsGroupsMode));
        OnPropertyChanged(nameof(IsReviewMode));
        OnPropertyChanged(nameof(IsPeopleScreen));
        if (_starting || same) return;
        if (entry.Kind == SidebarKind.Groups)
        {
            People.LoadClusters();
            return;
        }
        if (entry.Kind == SidebarKind.Review)
        {
            People.LoadReview();
            return;
        }
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

    private MediaFilter ScopeFilter() => WithDates(CurrentScope switch
    {
        { Kind: SidebarKind.Source } e => new MediaFilter { SourceId = e.Id },
        { Kind: SidebarKind.Bucket } e => new MediaFilter { BucketId = e.Id },
        { Kind: SidebarKind.Person } e => new MediaFilter { PersonIds = [e.Id] },
        _ => new MediaFilter(),
    });

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

    public CloudViewModel CreateCloudViewModel() => new(_host, () => Dispatcher.UIThread.Post(RebuildSidebar));

    /// <summary>
    /// macOS: the originals of the Photos app (iCloud Photos with "Download Originals to this Mac") as a
    /// source. Reading them needs Full Disk Access or the Photos permission for MediaLens.
    /// </summary>
    public static string? ApplePhotosOriginals()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Pictures", "Photos Library.photoslibrary", "originals");
        return Directory.Exists(path) ? path : null;
    }

    public void AddApplePhotos()
    {
        if (ApplePhotosOriginals() is not { } path) return;
        AddSource(path);
        RenameSourceToPhotos(path);
    }

    private void RenameSourceToPhotos(string path)
    {
        using var c = _host.Db.Open();
        Dapper.SqlMapper.Execute(c, "UPDATE source SET name='Apple Photos' WHERE root=@path", new { path });
        RebuildSidebar();
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

    partial void OnShowPersonFacesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGridMode));
        OnPropertyChanged(nameof(ShowPreviewPanel));
        if (value && CurrentScope is { Kind: SidebarKind.Person } p) People.LoadPerson(p.Id);
    }

    public void RenamePerson(SidebarEntry entry, string name) => People.RenamePerson(entry.Id, name);

    public void DeletePerson(SidebarEntry entry)
    {
        People.DeletePerson(entry.Id);
        SelectedSidebarItem = SidebarItems.OfType<SidebarEntry>().First();
    }

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
        var hasText = !string.IsNullOrWhiteSpace(QueryParser.Parse(text, _host.Search.People).Text);
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
            text += " with these words in the file name. To search by what is in the picture, download the AI models (above).";
        else if (hasText)
        {
            text += " best matches";
            var phrase = QueryParser.Parse(SearchText, _host.Search.People).Text;
            var english = _host.Search.QueryPhrase(phrase);
            if (!string.Equals(english.Trim('.', ' '), phrase.Trim('.', ' '), StringComparison.OrdinalIgnoreCase))
                text += $" for \"{english.Trim('.', ' ').ToLowerInvariant()}\"";
            var stats = _host.Repo.Stats(Indexer.AnalysisVersion);
            if (stats.Analyzed < stats.Total)
                text += $" (the AI has looked at {stats.Analyzed:N0} of {stats.Total:N0} so far, search again later for more)";
        }
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
        PlayerRequested?.Invoke(CreatePlayer(Tiles.ToList(), index, autoPlay: false), true);
    }

    /// <summary>Opens one item by id (from a face crop); a video at the moment of the crop, with the people boxes on.</summary>
    public void OpenMedia(long mediaId, int timeMs = 0, bool showPeople = false)
    {
        if (_host.Repo.Get(mediaId) is not { } item) return;
        var tile = timeMs > 0 ? new TileViewModel(item, timeMs) : Tiles.FirstOrDefault(t => t.Item.Id == mediaId) ?? new TileViewModel(item, 0);
        var player = CreatePlayer([tile], 0, autoPlay: false);
        player.ShowPeople = showPeople;
        PlayerRequested?.Invoke(player, true);
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
        PlayerRequested?.Invoke(CreatePlayer(list, 0, autoPlay: true), !OpenInPanel);
    }

    private PlayerViewModel CreatePlayer(List<TileViewModel> tiles, int index, bool autoPlay)
    {
        var (personIds, names, hasText) = CurrentQuery();
        var entries = tiles.Select(t => new PlaylistEntry(t.Item, t.StartMs,
            t.Item.IsVideo && hasText ? [(Math.Max(0, t.StartMs - 1000), t.StartMs + MatchMomentMs)] : null)).ToList();

        // What the videos can play: whole, the matching part, the searched people, anybody else in them.
        var whole = new MomentChoice("Whole videos", MomentKind.Whole, []);
        var choices = new List<MomentChoice> { whole };
        if (hasText) choices.Add(new MomentChoice("Only the matching part", MomentKind.Match, []));
        if (personIds.Count > 1) choices.Add(new MomentChoice($"Only {string.Join(" + ", names)}", MomentKind.Person, personIds));
        var personNames = _host.People.ListPersons().ToDictionary(p => p.Id, p => p.Name);
        var videoIds = entries.Where(e => e.Item.IsVideo).Select(e => e.Item.Id).ToList();
        if (videoIds.Count > 0)
            foreach (var id in personIds.Concat(_host.People.PersonsIn(videoIds)).Distinct())
                if (personNames.TryGetValue(id, out var name))
                    choices.Add(new MomentChoice($"Only {name}", MomentKind.Person, [id]));
        var initial = !VideoHighlightsOnly ? whole
            : personIds.Count > 0 ? choices.FirstOrDefault(c => c.Kind == MomentKind.Person && c.PersonIds.SequenceEqual(personIds)) ?? whole
            : hasText ? choices.First(c => c.Kind == MomentKind.Match) : whole;
        return new PlayerViewModel(entries, index, autoPlay, new PlayerSettings(SlideSeconds, Volume), this,
            videoIds.Count > 0 ? choices : [whole], initial);
    }

    /// <summary>The viewer's volume, kept for the next time.</summary>
    public int Volume => int.TryParse(_host.Db.GetSetting("volume"), CultureInfo.InvariantCulture, out var v) ? v : 80;

    public void SaveVolume(int volume) => _host.Db.SetSetting("volume", volume.ToString(CultureInfo.InvariantCulture));

    /// <summary>A video found by a description: how long it plays from the matching frame.</summary>
    private const int MatchMomentMs = 12_000;

    /// <summary>The people of the search (named in the box or the person in the sidebar) and whether there is text to match.</summary>
    private (List<long> PersonIds, List<string> Names, bool HasText) CurrentQuery()
    {
        var query = QueryParser.Parse(SearchText, _host.Search.People);
        var personIds = QueryParser.ToFilter(query, ScopeFilter()).PersonIds.ToList();
        var names = _host.Search.People.Where(p => personIds.Contains(p.Id)).Select(p => p.Name).ToList();
        return (personIds, names, !string.IsNullOrWhiteSpace(query.Text) && _host.Search.CanSearchText);
    }

    partial void OnVideoHighlightsOnlyChanged(bool value) => _host.Db.SetSetting("video_highlights", value ? "1" : "0");

    /// <summary>The viewer remembers whether the user wants whole videos or only the found moments.</summary>
    public void RememberMoments(bool onlyMoments) => VideoHighlightsOnly = onlyMoments;

    /// <summary>Where a clicked photo or video shows: full screen, or a panel under or right of the thumbnails.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OpenInPanel), nameof(ShowPreviewPanel), nameof(IsPreviewWindow), nameof(IsPreviewBelow), nameof(IsPreviewRight))]
    private PreviewMode _previewMode;

    public bool OpenInPanel => PreviewMode != PreviewMode.Window;
    public bool IsPreviewWindow => PreviewMode == PreviewMode.Window;
    public bool IsPreviewBelow => PreviewMode == PreviewMode.Below;
    public bool IsPreviewRight => PreviewMode == PreviewMode.Right;

    partial void OnPreviewModeChanged(PreviewMode value) => _host.Db.SetSetting("open_in", value.ToString().ToLowerInvariant());

    [RelayCommand]
    private void SetPreviewMode(PreviewMode mode) => PreviewMode = mode;

    /// <summary>The left panel can be hidden for more room; its width is the user's.</summary>
    [ObservableProperty] private bool _sidebarVisible;

    partial void OnSidebarVisibleChanged(bool value) => _host.Db.SetSetting("sidebar_hidden", value ? "0" : "1");

    [RelayCommand]
    private void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    /// <summary>Sizes the user dragged (panels, sidebar), kept for the next start.</summary>
    public double? SavedSize(string key) =>
        double.TryParse(_host.Db.GetSetting(key), CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    public void SaveSize(string key, double value) => _host.Db.SetSetting(key, value.ToString("0", CultureInfo.InvariantCulture));

    /// <summary>The panel shows this item of the results; arrows and clicks in the grid move it.</summary>
    public PlayerViewModel CreatePreview(int index) => CreatePlayer(Tiles.ToList(), index, autoPlay: false);
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
        var missing = ModelCatalog.All.Where(m => !_host.Models.IsInstalled(m)).ToList();
        if (missing.Count > 0)
        {
            var size = missing.Sum(m => m.TotalSize) / 1048576;
            AiSetupTitle = missing.Count == ModelCatalog.All.Count ? "Turn on the AI" : "Better AI";
            AiSetupText = missing.Count == ModelCatalog.All.Count
                ? $"Search by what is in the picture (skier, beach at sunset, birthday cake) and recognize people across the years. " +
                  $"The AI models are a one-time download of {size} MB, then everything runs offline on this computer."
                : $"New AI models are available: {string.Join(", ", missing.Select(m => m.Title))}. A download of {size} MB.";
            ModelText = "";
            CanDownloadModel = true;
        }
        if (missing.Count == ModelCatalog.All.Count) return;
        ModelText = "Loading the AI models...";
        try
        {
            var scene = await Task.Run(_host.TryLoadSceneModel);
            var people = await Task.Run(_host.TryLoadPeopleModel);
            var parts = new List<string>();
            if (scene) parts.Add("search");
            if (people) parts.Add("people");
            var device = _host.SceneModel?.Device ?? _host.PeopleModel?.Device;
            ModelText = $"AI ready: {string.Join(", ", parts)} ({device})" + (missing.Count > 0 ? ", more after the download" : "");
            CanDownloadModel = missing.Count > 0;
            if (people) RebuildSidebar();
            if (!string.IsNullOrWhiteSpace(QueryParser.Parse(SearchText, _host.Search.People).Text))
                await RunSearchAsync(keepSelection: false);
        }
        catch (Exception e)
        {
            ModelText = "The AI models cannot start: " + e.Message;
        }
    }

    /// <summary>The same button stops a running download, so the command must run while it runs.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DownloadModel()
    {
        if (IsDownloadingModel)
        {
            _downloadCts?.Cancel();
            return;
        }
        _downloadCts = new CancellationTokenSource();
        IsDownloadingModel = true;
        try
        {
            var missing = ModelCatalog.All.Where(m => !_host.Models.IsInstalled(m)).ToList();
            var total = missing.Sum(m => m.TotalSize);
            long before = 0;
            foreach (var model in missing)
            {
                await _host.Models.DownloadAsync(model, new Progress<(long Done, long Total)>(p =>
                {
                    DownloadProgress = 100.0 * (before + p.Done) / total;
                    DownloadText = $"Downloading {model.Title}: {(before + p.Done) / 1048576} / {total / 1048576} MB";
                }), _downloadCts.Token);
                before += model.TotalSize;
            }
            await LoadModelAsync();
        }
        catch (OperationCanceledException)
        {
            DownloadText = "Download stopped; it continues where it ended next time.";
        }
        catch (Exception e)
        {
            DownloadText = "Download failed: " + e.Message;
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
