using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.App.Services;
using MomentFinder.Cloud;

namespace MomentFinder.App.ViewModels;

public enum PutKind
{
    /// <summary>Files and folders of this computer.</summary>
    Upload,
    /// <summary>A folder of another storage.</summary>
    Import,
    /// <summary>Photos and videos of the library that are in the cloud.</summary>
    MoveFiles,
    /// <summary>A whole folder of a source in the cloud.</summary>
    MoveFolder,
}

/// <summary>A place files can go to: a folder of the library that lives in the cloud, or a folder inside it.</summary>
public sealed partial class PlaceNode(long sourceId, string folder, string title, string sourceName) : ObservableObject
{
    public long SourceId { get; } = sourceId;
    /// <summary>Inside the source; "" is its top.</summary>
    public string Folder { get; } = folder;
    public string Title { get; } = title;
    public string SourceName { get; } = sourceName;
    public bool IsSource => Folder.Length == 0;
    public ObservableCollection<PlaceNode> Children { get; } = [];
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>
/// What goes where in the cloud, before it starts: files of this computer or a folder of another storage uploaded
/// into a folder of the library that lives in the cloud, or photos and folders of the library moved to another
/// place there. The work itself is a job in the background (CloudJobs); this window only says what and where.
/// </summary>
public sealed partial class CloudPutViewModel : CloudWorkViewModel
{
    private readonly LibraryHost _host;
    private IReadOnlyList<string> _paths = [];
    private IReadOnlyList<(long SourceId, string RelPath)> _files = [];
    private long _fromId;
    private string _fromFolder = "";

    public PutKind Kind { get; }
    public string Title => Kind switch
    {
        PutKind.Upload => "Upload to the cloud",
        PutKind.Import => "Copy from another storage",
        PutKind.MoveFiles => "Move to another folder",
        _ => "Move the folder",
    };
    public string ActText => Kind switch
    {
        PutKind.Upload => "Upload",
        PutKind.Import => "Copy",
        _ => "Move",
    };
    public bool IsImport => Kind == PutKind.Import;
    public bool HasWhat => !IsImport;
    public string WhereTitle => IsImport ? "3. Where it goes" : "Where it goes";

    /// <summary>What was chosen, in words.</summary>
    public string WhatText { get; } = "";

    /// <summary>The other storage and the folder in it (the same browsing as adding a folder of a storage to the library).</summary>
    public AddCloudFolderViewModel Origin { get; }

