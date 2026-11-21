using System.Numerics.Tensors;
using MomentFinder.Analysis;

namespace MomentFinder.People;

/// <summary>
/// Who is on which detection, recomputed from what the user confirmed, in layers:
/// 1. faces against the faces of named people (all years; confident new matches extend a person, so a
///    child is followed as it grows), 2. a body linked to a face in the same picture, 3. everything on one
///    video track, 4. bodies in the same event (one day of skiing: same jacket and helmet) against bodies
///    already known, 5. tracks again. What stays unknown becomes a suggestion for the review queue,
///    remaining faces are grouped into unnamed clusters. Confirmed detections are never changed.
/// </summary>
public sealed class IdentityService
{
    // ArcFace cosine similarity: other people stay below ~0.36 on the test set, the same person mostly above 0.4.
    public float FaceAuto { get; set; } = 0.45f;
    public float FaceSuggest { get; set; } = 0.32f;
    public float FaceExtend { get; set; } = 0.55f;
    public float FaceCluster { get; set; } = 0.5f;
    public float FaceMinQuality { get; set; } = 0.12f;
    // OSNet cosine similarity of whole people (clothes); only within an event, where clothes do not change.
    public float BodyAuto { get; set; } = 0.72f;
    public float BodySuggest { get; set; } = 0.6f;
    public float BodyMinQuality { get; set; } = 0.25f;
    /// <summary>Unnamed figures of one event this alike are offered as one person in Who is this.</summary>
    public float FigureCluster { get; set; } = 0.75f;
    public float Margin { get; set; } = 0.06f;
    private const int MaxPrototypes = 400;

    private readonly PeopleRepository _repo;
    private readonly object _gate = new();

    public IdentityService(PeopleRepository repo) => _repo = repo;

    public event Action? Changed;

    public sealed record Result(int Faces, int Bodies, int Assigned, int Suggestions, int Clusters);

    public Result Recompute()
    {
        lock (_gate)
        {
            var all = _repo.LoadAll();
            var rejections = _repo.Rejections();
            var before = all.ToDictionary(d => d.Id, Snapshot);

            foreach (var d in all.Where(d => !d.Confirmed))
            {
                d.PersonId = null;
                d.AssignSource = null;
                d.Confidence = null;
                d.SuggestPersonId = null;
                d.SuggestScore = null;
                d.Cluster = null;
            }

            var byId = all.ToDictionary(d => d.Id);
            var faces = all.Where(d => d.Kind == DetectionKind.Face).ToList();
            var bodies = all.Where(d => d.Kind == DetectionKind.Body).ToList();

            MatchFaces(faces, rejections);
            LinkBodies(faces, byId);
            SpreadTracks(all);
            MatchOutfits(bodies, rejections);
            SpreadTracks(all);
            ClusterFaces(faces);
            ClusterFigures(bodies);

            var changed = all.Where(d => !d.Confirmed && Snapshot(d) != before[d.Id]).ToList();
            _repo.SaveAssignments(changed);
            if (changed.Count > 0) Changed?.Invoke();
            return new Result(faces.Count, bodies.Count, all.Count(d => d.PersonId != null),
                all.Count(d => d.SuggestPersonId != null), all.Where(d => d.Cluster != null).Select(d => d.Cluster).Distinct().Count());
        }
    }

    private static (long?, AssignSource?, float?, long?, float?, long?) Snapshot(DetectionRow d) =>
        (d.PersonId, d.AssignSource, d.Confidence is { } c ? MathF.Round(c, 3) : null, d.SuggestPersonId,
            d.SuggestScore is { } s ? MathF.Round(s, 3) : null, d.Cluster);

    private sealed class Prototypes
    {
        public readonly Dictionary<long, List<float[]>> ByPerson = new();

        public void Add(long person, float[] e)
        {
            if (!ByPerson.TryGetValue(person, out var list)) ByPerson[person] = list = [];
            if (list.Count < MaxPrototypes) list.Add(e);
        }

        /// <summary>Best and second best person by the closest prototype; rejected persons are skipped.</summary>
        public (long Person, float Score, float Second) Best(float[] e, HashSet<long>? rejected)
        {
            long best = 0;
            float bestScore = -1, second = -1;
            foreach (var (person, list) in ByPerson)
            {
                if (rejected != null && rejected.Contains(person)) continue;
                var max = -1f;
                foreach (var p in list)
                {
                    var s = TensorPrimitives.Dot(e, p);
                    if (s > max) max = s;
                }
                if (max > bestScore)
                {
                    second = bestScore;
                    bestScore = max;
                    best = person;
                }
                else if (max > second)
                {
                    second = max;
                }
            }
            return (best, bestScore, second);
        }
    }

