using System.Numerics;

namespace MoloMax.Core.Mesh;

public static partial class MeshOps
{
    /// <summary>
    /// Connect: splits each selected edge at its middle and links the new points across every
    /// face that has two selected edges (select a ring on a quad strip → one new loop).
    /// Returns the new edges.
    /// </summary>
    public static HashSet<EdgeKey> ConnectEdges(PolyMesh m, ISet<EdgeKey> edges)
    {
        var result = new HashSet<EdgeKey>();
        if (edges == null || edges.Count == 0) return result;
        var mid = new Dictionary<EdgeKey, int>();
        foreach (var e in edges) mid[e] = m.AddVertex((m.Verts[e.A] + m.Verts[e.B]) * 0.5f);

        var outFaces = new List<Face>(m.Faces.Count + edges.Count);
        foreach (var f in m.Faces)
        {
            int n = f.V.Length;
            var v = new List<int>(); var uv = new List<Vector2>(); var col = new List<Vector4>();
            var cut = new List<int>(); // positions (in the new polygon) of inserted midpoints
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                v.Add(f.V[i]); uv.Add(f.UV[i]); col.Add(f.Col[i]);
                if (mid.TryGetValue(EdgeKey.Make(f.V[i], f.V[j]), out var mv))
                {
                    cut.Add(v.Count);
                    v.Add(mv); uv.Add((f.UV[i] + f.UV[j]) * 0.5f); col.Add((f.Col[i] + f.Col[j]) * 0.5f);
                }
            }
            if (cut.Count < 2)
            {
                f.V = v.ToArray(); f.UV = uv.ToArray(); f.Col = col.ToArray();
                outFaces.Add(f);
                continue;
            }
            // Split the (now longer) polygon along consecutive pairs of cuts. With two cuts this is
            // one clean split; with more, cuts are paired in order around the face.
            var pieces = new List<(List<int> v, List<Vector2> uv, List<Vector4> col)> { (v, uv, col) };
            for (int k = 0; k + 1 < cut.Count; k += 2)
            {
                // re-locate the two cut vertices inside the piece that contains both
                int va = v[cut[k]], vb = v[cut[k + 1]];
                for (int p = 0; p < pieces.Count; p++)
                {
                    var (pv, puv, pcol) = pieces[p];
                    int ia = pv.IndexOf(va), ib = pv.IndexOf(vb);
                    if (ia < 0 || ib < 0) continue;
                    if (ia > ib) (ia, ib) = (ib, ia);
                    if (ib - ia < 2 && !(ia == 0 && ib == pv.Count - 1)) break; // adjacent: nothing to cut
                    var a1 = new List<int>(); var u1 = new List<Vector2>(); var c1 = new List<Vector4>();
                    var a2 = new List<int>(); var u2 = new List<Vector2>(); var c2 = new List<Vector4>();
                    for (int t = ia; t <= ib; t++) { a1.Add(pv[t]); u1.Add(puv[t]); c1.Add(pcol[t]); }
                    for (int t = ib; t != ia; t = (t + 1) % pv.Count) { a2.Add(pv[t]); u2.Add(puv[t]); c2.Add(pcol[t]); }
                    a2.Add(pv[ia]); u2.Add(puv[ia]); c2.Add(pcol[ia]);
                    pieces[p] = (a1, u1, c1);
                    pieces.Add((a2, u2, c2));
                    result.Add(EdgeKey.Make(va, vb));
                    break;
                }
            }
            foreach (var (pv, puv, pcol) in pieces)
            {
                if (pv.Count < 3) continue;
                outFaces.Add(new Face(pv.ToArray(), f.MatId, f.Smooth) { UV = puv.ToArray(), Col = pcol.ToArray() });
            }
        }
        m.Faces = outFaces;
        return result;
    }

    /// <summary>
    /// Remove edges: merges the two polygons on each side into one (3ds Max Backspace).
    /// With <paramref name="removeVertices"/>, vertices left in the middle of a straight run are dropped too (Ctrl+Backspace).
    /// </summary>
    public static void RemoveEdges(PolyMesh m, ISet<EdgeKey> edges, bool removeVertices = false)
    {
        if (edges == null) return;
        var touched = new HashSet<int>();
        foreach (var e in edges)
        {
            int f1 = -1, f2 = -1, i1 = -1, i2 = -1;
            for (int fi = 0; fi < m.Faces.Count && (f1 < 0 || f2 < 0); fi++)
            {
                var f = m.Faces[fi]; int n = f.V.Length;
                for (int i = 0; i < n; i++)
                {
                    int a = f.V[i], b = f.V[(i + 1) % n];
                    if (a == e.A && b == e.B && f1 < 0) { f1 = fi; i1 = i; }
                    else if (a == e.B && b == e.A && f2 < 0) { f2 = fi; i2 = i; }
                }
            }
            if (f1 < 0 || f2 < 0 || f1 == f2) continue;
            var F1 = m.Faces[f1]; var F2 = m.Faces[f2];
            // F1 has A→B at i1. Walk F1 from B round to A, then F2 from after A round to before B.
            var v = new List<int>(); var uv = new List<Vector2>(); var col = new List<Vector4>();
            int n1 = F1.V.Length, n2 = F2.V.Length;
            for (int t = 0; t < n1; t++) { int k = (i1 + 1 + t) % n1; v.Add(F1.V[k]); uv.Add(F1.UV[k]); col.Add(F1.Col[k]); }
            // F2 has B→A at i2; A sits at i2+1. Continue after A until just before B.
            for (int t = 2; t < n2; t++) { int k = (i2 + t) % n2; v.Add(F2.V[k]); uv.Add(F2.UV[k]); col.Add(F2.Col[k]); }
            if (v.Distinct().Count() != v.Count) continue; // would create a non-simple polygon
            var merged = new Face(v.ToArray(), F1.MatId, F1.Smooth | F2.Smooth) { UV = uv.ToArray(), Col = col.ToArray() };
            int hi = Math.Max(f1, f2), lo = Math.Min(f1, f2);
            m.Faces.RemoveAt(hi); m.Faces.RemoveAt(lo);
            m.Faces.Add(merged);
            touched.Add(e.A); touched.Add(e.B);
        }
        if (removeVertices && touched.Count > 0) DissolveStraightVertices(m, touched);
    }

    /// <summary>Removes vertices that only sit on a straight line between two neighbours in every face using them.</summary>
    public static void DissolveStraightVertices(PolyMesh m, ISet<int> candidates)
    {
        var vf = m.BuildVertexFaces();
        var drop = new HashSet<int>();
        foreach (var v in candidates)
        {
            if (v >= vf.Length || vf[v].Count == 0 || vf[v].Count > 2) continue;
            bool straight = true;
            foreach (var fi in vf[v])
            {
                var f = m.Faces[fi]; int i = f.IndexOf(v), n = f.V.Length;
                if (n <= 3) { straight = false; break; }
                var a = m.Verts[f.V[(i - 1 + n) % n]]; var b = m.Verts[f.V[(i + 1) % n]];
                var d1 = Vector3.Normalize(m.Verts[v] - a); var d2 = Vector3.Normalize(b - m.Verts[v]);
                if (Vector3.Dot(d1, d2) < 0.9999f) { straight = false; break; }
            }
            if (straight) drop.Add(v);
        }
        if (drop.Count == 0) return;
        foreach (var f in m.Faces)
        {
            if (!f.V.Any(drop.Contains)) continue;
            var keep = Enumerable.Range(0, f.V.Length).Where(i => !drop.Contains(f.V[i])).ToArray();
            if (keep.Length < 3) continue;
            f.V = keep.Select(i => f.V[i]).ToArray(); f.UV = keep.Select(i => f.UV[i]).ToArray(); f.Col = keep.Select(i => f.Col[i]).ToArray();
        }
        m.RemoveUnusedVertices();
    }

    // ---------------------------------------------------------------- loop / ring (quad topology)

    /// <summary>Edge loop through a quad mesh, stopping at poles (valence ≠ 4) and borders.</summary>
    public static HashSet<EdgeKey> EdgeLoop(PolyMesh m, EdgeKey start)
    {
        var em = m.BuildEdgeMap();
        var res = new HashSet<EdgeKey> { start };
        var nb = new Dictionary<int, List<int>>();
        foreach (var e in em.Keys)
        {
            (nb.TryGetValue(e.A, out var la) ? la : nb[e.A] = new List<int>()).Add(e.B);
            (nb.TryGetValue(e.B, out var lb) ? lb : nb[e.B] = new List<int>()).Add(e.A);
        }
        void Walk(int from, int at)
        {
            var cur = EdgeKey.Make(from, at);
            for (int guard = 0; guard < em.Count; guard++)
            {
                if (!nb.TryGetValue(at, out var around) || around.Count != 4) return;
                var curFaces = em[cur];
                int nextV = -1;
                foreach (var o in around)
                {
                    if (o == from) continue;
                    var cand = EdgeKey.Make(at, o);
                    if (em[cand].Any(curFaces.Contains)) continue; // shares a face: it's a side edge
                    nextV = o; break;
                }
                if (nextV < 0) return;
                var ne = EdgeKey.Make(at, nextV);
                if (!res.Add(ne)) return;
                from = at; at = nextV; cur = ne;
            }
        }
        Walk(start.A, start.B);
        Walk(start.B, start.A);
        return res;
    }

    /// <summary>Edge ring: the opposite edges across consecutive quads.</summary>
    public static HashSet<EdgeKey> EdgeRing(PolyMesh m, EdgeKey start)
    {
        var em = m.BuildEdgeMap();
        var res = new HashSet<EdgeKey> { start };
        void Walk(EdgeKey e, int viaFace)
        {
            for (int guard = 0; guard < em.Count; guard++)
            {
                var f = m.Faces[viaFace];
                if (f.V.Length != 4) return;
                int i = -1;
                for (int k = 0; k < 4; k++) if (EdgeKey.Make(f.V[k], f.V[(k + 1) % 4]) == e) { i = k; break; }
                if (i < 0) return;
                var opp = EdgeKey.Make(f.V[(i + 2) % 4], f.V[(i + 3) % 4]);
                if (!res.Add(opp)) return;
                var fs = em[opp];
                int nextFace = fs.FirstOrDefault(x => x != viaFace, -1);
                if (nextFace < 0) return;
                e = opp; viaFace = nextFace;
            }
        }
        foreach (var fi in em[start]) Walk(start, fi);
        return res;
    }

    /// <summary>Open (border) edges: used by a single face.</summary>
    public static HashSet<EdgeKey> BorderEdges(PolyMesh m)
    {
        return m.BuildEdgeMap().Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToHashSet();
    }

    /// <summary>All border edges of the open borders that the given edges belong to.</summary>
    public static HashSet<EdgeKey> BorderLoop(PolyMesh m, EdgeKey start)
    {
        var border = BorderEdges(m);
        var res = new HashSet<EdgeKey>();
        if (!border.Contains(start)) return res;
        var st = new Stack<EdgeKey>(); st.Push(start); res.Add(start);
        while (st.Count > 0)
        {
            var e = st.Pop();
            foreach (var o in border)
                if (!res.Contains(o) && (o.Has(e.A) || o.Has(e.B))) { res.Add(o); st.Push(o); }
        }
        return res;
    }
}
