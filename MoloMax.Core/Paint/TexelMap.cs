using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Paint;

/// <summary>
/// Every texel of a texture set that lies on the mesh's UV islands, with its object-space position and normal
/// (like the position / normal maps Substance bakes). Painting works in 3D on these texels, so strokes cross
/// UV seams cleanly. Texels outside the islands are dilated from their nearest island texel when composited.
/// </summary>
public sealed class TexelMap
{
    public readonly int Size;
    /// <summary>Index into the compact arrays for each texel of the image, -1 = empty.</summary>
    public readonly int[] Index;
    public Vector3[] Pos, Nrm;
    /// <summary>Pixel coordinates (x + y * Size) of each compact texel.</summary>
    public int[] Pixel;
    /// <summary>Per-vertex mesh maps interpolated at each texel: ambient occlusion (1 = open), curvature (-1 cavity .. 1 edge).</summary>
    public float[] Ao, Curv;
    /// <summary>Tangent frame per texel (T along +u, B along -v: the frame the normal maps are written in).</summary>
    public Vector3[] Tan, Bit;
    /// <summary>Mesh face, UV island and connected element of each texel (fill tools).</summary>
    public int[] FaceOf, IslandOf, ElementOf;
    /// <summary>For empty pixels near an island: the compact texel to copy (padding), else -1.</summary>
    public int[] Dilate;
    public Vector3 Min, Max;
    public int Count => Pos.Length;

    // spatial grid for brush queries
    float cell; Vector3 gridMin; int gx, gy, gz;
    int[] cellStart, cellItems;

    TexelMap(int size) { Size = size; Index = new int[size * size]; Array.Fill(Index, -1); }

    public static bool HasUsableUvs(PolyMesh m)
    {
        float area = 0;
        foreach (var f in m.Faces)
            for (int i = 1; i + 1 < f.Count; i++)
                area += MathF.Abs(Cross2(f.UV[i] - f.UV[0], f.UV[i + 1] - f.UV[0])) * 0.5f;
        return area > 1e-4f;
    }

