using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.App.Services;
using MomentFinder.Cloud;

namespace MomentFinder.App.ViewModels;

/// <summary>A file that did not go, and why.</summary>
public sealed record FailedFileRow(string Name, string Why);

/// <summary>One upload, copy, move or delete in the cloud, as the list shows it.</summary>
public sealed partial class JobRow(long id) : ObservableObject
{
    public long Id { get; } = id;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _running;
    /// <summary>Something did not go: it shows in orange.</summary>
    [ObservableProperty] private bool _attention;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private string _retryText = "Try again";
    [ObservableProperty] private bool _canRemove;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private string _moreFailed = "";
    public ObservableCollection<FailedFileRow> Failed { get; } = [];
    /// <summary>What the running job last said of itself.</summary>
    public string? Progress { get; set; }
}

/// <summary>
/// What the user asked to be done in the cloud (uploads, copies from another storage, moves, deletes): the list with
/// how far each is and what did not go, and the one line of it the main window always shows.
/// </summary>
public sealed partial class CloudJobsViewModel : ObservableObject
{
    private const int FailedShown = 100;
    private readonly CloudJobs _jobs;
    private readonly DispatcherTimer _reload;

    public ObservableCollection<JobRow> Rows { get; } = [];

    /// <summary>There is something on the list: the main window shows its line.</summary>
    [ObservableProperty] private bool _hasJobs;
    /// <summary>What runs and how far, or what waits for the user: said above the photos.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BarText))] private string _statusText = "";
    /// <summary>The same in the status bar, where the room is short: how far first, then what.</summary>
    public string BarText => _runningTitle is { } title && _runningText is { } text ? $"{text} ({title})" : StatusText;
    private string? _runningTitle;
    private string? _runningText;
    [ObservableProperty] private bool _busy;
    /// <summary>How far the running one is, 0 to 100.</summary>
    [ObservableProperty] private double _percent;
    /// <summary>Something did not go and waits for the user: said above the photos too, until it is looked at.</summary>
    [ObservableProperty] private bool _attention;
    [ObservableProperty] private string _attentionText = "";
    [ObservableProperty] private bool _hasDone;

