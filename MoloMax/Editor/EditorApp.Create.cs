using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

public enum CreateKind { None, Box, Plane, Sphere, Cylinder, Cone, Tube, Room, Wall, Stairs, Door, Portal, CollisionBox, Tufted }

/// <summary>Parameters of the primitives (edited in the Create panel and in Adjust Last Operation).</summary>
public sealed class CreateParams
{
    public float Width = 2, Length = 2, Height = 2, Radius = 1, Radius2 = 0.5f;
    public int WSegs = 1, LSegs = 1, HSegs = 1, Sides = 18, Segments = 24, Steps = 12;
    public bool Smooth = true;
    public float WallHeight = 3f, WallThickness = 0.2f;
    public float RoomWidth = 6f, RoomLength = 6f, RoomHeight = 3f;
    public float StairWidth = 1.2f, StairRise = 3f, StairRun = 3.6f;
    public float DoorWidth = 1.2f, DoorHeight = 2.3f, DoorThickness = 0.06f;
    public float PortalWidth = 1.2f, PortalHeight = 2.3f;
    public float TuftWidth = 1.4f, TuftLength = 0.48f, TuftHeight = 0.4f, TuftPuff = 0.02f;
    public int TuftX = 8, TuftY = 3, TuftZ = 2, TuftSegs = 8;
    public bool TuftBottom;
}

/// <summary>
/// Blender-style creation: Shift+A (or the Create panel) adds the primitive at the 3D cursor, then the
/// "Adjust Last Operation" panel (F9) edits its size. Walls are drawn point by point.
/// </summary>
public sealed partial class EditorApp
{
    public readonly CreateParams Params = new();
    (CreateKind Kind, int ObjectId)? lastAdd;
    bool adjustOpen = true;
    readonly List<Vector3> wallPts = new();
    Vector3 wallCursor;

    (PolyMesh Mesh, string Name, GtaObjectType Type) BuildPrimitive(CreateKind k)
    {
        var P = Params;
        return k switch
        {
            CreateKind.Box => (Primitives.Box(P.Width, P.Length, P.Height, P.WSegs, P.LSegs, P.HSegs), "Box", GtaObjectType.Drawable),
            CreateKind.CollisionBox => (Primitives.Box(P.Width, P.Length, P.Height), "Collision", GtaObjectType.Collision),
            CreateKind.Plane => (Primitives.Plane(P.Width, P.Length, P.WSegs, P.LSegs), "Plane", GtaObjectType.Drawable),
            CreateKind.Sphere => (Primitives.Sphere(P.Radius, P.Segments, P.Smooth), "Sphere", GtaObjectType.Drawable),
            CreateKind.Cylinder => (Primitives.Cylinder(P.Radius, P.Height, P.Sides, P.HSegs, P.Smooth), "Cylinder", GtaObjectType.Drawable),
            CreateKind.Cone => (Primitives.Cone(P.Radius, P.Radius2, P.Height, P.Sides, P.Smooth), "Cone", GtaObjectType.Drawable),
            CreateKind.Tube => (Primitives.Tube(P.Radius, MathF.Min(P.Radius2, P.Radius * 0.98f), P.Height, P.Sides, P.Smooth), "Tube", GtaObjectType.Drawable),
            CreateKind.Room => (Primitives.Room(P.RoomWidth, P.RoomLength, P.RoomHeight), "Room", GtaObjectType.MloRoom),
            CreateKind.Stairs => (Primitives.Stairs(P.StairWidth, Math.Max(1, P.Steps), P.StairRise, P.StairRun), "Stairs", GtaObjectType.Drawable),
            CreateKind.Door => (Primitives.Door(P.DoorWidth, P.DoorHeight, P.DoorThickness), "Door", GtaObjectType.Drawable),
            CreateKind.Portal => (PortalMesh(P.PortalWidth, P.PortalHeight), "Portal", GtaObjectType.Portal),
            CreateKind.Tufted => (Primitives.Tufted(P.TuftWidth, P.TuftLength, P.TuftHeight, P.TuftX, P.TuftY, P.TuftZ, P.TuftPuff, P.TuftSegs, P.TuftBottom), "Tufted", GtaObjectType.Drawable),
            _ => (null, null, GtaObjectType.Drawable),
        };
    }

