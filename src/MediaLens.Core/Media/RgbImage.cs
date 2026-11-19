using ImageMagick;
using ImageMagick.Formats;

namespace MediaLens.Media;

/// <summary>An upright 8-bit RGB image in memory (row major, 3 bytes per pixel): the input of the detectors.</summary>
public sealed class RgbImage
{
    public byte[] Data { get; }
    public int Width { get; }
    public int Height { get; }

    public RgbImage(byte[] data, int width, int height)
    {
        if (data.Length != width * height * 3) throw new ArgumentException("Size mismatch");
        Data = data;
        Width = width;
        Height = height;
    }

    /// <summary>Decoded upright, the longest side at most maxSize (faces in a group photo need the resolution).</summary>
    public static RgbImage Load(string path, int maxSize)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        settings.SetDefines(new JpegReadDefines { Size = new MagickGeometry((uint)maxSize, (uint)maxSize) });
        using var image = new MagickImage(path, settings);
        image.AutoOrient();
        if (image.Width > maxSize || image.Height > maxSize)
            image.Resize(new MagickGeometry((uint)maxSize, (uint)maxSize));
        image.Alpha(AlphaOption.Remove);
        return new RgbImage(image.ToByteArray(MagickFormat.Rgb), (int)image.Width, (int)image.Height);
    }

    /// <summary>Bilinear sample of channel c at (x, y); outside the image the border pixel.</summary>
    public float Sample(float x, float y, int c)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        var a = Data[(y0 * Width + x0) * 3 + c];
        var b = Data[(y0 * Width + x1) * 3 + c];
        var d = Data[(y1 * Width + x0) * 3 + c];
        var e = Data[(y1 * Width + x1) * 3 + c];
        return (a * (1 - fx) + b * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy;
    }

    /// <summary>The region (pixels, may reach outside) resized to width x height, bilinear.</summary>
    public RgbImage Crop(float x, float y, float w, float h, int width, int height)
    {
        var data = new byte[width * height * 3];
        var sx = w / width;
        var sy = h / height;
        for (var j = 0; j < height; j++)
        for (var i = 0; i < width; i++)
        for (var c = 0; c < 3; c++)
            data[(j * width + i) * 3 + c] = (byte)Math.Clamp(Sample(x + (i + 0.5f) * sx - 0.5f, y + (j + 0.5f) * sy - 0.5f, c) + 0.5f, 0, 255);
        return new RgbImage(data, width, height);
    }

    /// <summary>
    /// Output pixel (u, v) takes the source at (a*u + b*v + tx, c*u + d*v + ty): the inverse of an alignment
    /// transform, used to cut out a face upright and at the template scale.
    /// </summary>
    public RgbImage Warp(int width, int height, float a, float b, float tx, float c, float d, float ty)
    {
        var data = new byte[width * height * 3];
        for (var v = 0; v < height; v++)
        for (var u = 0; u < width; u++)
        {
            var x = a * u + b * v + tx;
            var y = c * u + d * v + ty;
            for (var ch = 0; ch < 3; ch++)
                data[(v * width + u) * 3 + ch] = (byte)Math.Clamp(Sample(x, y, ch) + 0.5f, 0, 255);
        }
        return new RgbImage(data, width, height);
    }

    /// <summary>
    /// Scaled to fit size x size, placed top-left on a square of the fill value (letterbox); returns the scale.
    /// </summary>
    public (RgbImage Image, float Scale) Letterbox(int size, byte fill)
    {
        var scale = Math.Min((float)size / Width, (float)size / Height);
        var w = Math.Max(1, (int)Math.Round(Width * scale));
        var h = Math.Max(1, (int)Math.Round(Height * scale));
        var resized = Crop(0, 0, Width, Height, w, h);
        var data = new byte[size * size * 3];
        Array.Fill(data, fill);
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(resized.Data, y * w * 3, data, y * size * 3, w * 3);
        return (new RgbImage(data, size, size), scale);
    }

    public void SaveJpeg(string path, int quality = 80)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var settings = new PixelReadSettings((uint)Width, (uint)Height, StorageType.Char, PixelMapping.RGB);
        using var image = new MagickImage();
        image.ReadPixels(Data, settings);
        image.Quality = (uint)quality;
        var tmp = Path.ChangeExtension(path, Guid.NewGuid().ToString("N") + ".jpg");
        image.Write(tmp, MagickFormat.Jpeg);
        File.Move(tmp, path, true);
    }
}
