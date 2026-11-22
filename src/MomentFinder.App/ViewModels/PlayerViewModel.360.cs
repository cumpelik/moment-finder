using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.App.Video;
using MomentFinder.Media;

namespace MomentFinder.App.ViewModels;

/// <summary>360 photos and videos: looking around with the mouse, and following the person whose moments play.</summary>
public sealed partial class PlayerViewModel
{
    private View360? _view360;
    private DateTime _lookedAt = DateTime.MinValue;

    /// <summary>The current item is 360 (both lenses of an Insta360): the view can be turned and zoomed.</summary>
    [ObservableProperty] private bool _is360;

    /// <summary>The look-around picture: wide, the Viewbox fits it to the panel or the screen.</summary>
    private View360 View => _view360 ??= new View360(1600, 900);

    /// <summary>A player of a 360 video: its frames go to the look-around view, not to a bitmap.</summary>
    private VideoPlayer New360Player()
    {
        var player = new VideoPlayer { RawFrames = true, Volume = Volume, Rate = (float)Rate };
        player.RawFrame += OnFrame360;
        return player;
    }

    /// <summary>Playing the light copy (both lenses side by side) instead of the original's first lens.</summary>
    private bool _playsProxy;

    /// <summary>
    /// What a 360 video plays: its light copy when it is there; otherwise the original's first lens (the view behind
    /// the camera stays dark) while the copy is made, then the copy from the same moment.
    /// </summary>
    private string Path360(Library.MediaItem item, string original, int version)
    {
        View.ClearLens(1);
        View.UseLenses();
        Preparing360 = false;
        if (Media.Proxy360.Ready(item) is { } proxy)
        {
            _playsProxy = true;
            Lens360Text = "";
            return proxy;
        }
        // The original plays choppy (two 4K streams): a still picture of the front lens and a card saying what
        // happens, with the progress and a way to stop it.
        _playsProxy = false;
        Lens360Text = "";
        PauseOnOpen = true;
        Preparing360 = true;
        PrepareText = "Preparing this 360 video for smooth playing...";
        PrepareProgress = 0;
        _ = SwitchToProxyAsync(item, version);
        return original;
    }

    /// <summary>The card over a 360 video whose playing copy is being made.</summary>
    [ObservableProperty] private bool _preparing360;
    [ObservableProperty] private string _prepareText = "";
    [ObservableProperty] private double _prepareProgress;
    [ObservableProperty] private string _prepareHint = "Once, then it plays smoothly from a lighter copy. You can look at other videos meanwhile.";

    /// <summary>Called with the position updates: the progress of the copy on the card.</summary>
    private void UpdatePrepare()
    {
        if (!Preparing360 || Media.Proxy360.Progress(Current.Item) is not { } p) return;
        PrepareProgress = p.Done * 100;
        PrepareText = $"Preparing this 360 video for smooth playing: {p.Done * 100:0} %" +
                      (p.Left is { } left ? $", about {Services.Format.Span(left)} left" : "");
        PrepareHint = "Once, then it plays smoothly from a lighter copy. You can look at other videos meanwhile." +
                      (Media.FFmpeg.Encoder360 is { } encoder
                          ? encoder == "libx264" ? " No supported graphics card was found, so the processor does it." : " Your graphics card is doing it."
                          : "");
    }

    /// <summary>Stop: no copy now; the front lens plays as it can (opening the video again prepares it again).</summary>
    [RelayCommand]
    private void StopPreparing()
    {
        Media.Proxy360.Stop(Current.Item);
        Preparing360 = false;
        Lens360Text = "Only the front lens, and not smoothly: open the video again to prepare it.";
    }

    private async Task SwitchToProxyAsync(Library.MediaItem item, int version)
    {
        var proxy = await Media.Proxy360.GetAsync(item);
        if (version != _loadVersion || _video is not { } player || !Preparing360) return;
        Preparing360 = false;
        if (proxy == null)
        {
            Lens360Text = "Smooth playing could not be prepared: only the front lens plays.";
            return;
        }
        _playsProxy = true;
        _playingPath = proxy;
        player.Play(proxy, NowMs);
        // Waited for it to watch it: it plays, unless the user had paused the viewer.
        if (StaysPaused) player.Pause();
        else IsRunning = true;
    }

    private void OnFrame360(byte[] pixels, int width, int height)
    {
        if (!_playsProxy)
        {
            Lens(0, pixels, width, height);
            return;
        }
        // The playing copy: one leveled picture of everything around.
        View.SetEquirect(pixels, width, height);
        Follow();
        Render360();
    }

    private byte[] _split = [];

    /// <summary>While the light copy is being made (the first time a 360 video is opened).</summary>
    [ObservableProperty] private string _lens360Text = "";

    private void Lens(int lens, byte[] pixels, int width, int height)
    {
        View.SetLens(lens, pixels, width, height);
        Render360();
    }

    private void Render360()
    {
        if (!View.Render()) return;
        SetPictureSize(View.Width, View.Height);
        VideoFrameChanged?.Invoke();
    }