    static PolyMesh PortalMesh(float w, float h)
    {
        var pm = new PolyMesh();
        pm.AddVertex(new Vector3(-w / 2, 0, 0)); pm.AddVertex(new Vector3(w / 2, 0, 0));
        pm.AddVertex(new Vector3(w / 2, 0, h)); pm.AddVertex(new Vector3(-w / 2, 0, h));
        pm.AddFace(new[] { 0, 1, 2, 3 });
        return pm;
    }

    /// <summary>Adds a primitive at the 3D cursor (walls start the drawing tool instead).</summary>
    public void AddPrimitive(CreateKind k)
    {
        if (ModalActive) CancelModal();
        if (k == CreateKind.Wall) { BeginWall(); return; }
        if (Level != SubObject.Object) Level = SubObject.Object;
        var (mesh, name, type) = BuildPrimitive(k);
        if (mesh == null) return;
        if (MapMode) Cursor3D = MapGround(); // on the map: on the ground at the middle of the view
        var o = AddObject(name, mesh, Cursor3D, type);
        if (o != null) { lastAdd = (k, o.Id); adjustOpen = true; }
    }

    /// <summary>Rebuilds the last added primitive with the current parameters (Adjust Last Operation).</summary>
    void RebuildLastAdd()
    {
        if (lastAdd == null) return;
        var o = Scene.Find(lastAdd.Value.ObjectId);
        if (o == null) { lastAdd = null; return; }
        var (mesh, _, _) = BuildPrimitive(lastAdd.Value.Kind);
        if (mesh == null) return;
        o.Mesh = mesh; o.Touch(); subSel.Remove(o.Id);
        Dirty = true;
    }

