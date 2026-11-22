using System.Text.RegularExpressions;

namespace MomentFinder.Media;

/// <summary>
/// The date a photo's folders or its file name say, for a file without a date of its own (a camera whose clock was
/// never set writes 0000:00:00, a scan has none): "2007/01_07_2007_Chorvatsko", "2008_10 Anglie", "2005_Svatba",
/// "IMG-20190102-WA0001.jpg". Without it such a photo is dated by its file, which in the cloud is the day it was
/// uploaded. Only digits are read (no month names), so every computer of a shared library reads the same date.
/// </summary>
public static partial class PathDate
{
    public enum Precision { Year, Month, Day, Second }

    public readonly record struct Found(DateTime Date, Precision Precision)
    {
        /// <summary>Where what was found ends: the next year, month, day.</summary>
        public DateTime End => Precision switch
        {
            Precision.Year => Date.AddYears(1),
            Precision.Month => Date.AddMonths(1),
            Precision.Day => Date.AddDays(1),
            _ => Date.AddSeconds(1),
        };
    }

    // 20080531, 20190102_101500 (the time only when it is whole)
    [GeneratedRegex(@"(?<!\d)((?:19|20)\d\d)(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])(?:[_\- T]?([01]\d|2[0-3])([0-5]\d)([0-5]\d))?(?!\d)")]
    private static partial Regex Compact();

    // 2007_05_12, 2019-01-02
    [GeneratedRegex(@"(?<!\d)((?:19|20)\d\d)[_\-. ]{1,2}(\d{1,2})[_\-. ]{1,2}(\d{1,2})(?!\d)")]
    private static partial Regex YearMonthDay();

    // 01_07_2007, 20_8_2005, 29_ 10_2005
    [GeneratedRegex(@"(?<!\d)(\d{1,2})[_\-. ]{1,2}(\d{1,2})[_\-. ]{1,2}((?:19|20)\d\d)(?!\d)")]
    private static partial Regex DayMonthYear();

    // 2008_10 Anglie
    [GeneratedRegex(@"(?<!\d)((?:19|20)\d\d)[_\-. ](0?[1-9]|1[0-2])(?!\d)")]
    private static partial Regex YearMonth();

    // 2005_Svatba, Velikonoce2007 (not a picture size: 1920x1080)
    [GeneratedRegex(@"(?<!\d|\d[xX])((?:19|20)\d\d)(?!\d|[xX]\d)")]
    private static partial Regex Year();

    /// <summary>
    /// The most exact date on the path, of equally exact ones the deepest: the file name (whole dates only, a
    /// number in a file name is mostly a counter), then the folders from the file up.
    /// </summary>
    public static Found? Of(string relPath)
    {
        var parts = relPath.Split('/', '\\');
        Found? best = null;
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var file = i == parts.Length - 1;
            var found = file ? InFileName(Path.GetFileNameWithoutExtension(parts[i])) : InFolderName(parts[i]);
            if (found is { } f && (best == null || f.Precision > best.Value.Precision)) best = f;
        }
        return best;
    }

    /// <summary>
    /// The capture time for a file dated only by its file time: what its path says, at noon when the path knows the
    /// day only (the first of the month or of the year when that is all it knows). Null when the path says nothing,
    /// or the file time is not later than it: in it, the file time is the more exact one; before it, the folder is
    /// named by something else than the day of the photo ("Backup 2015" of older photos). A file time after it is
    /// when the file was copied or uploaded.
    /// </summary>
    public static long? TakenAt(string relPath, long fileTaken)
    {
        if (Of(relPath) is not { } found || DateTime.UnixEpoch.AddMilliseconds(fileTaken) < found.End) return null;
        return MediaProbe.ToTakenAt(found.Precision == Precision.Second ? found.Date : found.Date.Date.AddHours(12));
    }

    private static Found? InFileName(string name) => Whole(name);

    private static Found? InFolderName(string name)
    {
        if (Whole(name) is { } whole) return whole;
        if (DayMonthYear().Match(name) is { Success: true } dmy)
        {
            var (a, b) = (int.Parse(dmy.Groups[1].ValueSpan), int.Parse(dmy.Groups[2].ValueSpan));
            // Day first; the other way round only when it cannot be (13_5 is a day and a month, 5_13 a month and a day).
            if ((Date(dmy.Groups[3].ValueSpan, b, a) ?? Date(dmy.Groups[3].ValueSpan, a, b)) is { } date)
                return new Found(date, Precision.Day);
        }
        if (YearMonth().Match(name) is { Success: true } ym && Date(ym.Groups[1].ValueSpan, int.Parse(ym.Groups[2].ValueSpan), 1) is { } month)
            return new Found(month, Precision.Month);
        if (Year().Match(name) is { Success: true } y && Date(y.Groups[1].ValueSpan, 1, 1) is { } year)
            return new Found(year, Precision.Year);
        return null;
    }

    /// <summary>A whole date written year first: 20080531, 20190102_101500, 2007_05_12.</summary>
    private static Found? Whole(string name)
    {
        if (Compact().Match(name) is { Success: true } c &&
            Date(c.Groups[1].ValueSpan, int.Parse(c.Groups[2].ValueSpan), int.Parse(c.Groups[3].ValueSpan)) is { } compact)
            return c.Groups[4].Success
                ? new Found(compact.AddHours(int.Parse(c.Groups[4].ValueSpan)).AddMinutes(int.Parse(c.Groups[5].ValueSpan)).AddSeconds(int.Parse(c.Groups[6].ValueSpan)), Precision.Second)
                : new Found(compact, Precision.Day);
        if (YearMonthDay().Match(name) is { Success: true } s &&
            Date(s.Groups[1].ValueSpan, int.Parse(s.Groups[2].ValueSpan), int.Parse(s.Groups[3].ValueSpan)) is { } separated)
            return new Found(separated, Precision.Day);
        return null;
    }

    /// <summary>A date that exists, from 1900 to next year.</summary>
    private static DateTime? Date(ReadOnlySpan<char> year, int month, int day)
    {
        var y = int.Parse(year);
        if (y < 1900 || y > DateTime.UtcNow.Year + 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(y, month)) return null;
        return new DateTime(y, month, day, 0, 0, 0, DateTimeKind.Utc);
    }
}
