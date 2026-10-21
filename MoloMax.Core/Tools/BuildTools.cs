using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Tools;

/// <summary>An oriented box: centre, rotation, half sizes (world or object space, as the caller decides).</summary>
public readonly record struct OBox(Vector3 Center, Quaternion Rotation, Vector3 Half);

/// <summary>Collision boxes from a mesh: one per loose part (axis-aligned or oriented), or one for the whole mesh.</summary>
public static class AutoCollision
{
    public enum Mode { PerPart, Single }

    public static List<OBox> Boxes(PolyMesh m, Matrix4x4 world, Mode mode, bool oriented)
    {
        var res = new List<OBox>();
        var parts = mode == Mode.Single ? new List<List<int>> { Enumerable.Range(0, m.Verts.Count).ToList() } : LooseParts(m);
        foreach (var part in parts)
        {
            var pts = part.Select(i => Vector3.Transform(m.Verts[i], world)).ToList();
            if (pts.Count < 2) continue;
            res.Add(oriented ? Obb(pts) : Aabb(pts));
        }
        return res;
    }

    static OBox Aabb(List<Vector3> pts)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return new OBox((mn + mx) / 2, Quaternion.Identity, Vector3.Max((mx - mn) / 2, new Vector3(0.01f)));
    }

    /// <summary>Oriented box: Z kept vertical (GTA props stand up), yaw from the principal axis of the footprint, refined by trying nearby angles.</summary>
    static OBox Obb(List<Vector3> pts)
    {
        var c = Vector2.Zero; foreach (var p in pts) c += new Vector2(p.X, p.Y); c /= pts.Count;
        float sxx = 0, syy = 0, sxy = 0;
        foreach (var p in pts) { float x = p.X - c.X, y = p.Y - c.Y; sxx += x * x; syy += y * y; sxy += x * y; }
        float yaw0 = 0.5f * MathF.Atan2(2 * sxy, sxx - syy);
        float best = float.MaxValue, bestYaw = yaw0;
        for (int k = -15; k <= 15; k++)
        {
            float yaw = yaw0 + k * MathF.PI / 180f;
            float cs = MathF.Cos(yaw), sn = MathF.Sin(yaw);
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var p in pts) { float x = p.X * cs + p.Y * sn, y = -p.X * sn + p.Y * cs; x0 = MathF.Min(x0, x); x1 = MathF.Max(x1, x); y0 = MathF.Min(y0, y); y1 = MathF.Max(y1, y); }
            float area = (x1 - x0) * (y1 - y0);
            if (area < best) { best = area; bestYaw = yaw; }
        }
        {
            float cs = MathF.Cos(bestYaw), sn = MathF.Sin(bestYaw);
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in pts)
            {
                float x = p.X * cs + p.Y * sn, y = -p.X * sn + p.Y * cs;
                x0 = MathF.Min(x0, x); x1 = MathF.Max(x1, x); y0 = MathF.Min(y0, y); y1 = MathF.Max(y1, y); z0 = MathF.Min(z0, p.Z); z1 = MathF.Max(z1, p.Z);
            }
            float lx = (x0 + x1) / 2, ly = (y0 + y1) / 2;
            var center = new Vector3(lx * cs - ly * sn, lx * sn + ly * cs, (z0 + z1) / 2);
            return new OBox(center, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, bestYaw), Vector3.Max(new Vector3((x1 - x0) / 2, (y1 - y0) / 2, (z1 - z0) / 2), new Vector3(0.01f)));
        }
    }

    /// <summary>Vertex sets of the connected parts (connected through faces, plus coincident vertices).</summary>
    public static List<List<int>> LooseParts(PolyMesh m)
    {
        int n = m.Verts.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        foreach (var f in m.Faces) for (int i = 1; i < f.V.Length; i++) Union(f.V[0], f.V[i]);
        var byPos = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < n; i++)
        {
            var p = m.Verts[i]; var k = ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));
            if (byPos.TryGetValue(k, out int j)) Union(i, j); else byPos[k] = i;
        }
        var used = new HashSet<int>(m.Faces.SelectMany(f => f.V));
        return Enumerable.Range(0, n).Where(used.Contains).GroupBy(Find).Select(g => g.ToList()).ToList();
    }
}