    void DrawAdjustPanel()
    {
        if (lastAdd == null || !Scene.Objects.Any(o => o.Id == lastAdd.Value.ObjectId)) { lastAdd = null; return; }
        var vp = Viewports[ActiveViewport];
        ImGui.SetNextWindowPos(new Vector2(vp.X + 10, vp.Y + vp.H - 10), ImGuiCond.Always, new Vector2(0, 1));
        ImGui.SetNextWindowSizeConstraints(new Vector2(250, 0), new Vector2(340, 600));
        ImGui.SetNextWindowCollapsed(!adjustOpen, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);
        bool open = ImGui.Begin($"Adjust Last Operation: Add {KindLabel(lastAdd.Value.Kind)}###adjust",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        adjustOpen = !ImGui.IsWindowCollapsed();
        if (open)
        {
            bool changed = ParamsFor(lastAdd.Value.Kind);
            if (changed) RebuildLastAdd();
            Ui.TextDisabled("F9 shows / hides this panel");
        }
        ImGui.End();
    }

    // ---------------------------------------------------------------- walls

    void BeginWall()
    {
        if (Level != SubObject.Object) Level = SubObject.Object;
        BeginModal(ModalKind.Wall);
        modal.Header = "Wall: click points on the grid";
        SetStatus("Wall: click points · Enter/RMB finish · click the first point to close · Backspace removes the last point · Esc cancels");
    }

    /// <summary>Point on the construction plane (surface under the mouse, the ground, or a side view's plane).</summary>
    bool GridPoint(Viewport vp, Vector2 screen, out Vector3 p)
    {
        var ray = RayAt(vp, screen);
        // walls are drawn on the horizontal plane through the 3D cursor (side views see it edge-on)
        bool ok = Picking.RayGround(ray, Cursor3D.Z, out p) || Picking.RayPlane(ray, Cursor3D, Vector3.UnitZ, out p);
        if (ok && SnapActive) p = new Vector3(SnapValue(p.X, GridStep), SnapValue(p.Y, GridStep), SnapValue(p.Z, GridStep));
        return ok;
    }

    void WallMouse(Viewport hv)
    {
        if (hv != null && GridPoint(hv, Input.Mouse, out var p)) wallCursor = p;
        modal.Header = wallPts.Count == 0 ? "Wall: click the first point" : $"Wall: {wallPts.Count} point(s), length {WallLength():0.00} m";
        if (Input.Pressed(MouseButtons.Left) && hv != null && !ImGui.GetIO().WantCaptureMouse)
        {
            if (wallPts.Count > 2 && Vector3.Distance(wallPts[0], wallCursor) < MathF.Max(GridStep, 0.05f) * 1.5f) { FinishWall(true); return; }
            bool samePoint = wallPts.Count > 0 && Vector3.Distance(wallPts[^1], wallCursor) < 1e-3f;
            if (Input.DoubleClick && samePoint && wallPts.Count > 1) { FinishWall(false); return; }
            if (!samePoint) wallPts.Add(wallCursor);
        }
        else if (Input.Pressed(MouseButtons.Right))
        {
            if (wallPts.Count > 1) FinishWall(false); else CancelModal();
        }
    }

    float WallLength()
    {
        float l = 0;
        for (int i = 1; i < wallPts.Count; i++) l += Vector3.Distance(wallPts[i - 1], wallPts[i]);
        if (wallPts.Count > 0) l += Vector3.Distance(wallPts[^1], wallCursor);
        return l;
    }

    PolyMesh WallMesh(List<Vector3> pts, bool closed, out Vector3 origin)
    {
        origin = pts[0];
        var o = origin;
        var path = pts.Select(q => new Vector2(q.X - o.X, q.Y - o.Y)).ToList();
        return Primitives.Wall(path, Params.WallHeight, Params.WallThickness, closed);
    }

    void FinishWall(bool closed)
    {
        var pts = new List<Vector3>(wallPts);
        wallPts.Clear();
        modal.Kind = ModalKind.None;
        if (pts.Count < 2) { SetStatus("A wall needs two points."); return; }
        try
        {
            var mesh = WallMesh(pts, closed, out var origin);
            AddObject("Wall", mesh, origin);
        }
        catch (Exception e) { SetStatus("Wall failed: " + e.Message); }
    }

    void WallPreviewLines()
    {
        if (modal.Kind != ModalKind.Wall || wallPts.Count == 0) return;
        var pts = new List<Vector3>(wallPts);
        if (Vector3.Distance(pts[^1], wallCursor) > 1e-3f) pts.Add(wallCursor);
        if (pts.Count < 2) return;
        try
        {
            var mesh = WallMesh(pts, false, out var origin);
            var w = Matrix4x4.CreateTranslation(origin);
            var col = new Vector4(1f, 0.85f, 0.3f, 1);
            foreach (var e in mesh.BuildEdgeMap().Keys) lines.Line(Vector3.Transform(mesh.Verts[e.A], w), Vector3.Transform(mesh.Verts[e.B], w), col);
        }
        catch { }
    }

    void WallOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (modal.Kind != ModalKind.Wall) return;
        if (vp.Camera.Project(wallCursor, vp.W, vp.H, out var s, out _))
        {
            var c = vp.ToScreen(s);
            uint col = ImGui.ColorConvertFloat4ToU32(Theme.Gold);
            dl.AddLine(c - new Vector2(8, 0), c + new Vector2(8, 0), col, 1.5f);
            dl.AddLine(c - new Vector2(0, 8), c + new Vector2(0, 8), col, 1.5f);
        }
    }

    // ---------------------------------------------------------------- 3D cursor drawing

    void CursorOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (!vp.Camera.Project(Cursor3D, vp.W, vp.H, out var s, out _)) return;
        var c = vp.ToScreen(s);
        const float r = 10f;
        for (int i = 0; i < 16; i++)
        {
            float a0 = MathF.Tau * i / 16, a1 = MathF.Tau * (i + 1) / 16;
            uint col = i % 2 == 0 ? 0xFF2020E0 : 0xFFF0F0F0; // red / white dashes (ABGR)
            dl.AddLine(c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r, c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r, col, 2f);
        }
        dl.AddLine(c - new Vector2(r + 5, 0), c - new Vector2(4, 0), 0xFF101010, 1f);
        dl.AddLine(c + new Vector2(4, 0), c + new Vector2(r + 5, 0), 0xFF101010, 1f);
        dl.AddLine(c - new Vector2(0, r + 5), c - new Vector2(0, 4), 0xFF101010, 1f);
        dl.AddLine(c + new Vector2(0, 4), c + new Vector2(0, r + 5), 0xFF101010, 1f);
    }
}
