using System.Text;
using Dapper;
using MomentFinder.Cloud;
using MomentFinder.Data;
using MomentFinder.Library;
using MomentFinder.Vectors;

namespace MomentFinder.Sync;

/// <summary>
/// What is computed from a photo's content (its thumbnail, its search data) is computed once for the whole library:
/// the computer that made it puts it into the cloud under the content's key, the others take it from there instead
/// of downloading the photo and computing it again. Stored once, however many computers use the library.
/// <code>
/// .medialens/shared/content/&lt;2 characters&gt;/&lt;quick hash&gt;-&lt;size&gt;.jpg     thumbnail
/// .medialens/shared/content/&lt;2 characters&gt;/&lt;quick hash&gt;-&lt;size&gt;.scene   search data (model, moments, vectors)
/// </code>
/// </summary>
public sealed class SharedContent
{
    private const int Batch = 500;
    /// <summary>Thumbnails are tens of kilobytes: the connections are what limits them, not the line.</summary>
    private const int SmallFilesAtOnce = 16;

    private readonly Database _db;
    private readonly CloudService _cloud;
    private readonly AppPaths _paths;
    private readonly Func<string?> _root;
    private readonly HashSet<long> _failed = [];

    public SharedContent(Database db, CloudService cloud, AppPaths paths, Func<string?> root)
    {
        _db = db;
        _cloud = cloud;
        _paths = paths;
        _root = root;
    }

    /// <summary>The search data, once its model is loaded.</summary>
    public FlatVectorIndex? Scene { get; set; }

    private string SceneField => $"scene:{Scene!.ModelId}:{Indexer.AnalysisVersion}";

    private static string Name(string quickHash, long size, string extension) => $"{quickHash[..2]}/{quickHash}-{size}{extension}";

    private sealed class Item
    {
        public long Id { get; init; }
        public string QuickHash { get; init; } = "";
        public long Size { get; init; }
    }

    // ---- taking ----

    /// <summary>Thumbnails and search data another computer made for photos still waiting for them here. True when some came.</summary>
    public async Task<bool> TakeAsync(CancellationToken ct)
    {
        if (_root() is not { } root) return false;
        var took = await TakeThumbnailsAsync(root, ct);
        if (Scene != null) took |= await TakeSceneAsync(root, ct);
        return took;
    }

    /// <summary>A batch is about to be fetched; with how many photos still wait (to say how far the taking is).</summary>
    public Action<int>? Coming { get; set; }

    /// <summary>
    /// How many photos still wait for a thumbnail or search data another computer made. Not counted: those that did
    /// not come (they are made here).
    /// </summary>
    private int Left()
    {
        using var c = _db.Open();
        int Count(string field, string where) => c.ExecuteScalar<int>($"""
            SELECT COUNT(*) FROM media m
            WHERE m.missing=0 AND m.quick_hash IS NOT NULL AND {where}
              AND EXISTS(SELECT 1 FROM fact f WHERE f.obj='c:'||m.quick_hash||'-'||m.size AND f.field=@field)
            """, new { field });
        var thumbs = Count("thumb", "m.thumb_state=0");
        var scene = Scene == null ? 0 : Count(SceneField, $"m.thumb_state IN (0,1) AND m.analysis_version<{Indexer.AnalysisVersion}");
        return Math.Max(0, Math.Max(thumbs, scene) - _failed.Count);
    }

    private List<Item> Waiting(string field, string where)
    {
        using var c = _db.Open();
        return c.Query<Item>($"""
            SELECT m.id AS Id, m.quick_hash AS QuickHash, m.size AS Size FROM media m
            JOIN fact f ON f.obj='c:'||m.quick_hash||'-'||m.size AND f.field=@field
            WHERE m.missing=0 AND m.quick_hash IS NOT NULL AND {where}
            LIMIT {Batch * 2}
            """, new { field }).Where(i => !_failed.Contains(i.Id)).Take(Batch).ToList();
    }

    private async Task<string?> DownloadAsync(string root, IEnumerable<string> names, CancellationToken ct)
    {
        var list = names.Distinct().ToList();
        if (list.Count == 0) return null;
        var staging = Path.Combine(_paths.CacheDir, "content-in-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await _cloud.Rclone.CopyFilesAsync(CloudService.Join(root, "content"), staging, list, null, ct, SmallFilesAtOnce);
        }
        catch (RcloneException)
        {
            // What came is used; what did not is made here.
        }
        return staging;
    }

