using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.Cloud;

namespace MomentFinder.App.ViewModels;

/// <summary>
/// Settings, the library in a cloud: where the library itself (settings, names, search data, thumbnails) is kept,
/// the recovery string, and bringing a library back on a new or second computer.
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

    [ObservableProperty] private string _restoreText = "";
    public ObservableCollection<LibraryRestore.Found> FoundBackups { get; } = [];
    public bool HasFoundBackups => FoundBackups.Count > 0;
    [ObservableProperty] private LibraryRestore.Found? _selectedBackup;
    [ObservableProperty] private bool _restoreThumbnails = true;
    /// <summary>A library is downloaded and waits for the next start.</summary>
    [ObservableProperty] private bool _restartNeeded;

    public event Action? RestartRequested;

    public LibraryCloudViewModel(LibraryHost host)
    {
        _host = host;
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
        if (cloud == null) return;
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

    /// <summary>The library is not kept in the cloud any more; what is there stays.</summary>
    [RelayCommand]
    private Task Disconnect() => _host.Cloud.BackupCloud is not { } cloud ? Task.CompletedTask :
        RunAsync("Disconnecting...", async ct =>
        {
            _host.Cloud.BackupCloud = null;
            RecoveryText = "";
            // A cloud that folders are backed up to or live in stays known.
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
            Status = "The library is downloaded. Restart MomentFinder to open it.";
        });

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke();
}