/// <summary>Doorways: cuts a rectangular opening (from the floor up) into a rectangular wall face.</summary>
public static class Doorway
{
    public sealed class Result
    {
        public bool Ok;
        public string Message;
        /// <summary>Opening in mesh space: bottom centre, right axis, up axis, face normal, width, height.</summary>
        public Vector3 BottomCenter, Right, Up, Normal;
        public float Width, Height;
    }

    /// <summary>Where the middle of a wall face's width sits, measured along the wall from FaceCenter (0 for a plain quad).</summary>
    public static float MidShift(PolyMesh m, int faceIndex)
    {
        var f = m.Faces[faceIndex]; var n = m.FaceNormal(f);
        var up = Vector3.UnitZ - n * Vector3.Dot(Vector3.UnitZ, n);
        if (up.LengthSquared() < 1e-6f) return 0;
        var right = Vector3.Normalize(Vector3.Cross(Vector3.Normalize(up), n));
        var o = m.FaceCenter(f);
        var xs = f.V.Select(v => Vector3.Dot(m.Verts[v] - o, right)).ToArray();
        return (xs.Min() + xs.Max()) / 2;
    }

    /// <param name="offset">Shift of the opening along the wall from FaceCenter (m): the middle of a plain quad wall.</param>
    /// <param name="sill">Height of the bottom of the opening above the face bottom (0 = door, &gt; 0 = window).</param>
    public static Result Cut(PolyMesh m, int faceIndex, float width, float height, float offset = 0, float sill = 0)
    {
        var r = new Result();
        var f = m.Faces[faceIndex];
        var n = m.FaceNormal(f);
        if (n.LengthSquared() < 1e-12f) { r.Message = "Degenerate face."; return r; }
        var up = Vector3.UnitZ - n * Vector3.Dot(Vector3.UnitZ, n);
        if (up.LengthSquared() < 0.25f) { r.Message = "Pick a wall face (not a floor or ceiling)."; return r; }
        up = Vector3.Normalize(up);
        var right = Vector3.Normalize(Vector3.Cross(up, n));
        var o = m.FaceCenter(f);
        // the face in its own 2D frame
        var pr = f.V.Select(v => new Vector2(Vector3.Dot(m.Verts[v] - o, right), Vector3.Dot(m.Verts[v] - o, up))).ToArray();
        float r0 = pr.Min(p => p.X), r1 = pr.Max(p => p.X), u0 = pr.Min(p => p.Y), u1 = pr.Max(p => p.Y);
        // a rectangle: every vertex on its bounding box, the 4 corners present (extra points on the sides are allowed,
        // so a wall already cut by another opening can take one more)
        bool OnBox(Vector2 p) => MathF.Abs(p.X - r0) < 1e-3f || MathF.Abs(p.X - r1) < 1e-3f || MathF.Abs(p.Y - u0) < 1e-3f || MathF.Abs(p.Y - u1) < 1e-3f;
        bool HasCorner(float x, float y) => pr.Any(p => MathF.Abs(p.X - x) < 1e-3f && MathF.Abs(p.Y - y) < 1e-3f);
        bool rect = f.V.Length >= 4 && pr.All(OnBox) && HasCorner(r0, u0) && HasCorner(r1, u0) && HasCorner(r1, u1) && HasCorner(r0, u1);
        if (!rect) { r.Message = "The wall face must be a rectangle (one quad per wall, like MLO room boxes)."; return r; }
        float cx = offset; // from FaceCenter (the vertex average: the middle of a plain quad)
        float a = cx - width / 2, b = cx + width / 2, top = u0 + sill + height, bottom = u0 + sill;
        if (a <= r0 + 0.01f || b >= r1 - 0.01f) { r.Message = "The opening is wider than the wall (or too close to its corners)."; return r; }
        if (top >= u1 - 0.01f) top = u1; // full height: no lintel
        bool lintel = top < u1 - 1e-4f, sillPart = sill > 1e-4f;

        // bilinear UV / colour from the 4 corners
        int Corner(bool hiR, bool hiU) => Array.FindIndex(pr, p => MathF.Abs(p.X - (hiR ? r1 : r0)) < 1e-3f && MathF.Abs(p.Y - (hiU ? u1 : u0)) < 1e-3f);
        int c00 = Corner(false, false), c10 = Corner(true, false), c11 = Corner(true, true), c01 = Corner(false, true);
        Vector2 Uv(float x, float y)
        {
            float tx = (x - r0) / (r1 - r0), ty = (y - u0) / (u1 - u0);
            return Vector2.Lerp(Vector2.Lerp(f.UV[c00], f.UV[c10], tx), Vector2.Lerp(f.UV[c01], f.UV[c11], tx), ty);
        }
        var col = f.Col[0]; int mat = f.MatId; uint smooth = f.Smooth;
        var cache = new Dictionary<(int, int), int>();
        int V(float x, float y)
        {
            var key = ((int)MathF.Round(x * 1e4f), (int)MathF.Round(y * 1e4f));
            if (cache.TryGetValue(key, out int vi)) return vi;
            // reuse the existing corner vertices
            for (int i = 0; i < pr.Length; i++) if (MathF.Abs(pr[i].X - x) < 1e-4f && MathF.Abs(pr[i].Y - y) < 1e-4f) return cache[key] = f.V[i];
            return cache[key] = m.AddVertex(o + right * x + up * y);
        }
        // polygons around the opening, counter-clockwise seen from +n (x = right, y = up); extra corners on shared
        // sides avoid T-junctions so the opening stays a clean open loop (the MLO portal finder relies on it)
        var polys = new List<(float X, float Y)[]>();
        var left = new List<(float, float)> { (r0, u0), (a, u0) };
        if (sillPart) left.Add((a, bottom));
        if (lintel) left.Add((a, top));
        left.Add((a, u1)); left.Add((r0, u1));
        var rightP = new List<(float, float)> { (b, u0), (r1, u0), (r1, u1), (b, u1) };
        if (lintel) rightP.Add((b, top));
        if (sillPart) rightP.Add((b, bottom));
        polys.Add(left.ToArray()); polys.Add(rightP.ToArray());
        if (lintel) polys.Add(new[] { (a, top), (b, top), (b, u1), (a, u1) });
        if (sillPart) polys.Add(new[] { (a, u0), (b, u0), (b, bottom), (a, bottom) });
        var oldCorners = (int[])f.V.Clone();
        m.Faces.RemoveAt(faceIndex);
        // points the old face already had on its sides (from an earlier opening) go into the new polygons' edges
        var extra = Enumerable.Range(0, pr.Length).Where(i => !((MathF.Abs(pr[i].X - r0) < 1e-3f || MathF.Abs(pr[i].X - r1) < 1e-3f) && (MathF.Abs(pr[i].Y - u0) < 1e-3f || MathF.Abs(pr[i].Y - u1) < 1e-3f))).ToList();
        for (int pi = 0; pi < polys.Count && extra.Count > 0; pi++)
        {
            var src = polys[pi]; var outP = new List<(float X, float Y)>();
            for (int c = 0; c < src.Length; c++)
            {
                var pa = src[c]; var pb = src[(c + 1) % src.Length];
                outP.Add(pa);
                float dx = pb.X - pa.X, dy = pb.Y - pa.Y, l2 = dx * dx + dy * dy;
                if (l2 < 1e-10f) continue;
                var mids = new List<(float t, (float, float) q)>();
                foreach (var i in extra)
                {
                    float t = ((pr[i].X - pa.X) * dx + (pr[i].Y - pa.Y) * dy) / l2;
                    float ex = pa.X + dx * t - pr[i].X, ey = pa.Y + dy * t - pr[i].Y;
                    if (t > 1e-4f && t < 1 - 1e-4f && ex * ex + ey * ey < 1e-7f) mids.Add((t, (pr[i].X, pr[i].Y)));
                }
                foreach (var mq in mids.OrderBy(x => x.t)) outP.Add(mq.q);
            }
            polys[pi] = outP.ToArray();
        }
        foreach (var xy in polys)
        {
            var idx = xy.Select(p => V(p.X, p.Y)).ToArray();
            var nf = new Face(idx, mat, smooth);
            for (int c = 0; c < idx.Length; c++) { nf.UV[c] = Uv(xy[c].X, xy[c].Y); nf.Col[c] = col; }
            if (Vector3.Dot(m.FaceNormal(nf), n) < 0) nf.Flip();
            m.Faces.Add(nf);
        }
        // new vertices on the old face's sides are inserted into the neighbour faces across those sides
        var newOnSides = cache.Values.Distinct().Where(v => !oldCorners.Contains(v)).ToList();
        for (int i = 0; i < oldCorners.Length; i++)
        {
            int ea = oldCorners[i], eb = oldCorners[(i + 1) % oldCorners.Length];
            var pa = m.Verts[ea]; var pb = m.Verts[eb]; var ab = pb - pa; float len2 = ab.LengthSquared();
            var on = newOnSides.Select(v => (v, t: Vector3.Dot(m.Verts[v] - pa, ab) / len2))
                .Where(x => x.t > 1e-4f && x.t < 1 - 1e-4f && (pa + ab * x.t - m.Verts[x.v]).LengthSquared() < 1e-8f).ToList();
            if (on.Count == 0) continue;
            foreach (var nb in m.Faces)
            {
                int k = Array.IndexOf(nb.V, eb);
                if (k < 0 || nb.V[(k + 1) % nb.V.Length] != ea) continue; // neighbour walks the side the other way
                var vs = nb.V.ToList(); var uvs = nb.UV.ToList(); var cs = nb.Col.ToList();
                var uvA = uvs[(k + 1) % vs.Count]; var uvB = uvs[k]; var colB = cs[k];
                int at = k + 1;
                foreach (var (v, t) in on.OrderByDescending(x => x.t))
                {
                    vs.Insert(at, v); uvs.Insert(at, Vector2.Lerp(uvA, uvB, t)); cs.Insert(at, colB); at++;
                }
                nb.V = vs.ToArray(); nb.UV = uvs.ToArray(); nb.Col = cs.ToArray();
                break;
            }
        }
        r.Ok = true;
        r.BottomCenter = o + right * cx + up * bottom;
        r.Right = right; r.Up = up; r.Normal = n; r.Width = width; r.Height = MathF.Min(height, u1 - bottom);
        r.Message = $"Opening {width:0.00} x {r.Height:0.00} m cut.";
        return r;
    }

    /// <summary>A door leaf (box, green vertex colour, UV 0..1 front and back) for an opening, with a small clearance.</summary>
    public static PolyMesh Leaf(float width, float height, float thickness = 0.05f, float clearance = 0.01f)
    {
        var m = Primitives.Door(width - 2 * clearance, height - clearance, thickness);
        // front/back faces get the whole texture, edges a thin strip of it
        foreach (var f in m.Faces)
        {
            var n = m.FaceNormal(f);
            for (int c = 0; c < f.V.Length; c++)
            {
                var p = m.Verts[f.V[c]];
                float u = (p.X + width / 2) / width, v = 1 - p.Z / height;
                if (MathF.Abs(n.X) > 0.9f) u = (p.Y + thickness / 2) / thickness * 0.02f;
                f.UV[c] = new Vector2(u, v);
                f.Col[c] = new Vector4(0, 1, 0, 1);
            }
        }
        return m;
    }
}
