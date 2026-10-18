using System.Numerics;

namespace MoloMax.Core.Mesh;

/// <summary>
/// 3ds Max style standard primitives plus GTA building blocks (room, wall, stairs, door).
/// Units are metres, Z is up, pivots follow Max (base centre for Box/Cylinder, centre for Sphere).
/// </summary>
public static class Primitives
{
    // ---------------------------------------------------------------- helpers

    static void Grid(PolyMesh m, Vector3 origin, Vector3 du, Vector3 dv, int nu, int nv, uint smooth, int mat)
    {
        nu = Math.Max(1, nu); nv = Math.Max(1, nv);
        int b = m.Verts.Count;
        for (int j = 0; j <= nv; j++)
            for (int i = 0; i <= nu; i++)
                m.AddVertex(origin + du * (i / (float)nu) + dv * (j / (float)nv));
        for (int j = 0; j < nv; j++)
            for (int i = 0; i < nu; i++)
            {
                int a = b + j * (nu + 1) + i;
                var f = m.AddFace(new[] { a, a + 1, a + nu + 2, a + nu + 1 }, mat, smooth);
                f.UV[0] = new Vector2(i / (float)nu, 1 - j / (float)nv);
                f.UV[1] = new Vector2((i + 1) / (float)nu, 1 - j / (float)nv);
                f.UV[2] = new Vector2((i + 1) / (float)nu, 1 - (j + 1) / (float)nv);
                f.UV[3] = new Vector2(i / (float)nu, 1 - (j + 1) / (float)nv);
            }
    }

    static void OrientFrom(PolyMesh m, Func<Vector3, Vector3> centre)
    {
        foreach (var f in m.Faces)
        {
            var c = m.FaceCenter(f);
            if (Vector3.Dot(m.FaceNormal(f), c - centre(c)) < 0) f.Flip();
        }
    }

    // ---------------------------------------------------------------- standard primitives

    /// <summary>
    /// Tufted (capitonné) cushion block: every side is a grid of puffy cells sewn together, like a leather bench,
    /// a padded headboard or a sofa seat. Width along X, length along Y, height along Z, pivot at the base centre.
    /// The bottom stays open (it sits on a base). <paramref name="puff"/> = how far a cell swells (metres).
    /// </summary>
    public static PolyMesh Tufted(float width, float length, float height, int cellsX, int cellsY, int cellsZ, float puff = 0.02f, int segs = 8, bool bottom = false, int mat = 0)
    {
        var m = new PolyMesh();
        cellsX = Math.Max(1, cellsX); cellsY = Math.Max(1, cellsY); cellsZ = Math.Max(1, cellsZ); segs = Math.Clamp(segs, 2, 32);
        float w = width / 2, l = length / 2, h = height;
        var X = Vector3.UnitX; var Y = Vector3.UnitY; var Z = Vector3.UnitZ;
        // pillow profile of a cell: flat-ish top, rounded down to the seams (0 at the cell border)
        static float Bump(float t) { float a = MathF.Abs(2 * t - 1); return 1 - a * a * a; }
        void Side(Vector3 origin, Vector3 du, Vector3 dv, Vector3 n, int nu, int nv, uint smooth, bool swell)
        {
            int U = nu * segs, V = nv * segs, b = m.Verts.Count;
            for (int j = 0; j <= V; j++)
                for (int i = 0; i <= U; i++)
                {
                    float cu = (i % segs) / (float)segs, cv = (j % segs) / (float)segs;
                    if (i == U) cu = 0; if (j == V) cv = 0; // the far border is a seam too
                    float d = swell ? puff * MathF.Pow(MathF.Max(0, Bump(cu) * Bump(cv)), 0.6f) : 0;
                    m.AddVertex(origin + du * (i / (float)U) + dv * (j / (float)V) + n * d);
                }
            // winding from the side's own axes (the puffs slope too much for a "face the centre" test)
            bool flip = Vector3.Dot(Vector3.Cross(du, dv), n) < 0;
            for (int j = 0; j < V; j++)
                for (int i = 0; i < U; i++)
                {
                    int a = b + j * (U + 1) + i;
                    var f = m.AddFace(new[] { a, a + 1, a + U + 2, a + U + 1 }, mat, smooth);
                    // UVs in metres (same texel density on every side), packed into 0..1 at the end
                    float su = du.Length(), sv = dv.Length();
                    f.UV[0] = new Vector2(i / (float)U * su, -j / (float)V * sv); f.UV[1] = new Vector2((i + 1) / (float)U * su, -j / (float)V * sv);
                    f.UV[2] = new Vector2((i + 1) / (float)U * su, -(j + 1) / (float)V * sv); f.UV[3] = new Vector2(i / (float)U * su, -(j + 1) / (float)V * sv);
                    if (flip) f.Flip();
                }
        }
        Side(new Vector3(-w, -l, h), X * width, Y * length, Z, cellsX, cellsY, 1u << 2, true);   // top
        Side(new Vector3(-w, -l, 0), X * width, Z * h, -Y, cellsX, cellsZ, 1u << 3, true);        // front
        Side(new Vector3(w, -l, 0), Y * length, Z * h, X, cellsY, cellsZ, 1u << 4, true);         // right
        Side(new Vector3(w, l, 0), -X * width, Z * h, Y, cellsX, cellsZ, 1u << 5, true);          // back
        Side(new Vector3(-w, l, 0), -Y * length, Z * h, -X, cellsY, cellsZ, 1u << 6, true);       // left
        if (bottom) Side(new Vector3(-w, l, 0), X * width, -Y * length, -Z, cellsX, cellsY, 1u << 1, false);
        MeshOps.WeldByDistance(m, 1e-5f);
        UvTools.Pack(m, Enumerable.Range(0, m.Faces.Count), 0.01f); // one island per side, ready to paint
        return m;
    }

