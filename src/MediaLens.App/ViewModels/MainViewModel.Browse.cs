using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaLens.App.Services;
using MediaLens.Library;
using MediaLens.Media;

namespace MediaLens.App.ViewModels;

public enum GroupMode { None, Day, Month, Year, Person }

public sealed record GroupOption(GroupMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A titled part of the results (a day, a month, a person).</summary>
public sealed class TileGroup(string title, string count, IReadOnlyList<TileViewModel> tiles)
{
    public string Title { get; } = title;
    public string Count { get; } = count;
    public IReadOnlyList<TileViewModel> Tiles { get; } = tiles;
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
    ];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsGrouped), nameof(FlatTiles))] private GroupOption _groupBy = null!;
    [ObservableProperty] private ObservableCollection<TileGroup> _groups = [];
    /// <summary>Tiles show the whole picture instead of a square cut out of it.</summary>
    [ObservableProperty] private bool _showWholePicture;

    public bool IsGrouped => GroupBy.Mode != GroupMode.None;
    /// <summary>The plain grid gets the tiles only when the results are not grouped (two grids would load every thumbnail twice).</summary>
    public ObservableCollection<TileViewModel>? FlatTiles => IsGrouped ? null : Tiles;

    partial void OnTilesChanged(ObservableCollection<TileViewModel> value)
    {
        OnPropertyChanged(nameof(FlatTiles));
        RebuildGroups();
    }

    partial void OnGroupByChanged(GroupOption value)
    {
        _host.Db.SetSetting("group_by", value.Mode.ToString());
        RebuildGroups();
        ResultsReset?.Invoke();
    }

    partial void OnShowWholePictureChanged(bool value) => _host.Db.SetSetting("tile_whole", value ? "1" : "0");

    private void LoadBrowseSettings()
    {
        _groupBy = GroupOptions.FirstOrDefault(o => o.Mode.ToString() == _host.Db.GetSetting("group_by")) ?? GroupOptions[0];
        _showWholePicture = _host.Db.GetSetting("tile_whole") == "1";
    }

    /// <summary>Days and months newest first; a text search keeps its best matches first inside a group.</summary>
    private void RebuildGroups()
    {
        if (!IsGrouped)
        {
            if (Groups.Count > 0) Groups = [];
            return;
        }
        var culture = CultureInfo.CurrentCulture;
        string Items(int n) => Format.Count(n, "item", "items");
        List<TileGroup> groups;
        if (GroupBy.Mode == GroupMode.Person)
        {
            var byMedia = _host.People.PersonsByMedia();
            var names = _host.People.ListPersons().ToDictionary(p => p.Id, p => p.Name);
            var named = Tiles
                .SelectMany(t => byMedia.GetValueOrDefault(t.Item.Id, []).Where(names.ContainsKey).Select(p => (Person: p, Tile: t)))
                .GroupBy(x => x.Person)
                .OrderByDescending(g => g.Count()).ThenBy(g => names[g.Key], StringComparer.CurrentCulture)
                .Select(g => new TileGroup(names[g.Key], Items(g.Count()), g.Select(x => x.Tile).ToList()));
            var nobody = Tiles.Where(t => !byMedia.GetValueOrDefault(t.Item.Id, []).Any(names.ContainsKey)).ToList();
            groups = named.ToList();
            if (nobody.Count > 0) groups.Add(new TileGroup("Nobody named", Items(nobody.Count), nobody));
        }
        else
        {
            groups = Tiles
                .GroupBy(t => GroupBy.Mode switch
                {
                    GroupMode.Day => t.Item.Taken.Date,
                    GroupMode.Month => new DateTime(t.Item.Taken.Year, t.Item.Taken.Month, 1),
                    _ => new DateTime(t.Item.Taken.Year, 1, 1),
                })
                .OrderByDescending(g => g.Key)
                .Select(g => new TileGroup(
                    GroupBy.Mode switch
                    {
                        GroupMode.Day => g.Key.ToString("dddd d. MMMM yyyy", culture),
                        GroupMode.Month => g.Key.ToString("MMMM yyyy", culture),
                        _ => g.Key.Year.ToString(culture),
                    },
                    Items(g.Count()), g.ToList()))
                .ToList();
        }
        Groups = new ObservableCollection<TileGroup>(groups);
    }

    // ---- date filter ----

    /// <summary>Inclusive days.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DateFilterText), nameof(IsDateFiltered))] private DateTime? _dateFrom;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DateFilterText), nameof(IsDateFiltered))] private DateTime? _dateTo;

    public ObservableCollection<DateChoice> Years { get; } = [];
    public ObservableCollection<DateChoice> Months { get; } = [];
    [ObservableProperty] private bool _hasMonths;

    public bool IsDateFiltered => DateFrom != null || DateTo != null;

    public string DateFilterText
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            if (DateFrom is not { } from)
                return DateTo is { } onlyTo ? $"Until {onlyTo:d. M. yyyy}" : "Any time";
            if (DateTo is not { } to) return $"Since {from:d. M. yyyy}";
            if (from.Month == 1 && from.Day == 1 && to.Month == 12 && to.Day == 31)
                return from.Year == to.Year ? from.Year.ToString(culture) : $"{from.Year} to {to.Year}";
            if (from.Day == 1 && to == from.AddMonths(1).AddDays(-1)) return from.ToString("MMMM yyyy", culture);
            return from == to ? from.ToString("d. M. yyyy", culture) : $"{from:d. M. yyyy} to {to:d. M. yyyy}";
        }
    }

    partial void OnDateFromChanged(DateTime? value) => DateChanged();
    partial void OnDateToChanged(DateTime? value) => DateChanged();

    private bool _settingDates;

    private void DateChanged()
    {
        if (_settingDates) return;
        MarkDateChoices();
        _ = RunSearchAsync(keepSelection: false);
    }

    private void SetDates(DateTime? from, DateTime? to)
    {
        _settingDates = true;
        DateFrom = from;
        DateTo = to;
        _settingDates = false;
        DateChanged();
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

    /// <summary>The months of the chosen year (all twelve, empty ones disabled).</summary>
    private void LoadMonths()
    {
        Months.Clear();
        if (DateFrom is { } from && DateTo is { } to && from.Year == to.Year)
        {
            var counts = _host.Repo.DateCounts(from.Year).ToDictionary(c => c.Month, c => c.Count);
            var culture = CultureInfo.CurrentCulture;
            for (var m = 1; m <= 12; m++)
                Months.Add(new DateChoice(from.Year, m, culture.DateTimeFormat.GetAbbreviatedMonthName(m), counts.GetValueOrDefault(m)));
        }
        HasMonths = Months.Count > 0;
    }

    private void MarkDateChoices()
    {
        foreach (var y in Years)
            y.IsSelected = DateFrom is { } f && DateTo is { } t && f == new DateTime(y.Year, 1, 1) && t == new DateTime(y.Year, 12, 31);
        foreach (var m in Months)
            m.IsSelected = DateFrom is { } f && DateTo is { } t && f == new DateTime(m.Year, m.Month, 1) && t == f.AddMonths(1).AddDays(-1);
    }

    [RelayCommand]
    private void PickYear(DateChoice year)
    {
        if (year.IsSelected) SetDates(null, null);
        else SetDates(new DateTime(year.Year, 1, 1), new DateTime(year.Year, 12, 31));
        LoadMonths();
        MarkDateChoices();
    }

    [RelayCommand]
    private void PickMonth(DateChoice month)
    {
        var first = new DateTime(month.Year, month.Month, 1);
        // The month again: back to its whole year.
        if (month.IsSelected) SetDates(new DateTime(month.Year, 1, 1), new DateTime(month.Year, 12, 31));
        else SetDates(first, first.AddMonths(1).AddDays(-1));
    }

    [RelayCommand]
    private void ClearDate()
    {
        SetDates(null, null);
        LoadMonths();
    }

    /// <summary>The scope (source, bucket, person) limited to the chosen days.</summary>
    private MediaFilter WithDates(MediaFilter filter) => filter with
    {
        From = DateFrom is { } from ? MediaProbe.ToTakenAt(from.Date) : filter.From,
        To = DateTo is { } to ? MediaProbe.ToTakenAt(to.Date.AddDays(1)) : filter.To,
    };
}
