using System.Numerics;

namespace MoloMax.Core.Mesh;

public static partial class MeshOps
{
    /// <summary>Directed boundary edges (a→b as they appear in their face) of a face region.</summary>
    static List<(int A, int B, int Face, int Corner)> RegionBoundary(PolyMesh m, ICollection<int> faces)
    {
        var count = new Dictionary<EdgeKey, int>();
        foreach (var fi in faces)
            foreach (var e in m.FaceEdges(m.Faces[fi])) count[e] = count.TryGetValue(e, out var c) ? c + 1 : 1;
        var res = new List<(int, int, int, int)>();
        foreach (var fi in faces)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                int a = f.V[i], b = f.V[(i + 1) % f.V.Length];
                if (count[EdgeKey.Make(a, b)] == 1) res.Add((a, b, fi, i));
            }
        }
        return res;
    }

    /// <summary>
    /// Shared topology of extrude / inset / bevel: duplicates the region's boundary vertices,
    /// re-points the region's faces at the copies and stitches a ring of quads between old and new.
    /// Returns old→new map for the boundary vertices and the indices of the ring faces.
    /// </summary>
    static (List<(int Orig, int Copy)> Map, List<int> Ring) SplitRegion(PolyMesh m, List<int> faces, bool metricSideUV, out List<(int A, int B, int Face, int Corner)> boundary)
    {
        boundary = RegionBoundary(m, faces);
        var inRegion = new HashSet<int>(faces);
        // a boundary point gets one copy per "sector" of the region around it: usually one, but where the region touches
        // itself at a corner (two fans of faces meeting at one point) each fan gets its own copy, else the solid pinches
        var bverts = new HashSet<int>(); foreach (var (a, b, _, _) in boundary) { bverts.Add(a); bverts.Add(b); }
        var copyOf = new Dictionary<(int V, int Face), int>();
        var pairs = new List<(int Orig, int Copy)>();
        foreach (var v in bverts)
        {
            // region faces around v, joined through edges at v shared by two region faces
            var around = faces.Where(fi => Array.IndexOf(m.Faces[fi].V, v) >= 0).ToList();
            var parent = new Dictionary<int, int>(); foreach (var fi in around) parent[fi] = fi;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var byEdge = new Dictionary<int, int>(); // other end of an edge at v -> first region face using it
            foreach (var fi in around)
            {
                var fv = m.Faces[fi].V; int n = fv.Length, c = Array.IndexOf(fv, v);
                foreach (var o in new[] { fv[(c + 1) % n], fv[(c + n - 1) % n] })
                {
                    if (byEdge.TryGetValue(o, out var g)) { int ra = Find(fi), rb = Find(g); if (ra != rb) parent[ra] = rb; }
                    else byEdge[o] = fi;
                }
            }
            var sectorCopy = new Dictionary<int, int>();
            foreach (var fi in around)
            {
                int r = Find(fi);
                if (!sectorCopy.TryGetValue(r, out var cp)) { cp = m.AddVertex(m.Verts[v]); sectorCopy[r] = cp; pairs.Add((v, cp)); }
                copyOf[(v, fi)] = cp;
            }
        }
        // remember the corner attributes before re-pointing
        var ringSpecs = new List<(int A, int B, Face Src, int Ca, int Cb, int CopyA, int CopyB)>();
        foreach (var (a, b, fi, ci) in boundary)
        {
            var f = m.Faces[fi];
            ringSpecs.Add((a, b, f, ci, (ci + 1) % f.V.Length, copyOf[(a, fi)], copyOf[(b, fi)]));
        }
        foreach (var fi in faces)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++) if (copyOf.TryGetValue((f.V[i], fi), out var nv)) f.V[i] = nv;
        }
        var ring = new List<int>();
        foreach (var (a, b, src, ca, cb, na, nb) in ringSpecs)
        {
            var q = new Face(new[] { a, b, nb, na }, src.MatId, 0);
            q.Col[0] = src.Col[ca]; q.Col[1] = src.Col[cb]; q.Col[2] = src.Col[cb]; q.Col[3] = src.Col[ca];
            if (!metricSideUV) { q.UV[0] = src.UV[ca]; q.UV[1] = src.UV[cb]; q.UV[2] = src.UV[cb]; q.UV[3] = src.UV[ca]; }
            m.Faces.Add(q);
            ring.Add(m.Faces.Count - 1);
        }
        return (pairs, ring);
    }

    static List<List<int>> Components(PolyMesh m, IEnumerable<int> faces)
    {
        // only the selected faces' edges are looked at (the whole mesh's edge map cost ~0.5 s on a 500k-polygon room)
        var list = faces.Distinct().ToList();
        var index = new Dictionary<int, int>(list.Count);
        for (int i = 0; i < list.Count; i++) index[list[i]] = i;
        var parent = new int[list.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        // faces sharing an edge are one group (like 3ds Max); groups touching at a single corner stay separate
        var firstOf = new Dictionary<EdgeKey, int>(list.Count * 4);
        for (int i = 0; i < list.Count; i++)
        {
            var f = m.Faces[list[i]].V;
            for (int k = 0; k < f.Length; k++)
            {
                var e = EdgeKey.Make(f[k], f[(k + 1) % f.Length]);
                if (firstOf.TryGetValue(e, out int j)) { int a = Find(i), b = Find(j); if (a != b) parent[a] = b; }
                else firstOf[e] = i;
            }
        }
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < list.Count; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<int>();
            g.Add(list[i]);
        }
        return groups.Values.ToList();
    }

    /// <summary>Normalised, or <paramref name="fallback"/> for a zero vector (normalising zero gives NaN points).</summary>
    internal static Vector3 SN(Vector3 v, Vector3 fallback) { float l = v.Length(); return l > 1e-12f && float.IsFinite(l) ? v / l : fallback; }

    public static void SideUVMetric(PolyMesh m, IEnumerable<int> ring)
    {
        foreach (var fi in ring)
        {
            var f = m.Faces[fi];
            var p0 = m.Verts[f.V[0]]; var p1 = m.Verts[f.V[1]]; var p3 = m.Verts[f.V[3]];
            float len = Vector3.Distance(p0, p1), h = Vector3.Distance(p0, p3);
            f.UV[0] = new Vector2(0, 0); f.UV[1] = new Vector2(len, 0); f.UV[2] = new Vector2(len, -h); f.UV[3] = new Vector2(0, -h);
        }
    }

    /// <summary>
    /// Extrude polygons (Group: each connected group along its average normal; By Polygon: each face alone).
    /// The extruded faces keep their indices; returns the indices of the new side faces.
    /// </summary>
    public static List<int> ExtrudeFaces(PolyMesh m, ISet<int> faces, float height, bool byPolygon = false) => ExtrudeFaces(m, faces, height, byPolygon, null);

    /// <summary>Same, and lists the points that moved with their direction (an interactive drag then only moves them further).</summary>
    public static List<int> ExtrudeFaces(PolyMesh m, ISet<int> faces, float height, bool byPolygon, List<(int V, Vector3 N)> moved)
    {
        var sides = new List<int>();
        if (faces == null || faces.Count == 0) return sides;
        var groups = byPolygon ? faces.Select(f => new List<int> { f }).ToList() : Components(m, faces);
        foreach (var g in groups)
        {
            var n = Vector3.Zero;
            foreach (var fi in g) n += m.FaceNormal(fi) * MathF.Max(m.FaceArea(m.Faces[fi]), 1e-6f);
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
            var (_, ring) = SplitRegion(m, g, true, out _);
            var mv = new HashSet<int>();
            foreach (var fi in g) foreach (var v in m.Faces[fi].V) mv.Add(v);
            foreach (var v in mv) { m.Verts[v] += n * height; moved?.Add((v, n)); }
            SideUVMetric(m, ring);
            sides.AddRange(ring);
        }
        return sides;
    }

    /// <summary>
    /// Inset polygons by a distance in metres (Group: the outline of each connected group;
    /// By Polygon: each face on its own). Negative amounts grow the faces outward.
    /// Returns the indices of the new border faces.
    /// </summary>
    public static List<int> InsetFaces(PolyMesh m, ISet<int> faces, float amount, bool byPolygon = false)
    {
        var border = new List<int>();
        if (faces == null || faces.Count == 0) return border;
        var groups = byPolygon ? faces.Select(f => new List<int> { f }).ToList() : Components(m, faces);
        foreach (var g in groups)
        {
            // incoming / outgoing boundary edges per vertex, and the averaged normal around it
            var bnd = RegionBoundary(m, g);
            var inc = new Dictionary<int, List<int>>(); var outg = new Dictionary<int, List<int>>();
            foreach (var (a, b, _, _) in bnd)
            {
                (outg.TryGetValue(a, out var l1) ? l1 : outg[a] = new List<int>()).Add(b);
                (inc.TryGetValue(b, out var l2) ? l2 : inc[b] = new List<int>()).Add(a);
            }
            var vNormal = new Dictionary<int, Vector3>(); var vCentre = new Dictionary<int, Vector3>(); var vCount = new Dictionary<int, int>();
            foreach (var fi in g)
            {
                var fn = m.FaceNormal(fi); var fc = m.FaceCenter(fi);
                foreach (var v in m.Faces[fi].V)
                {
                    vNormal[v] = (vNormal.TryGetValue(v, out var x) ? x : Vector3.Zero) + fn;
                    vCentre[v] = (vCentre.TryGetValue(v, out var y) ? y : Vector3.Zero) + fc;
                    vCount[v] = (vCount.TryGetValue(v, out var c) ? c : 0) + 1;
                }
            }
            var target = new Dictionary<int, Vector3>();
            foreach (var v in outg.Keys)
            {
                var p = m.Verts[v];
                var n = SN(vNormal[v], Vector3.UnitZ);
                if (inc.TryGetValue(v, out var ins) && ins.Count == 1 && outg[v].Count == 1)
                {
                    var e1 = SN(p - m.Verts[ins[0]], Vector3.Zero);
                    var e2 = SN(m.Verts[outg[v][0]] - p, Vector3.Zero);
                    var l1 = SN(Vector3.Cross(n, e1), Vector3.Zero);
                    var l2 = SN(Vector3.Cross(n, e2), Vector3.Zero);
                    var bis = l1 + l2;
                    if (bis.LengthSquared() < 1e-10f) bis = l1.LengthSquared() > 0 ? l1 : l2;
                    bis = SN(bis, Vector3.Zero);
                    float d = MathF.Max(Vector3.Dot(bis, l1), 0.2f);
                    target[v] = bis == Vector3.Zero ? p : p + bis * (amount / d); // degenerate corner (zero-length edge): stays put
                }
                else
                {
                    var c = vCentre[v] / vCount[v];
                    var dir = c - p; dir -= n * Vector3.Dot(dir, n);
                    target[v] = dir.LengthSquared() > 1e-12f ? p + Vector3.Normalize(dir) * amount : p;
                }
            }
            var (map, ring) = SplitRegion(m, g, false, out _);
            foreach (var (orig, copy) in map) if (target.TryGetValue(orig, out var t)) m.Verts[copy] = t;
            border.AddRange(ring);
        }
        return border;
    }

    /// <summary>3ds Max Bevel: extrude by <paramref name="height"/>, then grow (+) or shrink (−) the outline.</summary>
    public static List<int> BevelFaces(PolyMesh m, ISet<int> faces, float height, float outline, bool byPolygon = false)
    {
        var sides = ExtrudeFaces(m, faces, height, byPolygon);
        if (MathF.Abs(outline) < 1e-7f) return sides;
        // Outline moves the top's border vertices in-plane, without new geometry.
        var groups = byPolygon ? faces.Select(f => new List<int> { f }).ToList() : Components(m, faces);
        foreach (var g in groups)
        {
            var bnd = RegionBoundary(m, g);
            var inc = new Dictionary<int, int>(); var outg = new Dictionary<int, int>();
            foreach (var (a, b, _, _) in bnd) { outg[a] = b; inc[b] = a; }
            var n = Vector3.Zero; foreach (var fi in g) n += m.FaceNormal(fi);
            n = SN(n, Vector3.UnitZ);
            var moves = new Dictionary<int, Vector3>();
            foreach (var v in outg.Keys)
            {
                if (!inc.ContainsKey(v)) continue;
                var p = m.Verts[v];
                var e1 = SN(p - m.Verts[inc[v]], Vector3.Zero); var e2 = SN(m.Verts[outg[v]] - p, Vector3.Zero);
                var l1 = SN(Vector3.Cross(n, e1), Vector3.Zero); var l2 = SN(Vector3.Cross(n, e2), Vector3.Zero);
                var bis = l1 + l2; if (bis.LengthSquared() < 1e-10f) bis = l1.LengthSquared() > 0 ? l1 : l2; bis = SN(bis, Vector3.Zero);
                if (bis == Vector3.Zero) continue; // degenerate corner: stays put
                float d = MathF.Max(Vector3.Dot(bis, l1), 0.2f);
                moves[v] = p - bis * (outline / d);
            }
            foreach (var kv in moves) m.Verts[kv.Key] = kv.Value;
        }
        return sides;
    }

    /// <summary>
    /// Tessellate (edge midpoints + centre): quads → 4 quads, triangles → 4 triangles, n-gons → n quads.
    /// Null = whole mesh. Neighbours get the midpoints inserted so there are no cracks.
    /// Returns the new faces' indices.
    /// </summary>
    public static HashSet<int> Tessellate(PolyMesh m, ISet<int> faces)
    {
        var sel = faces ?? new HashSet<int>(Enumerable.Range(0, m.Faces.Count));
        var mid = new Dictionary<EdgeKey, int>();
        int Mid(int a, int b)
        {
            var k = EdgeKey.Make(a, b);
            if (!mid.TryGetValue(k, out var v)) mid[k] = v = m.AddVertex((m.Verts[a] + m.Verts[b]) * 0.5f);
            return v;
        }
        var newFaces = new List<Face>();
        var oldFaces = new List<Face>();
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            if (!sel.Contains(fi)) { oldFaces.Add(f); continue; }
            int n = f.V.Length;
            var ms = new int[n]; var muv = new Vector2[n]; var mcol = new Vector4[n];
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                ms[i] = Mid(f.V[i], f.V[j]);
                muv[i] = (f.UV[i] + f.UV[j]) * 0.5f; mcol[i] = (f.Col[i] + f.Col[j]) * 0.5f;
            }
            if (n == 3)
            {
                void Tri(int a, Vector2 ua, Vector4 ca, int b, Vector2 ub, Vector4 cb, int c, Vector2 uc, Vector4 cc)
                {
                    var t = new Face(new[] { a, b, c }, f.MatId, f.Smooth);
                    t.UV[0] = ua; t.UV[1] = ub; t.UV[2] = uc; t.Col[0] = ca; t.Col[1] = cb; t.Col[2] = cc;
                    newFaces.Add(t);
                }
                Tri(f.V[0], f.UV[0], f.Col[0], ms[0], muv[0], mcol[0], ms[2], muv[2], mcol[2]);
                Tri(f.V[1], f.UV[1], f.Col[1], ms[1], muv[1], mcol[1], ms[0], muv[0], mcol[0]);
                Tri(f.V[2], f.UV[2], f.Col[2], ms[2], muv[2], mcol[2], ms[1], muv[1], mcol[1]);
                Tri(ms[0], muv[0], mcol[0], ms[1], muv[1], mcol[1], ms[2], muv[2], mcol[2]);
                continue;
            }
            int c0 = m.AddVertex(m.FaceCenter(f));
            var cuv = Vector2.Zero; var ccol = Vector4.Zero;
            for (int i = 0; i < n; i++) { cuv += f.UV[i]; ccol += f.Col[i]; }
            cuv /= n; ccol /= n;
            for (int i = 0; i < n; i++)
            {
                int p = (i - 1 + n) % n;
                var q = new Face(new[] { f.V[i], ms[i], c0, ms[p] }, f.MatId, f.Smooth);
                q.UV[0] = f.UV[i]; q.UV[1] = muv[i]; q.UV[2] = cuv; q.UV[3] = muv[p];
                q.Col[0] = f.Col[i]; q.Col[1] = mcol[i]; q.Col[2] = ccol; q.Col[3] = mcol[p];
                newFaces.Add(q);
            }
        }
        // insert midpoints into untouched neighbours
        foreach (var f in oldFaces)
        {
            var v = new List<int>(); var uv = new List<Vector2>(); var col = new List<Vector4>();
            for (int i = 0; i < f.V.Length; i++)
            {
                int j = (i + 1) % f.V.Length;
                v.Add(f.V[i]); uv.Add(f.UV[i]); col.Add(f.Col[i]);
                if (mid.TryGetValue(EdgeKey.Make(f.V[i], f.V[j]), out var mv))
                {
                    v.Add(mv); uv.Add((f.UV[i] + f.UV[j]) * 0.5f); col.Add((f.Col[i] + f.Col[j]) * 0.5f);
                }
            }
            f.V = v.ToArray(); f.UV = uv.ToArray(); f.Col = col.ToArray();
        }
        m.Faces = oldFaces;
        int start = m.Faces.Count;
        m.Faces.AddRange(newFaces);
        return new HashSet<int>(Enumerable.Range(start, newFaces.Count));
    }

    /// <summary>
    /// Cap: closes open borders with one polygon each. When <paramref name="touching"/> is given,
    /// only borders that use one of those vertices are capped. Returns the new faces.
    /// </summary>
    public static List<int> CapBorders(PolyMesh m, ISet<int> touching = null)
    {
        var em = m.BuildEdgeMap();
        // reversed boundary edges: face has a→b, the cap needs b→a
        var next = new Dictionary<int, int>();
        var matOf = new Dictionary<int, int>();
        foreach (var f in m.Faces)
            for (int i = 0; i < f.V.Length; i++)
            {
                int a = f.V[i], b = f.V[(i + 1) % f.V.Length];
                if (em[EdgeKey.Make(a, b)].Count == 1) { next[b] = a; matOf[b] = f.MatId; }
            }
        var created = new List<int>();
        var used = new HashSet<int>();
        foreach (var start in next.Keys.ToList())
        {
            if (used.Contains(start)) continue;
            var loop = new List<int>(); int cur = start; int guard = 0;
            while (guard++ < next.Count + 2)
            {
                if (!next.TryGetValue(cur, out var nx)) { loop.Clear(); break; }
                loop.Add(cur); used.Add(cur);
                cur = nx;
                if (cur == start) break;
                if (used.Contains(cur)) { loop.Clear(); break; }
            }
            if (loop.Count < 3) continue;
            if (touching != null && !loop.Any(touching.Contains)) continue;
            var face = m.AddFace(loop.ToArray(), matOf[start], 0);
            created.Add(m.Faces.Count - 1);
        }
        if (created.Count > 0) BoxMap(m, new HashSet<int>(created), 1f);
        return created;
    }
}
