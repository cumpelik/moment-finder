using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.App.Services;
using MomentFinder.App.Video;
using MomentFinder.Library;
using MomentFinder.Media;

namespace MomentFinder.App.ViewModels;

/// <summary>An item to show; for a video found by a description, the part that matches.</summary>
public sealed record PlaylistEntry(MediaItem Item, int StartMs, IReadOnlyList<(int StartMs, int EndMs)>? MatchMoments = null);

/// <summary>The viewer's items, each made when it is reached: the panel over a whole library is millions of them.</summary>
public sealed class Playlist(IReadOnlyList<TileViewModel> tiles, Func<TileViewModel, PlaylistEntry> make) : IReadOnlyList<PlaylistEntry>
{
    private readonly PlaylistEntry?[] _made = new PlaylistEntry?[tiles.Count];

    /// <summary>The tiles it was made from: the same list means the same items.</summary>
    public IReadOnlyList<TileViewModel> Tiles => tiles;
    public int Count => _made.Length;
    public PlaylistEntry this[int index] => _made[index] ??= make(tiles[index]);

    public IEnumerator<PlaylistEntry> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Rate and Loop: what the previous viewer had, so an analysis session keeps them.</summary>
public sealed record PlayerSettings(int SlideSeconds, int Volume, double Rate = 1, bool Loop = false, bool ShowPeople = false);

public enum MomentKind { Whole, Match, Person }

/// <summary>What the videos play: whole, the part matching the search, or only the moments of some people.</summary>
public sealed record MomentChoice(string Label, MomentKind Kind, IReadOnlyList<long> PersonIds)
{
    public override string ToString() => Label;
}

/// <summary>
/// The full-screen viewer: browsing (arrows) or a slideshow (photos for N seconds, videos to the end or only
/// the matching part). Space pauses / continues, Esc closes.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly Playlist _entries;
    private readonly PlayerSettings _settings;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _slideTimer;
    private readonly DispatcherTimer _positionTimer;
    private readonly Dictionary<long, Task<DecodedImage?>> _prefetch = new();
    private VideoPlayer? _video;
    private int _loadVersion;
    private bool _seeking;
    private int _moment;
    private bool _insideMoment;
    private readonly Dictionary<string, Dictionary<long, List<(int StartMs, int EndMs)>>> _personMoments = new();

    /// <summary>Every choice of the playlist; the current video offers those that apply to it.</summary>
    private readonly List<MomentChoice> _allChoices;
    /// <summary>
    /// What this video can play: whole, the matching part, and only the people who are in it (not everybody of
    /// the library). The view shows them only when there is more than "whole".
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<MomentChoice> MomentChoices { get; } = [];
    /// <summary>The choice is about videos: not shown with a photo.</summary>
    public bool ShowMomentChoices => MomentChoices.Count > 1 && IsVideo;

