using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>
/// Smart UV Project (like Blender's): faces are grouped into charts whose normals stay within an angle of the
/// chart's normal, each chart is projected flat at the same texel density, then all charts are packed into
/// 0..1 without overlap — what painting and baking need.
/// </summary>
public static class UvUnwrap
{
    public static int SmartProject(PolyMesh m, float angleLimitDeg = 66f, float islandMargin = 0.004f, ISet<int> onlyFaces = null)
    {
        var faces = onlyFaces != null ? onlyFaces.Where(f => f >= 0 && f < m.Faces.Count).ToList() : Enumerable.Range(0, m.Faces.Count).ToList();
        if (faces.Count == 0) return 0;
        var inSet = new HashSet<int>(faces);
        var edgeMap = m.BuildEdgeMap();
        var normals = new Vector3[m.Faces.Count];
        foreach (var f in faces) normals[f] = m.FaceNormal(f);
        float cosLimit = MathF.Cos(angleLimitDeg * MathF.PI / 180f);
        var chartOf = new int[m.Faces.Count]; Array.Fill(chartOf, -1);
        var charts = new List<List<int>>();
        // seed from the biggest faces first so charts follow the main surfaces
        foreach (var seed in faces.OrderByDescending(f => m.FaceArea(m.Faces[f])))
        {
            if (chartOf[seed] >= 0) continue;
            var chart = new List<int> { seed }; chartOf[seed] = charts.Count;
            var n0 = normals[seed];
            var q = new Queue<int>(); q.Enqueue(seed);
            while (q.Count > 0)
            {
                int f = q.Dequeue();
                foreach (var e in m.FaceEdges(m.Faces[f]))
                    foreach (var g in edgeMap[e])
                    {
                        if (g == f || chartOf[g] >= 0 || !inSet.Contains(g)) continue;
                        if (Vector3.Dot(normals[g], n0) < cosLimit) continue;
                        chartOf[g] = charts.Count; chart.Add(g); q.Enqueue(g);
                    }
            }
            charts.Add(chart);
        }

        // project each chart on its average plane
        var rects = new List<(int Chart, float W, float H)>();
        var proj = new List<Dictionary<(int Face, int Corner), Vector2>>();
        for (int c = 0; c < charts.Count; c++)
        {
            var n = Vector3.Zero;
            foreach (var f in charts[c]) n += normals[f] * MathF.Max(m.FaceArea(m.Faces[f]), 1e-9f);
            n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitZ;
            Triangulator.Basis(n, out var u, out var v);
            var d = new Dictionary<(int, int), Vector2>();
            var mn = new Vector2(float.MaxValue); var mx = new Vector2(float.MinValue);
            foreach (var f in charts[c])
            {
                var face = m.Faces[f];
                for (int k = 0; k < face.Count; k++)
                {
                    var p = m.Verts[face.V[k]];
                    var uv = new Vector2(Vector3.Dot(p, u), -Vector3.Dot(p, v)); // v down (DirectX)
                    d[(f, k)] = uv; mn = Vector2.Min(mn, uv); mx = Vector2.Max(mx, uv);
                }
            }
            foreach (var key in d.Keys.ToList()) d[key] -= mn;
            proj.Add(d);
            var size = Vector2.Max(mx - mn, new Vector2(1e-5f));
            rects.Add((c, size.X, size.Y));
        }

        // shelf packing into a square, growing until everything fits
        float total = rects.Sum(r => r.W * r.H);
        float marginW = MathF.Sqrt(total) * islandMargin * 2;
        var order = rects.OrderByDescending(r => r.H).ToList();
        float side = MathF.Max(MathF.Sqrt(total) * 1.05f, order.Max(r => MathF.Max(r.W, r.H)) + marginW);
        Dictionary<int, Vector2> place = null;
        for (int attempt = 0; attempt < 60 && place == null; attempt++)
        {
            var pl = new Dictionary<int, Vector2>();
            float x = 0, y = 0, shelf = 0; bool ok = true;
            foreach (var r in order)
            {
                float w = r.W + marginW, h = r.H + marginW;
                if (x + w > side) { x = 0; y += shelf; shelf = 0; }
                if (y + h > side) { ok = false; break; }
                pl[r.Chart] = new Vector2(x + marginW / 2, y + marginW / 2);
                x += w; shelf = MathF.Max(shelf, h);
            }
            if (ok) place = pl; else side *= 1.05f;
        }
        place ??= new Dictionary<int, Vector2>();
        float inv = 1f / side;
        for (int c = 0; c < charts.Count; c++)
        {
            var off = place.TryGetValue(c, out var o) ? o : Vector2.Zero;
            foreach (var ((f, k), uv) in proj[c]) m.Faces[f].UV[k] = (uv + off) * inv;
        }
        return charts.Count;
    }
}