    /// <summary>The mouse turned the view (degrees) or zoomed it (factor of the field of view).</summary>
    public void Look(double yawBy, double pitchBy, double zoom = 1)
    {
        if (!Is360) return;
        _lookedAt = DateTime.UtcNow;
        View.Look(View.Yaw + yawBy, View.Pitch + pitchBy, View.Fov * zoom);
        Render360();
        PeopleChanged?.Invoke();
    }

    public double ViewFov => View.Fov;

    /// <summary>
    /// Every position update of a 360 video: the second lens kept in step with the first, and with only someone's
    /// moments the view turned smoothly to that person (unless the user looked around just now).
    /// </summary>
    private void Keep360InStep()
    {
        if (!Is360 || _video == null) return;
        UpdatePrepare();
        // A paused video gets no new frames: the view still turns to the person (after a seek, another choice).
        if (!IsRunning && Follow()) Render360();
    }

    private readonly System.Diagnostics.Stopwatch _sinceFollow = new();

    /// <summary>
    /// With only someone's moments the view turns to that person, with every frame so it keeps up with a skier
    /// passing by; not while the user looks around (and four seconds after).
    /// </summary>
    private bool Follow()
    {
        var dt = _sinceFollow.IsRunning ? Math.Min(0.5, _sinceFollow.Elapsed.TotalSeconds) : 0.25;
        _sinceFollow.Restart();
        if (MomentChoice.Kind != MomentKind.Person || DateTime.UtcNow - _lookedAt < TimeSpan.FromSeconds(4)) return false;
        if (PersonDirection(MomentChoice.PersonIds) is not { } target) return false;
        var dyaw = ((target.Yaw - View.Yaw) % 360 + 540) % 360 - 180;
        var dpitch = target.Pitch - View.Pitch;
        if (Math.Abs(dyaw) < 0.2 && Math.Abs(dpitch) < 0.2) return false;
        // Most of the way within a quarter of a second, whatever the frame rate: quick, without a jerk.
        var part = 1 - Math.Exp(-dt / 0.2);
        View.Look(View.Yaw + dyaw * part, View.Pitch + dpitch * part, View.Fov);
        return true;
    }

    /// <summary>Where on the sphere one of these people is now (the center of their box), from the stored paths.</summary>
    private (double Yaw, double Pitch)? PersonDirection(IReadOnlyList<long> personIds)
    {
        if (_pathsOf != Current.Item.Id) LoadPaths();
        var now = (int)SmoothNowMs;
        (double, double)? nearest = null;
        var nearestMs = 5000;
        foreach (var (track, path) in _paths)
        {
            if (!_detections.Any(d => d.Track == track && (d.PersonId ?? d.SuggestPersonId) is { } p && personIds.Contains(p))) continue;
            // On the way between two analyzed frames, also when the analysis lost the person for a while in between.
            var after = path.FindIndex(p => p.TimeMs >= now);
            if (after > 0)
            {
                var (p0, p1) = (path[after - 1], path[after]);
                var b = Between(p0, p1, (float)(now - p0.TimeMs) / Math.Max(1, p1.TimeMs - p0.TimeMs));
                return Fisheye360.FromEquirect(b.X + b.W / 2, b.Y + b.H / 2);
            }
            // Before the first or after the last sight of them (a few seconds): where they were then.
            var edge = after == 0 ? path[0] : path[^1];
            if (Math.Abs(edge.TimeMs - now) >= nearestMs) continue;
            nearestMs = Math.Abs(edge.TimeMs - now);
            nearest = Fisheye360.FromEquirect(edge.X + edge.W / 2, edge.Y + edge.H / 2);
        }
        return nearest;
    }

    private long _pathsOf = -1;

    /// <summary>Who is where in this video, also without the boxes shown (to follow someone).</summary>
    private void LoadPaths()
    {
        _pathsOf = Current.Item.Id;
        _detections = _main.People.DetectionsOf(Current.Item.Id);
        _paths = _main.People.TrackPaths(Current.Item.Id);
    }

    private async Task Show360PhotoAsync(PlaylistEntry entry, int version)
    {
        var decoded = await Task.Run<DecodedImage?>(async () =>
        {
            try
            {
                return ImageCodec.DecodeForDisplay(await Cloud.MediaFiles.LocalPathAsync(entry.Item, CancellationToken.None, waited: true), 5760, 2880);
            }
            catch (Exception)
            {
                return null;
            }
        });
        if (version != _loadVersion) return;
        if (decoded == null)
        {
            Error = "This photo cannot be opened.";
            EndLoading();
            return;
        }
        // Both lens circles side by side.
        View.UseLenses();
        var half = decoded.Width / 2;
        foreach (var lens in new[] { 0, 1 })
        {
            var pixels = new byte[half * decoded.Height * 4];
            for (var y = 0; y < decoded.Height; y++)
                Buffer.BlockCopy(decoded.Bgra, (y * decoded.Width + lens * half) * 4, pixels, y * half * 4, half * 4);
            View.SetLens(lens, pixels, half, decoded.Height);
        }
        Photo = null;
        Render360();
        VideoReady = true;
        EndLoading();
        PeopleChanged?.Invoke();
        if (AutoPlay) _slideTimer.Start();
    }
}
