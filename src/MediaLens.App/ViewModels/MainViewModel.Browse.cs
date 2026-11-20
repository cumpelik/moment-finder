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

    private void LoadBrowseSettings()
    {
        _groupBy = GroupOptions.FirstOrDefault(o => o.Mode.ToString() == _host.Db.GetSetting("group_by")) ?? GroupOptions[0];
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
