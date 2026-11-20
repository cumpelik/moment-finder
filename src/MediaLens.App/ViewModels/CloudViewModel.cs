using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.Cloud;
using MediaLens.Library;

namespace MediaLens.App.ViewModels;

/// <summary>The cloud window: clouds, mirrors of sources, restore and cloud folders as sources.</summary>
public sealed partial class CloudViewModel : ObservableObject
{
    private readonly LibraryHost _host;
    private readonly Action _libraryChanged;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CloudInfo> Clouds { get; } = [];
    public ObservableCollection<MirrorInfo> Mirrors { get; } = [];
    public ObservableCollection<Source> Sources { get; } = [];
    public ObservableCollection<string> CloudFolders { get; } = [];

    public IReadOnlyList<string> Kinds { get; } = ["OneDrive", "Google Drive", "S3 (Amazon, Backblaze, Wasabi, MinIO...)", "Folder (NAS, external disk)"];

    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _bucket = "";
    [ObservableProperty] private string _accessKey = "";
    [ObservableProperty] private string _secretKey = "";
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _region = "";
    [ObservableProperty] private string _provider = "AWS";
    [ObservableProperty] private string _folder = "";

    [ObservableProperty] private CloudInfo? _selectedCloud;
    [ObservableProperty] private string? _selectedFolder;
    [ObservableProperty] private MirrorInfo? _selectedMirror;
    [ObservableProperty] private Source? _mirrorSource;
    [ObservableProperty] private CloudInfo? _mirrorCloud;
    [ObservableProperty] private double _intervalHours;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    public bool IsS3 => KindIndex == 2;
    public bool IsFolder => KindIndex == 3;
    public bool IsSignIn => KindIndex is 0 or 1;
    public bool RcloneMissing => !Rclone.IsAvailable;

