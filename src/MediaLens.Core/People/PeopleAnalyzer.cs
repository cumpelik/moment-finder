using System.Globalization;
using System.Numerics.Tensors;
using MediaLens.Analysis;
using MediaLens.Library;
using MediaLens.Media;

namespace MediaLens.People;

/// <summary>Runs the people model on a photo or on frames of a video and stores the detections.</summary>
public sealed class PeopleAnalyzer
{
    private const int PhotoSize = 1600;
    private const int FrameSize = 960;
    private const int MaxPerTrack = 5;

    private readonly AppPaths _paths;
    private readonly PeopleRepository _repo;
    private readonly IPeopleModel _model;

    public PeopleAnalyzer(AppPaths paths, PeopleRepository repo, IPeopleModel model)
    {
        _paths = paths;
        _repo = repo;
        _model = model;
    }

    public IPeopleModel Model => _model;

    public async Task AnalyzeAsync(MediaItem item, CancellationToken ct)
    {
        if (item.IsVideo)
        {
            await AnalyzeVideoAsync(item, ct);
            return;
        }
        var path = await Cloud.MediaFiles.LocalPathAsync(item, ct);
        if (Fisheye360.IsDualFisheye(path))
        {
            var (lens0, lens1) = await Task.Run(() => Fisheye360.SplitSideBySide(RgbImage.Load(path, 2 * Lens360Size)), ct);
            var around = await Task.Run(() => Analyze360(lens0, lens1, includeWeak: false, Fisheye360.ModelOf(path)), ct);
            Save(item.Id, Frame(around, EquirectWidth, EquirectHeight, 0, null));
            return;
        }
        var image = await Task.Run(() => RgbImage.Load(path, PhotoSize), ct);
        var found = await Task.Run(() => _model.Analyze(image), ct);
        var items = Frame(found, image.Width, image.Height, 0, null);
        Save(item.Id, items);
    }

    /// <summary>Detections of one image, faces linked to the body they belong to.</summary>
    // ---- 360 ----

    /// <summary>Boxes of 360 media live on the equirectangular map; this is its size in "pixels" for the detections.</summary>
    private const int EquirectWidth = 4000;
    private const int EquirectHeight = 2000;
    private const int Lens360Size = 1440;
    private const int Views360 = 6;
    private const int View360Size = 768;
    private const double View360Fov = 100;

    /// <summary>
    /// People in a 360 picture: six perspective views around (neighbours overlap), each analyzed like a photo, the
    /// boxes put on the equirectangular map and the doubles of the overlaps dropped.
    /// </summary>
    private List<DetectedObject> Analyze360(RgbImage lens0, RgbImage lens1, bool includeWeak, LensModel model, Rot3? level = null)
    {
        var all = new List<DetectedObject>();
        foreach (var (yaw, view) in Fisheye360.ViewsAround(lens0, lens1, Views360, View360Size, View360Size, View360Fov, model, level))
            foreach (var d in _model.Analyze(view, includeWeak))
            {
                var (u, v, w, h) = Fisheye360.ViewBoxToEquirect(yaw, Fisheye360.DefaultPitch, View360Fov, View360Size, View360Size,
                    d.Box.X, d.Box.Y, d.Box.W, d.Box.H);
                // Near a view's edge the picture is stretched: the same person in the next view counts more.
                var central = 1 - Math.Abs(d.Box.CenterX / View360Size - 0.5f);
                all.Add(new DetectedObject
                {
                    Kind = d.Kind, Box = new Box(u * EquirectWidth, v * EquirectHeight, w * EquirectWidth, h * EquirectHeight),
                    Score = d.Score, Quality = d.Quality * central, Embedding = d.Embedding, Crop = d.Crop, Weak = d.Weak,
                });
            }
        var kept = new List<DetectedObject>();
        foreach (var d in all.OrderByDescending(d => d.Quality))
            if (!kept.Any(k => k.Kind == d.Kind && Fisheye360.WrappedIou(k.Box, d.Box, EquirectWidth) > 0.4f)) kept.Add(d);
        return kept;
    }

    // ---- 360: a closer look at people who ride away ----

    private const int LensZoomSize = 2880;
    private const int ZoomViewSize = 768;
    private const double ZoomFov = 36;
    /// <summary>A body lower than this part of the map's height (about 12 degrees) is looked at closer.</summary>
    private const float SmallBody = 0.07f;
    private const int MaxZooms = 6;

