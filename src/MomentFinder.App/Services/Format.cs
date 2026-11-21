namespace MomentFinder.App.Services;

public static class Format
{
    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>A rough length of time for people: "40 s", "12 min", "1 h 20 min".</summary>
    public static string Span(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";
        if (t.TotalMinutes < 60) return $"{(int)Math.Round(t.TotalMinutes)} min";
        var minutes = (int)Math.Round(t.TotalMinutes) % 60;
        return minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {minutes} min";
    }

    public static string Count(long n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
}
