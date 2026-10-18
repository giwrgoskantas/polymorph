using System.Numerics;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Paint;

/// <summary>What to take from the high-poly model.</summary>
public sealed class BakeSettings
{
    /// <summary>How far (metres) rays search in front of and behind the low-poly surface (the "cage").</summary>
    public float MaxDistance = 0.05f;
    public bool Normal = true, Ao = true, Curvature = true, Colour = true;
    public int AoRays = 24;
}

/// <summary>Per-texel result of a bake (arrays follow the texels of the <see cref="TexelMap"/>).</summary>
public sealed class BakeResult
{
    /// <summary>High-poly normal in the low-poly tangent frame (x along T, y along B, z along N); (0,0,1) where nothing was hit.</summary>
    public Vector3[] Normal;
    public float[] Ao, Curv;
    /// <summary>Colour of the high-poly (its texture, else its vertex colours), null when not baked.</summary>
    public Vector3[] Colour;
    public bool[] Hit;
    public int Hits, Texels;
    public float HitRatio => Texels == 0 ? 0 : Hits / (float)Texels;
}

/// <summary>
/// High to low baking (like Substance's "Bake mesh maps"): a ray from each low-poly texel along its normal finds the
/// high-poly surface; its normal (in the low tangent frame), ambient occlusion, curvature and colour are stored.
/// </summary>
public static class PaintBaker
{
    /// <param name="high">High-poly mesh.</param>
    /// <param name="highToLow">Transform of the high mesh into the low mesh's object space.</param>
    /// <param name="textureOfMaterial">Diffuse image of a high-poly material id (null = use vertex colours).</param>
    public static BakeResult Bake(TexelMap map, PolyMesh high, Matrix4x4 highToLow, BakeSettings s, Func<int, RgbaImage> textureOfMaterial = null, Action<float> progress = null)
    {
        var bvh = MeshBvh.Build(high, highToLow);
        int n = map.Count;
        var r = new BakeResult { Normal = new Vector3[n], Ao = new float[n], Curv = new float[n], Hit = new bool[n], Texels = n };
        if (s.Colour) r.Colour = new Vector3[n];
        var curvHigh = s.Curvature ? TexelMap.VertexCurvature(high) : null;
        var textures = new Dictionary<int, RgbaImage>();
        if (s.Colour && textureOfMaterial != null)
            foreach (var mat in high.Faces.Select(f => f.MatId).Distinct()) textures[mat] = textureOfMaterial(mat);
        var dirs = Hemisphere(Math.Max(4, s.AoRays));
        float aoDist = MathF.Max(bvh.Diagonal * 0.25f, s.MaxDistance * 4);
        int done = 0;
        Parallel.For(0, n, t =>
        {
            var p = map.Pos[t]; var nl = map.Nrm[t];
            // search both sides of the low surface: start in front, go through to the back
            var origin = p + nl * s.MaxDistance;
            if (!bvh.Closest(origin, -nl, s.MaxDistance * 2, out var hit))
            {
                r.Normal[t] = Vector3.UnitZ; r.Ao[t] = map.Ao[t]; r.Curv[t] = map.Curv[t];
                if (r.Colour != null) r.Colour[t] = new Vector3(0.5f);
                goto next;
            }
            r.Hit[t] = true;
            var nh = bvh.NormalAt(hit);
            if (Vector3.Dot(nh, nl) < 0) nh = -nh; // high mesh with flipped faces
            r.Normal[t] = Vector3.Normalize(new Vector3(Vector3.Dot(nh, map.Tan[t]), Vector3.Dot(nh, map.Bit[t]), MathF.Max(Vector3.Dot(nh, nl), 0.02f)));
            if (s.Ao)
            {
                var hp = origin - nl * hit.T + nh * (bvh.Diagonal * 2e-4f);
                Triangulator.Basis(nh, out var tu, out var tv);
                float open = 0, wsum = 0;
                foreach (var d in dirs)
                {
                    var dir = Vector3.Normalize(tu * d.X + tv * d.Y + nh * d.Z);
                    wsum += d.Z;
                    if (!bvh.Any(hp, dir, aoDist)) open += d.Z;
                }
                r.Ao[t] = wsum > 0 ? open / wsum : 1;
            }
            else r.Ao[t] = map.Ao[t];
            r.Curv[t] = curvHigh != null ? bvh.Interpolate(hit, curvHigh) : map.Curv[t];
            if (r.Colour != null)
            {
                var (mat, uv, col) = bvh.Surface(hit);
                if (textures.TryGetValue(mat, out var img) && img != null)
                {
                    var c = Gta.Library.TextureReader.Sample(img, uv.X, uv.Y);
                    r.Colour[t] = new Vector3(c.X, c.Y, c.Z);
                }
                else r.Colour[t] = new Vector3(col.X, col.Y, col.Z);
            }
            Interlocked.Increment(ref r.Hits);
        next:
            int k = Interlocked.Increment(ref done);
            if (progress != null && (k & 0x3FFF) == 0) progress(k / (float)n);
        });
        return r;
    }