    /// <summary>Somebody seen lately in a 360 video: where on the map, how many frames ago, how large at most.</summary>
    private sealed class Followed
    {
        public float U, V, Peak;
        public int Missed;
    }

    /// <summary>Degrees between two points of the map (0..1), across the seam too.</summary>
    private static double Apart(double u0, double v0, double u1, double v1)
    {
        var du = Math.Abs(u0 - u1) % 1;
        if (du > 0.5) du = 1 - du;
        var dv = v0 - v1;
        return Math.Sqrt(du * 360 * du * 360 + dv * 180 * dv * 180);
    }

    /// <summary>
    /// The six views around see a skier only to some distance: in the frame where a person seen before got small
    /// or was not found any more, a narrow view (like a telephoto lens, from the lenses at full detail) is pointed
    /// where they were. What it finds joins the frame; a small box of the same person is replaced by the closer look.
    /// </summary>
    private List<DetectedObject> LookCloser(List<DetectedObject> found, List<Followed> followed, FFmpeg.DualFrame frame, LensModel model, Rot3? level)
    {
        static (float U, float V) Center(DetectedObject d) => (d.Box.CenterX / EquirectWidth % 1, d.Box.CenterY / EquirectHeight);
        var centers = new List<(float U, float V)>();
        foreach (var f in followed.OrderByDescending(f => f.Peak))
        {
            var seen = found.Where(d => d.Kind == DetectionKind.Body && !d.Weak).Select(d => (d, c: Center(d)))
                .Where(x => Apart(x.c.U, x.c.V, f.U, f.V) < 10).OrderByDescending(x => x.d.Box.H).FirstOrDefault();
            if (seen.d != null && seen.d.Box.H / EquirectHeight >= SmallBody) continue;
            if (centers.Any(c => Apart(c.U, c.V, f.U, f.V) < ZoomFov / 4)) continue;
            centers.Add((f.U, f.V));
            if (centers.Count == MaxZooms) break;
        }
        if (centers.Count > 0)
        {
            var big0 = RgbImage.Load(frame.Lens0, LensZoomSize);
            var big1 = RgbImage.Load(frame.Lens1, LensZoomSize);
            foreach (var (cu, cv) in centers)
            {
                var (yaw, pitch) = Fisheye360.FromEquirect(cu, cv);
                var view = Fisheye360.RenderView(big0, big1, yaw, pitch, ZoomFov, ZoomViewSize, ZoomViewSize, model, level);
                foreach (var d in _model.Analyze(view, includeWeak: true))
                {
                    // Cut by the edge of the narrow view: half a person, the wide views know better.
                    if (d.Box.X < 2 || d.Box.Y < 2 || d.Box.Right > ZoomViewSize - 2 || d.Box.Bottom > ZoomViewSize - 2) continue;
                    var (u, v, w, h) = Fisheye360.ViewBoxToEquirect(yaw, pitch, ZoomFov, ZoomViewSize, ZoomViewSize, d.Box.X, d.Box.Y, d.Box.W, d.Box.H);
                    var closer = new DetectedObject
                    {
                        Kind = d.Kind, Box = new Box(u * EquirectWidth, v * EquirectHeight, w * EquirectWidth, h * EquirectHeight),
                        Score = d.Score, Quality = d.Quality, Embedding = d.Embedding, Crop = d.Crop, Weak = d.Weak,
                    };
                    var same = found.FindIndex(k => k.Kind == d.Kind && Fisheye360.WrappedIou(k.Box, closer.Box, EquirectWidth) > 0.3f);
                    if (same < 0) found.Add(closer);
                    else if (found[same].Box.H / EquirectHeight < SmallBody * 1.5f && (!closer.Weak || found[same].Weak)) found[same] = closer;
                }
            }
        }
        // Who to look for in the next frame: everybody found now, and those missing for a few frames where they were.
        foreach (var f in followed) f.Missed++;
        foreach (var d in found.Where(d => d.Kind == DetectionKind.Body))
        {
            var (u, v) = Center(d);
            var size = d.Box.H / EquirectHeight;
            var known = followed.Where(f => Apart(f.U, f.V, u, v) < 10).OrderBy(f => Apart(f.U, f.V, u, v)).FirstOrDefault();
            if (known != null) (known.U, known.V, known.Missed, known.Peak) = (u, v, 0, Math.Max(known.Peak, size));
            // The dots in the distance that never came near are not followed.
            else if (!d.Weak && size >= 0.05f) followed.Add(new Followed { U = u, V = v, Peak = size });
        }
        followed.RemoveAll(f => f.Missed > 6);
        return found;
    }

