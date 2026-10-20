using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

public enum ModalKind { None, Translate, Rotate, Scale, Extrude, Inset, LoopCut, Wall, Chamfer, Knife, Opening, Connect }

/// <summary>
/// Blender-style modal tools: G / R / S with X Y Z constraints (twice = local, Shift = plane),
/// typed values, Ctrl to toggle snapping, LMB / Enter to confirm and RMB / Esc to cancel; plus
/// interactive Extrude, Inset and Loop Cut, and wall drawing.
/// </summary>
public sealed partial class EditorApp
{
    sealed class ModalState
    {
        public ModalKind Kind;
        public Viewport Vp;
        public Vector2 MouseStart;
        public Vector3 Pivot;
        public Vector2 PivotScreen;
        public int Axis = -1;
        public bool Local, Plane;
        public string Num = "";
        public Vector3[] Global = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        public Vector3[] LocalAxes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        public float AngleAcc, LastAngle;
        // parametric tools (extrude / inset)
        public PolyMesh BaseMesh;
        public PolySelection BaseSel;
        public SceneObject Obj;
        public Vector3 Normal;
        public float LastValue = float.NaN;
        public float D0;
        // extrude drag: the mesh after the extrude, the points it moved and the new sides (later values only move them)
        public PolyMesh XMesh; public List<(int V, Vector3 N)> XMoved; public int[] XRing, XVerts; public float XApplied;
        /// <summary>The mesh was a closed solid when the tool started (the result must stay closed).</summary>
        public bool BaseClosed; public bool Broke;
        // loop cut preview
        public EdgeKey? Hover;
        public List<(Vector3 A, Vector3 B)> Preview = new();
        public string Header = "";
        public Vector3[] Axes => Local ? LocalAxes : Global;
    }

    readonly ModalState modal = new();
    public bool ModalActive => modal.Kind != ModalKind.None;

    // ---------------------------------------------------------------- start

    public void BeginModal(ModalKind kind)
    {
        if (modal.Kind != ModalKind.None || transforming) return;
        var vp = Hovered() ?? Viewports[ActiveViewport];
        var cur = Current;
        switch (kind)
        {
            case ModalKind.Translate:
            case ModalKind.Rotate:
            case ModalKind.Scale:
                {
                    if (!SelectionPivot(out var pivot, out _)) { SetStatus("Nothing selected."); return; }
                    Undo.Record(kind == ModalKind.Translate ? "Move" : kind == ModalKind.Rotate ? "Rotate" : "Scale", Scene);
                    StartModalCommon(kind, vp, pivot);
                    CaptureOriginals();
                    if (kind == ModalKind.Translate) CaptureSnapFrom(vp, pivot);
                    var r = cur != null ? cur.Rotation : Quaternion.Identity;
                    modal.LocalAxes = new[] { Vector3.Transform(Vector3.UnitX, r), Vector3.Transform(Vector3.UnitY, r), Vector3.Transform(Vector3.UnitZ, r) };
                    break;
                }
            case ModalKind.Extrude:
            case ModalKind.Inset:
                {
                    if (cur == null || !(Level is SubObject.Polygon or SubObject.Element) || SubSel(cur).Faces.Count == 0)
                    {
                        SetStatus(kind == ModalKind.Extrude ? "Extrude works on faces: press 3 (face mode) and select faces." : "Inset works on faces: press 3 (face mode) and select faces.");
                        return;
                    }
                    if (!SelectionPivot(out var pivot, out _)) return;
                    Undo.Record(kind == ModalKind.Extrude ? "Extrude" : "Inset", Scene);
                    StartModalCommon(kind, vp, pivot);
                    modal.Obj = cur; modal.BaseMesh = cur.Mesh.Clone(); modal.BaseSel = SubSel(cur).Clone();
                    var n = Vector3.Zero;
                    foreach (var f in modal.BaseSel.Faces) n += cur.Mesh.FaceNormal(f) * MathF.Max(cur.Mesh.FaceArea(cur.Mesh.Faces[f]), 1e-6f);
                    Matrix4x4.Invert(cur.World, out var inv);
                    var wn = Vector3.TransformNormal(n, Matrix4x4.Transpose(inv));
                    modal.Normal = wn.LengthSquared() > 1e-12f ? Vector3.Normalize(wn) : Vector3.UnitZ;
                    modal.D0 = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    ApplyParametric(0);
                    break;
                }
            case ModalKind.LoopCut:
                if (cur == null) { SetStatus("Select one object for Loop Cut."); return; }
                StartModalCommon(kind, vp, Vector3.Zero);
                break;
            case ModalKind.Chamfer:
                {
                    if (cur == null || Level is not (SubObject.Edge or SubObject.Border) || SubSel(cur).Edges.Count == 0)
                    { SetStatus("Chamfer works on edges: press 2 (edge mode) and select edges."); return; }
                    if (!SelectionPivot(out var pivot, out _)) return;
                    Undo.Record("Chamfer", Scene);
                    StartModalCommon(kind, vp, pivot);
                    modal.Obj = cur; modal.BaseMesh = cur.Mesh.Clone(); modal.BaseSel = SubSel(cur).Clone();
                    modal.BaseClosed = cur.Mesh.Faces.Count <= 200000 && cur.Mesh.IsClosedManifold(out _); modal.Broke = false;
                    modal.D0 = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    break;
                }
            case ModalKind.Knife:
                if (cur == null) { SetStatus("Select one object to cut."); return; }
                StartModalCommon(kind, vp, Vector3.Zero);
                knifeA = null;
                modal.Header = "Knife: click two points";
                SetStatus("Knife: click two points to cut along the line (through the view). Esc to cancel.");
                break;
            case ModalKind.Wall:
                StartModalCommon(kind, vp, Vector3.Zero);
                wallPts.Clear();
                break;
        }
    }

