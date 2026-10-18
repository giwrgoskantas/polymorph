using System.Numerics;

namespace MoloMax.Core.Mesh;

public static partial class MeshOps
{
    /// <summary>Copies these faces inside the same mesh (new vertices, shared between the copied faces; UVs, colours,
    /// materials and smoothing kept) — 3ds Max Shift+drag on polygons ("clone to element"). Returns the new face indices.</summary>
    public static List<int> CloneFaces(PolyMesh m, ISet<int> faces)
    {
        var map = new Dictionary<int, int>();
        var res = new List<int>();
        foreach (var fi in faces.Where(f => f >= 0 && f < m.Faces.Count).OrderBy(x => x).ToList())
        {
            var c = m.Faces[fi].Clone();
            for (int i = 0; i < c.V.Length; i++)
            {
                if (!map.TryGetValue(c.V[i], out var nv)) { nv = m.Verts.Count; m.Verts.Add(m.Verts[c.V[i]]); map[c.V[i]] = nv; }
                c.V[i] = nv;
            }
            res.Add(m.Faces.Count);
            m.Faces.Add(c);
        }
        return res;
    }

    /// <summary>
    /// 3ds Max Shift+drag on edges: each edge gets a copy, joined to it by a new quad (a strip of faces pulled out of
    /// the edges, the classic way to grow a border). The copies share their ends, so a chain of edges gives a connected
    /// strip. The quads follow the winding of the face next to each edge (no flipped faces). Returns the copied edges.
    /// </summary>
    public static HashSet<EdgeKey> CloneEdgesAsStrip(PolyMesh m, ISet<EdgeKey> edges)
    {
        var copy = new Dictionary<int, int>();
        int Dup(int v) { if (!copy.TryGetValue(v, out var nv)) { nv = m.Verts.Count; m.Verts.Add(m.Verts[v]); copy[v] = nv; } return nv; }
        // the face holding each edge and the direction it runs in that face
        var owner = new Dictionary<EdgeKey, (int Face, int CornerA, int CornerB, bool Forward)>();
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            for (int c = 0; c < f.V.Length; c++)
            {
                int a = f.V[c], b = f.V[(c + 1) % f.V.Length];
                var k = EdgeKey.Make(a, b);
                if (!edges.Contains(k) || owner.ContainsKey(k)) continue;
                owner[k] = (fi, c, (c + 1) % f.V.Length, a == k.A);
            }
        }
        var res = new HashSet<EdgeKey>();
        foreach (var e in edges)
        {
            if (!owner.TryGetValue(e, out var o)) continue;
            var f = m.Faces[o.Face];
            // the face runs a -> b; the new quad runs b -> a on the old edge, then out to the copies
            int a = f.V[o.CornerA], b = f.V[o.CornerB];
            int a2 = Dup(a), b2 = Dup(b);
            var q = new Face(new[] { b, a, a2, b2 }, f.MatId, f.Smooth);
            q.UV[0] = f.UV[o.CornerB]; q.UV[1] = f.UV[o.CornerA]; q.UV[2] = f.UV[o.CornerA]; q.UV[3] = f.UV[o.CornerB];
            q.Col[0] = f.Col[o.CornerB]; q.Col[1] = f.Col[o.CornerA]; q.Col[2] = f.Col[o.CornerA]; q.Col[3] = f.Col[o.CornerB];
            m.Faces.Add(q);
            res.Add(EdgeKey.Make(a2, b2));
        }
        return res;
    }

    /// <summary>The faces whose corners are all in this vertex set (what a vertex selection "holds").</summary>
    public static HashSet<int> FacesInside(PolyMesh m, ISet<int> verts)
    {
        var res = new HashSet<int>();
        for (int i = 0; i < m.Faces.Count; i++) if (m.Faces[i].V.All(verts.Contains)) res.Add(i);
        return res;
    }
}

