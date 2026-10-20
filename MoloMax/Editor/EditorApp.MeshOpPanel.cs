using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Blender-style comfort for the edit tools:
///  * an extrude arrow on the selected polygons (drag it to extrude, like Blender's Extrude Region tool gizmo),
///  * during extrude: the arrow follows the new faces with the distance written next to it,
///  * after extrude / inset / chamfer: "Adjust Last Operation" panel at the bottom of the view; changing a value redoes the operation.
/// </summary>
public sealed partial class EditorApp
{
    sealed class LastMeshOp
    {
        public ModalKind Kind;
        public SceneObject Obj;
        public PolyMesh BaseMesh;
        public PolySelection BaseSel;
        public float Value;
        public bool Flip;
        public int Repeat = 1;
        public int Segments = 1;
        public int Version;
    }
    LastMeshOp lastMeshOp;
    /// <summary>Sides of the last bevel / new edges of the last connect (kept for the next one, like 3ds Max).</summary>
    int chamferSegments = 1, connectSegments = 1;
    bool extrudeHandleHover;

    /// <summary>Runs extrude / inset / chamfer from a base mesh with a value (shared by the modal tool and the panel).</summary>
    void RunMeshOp(ModalKind kind, SceneObject o, PolyMesh baseMesh, PolySelection baseSel, float value, int repeat = 1, int segments = 1)
    {
        o.Mesh = baseMesh.Clone();
        var sel = baseSel.Clone();
        subSel[o.Id] = sel;
        float s = (MathF.Abs(o.Scale.X) + MathF.Abs(o.Scale.Y) + MathF.Abs(o.Scale.Z)) / 3f;
        float local = value / MathF.Max(s, 1e-4f);
        if (kind == ModalKind.Connect)
        {
            sel.Edges = MeshOps.ConnectEdges(o.Mesh, sel.Edges, segments);
            o.Mesh.Validate(); sel.Sanitise(o.Mesh); o.Touch();
            return;
        }
        for (int i = 0; i < Math.Max(1, repeat); i++)
        {
            if (kind == ModalKind.Extrude) MeshOps.ExtrudeFaces(o.Mesh, sel.Faces, local);
            else if (kind == ModalKind.Chamfer) { if (local > 1e-5f && sel.Edges.Count > 0) MeshOps.ChamferEdges(o.Mesh, sel.Edges.ToList(), local, segments); sel.Edges.Clear(); }
            else MeshOps.InsetFaces(o.Mesh, sel.Faces, local);
        }
        o.Touch();
    }

    /// <summary>Called when an extrude / inset / chamfer modal is confirmed.</summary>
    void RememberMeshOp()
    {
        if (modal.Obj == null || modal.BaseMesh == null || float.IsNaN(modal.LastValue)) return;
        lastMeshOp = new LastMeshOp { Kind = modal.Kind, Obj = modal.Obj, BaseMesh = modal.BaseMesh, BaseSel = modal.BaseSel, Value = modal.LastValue, Version = modal.Obj.Version, Segments = modal.Kind == ModalKind.Chamfer ? chamferSegments : 1 };
        lastAdd = null; cubeProj = null;
    }