    public CloudViewModel(LibraryHost host, Action libraryChanged)
    {
        _host = host;
        _libraryChanged = libraryChanged;
        _intervalHours = host.Mirrors.IntervalHours;
        Reload();
        host.Mirrors.MirrorsChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Reload);
    }

    partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsS3));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(IsSignIn));
    }

    partial void OnIntervalHoursChanged(double value)
    {
        if (value >= 1) _host.Mirrors.IntervalHours = value;
    }

    // ---- the library in the cloud and the way back ----

    public ObservableCollection<LibraryRestore.Found> FoundBackups { get; } = [];
    public bool HasClouds => Clouds.Count > 0;
    public bool HasBackupCloud => BackupCloud != null;
    public bool HasFoundBackups => FoundBackups.Count > 0;

    [ObservableProperty] private string _backupSummary = "";
    [ObservableProperty] private CloudInfo? _backupCloud;
    /// <summary>The recovery string, once asked for (or right after the first cloud is added).</summary>
    [ObservableProperty] private string _recoveryText = "";
    [ObservableProperty] private string _restoreText = "";
    [ObservableProperty] private LibraryRestore.Found? _selectedBackup;
    [ObservableProperty] private bool _restoreThumbnails = true;
    /// <summary>A library is downloaded and waits for the next start.</summary>
    [ObservableProperty] private bool _restartNeeded;

    public event Action? RestartRequested;

    private bool _reloading;

    partial void OnBackupCloudChanged(CloudInfo? value)
    {
        OnPropertyChanged(nameof(HasBackupCloud));
        if (_reloading || value == null || value.Id == _host.Cloud.BackupCloud?.Id) return;
        // Chosen: the library goes there now, and the way back is shown right away.
        _host.Cloud.BackupCloud = value;
        UpdateBackupSummary();
        _host.Mirrors.BackupNow();
        _ = ShowRecovery();
    }

    /// <summary>The library is not backed up any more; what is in the cloud stays.</summary>
    [RelayCommand]
    private void StopBackup()
    {
        _host.Cloud.BackupCloud = null;
        RecoveryText = "";
        Reload();
        Status = "The library is not backed up now. What was uploaded stays in the cloud.";
    }

    private void UpdateBackupSummary()
    {
        var backup = _host.Cloud.Backup;
        BackupSummary = _host.Cloud.BackupCloud is not { } cloud
            ? Clouds.Count == 0 ? "No cloud yet: add one first." : "Not backed up: on this computer only."
            : backup.LastAt is { } at
                ? $"Backed up to {cloud.Name}: {at.LocalDateTime:d. M. yyyy H:mm}" + (backup.LastResult is { } r && r != "OK" ? $". The last try failed: {r}" : ".")
                : backup.LastResult is { } failed && failed != "OK" ? $"Not backed up to {cloud.Name} yet: {failed}" : $"Not backed up to {cloud.Name} yet; it starts by itself.";
    }

    [RelayCommand]
    private void BackUpNow()
    {
        _host.Mirrors.BackupNow();
        Status = "The library backup started; the progress is in the status bar of the main window.";
    }

    [RelayCommand]
    private Task ShowRecovery() => _host.Cloud.BackupCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Making the recovery string...", async ct =>
        {
            RecoveryText = await _host.Cloud.RecoveryStringAsync(cloud, ct);
            Status = "Copy the recovery string or save it to a file, and keep it somewhere safe. The library is being backed up meanwhile.";
        });

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
                0 => "The storage answers, but there is no library backup in it yet.",
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

    private void Reload()
    {
        _reloading = true;
        Clouds.Clear();
        foreach (var c in _host.Cloud.Clouds()) Clouds.Add(c);
        BackupCloud = Clouds.FirstOrDefault(c => c.Id == _host.Cloud.BackupCloud?.Id);
        _reloading = false;
        OnPropertyChanged(nameof(HasClouds));
        UpdateBackupSummary();
        Mirrors.Clear();
        foreach (var m in _host.Cloud.Mirrors()) Mirrors.Add(m);
        Sources.Clear();
        foreach (var s in _host.Repo.ListSources().Where(s => s.Kind == SourceKinds.Local)) Sources.Add(s);
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
            Status = "Failed: " + e.Message;
        }
        finally
        {
            Busy = false;
            Reload();
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private Task AddCloud()
    {
        // No password to think up: the encryption key is made here and is part of the recovery string.
        string? password = null;
        var name = string.IsNullOrWhiteSpace(Name) ? Kinds[KindIndex].Split(' ')[0] : Name.Trim();
        return RunAsync(IsSignIn ? "Sign in in the browser window that opened..." : "Connecting...", async ct =>
        {
            var cloud = KindIndex switch
            {
                0 => await _host.Cloud.AddOneDriveAsync(name, password, ct),
                1 => await _host.Cloud.AddGoogleDriveAsync(name, password, ct),
                2 => await _host.Cloud.AddS3Async(name, new S3Settings(Provider, Endpoint, Region, AccessKey, SecretKey, Bucket), password, ct),
                _ => await _host.Cloud.AddFolderAsync(name, Folder, password, ct),
            };
            SecretKey = "";
            Status = $"{cloud.Name} added. Mirror a folder into it on the right; to keep the library itself there, choose it in Settings, Library backup.";
        });
    }

    [RelayCommand]
    private Task RemoveCloud() => SelectedCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Removing...", async ct =>
        {
            await _host.Cloud.RemoveCloudAsync(cloud.Id, ct);
            Status = $"{cloud.Name} removed here; the data in the cloud stays.";
        });

    partial void OnSelectedCloudChanged(CloudInfo? value)
    {
        CloudFolders.Clear();
        if (value == null) return;
        _ = RunAsync("Reading the folders...", async ct =>
        {
            foreach (var f in await _host.Cloud.FoldersAsync(value.Id, ct)) CloudFolders.Add(f);
            Status = CloudFolders.Count == 0 ? "The cloud has no folders yet: mirror a source into it." : "";
        });
    }

    [RelayCommand]
    private void AddMirror()
    {
        if (MirrorSource == null || MirrorCloud == null)
        {
            Status = "Choose a source and a cloud.";
            return;
        }
        var mirror = _host.Cloud.AddMirror(MirrorSource.Id, MirrorCloud.Id);
        Reload();
        _host.Mirrors.RunNow(mirror.Id);
        Status = $"{mirror.SourceName} will be mirrored to {mirror.CloudName}; the first upload starts now.";
    }

    [RelayCommand]
    private void RunMirror()
    {
        _host.Mirrors.RunNow(SelectedMirror?.Id);
        Status = "Mirroring started; the progress is in the status bar of the main window.";
    }

    [RelayCommand]
    private void RemoveMirror()
    {
        if (SelectedMirror is not { } m) return;
        _host.Cloud.RemoveMirror(m.Id);
        Reload();
        Status = "The mirror is stopped; what was uploaded stays in the cloud.";
    }

    /// <summary>Browses a cloud folder in the library without downloading it all.</summary>
    [RelayCommand]
    private void AddAsSource()
    {
        if (SelectedCloud is not { } cloud || SelectedFolder is not { } folder) return;
        var source = _host.Cloud.AddCloudSource(cloud.Id, folder);
        _host.Indexer.RequestScan();
        _libraryChanged();
        Status = $"{source.Name} added to the library; it is being read.";
    }

    public Task RestoreAsync(string localDir)
    {
        if (SelectedCloud is not { } cloud || SelectedFolder is not { } folder) return Task.CompletedTask;
        return RunAsync($"Restoring {folder}...", async ct =>
        {
            await _host.Cloud.RestoreAsync(cloud.Id, folder, localDir,
                new Progress<Rclone.CopyProgress>(p => Status = $"Restoring {folder}: {p.Text}"), ct);
            Status = $"{folder} restored to {localDir}. Add the folder as a source, then use Import names to get people and buckets back.";
        });
    }

    /// <summary>Names, people and buckets from the cloud's index backup, matched to the items by content.</summary>
    [RelayCommand]
    private Task ImportIndex() => SelectedCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Importing names and people...", async ct =>
        {
            var file = await _host.Cloud.DownloadIndexAsync(cloud.Id, ct);
            if (file == null)
            {
                Status = "This cloud holds no library backup (Settings, Library backup chooses where the library is kept).";
                return;
            }
            var r = await Task.Run(() => new IndexImport(_host.Db).Import(file), ct);
            File.Delete(file);
            _host.RefreshNames();
            _host.Indexer.RequestIdentity();
            _libraryChanged();
            Status = $"Matched {r.MatchedItems} items: {r.People} people added, {r.Detections} faces and figures, {r.BucketItems} bucket items.";
        });
}
