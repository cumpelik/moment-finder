using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using Dapper;
using MediaLens.Data;

namespace MediaLens.Vectors;

public sealed record VectorRow(long MediaId, int TimeMs, float[] Vector);

/// <summary>The best matching row of one media item (a video has many rows, one per sampled frame).</summary>
public sealed record VectorHit(long MediaId, int TimeMs, float Score);

public interface IVectorIndex
{
    string ModelId { get; }
    int Dimension { get; }
    int Count { get; }
    /// <summary>Replaces all rows of the media items in rows (re-analysis), or adds them.</summary>
    void Put(long mediaId, IReadOnlyList<VectorRow> rows);
    void RemoveMedia(long mediaId);
    /// <summary>The rows of one media item (to give a copy of the same file the same search data).</summary>
    List<VectorRow> RowsOf(long mediaId);
    /// <summary>Cosine similarity (vectors are L2 normalized), best row per media item, highest first.</summary>
    List<VectorHit> Search(ReadOnlySpan<float> query, IReadOnlySet<long>? allowedMedia, int topK);
}

/// <summary>
/// Exact search over a flat file of float16 vectors. SQLite maps a row number to the media item; the vectors
/// themselves are in memory as Half (200k rows x 768 = 300 MB) and every query scans all of them with SIMD.
/// That is tens of milliseconds for a family archive and needs no approximate index. File layout:
/// 64-byte header (magic, dimension, model id), then rows of Dimension halves. Rows are appended only;
/// a removed row stays in the file as dead space.
/// </summary>
public sealed class FlatVectorIndex : IVectorIndex, IDisposable
{
    private const int HeaderSize = 64;
    private static readonly byte[] Magic = "MLV1"u8.ToArray();

    private readonly Database _db;
    private readonly string _path;
    private readonly object _lock = new();
    private FileStream _file;
    private Half[] _data = [];
    private long[] _mediaOf = [];
    private int[] _timeOf = [];
    private int _rows;
    private readonly Dictionary<long, List<int>> _rowsOfMedia = new();

    public string ModelId { get; }
    public int Dimension { get; }
    public int Count { get { lock (_lock) return _rowsOfMedia.Values.Sum(r => r.Count); } }

    /// <summary>When the model id or dimension in the file differs, the old vectors are dropped and onReset runs (re-analysis).</summary>
    public FlatVectorIndex(Database db, string path, string modelId, int dimension, Action? onReset = null)
    {
        _db = db;
        _path = path;
        ModelId = modelId;
        Dimension = dimension;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (!HeaderMatches())
        {
            using (var c = db.Open()) c.Execute("DELETE FROM scene_vec");
            _file.SetLength(0);
            WriteHeader();
            onReset?.Invoke();
        }
        Load();
    }

