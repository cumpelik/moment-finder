using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using MomentFinder.Cloud;
using MomentFinder.Library;

namespace MomentFinder.App.ViewModels;

/// <summary>Where the browser looks: this computer, or a storage that is connected.</summary>
public sealed record BrowsePlace(string Title, CloudInfo? Cloud)
{
    public override string ToString() => Title;
}

/// <summary>A folder or a file the browser shows.</summary>
public sealed record BrowseEntry(string Name, bool IsFolder, string Detail, MaterialIconKind Icon)
{
    /// <summary>On this computer: where it is.</summary>
    public string? LocalPath { get; init; }
}

/// <summary>
/// What is being dragged out of the browser. Cloud null: files and folders of this computer (their LocalPath);
/// otherwise entries of the folder Remote of that storage, which Where names in words.
/// </summary>
public sealed record BrowseDrag(CloudInfo? Cloud, string Remote, string Where, IReadOnlyList<BrowseEntry> Entries)
{
    /// <summary>Stays inside the application: only its own folders take it.</summary>
    public static readonly DataFormat<BrowseDrag> Format = DataFormat.CreateInProcessFormat<BrowseDrag>("momentfinder-browse");
}

/// <summary>
/// The browser beside a folder in the cloud: this computer's folders and files, or those of another storage. What is
/// dragged from it onto a folder of the library is uploaded (copied) there.
/// </summary>
public sealed partial class FileBrowserViewModel : ObservableObject
{
    private const string PathSetting = "upload_browser_path";
    private readonly LibraryHost _host;
    private string _root = "";
    private int _listing;

    public ObservableCollection<BrowsePlace> Places { get; } = [];
    [ObservableProperty] private BrowsePlace? _place;
    /// <summary>The folder shown: on this computer its full path ("" is the start: the usual folders and the disks); in a storage the path inside it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanGoUp), nameof(PlaceText))] private string _path = "";
    public ObservableCollection<BrowseEntry> Entries { get; } = [];
    [ObservableProperty] private string _status = "";
    public bool CanGoUp => Path.Length > 0;
    public bool IsLocal => Place?.Cloud == null;

    public string PlaceText => IsLocal
        ? Path.Length == 0 ? "This computer" : Path
        : $"{_root[(_root.IndexOf(':') + 1)..]}/{Path}".Trim('/') is { Length: > 0 } inside ? inside : Place!.Title;

    public FileBrowserViewModel(LibraryHost host)
    {
        _host = host;
        Places.Add(new BrowsePlace("This computer", null));
        foreach (var c in host.Cloud.Clouds())
        {
            var where = c.Remote[(c.Remote.IndexOf(':') + 1)..].Split('/')[0];
            Places.Add(new BrowsePlace(where.Length > 0 && c.Kind == "s3" ? $"{c.Name}, bucket {where}" : c.Name, c));
        }
        // Where the user was the last time, when it is still there.
        _path = host.Db.GetSetting(PathSetting) is { Length: > 0 } last && Directory.Exists(last) ? last : "";
        Place = Places[0];
    }

    /// <summary>A storage was connected meanwhile: it is offered, and shown.</summary>
    public void Connected(CloudInfo cloud)
    {
        var place = new BrowsePlace(cloud.Name, cloud);
        Places.Add(place);
        Place = place;
    }

    partial void OnPlaceChanged(BrowsePlace? oldValue, BrowsePlace? newValue)
    {
        OnPropertyChanged(nameof(IsLocal));
        if (newValue == null) return;
        // The first time this computer opens where it was; another place opens at its top.
        if (oldValue != null) Path = "";
        _ = ListAsync(rootChanged: true);
    }

    public Task OpenAsync(BrowseEntry entry)
    {
        if (!entry.IsFolder) return Task.CompletedTask;
        Path = entry.LocalPath ?? (Path.Length == 0 ? entry.Name : $"{Path}/{entry.Name}");
        return ListAsync();
    }

    [RelayCommand]
    private Task Up()
    {
        if (!CanGoUp) return Task.CompletedTask;
        Path = IsLocal ? Directory.GetParent(Path)?.FullName ?? "" : Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
        return ListAsync();
    }

    [RelayCommand]
    private Task Refresh() => ListAsync();

