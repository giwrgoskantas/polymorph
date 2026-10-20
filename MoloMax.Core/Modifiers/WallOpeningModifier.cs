using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Tools;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.Modifiers;

/// <summary>A rectangular hole traced on a wall: door, window, passage.</summary>
public sealed class OpeningRect
{
    /// <summary>A point on the wall surface, in the object's space.</summary>
    public Vector3 Origin;
    /// <summary>Outward normal of that surface (object space).</summary>
    public Vector3 Normal = Vector3.UnitY;
    /// <summary>The rectangle on the wall, in metres from <see cref="Origin"/> along the wall's right / up axes.</summary>
    public float Left, Right, Bottom, Top;
    /// <summary>How deep it goes: 0 = right through the wall (thickness found by itself).</summary>
    public float Depth;

    public float Width => Right - Left;
    public float Height => Top - Bottom;
    public OpeningRect Copy() => (OpeningRect)MemberwiseClone();

    /// <summary>The wall's axes: up = world up on the wall, right = along the wall (the same ones the doorway tool uses).</summary>
    public (Vector3 Right, Vector3 Up, Vector3 N) Axes()
    {
        var n = Normal.LengthSquared() > 1e-12f ? Vector3.Normalize(Normal) : Vector3.UnitY;
        var up = Vector3.UnitZ - n * n.Z;
        if (up.LengthSquared() < 0.01f) up = Vector3.UnitY - n * n.Y; // floor / ceiling: a hatch
        up = Vector3.Normalize(up);
        return (Vector3.Normalize(Vector3.Cross(up, n)), up, n);
    }

    /// <summary>A rectangle point in object space.</summary>
    public Vector3 At(float x, float y) { var (r, u, _) = Axes(); return Origin + r * x + u * y; }
}

/// <summary>
/// Wall Opening: rectangular doors and windows traced straight on the wall, each side movable, no cutter object and no
/// boolean to set up. Thick walls are cut right through with clean straight faces (and the reveal inside); single
/// surfaces (MLO room boxes) get the hole in the face.
/// </summary>
public sealed class WallOpeningModifier : Modifier
{
    public List<OpeningRect> Openings = new();

    public WallOpeningModifier() { Name = "Wall Opening"; }
    public override string TypeName => "Wall Opening";
    public override ModifierCategory Category => ModifierCategory.Generate;

    public override Modifier Clone()
    {
        var c = (WallOpeningModifier)base.Clone();
        c.Openings = Openings.Select(o => o.Copy()).ToList();
        return c;
    }

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        foreach (var op in Openings) m = Cut(m, op);
        return m;
    }

    /// <summary>One opening cut into the mesh (the mesh comes back unchanged when it can't be cut).</summary>
    public static PolyMesh Cut(PolyMesh m, OpeningRect op)
    {
        if (op.Width < 0.01f || op.Height < 0.01f || m.Faces.Count == 0) return m;
        var (right, up, n) = op.Axes();
        var centre = op.At((op.Left + op.Right) * 0.5f, (op.Bottom + op.Top) * 0.5f);
        float depth = op.Depth > 0 ? op.Depth : Thickness(m, centre, n);
        if (depth <= 0) return Surface(m, op);
        // a box from just in front of the wall to just behind it, cut out (clean faces by the boolean's own cleaning)
        const float margin = 0.02f;
        float len = depth + 2 * margin;
        var cutter = Primitives.Box(op.Width, len, op.Height);
        var basis = new Matrix4x4(right.X, right.Y, right.Z, 0, -n.X, -n.Y, -n.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
        var t = op.At((op.Left + op.Right) * 0.5f, op.Bottom) - n * (len * 0.5f - margin);
        basis.Translation = t;
        try
        {
            var r = Csg.Apply(m, Matrix4x4.Identity, cutter, basis, Csg.Op.Subtract);
            return r.Faces.Count > 0 ? r : m;
        }
        catch { return m; }
    }

    /// <summary>Distance through the wall: from the surface, the first face behind it that faces the other way.</summary>
    public static float Thickness(PolyMesh m, Vector3 onSurface, Vector3 n)
    {
        var ray = new Ray(onSurface - n * 1e-3f, -n);
        float best = float.MaxValue;
        foreach (var f in m.Faces)
        {
            var fn = m.FaceNormal(f);
            if (Vector3.Dot(fn, n) > -0.5f) continue; // only the back of the wall
            foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
            {
                float d = Picking.RayTriangle(ray, m.Verts[f.V[a]], m.Verts[f.V[b]], m.Verts[f.V[c]]);
                if (d > 1e-4f && d < best) best = d;
            }
        }
        return best < float.MaxValue ? best + 1e-3f : 0;
    }

    /// <summary>Single surface (MLO room box): the hole goes in the face under the rectangle.</summary>
    static PolyMesh Surface(PolyMesh m, OpeningRect op)
    {
        var (right, up, n) = op.Axes();
        var c = op.At((op.Left + op.Right) * 0.5f, (op.Bottom + op.Top) * 0.5f);
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi];
            var fn = m.FaceNormal(f);
            if (Vector3.Dot(fn, n) < 0.999f || MathF.Abs(Vector3.Dot(m.Verts[f.V[0]] - c, fn)) > 1e-3f) continue;
            var o = m.FaceCenter(f);
            var pr = f.V.Select(v => new Vector2(Vector3.Dot(m.Verts[v] - o, right), Vector3.Dot(m.Verts[v] - o, up))).ToArray();
            float r0 = pr.Min(p => p.X), r1 = pr.Max(p => p.X), u0 = pr.Min(p => p.Y);
            var cc = new Vector2(Vector3.Dot(c - o, right), Vector3.Dot(c - o, up));
            if (cc.X < r0 || cc.X > r1 || cc.Y < u0) continue;
            var copy = m.Clone();
            float offset = cc.X - (r0 + r1) * 0.5f;
            float sill = Vector3.Dot(op.At(0, op.Bottom) - o, up) - u0;
            var res = Doorway.Cut(copy, fi, op.Width, op.Height, offset, MathF.Max(0, sill));
            return res.Ok ? copy : m;
        }
        return m;
    }
}
