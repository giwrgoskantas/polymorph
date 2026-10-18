using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>UV editing helpers: islands, transforms, packing, texel density (px per metre at a texture size).</summary>
public static class UvTools
{
    /// <summary>Connected UV islands: faces sharing a vertex with the same UV there.</summary>
    public static List<List<int>> Islands(PolyMesh m, IEnumerable<int> faces = null)
    {
        var set = (faces ?? Enumerable.Range(0, m.Faces.Count)).Where(f => f >= 0 && f < m.Faces.Count).ToHashSet();
        var parent = new Dictionary<int, int>(); foreach (var f in set) parent[f] = f;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        // key: (vertex, quantised uv) -> first face
        var seen = new Dictionary<(int, int, int), int>();
        foreach (var f in set)
        {
            var face = m.Faces[f];
            for (int c = 0; c < face.V.Length; c++)
            {
                var uv = face.UV[c];
                var k = (face.V[c], (int)MathF.Round(uv.X * 65536), (int)MathF.Round(uv.Y * 65536));
                if (seen.TryGetValue(k, out var g)) { int a = Find(f), b = Find(g); if (a != b) parent[a] = b; }
                else seen[k] = f;
            }
        }
        return set.GroupBy(Find).Select(g => g.ToList()).ToList();
    }

    public static (Vector2 Min, Vector2 Max) Bounds(PolyMesh m, IEnumerable<int> faces)
    {
        var mn = new Vector2(float.MaxValue); var mx = new Vector2(float.MinValue);
        foreach (var f in faces) foreach (var uv in m.Faces[f].UV) { mn = Vector2.Min(mn, uv); mx = Vector2.Max(mx, uv); }
        return (mn, mx);
    }

    /// <summary>Applies a 2D transform to the UVs of these faces.</summary>
    public static void Transform(PolyMesh m, IEnumerable<int> faces, Matrix3x2 t)
    {
        foreach (var f in faces.Distinct()) { var face = m.Faces[f]; for (int c = 0; c < face.UV.Length; c++) face.UV[c] = Vector2.Transform(face.UV[c], t); }
    }

    public static Matrix3x2 Around(Vector2 pivot, Matrix3x2 t) => Matrix3x2.CreateTranslation(-pivot) * t * Matrix3x2.CreateTranslation(pivot);

    public static float UvArea(PolyMesh m, int f)
    {
        var uv = m.Faces[f].UV; float a = 0;
        for (int i = 0; i < uv.Length; i++) { var p = uv[i]; var q = uv[(i + 1) % uv.Length]; a += p.X * q.Y - q.X * p.Y; }
        return MathF.Abs(a) * 0.5f;
    }

    /// <summary>Texture pixels per metre for these faces at a square texture of <paramref name="texSize"/>.</summary>
    public static float TexelDensity(PolyMesh m, IEnumerable<int> faces, int texSize = 1024, Matrix4x4? world = null)
    {
        double uvA = 0, wA = 0;
        foreach (var f in faces)
        {
            uvA += UvArea(m, f);
            var face = m.Faces[f];
            Vector3 s = Vector3.Zero;
            for (int i = 0; i < face.V.Length; i++)
            {
                var p = m.Verts[face.V[i]]; var q = m.Verts[face.V[(i + 1) % face.V.Length]];
                if (world.HasValue) { p = Vector3.Transform(p, world.Value); q = Vector3.Transform(q, world.Value); }
                s += Vector3.Cross(p, q);
            }
            wA += s.Length() * 0.5f;
        }
        return wA < 1e-12 ? 0 : (float)(Math.Sqrt(uvA / wA) * texSize);
    }

    /// <summary>Scales each island around its centre so it gets <paramref name="target"/> px/m.</summary>
    public static void SetDensity(PolyMesh m, IEnumerable<int> faces, float target, int texSize = 1024, Matrix4x4? world = null)
    {
        foreach (var isl in Islands(m, faces))
        {
            float d = TexelDensity(m, isl, texSize, world);
            if (d <= 1e-6f) continue;
            var (mn, mx) = Bounds(m, isl);
            Transform(m, isl, Around((mn + mx) * 0.5f, Matrix3x2.CreateScale(target / d)));
        }
    }

    /// <summary>Scales and moves the faces so their UVs fill 0..1 (keeps proportions).</summary>
    public static void Fit01(PolyMesh m, IEnumerable<int> faces, float margin = 0.01f)
    {
        var list = faces.ToList();
        var (mn, mx) = Bounds(m, list);
        var size = mx - mn; float s = (1 - 2 * margin) / MathF.Max(MathF.Max(size.X, size.Y), 1e-6f);
        Transform(m, list, Matrix3x2.CreateTranslation(-mn) * Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(margin, margin));
    }

    /// <summary>Packs the islands of these faces into 0..1 (shelf packing, biggest first, uniform scale).</summary>
    public static void Pack(PolyMesh m, IEnumerable<int> faces, float margin = 0.005f)
    {
        var islands = Islands(m, faces).Select(i => (Faces: i, B: Bounds(m, i))).ToList();
        if (islands.Count == 0) return;
        // rotate tall islands to lie flat
        for (int k = 0; k < islands.Count; k++)
        {
            var sz = islands[k].B.Max - islands[k].B.Min;
            if (sz.Y > sz.X * 1.2f) { Transform(m, islands[k].Faces, Around((islands[k].B.Min + islands[k].B.Max) * 0.5f, Matrix3x2.CreateRotation(MathF.PI / 2))); islands[k] = (islands[k].Faces, Bounds(m, islands[k].Faces)); }
        }
        islands.Sort((a, b) => (b.B.Max.Y - b.B.Min.Y).CompareTo(a.B.Max.Y - a.B.Min.Y));
        double total = islands.Sum(i => (double)(i.B.Max.X - i.B.Min.X + margin) * (i.B.Max.Y - i.B.Min.Y + margin));
        float minW = islands.Max(i => i.B.Max.X - i.B.Min.X) + margin;
        // shelf packing for several shelf widths: keep the one that fills the square best (biggest scale)
        List<(List<int> F, Vector2 Off)> place = null; float scale = 0;
        for (float k = 0.7f; k <= 2.01f; k += 0.05f)
        {
            float width = MathF.Max((float)Math.Sqrt(total) * k, minW);
            float x = 0, y = 0, rowH = 0, usedW = 0;
            var p = new List<(List<int> F, Vector2 Off)>();
            foreach (var (f, b) in islands)
            {
                var sz = b.Max - b.Min;
                if (x > 0 && x + sz.X > width) { y += rowH + margin; x = 0; rowH = 0; }
                p.Add((f, new Vector2(x, y) - b.Min));
                x += sz.X + margin; rowH = MathF.Max(rowH, sz.Y); usedW = MathF.Max(usedW, x);
            }
            float sc = 1f / MathF.Max(usedW, y + rowH);
            if (sc > scale) { scale = sc; place = p; }
        }
        foreach (var (f, off) in place) Transform(m, f, Matrix3x2.CreateTranslation(off) * Matrix3x2.CreateScale(scale));
    }
}
