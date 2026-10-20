using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>Chamfer (edge bevel), Bridge, Slice (knife / quick slice) for Editable Poly.</summary>
public static partial class MeshOps
{
    // ---------------------------------------------------------------- chamfer

    /// <summary>
    /// 3ds Max Chamfer / Blender edge Bevel (one segment): each selected edge becomes a strip of width
    /// 2 x <paramref name="amount"/>, corners where several selected edges meet get a cap polygon.
    /// </summary>
    public static void ChamferEdges(PolyMesh m, ICollection<EdgeKey> edges, float amount) => ChamferEdges(m, edges, amount, 1);

    /// <summary>Same, with <paramref name="segments"/> sides across each strip: 1 = flat chamfer, more = a rounded bevel
    /// (the profile is tangent to both faces, a quarter circle on a square corner).</summary>
    public static void ChamferEdges(PolyMesh m, ICollection<EdgeKey> edges, float amount, int segments)
    {
        if (edges == null || edges.Count == 0 || amount <= 0) return;
        segments = Math.Clamp(segments, 1, 32);
        var sel = new HashSet<EdgeKey>(edges);
        var touched = new HashSet<int>(); foreach (var e in sel) { touched.Add(e.A); touched.Add(e.B); }
        var vf = m.BuildVertexFaces();
        var copyOf = new Dictionary<(int Face, int Vert), int>(); // (face, original vertex) -> its own copy
        int fcount = m.Faces.Count;
        var swaps = new List<(int F, int C, int NV)>();
        for (int fi = 0; fi < fcount; fi++)
        {
            var f = m.Faces[fi]; int n = f.V.Length;
            for (int c = 0; c < n; c++)
            {
                int v = f.V[c];
                if (!touched.Contains(v)) continue;
                int prev = f.V[(c + n - 1) % n], next = f.V[(c + 1) % n];
                bool sp = sel.Contains(EdgeKey.Make(prev, v)), sn = sel.Contains(EdgeKey.Make(v, next));
                if (!sp && !sn) continue;
                var p = m.Verts[v];
                var a = Vector3.Normalize(m.Verts[prev] - p); var b = Vector3.Normalize(m.Verts[next] - p);
                Vector3 dir;
                if (sp && sn)
                {
                    // corner between two chamfered edges: along the bisector so both strips keep their width
                    float s = MathF.Max(0.2f, MathF.Sqrt(MathF.Max(0, (1 - Vector3.Dot(a, b)) * 0.5f)));
                    dir = Vector3.Normalize(a + b) / s;
                }
                else dir = sn ? a : b; // slide along this face's edge that is not chamfered
                float edgeLen = MathF.Min(Vector3.Distance(m.Verts[prev], p), Vector3.Distance(m.Verts[next], p));
                int nv = m.AddVertex(p + dir * MathF.Min(amount, edgeLen * 0.49f));
                copyOf[(fi, v)] = nv;
                swaps.Add((fi, c, nv));
            }
        }
        // a vertex stays when a face around it has a copy on one side only (a chamfer ending at a vertex with more
        // edges, e.g. half of a cut edge): the strip then closes on it instead of tearing the mesh open
        var kept = new HashSet<int>();
        foreach (var v in touched)
            foreach (var g in vf[v])
            {
                if (copyOf.ContainsKey((g, v))) continue;
                var gf = m.Faces[g]; int gn0 = gf.V.Length, gc = Array.IndexOf(gf.V, v);
                if (gc < 0) continue;
                int gpv = gf.V[(gc + gn0 - 1) % gn0], gnv = gf.V[(gc + 1) % gn0];
                int fa = vf[gpv].Intersect(vf[v]).FirstOrDefault(x => x != g, -1), fb = vf[v].Intersect(vf[gnv]).FirstOrDefault(x => x != g, -1);
                bool hp = fa >= 0 && copyOf.ContainsKey((fa, v)), hn = fb >= 0 && copyOf.ContainsKey((fb, v));
                if (!(hp && hn)) { kept.Add(v); break; }
            }
        foreach (var (fi, c, nv) in swaps) m.Faces[fi].V[c] = nv;
        var origOfCopy = copyOf.ToDictionary(kv => kv.Value, kv => kv.Key.Vert);
        // rounded bevel: the points between the two copies of a vertex (one per face of a chamfered edge), shared by the
        // strip, the end faces and the corner caps so everything stays joined
        var arcs = new Dictionary<(int V, int A, int B), List<int>>();
        List<int> Arc(int v, int qa, int qb)
        {
            if (segments <= 1 || qa == qb) return new List<int>();
            if (arcs.TryGetValue((v, qa, qb), out var l)) return l;
            if (arcs.TryGetValue((v, qb, qa), out var r)) { var rev = new List<int>(r); rev.Reverse(); return rev; }
            var p = m.Verts[v]; var da = m.Verts[qa] - p; var db = m.Verts[qb] - p;
            var pts = new List<int>();
            for (int k = 1; k < segments; k++)
            {
                float t = k / (float)segments * MathF.PI * 0.5f;
                pts.Add(m.AddVertex(p + da * (1 - MathF.Sin(t)) + db * (1 - MathF.Cos(t))));
            }
            arcs[(v, qa, qb)] = pts;
            return pts;
        }
        bool HasArc(int v, int qa, int qb) => arcs.ContainsKey((v, qa, qb)) || arcs.ContainsKey((v, qb, qa));
        // faces at a chamfered vertex with no chamfered edge of their own: the vertex becomes the copies of the two
        // neighbours across its edges (the classic extra corner a chamfer gives the side face)
        var faceAcross = new Func<int, int, int, int>((fi, a, b) => vf[a].Intersect(vf[b]).FirstOrDefault(g => g != fi, -1));
        for (int fi = 0; fi < fcount; fi++)
        {
            var f = m.Faces[fi]; int n = f.V.Length;
            var nvList = new List<int>(); var uvList = new List<Vector2>(); var colList = new List<Vector4>();
            bool changed = false;
            for (int c = 0; c < n; c++)
            {
                int v = f.V[c];
                if (!touched.Contains(v) || copyOf.ContainsKey((fi, v))) { nvList.Add(v); uvList.Add(f.UV[c]); colList.Add(f.Col[c]); continue; }
                int prev = f.V[(c + n - 1) % n], next = f.V[(c + 1) % n];
                // the original neighbours are found from the original vertex ids (copies replaced only v)
                int gp = faceAcross(fi, OrigOf(prev), v), gn = faceAcross(fi, v, OrigOf(next));
                if (kept.Contains(v))
                {
                    // the copies sit on this face's edges, the vertex stays
                    bool hp = gp >= 0 && copyOf.ContainsKey((gp, v)), hn = gn >= 0 && copyOf.ContainsKey((gn, v));
                    if (hp) { nvList.Add(copyOf[(gp, v)]); uvList.Add(f.UV[c]); colList.Add(f.Col[c]); }
                    nvList.Add(v); uvList.Add(f.UV[c]); colList.Add(f.Col[c]);
                    if (hn) { nvList.Add(copyOf[(gn, v)]); uvList.Add(f.UV[c]); colList.Add(f.Col[c]); }
                    changed |= hp || hn;
                    continue;
                }
                bool any = false;
                if (gp >= 0 && copyOf.TryGetValue((gp, v), out var cp)) { nvList.Add(cp); uvList.Add(f.UV[c]); colList.Add(f.Col[c]); any = true; }
                if (gn >= 0 && copyOf.TryGetValue((gn, v), out var cn) && (!any || cn != nvList[^1]))
                {
                    if (any && sel.Contains(EdgeKey.Make(v, OrigOf(prev))) == false && gp >= 0 && SharesChamferedEdge(gp, gn, v))
                        foreach (var ap in Arc(v, nvList[^1], cn)) { nvList.Add(ap); uvList.Add(f.UV[c]); colList.Add(f.Col[c]); }
                    nvList.Add(cn); uvList.Add(f.UV[c]); colList.Add(f.Col[c]); any = true;
                }
                if (!any) { nvList.Add(v); uvList.Add(f.UV[c]); colList.Add(f.Col[c]); } else changed = true;
            }
            if (changed) { f.V = nvList.ToArray(); f.UV = uvList.ToArray(); f.Col = colList.ToArray(); }
        }
        int OrigOf(int x) => origOfCopy.TryGetValue(x, out var o) ? o : x;
        // two faces meeting along a chamfered edge at v (their copies of v are the two sides of that strip)
        bool SharesChamferedEdge(int fa, int fb, int v)
        {
            foreach (var e in sel)
            {
                if (!e.Has(v)) continue;
                var fs = vf[e.A].Intersect(vf[e.B]).ToList();
                if (fs.Count == 2 && fs.Contains(fa) && fs.Contains(fb)) return true;
            }
            return false;
        }
        // strips: one quad per chamfered edge between the copies of its two faces
        foreach (var e in sel)
        {
            var fs = vf[e.A].Intersect(vf[e.B]).ToList();
            if (fs.Count != 2) continue;
            int f1 = fs[0], f2 = fs[1];
            if (!copyOf.TryGetValue((f1, e.A), out var a1) || !copyOf.TryGetValue((f1, e.B), out var b1) ||
                !copyOf.TryGetValue((f2, e.A), out var a2) || !copyOf.TryGetValue((f2, e.B), out var b2)) continue;
            var F1 = m.Faces[f1]; int ia = Array.IndexOf(F1.V, a1); bool f1AB = F1.V[(ia + 1) % F1.V.Length] == b1;
            if (segments == 1)
            {
                // flat strip, closing on a kept end vertex when the chamfer stops there
                var endA = kept.Contains(e.A) ? new List<int> { a1, e.A, a2 } : new List<int> { a1, a2 };
                var endB = kept.Contains(e.B) ? new List<int> { b1, e.B, b2 } : new List<int> { b1, b2 };
                var poly = f1AB ? endA.Concat(Enumerable.Reverse(endB)) : endB.Concat(Enumerable.Reverse(endA));
                var sf = m.AddFace(poly.ToArray(), F1.MatId);
                for (int i = 0; i < sf.Col.Length; i++) sf.Col[i] = F1.Col[0];
                continue;
            }
            List<int> Side(int end, int q1, int q2)
            {
                var l = new List<int> { q1 };
                if (kept.Contains(end)) for (int k = 1; k < segments; k++) l.Add(end); // the profile shrinks to the vertex
                else l.AddRange(Arc(end, q1, q2));
                l.Add(q2); return l;
            }
            var As = Side(e.A, a1, a2);
            var Bs = Side(e.B, b1, b2);
            for (int k = 0; k + 1 < As.Count; k++)
            {
                var nf = f1AB ? m.AddFace(new[] { Bs[k], As[k], As[k + 1], Bs[k + 1] }, F1.MatId) : m.AddFace(new[] { As[k], Bs[k], Bs[k + 1], As[k + 1] }, F1.MatId);
                nf.Smooth = segments > 1 ? 1u : 0u;
                for (int i = 0; i < 4; i++) nf.Col[i] = F1.Col[0];
            }
        }
        // caps where two or more chamfered edges meet: the ring of copies around the vertex
        foreach (var v in touched)
        {
            if (sel.Count(e => e.Has(v)) < 2 || kept.Contains(v)) continue;
            var ring = vf[v].Where(fi => copyOf.ContainsKey((fi, v))).Select(fi => copyOf[(fi, v)]).Distinct().ToList();
            if (ring.Count < 3) continue;
            var c = Vector3.Zero; foreach (var r in ring) c += m.Verts[r]; c /= ring.Count;
            var nrm = Vector3.Zero; foreach (var fi in vf[v]) nrm += m.FaceNormal(fi);
            if (nrm.LengthSquared() < 1e-12f) continue;
            nrm = Vector3.Normalize(nrm);
            Triangulator.Basis(nrm, out var u, out var w);
            float Ang(int x) => MathF.Atan2(Vector3.Dot(m.Verts[x] - c, w), Vector3.Dot(m.Verts[x] - c, u));
            ring.Sort((x, y) => Ang(x).CompareTo(Ang(y)));
            if (segments > 1)
            {
                var full = new List<int>();
                for (int i = 0; i < ring.Count; i++)
                {
                    int x = ring[i], y = ring[(i + 1) % ring.Count];
                    full.Add(x);
                    if (HasArc(v, x, y)) full.AddRange(Arc(v, x, y));
                }
                ring = full;
            }
            var cap = m.AddFace(ring.ToArray(), m.Faces[vf[v][0]].MatId);
            for (int i = 0; i < cap.Col.Length; i++) cap.Col[i] = m.Faces[vf[v][0]].Col[0];
        }
        // copies that landed on the same spot (two faces sliding down the same edge) become one vertex
        var weldSet = new HashSet<int>(copyOf.Values); foreach (var l in arcs.Values) foreach (var x in l) weldSet.Add(x);
        WeldByDistance(m, 1e-5f, weldSet);
        m.RemoveUnusedVertices();
        m.CleanFaces();
    }

