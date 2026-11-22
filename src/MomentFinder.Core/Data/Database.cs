using Dapper;
using Microsoft.Data.Sqlite;

namespace MomentFinder.Data;

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
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        Migrate(c, path);
        MomentFinder.Sync.FactSeed.Run(this);
    }

    private readonly string _dataDir;
    private MomentFinder.Cloud.DeviceInfo? _device;
    private MomentFinder.Sync.FactStore? _facts;

    /// <summary>This computer (kept beside the library, never in it: a library copied elsewhere is on another computer).</summary>
    public MomentFinder.Cloud.DeviceInfo Device => _device ??= MomentFinder.Cloud.DeviceInfo.Load(_dataDir);

    /// <summary>What the people of the library decided, as facts shared with the other computers (see FactStore).</summary>
    public MomentFinder.Sync.FactStore Facts => _facts ??= new MomentFinder.Sync.FactStore(Device.Id);

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        // A photo's key in the shared library: its folder and its place there (see Keys.MediaUid); the triggers use it.
        c.CreateFunction("media_uid", (string? sourceUid, string relPath) => MomentFinder.Sync.Keys.MediaUid(sourceUid, relPath), isDeterministic: true);
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
        // 12: a source can be read-only: nothing in it changes from MomentFinder until read-only is turned off with the password.
        "ALTER TABLE source ADD COLUMN protected INTEGER NOT NULL DEFAULT 0;",
        // 13: a library of millions of photos: what the sidebar, Who is this and the review ask for is answered from
        // indexes, without reading the detections themselves (their appearance vectors make the table large).
        """
        CREATE INDEX IF NOT EXISTS detection_person_media ON detection(person_id, media_id);
        CREATE INDEX IF NOT EXISTS detection_unnamed ON detection(person_id, kind, cluster, media_id, quality);
        CREATE INDEX IF NOT EXISTS detection_cluster_quality ON detection(cluster, quality);
        CREATE INDEX IF NOT EXISTS detection_suggest_open ON detection(suggest_person_id, confirmed, kind, suggest_score);
        CREATE INDEX IF NOT EXISTS media_source_state ON media(source_id, missing, kind, thumb_state, analysis_version, people_version);
        ANALYZE;
        """,
        // 14: whether the capture time is only the file's time (no date of its own in the photo or video): 1 yes, 0 no,
        // NULL read before this was kept.
        "ALTER TABLE media ADD COLUMN taken_from_file INTEGER;",
        // 15: one library shared by several computers. Folders, photos, people and buckets get keys that mean the same
        // on every computer (uid; a photo's is made of its folder's and its path, so two computers that read one
        // folder find the same keys). What the people decided is kept as facts (the newest value of each field, with
        // its time), changes made here wait in the outbox until they are sent; sync_seen is how far the changes of
        // each other computer were taken in; a person merged into another on any computer stays found by the old key.
        """
        ALTER TABLE source ADD COLUMN uid TEXT;
        ALTER TABLE source ADD COLUMN owner TEXT;
        ALTER TABLE person ADD COLUMN uid TEXT;
        ALTER TABLE bucket ADD COLUMN uid TEXT;
        ALTER TABLE media ADD COLUMN uid TEXT;
        UPDATE source SET uid=lower(hex(randomblob(16))) WHERE uid IS NULL;
        UPDATE person SET uid=lower(hex(randomblob(16))) WHERE uid IS NULL;
        UPDATE bucket SET uid=lower(hex(randomblob(16))) WHERE uid IS NULL;
        UPDATE media SET uid=media_uid((SELECT uid FROM source WHERE id=media.source_id), rel_path);
        CREATE UNIQUE INDEX source_uid ON source(uid);
        CREATE UNIQUE INDEX person_uid ON person(uid);
        CREATE UNIQUE INDEX bucket_uid ON bucket(uid);
        CREATE INDEX media_uid ON media(uid);
        CREATE TRIGGER source_uid_new AFTER INSERT ON source WHEN NEW.uid IS NULL
            BEGIN UPDATE source SET uid=lower(hex(randomblob(16))) WHERE id=NEW.id; END;
        CREATE TRIGGER person_uid_new AFTER INSERT ON person WHEN NEW.uid IS NULL
            BEGIN UPDATE person SET uid=lower(hex(randomblob(16))) WHERE id=NEW.id; END;
        CREATE TRIGGER bucket_uid_new AFTER INSERT ON bucket WHEN NEW.uid IS NULL
            BEGIN UPDATE bucket SET uid=lower(hex(randomblob(16))) WHERE id=NEW.id; END;
        CREATE TRIGGER media_uid_new AFTER INSERT ON media
            BEGIN UPDATE media SET uid=media_uid((SELECT uid FROM source WHERE id=NEW.source_id), NEW.rel_path) WHERE id=NEW.id; END;
        CREATE TRIGGER media_uid_moved AFTER UPDATE OF source_id, rel_path ON media
            BEGIN UPDATE media SET uid=media_uid((SELECT uid FROM source WHERE id=NEW.source_id), NEW.rel_path) WHERE id=NEW.id; END;
        CREATE TABLE fact(obj TEXT NOT NULL, field TEXT NOT NULL, value TEXT, hlc TEXT NOT NULL, PRIMARY KEY(obj, field)) WITHOUT ROWID;
        CREATE TABLE outbox(seq INTEGER PRIMARY KEY, obj TEXT NOT NULL, field TEXT NOT NULL, value TEXT, hlc TEXT NOT NULL);
        CREATE TABLE sync_seen(device TEXT PRIMARY KEY, seq INTEGER NOT NULL, name TEXT, last_seen INTEGER);
        CREATE TABLE person_alias(from_uid TEXT PRIMARY KEY, into_uid TEXT NOT NULL);
        """,
        // 16: the last file of each other computer's changes taken in (they are named by time, so later ones sort after).
        "ALTER TABLE sync_seen ADD COLUMN last_file TEXT;",
        // 17: facts found by what they say (the photos moved to a key, the files of a folder), not only by their object.
        "CREATE INDEX fact_field_value ON fact(field, value);",
        // 18: what the user asked to be done in the cloud (files uploaded into a folder there, copied from another
        // storage, moved, deleted): done in the background, file by file, so a break goes on where it stopped and what
        // did not go can be shown with its reason. spec says what and where (CloudJobs). A file uploaded from this
        // computer is read from where it came from while it is still there, not downloaded again.
        """
        CREATE TABLE cloud_job(
            id INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            title TEXT NOT NULL,
            spec TEXT NOT NULL,
            target_id INTEGER NOT NULL REFERENCES source(id) ON DELETE CASCADE,
            state TEXT NOT NULL DEFAULT 'waiting',
            prepared INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            finished_at INTEGER,
            attempts INTEGER NOT NULL DEFAULT 0,
            tried_at INTEGER,
            message TEXT);
        CREATE TABLE cloud_job_file(
            job_id INTEGER NOT NULL REFERENCES cloud_job(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            origin TEXT NOT NULL,
            target TEXT NOT NULL,
            size INTEGER NOT NULL DEFAULT 0,
            mtime INTEGER NOT NULL DEFAULT 0,
            state INTEGER NOT NULL DEFAULT 0,
            error TEXT,
            PRIMARY KEY(job_id, seq)) WITHOUT ROWID;
        CREATE INDEX cloud_job_file_state ON cloud_job_file(job_id, state);
        CREATE TABLE upload_origin(
            media_id INTEGER PRIMARY KEY REFERENCES media(id) ON DELETE CASCADE,
            path TEXT NOT NULL);
        """,
        // 19: a photo or video the user turned (0, 90, 180, 270 degrees clockwise): shown turned, the file and what
        // is stored about it (boxes, tracks) stay in the file's own coordinates (Media.QuarterTurn).
        "ALTER TABLE media ADD COLUMN rotation INTEGER NOT NULL DEFAULT 0;",
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
        lock (_unwritten)
            if (_unwritten.TryGetValue(key, out var value)) return value;
        using var c = Open();
        return c.ExecuteScalar<string?>("SELECT value FROM setting WHERE key=@key", new { key });
    }

    /// <summary>Preferences changed in the window and not written yet: read from here meanwhile.</summary>
    private readonly Dictionary<string, string?> _unwritten = [];
    private Task _writingSettings = Task.CompletedTask;

    /// <summary>
    /// A preference changed in the window (the grouping, the size of a panel): known at once, written in the
    /// background. The window never waits for another write to the library to end: the people matching or the
    /// other computers' changes can be written for many seconds, and the window would stand still that long.
    /// </summary>
    public void SetSettingSoon(string key, string? value)
    {
        lock (_unwritten)
        {
            _unwritten[key] = value;
            _writingSettings = _writingSettings.ContinueWith(_ => WriteSetting(key), TaskScheduler.Default);
        }
    }

    private void WriteSetting(string key)
    {
        string? value;
        lock (_unwritten)
            if (!_unwritten.TryGetValue(key, out value)) return;
        // Busy for longer than a connection waits: again, for a minute. What cannot be written stays known until the app closes.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SetSetting(key, value);
                break;
            }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6 && attempt < 12) { }
            catch (SqliteException)
            {
                return;
            }
        }
        // Changed again meanwhile: that value has its own turn after this one.
        lock (_unwritten)
            if (_unwritten.TryGetValue(key, out var now) && now == value) _unwritten.Remove(key);
    }

    /// <summary>Before the app closes: the preferences still waiting are written (not for longer than a moment).</summary>
    public void FlushSettings()
    {
        Task writing;
        lock (_unwritten) writing = _writingSettings;
        writing.Wait(TimeSpan.FromSeconds(10));
    }

    public void SetSetting(string key, string? value)
    {
        using var c = Open();
        c.Execute("INSERT INTO setting(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            new { key, value });
    }
}
