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
        Migrate(c, path);
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
        // 2: people. A detection is a face or a whole person in a photo or a video frame; its person_id says
        // who it is, assign_source how we know (see People.AssignSource), confirmed that the user said so.
        """
        CREATE TABLE person(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE,
            created_at INTEGER NOT NULL,
            cover_detection_id INTEGER,
            hidden INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE event(
            id INTEGER PRIMARY KEY,
            start_at INTEGER NOT NULL,
            end_at INTEGER NOT NULL,
            items INTEGER NOT NULL);

        ALTER TABLE media ADD COLUMN event_id INTEGER;
        ALTER TABLE media ADD COLUMN people_version INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX media_event ON media(event_id);

        CREATE TABLE detection(
            id INTEGER PRIMARY KEY,
            media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
            time_ms INTEGER NOT NULL DEFAULT 0,
            kind INTEGER NOT NULL,
            x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL,
            score REAL NOT NULL,
            quality REAL NOT NULL,
            track INTEGER,
            linked_id INTEGER,
            embedding BLOB NOT NULL,
            cluster INTEGER,
            person_id INTEGER REFERENCES person(id) ON DELETE SET NULL,
            assign_source INTEGER,
            confidence REAL,
            confirmed INTEGER NOT NULL DEFAULT 0,
            suggest_person_id INTEGER REFERENCES person(id) ON DELETE SET NULL,
            suggest_score REAL);
        CREATE INDEX detection_media ON detection(media_id);
        CREATE INDEX detection_person ON detection(person_id);
        CREATE INDEX detection_suggest ON detection(suggest_person_id);
        CREATE INDEX detection_cluster ON detection(cluster);

        CREATE TABLE rejection(
            detection_id INTEGER NOT NULL REFERENCES detection(id) ON DELETE CASCADE,
            person_id INTEGER NOT NULL REFERENCES person(id) ON DELETE CASCADE,
            PRIMARY KEY(detection_id, person_id));
        """,
        // 3: clouds (rclone remotes), mirrors of sources to a cloud, sources that live in a cloud
        """
        CREATE TABLE cloud(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            remote TEXT NOT NULL,
            encrypted INTEGER NOT NULL,
            created_at INTEGER NOT NULL);

        CREATE TABLE mirror(
            id INTEGER PRIMARY KEY,
            source_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE,
            cloud_id INTEGER NOT NULL REFERENCES cloud(id) ON DELETE CASCADE,
            target TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            last_run_at INTEGER,
            last_result TEXT);

        ALTER TABLE source ADD COLUMN remote TEXT;
        """,
        // People analysis 2 changed only videos: photos stay as they are.
        "UPDATE media SET people_version=2 WHERE kind=1 AND people_version=1;",
        // Where each person of a video is in every analyzed frame (detections keep only a few of them).
        """
        CREATE TABLE track_box(
            media_id INTEGER NOT NULL REFERENCES media(id) ON DELETE CASCADE,
            track INTEGER NOT NULL,
            time_ms INTEGER NOT NULL,
            x REAL NOT NULL, y REAL NOT NULL, w REAL NOT NULL, h REAL NOT NULL);
        CREATE INDEX track_box_media ON track_box(media_id, track, time_ms);
        UPDATE media SET people_version=3 WHERE kind=1 AND people_version=2;
        """,
        // Video frames were taken half an interval late: videos are analyzed again, photos stay.
        """
        UPDATE media SET analysis_version=2 WHERE kind=1 AND analysis_version=1;
        UPDATE media SET people_version=4 WHERE kind=1 AND people_version=3;
        """,
        // People analysis 5 changed only videos.
        "UPDATE media SET people_version=5 WHERE kind=1 AND people_version=4;",
        // 360 media: the lens model changed (190 degrees and the camera's calibration): thumbnails, search and people again.
        """
        UPDATE media SET thumb_state=0, analysis_version=0, people_version=0
        WHERE lower(rel_path) LIKE '%.insv' OR lower(rel_path) LIKE '%.insp';
        """,
        // 360 videos are leveled by the camera's gyroscope: analyzed again in the leveled world.
        "UPDATE media SET thumb_state=0, analysis_version=0, people_version=0 WHERE lower(rel_path) LIKE '%.insv';",
        // 10: a source connected with a cloud. A mirror is the backup of a local folder: its place in the cloud
        // (through a crypt remote of its own when encrypted) and the files that are gone from the folder but still
        // in the cloud, waiting for the user's word. A cloud source knows its storage, and whether another
        // computer's folder decides what is in it (then it is only for looking).
        """
        ALTER TABLE mirror ADD COLUMN remote TEXT;
        ALTER TABLE mirror ADD COLUMN encrypted INTEGER NOT NULL DEFAULT 0;
        CREATE TABLE mirror_gone(
            mirror_id INTEGER NOT NULL REFERENCES mirror(id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            size INTEGER NOT NULL,
            PRIMARY KEY(mirror_id, path));
        ALTER TABLE source ADD COLUMN cloud_id INTEGER;
        ALTER TABLE source ADD COLUMN read_only INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE source ADD COLUMN home_name TEXT;
        """,
        // 11: a local folder added to a folder that lives in the cloud (several folders made one there): what waits
        // or runs, into which folder inside it, and how the last try ended.
        """
        CREATE TABLE merge_job(
            source_id INTEGER PRIMARY KEY REFERENCES source(id) ON DELETE CASCADE,
            target_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE,
            subfolder TEXT NOT NULL DEFAULT '',
            last_result TEXT);
        """,
    ];

    /// <summary>
    /// Brings an older library up to date. Before that a copy is kept next to it (library.v6.db): an upgrade may
    /// analyze again, and the user's names and confirmations must never be lost for good.
    /// </summary>
    private static void Migrate(SqliteConnection c, string path)
    {
        var version = c.ExecuteScalar<int>("PRAGMA user_version;");
        if (version > 0 && version < Migrations.Length)
        {
            var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                $"{Path.GetFileNameWithoutExtension(path)}.v{version}{Path.GetExtension(path)}");
            if (!File.Exists(backup)) c.Execute("VACUUM INTO @backup", new { backup });
        }
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
