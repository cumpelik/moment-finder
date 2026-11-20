using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.Analysis;
using MediaLens.People;

namespace MediaLens.App.ViewModels;

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
        if (Image != null || !File.Exists(path)) return;
        try
        {
            using var s = File.OpenRead(path);
            Image = Bitmap.DecodeToWidth(s, width);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
        }
    }
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

    public ObservableCollection<CropViewModel> Clusters { get; } = [];
    public ObservableCollection<CropViewModel> Figures { get; } = [];
    public ObservableCollection<CropViewModel> PersonCrops { get; } = [];

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
    }

    private string CropPath(long detectionId) => _host.Paths.PeopleCropPath(detectionId);

    private void AfterChange()
    {
        _host.RefreshNames();
        _host.Indexer.RequestIdentity();
        _changed();
    }

    public IReadOnlyList<string> PersonNames() => _host.People.ListPersons().Select(p => p.Name).ToList();

    // ---- unnamed groups ----

    public void LoadClusters()
    {
        Clusters.Clear();
        Figures.Clear();
        var groups = _host.People.UnnamedClusters();
        foreach (var c in groups)
        {
            var crop = new CropViewModel(c.CoverId, 0, DetectionKind.Face)
            {
                Cluster = c.Cluster,
                Count = c.Media,
                Caption = c.Media == 1 ? "1 photo" : $"{c.Media} photos",
            };
            crop.Load(CropPath(c.CoverId), 160);
            Clusters.Add(crop);
        }
        var items = new Dictionary<long, MediaLens.Library.MediaItem?>();
        foreach (var f in _host.People.UnnamedFigures(groups.Select(g => g.Cluster).ToHashSet()))
        {
            if (!items.TryGetValue(f.MediaId, out var item)) items[f.MediaId] = item = _host.Repo.Get(f.MediaId);
            var crop = new CropViewModel(f.CoverId, f.MediaId, DetectionKind.Body)
            {
                FigureKey = f.GroupKey,
                TimeMs = f.TimeMs,
                Caption = f.Media > 1 ? $"{f.Media} videos and photos" :
                    item == null ? "" : item.IsVideo ? $"{item.FileName} {TimeSpan.FromMilliseconds(f.TimeMs):m\\:ss}" : item.FileName,
            };
            crop.Load(CropPath(f.CoverId), 160);
            Figures.Add(crop);
        }
        HasClusters = Clusters.Count > 0;
        HasFigures = Figures.Count > 0;
        ClustersSummary = Clusters.Count + Figures.Count == 0
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
        Figures.Remove(card);
        HasFigures = Figures.Count > 0;
        AfterChange();
    }

    public void IgnoreFigure(CropViewModel card)
    {
        if (card.FigureKey is not { } key) return;
        _host.People.Confirm(_host.People.FigureDetectionIds(key), _host.People.Strangers().Id);
        Figures.Remove(card);
        HasFigures = Figures.Count > 0;
        AfterChange();
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
        _host.People.Confirm([d.Id], d.SuggestPersonId!.Value);
        Next();
    }

    [RelayCommand]
    private void ReviewNo()
    {
        if (!HasReview) return;
        var d = _queue[_queueIndex];
        _host.People.Reject([d.Id], d.SuggestPersonId!.Value);
        Next();
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
        if (person.Id != d.SuggestPersonId) _host.People.Reject([d.Id], d.SuggestPersonId!.Value);
        _host.People.Confirm([d.Id], person.Id);
        Next();
    }

    private void Next()
    {
        _queueIndex++;
        AfterChange();
        ShowReview();
    }

    // ---- one person ----

    public void LoadPerson(long personId)
    {
        Person = _host.People.ListPersons(true).FirstOrDefault(p => p.Id == personId);
        PersonCrops.Clear();
        if (Person == null) return;
        var rows = _host.People.Detections(personId: personId, limit: 400);
        foreach (var d in rows)
        {
            var crop = new CropViewModel(d.Id, d.MediaId, d.Kind)
            {
                Caption = d.Confirmed ? "confirmed" : d.AssignSource switch
                {
                    AssignSource.Face => $"face {d.Confidence:0.00}",
                    AssignSource.Linked => "body of the face",
                    AssignSource.Track => "same video track",
                    AssignSource.Outfit => $"clothes {d.Confidence:0.00}",
                    _ => "",
                },
            };
            crop.Load(CropPath(d.Id), 120);
            PersonCrops.Add(crop);
        }
        PersonSummary = $"{Person.Name}: {rows.Count(r => r.Kind == DetectionKind.Face)} faces, {rows.Count(r => r.Kind == DetectionKind.Body)} figures. " +
                        "Select the wrong ones and press Not this person (Delete).";
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

    /// <summary>From the viewer: the user says who a box is (the seed for people in helmets).</summary>
    public void NameDetection(long detectionId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var person = _host.People.FindOrCreatePerson(name);
        _host.People.Confirm([detectionId], person.Id);
        AfterChange();
    }

    public List<DetectionRow> DetectionsOf(long mediaId) => _host.People.DetectionsOfMedia(mediaId);

    public Dictionary<long, List<TrackBox>> TrackPaths(long mediaId) =>
        _host.People.TrackPaths(mediaId).GroupBy(b => b.Track).ToDictionary(g => g.Key, g => g.ToList());

    public Dictionary<long, List<(int StartMs, int EndMs)>> PersonSegments(IReadOnlyList<long> personIds) =>
        _host.People.PersonSegments(personIds);

    public string? NameOf(long? personId) =>
        personId is { } id ? _host.People.ListPersons(true).FirstOrDefault(p => p.Id == id)?.Name : null;

    public Dictionary<long, string> Names() => _host.People.ListPersons(true).ToDictionary(p => p.Id, p => p.Name);

    public long SuggestionCount() => _host.People.SuggestionCount();
}
