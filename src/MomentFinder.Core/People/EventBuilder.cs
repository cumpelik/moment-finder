using Dapper;
using MomentFinder.Data;
using MomentFinder.Library;

namespace MomentFinder.People;

/// <summary>
/// Splits the library into events (a day of skiing, a weekend trip): consecutive items closer than
/// MaxGap in time and, where both have GPS, closer than MaxKm. Within an event people keep their clothes,
/// which is what the outfit matching relies on.
/// </summary>
public sealed class EventBuilder
{
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromHours(6);
    public double MaxKm { get; set; } = 40;

    private readonly Database _db;

    public EventBuilder(Database db) => _db = db;

    public int Rebuild()
    {
        using var c = _db.Open();
        var all = c.Query<(long Id, long TakenAt, double? Lat, double? Lon, int Kind, bool HasCamera, long Mtime)>(
            "SELECT id, taken_at, lat, lon, kind, camera IS NOT NULL, mtime FROM media WHERE missing=0 ORDER BY taken_at, id").ToList();
        var copied = CopiedInBulk(all.Where(i => i.Kind == (int)MediaKind.Photo && !i.HasCamera && i.Lat == null && IsFileTime(i.TakenAt, i.Mtime))
            .Select(i => (i.Id, i.TakenAt)).ToList());
        var items = all.Where(i => !copied.Contains(i.Id)).ToList();
        var events = new List<(long Start, long End, List<long> Ids)>();
        (double Lat, double Lon)? lastGps = null;
        long lastTime = long.MinValue;
        foreach (var item in items)
        {
            var split = events.Count == 0 || item.TakenAt - lastTime > MaxGap.TotalMilliseconds;
            if (!split && item.Lat is { } lat && item.Lon is { } lon && lastGps is { } g && Km(g.Lat, g.Lon, lat, lon) > MaxKm)
                split = true;
            if (split) events.Add((item.TakenAt, item.TakenAt, []));
            var e = events[^1];
            e.Ids.Add(item.Id);
            events[^1] = (e.Start, item.TakenAt, e.Ids);
            lastTime = item.TakenAt;
            if (item.Lat is { } la && item.Lon is { } lo) lastGps = (la, lo);
            if (split && item.Lat == null) lastGps = null;
        }

        using var tx = c.BeginTransaction();
        c.Execute("UPDATE media SET event_id=NULL", transaction: tx);
        c.Execute("DELETE FROM event", transaction: tx);
        foreach (var e in events)
        {
            var id = c.ExecuteScalar<long>("INSERT INTO event(start_at, end_at, items) VALUES(@Start, @End, @n) RETURNING id",
                new { e.Start, e.End, n = e.Ids.Count }, tx);
            foreach (var chunk in e.Ids.Chunk(500))
                c.Execute("UPDATE media SET event_id=@id WHERE id IN @chunk", new { id, chunk }, tx);
        }
        tx.Commit();
        return events.Count;
    }

    /// <summary>Photos this many within minutes of each other, all dated only by their file time, were copied then.</summary>
    public int BulkCount { get; set; } = 50;
    private static readonly long BulkWindowMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    /// <summary>
    /// The capture time is the file's own time (the wall clock of its modification time): the photo had no date
    /// of its own. The two differ by the time zone only (whole quarters of an hour).
    /// </summary>
    private static bool IsFileTime(long takenAt, long mtime)
    {
        const long quarter = 15 * 60 * 1000;
        var offset = takenAt - mtime;
        if (Math.Abs(offset) > 14 * 4 * quarter) return false;
        var rest = ((offset % quarter) + quarter) % quarter;
        return Math.Min(rest, quarter - rest) <= 2000;
    }

    /// <summary>
    /// Photos without a date of their own that came by the dozens within minutes (scanned slides, a folder
    /// uploaded to the cloud): their time is when they were copied, not taken, so they make no day out
    /// (outfits are matched within a day out, and this one would hold everybody).
    /// </summary>
    private HashSet<long> CopiedInBulk(List<(long Id, long TakenAt)> undated)
    {
        var copied = new HashSet<long>();
        int from = 0, to = 0;
        for (var i = 0; i < undated.Count; i++)
        {
            while (undated[from].TakenAt < undated[i].TakenAt - BulkWindowMs) from++;
            while (to + 1 < undated.Count && undated[to + 1].TakenAt <= undated[i].TakenAt + BulkWindowMs) to++;
            if (to - from + 1 >= BulkCount) copied.Add(undated[i].Id);
        }
        return copied;
    }

    private static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }
}