    private void MatchFaces(List<DetectionRow> faces, Dictionary<long, HashSet<long>> rejections)
    {
        var protos = new Prototypes();
        foreach (var f in faces.Where(f => f.Confirmed && f.PersonId != null).OrderByDescending(f => f.Quality))
            protos.Add(f.PersonId!.Value, f.Embedding);
        if (protos.ByPerson.Count == 0) return;

        var open = faces.Where(f => !f.Confirmed && f.Quality >= FaceMinQuality).ToList();
        // A few rounds: faces matched with high confidence join the prototypes, so years without a
        // confirmed face are bridged step by step (the 5 year old links to the 3 year old links to the baby).
        for (var round = 0; round < 3; round++)
        {
            var added = 0;
            var results = new (long Person, float Score, float Second)[open.Count];
            Parallel.For(0, open.Count, i =>
                results[i] = protos.Best(open[i].Embedding, rejections.GetValueOrDefault(open[i].Id)));
            for (var i = 0; i < open.Count; i++)
            {
                var f = open[i];
                var (person, score, second) = results[i];
                if (person == 0) continue;
                if (score >= FaceAuto && score - second >= Margin)
                {
                    if (f.PersonId == null && score >= FaceExtend && f.Quality >= 0.3f)
                    {
                        protos.Add(person, f.Embedding);
                        added++;
                    }
                    f.PersonId = person;
                    f.AssignSource = AssignSource.Face;
                    f.Confidence = score;
                    f.SuggestPersonId = null;
                    f.SuggestScore = null;
                }
                else if (f.PersonId == null && score >= FaceSuggest)
                {
                    f.SuggestPersonId = person;
                    f.SuggestScore = score;
                }
            }
            if (added == 0) break;
        }
    }

    /// <summary>A body whose face is known is that person (the face carries the link to its body).</summary>
    private static void LinkBodies(List<DetectionRow> faces, Dictionary<long, DetectionRow> byId)
    {
        foreach (var face in faces)
        {
            if (face.PersonId == null || face.LinkedId is not { } bodyId || !byId.TryGetValue(bodyId, out var b) || b.Confirmed)
                continue;
            b.PersonId = face.PersonId;
            b.AssignSource = AssignSource.Linked;
            b.Confidence = (face.Confidence ?? 1) * 0.95f;
            b.SuggestPersonId = null;
            b.SuggestScore = null;
        }
    }

    /// <summary>A video track is one person: the best supported identity on it goes to all its detections.</summary>
    private static void SpreadTracks(List<DetectionRow> all)
    {
        foreach (var track in all.Where(d => d.Track != null).GroupBy(d => d.Track!.Value))
        {
            var votes = track.Where(d => d.PersonId != null && d.AssignSource != AssignSource.Track)
                .GroupBy(d => d.PersonId!.Value)
                .Select(g => (Person: g.Key, Weight: g.Sum(d => d.Confirmed ? 3f : d.Confidence ?? 0.5f), Conf: g.Max(d => d.Confidence ?? 0.5f)))
                .OrderByDescending(v => v.Weight).ToList();
            if (votes.Count == 0) continue;
            var total = votes.Sum(v => v.Weight);
            var winner = votes[0];
            if (winner.Weight / total < 0.6f) continue;
            foreach (var d in track.Where(d => !d.Confirmed && d.PersonId == null))
            {
                d.PersonId = winner.Person;
                d.AssignSource = AssignSource.Track;
                d.Confidence = winner.Conf * 0.85f;
                d.SuggestPersonId = null;
                d.SuggestScore = null;
            }
        }
    }

