using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaLens.App.Video;
using MediaLens.Media;

namespace MediaLens.App.ViewModels;

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
        if (Media.Proxy360.Ready(item) is { } proxy)
        {
            _playsProxy = true;
            Lens360Text = "";
            return proxy;
        }
        _playsProxy = false;
        Lens360Text = "Preparing smooth 360 playing (the view behind the camera follows)...";
        _ = SwitchToProxyAsync(item, version);
        return original;
    }

    private async Task SwitchToProxyAsync(Library.MediaItem item, int version)
    {
        var proxy = await Media.Proxy360.GetAsync(item);
        if (version != _loadVersion || _video is not { } player) return;
        if (proxy == null)
        {
            Lens360Text = "";
            return;
        }
        var wasRunning = IsRunning;
        _playsProxy = true;
        _playingPath = proxy;
        Lens360Text = "";
        player.Play(proxy, NowMs);
        if (!wasRunning) player.Pause();
    }

    private void OnFrame360(byte[] pixels, int width, int height)
    {
        if (!_playsProxy)
        {
            Lens(0, pixels, width, height);
            return;
        }
        // Both lenses side by side.
        var half = width / 2;
        foreach (var lens in new[] { 0, 1 })
        {
            if (_split.Length != half * height * 4) _split = new byte[half * height * 4];
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, (y * width + lens * half) * 4, _split, y * half * 4, half * 4);
            View.SetLens(lens, _split, half, height);
        }
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
        ContentWidth = View.Width;
        ContentHeight = View.Height;
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
        if (MomentChoice.Kind != MomentKind.Person || DateTime.UtcNow - _lookedAt < TimeSpan.FromSeconds(4)) return;
        if (PersonDirection(MomentChoice.PersonIds) is not { } target) return;
        var dyaw = ((target.Yaw - View.Yaw) % 360 + 540) % 360 - 180;
        var dpitch = target.Pitch - View.Pitch;
        if (Math.Abs(dyaw) < 2 && Math.Abs(dpitch) < 2) return;
        View.Look(View.Yaw + dyaw * 0.35, View.Pitch + dpitch * 0.35, View.Fov);
        Render360();
    }

    /// <summary>Where on the sphere one of these people is now (the center of their box), from the stored paths.</summary>
    private (double Yaw, double Pitch)? PersonDirection(IReadOnlyList<long> personIds)
    {
        if (_pathsOf != Current.Item.Id) LoadPaths();
        var now = (int)NowMs;
        var interval = People.PeopleAnalyzer.FrameIntervalMs(Current.Item.DurationMs ?? 0);
        foreach (var (track, path) in _paths)
        {
            var who = _detections.FirstOrDefault(d => d.Track == track && d.PersonId is { } p && personIds.Contains(p));
            if (who == null || Where(path, now, Math.Max(interval, 2000)) is not { } b) continue;
            return Fisheye360.FromEquirect(b.X + b.W / 2, b.Y + b.H / 2);
        }
        return null;
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
                return ImageCodec.DecodeForDisplay(await Cloud.MediaFiles.LocalPathAsync(entry.Item, CancellationToken.None), 5760, 2880);
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