    static float Cross2(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <param name="faceFilter">Only the faces of one texture set (one material) when the object has several.</param>
    public static TexelMap Build(PolyMesh m, int size, int aoRays = 48, Func<int, bool> faceFilter = null)
    {
        var tm = new TexelMap(size);
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var pix = new List<int>();
        var tan = new List<Vector3>(); var bit = new List<Vector3>(); var fof = new List<int>();
        var faceIsland = new int[m.Faces.Count]; var faceElement = MeshElements(m);
        { int k = 0; foreach (var isl in UvTools.Islands(m)) { foreach (var f in isl) faceIsland[f] = k; k++; } }
        var vAo = VertexAo(m, aoRays); var vCurv = VertexCurvature(m);
        var ao = new List<float>(); var curv = new List<float>();
        // shading normals (smoothing groups, like the game draws the model): the tangent frame of the normal maps must match them
        var cn = RenderMeshBuilder.CornerNormals(m);

        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            if (faceFilter != null && !faceFilter(fi)) continue;
            var f = m.Faces[fi];
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
            {
                // UV (DirectX: v down) to pixels
                Vector2 P(int corner) => new(f.UV[corner].X * size - 0.5f, f.UV[corner].Y * size - 0.5f);
                var pa = P(a); var pb = P(b); var pc = P(c);
                float area = Cross2(pb - pa, pc - pa);
                if (MathF.Abs(area) < 1e-9f) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
                int x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
                int y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
                int va = f.V[a], vb = f.V[b], vc = f.V[c];
                // dP/dx and dP/dy of the triangle in pixels (x = u, y = v down)
                var e1 = m.Verts[vb] - m.Verts[va]; var e2 = m.Verts[vc] - m.Verts[va];
                var d1 = pb - pa; var d2 = pc - pa;
                float det = d1.X * d2.Y - d2.X * d1.Y;
                var dPdx = MathF.Abs(det) > 1e-12f ? (e1 * d2.Y - e2 * d1.Y) / det : Vector3.UnitX;
                var dPdy = MathF.Abs(det) > 1e-12f ? (e2 * d1.X - e1 * d2.X) / det : Vector3.UnitY;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2(x, y);
                        float w0 = Cross2(pb - p, pc - p) / area, w1 = Cross2(pc - p, pa - p) / area, w2 = 1 - w0 - w1;
                        const float e = -0.02f; // slight overlap so island edges have no holes
                        if (w0 < e || w1 < e || w2 < e) continue;
                        int id = x + y * size;
                        if (tm.Index[id] >= 0) continue;
                        tm.Index[id] = pos.Count;
                        pos.Add(m.Verts[va] * w0 + m.Verts[vb] * w1 + m.Verts[vc] * w2);
                        var n = cn[fi][a] * w0 + cn[fi][b] * w1 + cn[fi][c] * w2;
                        n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
                        nrm.Add(n);
                        // orthonormal frame: T along +u, B along -v (normal maps are written y up)
                        var T = dPdx - n * Vector3.Dot(n, dPdx);
                        T = T.LengthSquared() > 1e-20f ? Vector3.Normalize(T) : Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX, n));
                        var up = -dPdy;
                        var B = Vector3.Cross(n, T);
                        if (Vector3.Dot(B, up) < 0) B = -B; // mirrored UVs
                        tan.Add(T); bit.Add(B); fof.Add(fi);
                        ao.Add(vAo[va] * w0 + vAo[vb] * w1 + vAo[vc] * w2);
                        curv.Add(vCurv[va] * w0 + vCurv[vb] * w1 + vCurv[vc] * w2);
                        pix.Add(id);
                    }
            }
        }
        tm.Pos = pos.ToArray(); tm.Nrm = nrm.ToArray(); tm.Pixel = pix.ToArray(); tm.Ao = ao.ToArray(); tm.Curv = curv.ToArray();
        tm.Tan = tan.ToArray(); tm.Bit = bit.ToArray(); tm.FaceOf = fof.ToArray();
        tm.IslandOf = tm.FaceOf.Select(f => faceIsland[f]).ToArray(); tm.ElementOf = tm.FaceOf.Select(f => faceElement[f]).ToArray();
        tm.BuildDilation(Math.Max(2, size / 128));
        tm.BuildGrid();
        return tm;
    }

    /// <summary>Connected element (faces sharing vertices) of every face.</summary>
    public static int[] MeshElements(PolyMesh m)
    {
        var parent = Enumerable.Range(0, m.Verts.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var f in m.Faces) for (int i = 1; i < f.V.Length; i++) { int a = Find(f.V[0]), b = Find(f.V[i]); if (a != b) parent[a] = b; }
        var ids = new Dictionary<int, int>(); var res = new int[m.Faces.Count];
        for (int i = 0; i < m.Faces.Count; i++) { int r = m.Faces[i].V.Length > 0 ? Find(m.Faces[i].V[0]) : -1; if (!ids.TryGetValue(r, out var id)) ids[r] = id = ids.Count; res[i] = id; }
        return res;
    }

    /// <summary>Texel nearest to a point (within maxR), or -1. Used by the clone tool.</summary>
    public int Nearest(Vector3 p, float maxR)
    {
        int best = -1; float bd = float.MaxValue;
        Query(p, maxR, (t, d) => { if (d < bd) { bd = d; best = t; } });
        return best;
    }

    void BuildDilation(int radius)
    {
        Dilate = new int[Size * Size];
        var cur = (int[])Index.Clone();
        for (int pass = 0; pass < radius; pass++)
        {
            var next = (int[])cur.Clone();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int id = x + y * Size;
                    if (cur[id] >= 0) continue;
                    int src = -1;
                    if (x > 0 && cur[id - 1] >= 0) src = cur[id - 1];
                    else if (x < Size - 1 && cur[id + 1] >= 0) src = cur[id + 1];
                    else if (y > 0 && cur[id - Size] >= 0) src = cur[id - Size];
                    else if (y < Size - 1 && cur[id + Size] >= 0) src = cur[id + Size];
                    if (src >= 0) next[id] = src;
                }
            cur = next;
        }
        for (int i = 0; i < cur.Length; i++) Dilate[i] = Index[i] >= 0 ? -1 : cur[i];
    }

    void BuildGrid()
    {
        Min = new Vector3(float.MaxValue); Max = new Vector3(float.MinValue);
        foreach (var p in Pos) { Min = Vector3.Min(Min, p); Max = Vector3.Max(Max, p); }
        if (Pos.Length == 0) { Min = Max = Vector3.Zero; }
        var ext = Max - Min;
        cell = MathF.Max(MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)) / 64f, 1e-4f);
        gridMin = Min - new Vector3(cell * 0.5f);
        gx = (int)(ext.X / cell) + 2; gy = (int)(ext.Y / cell) + 2; gz = (int)(ext.Z / cell) + 2;
        var counts = new int[gx * gy * gz + 1];
        var cellOf = new int[Pos.Length];
        for (int i = 0; i < Pos.Length; i++) { cellOf[i] = Cell(Pos[i]); counts[cellOf[i] + 1]++; }
        for (int i = 1; i < counts.Length; i++) counts[i] += counts[i - 1];
        cellStart = counts;
        cellItems = new int[Pos.Length];
        var fill = (int[])counts.Clone();
        for (int i = 0; i < Pos.Length; i++) cellItems[fill[cellOf[i]]++] = i;
    }

    int Cell(Vector3 p)
    {
        int x = Math.Clamp((int)((p.X - gridMin.X) / cell), 0, gx - 1);
        int y = Math.Clamp((int)((p.Y - gridMin.Y) / cell), 0, gy - 1);
        int z = Math.Clamp((int)((p.Z - gridMin.Z) / cell), 0, gz - 1);
        return x + gx * (y + gy * z);
    }

    /// <summary>Calls <paramref name="visit"/>(texel, distance) for every texel within <paramref name="radius"/> of <paramref name="c"/>.</summary>
    public void Query(Vector3 c, float radius, Action<int, float> visit)
    {
        if (Pos.Length == 0) return;
        float r2 = radius * radius;
        int x0 = Math.Max(0, (int)((c.X - radius - gridMin.X) / cell)), x1 = Math.Min(gx - 1, (int)((c.X + radius - gridMin.X) / cell));
        int y0 = Math.Max(0, (int)((c.Y - radius - gridMin.Y) / cell)), y1 = Math.Min(gy - 1, (int)((c.Y + radius - gridMin.Y) / cell));
        int z0 = Math.Max(0, (int)((c.Z - radius - gridMin.Z) / cell)), z1 = Math.Min(gz - 1, (int)((c.Z + radius - gridMin.Z) / cell));
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int id = x + gx * (y + gy * z);
                    for (int k = cellStart[id]; k < cellStart[id + 1]; k++)
                    {
                        int t = cellItems[k];
                        float d2 = Vector3.DistanceSquared(Pos[t], c);
                        if (d2 <= r2) visit(t, MathF.Sqrt(d2));
                    }
                }
    }

    // ------------------------------------------------------------------------------ mesh maps

    /// <summary>Ambient occlusion per vertex: share of hemisphere rays that escape the mesh.</summary>
    public static float[] VertexAo(PolyMesh m, int rays)
    {
        var res = new float[m.Verts.Count];
        if (rays <= 0) { Array.Fill(res, 1f); return res; }
        var bvh = TriangleBvh.Build(m);
        var vn = new Vector3[m.Verts.Count];
        foreach (var f in m.Faces) { var n = m.FaceNormal(f); foreach (var v in f.V) vn[v] += n; }
        var (mn, mx) = m.Bounds();
        float diag = MathF.Max((mx - mn).Length(), 1e-3f);
        float maxDist = diag * 0.5f, eps = diag * 1e-4f;
        var dirs = Hemisphere(rays);
        Parallel.For(0, m.Verts.Count, i =>
        {
            if (vn[i].LengthSquared() < 1e-20f) { res[i] = 1; return; }
            var n = Vector3.Normalize(vn[i]);
            Mesh.Triangulator.Basis(n, out var t, out var b);
            var o = m.Verts[i] + n * eps * 10;
            float open = 0, wsum = 0;
            foreach (var d in dirs)
            {
                var dir = Vector3.Normalize(t * d.X + b * d.Y + n * d.Z);
                float w = d.Z; // cosine weighted
                wsum += w;
                if (!bvh.Hit(o, dir, maxDist)) open += w;
            }
            res[i] = wsum > 0 ? open / wsum : 1;
        });
        return res;
    }

    static List<Vector3> Hemisphere(int n)
    {
        var l = new List<Vector3>(n);
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float z = 1 - (i + 0.5f) / n; // uniform in z over the hemisphere
            float r = MathF.Sqrt(MathF.Max(0, 1 - z * z));
            float a = golden * i;
            l.Add(new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, MathF.Max(z, 0.05f)));
        }
        return l;
    }

    /// <summary>Convexity per vertex from neighbour positions against the vertex normal: + on edges, - in cavities.</summary>
    public static float[] VertexCurvature(PolyMesh m)
    {
        var vn = new Vector3[m.Verts.Count];
        foreach (var f in m.Faces) { var n = m.FaceNormal(f); foreach (var v in f.V) vn[v] += n; }
        var sum = new float[m.Verts.Count]; var cnt = new int[m.Verts.Count];
        foreach (var e in m.BuildEdgeMap().Keys)
        {
            var d = m.Verts[e.B] - m.Verts[e.A]; float len = d.Length();
            if (len < 1e-9f) continue;
            d /= len;
            if (vn[e.A].LengthSquared() > 0) { sum[e.A] += -Vector3.Dot(Vector3.Normalize(vn[e.A]), d); cnt[e.A]++; }
            if (vn[e.B].LengthSquared() > 0) { sum[e.B] += Vector3.Dot(Vector3.Normalize(vn[e.B]), d); cnt[e.B]++; }
        }
        var res = new float[m.Verts.Count];
        for (int i = 0; i < res.Length; i++) res[i] = cnt[i] > 0 ? Math.Clamp(sum[i] / cnt[i] * 2.5f, -1, 1) : 0;
        return res;
    }
}

