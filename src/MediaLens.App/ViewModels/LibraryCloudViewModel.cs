using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.Cloud;

namespace MediaLens.App.ViewModels;

/// <summary>A kind of storage the library can be kept in, as offered in Settings.</summary>
public sealed record StorageKind(string Title, string Provider, string AddressLabel, string AddressHint)
{
    public bool IsS3 => Provider.Length > 0;
    /// <summary>IDrive e2 tells its address by the access key: nothing to fill in.</summary>
    public bool NeedsAddress => AddressLabel.Length > 0;
    public override string ToString() => Title;
}

/// <summary>
/// Settings, the library in a cloud: where the library itself (settings, names, search data, thumbnails) is kept,
/// the recovery string, and bringing a library back on a new or second computer.
/// </summary>
public sealed partial class LibraryCloudViewModel : ObservableObject
{
    private const string Address = "Address (endpoint)";
    private readonly LibraryHost _host;
    private CancellationTokenSource? _cts;

    public IReadOnlyList<StorageKind> Kinds { get; } =
    [
        new("IDrive e2", "IDrive", "", ""),
        new("S3 compatible (other)", "Other", Address, "e.g. s3.example.com"),
        new("Amazon S3", "AWS", "Region", "e.g. eu-central-1"),
        new("Backblaze B2", "Other", Address, "e.g. s3.eu-central-003.backblazeb2.com"),
        new("Wasabi", "Wasabi", Address, "e.g. s3.eu-central-1.wasabisys.com"),
        new("MinIO", "Minio", Address, "e.g. https://minio.example.com:9000"),
        new("OneDrive", "", "", ""),
        new("Google Drive", "", "", ""),
        new("Folder (NAS, external disk)", "", "", ""),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsS3), nameof(IsFolder), nameof(IsSignIn), nameof(NeedsAddress), nameof(KeysHint))] private StorageKind _kind;
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _accessKey = "";
    [ObservableProperty] private string _secretKey = "";
    [ObservableProperty] private string _bucket;
    [ObservableProperty] private string _folder = "";

    public bool IsS3 => Kind.IsS3;
    public bool NeedsAddress => Kind.IsS3 && Kind.NeedsAddress;
    /// <summary>Where the keys come from, for the kind that needs nothing else.</summary>
    public string KeysHint => Kind.Provider == "IDrive" ? "The two keys are in IDrive e2 under Access Keys. The address is found by them." : "";
    public bool IsFolder => Kind.Title.StartsWith("Folder", StringComparison.Ordinal);
    public bool IsSignIn => !IsS3 && !IsFolder;
    public bool RcloneMissing => !Rclone.IsAvailable;

    /// <summary>The library has its cloud: the form gives way to the state and the recovery string.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NotConnected))] private bool _connected;
    public bool NotConnected => !Connected;
    [ObservableProperty] private string _connectedText = "";
    [ObservableProperty] private string _backupSummary = "";
    [ObservableProperty] private string _recoveryText = "";

    [ObservableProperty] private string _restoreText = "";
    public ObservableCollection<LibraryRestore.Found> FoundBackups { get; } = [];
    public bool HasFoundBackups => FoundBackups.Count > 0;
    [ObservableProperty] private LibraryRestore.Found? _selectedBackup;
    [ObservableProperty] private bool _restoreThumbnails = true;
    /// <summary>A library is downloaded and waits for the next start.</summary>
    [ObservableProperty] private bool _restartNeeded;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    public event Action? RestartRequested;

    public LibraryCloudViewModel(LibraryHost host)
    {
        _host = host;
        _kind = Kinds[0];
        // A bucket of the library's own, made if it is not there: nothing to prepare at the provider.
        _bucket = "medialens-" + host.Cloud.LibraryId;
        Reload();
        host.Mirrors.MirrorsChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    private void Reload()
    {
        var cloud = _host.Cloud.BackupCloud;
        Connected = cloud != null;
        if (cloud == null) return;
        var backup = _host.Cloud.Backup;
        ConnectedText = $"The library is kept in {cloud.Name}.";
        BackupSummary = backup.LastAt is { } at
            ? $"Last backed up {at.LocalDateTime:d. M. yyyy H:mm}" + (backup.LastResult is { } r && r != "OK" ? $". The last try failed: {r}" : ". It repeats by itself every few hours.")
            : backup.LastResult is { } failed && failed != "OK" ? $"Not backed up yet: {failed}" : "The first backup is running or starts in a moment.";
    }

    private async Task RunAsync(string what, Func<CancellationToken, Task> action)
    {
        _cts = new CancellationTokenSource();
        Busy = true;
        Status = what;
        try
        {
            await action(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception e) when (e is RcloneException or IOException or HttpRequestException or InvalidOperationException)
        {
            Status = "Failed: " + Plain(e.Message);
        }
        finally
        {
            Busy = false;
            Reload();
        }
    }

    /// <summary>What a storage's answer means, in words; the answer itself stays at the end for whoever needs it.</summary>
    private static string Plain(string error)
    {
        // rclone's own stamp ("2026/11/20 20:25:32 NOTICE: ") says nothing here.
        error = System.Text.RegularExpressions.Regex.Replace(error.Trim(), @"^\d{4}/\d\d/\d\d \d\d:\d\d:\d\d (NOTICE|ERROR)?\s*:?\s*", "");
        if (error.LastIndexOf("last error was:", StringComparison.Ordinal) is >= 0 and var last) error = error[(last + 15)..].Trim();
        bool Has(string part) => error.Contains(part, StringComparison.OrdinalIgnoreCase);
        var meaning =
            Has("SignatureDoesNotMatch") ? "The secret key does not belong to this access key."
            : Has("InvalidAccessKeyId") ? "The storage does not know this access key."
            : Has("BucketAlreadyExists") ? "A bucket of this name belongs to somebody else: choose another name."
            : Has("NoSuchBucket") ? "There is no such bucket, and it could not be made."
            : Has("AccessDenied") || Has("Forbidden") ? "The key is not allowed to do this. It has to read and write the bucket (and to make it, if it is not there)."
            : Has("send request failed") || Has("no such host") || Has("dial tcp") || Has("connection refused") ? "The storage does not answer. Check the address and the internet connection."
            : null;
        return meaning == null ? error : $"{meaning} ({error.Trim()})";
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>Connects the storage, keeps the library in it from now on and shows the recovery string.</summary>
    [RelayCommand]
    private Task Connect()
    {
        if (IsS3 && (NeedsAddress && string.IsNullOrWhiteSpace(Endpoint) || string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey) || string.IsNullOrWhiteSpace(Bucket)))
        {
            Status = NeedsAddress ? $"Fill in the {Kind.AddressLabel.ToLowerInvariant()}, both keys and the bucket." : "Fill in both keys and the bucket.";
            return Task.CompletedTask;
        }
        if (IsFolder && string.IsNullOrWhiteSpace(Folder))
        {
            Status = "Choose the folder.";
            return Task.CompletedTask;
        }
        var kind = Kind;
        return RunAsync(IsSignIn ? "Sign in in the browser window that opened..." : "Connecting...", async ct =>
        {
            var aws = kind.Provider == "AWS";
            // The library is stored as it is (index, search data, thumbnails: of use to MediaLens only); what must
            // not be readable, the access keys of the clouds, is encrypted by the backup itself.
            var cloud = kind.IsS3
                ? await _host.Cloud.AddS3Async(kind.Title, new S3Settings(kind.Provider, aws || !kind.NeedsAddress ? "" : Endpoint.Trim(), aws ? Endpoint.Trim() : "",
                    AccessKey.Trim(), SecretKey.Trim(), Bucket.Trim()), null, ct, encrypt: false)
                : kind.Title == "OneDrive" ? await _host.Cloud.AddOneDriveAsync(kind.Title, null, ct, encrypt: false)
                : kind.Title == "Google Drive" ? await _host.Cloud.AddGoogleDriveAsync(kind.Title, null, ct, encrypt: false)
                : await _host.Cloud.AddFolderAsync("Folder", Folder.Trim(), null, ct, encrypt: false);
            SecretKey = "";
            _host.Cloud.BackupCloud = cloud;
            RecoveryText = await _host.Cloud.RecoveryStringAsync(cloud, ct);
            _host.Mirrors.BackupNow();
            Status = "Connected, and the library is being backed up. Save the recovery string now.";
        });
    }

    /// <summary>The library is not kept in the cloud any more; what is there stays.</summary>
    [RelayCommand]
    private Task Disconnect() => _host.Cloud.BackupCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Disconnecting...", async ct =>
        {
            _host.Cloud.BackupCloud = null;
            RecoveryText = "";
            // A cloud that folders are mirrored into stays known.
            if (!_host.Cloud.CloudInUse(cloud.Id)) await _host.Cloud.RemoveCloudAsync(cloud.Id, ct);
            Status = "The library is on this computer only now. What was uploaded stays in the cloud.";
        });

    [RelayCommand]
    private void BackUpNow()
    {
        _host.Mirrors.BackupNow();
        Status = "The backup started; its progress is in the status bar of the main window.";
    }

    [RelayCommand]
    private Task ShowRecovery() => _host.Cloud.BackupCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Making the recovery string...", async ct =>
        {
            RecoveryText = await _host.Cloud.RecoveryStringAsync(cloud, ct);
            Status = "Copy the recovery string or save it to a file, and keep it somewhere safe.";
        });

    // ---- bringing a library back ----

    private Recovery? _recovery;

    [RelayCommand]
    private Task FindBackups()
    {
        FoundBackups.Clear();
        OnPropertyChanged(nameof(HasFoundBackups));
        if (RecoveryString.Parse(RestoreText) is not { } recovery)
        {
            Status = "This is not a recovery string: it starts with MLR1. and must be pasted whole.";
            return Task.CompletedTask;
        }
        _recovery = recovery;
        return RunAsync($"Looking into {recovery.Name}...", async ct =>
        {
            foreach (var found in await new LibraryRestore(_host.Paths).FindAsync(recovery, ct)) FoundBackups.Add(found);
            OnPropertyChanged(nameof(HasFoundBackups));
            SelectedBackup = FoundBackups.FirstOrDefault();
            Status = FoundBackups.Count switch
            {
                0 => "The storage answers, but there is no library in it yet.",
                1 => "Found your library. Bring it back with the button above.",
                _ => $"Found the libraries of {FoundBackups.Count} computers, the newest first. Choose one and bring it back.",
            };
        });
    }

    [RelayCommand]
    private Task RestoreLibrary() => _recovery is not { } recovery || SelectedBackup is not { } backup ? Task.CompletedTask :
        RunAsync("Downloading the library...", async ct =>
        {
            // Progress arrives a moment late: none of it may write over the final message.
            var done = false;
            await new LibraryRestore(_host.Paths).DownloadAsync(recovery, backup, RestoreThumbnails,
                new Progress<string>(what => { if (!done) Status = $"Downloading the library: {what}"; }), ct);
            done = true;
            RestartNeeded = true;
            Status = "The library is downloaded. Restart MediaLens to open it.";
        });

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke();
}
