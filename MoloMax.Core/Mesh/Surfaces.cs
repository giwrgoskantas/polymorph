using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>What a dropped material covers: the flat surface under the mouse (one wall, the floor), every wall, the floor, the ceiling.</summary>
public enum SurfaceScope { Surface, Walls, Floor, Ceiling, Whole }

public static class Surfaces
{
    public static bool IsWall(Vector3 n) => MathF.Abs(n.Z) < 0.35f;
    public static bool IsFloor(Vector3 n) => n.Z > 0.7f;
    public static bool IsCeiling(Vector3 n) => n.Z < -0.7f;

    /// <summary>Faces the scope covers from the seed face. Surface: faces joined to the seed whose normal stays within
    /// <paramref name="angleDeg"/> of it (a wall stops at the corners, a floor takes every floor face it touches).</summary>
    public static HashSet<int> Region(PolyMesh m, int seed, SurfaceScope scope, float angleDeg = 25f)
    {
        var res = new HashSet<int>();
        if (seed < 0 || seed >= m.Faces.Count) return res;
        var normals = new Vector3[m.Faces.Count];
        for (int f = 0; f < m.Faces.Count; f++) normals[f] = m.FaceNormal(m.Faces[f]);
        switch (scope)
        {
            case SurfaceScope.Whole: for (int f = 0; f < m.Faces.Count; f++) res.Add(f); return res;
            case SurfaceScope.Walls: for (int f = 0; f < m.Faces.Count; f++) if (IsWall(normals[f])) res.Add(f); return res;
            case SurfaceScope.Floor: for (int f = 0; f < m.Faces.Count; f++) if (IsFloor(normals[f])) res.Add(f); return res;
            case SurfaceScope.Ceiling: for (int f = 0; f < m.Faces.Count; f++) if (IsCeiling(normals[f])) res.Add(f); return res;
        }
        // flood over shared edges
        var byEdge = new Dictionary<EdgeKey, List<int>>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var v = m.Faces[f].V;
            for (int i = 0; i < v.Length; i++)
            {
                var e = EdgeKey.Make(v[i], v[(i + 1) % v.Length]);
                if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = new List<int>(2);
                l.Add(f);
            }
        }
        float cos = MathF.Cos(angleDeg * MathF.PI / 180f);
        var n0 = normals[seed];
        var stack = new Stack<int>(); stack.Push(seed); res.Add(seed);
        while (stack.Count > 0)
        {
            int f = stack.Pop(); var v = m.Faces[f].V;
            for (int i = 0; i < v.Length; i++)
                foreach (var g in byEdge[EdgeKey.Make(v[i], v[(i + 1) % v.Length])])
                    if (!res.Contains(g) && Vector3.Dot(normals[g], n0) >= cos) { res.Add(g); stack.Push(g); }
        }
        return res;
    }
}