/// <summary>Small bounding-volume hierarchy over a mesh's triangles, for occlusion rays.</summary>
public sealed class TriangleBvh
{
    Vector3[] A, B, C;
    Node[] nodes; int[] tris;
    struct Node { public Vector3 Min, Max; public int Left, Right, Start, Count; }

    public static TriangleBvh Build(PolyMesh m)
    {
        var a = new List<Vector3>(); var b = new List<Vector3>(); var c = new List<Vector3>();
        foreach (var f in m.Faces)
            foreach (var (i, j, k) in Triangulator.Triangulate(m, f)) { a.Add(m.Verts[f.V[i]]); b.Add(m.Verts[f.V[j]]); c.Add(m.Verts[f.V[k]]); }
        var t = new TriangleBvh { A = a.ToArray(), B = b.ToArray(), C = c.ToArray(), tris = Enumerable.Range(0, a.Count).ToArray() };
        var nodes = new List<Node>();
        t.BuildNode(nodes, 0, t.tris.Length);
        t.nodes = nodes.ToArray();
        return t;
    }

    int BuildNode(List<Node> nodes, int start, int count)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++) { int t = tris[i]; mn = Vector3.Min(mn, Vector3.Min(A[t], Vector3.Min(B[t], C[t]))); mx = Vector3.Max(mx, Vector3.Max(A[t], Vector3.Max(B[t], C[t]))); }
        int id = nodes.Count;
        nodes.Add(new Node { Min = mn, Max = mx, Left = -1, Right = -1, Start = start, Count = count });
        if (count <= 4) return id;
        var ext = mx - mn; int axis = ext.X > ext.Y ? (ext.X > ext.Z ? 0 : 2) : (ext.Y > ext.Z ? 1 : 2);
        float Key(int t) { var cc = (A[t] + B[t] + C[t]); return axis == 0 ? cc.X : axis == 1 ? cc.Y : cc.Z; }
        Array.Sort(tris, start, count, Comparer<int>.Create((p, q) => Key(p).CompareTo(Key(q))));
        int half = count / 2;
        int l = BuildNode(nodes, start, half), r = BuildNode(nodes, start + half, count - half);
        var n = nodes[id]; n.Left = l; n.Right = r; n.Count = 0; nodes[id] = n;
        return id;
    }

    public bool Hit(Vector3 o, Vector3 d, float maxDist)
    {
        if (nodes == null || nodes.Length == 0) return false;
        var inv = new Vector3(1 / (MathF.Abs(d.X) > 1e-12f ? d.X : 1e-12f), 1 / (MathF.Abs(d.Y) > 1e-12f ? d.Y : 1e-12f), 1 / (MathF.Abs(d.Z) > 1e-12f ? d.Z : 1e-12f));
        Span<int> stack = stackalloc int[64]; int sp = 0; stack[sp++] = 0;
        var ray = new Scenes.Ray(o, d);
        while (sp > 0)
        {
            var n = nodes[stack[--sp]];
            var t0 = (n.Min - o) * inv; var t1 = (n.Max - o) * inv;
            var tmin = Vector3.Min(t0, t1); var tmax = Vector3.Max(t0, t1);
            float enter = MathF.Max(MathF.Max(tmin.X, tmin.Y), MathF.Max(tmin.Z, 0)), exit = MathF.Min(MathF.Min(tmax.X, tmax.Y), MathF.Min(tmax.Z, maxDist));
            if (enter > exit) continue;
            if (n.Left < 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                {
                    int t = tris[i];
                    float hit = Scenes.Picking.RayTriangle(ray, A[t], B[t], C[t]);
                    if (hit > 0 && hit < maxDist) return true;
                }
            }
            else if (sp < 62) { stack[sp++] = n.Left; stack[sp++] = n.Right; }
        }
        return false;
    }
}
