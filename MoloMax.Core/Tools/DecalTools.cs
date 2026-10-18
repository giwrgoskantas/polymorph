using System.Numerics;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Tools;

/// <summary>A generated decal: its mesh (in its own space), where it sits in the scene and its texture.</summary>
public sealed class DecalResult
{
    public PolyMesh Mesh;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public RgbaImage Texture;
    public string Message;
}

public sealed class GlowSettings
{
    public int Resolution = 512;
    public float Padding = 0;          // 0 = auto (halo * 1.6 + 0.02)
    public float OffsetBehind = 0.02f;
    public float CoreSize = 0.015f, CoreIntensity = 1f, HaloSize = 0.08f, HaloIntensity = 0.55f, Opacity = 1f;
    public Vector3 Colour = new(0.45f, 0.85f, 1f);
    /// <summary>Fraction of the shorter side over which the plane border fades to transparent.</summary>
    public float EdgeFade = 0.08f;
    /// <summary>Direction the sign faces (the glow goes behind it); null = from the open side of the mesh, else -Y.</summary>
    public Vector3? Facing;
}

public sealed class ShadowSettings
{
    public float Margin = 0.15f, ZOffset = 0.002f, Opacity = 0.55f, Softness = 0.6f, Roundness = 3f;
}

/// <summary>Sign glow (halo plane behind lettering) and fake contact shadow (blob decal under an object).</summary>
public static class DecalTools
{
    /// <summary>
    /// Traces the silhouette of the given meshes seen from their average facing direction, bakes a core + halo
    /// glow and returns an emissive quad placed just behind the rearmost point. Vertex colour is white
    /// (emissive rule) with the border ring faded to alpha 0 so no lit rectangle shows.
    /// </summary>
    public static DecalResult SignGlow(IReadOnlyList<(PolyMesh Mesh, Matrix4x4 World)> sources, GlowSettings s)
    {
        var tris = new List<(Vector3, Vector3, Vector3)>();
        var nAcc = Vector3.Zero;
        foreach (var (mesh, w) in sources)
            foreach (var f in mesh.Faces)
                foreach (var (a, b, c) in Triangulator.Triangulate(mesh, f))
                {
                    var pa = Vector3.Transform(mesh.Verts[f.V[a]], w); var pb = Vector3.Transform(mesh.Verts[f.V[b]], w); var pc = Vector3.Transform(mesh.Verts[f.V[c]], w);
                    tris.Add((pa, pb, pc));
                    nAcc += Vector3.Cross(pb - pa, pc - pa) * 0.5f;
                }
        if (tris.Count == 0) return new DecalResult { Message = "No triangles to trace." };
        float area = 0; foreach (var (a, b, c) in tris) area += Vector3.Cross(b - a, c - a).Length() * 0.5f;
        // closed lettering has no net facing direction: fall back to -Y (GTA "front") unless told otherwise
        var fwd = s.Facing.HasValue && s.Facing.Value.LengthSquared() > 0 ? Vector3.Normalize(s.Facing.Value)
                : nAcc.Length() > area * 0.2f ? Vector3.Normalize(nAcc) : -Vector3.UnitY;
        var up = MathF.Abs(Vector3.Dot(fwd, Vector3.UnitZ)) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        var u = Vector3.Normalize(Vector3.Cross(up, fwd));
        var v = Vector3.Normalize(Vector3.Cross(fwd, u));

        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, dmin = float.MaxValue;
        foreach (var (a, b, c) in tris)
            foreach (var p in new[] { a, b, c })
            {
                float px = Vector3.Dot(p, u), py = Vector3.Dot(p, v);
                x0 = MathF.Min(x0, px); x1 = MathF.Max(x1, px); y0 = MathF.Min(y0, py); y1 = MathF.Max(y1, py);
                dmin = MathF.Min(dmin, Vector3.Dot(p, fwd));
            }
        float padFrac = s.Padding > 0 ? s.Padding : s.HaloSize * 1.6f + 0.02f;
        float pad = MathF.Max(x1 - x0, y1 - y0) * padFrac;
        x0 -= pad; x1 += pad; y0 -= pad; y1 += pad;
        float sx = x1 - x0, sy = y1 - y0;
        int W, H;
        if (sx >= sy) { W = s.Resolution; H = Math.Max(8, (int)MathF.Round(s.Resolution * sy / sx)); }
        else { H = s.Resolution; W = Math.Max(8, (int)MathF.Round(s.Resolution * sx / sy)); }
        W = DdsEncoder.NextPow2(W); H = DdsEncoder.NextPow2(H);

        // rasterise the silhouette (row 0 = top of the sign = max v)
        var mask = new float[W * H];
        Vector2 Px(Vector3 p) => new((Vector3.Dot(p, u) - x0) / sx * (W - 1), (y1 - Vector3.Dot(p, v)) / sy * (H - 1));
        foreach (var (a, b, c) in tris)
        {
            var A = Px(a); var B = Px(b); var C = Px(c);
            float det = (B.X - A.X) * (C.Y - A.Y) - (C.X - A.X) * (B.Y - A.Y);
            if (MathF.Abs(det) < 1e-9f) continue;
            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(A.X, MathF.Min(B.X, C.X)))), maxX = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(A.X, MathF.Max(B.X, C.X))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(A.Y, MathF.Min(B.Y, C.Y)))), maxY = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(A.Y, MathF.Max(B.Y, C.Y))));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float l1 = ((B.X - x) * (C.Y - y) - (C.X - x) * (B.Y - y)) / det;
                    float l2 = ((C.X - x) * (A.Y - y) - (A.X - x) * (C.Y - y)) / det;
                    float l3 = 1 - l1 - l2;
                    if (l1 >= -1e-4f && l2 >= -1e-4f && l3 >= -1e-4f) mask[y * W + x] = 1;
                }
        }
        var tex = ProcTextures.Halo(mask, W, H, s.Colour, s.CoreSize, s.CoreIntensity, s.HaloSize, s.HaloIntensity, s.Opacity);

        // plane: a 4x4 grid whose outer ring has alpha 0 (edge fade)
        float hx = sx / 2, hy = sy / 2;
        float fade = Math.Clamp(s.EdgeFade, 0, 0.49f) * MathF.Min(sx, sy);
        float[] xs = { -hx, -hx + fade, hx - fade, hx }, ys = { -hy, -hy + fade, hy - fade, hy };
        var mesh2 = new PolyMesh();
        var idx = new int[4, 4];
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) idx[i, j] = mesh2.AddVertex(new Vector3(xs[i], ys[j], 0));
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                var f = mesh2.AddFace(new[] { idx[i, j], idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1] });
                (int, int)[] cs = { (i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1) };
                for (int c = 0; c < 4; c++)
                {
                    var (ci, cj) = cs[c];
                    f.UV[c] = new Vector2((xs[ci] + hx) / sx, 1 - (ys[cj] + hy) / sy);
                    bool ring = ci == 0 || ci == 3 || cj == 0 || cj == 3;
                    f.Col[c] = new Vector4(1, 1, 1, ring && fade > 0 ? 0 : 1);
                }
            }
        var center = u * (x0 + hx) + v * (y0 + hy) + fwd * (dmin - s.OffsetBehind);
        // local axes: X = u, Y = v, Z = fwd (quad faces forward)
        var rot = Quaternion.CreateFromRotationMatrix(new Matrix4x4(u.X, u.Y, u.Z, 0, v.X, v.Y, v.Z, 0, fwd.X, fwd.Y, fwd.Z, 0, 0, 0, 0, 1));
        return new DecalResult { Mesh = mesh2, Position = center, Rotation = Quaternion.Normalize(rot), Texture = tex, Message = $"Glow {W}x{H}" };
    }

    /// <summary>A soft blob shadow quad on the floor under the world bounds of an object.</summary>
    public static DecalResult FakeShadow(Vector3 worldMin, Vector3 worldMax, ShadowSettings s)
    {
        float hx = (worldMax.X - worldMin.X) / 2 + s.Margin, hy = (worldMax.Y - worldMin.Y) / 2 + s.Margin;
        var m = new PolyMesh();
        var a = m.AddVertex(new Vector3(-hx, -hy, 0)); var b = m.AddVertex(new Vector3(hx, -hy, 0));
        var c = m.AddVertex(new Vector3(hx, hy, 0)); var d = m.AddVertex(new Vector3(-hx, hy, 0));
        var f = m.AddFace(new[] { a, b, c, d });
        f.UV[0] = new Vector2(0, 1); f.UV[1] = new Vector2(1, 1); f.UV[2] = new Vector2(1, 0); f.UV[3] = new Vector2(0, 0);
        for (int i = 0; i < 4; i++) f.Col[i] = new Vector4(0, 0.7f, 0, 1);
        return new DecalResult
        {
            Mesh = m,
            Position = new Vector3((worldMin.X + worldMax.X) / 2, (worldMin.Y + worldMax.Y) / 2, worldMin.Z + s.ZOffset),
            Texture = ProcTextures.BlobShadow(s.Opacity, s.Softness, s.Roundness),
        };
    }
}

