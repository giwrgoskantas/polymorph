using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>Ear-clipping triangulation of (possibly concave) polygons, in the polygon's own plane.</summary>
public static class Triangulator
{
    /// <summary>Returns triangles as corner indices into the face (0..n-1), wound like the face.</summary>
    public static List<(int A, int B, int C)> Triangulate(PolyMesh m, Face f)
    {
        int n = f.V.Length;
        var tris = new List<(int, int, int)>(Math.Max(1, n - 2));
        if (n < 3) return tris;
        if (n == 3) { tris.Add((0, 1, 2)); return tris; }

        var nrm = m.FaceNormal(f);
        if (n == 4)
        {
            // quads: pick the diagonal that keeps both triangles facing the polygon's way (no ear clipping needed)
            var p0 = m.Verts[f.V[0]]; var p1 = m.Verts[f.V[1]]; var p2 = m.Verts[f.V[2]]; var p3 = m.Verts[f.V[3]];
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), nrm) > 0 && Vector3.Dot(Vector3.Cross(p2 - p0, p3 - p0), nrm) > 0) { tris.Add((0, 1, 2)); tris.Add((0, 2, 3)); return tris; }
            if (Vector3.Dot(Vector3.Cross(p1 - p0, p3 - p0), nrm) > 0 && Vector3.Dot(Vector3.Cross(p2 - p1, p3 - p1), nrm) > 0) { tris.Add((0, 1, 3)); tris.Add((1, 2, 3)); return tris; }
        }
        Basis(nrm, out var u, out var v);
        var pts = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var p = m.Verts[f.V[i]];
            pts[i] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
        }
        Triangulate2D(pts, tris);
        return tris;
    }

    /// <summary>Two axes in the plane with Cross(u, v) == n, so CCW around n stays CCW in 2D.</summary>
    public static void Basis(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var helper = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(helper, n));
        v = Vector3.Cross(n, u);
    }

    public static void Triangulate2D(Vector2[] pts, List<(int, int, int)> tris)
    {
        int n = pts.Length;
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(i);

        // If the outline came out clockwise (bad normal on a degenerate polygon), work reversed.
        if (SignedArea(pts) < 0) idx.Reverse();

        int guard = 0;
        while (idx.Count > 3 && guard++ < n * n + 16)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                if (!IsEar(pts, idx, ia, ib, ic)) continue;
                tris.Add((ia, ib, ic));
                idx.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break; // self-intersecting outline: fall back to a fan for the rest
        }
        if (idx.Count == 3) tris.Add((idx[0], idx[1], idx[2]));
        else for (int i = 1; i + 1 < idx.Count; i++) tris.Add((idx[0], idx[i], idx[i + 1]));
    }

    static float SignedArea(Vector2[] p)
    {
        float a = 0;
        for (int i = 0; i < p.Length; i++) { var q = p[i]; var r = p[(i + 1) % p.Length]; a += q.X * r.Y - r.X * q.Y; }
        return a * 0.5f;
    }

    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    static bool IsEar(Vector2[] p, List<int> idx, int ia, int ib, int ic)
    {
        var a = p[ia]; var b = p[ib]; var c = p[ic];
        if (Cross(a, b, c) <= 1e-12f) return false; // reflex or degenerate corner
        foreach (var j in idx)
        {
            if (j == ia || j == ib || j == ic) continue;
            var q = p[j];
            // a vertex sitting exactly on a corner of the ear does not block it
            if ((q - a).LengthSquared() < 1e-14f || (q - b).LengthSquared() < 1e-14f || (q - c).LengthSquared() < 1e-14f) continue;
            if (Cross(a, b, q) >= -1e-9f && Cross(b, c, q) >= -1e-9f && Cross(c, a, q) >= -1e-9f) return false;
        }
        return true;
    }
}
