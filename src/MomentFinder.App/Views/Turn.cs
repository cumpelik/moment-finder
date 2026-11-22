using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MomentFinder.App.Views;

/// <summary>A picture the user turned (MediaItem.Rotation, degrees clockwise) is laid out turned: for LayoutTransformControl.</summary>
public static class Turn
{
    public static readonly IValueConverter ToTransform =
        new FuncValueConverter<int, ITransform?>(rotation => rotation == 0 ? null : new RotateTransform(rotation));
}
