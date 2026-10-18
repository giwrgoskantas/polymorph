using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>Blender's UV projections (U menu): cylinder, sphere, from view, lightmap pack, reset. Faces null = the whole mesh.</summary>
public static class UvProject
{
    static IEnumerable<int> All(PolyMesh m, ISet<int> faces) => faces ?? (IEnumerable<int>)Enumerable.Range(0, m.Faces.Count);

    static (Vector3 Min, Vector3 Max) Box(PolyMesh m, IEnumerable<int> faces)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var fi in faces) foreach (var v in m.Faces[fi].V) { mn = Vector3.Min(mn, m.Verts[v]); mx = Vector3.Max(mx, m.Verts[v]); }
        return (mn, mx);
    }

    /// <summary>Cylinder around the vertical axis through the centre of the faces: U goes around (0..1), V up (0..1).
    /// A face crossing the seam keeps its U continuous.</summary>
    public static void Cylinder(PolyMesh m, ISet<int> faces = null)
    {
        var list = All(m, faces).ToList(); if (list.Count == 0) return;
        var (mn, mx) = Box(m, list); var c = (mn + mx) * 0.5f; float h = MathF.Max(mx.Z - mn.Z, 1e-4f);
        foreach (var fi in list)
        {
            var f = m.Faces[fi];
            float uMean = 0;
            var us = new float[f.V.Length];
            for (int i = 0; i < f.V.Length; i++) { var p = m.Verts[f.V[i]] - c; us[i] = (MathF.Atan2(p.Y, p.X) / MathF.Tau + 1f) % 1f; }
            Unseam(us);
            for (int i = 0; i < f.V.Length; i++) { uMean += us[i]; f.UV[i] = new Vector2(us[i], 1 - (m.Verts[f.V[i]].Z - mn.Z) / h); }
        }
    }

    /// <summary>Sphere around the centre of the faces: U around, V from the bottom pole to the top one.</summary>
    public static void Sphere(PolyMesh m, ISet<int> faces = null)
    {
        var list = All(m, faces).ToList(); if (list.Count == 0) return;
        var (mn, mx) = Box(m, list); var c = (mn + mx) * 0.5f;
        foreach (var fi in list)
        {
            var f = m.Faces[fi];
            var us = new float[f.V.Length];
            for (int i = 0; i < f.V.Length; i++) { var p = m.Verts[f.V[i]] - c; us[i] = (MathF.Atan2(p.Y, p.X) / MathF.Tau + 1f) % 1f; }
            Unseam(us);
            for (int i = 0; i < f.V.Length; i++)
            {
                var p = m.Verts[f.V[i]] - c; float r = MathF.Max(p.Length(), 1e-6f);
                f.UV[i] = new Vector2(us[i], 1 - (MathF.Asin(Math.Clamp(p.Z / r, -1, 1)) / MathF.PI + 0.5f));
            }
        }
    }

    /// <summary>A face whose corners straddle the U seam (0 / 1) gets its small values moved past 1 (no stretched face).</summary>
    static void Unseam(float[] us)
    {
        if (us.Max() - us.Min() <= 0.5f) return;
        for (int i = 0; i < us.Length; i++) if (us[i] < 0.5f) us[i] += 1f;
    }

    /// <summary>Planar projection from the camera (its right / up axes, points in mesh space). <paramref name="bounds"/>: the
    /// result is fitted to 0..1, else 1 UV unit = <paramref name="unit"/> metres.</summary>
    public static void FromView(PolyMesh m, ISet<int> faces, Vector3 right, Vector3 up, bool bounds, float unit = 1f)
    {
        var list = All(m, faces).ToList(); if (list.Count == 0) return;
        right = Vector3.Normalize(right); up = Vector3.Normalize(up);
        foreach (var fi in list) { var f = m.Faces[fi]; for (int i = 0; i < f.V.Length; i++) { var p = m.Verts[f.V[i]]; f.UV[i] = new Vector2(Vector3.Dot(p, right), -Vector3.Dot(p, up)) / MathF.Max(unit, 1e-4f); } }
        if (bounds) UvTools.Fit01(m, list, 0f);
    }

    /// <summary>Lightmap pack: every face its own flat island (projected on its own plane), all packed into 0..1.</summary>
    public static void LightmapPack(PolyMesh m, ISet<int> faces = null, float margin = 0.01f)
    {
        var list = All(m, faces).ToList(); if (list.Count == 0) return;
        foreach (var fi in list)
        {
            var f = m.Faces[fi];
            var n = m.FaceNormal(f);
            Triangulator.Basis(n, out var u, out var v);
            for (int i = 0; i < f.V.Length; i++) { var p = m.Verts[f.V[i]]; f.UV[i] = new Vector2(Vector3.Dot(p, u), -Vector3.Dot(p, v)); }
        }
        // islands are found from shared UVs: faces sharing vertices with different projections stay apart
        UvTools.Pack(m, list, margin);
    }

    /// <summary>Reset: each face fills the whole 0..1 square (quads exactly, other polygons around the square).</summary>
    public static void Reset(PolyMesh m, ISet<int> faces = null)
    {
        foreach (var fi in All(m, faces))
        {
            var f = m.Faces[fi];
            if (f.V.Length == 4) { f.UV[0] = new(0, 1); f.UV[1] = new(1, 1); f.UV[2] = new(1, 0); f.UV[3] = new(0, 0); continue; }
            for (int i = 0; i < f.V.Length; i++) { float a = MathF.Tau * i / f.V.Length + MathF.PI * 0.75f; f.UV[i] = new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f - 0.5f * MathF.Sin(a)); }
        }
    }
}