    // ---------------------------------------------------------------- bridge

    /// <summary>
    /// 3ds Max Bridge (polygons): removes two faces with the same number of corners and joins their outlines
    /// with quads (the closest corner pairing, opposite winding).
    /// </summary>
    public static bool BridgeFaces(PolyMesh m, int fa, int fb)
    {
        if (fa == fb || fa < 0 || fb < 0 || fa >= m.Faces.Count || fb >= m.Faces.Count) return false;
        var A = m.Faces[fa]; var B = m.Faces[fb];
        if (A.V.Length != B.V.Length) return false;
        int n = A.V.Length;
        // B walks the other way when seen from A: try every rotation of reversed B, keep the shortest total length
        var rb = B.V.Reverse().ToArray();
        int best = 0; float bestLen = float.MaxValue;
        for (int s = 0; s < n; s++)
        {
            float len = 0; for (int i = 0; i < n; i++) len += Vector3.Distance(m.Verts[A.V[i]], m.Verts[rb[(i + s) % n]]);
            if (len < bestLen) { bestLen = len; best = s; }
        }
        int mat = A.MatId;
        var av = A.V.ToArray();
        foreach (var f in new[] { fa, fb }.OrderByDescending(x => x)) m.Faces.RemoveAt(f);
        for (int i = 0; i < n; i++)
        {
            int a0 = av[i], a1 = av[(i + 1) % n], b0 = rb[(i + best) % n], b1 = rb[(i + 1 + best) % n];
            m.AddFace(new[] { a0, a1, b1, b0 }, mat);
        }
        return true;
    }

