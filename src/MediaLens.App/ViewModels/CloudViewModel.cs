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
    [ObservableProperty] private bool _encrypt = true;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _passwordAgain = "";
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

    private void Reload()
    {
        Clouds.Clear();
        foreach (var c in _host.Cloud.Clouds()) Clouds.Add(c);
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
        if (Encrypt && (Password.Length < 8 || Password != PasswordAgain))
        {
            Status = Password.Length < 8 ? "The password needs at least 8 characters." : "The two passwords differ.";
            return Task.CompletedTask;
        }
        var password = Encrypt ? Password : null;
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
            Status = $"{cloud.Name} added" + (cloud.Encrypted ? ". Keep the password safe: without it nobody, not even you, can read the backup." : ".");
            Password = PasswordAgain = SecretKey = "";
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
                Status = "This cloud has no index backup yet (it is uploaded with every mirror run).";
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
