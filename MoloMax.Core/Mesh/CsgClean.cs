using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>
/// Clean topology after a boolean, like cutting by hand with loop cuts: every flat area cut by the boolean is
/// rebuilt as straight rows of quads that run from the corners of the hole to the borders (no diagonals, no slivers,
/// no stray points on straight edges). Areas it can't rebuild cleanly (round holes with many points) stay as they
/// were. If the cleaned mesh isn't closed with the same volume, the boolean result is kept unchanged.
/// </summary>
public static class CsgClean
{
    const float Eps = 1e-5f;
    const int MaxCuts = 14;

    /// <summary>Region of a face the boolean didn't touch: left exactly as it is.</summary>
    public const int Kept = int.MinValue;

    public static PolyMesh Clean(PolyMesh m) => Clean(m, null);

    /// <summary>Same, with a region per face: <see cref="Kept"/> faces stay as they are, the others are only merged within their own region.</summary>
    public static PolyMesh Clean(PolyMesh m, IDictionary<Face, int> region)
    {
        if (m.Faces.Count == 0 || !m.IsClosedManifold(out _)) return m;
        float vol0 = Volume(m);
        PolyMesh best = m;
        try
        {
            var c = m.Clone();
            Dictionary<Face, int> fixedFaces = null;
            if (region != null)
            {
                fixedFaces = new Dictionary<Face, int>(ReferenceEqualityComparer.Instance);
                for (int i = 0; i < m.Faces.Count; i++) if (region.TryGetValue(m.Faces[i], out var r)) fixedFaces[c.Faces[i]] = r;
            }
            DissolveCollinear(c, fixedFaces);
            for (int round = 0; round < 3; round++)
            {
                bool changed = RebuildFlatAreas(c, fixedFaces);
                Csg.FixTJunctions(c, 1e-4f);
                if (!changed) break;
            }
            c.RemoveUnusedVertices();
            if (c.IsClosedManifold(out _) && MathF.Abs(Volume(c) - vol0) <= 1e-3f * MathF.Max(1f, MathF.Abs(vol0))) best = c;
        }
        catch { /* keep the boolean result */ }
        return best;
    }

    // ------------------------------------------------------------------ stray points on straight edges

    /// <summary>A point used by two faces only, sitting on the straight line between its two neighbours, is removed.</summary>
    public static void DissolveCollinear(PolyMesh m) => DissolveCollinear(m, null);

