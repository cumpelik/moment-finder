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

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private bool _isSelected;

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
    private string? _path;
    private int _width;
    private int _version;

    /// <summary>Where the picture is; it is decoded when the card comes on screen (Show).</summary>
    public void LoadLater(string path, int width)
    {
        _path = path;
        _width = width;
    }

    public void Show()
    {
        if (Image != null || _path is not { } path) return;
        var version = ++_version;
        var width = _width;
        _ = Task.Run(() => Decode(path, width)).ContinueWith(t =>
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
/// The people screens: unnamed groups of faces to name, the review queue ("Is this Tomáš?"), and the
/// faces of one person to correct. Every answer goes to the database and the matching runs again in the background.
/// </summary>
public sealed partial class PeopleViewModel : ObservableObject
{
    private readonly LibraryHost _host;
    private readonly Action _changed;
    private List<DetectionRow> _queue = [];
    private int _queueIndex;
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
                crop.LoadLater(CropPath(f.CoverId), 160);
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
    [ObservableProperty] private CropViewModel? _reviewCrop;
    [ObservableProperty] private Bitmap? _reviewContext;
    [ObservableProperty] private string _reviewQuestion = "";
    [ObservableProperty] private string _reviewProgress = "";
    [ObservableProperty] private bool _hasReview;
    [ObservableProperty] private string _reviewDetail = "";
    /// <summary>The context image size and the reviewed detection's box in its pixels.</summary>
    [ObservableProperty] private double _contextWidth;
    [ObservableProperty] private double _contextHeight;
    [ObservableProperty] private double _boxLeft;
    [ObservableProperty] private double _boxTop;
    [ObservableProperty] private double _boxWidth;
    [ObservableProperty] private double _boxHeight;
    [ObservableProperty] private Person? _person;
    [ObservableProperty] private string _personSummary = "";

    public PeopleViewModel(LibraryHost host, Action changed)
    {
        _host = host;
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

    /// <summary>Who else the question in review may be, when it is not who the analysis thought.</summary>
    public Task<WhoQuestion> AskReviewAsync() => HasReview
        ? AskAsync([_queue[_queueIndex].Id], _queue[_queueIndex].Id, _queue[_queueIndex].SuggestPersonId)
        : Task.FromResult(new WhoQuestion(PersonNames(), 0, null));

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

    /// <summary>The photo (or the video moment) of the question in review.</summary>
    public (long MediaId, int TimeMs)? ReviewMoment => HasReview ? (_queue[_queueIndex].MediaId, _queue[_queueIndex].TimeMs) : null;

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
            crop.LoadLater(CropPath(c.CoverId), 160);
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

    public void LoadReview()
    {
        _names = _host.People.ListPersons(true).ToDictionary(p => p.Id, p => p.Name);
        _queue = _host.People.Suggestions(200);
        _queueIndex = 0;
        ShowReview();
    }

    private void ShowReview()
    {
        // Skip what got resolved meanwhile (another answer confirmed the whole track, for example).
        while (_queueIndex < _queue.Count && _queue[_queueIndex].SuggestPersonId == null) _queueIndex++;
        HasReview = _queueIndex < _queue.Count;
        if (!HasReview)
        {
            ReviewCrop = null;
            ReviewContext = null;
            ReviewQuestion = "Nothing to review right now.";
            ReviewProgress = "";
            ReviewDetail = "Suggestions appear when the analysis is not sure enough to decide by itself.";
            return;
        }
        var d = _queue[_queueIndex];
        var crop = new CropViewModel(d.Id, d.MediaId, d.Kind);
        crop.Load(CropPath(d.Id), d.Kind == DetectionKind.Face ? 240 : 160);
        ReviewCrop = crop;
        var name = _names.GetValueOrDefault(d.SuggestPersonId!.Value, "?");
        ReviewQuestion = $"Is this {name}?";
        ReviewProgress = $"{_queueIndex + 1} / {_queue.Count}";
        var item = _host.Repo.Get(d.MediaId);
        ReviewDetail = item == null ? "" :
            $"{item.FileName}, {item.Taken:d. M. yyyy}" + (d.Kind == DetectionKind.Body ? ", recognized by clothes" : "") +
            $", similarity {d.SuggestScore:0.00}";
        ReviewContext = null;
        var thumb = _host.Paths.ThumbPath(d.MediaId);
        if (File.Exists(thumb) && d.TimeMs == 0)
        {
            using var s = File.OpenRead(thumb);
            var bitmap = Bitmap.DecodeToWidth(s, 480);
            ContextWidth = bitmap.Size.Width;
            ContextHeight = bitmap.Size.Height;
            BoxLeft = d.X * ContextWidth;
            BoxTop = d.Y * ContextHeight;
            BoxWidth = d.W * ContextWidth;
            BoxHeight = d.H * ContextHeight;
            ReviewContext = bitmap;
        }
    }

    [RelayCommand]
    private void ReviewYes()
    {
        if (!HasReview) return;
        var d = _queue[_queueIndex];
        var ids = WithAlike(d);
        _host.People.Confirm(ids, d.SuggestPersonId!.Value);
        Next(ids.Count - 1);
    }

    [RelayCommand]
    private void ReviewNo()
    {
        if (!HasReview) return;
        var d = _queue[_queueIndex];
        var ids = WithAlike(d);
        _host.People.Reject(ids, d.SuggestPersonId!.Value);
        Next(ids.Count - 1);
    }

    /// <summary>Near the same picture: taken within a few minutes and looking almost the same (a burst, a copy).</summary>
    private static readonly TimeSpan AlikeWithin = TimeSpan.FromMinutes(5);
    private const float AlikeFace = 0.85f;
    private const float AlikeBody = 0.92f;

    /// <summary>
    /// The answered suggestion and the ones further in the queue that are almost the same picture (the same
    /// suggested person, taken within minutes, nearly the same look): one answer is enough for all of them.
    /// </summary>
    private List<long> WithAlike(DetectionRow answered)
    {
        var ids = new List<long> { answered.Id };
        var rest = _queue.Skip(_queueIndex + 1)
            .Where(q => q.SuggestPersonId == answered.SuggestPersonId && q.Kind == answered.Kind &&
                        Math.Abs(q.TakenAt - answered.TakenAt) <= AlikeWithin.TotalMilliseconds)
            .ToList();
        if (rest.Count == 0) return ids;
        var vectors = _host.People.Embeddings(rest.Select(r => r.Id).Append(answered.Id));
        if (!vectors.TryGetValue(answered.Id, out var look)) return ids;
        var limit = answered.Kind == DetectionKind.Face ? AlikeFace : AlikeBody;
        foreach (var q in rest)
        {
            if (!vectors.TryGetValue(q.Id, out var other) || PeopleRepository.Similarity(look, other) < limit) continue;
            ids.Add(q.Id);
            // Answered with this one: the queue skips it.
            q.SuggestPersonId = null;
        }
        return ids;
    }

    [RelayCommand]
    private void ReviewSkip()
    {
        if (!HasReview) return;
        _queueIndex++;
        ShowReview();
    }

    public void ReviewOther(string name)
    {
        if (!HasReview || string.IsNullOrWhiteSpace(name)) return;
        var d = _queue[_queueIndex];
        var person = _host.People.FindOrCreatePerson(name);
        var ids = WithAlike(d);
        if (person.Id != d.SuggestPersonId) _host.People.Reject(ids, d.SuggestPersonId!.Value);
        _host.People.Confirm(ids, person.Id);
        Next(ids.Count - 1);
    }

    /// <summary>Said under the question: the last answer counted for alike pictures too.</summary>
    [ObservableProperty] private string _reviewNote = "";

    private void Next(int alike = 0)
    {
        _queueIndex++;
        AfterChange();
        ShowReview();
        ReviewNote = alike switch
        {
            0 => "",
            1 => "The last answer counted for 1 almost the same picture too.",
            _ => $"The last answer counted for {alike} almost the same pictures too.",
        };
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
            crop.LoadLater(CropPath(d.Id), 120);
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
