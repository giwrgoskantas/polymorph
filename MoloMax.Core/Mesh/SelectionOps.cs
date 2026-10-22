namespace MoloMax.Core.Mesh;

public enum SubObject { Object = 0, Vertex = 1, Edge = 2, Border = 3, Polygon = 4, Element = 5 }

/// <summary>Sub-object selection of one Editable Poly, with 3ds Max style grow/shrink/convert.</summary>
public sealed class PolySelection
{
    public HashSet<int> Verts = new();
    public HashSet<EdgeKey> Edges = new();
    public HashSet<int> Faces = new();

    public void Clear() { Verts.Clear(); Edges.Clear(); Faces.Clear(); }

    public PolySelection Clone() => new()
    {
        Verts = new HashSet<int>(Verts),
        Edges = new HashSet<EdgeKey>(Edges),
        Faces = new HashSet<int>(Faces),
    };

    public int Count(SubObject level) => level switch
    {
        SubObject.Vertex => Verts.Count,
        SubObject.Edge or SubObject.Border => Edges.Count,
        SubObject.Polygon or SubObject.Element => Faces.Count,
        _ => 0,
    };

    /// <summary>Drops indices that no longer exist after a topology change.</summary>
    public void Sanitise(PolyMesh m)
    {
        Verts.RemoveWhere(v => v < 0 || v >= m.Verts.Count);
        Faces.RemoveWhere(f => f < 0 || f >= m.Faces.Count);
        if (Edges.Count == 0) return;
        // an edge is still there when one face still has both its points one after the other
        var (start, idx) = m.BuildVertexFacesPacked(); int nv = m.Verts.Count;
        Edges.RemoveWhere(e =>
        {
            if (e.A < 0 || e.A >= nv || e.B < 0 || e.B >= nv) return true;
            for (int k = start[e.A]; k < start[e.A + 1]; k++)
            {
                var f = m.Faces[idx[k]].V; int n = f.Length;
                for (int i = 0; i < n; i++)
                {
                    int a = f[i], b = f[(i + 1) % n];
                    if (a == e.A && b == e.B || a == e.B && b == e.A) return false;
                }
            }
            return true;
        });
    }

    /// <summary>The vertices touched by the current selection at a level.</summary>
    public HashSet<int> VerticesOf(PolyMesh m, SubObject level)
    {
        switch (level)
        {
            case SubObject.Vertex: return Verts.Where(v => v >= 0 && v < m.Verts.Count).ToHashSet();
            case SubObject.Edge:
            case SubObject.Border:
                { var s = new HashSet<int>(); foreach (var e in Edges) if (e.A < m.Verts.Count && e.B < m.Verts.Count) { s.Add(e.A); s.Add(e.B); } return s; }
            case SubObject.Polygon:
            case SubObject.Element:
                { var s = new HashSet<int>(); foreach (var f in Faces) if (f >= 0 && f < m.Faces.Count) foreach (var v in m.Faces[f].V) s.Add(v); return s; }
            default: return new HashSet<int>(Enumerable.Range(0, m.Verts.Count));
        }
    }

    /// <summary>Converts the selection from one level to another (3ds Max Ctrl+click on a level).</summary>
    public void Convert(PolyMesh m, SubObject from, SubObject to)
    {
        var verts = VerticesOf(m, from);
        switch (to)
        {
            case SubObject.Vertex: Verts = verts; break;
            case SubObject.Edge:
                Edges = m.BuildEdgeMap().Keys.Where(e => verts.Contains(e.A) && verts.Contains(e.B)).ToHashSet(); break;
            case SubObject.Border:
                Edges = MeshOps.BorderEdges(m).Where(e => verts.Contains(e.A) && verts.Contains(e.B)).ToHashSet(); break;
            case SubObject.Polygon:
            case SubObject.Element:
                Faces = Enumerable.Range(0, m.Faces.Count).Where(f => m.Faces[f].V.All(verts.Contains)).ToHashSet();
                if (to == SubObject.Element) Faces = MeshSelect.Elements(m, Faces);
                break;
        }
    }
}

public static class MeshSelect
{
    /// <summary>
    /// All faces connected to the seed faces. Points at the same place count as one (game meshes are cut at every
    /// texture seam, the pieces of one element only touch by position).
    /// </summary>
    sealed class ElemTable { public int Version, Verts, Faces; public int[] Canon, Start, Idx; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolyMesh, ElemTable> elemCache = new();

    /// <summary>Same, with the point table kept for this mesh <paramref name="version"/> (clicking around a big room).</summary>
    public static HashSet<int> Elements(PolyMesh m, IEnumerable<int> seed, int version)
    {
        if (!elemCache.TryGetValue(m, out var t) || t.Version != version || t.Verts != m.Verts.Count || t.Faces != m.Faces.Count)
        {
            t = BuildElemTable(m, 1e-4f); t.Version = version;
            elemCache.AddOrUpdate(m, t);
        }
        return Walk(m, seed, t);
    }

    public static HashSet<int> Elements(PolyMesh m, IEnumerable<int> seed, float weld = 1e-4f) => Walk(m, seed, BuildElemTable(m, weld));

