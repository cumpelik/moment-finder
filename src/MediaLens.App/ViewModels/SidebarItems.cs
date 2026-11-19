using Material.Icons;

namespace MediaLens.App.ViewModels;

public enum SidebarKind
{
    All,
    Source,
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

/// <summary>A selectable scope of the grid: the whole library, one source or one bucket.</summary>
public sealed record SidebarEntry(SidebarKind Kind, long Id, string Name, string Count, string ToolTip)
{
    public MaterialIconKind Icon => Kind switch
    {
        SidebarKind.All => MaterialIconKind.ImageMultiple,
        SidebarKind.Source => MaterialIconKind.Folder,
        SidebarKind.Person => MaterialIconKind.AccountOutline,
        SidebarKind.Groups => MaterialIconKind.AccountQuestionOutline,
        SidebarKind.Review => MaterialIconKind.AccountCheckOutline,
        _ => MaterialIconKind.BasketOutline,
    };

    public bool CanRename => Kind is SidebarKind.Bucket or SidebarKind.Person;
    public bool CanRescan => Kind == SidebarKind.Source;
    public bool CanRemove => Kind is SidebarKind.Source or SidebarKind.Bucket or SidebarKind.Person;
    public bool HasMenu => CanRename || CanRescan || CanRemove;
    public string RemoveText => Kind switch
    {
        SidebarKind.Source => "Remove from the library (files stay)",
        SidebarKind.Person => "Forget this person (photos stay)",
        _ => "Delete bucket (photos stay)",
    };
}
