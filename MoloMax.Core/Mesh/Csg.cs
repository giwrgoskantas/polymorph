using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>
/// Booleans (union, subtract, intersect) on closed meshes with a BSP tree (the csg.js method). Good for blockout
/// work: doors and windows cut in walls, merged volumes. UVs and colours are interpolated on the cut polygons.
/// </summary>
public static class Csg
{
    public enum Op { Union, Subtract, Intersect }

    struct Vtx { public Vector3 P; public Vector2 UV; public Vector4 Col; public Vtx Lerp(Vtx o, float t) => new() { P = Vector3.Lerp(P, o.P, t), UV = Vector2.Lerp(UV, o.UV, t), Col = Vector4.Lerp(Col, o.Col, t) }; }

    sealed class Poly
    {
        public List<Vtx> V; public Vector3 N; public float W; public int Mat; public uint Smooth;
        /// <summary>Face of A it comes from (-1 = B), whether it was split, whether it is that whole face (not a triangle of it).</summary>
        public int Src = -1; public bool Cut, Whole;
        /// <summary>Source face group: A face index, or 1 000 000 + B face index.</summary>
        public int Grp = -1;
        public Poly(List<Vtx> v, int mat, uint smooth) { V = v; Mat = mat; Smooth = smooth; var n = Vector3.Cross(v[1].P - v[0].P, v[2].P - v[0].P); for (int i = 2; i + 1 < v.Count && n.LengthSquared() < 1e-14f; i++) n = Vector3.Cross(v[i].P - v[0].P, v[i + 1].P - v[0].P); N = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ; W = Vector3.Dot(N, v[0].P); }
        public Poly Flipped() { var v = new List<Vtx>(V); v.Reverse(); return new Poly(v, Mat, Smooth) { Src = Src, Cut = Cut, Whole = Whole, Grp = Grp }; }
    }

    const float Eps = 1e-5f;
    const int MaxDepth = 20000; // normal trees can be deep (one polygon per level); 256 MB of stack holds this easily

    static void Split(Vector3 n, float w, Poly p, List<Poly> coF, List<Poly> coB, List<Poly> front, List<Poly> back)
    {
        const int CO = 0, FR = 1, BK = 2, SP = 3;
        int type = 0; var types = new int[p.V.Count];
        for (int i = 0; i < p.V.Count; i++) { float t = Vector3.Dot(n, p.V[i].P) - w; int ty = t < -Eps ? BK : t > Eps ? FR : CO; type |= ty; types[i] = ty; }
        switch (type)
        {
            case CO: (Vector3.Dot(n, p.N) > 0 ? coF : coB).Add(p); break;
            case FR: front.Add(p); break;
            case BK: back.Add(p); break;
            default:
                var f = new List<Vtx>(); var b = new List<Vtx>();
                for (int i = 0; i < p.V.Count; i++)
                {
                    int j = (i + 1) % p.V.Count; int ti = types[i], tj = types[j]; var vi = p.V[i]; var vj = p.V[j];
                    if (ti != BK) f.Add(vi);
                    if (ti != FR) b.Add(vi);
                    if ((ti | tj) == SP)
                    {
                        float t = (w - Vector3.Dot(n, vi.P)) / Vector3.Dot(n, vj.P - vi.P);
                        var v = vi.Lerp(vj, t); f.Add(v); b.Add(v);
                    }
                }
                if (f.Count >= 3) front.Add(new Poly(f, p.Mat, p.Smooth) { N = p.N, W = p.W, Src = p.Src, Grp = p.Grp, Cut = true });
                if (b.Count >= 3) back.Add(new Poly(b, p.Mat, p.Smooth) { N = p.N, W = p.W, Src = p.Src, Grp = p.Grp, Cut = true });
                break;
        }
    }