public static partial class MeshOps
{
    /// <summary>The edges as chains of vertices: open chains (walked from an end) and closed loops.</summary>
    public static List<(List<int> Verts, bool Closed)> EdgeChains(IEnumerable<EdgeKey> edges)
    {
        var adj = new Dictionary<int, List<int>>();
        foreach (var e in edges) { (adj.TryGetValue(e.A, out var la) ? la : adj[e.A] = new()).Add(e.B); (adj.TryGetValue(e.B, out var lb) ? lb : adj[e.B] = new()).Add(e.A); }
        var used = new HashSet<EdgeKey>();
        var res = new List<(List<int>, bool)>();
        List<int> Walk(int start)
        {
            var chain = new List<int> { start }; int cur = start;
            while (true)
            {
                int nx = -1;
                foreach (var x in adj[cur]) if (!used.Contains(EdgeKey.Make(cur, x))) { nx = x; break; }
                if (nx < 0) break;
                used.Add(EdgeKey.Make(cur, nx)); chain.Add(nx); cur = nx;
                if (nx == start) break;
            }
            return chain;
        }
        // open chains first, from their ends
        foreach (var v in adj.Keys.Where(v => adj[v].Count == 1).ToList())
            if (adj[v].Any(x => !used.Contains(EdgeKey.Make(v, x)))) res.Add((Walk(v), false));
        foreach (var v in adj.Keys.ToList())
            if (adj[v].Any(x => !used.Contains(EdgeKey.Make(v, x))))
            {
                var c = Walk(v);
                bool closed = c.Count > 2 && c[^1] == c[0];
                if (closed) c.RemoveAt(c.Count - 1);
                res.Add((c, closed));
            }
        return res;
    }

    /// <summary>
    /// Joins two vertex chains with new faces (3ds Max / Blender Bridge): open chains end to end, closed loops all
    /// around. Same number of vertices: quads; otherwise the strip zips with triangles where one side has more points.
    /// The new faces wind like the faces next to them. Returns the new face count (0 when it cannot).
    /// </summary>
    public static int BridgeChains(PolyMesh m, List<int> a, bool aClosed, List<int> b, bool bClosed, int mat = -1)
    {
        if (a.Count < 2 || b.Count < 2 || aClosed != bClosed || a.Intersect(b).Any()) return 0;
        Vector3 P(int v) => m.Verts[v];
        var bb = b.ToList();
        if (!aClosed)
        {
            // open: b runs the way that keeps the strip untwisted
            float keep = Vector3.Distance(P(a[0]), P(bb[0])) + Vector3.Distance(P(a[^1]), P(bb[^1]));
            float flip = Vector3.Distance(P(a[0]), P(bb[^1])) + Vector3.Distance(P(a[^1]), P(bb[0]));
            if (flip < keep) bb.Reverse();
        }
        else
        {
            // closed: the direction and start of b that give the shortest strip
            var cands = new[] { bb, Enumerable.Reverse(bb).ToList() };
            float bestS = float.MaxValue; List<int> best = bb;
            foreach (var cand in cands)
                for (int s = 0; s < cand.Count; s++)
                {
                    float sc = 0; for (int i = 0; i < a.Count; i++) sc += Vector3.Distance(P(a[i]), P(cand[(s + i * cand.Count / a.Count) % cand.Count]));
                    if (sc < bestS) { bestS = sc; best = cand.Skip(s).Concat(cand.Take(s)).ToList(); }
                }
            bb = best;
        }
        if (mat < 0) mat = m.Faces.Count > 0 ? m.Faces[0].MatId : 0;
        var added = new List<Face>();
        int na = a.Count, nb = bb.Count;
        // zipper: each step advances on a, on b, or on both (quad when the sides have the same count)
        int ia = 0, jb = 0;
        int lastA = aClosed ? na : na - 1, lastB = aClosed ? nb : nb - 1;
        while (ia < lastA || jb < lastB)
        {
            int a0 = a[ia % na], b0 = bb[jb % nb];
            bool canA = ia < lastA, canB = jb < lastB;
            if (canA && canB && na == nb)
            {
                int a1 = a[(ia + 1) % na], b1 = bb[(jb + 1) % nb];
                added.Add(m.AddFace(new[] { a0, a1, b1, b0 }, mat)); ia++; jb++; continue;
            }
            // advance where the progress lags behind (keeps the triangles spread evenly)
            bool stepA = canA && (!canB || (float)(ia + 1) / lastA <= (float)(jb + 1) / lastB);
            if (stepA) { int a1 = a[(ia + 1) % na]; added.Add(m.AddFace(new[] { a0, a1, b0 }, mat)); ia++; }
            else { int b1 = bb[(jb + 1) % nb]; added.Add(m.AddFace(new[] { a0, b1, b0 }, mat)); jb++; }
        }
        if (added.Count == 0) return 0;
        // wind like the neighbouring faces: a border edge is walked opposite to the face that already uses it
        var em = m.BuildEdgeMap();
        foreach (var f in added)
        {
            bool decided = false;
            for (int c = 0; c < f.V.Length && !decided; c++)
            {
                int x = f.V[c], y = f.V[(c + 1) % f.V.Length];
                if (!em.TryGetValue(EdgeKey.Make(x, y), out var users)) continue;
                var other = users.Select(i => m.Faces[i]).FirstOrDefault(o => !added.Contains(o));
                if (other == null) continue;
                int k = Array.IndexOf(other.V, x);
                bool same = k >= 0 && other.V[(k + 1) % other.V.Length] == y;
                if (same) foreach (var g in added) g.Flip();
                decided = true;
            }
            if (decided) break;
        }
        return added.Count;
    }
}

