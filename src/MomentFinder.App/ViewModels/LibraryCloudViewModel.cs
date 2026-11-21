using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.Cloud;

namespace MomentFinder.App.ViewModels;

/// <summary>
/// Settings, the library in a cloud: where the library itself (settings, names, search data, thumbnails) is kept,
/// the recovery string, and copying the library of another computer here (a second computer, or a new one).
/// </summary>
public sealed partial class LibraryCloudViewModel : CloudWorkViewModel
{
    private readonly LibraryHost _host;

    /// <summary>The storage to keep the library in; a bucket of the library's own is made if it is not there.</summary>
    public StorageFormViewModel Form { get; }

    /// <summary>The library has its cloud: the form gives way to the state and the recovery string.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NotConnected))] private bool _connected;
    public bool NotConnected => !Connected;
    [ObservableProperty] private string _connectedText = "";
    [ObservableProperty] private string _backupSummary = "";
    [ObservableProperty] private string _recoveryText = "";

    /// <summary>Storages this library knows already (a library copied from another computer brings them): no keys again.</summary>
    public List<CloudInfo> KnownClouds { get; private set; } = [];
    public bool HasKnownClouds => KnownClouds.Count > 0;
    [ObservableProperty] private CloudInfo? _knownCloud;

    [ObservableProperty] private string _restoreText = "";
    public ObservableCollection<BackupChoice> FoundBackups { get; } = [];
    public bool HasFoundBackups => FoundBackups.Count > 0;
    [ObservableProperty] private BackupChoice? _selectedBackup;
    [ObservableProperty] private bool _restoreThumbnails = true;

    /// <summary>The copy being made or made: shown in place of the choice.</summary>
    public LibraryCopyViewModel Copy { get; }

    /// <summary>What happens to the library on this computer, when it has one worth a word.</summary>
    public string ReplacesText { get; }
    public bool Replaces => ReplacesText.Length > 0;

    public LibraryCloudViewModel(LibraryHost host, LibraryCopyViewModel copy)
    {
        _host = host;
        Copy = copy;
        var sources = host.Repo.ListSources();
        ReplacesText = sources.Count == 0 ? "" :
            $"The library on this computer ({(sources.Count == 1 ? "1 folder" : $"{sources.Count} folders")}, its names and buckets) gives way to the copy. " +
            "It is kept beside it as library.before-restore.db, but MomentFinder does not use it any more.";
        Form = new StorageFormViewModel("momentfinder-" + host.Cloud.LibraryId);
        Reload();
        host.Mirrors.MirrorsChanged += OnMirrorsChanged;
    }

    private void OnMirrorsChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Reload);

    /// <summary>The window closed: no more updates.</summary>
    public void Detach() => _host.Mirrors.MirrorsChanged -= OnMirrorsChanged;

    protected override void Reload()
    {
        var cloud = _host.Cloud.BackupCloud;
        Connected = cloud != null;
        if (cloud == null)
        {
            KnownClouds = _host.Cloud.Clouds();
            OnPropertyChanged(nameof(KnownClouds));
            OnPropertyChanged(nameof(HasKnownClouds));
            KnownCloud ??= KnownClouds.FirstOrDefault();
            return;
        }
        var backup = _host.Cloud.Backup;
        ConnectedText = $"The library is kept in {cloud.Name}.";
        BackupSummary = backup.LastAt is { } at
            ? $"Last backed up {at.LocalDateTime:d. M. yyyy H:mm}" + (backup.LastResult is { } r && r != "OK" ? $". The last try failed: {r}" : ". It repeats by itself every few hours.")
            : backup.LastResult is { } failed && failed != "OK" ? $"Not backed up yet: {failed}" : "The first backup is running or starts in a moment.";
    }

    /// <summary>Connects the storage, keeps the library in it from now on and shows the recovery string.</summary>
    [RelayCommand]
    private Task Connect()
    {
        if (Form.Missing() is { } missing)
        {
            Status = missing;
            return Task.CompletedTask;
        }
        return RunAsync(Form.ConnectingText, async ct =>
        {
            // The library is stored as it is (index, search data, thumbnails: of use to MomentFinder only); what must
            // not be readable, the access keys of the clouds, is encrypted by the backup itself.
            var cloud = await Form.ConnectAsync(_host.Cloud, ct);
            _host.Cloud.BackupCloud = cloud;
            RecoveryText = await _host.Cloud.RecoveryStringAsync(cloud, ct);
            _host.Mirrors.BackupNow();
            Status = "Connected, and the library is being backed up. Save the recovery string now.";
        });
    }

    /// <summary>Keeps the library in a storage it knows already.</summary>
    [RelayCommand]
    private Task ConnectKnown() => KnownCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Connecting...", async ct =>
        {
            _host.Cloud.BackupCloud = cloud;
            RecoveryText = await _host.Cloud.RecoveryStringAsync(cloud, ct);
            _host.Mirrors.BackupNow();
            Status = "Connected, and the library is being backed up. Save the recovery string now.";
        });

    /// <summary>The library is not kept in the cloud any more; what is there stays.</summary>
    [RelayCommand]
    private Task Disconnect() => _host.Cloud.BackupCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Disconnecting...", async ct =>
        {
            _host.Cloud.BackupCloud = null;
            RecoveryText = "";
            // A cloud that folders are backed up to or live in stays known.
            if (!_host.Cloud.CloudInUse(cloud.Id)) await _host.Cloud.RemoveCloudAsync(cloud.Id, ct);
            Status = "This computer does not back its library up any more. Photos from the cloud open as before; what was uploaded stays there.";
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

    // ---- the library of another computer ----

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
            var here = _host.Cloud.Device.Folder;
            foreach (var found in await new LibraryRestore(_host.Paths).FindAsync(recovery, ct))
                FoundBackups.Add(new BackupChoice(found, found.Folder == here ? found.Title + " (this computer)" : found.Title));
            OnPropertyChanged(nameof(HasFoundBackups));
            // Another computer's library first: this one's own is here already.
            SelectedBackup = FoundBackups.FirstOrDefault(b => b.Found.Folder != here) ?? FoundBackups.FirstOrDefault();
            Status = FoundBackups.Count switch
            {
                0 => "The storage answers, but no computer has kept its library there yet.",
                1 => "Found it. Copy it here with the button above.",
                _ => $"Found the libraries of {FoundBackups.Count} computers, the newest first. Choose one and copy it here.",
            };
        });
    }

    [RelayCommand]
    private void RestoreLibrary()
    {
        if (_recovery is not { } recovery || SelectedBackup is not { } choice) return;
        Copy.Start(recovery, choice.Found, RestoreThumbnails);
        FoundBackups.Clear();
        OnPropertyChanged(nameof(HasFoundBackups));
        RestoreText = "";
        Status = "";
    }
}

/// <summary>A library found in the cloud, as offered: this computer's own is marked.</summary>
public sealed record BackupChoice(LibraryRestore.Found Found, string Title);
