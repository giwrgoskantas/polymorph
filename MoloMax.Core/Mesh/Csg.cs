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
        public Poly(List<Vtx> v, int mat, uint smooth) { V = v; Mat = mat; Smooth = smooth; var n = Vector3.Cross(v[1].P - v[0].P, v[2].P - v[0].P); for (int i = 2; i + 1 < v.Count && n.LengthSquared() < 1e-14f; i++) n = Vector3.Cross(v[i].P - v[0].P, v[i + 1].P - v[0].P); N = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ; W = Vector3.Dot(N, v[0].P); }
        public Poly Flipped() { var v = new List<Vtx>(V); v.Reverse(); return new Poly(v, Mat, Smooth); }
    }

    const float Eps = 1e-5f;

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
                if (f.Count >= 3) front.Add(new Poly(f, p.Mat, p.Smooth) { N = p.N, W = p.W });
                if (b.Count >= 3) back.Add(new Poly(b, p.Mat, p.Smooth) { N = p.N, W = p.W });
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
        public void Build(List<Poly> list)
        {
            if (list.Count == 0) return;
            if (!hasPlane) { n = list[0].N; w = list[0].W; hasPlane = true; }
            var f = new List<Poly>(); var b = new List<Poly>();
            foreach (var p in list) Split(n, w, p, polys, polys, f, b);
            if (f.Count > 0) { front ??= new Node(); front.Build(f); }
            if (b.Count > 0) { back ??= new Node(); back.Build(b); }
        }
    }

    static List<Poly> ToPolys(PolyMesh m, Matrix4x4 world)
    {
        var res = new List<Poly>();
        foreach (var f in m.Faces)
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
            {
                var v = new List<Vtx>();
                foreach (var k in new[] { a, b, c }) v.Add(new Vtx { P = Vector3.Transform(m.Verts[f.V[k]], world), UV = f.UV[k], Col = f.Col[k] });
                if (Vector3.Cross(v[1].P - v[0].P, v[2].P - v[0].P).LengthSquared() > 1e-14f) res.Add(new Poly(v, f.MatId, f.Smooth));
            }
        return res;
    }

    /// <summary>Boolean of mesh A (world matrix wa) with mesh B (wb). The result is in A's local space.</summary>
    public static PolyMesh Apply(PolyMesh a, Matrix4x4 wa, PolyMesh b, Matrix4x4 wb, Op op)
    {
        var A = new Node(ToPolys(a, wa)); var B = new Node(ToPolys(b, wb));
        switch (op)
        {
            case Op.Union:
                A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert(); A.Build(B.All()); break;
            case Op.Subtract:
                A.Invert(); A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert(); A.Build(B.All()); A.Invert(); break;
            case Op.Intersect:
                A.Invert(); B.ClipTo(A); B.Invert(); A.ClipTo(B); B.ClipTo(A); A.Build(B.All()); A.Invert(); break;
        }
        Matrix4x4.Invert(wa, out var inv);
        var res = new PolyMesh();
        foreach (var p in A.All())
        {
            var idx = p.V.Select(v => res.AddVertex(Vector3.Transform(v.P, inv))).ToArray();
            var f = res.AddFace(idx, p.Mat, p.Smooth);
            f.UV = p.V.Select(v => v.UV).ToArray(); f.Col = p.V.Select(v => v.Col).ToArray();
        }
        MeshOps.WeldByDistance(res, 1e-4f);
        FixTJunctions(res, 1e-4f);
        MergeCoplanar(res);
        return res;
    }

    /// <summary>The BSP leaves flat faces cut in long slivers: neighbours on the same plane (same material) become one polygon again.</summary>
    public static void MergeCoplanar(PolyMesh m)
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
                if (s.Count == 1 && g != fi && m.Faces[g].MatId == f.MatId && Vector3.Dot(n[g], n[fi]) > 0.99999f && MathF.Abs(d[g] - d[fi]) < 1e-4f) flat.Add(e);
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