    /// <summary>The list changes in place and the chosen one always stays in it, so the selection never jumps.</summary>
    private void SyncChoices()
    {
        var id = Current.Item.Id;
        var here = _main.People.PersonsIn(id);
        foreach (var person in here)
            if (!_allChoices.Any(c => c.Kind == MomentKind.Person && c.PersonIds.Count == 1 && c.PersonIds[0] == person)
                && _main.People.NameOf(person) is { } name)
                _allChoices.Add(new MomentChoice($"Only {name}", MomentKind.Person, [person]));
        var wanted = _allChoices.Where(c => c.Kind != MomentKind.Person || ReferenceEquals(c, MomentChoice)
            || (c.PersonIds.Any(here.Contains) && PersonMoments(c.PersonIds).ContainsKey(id))).ToList();
        for (var i = MomentChoices.Count - 1; i >= 0; i--)
            if (!wanted.Contains(MomentChoices[i])) MomentChoices.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= MomentChoices.Count || !ReferenceEquals(MomentChoices[i], wanted[i])) MomentChoices.Insert(i, wanted[i]);
        OnPropertyChanged(nameof(ShowMomentChoices));
    }
    /// <summary>A video plays or the slideshow runs: the button shows pause, otherwise play.</summary>
    [ObservableProperty] private bool _isRunning;
    /// <summary>A message in the middle of the picture, e.g. that the person's moments are over.</summary>
    [ObservableProperty] private string _notice = "";
    /// <summary>The photo is being decoded or the video has not shown its first frame: the view says so.</summary>
    [ObservableProperty] private bool _isLoading;
    /// <summary>The current video shows its own picture (not the previous video's last frame while it starts).</summary>
    [ObservableProperty] private bool _videoReady;
    /// <summary>The first video waits on its first frame (opened from a person's card).</summary>
    public bool PauseOnOpen { get; set; }
    private readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loadPending;

    /// <summary>What the slow load is doing: loading, or downloading a file that lives in the cloud.</summary>
    [ObservableProperty] private string _loadingText = "Loading...";

    /// <summary>A quick load shows nothing, a slow one "Loading..." (no flicker on every click).</summary>
    private void BeginLoading()
    {
        LoadingText = "Loading...";
        _loadPending = true;
        IsLoading = false;
        _loadingTimer.Stop();
        _loadingTimer.Start();
    }

    private void EndLoading()
    {
        _loadPending = false;
        _loadingTimer.Stop();
        IsLoading = false;
    }
    private MomentChoice _momentChoice;

    /// <summary>What the videos play now. A list control may report "nothing chosen" while its items change: ignored.</summary>
    public MomentChoice MomentChoice
    {
        get => _momentChoice;
        set
        {
            if (value == null || ReferenceEquals(value, _momentChoice)) return;
            _momentChoice = value;
            OnPropertyChanged();
            OnMomentChoiceChanged(value);
        }
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMuted))] private int _volume;
    public bool IsMuted => Volume == 0;

    public PixelSize ScreenSize { get; set; } = new(1920, 1080);

    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _autoPlay;
    [ObservableProperty] private string _peopleHint = "";
    /// <summary>"Greta: 2 / 3" while only the moments of a video play.</summary>
    [ObservableProperty] private string _momentText = "";
    [ObservableProperty] private Bitmap? _photo;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowMomentChoices))] private bool _isVideo;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private double _videoPosition;
    [ObservableProperty] private double _videoLength = 1;
    [ObservableProperty] private string _videoTimeText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _inBucket;
    [ObservableProperty] private double _contentWidth = 1;
    [ObservableProperty] private double _contentHeight = 1;
    /// <summary>
    /// The picture's own size (its file, a video frame, the 360 view). ContentWidth and ContentHeight are what is
    /// shown: the same, or turned when the user turned the item (SetPictureSize).
    /// </summary>
    [ObservableProperty] private double _pictureWidth = 1;
    [ObservableProperty] private double _pictureHeight = 1;
    /// <summary>How the user turned the item on screen, in degrees clockwise (MediaItem.Rotation); 0 for a 360 one.</summary>
    [ObservableProperty] private int _rotation;

    /// <summary>The size of the picture as it comes; what is shown is that, turned as the user turned the item.</summary>
    public void SetPictureSize(double width, double height)
    {
        PictureWidth = width;
        PictureHeight = height;
        var sideways = Media.QuarterTurn.Sideways(Rotation);
        ContentWidth = sideways ? height : width;
        ContentHeight = sideways ? width : height;
    }

    partial void OnRotationChanged(int value)
    {
        SetPictureSize(PictureWidth, PictureHeight);
        PeopleChanged?.Invoke();
    }

    /// <summary>
    /// Turns the photo or video on screen by a quarter (it lies on its side): at once here and in its tile, kept,
    /// and its people are looked for again on the turned picture. Not a 360 one (that is looked around in).
    /// </summary>
    public void Rotate(int by)
    {
        if (Is360) return;
        _main.Rotate(Current.Item, Current.Item.Rotation + by);
        if (ShowPeople) PeopleHint = "Looking for the people again on the turned picture...";
    }

    [RelayCommand]
    private void RotateRight() => Rotate(90);

    [RelayCommand]
    private void RotateLeft() => Rotate(-90);

    /// <summary>Turned here or in the other viewer (the same file elsewhere in the library is turned with it).</summary>
    private void OnItemTurned(IReadOnlyCollection<long> ids, int rotation)
    {
        if (!ids.Contains(Current.Item.Id) || Is360) return;
        Current.Item.Rotation = rotation;
        Rotation = rotation;
    }

    /// <summary>The people of the item on screen were looked for again: the boxes of before are gone.</summary>
    private void OnPeopleFound(long mediaId)
    {
        if (ShowPeople && mediaId == Current.Item.Id) LoadPeople();
    }

    /// <summary>Who is who changed somewhere (an analysis finished, not an answer given here: that one says more).</summary>
    private void OnRematched()
    {
        if (ShowPeople && _lookingFor == null) LoadPeople();
    }
    [ObservableProperty] private bool _showPeople;

    /// <summary>The people layer must be redrawn (other item, other moment of the video, a new name).</summary>
    public event Action? PeopleChanged;
    private List<People.DetectionRow> _detections = [];
    private Dictionary<long, List<People.TrackBox>> _paths = [];
    private Dictionary<long, string> _names = [];
    private HashSet<long> _hidden = [];
    private long _peopleTimeBucket = -1;

    public event Action? CloseRequested;
    /// <summary>For the view: the video image must be invalidated / replaced.</summary>
    public event Action? VideoFrameChanged;

    public PlayerViewModel(Playlist entries, int index, bool autoPlay, PlayerSettings settings, MainViewModel main,
        IReadOnlyList<MomentChoice> momentChoices, MomentChoice momentChoice)
    {
        _entries = entries;
        _settings = settings;
        _main = main;
        _index = index;
        _autoPlay = autoPlay;
        _volume = settings.Volume;
        _rate = settings.Rate;
        _loop = settings.Loop;
        _showPeople = settings.ShowPeople;
        _main.People.Answered += OnAnswered;
        _main.People.PeopleFound += OnPeopleFound;
        _main.People.Rematched += OnRematched;
        _main.ItemTurned += OnItemTurned;
        _allChoices = momentChoices.ToList();
        _momentChoice = momentChoice;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, settings.SlideSeconds)) };
        _slideTimer.Tick += (_, _) =>
        {
            _slideTimer.Stop();
            Next();
        };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            if (_loadPending) IsLoading = true;
        };
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdateVideoPosition();
        SyncChoices();
    }

    public WriteableBitmap? VideoFrame => Is360 ? _view360?.Output : _video?.Frame;

    public int Count => _entries.Count;
    public PlaylistEntry Current => _entries[Index];

    public void Start() => Show();

    private void Show()
    {
        var version = ++_loadVersion;
        _slideTimer.Stop();
        var entry = Current;
        var item = entry.Item;
        Error = null;
        BeginLoading();
        // The previous video's last frame must not stand under the new video's boxes.
        VideoReady = false;
        Is360 = Media.Fisheye360.IsDualFisheye(item.FullPath);
        Rotation = Is360 ? 0 : item.Rotation;
        Preparing360 = false;
        if (Is360) View.Model = Media.Fisheye360.ModelOf(item.FullPath);
        Title = item.FileName;
        Detail = $"{item.Taken:d. M. yyyy H:mm}" + (item.Camera is { } c ? $"  ·  {c}" : "");
        PositionText = PositionLabel();
        InBucket = _main.IsInActiveBucket(item.Id);

        _moment = 0;
        _insideMoment = false;
        Notice = "";
        // A chosen part belongs to its video.
        SetLoopRange(null, null);
        if (item.IsVideo)
        {
            SyncChoices();
            Photo = null;
            IsVideo = true;
            MomentText = MomentLabel();
            // libVLC still loading (the first video after the start): wait for it without blocking the window.
            if (!VideoPlayer.IsLoaded)
            {
                _ = ShowWhenVideoLoadedAsync(version);
                return;
            }
            if (!VideoPlayer.IsAvailable(out var error))
            {
                Error = error;
                EndLoading();
                if (AutoPlay) _slideTimer.Start();
                return;
            }
            // A 360 video plays both lenses (two players) into the look-around view; a flat one into a bitmap.
            if (_video != null && _video.RawFrames != Is360)
            {
                _video.Dispose();
                _video = null;
            }
            if (_video == null)
            {
                _video = Is360 ? New360Player() : new VideoPlayer { Volume = Volume, Rate = (float)Rate };
                _video.FrameUpdated += () => VideoFrameChanged?.Invoke();
                _video.Ended += OnVideoEnded;
                _video.Started += () =>
                {
                    VideoReady = true;
                    EndLoading();
                    PeopleChanged?.Invoke();
                    OnVideoBuffering();
                };
                _video.Failed += OnVideoFailed;
                _video.BufferingChanged += OnVideoBuffering;
            }
            VideoLength = Math.Max(1, item.DurationMs ?? 1);
            // Only someone's moments and they are not in this video: on to the next one.
            if (AutoPlay && MomentChoice.Kind == MomentKind.Person && Moments == null)
            {
                _video.Stop();
                Dispatcher.UIThread.Post(Next);
                return;
            }
            _streamed = null;
            if (Cloud.MediaFiles.IsLocal(item))
            {
                _playingPath = Is360 ? Path360(item, item.FullPath, version) : item.FullPath;
                _video.Play(_playingPath, TakeStart(entry));
                HoldIfWanted();
                _positionTimer.Start();
            }
            else
            {
                // The video before must not go on playing (and sounding) while this one is opened.
                _video.Stop();
                _positionTimer.Stop();
                // Its thumbnail meanwhile, not a black window.
                if (!Is360) ShowThumbnail(item);
                _ = PlayFromCloudAsync(entry, version);
            }
        }
        else
        {
            IsVideo = false;
            IsRunning = AutoPlay;
            MomentText = "";
            _positionTimer.Stop();
            _video?.Stop();
            _ = ShowPhotoAsync(entry, version);
        }
        Prefetch();
        LoadPeople();
    }

    private bool _notRemembered;

    /// <summary>Playing or paused changes what the people layer shows (setting: boxes only on a paused video).</summary>
    partial void OnIsRunningChanged(bool value)
    {
        if (ShowPeople) LoadPeople();
    }

    private long _lastVlcTime = -1;
    private readonly System.Diagnostics.Stopwatch _sinceVlcTime = new();

    /// <summary>
    /// The video's time between libVLC's reports (they come a few times a second): the last report plus the
    /// time since, at the playing speed, so a box moves with every frame and not in steps.
    /// </summary>
    private long SmoothNowMs
    {
        get
        {
            var reported = NowMs;
            if (reported != _lastVlcTime)
            {
                _lastVlcTime = reported;
                _sinceVlcTime.Restart();
                return reported;
            }
            return IsRunning ? reported + (long)Math.Min(1000, _sinceVlcTime.ElapsedMilliseconds * Rate) : reported;
        }
    }

    partial void OnShowPeopleChanged(bool value)
    {
        LoadPeople();
        if (!_notRemembered) _main.RememberShowPeople(value);
    }

    /// <summary>The boxes on for this viewer only (opened from a card of Who is this).</summary>
    public void ShowPeopleHere()
    {
        _notRemembered = true;
        ShowPeople = true;
        _notRemembered = false;
    }

    /// <summary>While a name is typed for a box the video waits, then goes on if it was playing.</summary>
    public bool HoldForDialog()
    {
        if (!IsVideo || _video == null || !IsRunning) return false;
        _video.Pause();
        IsRunning = false;
        return true;
    }

    public void ResumeAfterDialog(bool wasRunning)
    {
        if (wasRunning && IsVideo && _video != null && !IsRunning) TogglePlay();
    }

    partial void OnVolumeChanged(int value)
    {
        if (_video != null) _video.Volume = value;
        _main.SaveVolume(value);
    }

    /// <summary>Another choice of what to play: continue with its next moment in this video.</summary>
    private void OnMomentChoiceChanged(MomentChoice value)
    {
        _main.RememberMoments(value.Kind != MomentKind.Whole);
        _moment = 0;
        _insideMoment = false;
        if (!IsVideo || _video == null) return;
        if (Moments is not { } moments)
        {
            MomentText = "";
            if (AutoPlay && value.Kind == MomentKind.Person) Next();
            return;
        }
        var now = _video.TimeMs;
        _moment = Math.Max(0, moments.FindIndex(m => m.EndMs > now));
        if (moments.All(m => m.EndMs <= now)) _moment = 0;
        if (now < moments[_moment].StartMs - 1500 || now > moments[_moment].EndMs) _video.Seek(moments[_moment].StartMs);
        MomentText = MomentLabel();
    }

    private void LoadPeople()
    {
        // A 360 video waiting for its new analysis: the old boxes would sit in the wrong places.
        var stale = ShowPeople && Is360 && !_main.People.PeopleUpToDate(Current.Item.Id);
        _detections = ShowPeople && !stale ? _main.People.DetectionsOf(Current.Item.Id) : [];
        _paths = ShowPeople && Current.Item.IsVideo ? _main.People.TrackPaths(Current.Item.Id) : [];
        _pathsOf = ShowPeople ? Current.Item.Id : -1;
        _names = ShowPeople ? _main.People.Names() : [];
        _hidden = ShowPeople ? _main.People.HiddenPersonIds() : [];
        PeopleHint = !ShowPeople ? "" :
            stale ? "The people of this 360 video are being analyzed again. Open it later." :
            _detections.Count == 0 ? "No people found here (or the analysis has not got here yet)." :
            IsVideo && IsRunning && !_main.BoxesWhilePlaying ? "Pause the video to see who is who." :
            "Click a person to say who it is.";
        _peopleTimeBucket = -1;
        PeopleChanged?.Invoke();
    }

    /// <summary>
    /// PersonId: who the box says (assigned or suggested), for "Not Greta". Guess: the name is the analysis' own
    /// ("David?", "David??"), the user can say yes to it with one click.
    /// </summary>
    public sealed record PeopleBox(long DetectionId, double X, double Y, double W, double H, string Label, bool Known, bool IsFace,
        long? PersonId = null, string? PersonName = null, bool Guess = false);

    /// <summary>Boxes to draw now, in content pixels: one per person.</summary>
    public List<PeopleBox> PeopleBoxes()
    {
        if (!ShowPeople || IsVideo && !VideoReady) return [];
        if (!IsVideo)
        {
            // A face on a body is that person already: only the body gets a box.
            var bodiesWithFace = _detections.Where(d => d.LinkedId != null).Select(d => d.LinkedId!.Value).ToHashSet();
            return _detections
                .Where(d => d.Kind == Analysis.DetectionKind.Body || d.LinkedId == null)
                .Where(d => d.PersonId is not { } p || !_hidden.Contains(p))
                .Select(d => Box(d, d.X, d.Y, d.W, d.H, bodiesWithFace.Contains(d.Id) ? _detections.First(f => f.LinkedId == d.Id) : null))
                .OfType<PeopleBox>()
                .ToList();
        }
        // Only on a paused video, if the user prefers that.
        if (IsRunning && !_main.BoxesWhilePlaying) return [];
        var now = (int)SmoothNowMs;
        // The last seconds after the last analyzed frame: the people stay where that frame saw them.
        var lastFrame = _paths.Values.SelectMany(p => p).Select(p => p.TimeMs).DefaultIfEmpty(0).Max();
        if (now > lastFrame) now = lastFrame;
        var interval = People.PeopleAnalyzer.FrameIntervalMs(Current.Item.DurationMs ?? 0);
        var boxes = new List<PeopleBox>();
        // Who each track is: a confirmed detection first, then one settled by that day, any assigned, a suggestion.
        var tracks = new List<(List<People.TrackBox> Path, People.DetectionRow Who)>();
        foreach (var (track, path) in _paths)
        {
            var dets = _detections.Where(d => d.Track == track).ToList();
            if (dets.Count == 0) continue;
            var who = dets.OrderByDescending(d => d.Confirmed).ThenByDescending(Settled).ThenByDescending(d => d.PersonId != null).ThenByDescending(d => d.SuggestPersonId != null)
                .ThenByDescending(d => d.Kind == Analysis.DetectionKind.Body).ThenByDescending(d => d.Quality).First();
            if (who.PersonId is { } p && _hidden.Contains(p)) continue;
            tracks.Add((path, who));
        }
        // One named person in several tracks one after another (lost while falling, found again): one box that
        // goes from where the person was last seen to where they are found again.
        var bridge = Math.Max(2 * interval, Math.Min(4 * interval, 4000));
        foreach (var person in tracks.Where(t => t.Who.PersonId != null).GroupBy(t => t.Who.PersonId!.Value))
        {
            var parts = person.OrderBy(t => t.Path[0].TimeMs).ToList();
            var seen = parts.Select(t => (t.Who, t.Path, At: Where(t.Path, now, interval))).Where(t => t.At != null).ToList();
            if (seen.Count > 0)
            {
                // Everybody found has a box: two people on screen at once who are both taken for this person both
                // show (the name is then wrong on one, and can be corrected there). Of tracks one after another
                // only the first shows while both are held around the moment one ends and the next begins.
                var shown = new List<List<People.TrackBox>>();
                foreach (var (who, path, at) in seen)
                {
                    if (shown.Count > 0 && !shown.Any(s => s[0].TimeMs <= path[^1].TimeMs && path[0].TimeMs <= s[^1].TimeMs)) continue;
                    shown.Add(path);
                    var inside = at!.Value;
                    if (Box(who, inside.X, inside.Y, inside.W, inside.H, null) is { } box) boxes.Add(box);
                }
                continue;
            }
            var (before, after) = (parts.LastOrDefault(t => t.Path[^1].TimeMs <= now), parts.FirstOrDefault(t => t.Path[0].TimeMs > now));
            if (before.Path == null || after.Path == null) continue;
            var (p0, p1) = (before.Path[^1], after.Path[0]);
            if (p1.TimeMs - p0.TimeMs > bridge || now < p0.TimeMs) continue;
            var t = (float)(now - p0.TimeMs) / Math.Max(1, p1.TimeMs - p0.TimeMs);
            var mid = Between(p0, p1, t);
            if (Box(before.Who, mid.X, mid.Y, mid.W, mid.H, null) is { } bridged) boxes.Add(bridged);
        }
        foreach (var (path, who) in tracks.Where(t => t.Who.PersonId == null))
        {
            if (Where(path, now, interval) is not { } b) continue;
            if (Box(who, b.X, b.Y, b.W, b.H, null) is { } box) boxes.Add(box);
        }
        return boxes;
    }

    /// <summary>Where a track is at a time: between two analyzed frames moved proportionally, around one held briefly.</summary>
    private (float X, float Y, float W, float H)? Where(List<People.TrackBox> path, int now, int interval)
    {
        var after = path.FindIndex(p => p.TimeMs >= now);
        if (after == 0 || after < 0)
        {
            var edge = after == 0 ? path[0] : path[^1];
            return Math.Abs(edge.TimeMs - now) <= interval / 2 ? (edge.X, edge.Y, edge.W, edge.H) : null;
        }
        var (p0, p1) = (path[after - 1], path[after]);
        // A 360 video: a person far away is found only now and then, and directions hold still, so the box goes
        // on between two sightings some seconds apart.
        if (p1.TimeMs - p0.TimeMs > (Is360 ? Math.Max(interval * 2, 8000) : interval * 2))
        {
            var near = now - p0.TimeMs < p1.TimeMs - now ? p0 : p1;
            return Math.Abs(near.TimeMs - now) <= interval / 2 ? (near.X, near.Y, near.W, near.H) : null;
        }
        var t = (float)(now - p0.TimeMs) / Math.Max(1, p1.TimeMs - p0.TimeMs);
        return Between(p0, p1, t);
    }

    /// <summary>A box on the way from one to the other; on a 360 map the short way, across the seam behind the camera.</summary>
    private (float X, float Y, float W, float H) Between(People.TrackBox p0, People.TrackBox p1, float t)
    {
        var w = p0.W + (p1.W - p0.W) * t;
        var dx = p1.X + p1.W / 2 - (p0.X + p0.W / 2);
        if (Is360 && dx > 0.5f) dx -= 1;
        else if (Is360 && dx < -0.5f) dx += 1;
        return (p0.X + p0.W / 2 + dx * t - w / 2, p0.Y + (p1.Y - p0.Y) * t, w, p0.H + (p1.H - p0.H) * t);
    }

    /// <summary>A box in content pixels; for 360 media from the map into the current view, null when not in it.</summary>
    private PeopleBox? Box(People.DetectionRow d, float x, float y, float w, float h, People.DetectionRow? face)
    {
        // What is stored is where the person is in the file; shown is the picture as the user turned it.
        if (!Is360) (x, y, w, h) = Media.QuarterTurn.ToTurned(Rotation, x, y, w, h);
        double bx = x * ContentWidth, by = y * ContentHeight, bw = w * ContentWidth, bh = h * ContentHeight;
        if (Is360)
        {
            if (_view360 is not { } view ||
                Media.Fisheye360.EquirectBoxToView(view.Yaw, view.Pitch, view.Fov, view.Width, view.Height, x, y, w, h) is not { } r) return null;
            (bx, by, bw, bh) = r;
            // A box larger than the view (who holds the camera, somebody right at it) is only lines across the
            // picture, and so is one that is mostly outside: not drawn; zooming out shows it.
            var inside = Math.Max(0, Math.Min(bx + bw, view.Width) - Math.Max(bx, 0)) * Math.Max(0, Math.Min(by + bh, view.Height) - Math.Max(by, 0));
            if (bw > view.Width * 0.6 || inside < bw * bh * 0.3) return null;
        }
        // The face says who it is when the figure does not, and when the face's name is settled (a figure with such
        // a face is no guess).
        var who = !d.Confirmed && face is { PersonId: not null } && (Settled(face) || d.PersonId == null) ? face : d;
        var settled = Settled(who);
        var label = who.PersonId is { } p && _names.TryGetValue(p, out var n) ? n + (settled ? "" : "?") :
            who.SuggestPersonId is { } s && _names.TryGetValue(s, out var sn) ? sn + "??" : "Who?";
        var personId = who.PersonId ?? who.SuggestPersonId;
        var known = personId is { } id && _names.TryGetValue(id, out var name) ? name : null;
        return new PeopleBox(d.Id, bx, by, bw, bh, label,
            who.PersonId != null, d.Kind == Analysis.DetectionKind.Face, personId, known, known != null && !settled);
    }

    /// <summary>
    /// No guess: the user said who it is, or a face the user named on the same day did (one answer stands for the
    /// day's series; no "?" and nothing to confirm).
    /// </summary>
    private static bool Settled(People.DetectionRow d) => d.Confirmed || d.PersonId != null && d.AssignSource == People.AssignSource.SameDay;

    /// <summary>For the dialog that asks who a box is: its picture and the known people, the most likely first.</summary>
    public Task<WhoQuestion> AskAsync(long detectionId) => _main.People.AskBoxAsync(detectionId);

    /// <summary>The person the user named last, the photo or video it was on, and the name as written.</summary>
    private (long Person, long MediaId, string Name)? _lookingFor;

    public void NameDetection(long detectionId, string name)
    {
        var person = _main.People.NameDetection(detectionId, name);
        LoadPeople();
        // The named person can be chosen right away ("Only ...").
        _personMoments.Clear();
        if (IsVideo) SyncChoices();
        if (person is not { } id) return;
        // The answer is matched against the whole library in the background: said now, and what it found when it is done.
        _lookingFor = (id, Current.Item.Id, name.Trim());
        PeopleHint = $"Looking for {name.Trim()} in the other photos and videos...";
    }

    /// <summary>One click on the tick of a guessed name ("David?"): yes, it is David.</summary>
    public void ConfirmGuess(long detectionId)
    {
        if (PeopleBoxes().FirstOrDefault(b => b.DetectionId == detectionId) is { Guess: true, PersonName: { } name })
            NameDetection(detectionId, name);
    }

    /// <summary>
    /// The matching after an answer is done: the names it changed show at once (also on the photo opened meanwhile),
    /// and how far the last answer reached is said.
    /// </summary>
    private void OnAnswered(People.IdentityService.Reach? reach)
    {
        var asked = _lookingFor;
        _lookingFor = null;
        if (!ShowPeople) return;
        LoadPeople();
        if (asked is not var (person, mediaId, name)) return;
        var found = reach?.Found.GetValueOrDefault(person)?.Where(m => m != mediaId).ToHashSet() ?? [];
        // Named before as a guess, and settled now by this answer: the rest of the day's series.
        var settled = reach?.Settled.GetValueOrDefault(person)?.Count(m => m != mediaId && !found.Contains(m)) ?? 0;
        PeopleHint = reach == null ? $"The library is busy: {name} is looked for in the other photos and videos a little later."
            : found.Count > 0 ? $"{name} is on {Format.Count(found.Count, "more photo or video", "more photos and videos")} now."
            : settled > 0 ? $"{name}: also on {Format.Count(settled, "more photo", "more photos")} of that day, no need to confirm them."
            : $"{name}: no new photos or videos found this time.";
    }

    public void RejectDetection(long detectionId, long personId)
    {
        _main.People.RejectDetection(detectionId, personId);
        LoadPeople();
    }

    public void IgnoreDetection(long detectionId)
    {
        _main.People.IgnoreDetection(detectionId);
        LoadPeople();
    }

    /// <summary>The user paused and browses on: the next video waits on its first frame too (not in a slideshow).</summary>
    public bool StaysPaused { get; set; }

    /// <summary>
    /// A still picture instead of playing: opened to say who someone is (from Who is this), or the user had
    /// paused the previous video.
    /// </summary>
    private void HoldIfWanted()
    {
        if (_video == null || !(PauseOnOpen || StaysPaused && !AutoPlay)) return;
        PauseOnOpen = false;
        _video.Pause();
        IsRunning = false;
    }

    private async Task ShowWhenVideoLoadedAsync(int version)
    {
        await VideoPlayer.LoadAsync();
        if (version == _loadVersion) Show();
    }

    /// <summary>The video playing from the cloud as it comes (not from a file here): what to fall back on if that fails.</summary>
    private (PlaylistEntry Entry, int Version, int StartMs)? _streamed;

    /// <summary>
    /// A video that lives in the cloud plays while it comes. Downloaded whole first, as before: a 360 video (its
    /// leveling reads the end of the file, its playing copy all of it), a video with a copy on this computer, and
    /// any video the gate cannot serve.
    /// </summary>
    private async Task PlayFromCloudAsync(PlaylistEntry entry, int version)
    {
        if (!Is360 && Cloud.MediaFiles.LocalNow(entry.Item) == null)
        {
            LoadingText = "Opening the video from the cloud...";
            string? url = null;
            try
            {
                url = await _main.Streams.UrlAsync(entry.Item, CancellationToken.None);
            }
            catch (Exception e) when (e is Cloud.RcloneException or IOException)
            {
                // Downloaded instead.
            }
            if (version != _loadVersion || _video == null) return;
            if (url != null)
            {
                var start = TakeStart(entry);
                _streamed = (entry, version, start);
                _playingPath = url;
                _video.Play(url, start);
                HoldIfWanted();
                _positionTimer.Start();
                return;
            }
        }
        await DownloadAndPlayAsync(entry, version, null);
    }

    /// <summary>
    /// A video played from the cloud stands still on its picture while more of it comes (after a jump, on a slow
    /// line, or at the start of a video that needs much before it moves): said, so it is not taken for stuck.
    /// </summary>
    private void OnVideoBuffering()
    {
        if (_video == null || !VideoReady) return;
        if (_video.IsBuffering && IsRunning)
        {
            if (_loadPending || IsLoading) return;
            BeginLoading();
            LoadingText = "Loading more of the video from the cloud...";
        }
        else EndLoading();
    }

    /// <summary>libVLC could not open the video or lost it: a streamed one is downloaded and played from the file.</summary>
    private void OnVideoFailed()
    {
        if (_streamed is { } s && s.Version == _loadVersion)
        {
            _streamed = null;
            var at = VideoReady ? (int)NowMs : s.StartMs;
            VideoReady = false;
            _positionTimer.Stop();
            BeginLoading();
            _ = DownloadAndPlayAsync(s.Entry, s.Version, at);
        }
    }

    private async Task DownloadAndPlayAsync(PlaylistEntry entry, int version, int? at)
    {
        LoadingText = "Downloading the video from the cloud...";
        try
        {
            // A video is large: how far, so the wait is not a mystery.
            var downloaded = false;
            var progress = new Progress<Cloud.Rclone.CopyProgress>(p =>
            {
                if (version == _loadVersion && !downloaded) LoadingText = $"Downloading the video from the cloud: {p.Text}";
            });
            var path = await Cloud.MediaFiles.LocalPathAsync(entry.Item, CancellationToken.None, waited: true, progress);
            downloaded = true;
            if (version != _loadVersion || _video == null) return;
            LoadingText = "Loading...";
            _playingPath = Is360 ? Path360(entry.Item, path, version) : path;
            if (at != null) ResumeAtMs = at;
            _video.Play(_playingPath, TakeStart(entry));
            HoldIfWanted();
            _positionTimer.Start();
        }
        catch (Exception e) when (e is Cloud.RcloneException or IOException)
        {
            if (version == _loadVersion)
            {
                Error = "The video cannot be downloaded: " + e.Message;
                EndLoading();
            }
        }
    }

    private async Task ShowPhotoAsync(PlaylistEntry entry, int version)
    {
        if (Is360)
        {
            await Show360PhotoAsync(entry, version);
            return;
        }
        // A photo that lives in the cloud is downloaded first: the wait says so, over its thumbnail (the people
        // boxes are placed by fractions of the picture, so they fit the thumbnail too).
        if (!Cloud.MediaFiles.IsLocal(entry.Item) && !_prefetch.ContainsKey(entry.Item.Id))
        {
            LoadingText = "Downloading from the cloud...";
            ShowThumbnail(entry.Item);
        }
        var decoded = await DecodeAsync(entry.Item);
        if (version != _loadVersion) return;
        if (decoded == null)
        {
            Photo = null;
            Error = "This photo cannot be opened.";
            EndLoading();
        }
        else
        {
            Photo = ToBitmap(decoded);
            SetPictureSize(decoded.Width, decoded.Height);
            EndLoading();
            // The boxes are placed in the photo's pixels: now that its size is known.
            PeopleChanged?.Invoke();
        }
        if (AutoPlay) _slideTimer.Start();
    }

    private void ShowThumbnail(MediaItem item)
    {
        if (_main.Thumbnails.ExistingPath(item.Id) is not { } path) return;
        try
        {
            var thumb = new Bitmap(path);
            Photo = thumb;
            SetPictureSize(thumb.PixelSize.Width, thumb.PixelSize.Height);
            PeopleChanged?.Invoke();
        }
        catch (Exception)
        {
            // A broken thumbnail: the photo comes without it.
        }
    }

    private Task<DecodedImage?> DecodeAsync(MediaItem item)
    {
        if (_prefetch.Remove(item.Id, out var task)) return task;
        var (w, h) = (ScreenSize.Width, ScreenSize.Height);
        return Task.Run<DecodedImage?>(async () =>
        {
            try
            {
                return ImageCodec.DecodeForDisplay(await Cloud.MediaFiles.LocalPathAsync(item, CancellationToken.None, waited: true), w, h);
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    /// <summary>The next photo is decoded while the current one is shown, so moving on is instant.</summary>
    private void Prefetch()
    {
        // Only the photos around the current one stay decoded.
        var near = Enumerable.Range(Index - 2, 5).Where(i => i >= 0 && i < Count).Select(i => _entries[i].Item.Id).ToHashSet();
        foreach (var stale in _prefetch.Keys.ToList())
            if (!near.Contains(stale)) _prefetch.Remove(stale);
        if (Index + 1 < Count && _entries[Index + 1].Item is { IsVideo: false } next && !_prefetch.ContainsKey(next.Id)
            && !Media.Fisheye360.IsDualFisheye(next.FullPath))
            _prefetch[next.Id] = DecodeAsync(next);
    }

    private static Bitmap ToBitmap(DecodedImage d)
    {
        var bitmap = new WriteableBitmap(new PixelSize(d.Width, d.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        var rowBytes = d.Width * 4;
        for (var y = 0; y < d.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(d.Bgra, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
        return bitmap;
    }

    private void OnVideoEnded()
    {
        // A video from the cloud that "ended" well before its end stopped coming (the gate is gone): the rest is
        // downloaded and it goes on from there.
        if (_streamed is { } s && s.Version == _loadVersion && VideoLength > 5000 && NowMs < VideoLength - 5000)
        {
            OnVideoFailed();
            return;
        }
        // Loop: the same video (or its first moment) again.
        if (Loop && _video != null && _playingPath != null)
        {
            _moment = 0;
            _insideMoment = false;
            MomentText = MomentLabel();
            _video.Play(_playingPath, LoopStartMs ?? StartOf(Current));
            IsRunning = true;
            return;
        }
        if (AutoPlay) Next();
    }

    private void UpdateVideoPosition()
    {
        if (_video == null || _seeking) return;
        var length = _video.LengthMs;
        var time = NowMs;
        if (length > 0) VideoLength = length;
        if (time >= 0) VideoPosition = time;
        Keep360InStep();
        IsRunning = _video.IsPlaying;
        VideoTimeText = $"{Format.Duration(Math.Max(0, time))} / {Format.Duration(Math.Max(0, length))}";
        if (ShowPeople && time / 500 != _peopleTimeBucket)
        {
            _peopleTimeBucket = time / 500;
            PeopleChanged?.Invoke();
        }
        // Only the moments: at the end of one jump to the next, after the last one go on (or stop when browsing).
        // Right after a switch libVLC may still report the previous video's time: act only once inside the moment.
        if (_video.IsPlaying && KeepInLoop(time)) return;
        if (Moments is not { } moments || _moment >= moments.Count) return;
        var (start, end) = moments[_moment];
        if (!_insideMoment)
        {
            _insideMoment = time >= start - 1500 && time <= end;
            return;
        }
        if (time <= end) return;
        _insideMoment = false;
        if (++_moment < moments.Count)
        {
            _video.Seek(moments[_moment].StartMs);
            MomentText = MomentLabel();
        }
        else if (Loop)
        {
            // Loop: the moments of this video again from the first one.
            _moment = 0;
            _video.Seek(moments[0].StartMs);
            MomentText = MomentLabel();
        }
        else if (AutoPlay) Next();
        else
        {
            // Browsing: stop here, and say why the video stands still.
            _video.Pause();
            IsRunning = false;
            Notice = MomentChoice.Kind == MomentKind.Person
                ? $"{MomentChoice.Label.Replace("Only ", "")} is not in the rest of this video. Space plays on, the right arrow goes to the next one."
                : "The matching part is over. Space plays on, the right arrow goes to the next one.";
        }
    }

    /// <summary>The moments to play of the current video by the choice; null plays it whole.</summary>
    private List<(int StartMs, int EndMs)>? Moments => IsVideo ? MomentsOf(Current) : null;

    private List<(int StartMs, int EndMs)>? MomentsOf(PlaylistEntry entry)
    {
        var moments = MomentChoice.Kind switch
        {
            MomentKind.Match => entry.MatchMoments?.ToList(),
            MomentKind.Person => PersonMoments(MomentChoice.PersonIds).GetValueOrDefault(entry.Item.Id),
            _ => null,
        };
        return moments is { Count: > 0 } ? moments : null;
    }

    private Dictionary<long, List<(int StartMs, int EndMs)>> PersonMoments(IReadOnlyList<long> personIds)
    {
        var key = string.Join(",", personIds);
        if (!_personMoments.TryGetValue(key, out var moments))
            _personMoments[key] = moments = _main.People.PersonSegments(personIds);
        return moments;
    }

    private string MomentLabel() => Moments is { } m
        ? $"{MomentChoice.Label}: {Math.Min(_moment + 1, m.Count)} / {m.Count}"
        : "";

    /// <summary>Continue at this time (from the panel to full screen and back) instead of the usual start, once.</summary>
    public int? ResumeAtMs { get; set; }

    /// <summary>Where the video is now, to hand over to another viewer of the same item.</summary>
    public int PositionMs => IsVideo && _video != null ? (int)NowMs : 0;

    /// <summary>Where the current video was started: libVLC says 0 until it plays, paused on the first frame.</summary>
    private int _startedAtMs;

    private int TakeStart(PlaylistEntry entry)
    {
        var at = ResumeAtMs ?? StartOf(entry);
        ResumeAtMs = null;
        _lastReportedMs = 0;
        return _startedAtMs = at;
    }

    /// <summary>The video's time; the start until libVLC reports one.</summary>
    private long NowMs
    {
        get
        {
            if (_video is not { } v) return _startedAtMs;
            // At the end libVLC reports no time: the video stands on its last frame, so does the time.
            if (v.IsEnded) return Math.Max(_lastReportedMs, _startedAtMs);
            var time = v.TimeMs;
            if (time > 0) return _lastReportedMs = time;
            return _startedAtMs;
        }
    }

    private long _lastReportedMs;

    /// <summary>Another viewer of this item moved on: continue from there, paused or playing as this one was.</summary>
    public void ContinueAt(int ms)
    {
        if (!IsVideo || _video == null) return;
        _video.Seek(ms);
        _startedAtMs = ms;
        VideoPosition = ms;
        Notice = "";
    }

    /// <summary>Where a video starts: its first moment, or the matching frame.</summary>
    private int StartOf(PlaylistEntry entry) => entry.Item.IsVideo && MomentsOf(entry) is { } m ? m[0].StartMs : entry.StartMs;

    /// <summary>The slider was dragged.</summary>
    public void SeekTo(double ms)
    {
        SeekVideo((long)ms);
        _seeking = false;
    }

    /// <summary>A seek; the time also stands there until libVLC reports one (after the end it opens the video again).</summary>
    private void SeekVideo(long ms)
    {
        if (_video == null) return;
        ms = Math.Max(0, ms);
        _video.Seek(ms);
        _startedAtMs = (int)ms;
        _lastVlcTime = -1;
        VideoPosition = ms;
        Notice = "";
    }

    public void BeginSeek() => _seeking = true;

    public void ChangeVolume(int delta) => Volume = Math.Clamp(Volume + delta, 0, 100);

    // ---- slow motion, loop, frame by frame (looking at a technique) ----

    private static readonly double[] Rates = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2];
    private string? _playingPath;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(RateText), nameof(IsSlowOrFast))] private double _rate = 1;
    /// <summary>The video (or the person's moments in it) plays again and again.</summary>
    [ObservableProperty] private bool _loop;

    public string RateText => Rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "\u00d7";
    public bool IsSlowOrFast => Rate != 1;

    partial void OnRateChanged(double value)
    {
        if (_video != null) _video.Rate = (float)value;
        _main.RememberPlayback(value, Loop);
    }

    partial void OnLoopChanged(bool value)
    {
        _main.RememberPlayback(Rate, value);
        // The chosen part stays (shown faded) for switching the loop on again; Clear removes it.
        SetLoopRange(LoopStartMs, LoopEndMs);
    }

    // ---- loop of a part (Start / End, also two handles on the time line) ----

    /// <summary>The chosen part; null = from the beginning / to the end of the video.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLoopPart))] private int? _loopStartMs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLoopPart))] private int? _loopEndMs;
    public bool HasLoopPart => LoopStartMs != null || LoopEndMs != null;

    /// <summary>"Loop 0:12 to 0:18" under the title while a part is chosen.</summary>
    [ObservableProperty] private string _loopText = "";

    private void SetLoopRange(int? start, int? end)
    {
        (LoopStartMs, LoopEndMs) = (start, end);
        var part = (start, end) switch
        {
            ({ } a, { } b) => $"{Format.Duration(a)} to {Format.Duration(b)}",
            ({ } a, null) => $"from {Format.Duration(a)} (End marks where it turns back)",
            (null, { } b) => $"from the beginning to {Format.Duration(b)}",
            _ => "",
        };
        LoopText = part == "" ? "" : Loop ? $"Loop {part}" : $"Loop is off ({part} stays marked)";
    }

    /// <summary>The loop starts at the current moment, playing or paused (and loop goes on).</summary>
    [RelayCommand]
    private void SetLoopStart()
    {
        if (!IsVideo) return;
        var now = (int)NowMs;
        Loop = true;
        SetLoopRange(now, LoopEndMs is { } end && end > now + 200 ? end : null);
    }

    /// <summary>The loop ends at the current moment: it plays from its start right away, also when paused.</summary>
    [RelayCommand]
    private void SetLoopEnd()
    {
        if (!IsVideo) return;
        var now = (int)NowMs;
        var start = LoopStartMs is { } s && s < now - 200 ? s : (int?)null;
        Loop = true;
        SetLoopRange(start, now);
        SeekVideo(start ?? 0);
        if (!IsRunning) TogglePlay();
    }

    /// <summary>No chosen part: the loop is the whole video again.</summary>
    [RelayCommand]
    private void ClearLoopPart() => SetLoopRange(null, null);

    /// <summary>A handle on the time line is dragged: the start or the end of the part moves with it.</summary>
    public void MoveLoopEdge(bool start, double ms)
    {
        var at = (int)Math.Clamp(ms, 0, VideoLength);
        if (start) SetLoopRange(Math.Min(at, (LoopEndMs ?? (int)VideoLength) - 200), LoopEndMs);
        else SetLoopRange(LoopStartMs, Math.Max(at, (LoopStartMs ?? 0) + 200));
    }

    /// <summary>The handle was let go: show the result, from the new start or the last moments before the new end.</summary>
    public void LoopEdgeMoved(bool start)
    {
        Loop = true;
        var from = LoopStartMs ?? 0;
        SeekVideo(start || LoopEndMs is not { } end ? from : Math.Max(from, end - 1500));
    }

    /// <summary>Past the end of the chosen part: back to its start.</summary>
    private bool KeepInLoop(long time)
    {
        if (!Loop || LoopEndMs is not { } end || time < end) return false;
        SeekVideo(LoopStartMs ?? 0);
        return true;
    }

    [RelayCommand]
    private void SetRate(string rate) =>
        Rate = double.Parse(rate, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The next slower or faster speed ([ and ]).</summary>
    public void StepRate(int direction)
    {
        var i = Array.IndexOf(Rates, Rate);
        if (i < 0) i = Array.IndexOf(Rates, 1.0);
        Rate = Rates[Math.Clamp(i + direction, 0, Rates.Length - 1)];
    }

    [RelayCommand]
    public void ToggleLoop() => Loop = !Loop;

    /// <summary>One frame further (.); pauses first.</summary>
    [RelayCommand]
    public void NextFrame()
    {
        if (!IsVideo || _video == null) return;
        if (IsRunning)
        {
            _video.Pause();
            IsRunning = false;
        }
        _video.NextFrame();
    }

    private int _volumeBeforeMute = 70;

    [RelayCommand]
    private void ToggleMute()
    {
        if (Volume > 0)
        {
            _volumeBeforeMute = Volume;
            Volume = 0;
        }
        else Volume = _volumeBeforeMute;
    }

    public void SeekBy(int seconds)
    {
        if (_video != null && IsVideo) SeekVideo(NowMs + seconds * 1000L);
    }

    private string PositionLabel() => $"{Index + 1} / {Count}" + (AutoPlay ? "  ·  slideshow" : "");

    [RelayCommand]
    public void Next()
    {
        if (Index + 1 < Count)
        {
            Index++;
            Show();
        }
        else if (AutoPlay)
        {
            // End of the slideshow.
            CloseRequested?.Invoke();
        }
    }

    /// <summary>The panel follows the grid: another item of the same results.</summary>
    public void GoTo(int index)
    {
        if (index < 0 || index >= Count || (index == Index && _loadVersion > 0)) return;
        Index = index;
        Show();
    }

    /// <summary>Whether this viewer shows exactly these results (then the panel only moves, it is not rebuilt).</summary>
    public bool Plays(IReadOnlyList<TileViewModel> tiles) =>
        ReferenceEquals(tiles, _entries.Tiles)
        || tiles.Count == Count && tiles.Select(t => t.Item.Id).SequenceEqual(_entries.Tiles.Select(t => t.Item.Id));

    /// <summary>The panel's video stops while the same item plays full screen.</summary>
    public void Pause()
    {
        if (IsVideo && _video is { IsPlaying: true }) _video.TogglePause();
        _slideTimer.Stop();
    }

    [RelayCommand]
    public void Previous()
    {
        if (Index == 0) return;
        Index--;
        Show();
    }

    /// <summary>Space: on a video pauses it; on a photo starts / stops the slideshow.</summary>
    [RelayCommand]
    public void TogglePlay()
    {
        if (IsVideo && _video != null)
        {
            _video.TogglePause();
            IsRunning = !IsRunning;
            StaysPaused = !IsRunning;
            Notice = "";
            return;
        }
        AutoPlay = !AutoPlay;
        IsRunning = AutoPlay;
        PositionText = PositionLabel();
        if (AutoPlay) _slideTimer.Start();
        else _slideTimer.Stop();
    }

    [RelayCommand]
    public void AddToBucket()
    {
        _main.AddToBucket([Current.Item.Id]);
        InBucket = true;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    public void Dispose()
    {
        _main.People.Answered -= OnAnswered;
        _main.People.PeopleFound -= OnPeopleFound;
        _main.People.Rematched -= OnRematched;
        _main.ItemTurned -= OnItemTurned;
        _loadingTimer.Stop();
        _slideTimer.Stop();
        _positionTimer.Stop();
        _video?.Dispose();
        _video = null;
    }
}
