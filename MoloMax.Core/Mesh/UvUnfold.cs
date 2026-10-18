using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>
/// Unwrap UVW tools: automatic seams, charts, Peel (least-squares conformal unfolding), Relax and Stitch.
/// Same chain as the Molo Auto UV add-on: seams -> unfold -> uniform texel density -> pack.
/// </summary>
public static class UvUnfold
{
    // ------------------------------------------------------------------ seams

    /// <summary>
    /// Seams a modeller would cut: sharp edges (angle between faces above <paramref name="angleDeg"/>), material borders, open borders.
    /// Charts that still can't lie flat (closed or tube-like) are split by facing direction until they can.
    /// </summary>
    public static HashSet<EdgeKey> AutoSeams(PolyMesh m, float angleDeg = 66f, ISet<int> faces = null)
    {
        var seams = new HashSet<EdgeKey>();
        var fset = faces ?? Enumerable.Range(0, m.Faces.Count).ToHashSet();
        var edgeFaces = EdgeFaces(m, fset);
        float cosLimit = MathF.Cos(angleDeg * MathF.PI / 180f);
        var nrm = new Dictionary<int, Vector3>();
        Vector3 N(int f) { if (!nrm.TryGetValue(f, out var n)) nrm[f] = n = m.FaceNormal(f); return n; }
        foreach (var (e, fs) in edgeFaces)
        {
            if (fs.Count != 2) { seams.Add(e); continue; }
            if (m.Faces[fs[0]].MatId != m.Faces[fs[1]].MatId || Vector3.Dot(N(fs[0]), N(fs[1])) < cosLimit) seams.Add(e);
        }
        // charts that are not discs (closed shapes, tubes) get cut by the 6 main directions
        for (int pass = 0; pass < 3; pass++)
        {
            bool changed = false;
            foreach (var chart in Charts(m, seams, fset))
            {
                if (IsDisc(m, chart, seams)) continue;
                var dirOf = new Dictionary<int, int>();
                foreach (var f in chart) dirOf[f] = Dominant(N(f));
                foreach (var f in chart)
                {
                    var face = m.Faces[f];
                    for (int i = 0; i < face.V.Length; i++)
                    {
                        var e = EdgeKey.Make(face.V[i], face.V[(i + 1) % face.V.Length]);
                        if (seams.Contains(e) || !edgeFaces.TryGetValue(e, out var fs) || fs.Count != 2) continue;
                        int g = fs[0] == f ? fs[1] : fs[0];
                        if (dirOf.TryGetValue(g, out var dg) && dg != dirOf[f]) { seams.Add(e); changed = true; }
                    }
                }
                // a cylinder side all facing outwards in 4 directions is fine; a chart with one direction but closed
                // (rare) gets one extra cut along an edge path
                if (!changed && !IsDisc(m, chart, seams)) { CutOnePath(m, chart, seams, edgeFaces); changed = true; }
            }
            if (!changed) break;
        }
        return seams;
    }

    static int Dominant(Vector3 n)
    {
        var a = Vector3.Abs(n);
        if (a.X >= a.Y && a.X >= a.Z) return n.X >= 0 ? 0 : 1;
        if (a.Y >= a.Z) return n.Y >= 0 ? 2 : 3;
        return n.Z >= 0 ? 4 : 5;
    }

