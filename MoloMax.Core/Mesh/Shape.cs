using System.Numerics;
using MoloMax.Core.Tools;

namespace MoloMax.Core.Mesh;

/// <summary>How a line (3ds Max spline) shows: the bare line, a renderable section swept along it (rectangle, round),
/// a door / window casing profile swept along it, or an extrusion of the shape.</summary>
public enum ShapeRender { Line, Rectangular, Round, DoorFrame, Extrude }

/// <summary>
/// A line drawn point by point (3ds Max Create > Shapes > Line): its points in object space, open or closed, and how it
/// becomes a mesh. The object's mesh is rebuilt from it until "Convert to Editable Poly".
/// </summary>
public sealed class ShapeData
{
    public List<Vector3> Points = new();
    public bool Closed;
    public ShapeRender Render = ShapeRender.Line;
    /// <summary>Rectangular: width across the line (in the shape's plane) and depth out of the plane, metres.</summary>
    public float Width = 0.1f, Depth = 0.05f;
    /// <summary>Round: diameter and sides.</summary>
    public float Thickness = 0.05f; public int Sides = 8;
    /// <summary>Door frame: casing style (0 square, 1 stepped, 2 chamfer, 3 round), width (away from the opening), depth (off the wall).</summary>
    public int FrameStyle = 1; public float FrameWidth = 0.12f, FrameDepth = 0.035f;
    /// <summary>Extrude: amount along the shape's plane normal (a closed shape gets caps).</summary>
    public float Amount = 0.2f;
    /// <summary>Puts the section on the other side of the line / the other face of the plane.</summary>
    public bool Flip;
    /// <summary>The side the section stands out to (door frame / beam / extrude), set from the view when it is drawn:
    /// the plane normal is turned toward it, so a casing drawn on a wall sticks out of the wall, toward the viewer.</summary>
    public Vector3 Facing;

    public ShapeData Copy()
    {
        var c = (ShapeData)MemberwiseClone();
        c.Points = new List<Vector3>(Points);
        return c;
    }
}

