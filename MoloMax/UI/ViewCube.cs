using System.Numerics;
using ImGuiNET;
using MoloMax.Editor;

namespace MoloMax.UI;

/// <summary>The 3ds Max ViewCube: a shaded cube top-right of each viewport with a compass ring. Click a face to jump to that view; hold and drag the cube to turn the view, the ring to turn it around the vertical.</summary>
public static class ViewCube
{
    struct FaceDef { public Vector3 N; public string Label; public ViewKind View; }
    static readonly FaceDef[] faces =
    {
        new() { N = Vector3.UnitZ, Label = "TOP", View = ViewKind.Top },
        new() { N = -Vector3.UnitZ, Label = "BOTTOM", View = ViewKind.Bottom },
        new() { N = -Vector3.UnitY, Label = "FRONT", View = ViewKind.Front },
        new() { N = Vector3.UnitY, Label = "BACK", View = ViewKind.Back },
        new() { N = Vector3.UnitX, Label = "RIGHT", View = ViewKind.Right },
        new() { N = -Vector3.UnitX, Label = "LEFT", View = ViewKind.Left },
    };

    const float R = 22f;

    static Vector2 Centre(Viewport vp) => new(vp.X + vp.W - R * 2.1f - 8, vp.Y + R * 2.1f + 8);

    static Vector2 P(Viewport vp, Vector3 p)
    {
        var c = Centre(vp); var cam = vp.Camera;
        return c + new Vector2(Vector3.Dot(p, cam.Right), -Vector3.Dot(p, cam.CamUp)) * R;
    }

    static IEnumerable<(FaceDef F, Vector2[] Q, float Facing)> Visible(Viewport vp)
    {
        var fwd = vp.Camera.Forward;
        foreach (var f in faces)
        {
            float d = Vector3.Dot(f.N, fwd);
            if (d > -0.02f) continue;
            int ax = MathF.Abs(f.N.X) > 0.5f ? 0 : MathF.Abs(f.N.Y) > 0.5f ? 1 : 2;
            var u = ax == 0 ? Vector3.UnitY : Vector3.UnitX; var v = ax == 2 ? Vector3.UnitY : Vector3.UnitZ;
            var q = new[] { P(vp, f.N + u + v), P(vp, f.N - u + v), P(vp, f.N - u - v), P(vp, f.N + u - v) };
            yield return (f, q, -d);
        }
    }

    public static void Draw(ImDrawListPtr dl, Viewport vp)
    {
        var ring = new Vector2[49];
        for (int i = 0; i <= 48; i++)
        {
            float a = MathF.Tau * i / 48;
            ring[i] = P(vp, new Vector3(MathF.Cos(a) * 1.9f, MathF.Sin(a) * 1.9f, -1.05f));
        }
        uint ringCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.82f, 0.82f, 0.82f, 0.45f));
        for (int i = 0; i < 48; i++) dl.AddLine(ring[i], ring[i + 1], ringCol, 3.5f);
        var n = P(vp, new Vector3(0, 2.3f, -1.05f));
        dl.AddText(n - new Vector2(4, 7), ringCol, "N");
        var mouse = ImGui.GetIO().MousePos;
        foreach (var (f, q, facing) in Visible(vp).OrderBy(x => x.Facing))
        {
            bool hover = InQuad(mouse, q);
            float g = 0.55f + 0.35f * facing;
            var col = hover ? new Vector4(0.62f, 0.78f, 0.96f, 0.95f) : new Vector4(g, g, g, 0.92f);
            dl.AddQuadFilled(q[0], q[1], q[2], q[3], ImGui.ColorConvertFloat4ToU32(col));
            dl.AddQuad(q[0], q[1], q[2], q[3], 0xFF404040, 1f);
            if (facing > 0.3f)
            {
                var c = (q[0] + q[1] + q[2] + q[3]) / 4;
                var sz = ImGui.CalcTextSize(f.Label) * 0.8f;
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 0.75f, c - sz / 2, 0xFF262626, f.Label);
            }
        }
    }

    static bool InQuad(Vector2 p, Vector2[] q)
    {
        static float Cr(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        float s1 = Cr(q[0], q[1], p), s2 = Cr(q[1], q[2], p), s3 = Cr(q[2], q[3], p), s4 = Cr(q[3], q[0], p);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0 && s4 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0 && s4 <= 0);
    }

    /// <summary>The mouse is on the cube or its ring (a press there drags the view around).</summary>
    public static bool InArea(Viewport vp, Vector2 mouse) => Vector2.Distance(mouse, Centre(vp)) <= R * 2.4f;

    public static bool HitTest(Viewport vp, Vector2 mouse, out ViewKind kind)
    {
        kind = ViewKind.Perspective;
        if (Vector2.Distance(mouse, Centre(vp)) > R * 2.4f) return false;
        foreach (var (f, q, _) in Visible(vp).OrderByDescending(x => x.Facing))
            if (InQuad(mouse, q)) { kind = f.View; return true; }
        return false;
    }
}