public static partial class MeshOps
{
    /// <summary>
    /// Bridge between two groups of faces (each group connected): both groups are removed and their outlines joined
    /// with a strip (any corner counts). Returns the new face count, 0 when the selection is not two groups with one
    /// outline each.
    /// </summary>
    public static int BridgeFaceGroups(PolyMesh m, ISet<int> faces)
    {
        // connected groups (sharing an edge)
        var em = m.BuildEdgeMap();
        var left = new HashSet<int>(faces.Where(f => f >= 0 && f < m.Faces.Count));
        var groups = new List<HashSet<int>>();
        while (left.Count > 0)
        {
            var g = new HashSet<int>(); var stack = new Stack<int>(); stack.Push(left.First());
            while (stack.Count > 0)
            {
                int f = stack.Pop(); if (!left.Remove(f)) continue; g.Add(f);
                var V = m.Faces[f].V;
                for (int c = 0; c < V.Length; c++)
                    foreach (var o in em[EdgeKey.Make(V[c], V[(c + 1) % V.Length])]) if (left.Contains(o)) stack.Push(o);
            }
            groups.Add(g);
        }
        if (groups.Count != 2) return 0;
        // outline of each group, walked in the group's winding
        List<int> Outline(HashSet<int> g)
        {
            var next = new Dictionary<int, int>();
            foreach (var f in g)
            {
                var V = m.Faces[f].V;
                for (int c = 0; c < V.Length; c++)
                {
                    int x = V[c], y = V[(c + 1) % V.Length];
                    if (em[EdgeKey.Make(x, y)].Count(o => g.Contains(o)) == 1) { if (next.ContainsKey(x)) return null; next[x] = y; }
                }
            }
            if (next.Count < 3) return null;
            var loop = new List<int>(); int s0 = next.Keys.First(), cur = s0;
            do { loop.Add(cur); if (!next.TryGetValue(cur, out cur) || loop.Count > next.Count) return null; } while (cur != s0);
            return loop.Count == next.Count ? loop : null;
        }
        var la = Outline(groups[0]); var lb = Outline(groups[1]);
        if (la == null || lb == null || la.Intersect(lb).Any()) return 0;
        int mat = m.Faces[groups[0].First()].MatId;
        DeleteFaces(m, new HashSet<int>(groups[0].Concat(groups[1])), removeIsolatedVerts: false);
        return BridgeChains(m, la, true, lb, true, mat);
    }
}
