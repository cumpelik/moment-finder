using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MomentFinder.Sync;

/// <summary>
/// One change of the shared library: a field of an object got a value at a time. Objects are named by keys that
/// mean the same on every computer (Keys), never by the row numbers of one library.
/// </summary>
public sealed record Change(string Obj, string Field, string? Value, string Hlc);

/// <summary>
/// What the people of a library decided (names, who is who, buckets, folders in the cloud), as facts: the newest
/// value of each field of each object, with the time it was set. Every computer keeps them in its library; a
/// change made here also waits in the outbox until it is sent to the others (LibrarySync). Of two values of one
/// field the later one wins, wherever it was set.
/// </summary>
public sealed class FactStore
{
    private readonly object _lock = new();
    private long _lastMs;
    private int _counter;

    public FactStore(string device) => Device = device;

    /// <summary>A change was made here (it is sent soon).</summary>
    public event Action? Recorded;

    /// <summary>This computer, as it signs its changes.</summary>
    public string Device { get; }

    /// <summary>
    /// A time that is later than every time this computer has set or seen (a hybrid clock: milliseconds, a counter,
    /// the computer), so a computer whose clock is behind does not lose its changes to older ones.
    /// </summary>
    public string Now()
    {
        lock (_lock)
        {
            var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (ms > _lastMs)
            {
                _lastMs = ms;
                _counter = 0;
            }
            else _counter++;
            return Format(_lastMs, _counter, Device);
        }
    }

    /// <summary>A time seen in another computer's change: this computer's next changes are later.</summary>
    public void Observe(string hlc)
    {
        if (!TryParse(hlc, out var ms, out var counter)) return;
        lock (_lock)
        {
            if (ms > _lastMs || ms == _lastMs && counter > _counter)
            {
                _lastMs = ms;
                _counter = counter;
            }
        }
    }

    /// <summary>The time of the facts the library had before it was shared: any real change is later.</summary>
    public string Origin => Format(0, 0, Device);

    internal static string Format(long ms, int counter, string device) =>
        ms.ToString("x12", CultureInfo.InvariantCulture) + "." + counter.ToString("x4", CultureInfo.InvariantCulture) + "." + device;

    private static bool TryParse(string hlc, out long ms, out int counter)
    {
        ms = 0;
        counter = 0;
        var parts = hlc.Split('.');
        return parts.Length >= 3 && long.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ms) &&
               int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out counter);
    }

    /// <summary>A change made on this computer: kept as the field's newest value and queued for the others.</summary>
    public void Set(SqliteConnection c, IDbTransaction? tx, string obj, string field, string? value)
    {
        var hlc = Now();
        c.Execute("""
            INSERT INTO fact(obj, field, value, hlc) VALUES(@obj, @field, @value, @hlc)
            ON CONFLICT(obj, field) DO UPDATE SET value=excluded.value, hlc=excluded.hlc
            """, new { obj, field, value, hlc }, tx);
        c.Execute("INSERT INTO outbox(obj, field, value, hlc) VALUES(@obj, @field, @value, @hlc)", new { obj, field, value, hlc }, tx);
        Recorded?.Invoke();
    }

    /// <summary>A fact the library had before it was shared (kept, not sent: the first snapshot carries it).</summary>
    public void Seed(SqliteConnection c, IDbTransaction tx, string obj, string field, string? value) =>
        c.Execute("INSERT OR IGNORE INTO fact(obj, field, value, hlc) VALUES(@obj, @field, @value, @hlc)",
            new { obj, field, value, hlc = Origin }, tx);

    /// <summary>A change from another computer: kept when it is later than what this library has. True when kept.</summary>
    public bool Apply(SqliteConnection c, IDbTransaction tx, Change change)
    {
        Observe(change.Hlc);
        return c.Execute("""
            INSERT INTO fact(obj, field, value, hlc) VALUES(@Obj, @Field, @Value, @Hlc)
            ON CONFLICT(obj, field) DO UPDATE SET value=excluded.value, hlc=excluded.hlc WHERE excluded.hlc > fact.hlc
            """, change, tx) > 0;
    }

    public static string? Get(SqliteConnection c, IDbTransaction? tx, string obj, string field) =>
        c.ExecuteScalar<string?>("SELECT value FROM fact WHERE obj=@obj AND field=@field", new { obj, field }, tx);
}

/// <summary>The names of shared objects: the same on every computer.</summary>
public static class Keys
{
    public const string Library = "lib";

    public static string Person(string uid) => "p:" + uid;
    public static string Bucket(string uid) => "b:" + uid;
    public static string BucketItem(string bucketUid, string mediaUid) => $"bi:{bucketUid}:{mediaUid}";
    public static string Folder(string uid) => "f:" + uid;
    public static string Media(string uid) => "m:" + uid;
    public static string Content(string quickHash, long size) => $"c:{quickHash}-{size.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// A face or a figure: its picture, the moment and the place in the picture. Two computers that analyzed the
    /// same photo find nearly the same box: they are matched by overlap (see PersonFacts), not by this text.
    /// </summary>
    public static string Detection(string mediaUid, int kind, long timeMs, double x, double y, double w, double h) =>
        string.Create(CultureInfo.InvariantCulture, $"d:{mediaUid}:{kind}:{timeMs}:{x:0.0000}:{y:0.0000}:{w:0.0000}:{h:0.0000}");

    /// <summary>The parts of a detection key; null when it is not one.</summary>
    public static (string MediaUid, int Kind, long TimeMs, float X, float Y, float W, float H)? ParseDetection(string key)
    {
        var p = key.Split(':');
        if (p.Length != 8 || p[0] != "d") return null;
        var inv = CultureInfo.InvariantCulture;
        return int.TryParse(p[2], inv, out var kind) && long.TryParse(p[3], inv, out var time) &&
               float.TryParse(p[4], inv, out var x) && float.TryParse(p[5], inv, out var y) &&
               float.TryParse(p[6], inv, out var w) && float.TryParse(p[7], inv, out var h)
            ? (p[1], kind, time, x, y, w, h) : null;
    }

    /// <summary>
    /// A photo or video: its folder and its place in it. The same on the computer whose folder it is and on every
    /// computer that sees the folder in the cloud, and found the same by two computers that read it each on its own.
    /// </summary>
    public static string MediaUid(string? sourceUid, string relPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes((sourceUid ?? "") + "/" + relPath));
        return Convert.ToHexStringLower(hash, 0, 12);
    }

    public static string NewUid() => Guid.NewGuid().ToString("N");
}