    private async Task<bool> TakeThumbnailsAsync(string root, CancellationToken ct)
    {
        var items = Waiting("thumb", "m.thumb_state=0");
        if (items.Count == 0) return false;
        Coming?.Invoke(Left());
        var staging = await DownloadAsync(root, items.Select(i => Name(i.QuickHash, i.Size, ".jpg")), ct);
        try
        {
            using var c = _db.Open();
            foreach (var i in items)
            {
                var file = Path.Combine(staging!, Name(i.QuickHash, i.Size, ".jpg"));
                if (!File.Exists(file))
                {
                    _failed.Add(i.Id);
                    continue;
                }
                var target = _paths.ThumbPath(i.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                c.Execute("UPDATE media SET thumb_state=@ready WHERE id=@Id", new { ready = ThumbStates.Ready, i.Id });
            }
        }
        finally
        {
            if (staging != null) Directory.Delete(staging, true);
        }
        return true;
    }

    private async Task<bool> TakeSceneAsync(string root, CancellationToken ct)
    {
        var items = Waiting(SceneField, $"m.thumb_state=1 AND m.analysis_version<{Indexer.AnalysisVersion}");
        if (items.Count == 0) return false;
        Coming?.Invoke(Left());
        var staging = await DownloadAsync(root, items.Select(i => Name(i.QuickHash, i.Size, ".scene")), ct);
        try
        {
            foreach (var i in items)
            {
                var file = Path.Combine(staging!, Name(i.QuickHash, i.Size, ".scene"));
                var rows = File.Exists(file) ? ReadScene(file, i.Id) : null;
                if (rows == null)
                {
                    _failed.Add(i.Id);
                    continue;
                }
                Scene!.Put(i.Id, rows);
                using var c = _db.Open();
                c.Execute("UPDATE media SET analysis_version=@v WHERE id=@Id", new { v = Indexer.AnalysisVersion, i.Id });
            }
        }
        finally
        {
            if (staging != null) Directory.Delete(staging, true);
        }
        return true;
    }

    // ---- giving ----

    /// <summary>What this computer made for photos of shared folders and nobody gave yet: into the cloud. Returns how many.</summary>
    public async Task<int> GiveAsync(CancellationToken ct)
    {
        if (_root() is not { } root) return 0;
        var given = 0;
        // A few batches a round: the changes of the people are not held up behind thousands of thumbnails.
        for (var round = 0; round < 4; round++)
        {
            var n = await GiveThumbnailsAsync(root, ct);
            if (Scene != null) n += await GiveSceneAsync(root, ct);
            if (n == 0) break;
            given += n;
        }
        return given;
    }

    private List<Item> NotGiven(string field, string where)
    {
        using var c = _db.Open();
        return c.Query<Item>($"""
            SELECT m.id AS Id, m.quick_hash AS QuickHash, m.size AS Size FROM media m JOIN source s ON s.id=m.source_id
            WHERE m.missing=0 AND m.quick_hash IS NOT NULL AND {where}
              AND EXISTS(SELECT 1 FROM fact d WHERE d.obj='f:'||s.uid AND d.field='def')
              AND NOT EXISTS(SELECT 1 FROM fact d WHERE d.obj='f:'||s.uid AND d.field='deleted' AND d.value='1')
              AND NOT EXISTS(SELECT 1 FROM fact f WHERE f.obj='c:'||m.quick_hash||'-'||m.size AND f.field=@field)
            LIMIT {Batch}
            """, new { field }).ToList();
    }

    private async Task<int> UploadAsync(string root, string field, List<(Item Item, Action<string> Write)> files, string extension, CancellationToken ct)
    {
        if (files.Count == 0) return 0;
        var staging = Path.Combine(_paths.CacheDir, "content-out-" + Guid.NewGuid().ToString("N"));
        try
        {
            var names = new List<string>();
            foreach (var (item, write) in files)
            {
                var name = Name(item.QuickHash, item.Size, extension);
                var path = Path.Combine(staging, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                write(path);
                names.Add(name);
            }
            await _cloud.Rclone.CopyFilesAsync(staging, CloudService.Join(root, "content"), names, null, ct, SmallFilesAtOnce);
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var (item, _) in files) _db.Facts.Set(c, tx, Keys.Content(item.QuickHash, item.Size), field, "1");
            tx.Commit();
            return files.Count;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private Task<int> GiveThumbnailsAsync(string root, CancellationToken ct)
    {
        var files = NotGiven("thumb", $"m.thumb_state={ThumbStates.Ready}")
            .Where(i => File.Exists(_paths.ThumbPath(i.Id)))
            .Select(i => (i, (Action<string>)(path => File.Copy(_paths.ThumbPath(i.Id), path, true)))).ToList();
        return UploadAsync(root, "thumb", files, ".jpg", ct);
    }

    private Task<int> GiveSceneAsync(string root, CancellationToken ct)
    {
        var files = new List<(Item, Action<string>)>();
        foreach (var i in NotGiven(SceneField, $"m.analysis_version>={Indexer.AnalysisVersion}"))
        {
            var rows = Scene!.RowsOf(i.Id);
            if (rows.Count > 0) files.Add((i, path => WriteScene(path, Scene.ModelId, rows)));
        }
        return UploadAsync(root, SceneField, files, ".scene", ct);
    }

    // ---- the search data file ----

    private static readonly byte[] Magic = "MFS1"u8.ToArray();

    private static void WriteScene(string path, string modelId, IReadOnlyList<VectorRow> rows)
    {
        using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
        w.Write(Magic);
        w.Write(modelId);
        w.Write(rows[0].Vector.Length);
        w.Write(rows.Count);
        foreach (var r in rows)
        {
            w.Write(r.TimeMs);
            foreach (var v in r.Vector) w.Write((Half)v);
        }
    }

    /// <summary>The rows of a search data file; null when it is another model's or damaged.</summary>
    private List<VectorRow>? ReadScene(string path, long mediaId)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadString() != Scene!.ModelId) return null;
            var dims = r.ReadInt32();
            if (dims != Scene.Dimension) return null;
            var count = r.ReadInt32();
            var rows = new List<VectorRow>(count);
            for (var i = 0; i < count; i++)
            {
                var time = r.ReadInt32();
                var vector = new float[dims];
                for (var d = 0; d < dims; d++) vector[d] = (float)r.ReadHalf();
                rows.Add(new VectorRow(mediaId, time, vector));
            }
            return rows;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return null;
        }
    }
}
