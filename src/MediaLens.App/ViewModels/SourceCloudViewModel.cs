using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.Cloud;
using MediaLens.Library;

namespace MediaLens.App.ViewModels;

/// <summary>A storage to choose for a folder: one connected already, or another one filled in below.</summary>
public sealed record CloudChoice(string Title, CloudInfo? Cloud)
{
    public override string ToString() => Title;
}

/// <summary>
/// One source and the cloud: connecting a folder (backed up, or cloned to the cloud), the state of its backup and the
/// files deleted here that wait for a word, cloning it to the cloud, and giving a folder in the cloud a home here.
/// </summary>
public sealed partial class SourceCloudViewModel : CloudWorkViewModel
{
    private readonly LibraryHost _host;
    private readonly long _sourceId;
    private MirrorInfo? _mirror;

    public string SourceName { get; private set; } = "";
    public string Title => $"{SourceName} and the cloud";

    /// <summary>The sources changed (a folder cloned to the cloud or got a home here): the sidebar shows it.</summary>
    public event Action? SourcesChanged;
    /// <summary>A clone started: the window has nothing more to do, the clone shows itself in the sidebar.</summary>
    public event Action? CloneStarted;

    // ---- which state ----
    [ObservableProperty] private bool _isLocal;
    [ObservableProperty] private bool _isBackedUp;
    [ObservableProperty] private bool _isShared;
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private bool _isAway;
    [ObservableProperty] private string _awayText = "";

