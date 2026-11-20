using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaLens.Media;

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
            Environment.GetEnvironmentVariable("MEDIALENS_FFMPEG"),
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
    /// </summary>
    public static async Task<string[]> ExtractFramesAsync(string file, double intervalSeconds, int maxFrames, string outDir, int maxSize,
        CancellationToken ct = default, bool keyframesOnly = true)
    {
        if (FfmpegPath.Value is not { } ffmpeg) return [];
        Directory.CreateDirectory(outDir);
        var fps = (1.0 / intervalSeconds).ToString("0.######", CultureInfo.InvariantCulture);
        await RunAsync(ffmpeg,
        [
            "-v", "error", "-y", .. (keyframesOnly ? new[] { "-skip_frame", "nokey" } : []), "-i", file,
            "-vf", $"fps=fps={fps}:round=up,scale={maxSize}:{maxSize}:force_original_aspect_ratio=decrease",
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

    /// <summary>
    /// A light copy of a 360 video for playing: both lenses side by side at 1536 px each in one H.264 stream (about a
    /// sixth of the original's size). Two 4K HEVC streams do not decode in real time without a graphics card; this
    /// one plays anywhere. Made with hardware decoding where there is some.
    /// </summary>
    /// <param name="progress">How far it is, 0..1 (from ffmpeg's -progress output against the length).</param>
    public static async Task<bool> Make360ProxyAsync(string file, string outFile, double durationSeconds, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (FfmpegPath.Value is not { } ffmpeg) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        var tmp = outFile + ".part.mp4";
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-v", "error", "-nostats", "-progress", "pipe:1", "-y", "-hwaccel", "auto", "-i", file,
                     "-filter_complex", "[0:v:0]scale=1536:1536[a];[0:v:1]scale=1536:1536[b];[a][b]hstack",
                     "-map", "0:a?", "-c:v", "libx264", "-preset", "veryfast", "-crf", "27", "-g", "25", "-c:a", "copy", tmp,
                 })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start ffmpeg");
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        p.StandardInput.Close();
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                // out_time_us=12345678 (microseconds written so far)
                if (durationSeconds > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), out var us))
                    progress?.Report(Math.Clamp(us / 1e6 / durationSeconds, 0, 1));
            }
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            try { await p.WaitForExitAsync(CancellationToken.None); File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        await stderr;
        if (p.ExitCode != 0 || !File.Exists(tmp)) return false;
        File.Move(tmp, outFile, true);
        return true;
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