    /// <summary>Box: width along X, length along Y, height along Z, pivot at the base centre.</summary>
    public static PolyMesh Box(float width, float length, float height, int wSegs = 1, int lSegs = 1, int hSegs = 1, int mat = 0)
    {
        var m = new PolyMesh();
        float w = width / 2, l = length / 2, h = height;
        var X = Vector3.UnitX; var Y = Vector3.UnitY; var Z = Vector3.UnitZ;
        Grid(m, new Vector3(-w, l, 0), X * width, -Y * length, wSegs, lSegs, 1u << 1, mat);   // bottom
        Grid(m, new Vector3(-w, -l, h), X * width, Y * length, wSegs, lSegs, 1u << 2, mat);   // top
        Grid(m, new Vector3(-w, -l, 0), X * width, Z * h, wSegs, hSegs, 1u << 3, mat);        // front
        Grid(m, new Vector3(w, -l, 0), Y * length, Z * h, lSegs, hSegs, 1u << 4, mat);        // right
        Grid(m, new Vector3(w, l, 0), -X * width, Z * h, wSegs, hSegs, 1u << 5, mat);         // back
        Grid(m, new Vector3(-w, l, 0), -Y * length, Z * h, lSegs, hSegs, 1u << 6, mat);       // left
        MeshOps.WeldByDistance(m, 1e-5f);
        return m;
    }

    public static PolyMesh Plane(float width, float length, int wSegs = 4, int lSegs = 4, int mat = 0)
    {
        var m = new PolyMesh();
        Grid(m, new Vector3(-width / 2, -length / 2, 0), Vector3.UnitX * width, Vector3.UnitY * length, wSegs, lSegs, 1u, mat);
        return m;
    }