    static List<Vector3> Hemisphere(int n)
    {
        var l = new List<Vector3>(n);
        float golden = MathF.PI * (3 - MathF.Sqrt(5));
        for (int i = 0; i < n; i++)
        {
            float z = 1 - (i + 0.5f) / n, rr = MathF.Sqrt(MathF.Max(0, 1 - z * z)), a = golden * i;
            l.Add(new Vector3(MathF.Cos(a) * rr, MathF.Sin(a) * rr, MathF.Max(z, 0.05f)));
        }
        return l;
    }
}

/// <summary>BVH of a mesh's triangles with closest-hit queries and per-corner attributes (normals, UVs, colours).</summary>
public sealed class MeshBvh
{
    public struct Hit { public float T, U, V; public int Tri; }

    Vector3[] A, B, C, NA, NB, NC;
    Vector2[] UA, UB, UC; Vector4[] CA, CB, CC; int[] Mat, VA, VB, VC;
    Node[] nodes; int[] tris;
    struct Node { public Vector3 Min, Max; public int Left, Right, Start, Count; }
    public float Diagonal { get; private set; }

    public static MeshBvh Build(PolyMesh m, Matrix4x4 xf)
    {
        var vn = new Vector3[m.Verts.Count];
        foreach (var f in m.Faces) { var fn = m.FaceNormal(f) * MathF.Max(m.FaceArea(f), 1e-8f); foreach (var v in f.V) vn[v] += fn; }
        var nx = Matrix4x4.Invert(xf, out var inv) ? Matrix4x4.Transpose(inv) : Matrix4x4.Identity;
        Vector3 P(int v) => Vector3.Transform(m.Verts[v], xf);
        Vector3 N(int v) { var q = Vector3.TransformNormal(vn[v], nx); return q.LengthSquared() > 1e-20f ? Vector3.Normalize(q) : Vector3.UnitZ; }
        var b = new MeshBvh();
        var la = new List<Vector3>(); var lb = new List<Vector3>(); var lc = new List<Vector3>();
        var na = new List<Vector3>(); var nb = new List<Vector3>(); var nc = new List<Vector3>();
        var ua = new List<Vector2>(); var ub = new List<Vector2>(); var uc = new List<Vector2>();
        var ca = new List<Vector4>(); var cb = new List<Vector4>(); var cc = new List<Vector4>();
        var mat = new List<int>(); var ia = new List<int>(); var ib = new List<int>(); var ic = new List<int>();
        foreach (var f in m.Faces)
            foreach (var (i, j, k) in Triangulator.Triangulate(m, f))
            {
                la.Add(P(f.V[i])); lb.Add(P(f.V[j])); lc.Add(P(f.V[k]));
                na.Add(N(f.V[i])); nb.Add(N(f.V[j])); nc.Add(N(f.V[k]));
                ua.Add(f.UV[i]); ub.Add(f.UV[j]); uc.Add(f.UV[k]);
                ca.Add(f.Col[i]); cb.Add(f.Col[j]); cc.Add(f.Col[k]);
                mat.Add(f.MatId); ia.Add(f.V[i]); ib.Add(f.V[j]); ic.Add(f.V[k]);
            }
        b.A = la.ToArray(); b.B = lb.ToArray(); b.C = lc.ToArray(); b.NA = na.ToArray(); b.NB = nb.ToArray(); b.NC = nc.ToArray();
        b.UA = ua.ToArray(); b.UB = ub.ToArray(); b.UC = uc.ToArray(); b.CA = ca.ToArray(); b.CB = cb.ToArray(); b.CC = cc.ToArray();
        b.Mat = mat.ToArray(); b.VA = ia.ToArray(); b.VB = ib.ToArray(); b.VC = ic.ToArray();
        b.tris = Enumerable.Range(0, b.A.Length).ToArray();
        var list = new List<Node>();
        if (b.A.Length > 0) b.BuildNode(list, 0, b.tris.Length);
        b.nodes = list.ToArray();
        if (b.nodes.Length > 0) b.Diagonal = (b.nodes[0].Max - b.nodes[0].Min).Length();
        return b;
    }

