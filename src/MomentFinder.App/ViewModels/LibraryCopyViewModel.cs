using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.Cloud;
using MomentFinder.Data;

namespace MomentFinder.App.ViewModels;

/// <summary>
/// The library of another computer being copied here, as the main window and Settings show it: how far it is, and
/// what to do when it is here or failed. Once it is in place, what became of its folders on this computer, said once.
/// </summary>
public sealed partial class LibraryCopyViewModel : ObservableObject
{
    private readonly LibraryHost _host;
    private readonly LibraryRestore _restore;
    private readonly Database _db;

    /// <summary>The copy is here and the user wants it now: the application starts again.</summary>
    public event Action? RestartRequested;

    /// <summary>Something is being copied, is copied or failed.</summary>
    [ObservableProperty] private bool _active;
    [ObservableProperty] private bool _copying;
    [ObservableProperty] private bool _ready;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _percentKnown;

    /// <summary>A library copied here was put in place at this start: what became of its folders.</summary>
    [ObservableProperty] private bool _restoredShown;
    [ObservableProperty] private string _restoredTitle = "";
    [ObservableProperty] private string _restoredText = "";

    public LibraryCopyViewModel(LibraryHost host)
    {
        _host = host;
        _restore = host.Restore;
        _db = host.Db;
        _restore.Changed += () => Dispatcher.UIThread.Post(Update);
        // A copy that the end of MomentFinder interrupted goes on.
        if (!_restore.Resume()) Update();
        ShowRestored();
    }

    /// <summary>Starts copying a library found with a recovery string.</summary>
    public void Start(Recovery recovery, LibraryRestore.Found backup, bool thumbnails) => _restore.Start(recovery, backup, thumbnails);

    /// <summary>
    /// The library's cloud has the family's shared library, and this computer still uses a library of its own (it
    /// was updated, not joined): its photos, counts and names differ from the others'. Said in the main window at
    /// every start until it joins, not only in Settings.
    /// </summary>
    [ObservableProperty] private bool _joinOffered;
    [ObservableProperty] private string _joinStatus = "";

    public async Task OfferJoinAsync()
    {
        if (_host.Sync.Enabled || _host.Cloud.BackupCloud == null || _restore.State != LibraryRestore.CopyState.None) return;
        try
        {
            JoinOffered = await _host.Sync.SharedLibraryExistsAsync(CancellationToken.None) && _restore.State == LibraryRestore.CopyState.None;
        }
        catch (RcloneException)
        {
            // The cloud does not answer now: asked again at the next start.
        }
    }

    /// <summary>Starts copying the family's shared library from the library's own cloud; false when it is not there.</summary>
    public async Task<bool> JoinAsync(CancellationToken ct)
    {
        if (_host.Cloud.BackupCloud is not { } cloud) return false;
        var recovery = RecoveryString.Parse(await _host.Cloud.RecoveryStringAsync(cloud, ct))!;
        var shared = (await new LibraryRestore(_host.Paths).FindAsync(recovery, ct)).FirstOrDefault(f => f.Shared);
        if (shared == null) return false;
        Start(recovery, shared, thumbnails: false);
        return true;
    }

    [RelayCommand]
    private async Task Join()
    {
        JoinStatus = "Looking for the shared library...";
        try
        {
            if (await JoinAsync(CancellationToken.None))
            {
                JoinOffered = false;
                JoinStatus = "";
            }
            else JoinStatus = "The shared library is not in the cloud any more.";
        }
        catch (Exception e) when (e is RcloneException or IOException)
        {
            JoinStatus = "The shared library cannot be reached now: " + CloudWorkViewModel.Plain(e.Message);
        }
    }

