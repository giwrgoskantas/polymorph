using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>
/// Preserve UVs (3ds Max): when vertices move, the polygons around them that are stretched (not moved as a whole)
/// get new UVs for the moving corners, read from where the corner lands on the polygon's original texture.
/// The texture stays put on the surface instead of sliding / stretching.
/// </summary>
public sealed partial class EditorApp
{
    sealed class UvMap
    {
        public int Face; public Vector3 C, T, B;
        public Vector3 U, V; // uv.x = U.x*qx + U.y*qy + U.z, uv.y = V.x*qx + V.y*qy + V.z
        public int SourceObj;
    }
    readonly List<UvMap> uvMaps = new();

    void CaptureUvMaps(SceneObject o)
    {
        uvMaps.Clear();
        if (!Settings.PreserveUVs || o?.Mesh == null || xfVerts.Count == 0) return;
        var m = o.Mesh;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            int moving = 0;
            foreach (var v in f.V) if (xfVerts.ContainsKey(v)) moving++;
            if (moving == 0 || moving == f.V.Length || f.V.Length < 3) continue; // untouched, or moved as a whole: UVs stay
            var map = FitUvMap(m, f);
            if (map == null) continue;
            map.Face = fi; map.SourceObj = o.Id;
            uvMaps.Add(map);
        }
    }

    void ApplyUvMaps(SceneObject o)
    {
        if (uvMaps.Count == 0) return;
        var m = o.Mesh;
        foreach (var map in uvMaps)
        {
            if (map.SourceObj != o.Id || map.Face >= m.Faces.Count) continue;
            var f = m.Faces[map.Face];
            for (int i = 0; i < f.V.Length; i++)
            {
                if (!xfVerts.ContainsKey(f.V[i])) continue;
                var d = m.Verts[f.V[i]] - map.C;
                float qx = Vector3.Dot(d, map.T), qy = Vector3.Dot(d, map.B);
                f.UV[i] = new Vector2(map.U.X * qx + map.U.Y * qy + map.U.Z, map.V.X * qx + map.V.Y * qy + map.V.Z);
            }
        }
    }

    /// <summary>Least-squares affine map from the polygon's plane (2D) to its UVs. Null when degenerate.</summary>
    static UvMap FitUvMap(PolyMesh m, Face f)
    {
        var n = m.FaceNormal(f);
        if (n.LengthSquared() < 1e-12f) return null;
        n = Vector3.Normalize(n);
        var c = Vector3.Zero; foreach (var v in f.V) c += m.Verts[v]; c /= f.V.Length;
        var t = m.Verts[f.V[1]] - m.Verts[f.V[0]];
        t -= Vector3.Dot(t, n) * n;
        if (t.LengthSquared() < 1e-12f) return null;
        t = Vector3.Normalize(t); var b = Vector3.Cross(n, t);
        // normal equations: [Sxx Sxy Sx; Sxy Syy Sy; Sx Sy N] * (a, b, c) = (Sxu, Syu, Su)
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, N = f.V.Length;
        double sxu = 0, syu = 0, su = 0, sxv = 0, syv = 0, sv = 0;
        for (int i = 0; i < f.V.Length; i++)
        {
            var d = m.Verts[f.V[i]] - c;
            double x = Vector3.Dot(d, t), y = Vector3.Dot(d, b);
            var uv = f.UV[i];
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
            sxu += x * uv.X; syu += y * uv.X; su += uv.X;
            sxv += x * uv.Y; syv += y * uv.Y; sv += uv.Y;
        }
        double det = sxx * (syy * N - sy * sy) - sxy * (sxy * N - sy * sx) + sx * (sxy * sy - syy * sx);
        if (Math.Abs(det) < 1e-14) return null;
        Vector3 Solve(double r0, double r1, double r2)
        {
            double a = (r0 * (syy * N - sy * sy) - sxy * (r1 * N - sy * r2) + sx * (r1 * sy - syy * r2)) / det;
            double bb = (sxx * (r1 * N - sy * r2) - r0 * (sxy * N - sy * sx) + sx * (sxy * r2 - r1 * sx)) / det;
            double cc = (sxx * (syy * r2 - r1 * sy) - sxy * (sxy * r2 - r1 * sx) + r0 * (sxy * sy - syy * sx)) / det;
            return new Vector3((float)a, (float)bb, (float)cc);
        }
        return new UvMap { C = c, T = t, B = b, U = Solve(sxu, syu, su), V = Solve(sxv, syv, sv) };
    }
}
