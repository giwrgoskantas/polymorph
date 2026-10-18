using System.Numerics;
using ImGuiNET;

namespace MoloMax.Editor;

public enum Tool { Select, Move, Rotate, Scale }
public enum GizmoHandle { None, X, Y, Z, XY, YZ, ZX, View }

/// <summary>
/// 3ds Max style transform gizmo, laid out and hit-tested in screen space so it keeps a constant size.
/// X red, Y green, Z blue, the active handle turns yellow.
/// </summary>
public sealed class Gizmo
{
    public float SizePixels = 85f;
    public GizmoHandle Hover = GizmoHandle.None;
    public GizmoHandle Active = GizmoHandle.None;

    public static readonly uint[] AxisCol = { Col(0.9f, 0.18f, 0.2f), Col(0.25f, 0.85f, 0.2f), Col(0.25f, 0.45f, 1f) };
    public static readonly uint Yellow = Col(1f, 0.9f, 0f);
    static uint Col(float r, float g, float b, float a = 1) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    public struct Layout
    {
        public bool Valid;
        public Vector2 Centre;
        public Vector2[] AxisEnd;      // screen end of each axis
        public Vector3[] Axis;         // world axis directions
        public float WorldLen;
        public Vector3 Pivot;
    }

    public Layout Compute(Viewport vp, Vector3 pivot, Matrix4x4 orientation)
    {
        var L = new Layout { Axis = new Vector3[3], AxisEnd = new Vector2[3], Pivot = pivot };
        if (!vp.Camera.Project(pivot, vp.W, vp.H, out var c, out var depth)) return L;
        float d = vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, pivot);
        L.WorldLen = vp.Camera.PixelSize(vp.H, d) * SizePixels;
        L.Centre = vp.ToScreen(c);
        L.Axis[0] = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, orientation));
        L.Axis[1] = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, orientation));
        L.Axis[2] = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, orientation));
        for (int i = 0; i < 3; i++)
        {
            vp.Camera.Project(pivot + L.Axis[i] * L.WorldLen, vp.W, vp.H, out var e, out _);
            L.AxisEnd[i] = vp.ToScreen(e);
        }
        L.Valid = true;
        return L;
    }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float l = ab.LengthSquared();
        float t = l > 1e-6f ? Math.Clamp(Vector2.Dot(p - a, ab) / l, 0, 1) : 0;
        return Vector2.Distance(p, a + ab * t);
    }

    static bool InQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cr(Vector2 o, Vector2 x, Vector2 y) => (x.X - o.X) * (y.Y - o.Y) - (x.Y - o.Y) * (y.X - o.X);
        float s1 = Cr(a, b, p), s2 = Cr(b, c, p), s3 = Cr(c, d, p), s4 = Cr(d, a, p);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0 && s4 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0 && s4 <= 0);
    }

    Vector2[] PlaneQuad(in Layout L, int a, int b)
    {
        var c = L.Centre; float k = 0.32f;
        var da = (L.AxisEnd[a] - c) * k; var db = (L.AxisEnd[b] - c) * k;
        return new[] { c, c + da, c + da + db, c + db };
    }

    List<Vector2> Circle(Viewport vp, in Layout L, int axis)
    {
        var pts = new List<Vector2>(49);
        var u = L.Axis[(axis + 1) % 3]; var v = L.Axis[(axis + 2) % 3];
        for (int i = 0; i <= 48; i++)
        {
            float a = MathF.Tau * i / 48;
            var p = L.Pivot + (u * MathF.Cos(a) + v * MathF.Sin(a)) * L.WorldLen * 0.9f;
            if (vp.Camera.Project(p, vp.W, vp.H, out var s, out _)) pts.Add(vp.ToScreen(s));
        }
        return pts;
    }

    public GizmoHandle HitTest(Viewport vp, in Layout L, Tool tool, Vector2 mouse)
    {
        if (!L.Valid) return GizmoHandle.None;
        const float tol = 7f;
        if (tool == Tool.Rotate)
        {
            float best = tol; var h = GizmoHandle.None;
            for (int a = 0; a < 3; a++)
            {
                var pts = Circle(vp, L, a);
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    float dd = SegDist(mouse, pts[i], pts[i + 1]);
                    if (dd < best) { best = dd; h = (GizmoHandle)(a + 1); }
                }
            }
            if (h != GizmoHandle.None) return h;
            float r = Vector2.Distance(mouse, L.Centre);
            float rad = Vector2.Distance(L.Centre, L.AxisEnd[0]);
            float viewR = SizePixels * 1.08f;
            if (MathF.Abs(r - viewR) < tol) return GizmoHandle.View;
            return GizmoHandle.None;
        }
        if (Vector2.Distance(mouse, L.Centre) < 9f) return tool == Tool.Scale ? GizmoHandle.View : GizmoHandle.XY;
        if (tool == Tool.Move)
        {
            if (InQuad(mouse, PlaneQuad(L, 0, 1)[0], PlaneQuad(L, 0, 1)[1], PlaneQuad(L, 0, 1)[2], PlaneQuad(L, 0, 1)[3])) return GizmoHandle.XY;
            var yz = PlaneQuad(L, 1, 2); if (InQuad(mouse, yz[0], yz[1], yz[2], yz[3])) return GizmoHandle.YZ;
            var zx = PlaneQuad(L, 2, 0); if (InQuad(mouse, zx[0], zx[1], zx[2], zx[3])) return GizmoHandle.ZX;
        }
        else if (tool == Tool.Scale)
        {
            // inner triangle = uniform, like Max's scale gizmo centre
            var c = L.Centre;
            var a = c + (L.AxisEnd[0] - c) * 0.4f; var b = c + (L.AxisEnd[1] - c) * 0.4f; var d = c + (L.AxisEnd[2] - c) * 0.4f;
            if (InQuad(mouse, c, a, b, c) || InQuad(mouse, c, b, d, c) || InQuad(mouse, c, d, a, c)) return GizmoHandle.View;
        }
        float bestA = tol; var ha = GizmoHandle.None;
        for (int i = 0; i < 3; i++)
        {
            float dd = SegDist(mouse, L.Centre, L.AxisEnd[i]);
            if (dd < bestA) { bestA = dd; ha = (GizmoHandle)(i + 1); }
        }
        return ha;
    }

    bool IsOn(GizmoHandle h, int axis)
    {
        var cur = Active != GizmoHandle.None ? Active : Hover;
        return cur switch
        {
            GizmoHandle.X => axis == 0, GizmoHandle.Y => axis == 1, GizmoHandle.Z => axis == 2,
            GizmoHandle.XY => axis == 0 || axis == 1, GizmoHandle.YZ => axis == 1 || axis == 2, GizmoHandle.ZX => axis == 2 || axis == 0,
            GizmoHandle.View => true,
            _ => false,
        };
    }

    public void Draw(ImDrawListPtr dl, Viewport vp, in Layout L, Tool tool)
    {
        if (!L.Valid) return;
        var cur = Active != GizmoHandle.None ? Active : Hover;
        string[] names = { "X", "Y", "Z" };
        if (tool == Tool.Rotate)
        {
            for (int a = 0; a < 3; a++)
            {
                var pts = Circle(vp, L, a);
                uint col = cur == (GizmoHandle)(a + 1) ? Yellow : AxisCol[a];
                for (int i = 0; i + 1 < pts.Count; i++) dl.AddLine(pts[i], pts[i + 1], col, 2f);
            }
            dl.AddCircle(L.Centre, SizePixels * 1.08f, cur == GizmoHandle.View ? Yellow : Col(0.75f, 0.75f, 0.75f), 64, 1.5f);
            dl.AddCircle(L.Centre, SizePixels * 0.9f, Col(0.55f, 0.55f, 0.55f, 0.6f), 64, 1f);
            return;
        }
        if (tool == Tool.Move)
        {
            int[][] planes = { new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 0 } };
            GizmoHandle[] ph = { GizmoHandle.XY, GizmoHandle.YZ, GizmoHandle.ZX };
            for (int p = 0; p < 3; p++)
            {
                var q = PlaneQuad(L, planes[p][0], planes[p][1]);
                bool on = cur == ph[p];
                if (on) dl.AddQuadFilled(q[0], q[1], q[2], q[3], Col(1f, 0.9f, 0f, 0.35f));
                dl.AddLine(q[1], q[2], on ? Yellow : AxisCol[planes[p][0]], 1.5f);
                dl.AddLine(q[2], q[3], on ? Yellow : AxisCol[planes[p][1]], 1.5f);
            }
        }
        if (tool == Tool.Scale)
        {
            var c = L.Centre;
            var a = c + (L.AxisEnd[0] - c) * 0.4f; var b = c + (L.AxisEnd[1] - c) * 0.4f; var d = c + (L.AxisEnd[2] - c) * 0.4f;
            uint fill = cur == GizmoHandle.View ? Col(1f, 0.9f, 0f, 0.35f) : Col(0.9f, 0.9f, 0.9f, 0.12f);
            dl.AddTriangleFilled(c, a, b, fill); dl.AddTriangleFilled(c, b, d, fill); dl.AddTriangleFilled(c, d, a, fill);
        }
        for (int i = 0; i < 3; i++)
        {
            uint col = IsOn(cur, i) ? Yellow : AxisCol[i];
            dl.AddLine(L.Centre, L.AxisEnd[i], col, 2.5f);
            var dir = L.AxisEnd[i] - L.Centre;
            if (dir.LengthSquared() > 4)
            {
                var n = Vector2.Normalize(dir); var side = new Vector2(-n.Y, n.X);
                if (tool == Tool.Move) dl.AddTriangleFilled(L.AxisEnd[i] + n * 10, L.AxisEnd[i] + side * 4.5f, L.AxisEnd[i] - side * 4.5f, col);
                else dl.AddRectFilled(L.AxisEnd[i] - new Vector2(4), L.AxisEnd[i] + new Vector2(4), col);
                dl.AddText(L.AxisEnd[i] + n * 16 - new Vector2(4, 7), col, names[i]);
            }
        }
        dl.AddRect(L.Centre - new Vector2(4), L.Centre + new Vector2(4), cur == GizmoHandle.View ? Yellow : Col(1, 1, 1), 0, ImDrawFlags.None, 1.5f);
    }
}
