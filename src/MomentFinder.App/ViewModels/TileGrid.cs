using System.Collections;

namespace MomentFinder.App.ViewModels;

/// <summary>
/// What the thumbnail grid shows, as one list: the tiles, or each group's title followed by its tiles.
/// It never changes; other results or another grouping make a new one.
/// </summary>
public sealed class TileGrid : IList, IReadOnlyList<object>
{
    public static readonly TileGrid Empty = new([], titled: false);

    private readonly IReadOnlyList<TileGroup> _groups;
    /// <summary>Where each group starts in the list (its title when titled).</summary>
    private readonly int[] _firsts;

    private TileGrid(IReadOnlyList<TileGroup> groups, bool titled)
    {
        _groups = groups;
        Titled = titled;
        _firsts = new int[groups.Count];
        var index = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            _firsts[g] = index;
            index += groups[g].Tiles.Count + (titled ? 1 : 0);
        }
        Count = index;
    }

    public static TileGrid Plain(IReadOnlyList<TileViewModel> tiles) =>
        tiles.Count == 0 ? Empty : new TileGrid([new TileGroup("", "", tiles)], titled: false);

    public static TileGrid Grouped(IReadOnlyList<TileGroup> groups) => new(groups, titled: true);

    /// <summary>The groups have titles (the plain grid is one group without one).</summary>
    public bool Titled { get; }
    public int Count { get; }
    public int GroupCount => _groups.Count;
    public int GroupSize(int group) => _groups[group].Tiles.Count;
    public int First(int group) => _firsts[group];
    public int TileIndex(int group, int tile) => _firsts[group] + (Titled ? 1 : 0) + tile;

    /// <summary>A tile's place among the tiles alone (the titles left out): its place in the shown order.</summary>
    public int ShownIndexOf(int index)
    {
        var (group, tile) = Locate(index);
        return _firsts[group] - (Titled ? group : 0) + Math.Max(0, tile);
    }

    /// <summary>The list index of the n-th tile in the shown order.</summary>
    public int IndexOfShown(int shown)
    {
        if (!Titled) return shown;
        // Group g starts at tile number _firsts[g] - g; the titles before it shift it by g.
        int lo = 0, hi = _groups.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_firsts[mid] - mid <= shown) lo = mid;
            else hi = mid - 1;
        }
        return shown + lo + 1;
    }

    /// <summary>The group of an item and its place in it; -1 is the group's title.</summary>
    public (int Group, int Tile) Locate(int index)
    {
        var group = Array.BinarySearch(_firsts, index);
        if (group < 0) group = ~group - 1;
        return (group, index - _firsts[group] - (Titled ? 1 : 0));
    }

    public object this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var (group, tile) = Locate(index);
            return tile < 0 ? _groups[group] : _groups[group].Tiles[tile];
        }
    }

    /// <summary>Where the tile is first (grouped by person, one item can be in several groups).</summary>
    public int IndexOf(TileViewModel tile)
    {
        for (var g = 0; g < _groups.Count; g++)
        {
            var tiles = _groups[g].Tiles;
            for (var i = 0; i < tiles.Count; i++)
                if (ReferenceEquals(tiles[i], tile)) return TileIndex(g, i);
        }
        return -1;
    }

    public int IndexOf(object? value)
    {
        if (value is TileViewModel tile) return IndexOf(tile);
        if (value is TileGroup group && Titled)
            for (var g = 0; g < _groups.Count; g++)
                if (ReferenceEquals(_groups[g], group)) return _firsts[g];
        return -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<object> GetEnumerator()
    {
        for (var g = 0; g < _groups.Count; g++)
        {
            if (Titled) yield return _groups[g];
            foreach (var tile in _groups[g].Tiles) yield return tile;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CopyTo(Array array, int index)
    {
        foreach (var item in this) array.SetValue(item, index++);
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