    private async Task ListAsync(bool rootChanged = false)
    {
        var mine = ++_listing;
        var place = Place;
        var path = Path;
        Status = IsLocal ? "" : "Looking into the storage...";
        List<BrowseEntry> entries;
        try
        {
            if (place?.Cloud is { } cloud)
            {
                if (rootChanged || _root.Length == 0) _root = await _host.Cloud.StorageRootAsync(cloud, CancellationToken.None);
                entries = (await _host.Cloud.Rclone.EntriesAsync(CloudService.Join(_root, path), CancellationToken.None))
                    .Where(e => !e.Name.StartsWith('.'))
                    .OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(e => new BrowseEntry(e.Name, e.IsFolder, e.IsFolder ? "" : Rclone.SizeText(e.Size), Icon(e.Name, e.IsFolder))).ToList();
            }
            else
            {
                entries = await Task.Run(() => path.Length == 0 ? Start() : Local(path));
                if (path.Length > 0) _host.Db.SetSetting(PathSetting, path);
            }
        }
        catch (Exception e) when (e is RcloneException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (mine != _listing) return;
            Entries.Clear();
            Status = "It cannot be looked into: " + CloudWorkViewModel.Plain(e.Message);
            return;
        }
        if (mine != _listing) return;
        Entries.Clear();
        foreach (var e in entries) Entries.Add(e);
        OnPropertyChanged(nameof(PlaceText));
        Status = entries.Count == 0 ? "Nothing in this folder." : "";
    }

    private static MaterialIconKind Icon(string name, bool folder) => folder ? MaterialIconKind.FolderOutline : FileTypes.KindOf(name) switch
    {
        MediaKind.Photo => MaterialIconKind.ImageOutline,
        MediaKind.Video => MaterialIconKind.VideoOutline,
        _ => MaterialIconKind.FileOutline,
    };

    /// <summary>Where photos usually are, and the disks.</summary>
    private static List<BrowseEntry> Start()
    {
        var entries = new List<BrowseEntry>();
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile,
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Directory.Exists(path) && entries.All(e => e.LocalPath != path))
                entries.Add(new BrowseEntry(System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path, true, "", MaterialIconKind.FolderOutline) { LocalPath = path });
        }
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) entries.Insert(Math.Min(3, entries.Count), new BrowseEntry("Downloads", true, "", MaterialIconKind.FolderOutline) { LocalPath = downloads });
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.Ram or DriveType.Unknown) continue;
                // On Linux and macOS every mount is a "drive": only real disks and what is plugged in.
                if (!OperatingSystem.IsWindows() && drive.Name != "/" && !drive.Name.StartsWith("/media/", StringComparison.Ordinal) &&
                    !drive.Name.StartsWith("/mnt/", StringComparison.Ordinal) && !drive.Name.StartsWith("/Volumes/", StringComparison.Ordinal))
                    continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) || drive.VolumeLabel == drive.Name ? drive.Name : $"{drive.Name} ({drive.VolumeLabel})";
                entries.Add(new BrowseEntry(label, true, Rclone.SizeText(drive.TotalSize), MaterialIconKind.Harddisk) { LocalPath = drive.RootDirectory.FullName });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A drive that went away while it was asked.
            }
        }
        return entries;
    }

    private static List<BrowseEntry> Local(string path)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var dir = new DirectoryInfo(path);
        var folders = dir.EnumerateDirectories("*", options).Where(d => !LibraryScanner.IsIgnored(d.Name))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new BrowseEntry(d.Name, true, "", MaterialIconKind.FolderOutline) { LocalPath = d.FullName });
        var files = dir.EnumerateFiles("*", options).Where(f => !f.Name.StartsWith('.'))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new BrowseEntry(f.Name, false, Rclone.SizeText(f.Length), Icon(f.Name, false)) { LocalPath = f.FullName });
        return [.. folders, .. files];
    }

    /// <summary>These entries of the folder shown, as they are dragged out.</summary>
    public BrowseDrag Drag(IReadOnlyList<BrowseEntry> entries) =>
        new(Place?.Cloud, IsLocal ? "" : CloudService.Join(_root, Path), PlaceText, entries);
}