    public CloudJobsViewModel(CloudJobs jobs)
    {
        _jobs = jobs;
        // A running job reports many times a second: the list is read again a moment after the last change.
        _reload = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _reload!.Stop();
            _ = ReloadAsync();
        });
        jobs.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (!_reload.IsEnabled) _reload.Start();
        });
        jobs.Progress += (id, p) => Dispatcher.UIThread.Post(() => OnProgress(id, p));
        Show(Read());
    }

    private bool _reading;
    private bool _again;

    /// <summary>The list as it is now. Read off this thread: a job of a hundred thousand files takes a moment to count.</summary>
    public async Task ReloadAsync()
    {
        if (_reading)
        {
            _again = true;
            return;
        }
        _reading = true;
        try
        {
            do
            {
                _again = false;
                Show(await Task.Run(Read));
            } while (_again);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            // The library is busy or closing: the next change reads again.
        }
        finally
        {
            _reading = false;
        }
    }

    private List<(CloudJob Job, List<CloudJobFile> Failed)> Read() =>
        _jobs.List().Select(j => (j, j.Failed == 0 ? [] : _jobs.FailedFiles(j.Id, FailedShown))).ToList();

    private void OnProgress(long id, JobProgress p)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is not { } row) return;
        row.Progress = p.Text;
        row.Percent = p.Percent;
        if (!row.Running) return;
        row.StateText = p.Text;
        Percent = p.Percent;
        (_runningTitle, _runningText) = (row.Title, p.Text);
        StatusText = $"{row.Title}: {p.Text}";
    }

    private static string Done(CloudJob j) => j.Kind switch
    {
        JobKinds.Upload => "uploaded",
        JobKinds.Import => "copied",
        JobKinds.Move => "moved",
        _ => "deleted",
    };

    private static string StateText(CloudJob j, JobRow row)
    {
        var files = Format.Count(j.Total, "file", "files");
        switch (j.State)
        {
            case JobStates.Running:
                return row.Progress ?? "Starting...";
            case JobStates.Done when j.Total == 0:
                return "Nothing to do: there were no files.";
            case JobStates.Done:
                return $"Done: {files} {Done(j)}" + (j.Same == 1 ? " (1 of them was there already)." : j.Same > 0 ? $" ({j.Same:N0} of them were there already)." : ".") +
                       (j.Kind == JobKinds.Import ? " The folder is read after that: its photos show as they are found." : "");
            case JobStates.Stopped:
                return j.Prepared ? $"Stopped. {j.Done:N0} of {files} were {Done(j)} and stay so; press Go on for the rest." : "Stopped before it began.";
            case JobStates.Failed when j.Failed > 0:
                return $"{Format.Count(j.Failed, "file", "files")} of {j.Total:N0} could not be {Done(j)}, also when tried again. The reasons are below. " +
                       (j.Done == 0 ? "" : j.Done == 1 ? "The other one is done. " : $"The other {j.Done:N0} are done. ") + "Press Try again when the reason is gone.";
            case JobStates.Failed:
                return CloudWorkViewModel.Plain(j.Message ?? "It could not be done.");
            default:
                if (j.Message != null) return $"Could not go on: {CloudWorkViewModel.Plain(j.Message)} It is tried again by itself in a few minutes.";
                if (j.Failed > 0) return $"{Format.Count(j.Failed, "file", "files")} of {j.Total:N0} did not go (the reasons are below). Tried again in a moment.";
                return j.Attempts > 0 || j.Done > 0 ? "Waits to go on." : "Waits for its turn.";
        }
    }

    /// <summary>Why a file did not go, in words; what the storage or the system said stays behind it.</summary>
    private static string Why(string? error)
    {
        var e = (error ?? "").Trim();
        foreach (var prefix in new[] { "Failed to copy: ", "Failed to move: ", "Couldn't delete: ", "Couldn't move: " })
            if (e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) e = e[prefix.Length..];
        bool Has(string part) => e.Contains(part, StringComparison.OrdinalIgnoreCase);
        var meaning =
            Has("not on this computer") ? null
            : Has("permission denied") || Has("Access is denied") ? "It cannot be read: no permission."
            : Has("being used by another process") || Has("sharing violation") ? "Another program holds the file: close it there."
            : Has("no such file") || Has("cannot find") || Has("object not found") ? "It is not there any more."
            : Has("file name too long") || Has("path too long") ? "Its name with the folders is too long for the storage."
            : null;
        return meaning == null ? CloudWorkViewModel.Plain(e) : $"{meaning} ({e})";
    }

    private void Show(List<(CloudJob Job, List<CloudJobFile> Failed)> read)
    {
        var jobs = read.Select(r => r.Job).ToList();
        var failedOf = read.ToDictionary(r => r.Job.Id, r => r.Failed);
        foreach (var gone in Rows.Where(r => jobs.All(j => j.Id != r.Id)).ToList()) Rows.Remove(gone);
        // The newest first: what was just asked for is at the top.
        var at = 0;
        foreach (var j in jobs.OrderByDescending(j => j.Id))
        {
            var row = Rows.FirstOrDefault(r => r.Id == j.Id);
            if (row == null) Rows.Insert(at, row = new JobRow(j.Id));
            at++;
            row.Title = j.Title;
            row.Running = j.State == JobStates.Running;
            if (!row.Running) row.Progress = null;
            row.Done = j.State == JobStates.Done;
            row.Attention = j.State == JobStates.Failed || j.State == JobStates.Waiting && (j.Message != null || j.Failed > 0);
            row.StateText = StateText(j, row);
            if (!row.Running) row.Percent = j.Total == 0 ? 0 : 100.0 * j.Done / j.Total;
            row.CanStop = j.IsOpen;
            row.CanRetry = j.State is JobStates.Failed or JobStates.Stopped || j.State == JobStates.Waiting && j.TriedAt != null;
            row.RetryText = j.State == JobStates.Stopped ? "Go on" : j.State == JobStates.Waiting ? "Try now" : "Try again";
            row.CanRemove = j.State != JobStates.Running;
            var failed = failedOf[j.Id];
            if (!failed.Select(f => f.Origin).SequenceEqual(row.Failed.Select(f => f.Name)))
            {
                row.Failed.Clear();
                foreach (var f in failed) row.Failed.Add(new FailedFileRow(f.Origin, Why(f.Error)));
            }
            row.HasFailed = row.Failed.Count > 0;
            row.MoreFailed = j.Failed > failed.Count ? $"and {j.Failed - failed.Count:N0} more" : "";
        }

        HasJobs = jobs.Count > 0;
        HasDone = jobs.Any(j => j.State == JobStates.Done);
        var running = Rows.FirstOrDefault(r => r.Running);
        var waiting = jobs.Count(j => j.State == JobStates.Waiting);
        var broken = jobs.Where(j => j.State == JobStates.Failed || j.State == JobStates.Waiting && j.Message != null).ToList();
        Busy = running != null;
        Percent = running?.Percent ?? 0;
        (_runningTitle, _runningText) = (running?.Title, running?.Progress);
        // Files that did not go and are tried again by themselves: said, but nothing to do for the user yet.
        var again = jobs.FirstOrDefault(j => j.State == JobStates.Waiting && j.Message == null && j.Failed > 0);
        Attention = broken.Count > 0;
        AttentionText = broken.Count == 0 ? ""
            : broken.Count == 1 ? $"{broken[0].Title}: " + (broken[0].Failed > 0
                ? $"{Format.Count(broken[0].Failed, "file", "files")} could not be {Done(broken[0])}."
                : "it could not go on.")
            : $"{broken.Count} uploads or changes in the cloud could not be finished.";
        StatusText = running != null ? $"{running.Title}: {running.StateText}"
            : Attention ? AttentionText + " Click to see why."
            : again != null ? $"{again.Title}: {Format.Count(again.Failed, "file", "files")} did not go, tried again in a moment. Click to see why."
            : waiting > 0 ? $"{Format.Count(waiting, "upload or change in the cloud waits", "uploads or changes in the cloud wait")} to go on."
            : "Uploads and changes in the cloud: all done.";
    }

    [RelayCommand]
    private void Stop(JobRow row) => _jobs.Stop(row.Id);

    [RelayCommand]
    private void Retry(JobRow row) => _jobs.Retry(row.Id);

    [RelayCommand]
    private void Remove(JobRow row) => _jobs.Remove(row.Id);

    [RelayCommand]
    private void ClearDone() => _jobs.ClearDone();
}
