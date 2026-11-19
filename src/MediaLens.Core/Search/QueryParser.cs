using System.Globalization;
using System.Text.RegularExpressions;
using MediaLens.Library;

namespace MediaLens.Search;

public sealed record SearchQuery
{
    /// <summary>What remains after the filters: a phrase for the image model ("skier in a red jacket").</summary>
    public string Text { get; init; } = "";
    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    public MediaKind? Kind { get; init; }
}

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

    public static SearchQuery Parse(string? input)
    {
        var rest = new List<string>();
        int? from = null, to = null;
        MediaKind? kind = null;
        foreach (var token in (input ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
        return new SearchQuery { Text = string.Join(' ', rest), YearFrom = from, YearTo = to, Kind = kind };
    }

    public static MediaFilter ToFilter(SearchQuery q, MediaFilter scope) => scope with
    {
        Kind = q.Kind ?? scope.Kind,
        From = q.YearFrom is { } f ? TakenAt(new DateTime(f, 1, 1)) : scope.From,
        To = q.YearTo is { } t ? TakenAt(new DateTime(t + 1, 1, 1)) : scope.To,
    };

    private static long TakenAt(DateTime d) => Media.MediaProbe.ToTakenAt(d);
}