    void StartModalCommon(ModalKind kind, Viewport vp, Vector3 pivot)
    {
        modal.Kind = kind; modal.Vp = vp; modal.MouseStart = Input.Mouse; modal.Pivot = pivot;
        modal.Axis = -1; modal.Local = false; modal.Plane = false; modal.Num = ""; modal.AngleAcc = 0; modal.LastValue = float.NaN; modal.XMesh = null;
        modal.Hover = null; modal.Preview.Clear(); modal.Header = "";
        ctrlLatch = Input.Ctrl; // Ctrl held from the shortcut (Ctrl+E…) must not flip snapping
        vp.Camera.Project(pivot, vp.W, vp.H, out var ps, out _);
        modal.PivotScreen = vp.ToScreen(ps);
        modal.LastAngle = MathF.Atan2(Input.Mouse.Y - modal.PivotScreen.Y, Input.Mouse.X - modal.PivotScreen.X);
        ActiveViewport = Array.IndexOf(Viewports, vp);
        lmbTracking = false; boxSelecting = false; rmbTracking = false;
    }

    // ---------------------------------------------------------------- keys while a modal tool runs

    void ModalKeys()
    {
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        bool shift = ImGui.GetIO().KeyShift;
        if (P(ImGuiKey.Escape)) { CancelModal(); return; }
        if (P(ImGuiKey.Enter) || P(ImGuiKey.KeypadEnter)) { ConfirmModal(); return; }
        if (modal.Kind == ModalKind.Opening) { OpeningKeys(); return; }

        if (modal.Kind == ModalKind.Wall)
        {
            if (P(ImGuiKey.Backspace) && wallPts.Count > 0) wallPts.RemoveAt(wallPts.Count - 1);
            return;
        }
        if (modal.Kind == ModalKind.Chamfer)
        {
            // sides of the bevel: mouse wheel (Blender), Page Up / Down, + / -
            int d = 0;
            if (MathF.Abs(Input.Wheel) > 0) d = Math.Sign(Input.Wheel);
            if (P(ImGuiKey.PageUp) || P(ImGuiKey.KeypadAdd) || P(ImGuiKey.Equal)) d = 1;
            if (P(ImGuiKey.PageDown) || P(ImGuiKey.KeypadSubtract)) d = -1;
            if (d != 0)
            {
                chamferSegments = Math.Clamp(chamferSegments + d, 1, 32);
                if (!float.IsNaN(modal.LastValue)) { float v = modal.LastValue; modal.LastValue = float.NaN; ApplyParametric(v); }
            }
        }
        if (modal.Kind is ModalKind.Translate or ModalKind.Rotate or ModalKind.Scale)
        {
            if (P(ImGuiKey.X)) CycleAxis(0, shift);
            if (P(ImGuiKey.Y)) CycleAxis(1, shift);
            if (P(ImGuiKey.Z)) CycleAxis(2, shift);
        }
        if (modal.Kind is ModalKind.Translate or ModalKind.Rotate or ModalKind.Scale or ModalKind.Extrude or ModalKind.Inset or ModalKind.Chamfer)
        {
            // typed value: characters (works with any layout, numpad included) …
            bool gotDigit = false;
            foreach (var c in Input.Chars)
            {
                if (char.IsDigit(c)) { modal.Num += c; gotDigit = true; }
                else if ((c == '.' || c == ',') && !modal.Num.Contains('.')) modal.Num += '.';
                else if (c == '-') modal.Num = modal.Num.StartsWith('-') ? modal.Num[1..] : "-" + modal.Num;
            }
            // … plus the top-row keys pressed without Shift on AZERTY (they type & é " ' …)
            if (!gotDigit && Input.Chars.Count == 0)
                for (int d = 0; d <= 9; d++)
                    if (P(ImGuiKey._0 + d) && !shift) modal.Num += (char)('0' + d);
            if (P(ImGuiKey.Backspace) && modal.Num.Length > 0) modal.Num = modal.Num[..^1];
        }
    }

