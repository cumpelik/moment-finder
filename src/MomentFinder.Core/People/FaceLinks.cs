using MomentFinder.Analysis;

namespace MomentFinder.People;

/// <summary>
/// Which face belongs to which body in one picture. A face sits in the upper part of its body's box, near the
/// middle across; the closest fits are paired first, each face and each body once. Two people close together
/// (a mother holding her child) overlap, so the first box a face falls into is not always its own.
/// </summary>
public static class FaceLinks
{
    /// <summary>Faces lower than this part of the box are not its head (a sitting child's box shows only the upper body: about half).</summary>
    private const float HeadZone = 0.6f;

    /// <summary>For each face (its index) the body (its index); the boxes in any one unit (pixels, or 0..1 of the picture).</summary>
    public static Dictionary<int, int> Link(IReadOnlyList<(DetectionKind Kind, Box Box)> found)
    {
        var pairs = new List<(int Face, int Body, float Fit)>();
        for (var f = 0; f < found.Count; f++)
        {
            if (found[f].Kind != DetectionKind.Face) continue;
            var face = found[f].Box;
            for (var b = 0; b < found.Count; b++)
            {
                if (found[b].Kind != DetectionKind.Body) continue;
                var body = found[b].Box;
                if (face.CenterX < body.X || face.CenterX > body.Right || face.CenterY < body.Y || face.W > body.W) continue;
                var down = (face.CenterY - body.Y) / body.H;
                if (down > HeadZone) continue;
                var across = Math.Abs(face.CenterX - body.CenterX) / body.W;
                pairs.Add((f, b, down + across));
            }
        }
        var links = new Dictionary<int, int>();
        var used = new HashSet<int>();
        foreach (var (face, body, _) in pairs.OrderBy(p => p.Fit))
        {
            if (links.ContainsKey(face) || used.Contains(body)) continue;
            links[face] = body;
            used.Add(body);
        }
        return links;
    }
}
