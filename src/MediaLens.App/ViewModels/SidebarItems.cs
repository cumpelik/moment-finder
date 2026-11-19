using Material.Icons;

namespace MediaLens.App.ViewModels;

public enum SidebarKind
{
    All,
    Source,
    Bucket,
}

/// <summary>A section title of the sidebar with its add button.</summary>
public sealed record SidebarHeader(string Title, string AddToolTip, SidebarKind Section);

/// <summary>A selectable scope of the grid: the whole library, one source or one bucket.</summary>
public sealed record SidebarEntry(SidebarKind Kind, long Id, string Name, string Count, string ToolTip)
{
    public MaterialIconKind Icon => Kind switch
    {
        SidebarKind.All => MaterialIconKind.ImageMultiple,
        SidebarKind.Source => MaterialIconKind.Folder,
        _ => MaterialIconKind.BasketOutline,
    };
}