    /// <summary>
    /// Within an event (a day out), people keep their clothes: an unknown body that looks like a known one
    /// of the same event is that person. Across events only a suggestion, clothes change.
    /// </summary>
    private void MatchOutfits(List<DetectionRow> bodies, Dictionary<long, HashSet<long>> rejections)
    {
        foreach (var ev in bodies.Where(b => b.EventId != null).GroupBy(b => b.EventId!.Value))
        {
            var protos = new Prototypes();
            foreach (var b in ev.Where(b => b.PersonId != null && b.AssignSource != AssignSource.Outfit &&
                                            (b.Confirmed || (b.Confidence ?? 0) >= 0.4f) && b.Quality >= BodyMinQuality)
                         .OrderByDescending(b => b.Quality))
                protos.Add(b.PersonId!.Value, b.Embedding);
            if (protos.ByPerson.Count == 0) continue;
            foreach (var b in ev.Where(b => !b.Confirmed && b.PersonId == null && b.Quality >= BodyMinQuality))
            {
                var (person, score, second) = protos.Best(b.Embedding, rejections.GetValueOrDefault(b.Id));
                if (person == 0) continue;
                if (score >= BodyAuto && score - second >= Margin)
                {
                    b.PersonId = person;
                    b.AssignSource = AssignSource.Outfit;
                    b.Confidence = score * 0.8f;
                }
                else if (score >= BodySuggest)
                {
                    b.SuggestPersonId = person;
                    b.SuggestScore = score;
                }
            }
        }
    }

    /// <summary>
    /// Faces nobody is known for, grouped (leader clustering on the better faces first). Strangers the user
    /// set aside belong to a hidden person, so they are matched above and do not come back here.
    /// </summary>
    private void ClusterFaces(List<DetectionRow> faces)
    {
        var open = faces.Where(f => f.PersonId == null && f.Quality >= 0.2f)
            .OrderByDescending(f => f.Quality).ThenBy(f => f.Id).ToList();
        var centroids = new List<(long Id, float[] Sum, float[] Unit)>();
        foreach (var f in open)
        {
            var best = -1;
            var bestScore = FaceCluster;
            for (var i = 0; i < centroids.Count; i++)
            {
                var s = TensorPrimitives.Dot(f.Embedding, centroids[i].Unit);
                if (s > bestScore) { bestScore = s; best = i; }
            }
            if (best < 0)
            {
                // Only reasonably good faces start a group; a weak one may join but not lead.
                if (f.Quality < 0.3f) continue;
                centroids.Add((f.Id, (float[])f.Embedding.Clone(), (float[])f.Embedding.Clone()));
                f.Cluster = f.Id;
                continue;
            }
            var c = centroids[best];
            TensorPrimitives.Add(c.Sum, f.Embedding, c.Sum);
            var norm = TensorPrimitives.Norm(c.Sum);
            TensorPrimitives.Divide(c.Sum, norm, c.Unit);
            f.Cluster = c.Id;
        }
    }

    /// <summary>
    /// Unnamed figures for Who is this: a video track is one unit (the tracker loses a skier who leaves the
    /// picture, so one person makes several tracks), units of one event with alike clothes are one group.
    /// Blurry or cut figures may join a group but not start one.
    /// </summary>
    private void ClusterFigures(List<DetectionRow> bodies)
    {
        var units = bodies.Where(b => b.PersonId == null)
            .GroupBy(b => b.Track ?? b.Id)
            .Select(g =>
            {
                var good = g.Where(b => b.Quality >= BodyMinQuality).ToList();
                var sum = new float[g.First().Embedding.Length];
                foreach (var b in good.Count > 0 ? good : g.ToList()) TensorPrimitives.Add(sum, b.Embedding, sum);
                var norm = TensorPrimitives.Norm(sum);
                if (norm > 0) TensorPrimitives.Divide(sum, norm, sum);
                return (Key: g.Key, Members: g.ToList(), Event: g.First().EventId, Quality: g.Max(b => b.Quality), Unit: sum);
            })
            .OrderByDescending(u => u.Quality).ThenBy(u => u.Key).ToList();
        foreach (var ev in units.GroupBy(u => u.Event ?? -u.Key))
        {
            var leaders = new List<(long Id, float[] Sum, float[] Unit)>();
            foreach (var u in ev)
            {
                var best = -1;
                var bestScore = FigureCluster;
                for (var i = 0; i < leaders.Count; i++)
                {
                    var s = TensorPrimitives.Dot(u.Unit, leaders[i].Unit);
                    if (s > bestScore) { bestScore = s; best = i; }
                }
                long cluster;
                if (best < 0)
                {
                    if (u.Quality < BodyMinQuality) continue;
                    leaders.Add((u.Key, (float[])u.Unit.Clone(), (float[])u.Unit.Clone()));
                    cluster = u.Key;
                }
                else
                {
                    var l = leaders[best];
                    TensorPrimitives.Add(l.Sum, u.Unit, l.Sum);
                    TensorPrimitives.Divide(l.Sum, TensorPrimitives.Norm(l.Sum), l.Unit);
                    cluster = l.Id;
                }
                foreach (var b in u.Members) b.Cluster = cluster;
            }
        }
    }
}
