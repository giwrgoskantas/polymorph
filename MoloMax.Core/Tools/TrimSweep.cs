using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Tools;

public enum TrimKind { Baseboard, Cornice, DoorFrame, WindowFrame }
public enum TrimEdge { Square, Chamfer, Round, Cove, Ogee, Step }

public sealed class TrimSettings
{
    public TrimKind Kind = TrimKind.Baseboard;
    /// <summary>Profile size along "up" (baseboard height, casing width) and along "out" (thickness), metres.</summary>
    public float Height = 0.12f, Depth = 0.02f;
    public TrimEdge Inner = TrimEdge.Square, Outer = TrimEdge.Chamfer;
    public float InnerAmount = 0.5f, OuterAmount = 0.5f;
    public int Steps = 2, Segments = 3;
    public float StepDepth = 0.7f;
    public bool CapEnds = true, Flip;
    public float UvScale = 1f;
    public float MiterLimit = 4f;
    public Vector4 Colour = new(0, 1, 0, 1);

    public static TrimSettings Frame(TrimKind kind) => new()
    {
        Kind = kind, Height = 0.15f, Depth = 0.04f, Inner = TrimEdge.Step, InnerAmount = 0.62f, Steps = 1, StepDepth = 0.74f, CapEnds = false,
    };
}

/// <summary>
/// Moulding sweep (baseboard, cornice, door and window casing): a 2D profile (out, up) swept along chains of
/// selected edges with exact mitres (least-squares joint of the two neighbouring segments), real-world UVs and
/// GTA green vertex colour. "out" leaves the wall, "up" rises from the floor (baseboard) or leaves the opening (casings).
/// </summary>
public static class TrimSweep
{
    /// <summary>The profile polyline (out, up), back-bottom first, back-top last.</summary>
    public static List<Vector2> Profile(TrimSettings s)
    {
        float D = MathF.Max(s.Depth, 1e-4f), H = MathF.Max(s.Height, 1e-4f), budget = H / 2;
        var (inner, innerSpan) = End(s.Inner, D, budget, s.InnerAmount, s.Steps, s.StepDepth, s.Segments);
        var (outer, outerSpan) = End(s.Outer, D, budget, s.OuterAmount, s.Steps, s.StepDepth, s.Segments);
        var pts = new List<Vector2> { Vector2.Zero };
        for (int i = inner.Count - 1; i >= 0; i--) pts.Add(new Vector2(inner[i].X, innerSpan - inner[i].Y));
        foreach (var p in outer) pts.Add(new Vector2(p.X, H - outerSpan + p.Y));
        pts.Add(new Vector2(0, H));
        var clean = new List<Vector2>();
        foreach (var p in pts) if (clean.Count == 0 || Vector2.Distance(clean[^1], p) > 1e-7f) clean.Add(p);
        return clean;
    }

    static (List<Vector2>, float) End(TrimEdge kind, float D, float budget, float amount, int steps, float stepDepth, int seg)
    {
        float size = MathF.Min(D, budget) * Math.Clamp(amount, 0, 0.98f);
        seg = Math.Max(1, seg);
        switch (kind)
        {
            case TrimEdge.Chamfer when size > 1e-6f: return (new() { new(D, 0), new(D - size, size) }, size);
            case TrimEdge.Round when size > 1e-6f:
                {
                    var l = new List<Vector2>();
                    for (int i = 0; i <= seg; i++) { float a = MathF.PI / 2 * i / seg; l.Add(new Vector2(D - size + size * MathF.Cos(a), size * MathF.Sin(a))); }
                    return (l, size);
                }
            case TrimEdge.Cove when size > 1e-6f:
                {
                    var l = new List<Vector2>();
                    for (int i = 0; i <= seg; i++) { float a = -MathF.PI / 2 - MathF.PI / 2 * i / seg; l.Add(new Vector2(D + size * MathF.Cos(a), size + size * MathF.Sin(a))); }
                    return (l, size);
                }
            case TrimEdge.Ogee:
                {
                    float span = MathF.Min(budget, 4 * D);
                    var l = new List<Vector2>();
                    for (int i = 0; i <= seg; i++) { float t = (float)i / seg, sm = t * t * (3 - 2 * t); l.Add(new Vector2(D * (1 - sm), span * t)); }
                    return (l, span);
                }
            case TrimEdge.Step:
                {
                    int n = Math.Max(1, steps);
                    float span = budget * Math.Clamp(amount, 0, 1), cut = D * Math.Clamp(stepDepth, 0, 1);
                    var l = new List<Vector2> { new(D, 0) };
                    for (int k = 1; k <= n; k++) { float th = D - cut * k / n; l.Add(new Vector2(th, span * (k - 1) / n)); l.Add(new Vector2(th, span * k / n)); }
                    return (l, span);
                }
            default: return (new() { new(D, 0) }, 0);
        }
    }

