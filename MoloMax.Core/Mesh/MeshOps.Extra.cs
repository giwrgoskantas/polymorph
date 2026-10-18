using System.Numerics;

namespace MoloMax.Core.Mesh;

public static partial class MeshOps
{
    /// <summary>
    /// Make Face (Blender F): builds one polygon from the selected vertices, ordered around their
    /// centre in their best-fit plane. Its winding follows the neighbouring faces when it shares an
    /// edge with them, otherwise it faces away from the mesh centre. Returns the new face index or -1.
    /// </summary>
    public static int MakeFace(PolyMesh m, ISet<int> verts)
    {
        if (verts == null || verts.Count < 3) return -1;
        var ids = verts.Where(v => v >= 0 && v < m.Verts.Count).ToList();
        if (ids.Count < 3) return -1;
        var c = Vector3.Zero; foreach (var v in ids) c += m.Verts[v]; c /= ids.Count;

        // plane normal from the covariance's smallest axis approximated by the best of three projections
        List<int> best = null; float bestArea = -1; Vector3 bestN = Vector3.UnitZ;
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            Triangulator.Basis(axis, out var u, out var w);
            float Ang(int v) { var d = m.Verts[v] - c; return MathF.Atan2(Vector3.Dot(d, w), Vector3.Dot(d, u)); }
            var ord = ids.OrderBy(Ang).ToList();
            var face = new Face(ord.ToArray());
            float area = m.FaceArea(face);
            if (area > bestArea) { bestArea = area; best = ord; bestN = axis; }
        }
        if (bestArea < 1e-10f) return -1;
        var f = new Face(best.ToArray(), 0, 0);

        // winding: an existing edge a→b means the new face must use b→a
        var dir = new HashSet<(int, int)>();
        foreach (var g in m.Faces) for (int i = 0; i < g.V.Length; i++) dir.Add((g.V[i], g.V[(i + 1) % g.V.Length]));
        int agree = 0, disagree = 0;
        for (int i = 0; i < f.V.Length; i++)
        {
            var e = (f.V[i], f.V[(i + 1) % f.V.Length]);
            if (dir.Contains(e)) disagree++;
            if (dir.Contains((e.Item2, e.Item1))) agree++;
        }
        if (disagree > agree) f.Flip();
        else if (agree == 0 && m.Verts.Count > 0)
        {
            var mc = Vector3.Zero; foreach (var v in m.Verts) mc += v; mc /= m.Verts.Count;
            var tmpN = m.FaceNormal(f);
            if (Vector3.Dot(tmpN, m.FaceCenter(f) - mc) < 0) f.Flip();
        }
        // borrow material / colour from a neighbour face
        var neighbour = m.Faces.FirstOrDefault(g => g.V.Any(verts.Contains));
        if (neighbour != null) { f.MatId = neighbour.MatId; f.Smooth = neighbour.Smooth; for (int i = 0; i < f.Col.Length; i++) f.Col[i] = neighbour.Col[0]; }
        m.Faces.Add(f);
        BoxMap(m, new HashSet<int> { m.Faces.Count - 1 }, 1f);
        return m.Faces.Count - 1;
    }

    /// <summary>
    /// Recalculate Normals (Blender Shift+N): makes every connected part consistently wound, then
    /// points it outside (or inside for MLO rooms) using its signed volume. Null = whole mesh.
    /// Returns how many faces were flipped.
    /// </summary>
    public static int RecalcNormals(PolyMesh m, ISet<int> faces = null, bool inside = false)
    {
        var scope = faces ?? new HashSet<int>(Enumerable.Range(0, m.Faces.Count));
        var em = m.BuildEdgeMap();
        var visited = new HashSet<int>();
        int flipped = 0;
        foreach (var seed in scope)
        {
            if (visited.Contains(seed)) continue;
            // BFS: a neighbour sharing edge a→b must run b→a
            var comp = new List<int>(); var q = new Queue<int>();
            q.Enqueue(seed); visited.Add(seed);
            while (q.Count > 0)
            {
                int fi = q.Dequeue(); comp.Add(fi);
                var f = m.Faces[fi];
                for (int i = 0; i < f.V.Length; i++)
                {
                    int a = f.V[i], b = f.V[(i + 1) % f.V.Length];
                    var list = em[EdgeKey.Make(a, b)];
                    if (list.Count != 2) continue; // only walk across manifold edges
                    foreach (var gi in list)
                    {
                        if (gi == fi || visited.Contains(gi) || !scope.Contains(gi)) continue;
                        var g = m.Faces[gi];
                        bool sameDir = false;
                        for (int k = 0; k < g.V.Length; k++) if (g.V[k] == a && g.V[(k + 1) % g.V.Length] == b) { sameDir = true; break; }
                        if (sameDir) { g.Flip(); flipped++; }
                        visited.Add(gi); q.Enqueue(gi);
                    }
                }
            }
            // orientation of the part from its signed volume (about its own centre, so open parts behave)
            var cen = Vector3.Zero; int n = 0;
            foreach (var fi in comp) foreach (var v in m.Faces[fi].V) { cen += m.Verts[v]; n++; }
            cen /= Math.Max(n, 1);
            float vol = 0;
            foreach (var fi in comp)
            {
                var f = m.Faces[fi];
                foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                    vol += Vector3.Dot(m.Verts[f.V[a]] - cen, Vector3.Cross(m.Verts[f.V[b]] - cen, m.Verts[f.V[c]] - cen));
            }
            if (inside ? vol > 0 : vol < 0) { foreach (var fi in comp) m.Faces[fi].Flip(); flipped += comp.Count; }
        }
        return flipped;
    }

    /// <summary>Delete Edges (Blender X → Edges): removes the edges and every face that uses them.</summary>
    public static void DeleteEdges(PolyMesh m, ISet<EdgeKey> edges)
    {
        if (edges == null || edges.Count == 0) return;
        var faces = new HashSet<int>();
        for (int i = 0; i < m.Faces.Count; i++) if (m.FaceEdges(m.Faces[i]).Any(edges.Contains)) faces.Add(i);
        DeleteFaces(m, faces);
    }

    /// <summary>Loop Cut (Blender Ctrl+R): one new edge loop through the ring of the given edge. Returns the new edges.</summary>
    public static HashSet<EdgeKey> LoopCut(PolyMesh m, EdgeKey edge)
    {
        var ring = EdgeRing(m, edge);
        return ConnectEdges(m, ring);
    }

    /// <summary>
    /// The loop a loop cut would create, as segments between edge midpoints (for the yellow preview).
    /// </summary>
    public static List<(Vector3 A, Vector3 B)> LoopCutPreview(PolyMesh m, EdgeKey edge)
    {
        var ring = EdgeRing(m, edge);
        var res = new List<(Vector3, Vector3)>();
        foreach (var f in m.Faces)
        {
            var hits = m.FaceEdges(f).Where(ring.Contains).ToList();
            if (hits.Count < 2) continue;
            Vector3 Mid(EdgeKey e) => (m.Verts[e.A] + m.Verts[e.B]) * 0.5f;
            res.Add((Mid(hits[0]), Mid(hits[1])));
        }
        return res;
    }

    /// <summary>Face loop (Blender Alt+click in face mode): the strip of quads crossed by the ring of an edge.</summary>
    public static HashSet<int> FaceLoop(PolyMesh m, EdgeKey edge)
    {
        var ring = EdgeRing(m, edge);
        var res = new HashSet<int>();
        for (int i = 0; i < m.Faces.Count; i++)
            if (m.FaceEdges(m.Faces[i]).Count(ring.Contains) >= 2) res.Add(i);
        return res;
    }
}
