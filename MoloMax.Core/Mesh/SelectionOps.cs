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
        var valid = m.BuildEdgeMap();
        Edges.RemoveWhere(e => !valid.ContainsKey(e));
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
    /// <summary>All faces connected (through shared edges) to the seed faces.</summary>
    public static HashSet<int> Elements(PolyMesh m, IEnumerable<int> seed)
    {
        var em = m.BuildEdgeMap();
        var res = new HashSet<int>(); var st = new Stack<int>();
        foreach (var f in seed) if (res.Add(f)) st.Push(f);
        while (st.Count > 0)
        {
            var f = st.Pop();
            foreach (var e in m.FaceEdges(m.Faces[f])) foreach (var g in em[e]) if (res.Add(g)) st.Push(g);
        }
        return res;
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
        // A face survives only if none of its corners touches the selection's outline
        // (an unselected neighbour or an open border).
        var em = m.BuildEdgeMap();
        var outline = new HashSet<int>();
        foreach (var kv in em)
            if (kv.Value.Count(faces.Contains) == 1) { outline.Add(kv.Key.A); outline.Add(kv.Key.B); }
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
