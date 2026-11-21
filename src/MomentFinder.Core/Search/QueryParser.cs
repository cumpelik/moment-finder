using System.Globalization;
using System.Text.RegularExpressions;
using MomentFinder.Library;

namespace MomentFinder.Search;

public sealed record SearchQuery
{
    /// <summary>What remains after the filters: a phrase for the image model ("skier in a red jacket").</summary>
    public string Text { get; init; } = "";
    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    public MediaKind? Kind { get; init; }
    /// <summary>Names found in the query; every one of them must be on the item.</summary>
    public IReadOnlyList<long> PersonIds { get; init; } = [];
}

/// <summary>A name the parser can recognize.</summary>
public sealed record NamedPerson(long Id, string Name);

/// <summary>
/// One search box: "lyžař 2023", "video 2019-2021 moře", "photos beach". Years and kind words are filters,
/// the rest goes to the image model. Person names join in stage 3.
/// </summary>
public static partial class QueryParser
{
    private static readonly HashSet<string> PhotoWords = new(StringComparer.OrdinalIgnoreCase)
        { "photo", "photos", "foto", "fotka", "fotky", "fotek", "fotografie" };
    private static readonly HashSet<string> VideoWords = new(StringComparer.OrdinalIgnoreCase)
        { "video", "videos", "videa", "videí", "videi" };

    [GeneratedRegex(@"^(\d{4})(?:-(\d{4}))?$")]
    private static partial Regex YearRange();

    public static SearchQuery Parse(string? input, IReadOnlyList<NamedPerson>? people = null)
    {
        var rest = new List<string>();
        int? from = null, to = null;
        MediaKind? kind = null;
        var tokens = (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var personIds = people is { Count: > 0 } ? TakeNames(tokens, people) : [];
        foreach (var token in tokens)
        {
            var m = YearRange().Match(token);
            if (m.Success)
            {
                var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : a;
                if (a is >= 1900 and <= 2100 && b is >= 1900 and <= 2100)
                {
                    (from, to) = (Math.Min(a, b), Math.Max(a, b));
                    continue;
                }
            }
            if (PhotoWords.Contains(token)) { kind = MediaKind.Photo; continue; }
            if (VideoWords.Contains(token)) { kind = MediaKind.Video; continue; }
            rest.Add(token);
        }
        return new SearchQuery { Text = string.Join(' ', rest), YearFrom = from, YearTo = to, Kind = kind, PersonIds = personIds };
    }

    /// <summary>
    /// Removes the names of people from the tokens: a full name ("Tomáš Novák"), or a first name that only
    /// one person has. Case and diacritics do not matter ("tomas").
    /// </summary>
    private static List<long> TakeNames(List<string> tokens, IReadOnlyList<NamedPerson> people)
    {
        var found = new List<long>();
        var named = people.Select(p => (p.Id, Parts: Normalize(p.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Parts.Length > 0).OrderByDescending(p => p.Parts.Length).ToList();
        var firstNames = named.GroupBy(p => p.Parts[0]).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Id);
        var norm = tokens.Select(Normalize).ToList();
        foreach (var (id, parts) in named)
        {
            for (var i = 0; i + parts.Length <= norm.Count; i++)
            {
                if (!parts.Select((p, k) => norm[i + k] == p).All(x => x)) continue;
                found.Add(id);
                tokens.RemoveRange(i, parts.Length);
                norm.RemoveRange(i, parts.Length);
                break;
            }
        }
        for (var i = norm.Count - 1; i >= 0; i--)
        {
            if (!firstNames.TryGetValue(norm[i], out var id) || found.Contains(id)) continue;
            found.Add(id);
            tokens.RemoveAt(i);
        }
        return found;
    }

    public static string Normalize(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(d.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    public static MediaFilter ToFilter(SearchQuery q, MediaFilter scope) => scope with
    {
        Kind = q.Kind ?? scope.Kind,
        From = q.YearFrom is { } f ? TakenAt(new DateTime(f, 1, 1)) : scope.From,
        To = q.YearTo is { } t ? TakenAt(new DateTime(t + 1, 1, 1)) : scope.To,
        PersonIds = scope.PersonIds.Concat(q.PersonIds).Distinct().ToList(),
    };

    private static long TakenAt(DateTime d) => Media.MediaProbe.ToTakenAt(d);
}