    int BuildNode(List<Node> list, int start, int count)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++) { int t = tris[i]; mn = Vector3.Min(mn, Vector3.Min(A[t], Vector3.Min(B[t], C[t]))); mx = Vector3.Max(mx, Vector3.Max(A[t], Vector3.Max(B[t], C[t]))); }
        int id = list.Count;
        list.Add(new Node { Min = mn, Max = mx, Left = -1, Right = -1, Start = start, Count = count });
        if (count <= 4) return id;
        var ext = mx - mn; int axis = ext.X > ext.Y ? (ext.X > ext.Z ? 0 : 2) : (ext.Y > ext.Z ? 1 : 2);
        var keys = new float[count]; var seg = new int[count];
        for (int i = 0; i < count; i++) { seg[i] = tris[start + i]; var c = A[seg[i]] + B[seg[i]] + C[seg[i]]; keys[i] = axis == 0 ? c.X : axis == 1 ? c.Y : c.Z; }
        Array.Sort(keys, seg);
        Array.Copy(seg, 0, tris, start, count);
        int half = count / 2;
        int l = BuildNode(list, start, half), r = BuildNode(list, start + half, count - half);
        var n = list[id]; n.Left = l; n.Right = r; n.Count = 0; list[id] = n;
        return id;
    }

    /// <summary>Closest triangle hit along the ray within maxDist (both faces).</summary>
    public bool Closest(Vector3 o, Vector3 d, float maxDist, out Hit hit) => Trace(o, d, maxDist, false, out hit);

    /// <summary>Any hit (occlusion).</summary>
    public bool Any(Vector3 o, Vector3 d, float maxDist) => Trace(o, d, maxDist, true, out _);

    bool Trace(Vector3 o, Vector3 d, float maxDist, bool any, out Hit hit)
    {
        hit = default; hit.T = maxDist; bool found = false;
        if (nodes.Length == 0) return false;
        var inv = new Vector3(1 / (MathF.Abs(d.X) > 1e-12f ? d.X : 1e-12f), 1 / (MathF.Abs(d.Y) > 1e-12f ? d.Y : 1e-12f), 1 / (MathF.Abs(d.Z) > 1e-12f ? d.Z : 1e-12f));
        Span<int> stack = stackalloc int[64]; int sp = 0; stack[sp++] = 0;
        while (sp > 0)
        {
            var n = nodes[stack[--sp]];
            var t0 = (n.Min - o) * inv; var t1 = (n.Max - o) * inv;
            var tmin = Vector3.Min(t0, t1); var tmax = Vector3.Max(t0, t1);
            float enter = MathF.Max(MathF.Max(tmin.X, tmin.Y), MathF.Max(tmin.Z, 0)), exit = MathF.Min(MathF.Min(tmax.X, tmax.Y), MathF.Min(tmax.Z, hit.T));
            if (enter > exit) continue;
            if (n.Left < 0)
            {
                for (int i = n.Start; i < n.Start + n.Count; i++)
                {
                    int t = tris[i];
                    // Möller-Trumbore, both sides
                    var e1 = B[t] - A[t]; var e2 = C[t] - A[t];
                    var pv = Vector3.Cross(d, e2); float det = Vector3.Dot(e1, pv);
                    if (MathF.Abs(det) < 1e-14f) continue;
                    float id = 1 / det; var tv = o - A[t];
                    float u = Vector3.Dot(tv, pv) * id; if (u < 0 || u > 1) continue;
                    var qv = Vector3.Cross(tv, e1);
                    float v = Vector3.Dot(d, qv) * id; if (v < 0 || u + v > 1) continue;
                    float tt = Vector3.Dot(e2, qv) * id;
                    if (tt <= 1e-6f || tt >= hit.T) continue;
                    hit = new Hit { T = tt, U = u, V = v, Tri = t }; found = true;
                    if (any) return true;
                }
            }
            else if (sp < 62) { stack[sp++] = n.Left; stack[sp++] = n.Right; }
        }
        return found;
    }

    public Vector3 NormalAt(Hit h)
    {
        float w = 1 - h.U - h.V;
        var n = NA[h.Tri] * w + NB[h.Tri] * h.U + NC[h.Tri] * h.V;
        return n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.Normalize(Vector3.Cross(B[h.Tri] - A[h.Tri], C[h.Tri] - A[h.Tri]));
    }

    public float Interpolate(Hit h, float[] perVertex) => perVertex[VA[h.Tri]] * (1 - h.U - h.V) + perVertex[VB[h.Tri]] * h.U + perVertex[VC[h.Tri]] * h.V;

    public (int Mat, Vector2 Uv, Vector4 Colour) Surface(Hit h)
    {
        float w = 1 - h.U - h.V; int t = h.Tri;
        return (Mat[t], UA[t] * w + UB[t] * h.U + UC[t] * h.V, CA[t] * w + CB[t] * h.U + CC[t] * h.V);
    }
}
