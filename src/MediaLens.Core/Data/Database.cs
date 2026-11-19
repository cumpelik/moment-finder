using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaLens.Data;

/// <summary>
/// The SQLite index: metadata and relations only. Embeddings live in flat vector files (see Vectors).
/// Every call opens its own pooled connection, so the services can run on any thread.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        Migrate(c);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        return c;
    }

    private static readonly string[] Migrations =
    [
        // 1: library, vectors, buckets
        """
        CREATE TABLE source(
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            root TEXT NOT NULL,
            name TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            added_at INTEGER NOT NULL,
            last_scan_at INTEGER);

        CREATE TABLE media(
            id INTEGER PRIMARY KEY,
            source_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE,
            rel_path TEXT NOT NULL,
            kind INTEGER NOT NULL,
            size INTEGER NOT NULL,
            mtime INTEGER NOT NULL,
            quick_hash TEXT,
            taken_at INTEGER NOT NULL,
            width INTEGER,
            height INTEGER,
            duration_ms INTEGER,
            lat REAL,
            lon REAL,
            camera TEXT,
            thumb_state INTEGER NOT NULL DEFAULT 0,
            analysis_version INTEGER NOT NULL DEFAULT 0,
            missing INTEGER NOT NULL DEFAULT 0,
            UNIQUE(source_id, rel_path));
        CREATE INDEX media_taken ON media(taken_at);
        CREATE INDEX media_quick_hash ON media(quick_hash);

        CREATE TABLE scene_vec(
            row INTEGER PRIMARY KEY,
            media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
            time_ms INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX scene_vec_media ON scene_vec(media_id);

        CREATE TABLE bucket(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            created_at INTEGER NOT NULL);

        CREATE TABLE bucket_item(
            bucket_id INTEGER NOT NULL REFERENCES bucket(id) ON DELETE CASCADE,
            media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
            position REAL NOT NULL,
            added_at INTEGER NOT NULL,
            PRIMARY KEY(bucket_id, media_id));

        CREATE TABLE setting(key TEXT PRIMARY KEY, value TEXT);
        """,
    ];

    private static void Migrate(SqliteConnection c)
    {
        var version = c.ExecuteScalar<int>("PRAGMA user_version;");
        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            c.Execute(Migrations[i], transaction: tx);
            c.Execute($"PRAGMA user_version={i + 1};", transaction: tx);
            tx.Commit();
        }
    }

    public string? GetSetting(string key)
    {
        using var c = Open();
        return c.ExecuteScalar<string?>("SELECT value FROM setting WHERE key=@key", new { key });
    }

    public void SetSetting(string key, string? value)
    {
        using var c = Open();
        c.Execute("INSERT INTO setting(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            new { key, value });
    }
}
