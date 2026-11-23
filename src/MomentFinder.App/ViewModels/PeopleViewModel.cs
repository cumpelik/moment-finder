using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.Analysis;
using MomentFinder.App.Services;
using MomentFinder.People;

namespace MomentFinder.App.ViewModels;

/// <summary>A face or person crop in a gallery (an unnamed group, the faces of a person, the review).</summary>
public sealed partial class CropViewModel : ObservableObject
{
    public long DetectionId { get; }
    public long MediaId { get; }
    public DetectionKind Kind { get; }
    public long? Cluster { get; init; }
    /// <summary>A figure without a usable face: its video track (or the detection in a photo).</summary>
    public long? FigureKey { get; init; }
    public int TimeMs { get; init; }
    public long Count { get; init; }
    public string Caption { get; init; } = "";

    /// <summary>Everything the picture stands for (the review: a figure of a video is its whole track).</summary>
    public IReadOnlyList<long> DetectionIds { get; init; } = [];

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private bool _isSelected;
    /// <summary>In the review: the user said this one is not the person asked about.</summary>
    [ObservableProperty] private bool _isNot;
    /// <summary>In the review: the one whose whole photo is shown below.</summary>
    [ObservableProperty] private bool _isCurrent;

    public CropViewModel(long detectionId, long mediaId, DetectionKind kind)
    {
        DetectionId = detectionId;
        MediaId = mediaId;
        Kind = kind;
    }

    public bool IsBody => Kind == DetectionKind.Body;

    public void Load(string path, int width)
    {
        if (Image != null) return;
        Image = Decode(path, width);
    }

    private static Bitmap? Decode(string path, int width)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var s = File.OpenRead(path);
            return Bitmap.DecodeToWidth(s, width);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // ---- in a gallery of thousands: decoded only while on screen, off the UI thread ----
    private Func<string>? _path;
    private int _width;
    private int _version;

    /// <summary>
    /// Where the picture is; it is found (a missing crop is cut from the thumbnail) and decoded when the card comes on
    /// screen (Show), in the background: thousands of cards would stop the window for seconds.
    /// </summary>
    public void LoadLater(Func<string> path, int width)
    {
        _path = path;
        _width = width;
    }