    void DrawMeshOpPanel()
    {
        var op = lastMeshOp; if (op == null) return;
        // gone stale: the object changed since (another edit, undo...)
        if (Scene.Find(op.Obj.Id) != op.Obj || op.Obj.Version != op.Version || Level == SubObject.Object) { lastMeshOp = null; return; }
        var vp = Viewports[ActiveViewport];
        ImGui.SetNextWindowPos(new Vector2(vp.X + 10, vp.Y + vp.H - 10), ImGuiCond.Always, new Vector2(0, 1));
        ImGui.SetNextWindowSizeConstraints(new Vector2(260, 0), new Vector2(340, 400));
        ImGui.SetNextWindowCollapsed(!adjustOpen, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);
        string title = op.Kind switch { ModalKind.Extrude => "Extrude Region", ModalKind.Inset => "Inset Faces", ModalKind.Connect => "Connect", _ => "Chamfer" };
        bool open = ImGui.Begin($"Adjust Last Operation: {title}###meshop", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        adjustOpen = !ImGui.IsWindowCollapsed();
        if (open)
        {
            bool ch = false;
            if (op.Kind == ModalKind.Connect)
            {
                ImGui.AlignTextToFramePadding(); Ui.Text("Segments"); ImGui.SameLine(100); ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##cseg", ref op.Segments, 1, 16)) { connectSegments = op.Segments; ch = true; }
                Theme.Tip("Number of new edges across each face (evenly spaced).");
                if (ch) { RunMeshOp(op.Kind, op.Obj, op.BaseMesh, op.BaseSel, 0, 1, op.Segments); op.Version = op.Obj.Version; Dirty = true; }
                Ui.TextDisabled("F9 shows / hides this panel");
                ImGui.End();
                return;
            }
            float v = op.Flip ? -op.Value : op.Value;
            ImGui.AlignTextToFramePadding(); Ui.Text(op.Kind == ModalKind.Extrude ? "Offset" : op.Kind == ModalKind.Inset ? "Thickness" : "Width"); ImGui.SameLine(100); ImGui.SetNextItemWidth(-1);
            if (ImGui.DragFloat("##mov", ref op.Value, 0.005f, op.Kind == ModalKind.Extrude ? -100 : 0, 100, "%.3f m")) ch = true;
            if (op.Kind == ModalKind.Extrude)
            {
                if (ImGui.Checkbox("Flip (extrude inward)", ref op.Flip)) ch = true;
                ImGui.AlignTextToFramePadding(); Ui.Text("Steps"); ImGui.SameLine(100); ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##mrep", ref op.Repeat, 1, 10)) ch = true;
                Theme.Tip("Extrude several times the same distance (segments along the extrusion).");
            }
            if (op.Kind == ModalKind.Chamfer)
            {
                ImGui.AlignTextToFramePadding(); Ui.Text("Segments"); ImGui.SameLine(100); ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##chseg", ref op.Segments, 1, 16)) { chamferSegments = op.Segments; ch = true; }
                Theme.Tip("Sides of the bevel: 1 = flat chamfer, more = rounded (mouse wheel while dragging).");
            }
            float b4 = (ImGui.GetContentRegionAvail().X - 12) / 4;
            foreach (var q in op.Kind == ModalKind.Extrude ? new[] { 0.1f, 0.5f, 1f, 2.5f } : new[] { 0.01f, 0.02f, 0.05f, 0.1f })
            {
                if (q != (op.Kind == ModalKind.Extrude ? 0.1f : 0.01f)) ImGui.SameLine(0, 4);
                if (ImGui.Button($"{q:0.###}##mq{q}", new Vector2(b4, 0))) { op.Value = q; ch = true; }
            }
            if (ch)
            {
                float val = op.Kind == ModalKind.Extrude && op.Flip ? -MathF.Abs(op.Value) : op.Value;
                RunMeshOp(op.Kind, op.Obj, op.BaseMesh, op.BaseSel, val, op.Kind == ModalKind.Extrude ? op.Repeat : 1, op.Segments);
                op.Version = op.Obj.Version; Dirty = true;
            }
            Ui.TextDisabled("F9 shows / hides this panel");
        }
        ImGui.End();
    }

    // ---------------------------------------------------------------- extrude arrow

    bool ExtrudeHandle(Viewport vp, out Vector2 a, out Vector2 b, out Vector3 pivot, out Vector3 normal)
    {
        a = b = default; pivot = normal = default;
        var cur = Current;
        if (!Settings.ExtrudeArrow || cur == null || !(Level is SubObject.Polygon or SubObject.Element) || ModalActive || transforming || SculptMode || paintMode) return false;
        var faces = SubSel(cur).Faces;
        if (faces.Count == 0 || faces.Count > 20000) return false;
        var m = cur.Mesh; var w = cur.World;
        var n = Vector3.Zero; var c = Vector3.Zero; float area = 0;
        foreach (var f in faces)
        {
            if (f >= m.Faces.Count) continue;
            float ar = MathF.Max(m.FaceArea(m.Faces[f]), 1e-6f);
            n += m.FaceNormal(f) * ar; c += m.FaceCenter(f) * ar; area += ar;
        }
        if (area <= 0) return false;
        c /= area;
        Matrix4x4.Invert(w, out var inv);
        var wn = Vector3.TransformNormal(n, Matrix4x4.Transpose(inv));
        if (wn.LengthSquared() < 1e-12f) return false;
        normal = Vector3.Normalize(wn); pivot = Vector3.Transform(c, w);
        if (!vp.Camera.Project(pivot, vp.W, vp.H, out var s0, out _)) return false;
        float px = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, pivot));
        if (!vp.Camera.Project(pivot + normal * px * 115, vp.W, vp.H, out var s1, out _)) return false;
        a = vp.ToScreen(s0); b = vp.ToScreen(s1);
        if (Vector2.Distance(a, b) < 12) return false; // pointing at the camera: the normal gizmo would be unusable
        return true;
    }

    /// <summary>Hover / press on the extrude arrow (before the normal click handling). True when it took the click.</summary>
    bool ExtrudeHandleInput(Viewport hv, bool uiMouse)
    {
        extrudeHandleHover = false;
        if (hv == null || uiMouse || Tool == Tool.Select && false) return false;
        if (!ExtrudeHandle(hv, out var a, out var b, out _, out _)) return false;
        // the tip (a circle of 9 px) is the handle; the shaft too, past the transform gizmo centre
        var mp = Input.Mouse;
        var ab = b - a; float t = Math.Clamp(Vector2.Dot(mp - a, ab) / ab.LengthSquared(), 0, 1);
        float d = Vector2.Distance(mp, a + ab * t);
        extrudeHandleHover = (t > 0.75f && d < 7) || Vector2.Distance(mp, b) < 10;
        if (extrudeHandleHover && Input.Pressed(System.Windows.Forms.MouseButtons.Left) && !Input.Alt)
        {
            BeginModal(ModalKind.Extrude);
            extrudeDragging = true;
            return true;
        }
        return false;
    }
    bool extrudeDragging;

    void ExtrudeHandleOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (modal.Kind == ModalKind.Extrude && vp == modal.Vp)
        {
            // live arrow from the original faces to the extruded ones, distance at its tip
            float h = float.IsNaN(modal.LastValue) ? 0 : modal.LastValue;
            if (vp.Camera.Project(modal.Pivot, vp.W, vp.H, out var p0, out _) && vp.Camera.Project(modal.Pivot + modal.Normal * h, vp.W, vp.H, out var p1, out _))
            {
                var a = vp.ToScreen(p0); var b = vp.ToScreen(p1);
                Arrow(dl, a, b, 0xFF30D0FF, 2.5f);
                var lbl = $"{h:0.000} m";
                var ts = ImGui.CalcTextSize(lbl);
                var at = b + new Vector2(12, -ts.Y * 0.5f);
                dl.AddRectFilled(at - new Vector2(4, 2), at + ts + new Vector2(4, 2), 0xD0202020, 3);
                dl.AddText(at, 0xFF30D0FF, lbl);
            }
            return;
        }
        if (vp != Viewports[ActiveViewport] || !ExtrudeHandle(vp, out var s, out var e, out _, out _)) return;
        uint col = extrudeHandleHover ? 0xFF60FFFF : 0xFF20C8F0; // yellow, like Blender's tool gizmo
        Arrow(dl, s, e, col, extrudeHandleHover ? 3f : 2f);
        dl.AddCircleFilled(e, extrudeHandleHover ? 7 : 5.5f, col, 16);
        dl.AddText(e + new Vector2(8, -18), col, "+");
        if (extrudeHandleHover) ImGui.SetTooltip("Drag to extrude the selected polygons (E). Ctrl = snap, type a value.");
    }

    static void Arrow(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float th)
    {
        dl.AddLine(a, b, col, th);
        var d = b - a; float len = d.Length(); if (len < 1) return;
        d /= len; var n = new Vector2(-d.Y, d.X);
        dl.AddTriangleFilled(b + d * 8, b - d * 6 + n * 6, b - d * 6 - n * 6, col);
    }
}