    /// <summary>Sweeps the profile along the selected edges of a mesh (mesh space in, mesh space out).</summary>
    public static PolyMesh Build(PolyMesh m, ISet<EdgeKey> edges, TrimSettings s, int matId = 0)
    {
        var res = new PolyMesh();
        var prof = Profile(s);
        var map = m.BuildEdgeMap();
        var up = Vector3.UnitZ;
        var sel = edges.Where(e => map.ContainsKey(e) && (m.Verts[e.A] - m.Verts[e.B]).LengthSquared() > 1e-12f).ToList();
        foreach (var chain in EdgeStrips.Chains(sel))
        {
            var pts = chain.Select(i => m.Verts[i]).ToList();
            bool closed = chain.Count > 2 && chain[0] == chain[^1];
            int ns = pts.Count - 1;
            if (ns < 1) continue;
            // out direction per segment, from the faces next to it
            var outs = new Vector3[ns];
            for (int k = 0; k < ns; k++)
            {
                var d = Vector3.Normalize(pts[k + 1] - pts[k]);
                var e = EdgeKey.Make(chain[k], chain[k + 1]);
                var cand = Vector3.Zero; float bestVert = float.MaxValue;
                foreach (var fi in map[e])
                {
                    var n = m.FaceNormal(fi);
                    float vert = MathF.Abs(Vector3.Dot(n, up));
                    if (vert < bestVert) { bestVert = vert; cand = n; }
                }
                if (s.Kind is TrimKind.Baseboard or TrimKind.Cornice) cand -= up * Vector3.Dot(cand, up); // horizontal, off the wall
                cand -= d * Vector3.Dot(cand, d);
                outs[k] = cand.LengthSquared() > 1e-12f ? Vector3.Normalize(cand) : Vector3.Normalize(Vector3.Cross(up, d));
                if (s.Flip) outs[k] = -outs[k];
            }
            // keep neighbouring outs on the same side
            for (int k = 1; k < ns; k++) if (Vector3.Dot(outs[k], outs[k - 1]) < -0.7f) outs[k] = -outs[k];

            // orientation: casings grow away from the opening, baseboards stand up
            var centroid = Vector3.Zero; int cnt = closed ? pts.Count - 1 : pts.Count;
            for (int i = 0; i < cnt; i++) centroid += pts[i]; centroid /= cnt;
            float vote = 0;
            for (int k = 0; k < ns; k++)
            {
                var d = pts[k + 1] - pts[k]; var uk = Vector3.Cross(Vector3.Normalize(d), outs[k]);
                vote += d.Length() * (s.Kind is TrimKind.DoorFrame or TrimKind.WindowFrame
                    ? Vector3.Dot(uk, (pts[k] + pts[k + 1]) * 0.5f - centroid)
                    : Vector3.Dot(uk, up));
            }
            if (vote < 0) { pts.Reverse(); Array.Reverse(outs); chain.Reverse(); }

            var segD = new Vector3[ns]; var segO = new Vector3[ns]; var segU = new Vector3[ns];
            var dist = new float[pts.Count];
            for (int k = 0; k < ns; k++)
            {
                segD[k] = Vector3.Normalize(pts[k + 1] - pts[k]);
                segO[k] = Vector3.Normalize(outs[k] - segD[k] * Vector3.Dot(outs[k], segD[k]));
                segU[k] = Vector3.Normalize(Vector3.Cross(segD[k], segO[k]));
                if (s.Kind == TrimKind.Cornice) segU[k] = -segU[k];
                dist[k + 1] = dist[k] + (pts[k + 1] - pts[k]).Length();
            }
            // joint basis per node
            var A = new Vector3[pts.Count]; var B = new Vector3[pts.Count];
            for (int j = 0; j < pts.Count; j++)
            {
                int a = j - 1, b = j;
                if (closed) { if (a < 0) a = ns - 1; if (b >= ns) b = 0; }
                bool ha = a >= 0 && a < ns, hb = b >= 0 && b < ns;
                if (ha && hb && a != b)
                {
                    A[j] = Joint(segD[a], segD[b], segO[a], segO[b], s.MiterLimit);
                    B[j] = Joint(segD[a], segD[b], segU[a], segU[b], s.MiterLimit);
                }
                else { int k = ha ? a : b; A[j] = segO[k]; B[j] = segU[k]; }
            }
            // rings and quads
            int mpts = prof.Count;
            var pv = new float[mpts];
            for (int i = 1; i < mpts; i++) pv[i] = pv[i - 1] + Vector2.Distance(prof[i], prof[i - 1]);
            var ring = new int[pts.Count, mpts];
            for (int j = 0; j < pts.Count; j++)
                for (int i = 0; i < mpts; i++)
                    ring[j, i] = res.AddVertex(pts[j] + A[j] * prof[i].X + B[j] * prof[i].Y);
            for (int k = 0; k < ns; k++)
                for (int i = 0; i + 1 < mpts; i++)
                {
                    if (i == 0) continue; // bottom strip sits on the floor / against the opening: not visible
                    var dl = prof[i + 1] - prof[i];
                    var want = segO[k] * dl.Y - segU[k] * dl.X;
                    if (want.LengthSquared() < 1e-14f) continue;
                    int[] q = { ring[k, i], ring[k, i + 1], ring[k + 1, i + 1], ring[k + 1, i] };
                    Vector2 Uv(int node, int pi)
                    {
                        float u = dist[k] + Vector3.Dot(res.Verts[ring[node, pi]] - pts[k], segD[k]);
                        return new Vector2(pv[pi] / s.UvScale, u / s.UvScale);
                    }
                    var uvs = new[] { Uv(k, i), Uv(k, i + 1), Uv(k + 1, i + 1), Uv(k + 1, i) };
                    var gn = Vector3.Cross(res.Verts[q[1]] - res.Verts[q[0]], res.Verts[q[2]] - res.Verts[q[1]]);
                    if (Vector3.Dot(gn, want) < 0) { Array.Reverse(q); Array.Reverse(uvs); }
                    var f = res.AddFace(q, matId);
                    for (int c = 0; c < 4; c++) { f.UV[c] = uvs[c]; f.Col[c] = s.Colour; }
                }
            // caps on loose ends
            if (s.CapEnds && !closed && mpts >= 3)
                foreach (var (j, nb) in new[] { (0, 1), (pts.Count - 1, pts.Count - 2) })
                {
                    var want = Vector3.Normalize(pts[j] - pts[nb]);
                    var idx = Enumerable.Range(0, mpts).Select(i => ring[j, i]).ToArray();
                    var f = new Face(idx, matId);
                    for (int i = 0; i < mpts; i++) { f.UV[i] = new Vector2(prof[i].Y / s.UvScale, prof[i].X / s.UvScale); f.Col[i] = s.Colour; }
                    if (Vector3.Dot(res.FaceNormal(f), want) < 0) f.Flip();
                    res.Faces.Add(f);
                }
        }
        EdgeStrips.WeldPositions(res, 2e-4f);
        return res;
    }

    /// <summary>Least-squares intersection of the two extruded profile lines (exact mitre), length-limited.</summary>
    static Vector3 Joint(Vector3 da, Vector3 db, Vector3 axA, Vector3 axB, float limit)
    {
        float c = Vector3.Dot(da, db), den = 1 - c * c;
        if (den < 1e-9f) return axA;
        var r = axB - axA;
        float t = (Vector3.Dot(da, r) - c * Vector3.Dot(db, r)) / den;
        var res = axA + da * t;
        if (res.Length() > limit) res = Vector3.Normalize(res) * limit;
        return res;
    }
}