    static void CutOnePath(PolyMesh m, List<int> chart, HashSet<EdgeKey> seams, Dictionary<EdgeKey, List<int>> edgeFaces)
    {
        // cut the edges of the first face's ring outward: a simple straight run of inner edges
        var set = chart.ToHashSet();
        var inner = new List<EdgeKey>();
        foreach (var f in chart)
        {
            var face = m.Faces[f];
            for (int i = 0; i < face.V.Length; i++)
            {
                var e = EdgeKey.Make(face.V[i], face.V[(i + 1) % face.V.Length]);
                if (!seams.Contains(e) && edgeFaces.TryGetValue(e, out var fs) && fs.Count == 2 && set.Contains(fs[0]) && set.Contains(fs[1])) inner.Add(e);
            }
        }
        if (inner.Count == 0) return;
        // walk an edge path from a vertex of the first inner edge along the longest axis
        var adj = new Dictionary<int, List<int>>();
        foreach (var e in inner.Distinct()) { (adj.TryGetValue(e.A, out var a) ? a : adj[e.A] = new()).Add(e.B); (adj.TryGetValue(e.B, out var b) ? b : adj[e.B] = new()).Add(e.A); }
        var (mn, mx) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var v in adj.Keys) { mn = Vector3.Min(mn, m.Verts[v]); mx = Vector3.Max(mx, m.Verts[v]); }
        var axis = mx - mn; int ax = axis.X >= axis.Y && axis.X >= axis.Z ? 0 : axis.Y >= axis.Z ? 1 : 2;
        float C(int v) => ax == 0 ? m.Verts[v].X : ax == 1 ? m.Verts[v].Y : m.Verts[v].Z;
        int cur = adj.Keys.OrderBy(C).First();
        var seen = new HashSet<int> { cur };
        for (int step = 0; step < 4096; step++)
        {
            int next = -1; float best = C(cur);
            foreach (var n in adj[cur]) if (!seen.Contains(n) && C(n) > best) { best = C(n); next = n; }
            if (next < 0) break;
            seams.Add(EdgeKey.Make(cur, next)); seen.Add(next); cur = next;
        }
    }

    public static Dictionary<EdgeKey, List<int>> EdgeFaces(PolyMesh m, ISet<int> faces)
    {
        var d = new Dictionary<EdgeKey, List<int>>();
        foreach (var fi in faces)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                var e = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]);
                if (!d.TryGetValue(e, out var l)) d[e] = l = new List<int>(2);
                l.Add(fi);
            }
        }
        return d;
    }

    /// <summary>Seams as they are in the UVs now: edges whose two faces don't share the same UVs there (island borders).</summary>
    public static HashSet<EdgeKey> SeamsFromUv(PolyMesh m, ISet<int> faces = null)
    {
        var fset = faces ?? Enumerable.Range(0, m.Faces.Count).ToHashSet();
        var res = new HashSet<EdgeKey>();
        foreach (var (e, fs) in EdgeFaces(m, fset))
        {
            if (fs.Count != 2) { res.Add(e); continue; }
            Vector2 U(int f, int v) { var face = m.Faces[f]; return face.UV[Array.IndexOf(face.V, v)]; }
            if (Vector2.DistanceSquared(U(fs[0], e.A), U(fs[1], e.A)) > 1e-10f || Vector2.DistanceSquared(U(fs[0], e.B), U(fs[1], e.B)) > 1e-10f) res.Add(e);
        }
        return res;
    }

    /// <summary>Faces grouped across edges that are not seams.</summary>
    public static List<List<int>> Charts(PolyMesh m, HashSet<EdgeKey> seams, ISet<int> faces)
    {
        var edgeFaces = EdgeFaces(m, faces);
        var seen = new HashSet<int>(); var res = new List<List<int>>();
        foreach (var start in faces)
        {
            if (!seen.Add(start)) continue;
            var chart = new List<int>(); var stack = new Stack<int>(); stack.Push(start);
            while (stack.Count > 0)
            {
                int f = stack.Pop(); chart.Add(f);
                var face = m.Faces[f];
                for (int i = 0; i < face.V.Length; i++)
                {
                    var e = EdgeKey.Make(face.V[i], face.V[(i + 1) % face.V.Length]);
                    if (seams.Contains(e)) continue;
                    foreach (var g in edgeFaces[e]) if (seen.Add(g)) stack.Push(g);
                }
            }
            res.Add(chart);
        }
        return res;
    }

    /// <summary>True when the chart is topologically a disc (one border loop, Euler characteristic 1): it can lie flat.</summary>
    public static bool IsDisc(PolyMesh m, List<int> chart, HashSet<EdgeKey> seams)
    {
        var set = chart.ToHashSet();
        var edges = new Dictionary<EdgeKey, int>();
        // vertices are split along seams: count chart corners welded only through non-seam edges
        foreach (var f in chart)
        {
            var face = m.Faces[f];
            for (int i = 0; i < face.V.Length; i++) { var e = EdgeKey.Make(face.V[i], face.V[(i + 1) % face.V.Length]); edges[e] = edges.TryGetValue(e, out var c) ? c + 1 : 1; }
        }
        var border = edges.Where(kv => kv.Value == 1 || seams.Contains(kv.Key)).Select(kv => kv.Key).ToList();
        if (border.Count == 0) return false; // closed
        var verts = UnfoldVertices(m, chart, seams, out _).Count;
        int E = edges.Count + edges.Keys.Count(e => seams.Contains(e) && edges[e] == 2); // a seam inside the chart is two border edges
        int chi = verts - E + chart.Count;
        return chi == 1;
    }

    // ------------------------------------------------------------------ peel (LSCM)

    /// <summary>
    /// Chart vertices: corners that share a mesh vertex and are joined through non-seam edges get one UV point.
    /// Returns, for each (face, corner), the point index.
    /// </summary>
    static List<int> UnfoldVertices(PolyMesh m, List<int> chart, HashSet<EdgeKey> seams, out Dictionary<(int F, int C), int> pointOf)
    {
        // union-find over corners
        var corners = new List<(int F, int C)>();
        var idx = new Dictionary<(int, int), int>();
        foreach (var f in chart) for (int c = 0; c < m.Faces[f].V.Length; c++) { idx[(f, c)] = corners.Count; corners.Add((f, c)); }
        var parent = Enumerable.Range(0, corners.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        var set = chart.ToHashSet();
        var edgeOwner = new Dictionary<EdgeKey, (int F, int C)>();
        foreach (var f in chart)
        {
            var face = m.Faces[f]; int n = face.V.Length;
            for (int c = 0; c < n; c++)
            {
                var e = EdgeKey.Make(face.V[c], face.V[(c + 1) % n]);
                if (seams.Contains(e)) continue;
                if (edgeOwner.TryGetValue(e, out var other))
                {
                    // join the matching corners of the two faces on this edge
                    var of = m.Faces[other.F]; int on = of.V.Length;
                    int a0 = Array.IndexOf(of.V, face.V[c]), a1 = Array.IndexOf(of.V, face.V[(c + 1) % n]);
                    if (a0 >= 0) Union(idx[(f, c)], idx[(other.F, a0)]);
                    if (a1 >= 0) Union(idx[(f, (c + 1) % n)], idx[(other.F, a1)]);
                }
                else edgeOwner[e] = (f, c);
            }
        }
        pointOf = new Dictionary<(int, int), int>();
        var rootToPoint = new Dictionary<int, int>(); var meshVert = new List<int>();
        for (int i = 0; i < corners.Count; i++)
        {
            int r = Find(i);
            if (!rootToPoint.TryGetValue(r, out var p)) { rootToPoint[r] = p = meshVert.Count; meshVert.Add(m.Faces[corners[i].F].V[corners[i].C]); }
            pointOf[corners[i]] = p;
        }
        return meshVert;
    }

    /// <summary>Unfolds one chart flat (least-squares conformal map, 3D scale kept). Returns false when it can't.</summary>
    public static bool Peel(PolyMesh m, List<int> chart, HashSet<EdgeKey> seams)
    {
        var meshVert = UnfoldVertices(m, chart, seams, out var pointOf);
        int n = meshVert.Count;
        if (n < 3) return false;
        // triangles in point indices, with local 2D coords
        var rows = new List<(int[] Col, float[] Val)>();
        double area3d = 0;
        foreach (var f in chart)
        {
            var face = m.Faces[f];
            foreach (var (a, b, c) in Triangulator.Triangulate(m, face))
            {
                int[] p = { pointOf[(f, a)], pointOf[(f, b)], pointOf[(f, c)] };
                var q0 = m.Verts[face.V[a]]; var q1 = m.Verts[face.V[b]]; var q2 = m.Verts[face.V[c]];
                var e1 = q1 - q0; var e2 = q2 - q0;
                var cr = Vector3.Cross(e1, e2); float area = cr.Length() * 0.5f;
                if (area < 1e-12f) continue;
                area3d += area;
                // local frame
                var x = Vector3.Normalize(e1); var nn = Vector3.Normalize(cr); var y = Vector3.Cross(nn, x);
                var l = new[] { Vector2.Zero, new Vector2(e1.Length(), 0), new Vector2(Vector3.Dot(e2, x), Vector3.Dot(e2, y)) };
                // gradient basis g_j = rot90(p_{j+2} - p_{j+1}) / (2A)
                var g = new Vector2[3];
                for (int j = 0; j < 3; j++) { var d = l[(j + 2) % 3] - l[(j + 1) % 3]; g[j] = new Vector2(-d.Y, d.X) / (2 * area); }
                float w = MathF.Sqrt(area);
                // R grad u - grad v = 0, R(x,y) = (-y, x): rows for x and y
                rows.Add((new[] { p[0], p[1], p[2], n + p[0], n + p[1], n + p[2] }, new[] { -g[0].Y * w, -g[1].Y * w, -g[2].Y * w, -g[0].X * w, -g[1].X * w, -g[2].X * w }));
                rows.Add((new[] { p[0], p[1], p[2], n + p[0], n + p[1], n + p[2] }, new[] { g[0].X * w, g[1].X * w, g[2].X * w, -g[0].Y * w, -g[1].Y * w, -g[2].Y * w }));
            }
        }
        if (rows.Count == 0) return false;
        // pin the two points farthest apart (approx: extremes along the longest axis)
        int pa = 0, pb = 0; float best = -1;
        {
            var (mn, mx) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            foreach (var v in meshVert) { mn = Vector3.Min(mn, m.Verts[v]); mx = Vector3.Max(mx, m.Verts[v]); }
            var axis = mx - mn; int ax = axis.X >= axis.Y && axis.X >= axis.Z ? 0 : axis.Y >= axis.Z ? 1 : 2;
            float C(int i) { var p = m.Verts[meshVert[i]]; return ax == 0 ? p.X : ax == 1 ? p.Y : p.Z; }
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < n; i++) { float c = C(i); if (c < lo) { lo = c; pa = i; } if (c > hi) { hi = c; pb = i; } }
            best = Vector3.Distance(m.Verts[meshVert[pa]], m.Verts[meshVert[pb]]);
            if (pa == pb || best < 1e-9f) return false;
        }
        var fixedVal = new Dictionary<int, double> { [pa] = 0, [n + pa] = 0, [pb] = best, [n + pb] = 0 };
        var x0 = Solve(rows, 2 * n, fixedVal);
        if (x0 == null) return false;
        var uv = new Vector2[n];
        for (int i = 0; i < n; i++) uv[i] = new Vector2((float)x0[i], (float)x0[n + i]);
        // keep the faces' orientation (not mirrored)
        double signed = 0;
        foreach (var f in chart)
        {
            var face = m.Faces[f];
            foreach (var (a, b, c) in Triangulator.Triangulate(m, face))
            {
                var u0 = uv[pointOf[(f, a)]]; var u1 = uv[pointOf[(f, b)]]; var u2 = uv[pointOf[(f, c)]];
                signed += (u1.X - u0.X) * (u2.Y - u0.Y) - (u2.X - u0.X) * (u1.Y - u0.Y);
            }
        }
        if (signed < 0) for (int i = 0; i < n; i++) uv[i].Y = -uv[i].Y;
        AlignMinArea(uv, chart.SelectMany(f => { var face = m.Faces[f]; return Enumerable.Range(0, face.V.Length).Select(c => (pointOf[(f, c)], pointOf[(f, (c + 1) % face.V.Length)])); }).ToList());
        // 3D scale: UV area equals surface area (meters), packing scales everything together later
        double area2d = Math.Abs(signed) * 0.5;
        float s = area2d > 1e-12 ? (float)Math.Sqrt(area3d / area2d) : 1f;
        foreach (var f in chart)
        {
            var face = m.Faces[f];
            for (int c = 0; c < face.V.Length; c++) face.UV[c] = uv[pointOf[(f, c)]] * s;
        }
        return true;
    }

    /// <summary>Turns the chart so its bounding box is as small as possible (edge directions tried, like rotating calipers): packs tighter, edges stay straight.</summary>
    static void AlignMinArea(Vector2[] uv, List<(int A, int B)> edges)
    {
        if (uv.Length < 3 || edges.Count == 0) return;
        var tried = new HashSet<int>();
        float bestArea = float.MaxValue, bestAng = 0;
        int step = Math.Max(1, edges.Count / 96);
        for (int k = 0; k < edges.Count; k += step)
        {
            var d = uv[edges[k].B] - uv[edges[k].A];
            if (d.LengthSquared() < 1e-14f) continue;
            float ang = MathF.Atan2(d.Y, d.X);
            int key = (int)MathF.Round(((ang % (MathF.PI / 2)) + MathF.PI / 2) % (MathF.PI / 2) * 1000);
            if (!tried.Add(key)) continue;
            float c = MathF.Cos(-ang), s = MathF.Sin(-ang);
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var p in uv) { float x = p.X * c - p.Y * s, y = p.X * s + p.Y * c; x0 = MathF.Min(x0, x); x1 = MathF.Max(x1, x); y0 = MathF.Min(y0, y); y1 = MathF.Max(y1, y); }
            float area = (x1 - x0) * (y1 - y0);
            if (area < bestArea - 1e-9f) { bestArea = area; bestAng = ang; }
        }
        float cc = MathF.Cos(-bestAng), ss = MathF.Sin(-bestAng);
        for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(uv[i].X * cc - uv[i].Y * ss, uv[i].X * ss + uv[i].Y * cc);
    }

    /// <summary>Least squares A x = 0 with some unknowns fixed: conjugate gradient on the normal equations (Jacobi preconditioned).</summary>
    static double[] Solve(List<(int[] Col, float[] Val)> rows, int n, Dictionary<int, double> fixedVal)
    {
        // free index map
        var free = new int[n]; int nf = 0;
        for (int i = 0; i < n; i++) free[i] = fixedVal.ContainsKey(i) ? -1 : nf++;
        // b = -A_fixed * x_fixed ; matrix rows restricted to free columns
        int R = rows.Count;
        var b = new double[R];
        var rc = new int[R][]; var rv = new double[R][];
        for (int r = 0; r < R; r++)
        {
            var (cols, vals) = rows[r];
            var c2 = new List<int>(6); var v2 = new List<double>(6);
            for (int k = 0; k < cols.Length; k++)
            {
                if (fixedVal.TryGetValue(cols[k], out var fv)) b[r] -= vals[k] * fv;
                else { c2.Add(free[cols[k]]); v2.Add(vals[k]); }
            }
            rc[r] = c2.ToArray(); rv[r] = v2.ToArray();
        }
        // normal equations: M = A^T A, rhs = A^T b
        var diag = new double[nf];
        var rhs = new double[nf];
        for (int r = 0; r < R; r++) for (int k = 0; k < rc[r].Length; k++) { diag[rc[r][k]] += rv[r][k] * rv[r][k]; rhs[rc[r][k]] += rv[r][k] * b[r]; }
        void Mul(double[] x, double[] y)
        {
            Array.Clear(y);
            for (int r = 0; r < R; r++)
            {
                double s = 0; var c = rc[r]; var v = rv[r];
                for (int k = 0; k < c.Length; k++) s += v[k] * x[c[k]];
                for (int k = 0; k < c.Length; k++) y[c[k]] += v[k] * s;
            }
        }
        var xv = new double[nf]; var rr = (double[])rhs.Clone(); var z = new double[nf]; var p = new double[nf]; var Ap = new double[nf];
        for (int i = 0; i < nf; i++) z[i] = diag[i] > 1e-30 ? rr[i] / diag[i] : rr[i];
        Array.Copy(z, p, nf);
        double rz = 0; for (int i = 0; i < nf; i++) rz += rr[i] * z[i];
        double r0 = Math.Sqrt(rhs.Sum(v => v * v)) + 1e-30;
        int maxIt = Math.Max(200, Math.Min(20000, nf * 4));
        for (int it = 0; it < maxIt; it++)
        {
            Mul(p, Ap);
            double pAp = 0; for (int i = 0; i < nf; i++) pAp += p[i] * Ap[i];
            if (Math.Abs(pAp) < 1e-300) break;
            double alpha = rz / pAp;
            double rn = 0;
            for (int i = 0; i < nf; i++) { xv[i] += alpha * p[i]; rr[i] -= alpha * Ap[i]; rn += rr[i] * rr[i]; }
            if (Math.Sqrt(rn) < 1e-9 * r0) break;
            for (int i = 0; i < nf; i++) z[i] = diag[i] > 1e-30 ? rr[i] / diag[i] : rr[i];
            double rzNew = 0; for (int i = 0; i < nf; i++) rzNew += rr[i] * z[i];
            double beta = rzNew / rz; rz = rzNew;
            for (int i = 0; i < nf; i++) p[i] = z[i] + beta * p[i];
        }
        var x = new double[n];
        for (int i = 0; i < n; i++) x[i] = fixedVal.TryGetValue(i, out var fv) ? fv : xv[free[i]];
        if (x.Any(double.IsNaN)) return null;
        return x;
    }

    // ------------------------------------------------------------------ whole unwrap

    /// <summary>Unfolds these faces along the seams, gives every chart the same texel density and packs them in 0..1.</summary>
    public static int Unwrap(PolyMesh m, HashSet<EdgeKey> seams, ISet<int> faces = null, float margin = 0.006f)
    {
        var fset = faces ?? Enumerable.Range(0, m.Faces.Count).ToHashSet();
        var charts = Charts(m, seams, fset);
        int ok = 0; float x = 0;
        foreach (var c in charts)
        {
            if (Peel(m, c, seams)) ok++;
            // charts side by side first: packing finds islands by shared UVs, two charts must never touch before it
            var (mn, mx) = UvTools.Bounds(m, c);
            UvTools.Transform(m, c, Matrix3x2.CreateTranslation(new Vector2(x, 0) - mn));
            x += mx.X - mn.X + 1f;
        }
        UvTools.Pack(m, fset, margin);
        return charts.Count;
    }

    // ------------------------------------------------------------------ relax

    /// <summary>
    /// Relax (by edge lengths): every UV edge is pulled toward its 3D length (charts keep their overall size), which evens out
    /// stretched areas. Island borders move too, like Relax with "keep boundary" off.
    /// </summary>
    public static void Relax(PolyMesh m, IEnumerable<int> faces, int iterations = 60, float strength = 0.5f)
    {
        foreach (var island in UvTools.Islands(m, faces))
        {
            // UV points of this island: corners sharing a mesh vertex and (almost) the same UV
            var key = new Dictionary<(int V, int X, int Y), int>(); var pts = new List<Vector2>(); var vtx = new List<int>();
            var cornerPt = new Dictionary<(int F, int C), int>();
            foreach (var f in island)
            {
                var face = m.Faces[f];
                for (int c = 0; c < face.V.Length; c++)
                {
                    var k = (face.V[c], (int)MathF.Round(face.UV[c].X * 1e5f), (int)MathF.Round(face.UV[c].Y * 1e5f));
                    if (!key.TryGetValue(k, out var p)) { key[k] = p = pts.Count; pts.Add(face.UV[c]); vtx.Add(face.V[c]); }
                    cornerPt[(f, c)] = p;
                }
            }
            var edges = new HashSet<(int, int)>();
            foreach (var f in island)
            {
                var face = m.Faces[f]; int n = face.V.Length;
                for (int c = 0; c < n; c++) { int a = cornerPt[(f, c)], b = cornerPt[(f, (c + 1) % n)]; edges.Add(a < b ? (a, b) : (b, a)); }
            }
            var el = edges.Select(e => (e.Item1, e.Item2, L: Vector3.Distance(m.Verts[vtx[e.Item1]], m.Verts[vtx[e.Item2]]))).Where(e => e.L > 1e-9f).ToList();
            if (el.Count == 0) continue;
            double sumUv = el.Sum(e => (double)Vector2.Distance(pts[e.Item1], pts[e.Item2])), sum3 = el.Sum(e => (double)e.L);
            float s = (float)(sumUv / sum3);
            var centre = pts.Aggregate(Vector2.Zero, (a, b) => a + b) / pts.Count;
            var acc = new Vector2[pts.Count]; var cnt = new int[pts.Count];
            for (int it = 0; it < iterations; it++)
            {
                Array.Clear(acc); Array.Clear(cnt);
                foreach (var (a, b, L) in el)
                {
                    var d = pts[b] - pts[a]; float l = d.Length(); if (l < 1e-12f) continue;
                    var corr = d * ((l - L * s) / l) * 0.5f;
                    acc[a] += corr; acc[b] -= corr; cnt[a]++; cnt[b]++;
                }
                for (int i = 0; i < pts.Count; i++) if (cnt[i] > 0) pts[i] += acc[i] / cnt[i] * strength;
            }
            var c2 = pts.Aggregate(Vector2.Zero, (a, b) => a + b) / pts.Count;
            foreach (var ((f, c), p) in cornerPt) m.Faces[f].UV[c] = pts[p] - c2 + centre;
        }
    }

    // ------------------------------------------------------------------ stitch

    /// <summary>
    /// Stitch: the islands touched by these faces are moved onto the biggest one along their shared mesh edges
    /// (best fit move / turn / scale), then their shared corners are welded. Returns how many islands were stitched.
    /// </summary>
    public static int Stitch(PolyMesh m, ISet<int> faces)
    {
        var all = Enumerable.Range(0, m.Faces.Count).ToList();
        var islands = UvTools.Islands(m, all);
        var islandOf = new int[m.Faces.Count];
        for (int i = 0; i < islands.Count; i++) foreach (var f in islands[i]) islandOf[f] = i;
        var touched = faces.Select(f => islandOf[f]).Distinct().ToList();
        if (touched.Count < 2) return 0;
        int anchor = touched.OrderByDescending(i => islands[i].Count).First();
        var done = new HashSet<int> { anchor };
        int stitched = 0;
        var edgeFaces = EdgeFaces(m, Enumerable.Range(0, m.Faces.Count).ToHashSet());
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var isl in touched)
            {
                if (done.Contains(isl)) continue;
                // pairs (UV in the moving island, UV in a done island) for every shared vertex
                var pairs = new Dictionary<int, (Vector2 From, Vector2 To)>();
                foreach (var (e, fs) in edgeFaces)
                {
                    if (fs.Count != 2) continue;
                    int fa = fs[0], fb = fs[1];
                    if (islandOf[fa] == isl && done.Contains(islandOf[fb])) { } else if (islandOf[fb] == isl && done.Contains(islandOf[fa])) (fa, fb) = (fb, fa); else continue;
                    foreach (var v in new[] { e.A, e.B })
                    {
                        var A = m.Faces[fa]; var B = m.Faces[fb];
                        pairs[v] = (A.UV[Array.IndexOf(A.V, v)], B.UV[Array.IndexOf(B.V, v)]);
                    }
                }
                if (pairs.Count < 2) continue;
                var t = FitSimilarity(pairs.Values.Select(p => p.From).ToList(), pairs.Values.Select(p => p.To).ToList());
                UvTools.Transform(m, islands[isl], t);
                // weld the shared corners exactly
                foreach (var f in islands[isl])
                {
                    var face = m.Faces[f];
                    for (int c = 0; c < face.V.Length; c++) if (pairs.TryGetValue(face.V[c], out var p)) face.UV[c] = p.To;
                }
                done.Add(isl); stitched++; progress = true;
            }
        }
        return stitched;
    }

    /// <summary>Best move + turn + uniform scale taking <paramref name="from"/> onto <paramref name="to"/> (2D Procrustes).</summary>
    public static Matrix3x2 FitSimilarity(List<Vector2> from, List<Vector2> to)
    {
        var cf = from.Aggregate(Vector2.Zero, (a, b) => a + b) / from.Count;
        var ct = to.Aggregate(Vector2.Zero, (a, b) => a + b) / to.Count;
        double a = 0, b = 0, d = 0;
        for (int i = 0; i < from.Count; i++)
        {
            var p = from[i] - cf; var q = to[i] - ct;
            a += p.X * q.X + p.Y * q.Y; b += p.X * q.Y - p.Y * q.X; d += p.X * p.X + p.Y * p.Y;
        }
        if (d < 1e-20) return Matrix3x2.CreateTranslation(ct - cf);
        float s = (float)(Math.Sqrt(a * a + b * b) / d), ang = (float)Math.Atan2(b, a);
        return Matrix3x2.CreateTranslation(-cf) * Matrix3x2.CreateRotation(ang) * Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(ct);
    }

    /// <summary>The Auto UV chain: automatic seams, unfold, same texel density for all charts, pack.</summary>
    public static (int Charts, HashSet<EdgeKey> Seams) AutoUnwrap(PolyMesh m, float angleDeg = 66f, ISet<int> faces = null, float margin = 0.006f)
    {
        var seams = AutoSeams(m, angleDeg, faces);
        int n = Unwrap(m, seams, faces, margin);
        return (n, seams);
    }
}
