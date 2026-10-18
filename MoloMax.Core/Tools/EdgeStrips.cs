using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Tools;

/// <summary>Settings of an edge strip (fake AO, edge dirt, edge wear, smooth edge).</summary>
public sealed class StripSettings
{
    /// <summary>How far each wing reaches onto its face (m).</summary>
    public float Width = 0.10f;
    /// <summary>Lift off the surface against z-fighting (m).</summary>
    public float Offset = 0.002f;
    public bool Flip;
    /// <summary>Only build the wing on the first face of each edge (one-sided strips).</summary>
    public bool OneSided;
}

/// <summary>
/// Builds decal ribbons along selected edges: one wing per adjacent face, lying on that face, lifted by
/// Offset, mitred where the chain turns. UV U runs along the chain (square texels), V = 0.5 on the edge and
/// 0 / 1 at the outer border of wing 0 / wing 1 — the layout the procedural strip textures are made for.
/// The result is a new mesh in the source's local space.
/// </summary>
public static class EdgeStrips
{
    sealed class Seg
    {
        public int A, B;                  // vertex indices
        public Vector3 Dir;
        public float Len;
        public (Vector3 N, Vector3 T, Vector3 C)?[] Side = new (Vector3, Vector3, Vector3)?[2]; // per wing slot
    }

    public static PolyMesh Build(PolyMesh m, ISet<EdgeKey> edges, StripSettings s, int matId = 0, Vector4? colour = null)
    {
        var col = colour ?? new Vector4(0, 0.7f, 0, 1);
        var map = m.BuildEdgeMap();
        var sel = edges.Where(e => map.ContainsKey(e) && map[e].Count > 0 && (m.Verts[e.A] - m.Verts[e.B]).LengthSquared() > 1e-12f).ToList();
        var outMesh = new PolyMesh();
        if (sel.Count == 0) return outMesh;

        foreach (var chain in Chains(sel))
        {
            // ---- segments with their (up to 2) faces
            var segs = new List<Seg>();
            for (int k = 0; k + 1 < chain.Count; k++)
            {
                int a = chain[k], b = chain[k + 1];
                var e = EdgeKey.Make(a, b);
                var d = m.Verts[b] - m.Verts[a];
                var seg = new Seg { A = a, B = b, Len = d.Length(), Dir = Vector3.Normalize(d) };
                var mid = (m.Verts[a] + m.Verts[b]) * 0.5f;
                var faces = map[e].OrderBy(f => f).Take(s.OneSided ? 1 : 2).ToList();
                var sides = new List<(Vector3 N, Vector3 T, Vector3 C)>();
                foreach (var fi in faces)
                {
                    var n = m.FaceNormal(fi);
                    if (n.LengthSquared() < 1e-12f) continue;
                    var c = m.FaceCenter(fi);
                    var t = Vector3.Cross(n, seg.Dir);
                    if (t.LengthSquared() < 1e-12f) continue;
                    t = Vector3.Normalize(t);
                    if (Vector3.Dot(t, c - mid) < 0) t = -t;
                    if (s.Flip) t = -t;
                    sides.Add((n, t, c));
                }
                // keep wing slots consistent along the chain so the texture does not flip
                if (segs.Count > 0 && sides.Count > 0)
                {
                    var prev = segs[^1].Side;
                    if (sides.Count == 2)
                    {
                        float keep = Dot(prev[0], sides[0]) + Dot(prev[1], sides[1]);
                        float swap = Dot(prev[0], sides[1]) + Dot(prev[1], sides[0]);
                        if (swap > keep) (sides[0], sides[1]) = (sides[1], sides[0]);
                    }
                    else if (prev[1] != null && Dot(prev[1], sides[0]) > Dot(prev[0], sides[0])) sides.Insert(0, default);
                }
                for (int i = 0; i < Math.Min(2, sides.Count); i++)
                    if (sides[i].N != default) seg.Side[i] = sides[i];
                segs.Add(seg);
            }
            if (segs.Count == 0) continue;
            bool closed = chain.Count > 2 && chain[0] == chain[^1];

            // ---- cross-section at every chain point
            int np = chain.Count;
            var inner = new Vector3[np];
            var outer = new Vector3?[np, 2];
            var arc = new float[np];
            for (int j = 1; j < np; j++) arc[j] = arc[j - 1] + segs[j - 1].Len;
            for (int j = 0; j < np; j++)
            {
                var inc = new List<Seg>();
                if (j > 0) inc.Add(segs[j - 1]); else if (closed) inc.Add(segs[^1]);
                if (j < segs.Count) inc.Add(segs[j]); else if (closed) inc.Add(segs[0]);
                var p = m.Verts[chain[j]];
                var nsum = Vector3.Zero;
                foreach (var sg in inc) foreach (var sd in sg.Side) if (sd != null) nsum += sd.Value.N;
                var nAvg = nsum.LengthSquared() > 1e-12f ? Vector3.Normalize(nsum) : Vector3.UnitZ;
                inner[j] = p + nAvg * s.Offset;
                for (int slot = 0; slot < 2; slot++)
                {
                    var with = inc.Where(sg => sg.Side[slot] != null).Select(sg => sg.Side[slot].Value).ToList();
                    if (with.Count == 0) continue;
                    if (with.Count == 1) { outer[j, slot] = inner[j] + with[0].T * s.Width; continue; }
                    var (nA, tA, _) = with[0]; var (nB, tB, _) = with[1];
                    var N = nA + nB; N = N.LengthSquared() > 1e-12f ? Vector3.Normalize(N) : nA;
                    var mv = Solve(tA, tB, N, s.Width, s.Width, 0);
                    if (mv == null) mv = tA * s.Width;
                    var v = mv.Value;
                    if (v.Length() > 4 * s.Width) v = Vector3.Normalize(v) * 4 * s.Width; // mitre limit
                    outer[j, slot] = inner[j] + v;
                }
            }

            // ---- faces: per segment and wing
            float texel = 2 * s.Width; // UV: 2*width of strip = one texture height
            for (int k = 0; k < segs.Count; k++)
            {
                int j0 = k, j1 = k + 1;
                for (int slot = 0; slot < 2; slot++)
                {
                    var sd = segs[k].Side[slot];
                    if (sd == null || outer[j0, slot] == null || outer[j1, slot] == null) continue;
                    float vOut = slot == 0 ? 0f : 1f;
                    var pts = new[] { outer[j0, slot].Value, inner[j0], inner[j1], outer[j1, slot].Value };
                    var uvs = new[]
                    {
                        new Vector2(arc[j0] / texel, vOut), new Vector2(arc[j0] / texel, 0.5f),
                        new Vector2(arc[j1] / texel, 0.5f), new Vector2(arc[j1] / texel, vOut),
                    };
                    // wind toward the face normal
                    var gn = Vector3.Cross(pts[1] - pts[0], pts[2] - pts[1]);
                    if (Vector3.Dot(gn, sd.Value.N) < 0) { Array.Reverse(pts); Array.Reverse(uvs); }
                    var idx = pts.Select(p => outMesh.AddVertex(p)).ToArray();
                    var f = outMesh.AddFace(idx, matId);
                    for (int c = 0; c < 4; c++) { f.UV[c] = uvs[c]; f.Col[c] = col; }
                }
            }
        }
        WeldPositions(outMesh, 1e-5f);
        return outMesh;
    }