    static void DissolveCollinear(PolyMesh m, Dictionary<Face, int> region)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            var uses = new Dictionary<int, List<int>>();
            for (int fi = 0; fi < m.Faces.Count; fi++)
                foreach (var v in m.Faces[fi].V) { if (!uses.TryGetValue(v, out var l)) uses[v] = l = new List<int>(2); if (!l.Contains(fi)) l.Add(fi); }
            bool any = false;
            var removed = new HashSet<int>();
            foreach (var (v, faces) in uses)
            {
                if (faces.Count != 2 || faces.Any(f => removed.Contains(f))) continue;
                if (region != null && faces.Any(f => region.GetValueOrDefault(m.Faces[f]) == Kept)) continue;
                (int, int)? pair = null; bool ok = true;
                foreach (var fi in faces)
                {
                    var f = m.Faces[fi]; if (f.V.Length <= 3) { ok = false; break; }
                    int i = Array.IndexOf(f.V, v); int a = f.V[(i + f.V.Length - 1) % f.V.Length], b = f.V[(i + 1) % f.V.Length];
                    var p = a < b ? (a, b) : (b, a);
                    if (pair == null) pair = p; else if (pair.Value != p) { ok = false; break; }
                }
                if (!ok || pair == null) continue;
                var pa = m.Verts[pair.Value.Item1]; var pb = m.Verts[pair.Value.Item2]; var pv = m.Verts[v];
                var d = pb - pa; float len2 = d.LengthSquared(); if (len2 < 1e-12f) continue;
                float t = Vector3.Dot(pv - pa, d) / len2;
                if (t <= 0 || t >= 1 || Vector3.DistanceSquared(pa + d * t, pv) > 1e-10f) continue;
                foreach (var fi in faces)
                {
                    var f = m.Faces[fi]; int i = Array.IndexOf(f.V, v);
                    f.V = f.V.Where((_, k) => k != i).ToArray(); f.UV = f.UV.Where((_, k) => k != i).ToArray(); f.Col = f.Col.Where((_, k) => k != i).ToArray();
                    if (f.Col1 != null) f.Col1 = f.Col1.Where((_, k) => k != i).ToArray();
                    removed.Add(fi);
                }
                any = true;
            }
            if (!any) return;
        }
    }

    // ------------------------------------------------------------------ flat areas rebuilt as quads

    sealed class Quad { public float Xa, Xb, Ya, Yb; } // a rectangle in the plane's own axes

    static bool RebuildFlatAreas(PolyMesh m, Dictionary<Face, int> region)
    {
        int nf = m.Faces.Count;
        var normal = new Vector3[nf]; var dist = new float[nf];
        for (int i = 0; i < nf; i++) { normal[i] = m.FaceNormal(i); dist[i] = Vector3.Dot(normal[i], m.Verts[m.Faces[i].V[0]]); }
        // flat areas: neighbours on the same plane with the same material
        var parent = Enumerable.Range(0, nf).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var edgeFaces = new Dictionary<EdgeKey, List<int>>();
        for (int fi = 0; fi < nf; fi++)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++) { var e = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]); if (!edgeFaces.TryGetValue(e, out var l)) edgeFaces[e] = l = new List<int>(2); l.Add(fi); }
        }
        foreach (var l in edgeFaces.Values)
            if (l.Count == 2)
            {
                int a = l[0], b = l[1];
                if (region != null)
                {
                    int ra = region.GetValueOrDefault(m.Faces[a]), rb = region.GetValueOrDefault(m.Faces[b]);
                    if (ra == Kept || rb == Kept || ra != rb) continue;
                }
                if (m.Faces[a].MatId == m.Faces[b].MatId && Vector3.Dot(normal[a], normal[b]) > 0.99999f && MathF.Abs(dist[a] - dist[b]) < 1e-4f) parent[Find(a)] = Find(b);
            }
        var groups = Enumerable.Range(0, nf).GroupBy(Find).Select(g => g.ToList()).ToList();

        var drop = new HashSet<int>(); var add = new List<Face>(); bool changed = false;
        var vertKey = new Dictionary<(long, long, long), int>();
        (long, long, long) Key(Vector3 p) => ((long)MathF.Round(p.X / 1e-4f), (long)MathF.Round(p.Y / 1e-4f), (long)MathF.Round(p.Z / 1e-4f));
        for (int i = 0; i < m.Verts.Count; i++) vertKey.TryAdd(Key(m.Verts[i]), i);

        foreach (var g in groups)
        {
            // only areas the boolean cut: several faces, or one polygon with more than 4 corners
            if (g.Count == 1 && (m.Faces[g[0]].V.Length <= 4 || (region != null && region.GetValueOrDefault(m.Faces[g[0]]) == Kept))) continue;
            var rebuilt = Rebuild(m, g, normal[g[0]], vertKey, Key);
            if (rebuilt == null) continue;
            if (rebuilt.Count == g.Count && g.All(fi => m.Faces[fi].V.Length == 4)) continue; // already clean quads
            foreach (var fi in g) drop.Add(fi);
            if (region != null) { int r = region.GetValueOrDefault(m.Faces[g[0]]); foreach (var rf in rebuilt) region[rf] = r; }
            add.AddRange(rebuilt); changed = true;
        }
        if (!changed) return false;
        var keep = new List<Face>(m.Faces.Count - drop.Count + add.Count);
        for (int i = 0; i < m.Faces.Count; i++) if (!drop.Contains(i)) keep.Add(m.Faces[i]);
        keep.AddRange(add);
        m.Faces = keep;
        return true;
    }

    /// <summary>The faces of one flat area as a grid of quads, or null when it can't be done cleanly.</summary>
    static List<Face> Rebuild(PolyMesh m, List<int> faces, Vector3 n, Dictionary<(long, long, long), int> vertKey, Func<Vector3, (long, long, long)> key)
    {
        // plane axes: walls use the world up as V, floors / ceilings use the world Y
        Vector3 V = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ - n * n.Z : Vector3.UnitY - n * n.Y;
        if (V.LengthSquared() < 1e-8f) return null;
        V = Vector3.Normalize(V); var U = Vector3.Normalize(Vector3.Cross(V, n));
        var O = m.Verts[m.Faces[faces[0]].V[0]];
        Vector2 To2(Vector3 p) => new(Vector3.Dot(p - O, U), Vector3.Dot(p - O, V));

        // the outline (outer border + holes): edges used once inside the area
        var count = new Dictionary<EdgeKey, int>();
        foreach (var fi in faces) { var f = m.Faces[fi]; for (int i = 0; i < f.V.Length; i++) { var e = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]); count[e] = count.TryGetValue(e, out var c) ? c + 1 : 1; } }
        var border = new List<(Vector2 A, Vector2 B)>();
        foreach (var fi in faces) { var f = m.Faces[fi]; for (int i = 0; i < f.V.Length; i++) { int a = f.V[i], b = f.V[(i + 1) % f.V.Length]; if (count[EdgeKey.Make(a, b)] == 1) border.Add((To2(m.Verts[a]), To2(m.Verts[b]))); } }
        if (border.Count < 3) return null;
        // straight lines only: a clean grid needs the outline made of horizontal and vertical edges; anything else
        // (round holes, slanted cuts) becomes one polygon per area, holes joined to the border by two straight edges
        foreach (var (a, b) in border) if (MathF.Abs(a.X - b.X) > Eps && MathF.Abs(a.Y - b.Y) > Eps) return Bridged(m, faces, n, O, U, V, border, To2, vertKey, key);

        var xs = Distinct(border.SelectMany(e => new[] { e.A.X, e.B.X }));
        var ys = Distinct(border.SelectMany(e => new[] { e.A.Y, e.B.Y }));
        // points sitting in the middle of a straight border edge (a neighbour's edge ends there): the faces must be cut
        // through them, else the neighbour's edge would leave a T (an extra corner in the middle of a side)
        var forcedX = new List<float>(); var forcedY = new List<float>();
        var ends = new Dictionary<(long, long), List<bool>>();
        (long, long) K2(Vector2 p) => ((long)MathF.Round(p.X / Eps), (long)MathF.Round(p.Y / Eps));
        foreach (var (a, b) in border)
        {
            bool vert = MathF.Abs(a.X - b.X) <= Eps;
            foreach (var p in new[] { a, b }) { var k = K2(p); if (!ends.TryGetValue(k, out var l)) ends[k] = l = new List<bool>(2); l.Add(vert); }
        }
        foreach (var (a, b) in border)
            foreach (var p in new[] { a, b })
            {
                var l = ends[K2(p)];
                if (l.Count != 2 || l[0] != l[1]) continue;
                if (l[0]) forcedY.Add(p.Y); else forcedX.Add(p.X);
            }
        forcedX = Distinct(forcedX); forcedY = Distinct(forcedY);
        if (xs.Count - forcedX.Count > MaxCuts || ys.Count - forcedY.Count > MaxCuts || (long)xs.Count * ys.Count > 40000) return null;
        var vertical = border.Where(e => MathF.Abs(e.A.X - e.B.X) <= Eps).ToList();

        // a grid cell is inside when a ray going left from its centre crosses the vertical border an odd number of times
        var cells = new List<Quad>();
        for (int i = 0; i + 1 < xs.Count; i++)
            for (int j = 0; j + 1 < ys.Count; j++)
            {
                float cx = (xs[i] + xs[i + 1]) * 0.5f, cy = (ys[j] + ys[j + 1]) * 0.5f;
                int hits = 0;
                foreach (var (a, b) in vertical) if (a.X < cx && (a.Y > cy) != (b.Y > cy)) hits++;
                if ((hits & 1) == 1) cells.Add(new Quad { Xa = xs[i], Xb = xs[i + 1], Ya = ys[j], Yb = ys[j + 1] });
            }
        if (cells.Count == 0) return null;
        cells = MergeCells(cells, forcedX, forcedY);
        // the grid must cover exactly the same area
        float area = 0; foreach (var (a, b) in border) area += a.X * b.Y - b.X * a.Y; area = MathF.Abs(area) * 0.5f;
        float cellArea = cells.Sum(c => (c.Xb - c.Xa) * (c.Yb - c.Ya));
        if (MathF.Abs(area - cellArea) > 1e-3f * MathF.Max(1f, area)) return null;

        // UVs follow the plane (affine fit on the original corners), colour / smoothing from the first face
        var fit = FitUv(m, faces, To2);
        var f0 = m.Faces[faces[0]];
        var col = f0.Col[0];
        int Vert(float x, float y)
        {
            var p = O + U * x + V * y;
            var k = key(p);
            if (vertKey.TryGetValue(k, out var idx)) return idx;
            idx = m.AddVertex(p); vertKey[k] = idx; return idx;
        }
        var res = new List<Face>();
        foreach (var c in cells)
        {
            var pts = new[] { (c.Xa, c.Ya), (c.Xb, c.Ya), (c.Xb, c.Yb), (c.Xa, c.Yb) };
            var idx = pts.Select(p => Vert(p.Item1, p.Item2)).ToArray();
            var f = new Face(idx, f0.MatId, f0.Smooth)
            {
                UV = pts.Select(p => fit(p.Item1, p.Item2)).ToArray(),
                Col = Enumerable.Repeat(col, 4).ToArray(),
                Col1 = f0.Col1 != null ? Enumerable.Repeat(f0.Col1[0], 4).ToArray() : null,
            };
            // same side as the original faces
            var fn = Vector3.Cross(m.Verts[idx[1]] - m.Verts[idx[0]], m.Verts[idx[2]] - m.Verts[idx[0]]);
            if (Vector3.Dot(fn, n) < 0) { Array.Reverse(f.V); Array.Reverse(f.UV); }
            res.Add(f);
        }
        return res;
    }

    /// <summary>Grid cells joined into as few rectangles as possible: rows then columns, or columns then rows,
    /// whichever gives fewer faces (columns first on a tie: vertical bands, like loop cuts on a wall).</summary>
    static List<Quad> MergeCells(List<Quad> cells, List<float> forcedX, List<float> forcedY)
    {
        static Quad T(Quad q) => new() { Xa = q.Ya, Xb = q.Yb, Ya = q.Xa, Yb = q.Xb };
        var rowsFirst = MergeRowsFirst(cells, forcedX, forcedY);
        var colsFirst = MergeRowsFirst(cells.Select(T).ToList(), forcedY, forcedX).Select(T).ToList();
        return colsFirst.Count <= rowsFirst.Count ? colsFirst : rowsFirst;
    }

    static List<Quad> MergeRowsFirst(List<Quad> cells, List<float> forcedX, List<float> forcedY)
    {
        bool Same(float a, float b) => MathF.Abs(a - b) <= Eps;
        bool CutX(float x) => forcedX.Any(f => Same(f, x));
        bool CutY(float y) => forcedY.Any(f => Same(f, y));
        var rows = new List<Quad>();
        foreach (var row in cells.GroupBy(c => MathF.Round(c.Ya / Eps)).Select(g => g.OrderBy(c => c.Xa).ToList()))
        {
            Quad cur = null;
            foreach (var c in row)
            {
                if (cur != null && Same(cur.Xb, c.Xa) && !CutX(c.Xa)) cur.Xb = c.Xb;
                else { cur = new Quad { Xa = c.Xa, Xb = c.Xb, Ya = c.Ya, Yb = c.Yb }; rows.Add(cur); }
            }
        }
        var res = new List<Quad>();
        foreach (var col in rows.GroupBy(c => (MathF.Round(c.Xa / Eps), MathF.Round(c.Xb / Eps))).Select(g => g.OrderBy(c => c.Ya).ToList()))
        {
            Quad cur = null;
            foreach (var c in col)
            {
                if (cur != null && Same(cur.Yb, c.Ya) && !CutY(c.Ya)) cur.Yb = c.Yb;
                else { cur = new Quad { Xa = c.Xa, Xb = c.Xb, Ya = c.Ya, Yb = c.Yb }; res.Add(cur); }
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ any outline: one polygon, holes bridged

    static List<Face> Bridged(PolyMesh m, List<int> faces, Vector3 n, Vector3 O, Vector3 U, Vector3 V, List<(Vector2 A, Vector2 B)> border0,
        Func<Vector3, Vector2> to2, Dictionary<(long, long, long), int> vertKey, Func<Vector3, (long, long, long)> key)
    {
        // points of the outline, shared by position
        var pts = new List<Vector2>(); var at = new Dictionary<(long, long), int>();
        (long, long) K(Vector2 p) => ((long)MathF.Round(p.X / Eps), (long)MathF.Round(p.Y / Eps));
        int P(Vector2 p) { var k = K(p); if (at.TryGetValue(k, out var i)) return i; pts.Add(p); at[k] = pts.Count - 1; return pts.Count - 1; }
        var edges = border0.Select(e => (A: P(e.A), B: P(e.B))).Where(e => e.A != e.B).ToList();
        // loops (the area is on the left of each edge): the outer one turns counter-clockwise, holes clockwise
        var next = new Dictionary<int, int>();
        foreach (var (a, b) in edges) { if (next.ContainsKey(a)) return null; next[a] = b; } // a point used twice: leave it
        var loops = new List<List<int>>(); var seen = new HashSet<int>();
        foreach (var (a, _) in edges)
        {
            if (seen.Contains(a)) continue;
            var loop = new List<int>(); int c = a;
            while (seen.Add(c)) { loop.Add(c); if (!next.TryGetValue(c, out c)) return null; }
            if (c != a) return null;
            loops.Add(loop);
        }
        float Area(List<int> l) { float s = 0; for (int i = 0; i < l.Count; i++) { var p = pts[l[i]]; var q = pts[l[(i + 1) % l.Count]]; s += p.X * q.Y - q.X * p.Y; } return s * 0.5f; }
        var outer = loops.Where(l => Area(l) > 0).ToList(); var holes = loops.Where(l => Area(l) < 0).ToList();
        if (outer.Count != 1) return null;
        float total = Area(outer[0]) + holes.Sum(Area);
        if (total <= 0) return null;

        // half-edges: the outline one way, the bridges both ways
        var half = new List<(int A, int B)>(edges);
        void SplitEdge(int ei, int p) { var (a, b) = half[ei]; half[ei] = (a, p); half.Add((p, b)); }
        // a horizontal ray from a hole point to the nearest outline edge (any loop), split where it lands
        bool Bridge(int from, int dir)
        {
            var o = pts[from]; float best = float.MaxValue; int bestEdge = -1; float bx = 0;
            for (int i = 0; i < half.Count; i++)
            {
                var (a, b) = half[i]; if (a == from || b == from) continue;
                var pa = pts[a]; var pb = pts[b];
                if ((pa.Y > o.Y) == (pb.Y > o.Y)) continue; // must cross the line y = o.Y
                float x = pa.X + (o.Y - pa.Y) / (pb.Y - pa.Y) * (pb.X - pa.X);
                float d = (x - o.X) * dir;
                if (d > Eps && d < best) { best = d; bestEdge = i; bx = x; }
            }
            if (bestEdge < 0) return false;
            var hit = new Vector2(bx, o.Y);
            var (ea, eb) = half[bestEdge];
            int target = Vector2.Distance(hit, pts[ea]) <= 1e-4f ? ea : Vector2.Distance(hit, pts[eb]) <= 1e-4f ? eb : -1;
            if (target < 0) { target = P(hit); SplitEdge(bestEdge, target); }
            half.Add((from, target)); half.Add((target, from));
            return true;
        }
        foreach (var h in holes)
        {
            int left = h.OrderBy(i => pts[i].X).ThenBy(i => pts[i].Y).First();
            int right = h.OrderByDescending(i => pts[i].X).ThenByDescending(i => pts[i].Y).First();
            if (!Bridge(left, -1) || !Bridge(right, +1)) return null;
        }

        // faces of the planar graph: from each half-edge, turn as far right as possible (area on the left)
        var outgoing = half.Select((e, i) => (e, i)).GroupBy(x => x.e.A).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        var used = new bool[half.Count];
        var polys = new List<List<int>>();
        for (int s0 = 0; s0 < half.Count; s0++)
        {
            if (used[s0]) continue;
            var poly = new List<int>(); int e = s0; int guard = 0;
            while (!used[e])
            {
                used[e] = true; var (a, b) = half[e]; poly.Add(a);
                if (!outgoing.TryGetValue(b, out var outs) || ++guard > 100000) return null;
                var back = pts[a] - pts[b]; float a0 = MathF.Atan2(back.Y, back.X);
                int pick = -1; float bestAng = -1;
                foreach (var oi in outs)
                {
                    var d = pts[half[oi].B] - pts[b];
                    float ang = MathF.Atan2(d.Y, d.X) - a0; while (ang < 0) ang += MathF.Tau; while (ang >= MathF.Tau) ang -= MathF.Tau;
                    if (half[oi].B == a) ang = 0; // going back the same way: last choice
                    if (ang > bestAng) { bestAng = ang; pick = oi; }
                }
                e = pick;
            }
            if (e != s0 || poly.Count < 3) return null;
            polys.Add(poly);
        }
        float sum = polys.Sum(Area);
        if (polys.Any(pl => Area(pl) <= 0) || MathF.Abs(sum - total) > 1e-3f * MathF.Max(1f, total)) return null;

        var fit = FitUv(m, faces, to2); var f0 = m.Faces[faces[0]];
        var res = new List<Face>();
        foreach (var pl in polys)
        {
            var idx = pl.Select(i => { var p = O + U * pts[i].X + V * pts[i].Y; var k = key(p); if (vertKey.TryGetValue(k, out var vi)) return vi; vi = m.AddVertex(p); vertKey[k] = vi; return vi; }).ToArray();
            var f = new Face(idx, f0.MatId, f0.Smooth)
            {
                UV = pl.Select(i => fit(pts[i].X, pts[i].Y)).ToArray(),
                Col = Enumerable.Repeat(f0.Col[0], idx.Length).ToArray(),
                Col1 = f0.Col1 != null ? Enumerable.Repeat(f0.Col1[0], idx.Length).ToArray() : null,
            };
            if (Vector3.Dot(m.FaceNormal(f), n) < 0) { Array.Reverse(f.V); Array.Reverse(f.UV); }
            res.Add(f);
        }
        return res;
    }

    static List<float> Distinct(IEnumerable<float> values)
    {
        var s = values.OrderBy(v => v).ToList(); var r = new List<float>();
        foreach (var v in s) if (r.Count == 0 || v - r[^1] > Eps) r.Add(v);
        return r;
    }

    static Func<float, float, Vector2> FitUv(PolyMesh m, List<int> faces, Func<Vector3, Vector2> to2)
    {
        // least squares uv = a*x + b*y + c, for U and V
        double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, s1 = 0; var su = new double[3]; var sv = new double[3];
        foreach (var fi in faces)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                var p = to2(m.Verts[f.V[i]]); double x = p.X, y = p.Y; var uv = f.UV[i];
                sxx += x * x; sxy += x * y; sx += x; syy += y * y; sy += y; s1 += 1;
                su[0] += x * uv.X; su[1] += y * uv.X; su[2] += uv.X;
                sv[0] += x * uv.Y; sv[1] += y * uv.Y; sv[2] += uv.Y;
            }
        }
        var A = new double[3, 3] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, s1 } };
        var cu = Solve3(A, su); var cv = Solve3(A, sv);
        if (cu == null || cv == null) { var u0 = m.Faces[faces[0]].UV[0]; return (x, y) => u0; }
        return (x, y) => new Vector2((float)(cu[0] * x + cu[1] * y + cu[2]), (float)(cv[0] * x + cv[1] * y + cv[2]));
    }

    static double[] Solve3(double[,] a, double[] b)
    {
        var m = new double[3, 4];
        for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) m[i, j] = a[i, j]; m[i, 3] = b[i]; }
        for (int c = 0; c < 3; c++)
        {
            int p = c; for (int r = c + 1; r < 3; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) < 1e-12) return null;
            for (int k = 0; k < 4; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
            for (int r = 0; r < 3; r++) if (r != c) { double f = m[r, c] / m[c, c]; for (int k = 0; k < 4; k++) m[r, k] -= f * m[c, k]; }
        }
        return new[] { m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2] };
    }

    public static float Volume(PolyMesh m)
    {
        double v = 0;
        foreach (var f in m.Faces)
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                v += Vector3.Dot(m.Verts[f.V[a]], Vector3.Cross(m.Verts[f.V[b]], m.Verts[f.V[c]]));
        return (float)(v / 6);
    }
}
