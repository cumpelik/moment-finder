using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace MediaLens.App.Video;

/// <summary>
/// libVLC decodes (any codec, hardware accelerated, with audio) and hands frames to a WriteableBitmap, so the
/// video is an ordinary Avalonia image: overlays work, no native window, same code on Windows, Linux and macOS.
/// Frames larger than MaxWidth x MaxHeight are scaled down by libVLC (4K would only cost copying).
/// Commands (play, stop, pause, seek) run one after another on a background queue: libVLC may block for a
/// while (stopping, switching videos) and the window must stay responsive.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    private const int MaxWidth = 2560;
    private const int MaxHeight = 1440;

    private static LibVLC? _libVlc;
    private static string? _initError;

    private readonly MediaPlayer _player;
    private readonly object _frameLock = new();
    // Kept in fields: libVLC calls them from its threads, the GC must not collect the delegates.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private IntPtr _buffer;
    private int _width, _height;
    private byte[] _latest = [];
    private bool _frameDirty;
    private bool _uiUpdatePosted;
    private VlcMedia? _media;

    /// <summary>The current frame; a new bitmap when the video size changes.</summary>
    public WriteableBitmap? Frame { get; private set; }

    /// <summary>On the UI thread, after Frame got new pixels (the Image has to be invalidated).</summary>
    public event Action? FrameUpdated;
    /// <summary>On the UI thread.</summary>
    public event Action? Ended;
    /// <summary>On the UI thread: the first frame of the video just started is on screen.</summary>
    public event Action? Started;

    private readonly object _queueLock = new();
    private Task _queue = Task.CompletedTask;
    /// <summary>A video was asked to play and has not shown a frame yet.</summary>
    private volatile bool _starting;
    /// <summary>Which Play a frame belongs to: the last frames of the previous video are not the new one starting.</summary>
    private int _requested;
    private volatile int _active;
    private int _frameOf;

    /// <summary>Runs a libVLC command after the ones before it, off the UI thread.</summary>
    private void Run(Action command)
    {
        lock (_queueLock)
            _queue = _queue.ContinueWith(_ =>
            {
                try
                {
                    command();
                }
                catch (Exception e) when (e is VLCException or ObjectDisposedException)
                {
                    // A command for a player that is going away.
                }
            }, TaskScheduler.Default);
    }

    private static readonly Lazy<Task> Loading = new(() => Task.Run(() => IsAvailable(out _)));

    /// <summary>
    /// Loads libVLC in the background. The first load scans its codec plugins: on Windows that takes seconds
    /// (and the antivirus looks at each one), so it is started with the app and never waited for on the UI thread.
    /// </summary>
    public static Task LoadAsync() => Loading.Value;

    public static bool IsLoaded => _libVlc != null || _initError != null;

    public static bool IsAvailable(out string? error)
    {
        if (_libVlc == null && _initError == null)
        {
            try
            {
                Core.Initialize();
                _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--no-osd");
            }
            catch (Exception e)
            {
                _initError = OperatingSystem.IsLinux()
                    ? "Video needs libVLC: install the package vlc (or libvlc5 and vlc-plugin-base)."
                    : "Video playback cannot start: " + e.Message;
            }
        }
        error = _initError;
        return _libVlc != null;
    }

    public VideoPlayer()
    {
        if (!IsAvailable(out var error)) throw new InvalidOperationException(error);
        _player = new MediaPlayer(_libVlc!);
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _displayCb = OnDisplay;
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, null, _displayCb);
        _player.EndReached += (_, _) =>
        {
            _ended = true;
            Dispatcher.UIThread.Post(() => Ended?.Invoke());
        };
    }

    /// <summary>Which video stream plays (a 360 video has one per lens); null the default one.</summary>
    public int? VideoTrack { get; init; }
    /// <summary>The second lens of a 360 video plays silently next to the first.</summary>
    public bool NoAudio { get; init; }
    /// <summary>Frames go to RawFrame (for the 360 view) instead of the Frame bitmap.</summary>
    public bool RawFrames { get; init; }
    /// <summary>Raw BGRA pixels of a new frame, on the UI thread; the array is reused, copy what is needed.</summary>
    public event Action<byte[], int, int>? RawFrame;
    /// <summary>A player that does what this one does (the second lens of a 360 video): play, pause, seek, speed.</summary>
    public VideoPlayer? Partner { get; set; }
    /// <summary>What the partner plays (the second lens copied into a file of its own).</summary>
    public string? PartnerPath { get; set; }

    public bool IsPlaying => _player.IsPlaying;
    /// <summary>Played to the end: libVLC reports no time any more.</summary>
    public bool IsEnded => _ended;
    public long TimeMs => _player.Time;
    public long LengthMs => _player.Length;

    private string? _path;
    /// <summary>The video played to its end: libVLC ignores a seek then, the video has to be opened again.</summary>
    private volatile bool _ended;

    public void Play(string path, long startMs)
    {
        _path = path;
        _ended = false;
        var old = _media;
        var media = _media = new VlcMedia(_libVlc!, path, FromType.FromPath);
        if (startMs > 0)
            media.AddOption(":start-time=" + (startMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (VideoTrack is { } track) media.AddOption(":video-track=" + track.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (NoAudio) media.AddOption(":no-audio");
        if (Partner != null && PartnerPath != null) Partner.Play(PartnerPath, startMs);
        _starting = true;
        _pauseOnFrame = false;
        var generation = ++_requested;
        Run(() =>
        {
            // Stopped first: frames of the previous video still on their way would count as the new one starting
            // (a wrong picture under the new boxes, and the pause meant for the new first frame hitting nothing).
            if (_player.IsPlaying || _player.State is VLCState.Paused or VLCState.Ended or VLCState.Opening or VLCState.Buffering)
                _player.Stop();
            _active = generation;
            _player.Play(media);
            _player.SetRate(_rate);
            old?.Dispose();
        });
    }

    public void TogglePause()
    {
        Partner?.TogglePause();
        var media = _media;
        Run(() =>
        {
            if (_player.State == VLCState.Ended && media != null)
            {
                _player.Stop();
                _player.Play(media);
            }
            else
            {
                _player.Pause();
            }
        });
    }

    private volatile bool _pauseOnFrame;
    private float _rate = 1;

    /// <summary>Playback speed (0.1 to 2): slow motion for looking at a technique. Kept for the next videos.</summary>
    public float Rate
    {
        get => _rate;
        set
        {
            _rate = value;
            if (Partner != null) Partner.Rate = value;
            Run(() => _player.SetRate(value));
        }
    }

    /// <summary>One frame further, while paused.</summary>
    public void NextFrame()
    {
        Partner?.NextFrame();
        Run(_player.NextFrame);
    }

    /// <summary>Pauses now, or on the first frame of a video that is just starting (not before, the picture would stay black).</summary>
    public void Pause()
    {
        Partner?.Pause();
        if (_starting) _pauseOnFrame = true;
        else Run(() => _player.SetPause(true));
    }

    public void Seek(long ms)
    {
        Partner?.Seek(ms);
        if (_ended && _path != null)
        {
            // After the end: open it again at that moment and stand there, like a seek on a paused video.
            Play(_path, Math.Max(0, ms));
            _pauseOnFrame = true;
            return;
        }
        Run(() =>
        {
            if (_player.IsSeekable) _player.Time = Math.Max(0, ms);
        });
    }

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 100);
    }

    /// <summary>In the queue, so a following Play cannot be overtaken.</summary>
    public void Stop()
    {
        Partner?.Stop();
        _starting = false;
        _pauseOnFrame = false;
        Run(_player.Stop);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var scale = Math.Min(1.0, Math.Min((double)MaxWidth / width, (double)MaxHeight / height));
        width = (uint)Math.Max(2, Math.Round(width * scale));
        height = (uint)Math.Max(2, Math.Round(height * scale));
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        pitches = width * 4;
        lines = height;
        lock (_frameLock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _width = (int)width;
            _height = (int)height;
            _buffer = Marshal.AllocHGlobal(_width * _height * 4);
            _latest = new byte[_width * _height * 4];
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_frameLock)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_frameLock)
        {
            if (_buffer == IntPtr.Zero) return;
            Marshal.Copy(_buffer, _latest, 0, _latest.Length);
            _frameOf = _active;
            _frameDirty = true;
            if (_uiUpdatePosted) return;
            _uiUpdatePosted = true;
        }
        Dispatcher.UIThread.Post(PushFrame, DispatcherPriority.Render);
    }

    private void PushFrame()
    {
        int frameOf;
        lock (_frameLock)
        {
            _uiUpdatePosted = false;
            if (!_frameDirty || _width == 0) return;
            _frameDirty = false;
            frameOf = _frameOf;
            if (RawFrames)
                RawFrame?.Invoke(_latest, _width, _height);
            else
            {
            if (Frame == null || Frame.PixelSize.Width != _width || Frame.PixelSize.Height != _height)
                Frame = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var fb = Frame.Lock();
            var rowBytes = _width * 4;
            if (fb.RowBytes == rowBytes)
                Marshal.Copy(_latest, 0, fb.Address, _latest.Length);
            else
                for (var y = 0; y < _height; y++)
                    Marshal.Copy(_latest, y * rowBytes, fb.Address + y * fb.RowBytes, rowBytes);
            }
        }
        FrameUpdated?.Invoke();
        if (frameOf != _requested) return;
        if (_starting)
        {
            _starting = false;
            // A new video may start at normal speed: the chosen one again.
            var rate = _rate;
            if (rate != 1) Run(() => _player.SetRate(rate));
            Started?.Invoke();
        }
        if (_pauseOnFrame)
        {
            _pauseOnFrame = false;
            Run(() => _player.SetPause(true));
        }
    }

    public void Dispose()
    {
        Partner?.Dispose();
        var player = _player;
        var media = _media;
        // libVLC must stop before the buffer goes away; after the queued commands, so closing the window is instant.
        Run(() =>
        {
            player.Stop();
            player.Dispose();
            media?.Dispose();
        });
    }
}
