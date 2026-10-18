using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>
/// Quadric edge-collapse decimation (Garland-Heckbert) for LODs. Works on triangles, keeps per-corner UVs and
/// colours, keeps open borders and material seams in place, refuses collapses that would flip a triangle.
/// </summary>
public static class MeshSimplify
{
    struct Quadric
    {
        public double A, B, C, D, E, F, G, H, I, J; // symmetric 4x4 upper triangle
        public static Quadric Plane(Vector3 n, float d, double w)
        {
            double a = n.X, b = n.Y, c = n.Z, dd = d;
            return new Quadric { A = a * a * w, B = a * b * w, C = a * c * w, D = a * dd * w, E = b * b * w, F = b * c * w, G = b * dd * w, H = c * c * w, I = c * dd * w, J = dd * dd * w };
        }
        public static Quadric operator +(Quadric q, Quadric r) => new() { A = q.A + r.A, B = q.B + r.B, C = q.C + r.C, D = q.D + r.D, E = q.E + r.E, F = q.F + r.F, G = q.G + r.G, H = q.H + r.H, I = q.I + r.I, J = q.J + r.J };
        public double Eval(Vector3 v)
        {
            double x = v.X, y = v.Y, z = v.Z;
            return A * x * x + 2 * B * x * y + 2 * C * x * z + 2 * D * x + E * y * y + 2 * F * y * z + 2 * G * y + H * z * z + 2 * I * z + J;
        }
    }

    sealed class Tri { public int[] V = new int[3]; public Vector2[] UV = new Vector2[3]; public Vector4[] Col = new Vector4[3]; public int Mat; public uint Smooth; public bool Dead; }

