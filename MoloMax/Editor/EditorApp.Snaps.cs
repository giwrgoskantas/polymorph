using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Snaps like the classic modeller: one snap button (magnet "3") and a small menu of targets:
/// Grid Points, Nearest, Vertex, Edge/Segment, Face. The grid step is the grid spacing.
/// </summary>
public sealed partial class EditorApp
{
    public bool SnapGrid = true, SnapNearest, SnapVertexT = true, SnapEdge = true, SnapFace = true;
    /// <summary>Off (default, like the classic modeller): a vertex / edge / face snap lands exactly on the target whatever handle
    /// is dragged. On: the snap stays on the dragged axis / plane.</summary>
    public bool SnapAxisConstraint = true;
    /// <summary>Points of the moving selection at the start of the drag (world): a face snap brings the selection against the
    /// face (touching, never inside), using its point deepest along the face normal.</summary>
    readonly List<Vector3> snapMovingPts = new();
    /// <summary>Outward normal of the face found by the last snap search (face targets only).</summary>
    Vector3? snapFaceNormal;
    /// <summary>Object whose points the snap is looking at (its vertices are drawn while moving).</summary>
    int snapTargetObj = -1;
    /// <summary>Move tool, snap on: the vertex / edge point of the selection under the mouse (grab it to carry it onto a target).</summary>
    Vector3? snapHover;
    /// <summary>The element found by the last snap search: its kind and points (edge: 2 ends, face: its corners), and the hovered one before a drag.</summary>
    Vector3[] snapElPts; string snapHoverKind; Vector3[] snapHoverPts;
    bool AnyElementSnap => SnapNearest || SnapVertexT || SnapEdge || SnapFace;
    Vector3? snapMarker; string snapMarkerKind;

    /// <summary>The point of the moving selection the snap carries ("snap from"): the selection's vertex / edge under the
    /// cursor when the move starts on one, else the pivot. Like the classic modeller, grab a box by its corner and drop
    /// that corner on another corner.</summary>
    Vector3? snapFrom;

    void CaptureSnapFrom(Viewport vp, Vector3 pivot)
    {
        snapFrom = null; snapMovingPts.Clear();
        if (!SnapActive || !AnyElementSnap) return;
        // the moving geometry as it is now (a face snap needs its extent along the face normal)
        if (Level != SubObject.Object && xfVerts.Count > 0) snapMovingPts.AddRange(xfVerts.Values.Take(60000));
        else
            foreach (var id in xfObjects.Keys)
                if (Scene.Find(id) is { } mo && mo.Mesh != null && snapMovingPts.Count < 60000)
                { var w = mo.World; foreach (var v in mo.Mesh.Verts) snapMovingPts.Add(Vector3.Transform(v, w)); }
        bool f = SnapFace; SnapFace = false; // from the gizmo: a corner / edge under it, else the pivot (not the surface behind the gizmo)
        try { if (SnapPoint(vp, out var p, selectionOnly: true, pxRadius: 14f)) snapFrom = p; }
        finally { SnapFace = f; }
        snapMarker = null;
    }

    /// <summary>
    /// The move delta with every snap applied. Element snaps (vertex / edge / face under the mouse) carry the snap-from
    /// point onto the target, kept on the axis or plane being dragged. Grid snapping puts the pivot on grid points
    /// (world grid when the axes are the world ones, steps along the axes otherwise), only along the dragged axes.
    /// </summary>
    Vector3 ApplySnaps(Viewport vp, Vector3 raw, Vector3 pivot, Vector3[] axes, int axis, bool plane)
    {
        snapMarker = null;
        if (!SnapActive) return raw;
        axes ??= new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        var from = snapFrom ?? pivot;
        var r = ApplyElementSnap(vp, from, axes, axis, plane);
        if (r is { } got) { lastSnap = (Input.Mouse, got, snapMarker ?? from, snapMarkerKind); return got; }
        // the mouse has not left the point it snapped on: keep that snap (the moved geometry can hide the target for a frame)
        if (lastSnap is { } ls && Vector2.Distance(ls.Mouse, Input.Mouse) < 3f && AnyElementSnap) { snapMarker = ls.Marker; snapMarkerKind = ls.Kind; return ls.Delta; }
        lastSnap = null;
        if (!SnapGrid || GridStep <= 0) return raw;
        bool world = MathF.Abs(axes[0].X) > 0.9999f && MathF.Abs(axes[1].Y) > 0.9999f && MathF.Abs(axes[2].Z) > 0.9999f;
        var res = Vector3.Zero;
        for (int i = 0; i < 3; i++)
        {
            float di = Vector3.Dot(raw, axes[i]);
            bool moving = axis < 0 || (!plane && i == axis) || (plane && i != axis);
            if (!moving) continue;
            float b = Vector3.Dot(pivot, axes[i]);
            di = world ? SnapValue(b + di, GridStep) - b : SnapValue(di, GridStep);
            res += axes[i] * di;
        }
        return res;
    }

