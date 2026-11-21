using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MomentFinder.Cloud;
using MomentFinder.Library;

namespace MomentFinder.Sync;

/// <summary>
/// A folder as every computer of the library sees it: where it is in the cloud, and whether a computer's folder
/// decides what is in it (backup: the others only look) or it lives in the cloud (shared).
/// </summary>
public sealed record FolderDef(string Name, string Remote, string Mode, string? Owner, string? OwnerName)
{
    public const string Backup = "backup";
    public const string Shared = "shared";

    public string ToJson() => JsonSerializer.Serialize(this);
    public static FolderDef? FromJson(string? json)
    {
        try
        {
            return json is { Length: > 0 } ? JsonSerializer.Deserialize<FolderDef>(json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which folders the computers of a library share: every folder that is in a cloud (a cloud folder, a clone, a
/// folder backed up there). A folder only on this computer's disk is its own: nobody else could open its photos.
/// </summary>
public static class SharedFolders
{
    /// <summary>How the other computers see the folder; null when it is only on this computer.</summary>
    public static FolderDef? Def(SqliteConnection c, IDbTransaction? tx, long sourceId, DeviceInfo me)
    {
        var s = c.QueryFirstOrDefault<SourceRow>(
            "SELECT kind AS Kind, name AS Name, remote AS Remote, read_only AS ReadOnly, home_name AS HomeName, owner AS Owner FROM source WHERE id=@sourceId",
            new { sourceId }, tx);
        if (s == null) return null;
        if (s.Kind == SourceKinds.Cloud && s.Remote != null)
            return new FolderDef(s.Name, s.Remote, s.ReadOnly ? FolderDef.Backup : FolderDef.Shared, s.Owner, s.HomeName);
        var mirror = c.QueryFirstOrDefault<MirrorRow>("""
            SELECT m.remote AS Own, m.target AS Target, c.remote AS CloudRemote FROM mirror m JOIN cloud c ON c.id=m.cloud_id
            WHERE m.source_id=@sourceId AND m.enabled=1 ORDER BY m.last_run_at DESC LIMIT 1
            """, new { sourceId }, tx);
        return mirror == null ? null
            : new FolderDef(s.Name, mirror.Own ?? CloudService.Join(mirror.CloudRemote, mirror.Target), FolderDef.Backup, me.Id, me.Name);
    }

    /// <summary>
    /// The folder changed how it is in the cloud (added, backed up, cloned, taken home, removed): the other computers
    /// learn it. A folder that is in no cloud any more is gone for them.
    /// </summary>
    public static void Publish(SqliteConnection c, IDbTransaction? tx, FactStore facts, DeviceInfo me, long sourceId)
    {
        var uid = c.ExecuteScalar<string?>("SELECT uid FROM source WHERE id=@sourceId", new { sourceId }, tx);
        if (uid == null) return;
        var obj = Keys.Folder(uid);
        var def = Def(c, tx, sourceId, me);
        if (def == null)
        {
            if (FactStore.Get(c, tx, obj, "def") != null && FactStore.Get(c, tx, obj, "deleted") != "1") facts.Set(c, tx, obj, "deleted", "1");
            return;
        }
        var json = def.ToJson();
        if (FactStore.Get(c, tx, obj, "def") != json) facts.Set(c, tx, obj, "def", json);
        if (FactStore.Get(c, tx, obj, "deleted") == "1") facts.Set(c, tx, obj, "deleted", "0");
    }

    /// <summary>The folder is removed from the library: from every computer's.</summary>
    public static void Removed(SqliteConnection c, IDbTransaction? tx, FactStore facts, string? uid)
    {
        if (uid == null) return;
        var obj = Keys.Folder(uid);
        if (FactStore.Get(c, tx, obj, "def") != null) facts.Set(c, tx, obj, "deleted", "1");
    }

    private sealed class SourceRow
    {
        public string Kind { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Remote { get; init; }
        public bool ReadOnly { get; init; }
        public string? HomeName { get; init; }
        public string? Owner { get; init; }
    }

    private sealed class MirrorRow
    {
        public string? Own { get; init; }
        public string Target { get; init; } = "";
        public string CloudRemote { get; init; } = "";
    }
}
