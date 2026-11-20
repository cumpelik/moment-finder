using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaLens.Cloud;

public sealed record RemoteFile(string Path, long Size, DateTimeOffset ModTime);

public sealed class RcloneException(string message) : Exception(message);

/// <summary>
/// rclone (MIT) does the cloud work: S3 and compatible storage, OneDrive, Google Drive and many more, and
/// the client-side encryption (its "crypt" remote: file contents and names encrypted before upload, the
/// provider sees only noise). Encrypted data can be read back with plain rclone and the password, even
/// without MediaLens. Our remotes live in a private rclone.conf in the library folder.
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
            Environment.GetEnvironmentVariable("MEDIALENS_RCLONE"),
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

    public async Task DownloadAsync(string remoteFile, string localPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var tmp = localPath + ".part";
        await RunAsync(["copyto", remoteFile, tmp], ct);
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
        var list = Path.Combine(Path.GetTempPath(), "medialens-files-" + Guid.NewGuid().ToString("N") + ".txt");
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
            var m = StatsLine().Match(line);
            if (!m.Success) return;
            progress.Report(new CopyProgress(ParseSize(m.Groups[1].Value), ParseSize(m.Groups[2].Value),
                int.TryParse(m.Groups[3].Value, out var pct) ? pct : 0, line[(line.IndexOf(':') + 1)..].Trim()));
        });
    }

    // "Transferred:   	   12.345 MiB / 100.000 MiB, 12%, 1.234 MiB/s, ETA 1m10s"
    [GeneratedRegex(@"([\d.]+\s*\w*B)\s*/\s*([\d.]+\s*\w*B),\s*(\d+)%")]
    private static partial Regex StatsLine();

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
