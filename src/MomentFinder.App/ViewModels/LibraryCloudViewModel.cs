using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.Cloud;
using MomentFinder.Sync;

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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RestoreButtonText), nameof(ThumbnailChoice))] private BackupChoice? _selectedBackup;
    /// <summary>A computer's backup: with its thumbnails or not. The shared library takes them as they are needed.</summary>
    public bool ThumbnailChoice => SelectedBackup?.Found.Shared != true;
    public string RestoreButtonText => SelectedBackup?.Found.Shared == true ? "Join this library" : "Copy this library here";
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
        Shared = _host.Db.GetSetting(LibrarySync.SharedSetting) == "1";
        if (Shared)
        {
            ConnectedText = $"The library is shared through {cloud.Name}: it is the same on every computer that joined it.";
            BackupSummary = SharedSummary();
            CanShare = CanJoin = false;
            return;
        }
        _ = CheckSharedAsync();
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

    // ---- the family's shared library ----

    /// <summary>This library is the family's shared one.</summary>
    [ObservableProperty] private bool _shared;
    /// <summary>The cloud has no shared library yet: this one can become it.</summary>
    [ObservableProperty] private bool _canShare;
    /// <summary>The cloud has the family's shared library, and this library is not it: it can join.</summary>
    [ObservableProperty] private bool _canJoin;

    /// <summary>Which computers use the shared library, and when they were last heard of.</summary>
    private string SharedSummary()
    {
        using var c = _host.Db.Open();
        var others = Dapper.SqlMapper.Query<(string Device, long? LastSeen)>(c, "SELECT device, last_seen FROM sync_seen WHERE device<>@me",
            new { me = _host.Db.Device.Id }).ToList();
        var sync = _host.Sync.LastError is { } error ? $"The last exchange with the others failed: {Plain(error)}"
            : _host.Sync.LastSync is { } at ? $"Changes exchanged with the other computers at {at.LocalDateTime:H:mm}; it repeats every minute."
            : "Changes are exchanged with the other computers every minute.";
        return others.Count == 0
            ? "No other computer joined it yet: show the recovery string and paste it there (Settings, Your library on this computer too). " + sync
            : $"Changes from {others.Count} other {(others.Count == 1 ? "computer" : "computers")} came in. " + sync;
    }

    private async Task CheckSharedAsync()
    {
        try
        {
            var exists = await _host.Sync.SharedLibraryExistsAsync(CancellationToken.None);
            CanShare = !exists;
            CanJoin = exists;
        }
        catch (RcloneException)
        {
            CanShare = CanJoin = false;
        }
    }

    /// <summary>This library becomes the family's: the other computers join it.</summary>
    [RelayCommand]
    private Task Share() => RunAsync("Sharing the library...", async ct =>
    {
        await _host.Sync.ShareAsync(new Progress<string>(what => Status = $"Sharing the library: {what}..."), ct);
        Status = "Shared. On the other computers paste the recovery string (Settings, Your library on this computer too).";
    });

    /// <summary>This computer takes the family's shared library, and keeps its own folders and what was named here.</summary>
    [RelayCommand]
    private Task Join() => _host.Cloud.BackupCloud is not { } cloud ? Task.CompletedTask : RunAsync("Looking for the shared library...", async ct =>
    {
        var recovery = RecoveryString.Parse(await _host.Cloud.RecoveryStringAsync(cloud, ct))!;
        var shared = (await new LibraryRestore(_host.Paths).FindAsync(recovery, ct)).FirstOrDefault(f => f.Shared);
        if (shared == null)
        {
            Status = "The shared library is not there any more.";
            return;
        }
        Copy.Start(recovery, shared, thumbnails: false);
        Status = "";
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
            Status = FoundBackups.Count == 0 ? "The storage answers, but no computer has kept its library there yet."
                : FoundBackups[0].Found.Shared ? "Found the family's shared library (first in the list). Join it with the button above."
                : FoundBackups.Count == 1 ? "Found it. Copy it here with the button above."
                : $"Found the libraries of {FoundBackups.Count} computers, the newest first. Choose one and copy it here.";
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