    /// <summary>Returns a new mesh with about <paramref name="ratio"/> of the triangles (0..1).</summary>
    public static PolyMesh Decimate(PolyMesh src, float ratio)
    {
        ratio = Math.Clamp(ratio, 0.01f, 1f);
        var pos = src.Verts.ToList();
        var tris = new List<Tri>();
        foreach (var f in src.Faces)
            foreach (var (a, b, c) in Triangulator.Triangulate(src, f))
            {
                var t = new Tri { Mat = f.MatId, Smooth = f.Smooth };
                t.V[0] = f.V[a]; t.V[1] = f.V[b]; t.V[2] = f.V[c];
                t.UV[0] = f.UV[a]; t.UV[1] = f.UV[b]; t.UV[2] = f.UV[c];
                t.Col[0] = f.Col[a]; t.Col[1] = f.Col[b]; t.Col[2] = f.Col[c];
                if (t.V[0] != t.V[1] && t.V[1] != t.V[2] && t.V[0] != t.V[2]) tris.Add(t);
            }
        int target = Math.Max(4, (int)(tris.Count * ratio));
        int n = pos.Count;
        var vt = new List<int>[n];
        for (int i = 0; i < n; i++) vt[i] = new List<int>(6);
        for (int i = 0; i < tris.Count; i++) foreach (var v in tris[i].V) vt[v].Add(i);

        // quadrics from the triangle planes (area weighted)
        var q = new Quadric[n];
        foreach (var t in tris)
        {
            var p0 = pos[t.V[0]]; var cr = Vector3.Cross(pos[t.V[1]] - p0, pos[t.V[2]] - p0);
            float len = cr.Length(); if (len < 1e-12f) continue;
            var nrm = cr / len;
            var pq = Quadric.Plane(nrm, -Vector3.Dot(nrm, p0), len * 0.5);
            foreach (var v in t.V) q[v] = q[v] + pq;
        }
        // locked: open borders and material seams stay where they are (no holes, no texture swimming at edges)
        var locked = new bool[n];
        var edgeUse = new Dictionary<long, int>(MeshOps.LongKey.I);
        long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        foreach (var t in tris) for (int i = 0; i < 3; i++) { var k = Key(t.V[i], t.V[(i + 1) % 3]); edgeUse[k] = edgeUse.TryGetValue(k, out var c) ? c + 1 : 1; }
        foreach (var kv in edgeUse) if (kv.Value == 1) { locked[(int)(kv.Key >> 32)] = true; locked[(int)(kv.Key & 0xFFFFFFFF)] = true; }
        for (int v = 0; v < n; v++) { int m0 = -1; foreach (var ti in vt[v]) { if (m0 < 0) m0 = tris[ti].Mat; else if (tris[ti].Mat != m0) { locked[v] = true; break; } } }

        var version = new int[n];
        var heap = new PriorityQueue<(int A, int B, int VA, int VB, Vector3 P), double>();
        void Push(int a, int b)
        {
            if (locked[a] && locked[b]) return;
            var qq = q[a] + q[b];
            Vector3 best; double cost;
            if (locked[a]) { best = pos[a]; cost = qq.Eval(best); }
            else if (locked[b]) { best = pos[b]; cost = qq.Eval(best); }
            else
            {
                var mid = (pos[a] + pos[b]) * 0.5f;
                double ca = qq.Eval(pos[a]), cb = qq.Eval(pos[b]), cm = qq.Eval(mid);
                (best, cost) = ca <= cb && ca <= cm ? (pos[a], ca) : cb <= cm ? (pos[b], cb) : (mid, cm);
            }
            heap.Enqueue((a, b, version[a], version[b], best), cost + 1e-9 * Vector3.DistanceSquared(pos[a], pos[b]));
        }
        foreach (var k in edgeUse.Keys) Push((int)(k >> 32), (int)(k & 0xFFFFFFFF));

        int alive = tris.Count;
        while (alive > target && heap.TryDequeue(out var e, out _))
        {
            int a = e.A, b = e.B;
            if (version[a] != e.VA || version[b] != e.VB) continue;
            // keep = a, remove = b (b's triangles move to a)
            if (locked[b] && !locked[a]) (a, b) = (b, a);
            var p = e.P;
            if (Flips(a, b, p) || Flips(b, a, p)) continue;
            pos[a] = p; q[a] = q[a] + q[b];
            foreach (var ti in vt[b])
            {
                var t = tris[ti]; if (t.Dead) continue;
                bool hasA = t.V.Contains(a);
                if (hasA) { t.Dead = true; alive--; continue; }
                for (int i = 0; i < 3; i++) if (t.V[i] == b) t.V[i] = a;
                vt[a].Add(ti);
            }
            vt[b].Clear();
            vt[a].RemoveAll(ti => tris[ti].Dead);
            version[a]++; version[b]++;
            var nbs = new HashSet<int>();
            foreach (var ti in vt[a]) foreach (var v in tris[ti].V) if (v != a) nbs.Add(v);
            foreach (var v in nbs) Push(a, v);
        }

        bool Flips(int keep, int gone, Vector3 np)
        {
            foreach (var ti in vt[gone])
            {
                var t = tris[ti]; if (t.Dead || t.V.Contains(keep)) continue;
                var p0 = pos[t.V[0]]; var p1 = pos[t.V[1]]; var p2 = pos[t.V[2]];
                var before = Vector3.Cross(p1 - p0, p2 - p0);
                Vector3 P(int i) => t.V[i] == gone ? np : pos[t.V[i]];
                var after = Vector3.Cross(P(1) - P(0), P(2) - P(0));
                if (after.LengthSquared() < 1e-14f || Vector3.Dot(Vector3.Normalize(before), Vector3.Normalize(after)) < 0.3f) return true;
            }
            return false;
        }

        // rebuild a compact mesh
        var res = new PolyMesh();
        var map = new Dictionary<int, int>();
        foreach (var t in tris)
        {
            if (t.Dead) continue;
            var vv = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (!map.TryGetValue(t.V[i], out var nv)) { nv = res.Verts.Count; res.Verts.Add(pos[t.V[i]]); map[t.V[i]] = nv; }
                vv[i] = nv;
            }
            if (vv[0] == vv[1] || vv[1] == vv[2] || vv[0] == vv[2]) continue;
            var f = res.AddFace(vv, t.Mat);
            f.UV = (Vector2[])t.UV.Clone(); f.Col = (Vector4[])t.Col.Clone(); f.Smooth = t.Smooth;
        }
        return res;
    }
}
