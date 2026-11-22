using System.Numerics.Tensors;
using MomentFinder.Analysis;

namespace MomentFinder.People;

/// <summary>
/// Who is on which detection, recomputed from what the user confirmed, in layers:
/// (after the layers: nobody is twice on screen at once in a video, nor twice in one picture)
/// 1. the clear face on a figure the user named is that person; faces against the faces named on the same day
///    (the closest one decides), then against the faces of named people of all years (confident new matches
///    extend a person, so a child is followed as it grows),
///    2. a body linked to a face in the same picture, 3. everything on one
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
    /// <summary>
    /// A face this close to one the user named on the same day is that person. Lower than it sounds: other people
    /// stay below ~0.36, and there are only the few named faces of that day to be close to by chance.
    /// </summary>
    public float FaceSameDay { get; set; } = 0.5f;
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

    /// <summary>
    /// How far answers of the user reached, by person: the photos and videos the person was newly found on, and
    /// those where the name is settled now by a face named that day (it was a guess, or not there, before).
    /// </summary>
    public sealed record Reach(IReadOnlyDictionary<long, HashSet<long>> Found, IReadOnlyDictionary<long, HashSet<long>> Settled);

    /// <summary>
    /// After answers of the user (how far they reached since the first of them): raised on a background thread
    /// when the matching they asked for is done. Null: it could not be done now (the library was busy or away).
    /// </summary>
    public event Action<Reach?>? Answered;

    /// <summary>A few answers given in a row are matched together.</summary>
    public TimeSpan AnswerDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    private readonly object _soonGate = new();
    private bool _soonRunning;
    private bool _soonAgain;
    private readonly Dictionary<long, HashSet<long>> _gained = [];
    private readonly Dictionary<long, HashSet<long>> _settled = [];

    /// <summary>
    /// The user said who somebody is: everything is matched again right away in the background, not when the
    /// analysis of the library gets to it. Answers given while it runs are matched after it, together.
    /// </summary>
    public void RecomputeSoon()
    {
        lock (_soonGate)
        {
            if (_soonRunning)
            {
                _soonAgain = true;
                return;
            }
            _soonRunning = true;
            _soonAgain = false;
        }
        lock (_gained)
        {
            _gained.Clear();
            _settled.Clear();
        }
        _ = Task.Run(async () =>
        {
            var failed = false;
            try
            {
                while (true)
                {
                    await Task.Delay(AnswerDelay);
                    lock (_soonGate) _soonAgain = false;
                    try
                    {
                        Recompute();
                        failed = false;
                    }
                    catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
                    {
                        // The library is busy or away: said to who asked, the next matching does it.
                        failed = true;
                    }
                    lock (_soonGate)
                    {
                        if (_soonAgain) continue;
                        _soonRunning = false;
                        break;
                    }
                }
            }
            catch
            {
                // Whatever it was: the next answer starts a matching again.
                lock (_soonGate) _soonRunning = false;
                throw;
            }
            Reach reach;
            lock (_gained)
                reach = new Reach(_gained.ToDictionary(g => g.Key, g => g.Value.ToHashSet()), _settled.ToDictionary(g => g.Key, g => g.Value.ToHashSet()));
            Answered?.Invoke(failed ? null : reach);
        });
    }

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

            var relinked = Relink(all);
            if (relinked.Count > 0) _repo.SaveLinks(relinked);
            MatchFaces(faces, FacesOfNamedFigures(faces, byId, rejections), rejections);
            LinkBodies(faces, byId);
            SpreadTracks(all);
            MatchOutfits(bodies, faces, rejections);
            SpreadTracks(all);
            OncePerMoment(all);
            OncePerPicture(all);
            ClusterFaces(faces);
            ClusterFigures(bodies);

            var changed = all.Where(d => !d.Confirmed && Snapshot(d) != before[d.Id]).ToList();
            _repo.SaveAssignments(changed);
            lock (_gained)
                foreach (var d in changed.Where(d => d.PersonId != null))
                {
                    var (person, was) = (d.PersonId!.Value, before[d.Id]);
                    void Add(Dictionary<long, HashSet<long>> to)
                    {
                        if (!to.TryGetValue(person, out var media)) to[person] = media = [];
                        media.Add(d.MediaId);
                    }
                    if (was.Item1 != person) Add(_gained);
                    if (d.AssignSource == AssignSource.SameDay && (was.Item1 != person || was.Item2 != AssignSource.SameDay)) Add(_settled);
                }
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

        /// <summary>
        /// Best and second best person; rejected persons are skipped. A person counts by the average of their
        /// three closest pictures, not the single closest: among hundreds of pictures of one person one is
        /// close to anybody by chance, so the person with the most pictures would win everybody.
        /// </summary>
        public (long Person, float Score, float Second) Best(float[] e, HashSet<long>? rejected)
        {
            long best = 0;
            float bestScore = -1, second = -1;
            foreach (var (person, list) in ByPerson)
            {
                if (rejected != null && rejected.Contains(person)) continue;
                var max = Closeness(e, list);
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

        /// <summary>How close a face is to a person: the average of the three closest of the person's pictures.</summary>
        public static float Closeness(float[] e, List<float[]> list)
        {
            Span<float> top = stackalloc float[3];
            top.Fill(float.MinValue);
            foreach (var p in list)
            {
                var s = TensorPrimitives.Dot(e, p);
                if (s <= top[2]) continue;
                if (s > top[0]) (top[0], top[1], top[2]) = (s, top[0], top[1]);
                else if (s > top[1]) (top[1], top[2]) = (s, top[1]);
                else top[2] = s;
            }
            var n = Math.Min(3, list.Count);
            var sum = 0f;
            for (var k = 0; k < n; k++) sum += top[k];
            return sum / n;
        }
    }

    /// <summary>Somebody the detections asked about may be. Likely: close enough to be offered first, with one click.</summary>
    public sealed record Candidate(Person Person, float Score, bool Likely);

    /// <summary>
    /// Who somebody may be (one person: a face, a figure, a group or a video track of them), for choosing the name:
    /// all known people, the most likely first. By the face against the faces the people are known by (as the
    /// matching does it: the closest named face of the same day, or the three closest of all); by the clothes
    /// against the figures known on the same day; who was there that day comes before who was not, who is on more
    /// photos before who is on few. People the user said it is not come last.
    /// </summary>
    public List<Candidate> Candidates(IReadOnlyCollection<long> detectionIds)
    {
        var persons = _repo.ListPersons();
        if (persons.Count == 0) return [];
        var asked = _repo.Load(detectionIds);
        var faces = asked.Where(d => d.Kind == DetectionKind.Face && d.Quality >= FaceMinQuality).OrderByDescending(d => d.Quality).Take(8).ToList();
        var bodies = asked.Where(d => d.Kind == DetectionKind.Body && d.EventId != null).OrderByDescending(d => d.Quality).Take(8).ToList();
        var rejections = _repo.Rejections();
        var not = asked.SelectMany(d => rejections.GetValueOrDefault(d.Id) ?? []).ToHashSet();

        var byFace = new Dictionary<long, float>();
        if (faces.Count > 0)
        {
            var askedIds = asked.Select(d => d.Id).ToHashSet();
            var known = _repo.KnownFaces(FaceExtend, MaxPrototypes).Where(f => !askedIds.Contains(f.Id)).ToList();
            bool Said(DetectionRow f) => f.Confirmed || f.AssignSource == AssignSource.Linked;
            var protos = new Prototypes();
            foreach (var f in known.OrderByDescending(Said).ThenByDescending(f => f.Quality)) protos.Add(f.PersonId!.Value, f.Embedding);
            foreach (var (person, list) in protos.ByPerson)
                byFace[person] = faces.Where(f => f.Embedding.Length == list[0].Length).Select(f => Prototypes.Closeness(f.Embedding, list)).DefaultIfEmpty(-1).Max();
            // A face the user named on the same day counts by itself (MatchSameDay).
            foreach (var named in known.Where(k => Said(k) && k.EventId != null))
            {
                var s = faces.Where(f => f.EventId == named.EventId && f.Embedding.Length == named.Embedding.Length)
                    .Select(f => TensorPrimitives.Dot(f.Embedding, named.Embedding)).DefaultIfEmpty(-1).Max();
                if (s > byFace.GetValueOrDefault(named.PersonId!.Value, -1)) byFace[named.PersonId.Value] = s;
            }
        }
        var there = _repo.KnownFiguresOf(asked.Where(d => d.EventId != null).Select(d => d.EventId!.Value).Distinct().ToList(),
            asked.Select(d => d.Id).ToList());
        var byClothes = new Dictionary<long, float>();
        foreach (var known in there.Where(k => k.Quality >= BodyMinQuality && (k.Confirmed || (k.Confidence ?? 0) >= 0.4f) && k.AssignSource != AssignSource.Outfit))
        {
            var s = bodies.Where(b => b.EventId == known.EventId && b.Embedding.Length == known.Embedding.Length)
                .Select(b => TensorPrimitives.Dot(b.Embedding, known.Embedding)).DefaultIfEmpty(-1).Max();
            if (s > byClothes.GetValueOrDefault(known.PersonId!.Value, -1)) byClothes[known.PersonId.Value] = s;
        }
        var wasThere = there.Select(k => k.PersonId!.Value).ToHashSet();

        // Clothes on the scale of faces: as sure as a face (BodyAuto) counts like a face that is (FaceAuto).
        var clothes = BodyAuto - FaceAuto;
        return persons.Select(p =>
            {
                var score = Math.Max(byFace.GetValueOrDefault(p.Id, -1), byClothes.GetValueOrDefault(p.Id, -1) - clothes);
                if (not.Contains(p.Id)) score = -2;
                return (Person: p, Score: score, There: wasThere.Contains(p.Id));
            })
            .OrderByDescending(c => c.Score + (c.There && c.Score > -2 ? 0.05f : 0)).ThenByDescending(c => c.Person.MediaCount).ThenBy(c => c.Person.Name)
            .Select(c => new Candidate(c.Person, c.Score, c.Score >= FaceSuggest))
            .ToList();
    }

    /// <summary>
    /// The clear face on a figure the user named is that person. In a photo the box that is clicked and named is the
    /// figure: without this the person would have no face to be found by in the other photos, however often named.
    /// </summary>
    private static HashSet<long> FacesOfNamedFigures(List<DetectionRow> faces, Dictionary<long, DetectionRow> byId,
        Dictionary<long, HashSet<long>> rejections)
    {
        var named = new HashSet<long>();
        foreach (var f in faces)
        {
            if (f.Confirmed || f.Quality < 0.3f || f.LinkedId is not { } bodyId || !byId.TryGetValue(bodyId, out var b)) continue;
            if (!b.Confirmed || b.PersonId is not { } person || rejections.GetValueOrDefault(f.Id)?.Contains(person) == true) continue;
            f.PersonId = person;
            f.AssignSource = AssignSource.Linked;
            f.Confidence = 1;
            named.Add(f.Id);
        }
        return named;
    }

    /// <param name="ofFigures">Faces named through their figure (FacesOfNamedFigures): they count like the confirmed ones.</param>
    private void MatchFaces(List<DetectionRow> faces, HashSet<long> ofFigures, Dictionary<long, HashSet<long>> rejections)
    {
        var protos = new Prototypes();
        var named = faces.Where(f => f.PersonId != null && (f.Confirmed || ofFigures.Contains(f.Id))).ToList();
        foreach (var f in named.OrderByDescending(f => f.Quality))
            protos.Add(f.PersonId!.Value, f.Embedding);
        if (protos.ByPerson.Count == 0) return;
        MatchSameDay(faces, named.Select(f => f.Id).ToHashSet(), protos, rejections);

        var open = faces.Where(f => !f.Confirmed && f.PersonId == null && f.Quality >= FaceMinQuality).ToList();
        // The three closest pictures of every person for every open face (see Prototypes.Best) are kept between the
        // rounds: a round compares only with the pictures the round before added, not with everything again.
        var persons = protos.ByPerson.Keys.ToArray();
        var lists = persons.Select(p => protos.ByPerson[p]).ToArray();
        var compared = new int[persons.Length];
        var closest = new float[open.Count][];
        // A few rounds: faces matched with high confidence join the prototypes, so years without a
        // confirmed face are bridged step by step (the 5 year old links to the 3 year old links to the baby).
        for (var round = 0; round < 3; round++)
        {
            var added = 0;
            var results = new (long Person, float Score, float Second)[open.Count];
            var counts = lists.Select(l => l.Count).ToArray();
            Parallel.For(0, open.Count, i =>
            {
                var e = open[i].Embedding;
                var top = closest[i];
                if (top == null)
                {
                    closest[i] = top = new float[persons.Length * 3];
                    Array.Fill(top, float.MinValue);
                }
                var rejected = rejections.GetValueOrDefault(open[i].Id);
                long best = 0;
                float bestScore = -1, second = -1;
                for (var p = 0; p < persons.Length; p++)
                {
                    var (a, b, c) = (p * 3, p * 3 + 1, p * 3 + 2);
                    for (var k = compared[p]; k < counts[p]; k++)
                    {
                        var s = TensorPrimitives.Dot(e, lists[p][k]);
                        if (s <= top[c]) continue;
                        if (s > top[a]) (top[a], top[b], top[c]) = (s, top[a], top[b]);
                        else if (s > top[b]) (top[b], top[c]) = (s, top[b]);
                        else top[c] = s;
                    }
                    if (rejected != null && rejected.Contains(persons[p])) continue;
                    var n = Math.Min(3, counts[p]);
                    var score = 0f;
                    for (var k = 0; k < n; k++) score += top[a + k];
                    score /= n;
                    if (score > bestScore) (best, bestScore, second) = (persons[p], score, bestScore);
                    else if (score > second) second = score;
                }
                results[i] = (best, bestScore, second);
            });
            compared = counts;
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

    /// <summary>
    /// The same day (photos without a date: the same folder): a face the user named there is the best picture of
    /// that person for the photos around it, the same age, hair and light. A face close to it is that person, though
    /// the person's pictures of other years are less alike (in MatchFaces their average decides, and a brother or
    /// sister with more pictures comes as close). So naming one photo of a series names the series. Not when
    /// somebody else, named elsewhere, is closer still.
    /// </summary>
    private void MatchSameDay(List<DetectionRow> faces, HashSet<long> namedFaces, Prototypes protos, Dictionary<long, HashSet<long>> rejections)
    {
        var days = faces.GroupBy(f => f.EventId ?? -1 - f.Folder)
            .Select(day => (Named: day.Where(f => namedFaces.Contains(f.Id)).Select(f => (Person: f.PersonId!.Value, f.Embedding)).ToList(),
                Open: day.Where(f => f.PersonId == null && f.Quality >= FaceMinQuality).ToList()))
            .Where(day => day.Named.Count > 0 && day.Open.Count > 0).ToList();
        // A few rounds: a clear face found this way stands for the person too (turning the head step by step).
        for (var round = 0; round < 3; round++)
        {
            var found = new List<(int Day, DetectionRow Face, long Person, float Score)>();
            var scores = new Dictionary<long, float>();
            for (var d = 0; d < days.Count; d++)
                foreach (var f in days[d].Open)
                {
                    var rejected = rejections.GetValueOrDefault(f.Id);
                    scores.Clear();
                    foreach (var (person, e) in days[d].Named)
                    {
                        if (rejected != null && rejected.Contains(person) || e.Length != f.Embedding.Length) continue;
                        var s = TensorPrimitives.Dot(f.Embedding, e);
                        if (!scores.TryGetValue(person, out var had) || s > had) scores[person] = s;
                    }
                    long best = 0;
                    float score = -1, second = -1;
                    foreach (var (person, s) in scores)
                    {
                        if (s > score) (best, score, second) = (person, s, score);
                        else if (s > second) second = s;
                    }
                    if (best != 0 && score >= FaceSameDay && score - second >= Margin) found.Add((d, f, best, score));
                }
            // Somebody else, named elsewhere, is closer still: left to the pictures of all the years.
            var elsewhere = new bool[found.Count];
            Parallel.For(0, found.Count, i =>
            {
                var (_, f, person, score) = found[i];
                HashSet<long> others = rejections.TryGetValue(f.Id, out var rejected) ? [.. rejected, person] : [person];
                elsewhere[i] = protos.Best(f.Embedding, others).Score > score;
            });
            var extended = false;
            for (var i = 0; i < found.Count; i++)
            {
                if (elsewhere[i]) continue;
                var (d, f, person, score) = found[i];
                f.PersonId = person;
                f.AssignSource = AssignSource.SameDay;
                f.Confidence = score;
                days[d].Open.Remove(f);
                if (score < FaceExtend || f.Quality < 0.3f) continue;
                days[d].Named.Add((person, f.Embedding));
                protos.Add(person, f.Embedding);
                extended = true;
            }
            if (!extended) break;
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
    private void MatchOutfits(List<DetectionRow> bodies, List<DetectionRow> faces, Dictionary<long, HashSet<long>> rejections)
    {
        // A body with a clear face of its own: the face says who it is (or that it is nobody known), not the clothes.
        // A child in the same striped shirt as her mother is not her mother.
        var faceShown = faces.Where(f => f.LinkedId != null && f.Quality >= 0.3f).Select(f => f.LinkedId!.Value).ToHashSet();
        // Who is in each picture already (by the face, by the user's word): the clothes name no second one there.
        var there = bodies.Concat(faces).Where(d => d.PersonId != null).Select(d => (d.MediaId, d.TimeMs, d.PersonId!.Value)).ToHashSet();
        foreach (var ev in bodies.Where(b => b.EventId != null).GroupBy(b => b.EventId!.Value))
        {
            var protos = new Prototypes();
            foreach (var b in ev.Where(b => b.PersonId != null && b.AssignSource != AssignSource.Outfit &&
                                            (b.Confirmed || (b.Confidence ?? 0) >= 0.4f) && b.Quality >= BodyMinQuality)
                         .OrderByDescending(b => b.Quality))
                protos.Add(b.PersonId!.Value, b.Embedding);
            if (protos.ByPerson.Count == 0) continue;
            var alike = new List<(DetectionRow Body, long Person, float Score, bool Sure)>();
            foreach (var b in ev.Where(b => !b.Confirmed && b.PersonId == null && b.Quality >= BodyMinQuality && !faceShown.Contains(b.Id)))
            {
                var (person, score, second) = protos.Best(b.Embedding, rejections.GetValueOrDefault(b.Id));
                if (person == 0 || score < BodySuggest || there.Contains((b.MediaId, b.TimeMs, person))) continue;
                alike.Add((b, person, score, score >= BodyAuto && score - second >= Margin));
            }
            // Clothes tell people apart only where they differ: several figures of one picture in what the person
            // wears (a class in white shirts, a team in its jerseys) are not named by it, none of them.
            foreach (var same in alike.GroupBy(a => (a.Body.MediaId, a.Body.TimeMs, a.Person)))
            {
                if (same.Count() > 1) continue;
                var (b, person, score, sure) = same.First();
                if (sure)
                {
                    b.PersonId = person;
                    b.AssignSource = AssignSource.Outfit;
                    b.Confidence = score * 0.8f;
                }
                else
                {
                    b.SuggestPersonId = person;
                    b.SuggestScore = score;
                }
            }
        }
    }

    /// <summary>
    /// Faces of photos paired with bodies again (the pairing got better; photos analyzed before keep their old,
    /// sometimes wrong pairs otherwise). Video frames keep theirs: a face rides on the track of its body.
    /// </summary>
    private static List<DetectionRow> Relink(List<DetectionRow> all)
    {
        var changed = new List<DetectionRow>();
        foreach (var frame in all.GroupBy(d => (d.MediaId, d.TimeMs)))
        {
            var dets = frame.ToList();
            if (dets.Any(d => d.Track != null) || !dets.Any(d => d.Kind == DetectionKind.Face)) continue;
            var links = FaceLinks.Link(dets.Select(d => (d.Kind, new Box(d.X, d.Y, d.W, d.H))).ToList());
            for (var i = 0; i < dets.Count; i++)
            {
                if (dets[i].Kind != DetectionKind.Face) continue;
                long? body = links.TryGetValue(i, out var b) ? dets[b].Id : null;
                if (dets[i].LinkedId == body) continue;
                dets[i].LinkedId = body;
                changed.Add(dets[i]);
            }
        }
        return changed;
    }

    /// <summary>How strong a reason for a name is: the user's word, a face named that day, the face, the body of a known face, a video track, the clothes.</summary>
    private static int Rank(AssignSource? s) => s switch
    {
        AssignSource.User => 6, AssignSource.SameDay => 5, AssignSource.Face => 4, AssignSource.Linked => 3, AssignSource.Track => 2,
        AssignSource.Outfit => 1, _ => 0,
    };

    /// <summary>
    /// Nobody is twice on screen at once: tracks of one video that share an analyzed frame are different people.
    /// When several of them carry the same name (each got it in another frame, and a track is named as a whole), the
    /// one with the strongest reason keeps it; the others lose it in all their frames and are asked about.
    /// Tracks one after another (a skier lost and found again) share a name as before; what the user said stays.
    /// </summary>
    private static void OncePerMoment(List<DetectionRow> all)
    {
        foreach (var video in all.Where(d => d.Track != null).GroupBy(d => d.MediaId))
        {
            var tracks = video.GroupBy(d => d.Track!.Value).Select(g => (Detections: g.ToList(), Times: g.Select(d => d.TimeMs).ToHashSet())).ToList();
            if (tracks.Count < 2) continue;
            foreach (var person in video.Where(d => d.PersonId != null).Select(d => d.PersonId!.Value).Distinct().ToList())
            {
                var named = tracks.Select(t => (Track: t, Of: t.Detections.Where(d => d.PersonId == person).ToList())).Where(t => t.Of.Count > 0)
                    .OrderByDescending(t => t.Of.Any(d => d.Confirmed))
                    .ThenByDescending(t => t.Of.Max(d => Rank(d.AssignSource)))
                    .ThenByDescending(t => t.Of.Where(d => d.AssignSource != AssignSource.Track).Sum(d => d.Confidence ?? 0))
                    .ThenBy(t => t.Track.Detections.Min(d => d.Id)).ToList();
                if (named.Count < 2) continue;
                var kept = new List<HashSet<int>>();
                foreach (var (track, of) in named)
                {
                    if (of.Any(d => d.Confirmed) || !kept.Any(times => times.Overlaps(track.Times)))
                    {
                        kept.Add(track.Times);
                        continue;
                    }
                    foreach (var d in of.Where(d => !d.Confirmed))
                    {
                        d.PersonId = null;
                        d.AssignSource = null;
                        d.Confidence = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Nobody is twice in one picture: when two people in it are both taken for the same person, the one with the
    /// stronger reason keeps the name (confirmed, then the face, then the body of a known face, a video track, the
    /// clothes; then the higher confidence). The same for suggestions.
    /// </summary>
    private static void OncePerPicture(List<DetectionRow> all)
    {
        foreach (var frame in all.GroupBy(d => (d.MediaId, d.TimeMs)))
        {
            var dets = frame.ToList();
            if (dets.Count < 2) continue;
            // One person in the picture: a body with its face, or a face or a body alone.
            var faceOf = dets.Where(d => d.Kind == DetectionKind.Face && d.LinkedId != null).GroupBy(d => d.LinkedId!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            var bodyIds = dets.Where(d => d.Kind == DetectionKind.Body).Select(d => d.Id).ToHashSet();
            var people = dets.Where(d => d.Kind == DetectionKind.Body || d.LinkedId is not { } l || !bodyIds.Contains(l))
                .Select(d => d.Kind == DetectionKind.Body && faceOf.TryGetValue(d.Id, out var f) ? new List<DetectionRow> { d, f } : [d])
                .ToList();
            if (people.Count < 2) continue;

            foreach (var same in people.Where(p => p.Any(d => d.PersonId != null))
                         .GroupBy(p => p.First(d => d.PersonId != null).PersonId!.Value).Where(g => g.Count() > 1))
            {
                var ranked = same.OrderByDescending(p => p.Any(d => d.Confirmed && d.PersonId == same.Key))
                    .ThenByDescending(p => p.Where(d => d.PersonId == same.Key).Max(d => Rank(d.AssignSource)))
                    .ThenByDescending(p => p.Where(d => d.PersonId == same.Key).Max(d => d.Confidence ?? 0)).ToList();
                foreach (var other in ranked.Skip(1))
                    foreach (var d in other.Where(d => !d.Confirmed && d.PersonId == same.Key))
                    {
                        d.PersonId = null;
                        d.AssignSource = null;
                        d.Confidence = null;
                    }
            }

            var named = people.SelectMany(p => p).Where(d => d.PersonId != null).Select(d => d.PersonId!.Value).ToHashSet();
            foreach (var p in people.Where(p => p.All(d => d.PersonId == null)))
                foreach (var d in p.Where(d => d.SuggestPersonId is { } s && named.Contains(s)))
                {
                    d.SuggestPersonId = null;
                    d.SuggestScore = null;
                }
            foreach (var same in people.Where(p => p.All(d => d.PersonId == null) && p.Any(d => d.SuggestPersonId != null))
                         .GroupBy(p => p.First(d => d.SuggestPersonId != null).SuggestPersonId!.Value).Where(g => g.Count() > 1))
                foreach (var other in same.OrderByDescending(p => p.Max(d => d.SuggestScore ?? 0)).Skip(1))
                    foreach (var d in other.Where(d => d.SuggestPersonId == same.Key))
                    {
                        d.SuggestPersonId = null;
                        d.SuggestScore = null;
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
            var (best, _) = Closest(f.Embedding, centroids, FaceCluster);
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
    /// The group a face is closest to, above the limit (the first one of equally close ones); -1 when none is.
    /// Thousands of groups (strangers in the background of a big library) are looked through on all cores.
    /// </summary>
    private static (int Index, float Score) Closest(float[] e, List<(long Id, float[] Sum, float[] Unit)> centroids, float above)
    {
        (int Index, float Score) Scan(int from, int to)
        {
            var (index, score) = (-1, above);
            for (var i = from; i < to; i++)
            {
                var s = TensorPrimitives.Dot(e, centroids[i].Unit);
                if (s > score) (index, score) = (i, s);
            }
            return (index, score);
        }
        const int Part = 256;
        if (centroids.Count < 4 * Part) return Scan(0, centroids.Count);
        var parts = new (int Index, float Score)[(centroids.Count + Part - 1) / Part];
        Parallel.For(0, parts.Length, p => parts[p] = Scan(p * Part, Math.Min(centroids.Count, (p + 1) * Part)));
        var best = (Index: -1, Score: above);
        foreach (var part in parts)
            if (part.Score > best.Score) best = part;
        return best;
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