    // ---- connecting ----
    public ObservableCollection<CloudChoice> Destinations { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNewStorage), nameof(WhereText))] private CloudChoice? _destination;
    public bool IsNewStorage => Destination?.Cloud == null;
    /// <summary>Storage connected already (for the library or another folder): offered first.</summary>
    public bool HasClouds { get; }
    public StorageFormViewModel Form { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyAtRisk))] private bool _encrypt;

    // ---- the folder in the storage: a proposal the user may change ----
    /// <summary>Inside a folder named after this computer (two computers on one storage keep their folders apart).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Target), nameof(WhereText))] private bool _underComputer = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Target), nameof(WhereText))] private string _folderName = "";
    public string UnderComputerText => $"Inside a folder named after this computer ({_host.Cloud.Device.MirrorFolder})";
    /// <summary>The folder in the storage, as it will be.</summary>
    public string Target => CloudService.CleanFolder((UnderComputer ? _host.Cloud.Device.MirrorFolder + "/" : "") + FolderName);
    /// <summary>The proposal is kept up to date (a free name) until the user types their own.</summary>
    private bool _nameTyped;
    private string _proposed = "";
    private int _proposal;
    /// <summary>The key of an encrypted folder is kept in the library: a library on this computer only can lose it.</summary>
    public bool KeyAtRisk => Encrypt && _host.Cloud.BackupCloud == null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMoveMode), nameof(ConnectText))] private bool _isBackupMode = true;
    public bool IsMoveMode
    {
        get => !IsBackupMode;
        set => IsBackupMode = !value;
    }
    // A clone is no lasting connection: the words say what happens.
    public string ConnectText => IsBackupMode ? "Back it up" : "Clone it to the cloud";
    public string WhereText => Target.Length == 0 ? "Fill in the name of the folder."
        : Destination?.Cloud is { } cloud ? $"It goes into {cloud.Remote[(cloud.Remote.IndexOf(':') + 1)..].TrimEnd('/')}/{Target}."
        : $"It goes into the folder {Target} of the storage.";

    partial void OnFolderNameChanged(string value)
    {
        if (value != _proposed) _nameTyped = true;
    }

    partial void OnDestinationChanged(CloudChoice? value) => _ = ProposeAsync();
    partial void OnUnderComputerChanged(bool value) => _ = ProposeAsync();
    partial void OnEncryptChanged(bool value) => _ = ProposeAsync();
    partial void OnIsBackupModeChanged(bool value) => _ = ProposeAsync();

    /// <summary>The folder's name, with a number when the storage has that folder for something else.</summary>
    private async Task ProposeAsync()
    {
        if (_nameTyped || !IsLocal) return;
        var mine = ++_proposal;
        var name = DeviceInfo.Safe(SourceName);
        var parent = UnderComputer ? _host.Cloud.Device.MirrorFolder + "/" : "";
        if (Destination?.Cloud is { } cloud)
        {
            try
            {
                var free = await _host.Cloud.FreeFolderAsync(cloud.Id, parent + name, IsBackupMode && !Encrypt, CancellationToken.None);
                name = free[parent.Length..];
            }
            catch (Exception e) when (e is RcloneException or IOException or InvalidOperationException)
            {
                // The storage does not answer now: the name is checked again when connecting.
            }
        }
        if (mine != _proposal || _nameTyped) return;
        _proposed = name;
        FolderName = name;
    }

    // ---- backed up ----
    [ObservableProperty] private string _placeText = "";
    [ObservableProperty] private string _lastText = "";
    [ObservableProperty] private bool _lastFailed;
    [ObservableProperty] private bool _hasGone;
    [ObservableProperty] private string _goneText = "";
    public ObservableCollection<string> GoneFiles { get; } = [];
    /// <summary>A clone was asked for: the files are being copied, then the copy becomes a source of its own.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CardTitle), nameof(CanBackUpNow), nameof(DisconnectText))] private bool _moveWaiting;
    public string CardTitle => MoveWaiting ? "Cloning to the cloud" : "Backup";
    public bool CanBackUpNow => !Stopped && !MoveWaiting;
    public string DisconnectText => MoveWaiting ? "Stop the clone..." : "Stop backing it up...";
    [ObservableProperty] private bool _canMove;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBackUpNow))] private bool _stopped;

    // ---- in the cloud ----
    [ObservableProperty] private string _cloudText = "";
    [ObservableProperty] private string _homeText = "";

    public SourceCloudViewModel(LibraryHost host, long sourceId)
    {
        _host = host;
        _sourceId = sourceId;
        Form = new StorageFormViewModel("medialens-" + host.Cloud.LibraryId);
        foreach (var c in host.Cloud.Clouds())
        {
            var where = c.Remote[(c.Remote.IndexOf(':') + 1)..].Split('/')[0];
            Destinations.Add(new CloudChoice(where.Length > 0 && c.Kind == "s3" ? $"{c.Name}, bucket {where}" : c.Name, c));
        }
        HasClouds = Destinations.Count > 0;
        Destinations.Add(new CloudChoice("Another storage (fill it in below)", null));
        Reload();
        // The library's own storage first, otherwise the first one connected.
        var library = host.Cloud.BackupCloud;
        Destination = Destinations.FirstOrDefault(d => library != null && d.Cloud?.Id == library.Id) ?? Destinations[0];
        host.Mirrors.MirrorsChanged += OnMirrorsChanged;
        host.Mirrors.Copying += OnCopying;
    }

    private void OnMirrorsChanged() => Dispatcher.UIThread.Post(Reload);

    private void OnCopying(long mirrorId, Rclone.CopyProgress p) => Dispatcher.UIThread.Post(() =>
    {
        if (_mirror?.Id == mirrorId && MoveWaiting && !Stopped)
            LastText = $"Copying: {p.Percent}% ({p.Text}). Then the copy becomes a source of its own. This window can be closed.";
    });

    /// <summary>The window closed: no more updates.</summary>
    public void Detach()
    {
        _host.Mirrors.MirrorsChanged -= OnMirrorsChanged;
        _host.Mirrors.Copying -= OnCopying;
    }

    protected override void Reload()
    {
        var source = _host.Repo.ListSources().FirstOrDefault(s => s.Id == _sourceId);
        if (source == null)
        {
            IsLocal = IsBackedUp = IsShared = IsReadOnly = IsAway = false;
            return;
        }
        SourceName = source.Name;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WhereText));
        _mirror = _host.Cloud.Mirrors().FirstOrDefault(m => m.SourceId == _sourceId);
        var local = source.Kind == SourceKinds.Local;
        var here = local && Directory.Exists(source.Root);
        // Done in the background while the window was open: what it said about it is old now.
        if (IsBackedUp && !local) Status = "Cloned: the copy lives in the cloud now as a source of its own, and the folder on this computer is a separate source, as it was.";
        IsBackedUp = local && _mirror != null;
        IsLocal = here && _mirror == null;
        IsAway = local && !here && _mirror == null;
        AwayText = $"The folder {source.Root} is not on this computer now. Connect its disk, or say where it is: right click the folder, Locate folder.";
        IsShared = !local && !source.ReadOnly;
        IsReadOnly = !local && source.ReadOnly;

        var cloudName = _host.Cloud.CloudOf(source)?.Name ?? "the cloud";
        CloudText = $"This folder lives in {cloudName}. Every computer of this library sees it there; what changes in it is read at the next scan.";
        var home = source.HomeName ?? "another computer";
        HomeText = $"This is the backup of a folder on {home}. Only that computer changes it; here it is for looking.";

        if (_mirror is not { } m) return;
        MoveWaiting = _host.Cloud.MoveRequested(m.Id);
        PlaceText = (MoveWaiting ? $"Copying to {m.CloudName}, into the folder {m.Target}" : $"Backed up to {m.CloudName}, in the folder {m.Target}") +
            (m.Encrypted ? ", encrypted." : ".");
        Stopped = !m.Enabled;
        LastFailed = m.LastResult is { } r && r != "OK";
        LastText = Stopped ? CloudWorkViewModel.Plain(m.LastResult ?? "Stopped.")
            : MoveWaiting && m.LastOk && m.Gone > 0 ? "Waiting for you: decide about the deleted files below, then the copy becomes a source of its own."
            : MoveWaiting && !LastFailed ? "The files are copied in the background, then the copy becomes a source of its own. The progress is in the status bar of the main window; this window can be closed."
            : m.LastRunAt is not { } at ? "The first backup is running or starts in a moment. Its progress is in the status bar of the main window; this window can be closed."
            : m.LastOk ? $"Compared with the cloud {When(at)}: everything of the folder is there. It is compared again two minutes after the folder changes, and every hour."
            : $"The last try ({When(at)}) failed: {CloudWorkViewModel.Plain(m.LastResult!)} It is tried again in an hour.";
        HasGone = m.Gone > 0;
        GoneText = $"{m.Gone:N0} {(m.Gone == 1 ? "file was" : "files were")} deleted from the folder but {(m.Gone == 1 ? "is" : "are")} still in the cloud ({Size(m.GoneBytes)}). Nothing is deleted there until you say so.";
        GoneFiles.Clear();
        if (HasGone)
        {
            var gone = _host.Cloud.Gone(m.Id);
            foreach (var g in gone.Take(30)) GoneFiles.Add(g.Path);
            if (gone.Count > 30) GoneFiles.Add($"and {gone.Count - 30:N0} more");
        }
        CanMove = !MoveWaiting && !Stopped;
    }

    private static string When(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("d. M. yyyy H:mm");

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024):N0} KB",
    };

    public string GoneSummary => _mirror is { } m ? $"{m.Gone:N0} {(m.Gone == 1 ? "file" : "files")} ({Size(m.GoneBytes)})" : "";

    [RelayCommand]
    private Task Connect()
    {
        if (IsNewStorage && Form.Missing() is { } missing)
        {
            Status = missing;
            return Task.CompletedTask;
        }
        if (Target.Length == 0)
        {
            Status = "Fill in the name of the folder in the storage.";
            return Task.CompletedTask;
        }
        var target = Target;
        var move = IsMoveMode;
        return RunAsync(IsNewStorage ? Form.ConnectingText : "Connecting...", async ct =>
        {
            var cloud = Destination?.Cloud ?? await Form.ConnectAsync(_host.Cloud, ct);
            var mirror = await _host.Cloud.AddMirrorAsync(_sourceId, cloud.Id, Encrypt, ct, clone: move, target: target);
            if (move) _host.Cloud.RequestMoveToCloud(mirror.Id, true);
            _host.Mirrors.RunNow(mirror.Id);
            Status = move
                ? "Cloning: the files are copied to the cloud in the background, then the copy becomes a source of its own. Nothing here is deleted; the progress is in the status bar of the main window."
                : "Connected. The backup runs in the background; its progress is in the status bar of the main window.";
            SourcesChanged?.Invoke();
            if (move) CloneStarted?.Invoke();
        });
    }

    [RelayCommand]
    private void BackUpNow()
    {
        if (_mirror == null) return;
        _host.Mirrors.RunNow(_mirror.Id);
        Status = "Comparing the folder with the cloud; the progress is in the status bar of the main window.";
    }

    [RelayCommand]
    private Task BringBack() => _mirror is not { } m ? Task.CompletedTask :
        RunAsync("Copying the deleted files back into the folder...", async ct =>
        {
            await _host.Cloud.BringBackGoneAsync(m.Id, new Progress<Rclone.CopyProgress>(p => Status = $"Copying back: {p.Percent}% ({p.Text})"), ct);
            _host.Indexer.RequestScan();
            Status = "The files are back in the folder.";
            SourcesChanged?.Invoke();
        });

    /// <summary>After the user confirmed it.</summary>
    public Task DeleteGoneAsync() => _mirror is not { } m ? Task.CompletedTask :
        RunAsync("Deleting in the cloud...", async ct =>
        {
            await _host.Cloud.DeleteGoneAsync(m.Id, ct);
            Status = "Deleted in the cloud too.";
            SourcesChanged?.Invoke();
        });

    /// <summary>After the user confirmed it: done by the background work once the backup is complete.</summary>
    public void MoveToCloud()
    {
        if (_mirror is not { } m) return;
        _host.Cloud.RequestMoveToCloud(m.Id, true);
        _host.Mirrors.RunNow(m.Id);
        Status = "Cloning: the clone is in the sidebar now, with how far its copy is.";
        Reload();
        SourcesChanged?.Invoke();
        CloneStarted?.Invoke();
    }

    [RelayCommand]
    private void KeepHere()
    {
        if (_mirror is not { } m) return;
        _host.Cloud.RequestMoveToCloud(m.Id, false);
        Status = "No clone: the folder stays here as the main one, backed up.";
        Reload();
    }

    /// <summary>After the user confirmed it.</summary>
    public void Disconnect()
    {
        if (_mirror is not { } m) return;
        Status = MoveWaiting ? "The clone is stopped. What was copied stays in the cloud as it is."
            : "The folder is not backed up any more. Its copy stays in the cloud as it is.";
        _host.Mirrors.Cancel(m.Id);
        _host.Cloud.RequestMoveToCloud(m.Id, false);
        _host.Cloud.RemoveMirror(m.Id);
        Reload();
        SourcesChanged?.Invoke();
    }

    /// <summary>The folder in the cloud is downloaded into this folder, which is its home from now on.</summary>
    public Task MakeHomeAsync(string folder) =>
        RunAsync("Downloading the folder...", async ct =>
        {
            folder = await _host.Cloud.MakeHomeAsync(_sourceId, folder, new Progress<Rclone.CopyProgress>(p => Status = $"Downloading the folder: {p.Percent}% ({p.Text})"), ct);
            _host.Indexer.RequestScan();
            if (_host.Cloud.Mirrors().FirstOrDefault(x => x.SourceId == _sourceId) is { } mirror) _host.Mirrors.RunNow(mirror.Id);
            Status = $"The folder is at home in {folder} now, and its copy in the cloud is its backup.";
            SourcesChanged?.Invoke();
        });
}
