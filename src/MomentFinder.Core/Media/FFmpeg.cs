using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MomentFinder.Media;

/// <summary>
/// ffmpeg / ffprobe as external processes. The installers ship them next to the application (tools/);
/// a development build falls back to the PATH.
/// </summary>
public static class FFmpeg
{
    private static readonly Lazy<string?> FfmpegPath = new(() => Locate("ffmpeg"));
    private static readonly Lazy<string?> FfprobePath = new(() => Locate("ffprobe"));

    public static bool IsAvailable => FfmpegPath.Value != null && FfprobePath.Value != null;

    private static string? Locate(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var dirs = new List<string?>
        {
            Environment.GetEnvironmentVariable("MOMENTFINDER_FFMPEG"),
            Path.Combine(AppContext.BaseDirectory, "tools"),
            AppContext.BaseDirectory,
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        if (OperatingSystem.IsMacOS())
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var path = Path.Combine(dir, exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public sealed record ProbeResult(long? DurationMs, int? Width, int? Height, DateTime? CreationTime, double? Lat, double? Lon, string? Camera);

    public static async Task<ProbeResult?> ProbeAsync(string file, CancellationToken ct = default)
    {
        if (FfprobePath.Value is not { } ffprobe) return null;
        var (code, stdout, _) = await RunAsync(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", file], ct);
        if (code != 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            long? duration = null;
            DateTime? created = null;
            string? location = null, make = null, model = null;
            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var secs))
                    duration = (long)(secs * 1000);
                if (format.TryGetProperty("tags", out var tags))
                {
                    foreach (var tag in tags.EnumerateObject())
                    {
                        var value = tag.Value.GetString();
                        switch (tag.Name.ToLowerInvariant())
                        {
                            case "com.apple.quicktime.creationdate":
                                // Local time with offset (iPhone) - the wall clock is what EXIF photos have too.
                                if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                                    created = dto.DateTime;
                                break;
                            case "creation_time" when created == null:
                                // UTC; shown as the local time of this computer, which is right for most home videos.
                                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                                    created = utc.ToLocalTime();
                                break;
                            case "com.apple.quicktime.location.iso6709":
                            case "location":
                                location ??= value;
                                break;
                            case "com.apple.quicktime.make":
                                make = value;
                                break;
                            case "com.apple.quicktime.model":
                                model = value;
                                break;
                        }
                    }
                }
            }

            int? width = null, height = null;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    if (s.GetProperty("codec_type").GetString() != "video") continue;
                    if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1)
                        continue;
                    width = s.TryGetProperty("width", out var w) ? w.GetInt32() : null;
                    height = s.TryGetProperty("height", out var h) ? h.GetInt32() : null;
                    if (IsRotatedSideways(s) && width != null)
                        (width, height) = (height, width);
                    break;
                }
            }

            var (lat, lon) = ParseIso6709(location);
            var camera = string.Join(' ', new[] { make, model }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new ProbeResult(duration, width, height, created, lat, lon, camera.Length > 0 ? camera : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsRotatedSideways(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var r) &&
            int.TryParse(r.GetString(), out var deg))
            return Math.Abs(deg) % 180 == 90;
        if (stream.TryGetProperty("side_data_list", out var sdl))
        {
            foreach (var sd in sdl.EnumerateArray())
            {
                if (sd.TryGetProperty("rotation", out var rot) && rot.TryGetDouble(out var d))
                    return Math.Abs((int)d) % 180 == 90;
            }
        }
        return false;
    }

    /// <summary>"+50.0755+014.4378+235.000/" -> (50.0755, 14.4378)</summary>
    internal static (double?, double?) ParseIso6709(string? s)
    {
        if (string.IsNullOrEmpty(s)) return (null, null);
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in s)
        {
            if ((ch == '+' || ch == '-') && current.Length > 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            if (ch == '/') break;
            current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        if (parts.Count >= 2 &&
            double.TryParse(parts[0], CultureInfo.InvariantCulture, out var lat) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var lon))
            return (lat, lon);
        return (null, null);
    }

