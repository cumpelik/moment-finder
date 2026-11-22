using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MomentFinder.Library;

namespace MomentFinder.Cloud;

/// <summary>
/// A video that lives in the cloud plays while it comes, not after all of it was downloaded: rclone serves the
/// folder on this computer only (http on 127.0.0.1, behind a random path nobody else knows) and the player reads
/// from there what it needs: the start, the place it was moved to. Encrypted folders are decrypted on the way.
/// Measured on IDrive e2: the first frame of a 314 MB video in about a second, against 33 s to download it whole.
/// <para>
/// What was read stays on the disk for a while (rclone's own cache, limited): going back, a loop or jumping between
/// moments does not read it again. One rclone per folder of the library, started with its first video and stopped
/// with the application.
/// </para>
/// </summary>
public sealed class CloudStreams : IDisposable
{
    private sealed record Gate(Process Process, string BaseUrl);

    private readonly Rclone _rclone;
    private readonly string _cacheDir;
    private readonly Dictionary<string, Task<Gate?>> _gates = [];
    /// <summary>A folder whose gate did not start is not tried again at every video (each try waits seconds).</summary>
    private readonly Dictionary<string, DateTime> _failed = [];
    private bool _cleaned;
    private bool _disposed;

    /// <summary>How long a gate may take to start answering (rclone asks the storage about the folder first).</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public CloudStreams(Rclone rclone, string cacheDir)
    {
        _rclone = rclone;
        _cacheDir = cacheDir;
    }

    /// <summary>
    /// The address the item plays from; null when it cannot be served (no rclone, the gate does not start): the
    /// caller downloads the file then, as before.
    /// </summary>
    public async Task<string?> UrlAsync(MediaItem item, CancellationToken ct)
    {
        if (item.SourceRemote is not { } remote || !Rclone.IsAvailable) return null;
        Task<Gate?> starting;
        lock (_gates)
        {
            if (_disposed) return null;
            if (_gates.TryGetValue(remote, out var known) && (!known.IsCompleted || known.Result is { Process.HasExited: false }))
                starting = known;
            else
            {
                if (_failed.TryGetValue(remote, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(30)) return null;
                _gates[remote] = starting = Task.Run(() => StartAsync(remote));
            }
        }
        var gate = await starting.WaitAsync(ct);
        if (gate == null) return null;
        return gate.BaseUrl + string.Join('/', item.RelPath.Split('/').Select(Uri.EscapeDataString));
    }

    private async Task<Gate?> StartAsync(string remote)
    {
        try
        {
            // What the gates of the last run left on the disk is of no use to these.
            lock (_gates)
            {
                if (!_cleaned && Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, true);
                _cleaned = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held by something: rclone trims it by itself.
        }
        // Several connections at once, each a megabyte at a time (rclone 1.68 and newer): one connection to IDrive
        // e2 gave 2 MB a second where a video needed 4, eight gave 7 to 12, and the first megabytes come soonest
        // with the small pieces. An older rclone reads over one connection, as fast as that goes.
        string[] parallel = [];
        try
        {
            if (await _rclone.VersionAsync(CancellationToken.None) >= new Version(1, 68))
                parallel = ["--vfs-read-chunk-streams", "8", "--vfs-read-chunk-size", "1M"];
        }
        catch (RcloneException)
        {
            // Unknown version: the flags every version knows.
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var port = FreePort();
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            // Each folder its own cache: two gates must not write the same file.
            var cache = Path.Combine(_cacheDir, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(remote)))[..12]);
            Process process;
            try
            {
                process = _rclone.Start([
                    "serve", "http", remote, "--addr", $"127.0.0.1:{port}", "--baseurl", "/" + token, "--read-only",
                    // Without it an S3 storage is asked for the time of every file of the folder, one by one.
                    "--use-server-modtime", "--dir-cache-time", "30s",
                    // What was read is kept on the disk, and read ahead of the player.
                    "--vfs-cache-mode", "full", "--cache-dir", cache, "--vfs-cache-max-size", "2G", "--vfs-cache-max-age", "24h",
                    "--vfs-read-ahead", "64M", "--buffer-size", "16M", .. parallel,
                ]);
            }
            catch (Exception e) when (e is RcloneException or System.ComponentModel.Win32Exception or IOException)
            {
                break;
            }
            if (await AnswersAsync(process, port))
            {
                lock (_gates)
                {
                    if (!_disposed) return new Gate(process, $"http://127.0.0.1:{port}/{token}/");
                }
            }
            Kill(process);
            lock (_gates)
                if (_disposed) return null;
        }
        lock (_gates) _failed[remote] = DateTime.UtcNow;
        return null;
    }

    private async Task<bool> AnswersAsync(Process process, int port)
    {
        var until = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < until && !process.HasExited)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }
        return false;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already.
        }
        process.Dispose();
    }

    public void Dispose()
    {
        List<Task<Gate?>> gates;
        lock (_gates)
        {
            if (_disposed) return;
            _disposed = true;
            gates = [.. _gates.Values];
            _gates.Clear();
        }
        // One still starting sees _disposed and ends itself.
        foreach (var g in gates)
            if (g is { IsCompletedSuccessfully: true, Result: { } gate }) Kill(gate.Process);
    }
}

/// <summary>
/// A helper program that keeps running (a gate of the cloud) must not outlive the application, also when the
/// application is ended the hard way. On Windows the system sees to it: a job that ends its processes when its last
/// handle closes, which is when this process is gone.
/// </summary>
internal static class ChildProcesses
{
    private static readonly object Lock = new();
    private static IntPtr _job;

    public static void EndWithThisOne(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            lock (Lock)
            {
                if (_job == IntPtr.Zero) _job = NewJob();
                if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, process.Handle);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            // Without it the process is still ended when the application closes in the usual way.
        }
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static IntPtr NewJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return job;
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimits>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            SetInformationJobObject(job, ExtendedLimitInformation, memory, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadTransfers, WriteTransfers, OtherTransfers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
