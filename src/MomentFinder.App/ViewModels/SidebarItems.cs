using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;

namespace MomentFinder.App.ViewModels;

public enum SidebarKind
{
    All,
    /// <summary>Videos with moments worth watching: somebody named, big, for a few seconds.</summary>
    Best,
    Source,
    /// <summary>A folder being cloned to the cloud: there already, its files on their way (the id is the folder's source).</summary>
    Clone,
    Bucket,
    Person,
    Groups,
    Review,
}

/// <summary>A section title of the sidebar with its add button.</summary>
public sealed record SidebarHeader(string Title, string AddToolTip, SidebarKind Section)
{
    public bool CanAdd => AddToolTip.Length > 0;
}

/// <summary>How far a clone is: changes while the sidebar stays as it is.</summary>
public sealed partial class CloneProgress : ObservableObject
{
    [ObservableProperty] private string _text = "";
}

/// <summary>A selectable scope of the grid: the whole library, one source or one bucket.</summary>
public sealed record SidebarEntry(SidebarKind Kind, long Id, string Name, string Count, string ToolTip)
{
    /// <summary>A source in a cloud shows a cloud instead of a folder.</summary>
    public MaterialIconKind? IconOverride { get; init; }

    public MaterialIconKind Icon => IconOverride ?? Kind switch
    {
        SidebarKind.All => MaterialIconKind.ImageMultiple,
        SidebarKind.Best => MaterialIconKind.StarOutline,
        SidebarKind.Source => MaterialIconKind.Folder,
        SidebarKind.Clone => MaterialIconKind.CloudUploadOutline,
        SidebarKind.Person => MaterialIconKind.AccountOutline,
        SidebarKind.Groups => MaterialIconKind.AccountQuestionOutline,
        SidebarKind.Review => MaterialIconKind.AccountCheckOutline,
        _ => MaterialIconKind.BasketOutline,
    };

    /// <summary>A local folder that is not on this computer now (a disk not connected, a library from another computer).</summary>
    public bool IsAway { get; init; }

    /// <summary>The button of a source that opens its window about the cloud, showing the state of its backup.</summary>
    public bool HasCloudButton => Kind is SidebarKind.Source or SidebarKind.Clone;
    /// <summary>A copy on its way (a clone, a folder being added here): shown instead of the count.</summary>
    public CloneProgress? Progress { get; init; }
    public bool HasProgress => Progress != null;
    public bool HasCount => Progress == null;
    public MaterialIconKind CloudIcon { get; init; } = MaterialIconKind.CloudArrowUpOutline;
    public string CloudTip { get; init; } = "";
    /// <summary>The backup waits for the user (deleted files, a failure, stopped).</summary>
    public bool CloudAttention { get; init; }
    /// <summary>Backed up and all is well.</summary>
    public bool CloudDone { get; init; }
    /// <summary>Not connected: the button is there, quietly.</summary>
    public bool CloudIdle { get; init; }

    /// <summary>A folder that lives in the cloud and can be changed from here: files can be put into it.</summary>
    public bool CanUpload { get; init; }
    public bool CanRename => Kind is SidebarKind.Bucket or SidebarKind.Person;
    public bool CanRescan => Kind == SidebarKind.Source;
    public bool CanRemove => Kind is SidebarKind.Bucket or SidebarKind.Person || Kind == SidebarKind.Source && !IsLocked;

    // ---- read-only (ReadOnlyLock) ----
    /// <summary>Read-only now: nothing in it changes from MomentFinder.</summary>
    public bool IsLocked { get; init; }
    /// <summary>Read-only, but turned off with the password until it is turned on again or the app ends.</summary>
    public bool IsTurnedOff { get; init; }
    public bool CanMakeReadOnly => Kind == SidebarKind.Source && !IsLocked && !IsTurnedOff;
    public bool HasMenu => CanRename || CanRescan || CanRemove || HasCloudButton;
    public string RemoveText => Kind switch
    {
        SidebarKind.Source => "Remove from the library (files stay)",
        SidebarKind.Person => "Forget this person (photos stay)",
        _ => "Delete bucket (photos stay)",
    };
}