    private static List<(NewDetection Det, RgbImage? Crop)> Frame(IReadOnlyList<DetectedObject> found, int width, int height, int timeMs, int[]? tracks)
    {
        var list = new List<(NewDetection, RgbImage?)>();
        var faces = found.Select((d, i) => (d, i)).Where(x => x.d.Kind == DetectionKind.Face).ToList();
        var bodies = found.Select((d, i) => (d, i)).Where(x => x.d.Kind == DetectionKind.Body).ToList();
        var linkOf = new Dictionary<int, int>();
        var usedBodies = new HashSet<int>();
        foreach (var (face, fi) in faces.OrderByDescending(f => f.d.Box.W))
        {
            var body = bodies.Where(b => !usedBodies.Contains(b.i) && b.d.Box.Contains(face.Box.CenterX, face.Box.CenterY) &&
                                         face.Box.CenterY <= b.d.Box.Y + b.d.Box.H * 0.4f && face.Box.W <= b.d.Box.W)
                .OrderBy(b => b.d.Box.W * b.d.Box.H).Select(b => (int?)b.i).FirstOrDefault();
            if (body is { } bi)
            {
                linkOf[fi] = bi;
                usedBodies.Add(bi);
            }
        }
        for (var i = 0; i < found.Count; i++)
        {
            var d = found[i];
            list.Add((new NewDetection(d.Kind, timeMs, d.Box.X / width, d.Box.Y / height,
                d.Box.W / width, d.Box.H / height, d.Score, d.Quality, d.Embedding,
                // A face rides on the track of its body: one track, one person.
                tracks?[linkOf.TryGetValue(i, out var lb) ? lb : i], linkOf.TryGetValue(i, out var b) ? b : null), d.Crop));
        }
        return list;
    }

    private void Save(long mediaId, List<(NewDetection Det, RgbImage? Crop)> items, IReadOnlyList<NewTrackBox>? paths = null, int oldTimeShiftMs = 0)
    {
        var ids = _repo.ReplaceDetections(mediaId, items.Select(x => x.Det).ToList(), paths, oldTimeShiftMs);
        for (var i = 0; i < ids.Count; i++)
            items[i].Crop?.SaveJpeg(_paths.PeopleCropPath(ids[i]), 82);
    }