    public static PolyMesh Cylinder(float radius, float height, int sides = 18, int heightSegs = 1, bool smooth = true, int mat = 0)
    {
        sides = Math.Max(3, sides); heightSegs = Math.Max(1, heightSegs);
        var m = new PolyMesh();
        for (int k = 0; k <= heightSegs; k++)
            for (int j = 0; j < sides; j++)
            {
                float a = MathF.Tau * j / sides;
                m.AddVertex(new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, height * k / heightSegs));
            }
        for (int k = 0; k < heightSegs; k++)
            for (int j = 0; j < sides; j++)
            {
                int j2 = (j + 1) % sides;
                var f = m.AddFace(new[] { k * sides + j, k * sides + j2, (k + 1) * sides + j2, (k + 1) * sides + j }, mat, smooth ? 1u : 0u);
                float u0 = j / (float)sides, u1 = (j + 1) / (float)sides, v0 = k / (float)heightSegs, v1 = (k + 1) / (float)heightSegs;
                f.UV[0] = new(u0, 1 - v0); f.UV[1] = new(u1, 1 - v0); f.UV[2] = new(u1, 1 - v1); f.UV[3] = new(u0, 1 - v1);
            }
        var top = new int[sides]; var bot = new int[sides];
        for (int j = 0; j < sides; j++) { top[j] = heightSegs * sides + j; bot[j] = sides - 1 - j; }
        CapUV(m, m.AddFace(top, mat, 1u << 1), radius);
        CapUV(m, m.AddFace(bot, mat, 1u << 2), radius);
        return m;
    }

    static void CapUV(PolyMesh m, Face f, float r)
    {
        for (int i = 0; i < f.V.Length; i++)
        {
            var p = m.Verts[f.V[i]];
            f.UV[i] = new Vector2(p.X / (2 * r) + 0.5f, 0.5f - p.Y / (2 * r));
        }
    }

    public static PolyMesh Cone(float radius1, float radius2, float height, int sides = 18, bool smooth = true, int mat = 0)
    {
        sides = Math.Max(3, sides);
        var m = new PolyMesh();
        for (int j = 0; j < sides; j++)
        {
            float a = MathF.Tau * j / sides;
            m.AddVertex(new Vector3(MathF.Cos(a) * radius1, MathF.Sin(a) * radius1, 0));
        }
        bool apex = radius2 <= 1e-5f;
        if (apex) m.AddVertex(new Vector3(0, 0, height));
        else for (int j = 0; j < sides; j++)
            {
                float a = MathF.Tau * j / sides;
                m.AddVertex(new Vector3(MathF.Cos(a) * radius2, MathF.Sin(a) * radius2, height));
            }
        for (int j = 0; j < sides; j++)
        {
            int j2 = (j + 1) % sides;
            var f = apex ? m.AddFace(new[] { j, j2, sides }, mat, smooth ? 1u : 0u)
                         : m.AddFace(new[] { j, j2, sides + j2, sides + j }, mat, smooth ? 1u : 0u);
            float u0 = j / (float)sides, u1 = (j + 1) / (float)sides;
            f.UV[0] = new(u0, 1); f.UV[1] = new(u1, 1);
            if (apex) f.UV[2] = new((u0 + u1) / 2, 0); else { f.UV[2] = new(u1, 0); f.UV[3] = new(u0, 0); }
        }
        var bot = new int[sides]; for (int j = 0; j < sides; j++) bot[j] = sides - 1 - j;
        CapUV(m, m.AddFace(bot, mat, 1u << 2), radius1);
        if (!apex)
        {
            var top = new int[sides]; for (int j = 0; j < sides; j++) top[j] = sides + j;
            CapUV(m, m.AddFace(top, mat, 1u << 1), radius2);
        }
        return m;
    }

    /// <summary>UV sphere centred on its pivot.</summary>
    public static PolyMesh Sphere(float radius, int segments = 24, bool smooth = true, int mat = 0)
    {
        int seg = Math.Max(4, segments), rings = Math.Max(2, segments / 2);
        var m = new PolyMesh();
        m.AddVertex(new Vector3(0, 0, radius));
        for (int i = 1; i < rings; i++)
        {
            float t = MathF.PI * i / rings;
            for (int j = 0; j < seg; j++)
            {
                float p = MathF.Tau * j / seg;
                m.AddVertex(new Vector3(MathF.Sin(t) * MathF.Cos(p), MathF.Sin(t) * MathF.Sin(p), MathF.Cos(t)) * radius);
            }
        }
        m.AddVertex(new Vector3(0, 0, -radius));
        int bottom = m.Verts.Count - 1;
        uint sg = smooth ? 1u : 0u;
        Vector2 UV(int ring, float j) => new(j / seg, ring / (float)rings);
        for (int j = 0; j < seg; j++)
        {
            var f = m.AddFace(new[] { 0, 1 + j, 1 + (j + 1) % seg }, mat, sg);
            f.UV[0] = UV(0, j + 0.5f); f.UV[1] = UV(1, j); f.UV[2] = UV(1, j + 1);
        }
        for (int i = 0; i < rings - 2; i++)
        {
            int a = 1 + i * seg, b = a + seg;
            for (int j = 0; j < seg; j++)
            {
                int j2 = (j + 1) % seg;
                var f = m.AddFace(new[] { a + j, b + j, b + j2, a + j2 }, mat, sg);
                f.UV[0] = UV(i + 1, j); f.UV[1] = UV(i + 2, j); f.UV[2] = UV(i + 2, j + 1); f.UV[3] = UV(i + 1, j + 1);
            }
        }
        int last = 1 + (rings - 2) * seg;
        for (int j = 0; j < seg; j++)
        {
            var f = m.AddFace(new[] { last + j, bottom, last + (j + 1) % seg }, mat, sg);
            f.UV[0] = UV(rings - 1, j); f.UV[1] = UV(rings, j + 0.5f); f.UV[2] = UV(rings - 1, j + 1);
        }
        OrientFrom(m, _ => Vector3.Zero);
        return m;
    }

    /// <summary>Tube (pipe): outer and inner radius, open through the middle.</summary>
    public static PolyMesh Tube(float outer, float inner, float height, int sides = 18, bool smooth = true, int mat = 0)
    {
        sides = Math.Max(3, sides);
        var m = new PolyMesh();
        // rings: 0 outer bottom, 1 outer top, 2 inner top, 3 inner bottom
        float[] rad = { outer, outer, inner, inner }; float[] z = { 0, height, height, 0 };
        for (int r = 0; r < 4; r++)
            for (int j = 0; j < sides; j++)
            {
                float a = MathF.Tau * j / sides;
                m.AddVertex(new Vector3(MathF.Cos(a) * rad[r], MathF.Sin(a) * rad[r], z[r]));
            }
        uint[] sg = { smooth ? 1u : 0u, 1u << 1, smooth ? 1u << 2 : 0u, 1u << 3 };
        for (int r = 0; r < 4; r++)
        {
            int r2 = (r + 1) % 4;
            for (int j = 0; j < sides; j++)
            {
                int j2 = (j + 1) % sides;
                m.AddFace(new[] { r * sides + j, r * sides + j2, r2 * sides + j2, r2 * sides + j }, mat, sg[r]);
            }
        }
        MeshOps.BoxMap(m, null, 1f);
        return m;
    }

    // ---------------------------------------------------------------- GTA / architecture

    /// <summary>
    /// MLO room: a closed box with its normals turned inward (you see the walls from inside,
    /// they disappear from outside), floor at Z = 0. Vertex colour green, flat shading.
    /// </summary>
    public static PolyMesh Room(float width, float length, float height, int mat = 0)
    {
        var m = Box(width, length, height, 1, 1, 1, mat);
        foreach (var f in m.Faces) { f.Flip(); f.Smooth = 0; }
        MeshOps.BoxMap(m, null, 1f);
        return m;
    }

    /// <summary>Simple GTA door: a box with good vertex colour, ready for a door texture.</summary>
    public static PolyMesh Door(float width = 1.2f, float height = 2.3f, float thickness = 0.06f, int mat = 0)
    {
        var m = Box(width, thickness, height, 1, 1, 1, mat);
        foreach (var f in m.Faces) f.Smooth = 0;
        return m;
    }

    /// <summary>
    /// Solid straight stairs running along +Y, pivot at the bottom front centre.
    /// Closed volume (no single faces seen from both sides).
    /// </summary>
    public static PolyMesh Stairs(float width, int steps, float totalRise, float totalRun, int mat = 0)
    {
        steps = Math.Max(1, steps);
        float r = totalRise / steps, t = totalRun / steps;
        var prof = new List<Vector2> { new(0, 0) }; // (y, z)
        for (int i = 0; i < steps; i++)
        {
            prof.Add(new Vector2(i * t, (i + 1) * r));
            prof.Add(new Vector2((i + 1) * t, (i + 1) * r));
        }
        prof.Add(new Vector2(steps * t, 0));
        return ExtrudeProfileX(prof, width, mat);
    }

    /// <summary>Extrudes a closed (y, z) profile along X, centred on X = 0, into a closed solid.</summary>
    public static PolyMesh ExtrudeProfileX(List<Vector2> prof, float width, int mat = 0)
    {
        var m = new PolyMesh();
        float hw = width / 2;
        // right cap must wind CCW around +X; reverse the profile if it does not
        float area = 0;
        for (int i = 0; i < prof.Count; i++) { var a = prof[i]; var b = prof[(i + 1) % prof.Count]; area += a.X * b.Y - b.X * a.Y; }
        // Looking from +X towards the origin, +Y points left. CCW in (y,z) seen with Y right is CW from +X.
        if (area > 0) { prof = new List<Vector2>(prof); prof.Reverse(); }
        int n = prof.Count;
        for (int i = 0; i < n; i++) m.AddVertex(new Vector3(hw, prof[i].X, prof[i].Y));   // right  0..n-1
        for (int i = 0; i < n; i++) m.AddVertex(new Vector3(-hw, prof[i].X, prof[i].Y));  // left   n..2n-1
        var right = new int[n]; var left = new int[n];
        for (int i = 0; i < n; i++) { right[i] = i; left[i] = n + (n - 1 - i); }
        m.AddFace(right, mat);
        m.AddFace(left, mat);
        for (int k = 0; k < n; k++)
        {
            int k2 = (k + 1) % n;
            m.AddFace(new[] { k2, k, n + k, n + k2 }, mat);
        }
        if (Vector3.Dot(m.FaceNormal(0), Vector3.UnitX) < 0) foreach (var f in m.Faces) f.Flip();
        MeshOps.BoxMap(m, null, 1f);
        return m;
    }

    /// <summary>
    /// 3ds Max AEC-style wall following a 2D path (XY), mitred at the corners, closed solid.
    /// Justification is centred on the path.
    /// </summary>
    public static PolyMesh Wall(IReadOnlyList<Vector2> path, float height, float thickness, bool closed = false, int mat = 0)
    {
        var pts = path.ToList();
        if (closed && pts.Count > 2 && Vector2.Distance(pts[0], pts[^1]) < 1e-5f) pts.RemoveAt(pts.Count - 1);
        int n = pts.Count;
        if (n < 2) throw new ArgumentException("A wall needs at least two points.");
        if (closed && n < 3) closed = false;
        float ht = thickness / 2;

        Vector2 Dir(int i) => Vector2.Normalize(pts[(i + 1) % n] - pts[i]);
        Vector2 Left(Vector2 d) => new(-d.Y, d.X);

        var L = new Vector2[n]; var R = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 off;
            bool hasPrev = closed || i > 0, hasNext = closed || i < n - 1;
            if (hasPrev && hasNext)
            {
                var n1 = Left(Dir((i - 1 + n) % n)); var n2 = Left(Dir(i));
                var mdir = n1 + n2;
                if (mdir.LengthSquared() < 1e-8f) mdir = n1; // 180° turn
                mdir = Vector2.Normalize(mdir);
                float cos = MathF.Max(Vector2.Dot(mdir, n1), 0.25f); // limit very sharp mitres
                off = mdir * (ht / cos);
            }
            else off = Left(hasNext ? Dir(i) : Dir(i - 1)) * ht;
            L[i] = pts[i] + off; R[i] = pts[i] - off;
        }

        var m = new PolyMesh();
        int Lb(int i) => i * 4;
        int Rb(int i) => i * 4 + 1;
        int Lt(int i) => i * 4 + 2;
        int Rt(int i) => i * 4 + 3;
        for (int i = 0; i < n; i++)
        {
            m.AddVertex(new Vector3(L[i], 0)); m.AddVertex(new Vector3(R[i], 0));
            m.AddVertex(new Vector3(L[i], height)); m.AddVertex(new Vector3(R[i], height));
        }
        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            int j = (i + 1) % n;
            m.AddFace(new[] { Lb(j), Lb(i), Lt(i), Lt(j) }, mat);   // left side
            m.AddFace(new[] { Rb(i), Rb(j), Rt(j), Rt(i) }, mat);   // right side
            m.AddFace(new[] { Rt(i), Rt(j), Lt(j), Lt(i) }, mat);   // top
            m.AddFace(new[] { Lb(i), Lb(j), Rb(j), Rb(i) }, mat);   // bottom
        }
        if (!closed)
        {
            m.AddFace(new[] { Lb(0), Rb(0), Rt(0), Lt(0) }, mat);               // start cap
            m.AddFace(new[] { Rb(n - 1), Lb(n - 1), Lt(n - 1), Rt(n - 1) }, mat); // end cap
        }
        MeshOps.BoxMap(m, null, 1f);
        return m;
    }
}