    /// <summary>One frame scaled to fit maxSize, as JPEG. ffmpeg applies the rotation of phone videos itself.</summary>
    public static async Task<bool> ExtractFrameAsync(string file, double atSeconds, string outJpeg, int maxSize, CancellationToken ct = default)
    {
        if (FfmpegPath.Value is not { } ffmpeg) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(outJpeg)!);
        var (code, _, _) = await RunAsync(ffmpeg,
        [
            "-v", "error", "-y", "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", file,
            "-frames:v", "1", "-vf", $"scale={maxSize}:{maxSize}:force_original_aspect_ratio=decrease", "-q:v", "3", outJpeg,
        ], ct);
        return code == 0 && File.Exists(outJpeg);
    }

    /// <summary>
    /// Frames every intervalSeconds, named 0001.jpg, 0002.jpg... Frame n is at (n-1) * intervalSeconds (round=up:
    /// the default takes the frame half an interval later, measured on a video numbering its frames).
    /// keyframesOnly decodes only keyframes: fast even for long videos, but the times are approximate.
    /// rotation: the frames turned clockwise by 90, 180 or 270 degrees (a video the user turned, see QuarterTurn).
    /// </summary>
    public static async Task<string[]> ExtractFramesAsync(string file, double intervalSeconds, int maxFrames, string outDir, int maxSize,
        CancellationToken ct = default, bool keyframesOnly = true, int rotation = 0)
    {
        if (FfmpegPath.Value is not { } ffmpeg) return [];
        Directory.CreateDirectory(outDir);
        var fps = (1.0 / intervalSeconds).ToString("0.######", CultureInfo.InvariantCulture);
        var turned = rotation switch { 90 => ",transpose=1", 180 => ",hflip,vflip", 270 => ",transpose=2", _ => "" };
        await RunAsync(ffmpeg,
        [
            "-v", "error", "-y", .. (keyframesOnly ? new[] { "-skip_frame", "nokey" } : []), "-i", file,
            "-vf", $"fps=fps={fps}:round=up,scale={maxSize}:{maxSize}:force_original_aspect_ratio=decrease{turned}",
            "-frames:v", maxFrames.ToString(CultureInfo.InvariantCulture), "-q:v", "3",
            Path.Combine(outDir, "%04d.jpg"),
        ], ct);
        return Directory.GetFiles(outDir, "*.jpg").Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>A moment of a 360 video: the two lens images at that time.</summary>
    public sealed record DualFrame(int TimeMs, string Lens0, string Lens1);

    /// <summary>
    /// The two lenses of a dual fisheye video (Insta360: two video streams) at about intervalSeconds apart, keyframes
    /// only (decoding two 4K streams fully is several times slower than real time). Times are the keyframes' own.
    /// </summary>
    public static async Task<List<DualFrame>> ExtractDualFramesAsync(string file, double intervalSeconds, int maxFrames, string outDir, int size,
        CancellationToken ct = default)
    {
        if (FfmpegPath.Value is not { } ffmpeg || FfprobePath.Value is not { } ffprobe) return [];
        Directory.CreateDirectory(outDir);
        // The keyframes and the same choice as the select filter below: at least an interval after the last one taken.
        var (_, packets, _) = await RunAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", file], ct);
        var keyframes = packets.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(','))
            .Where(p => p.Length >= 2 && p[1].Contains('K') && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(p => double.Parse(p[0], CultureInfo.InvariantCulture)).Order().ToList();
        var times = new List<double>();
        foreach (var t in keyframes)
            if (times.Count == 0 || t - times[^1] >= intervalSeconds - 0.01)
                times.Add(t);
        if (times.Count > maxFrames) times = times.Take(maxFrames).ToList();
        if (times.Count == 0) return [];
        var interval = (intervalSeconds - 0.01).ToString("0.###", CultureInfo.InvariantCulture);
        var select = $"select='isnan(prev_selected_t)+gte(t-prev_selected_t\\,{interval})',scale={size}:{size}";
        await RunAsync(ffmpeg,
        [
            "-v", "error", "-y", "-skip_frame", "nokey", "-i", file,
            "-filter_complex", $"[0:v:0]{select}[a];[0:v:1]{select}[b]",
            "-map", "[a]", "-fps_mode", "passthrough", "-frames:v", times.Count.ToString(CultureInfo.InvariantCulture), "-q:v", "3",
            Path.Combine(outDir, "a_%04d.jpg"),
            "-map", "[b]", "-fps_mode", "passthrough", "-frames:v", times.Count.ToString(CultureInfo.InvariantCulture), "-q:v", "3",
            Path.Combine(outDir, "b_%04d.jpg"),
        ], ct);
        var result = new List<DualFrame>();
        for (var i = 0; i < times.Count; i++)
        {
            var a = Path.Combine(outDir, $"a_{i + 1:D4}.jpg");
            var b = Path.Combine(outDir, $"b_{i + 1:D4}.jpg");
            if (File.Exists(a) && File.Exists(b)) result.Add(new DualFrame((int)Math.Round(times[i] * 1000), a, b));
        }
        return result;
    }

    /// <summary>Frames a second of the first video stream (25 when it cannot be read).</summary>
    public static async Task<double> FrameRateAsync(string file, CancellationToken ct = default)
    {
        if (FfprobePath.Value is not { } ffprobe) return 25;
        var (_, text, _) = await RunAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", file], ct);
        var parts = text.Trim().Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && n > 0 ? n / d : 25;
    }

    /// <summary>The H.264 encoders of graphics cards, tried in this order before the CPU one.</summary>
    private static readonly string[] HardwareEncoders = ["h264_nvenc", "h264_qsv", "h264_amf"];
    private static string? _encoder;

    /// <summary>The fastest H.264 encoder that works here (a short test encode of each, once).</summary>
    private static async Task<string> PickEncoderAsync(string ffmpeg, CancellationToken ct)
    {
        if (_encoder != null) return _encoder;
        foreach (var candidate in HardwareEncoders)
        {
            var (code, _, _) = await RunAsync(ffmpeg,
                ["-v", "error", "-f", "lavfi", "-i", "color=c=black:s=1920x960:r=25", "-frames:v", "5", "-c:v", candidate, "-f", "null", "-"], ct);
            if (code == 0) return _encoder = candidate;
        }
        return _encoder = "libx264";
    }

    /// <summary>Which encoder the 360 copies use ("h264_nvenc": the graphics card; "libx264": the processor).</summary>
    public static string? Encoder360 => _encoder;

    /// <summary>
    /// A light copy of a 360 video for playing: one leveled equirectangular picture (2880 x 1440) in one H.264
    /// stream. Two 4K HEVC streams do not decode in real time everywhere; this one plays anywhere, and its horizon is
    /// level whatever way the camera was held. One ffmpeg decodes both lenses (with the graphics card where there
    /// is one), level turns every frame, a second ffmpeg encodes it with the sound of the original.
    /// </summary>
    /// <param name="level">Turns a frame of both lenses side by side (BGRA) into the output picture (BGRA).</param>
    /// <param name="progress">How far it is, 0..1.</param>
    public static async Task<bool> Make360ProxyAsync(string file, string outFile, int frameCount, double fps,
        Action<int, byte[], byte[]> level, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (FfmpegPath.Value is not { } ffmpeg) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        var tmp = outFile + ".part.mp4";
        var encoder = await PickEncoderAsync(ffmpeg, ct);
        var rate = fps.ToString("0.###", CultureInfo.InvariantCulture);
        using var decode = Start(ffmpeg,
        [
            "-v", "error", "-hwaccel", "auto", "-i", file,
            "-filter_complex", $"[0:v:0]scale={Proxy360Lens}:{Proxy360Lens}[a];[0:v:1]scale={Proxy360Lens}:{Proxy360Lens}[b];[a][b]hstack",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-",
        ]);
        string[] quality = encoder == "libx264" ? ["-preset", "veryfast", "-crf", "24"] : ["-b:v", "14M", "-maxrate", "20M"];
        using var encode = Start(ffmpeg,
        [
            "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Proxy360Width}x{Proxy360Height}", "-r", rate, "-i", "-",
            "-i", file, "-map", "0:v", "-map", "1:a?", "-c:v", encoder, .. quality, "-pix_fmt", "yuv420p", "-g", "30", "-c:a", "copy",
            "-shortest", tmp,
        ]);
        var decodeErrors = decode.StandardError.ReadToEndAsync(CancellationToken.None);
        var encodeErrors = encode.StandardError.ReadToEndAsync(CancellationToken.None);
        var input = new byte[Proxy360Lens * 2 * Proxy360Lens * 4];
        var output = new byte[Proxy360Width * Proxy360Height * 4];
        try
        {
            var source = decode.StandardOutput.BaseStream;
            var sink = encode.StandardInput.BaseStream;
            for (var frame = 0; ; frame++)
            {
                var read = 0;
                while (read < input.Length)
                {
                    var n = await source.ReadAsync(input.AsMemory(read), ct);
                    if (n == 0) break;
                    read += n;
                }
                if (read < input.Length) break;
                level(frame, input, output);
                await sink.WriteAsync(output, ct);
                if (frameCount > 0) progress?.Report(Math.Min(1, (frame + 1) / (double)frameCount));
            }
            encode.StandardInput.Close();
            await encode.WaitForExitAsync(ct);
            await decode.WaitForExitAsync(ct);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            foreach (var p in new[] { decode, encode })
                try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
            try { await encode.WaitForExitAsync(CancellationToken.None); File.Delete(tmp); } catch (IOException) { }
            if (e is OperationCanceledException) throw;
            return false;
        }
        await decodeErrors;
        await encodeErrors;
        if (encode.ExitCode != 0 || !File.Exists(tmp)) return false;
        File.Move(tmp, outFile, true);
        return true;
    }

    /// <summary>The playing copy: each lens decoded at this size, the leveled picture at this size.</summary>
    public const int Proxy360Lens = 1440;
    public const int Proxy360Width = 2880;
    public const int Proxy360Height = 1440;

    private static Process Start(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        return p;
    }

    /// <summary>The two lens images of a dual fisheye video at a moment (for a thumbnail).</summary>
    public static async Task<DualFrame?> ExtractDualFrameAsync(string file, double atSeconds, string outDir, int size, CancellationToken ct = default)
    {
        if (FfmpegPath.Value is not { } ffmpeg) return null;
        Directory.CreateDirectory(outDir);
        var a = Path.Combine(outDir, "a.jpg");
        var b = Path.Combine(outDir, "b.jpg");
        await RunAsync(ffmpeg,
        [
            "-v", "error", "-y", "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", file,
            "-filter_complex", $"[0:v:0]scale={size}:{size}[a];[0:v:1]scale={size}:{size}[b]",
            "-map", "[a]", "-frames:v", "1", "-q:v", "3", a,
            "-map", "[b]", "-frames:v", "1", "-q:v", "3", b,
        ], ct);
        return File.Exists(a) && File.Exists(b) ? new DualFrame((int)(atSeconds * 1000), a, b) : null;
    }

    private static async Task<(int Code, string StdOut, string StdErr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Cannot start {exe}");
        try
        {
            // Background work (thumbnails, frames for the analysis): the user's clicks come first.
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already finished, or not allowed here: it just runs at the normal priority.
        }
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        return (p.ExitCode, await stdout, await stderr);
    }
}