    /// <summary>Blender Bridge Edge Loops: joins two selected border loops with the same vertex count.</summary>
    public static bool BridgeLoops(PolyMesh m, ICollection<EdgeKey> edges)
    {
        var loops = SplitLoops(edges);
        if (loops.Count != 2 || loops[0].Count != loops[1].Count || loops[0].Count < 3) return false;
        var a = loops[0]; var b = loops[1]; int n = a.Count;
        // make b run the opposite way of a (faces between them wind consistently) and align the start
        float Score(List<int> bb, int s) { float l = 0; for (int i = 0; i < n; i++) l += Vector3.Distance(m.Verts[a[i]], m.Verts[bb[(i + s) % n]]); return l; }
        var br = Enumerable.Reverse(b).ToList();
        (List<int> bb, int s, float sc) best = (b, 0, float.MaxValue);
        foreach (var cand in new[] { b, br }) for (int s = 0; s < n; s++) { float sc = Score(cand, s); if (sc < best.sc) best = (cand, s, sc); }
        int mat = m.Faces.Count > 0 ? m.Faces[0].MatId : 0;
        var added = new List<Face>();
        for (int i = 0; i < n; i++)
        {
            int a0 = a[i], a1 = a[(i + 1) % n], b0 = best.bb[(i + best.s) % n], b1 = best.bb[(i + 1 + best.s) % n];
            added.Add(m.AddFace(new[] { a0, a1, b1, b0 }, mat));
        }
        // face the same way as the neighbouring geometry (border edges must be walked opposite to their face)
        var em = m.BuildEdgeMap();
        var first = added[0]; var ek = EdgeKey.Make(first.V[0], first.V[1]);
        if (em.TryGetValue(ek, out var users))
        {
            var other = users.Select(i => m.Faces[i]).FirstOrDefault(f => f != first);
            if (other != null)
            {
                int i0 = Array.IndexOf(other.V, first.V[0]);
                bool sameDir = other.V[(i0 + 1) % other.V.Length] == first.V[1];
                if (sameDir) foreach (var f in added) f.Flip();
            }
        }
        return true;
    }