public static class ShapeBuilder
{
    /// <summary>Normal of the plane the points lie in (Newell); a straight line takes a horizontal side, so a vertical
    /// line gets a normal along Y or X and a horizontal one stands up (Z).</summary>
    public static Vector3 PlaneNormal(IReadOnlyList<Vector3> p, bool closed)
    {
        var n = Vector3.Zero;
        int cnt = p.Count;
        for (int i = 0; i < cnt; i++) { var a = p[i]; var b = p[(i + 1) % cnt]; n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
        if (n.LengthSquared() > 1e-10f) return Vector3.Normalize(n);
        // collinear: any normal perpendicular to the line, preferably Z (a line on the floor) else horizontal
        var d = cnt >= 2 ? p[^1] - p[0] : Vector3.UnitX;
        if (d.LengthSquared() < 1e-12f) return Vector3.UnitZ;
        d = Vector3.Normalize(d);
        if (MathF.Abs(d.Z) < 0.9f) return Vector3.UnitZ;
        var side = Vector3.Normalize(Vector3.Cross(d, Vector3.UnitX));
        return side;
    }

    /// <summary>The mesh of a shape (object space). The bare line keeps only its points (they still snap).</summary>
    public static PolyMesh Build(ShapeData s)
    {
        var pts = Clean(s.Points, s.Closed);
        var m = new PolyMesh();
        if (pts.Count < 2 || s.Render == ShapeRender.Line) { foreach (var p in pts) m.AddVertex(p); return m; }
        bool closed = s.Closed && pts.Count >= 3;
        var n = PlaneNormal(pts, closed);
        if (s.Facing.LengthSquared() > 1e-8f && Vector3.Dot(n, s.Facing) < 0) n = -n;
        if (s.Flip && s.Render is ShapeRender.Extrude) n = -n;
        switch (s.Render)
        {
            case ShapeRender.Rectangular:
                {
                    float w = MathF.Max(s.Width, 1e-4f) / 2, d = MathF.Max(s.Depth, 1e-4f) / 2;
                    Sweep(m, pts, closed, n, new List<Vector2> { new(-w, -d), new(w, -d), new(w, d), new(-w, d) }, outward: false, s.Flip);
                    break;
                }
            case ShapeRender.Round:
                {
                    int k = Math.Clamp(s.Sides, 3, 64); float r = MathF.Max(s.Thickness, 1e-4f) / 2;
                    var prof = Enumerable.Range(0, k).Select(i => new Vector2(MathF.Cos(MathF.Tau * i / k) * r, MathF.Sin(MathF.Tau * i / k) * r)).ToList();
                    Sweep(m, pts, closed, n, prof, outward: false, s.Flip);
                    break;
                }
            case ShapeRender.DoorFrame:
                {
                    var ts = CasingSettings(s);
                    // the trim profile (out, up): "up" goes away from the opening (across the line, outward), "out" off the wall
                    var prof = TrimSweep.Profile(ts).Select(q => new Vector2(q.Y, q.X)).ToList();
                    Sweep(m, pts, closed, n, prof, outward: true, s.Flip);
                    break;
                }
            case ShapeRender.Extrude: Extrude(m, pts, closed, n * s.Amount); break;
        }
        MeshOps.RecalcNormals(m, null, false);
        foreach (var f in m.Faces) for (int i = 0; i < f.Col.Length; i++) f.Col[i] = new Vector4(0, 1, 0, 1); // GTA green
        MeshOps.BoxMap(m, null, 1f);
        return m;
    }

    public static TrimSettings CasingSettings(ShapeData s)
    {
        var t = TrimSettings.Frame(TrimKind.DoorFrame);
        t.Height = MathF.Max(s.FrameWidth, 0.01f); t.Depth = MathF.Max(s.FrameDepth, 0.002f); t.CapEnds = true;
        switch (s.FrameStyle)
        {
            case 0: t.Inner = TrimEdge.Square; t.Outer = TrimEdge.Square; break;
            case 2: t.Inner = TrimEdge.Chamfer; t.Outer = TrimEdge.Chamfer; t.InnerAmount = 0.5f; t.OuterAmount = 0.5f; break;
            case 3: t.Inner = TrimEdge.Round; t.Outer = TrimEdge.Round; t.InnerAmount = 0.6f; t.OuterAmount = 0.6f; t.Segments = 4; break;
            default: break; // stepped (the frame preset)
        }
        return t;
    }

    static List<Vector3> Clean(List<Vector3> src, bool closed)
    {
        var r = new List<Vector3>();
        foreach (var p in src) if (r.Count == 0 || Vector3.Distance(r[^1], p) > 1e-5f) r.Add(p);
        if (closed && r.Count > 2 && Vector3.Distance(r[0], r[^1]) < 1e-5f) r.RemoveAt(r.Count - 1);
        return r;
    }

    /// <summary>
    /// Sweeps a closed 2D section (x across the line in the shape's plane, y along the plane normal) along the points, with
    /// mitred corners. <paramref name="outward"/>: x grows away from the middle of the shape (a casing grows away from its
    /// opening); else x is centred on the line.
    /// </summary>
    static void Sweep(PolyMesh m, List<Vector3> pts, bool closed, Vector3 n, List<Vector2> prof, bool outward, bool flip)
    {
        int np = pts.Count, ns = closed ? np : np - 1;
        var segSide = new Vector3[ns];
        for (int k = 0; k < ns; k++)
        {
            var d = Vector3.Normalize(pts[(k + 1) % np] - pts[k]);
            var sd = Vector3.Cross(n, d);
            segSide[k] = sd.LengthSquared() > 1e-10f ? Vector3.Normalize(sd) : Vector3.UnitX;
        }
        // which way is "away from the middle": vote over the segments
        float sign = 1;
        if (outward)
        {
            var c = Vector3.Zero; foreach (var p in pts) c += p; c /= np;
            float vote = 0;
            for (int k = 0; k < ns; k++) { var mid = (pts[k] + pts[(k + 1) % np]) * 0.5f; vote += Vector3.Dot(segSide[k], mid - c) * Vector3.Distance(pts[k], pts[(k + 1) % np]); }
            sign = vote >= 0 ? 1 : -1;
        }
        if (flip) sign = -sign;
        // a side per point: the mitre of the two segments around it, scaled so the section keeps its width
        var side = new Vector3[np];
        for (int i = 0; i < np; i++)
        {
            bool hasPrev = closed || i > 0, hasNext = closed || i < np - 1;
            Vector3 a = hasPrev ? segSide[(i - 1 + ns) % ns] : segSide[i], b = hasNext ? segSide[i % ns] : a;
            var mtr = a + b;
            if (mtr.LengthSquared() < 1e-8f) mtr = b;
            mtr = Vector3.Normalize(mtr);
            float cos = MathF.Max(Vector3.Dot(mtr, b), 0.25f); // limit very sharp corners
            side[i] = mtr / cos * sign;
        }
        int nq = prof.Count, rings = np;
        var idx = new int[rings, nq];
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < nq; j++)
                idx[i, j] = m.AddVertex(pts[i] + side[i] * prof[j].X + n * prof[j].Y);
        int last = closed ? rings : rings - 1;
        for (int i = 0; i < last; i++)
        {
            int i2 = (i + 1) % rings;
            for (int j = 0; j < nq; j++)
            {
                int j2 = (j + 1) % nq;
                m.AddFace(new[] { idx[i, j], idx[i2, j], idx[i2, j2], idx[i, j2] });
            }
        }
        if (!closed)
        {
            m.AddFace(Enumerable.Range(0, nq).Select(j => idx[0, j]).Reverse().ToArray());
            m.AddFace(Enumerable.Range(0, nq).Select(j => idx[rings - 1, j]).ToArray());
        }
    }

    /// <summary>Extrusion: a closed shape becomes a solid (two caps and the sides), an open one a strip (a wall).</summary>
    static void Extrude(PolyMesh m, List<Vector3> pts, bool closed, Vector3 dir)
    {
        int np = pts.Count;
        var bot = pts.Select(p => m.AddVertex(p)).ToArray();
        var top = pts.Select(p => m.AddVertex(p + dir)).ToArray();
        int last = closed ? np : np - 1;
        for (int i = 0; i < last; i++)
        {
            int i2 = (i + 1) % np;
            m.AddFace(new[] { bot[i], bot[i2], top[i2], top[i] });
        }
        if (closed)
        {
            m.AddFace(bot.Reverse().ToArray());
            m.AddFace(top.ToArray());
        }
    }
}