/// <summary>Vertex colour tools: GTA colours, interior height gradient, sun glow around openings.</summary>
public static class VertexColours
{
    public static readonly Vector4 Green = new(0, 1, 0, 1), DarkGreen = new(0, 0.55f, 0, 1), White = new(1, 1, 1, 1), Red = new(1, 0, 0, 1);

    // stored as sRGB bytes / 255 (what the game reads)
    public static Vector3 Floor = new(69 / 255f, 221 / 255f, 63 / 255f), Lower = new(25 / 255f, 144 / 255f, 25 / 255f),
                          Upper = new(63 / 255f, 149 / 255f, 237 / 255f), Ceiling = new(0, 39 / 255f, 97 / 255f);
    static readonly Vector3 SunCore = new(1f, 234 / 255f, 89 / 255f), SunEdge = new(243 / 255f, 118 / 255f, 0);

    static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>Interior gradient by world height: floor green, dark green, blue, ceiling (alpha kept).</summary>
    public static void InteriorGradient(PolyMesh m, Matrix4x4 world, float split = 0.55f, float zMin = float.NaN, float zMax = float.NaN)
    {
        if (float.IsNaN(zMin))
        {
            zMin = float.MaxValue; zMax = float.MinValue;
            foreach (var p in m.Verts) { float z = Vector3.Transform(p, world).Z; zMin = MathF.Min(zMin, z); zMax = MathF.Max(zMax, z); }
        }
        float span = MathF.Max(1e-6f, zMax - zMin), lo = split - 0.08f, hi = split + 0.08f;
        foreach (var f in m.Faces)
            for (int c = 0; c < f.V.Length; c++)
            {
                float t = (Vector3.Transform(m.Verts[f.V[c]], world).Z - zMin) / span;
                Vector3 rgb = t <= lo ? Vector3.Lerp(Floor, Lower, Smooth(t / lo))
                            : t >= hi ? Vector3.Lerp(Upper, Ceiling, Smooth((t - hi) / (1 - hi)))
                            : Vector3.Lerp(Lower, Upper, Smooth((t - lo) / (hi - lo)));
                f.Col[c] = new Vector4(rgb, f.Col[c].W);
            }
    }

    /// <summary>Warm sun glow around the given world points (a window or door opening), fading over radius.</summary>
    public static void SunGlow(PolyMesh m, Matrix4x4 world, IReadOnlyList<Vector3> centres, float radius = 2.5f)
    {
        if (centres.Count == 0) return;
        foreach (var f in m.Faces)
            for (int c = 0; c < f.V.Length; c++)
            {
                var p = Vector3.Transform(m.Verts[f.V[c]], world);
                float d = centres.Min(q => Vector3.Distance(p, q));
                if (d > radius) continue;
                float k = MathF.Pow(1 - d / radius, 1.6f);
                var target = Vector3.Lerp(SunEdge, SunCore, k);
                var cur = new Vector3(f.Col[c].X, f.Col[c].Y, f.Col[c].Z);
                f.Col[c] = new Vector4(cur * (1 - k) + target * k, f.Col[c].W);
            }
    }
}