    /// <summary>Groups edges into closed loops (vertex order).</summary>
    public static List<List<int>> SplitLoops(IEnumerable<EdgeKey> edges)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (var e in edges) { (adj.TryGetValue(e.A, out var la) ? la : adj[e.A] = new()).Add(e.B); (adj.TryGetValue(e.B, out var lb) ? lb : adj[e.B] = new()).Add(e.A); }
        var seen = new HashSet<int>(); var res = new List<List<int>>();
        foreach (var start in adj.Keys)
        {
            if (seen.Contains(start)) continue;
            var loop = new List<int> { start }; seen.Add(start);
            int prev = -1, cur = start;
            while (true)
            {
                int nx = adj[cur].FirstOrDefault(x => x != prev && !seen.Contains(x), -1);
                if (nx < 0) break;
                loop.Add(nx); seen.Add(nx); prev = cur; cur = nx;
            }
            res.Add(loop);
        }
        return res;
    }

    // ---------------------------------------------------------------- slice

    /// <summary>
    /// Cuts polygons by a plane (3ds Max Slice Plane / QuickSlice, Blender Knife with a straight line): every face the
    /// plane crosses is split in two along it, UVs and colours interpolated. Returns the new edges.
    /// </summary>
    public static HashSet<EdgeKey> Slice(PolyMesh m, Vector3 planePoint, Vector3 planeNormal, ISet<int> faces = null)
    {
        planeNormal = Vector3.Normalize(planeNormal);
        float D(int v) => Vector3.Dot(m.Verts[v] - planePoint, planeNormal);
        var cut = new Dictionary<EdgeKey, int>();
        var newEdges = new HashSet<EdgeKey>();
        int count = m.Faces.Count;
        const float eps = 1e-5f;
        for (int fi = 0; fi < count; fi++)
        {
            if (faces != null && !faces.Contains(fi)) continue;
            var f = m.Faces[fi]; int n = f.V.Length;
            var d = f.V.Select(D).ToArray();
            if (d.All(x => x >= -eps) || d.All(x => x <= eps)) continue;
            // walk the polygon, inserting a point on each crossing edge
            var pts = new List<(int V, Vector2 UV, Vector4 Col, float D)>();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                pts.Add((f.V[i], f.UV[i], f.Col[i], d[i]));
                if ((d[i] > eps && d[j] < -eps) || (d[i] < -eps && d[j] > eps))
                {
                    float t = d[i] / (d[i] - d[j]);
                    var k = EdgeKey.Make(f.V[i], f.V[j]);
                    if (!cut.TryGetValue(k, out var nv)) { nv = m.AddVertex(Vector3.Lerp(m.Verts[f.V[i]], m.Verts[f.V[j]], t)); cut[k] = nv; }
                    pts.Add((nv, Vector2.Lerp(f.UV[i], f.UV[j], t), Vector4.Lerp(f.Col[i], f.Col[j], t), 0));
                }
            }
            var pos = pts.Where(p => p.D >= -eps).ToList(); var neg = pts.Where(p => p.D <= eps).ToList();
            if (pos.Count < 3 || neg.Count < 3) continue;
            Face Make(List<(int V, Vector2 UV, Vector4 Col, float D)> l)
            {
                var nf = new Face(l.Select(p => p.V).ToArray(), f.MatId, f.Smooth) { UV = l.Select(p => p.UV).ToArray(), Col = l.Select(p => p.Col).ToArray() };
                return nf;
            }
            m.Faces[fi] = Make(pos);
            m.Faces.Add(Make(neg));
            var onPlane = pts.Where(p => MathF.Abs(p.D) <= eps).Select(p => p.V).Distinct().ToList();
            if (onPlane.Count == 2) newEdges.Add(EdgeKey.Make(onPlane[0], onPlane[1]));
        }
        return newEdges;
    }
}
