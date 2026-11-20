using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MediaLens.Media;

namespace MediaLens.App.Video;

/// <summary>
/// The look around in a 360 photo or video: a perspective view rendered from the two lens images. The lookup of
/// every view pixel (which lens pixels, how much of each across the seam) is computed when the view direction or
/// size changes; a new video frame then costs only the gather.
/// </summary>
public sealed class View360
{
    private readonly uint[]?[] _lens = new uint[2][];
    private readonly int[] _lensW = new int[2];
    private readonly int[] _lensH = new int[2];
    private int[] _idx0 = [];
    private int[] _idx1 = [];
    private byte[] _weight = [];
    private int[] _pixels = [];
    private bool _dirty = true;

    public int Width { get; }
    public int Height { get; }
    public double Yaw { get; private set; }
    public double Pitch { get; private set; } = Fisheye360.DefaultPitch;
    public double Fov { get; private set; } = Fisheye360.DefaultFov;
    public WriteableBitmap Output { get; }

    public View360(int width, int height)
    {
        Width = width;
        Height = height;
        Output = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    public bool HasPicture => _lens[0] != null;

    /// <summary>The second lens is not there yet (being prepared): its half of the sphere stays dark.</summary>
    public void ClearLens(int lens) => _lens[lens] = null;

    /// <summary>Where the view looks; the pitch stops at straight up or down, the zoom between 30 and 140 degrees.</summary>
    public void Look(double yaw, double pitch, double fov)
    {
        yaw = (yaw % 360 + 540) % 360 - 180;
        pitch = Math.Clamp(pitch, -89, 89);
        fov = Math.Clamp(fov, 30, 140);
        if (yaw == Yaw && pitch == Pitch && fov == Fov) return;
        (Yaw, Pitch, Fov) = (yaw, pitch, fov);
        _dirty = true;
    }

    /// <summary>New pixels of a lens (BGRA, width x height), copied.</summary>
    public void SetLens(int lens, byte[] bgra, int width, int height)
    {
        var size = width * height;
        if (_lens[lens] is not { } buffer || buffer.Length != size)
        {
            _lens[lens] = buffer = new uint[size];
            _dirty = true;
        }
        if (_lensW[lens] != width || _lensH[lens] != height) _dirty = true;
        _lensW[lens] = width;
        _lensH[lens] = height;
        Buffer.BlockCopy(bgra, 0, buffer, 0, size * 4);
    }

    /// <summary>The view into Output; false until both lenses have a picture.</summary>
    public bool Render()
    {
        if (_lens[0] is not { } a) return false;
        if (_dirty) BuildMap();
        var b = _lens[1];
        var pixels = _pixels;
        Parallel.For(0, Height, j =>
        {
            for (var i = j * Width; i < (j + 1) * Width; i++)
            {
                var w = _weight[i];
                if (w == 255) pixels[i] = (int)a[_idx0[i]];
                else if (b == null) pixels[i] = w > 127 ? (int)a[_idx0[i]] : unchecked((int)0xFF101010);
                else if (w == 0) pixels[i] = (int)b[_idx1[i]];
                else pixels[i] = (int)Blend(a[_idx0[i]], b[_idx1[i]], w);
            }
        });
        using var fb = Output.Lock();
        if (fb.RowBytes == Width * 4) Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        else
            for (var y = 0; y < Height; y++)
                Marshal.Copy(pixels, y * Width, fb.Address + y * fb.RowBytes, Width);
        return true;
    }

    private static uint Blend(uint p, uint q, int w)
    {
        uint r = 0xFF000000;
        for (var shift = 0; shift < 24; shift += 8)
        {
            var c = (((p >> shift) & 0xFF) * (uint)w + ((q >> shift) & 0xFF) * (uint)(255 - w)) / 255;
            r |= c << shift;
        }
        return r;
    }

    private void BuildMap()
    {
        var n = Width * Height;
        if (_idx0.Length != n)
        {
            _idx0 = new int[n];
            _idx1 = new int[n];
            _weight = new byte[n];
            _pixels = new int[n];
        }
        var (w0, h0) = (_lensW[0], _lensH[0]);
        // Without the second lens yet: its lookup is made with the first lens's size and rebuilt when it comes.
        var (w1, h1) = _lensW[1] > 0 ? (_lensW[1], _lensH[1]) : (w0, h0);
        Parallel.For(0, Height, j =>
        {
            for (var i = 0; i < Width; i++)
            {
                var (x, y, z) = Fisheye360.ViewRay(Yaw, Pitch, Fov, Width, Height, i + 0.5, j + 0.5);
                var k = j * Width + i;
                var w = Fisheye360.FrontWeight(z);
                _weight[k] = (byte)Math.Round(w * 255);
                var (u0, v0) = Fisheye360.InLens(0, x, y, z);
                var (u1, v1) = Fisheye360.InLens(1, x, y, z);
                _idx0[k] = Math.Clamp((int)(v0 * h0), 0, h0 - 1) * w0 + Math.Clamp((int)(u0 * w0), 0, w0 - 1);
                _idx1[k] = Math.Clamp((int)(v1 * h1), 0, h1 - 1) * w1 + Math.Clamp((int)(u1 * w1), 0, w1 - 1);
            }
        });
        _dirty = false;
    }
}