    static float Dot((Vector3 N, Vector3 T, Vector3 C)? a, (Vector3 N, Vector3 T, Vector3 C) b) => a == null ? 0 : Vector3.Dot(a.Value.N, b.N);

    /// <summary>Solves m·a = x, m·b = y, m·c = z (3x3); null when singular.</summary>
    static Vector3? Solve(Vector3 a, Vector3 b, Vector3 c, float x, float y, float z)
    {
        var mat = new Matrix4x4(a.X, a.Y, a.Z, 0, b.X, b.Y, b.Z, 0, c.X, c.Y, c.Z, 0, 0, 0, 0, 1);
        if (MathF.Abs(mat.GetDeterminant()) < 1e-6f || !Matrix4x4.Invert(mat, out var inv)) return null;
        // rows of mat are a, b, c: m = inv * (x, y, z)
        return new Vector3(inv.M11 * x + inv.M12 * y + inv.M13 * z, inv.M21 * x + inv.M22 * y + inv.M23 * z, inv.M31 * x + inv.M32 * y + inv.M33 * z);
    }

    /// <summary>Edge chains: walks from loose ends first, stops at junctions; a loop repeats its first vertex.</summary>
    public static List<List<int>> Chains(IEnumerable<EdgeKey> edges)
    {
        var adj = new Dictionary<int, List<EdgeKey>>();
        foreach (var e in edges)
        {
            if (!adj.TryGetValue(e.A, out var la)) adj[e.A] = la = new List<EdgeKey>(); la.Add(e);
            if (!adj.TryGetValue(e.B, out var lb)) adj[e.B] = lb = new List<EdgeKey>(); lb.Add(e);
        }
        var used = new HashSet<EdgeKey>();
        var chains = new List<List<int>>();
        IEnumerable<int> starts = adj.Keys.Where(v => adj[v].Count == 1).OrderBy(v => v).Concat(adj.Keys.OrderBy(v => v));
        foreach (var start in starts)
        {
            while (adj[start].Any(e => !used.Contains(e)))
            {
                var chain = new List<int> { start };
                int cur = start;
                while (true)
                {
                    EdgeKey? nextE = adj[cur].Where(e => !used.Contains(e)).Select(e => (EdgeKey?)e).FirstOrDefault();
                    if (nextE == null) break;
                    used.Add(nextE.Value);
                    cur = nextE.Value.Other(cur);
                    chain.Add(cur);
                    if (cur == start || adj[cur].Count > 2) break;
                }
                if (chain.Count > 1) chains.Add(chain);
            }
        }
        return chains;
    }

    /// <summary>Merges vertices closer than tol (keeps face corners' UV and colour).</summary>
    public static void WeldPositions(PolyMesh m, float tol)
    {
        var map = new Dictionary<(long, long, long), int>();
        var remap = new int[m.Verts.Count];
        var verts = new List<Vector3>();
        for (int i = 0; i < m.Verts.Count; i++)
        {
            var p = m.Verts[i];
            var k = ((long)MathF.Round(p.X / tol), (long)MathF.Round(p.Y / tol), (long)MathF.Round(p.Z / tol));
            if (!map.TryGetValue(k, out int j)) { j = verts.Count; verts.Add(p); map[k] = j; }
            remap[i] = j;
        }
        m.Verts = verts;
        foreach (var f in m.Faces) for (int c = 0; c < f.V.Length; c++) f.V[c] = remap[f.V[c]];
        m.Faces.RemoveAll(f => f.V.Distinct().Count() < 3);
    }
}
