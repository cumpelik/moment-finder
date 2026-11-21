using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MomentFinder.App.ViewModels;

namespace MomentFinder.App.Views;

/// <summary>
/// The thumbnails, plain or in titled groups, in one list. Every tile has the same size, so where any item
/// sits is known without creating it: only what is near the screen exists, and dragging the scroll bar
/// anywhere in millions of items costs as much as scrolling one row.
/// </summary>
public sealed class TileGridLayout : VirtualizingLayout
{
    public enum Arrangement
    {
        /// <summary>Rows of tiles of the slider's size, filling the width.</summary>
        Grid,
        /// <summary>One row scrolling sideways (the strip above the viewer), the tiles as high as it.</summary>
        Row,
        /// <summary>One column (beside the viewer), the tiles as wide as it.</summary>
        Column,
    }

    private const double Gap = 6;
    private const double TitleTop = 4, TitleHeight = 24, TitleGap = 6;
    private const double GroupGap = 14, RowGroupGap = 20;

    private Arrangement _arrangement;
    private double _tileSize = 180;
    private TileGrid _items = TileGrid.Empty;

    public Arrangement Mode
    {
        get => _arrangement;
        set
        {
            if (_arrangement == value) return;
            _arrangement = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The smallest tile in the grid; the tiles of a row stretch to fill it.</summary>
    public double TileSize
    {
        get => _tileSize;
        set
        {
            if (_tileSize == value) return;
            _tileSize = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The same list as the repeater's ItemsSource; set together with it.</summary>
    public TileGrid Items
    {
        get => _items;
        set
        {
            _items = value;
            InvalidateMeasure();
        }
    }

    /// <summary>Tiles in one row of the grid, for Up and Down.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>
    /// The tile a row up (-1) or down (+1) from the item, in the same column as far as the row reaches; from the
    /// first or last row of a group into the next group. In one row or one column it is the tile before or after.
    /// </summary>
    public int RowStep(int index, int direction)
    {
        var (group, k) = _items.Locate(index);
        if (k < 0) return index;
        var n = _items.GroupSize(group);
        var row = k / Columns;
        var column = k % Columns;
        if (direction < 0)
        {
            if (row > 0) return _items.TileIndex(group, k - Columns);
            if (group == 0) return index;
            var above = _items.GroupSize(group - 1);
            return _items.TileIndex(group - 1, Math.Min((above - 1) / Columns * Columns + column, above - 1));
        }
        if ((row + 1) * Columns <= n - 1) return _items.TileIndex(group, Math.Min(k + Columns, n - 1));
        if (group == _items.GroupCount - 1) return index;
        return _items.TileIndex(group + 1, Math.Min(column, _items.GroupSize(group + 1) - 1));
    }

    // Laid out at the last measure: where each group starts along the scrolling direction, the tile size.
    private double[] _starts = [];
    private double _tileWidth, _tileHeight, _across;
    private TileGrid? _laidOut;

    private readonly Dictionary<int, (Layoutable Element, Rect Rect)> _realized = [];

    private bool Sideways => _arrangement == Arrangement.Row;
    private double Head => !_items.Titled ? 0 : Sideways ? TitleHeight + TitleGap : TitleTop + TitleHeight + TitleGap;

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object? source, NotifyCollectionChangedEventArgs args)
    {
        RecycleAll(context);
        InvalidateMeasure();
    }

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context) => RecycleAll(context);

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        // Another list: the elements made for the old one stand for other items.
        if (!ReferenceEquals(_laidOut, _items)) RecycleAll(context);
        _laidOut = _items;
        if (_items.Count != context.ItemCount)
        {
            RecycleAll(context);
            return default;
        }
        var extent = Lay(availableSize);

        var wanted = new Dictionary<int, Rect>();
        var window = context.RealizationRect;
        var (from, to) = Sideways ? (window.Left, window.Right) : (window.Top, window.Bottom);
        if (window.Width > 0 && window.Height > 0) Visible(from, to, wanted);
        // An item asked for from code (scrolled to by the keyboard): it gets its place too.
        if (context.RecommendedAnchorIndex is var anchor and >= 0 && anchor < _items.Count && !wanted.ContainsKey(anchor))
            wanted[anchor] = RectOf(anchor);

        foreach (var (index, (element, _)) in _realized.ToList())
            if (!wanted.ContainsKey(index))
            {
                Recycle(context, element);
                _realized.Remove(index);
            }
        foreach (var (index, rect) in wanted)
        {
            var element = context.GetOrCreateElementAt(index);
            element.Measure(rect.Size);
            _realized[index] = (element, rect);
        }
        return extent;
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        foreach (var (element, rect) in _realized.Values) element.Arrange(rect);
        return finalSize;
    }

    /// <summary>Where the item is (relative to the repeater), as of the last layout.</summary>
    public Rect? Place(int index) =>
        ReferenceEquals(_laidOut, _items) && index >= 0 && index < _items.Count && _starts.Length == _items.GroupCount ? RectOf(index) : null;

    /// <summary>Sizes the tiles for the space and places the groups one after another; returns the whole size.</summary>
    private Size Lay(Size available)
    {
        if (Sideways)
        {
            _across = double.IsFinite(available.Height) ? available.Height : _tileSize;
            _tileHeight = Math.Max(30, _across - Head);
            _tileWidth = _tileHeight * 4 / 3;
            Columns = 1;
        }
        else
        {
            _across = double.IsFinite(available.Width) ? available.Width : _tileSize;
            if (_arrangement == Arrangement.Column)
            {
                Columns = 1;
                _tileWidth = Math.Max(40, _across);
                _tileHeight = _tileWidth * 3 / 4;
            }
            else
            {
                Columns = Math.Max(1, (int)((_across + Gap) / (_tileSize + Gap)));
                _tileWidth = Math.Max(1, (_across - (Columns - 1) * Gap) / Columns);
                _tileHeight = _tileSize;
            }
        }

        var groups = _items.GroupCount;
        if (_starts.Length != groups) _starts = new double[groups];
        var between = !_items.Titled ? 0 : Sideways ? RowGroupGap : GroupGap;
        double at = 0;
        for (var g = 0; g < groups; g++)
        {
            _starts[g] = at;
            at += Length(g) + (g < groups - 1 ? between : 0);
        }
        return Sideways ? new Size(at, _across) : new Size(_across, at);
    }

    /// <summary>A group's length along the scrolling direction.</summary>
    private double Length(int group)
    {
        var n = _items.GroupSize(group);
        if (Sideways) return n * _tileWidth + (n - 1) * Gap;
        var rows = (n + Columns - 1) / Columns;
        return Head + rows * _tileHeight + (rows - 1) * Gap;
    }

    /// <summary>The titles and tiles between from and to along the scrolling direction.</summary>
    private void Visible(double from, double to, Dictionary<int, Rect> into)
    {
        var groups = _items.GroupCount;
        if (groups == 0) return;
        var g = Array.BinarySearch(_starts, from);
        if (g < 0) g = Math.Max(0, ~g - 1);
        for (; g < groups && _starts[g] < to; g++)
        {
            var n = _items.GroupSize(g);
            if (_items.Titled) into[_items.First(g)] = TitleRect(g);
            int first, last;
            if (Sideways)
            {
                var step = _tileWidth + Gap;
                first = (int)Math.Floor((from - _starts[g]) / step);
                last = (int)Math.Floor((to - _starts[g]) / step);
            }
            else
            {
                var top = _starts[g] + Head;
                var step = _tileHeight + Gap;
                first = (int)Math.Floor((from - top) / step) * Columns;
                last = ((int)Math.Floor((to - top) / step) + 1) * Columns - 1;
            }
            first = Math.Max(0, first);
            last = Math.Min(n - 1, last);
            for (var k = first; k <= last; k++) into[_items.TileIndex(g, k)] = TileRect(g, k);
        }
    }

    private Rect RectOf(int index)
    {
        var (group, tile) = _items.Locate(index);
        return tile < 0 ? TitleRect(group) : TileRect(group, tile);
    }

    private Rect TitleRect(int group) => Sideways
        ? new Rect(_starts[group], 0, Math.Max(1, Length(group)), TitleHeight)
        : new Rect(0, _starts[group] + TitleTop, _across, TitleHeight);

    private Rect TileRect(int group, int k)
    {
        if (Sideways) return new Rect(_starts[group] + k * (_tileWidth + Gap), Head, _tileWidth, _tileHeight);
        var row = k / Columns;
        var column = k % Columns;
        return new Rect(column * (_tileWidth + Gap), _starts[group] + Head + row * (_tileHeight + Gap), _tileWidth, _tileHeight);
    }

    private void RecycleAll(VirtualizingLayoutContext context)
    {
        foreach (var (element, _) in _realized.Values) Recycle(context, element);
        _realized.Clear();
    }

    /// <summary>Gives an element back, unless the repeater took it back already (when its list was replaced).</summary>
    private static void Recycle(VirtualizingLayoutContext context, Layoutable element)
    {
        if (element is Control control && control.GetVisualParent() is ItemsRepeater repeater && repeater.GetElementIndex(control) >= 0)
            context.RecycleElement(element);
    }
}

/// <summary>A tile or a group's title for the item, each kind reused only for its own kind.</summary>
public sealed class TileTemplates : RecyclingElementFactory
{
    public TileTemplates(IDataTemplate tile, IDataTemplate title)
    {
        RecyclePool = new RecyclePool();
        Templates["tile"] = tile;
        Templates["title"] = title;
    }

    protected override string OnSelectTemplateKeyCore(object? dataContext, Control? owner) => dataContext is TileGroup ? "title" : "tile";
}