    void CycleAxis(int axis, bool plane)
    {
        // Blender: first press global, second press local, third press off
        if (modal.Axis != axis || modal.Plane != plane) { modal.Axis = axis; modal.Local = false; modal.Plane = plane; }
        else if (!modal.Local) modal.Local = true;
        else { modal.Axis = -1; modal.Local = false; modal.Plane = false; }
    }

    bool NumValue(out float v)
    {
        v = 0;
        if (modal.Num.Length == 0 || modal.Num == "-" || modal.Num == ".") return false;
        return float.TryParse(modal.Num, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    // ---------------------------------------------------------------- mouse while a modal tool runs

    void ModalMouse(Viewport hv)
    {
        if (modal.Kind == ModalKind.Wall) { WallMouse(hv); return; }
        if (modal.Kind == ModalKind.Opening) { OpeningMouse(hv); return; }
        if (modal.Kind == ModalKind.LoopCut) { LoopCutMouse(hv); return; }
        if (modal.Kind == ModalKind.Knife) { KnifeMouse(hv); return; }
        UpdateModal();
        // started by dragging the extrude arrow: releasing the button confirms (Blender's tool gizmo)
        if (extrudeDragging && modal.Kind == ModalKind.Extrude && !Input.Down(MouseButtons.Left)) { extrudeDragging = false; ConfirmModal(); }
        else if (Input.Pressed(MouseButtons.Left)) { extrudeDragging = false; ConfirmModal(); }
        else if (Input.Pressed(MouseButtons.Right)) { extrudeDragging = false; CancelModal(); }
    }

    /// <summary>Screen pixels moved along a world direction through the pivot, as metres.</summary>
    float ScreenAlong(Vector3 dir, Vector2 mouseDelta)
    {
        var vp = modal.Vp;
        vp.Camera.Project(modal.Pivot, vp.W, vp.H, out var a, out _);
        vp.Camera.Project(modal.Pivot + dir, vp.W, vp.H, out var b, out _);
        var sd = b - a; float l2 = sd.LengthSquared();
        if (l2 < 1e-4f) return 0;
        return Vector2.Dot(mouseDelta, sd) / l2;
    }

    void UpdateModal()
    {
        var vp = modal.Vp; var d = Input.Mouse - modal.MouseStart;
        bool hasNum = NumValue(out float num);
        string axisName = modal.Axis < 0 ? "" : (modal.Plane ? "plane without " : "along ") + "XYZ"[modal.Axis] + (modal.Local ? " (local)" : " (global)");
        switch (modal.Kind)
        {
            case ModalKind.Translate:
                {
                    var axes = modal.Axes;
                    Vector3 delta;
                    if (hasNum)
                    {
                        int ax = modal.Axis < 0 ? 0 : modal.Plane ? (modal.Axis + 1) % 3 : modal.Axis;
                        delta = axes[ax] * num;
                    }
                    else if (modal.Axis >= 0 && !modal.Plane) delta = axes[modal.Axis] * ScreenAlong(axes[modal.Axis], d);
                    else if (modal.Axis >= 0 && modal.Plane)
                    {
                        var n = axes[modal.Axis];
                        if (!Picking.RayPlane(RayAt(vp, Input.Mouse), modal.Pivot, n, out var p1) || !Picking.RayPlane(RayAt(vp, modal.MouseStart), modal.Pivot, n, out var p0)) return;
                        delta = p1 - p0;
                    }
                    else
                    {
                        float px = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, modal.Pivot));
                        delta = (vp.Camera.Right * d.X - vp.Camera.CamUp * d.Y) * px;
                    }
                    if (!hasNum) delta = ApplySnaps(vp, delta, modal.Pivot, axes, modal.Axis, modal.Plane); // vertex / edge / face under the mouse, else grid points
                    ApplyLive(p => p + delta, (pos, rot, sc) => (pos + delta, rot, sc));
                    modal.Header = $"Move  D: {delta.X:0.000}  {delta.Y:0.000}  {delta.Z:0.000} m  {axisName}  {NumShown()}";
                    break;
                }
            case ModalKind.Rotate:
                {
                    var toViewer = -vp.Camera.Forward;
                    var axis = modal.Axis >= 0 ? modal.Axes[modal.Axis] : toViewer;
                    float a = MathF.Atan2(Input.Mouse.Y - modal.PivotScreen.Y, Input.Mouse.X - modal.PivotScreen.X);
                    float da = a - modal.LastAngle;
                    if (da > MathF.PI) da -= MathF.Tau; else if (da < -MathF.PI) da += MathF.Tau;
                    modal.AngleAcc += da; modal.LastAngle = a;
                    // screen angles grow clockwise (y down); clockwise on screen is negative about an axis facing the viewer
                    float deg = -modal.AngleAcc * 180f / MathF.PI * (Vector3.Dot(axis, toViewer) >= 0 ? 1 : -1);
                    if (hasNum) deg = num;
                    else if (SnapActive) deg = SnapValue(deg, AngleSnap);
                    ApplyRotation(axis, deg * MathF.PI / 180f, modal.Pivot);
                    modal.Header = $"Rotate  {deg:0.0}°  {axisName}  {NumShown()}";
                    break;
                }
            case ModalKind.Scale:
                {
                    float d0 = MathF.Max(Vector2.Distance(modal.MouseStart, modal.PivotScreen), 30f);
                    float f = Vector2.Distance(Input.Mouse, modal.PivotScreen) / d0;
                    if (hasNum) f = num;
                    else if (SnapActive) f = SnapValue(f, 0.1f);
                    Vector3 fv;
                    if (modal.Axis < 0) fv = new Vector3(f);
                    else if (modal.Plane) { fv = new Vector3(f); if (modal.Axis == 0) fv.X = 1; else if (modal.Axis == 1) fv.Y = 1; else fv.Z = 1; }
                    else { fv = Vector3.One; if (modal.Axis == 0) fv.X = f; else if (modal.Axis == 1) fv.Y = f; else fv.Z = f; }
                    ApplyScale(fv, modal.Pivot, modal.Axes);
                    modal.Header = $"Scale  {f:0.000}  {axisName}  {NumShown()}";
                    break;
                }
            case ModalKind.Extrude:
                {
                    float h = hasNum ? num : ScreenAlong(modal.Normal, d);
                    if (SnapActive && !hasNum) h = SnapValue(h, GridStep);
                    ApplyParametric(h);
                    modal.Header = $"Extrude  {h:0.000} m along normal  {NumShown()}";
                    break;
                }
            case ModalKind.Chamfer:
                {
                    float dist = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    float px = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, modal.Pivot));
                    float amount = MathF.Max(0, (dist - modal.D0) * px * 0.5f);
                    if (hasNum) amount = num;
                    else if (SnapActive) amount = SnapValue(amount, GridStep * 0.1f);
                    ApplyParametric(amount);
                    modal.Header = modal.Broke ? "Chamfer: these edges can't be chamfered together without holes (try fewer edges)"
                        : $"Chamfer  {amount:0.000} m  {chamferSegments} segment{(chamferSegments > 1 ? "s" : "")} (wheel / Page Up-Down)  {NumShown()}";
                    break;
                }
            case ModalKind.Inset:
                {
                    float dist = Vector2.Distance(Input.Mouse, modal.PivotScreen);
                    float px = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, modal.Pivot));
                    // move towards the centre to thicken (Blender); starting on the centre, move away instead
                    float amount = modal.D0 >= 30 ? (modal.D0 - dist) * px : (dist - modal.D0) * px;
                    amount = MathF.Max(0, amount);
                    if (hasNum) amount = num;
                    else if (SnapActive) amount = SnapValue(amount, GridStep * 0.1f);
                    ApplyParametric(amount);
                    modal.Header = $"Inset  thickness {amount:0.000} m  {NumShown()}";
                    break;
                }
        }
        SetStatus(modal.Header + "    LMB/Enter confirm · RMB/Esc cancel · X Y Z axis (twice = local, Shift = plane) · type a value · Ctrl snap");
    }

    string NumShown() => modal.Num.Length > 0 ? $"[{modal.Num}]" : "";

    /// <summary>Re-runs extrude / inset from the original mesh with a new value.</summary>
    void ApplyParametric(float value)
    {
        if (MathF.Abs(value - modal.LastValue) < 1e-6f) return;
        modal.LastValue = value;
        var o = modal.Obj;
        float s = (MathF.Abs(o.Scale.X) + MathF.Abs(o.Scale.Y) + MathF.Abs(o.Scale.Z)) / 3f;
        float local = value / MathF.Max(s, 1e-4f);
        if (modal.Kind == ModalKind.Extrude && modal.XMesh != null && ReferenceEquals(o.Mesh, modal.XMesh))
        {
            // same faces, another height: the extruded points slide along their direction, the side UVs follow
            float d = local - modal.XApplied;
            foreach (var (v, n) in modal.XMoved) o.Mesh.Verts[v] += n * d;
            MeshOps.SideUVMetric(o.Mesh, modal.XRing);
            modal.XApplied = local;
            int from = o.Version; o.Touch();
            renderer.HintPointsMoved(o.Id, from, o.Version, modal.XVerts, modal.XRing);
            wires.HintPointsMoved(o.Id, from, o.Version, modal.XVerts, o.Mesh.Verts);
            xfPointsOnly = true;
            return;
        }
        o.Mesh = modal.BaseMesh.Clone();
        var sel = modal.BaseSel.Clone();
        subSel[o.Id] = sel;
        modal.XMesh = null;
        if (modal.Kind == ModalKind.Extrude)
        {
            var moved = new List<(int V, Vector3 N)>();
            var ring = MeshOps.ExtrudeFaces(o.Mesh, sel.Faces, local, false, moved);
            modal.XMesh = o.Mesh; modal.XMoved = moved; modal.XRing = ring.ToArray(); modal.XVerts = moved.Select(x => x.V).ToArray(); modal.XApplied = local;
        }
        else if (modal.Kind == ModalKind.Chamfer)
        {
            if (local > 1e-5f) MeshOps.ChamferEdges(o.Mesh, sel.Edges.ToList(), local, chamferSegments);
            sel.Edges.Clear();
            // an odd corner the chamfer can't close: the mesh stays as it was rather than getting holes
            modal.Broke = modal.BaseClosed && local > 1e-5f && !o.Mesh.IsClosedManifold(out _);
            if (modal.Broke) { o.Mesh = modal.BaseMesh.Clone(); subSel[o.Id] = modal.BaseSel.Clone(); }
        }
        else MeshOps.InsetFaces(o.Mesh, sel.Faces, local);
        o.Touch();
    }

    // ---------------------------------------------------------------- knife (straight cut through the view)

    Vector2? knifeA;

    void KnifeMouse(Viewport hv)
    {
        if (Input.Pressed(MouseButtons.Right)) { CancelModal(); return; }
        if (hv == null || !Input.Pressed(MouseButtons.Left)) return;
        if (knifeA == null) { knifeA = Input.Mouse; return; }
        var cur = Current; var a = knifeA.Value; var b = Input.Mouse;
        knifeA = null;
        if (cur == null || Vector2.Distance(a, b) < 3) return;
        // plane through both view rays
        var ra = RayAt(hv, a); var rb = RayAt(hv, b);
        var pa = ra.Origin + ra.Direction * 10; var pb = rb.Origin + rb.Direction * 10;
        var n = Vector3.Cross(pb - pa, hv.Camera.Ortho ? hv.Camera.Forward : (pa - hv.Camera.Eye));
        if (n.LengthSquared() < 1e-10f) return;
        Matrix4x4.Invert(cur.World, out var inv);
        var lp = Vector3.Transform(pa, inv); var ln = Vector3.Normalize(Vector3.TransformNormal(n, Matrix4x4.Transpose(cur.World)));
        var faces = Level is SubObject.Polygon or SubObject.Element && SubSel(cur).Faces.Count > 0 ? new HashSet<int>(SubSel(cur).Faces) : null;
        // only faces the line actually crosses on screen (a knife, not an infinite slice)
        var keep = new HashSet<int>();
        var w = cur.World; var m = cur.Mesh;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            if (faces != null && !faces.Contains(fi)) continue;
            var f = m.Faces[fi]; bool hit = false;
            for (int i = 0; i < f.V.Length && !hit; i++)
            {
                if (!hv.Camera.Project(Vector3.Transform(m.Verts[f.V[i]], w), hv.W, hv.H, out var s0, out _)) continue;
                if (!hv.Camera.Project(Vector3.Transform(m.Verts[f.V[(i + 1) % f.V.Length]], w), hv.W, hv.H, out var s1, out _)) continue;
                hit = SegmentsCross(hv.Local(a), hv.Local(b), s0, s1);
            }
            if (hit) keep.Add(fi);
        }
        if (keep.Count == 0) { SetStatus("The line does not cross the mesh."); return; }
        HashSet<EdgeKey> cut = null;
        DoMesh("Knife", (o, mm, s) => { cut = MeshOps.Slice(mm, lp, ln, keep); });
        if (cut != null && Level is SubObject.Edge or SubObject.Border) SubSel(cur).Edges = cut;
        SetStatus($"Knife: {cut?.Count ?? 0} new edge(s). Click two more points, or Esc / right click to stop.");
    }

    static bool SegmentsCross(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        static float Cr(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        float d1 = Cr(q1, q2, p1), d2 = Cr(q1, q2, p2), d3 = Cr(p1, p2, q1), d4 = Cr(p1, p2, q2);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    // ---------------------------------------------------------------- loop cut

    void LoopCutMouse(Viewport hv)
    {
        var cur = Current;
        if (cur == null) { CancelModal(); return; }
        if (hv == null || ImGui.GetIO().WantCaptureMouse) { modal.Hover = null; modal.Preview.Clear(); }
        else
        {
            var e = EdgeUnder(hv, cur, hv.Local(Input.Mouse), 14f);
            if (!e.Equals(modal.Hover))
            {
                modal.Hover = e; modal.Preview.Clear();
                if (e.HasValue)
                {
                    var w = cur.World;
                    foreach (var (a, b) in MeshOps.LoopCutPreview(cur.Mesh, e.Value)) modal.Preview.Add((Vector3.Transform(a, w), Vector3.Transform(b, w)));
                }
            }
        }
        modal.Header = modal.Hover.HasValue ? "Loop Cut: click to cut" : "Loop Cut: hover an edge";
        SetStatus(modal.Header + "    LMB cut · RMB/Esc cancel");
        if (Input.Pressed(MouseButtons.Left) && modal.Hover.HasValue)
        {
            var edge = modal.Hover.Value;
            modal.Kind = ModalKind.None;
            DoMesh("Loop Cut", (o, m, s) =>
            {
                var added = MeshOps.LoopCut(m, edge);
                Level = SubObject.Edge;
                s.Edges = added;
                SetStatus($"Loop cut: {added.Count} new edges");
            });
            if (Level == SubObject.Object) Level = SubObject.Edge;
        }
        else if (Input.Pressed(MouseButtons.Right)) CancelModal();
    }

    // ---------------------------------------------------------------- confirm / cancel

    void ConfirmModal()
    {
        var k = modal.Kind;
        if (k == ModalKind.Wall) { FinishWall(false); return; }
        if (k == ModalKind.Opening) { ConfirmOpening(); return; }
        if (k == ModalKind.LoopCut) { if (modal.Hover.HasValue) { } CancelModal(); return; }
        if (k == ModalKind.Knife) { modal.Kind = ModalKind.None; return; }
        if (k is ModalKind.Extrude or ModalKind.Inset or ModalKind.Chamfer) RememberMeshOp();
        modal.Kind = ModalKind.None;
        FinishPointsOnly();
        Dirty = true;
        lastAdd = null;
        SetStatus(modal.Header);
    }

    void CancelModal()
    {
        var k = modal.Kind;
        modal.Kind = ModalKind.None;
        switch (k)
        {
            case ModalKind.Translate:
            case ModalKind.Rotate:
            case ModalKind.Scale:
                RestoreOriginals();
                FinishPointsOnly();
                Undo.DropLast();
                break;
            case ModalKind.Extrude:
            case ModalKind.Inset:
            case ModalKind.Chamfer:
                modal.Obj.Mesh = modal.BaseMesh;
                subSel[modal.Obj.Id] = modal.BaseSel;
                modal.Obj.Touch();
                Undo.DropLast();
                break;
            case ModalKind.Wall:
                wallPts.Clear();
                break;
            case ModalKind.Opening:
                CancelOpening();
                break;
        }
        SetStatus("Cancelled");
    }

    // ---------------------------------------------------------------- drawing

    void ModalPreviewLines()
    {
        if (modal.Kind == ModalKind.LoopCut)
        {
            var y = new Vector4(1f, 0.85f, 0.1f, 1);
            foreach (var (a, b) in modal.Preview) lines.Line(a, b, y);
        }
        if (modal.Kind is ModalKind.Translate or ModalKind.Rotate or ModalKind.Scale && modal.Axis >= 0)
        {
            var c = modal.Axis == 0 ? new Vector4(0.95f, 0.25f, 0.3f, 1) : modal.Axis == 1 ? new Vector4(0.45f, 0.85f, 0.2f, 1) : new Vector4(0.3f, 0.55f, 1f, 1);
            var axes = modal.Axes;
            if (modal.Plane)
            {
                var a1 = axes[(modal.Axis + 1) % 3] * 2000; var a2 = axes[(modal.Axis + 2) % 3] * 2000;
                lines.Line(modal.Pivot - a1, modal.Pivot + a1, c * 0.8f);
                lines.Line(modal.Pivot - a2, modal.Pivot + a2, c * 0.8f);
            }
            else lines.Line(modal.Pivot - axes[modal.Axis] * 2000, modal.Pivot + axes[modal.Axis] * 2000, c);
        }
        if (modal.Kind == ModalKind.Extrude)
            lines.Line(modal.Pivot - modal.Normal * 2000, modal.Pivot + modal.Normal * 2000, new Vector4(0.3f, 0.75f, 1f, 1));
        WallPreviewLines();
        OpeningLines();
    }

    void ModalOverlay(ImDrawListPtr dl, Viewport vp)
    {
        ExtrudeHandleOverlay(dl, vp);
        OpeningOverlay(dl, vp);
        if (modal.Kind == ModalKind.Opening && vp == Viewports[ActiveViewport]) OpeningPanel();
        if (modal.Kind == ModalKind.None || vp != Viewports[ActiveViewport]) return;
        // header strip at the top of the viewport, like Blender's tool header
        var text = modal.Header;
        if (string.IsNullOrEmpty(text)) return;
        var size = ImGui.CalcTextSize(text);
        var p = new Vector2(vp.X + (vp.W - size.X) / 2, vp.Y + 6);
        dl.AddRectFilled(p - new Vector2(8, 3), p + size + new Vector2(8, 3), 0xD0202020, 2);
        dl.AddText(p, ImGui.ColorConvertFloat4ToU32(Theme.Gold), text);
        if (modal.Kind is ModalKind.Rotate or ModalKind.Scale or ModalKind.Inset or ModalKind.Chamfer)
            dl.AddLine(modal.PivotScreen, Input.Mouse, 0x80FFFFFF, 1f);
        if (modal.Kind == ModalKind.Knife && knifeA.HasValue) dl.AddLine(knifeA.Value, Input.Mouse, 0xFF40E0FF, 2f);
        WallOverlay(dl, vp);
    }
}