    public ObservableCollection<PlaceNode> Places { get; } = [];
    public bool NoPlaces => Places.Count == 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WhereText), nameof(CanAct))] private PlaceNode? _selectedPlace;
    /// <summary>A new folder inside the chosen place (may have several parts: "2024/Ski").</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WhereText), nameof(CanAct))] private string _newFolder = "";
    private bool _newFolderTyped;
    private string _proposed = "";

    /// <summary>The job was added: the window closes, the main window shows how far it is.</summary>
    public event Action? Started;

    private CloudPutViewModel(LibraryHost host, PutKind kind, string what, long? sourceId, string? folder)
    {
        _host = host;
        Kind = kind;
        WhatText = what;
        Origin = new AddCloudFolderViewModel(host, browse: kind == PutKind.Import);
        Origin.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(AddCloudFolderViewModel.Path) or nameof(AddCloudFolderViewModel.PlaceText))) return;
            // The folder gets its name here too, until the user types another.
            if (IsImport && !_newFolderTyped)
            {
                _proposed = Origin.Path.Length == 0 ? "" : Origin.Path[(Origin.Path.LastIndexOf('/') + 1)..];
                NewFolder = _proposed;
            }
            OnPropertyChanged(nameof(WhereText));
            OnPropertyChanged(nameof(CanAct));
        };
        foreach (var source in host.Jobs.Changeable())
        {
            var top = new PlaceNode(source.Id, "", source.Name, source.Name);
            var nodes = new Dictionary<string, PlaceNode> { [""] = top };
            // Sorted: a folder comes after the one it is in.
            foreach (var path in host.Repo.Folders(source.Id))
            {
                var slash = path.LastIndexOf('/');
                var node = new PlaceNode(source.Id, path, path[(slash + 1)..], source.Name);
                nodes[slash < 0 ? "" : path[..slash]].Children.Add(node);
                nodes[path] = node;
            }
            Places.Add(top);
            if (source.Id != sourceId) continue;
            // Where the user is: chosen, with the folders above it open.
            var wanted = folder ?? "";
            while (wanted.Length > 0 && !nodes.ContainsKey(wanted)) wanted = wanted.Contains('/') ? wanted[..wanted.LastIndexOf('/')] : "";
            SelectedPlace = nodes[wanted];
            for (var up = wanted; ; up = up.Contains('/') ? up[..up.LastIndexOf('/')] : "")
            {
                nodes[up].IsExpanded = true;
                if (up.Length == 0) break;
            }
        }
        if (Places.Count == 1) Places[0].IsExpanded = true;
        SelectedPlace ??= Places.Count == 1 ? Places[0] : null;
    }

    public static CloudPutViewModel Upload(LibraryHost host, IReadOnlyList<string> paths, long? sourceId, string? folder)
    {
        var folders = paths.Count(Directory.Exists);
        var names = string.Join(", ", paths.Take(4).Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p)))) + (paths.Count > 4 ? ", ..." : "");
        var what = (folders == 0 ? Format.Count(paths.Count, "file", "files")
            : folders == paths.Count ? Format.Count(folders, "folder", "folders") + " with everything inside"
            : $"{Format.Count(folders, "folder", "folders")} with everything inside and {Format.Count(paths.Count - folders, "file", "files")}") +
            $" from this computer: {names}";
        return new CloudPutViewModel(host, PutKind.Upload, what, sourceId, folder) { _paths = paths };
    }

    public static CloudPutViewModel Import(LibraryHost host, long? sourceId, string? folder) => new(host, PutKind.Import, "", sourceId, folder);

    public static CloudPutViewModel MoveFiles(LibraryHost host, IReadOnlyList<(long SourceId, string RelPath)> files, long? sourceId, string? folder) =>
        new(host, PutKind.MoveFiles, Format.Count(files.Count, "selected photo or video", "selected photos and videos"), sourceId, folder) { _files = files };

    public static CloudPutViewModel MoveFolder(LibraryHost host, long fromId, string folder)
    {
        var source = host.Repo.ListSources().First(s => s.Id == fromId);
        var parent = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "";
        return new CloudPutViewModel(host, PutKind.MoveFolder, $"The folder {folder.Replace("/", " / ")} of {source.Name}, with everything inside", fromId, parent)
        {
            _fromId = fromId,
            _fromFolder = folder,
        };
    }

    protected override void Reload() { }

    partial void OnNewFolderChanged(string value)
    {
        if (value != _proposed) _newFolderTyped = true;
    }

    /// <summary>The folder inside the source everything goes into.</summary>
    private string Into => SelectedPlace is not { } place ? "" : CloudService.Join(place.Folder, CloudService.CleanFolder(NewFolder));

    private static string Words(PlaceNode place, string folder) => folder.Length == 0 ? place.SourceName : $"{place.SourceName} / {folder.Replace("/", " / ")}";

    /// <summary>A move to where it is now: nothing to do, another place has to be chosen.</summary>
    private bool AlreadyThere => SelectedPlace is { } place && Kind switch
    {
        PutKind.MoveFolder => place.SourceId == _fromId && CloudService.Join(Into, Name(_fromFolder)) == _fromFolder,
        PutKind.MoveFiles => _files.All(f => f.SourceId == place.SourceId && CloudService.Join(Into, Name(f.RelPath)) == f.RelPath),
        _ => false,
    };

    public bool CanAct => SelectedPlace != null && !AlreadyThere && (!IsImport || Origin.CanAdd);

    public string WhereText
    {
        get
        {
            if (Places.Count == 0)
                return "The library has no folder in the cloud that can be changed from here (a backup of a computer's folder is changed on that computer; a read-only folder after read-only is turned off).";
            if (SelectedPlace is not { } place) return "Click the folder it should go into.";
            if (AlreadyThere) return $"That is where {(Kind == PutKind.MoveFolder || _files.Count == 1 ? "it is" : "they are")} now. Click the folder to move into, above.";
            var into = Words(place, Into);
            var never = " Nothing there is written over: the same file is left as it is, another file of the same name gets a number.";
            return Kind switch
            {
                PutKind.Upload => $"It goes into {into}. A folder keeps its name and what is inside it." + never,
                PutKind.Import => (Origin.CanAdd ? $"What is in {Origin.PlaceText} is copied into {into}." : $"It is copied into {into}.") +
                                  " The other storage stays as it is." + never,
                PutKind.MoveFiles => $"They move into {into}, with everything known of them (people, buckets)." + never,
                _ => $"It becomes {Words(place, CloudService.Join(Into, Name(_fromFolder)))}, with everything known of its photos (people, buckets)." + never,
            };
        }
    }

    private static string Name(string folder) => folder[(folder.LastIndexOf('/') + 1)..];

    [RelayCommand]
    private void Act()
    {
        if (SelectedPlace is not { } place)
        {
            Status = "Click the folder it should go into.";
            return;
        }
        try
        {
            switch (Kind)
            {
                case PutKind.Upload:
                    _host.Jobs.Upload(place.SourceId, Into, _paths);
                    break;
                case PutKind.Import:
                    if (!Origin.CanAdd)
                    {
                        Status = "Go into the folder to copy (click it).";
                        return;
                    }
                    _host.Jobs.Import(place.SourceId, Into, Origin.Remote, $"{Origin.PlaceText} ({Origin.StorageName})");
                    break;
                case PutKind.MoveFiles:
                    foreach (var from in _files.GroupBy(f => f.SourceId))
                        _host.Jobs.Move(from.Key, from.Select(f => f.RelPath).ToList(), place.SourceId, Into);
                    break;
                case PutKind.MoveFolder:
                    _host.Jobs.MoveFolder(_fromId, _fromFolder, place.SourceId, CloudService.Join(Into, Name(_fromFolder)));
                    break;
            }
        }
        catch (InvalidOperationException e)
        {
            Status = e.Message;
            return;
        }
        Started?.Invoke();
    }
}
