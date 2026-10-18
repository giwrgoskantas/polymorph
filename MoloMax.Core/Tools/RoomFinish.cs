using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Tools;

/// <summary>
/// Finds the edges a room needs finishing on: floor / wall (skirting boards, floor dirt), ceiling / wall
/// (cornice, dirt under the ceiling) and wall / wall corners (edge wear). Door and window cuts are skipped:
/// an edge counts only when a real wall face sits on it.
/// </summary>
public static class RoomFinish
{
    public sealed class Edges
    {
        public HashSet<EdgeKey> FloorWall = new(), CeilingWall = new(), Corners = new();
    }

    public static Edges Classify(PolyMesh m, float cornerAngleDeg = 30f)
    {
        var res = new Edges();
        var faces = new Dictionary<EdgeKey, List<int>>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var v = m.Faces[f].V;
            for (int i = 0; i < v.Length; i++)
            {
                var e = EdgeKey.Make(v[i], v[(i + 1) % v.Length]);
                if (!faces.TryGetValue(e, out var l)) faces[e] = l = new List<int>(2);
                l.Add(f);
            }
        }
        float cosCorner = MathF.Cos(cornerAngleDeg * MathF.PI / 180f);
        foreach (var (e, l) in faces)
        {
            if (l.Count != 2) continue;
            var n1 = m.FaceNormal(m.Faces[l[0]]); var n2 = m.FaceNormal(m.Faces[l[1]]);
            bool wall1 = MathF.Abs(n1.Z) < 0.3f, wall2 = MathF.Abs(n2.Z) < 0.3f;
            // the edge itself must be horizontal for floor / ceiling joints
            var d = m.Verts[e.B] - m.Verts[e.A];
            bool horizontal = MathF.Abs(d.Z) < 1e-3f * MathF.Max(d.Length(), 1e-3f) + 1e-4f;
            if (horizontal && ((n1.Z > 0.7f && wall2) || (n2.Z > 0.7f && wall1))) res.FloorWall.Add(e);
            else if (horizontal && ((n1.Z < -0.7f && wall2) || (n2.Z < -0.7f && wall1))) res.CeilingWall.Add(e);
            else if (wall1 && wall2 && Vector3.Dot(n1, n2) < cosCorner && MathF.Abs(d.Z) > 0.5f * d.Length()) res.Corners.Add(e);
        }
        return res;
    }
}