    static HashSet<int> Walk(PolyMesh m, IEnumerable<int> seed, ElemTable t)
    {
        int nv = m.Verts.Count, fc = m.Faces.Count; var canon = t.Canon; var start = t.Start; var idx = t.Idx;
        var seen = new bool[fc]; var res = new HashSet<int>(); var st = new Stack<int>();
        foreach (var f in seed) if (f >= 0 && f < fc && !seen[f]) { seen[f] = true; res.Add(f); st.Push(f); }
        while (st.Count > 0)
        {
            var f = st.Pop();
            foreach (var v in m.Faces[f].V)
            {
                if (v >= nv) continue; int c = canon[v];
                for (int k = start[c]; k < start[c + 1]; k++) { int g = idx[k]; if (!seen[g]) { seen[g] = true; res.Add(g); st.Push(g); } }
            }
        }
        return res;
    }

    static ElemTable BuildElemTable(PolyMesh m, float weld)
    {
        int nv = m.Verts.Count, fc = m.Faces.Count;
        // each point -> the first point found at the same place (grid cell of the weld size)
        var canon = new int[nv];
        var first = new Dictionary<(int, int, int), int>(nv);
        float inv = 1f / weld;
        for (int v = 0; v < nv; v++)
        {
            var p = m.Verts[v];
            var key = ((int)MathF.Round(p.X * inv), (int)MathF.Round(p.Y * inv), (int)MathF.Round(p.Z * inv));
            if (first.TryGetValue(key, out int u)) canon[v] = u;
            else { first[key] = v; canon[v] = v; }
        }
        // point -> faces (CSR on the merged points)
        var start = new int[nv + 1];
        foreach (var f in m.Faces) foreach (var v in f.V) if (v < nv) start[canon[v] + 1]++;
        for (int i = 0; i < nv; i++) start[i + 1] += start[i];
        var fill = (int[])start.Clone(); var idx = new int[start[nv]];
        for (int fi = 0; fi < fc; fi++) foreach (var v in m.Faces[fi].V) if (v < nv) idx[fill[canon[v]]++] = fi;
        return new ElemTable { Verts = nv, Faces = fc, Canon = canon, Start = start, Idx = idx };
    }

    public static HashSet<int> GrowFaces(PolyMesh m, ISet<int> faces)
    {
        var verts = new HashSet<int>(); foreach (var f in faces) foreach (var v in m.Faces[f].V) verts.Add(v);
        var res = new HashSet<int>(faces);
        for (int i = 0; i < m.Faces.Count; i++) if (m.Faces[i].V.Any(verts.Contains)) res.Add(i);
        return res;
    }

    public static HashSet<int> ShrinkFaces(PolyMesh m, ISet<int> faces)
    {
        // A face survives only if none of its corners touches an unselected face. The open border of the mesh does not
        // count while some face is left out: on a stair, a strip or any open GTA mesh every face touches the border.
        var outline = new HashSet<int>();
        var mine = new HashSet<int>();
        foreach (var f in faces) foreach (var v in m.Faces[f].V) mine.Add(v);
        for (int i = 0; i < m.Faces.Count; i++)
            if (!faces.Contains(i)) foreach (var v in m.Faces[i].V) if (mine.Contains(v)) outline.Add(v);
        if (outline.Count == 0)
        {
            // the whole part is selected: then it shrinks from its open border (a selected plane keeps its middle)
            foreach (var kv in m.BuildEdgeMap())
                if (kv.Value.Count(faces.Contains) == 1) { outline.Add(kv.Key.A); outline.Add(kv.Key.B); }
        }
        return faces.Where(f => !m.Faces[f].V.Any(outline.Contains)).ToHashSet();
    }

    public static HashSet<int> GrowVerts(PolyMesh m, ISet<int> verts)
    {
        var res = new HashSet<int>(verts);
        foreach (var e in m.BuildEdgeMap().Keys) { if (verts.Contains(e.A)) res.Add(e.B); if (verts.Contains(e.B)) res.Add(e.A); }
        return res;
    }

    public static HashSet<int> ShrinkVerts(PolyMesh m, ISet<int> verts)
    {
        var res = new HashSet<int>(verts);
        foreach (var e in m.BuildEdgeMap().Keys)
        {
            if (verts.Contains(e.A) && !verts.Contains(e.B)) res.Remove(e.A);
            if (verts.Contains(e.B) && !verts.Contains(e.A)) res.Remove(e.B);
        }
        return res;
    }

    /// <summary>Select Less on edges: drops the ends of each selected run (an edge whose point has no other selected edge).</summary>
    public static HashSet<EdgeKey> ShrinkEdges(PolyMesh m, ISet<EdgeKey> edges)
    {
        var count = new Dictionary<int, int>();
        foreach (var e in edges) { count[e.A] = count.GetValueOrDefault(e.A) + 1; count[e.B] = count.GetValueOrDefault(e.B) + 1; }
        return edges.Where(e => count[e.A] > 1 && count[e.B] > 1).ToHashSet();
    }

    public static HashSet<EdgeKey> GrowEdges(PolyMesh m, ISet<EdgeKey> edges)
    {
        var vs = new HashSet<int>(); foreach (var e in edges) { vs.Add(e.A); vs.Add(e.B); }
        return m.BuildEdgeMap().Keys.Where(e => vs.Contains(e.A) || vs.Contains(e.B)).ToHashSet();
    }

    public static HashSet<int> FacesByMaterial(PolyMesh m, int matId)
        => Enumerable.Range(0, m.Faces.Count).Where(f => m.Faces[f].MatId == matId).ToHashSet();

    public static HashSet<int> FacesBySmoothing(PolyMesh m, uint bits)
        => Enumerable.Range(0, m.Faces.Count).Where(f => (m.Faces[f].Smooth & bits) != 0).ToHashSet();
}