    sealed class Node
    {
        Vector3 n; float w; bool hasPlane;
        Node front, back; List<Poly> polys = new();
        public Node(List<Poly> list = null) { if (list != null) Build(list); }
        public void Invert()
        {
            for (int i = 0; i < polys.Count; i++) polys[i] = polys[i].Flipped();
            n = -n; w = -w;
            front?.Invert(); back?.Invert();
            (front, back) = (back, front);
        }
        public List<Poly> ClipPolygons(List<Poly> list)
        {
            if (!hasPlane) return new List<Poly>(list);
            var f = new List<Poly>(); var b = new List<Poly>();
            foreach (var p in list) Split(n, w, p, f, b, f, b);
            f = front != null ? front.ClipPolygons(f) : f;
            b = back != null ? back.ClipPolygons(b) : new List<Poly>();
            f.AddRange(b); return f;
        }
        public void ClipTo(Node bsp) { polys = bsp.ClipPolygons(polys); front?.ClipTo(bsp); back?.ClipTo(bsp); }
        public List<Poly> All() { var l = new List<Poly>(polys); if (front != null) l.AddRange(front.All()); if (back != null) l.AddRange(back.All()); return l; }
        public void Build(List<Poly> list) => Build(list, 0);

        void Build(List<Poly> list, int depth)
        {
            if (list.Count == 0) return;
            // far too deep = rounding keeps splitting the same slivers: keep them here instead of crashing
            if (depth > MaxDepth) { polys.AddRange(list); return; }
            if (!hasPlane) { n = list[0].N; w = list[0].W; hasPlane = true; }
            var f = new List<Poly>(); var b = new List<Poly>();
            foreach (var p in list) Split(n, w, p, polys, polys, f, b);
            if (f.Count > 0) { front ??= new Node(); front.Build(f, depth + 1); }
            if (b.Count > 0) { back ??= new Node(); back.Build(b, depth + 1); }
        }
    }

