using Dapper;
using MediaLens.Data;

namespace MediaLens.People;

/// <summary>A part of a video worth watching: who is in it, and how much it is worth.</summary>
public sealed record Moment(int StartMs, int EndMs, double Score, IReadOnlyList<long> PersonIds);

/// <summary>A video with its moments (in time order) and its score: the higher, the more worth watching.</summary>
public sealed record VideoInterest(long MediaId, double Score, IReadOnlyList<Moment> Moments)
{
    /// <summary>Where the video is best: its moment of the highest score.</summary>
    public Moment Best => Moments.MaxBy(m => m.Score)!;
}

/// <summary>
/// Finds the moments of videos worth watching from what the people analysis knows already (nothing is read again):
/// somebody with a name, big on the screen (not a dot far away), for at least a few seconds. A moment is worth
/// more the longer and the bigger the person is, and when more named people are in it together. A video's score
/// adds its moments up, so a video with several good moments comes before one with a single short one.
/// </summary>
public static class InterestingMoments
{
    /// <summary>Big: at least this part of the picture's height.</summary>
    public const double BigHeight = 0.2;
    /// <summary>A moment lasts at least this long.</summary>
    public const int MinMs = 2000;
    /// <summary>A person at half the picture's height or more counts fully; smaller counts less.</summary>
    private const double FullHeight = 0.5;
    /// <summary>A long moment does not outweigh everything: its length counts up to this.</summary>
    private const double MaxSeconds = 30;
    /// <summary>Played with a little of before and after.</summary>
    private const int PadMs = 1000;

    private sealed class Row
    {
        public long MediaId { get; init; }
        public long Track { get; init; }
        public long TimeMs { get; init; }
        public double H { get; init; }
        public long PersonId { get; init; }
        public long DurationMs { get; init; }
    }

    /// <summary>The videos with at least one moment, the best first.</summary>
    public static List<VideoInterest> Find(Database db)
    {
        List<Row> rows;
        using (var c = db.Open())
        {
            // The path of every track that has a name (a person the user named and did not hide).
            rows = c.Query<Row>("""
                SELECT tb.media_id AS MediaId, tb.track AS Track, tb.time_ms AS TimeMs, tb.h AS H,
                       nt.person_id AS PersonId, COALESCE(m.duration_ms, 0) AS DurationMs
                FROM track_box tb
                JOIN (SELECT d.track, MIN(d.person_id) AS person_id FROM detection d
                      JOIN person p ON p.id=d.person_id AND p.hidden=0
                      WHERE d.track IS NOT NULL GROUP BY d.track) nt ON nt.track=tb.track
                JOIN media m ON m.id=tb.media_id
                JOIN source s ON s.id=m.source_id
                WHERE m.kind=2 AND m.missing=0 AND s.enabled=1
                ORDER BY tb.media_id, tb.track, tb.time_ms
                """).ToList();
        }
        var result = new List<VideoInterest>();
        foreach (var video in rows.GroupBy(r => r.MediaId))
        {
            var interval = PeopleAnalyzer.FrameIntervalMs(video.First().DurationMs);
            var runs = video.GroupBy(r => r.Track).SelectMany(t => Runs(t.ToList(), interval)).ToList();
            var moments = Merge(runs, video.First().DurationMs);
            if (moments.Count > 0) result.Add(new VideoInterest(video.Key, moments.Sum(m => m.Score), moments));
        }
        return result.OrderByDescending(v => v.Score).ToList();
    }

    /// <summary>The parts of one person's path where they are big, without a gap, long enough.</summary>
    internal static IEnumerable<Moment> Runs(IReadOnlyList<(int TimeMs, double H)> path, int intervalMs, long personId)
    {
        var start = -1;
        for (var i = 0; i <= path.Count; i++)
        {
            var big = i < path.Count && path[i].H >= BigHeight &&
                      (start < 0 || path[i].TimeMs - path[i - 1].TimeMs <= 2 * intervalMs);
            if (big && start < 0) start = i;
            if (big) continue;
            if (start >= 0)
            {
                // Each look stands for the time around it: one look is one interval long.
                var from = path[start].TimeMs - intervalMs / 2;
                var to = path[i - 1].TimeMs + intervalMs / 2;
                if (to - from >= MinMs)
                {
                    var size = path.Skip(start).Take(i - start).Average(p => Math.Min(1, p.H / FullHeight));
                    yield return new Moment(Math.Max(0, from), to, Math.Min(MaxSeconds, (to - from) / 1000.0) * size, [personId]);
                }
            }
            // A big look right after a gap starts a run of its own.
            start = i < path.Count && path[i].H >= BigHeight ? i : -1;
        }
    }

    private static IEnumerable<Moment> Runs(List<Row> track, int intervalMs) =>
        Runs(track.Select(r => ((int)r.TimeMs, r.H)).ToList(), intervalMs, track[0].PersonId);

    /// <summary>Moments that overlap (two people at once) are one, worth both; a little before and after is added.</summary>
    internal static List<Moment> Merge(IEnumerable<Moment> runs, long durationMs)
    {
        var merged = new List<Moment>();
        foreach (var r in runs.OrderBy(r => r.StartMs))
        {
            var start = Math.Max(0, r.StartMs - PadMs);
            var end = durationMs > 0 ? (int)Math.Min(durationMs, r.EndMs + PadMs) : r.EndMs + PadMs;
            if (merged.Count > 0 && start <= merged[^1].EndMs)
            {
                var last = merged[^1];
                merged[^1] = new Moment(last.StartMs, Math.Max(last.EndMs, end), last.Score + r.Score,
                    last.PersonIds.Concat(r.PersonIds).Distinct().ToList());
            }
            else merged.Add(new Moment(start, end, r.Score, r.PersonIds));
        }
        return merged;
    }
}
