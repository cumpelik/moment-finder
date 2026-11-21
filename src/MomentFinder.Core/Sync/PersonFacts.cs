using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MomentFinder.Sync;

/// <summary>Who is on a face or a figure, as the user said it: "yes" or "no" for a person, kept per detection.</summary>
public static class PersonFacts
{
    public const string Yes = "yes";
    public const string No = "no";

    /// <summary>The shared keys of detections (their photo, moment and box).</summary>
    public static List<(long Id, string Key)> DetectionKeys(SqliteConnection c, IDbTransaction? tx, IEnumerable<long> detectionIds)
    {
        var result = new List<(long, string)>();
        foreach (var chunk in detectionIds.Distinct().Chunk(500))
        {
            foreach (var d in c.Query<KeyRow>("""
                         SELECT d.id AS Id, m.uid AS MediaUid, d.kind AS Kind, d.time_ms AS TimeMs, d.x AS X, d.y AS Y, d.w AS W, d.h AS H
                         FROM detection d JOIN media m ON m.id=d.media_id WHERE d.id IN @chunk
                         """, new { chunk }, tx))
            {
                if (d.MediaUid != null) result.Add((d.Id, Keys.Detection(d.MediaUid, d.Kind, d.TimeMs, d.X, d.Y, d.W, d.H)));
            }
        }
        return result;
    }

    public static string? PersonUid(SqliteConnection c, IDbTransaction? tx, long personId) =>
        c.ExecuteScalar<string?>("SELECT uid FROM person WHERE id=@personId", new { personId }, tx);

    /// <summary>The user said these detections are (yes) or are not (no) the person.</summary>
    public static void Say(SqliteConnection c, IDbTransaction? tx, FactStore facts, IEnumerable<long> detectionIds, long personId, string verdict)
    {
        if (PersonUid(c, tx, personId) is not { } person) return;
        foreach (var (_, key) in DetectionKeys(c, tx, detectionIds))
            if (FactStore.Get(c, tx, key, person) != verdict) facts.Set(c, tx, key, person, verdict);
    }

    private sealed class KeyRow
    {
        public long Id { get; init; }
        public string? MediaUid { get; init; }
        public int Kind { get; init; }
        public long TimeMs { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double W { get; init; }
        public double H { get; init; }
    }
}
