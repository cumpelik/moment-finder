using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.App.Services;
using MediaLens.App.Video;
using MediaLens.Library;
using MediaLens.Media;

namespace MediaLens.App.ViewModels;

/// <summary>An item to show; for a video found by a search or a person, the moments that matter.</summary>
public sealed record PlaylistEntry(MediaItem Item, int StartMs, IReadOnlyList<(int StartMs, int EndMs)>? Moments = null);

/// <summary>OnlyMoments: videos play only their Moments (the person, the matching part), then go on.</summary>
public sealed record PlayerSettings(int SlideSeconds, bool OnlyMoments, string? MomentsOf = null);

/// <summary>
/// The full-screen viewer: browsing (arrows) or a slideshow (photos for N seconds, videos to the end or only
/// the matching part). Space pauses / continues, Esc closes.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly List<PlaylistEntry> _entries;
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

    public PixelSize ScreenSize { get; set; } = new(1920, 1080);

    [ObservableProperty] private int _index;
    [ObservableProperty] private bool _autoPlay;
    [ObservableProperty] private string _peopleHint = "";
    /// <summary>"Greta: 2 / 3" while only the moments of a video play.</summary>
    [ObservableProperty] private string _momentText = "";
    [ObservableProperty] private Bitmap? _photo;
    [ObservableProperty] private bool _isVideo;
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
    [ObservableProperty] private bool _showPeople;

    /// <summary>The people layer must be redrawn (other item, other moment of the video, a new name).</summary>
    public event Action? PeopleChanged;
    private List<People.DetectionRow> _detections = [];
    private Dictionary<long, string> _names = [];
    private long _peopleTimeBucket = -1;

    public event Action? CloseRequested;
    /// <summary>For the view: the video image must be invalidated / replaced.</summary>
    public event Action? VideoFrameChanged;

    public PlayerViewModel(List<PlaylistEntry> entries, int index, bool autoPlay, PlayerSettings settings, MainViewModel main)
    {
        _entries = entries;
        _settings = settings;
        _main = main;
        _index = index;
        _autoPlay = autoPlay;
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, settings.SlideSeconds)) };
        _slideTimer.Tick += (_, _) =>
        {
            _slideTimer.Stop();
            Next();
        };
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdateVideoPosition();
    }

    public WriteableBitmap? VideoFrame => _video?.Frame;

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
        Title = item.FileName;
        Detail = $"{item.Taken:d. M. yyyy H:mm}" + (item.Camera is { } c ? $"  ·  {c}" : "");
        PositionText = PositionLabel();
        InBucket = _main.IsInActiveBucket(item.Id);

        _moment = 0;
        _insideMoment = false;
        if (item.IsVideo)
        {
            Photo = null;
            IsVideo = true;
            MomentText = MomentLabel();
            if (!VideoPlayer.IsAvailable(out var error))
            {
                Error = error;
                if (AutoPlay) _slideTimer.Start();
                return;
            }
            if (_video == null)
            {
                _video = new VideoPlayer();
                _video.FrameUpdated += () => VideoFrameChanged?.Invoke();
                _video.Ended += OnVideoEnded;
            }
            VideoLength = Math.Max(1, item.DurationMs ?? 1);
            if (Cloud.MediaFiles.IsLocal(item))
            {
                _video.Play(item.FullPath, StartOf(entry));
                // Saying who is who: a still picture, so the boxes stay on the people.
                if (ShowPeople) _video.Pause();
                _positionTimer.Start();
            }
            else
            {
                _ = PlayFromCloudAsync(entry, version);
            }
        }
        else
        {
            IsVideo = false;
            MomentText = "";
            _positionTimer.Stop();
            _video?.Stop();
            _ = ShowPhotoAsync(entry, version);
        }
        Prefetch();
        LoadPeople();
    }

    partial void OnShowPeopleChanged(bool value)
    {
        LoadPeople();
        // In a video the people are known at sampled moments: stop at the nearest one so the boxes fit.
        if (value && IsVideo && _video != null && _detections.Count > 0)
        {
            var now = _video.TimeMs;
            var nearest = _detections.MinBy(d => Math.Abs(d.TimeMs - now))!.TimeMs;
            _video.Pause();
            if (Math.Abs(nearest - now) > 1000) _video.Seek(nearest);
        }
    }

    private void LoadPeople()
    {
        _detections = ShowPeople ? _main.People.DetectionsOf(Current.Item.Id) : [];
        _names = ShowPeople ? _main.People.Names() : [];
        PeopleHint = !ShowPeople ? "" :
            _detections.Count == 0 ? "No people found here (or the analysis has not got here yet)." :
            IsVideo ? "Click a person to say who it is. Space plays on; the boxes show at the moments the analysis looked at." :
            "Click a person to say who it is.";
        _peopleTimeBucket = -1;
        PeopleChanged?.Invoke();
    }

    public sealed record PeopleBox(long DetectionId, double X, double Y, double W, double H, string Label, bool Known, bool IsFace);

    /// <summary>Boxes to draw now, in content pixels; for a video only those near the current time.</summary>
    public List<PeopleBox> PeopleBoxes()
    {
        if (!ShowPeople) return [];
        // Paused on the first frame, libVLC still says 0: the video was opened at its start time.
        var now = IsVideo && _video != null ? (_video.TimeMs > 0 ? _video.TimeMs : StartOf(Current)) : 0;
        return _detections
            .Where(d => !IsVideo || Math.Abs(d.TimeMs - now) <= 1500)
            .Select(d => new PeopleBox(d.Id, d.X * ContentWidth, d.Y * ContentHeight, d.W * ContentWidth, d.H * ContentHeight,
                d.PersonId is { } p && _names.TryGetValue(p, out var n) ? n + (d.Confirmed ? "" : "?") :
                d.SuggestPersonId is { } s && _names.TryGetValue(s, out var sn) ? sn + "??" : "Who?",
                d.PersonId != null, d.Kind == Analysis.DetectionKind.Face))
            .ToList();
    }

    public IReadOnlyList<string> Names() => _main.People.PersonNames();

    public void NameDetection(long detectionId, string name)
    {
        _main.People.NameDetection(detectionId, name);
        LoadPeople();
    }

    private async Task PlayFromCloudAsync(PlaylistEntry entry, int version)
    {
        Error = "Downloading from the cloud...";
        try
        {
            var path = await Cloud.MediaFiles.LocalPathAsync(entry.Item, CancellationToken.None);
            if (version != _loadVersion || _video == null) return;
            Error = null;
            _video.Play(path, StartOf(entry));
            _positionTimer.Start();
        }
        catch (Exception e) when (e is Cloud.RcloneException or IOException)
        {
            if (version == _loadVersion) Error = "The video cannot be downloaded: " + e.Message;
        }
    }

    private async Task ShowPhotoAsync(PlaylistEntry entry, int version)
    {
        var decoded = await DecodeAsync(entry.Item);
        if (version != _loadVersion) return;
        if (decoded == null)
        {
            Photo = null;
            Error = "This photo cannot be opened.";
        }
        else
        {
            Photo = ToBitmap(decoded);
            ContentWidth = decoded.Width;
            ContentHeight = decoded.Height;
        }
        if (AutoPlay) _slideTimer.Start();
    }

    private Task<DecodedImage?> DecodeAsync(MediaItem item)
    {
        if (_prefetch.Remove(item.Id, out var task)) return task;
        var (w, h) = (ScreenSize.Width, ScreenSize.Height);
        return Task.Run<DecodedImage?>(async () =>
        {
            try
            {
                return ImageCodec.DecodeForDisplay(await Cloud.MediaFiles.LocalPathAsync(item, CancellationToken.None), w, h);
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
        foreach (var stale in _prefetch.Keys.ToList())
            if (Math.Abs(_entries.FindIndex(e => e.Item.Id == stale) - Index) > 2) _prefetch.Remove(stale);
        if (Index + 1 < Count && _entries[Index + 1].Item is { IsVideo: false } next && !_prefetch.ContainsKey(next.Id))
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
        if (AutoPlay) Next();
    }

    private void UpdateVideoPosition()
    {
        if (_video == null || _seeking) return;
        var length = _video.LengthMs;
        var time = _video.TimeMs;
        if (length > 0) VideoLength = length;
        if (time >= 0) VideoPosition = time;
        VideoTimeText = $"{Format.Duration(Math.Max(0, time))} / {Format.Duration(Math.Max(0, length))}";
        if (ShowPeople && time / 500 != _peopleTimeBucket)
        {
            _peopleTimeBucket = time / 500;
            PeopleChanged?.Invoke();
        }
        // Only the moments: at the end of one jump to the next, after the last one go on (or stop when browsing).
        // Right after a switch libVLC may still report the previous video's time: act only once inside the moment.
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
        else if (AutoPlay) Next();
        else _video.Pause();
    }

    /// <summary>The moments to play of the current video, when the setting says so.</summary>
    private IReadOnlyList<(int StartMs, int EndMs)>? Moments =>
        _settings.OnlyMoments && IsVideo && Current.Moments is { Count: > 0 } m ? m : null;

    private string MomentLabel() => Moments is { } m
        ? $"{_settings.MomentsOf ?? "Matching part"}: {Math.Min(_moment + 1, m.Count)} / {m.Count}"
        : "";

    /// <summary>Where a video starts: its first moment, or the matching frame.</summary>
    private int StartOf(PlaylistEntry entry) =>
        _settings.OnlyMoments && entry.Moments is { Count: > 0 } m ? m[0].StartMs : entry.StartMs;

    /// <summary>The slider was dragged.</summary>
    public void SeekTo(double ms)
    {
        _video?.Seek((long)ms);
        _seeking = false;
    }

    public void BeginSeek() => _seeking = true;

    public void ChangeVolume(int delta)
    {
        if (_video != null) _video.Volume += delta;
    }

    public void SeekBy(int seconds)
    {
        if (_video != null && IsVideo) _video.Seek(_video.TimeMs + seconds * 1000L);
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
            return;
        }
        AutoPlay = !AutoPlay;
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
        _slideTimer.Stop();
        _positionTimer.Stop();
        _video?.Dispose();
        _video = null;
    }
}
