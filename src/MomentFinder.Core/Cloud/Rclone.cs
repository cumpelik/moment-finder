using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MomentFinder.Cloud;

public sealed record RemoteFile(string Path, long Size, DateTimeOffset ModTime);

public sealed class RcloneException(string message) : Exception(message);

/// <summary>
/// rclone (MIT) does the cloud work: S3 and compatible storage, OneDrive, Google Drive and many more, and
/// the client-side encryption (its "crypt" remote: file contents and names encrypted before upload, the
/// provider sees only noise). Encrypted data can be read back with plain rclone and the password, even
/// without MomentFinder. Our remotes live in a private rclone.conf in the library folder.
/// </summary>
public sealed partial class Rclone
{
    private readonly string _configPath;
    private static readonly Lazy<string?> Exe = new(Locate);

    public Rclone(string configPath) => _configPath = configPath;

    public static bool IsAvailable => Exe.Value != null;

    private static string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        var dirs = new List<string?>
        {
            Environment.GetEnvironmentVariable("MOMENTFINDER_RCLONE"),
            Path.Combine(AppContext.BaseDirectory, "tools"),
            AppContext.BaseDirectory,
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        if (OperatingSystem.IsMacOS()) dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin"]);
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var path = Path.Combine(dir, exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public async Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct, Action<string>? onStderrLine = null)
    {
        if (Exe.Value is not { } exe) throw new RcloneException("rclone is not installed");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(_configPath);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new RcloneException("Cannot start rclone");
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync(ct) is { } line)
            {
                stderr.AppendLine(line);
                onStderrLine?.Invoke(line);
            }
        }, ct);
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        await errTask;
        var output = await stdout;
        if (p.ExitCode != 0)
        {
            var message = stderr.ToString().Split('\n').LastOrDefault(l => l.Contains("ERROR") || l.Contains("Failed"))?.Trim()
                          ?? stderr.ToString().Trim();
            throw new RcloneException(string.IsNullOrEmpty(message) ? $"rclone failed ({p.ExitCode})" : message);
        }
        return output;
    }

    /// <summary>Creates or replaces a remote; secret values are passed as given and obscured by rclone.</summary>
    public Task CreateRemoteAsync(string name, string type, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var args = new List<string> { "config", "create", name, type };
        foreach (var (k, v) in parameters)
        {
            args.Add(k);
            args.Add(v);
        }
        args.Add("--obscure");
        args.Add("--non-interactive");
        return RunAsync(args, ct);
    }

    public Task DeleteRemoteAsync(string name, CancellationToken ct) => RunAsync(["config", "delete", name], ct);

    /// <summary>Every remote of the config with its settings as stored (passwords stay obscured).</summary>
    public async Task<Dictionary<string, Dictionary<string, string>>> DumpAsync(CancellationToken ct)
    {
        if (!File.Exists(_configPath)) return [];
        using var doc = JsonDocument.Parse(await RunAsync(["config", "dump"], ct));
        var remotes = new Dictionary<string, Dictionary<string, string>>();
        foreach (var remote in doc.RootElement.EnumerateObject())
            remotes[remote.Name] = remote.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
        return remotes;
    }

    /// <summary>A config file with these remotes, as DumpAsync gave them.</summary>
    public static void WriteConfig(string path, IReadOnlyDictionary<string, Dictionary<string, string>> remotes)
    {
        var text = new StringBuilder();
        foreach (var (name, settings) in remotes)
        {
            text.Append('[').Append(name).Append("]\n");
            foreach (var (key, value) in settings) text.Append(key).Append(" = ").Append(value.ReplaceLineEndings(" ")).Append('\n');
            text.Append('\n');
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ToString());
    }

    /// <summary>Files down to a depth (1 = directly in the folder); an empty list when the folder is not there.</summary>
    public async Task<List<RemoteFile>> ListAsync(string remotePath, int depth, CancellationToken ct)
    {
        try
        {
            return Parse(await RunAsync(["lsjson", "-R", "--files-only", "--no-mimetype", "--max-depth", depth.ToString(CultureInfo.InvariantCulture), remotePath], ct));
        }
        catch (RcloneException e) when (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
    }

    public async Task<List<string>> DirsAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await RunAsync(["lsjson", "--dirs-only", "--max-depth", "1", remotePath], ct));
            return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("Path").GetString()!).ToList();
        }
        catch (RcloneException e) when (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
    }

    /// <summary>
    /// The browser sign-in of OneDrive or Google Drive (rclone's own registered app): rclone opens the
    /// browser, waits for the login and prints the token.
    /// </summary>
    public async Task<string> AuthorizeAsync(string backend, CancellationToken ct)
    {
        var output = await RunAsync(["authorize", backend], ct);
        var m = TokenBlock().Match(output);
        if (!m.Success) throw new RcloneException("The sign-in did not return a token");
        return m.Groups[1].Value.Trim();
    }

    [GeneratedRegex(@"--->\s*(\{.*?\})\s*<---", RegexOptions.Singleline)]
    private static partial Regex TokenBlock();

    /// <summary>Checks that the remote answers (and the bucket or folder exists or can be made).</summary>
    public async Task TestAsync(string remotePath, CancellationToken ct)
    {
        await RunAsync(["mkdir", remotePath], ct);
        await RunAsync(["lsjson", "--max-depth", "1", remotePath], ct);
    }

    public async Task<List<RemoteFile>> ListAsync(string remotePath, CancellationToken ct)
    {
        return Parse(await RunAsync(["lsjson", "-R", "--files-only", "--no-mimetype", remotePath], ct));
    }

    private static List<RemoteFile> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var files = new List<RemoteFile>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            files.Add(new RemoteFile(
                e.GetProperty("Path").GetString()!,
                e.GetProperty("Size").GetInt64(),
                DateTimeOffset.Parse(e.GetProperty("ModTime").GetString()!, CultureInfo.InvariantCulture)));
        }
        return files;
    }

    public Task DownloadAsync(string remoteFile, string localPath, CancellationToken ct) => DownloadAsync(remoteFile, localPath, null, ct);

    /// <summary>One file; with progress, from rclone's statistics (a large file says how far it is).</summary>
    public async Task DownloadAsync(string remoteFile, string localPath, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var tmp = localPath + ".part";
        if (progress == null) await RunAsync(["copyto", remoteFile, tmp], ct);
        else
            await RunAsync(["copyto", remoteFile, tmp, "--stats", "1s", "--stats-one-line", "--stats-log-level", "NOTICE"], ct, line =>
            {
                if (ParseStats(line) is { } p) progress.Report(p);
            });
        File.Move(tmp, localPath, true);
    }

    public Task UploadAsync(string localFile, string remoteFile, CancellationToken ct) =>
        RunAsync(["copyto", localFile, remoteFile], ct);

    /// <summary>A small text file of the cloud; null when it is not there.</summary>
    public async Task<string?> ReadTextAsync(string remoteFile, CancellationToken ct)
    {
        try
        {
            var text = await RunAsync(["cat", remoteFile], ct);
            return text.Length == 0 ? null : text;
        }
        catch (RcloneException e) when (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                         e.Message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    /// <summary>Moves a file inside one cloud (the provider copies it there, nothing goes over this computer's line).</summary>
    public Task MoveFileAsync(string from, string to, CancellationToken ct) => RunAsync(["moveto", from, to], ct);

    /// <summary>Copies only these files (paths relative to both folders) from one folder to the other.</summary>
    public async Task CopyFilesAsync(string from, string to, IReadOnlyCollection<string> files, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var list = await WriteListAsync(files, ct);
        try
        {
            await CopyAsync(from, to, ["--files-from-raw", list, "--no-traverse"], progress, ct);
        }
        finally
        {
            File.Delete(list);
        }
    }

    /// <summary>Deletes a folder with everything in it; nothing to do when it is not there.</summary>
    public async Task PurgeAsync(string remotePath, CancellationToken ct)
    {
        try
        {
            await RunAsync(["purge", remotePath], ct);
        }
        catch (RcloneException e) when (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    /// <summary>Deletes these files (paths relative to the folder), then the folders left empty.</summary>
    public async Task DeleteFilesAsync(string folder, IReadOnlyCollection<string> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        var list = await WriteListAsync(files, ct);
        try
        {
            await RunAsync(["delete", folder, "--files-from-raw", list], ct);
            await RunAsync(["rmdirs", folder, "--leave-root"], ct);
        }
        finally
        {
            File.Delete(list);
        }
    }

    private static async Task<string> WriteListAsync(IEnumerable<string> files, CancellationToken ct)
    {
        var list = Path.Combine(Path.GetTempPath(), "momentfinder-files-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllLinesAsync(list, files, new UTF8Encoding(false), ct);
        return list;
    }

    public sealed record CopyProgress(long Bytes, long TotalBytes, int Percent, string Text);

    /// <summary>
    /// Copies a folder (new and changed files only; nothing is ever deleted at the target, so a file deleted
    /// by mistake locally stays in the backup). Progress comes from rclone's one-line statistics.
    /// </summary>
    public Task CopyAsync(string from, string to, IEnumerable<string> extraArgs, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        var args = new List<string> { "copy", from, to, "--stats", "1s", "--stats-one-line", "--stats-log-level", "NOTICE", "--transfers", "4" };
        args.AddRange(extraArgs);
        return RunAsync(args, ct, line =>
        {
            if (progress == null) return;
            if (ParseStats(line) is { } p) progress.Report(p);
        });
    }

    // "2026/11/21 10:00:49 NOTICE: 341.238 MiB / 1.367 GiB, 24%, 13.688 MiB/s, ETA 1m17s (xfr#0/11)"
    [GeneratedRegex(@"([\d.]+\s*\w*B)\s*/\s*([\d.]+\s*\w*B),\s*(\d+)%(?:,\s*([\d.]+\s*\w*B)/s)?(?:,\s*ETA\s*(\S+))?(?:\s*\(xfr#(\d+)/(\d+)\))?")]
    private static partial Regex StatsLine();

    /// <summary>rclone's statistics in words: "341 MB of 1.4 GB, 13.7 MB/s, 1 min 17 s left, 0 of 11 files done".</summary>
    internal static CopyProgress? ParseStats(string line)
    {
        var m = StatsLine().Match(line);
        if (!m.Success) return null;
        var done = ParseSize(m.Groups[1].Value);
        var total = ParseSize(m.Groups[2].Value);
        var parts = new List<string> { $"{Size(done)} of {Size(total)}" };
        if (m.Groups[4].Success && ParseSize(m.Groups[4].Value) is > 0 and var speed) parts.Add($"{Size(speed)}/s");
        if (m.Groups[5].Success && Left(m.Groups[5].Value) is { } left) parts.Add(left);
        if (m.Groups[6].Success) parts.Add($"{m.Groups[6].Value} of {m.Groups[7].Value} files done");
        return new CopyProgress(done, total, int.TryParse(m.Groups[3].Value, out var pct) ? pct : 0, string.Join(", ", parts));
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes >> 20).ToString("N0", CultureInfo.InvariantCulture) + " MB",
        0 => "0 KB",
        _ => Math.Max(1, bytes >> 10).ToString("N0", CultureInfo.InvariantCulture) + " KB",
    };

    /// <summary>"1h2m17s" as "1 h 2 min left"; nothing while rclone does not know yet ("-").</summary>
    private static string? Left(string eta)
    {
        var m = Regex.Match(eta, @"^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$");
        if (!m.Success || eta.Length == 0) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        var (d, h, min, sec) = (Part(1), Part(2), Part(3), Part(4));
        return d > 0 ? $"{d} d {h} h left" : h > 0 ? $"{h} h {min} min left" : min > 0 ? $"{min} min {sec} s left" : $"{sec} s left";
    }

    internal static long ParseSize(string s)
    {
        var m = Regex.Match(s.Trim(), @"^([\d.]+)\s*(\w*)B$");
        if (!m.Success) return 0;
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Value.TrimEnd('i').ToUpperInvariant();
        var power = unit switch { "K" => 1, "M" => 2, "G" => 3, "T" => 4, _ => 0 };
        return (long)(n * Math.Pow(1024, power));
    }
}