    (Vector2 Mouse, Vector3 Delta, Vector3 Marker, string Kind)? lastSnap;

    /// <summary>The move delta that carries the snap-from point onto the vertex / edge / face under the mouse, kept on the axis or plane; null when none.</summary>
    Vector3? ApplyElementSnap(Viewport vp, Vector3 from, Vector3[] axes, int axis, bool plane)
    {
        if (!SnapPoint(vp, out var target)) return null;
        bool onAxis = SnapAxisConstraint && axis >= 0 && !plane, onPlane = SnapAxisConstraint && axis >= 0 && plane;
        if (snapMarkerKind == "face" && snapFaceNormal is { } n && snapMovingPts.Count > 0)
        {
            // against the face: the selection's deepest point along -n lands on the face plane (it touches, it never
            // goes in); along the face it follows the mouse (the grabbed point goes under the cursor)
            float minN = float.MaxValue; foreach (var q in snapMovingPts) minN = MathF.Min(minN, Vector3.Dot(q, n));
            float push = Vector3.Dot(target, n) - minN;
            if (onAxis)
            {
                var ax = axes[axis]; float an = Vector3.Dot(ax, n);
                // along the dragged arrow until it meets the face; an arrow parallel to the face just slides
                return MathF.Abs(an) > 0.2f ? ax * (push / an) : ax * Vector3.Dot(target - from, ax);
            }
            var t = target - from; t -= n * Vector3.Dot(t, n);
            // against a wall (vertical face) it keeps its height: it slides along the wall and stays on its floor
            if (MathF.Abs(n.Z) < 0.3f) { var up = Vector3.UnitZ - n * n.Z; if (up.LengthSquared() > 1e-6f) { up = Vector3.Normalize(up); t -= up * Vector3.Dot(t, up); } }
            var d0 = t + n * push;
            if (onPlane) d0 -= axes[axis] * Vector3.Dot(d0, axes[axis]);
            return d0;
        }
        var d = target - from;
        if (onAxis) d = axes[axis] * Vector3.Dot(d, axes[axis]);
        else if (onPlane) d -= axes[axis] * Vector3.Dot(d, axes[axis]);
        return d;
    }

