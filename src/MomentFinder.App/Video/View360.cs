using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MomentFinder.Media;

namespace MomentFinder.App.Video;

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

    /// <summary>The camera's lenses (its own calibration when the file has one).</summary>
    public LensModel Model
    {
        get => _model;
        set
        {
            if (value == _model) return;
            _model = value;
            _dirty = true;
        }
    }

    private LensModel _model = LensModel.Default;

    public View360(int width, int height)
    {
        Width = width;
        Height = height;
        Output = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    public bool HasPicture => _lens[0] != null || _equirect != null;

    private uint[]? _equirect;
    private int _eqW, _eqH;
    private float[] _grid = [];

    /// <summary>A leveled equirectangular frame (the playing copy of a 360 video), BGRA, copied.</summary>
    public void SetEquirect(byte[] bgra, int width, int height)
    {
        if (_equirect == null || _equirect.Length != width * height) _equirect = new uint[width * height];
        (_eqW, _eqH) = (width, height);
        Buffer.BlockCopy(bgra, 0, _equirect, 0, width * height * 4);
    }

    /// <summary>Back to the two lens images (a photo, or a video without its playing copy).</summary>
    public void UseLenses() => _equirect = null;

    /// <summary>
    /// The view from an equirectangular frame: where each view pixel is on the map, computed on a grid of 8 pixels
    /// (cheap enough for every frame and every mouse move) and interpolated.
    /// </summary>
    private void RenderEquirect(uint[] source)
    {
        const int cell = 8;
        int gw = Width / cell + 2, gh = Height / cell + 2;
        if (_grid.Length != gw * gh * 2) _grid = new float[gw * gh * 2];
        if (_pixels.Length != Width * Height) _pixels = new int[Width * Height];
        var grid = _grid;
        Parallel.For(0, gh, gj =>
        {
            for (var gi = 0; gi < gw; gi++)
            {
                var (x, y, z) = Fisheye360.ViewRay(Yaw, Pitch, Fov, Width, Height, gi * cell, gj * cell);
                var (u, v) = Fisheye360.ToEquirect(x, y, z);
                grid[(gj * gw + gi) * 2] = (float)(u * _eqW);
                grid[(gj * gw + gi) * 2 + 1] = (float)(v * _eqH);
            }
        });
        var pixels = _pixels;
        int w = _eqW, h = _eqH;
        Parallel.For(0, Height, j =>
        {
            var gj = j / cell;
            var fy = (j % cell) / (float)cell;
            for (var i = 0; i < Width; i++)
            {
                var gi = i / cell;
                var fx = (i % cell) / (float)cell;
                var a = (gj * gw + gi) * 2;
                var b = a + gw * 2;
                float u00 = grid[a], u10 = grid[a + 2], u01 = grid[b], u11 = grid[b + 2];
                // A cell across the seam behind the camera: its corners are brought to the same side.
                if (Math.Abs(u10 - u00) > w / 2f || Math.Abs(u01 - u00) > w / 2f || Math.Abs(u11 - u00) > w / 2f)
                {
                    if (u00 < w / 2f) u00 += w;
                    if (u10 < w / 2f) u10 += w;
                    if (u01 < w / 2f) u01 += w;
                    if (u11 < w / 2f) u11 += w;
                }
                var u = (u00 * (1 - fx) + u10 * fx) * (1 - fy) + (u01 * (1 - fx) + u11 * fx) * fy;
                var v = (grid[a + 1] * (1 - fx) + grid[a + 3] * fx) * (1 - fy) + (grid[b + 1] * (1 - fx) + grid[b + 3] * fx) * fy;
                var sx = (int)u % w;
                var sy = Math.Clamp((int)v, 0, h - 1);
                pixels[j * Width + i] = (int)source[sy * w + sx];
            }
        });
        using var fb = Output.Lock();
        if (fb.RowBytes == Width * 4) Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        else
            for (var y = 0; y < Height; y++)
                Marshal.Copy(pixels, y * Width, fb.Address + y * fb.RowBytes, Width);
    }

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
        if (_equirect is { } equirect)
        {
            RenderEquirect(equirect);
            return true;
        }
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
                var (u0, v0) = Fisheye360.InLens(0, x, y, z, _model);
                var (u1, v1) = Fisheye360.InLens(1, x, y, z, _model);
                _idx0[k] = Math.Clamp((int)(v0 * h0), 0, h0 - 1) * w0 + Math.Clamp((int)(u0 * w0), 0, w0 - 1);
                _idx1[k] = Math.Clamp((int)(v1 * h1), 0, h1 - 1) * w1 + Math.Clamp((int)(u1 * w1), 0, w1 - 1);
            }
        });
        _dirty = false;
    }
}
