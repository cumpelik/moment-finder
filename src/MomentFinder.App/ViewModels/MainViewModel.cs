using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.Analysis;
using MomentFinder.App.Services;
using Material.Icons;
using MomentFinder.Buckets;
using MomentFinder.Cloud;
using MomentFinder.Library;
using MomentFinder.People;
using MomentFinder.Search;

namespace MomentFinder.App.ViewModels;

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
        host.Indexer.Proxies360 = true;
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
            // The next wait: a few seconds, longer the longer the search takes (at most a minute and a half).
            _refreshThrottle.Interval = TimeSpan.FromSeconds(Math.Clamp(_lastSearch.TotalSeconds * 8, 3, 90));
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
            "window" => PreviewMode.Window,
            // Saved by an older version, before the choice of where.
            _ => host.Db.GetSetting("open_in_panel") == "1" ? PreviewMode.Below : PreviewMode.Window,
        };
        _sidebarVisible = host.Db.GetSetting("sidebar_hidden") != "1";
        LoadBrowseSettings();

        LibraryCopy = new LibraryCopyViewModel(host);
        CloudJobs = new CloudJobsViewModel(host.Jobs);
        // Uploaded, moved or deleted in the cloud: the photos shown follow.
        host.Jobs.LibraryChanged += () => Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = true;
            if (!_refreshThrottle!.IsEnabled) _refreshThrottle.Start();
        });
        host.Indexer.StatusChanged += st => Dispatcher.UIThread.Post(() => OnIndexerStatus(st));
        host.Mirrors.StatusChanged += text => Dispatcher.UIThread.Post(() => MirrorText = text.Length > 0 ? text : CloudSummary());
        host.Mirrors.MirrorsChanged += () => Dispatcher.UIThread.Post(OnCloudChanged);
        host.Mirrors.FoldersChanged += host.Indexer.RequestScan;
        // Another computer of the library changed something: names, buckets and who is who show it, its folders are read.
        host.Sync.FoldersChanged += () => Dispatcher.UIThread.Post(() =>
        {
            RebuildSidebar();
            host.Indexer.RequestScan();
        });
        host.Sync.StatusChanged += text => Dispatcher.UIThread.Post(() => MirrorText = text.Length > 0 ? text : CloudSummary());
        host.Sync.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            host.RefreshNames();
            host.Indexer.RequestIdentity();
            RebuildSidebar();
            _refreshPending = true;
            if (!_refreshThrottle!.IsEnabled) _refreshThrottle.Start();
        });
        host.Mirrors.Copying += (id, p) => Dispatcher.UIThread.Post(() => OnCopying(id, p));
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
        _host.Jobs.Start();
        _host.Sync.Start();
        _ = LibraryCopy.OfferJoinAsync();
        // A folder that changed is read again, and its backup follows, once it has been quiet for a while.
        _watcher = new FolderWatcher(_host.Repo);
        _watcher.FolderChanged += id =>
        {
            _host.Indexer.RequestScan(id);
            _host.Mirrors.FolderChanged(id);
        };
        _ = LoadModelAsync();
    }

    // ---- sidebar ----

    /// <summary>
    /// The counts that take a while in a big library (groups of unnamed faces and figures, suggestions, best moments):
    /// worked out in the background, the sidebar shows the last ones and is built again when they change.
    /// </summary>
    private sealed record SidebarCounts(int Groups, long Suggestions, int Best, LibraryStats Stats, Dictionary<long, long> BySource,
        List<Person> Persons)
    {
        // Equal when the sidebar would look the same (the dictionaries and lists compared by content).
        public bool Same(SidebarCounts? other) => other != null && Groups == other.Groups && Suggestions == other.Suggestions &&
            Best == other.Best && Stats.Total == other.Stats.Total && Stats.Photos == other.Stats.Photos &&
            Stats.Videos == other.Stats.Videos && Stats.Analyzed == other.Stats.Analyzed && BySource.Count == other.BySource.Count &&
            BySource.All(kv => other.BySource.GetValueOrDefault(kv.Key, -1) == kv.Value) &&
            Persons.Select(p => (p.Id, p.Name, p.MediaCount)).SequenceEqual(other.Persons.Select(p => (p.Id, p.Name, p.MediaCount)));
    }

    private SidebarCounts? _counts;
    private int _countsRunning;
    private bool _countsAgain;

    private void RefreshCounts()
    {
        if (Interlocked.Exchange(ref _countsRunning, 1) == 1)
        {
            _countsAgain = true;
            return;
        }
        _ = Task.Run(() =>
        {
            SidebarCounts? counts = null;
            try
            {
                var clusters = _host.People.UnnamedClusters();
                counts = new SidebarCounts(
                    clusters.Count + _host.People.UnnamedFigures(clusters.Select(c => c.Cluster).ToHashSet()).Count,
                    _host.People.SuggestionCount(),
                    InterestingMoments.Find(_host.Db).Count,
                    _host.Repo.Stats(Indexer.AnalysisVersion),
                    _host.Repo.CountBySource(),
                    _host.People.ListPersons());
            }
            catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Tried again at the next change.
            }
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _countsRunning, 0);
                if (counts != null && !counts.Same(_counts))
                {
                    _counts = counts;
                    BuildSidebar();
                }
                if (_countsAgain)
                {
                    _countsAgain = false;
                    RefreshCounts();
                }
            });
        });
    }

    /// <summary>The sidebar as the library is now; the slow counts follow in the background.</summary>
    private void RebuildSidebar()
    {
        BuildSidebar();
        RefreshCounts();
    }

    private void BuildSidebar()
    {
        // A folder added, removed or located elsewhere is watched from now on (off this thread: a network folder may be slow).
        if (_watcher is { } watcher) _ = Task.Run(watcher.Refresh);
        // Clearing the lists makes the bound controls push null into the selections; restore them afterwards.
        _rebuilding = true;
        var selected = _scope;
        var activeId = ActiveBucket?.Id;
        SidebarItems.Clear();
        var stats = _counts?.Stats;
        SidebarItems.Add(new SidebarEntry(SidebarKind.All, 0, "All photos and videos", stats?.Total.ToString("N0") ?? "",
            stats == null ? "" : $"{stats.Photos:N0} photos, {stats.Videos:N0} videos"));
        var best = _counts?.Best ?? 0;
        if (best > 0 || _host.PeopleModel != null)
            SidebarItems.Add(new SidebarEntry(SidebarKind.Best, 0, "Best moments", _counts == null ? "" : best.ToString("N0"), "The best moments of your videos"));
        SidebarItems.Add(new SidebarHeader("SOURCES", "Add a folder", SidebarKind.Source));
        var sources = _host.Repo.ListSources();
        _sources = sources;
        _changeable = _host.Jobs.Changeable().Select(s => s.Id).ToHashSet();
        var counts = _counts?.BySource ?? [];
        var mirrors = _host.Cloud.Mirrors().GroupBy(m => m.SourceId).ToDictionary(g => g.Key, g => g.First());
        var merges = _host.Cloud.Merges();
        foreach (var s in sources)
        {
            // A folder of a library restored from another computer, or a disk that is not connected.
            var away = s.Kind == SourceKinds.Local && !Directory.Exists(s.Root);
            var mirror = mirrors.GetValueOrDefault(s.Id);
            var entry = SourceEntry(s, away, counts.GetValueOrDefault(s.Id), mirror);
            if (_counts == null) entry = entry with { Count = "" };
            // A folder being added to a cloud folder: how far it is shows at the cloud folder it goes to.
            if (merges.FirstOrDefault(j => j.TargetId == s.Id) is { } incoming) entry = MergeTarget(entry, incoming);
            if (merges.FirstOrDefault(j => j.SourceId == s.Id) is { } outgoing)
                entry = entry with { CloudTip = $"Being added to {outgoing.TargetName}: see it there. Nothing here is deleted." };
            SidebarItems.Add(entry);
            // A clone is in the library from the start, at its place among the sources, with how far its copy is.
            if (mirror != null && _host.Cloud.MoveRequested(mirror.Id)) SidebarItems.Add(CloneEntry(s, mirror));
        }
        IsLibraryEmpty = sources.Count == 0;
        var persons = _counts?.Persons ?? [];
        var groups = _counts?.Groups ?? 0;
        var suggestions = _counts?.Suggestions ?? 0;
        if (persons.Count > 0 || groups > 0 || suggestions > 0 || _host.PeopleModel != null)
        {
            SidebarItems.Add(new SidebarHeader("PEOPLE", "", SidebarKind.Person));
            SidebarItems.Add(new SidebarEntry(SidebarKind.Groups, 0, "Who is this?", _counts == null ? "" : groups.ToString("N0"),
                "People nobody named yet: groups of faces, and people without a visible face (helmets, from behind)"));
            SidebarItems.Add(new SidebarEntry(SidebarKind.Review, 0, "Review suggestions", _counts == null ? "" : suggestions.ToString("N0"),
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
        // A clone that is done is a source of the same id (the folder here got a new one).
        var restored = selected == null ? null : SidebarItems.OfType<SidebarEntry>().FirstOrDefault(e => e.Kind == selected.Kind && e.Id == selected.Id)
            ?? (selected.Kind == SidebarKind.Clone ? SidebarItems.OfType<SidebarEntry>().FirstOrDefault(e => e.Kind == SidebarKind.Source && e.Id == selected.Id) : null);
        ActiveBucket = activeId is { } id ? Buckets.FirstOrDefault(b => b.Id == id) : null;
        OnPropertyChanged(nameof(HasBuckets));
        _rebuilding = false;
        // A removed scope falls back to the whole library (and searches again).
        SelectedSidebarItem = restored ?? SidebarItems.OfType<SidebarEntry>().First();
        // Read-only turned on or off: what can be done in the folder shown changes with it.
        if (SelectedSidebarItem is SidebarEntry now) _scope = now;
        OnPropertyChanged(nameof(IsCloudScope));
    }

    /// <summary>The progress of clones and of folders being added to a cloud folder, by MirrorScheduler's keys.</summary>
    private readonly Dictionary<string, CloneProgress> _clones = [];

    private CloneProgress ProgressOf(string key, string start)
    {
        if (!_clones.TryGetValue(key, out var progress)) _clones[key] = progress = new CloneProgress { Text = start };
        return progress;
    }

    private SidebarEntry MergeTarget(SidebarEntry entry, MergeInfo job)
    {
        var progress = ProgressOf(MirrorScheduler.MergeKey(job.SourceId), "adding");
        var failed = job.LastResult != null;
        progress.Text = failed ? "waits" : progress.Text == "waits" ? "adding" : progress.Text;
        return entry with
        {
            Progress = progress,
            ToolTip = $"{job.SourceName} is being added here" + (job.Subfolder.Length > 0 ? $", into the folder {job.Subfolder}." : "."),
            CloudIcon = failed ? MaterialIconKind.CloudAlertOutline : MaterialIconKind.CloudSyncOutline,
            CloudAttention = failed,
            CloudTip = failed ? "Adding a folder here could not go on. Click for why, or to stop it." : $"Adding {job.SourceName} here. Click to see how far it is, or to stop it.",
        };
    }

    private SidebarEntry CloneEntry(Source s, MirrorInfo mirror)
    {
        var progress = ProgressOf(MirrorScheduler.MirrorKey(mirror.Id), "copying");
        var waits = mirror.Gone > 0;
        var failed = !mirror.Enabled || mirror.LastResult is { } r && r != "OK";
        progress.Text = waits ? "waits" : failed ? "failed" : progress.Text is "waits" or "failed" ? "copying" : progress.Text;
        var where = $"{mirror.CloudName}, folder {mirror.Target}";
        return new SidebarEntry(SidebarKind.Clone, s.Id, _host.Cloud.CloneName(s.Name, mirror), "",
            $"Being copied to {where}. Once all files are there it is a source of its own; the folder {s.Name} on this computer is not touched.")
        {
            Progress = progress,
            CloudIcon = waits || failed ? MaterialIconKind.CloudAlertOutline : MaterialIconKind.CloudSyncOutline,
            CloudAttention = waits || failed,
            CloudTip = waits ? "Waits for you: click to decide about files deleted from the folder."
                : failed ? "The copy failed. Click for why, or to stop it."
                : "Click to see how far it is, or to stop it.",
        };
    }

    private void OnCopying(string key, Rclone.CopyProgress p)
    {
        if (_clones.TryGetValue(key, out var progress)) progress.Text = $"{p.Percent}%";
    }

    private SidebarEntry SourceEntry(Source s, bool away, long count, MirrorInfo? mirror)
    {
        var tip = away ? $"{s.Root} is not on this computer. Right click, Locate folder: say where it is now."
            : s.Kind == SourceKinds.Cloud ? $"In the cloud: files are fetched when opened ({s.Remote})" : s.Root;
        var locked = s.Protected && _host.Repo.ReadOnly.IsLocked(s.Id);
        var entry = new SidebarEntry(SidebarKind.Source, s.Id, away ? s.Name + " (not here)" : s.Name, count.ToString("N0"),
            tip + (locked ? "\nRead-only" : ""))
        {
            IsAway = away,
            IsLocked = locked,
            IsTurnedOff = s.Protected && !locked,
            CanUpload = _changeable.Contains(s.Id),
        };
        if (s.Kind == SourceKinds.Cloud)
            return s.ReadOnly
                ? entry with
                {
                    IconOverride = MaterialIconKind.CloudLockOutline, CloudIcon = MaterialIconKind.CloudLockOutline,
                    CloudTip = $"The backup of a folder on {s.HomeName ?? "another computer"}: here only for looking. Click to make this computer its home.",
                }
                : entry with
                {
                    IconOverride = MaterialIconKind.CloudOutline, CloudIcon = MaterialIconKind.CloudOutline,
                    CloudTip = "Lives in the cloud. Click to keep it on this computer too.",
                };
        if (mirror == null)
            return entry with { CloudIdle = true, CloudTip = "Back this folder up to a cloud, or clone it there..." };
        // Being cloned: the clone below shows it; this folder stays as it was.
        if (_host.Cloud.MoveRequested(mirror.Id))
            return entry with { CloudIdle = true, CloudTip = $"Being cloned to {mirror.CloudName}: see {s.Name} ({mirror.CloudName}) below. Nothing here is deleted." };
        if (mirror.Gone > 0 || !mirror.Enabled || mirror.LastResult is { } r && r != "OK")
            return entry with
            {
                CloudIcon = MaterialIconKind.CloudAlertOutline, CloudAttention = true,
                CloudTip = mirror.Gone > 0 ? $"{mirror.Gone:N0} files deleted here are still in the cloud. Click to decide." :
                    !mirror.Enabled ? "The backup stopped. Click for why." : "The last backup failed. Click for why.",
            };
        if (mirror.LastRunAt == null)
            return entry with { CloudIcon = MaterialIconKind.CloudSyncOutline, CloudTip = $"The first backup to {mirror.CloudName} is running." };
        return entry with { CloudIcon = MaterialIconKind.CloudCheckOutline, CloudDone = true, CloudTip = $"Backed up to {mirror.CloudName}. Click for more." };
    }

    /// <summary>Something waits for the user in the backups: said in the status bar while nothing runs.</summary>
    private string CloudSummary()
    {
        var mirrors = _host.Cloud.Mirrors();
        var gone = mirrors.Sum(m => m.Gone);
        var failed = mirrors.Count(m => !m.Enabled || m.LastResult is { } r && r != "OK");
        return gone > 0 ? $"{gone:N0} {(gone == 1 ? "file deleted here is" : "files deleted here are")} still in the cloud: click the orange cloud of the folder."
            : failed > 0 ? "A backup needs a look: click the orange cloud of the folder." : "";
    }

    /// <summary>A backup ran, a folder cloned to the cloud or got a home here: the sidebar shows it, copies get their twins' analysis.</summary>
    public void OnCloudChanged()
    {
        RebuildSidebar();
        if (MirrorText.Length == 0 || !MirrorText.StartsWith("Backing up", StringComparison.Ordinal) && !MirrorText.StartsWith("Cloning", StringComparison.Ordinal) && !MirrorText.StartsWith("Adding", StringComparison.Ordinal))
            MirrorText = CloudSummary();
        _host.Indexer.Wake();
    }

    // ---- read-only sources ----

    public ReadOnlyLock ReadOnly => _host.Repo.ReadOnly;

    /// <summary>A source became read-only or not: its row (lock, menu) and its window show it.</summary>
    public void OnReadOnlyChanged() => RebuildSidebar();

    public SourceCloudViewModel CreateSourceCloudViewModel(long sourceId)
    {
        var vm = new SourceCloudViewModel(_host, sourceId);
        vm.SourcesChanged += OnCloudChanged;
        return vm;
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
        UpdateScopeBanner();
        OnPropertyChanged(nameof(IsBucketScope));
        OnPropertyChanged(nameof(IsCloudScope));
        OnPropertyChanged(nameof(IsPersonScope));
        if (!same) ShowPersonFaces = false;
        OnPropertyChanged(nameof(IsGridMode));
        OnPropertyChanged(nameof(ShowGroupTree));
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

    // ---- changing folders that live in the cloud: uploading into them, moving and deleting ----

    /// <summary>The uploads and changes in the cloud the user asked for: how far they are, what did not go.</summary>
    public CloudJobsViewModel CloudJobs { get; }

    /// <summary>The sources in the cloud that can be changed from here now (not a computer's backup, not read-only).</summary>
    private HashSet<long> _changeable = [];

    /// <summary>The folder shown lives in the cloud and can be changed: its own actions show (upload, move, delete).</summary>
    public bool IsCloudScope => CurrentScope is { Kind: SidebarKind.Source, CanUpload: true } && IsGridMode;

    /// <summary>Where things put "here" go: the folder chosen in the tree, or the source shown; null when that is not a folder in the cloud.</summary>
    public (long SourceId, string Folder)? CurrentCloudFolder()
    {
        if (ShowGroupTree && SelectedGroup is { CanUpload: true, SourceId: { } id } node) return (id, node.Folder);
        return CurrentScope is { Kind: SidebarKind.Source, CanUpload: true } scope ? (scope.Id, "") : null;
    }

    public CloudPutViewModel PutUpload(IReadOnlyList<string> paths, (long SourceId, string Folder)? where) =>
        CloudPutViewModel.Upload(_host, paths, where?.SourceId, where?.Folder);

    public CloudPutViewModel PutImport((long SourceId, string Folder)? where) => CloudPutViewModel.Import(_host, where?.SourceId, where?.Folder);

    public CloudPutViewModel PutMoveFolder(GroupNode folder) => CloudPutViewModel.MoveFolder(_host, folder.SourceId!.Value, folder.Folder);

    /// <summary>The selection to move, with the folder of the first one chosen to start from.</summary>
    public CloudPutViewModel PutMoveSelection()
    {
        var items = SelectedTiles().Select(t => t.Item).DistinctBy(i => i.Id).ToList();
        var first = items[0];
        var folder = first.RelPath.Contains('/') ? first.RelPath[..first.RelPath.LastIndexOf('/')] : "";
        return CloudPutViewModel.MoveFiles(_host, items.Select(i => (i.SourceId, i.RelPath)).ToList(), first.SourceId, folder);
    }

    public int SelectedCount => SelectedTiles().Select(t => t.Item.Id).Distinct().Count();

    /// <summary>Why the selection cannot be moved or deleted from here; null when it can.</summary>
    public string? SelectionProblem()
    {
        var sources = SelectedTiles().Select(t => t.Item.SourceId).Distinct().ToList();
        if (sources.Count == 0) return "Select the photos or videos first: click one, Ctrl+click more, Shift+click a range, Ctrl+A all.";
        foreach (var id in sources.Where(id => !_changeable.Contains(id)))
        {
            var source = _sources.FirstOrDefault(s => s.Id == id);
            return source == null ? "The folder is not in the library any more."
                : source.Kind != SourceKinds.Cloud ? $"\"{source.Name}\" is a folder on a computer. Move or delete its files there, in the file manager: MomentFinder follows."
                : source.ReadOnly ? $"\"{source.Name}\" is the backup of a folder on {source.HomeName ?? "another computer"}: only that computer changes it."
                : $"\"{source.Name}\" is read-only. Turn read-only off first: right click it on the left.";
        }
        return null;
    }

    /// <summary>After the user confirmed it: the selection is deleted in the cloud, for good.</summary>
    public string? DeleteSelection()
    {
        try
        {
            foreach (var from in SelectedTiles().Select(t => t.Item).DistinctBy(i => i.Id).GroupBy(i => i.SourceId))
                _host.Jobs.Delete(from.Key, from.Select(i => i.RelPath).ToList());
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    /// <summary>After the user confirmed it: the folder is deleted in the cloud with everything in it.</summary>
    public string? DeleteFolder(GroupNode folder) => Try(() => _host.Jobs.DeleteFolder(folder.SourceId!.Value, folder.Folder));

    /// <summary>The folder gets another name, where it is.</summary>
    public string? RenameFolder(GroupNode folder, string name)
    {
        name = CloudService.CleanFolder(name.Replace('/', ' ').Replace('\\', ' '));
        if (name.Length == 0) return "Type the new name.";
        var parent = folder.Folder.Contains('/') ? folder.Folder[..folder.Folder.LastIndexOf('/')] : "";
        return Try(() => _host.Jobs.MoveFolder(folder.SourceId!.Value, folder.Folder, folder.SourceId.Value, CloudService.Join(parent, name)));
    }

    private static string? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    [ObservableProperty] private bool _isLibraryEmpty;

    private MediaFilter ScopeFilter() => WithDates(CurrentScope switch
    {
        { Kind: SidebarKind.Source or SidebarKind.Clone } e => new MediaFilter { SourceId = e.Id },
        { Kind: SidebarKind.Best } => new MediaFilter { BestMoments = true, Kind = MediaKind.Video },
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

    /// <summary>The library of another computer being copied here, or just put in place.</summary>
    public LibraryCopyViewModel LibraryCopy { get; }

    public LibraryCloudViewModel CreateLibraryCloudViewModel() => new(_host, LibraryCopy);

    public AddCloudFolderViewModel CreateAddCloudFolderViewModel()
    {
        var vm = new AddCloudFolderViewModel(_host);
        vm.Added += id =>
        {
            RebuildSidebar();
            SelectedSidebarItem = SidebarItems.OfType<SidebarEntry>().FirstOrDefault(e => e.Kind == SidebarKind.Source && e.Id == id);
        };
        return vm;
    }

    /// <summary>
    /// macOS: the originals of the Photos app (iCloud Photos with "Download Originals to this Mac") as a
    /// source. Reading them needs Full Disk Access or the Photos permission for MomentFinder.
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

    /// <summary>The folder of a source is somewhere else on this computer: the same files are found there again.</summary>
    public void RelocateSource(SidebarEntry entry, string folder)
    {
        _host.Repo.SetSourceRoot(entry.Id, folder);
        RebuildSidebar();
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

    partial void OnShowPersonFacesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGridMode));
        OnPropertyChanged(nameof(ShowGroupTree));
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
        _anchorIndex = Shown.IndexOf(selected[0]);
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
        var best = CurrentScope?.Kind == SidebarKind.Best && !hasText;
        var old = Tiles;
        var bucket = ActiveBucket?.Id;
        // Everything that grows with the library is done off the UI thread (millions of photos): the search, the
        // tiles (the ones already shown kept with their thumbnails), the bucket marks.
        ObservableCollection<TileViewModel>? tiles;
        HashSet<long> inBucket = [];
        int count;
        var started = DateTime.UtcNow;
        try
        {
            (tiles, count) = await Task.Run(() =>
            {
                var hits = _host.Search.Search(text, scope);
                cts.Token.ThrowIfCancellationRequested();
                // The same items in the same order: nothing changes (the tiles and their thumbnails stay).
                // (A photo moved to another folder is another item here: its tile knows where the file is.)
                if (keepSelection && hits.Count == old.Count &&
                    hits.Select(h => (h.Item.Id, h.Item.SourceId, h.Item.RelPath)).SequenceEqual(old.Select(t => (t.Item.Id, t.Item.SourceId, t.Item.RelPath))))
                    return ((ObservableCollection<TileViewModel>?)null, hits.Count);
                var places = new Dictionary<(long, int), (long, string)>();
                foreach (var h in hits) places.TryAdd((h.Item.Id, h.TimeMs), (h.Item.SourceId, h.Item.RelPath));
                var kept = keepSelection
                    ? old.Where(t => places.TryGetValue((t.Item.Id, t.StartMs), out var place) && place == (t.Item.SourceId, t.Item.RelPath))
                        .GroupBy(t => (t.Item.Id, t.StartMs)).ToDictionary(g => g.Key, g => g.First())
                    : [];
                var notes = best ? BestNotes() : null;
                inBucket = bucket is { } b ? _host.Buckets.MediaIds(b).ToHashSet() : [];
                var list = new List<TileViewModel>(hits.Count);
                // Tiles shown already are touched on the UI thread only (below); new ones are not shown yet.
                foreach (var h in hits)
                    list.Add(kept.GetValueOrDefault((h.Item.Id, h.TimeMs)) ?? new TileViewModel(h.Item, h.TimeMs) { Note = notes?.GetValueOrDefault(h.Item.Id) });
                return (new ObservableCollection<TileViewModel>(list), hits.Count);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;
        _lastSearch = DateTime.UtcNow - started;
        if (tiles != null)
        {
            foreach (var t in tiles)
            {
                if (!keepSelection) t.IsSelected = false;
                t.InBucket = inBucket.Contains(t.Item.Id);
            }
            Tiles = tiles;
            _anchorIndex = -1;
        }
        UpdateSummary(count, hasText);
        if (!keepSelection) ResultsReset?.Invoke();
    }

    /// <summary>How long the last search took: a library of millions is not searched again every few seconds while it is indexed.</summary>
    private TimeSpan _lastSearch;

    /// <summary>For each video of Best moments: its score and its moments, when and who (to see why it is there).</summary>
    private Dictionary<long, string> BestNotes()
    {
        var names = _host.People.ListPersons().ToDictionary(p => p.Id, p => p.Name);
        static string At(int ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
        return InterestingMoments.Find(_host.Db).ToDictionary(v => v.MediaId, v =>
            $"Score {v.Score:0.0}\n" + string.Join("\n", v.Moments.Select(m =>
                $"{At(m.StartMs)} to {At(m.EndMs)}: {string.Join(", ", m.PersonIds.Select(id => names.GetValueOrDefault(id, "?")))} ({m.Score:0.0})")));
    }

    private void UpdateSummary(int count, bool hasText)
    {
        var text = Format.Count(count, "item", "items");
        UpdateScopeBanner();
        if (CurrentScope?.Kind == SidebarKind.Best && !hasText)
            text = count == 0 ? "No best moments yet. They come with the people you name in your videos (Who is this?)."
                : Format.Count(count, "video", "videos");
        else if (hasText && !_host.Search.CanSearchText)
            text += " with these words in the file name. To search by what is in the picture, download the AI models (above).";
        else if (hasText)
        {
            text += " best matches";
            var phrase = QueryParser.Parse(SearchText, _host.Search.People).Text;
            var english = _host.Search.QueryPhrase(phrase);
            if (!string.Equals(english.Trim('.', ' '), phrase.Trim('.', ' '), StringComparison.OrdinalIgnoreCase))
                text += $" for \"{english.Trim('.', ' ').ToLowerInvariant()}\"";
            if (_counts?.Stats is { } stats && stats.Analyzed < stats.Total)
                text += $" (the AI has looked at {stats.Analyzed:N0} of {stats.Total:N0} so far, search again later for more)";
        }
        ResultSummary = text;
    }

    public IEnumerable<TileViewModel> SelectedTiles() => Tiles.Where(t => t.IsSelected);

    /// <summary>Click: select one; Ctrl: toggle; Shift: range from the last click.</summary>
    public void Select(TileViewModel tile, bool toggle, bool range)
    {
        var index = Shown.IndexOf(tile);
        if (index < 0) return;
        if (range && _anchorIndex >= 0)
        {
            var (a, b) = (Math.Min(_anchorIndex, index), Math.Max(_anchorIndex, index));
            if (!toggle)
                foreach (var t in Tiles) t.IsSelected = false;
            for (var i = a; i <= b; i++) Shown[i].IsSelected = true;
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
    public TileViewModel? MoveSelection(int delta, bool extend) =>
        MoveSelectionTo(_anchorIndex >= 0 ? _anchorIndex + delta : 0, extend);

    /// <summary>Selects the tile at this place of the shown order (Up and Down: the view knows the rows).</summary>
    public TileViewModel? MoveSelectionTo(int index, bool extend)
    {
        if (Shown.Count == 0) return null;
        var to = Math.Clamp(index, 0, Shown.Count - 1);
        if (!extend)
            foreach (var t in Tiles) t.IsSelected = false;
        Shown[to].IsSelected = true;
        _anchorIndex = to;
        return Shown[to];
    }

    /// <summary>The last clicked or reached tile's place in the shown order; -1 when none.</summary>
    public int AnchorIndex => AnchorTile == null ? -1 : _anchorIndex;

    public TileViewModel? AnchorTile => _anchorIndex >= 0 && _anchorIndex < Shown.Count ? Shown[_anchorIndex] : null;

    // ---- playback ----

    /// <summary>Opens the viewer on one item; arrows go through the whole result.</summary>
    /// <summary>Full screen from the panel: the same moment, paused if the panel was paused.</summary>
    public void OpenAt(TileViewModel tile, int positionMs, bool paused)
    {
        var index = Shown.IndexOf(tile);
        if (index < 0) return;
        var player = CreatePlayer(Shown, index, autoPlay: false);
        if (positionMs > 0) player.ResumeAtMs = positionMs;
        player.PauseOnOpen = paused;
        PlayerRequested?.Invoke(player, true);
    }

    public void Open(TileViewModel tile)
    {
        var index = Shown.IndexOf(tile);
        if (index < 0) return;
        PlayerRequested?.Invoke(CreatePlayer(Shown, index, autoPlay: false), true);
    }

    /// <summary>Opens one item by id (from a face crop); a video at the moment of the crop, with the people boxes on.</summary>
    public void OpenMedia(long mediaId, int timeMs = 0, bool showPeople = false)
    {
        if (_host.Repo.Get(mediaId) is not { } item) return;
        var tile = timeMs > 0 ? new TileViewModel(item, timeMs) : Tiles.FirstOrDefault(t => t.Item.Id == mediaId) ?? new TileViewModel(item, 0);
        var player = CreatePlayer([tile], 0, autoPlay: false);
        // Opened to say who someone is: the boxes for this viewer, the remembered choice stays.
        if (showPeople) player.ShowPeopleHere();
        player.PauseOnOpen = showPeople;
        PlayerRequested?.Invoke(player, true);
    }

    /// <summary>Several photos (and video moments) in the viewer with the people boxes: to see who someone is when a crop is too small.</summary>
    public void OpenMoments(IEnumerable<(long MediaId, int TimeMs)> moments)
    {
        var tiles = moments.Select(m => _host.Repo.Get(m.MediaId) is { } item ? new TileViewModel(item, m.TimeMs) : null)
            .OfType<TileViewModel>().ToList();
        if (tiles.Count == 0) return;
        var player = CreatePlayer(tiles, 0, autoPlay: false);
        player.ShowPeopleHere();
        player.PauseOnOpen = true;
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
        var list = selected.Count > 1 ? Shown.Where(t => t.IsSelected).Distinct().ToList() : Shown.ToList();
        if (list.Count == 0) return;
        if (shuffle) list = list.OrderBy(_ => Random.Shared.Next()).ToList();
        PlayerRequested?.Invoke(CreatePlayer(list, 0, autoPlay: true), !OpenInPanel);
    }

    private PlayerViewModel CreatePlayer(IReadOnlyList<TileViewModel> tiles, int index, bool autoPlay)
    {
        var (personIds, names, hasText) = CurrentQuery();
        // Best moments: each video plays its moments worth watching (unless words were searched for).
        var best = CurrentScope?.Kind == SidebarKind.Best && !hasText
            ? InterestingMoments.Find(_host.Db).ToDictionary(v => v.MediaId, v => v.Moments.Select(m => (m.StartMs, m.EndMs)).ToList())
            : null;
        var entries = new Playlist(tiles, t => new PlaylistEntry(t.Item, t.StartMs,
            t.Item.IsVideo && hasText ? [(Math.Max(0, t.StartMs - 1000), t.StartMs + MatchMomentMs)]
            : best?.GetValueOrDefault(t.Item.Id)));

        // What the videos can play: whole, the matching part, the best moments, the searched people, anybody else in them.
        var whole = new MomentChoice("Whole videos", MomentKind.Whole, []);
        var choices = new List<MomentChoice> { whole };
        if (hasText) choices.Add(new MomentChoice("Only the matching part", MomentKind.Match, []));
        if (best != null) choices.Add(new MomentChoice("Only the best moments", MomentKind.Match, []));
        if (personIds.Count > 1) choices.Add(new MomentChoice($"Only {string.Join(" + ", names)}", MomentKind.Person, personIds));
        var personNames = _host.People.ListPersons().ToDictionary(p => p.Id, p => p.Name);
        var videoIds = tiles.Where(t => t.Item.IsVideo).Select(t => t.Item.Id).ToList();
        // Who is in the videos only orders the choices (the viewer offers whoever is in the video it shows):
        // over a whole large library it is not worth the wait.
        if (videoIds.Count > 0)
            foreach (var id in personIds.Concat(videoIds.Count <= 5000 ? _host.People.PersonsIn(videoIds) : []).Distinct())
                if (personNames.TryGetValue(id, out var name))
                    choices.Add(new MomentChoice($"Only {name}", MomentKind.Person, [id]));
        // The best moments are what that list is for: they play first even after whole videos were chosen elsewhere.
        var initial = best != null ? choices.First(c => c.Kind == MomentKind.Match)
            : !VideoHighlightsOnly ? whole
            : personIds.Count > 0 ? choices.FirstOrDefault(c => c.Kind == MomentKind.Person && c.PersonIds.SequenceEqual(personIds)) ?? whole
            : hasText ? choices.First(c => c.Kind == MomentKind.Match) : whole;
        return new PlayerViewModel(entries, index, autoPlay, new PlayerSettings(SlideSeconds, Volume, _playbackRate, _loopVideos, _host.Db.GetSetting("show_people") == "1"), this,
            videoIds.Count > 0 ? choices : [whole], initial);
    }

    /// <summary>The viewer's volume, kept for the next time.</summary>
    public int Volume => int.TryParse(_host.Db.GetSetting("volume"), CultureInfo.InvariantCulture, out var v) ? v : 80;

    private double _playbackRate = 1;
    private bool _loopVideos;

    /// <summary>Slow motion and loop stay for the next viewer while the app runs (an analysis goes over several videos).</summary>
    public void RememberPlayback(double rate, bool loop) => (_playbackRate, _loopVideos) = (rate, loop);

    /// <summary>Who is who stays on for the next viewer and the next start, until it is switched off.</summary>
    public void RememberShowPeople(bool on) => _host.Db.SetSetting("show_people", on ? "1" : "0");

    /// <summary>People boxes follow the people in a playing video, or show only on a paused one.</summary>
    public bool BoxesWhilePlaying
    {
        get => _host.Db.GetSetting("boxes_playing") != "0";
        set
        {
            _host.Db.SetSetting("boxes_playing", value ? "1" : "0");
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoxesOnlyPaused));
        }
    }

    public bool BoxesOnlyPaused
    {
        get => !BoxesWhilePlaying;
        set => BoxesWhilePlaying = !value;
    }

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
    public PlayerViewModel CreatePreview(int index) => CreatePlayer(Shown, index, autoPlay: false);

    // ---- background work ----

    // ---- how long the background work takes ----

    private IndexerPhase _phase;
    private DateTime _phaseStarted;
    /// <summary>Recent (time, done) points of the running phase: the speed over the last minutes, not since the start.</summary>
    private readonly Queue<(DateTime At, int Done)> _progressSamples = new();

    // ---- the folder shown: being read, or empty ----
    [ObservableProperty] private bool _scopeBanner;
    [ObservableProperty] private string _scopeBannerText = "";
    /// <summary>The banner shows a progress bar (reading), not only words.</summary>
    [ObservableProperty] private bool _scopeBusy;
    [ObservableProperty] private double _scopeReadingProgress;
    [ObservableProperty] private bool _scopeReadingKnown;

    /// <summary>
    /// A folder read for the first time (thousands of photos from the cloud take a while) says how far it is, and an
    /// empty one why, above where its photos would be: not only in the status bar.
    /// </summary>
    /// <summary>The sources as the sidebar shows them (the indexer reports many times a second: no query for each).</summary>
    private List<Source> _sources = [];

    private void UpdateScopeBanner()
    {
        var st = _host.Indexer.Status;
        var source = CurrentScope is { Kind: SidebarKind.Source or SidebarKind.Clone } scope
            ? _sources.FirstOrDefault(s => s.Id == scope.Id) : null;
        if (source == null)
        {
            ScopeBanner = false;
            return;
        }
        var cloud = source.Kind == SourceKinds.Cloud;
        var away = CurrentScope?.IsAway == true;
        var reading = st.Phase == IndexerPhase.Scanning && _host.Indexer.ScanningSourceId == source.Id;
        var waiting = !reading && source.LastScanAt == null && !away;
        var empty = !reading && !waiting && Tiles.Count == 0 && !IsDateFiltered &&
                    string.IsNullOrWhiteSpace(QueryParser.Parse(SearchText, _host.Search.People).Text);
        ScopeBanner = reading || waiting || empty;
        ScopeBusy = reading || waiting;
        ScopeReadingKnown = reading && st.Total > 0;
        ScopeReadingProgress = ScopeReadingKnown ? 100.0 * st.Done / st.Total : 0;
        var where = cloud ? source.Remote?[(source.Remote.IndexOf(':') + 1)..] : source.Root;
        ScopeBannerText =
            reading && st.Total > 0 ? $"Reading this folder{(cloud ? " from the cloud" : "")}: {st.Done:N0} of {st.Total:N0} files. What is read shows below as it comes."
            : reading ? (cloud ? "Looking through the folder in the cloud (its folders inside too)..." : "Looking through the folder...")
            : waiting ? "This folder waits to be read: another one is read first."
            : away ? $"The folder {source.Root} is not on this computer now."
            : $"No photos or videos found in {where} (its folders inside too). If it is not the right folder, remove it and add the right one" +
              (source.Protected ? " (turn read-only off first)." : ".");
    }

    private void OnIndexerStatus(IndexerStatus st)
    {
        UpdateScopeBanner();
        IndexerPaused = st.Paused;
        IndexerBusy = st.Phase != IndexerPhase.Idle;
        IndexerProgress = st.Total > 0 ? 100.0 * st.Done / st.Total : 0;
        var now = DateTime.UtcNow;
        if (st.Phase != _phase || st.Paused)
        {
            _phase = st.Phase;
            _phaseStarted = now;
            _progressSamples.Clear();
        }
        if (st.Total <= 0 || st.Phase == IndexerPhase.Idle)
        {
            IndexerText = st.Message;
            return;
        }
        _progressSamples.Enqueue((now, st.Done));
        while (_progressSamples.Count > 2 && now - _progressSamples.Peek().At > TimeSpan.FromMinutes(3)) _progressSamples.Dequeue();

        var text = $"{st.Message} {st.Done:N0} / {st.Total:N0}";
        var elapsed = now - _phaseStarted;
        if (elapsed > TimeSpan.FromSeconds(5)) text += $"  \u00b7  {Format.Span(elapsed)} so far";
        // An estimate once there is something to measure: a few items over at least 10 s.
        var (firstAt, firstDone) = _progressSamples.Peek();
        var span = (now - firstAt).TotalSeconds;
        var made = st.Done - firstDone;
        if (span >= 10 && made >= 3)
        {
            var left = TimeSpan.FromSeconds((st.Total - st.Done) / (made / span));
            text += $"  \u00b7  about {Format.Span(left)} left";
        }
        IndexerText = text;
    }

    [RelayCommand]
    /// <summary>All background work: the reading and analysis, and what goes to the cloud.</summary>
    private void TogglePause() => _host.Indexer.Paused = _host.Mirrors.Paused = _host.Jobs.Paused = _host.Sync.Paused = !_host.Indexer.Paused;

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
            if (await Task.Run(_host.TryLoadPlaces)) RebuildGroups();
            var people = await Task.Run(_host.TryLoadPeopleModel);
            var parts = new List<string>();
            if (scene) parts.Add("search");
            if (people) parts.Add("people");
            var device = _host.SceneModel?.Device ?? _host.PeopleModel?.Device;
            // CPU, or the graphics card (DirectML, CUDA, CoreML).
            var where = device == null ? "" : device.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? " on the processor" : " on the graphics card";
            ModelText = $"AI ready: {string.Join(", ", parts)}{where}" + (missing.Count > 0 ? ", more after the download" : "");
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

    private FolderWatcher? _watcher;

    public void Dispose()
    {
        _watcher?.Dispose();
        _searchCts?.Cancel();
        _downloadCts?.Cancel();
    }
}