    public void Show()
    {
        if (Image != null || _path is not { } path) return;
        var version = ++_version;
        var width = _width;
        _ = Task.Run(() => Decode(path(), width)).ContinueWith(t =>
        {
            if (version == _version && t.Result is { } bitmap) Image = bitmap;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Off screen: the picture is let go (thousands of them would fill the memory).</summary>
    public void Hide()
    {
        if (_path == null) return;
        _version++;
        Image = null;
    }
}

/// <summary>
/// What a dialog that asks who somebody is shows: the known people, the most likely first (the first Likely of them
/// are close enough to be offered with one click), and the picture of the person asked about.
/// </summary>
public sealed record WhoQuestion(IReadOnlyList<string> Names, int Likely, string? Picture);

/// <summary>A titled part of Who is this: a day out with its place, or a video.</summary>
public sealed class FigureSection(string title, string count, List<CropViewModel> cards)
{
    public string Title { get; } = title;
    public string Count { get; } = count;
    public ObservableCollection<CropViewModel> Cards { get; } = new(cards);
}

/// <summary>
/// The people screens: unnamed groups of faces to name, the review ("Is this Tomáš?", his guessed pictures
/// together), and the faces of one person to correct. Every answer goes to the database and the matching runs again in the background.
/// </summary>
public sealed partial class PeopleViewModel : ObservableObject
{
    private readonly LibraryHost _host;
    private readonly ThumbnailLoader _thumbnails;
    private readonly Action _changed;
    private Dictionary<long, string> _names = [];

    [ObservableProperty] private ObservableCollection<CropViewModel> _clusters = [];
    [ObservableProperty] private ObservableCollection<FigureSection> _figureSections = [];

    public IReadOnlyList<string> FigureGroupings { get; } = ["By day and place", "By video"];
    [ObservableProperty] private string _figureGrouping = "By day and place";
    /// <summary>People seen in one video moment only are mostly strangers passing by: hidden unless asked for.</summary>
    [ObservableProperty] private bool _showSeenOnce;
    [ObservableProperty] private string _seenOnceText = "";
    [ObservableProperty] private bool _hasSeenOnce;
    private List<PeopleRepository.FigureInfo> _figures = [];
    /// <summary>The photos and videos of the figures, and the GPS positions of their days: read with the figures.</summary>
    private Dictionary<long, Library.MediaItem> _figureItems = [];
    private Dictionary<long, List<(double Lat, double Lon)>> _eventPositions = [];

    partial void OnFigureGroupingChanged(string value) => BuildFigureSections();
    partial void OnShowSeenOnceChanged(bool value) => BuildFigureSections();

    /// <summary>
    /// The figures in sections (a day out with its place, or a video), the people seen most often first. Thumbnails
    /// load only for what is shown.
    /// </summary>
    private void BuildFigureSections()
    {
        var built = new List<FigureSection>();
        var once = _figures.Count(f => f.Tracks <= 1);
        HasSeenOnce = once > 0;
        SeenOnceText = $"Also people seen only once ({once:N0})";
        var shown = ShowSeenOnce ? _figures : _figures.Where(f => f.Tracks > 1).ToList();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        IEnumerable<IGrouping<long, PeopleRepository.FigureInfo>> sections;
        Func<IGrouping<long, PeopleRepository.FigureInfo>, string> title;
        if (FigureGrouping == "By video")
        {
            sections = shown.GroupBy(f => f.MediaId).OrderByDescending(g => g.Max(f => f.TakenAt));
            title = g => _figureItems.GetValueOrDefault(g.Key) is { } item ? $"{item.FileName}  \u00b7  {item.Taken.ToString("d. M. yyyy", culture)}" : "";
        }
        else
        {
            var positions = _eventPositions;
            sections = shown.GroupBy(f => f.EventId).OrderByDescending(g => g.Max(f => f.TakenAt));
            title = g =>
            {
                var day = DateTime.UnixEpoch.AddMilliseconds(g.Max(f => f.TakenAt)).ToString("dddd d. MMMM yyyy", culture);
                return _host.Places?.MostCommon(positions.GetValueOrDefault(g.Key, [])) is { } place ? $"{day}  \u00b7  {place}" : day;
            };
        }
        foreach (var g in sections)
        {
            var cards = g.OrderByDescending(f => f.Tracks).ThenByDescending(f => f.Media).Select(f =>
            {
                var item = _figureItems.GetValueOrDefault(f.MediaId);
                var crop = new CropViewModel(f.CoverId, f.MediaId, DetectionKind.Body)
                {
                    FigureKey = f.GroupKey,
                    TimeMs = f.TimeMs,
                    Caption = f.Media > 1 ? $"{f.Media} videos and photos" :
                        f.Tracks > 1 ? $"{f.Tracks} times in {item?.FileName}" :
                        item == null ? "" : item.IsVideo ? $"{item.FileName} {TimeSpan.FromMilliseconds(f.TimeMs):m\\:ss}" : item.FileName,
                };
                crop.LoadLater(() => CropPath(f.CoverId), 160);
                return crop;
            }).ToList();
            built.Add(new FigureSection(title(g), Format.Count(cards.Count, "person", "people"), cards));
        }
        FigureSections = new ObservableCollection<FigureSection>(built);
        HasFigures = FigureSections.Count > 0 || HasSeenOnce;
    }
    [ObservableProperty] private ObservableCollection<CropViewModel> _personCrops = [];

    [ObservableProperty] private string _clustersSummary = "";
    [ObservableProperty] private bool _hasClusters;
    [ObservableProperty] private bool _hasFigures;
    [ObservableProperty] private string _reviewQuestion = "";
    [ObservableProperty] private string _reviewProgress = "";
    [ObservableProperty] private bool _hasReview;
    [ObservableProperty] private string _reviewDetail = "";
    /// <summary>The pictures of the question on screen: the guesses for one person.</summary>
    [ObservableProperty] private ObservableCollection<CropViewModel> _reviewCrops = [];
    /// <summary>The person as known already, to compare with (figures: what the person wore that day).</summary>
    [ObservableProperty] private ObservableCollection<CropViewModel> _reviewKnown = [];
    [ObservableProperty] private string _reviewKnownText = "";
    [ObservableProperty] private bool _hasReviewKnown;
    [ObservableProperty] private string _reviewYesText = "Yes (Y)";
    [ObservableProperty] private string _reviewCrossAllText = "Cross out all (A)";

    /// <summary>The picture whose whole photo is shown under the pictures of the question (pointed at, or moved to with the arrows).</summary>
    [ObservableProperty] private CropViewModel? _reviewCurrent;
    /// <summary>The whole photo (or the video at that moment): its thumbnail at once, the photo itself a moment later.</summary>
    [ObservableProperty] private Bitmap? _reviewPhoto;
    /// <summary>The size the photo is laid out in, and the box of the person in it, in the same units.</summary>
    [ObservableProperty] private double _photoWidth = 1;
    [ObservableProperty] private double _photoHeight = 1;
    [ObservableProperty] private double _boxLeft;
    [ObservableProperty] private double _boxTop;
    [ObservableProperty] private double _boxWidth;
    [ObservableProperty] private double _boxHeight;
    [ObservableProperty] private double _boxStroke = 3;
    [ObservableProperty] private bool _hasBox;
    /// <summary>How the user turned the photo (degrees clockwise): it is shown upright, as in the viewer.</summary>
    [ObservableProperty] private int _photoRotation;
    [ObservableProperty] private string _photoWhen = "";
    [ObservableProperty] private string _photoWhere = "";
    [ObservableProperty] private string _photoLikeness = "";
    [ObservableProperty] private Person? _person;
    [ObservableProperty] private string _personSummary = "";

    public PeopleViewModel(LibraryHost host, ThumbnailLoader thumbnails, Action changed)
    {
        _host = host;
        _thumbnails = thumbnails;
        _changed = changed;
        host.Identity.Answered += gained =>
        {
            // Not done (the library was busy): the analysis does it when it gets to it.
            if (gained == null) host.Indexer.RequestIdentity();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Answered?.Invoke(gained));
        };
        host.Indexer.PeopleFound += id => Avalonia.Threading.Dispatcher.UIThread.Post(() => PeopleFound?.Invoke(id));
        host.Identity.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Rematched?.Invoke());
    }

    /// <summary>The people of this photo or video were looked for again (it was turned): its boxes are other ones now. On the UI thread.</summary>
    public event Action<long>? PeopleFound;

    /// <summary>The matching changed who is who somewhere (after an analysis, after an answer). On the UI thread.</summary>
    public event Action? Rematched;

    /// <summary>
    /// The matching after the user's answers is done: how far they reached, null when it could not be done now
    /// (IdentityService.Answered, on the UI thread).
    /// </summary>
    public event Action<IdentityService.Reach?>? Answered;

    private string CropPath(long detectionId) => CropFallback.Ensure(_host.Paths, _host.Db, detectionId);

    private void AfterChange()
    {
        _host.RefreshNames();
        // Right away, not when the analysis of the library gets to it: the user waits to see where the answer reaches.
        _host.Identity.RecomputeSoon();
        _changed();
    }

    public IReadOnlyList<string> PersonNames() => _host.People.ListPersons().Select(p => p.Name).ToList();

    /// <summary>Who a card of Who is this may be (a group of faces or of figures).</summary>
    public Task<WhoQuestion> AskAsync(CropViewModel card) => AskAsync(
        card.FigureKey is { } key ? _host.People.FigureDetectionIds(key) :
        card.Cluster is { } cluster ? _host.People.ClusterDetectionIds(cluster) : [card.DetectionId], card.DetectionId);

    /// <summary>Who a box in the viewer may be: its whole video track and the faces on it say.</summary>
    public Task<WhoQuestion> AskBoxAsync(long detectionId) => AskAsync(_host.People.SamePersonDetectionIds(detectionId), detectionId);

    /// <summary>Who else a picture of the review may be, when it is not who the analysis thought.</summary>
    public Task<WhoQuestion> AskReviewAsync(CropViewModel crop) => AskAsync(crop.DetectionIds, crop.DetectionId, _page?.PersonId);

    /// <summary>Who the selected pictures of the person shown may be instead.</summary>
    public Task<WhoQuestion> AskSelectedAsync()
    {
        var selected = PersonCrops.Where(c => c.IsSelected).Select(c => c.DetectionId).ToList();
        return selected.Count == 0 ? Task.FromResult(new WhoQuestion(PersonNames(), 0, null)) : AskAsync(selected, selected[0], Person?.Id);
    }

    /// <param name="pictureOf">The detection whose picture stands for the person asked about.</param>
    /// <param name="not">Who it is known not to be (the question is "who else"): left out.</param>
    private Task<WhoQuestion> AskAsync(IReadOnlyCollection<long> detectionIds, long pictureOf, long? not = null) => Task.Run(() =>
    {
        var candidates = _host.Identity.Candidates(detectionIds).Where(c => c.Person.Id != not).ToList();
        var picture = CropPath(pictureOf);
        return new WhoQuestion(candidates.Select(c => c.Person.Name).ToList(), candidates.Count(c => c.Likely), File.Exists(picture) ? picture : null);
    });

    // ---- unnamed groups ----

    /// <summary>The photos of an unnamed group, to see the whole pictures (a face crop alone can be too small to tell).</summary>
    public List<(long MediaId, int TimeMs)> ClusterMoments(CropViewModel card) =>
        card.Cluster is { } cluster ? _host.People.ClusterMoments(cluster, 30) : [];

    private int _clustersVersion;

    /// <summary>
    /// Who is this: read in the background (thousands of faces in a big library), shown when ready; what is shown
    /// meanwhile stays.
    /// </summary>
    public async void LoadClusters()
    {
        var version = ++_clustersVersion;
        if (Clusters.Count == 0 && FigureSections.Count == 0) ClustersSummary = "Looking for the people nobody named yet...";
        var (groups, figures, items, positions) = await Task.Run(() =>
        {
            var groups = _host.People.UnnamedClusters();
            var figures = _host.People.UnnamedFigures(groups.Select(g => g.Cluster).ToHashSet());
            var items = _host.Repo.Query(new Library.MediaFilter { Ids = figures.Select(f => f.MediaId).Distinct().ToList() })
                .ToDictionary(i => i.Id);
            var positions = _host.Repo.EventPositions(figures.Select(f => f.EventId).Distinct());
            return (groups, figures, items, positions);
        });
        if (version != _clustersVersion) return;
        Clusters = new ObservableCollection<CropViewModel>(groups.Select(c =>
        {
            var crop = new CropViewModel(c.CoverId, 0, DetectionKind.Face)
            {
                Cluster = c.Cluster,
                Count = c.Media,
                Caption = c.Media == 1 ? "1 photo" : $"{c.Media} photos",
            };
            crop.LoadLater(() => CropPath(c.CoverId), 160);
            return crop;
        }));
        _figures = figures;
        _figureItems = items;
        _eventPositions = positions;
        BuildFigureSections();
        HasClusters = Clusters.Count > 0;
        ClustersSummary = Clusters.Count + _figures.Count == 0
            ? "Everybody found so far has a name. New people appear here as the analysis goes through the photos and videos."
            : "Click a person and write who it is. An existing name adds them to that person, and the analysis then " +
              "looks for them elsewhere (by the face, and on the same day also by the clothes). The eye hides strangers for good.";
    }

    public void NameCluster(CropViewModel card, string name)
    {
        if (card.Cluster is not { } cluster || string.IsNullOrWhiteSpace(name)) return;
        var person = _host.People.FindOrCreatePerson(name);
        _host.People.Confirm(_host.People.ClusterDetectionIds(cluster), person.Id);
        if (person.CoverDetectionId == null) _host.People.SetCover(person.Id, card.DetectionId);
        Clusters.Remove(card);
        HasClusters = Clusters.Count > 0;
        AfterChange();
    }

    public void NameFigure(CropViewModel card, string name)
    {
        if (card.FigureKey is not { } key || string.IsNullOrWhiteSpace(name)) return;
        var person = _host.People.FindOrCreatePerson(name);
        _host.People.Confirm(_host.People.FigureDetectionIds(key), person.Id);
        if (person.CoverDetectionId == null) _host.People.SetCover(person.Id, card.DetectionId);
        RemoveFigure(card);
        AfterChange();
    }

    public void IgnoreFigure(CropViewModel card)
    {
        if (card.FigureKey is not { } key) return;
        _host.People.Confirm(_host.People.FigureDetectionIds(key), _host.People.Strangers().Id);
        RemoveFigure(card);
        AfterChange();
    }

    private void RemoveFigure(CropViewModel card)
    {
        _figures.RemoveAll(f => f.GroupKey == card.FigureKey);
        foreach (var section in FigureSections.ToList())
        {
            section.Cards.Remove(card);
            if (section.Cards.Count == 0) FigureSections.Remove(section);
        }
        HasFigures = FigureSections.Count > 0 || _figures.Count > 0;
    }

    /// <summary>Strangers (people in the background): set aside for good, they do not come back as a group.</summary>
    public void IgnoreCluster(CropViewModel card)
    {
        if (card.Cluster is not { } cluster) return;
        _host.People.Confirm(_host.People.ClusterDetectionIds(cluster), _host.People.Strangers().Id);
        Clusters.Remove(card);
        HasClusters = Clusters.Count > 0;
        AfterChange();
    }

    // ---- review ----

    /// <summary>
    /// One question of the review: guesses for one person shown together and answered at once. Faces: the person's
    /// surest guesses. Figures: the ones taken for the person on one day out by the clothes (their day is what they
    /// are compared in). A unit is one picture of the question: a detection, or a figure's whole track in a video.
    /// </summary>
    private sealed record ReviewPage(long PersonId, DetectionKind Kind, long? EventId, List<List<DetectionRow>> Units)
    {
        public float Score => Units[0][0].SuggestScore ?? 0;
        public IEnumerable<long> Ids => Units.SelectMany(u => u).Select(d => d.Id);
    }

    /// <summary>How many pictures one question shows: two rows of them, the whole photo of one below.</summary>
    private const int PageSize = 24;

    private List<ReviewPage> _pages = [];
    private int _pageIndex;
    private ReviewPage? _page;
    private int _reviewVersion;
    /// <summary>Answered or skipped since the review was opened: not asked again until it is opened again.</summary>
    private readonly HashSet<long> _passed = [];
    private bool _skipped;
    private (int Faces, int Figures) _open;

    /// <summary>The review was opened: everything open is asked, the skipped ones too.</summary>
    public void LoadReview()
    {
        _passed.Clear();
        _skipped = false;
        _pages = [];
        _pageIndex = 0;
        _page = null;
        HasReview = false;
        ReviewCrops = [];
        ShowCurrent(null);
        HasReviewKnown = false;
        ReviewQuestion = "Looking for what to ask...";
        ReviewDetail = "";
        ReviewProgress = "";
        _ = ReadReviewAsync();
    }

    /// <summary>The matching changed the guesses: the questions after the one on screen are made again.</summary>
    public void RefreshReview() => _ = ReadReviewAsync();

    private async Task ReadReviewAsync()
    {
        var version = ++_reviewVersion;
        List<DetectionRow> rows;
        Dictionary<long, string> names;
        try
        {
            (rows, names) = await Task.Run(() => (_host.People.OpenSuggestions(), _host.People.ListPersons(true).ToDictionary(p => p.Id, p => p.Name)));
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // The library is busy or away: the next matching brings them.
            return;
        }
        if (version != _reviewVersion) return;
        _names = names;
        // The question on screen stays as it is (the user may be clicking in it); what was answered meanwhile is not asked again.
        var onScreen = _page?.Ids.ToHashSet() ?? [];
        var open = rows.Where(d => !_passed.Contains(d.Id) && !onScreen.Contains(d.Id)).ToList();
        var pages = Pages(open);
        _open = (open.Count(d => d.Kind == DetectionKind.Face) + (_page?.Kind == DetectionKind.Face ? onScreen.Count : 0),
            open.Count(d => d.Kind == DetectionKind.Body) + (_page?.Kind == DetectionKind.Body ? onScreen.Count : 0));
        if (_page != null)
        {
            _pages = [_page, .. pages];
            _pageIndex = 0;
            ShowProgress();
        }
        else
        {
            _pages = pages;
            _pageIndex = 0;
            ShowPage();
        }
    }

    /// <summary>
    /// The questions: the faces first, a person's surest guesses together and the surest question first; then the
    /// figures by person and day out, the biggest first (one answer settles the most).
    /// </summary>
    private static List<ReviewPage> Pages(List<DetectionRow> rows)
    {
        var units = rows.GroupBy(d => (d.SuggestPersonId, d.Kind, d.Track, Alone: d.Track == null ? d.Id : 0))
            .Select(g => g.OrderByDescending(d => d.Quality).ToList()).ToList();
        static float Score(List<DetectionRow> unit) => unit.Max(d => d.SuggestScore ?? 0);
        var faces = units.Where(u => u[0].Kind == DetectionKind.Face).GroupBy(u => u[0].SuggestPersonId!.Value)
            .SelectMany(g => g.OrderByDescending(Score).Chunk(PageSize).Select(c => new ReviewPage(g.Key, DetectionKind.Face, null, [.. c])))
            .OrderByDescending(p => p.Score);
        var figures = units.Where(u => u[0].Kind == DetectionKind.Body).GroupBy(u => (Person: u[0].SuggestPersonId!.Value, u[0].EventId))
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(Score))
            .SelectMany(g => g.OrderByDescending(Score).Chunk(PageSize).Select(c => new ReviewPage(g.Key.Person, DetectionKind.Body, g.Key.EventId, [.. c])));
        return [.. faces, .. figures];
    }