    static List<Poly> ToPolys(PolyMesh m, Matrix4x4 world)
    {
        var res = new List<Poly>();
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
            {
                var v = new List<Vtx>();
                foreach (var k in new[] { a, b, c }) v.Add(new Vtx { P = Vector3.Transform(m.Verts[f.V[k]], world), UV = f.UV[k], Col = f.Col[k] });
                if (Vector3.Cross(v[1].P - v[0].P, v[2].P - v[0].P).LengthSquared() > 1e-14f) res.Add(new Poly(v, f.MatId, f.Smooth) { Grp = 1_000_000 + fi });
            }
        }
        return res;
    }

    /// <summary>Boolean of mesh A (world matrix wa) with mesh B (wb). The result is in A's local space.</summary>
    public static PolyMesh Apply(PolyMesh a, Matrix4x4 wa, PolyMesh b, Matrix4x4 wb, Op op)
    {
        // on its own thread with a big stack: a deep tree can never take the editor down
        PolyMesh res = null; Exception err = null;
        var t = new Thread(() => { try { res = ApplyCore(a, wa, b, wb, op); } catch (Exception e) { err = e; } }, 256 * 1024 * 1024);
        t.Start(); t.Join();
        if (err != null) throw new InvalidOperationException("Boolean failed: " + err.Message, err);
        return res;
    }

    static PolyMesh ApplyCore(PolyMesh a, Matrix4x4 wa, PolyMesh b, Matrix4x4 wb, Op op)
    {
        // work around the middle of A: GTA coordinates (thousands of metres) would eat the float precision
        var centre = Vector3.Zero;
        if (a.Verts.Count > 0)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in a.Verts) { var p = Vector3.Transform(v, wa); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            centre = (mn + mx) * 0.5f;
        }
        var toLocal = Matrix4x4.CreateTranslation(-centre);
        var waL = wa * toLocal;
        var polysB = ToPolys(b, wb * toLocal);
        foreach (var p in polysB) p.Cut = true;
        // only the faces of A near B take part: the rest can't change and comes out exactly as it was
        var bmn = new Vector3(float.MaxValue); var bmx = new Vector3(float.MinValue);
        foreach (var p in polysB) foreach (var v in p.V) { bmn = Vector3.Min(bmn, v.P); bmx = Vector3.Max(bmx, v.P); }
        bmn -= new Vector3(1e-3f); bmx += new Vector3(1e-3f);
        var near = new List<Poly>(); var far = new List<int>();
        var aWorld = a.Verts.Select(v => Vector3.Transform(v, waL)).ToArray();
        for (int fi = 0; fi < a.Faces.Count; fi++)
        {
            var f = a.Faces[fi];
            var fmn = new Vector3(float.MaxValue); var fmx = new Vector3(float.MinValue);
            foreach (var vi in f.V) { fmn = Vector3.Min(fmn, aWorld[vi]); fmx = Vector3.Max(fmx, aWorld[vi]); }
            bool overlap = fmn.X <= bmx.X && fmx.X >= bmn.X && fmn.Y <= bmx.Y && fmx.Y >= bmn.Y && fmn.Z <= bmx.Z && fmx.Z >= bmn.Z;
            if (!overlap) { far.Add(fi); continue; }
            near.AddRange(FacePolys(a, f, fi, aWorld));
        }
        var res = Run(a, b, near, polysB, far, op, waL, wa, centre, out var region);
        if (res == null || !res.IsClosedManifold(out _))
        {
            // the local cut didn't close (odd shapes): do it on the whole mesh
            var all = new List<Poly>();
            for (int fi = 0; fi < a.Faces.Count; fi++) all.AddRange(FacePolys(a, a.Faces[fi], fi, aWorld));
            var polysB2 = ToPolys(b, wb * toLocal); foreach (var p in polysB2) p.Cut = true;
            var full = Run(a, b, all, polysB2, new List<int>(), op, waL, wa, centre, out region);
            if (full != null) res = full;
        }
        return CsgClean.Clean(res, region); // straight faces like a hand cut, on the cut faces only
    }

    /// <summary>A face as one polygon when it is flat and convex (it can then come out untouched), else its triangles.</summary>
    static IEnumerable<Poly> FacePolys(PolyMesh m, Face f, int fi, Vector3[] world)
    {
        bool convex = f.V.Length >= 3 && IsFlatConvex(f.V.Select(v => world[v]).ToArray());
        if (convex)
        {
            var v = f.V.Select((vi, k) => new Vtx { P = world[vi], UV = f.UV[k], Col = f.Col[k] }).ToList();
            yield return new Poly(v, f.MatId, f.Smooth) { Src = fi, Grp = fi, Whole = true };
            yield break;
        }
        foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
        {
            var v = new List<Vtx>();
            foreach (var k in new[] { a, b, c }) v.Add(new Vtx { P = world[f.V[k]], UV = f.UV[k], Col = f.Col[k] });
            if (Vector3.Cross(v[1].P - v[0].P, v[2].P - v[0].P).LengthSquared() > 1e-14f) yield return new Poly(v, f.MatId, f.Smooth) { Src = fi, Grp = fi };
        }
    }

    static bool IsFlatConvex(Vector3[] p)
    {
        int n = p.Length; if (n < 3) return false;
        var nrm = Vector3.Zero;
        for (int i = 0; i < n; i++) nrm += Vector3.Cross(p[i], p[(i + 1) % n]);
        if (nrm.LengthSquared() < 1e-14f) return false;
        nrm = Vector3.Normalize(nrm); float d = Vector3.Dot(nrm, p[0]);
        for (int i = 0; i < n; i++)
        {
            if (MathF.Abs(Vector3.Dot(nrm, p[i]) - d) > 1e-5f) return false;
            var e1 = p[(i + 1) % n] - p[i]; var e2 = p[(i + 2) % n] - p[(i + 1) % n];
            if (Vector3.Dot(Vector3.Cross(e1, e2), nrm) < -1e-9f) return false;
        }
        return true;
    }

    static PolyMesh Run(PolyMesh a, PolyMesh b, List<Poly> nearA, List<Poly> polysB, List<int> far, Op op, Matrix4x4 waL, Matrix4x4 wa, Vector3 centre, out Dictionary<Face, int> region)
    {
        // region of each output face: Kept = untouched, else its source face (cleaning never mixes two source faces)
        var reg = new Dictionary<Face, int>(ReferenceEqualityComparer.Instance); region = reg;
        var A = new Node(nearA); var B = new Node(polysB);
        switch (op)
        {
            case Op.Union:
                A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert(); A.Build(B.All()); break;
            case Op.Subtract:
                A.Invert(); A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert(); A.Build(B.All()); A.Invert(); break;
            case Op.Intersect:
                A.Invert(); B.ClipTo(A); B.Invert(); A.ClipTo(B); B.ClipTo(A); A.Build(B.All()); A.Invert(); break;
        }
        Matrix4x4.Invert(wa, out var invA);
        var inv = Matrix4x4.CreateTranslation(centre) * invA;
        var res = new PolyMesh();
        Face CopyOriginal(Face f)
        {
            var idx = f.V.Select(v => res.AddVertex(a.Verts[v])).ToArray();
            var nf = res.AddFace(idx, f.MatId, f.Smooth); nf.UV = (Vector2[])f.UV.Clone(); nf.Col = (Vector4[])f.Col.Clone();
            if (f.Col1 != null) nf.Col1 = (Vector4[])f.Col1.Clone();
            return nf;
        }
        // faces that can't be touched (subtract / union keep them, intersect drops them)
        if (op != Op.Intersect) foreach (var fi in far) reg[CopyOriginal(a.Faces[fi])] = CsgClean.Kept;
        var outPolys = A.All();
        // whole faces of A that came through without a cut: written back exactly as they were
        var triCount = new Dictionary<int, int>(); var triUncut = new Dictionary<int, int>();
        foreach (var p in nearA) if (!p.Whole) triCount[p.Src] = triCount.GetValueOrDefault(p.Src) + 1;
        foreach (var p in outPolys) if (p.Src >= 0 && !p.Whole && !p.Cut) triUncut[p.Src] = triUncut.GetValueOrDefault(p.Src) + 1;
        var wholeDone = new HashSet<int>();
        var groups = new Dictionary<int, List<Poly>>();
        foreach (var p in outPolys)
        {
            bool sameSide = p.Src >= 0 && Vector3.Dot(p.N, a.FaceNormal(a.Faces[p.Src]) is var fn && fn.LengthSquared() > 0 ? Vector3.Normalize(Vector3.TransformNormal(fn, waL)) : p.N) > 0;
            if (p.Src >= 0 && !p.Cut && sameSide && (p.Whole || triUncut.GetValueOrDefault(p.Src) == triCount.GetValueOrDefault(p.Src)))
            {
                if (wholeDone.Add(p.Src)) reg[CopyOriginal(a.Faces[p.Src])] = CsgClean.Kept;
                continue;
            }
            (groups.TryGetValue(p.Grp, out var gl) ? gl : groups[p.Grp] = new List<Poly>()).Add(p);
        }
        // the pieces of each cut face put back together on their own (the BSP slivers become one polygon per face)
        foreach (var (grp, list) in groups)
        {
            var t = new PolyMesh();
            foreach (var p in list)
            {
                var idx = p.V.Select(v => t.AddVertex(Vector3.Transform(v.P, inv))).ToArray();
                var f = t.AddFace(idx, p.Mat, p.Smooth);
                f.UV = p.V.Select(v => v.UV).ToArray(); f.Col = p.V.Select(v => v.Col).ToArray();
            }
            MeshOps.WeldByDistance(t, 1e-4f);
            FixTJunctions(t, 1e-4f);
            MergeCoplanar(t);
            foreach (var f in t.Faces)
            {
                var idx = f.V.Select(v => res.AddVertex(t.Verts[v])).ToArray();
                var nf = res.AddFace(idx, f.MatId, f.Smooth); nf.UV = f.UV; nf.Col = f.Col;
                reg[nf] = grp;
            }
        }
        MeshOps.WeldByDistance(res, 1e-4f);
        FixTJunctions(res, 1e-4f);
        return res;
    }

    /// <summary>The BSP leaves flat faces cut in long slivers: neighbours on the same plane (same material) become one polygon again.</summary>
    public static void MergeCoplanar(PolyMesh m) => MergeCoplanar(m, null);

    public static void MergeCoplanar(PolyMesh m, Func<Face, bool> allow)
    {
        var n = new Vector3[m.Faces.Count]; var d = new float[m.Faces.Count];
        for (int i = 0; i < n.Length; i++) { n[i] = m.FaceNormal(i); d[i] = Vector3.Dot(n[i], m.Verts[m.Faces[i].V[0]]); }
        var side = new Dictionary<EdgeKey, (int F, int Count)>();
        var flat = new HashSet<EdgeKey>();
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            for (int i = 0; i < f.V.Length; i++)
            {
                var e = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]);
                if (!side.TryGetValue(e, out var s)) { side[e] = (fi, 1); continue; }
                side[e] = (s.F, s.Count + 1);
                int g = s.F;
                if (s.Count == 1 && g != fi && (allow == null || (allow(m.Faces[g]) && allow(f))) && m.Faces[g].MatId == f.MatId && Vector3.Dot(n[g], n[fi]) > 0.99999f && MathF.Abs(d[g] - d[fi]) < 1e-4f) flat.Add(e);
                else flat.Remove(e);
            }
        }
        flat.RemoveWhere(e => side[e].Count != 2);
        if (flat.Count > 0) MeshOps.RemoveEdges(m, flat, removeVertices: true);
    }

    /// <summary>
    /// BSP booleans leave T-junctions (a face split on one side of an edge but not on the other): the vertex of the
    /// split lies on the neighbour's edge. Inserting it into that edge closes the mesh again (no cracks in the game).
    /// </summary>
    public static void FixTJunctions(PolyMesh m, float eps)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var count = new Dictionary<EdgeKey, int>();
            foreach (var f in m.Faces)
                for (int i = 0; i < f.V.Length; i++) { var e = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]); count[e] = count.TryGetValue(e, out var c) ? c + 1 : 1; }
            var open = count.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
            if (open.Count == 0) return;
            var cand = open.SelectMany(e => new[] { e.A, e.B }).Distinct().ToArray();
            if ((long)open.Count * cand.Length > 50_000_000) return;
            bool changed = false;
            foreach (var f in m.Faces)
            {
                List<int> nv = null; List<Vector2> nuv = null; List<Vector4> ncol = null;
                int n = f.V.Length;
                for (int i = 0; i < n; i++)
                {
                    int a = f.V[i], b = f.V[(i + 1) % n];
                    List<(float T, int V)> on = null;
                    if (count[EdgeKey.Make(a, b)] == 1)
                    {
                        var pa = m.Verts[a]; var d = m.Verts[b] - pa; float len2 = d.LengthSquared();
                        if (len2 > eps * eps)
                            foreach (var c in cand)
                            {
                                if (c == a || c == b) continue;
                                float t = Vector3.Dot(m.Verts[c] - pa, d) / len2;
                                if (t <= 1e-4f || t >= 1 - 1e-4f) continue;
                                if (Vector3.DistanceSquared(pa + d * t, m.Verts[c]) > eps * eps) continue;
                                (on ??= new()).Add((t, c));
                            }
                    }
                    if (on == null && nv == null) continue;
                    if (nv == null) { nv = f.V.Take(i + 1).ToList(); nuv = f.UV.Take(i + 1).ToList(); ncol = f.Col.Take(i + 1).ToList(); }
                    else { nv.Add(a); nuv.Add(f.UV[i]); ncol.Add(f.Col[i]); }
                    if (on == null) continue;
                    int j = (i + 1) % n;
                    foreach (var (t, c) in on.OrderBy(x => x.T)) { nv.Add(c); nuv.Add(Vector2.Lerp(f.UV[i], f.UV[j], t)); ncol.Add(Vector4.Lerp(f.Col[i], f.Col[j], t)); }
                    changed = true;
                }
                if (nv != null && nv.Count > n) { f.V = nv.ToArray(); f.UV = nuv.ToArray(); f.Col = ncol.ToArray(); }
            }
            if (!changed) return;
        }
    }
}