    /// <summary>Where a move should land with the element snaps on (vertex / edge / face under the mouse). Grid snapping is separate.
    /// <paramref name="selectionOnly"/>: look on the moving selection instead (the snap-from point).</summary>
    bool SnapPoint(Viewport vp, out Vector3 pos, bool selectionOnly = false, float pxRadius = 18f)
    {
        pos = default; snapMarker = null; snapTargetObj = -1; snapElPts = null; snapFaceNormal = null;
        if (!AnyElementSnap) return false;
        int vObj = -1, eObj = -1, fObj = -1;
        Vector3[] ePts = null, fPts = null;
        var ray = RayAt(vp, Input.Mouse);
        var local = vp.Local(Input.Mouse);
        var cur = Current;
        float bestV = pxRadius, bestE = pxRadius * 0.67f; Vector3 vPos = default, ePos = default; bool vFound = false, eFound = false;
        float bestF = float.MaxValue; Vector3 fPos = default; bool fFound = false;
        bool wantV = SnapVertexT || SnapNearest, wantE = SnapEdge || SnapNearest;
        // nearest visible surface along the mouse ray: vertices / edges behind it are hidden and not offered
        // (else the back corner of a box wins over its top face)
        bool moved = transforming || ModalActive;
        float occl = float.MaxValue;
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Mesh.Faces.Count == 0 || moved && xfObjects.ContainsKey(o.Id) && !selectionOnly) continue;
            if (Level != SubObject.Object && o == cur && moved && !selectionOnly) continue;
            var (bmn, bmx) = o.FastWorldBounds();
            if (!Picking.RayBox(ray, bmn, bmx, out _)) continue;
            if (Picking.PickFace(o, ray) is { } oh && oh.Distance < occl) occl = oh.Distance;
        }
        float tol = occl == float.MaxValue ? 0 : MathF.Max(0.02f, occl * 0.01f);
        bool Visible(Vector3 p) => occl == float.MaxValue || Vector3.Dot(p - ray.Origin, ray.Direction) <= occl + tol;
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Mesh.Verts.Count == 0) continue;
            bool editing = Level != SubObject.Object && o == cur;
            bool inSel = transforming || ModalActive ? xfObjects.ContainsKey(o.Id) : Selected.Contains(o.Id);
            if (selectionOnly) { if (editing ? false : !inSel) continue; if (Level != SubObject.Object && o != cur) continue; }
            else if (!editing && inSel) continue;
            var (mn, mx) = o.FastWorldBounds();
            if (!Picking.RayBox(ray, mn - new Vector3(0.5f), mx + new Vector3(0.5f), out _)) continue;
            var w = o.World; var m = o.Mesh;
            Vector2[] sp = null; bool[] ok = null;
            if (wantV || wantE)
            {
                sp = new Vector2[m.Verts.Count]; ok = new bool[m.Verts.Count];
                for (int i = 0; i < m.Verts.Count; i++)
                {
                    bool moving = editing && xfVerts.ContainsKey(i);
                    ok[i] = vp.Camera.Project(Vector3.Transform(m.Verts[i], w), vp.W, vp.H, out sp[i], out _) && (selectionOnly && editing ? moving : !moving);
                }
            }
            if (wantV)
                for (int i = 0; i < m.Verts.Count; i++)
                {
                    if (!ok[i]) continue;
                    float d = Vector2.Distance(sp[i], local);
                    if (d < bestV) { var wp = Vector3.Transform(m.Verts[i], w); if (!Visible(wp)) continue; bestV = d; vPos = wp; vFound = true; vObj = o.Id; }
                }
            if (wantE)
                foreach (var f in m.Faces)
                    for (int c = 0; c < f.V.Length; c++)
                    {
                        int a = f.V[c], b = f.V[(c + 1) % f.V.Length];
                        if (a > b || !ok[a] || !ok[b]) continue; // each edge once (shared edges come with the other order)
                        var ab = sp[b] - sp[a]; float len2 = ab.LengthSquared(); if (len2 < 1e-6f) continue;
                        float t = Math.Clamp(Vector2.Dot(local - sp[a], ab) / len2, 0, 1);
                        float d = Vector2.Distance(sp[a] + ab * t, local);
                        if (d < bestE) { var wa = Vector3.Transform(m.Verts[a], w); var wb = Vector3.Transform(m.Verts[b], w); var wq = Vector3.Lerp(wa, wb, t); if (!Visible(wq)) continue; bestE = d; ePos = wq; eFound = true; eObj = o.Id; ePts = new[] { wa, wb }; }
                    }
            if (SnapFace && !editing)
            {
                var h = Picking.PickFace(o, ray);
                if (h is { } hit && hit.Distance < bestF)
                {
                    bestF = hit.Distance; fPos = hit.Point; fFound = true; fObj = o.Id;
                    fPts = hit.Face >= 0 && hit.Face < m.Faces.Count ? m.Faces[hit.Face].V.Select(i => Vector3.Transform(m.Verts[i], w)).ToArray() : null;
                }
            }
        }
        // priority like the classic modeller: vertex, then edge, then face; "Nearest" takes whichever element is closest to the cursor
        if (SnapNearest && vFound && eFound) { if (bestV <= bestE + 2) { pos = vPos; snapMarkerKind = "vertex"; snapTargetObj = vObj; } else { pos = ePos; snapMarkerKind = "edge"; snapTargetObj = eObj; snapElPts = ePts; } }
        else if (vFound && (SnapVertexT || SnapNearest)) { pos = vPos; snapMarkerKind = "vertex"; snapTargetObj = vObj; }
        else if (eFound && (SnapEdge || SnapNearest)) { pos = ePos; snapMarkerKind = "edge"; snapTargetObj = eObj; snapElPts = ePts; }
        else if (fFound)
        {
            pos = fPos; snapMarkerKind = "face"; snapTargetObj = fObj; snapElPts = fPts;
            if (fPts is { Length: >= 3 })
            {
                // Newell normal of the face, turned toward the viewer (the side the mouse sees)
                var nn = Vector3.Zero;
                for (int i = 0; i < fPts.Length; i++) { var a = fPts[i]; var b = fPts[(i + 1) % fPts.Length]; nn += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)); }
                if (nn.LengthSquared() > 1e-12f) { nn = Vector3.Normalize(nn); if (Vector3.Dot(nn, ray.Direction) > 0) nn = -nn; snapFaceNormal = nn; }
            }
        }
        else return false;
        snapMarker = pos;
        return true;
    }

    /// <summary>A small cyan marker on the snapped point (square = vertex, cross = edge, triangle = face).</summary>
    void SnapMarkerLines()
    {
        if (snapMarker is not { } p || !(transforming || ModalActive)) { if (!(transforming || ModalActive)) snapMarker = null; return; }
        var vp = Viewports[ActiveViewport];
        float s = vp.Camera.PixelSize(vp.H, vp.Camera.Ortho ? 1 : Vector3.Distance(vp.Camera.Eye, p)) * 6;
        var col = new Vector4(0.2f, 0.95f, 1f, 1);
        var r = vp.Camera.Right * s; var u = vp.Camera.CamUp * s;
        if (snapMarkerKind == "vertex") { lines.Line(p - r - u, p + r - u, col); lines.Line(p + r - u, p + r + u, col); lines.Line(p + r + u, p - r + u, col); lines.Line(p - r + u, p - r - u, col); }
        else if (snapMarkerKind == "edge") { lines.Line(p - r - u, p + r + u, col); lines.Line(p - r + u, p + r - u, col); }
        else { lines.Line(p - r - u, p + r - u, col); lines.Line(p + r - u, p + u, col); lines.Line(p + u, p - r - u, col); }
    }

    /// <summary>Move tool with the snap on, before any drag: finds the point of the selection under the mouse (the one a drag
    /// will carry). Called once per frame.</summary>
    void UpdateSnapHover(Viewport hv)
    {
        snapHover = null; snapHoverPts = null;
        if (hv == null || transforming || ModalActive || Tool != Tool.Move || Selected.Count == 0 || !SnapActive || !AnyElementSnap) return;
        if (Level != SubObject.Object)
        {
            // (checked before the gizmo: the gizmo sits on the selected vertex, the vertex wins within a few pixels)
            // edit mode: a selected vertex (or a corner of the selected edges / faces) under the mouse is the point a drag carries
            var cur = Current; if (cur == null || hv.Camera == null) return;
            var m = cur.Mesh; var w = cur.World; var local = hv.Local(Input.Mouse);
            float best = gizmo.Hover != GizmoHandle.None ? 9f : 12f; int bi = -1;
            var sc = ScreenVerts(hv, cur);
            foreach (var vi in SelectedPoints(cur))
            {
                if (vi >= sc.P.Length || !sc.Ok[vi]) continue;
                float d = Vector2.Distance(sc.P[vi], local);
                if (d < best) { best = d; bi = vi; }
            }
            if (bi >= 0) { snapHover = Vector3.Transform(m.Verts[bi], w); snapHoverKind = "vertex"; }
            snapMarker = null;
            return;
        }
        if (gizmo.Hover != GizmoHandle.None) return;
        // a corner, a point of an edge or of a face of the selection (priority in that order)
        if (SnapPoint(hv, out var p, selectionOnly: true, pxRadius: 12f)) { snapHover = p; snapHoverKind = snapMarkerKind; snapHoverPts = snapElPts; }
        snapMarker = null;
    }

    /// <summary>The vertices the snap works with, drawn as dots: the selection's while choosing the point to grab, the target's while moving.</summary>
    void SnapDotsOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (!SnapActive || !AnyElementSnap || Tool != Tool.Move) return;
        bool moving = transforming || ModalActive;
        if (Level != SubObject.Object && !moving && snapHover == null) return; // edit mode: only the grabbed point and the target
        if (!moving && Selected.Count == 0) return;
        if (!moving && Hovered() != vp) return;
        IEnumerable<SceneObject> objs = moving ? (snapTargetObj >= 0 && Scene.Find(snapTargetObj) is { } t ? new[] { t } : Array.Empty<SceneObject>())
                                               : Level != SubObject.Object ? Array.Empty<SceneObject>() : Selected.Select(id => Scene.Find(id)).Where(o => o != null);
        var origin = new Vector2(vp.X, vp.Y);
        var tint = moving ? new Vector3(0.2f, 0.95f, 1f) : new Vector3(1f, 0.85f, 0.3f);
        uint Col(float a) => ImGui.ColorConvertFloat4ToU32(new Vector4(tint, a));
        bool Scr(Vector3 w, out Vector2 s) { bool ok = vp.Camera.Project(w, vp.W, vp.H, out var l, out _); s = origin + l; return ok; }
        int budget = 20000;
        foreach (var o in objs)
        {
            if (o.Hidden) continue;
            var w = o.World; var vs = o.Mesh.Verts;
            if (vs.Count > budget) continue;
            var sp = new Vector2[vs.Count]; var ok = new bool[vs.Count];
            for (int i = 0; i < vs.Count; i++) ok[i] = Scr(Vector3.Transform(vs[i], w), out sp[i]);
            // its edges (thin) when edge snapping is on, its vertices as dots when vertex snapping is on
            if (SnapEdge || SnapNearest)
                foreach (var f in o.Mesh.Faces)
                    for (int c = 0; c < f.V.Length; c++)
                    {
                        int a = f.V[c], b = f.V[(c + 1) % f.V.Length];
                        if (a < b && ok[a] && ok[b]) dl.AddLine(sp[a], sp[b], Col(0.35f), 1f);
                    }
            if (SnapVertexT || SnapNearest)
                for (int i = 0; i < vs.Count; i++) if (ok[i]) dl.AddRectFilled(sp[i] - new Vector2(2), sp[i] + new Vector2(2), Col(0.85f));
            budget -= vs.Count;
        }
        // the element under the mouse: the one a drag grabs (yellow), or the target while moving (cyan)
        string kind = moving ? snapMarkerKind : snapHoverKind;
        Vector3? point = moving ? snapMarker : snapHover;
        var pts = moving ? snapElPts : snapHoverPts;
        if (point is not { } pt || !Scr(pt, out var pc)) return;
        if (kind == "edge" && pts is { Length: 2 } && Scr(pts[0], out var e0) && Scr(pts[1], out var e1))
            dl.AddLine(e0, e1, Col(1f), 3f);
        else if (kind == "face" && pts is { Length: >= 3 })
        {
            var poly = new List<Vector2>();
            foreach (var q in pts) if (Scr(q, out var s2)) poly.Add(s2);
            if (poly.Count == pts.Length)
            {
                for (int i = 1; i + 1 < poly.Count; i++) dl.AddTriangleFilled(poly[0], poly[i], poly[i + 1], Col(0.28f));
                for (int i = 0; i < poly.Count; i++) dl.AddLine(poly[i], poly[(i + 1) % poly.Count], Col(1f), 2f);
            }
        }
        if (kind == "vertex") dl.AddRect(pc - new Vector2(6), pc + new Vector2(6), Col(1f), 0, ImDrawFlags.None, 2f);
        else dl.AddCircleFilled(pc, 4f, Col(1f));
        if (!moving)
        {
            string what = kind == "edge" ? "this edge point" : kind == "face" ? "this face point" : "this corner";
            dl.AddText(pc + new Vector2(9, -18), Col(1f), "drag to snap " + what);
        }
    }

    // ------------------------------------------------------------------ toolbar button + menu (classic look)

    /// <summary>The snap toggle: a magnet with a "3" (3D snap) and a small arrow that opens the targets menu.</summary>
    void SnapButton(float h)
    {
        var dl = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos();
        var size = new Vector2(30, h);
        ImGui.PushStyleColor(ImGuiCol.Button, Snap ? Theme.ToggleOn : Theme.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Snap ? Theme.ToggleOnHover : Theme.ButtonHover);
        if (ImGui.Button("##snaptoggle", size)) Snap = !Snap;
        TutRect("snap_btn");
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.OpenPopup("##snapmenu");
        Theme.Tip("3D Snaps Toggle (Shift+Tab)  ·  right-click or the arrow: snap targets");
        DrawMagnet(dl, p0 + new Vector2(4, 3), h - 6, Snap);
        ImGui.SameLine(0, 1);
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.Button);
        if (ImGui.ArrowButton("##snaparrow", ImGuiDir.Down)) ImGui.OpenPopup("##snapmenu");
        ImGui.PopStyleColor();
        Theme.Tip("Snap targets");
        if (ImGui.BeginPopup("##snapmenu"))
        {
            Ui.Label("Snaps", Theme.TextDim);
            ImGui.Separator();
            ImGui.MenuItem("Grid Points", null, ref SnapGrid);
            Theme.Tip($"Moves in steps of the grid ({GridStep:0.###} m), rotations in steps of {AngleSnap:0.#}°");
            ImGui.MenuItem("Nearest", null, ref SnapNearest);
            Theme.Tip("The closest vertex or edge point to the cursor");
            ImGui.MenuItem("Vertex", null, ref SnapVertexT);
            ImGui.MenuItem("Edge/Segment", null, ref SnapEdge);
            ImGui.MenuItem("Face", null, ref SnapFace);
            Theme.Tip("The point of the surface under the cursor");
            ImGui.Separator();
            ImGui.MenuItem("Use axis constraints", null, ref SnapAxisConstraint);
            Theme.Tip("On (default): dragging a gizmo arrow / plane keeps the move on it (against a face it stops touching it). Off: the snap lands on the target whatever handle you drag. Grabbing a point of the object (no gizmo) is always free.");
            ImGui.Separator();
            if (ImGui.MenuItem("Clear All")) { SnapGrid = SnapNearest = SnapVertexT = SnapEdge = SnapFace = false; }
            if (ImGui.IsItemDeactivated() || ImGui.IsAnyItemActive()) { }
            ImGui.EndPopup();
        }
    }

    /// <summary>Horseshoe magnet (dark body, light tips) with a small "3", drawn with lines.</summary>
    static void DrawMagnet(ImDrawListPtr dl, Vector2 p, float h, bool on)
    {
        uint body = ImGui.ColorConvertFloat4ToU32(on ? new Vector4(1, 1, 1, 1) : new Vector4(0.80f, 0.84f, 0.90f, 1));
        uint tip = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.42f, 0.11f, 1));
        float w = h * 0.72f, cx = p.X + w / 2, top = p.Y + h * 0.18f, r = w * 0.36f, thick = MathF.Max(2.2f, h * 0.17f);
        // the U: an arc at the bottom, two legs going up
        float cy = p.Y + h - r - 1;
        dl.PathArcTo(new Vector2(cx, cy), r, 0, MathF.PI, 16);
        dl.PathStroke(body, ImDrawFlags.None, thick);
        dl.AddLine(new Vector2(cx - r, cy), new Vector2(cx - r, top + thick), body, thick);
        dl.AddLine(new Vector2(cx + r, cy), new Vector2(cx + r, top + thick), body, thick);
        // pole tips
        dl.AddLine(new Vector2(cx - r, top + thick), new Vector2(cx - r, top), tip, thick);
        dl.AddLine(new Vector2(cx + r, top + thick), new Vector2(cx + r, top), tip, thick);
        // "3" (3D snap)
        dl.AddText(new Vector2(p.X + w + 1, p.Y - 2), body, "3");
    }
}