    private async Task AnalyzeVideoAsync(MediaItem video, CancellationToken ct)
    {
        var duration = (video.DurationMs ?? 0) / 1000.0;
        if (duration <= 0)
        {
            Save(video.Id, []);
            return;
        }
        var interval = FrameIntervalMs(video.DurationMs ?? 0) / 1000.0;
        var dir = Path.Combine(_paths.CacheDir, "people-frames", video.Id.ToString(CultureInfo.InvariantCulture));
        try
        {
            var source = await Cloud.MediaFiles.LocalPathAsync(video, ct);
            var perFrame = new List<(int TimeMs, int Width, int Height, IReadOnlyList<DetectedObject> Found)>();
            if (Fisheye360.IsDualFisheye(source))
            {
                // Keyframes only (two 4K streams); six views per moment.
                var followed = new List<Followed>();
                foreach (var frame in await FFmpeg.ExtractDualFramesAsync(source, interval, 90, dir, LensZoomSize, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var lens0 = await Task.Run(() => RgbImage.Load(frame.Lens0, Lens360Size), ct);
                    var lens1 = await Task.Run(() => RgbImage.Load(frame.Lens1, Lens360Size), ct);
                    var model = Fisheye360.ModelOf(source);
                    var level = Fisheye360.LevelAt(source, frame.TimeMs / 1000.0);
                    var found = await Task.Run(() => Analyze360(lens0, lens1, includeWeak: true, model, level), ct);
                    found = await Task.Run(() => LookCloser(found, followed, frame, model, level), ct);
                    perFrame.Add((frame.TimeMs, EquirectWidth, EquirectHeight, found));
                }
            }
            else
            {
                var frames = await FFmpeg.ExtractFramesAsync(source, interval, 90, dir, FrameSize, ct, keyframesOnly: false);
                foreach (var frame in frames)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = int.Parse(Path.GetFileNameWithoutExtension(frame), CultureInfo.InvariantCulture) - 1;
                    var image = await Task.Run(() => RgbImage.Load(frame, FrameSize), ct);
                    var found = await Task.Run(() => _model.Analyze(image, includeWeak: true), ct);
                    perFrame.Add(((int)(n * interval * 1000), image.Width, image.Height, found));
                }
            }

            var tracks = VideoTracker.Track(perFrame.Select(f => f.Found).ToList());
            // Around a 360 camera directions stay put (the picture is leveled): a person lost for a few seconds and
            // found again near there in the same clothes is the same person.
            if (Fisheye360.IsDualFisheye(source)) VideoTracker.Stitch(perFrame.Select(f => f.Found).ToList(), tracks, EquirectWidth);
            // Weak detections that continue nobody are dropped (a shadow, a snow cannon at low confidence).
            for (var f = 0; f < perFrame.Count; f++)
            {
                var keep = Enumerable.Range(0, perFrame[f].Found.Count).Where(i => !perFrame[f].Found[i].Weak || tracks[f][i] != 0).ToList();
                if (keep.Count == perFrame[f].Found.Count) continue;
                perFrame[f] = (perFrame[f].TimeMs, perFrame[f].Width, perFrame[f].Height, keep.Select(i => perFrame[f].Found[i]).ToList());
                tracks[f] = keep.Select(i => tracks[f][i]).ToArray();
            }
            var all = new List<(NewDetection Det, RgbImage? Crop, int Frame)>();
            for (var f = 0; f < perFrame.Count; f++)
            {
                var offset = all.Count;
                foreach (var (det, crop) in Frame(perFrame[f].Found, perFrame[f].Width, perFrame[f].Height, perFrame[f].TimeMs, tracks[f]))
                    all.Add((det with { LinkedIndex = det.LinkedIndex + offset }, crop, f));
            }
            // The path of each track: its body in every frame, its face where no body was found.
            var paths = all.Where(x => x.Det.Track != null)
                .GroupBy(x => (x.Det.Track!.Value, x.Frame))
                .Select(g => g.OrderBy(x => x.Det.Kind == DetectionKind.Body ? 0 : 1).First().Det)
                .Select(d => new NewTrackBox(d.Track!.Value, d.TimeMs, d.X, d.Y, d.W, d.H)).ToList();
            // Before version 4 a frame stored at time t was really half an interval later.
            var shift = _repo.PeopleVersionOf(video.Id) is > 0 and < 4 ? FrameIntervalMs(video.DurationMs ?? 0) / 2 : 0;
            Save(video.Id, KeepRepresentatives(all), paths, shift);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>How far apart the analyzed frames of a video are: about one a second for short clips, at most 90 frames.</summary>
    public static int FrameIntervalMs(long durationMs) => (int)(Math.Clamp(durationMs / 1000.0 / 90, 1.0, 20.0) * 1000);

    /// <summary>
    /// A person seen in 90 frames does not need 90 stored detections: per track and kind the best few,
    /// spread over time, plus the first and the last one (when the person is on screen, for playing only
    /// their moments). Links to dropped detections are dropped; the track still connects them.
    /// </summary>
    private static List<(NewDetection Det, RgbImage? Crop)> KeepRepresentatives(List<(NewDetection Det, RgbImage? Crop, int Frame)> all)
    {
        var keep = new HashSet<int>();
        foreach (var group in all.Select((x, i) => (x, i)).GroupBy(t => (t.x.Det.Track, t.x.Det.Kind)))
        {
            var picked = new List<int>();
            foreach (var (x, i) in group.OrderByDescending(t => t.x.Det.Quality))
            {
                if (picked.Count >= MaxPerTrack) break;
                if (picked.Any(p => Math.Abs(all[p].Det.TimeMs - x.Det.TimeMs) < 2000) && group.Count() > MaxPerTrack) continue;
                picked.Add(i);
            }
            keep.UnionWith(picked);
            if (group.Key.Track != null)
            {
                keep.Add(group.MinBy(t => t.x.Det.TimeMs).i);
                keep.Add(group.MaxBy(t => t.x.Det.TimeMs).i);
            }
        }
        var order = keep.Order().ToList();
        var newIndex = order.Select((old, idx) => (old, idx)).ToDictionary(t => t.old, t => t.idx);
        return order.Select(i =>
        {
            var d = all[i].Det;
            int? link = d.LinkedIndex is { } l && newIndex.TryGetValue(l, out var nl) ? nl : null;
            return (d with { LinkedIndex = link }, all[i].Crop);
        }).ToList();
    }
}

/// <summary>
/// Follows people through sampled video frames. Frames are a second apart, so a skier moves far between
/// them: matching is by appearance first (same clothes), position only helps. Faces ride on the track of
/// their body; a face without a body gets its own track by face similarity.
/// </summary>
public static class VideoTracker
{
    private sealed class ActiveTrack
    {
        public int Id;
        public DetectionKind Kind;
        public float[] Embedding = [];
        public Box Box;
        public int LastFrame;
    }

    /// <summary>For every frame, the track number of each detection (same order as the input).</summary>
    public static List<int[]> Track(IReadOnlyList<IReadOnlyList<DetectedObject>> frames)
    {
        var tracks = new List<ActiveTrack>();
        var result = new List<int[]>();
        var next = 1;
        for (var f = 0; f < frames.Count; f++)
        {
            var found = frames[f];
            var ids = new int[found.Count];
            Array.Fill(ids, 0);

            foreach (var kind in new[] { DetectionKind.Body, DetectionKind.Face })
            {
                var candidates = new List<(float Score, int Det, ActiveTrack Track)>();
                for (var i = 0; i < found.Count; i++)
                {
                    if (found[i].Kind != kind) continue;
                    if (found[i].Weak) continue;
                    foreach (var t in tracks.Where(t => t.Kind == kind && f - t.LastFrame <= 3))
                    {
                        var sim = TensorPrimitives.Dot(found[i].Embedding, t.Embedding);
                        var iou = found[i].Box.Iou(t.Box);
                        var min = kind == DetectionKind.Body ? 0.55f : 0.4f;
                        // Close to where the person was: a changed pose (a fall) may look rather different.
                        if (sim < min && iou < 0.3f && !(sim >= min - 0.15f && Near(found[i].Box, t.Box))) continue;
                        // A person does not shrink to a third between two frames; a distant passer-by does not join.
                        var size = MathF.Sqrt(found[i].Box.W * found[i].Box.H / MathF.Max(1, t.Box.W * t.Box.H));
                        if ((size < 0.5f || size > 2f) && sim < 0.75f) continue;
                        candidates.Add((sim + 0.3f * iou, i, t));
                    }
                }
                var usedTracks = new HashSet<ActiveTrack>();
                foreach (var (_, det, track) in candidates.OrderByDescending(c => c.Score))
                {
                    if (ids[det] != 0 || usedTracks.Contains(track)) continue;
                    ids[det] = track.Id;
                    usedTracks.Add(track);
                    Update(track, found[det], f);
                }
                // Weak detections only continue a person not found otherwise in this frame, where they were.
                var weak = new List<(float Score, int Det, ActiveTrack Track)>();
                for (var i = 0; i < found.Count; i++)
                {
                    if (found[i].Kind != kind || !found[i].Weak) continue;
                    foreach (var t in tracks.Where(t => t.Kind == kind && f - t.LastFrame <= 3 && !usedTracks.Contains(t)))
                    {
                        var sim = TensorPrimitives.Dot(found[i].Embedding, t.Embedding);
                        var iou = found[i].Box.Iou(t.Box);
                        if (iou < 0.1f && !(sim >= 0.45f && Near(found[i].Box, t.Box))) continue;
                        weak.Add((sim + 0.5f * iou, i, t));
                    }
                }
                foreach (var (_, det, track) in weak.OrderByDescending(c => c.Score))
                {
                    if (ids[det] != 0 || usedTracks.Contains(track)) continue;
                    ids[det] = track.Id;
                    usedTracks.Add(track);
                    // The box follows; the appearance of a weak view does not change the person's.
                    track.Box = found[det].Box;
                    track.LastFrame = f;
                }
                for (var i = 0; i < found.Count; i++)
                {
                    if (found[i].Kind != kind || ids[i] != 0 || found[i].Weak) continue;
                    var t = new ActiveTrack { Id = next++, Kind = kind };
                    Update(t, found[i], f);
                    t.Embedding = (float[])found[i].Embedding.Clone();
                    tracks.Add(t);
                    ids[i] = t.Id;
                }
            }
            result.Add(ids);
        }
        return result;
    }

    /// <summary>
    /// Pieces of one person's way joined: a track that begins a few frames after another one ended, near where it
    /// ended (on a map mapWidth wide that wraps around), in the same clothes and about the same size.
    /// </summary>
    public static void Stitch(IReadOnlyList<IReadOnlyList<DetectedObject>> frames, List<int[]> tracks, float mapWidth)
    {
        const int maxGap = 8;
        var ends = new Dictionary<int, (int First, int Last, DetectedObject Start, DetectedObject End)>();
        for (var f = 0; f < frames.Count; f++)
            for (var i = 0; i < frames[f].Count; i++)
            {
                var (id, d) = (tracks[f][i], frames[f][i]);
                if (id == 0 || d.Kind != DetectionKind.Body) continue;
                // The appearance of a weak view is not trusted: the ends are the strong detections where there are any.
                if (!ends.TryGetValue(id, out var e)) ends[id] = (f, f, d, d);
                else ends[id] = (e.First, f, e.Start.Weak && !d.Weak ? d : e.Start, d.Weak && !e.End.Weak && f - e.Last <= 2 ? e.End : d);
            }
        var joined = new Dictionary<int, int>();
        foreach (var (id, part) in ends.OrderBy(e => e.Value.First))
        {
            var best = (Sim: 0f, Into: 0);
            foreach (var (other, before) in ends)
            {
                var gap = part.First - before.Last;
                if (other == id || joined.ContainsKey(other) || gap < 1 || gap > maxGap) continue;
                var dx = Math.Abs(part.Start.Box.CenterX - before.End.Box.CenterX) % mapWidth;
                if (dx > mapWidth / 2) dx = mapWidth - dx;
                var dy = part.Start.Box.CenterY - before.End.Box.CenterY;
                // Degrees on a 360 map: a person moves a little between the frames, not across the picture.
                var apart = MathF.Sqrt(dx * dx + dy * dy) * 360 / mapWidth;
                if (apart > 12 + 3 * gap) continue;
                var size = MathF.Sqrt(part.Start.Box.W * part.Start.Box.H / MathF.Max(1, before.End.Box.W * before.End.Box.H));
                if (size < 0.33f || size > 3f) continue;
                var sim = TensorPrimitives.Dot(part.Start.Embedding, before.End.Embedding);
                if (sim >= 0.6f && sim > best.Sim) best = (sim, other);
            }
            if (best.Into == 0) continue;
            joined[id] = best.Into;
            var into = ends[best.Into];
            ends[best.Into] = (into.First, part.Last, into.Start, part.End);
        }
        if (joined.Count == 0) return;
        foreach (var ids in tracks)
            for (var i = 0; i < ids.Length; i++)
                if (joined.TryGetValue(ids[i], out var into)) ids[i] = into;
    }

    /// <summary>The centers within the larger side of the earlier box: the same place, give or take a fall.</summary>
    private static bool Near(Box a, Box b)
    {
        var dx = a.CenterX - b.CenterX;
        var dy = a.CenterY - b.CenterY;
        var reach = MathF.Max(MathF.Max(b.W, b.H), MathF.Max(a.W, a.H));
        return dx * dx + dy * dy <= reach * reach;
    }

    private static void Update(ActiveTrack t, DetectedObject d, int frame)
    {
        t.Box = d.Box;
        t.LastFrame = frame;
        if (t.Embedding.Length == 0)
        {
            t.Embedding = (float[])d.Embedding.Clone();
            return;
        }
        // Running appearance, so a turn of the body does not break the track.
        TensorPrimitives.Multiply(t.Embedding, 0.7f, t.Embedding);
        TensorPrimitives.MultiplyAdd(d.Embedding, 0.3f, t.Embedding, t.Embedding);
        var norm = TensorPrimitives.Norm(t.Embedding);
        TensorPrimitives.Divide(t.Embedding, norm, t.Embedding);
    }
}