    private bool HeaderMatches()
    {
        if (_file.Length < HeaderSize) return false;
        var header = new byte[HeaderSize];
        _file.Position = 0;
        _file.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic)) return false;
        if (BitConverter.ToInt32(header, 4) != Dimension) return false;
        var id = Encoding.UTF8.GetString(header, 8, HeaderSize - 8).TrimEnd('\0');
        return id == ModelId;
    }

    private void WriteHeader()
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4), Dimension);
        var id = Encoding.UTF8.GetBytes(ModelId);
        id.AsSpan(0, Math.Min(id.Length, HeaderSize - 8)).CopyTo(header.AsSpan(8));
        _file.Position = 0;
        _file.Write(header);
        _file.Flush(true);
    }

    private void Load()
    {
        var rowBytes = Dimension * 2;
        _rows = (int)((_file.Length - HeaderSize) / rowBytes);
        EnsureCapacity(_rows);
        _file.Position = HeaderSize;
        _file.ReadExactly(MemoryMarshal.AsBytes(_data.AsSpan(0, _rows * Dimension)));
        Array.Fill(_mediaOf, -1);

        using var c = _db.Open();
        var orphans = new List<long>();
        foreach (var r in c.Query<(long Row, long MediaId, long TimeMs)>("SELECT row, media_id, time_ms FROM scene_vec"))
        {
            // The file is written before the database, so this only happens with a damaged file.
            if (r.Row >= _rows) { orphans.Add(r.MediaId); continue; }
            Link((int)r.Row, r.MediaId, (int)r.TimeMs);
        }
        if (orphans.Count > 0)
        {
            c.Execute("DELETE FROM scene_vec WHERE media_id IN @orphans", new { orphans });
            c.Execute("UPDATE media SET analysis_version=0 WHERE id IN @orphans", new { orphans });
            foreach (var m in orphans) Unlink(m);
        }
    }

    private void EnsureCapacity(int rows)
    {
        if (_mediaOf.Length >= rows) return;
        var capacity = Math.Max(rows, Math.Max(1024, _mediaOf.Length * 2));
        Array.Resize(ref _data, capacity * Dimension);
        var oldLength = _mediaOf.Length;
        Array.Resize(ref _mediaOf, capacity);
        Array.Resize(ref _timeOf, capacity);
        _mediaOf.AsSpan(oldLength).Fill(-1);
    }

    private void Link(int row, long mediaId, int timeMs)
    {
        _mediaOf[row] = mediaId;
        _timeOf[row] = timeMs;
        if (!_rowsOfMedia.TryGetValue(mediaId, out var list))
            _rowsOfMedia[mediaId] = list = [];
        list.Add(row);
    }

    private void Unlink(long mediaId)
    {
        if (!_rowsOfMedia.Remove(mediaId, out var rows)) return;
        foreach (var r in rows) _mediaOf[r] = -1;
    }

    public void Put(long mediaId, IReadOnlyList<VectorRow> rows)
    {
        lock (_lock)
        {
            var first = _rows;
            EnsureCapacity(_rows + rows.Count);
            var buffer = new Half[rows.Count * Dimension];
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Vector.Length != Dimension)
                    throw new ArgumentException($"Vector has {rows[i].Vector.Length} dimensions, the index {Dimension}");
                TensorPrimitives.ConvertToHalf(rows[i].Vector, buffer.AsSpan(i * Dimension, Dimension));
            }
            _file.Position = HeaderSize + (long)first * Dimension * 2;
            _file.Write(MemoryMarshal.AsBytes(buffer.AsSpan()));
            _file.Flush(true);
            buffer.CopyTo(_data, first * Dimension);
            _rows += rows.Count;

            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                c.Execute("DELETE FROM scene_vec WHERE media_id=@mediaId", new { mediaId }, tx);
                for (var i = 0; i < rows.Count; i++)
                    c.Execute("INSERT INTO scene_vec(row, media_id, time_ms) VALUES(@row, @mediaId, @time)",
                        new { row = first + i, mediaId, time = rows[i].TimeMs }, tx);
                tx.Commit();
            }

            Unlink(mediaId);
            for (var i = 0; i < rows.Count; i++) Link(first + i, mediaId, rows[i].TimeMs);
        }
    }

    public List<VectorRow> RowsOf(long mediaId)
    {
        lock (_lock)
        {
            if (!_rowsOfMedia.TryGetValue(mediaId, out var rows)) return [];
            return rows.Select(r =>
            {
                var vector = new float[Dimension];
                TensorPrimitives.ConvertToSingle(_data.AsSpan(r * Dimension, Dimension), vector);
                return new VectorRow(mediaId, _timeOf[r], vector);
            }).ToList();
        }
    }

    public void RemoveMedia(long mediaId)
    {
        lock (_lock)
        {
            using (var c = _db.Open())
                c.Execute("DELETE FROM scene_vec WHERE media_id=@mediaId", new { mediaId });
            Unlink(mediaId);
        }
    }

    public List<VectorHit> Search(ReadOnlySpan<float> query, IReadOnlySet<long>? allowedMedia, int topK)
    {
        if (query.Length != Dimension) throw new ArgumentException("Query dimension mismatch");
        var q = query.ToArray();
        Half[] data;
        long[] mediaOf;
        int[] timeOf;
        int rows;
        lock (_lock)
        {
            // Rows only get appended, so a snapshot of the arrays is safe to read without the lock.
            data = _data;
            mediaOf = _mediaOf;
            timeOf = _timeOf;
            rows = _rows;
        }

        var best = new Dictionary<long, VectorHit>();
        var gate = new object();
        const int chunk = 4096;
        Parallel.For(0, (rows + chunk - 1) / chunk, () => new Dictionary<long, VectorHit>(), (ci, _, local) =>
        {
            Span<float> scratch = stackalloc float[Dimension];
            var end = Math.Min(rows, (ci + 1) * chunk);
            for (var r = ci * chunk; r < end; r++)
            {
                var m = mediaOf[r];
                if (m < 0 || (allowedMedia != null && !allowedMedia.Contains(m))) continue;
                TensorPrimitives.ConvertToSingle(data.AsSpan(r * Dimension, Dimension), scratch);
                var score = TensorPrimitives.Dot(scratch, q);
                if (!local.TryGetValue(m, out var hit) || hit.Score < score)
                    local[m] = new VectorHit(m, timeOf[r], score);
            }
            return local;
        }, local =>
        {
            lock (gate)
            {
                foreach (var (m, hit) in local)
                    if (!best.TryGetValue(m, out var b) || b.Score < hit.Score)
                        best[m] = hit;
            }
        });

        return best.Values.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    public void Dispose() => _file.Dispose();
}
