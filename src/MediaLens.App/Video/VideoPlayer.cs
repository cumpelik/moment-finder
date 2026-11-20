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
        _player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => Ended?.Invoke());
    }

    public bool IsPlaying => _player.IsPlaying;
    public long TimeMs => _player.Time;
    public long LengthMs => _player.Length;

    public void Play(string path, long startMs)
    {
        var old = _media;
        _media = new VlcMedia(_libVlc!, path, FromType.FromPath);
        if (startMs > 0)
            _media.AddOption(":start-time=" + (startMs / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _player.Play(_media);
        old?.Dispose();
    }

    public void TogglePause()
    {
        if (_player.State == VLCState.Ended && _media != null)
        {
            _player.Stop();
            _player.Play(_media);
        }
        else
        {
            _player.Pause();
        }
    }

    private volatile bool _pauseOnFrame;

    /// <summary>Pauses now, or on the first frame of a video that is just starting (not before, the picture would stay black).</summary>
    public void Pause()
    {
        if (_player.IsPlaying && Frame != null) _player.SetPause(true);
        else _pauseOnFrame = true;
    }

    public void Seek(long ms)
    {
        if (_player.IsSeekable) _player.Time = Math.Max(0, ms);
    }

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 100);
    }

    /// <summary>Synchronous, so a following Play cannot be overtaken (events are posted, so this cannot deadlock).</summary>
    public void Stop() => _player.Stop();

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
            _frameDirty = true;
            if (_uiUpdatePosted) return;
            _uiUpdatePosted = true;
        }
        Dispatcher.UIThread.Post(PushFrame, DispatcherPriority.Render);
    }

    private void PushFrame()
    {
        lock (_frameLock)
        {
            _uiUpdatePosted = false;
            if (!_frameDirty || _width == 0) return;
            _frameDirty = false;
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
        FrameUpdated?.Invoke();
        if (_pauseOnFrame)
        {
            _pauseOnFrame = false;
            ThreadPool.QueueUserWorkItem(_ => _player.SetPause(true));
        }
    }

    public void Dispose()
    {
        var player = _player;
        var media = _media;
        // libVLC must stop before the buffer goes away; in the background so closing the window is instant.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            player.Stop();
            player.Dispose();
            media?.Dispose();
        });
    }
}
