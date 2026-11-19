namespace MediaLens.App.Services;

public static class Format
{
    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string Count(long n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";
}