    private void Update()
    {
        var state = _restore.State;
        var computer = _restore.From is { Shared: true } ? "the family (the shared one)"
            : _restore.From?.Computer is { Length: > 0 } name ? name : "the other computer";
        Active = state != LibraryRestore.CopyState.None;
        if (Active) JoinOffered = false;
        Copying = state == LibraryRestore.CopyState.Copying;
        Ready = state == LibraryRestore.CopyState.Ready;
        Failed = state == LibraryRestore.CopyState.Failed;
        var step = _restore.Now;
        PercentKnown = step is { Percent: >= 0 };
        Percent = step?.Percent ?? 0;
        (Title, Detail) = state switch
        {
            LibraryRestore.CopyState.Copying => (
                step == null ? $"Copying the library of {computer} to this computer..."
                    : $"Copying the library of {computer} to this computer, part {step.Number} of {step.Of}: {step.What}.",
                (step is { Detail.Length: > 0 } ? step.Detail + ". " : "") +
                "You can go on working here, and even close MomentFinder: the copy goes on when it starts again."),
            LibraryRestore.CopyState.Ready when _restore.From is { Shared: true } => (
                "The family's shared library is here.",
                "MomentFinder opens it when it starts again. This computer's own folders and what you named here come along, also what you change until then."),
            LibraryRestore.CopyState.Ready => (
                $"The library of {computer} is here.",
                "MomentFinder opens it when it starts again. Until then you work in the library that was here; what you change in it now does not go over."),
            LibraryRestore.CopyState.Failed => (
                $"Copying the library of {computer} stopped.",
                CloudWorkViewModel.Plain(_restore.Error ?? "") + " What came already is kept: another try goes on from there."),
            _ => ("", ""),
        };
    }

    [RelayCommand]
    private void Stop() => _restore.Stop();

    [RelayCommand]
    private void Retry() => _restore.Retry();

    [RelayCommand]
    private void Discard() => _restore.Discard();

    [RelayCommand]
    private void Restart() => RestartRequested?.Invoke();

    private void ShowRestored()
    {
        if (LibraryRestore.LastRestored(_db) is not { } r) return;
        if (r.Shared)
        {
            ShowJoined(r);
            return;
        }
        var computer = r.Computer.Length > 0 ? r.Computer : "the other computer";
        RestoredTitle = $"This is the library of {computer} now, as it was backed up on {r.When.LocalDateTime:d. M. yyyy H:mm}.";
        var lines = new List<string>();
        if (r.FromCloud > 0)
            lines.Add($"{Folders(r.FromCloud)} of {computer} {(r.FromCloud == 1 ? "is" : "are")} shown from {(r.FromCloud == 1 ? "its backup" : "their backups")} in the cloud, " +
                      "only for looking: a photo downloads when you open it, and what is backed up there later comes here at each start.");
        if (r.Away > 0)
            lines.Add($"{Folders(r.Away)} {(r.Away == 1 ? "is" : "are")} not backed up to a cloud, so only {computer} has {(r.Away == 1 ? "its" : "their")} photos: " +
                      $"{(r.Away == 1 ? "it says" : "they say")} \"not on this computer\".");
        lines.Add("Folders of this computer are added with + in the sidebar, as usual.");
        lines.Add($"From now on each computer keeps its own library: names and buckets made here do not show on {computer}, nor the other way round.");
        lines.Add($"The library is kept in the cloud by {computer}, not by this computer. If this one is your main computer now, turn it on in Settings.");
        if (r.ReplacedOwn) lines.Add("The library that was here before is kept beside it, as library.before-restore.db.");
        RestoredText = string.Join("\n", lines);
        RestoredShown = true;
    }

    private static string Folders(int n) => n == 1 ? "1 folder" : $"{n} folders";

    /// <summary>This computer joined the family's shared library.</summary>
    private void ShowJoined(LibraryRestore.Restored r)
    {
        RestoredTitle = "This computer uses the family's shared library now.";
        var lines = new List<string>
        {
            "Its folders in the cloud show here, the same as on the other computers. The folders of another computer are only for " +
            "looking; a photo downloads when you open it. Thumbnails and search data come from the cloud as they are needed.",
            "Names, who is who and buckets are the same everywhere: what anybody changes shows on the others within a minute.",
        };
        if (r.OwnFolders > 0)
            lines.Add($"{Folders(r.OwnFolders)} of this computer came along with everything found in {(r.OwnFolders == 1 ? "it" : "them")}; " +
                      "only its search data is made again. Back a folder up to the cloud (its cloud button) and the others see it too.");
        if (r.Kept > 0) lines.Add("What you named here or put in buckets before stays, and went to the other computers.");
        if (r.ReplacedOwn) lines.Add("The library this computer had before is kept beside it, as library.before-restore.db.");
        RestoredText = string.Join("\n", lines);
        RestoredShown = true;
    }

    [RelayCommand]
    private void DismissRestored()
    {
        RestoredShown = false;
        LibraryRestore.ForgetRestored(_db);
    }
}