    private void ShowProgress() => ReviewProgress = !HasReview ? "" :
        $"{Format.Count(_pages.Count - _pageIndex, "question", "questions")} to go  \u00b7  {Format.Count(_open.Faces, "face", "faces")}, {_open.Figures:N0} by the clothes";

    private void ShowPage()
    {
        _page = _pageIndex < _pages.Count ? _pages[_pageIndex] : null;
        HasReview = _page != null;
        if (_page is not { } page)
        {
            ReviewCrops = [];
            ShowCurrent(null);
            ReviewKnown = [];
            HasReviewKnown = false;
            ReviewQuestion = _skipped ? "Nothing more to review right now." : "Nothing to review right now.";
            ReviewProgress = "";
            ReviewDetail = _skipped
                ? "What you skipped is asked again when you open the review the next time."
                : "Guesses appear when the analysis is not sure enough to decide by itself.";
            return;
        }
        var name = _names.GetValueOrDefault(page.PersonId, "?");
        _pageItems = _host.Repo.Query(new Library.MediaFilter { Ids = page.Units.Select(u => u[0].MediaId).Distinct().ToList() }).ToDictionary(i => i.Id);
        _undated = _host.Repo.Undated(_pageItems.Keys);
        _units = [];
        var figures = page.Kind == DetectionKind.Body;
        ReviewCrops = new ObservableCollection<CropViewModel>(page.Units.Select(unit =>
        {
            var d = unit[0];
            var crop = new CropViewModel(d.Id, d.MediaId, d.Kind)
            {
                TimeMs = d.TimeMs,
                DetectionIds = unit.Select(u => u.Id).ToList(),
            };
            _units[crop] = unit;
            crop.LoadLater(() => CropPath(d.Id), 160);
            return crop;
        }));
        const string how = "Point at a picture to see its whole photo below.";
        if (figures)
        {
            var day = DateTime.UnixEpoch.AddMilliseconds(page.Units.Max(u => u[0].TakenAt)).ToString("dddd d. MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
            ReviewQuestion = $"Is this {name}, on {day}?";
            ReviewDetail = $"People without a clear face, taken for {name} by the clothes of that day. Click the ones that are somebody else, then answer for the rest. {how}";
        }
        else
        {
            ReviewQuestion = $"Is this {name}?";
            ReviewDetail = $"Click the pictures that are somebody else, then answer for the rest. {how}";
        }
        ShowCurrent(ReviewCrops.FirstOrDefault());
        ShowKnown(page, name);
        ShowYesText();
        ShowProgress();
    }

    /// <summary>The person as known already, beside the question: a few named faces, or what the person wore that day.</summary>
    private void ShowKnown(ReviewPage page, string name)
    {
        var known = page.Kind == DetectionKind.Body && page.EventId is { } eventId
            ? _host.People.KnownFiguresOf([eventId], page.Ids.ToList())
                .Where(d => d.PersonId == page.PersonId && d.AssignSource != AssignSource.Outfit && d.Quality >= 0.25f)
                // Known by the face first: those show the clothes from the front, with the face to be sure by.
                .OrderByDescending(d => d.AssignSource == AssignSource.Linked).ThenByDescending(d => d.Quality).DistinctBy(d => d.MediaId).Take(5).ToList()
            : _host.People.Detections(personId: page.PersonId, kind: DetectionKind.Face, limit: 5);
        ReviewKnown = new ObservableCollection<CropViewModel>(known.Select(d =>
        {
            var crop = new CropViewModel(d.Id, d.MediaId, d.Kind) { TimeMs = d.TimeMs };
            crop.Load(CropPath(d.Id), 120);
            return crop;
        }).Where(c => c.Image != null));
        ReviewKnownText = page.Kind == DetectionKind.Body ? $"{name} that day:" : $"{name}, as named:";
        HasReviewKnown = ReviewKnown.Count > 0;
    }

    private void ShowYesText()
    {
        var all = ReviewCrops.Count;
        var yes = ReviewCrops.Count(c => !c.IsNot);
        ReviewYesText = all == 1 && yes == 1 ? "Yes (Y)" : yes == all ? $"Yes, all {all} (Y)" : yes == 0 ? "None of them (Y)" : $"Yes, the other {yes} (Y)";
        ReviewCrossAllText = yes == 0 && all > 0 ? "Uncross all (A)" : "Cross out all (A)";
    }

    /// <summary>
    /// When only a few of the pictures are the person (the clothes of a day guess badly in a crowd): all crossed
    /// out at once, the few are then clicked back. With all crossed out already: none is.
    /// </summary>
    [RelayCommand]
    private void ReviewCrossAll()
    {
        var cross = ReviewCrops.Any(c => !c.IsNot);
        foreach (var c in ReviewCrops) c.IsNot = cross;
        ShowYesText();
    }

    /// <summary>A picture of the question was clicked: it is not the person (or, clicked again, it is).</summary>
    public void ReviewToggle(CropViewModel crop)
    {
        crop.IsNot = !crop.IsNot;
        ShowYesText();
        ShowCurrent(crop);
    }

    /// <summary>The picture shown below changes: pointed at with the mouse.</summary>
    public void ReviewPoint(CropViewModel crop) => ShowCurrent(crop);

    /// <summary>The arrows move the picture shown below (step: -1, 1; a row up or down: the row's length).</summary>
    public void ReviewMove(int step)
    {
        if (ReviewCrops.Count == 0) return;
        var at = ReviewCurrent == null ? -1 : ReviewCrops.IndexOf(ReviewCurrent);
        ShowCurrent(ReviewCrops[Math.Clamp(at + step, 0, ReviewCrops.Count - 1)]);
    }

    /// <summary>The space bar: the picture shown below is not the person (or, again, it is).</summary>
    public void ReviewToggleCurrent()
    {
        if (ReviewCurrent is { } crop) ReviewToggle(crop);
    }

    private Dictionary<long, Library.MediaItem> _pageItems = [];
    private HashSet<long> _undated = [];
    private Dictionary<CropViewModel, List<DetectionRow>> _units = [];
    private CancellationTokenSource? _photoCts;
    /// <summary>The photo itself comes when the pointer stays on a picture this long (it may have to be downloaded).</summary>
    private static readonly TimeSpan PhotoDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The whole photo of a picture of the question, with the person's box: what the crop alone does not tell (who
    /// stands around, where, when). The thumbnail shows at once, the photo itself replaces it when the pointer stays.
    /// </summary>
    private void ShowCurrent(CropViewModel? crop)
    {
        if (crop == ReviewCurrent && crop != null) return;
        if (ReviewCurrent != null) ReviewCurrent.IsCurrent = false;
        ReviewCurrent = crop;
        _photoCts?.Cancel();
        _photoCts = null;
        if (crop == null || !_units.TryGetValue(crop, out var unit) || _pageItems.GetValueOrDefault(crop.MediaId) is not { } item)
        {
            ReviewPhoto = null;
            HasBox = false;
            PhotoWhen = PhotoWhere = PhotoLikeness = "";
            return;
        }
        crop.IsCurrent = true;
        var d = unit[0];
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        // Dated by its folder or file (no date of its own): the day, not a made-up hour.
        var dated = !_undated.Contains(item.Id);
        PhotoWhen = item.Taken.ToString(dated ? "dddd d. MMMM yyyy, H:mm" : "dddd d. MMMM yyyy", culture) +
                    (item.IsVideo ? $", at {TimeSpan.FromMilliseconds(d.TimeMs):m\\:ss} of the video" : "");
        var folder = Path.GetDirectoryName(item.RelPath.Replace('\\', '/'))?.Replace('\\', '/');
        PhotoWhere = string.IsNullOrEmpty(folder) ? item.FileName : $"{item.FileName}  \u00b7  {folder}";
        PhotoLikeness = $"Likeness {unit.Max(u => u.SuggestScore ?? 0):0.00}";
        PhotoRotation = item.Rotation;
        ReviewPhoto = null;
        HasBox = false;
        var cts = _photoCts = new CancellationTokenSource();
        _ = LoadPhotoAsync(item, d, cts.Token);
    }

    private async Task LoadPhotoAsync(Library.MediaItem item, DetectionRow d, CancellationToken ct)
    {
        try
        {
            // A 360 video: its thumbnail is another view, the box would be somewhere else; the frame of the moment is shown without it.
            var box = !Media.Fisheye360.IsDualFisheye(item.RelPath);
            var thumb = item.IsVideo
                ? await _thumbnails.LoadAsync(item, d.TimeMs, ct)
                : _thumbnails.ExistingPath(item.Id) is { } path ? await Task.Run(() => DecodeFile(path), ct) : null;
            if (ct.IsCancellationRequested) return;
            if (thumb != null) ShowPhoto(thumb, d, box);
            if (item.IsVideo) return;
            await Task.Delay(PhotoDelay, ct);
            var (w, h) = PhotoSize;
            var full = await Task.Run(async () =>
                Media.ImageCodec.DecodeForDisplay(await Cloud.MediaFiles.LocalPathAsync(item, ct, waited: true), w, h), ct);
            if (!ct.IsCancellationRequested) ShowPhoto(ToBitmap(full), d, box);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or Cloud.RcloneException or ImageMagick.MagickException or InvalidOperationException)
        {
            // Not to be had now (offline, a broken file): the thumbnail stays, or the crop says enough.
        }
    }

    /// <summary>How big the photo is decoded: about the size it gets on the screen.</summary>
    public (int Width, int Height) PhotoSize { get; set; } = (1400, 900);

    private void ShowPhoto(Bitmap bitmap, DetectionRow d, bool box)
    {
        ReviewPhoto = bitmap;
        PhotoWidth = bitmap.Size.Width;
        PhotoHeight = bitmap.Size.Height;
        BoxLeft = d.X * PhotoWidth;
        BoxTop = d.Y * PhotoHeight;
        BoxWidth = d.W * PhotoWidth;
        BoxHeight = d.H * PhotoHeight;
        // The same line whatever size the photo is laid out in.
        BoxStroke = Math.Max(2, PhotoWidth / 300);
        HasBox = box;
    }

    private static Bitmap? DecodeFile(string path)
    {
        using var s = File.OpenRead(path);
        return new Bitmap(s);
    }

    private static Bitmap ToBitmap(Media.DecodedImage d)
    {
        var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(d.Width, d.Height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        var rowBytes = d.Width * 4;
        for (var y = 0; y < d.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(d.Bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        return bitmap;
    }

    /// <summary>The photo of the picture shown below, to open in the viewer (with the boxes).</summary>
    public (long MediaId, int TimeMs)? ReviewCurrentMoment => ReviewCurrent is { } c ? (c.MediaId, c.TimeMs) : null;

    /// <summary>Not the person: a figure of a video on its whole track, and never guessed for it again.</summary>
    private void RejectUnits(IEnumerable<CropViewModel> crops, long personId)
    {
        var ids = crops.SelectMany(c => c.DetectionIds.Concat(_host.People.SamePersonDetectionIds(c.DetectionId))).Distinct().ToList();
        if (ids.Count > 0) _host.People.Reject(ids, personId);
    }

    /// <summary>Yes for the pictures left as they are; the clicked ones are not the person.</summary>
    [RelayCommand]
    private void ReviewYes()
    {
        if (_page is not { } page) return;
        var yes = ReviewCrops.Where(c => !c.IsNot).SelectMany(c => c.DetectionIds).ToList();
        if (yes.Count > 0) _host.People.Confirm(yes, page.PersonId);
        RejectUnits(ReviewCrops.Where(c => c.IsNot), page.PersonId);
        Next(answered: true);
    }

    [RelayCommand]
    private void ReviewNo()
    {
        if (_page is not { } page) return;
        RejectUnits(ReviewCrops, page.PersonId);
        Next(answered: true);
    }

    [RelayCommand]
    private void ReviewSkip()
    {
        if (_page == null) return;
        _skipped = true;
        Next(answered: false);
    }

    /// <summary>One picture of the question is somebody else, said by name: it leaves the question.</summary>
    public void ReviewOther(CropViewModel crop, string name)
    {
        if (_page is not { } page || string.IsNullOrWhiteSpace(name) || !ReviewCrops.Contains(crop)) return;
        var person = _host.People.FindOrCreatePerson(name);
        if (person.Id != page.PersonId) RejectUnits([crop], page.PersonId);
        _host.People.Confirm(crop.DetectionIds, person.Id);
        _passed.UnionWith(crop.DetectionIds);
        var at = ReviewCrops.IndexOf(crop);
        ReviewCrops.Remove(crop);
        if (ReviewCurrent == crop) ShowCurrent(ReviewCrops.Count == 0 ? null : ReviewCrops[Math.Min(at, ReviewCrops.Count - 1)]);
        if (ReviewCrops.Count == 0)
        {
            Next(answered: true);
            return;
        }
        ShowYesText();
        AfterChange();
    }

    private void Next(bool answered)
    {
        if (_page is { } page)
        {
            _passed.UnionWith(page.Ids);
            var asked = page.Ids.Count();
            _open = page.Kind == DetectionKind.Face ? (Math.Max(0, _open.Faces - asked), _open.Figures) : (_open.Faces, Math.Max(0, _open.Figures - asked));
        }
        _pageIndex++;
        ShowPage();
        // The matching runs again in the background and brings the questions that are left (RefreshReview).
        if (answered) AfterChange();
    }

    // ---- one person ----

    public void LoadPerson(long personId)
    {
        Person = _host.People.ListPersons(true).FirstOrDefault(p => p.Id == personId);
        var crops = new List<CropViewModel>();
        PersonCrops = [];
        if (Person == null) return;
        var rows = _host.People.Detections(personId: personId, limit: 400);
        foreach (var d in rows)
        {
            var crop = new CropViewModel(d.Id, d.MediaId, d.Kind)
            {
                Caption = d.Confirmed ? "confirmed" : d.AssignSource switch
                {
                    AssignSource.SameDay => $"like a named face of that day {d.Confidence:0.00}",
                    AssignSource.Face => $"face {d.Confidence:0.00}",
                    AssignSource.Linked => "body of the face",
                    AssignSource.Track => "same video track",
                    AssignSource.Outfit => $"clothes {d.Confidence:0.00}",
                    _ => "",
                },
            };
            crop.LoadLater(() => CropPath(d.Id), 120);
            crops.Add(crop);
        }
        PersonCrops = new ObservableCollection<CropViewModel>(crops);
        PersonSummary = $"{Person.Name}: {rows.Count(r => r.Kind == DetectionKind.Face)} faces, {rows.Count(r => r.Kind == DetectionKind.Body)} figures. " +
                        "Select the wrong ones: It is someone else says who they are, Not this person (Delete) only takes them away.";
    }

    [RelayCommand]
    private void RejectSelected()
    {
        if (Person == null) return;
        var selected = PersonCrops.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        _host.People.Reject(selected.Select(c => c.DetectionId), Person.Id);
        foreach (var c in selected) PersonCrops.Remove(c);
        AfterChange();
    }

    public bool AnyCropSelected => PersonCrops.Any(c => c.IsSelected);

    /// <summary>The selected pictures are somebody else: they go to that person (on the whole track of a video).</summary>
    public void MoveSelectedTo(string name)
    {
        if (Person == null || string.IsNullOrWhiteSpace(name)) return;
        var selected = PersonCrops.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var c in selected) NameDetection(c.DetectionId, name);
        LoadPerson(Person.Id);
    }

    [RelayCommand]
    private void ConfirmSelected()
    {
        if (Person == null) return;
        var selected = PersonCrops.Where(c => c.IsSelected).ToList();
        _host.People.Confirm(selected.Select(c => c.DetectionId), Person.Id);
        foreach (var c in selected)
        {
            c.IsSelected = false;
        }
        AfterChange();
        LoadPerson(Person.Id);
    }

    [RelayCommand]
    private void SetCover()
    {
        if (Person == null || PersonCrops.FirstOrDefault(c => c.IsSelected) is not { } crop) return;
        _host.People.SetCover(Person.Id, crop.DetectionId);
        _changed();
    }

    public void RenamePerson(long personId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        // An existing name: the two are one person.
        if (_host.People.FindPerson(name) is { } other && other.Id != personId)
            _host.People.MergePersons(personId, other.Id);
        else
            _host.People.RenamePerson(personId, name);
        AfterChange();
    }

    public void DeletePerson(long personId)
    {
        _host.People.DeletePerson(personId);
        AfterChange();
    }

    /// <summary>From the viewer: the user says who a box is (the seed for people in helmets). Returns the person.</summary>
    /// <param name="dayNow">
    /// Said in the viewer, where the next photo of the series is one key away: the photos of that day get their
    /// names at once, not when the whole library is matched again (many seconds in a big one).
    /// </param>
    public long? NameDetection(long detectionId, string name, bool dayNow = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var person = _host.People.FindOrCreatePerson(name);
        // A correction: the person said before is not this one, on the whole track.
        var same = _host.People.SamePersonDetectionIds(detectionId);
        foreach (var other in _host.People.ConfirmedPersonsOf(same).Where(p => p != person.Id))
            _host.People.Reject(same, other);
        _host.People.Confirm([detectionId], person.Id);
        AfterChange();
        if (dayNow) _ = Task.Run(() => MatchDay(detectionId));
        return person.Id;
    }

    /// <summary>The day of an answer, off the UI thread (a long day out takes a second); the viewer shows its names when it is done.</summary>
    private void MatchDay(long detectionId)
    {
        try
        {
            if (_host.Identity.MatchDayOf(detectionId)) Avalonia.Threading.Dispatcher.UIThread.Post(() => Rematched?.Invoke());
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // The library is busy or away: the matching of the whole library, asked for by the same answer, names them.
        }
    }

    /// <summary>"Not Greta" on a box: the name goes from the whole track and is never suggested for it again.</summary>
    public void RejectDetection(long detectionId, long personId)
    {
        _host.People.Reject(_host.People.SamePersonDetectionIds(detectionId), personId);
        AfterChange();
    }

    /// <summary>Not a person (a hand, a snow cannon) or a stranger: no box, never offered again.</summary>
    public void IgnoreDetection(long detectionId)
    {
        _host.People.Confirm(_host.People.SamePersonDetectionIds(detectionId), _host.People.Strangers().Id);
        AfterChange();
    }

    public List<DetectionRow> DetectionsOf(long mediaId) => _host.People.DetectionsOfMedia(mediaId);

    /// <summary>The people of this item come from the current analysis (older boxes of a 360 video do not fit its leveled picture).</summary>
    public bool PeopleUpToDate(long mediaId) => _host.People.PeopleVersionOf(mediaId) >= MomentFinder.Library.Indexer.PeopleVersion;

    /// <summary>Strangers and things that are not people: no box for them.</summary>
    public HashSet<long> HiddenPersonIds() => _host.People.ListPersons(true).Where(p => p.Hidden).Select(p => p.Id).ToHashSet();

    public Dictionary<long, List<TrackBox>> TrackPaths(long mediaId) =>
        _host.People.TrackPaths(mediaId).GroupBy(b => b.Track).ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>The named people of one photo or video.</summary>
    public HashSet<long> PersonsIn(long mediaId) => _host.People.PersonsIn([mediaId]).ToHashSet();

    public Dictionary<long, List<(int StartMs, int EndMs)>> PersonSegments(IReadOnlyList<long> personIds) =>
        _host.People.PersonSegments(personIds);

    public string? NameOf(long? personId) =>
        personId is { } id ? _host.People.ListPersons(true).FirstOrDefault(p => p.Id == id)?.Name : null;

    public Dictionary<long, string> Names() => _host.People.ListPersons(true).ToDictionary(p => p.Id, p => p.Name);

    public long SuggestionCount() => _host.People.SuggestionCount();
}
