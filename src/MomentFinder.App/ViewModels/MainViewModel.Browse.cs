using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MomentFinder.App.Services;
using MomentFinder.Library;
using MomentFinder.Media;

namespace MomentFinder.App.ViewModels;

public enum GroupMode { None, Day, Month, Year, Person, Folder }

public enum PreviewMode { Window, Below, Right }

public sealed record GroupOption(GroupMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A titled part of the results (a day, a month, a person).</summary>
public sealed class TileGroup(string title, string count, IReadOnlyList<TileViewModel> tiles, IReadOnlyList<string>? path = null)
{
    public string Title { get; } = title;
    public string Count { get; } = count;
    public IReadOnlyList<TileViewModel> Tiles { get; } = tiles;
    /// <summary>Where it is in the groups' tree: a folder under its parent folders, a day under its year and month.</summary>
    public IReadOnlyList<string> Path { get; } = path ?? [title];
}

/// <summary>A place in the groups' tree: a folder with its subfolders, a year with its months.</summary>
public sealed partial class GroupNode(string key, string title) : ObservableObject
{
    /// <summary>The whole path: the same place after the results refresh.</summary>
    public string Key { get; } = key;
    public string Title { get; } = title;
    public List<GroupNode> Children { get; } = [];
    /// <summary>Its own group and those of everything under it, in the order the grid shows them.</summary>
    public List<TileGroup> Groups { get; set; } = [];
    public int Total { get; set; }
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private bool _isExpanded;

    public IEnumerable<GroupNode> SelfAndBelow() => Children.SelectMany(c => c.SelfAndBelow()).Prepend(this);
}

/// <summary>A year or a month in the date filter, with how many items it has.</summary>
public sealed partial class DateChoice(int year, int month, string label, long count) : ObservableObject
{
    public int Year { get; } = year;
    public int Month { get; } = month;
    public string Label { get; } = label;
    public string Count { get; } = count.ToString("N0", CultureInfo.CurrentCulture);
    public bool HasItems { get; } = count > 0;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Browsing: grouping the results and limiting them to a time span.</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<GroupOption> GroupOptions { get; } =
    [
        new(GroupMode.None, "Not grouped"),
        new(GroupMode.Day, "By day"),
        new(GroupMode.Month, "By month"),
        new(GroupMode.Year, "By year"),
        new(GroupMode.Person, "By person"),
        new(GroupMode.Folder, "By folder"),
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsGrouped))] private GroupOption _groupBy = null!;
    /// <summary>What the grid shows: the results, plain or each group's title followed by its tiles.</summary>
    [ObservableProperty] private TileGrid _gridItems = TileGrid.Empty;

    /// <summary>The tiles in the order the grid shows them (group after group): arrows, Shift ranges and the viewer go this way.</summary>
    public List<TileViewModel> Shown { get; private set; } = [];

    private void ShowGrid(TileGrid grid, List<TileViewModel> shown)
    {
        var anchor = AnchorTile;
        Shown = shown;
        _anchorIndex = anchor == null ? -1 : shown.IndexOf(anchor);
        GridItems = grid;
    }

    public bool IsGrouped => GroupBy.Mode != GroupMode.None;

    /// <summary>The groups as a tree on the left (like folders in a file manager); the grid shows the chosen one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowGroupTree))] private bool _groupTree;
    public bool ShowGroupTree => GroupTree && IsGrouped && IsGridMode;
    [ObservableProperty] private List<GroupNode> _groupRoots = [];
    [ObservableProperty] private GroupNode? _selectedGroup;
    private List<TileGroup> _allGroups = [];
    private bool _applyingTree;

    partial void OnGroupTreeChanged(bool value)
    {
        _host.Db.SetSetting("group_tree", value ? "1" : "0");
        ShowChosenGroups();
        ResultsReset?.Invoke();
    }

    partial void OnSelectedGroupChanged(GroupNode? value)
    {
        if (_applyingTree) return;
        ShowChosenGroups();
        ResultsReset?.Invoke();
    }

    /// <summary>All groups under each other, or with the tree only the chosen place.</summary>
    private void ShowChosenGroups()
    {
        if (!IsGrouped) return;
        var groups = GroupTree && SelectedGroup != null ? SelectedGroup.Groups : _allGroups;
        ShowGrid(TileGrid.Grouped(groups), groups.SelectMany(g => g.Tiles).ToList());
    }

    /// <summary>
    /// New groups: the tree keeps what was open and chosen. The same places with the same counts (a refresh while
    /// indexing) keep the tree as it is, so it does not jump.
    /// </summary>
    private void ApplyGroups(List<TileGroup> groups, List<GroupNode> roots)
    {
        _allGroups = groups;
        var old = GroupRoots.SelectMany(r => r.SelfAndBelow()).ToList();
        var fresh = roots.SelectMany(r => r.SelfAndBelow()).ToList();
        _applyingTree = true;
        if (old.Count == fresh.Count && old.Zip(fresh).All(p => p.First.Key == p.Second.Key && p.First.Total == p.Second.Total))
            foreach (var (o, f) in old.Zip(fresh)) o.Groups = f.Groups;
        else
        {
            var open = old.Where(n => n.IsExpanded).Select(n => n.Key).ToHashSet();
            foreach (var node in fresh) node.IsExpanded = open.Contains(node.Key);
            // The first time one top place opens by itself (the source of the folders, the only year).
            if (old.Count == 0 && roots.Count == 1) roots[0].IsExpanded = true;
            var chosen = SelectedGroup?.Key;
            GroupRoots = roots;
            SelectedGroup = fresh.FirstOrDefault(n => n.Key == chosen) ?? roots.FirstOrDefault();
        }
        _applyingTree = false;
        ShowChosenGroups();
    }

    /// <summary>The tree of the groups' paths; each place collects the groups under it.</summary>
    private static List<GroupNode> BuildTree(List<TileGroup> groups)
    {
        var roots = new List<GroupNode>();
        var byKey = new Dictionary<string, GroupNode>();
        foreach (var group in groups)
        {
            GroupNode? parent = null;
            var key = "";
            foreach (var part in group.Path)
            {
                key += "\u0001" + part;
                if (!byKey.TryGetValue(key, out var node))
                {
                    byKey[key] = node = new GroupNode(key, part);
                    (parent?.Children ?? roots).Add(node);
                }
                node.Groups.Add(group);
                node.Total += group.Tiles.Count;
                parent = node;
            }
        }
        foreach (var node in byKey.Values) node.Count = node.Total.ToString("N0", CultureInfo.CurrentCulture);
        return roots;
    }

    /// <summary>The panel under the thumbnails is open (the choice is on and the grid is shown).</summary>
    public bool ShowPreviewPanel => OpenInPanel && IsGridMode;
    partial void OnTilesChanged(ObservableCollection<TileViewModel> value) => RebuildGroups();

    partial void OnGroupByChanged(GroupOption value)
    {
        _host.Db.SetSetting("group_by", value.Mode.ToString());
        RebuildGroups();
        ResultsReset?.Invoke();
    }

    private void LoadBrowseSettings()
    {
        _groupBy = GroupOptions.FirstOrDefault(o => o.Mode.ToString() == _host.Db.GetSetting("group_by")) ?? GroupOptions[0];
        _groupTree = _host.Db.GetSetting("group_tree") == "1";
    }

    /// <summary>Days and months newest first; a text search keeps its best matches first inside a group.</summary>
    private int _groupsVersion;

    private async void RebuildGroups()
    {
        var version = ++_groupsVersion;
        if (!IsGrouped)
        {
            OnPropertyChanged(nameof(ShowGroupTree));
            ShowGrid(TileGrid.Plain(Tiles), Tiles.ToList());
            return;
        }
        // Grouped off the UI thread: a library of millions takes a moment.
        var tiles = Tiles.ToList();
        var mode = GroupBy.Mode;
        var (groups, roots) = await Task.Run(() =>
        {
            var groups = BuildGroups(tiles, mode);
            return (groups, BuildTree(groups));
        });
        if (version != _groupsVersion) return;
        OnPropertyChanged(nameof(ShowGroupTree));
        ApplyGroups(groups, roots);
    }

    private List<TileGroup> BuildGroups(List<TileViewModel> tiles, GroupMode mode)
    {
        var culture = CultureInfo.CurrentCulture;
        string Items(int n) => Format.Count(n, "item", "items");
        List<TileGroup> groups;
        if (mode == GroupMode.Folder)
        {
            // The folders as they are on the disk or in the cloud; the source's name too when several are shown.
            var sources = _host.Repo.ListSources().ToDictionary(s => s.Id, s => s.Name);
            var several = tiles.Select(t => t.Item.SourceId).Distinct().Count() > 1;
            groups = tiles
                .GroupBy(t => (t.Item.SourceId, Folder: t.Item.RelPath.Contains('/') ? t.Item.RelPath[..t.Item.RelPath.LastIndexOf('/')] : ""))
                .Select(g =>
                {
                    var source = sources.GetValueOrDefault(g.Key.SourceId, "");
                    var title = g.Key.Folder.Length == 0 ? source
                        : several ? $"{source}  \u00b7  {g.Key.Folder.Replace("/", " / ")}" : g.Key.Folder.Replace("/", " / ");
                    // In the tree each source is the top, its folders under it.
                    string[] path = g.Key.Folder.Length == 0 ? [source] : [source, .. g.Key.Folder.Split('/')];
                    return (Title: title, Path: path, Group: g);
                })
                .OrderBy(x => x.Title, StringComparer.Create(culture, CompareOptions.IgnoreCase))
                .Select(x => new TileGroup(x.Title, Items(x.Group.Count()), x.Group.ToList(), x.Path))
                .ToList();
        }
        else if (mode == GroupMode.Person)
        {
            var byMedia = _host.People.PersonsByMedia();
            var names = _host.People.ListPersons().ToDictionary(p => p.Id, p => p.Name);
            var named = tiles
                .SelectMany(t => byMedia.GetValueOrDefault(t.Item.Id, []).Where(names.ContainsKey).Select(p => (Person: p, Tile: t)))
                .GroupBy(x => x.Person)
                .OrderByDescending(g => g.Count()).ThenBy(g => names[g.Key], StringComparer.CurrentCulture)
                .Select(g => new TileGroup(names[g.Key], Items(g.Count()), g.Select(x => x.Tile).ToList()));
            var nobody = tiles.Where(t => !byMedia.GetValueOrDefault(t.Item.Id, []).Any(names.ContainsKey)).ToList();
            groups = named.ToList();
            if (nobody.Count > 0) groups.Add(new TileGroup("Nobody named", Items(nobody.Count), nobody));
        }
        else
        {
            groups = tiles
                .GroupBy(t => mode switch
                {
                    GroupMode.Day => t.Item.Taken.Date,
                    GroupMode.Month => new DateTime(t.Item.Taken.Year, t.Item.Taken.Month, 1),
                    _ => new DateTime(t.Item.Taken.Year, 1, 1),
                })
                .OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    var place = mode == GroupMode.Day ? PlaceOf(g) : "";
                    // In the tree: the year, its months, their days.
                    var year = g.Key.Year.ToString(culture);
                    var month = culture.TextInfo.ToTitleCase(g.Key.ToString("MMMM", culture));
                    string[] path = mode switch
                    {
                        GroupMode.Day => [year, month, g.Key.ToString("d. dddd", culture) + place],
                        GroupMode.Month => [year, month],
                        _ => [year],
                    };
                    return new TileGroup(
                        mode switch
                        {
                            GroupMode.Day => g.Key.ToString("dddd d. MMMM yyyy", culture) + place,
                            GroupMode.Month => g.Key.ToString("MMMM yyyy", culture),
                            _ => year,
                        },
                        Items(g.Count()), g.ToList(), path);
                })
                .ToList();
        }
        return groups;
    }

    /// <summary>"  ·  Sölden" for a day whose photos have GPS, once the place names are downloaded.</summary>
    private string PlaceOf(IEnumerable<TileViewModel> tiles) =>
        _host.Places?.MostCommon(tiles.Where(t => t.Item.Lat != null && t.Item.Lon != null).Select(t => (t.Item.Lat!.Value, t.Item.Lon!.Value)))
            is { } place ? "  \u00b7  " + place : "";

    // ---- date filter ----

    /// <summary>Any days from / to (inclusive), set in the calendars.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DateFilterText), nameof(IsDateFiltered))] private DateTime? _dateFrom;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DateFilterText), nameof(IsDateFiltered))] private DateTime? _dateTo;

    /// <summary>Clicked years, and months of the one clicked year; several of each, not necessarily next to each other.</summary>
    private readonly SortedSet<int> _years = [];
    private readonly SortedSet<int> _months = [];

    public ObservableCollection<DateChoice> Years { get; } = [];
    public ObservableCollection<DateChoice> Months { get; } = [];
    [ObservableProperty] private bool _hasMonths;

    public bool IsDateFiltered => DateFrom != null || DateTo != null || _years.Count > 0;

    public string DateFilterText
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            if (_years.Count > 0)
            {
                if (_months.Count == 0)
                    return _years.Count > 4 ? $"{_years.Count} years" : string.Join(", ", _years.Reverse());
                var year = _years.Single();
                return _months.Count > 4
                    ? $"{_months.Count} months of {year}"
                    : string.Join(", ", _months.Select(m => culture.DateTimeFormat.GetAbbreviatedMonthName(m))) + $" {year}";
            }
            if (DateFrom is not { } from)
                return DateTo is { } onlyTo ? $"Until {onlyTo:d. M. yyyy}" : "Any time";
            if (DateTo is not { } to) return $"Since {from:d. M. yyyy}";
            return from == to ? from.ToString("d. M. yyyy", culture) : $"{from:d. M. yyyy} to {to:d. M. yyyy}";
        }
    }

    partial void OnDateFromChanged(DateTime? value) => DaysChanged();
    partial void OnDateToChanged(DateTime? value) => DaysChanged();

    private bool _settingDates;

    /// <summary>Days in the calendars replace the clicked years and months.</summary>
    private void DaysChanged()
    {
        if (_settingDates) return;
        _years.Clear();
        _months.Clear();
        LoadMonths();
        DateChanged();
    }

    private void DateChanged()
    {
        MarkDateChoices();
        OnPropertyChanged(nameof(DateFilterText));
        OnPropertyChanged(nameof(IsDateFiltered));
        _ = RunSearchAsync(keepSelection: false);
    }

    /// <summary>Clicked years and months replace the days in the calendars.</summary>
    private void ClearDays()
    {
        _settingDates = true;
        DateFrom = null;
        DateTo = null;
        _settingDates = false;
    }

    /// <summary>The years of the library with their counts; called when the date filter opens.</summary>
    public void LoadDateChoices()
    {
        Years.Clear();
        foreach (var (year, _, count) in _host.Repo.DateCounts())
            Years.Add(new DateChoice(year, 0, year.ToString(CultureInfo.CurrentCulture), count));
        LoadMonths();
        MarkDateChoices();
    }

    /// <summary>The months of the year when exactly one is clicked (all twelve, empty ones disabled).</summary>
    private void LoadMonths()
    {
        Months.Clear();
        if (_years.Count == 1)
        {
            var year = _years.Single();
            var counts = _host.Repo.DateCounts(year).ToDictionary(c => c.Month, c => c.Count);
            var culture = CultureInfo.CurrentCulture;
            for (var m = 1; m <= 12; m++)
                Months.Add(new DateChoice(year, m, culture.DateTimeFormat.GetAbbreviatedMonthName(m), counts.GetValueOrDefault(m)));
        }
        HasMonths = Months.Count > 0;
        HasManyYears = _years.Count > 1;
    }

    /// <summary>Months are offered for one year; with more years the filter says so.</summary>
    [ObservableProperty] private bool _hasManyYears;

    private void MarkDateChoices()
    {
        foreach (var y in Years) y.IsSelected = _years.Contains(y.Year);
        foreach (var m in Months) m.IsSelected = _months.Contains(m.Month);
    }

    /// <summary>A click adds the year, another click takes it away.</summary>
    [RelayCommand]
    private void PickYear(DateChoice year)
    {
        ClearDays();
        if (!_years.Remove(year.Year)) _years.Add(year.Year);
        // Months belong to one year: with another year they would mean something else.
        _months.Clear();
        LoadMonths();
        DateChanged();
    }

    /// <summary>A click adds the month, another click takes it away; no month means the whole year.</summary>
    [RelayCommand]
    private void PickMonth(DateChoice month)
    {
        ClearDays();
        if (!_months.Remove(month.Month)) _months.Add(month.Month);
        DateChanged();
    }

    [RelayCommand]
    private void ClearDate()
    {
        ClearDays();
        _years.Clear();
        _months.Clear();
        LoadMonths();
        DateChanged();
    }

    /// <summary>The scope (source, bucket, person) limited to the chosen time.</summary>
    private MediaFilter WithDates(MediaFilter filter)
    {
        var spans = _years.Count == 0 ? [] :
            _months.Count == 0
                ? _years.Select(y => Span(new DateTime(y, 1, 1), new DateTime(y + 1, 1, 1))).ToList()
                : _months.Select(m => new DateTime(_years.Single(), m, 1)).Select(d => Span(d, d.AddMonths(1))).ToList();
        return filter with
        {
            From = DateFrom is { } from ? MediaProbe.ToTakenAt(from.Date) : filter.From,
            To = DateTo is { } to ? MediaProbe.ToTakenAt(to.Date.AddDays(1)) : filter.To,
            Spans = spans,
        };
    }

    private static (long From, long To) Span(DateTime from, DateTime to) => (MediaProbe.ToTakenAt(from), MediaProbe.ToTakenAt(to));
}
