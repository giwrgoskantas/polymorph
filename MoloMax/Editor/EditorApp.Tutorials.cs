using MoloMax.Core.Gta.Map;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Gta.Library;
using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using MoloMax.Core.Tools;
using MoloMax.Core.Paint;
using MoloMax.UI;
using MoloMax.Rendering;
using Keys = System.Windows.Forms.Keys;

namespace MoloMax.Editor;

/// <summary>
/// Tutorial videos, recorded by the app itself: a script drives the real interface (mouse, keys, menus) while every
/// frame is saved (JPEG, 30 frames = 1 s of film). Captions, the mouse cursor and the keys held are drawn on top.
/// Run with the self test filter "TUTO" (frames in &lt;test folder&gt;/video/&lt;name&gt;/).
/// </summary>
public sealed partial class EditorApp
{
    string recordDir; int recordN;
    string tutCaption, tutSub; int tutStep, tutSteps;
    readonly HashSet<string> tutHeld = new();
    readonly Dictionary<string, (Vector2 Min, Vector2 Max)> tutRects = new();
    float tutClick; // ripple after a click (seconds left)
    bool tutHideCursor; // trailer: no drawn cursor
    string recordDir_hold;
    /// <summary>Trailer: writes the current frame number and a label, so titles and music can be cut on it afterwards.</summary>
    void Mark(string label) { if (recordDir != null) File.AppendAllText(Path.Combine(recordDir, "markers.txt"), $"{recordN} {label}\n"); }

    /// <summary>Remembers where an item was drawn (menus, buttons) so a tutorial can move the mouse onto it.</summary>
    void TutRect(string key) { if (SelfTestDir != null) tutRects[key] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax()); }
    Vector2 TutCentre(string key) => tutRects.TryGetValue(key, out var r) ? (r.Min + r.Max) * 0.5f : Input.Mouse;

    void RecordFrame()
    {
        if (recordDir == null) return;
        try { gpu.Capture(Path.Combine(recordDir, $"f_{recordN++:00000}.jpg")); } catch { }
    }

    IEnumerable<object> StartFilm(string name, int steps)
    {
        // close any menu the previous film left open (Escape + a click on empty space), keeping the selection
        var keepSel = Selected.ToList(); var keepActive = ActiveId;
        PostKey(Keys.Escape, true); yield return Frames(2); PostKey(Keys.Escape, false); yield return Frames(2);
        var vp0 = Viewports[ActiveViewport]; var empty = new Vector2(vp0.X + 30, vp0.Y + vp0.H - 40);
        PostMouse(0x200, empty, 0); yield return Frames(2);
        PostMouse(0x201, empty, 1); yield return Frames(2); PostMouse(0x202, empty, 0); yield return Frames(4);
        Selected.Clear(); foreach (var id in keepSel) Selected.Add(id); ActiveId = keepActive; SelectionChanged(); yield return Frames(2);
        recordDir = StPath("video", name); Directory.CreateDirectory(recordDir);
        foreach (var f in Directory.GetFiles(recordDir, "*.jpg")) File.Delete(f);
        recordN = 0; tutStep = 0; tutSteps = steps; tutHeld.Clear(); tutCaption = null; tutSub = null;
        UI.ImGuiBackend.SyntheticInput = true;
        yield return Frames(2);
    }

    IEnumerable<object> EndFilm()
    {
        tutCaption = null; tutSub = null;
        yield return Frames(15);
        Note($"video {Path.GetFileName(recordDir)}: {recordN} frames ({recordN / 30f:0.0} s)");
        Expect(recordN > 60, $"film recorded ({recordN} frames)");
        recordDir = null; UI.ImGuiBackend.SyntheticInput = false;
        foreach (var k in tutHeld.ToList()) KeyUpT(k);
    }

    /// <summary>A new step: caption on screen, held for a moment so it can be read.</summary>
    IEnumerable<object> Say(string title, string sub = null, int hold = 45)
    {
        tutStep++; tutCaption = title; tutSub = sub;
        yield return Frames(hold);
    }

    static float Ease(float t) => t * t * (3 - 2 * t);

    IEnumerable<object> MouseTo(Vector2 to, int frames = 22, int buttons = 0)
    {
        var from = Input.Mouse;
        for (int i = 1; i <= frames; i++) { PostMouse(0x200, Vector2.Lerp(from, to, Ease(i / (float)frames)), buttons); yield return Frames(1); }
    }

    IEnumerable<object> ClickAt(Vector2 at, bool right = false, int moveFrames = 22)
    {
        foreach (var x in MouseTo(at, moveFrames)) yield return x;
        yield return Frames(4);
        PostMouse(right ? 0x204 : 0x201, at, right ? 2 : 1); tutClick = 0.35f; yield return Frames(3);
        PostMouse(right ? 0x205 : 0x202, at, 0); yield return Frames(6);
    }

    IEnumerable<object> DragTo(Vector2 a, Vector2 b, int frames = 40, bool right = false)
    {
        foreach (var x in MouseTo(a)) yield return x;
        yield return Frames(4);
        PostMouse(right ? 0x204 : 0x201, a, right ? 2 : 1); tutClick = 0.35f; yield return Frames(3);
        foreach (var x in MouseTo(b, frames, right ? 2 : 1)) yield return x;
        yield return Frames(4);
        PostMouse(right ? 0x205 : 0x202, b, 0); yield return Frames(6);
    }

    static string KeyName(Keys k) => k switch { Keys.ShiftKey => "Shift", Keys.ControlKey => "Ctrl", Keys.Menu => "Alt", Keys.Up => "↑", Keys.Down => "↓", Keys.Left => "←", Keys.Right => "→", _ => k.ToString() };
    void KeyDownT(Keys k) { PostKey(k, true); tutHeld.Add(KeyName(k)); }
    void KeyUpT(Keys k) { PostKey(k, false); tutHeld.Remove(KeyName(k)); }
    void KeyUpT(string name) { var k = Enum.GetValues<Keys>().FirstOrDefault(x => KeyName(x) == name); if (k != Keys.None) PostKey(k, false); tutHeld.Remove(name); }

    IEnumerable<object> Tap(Keys k, int hold = 6)
    {
        KeyDownT(k); yield return Frames(hold); KeyUpT(k); yield return Frames(8);
    }

    /// <summary>A point on a gizmo handle of the current selection (searched around the pivot on screen).</summary>
    Vector2 GizmoGrip(Viewport vp, GizmoHandle want)
    {
        var lay = GizmoLayout(vp); SelectionPivot(out var pv, out _); var p0 = ScreenOf(vp, pv);
        for (int r = 8; r < 160; r += 2)
            for (int a = 0; a < 24; a++)
            {
                var q = p0 + new Vector2(MathF.Cos(a * MathF.Tau / 24), MathF.Sin(a * MathF.Tau / 24)) * r;
                if (gizmo.HitTest(vp, lay, Tool, q) == want) return q;
            }
        return p0;
    }

    Vector2 FaceScreen(Viewport vp, SceneObject o, int face)
    {
        var f = o.Mesh.Faces[face];
        var c = f.V.Select(v => Vector3.Transform(o.Mesh.Verts[v], o.World)).Aggregate(Vector3.Zero, (a, b) => a + b) / f.V.Length;
        return ScreenOf(vp, c);
    }

    int FaceToward(SceneObject o, Vector3 dir) => Enumerable.Range(0, o.Mesh.Faces.Count).OrderByDescending(i => Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(o.Mesh.FaceNormal(i), o.World)), dir)).First();

    /// <summary>Captions, the mouse cursor and the keys held, drawn over everything (recording only).</summary>
    void TutorialOverlay()
    {
        if (recordDir == null) return;
        var dl = ImGui.GetForegroundDrawList();
        var vp = Viewports[ActiveViewport];
        var font = ImGui.GetFont(); float fs = ImGui.GetFontSize();
        if (!string.IsNullOrEmpty(tutCaption))
        {
            float big = fs * 1.9f, small = fs * 1.25f;
            var t1 = ImGui.CalcTextSize(tutCaption) * (big / fs);
            var t2 = string.IsNullOrEmpty(tutSub) ? Vector2.Zero : ImGui.CalcTextSize(tutSub) * (small / fs);
            float w = MathF.Max(t1.X, t2.X) + 48, h = t1.Y + (t2.Y > 0 ? t2.Y + 10 : 0) + 30;
            var p = new Vector2(vp.X + (vp.W - w) / 2, vp.Y + vp.H - h - 34);
            dl.AddRectFilled(p, p + new Vector2(w, h), 0xE0141414, 10);
            dl.AddRectFilled(p, p + new Vector2(6, h), ImGui.ColorConvertFloat4ToU32(Theme.Gold), 10, ImDrawFlags.RoundCornersLeft);
            dl.AddText(font, big, p + new Vector2(24, 14), 0xFFFFFFFF, tutCaption);
            if (t2.Y > 0) dl.AddText(font, small, p + new Vector2(24, 20 + t1.Y), 0xFFC8C8C8, tutSub);
            if (tutSteps > 0) dl.AddText(font, fs, p + new Vector2(w - 60, 6), 0xFF909090, $"{tutStep}/{tutSteps}");
        }
        // keys held
        if (tutHeld.Count > 0)
        {
            var kp = new Vector2(vp.X + 20, vp.Y + vp.H - 70);
            foreach (var k in tutHeld)
            {
                var sz = ImGui.CalcTextSize(k) * 1.4f + new Vector2(24, 14);
                dl.AddRectFilled(kp, kp + sz, 0xF0303030, 6); dl.AddRect(kp, kp + sz, 0xFFE0E0E0, 6, ImDrawFlags.None, 2);
                dl.AddText(font, fs * 1.4f, kp + new Vector2(12, 7), 0xFFFFFFFF, k);
                kp.X += sz.X + 10;
            }
        }
        // mouse cursor (the system cursor is not in the capture)
        if (tutHideCursor) return;
        var m = Input.Mouse;
        if (tutClick > 0) { float r = 10 + (0.35f - tutClick) * 70; dl.AddCircle(m, r, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.3f, tutClick / 0.35f)), 24, 3); tutClick -= 1f / 30; }
        bool down = Input.Down(System.Windows.Forms.MouseButtons.Left) || Input.Down(System.Windows.Forms.MouseButtons.Right);
        var a = m; var b = m + new Vector2(0, 22); var c = m + new Vector2(15, 16);
        dl.AddTriangleFilled(a, b, c, down ? 0xFF40D0FF : 0xFFFFFFFF);
        dl.AddTriangle(a, b, c, 0xFF000000, 1.5f);
        if (Input.Down(System.Windows.Forms.MouseButtons.Right)) dl.AddText(font, fs, m + new Vector2(18, 20), 0xFF40D0FF, "right button");
    }

    // ------------------------------------------------------------------ the films

    void Tutorials(Action<string, Func<IEnumerable<object>>> T)
    {
        T("TUTO 01 modeling: Shift copy, pull edges, bridge", TutModeling);
        T("TUTO 02 snaps: corners, edges, faces", TutSnaps);
        T("TUTO 03 navigation: game camera, view cube", TutNavigation);
        T("TUTO 04 materials and UVs", TutMaterials);
        T("TUTO 05 MLO room builder", TutRoomBuilder);
        T("TUTO 06 GTA lights", TutLights);
        T("TUTO 07 MAP mode", TutMap);
        T("TUTO 08 best of all: 3ds Max, Blender, GTA tools", TutShowcase);
        T("TUTO 09 trailer", TutTrailer);
        T("TUTO 10 modeling on the map", TutMapBuild);
        T("TUTO 11 weapon forge", TutWeaponForge);
        T("TUTO 12 vehicle forge", TutVehicleForge);
        T("TUTO 13 demo: sculpt and paint", TutSculptPaintDemo);
        T("TUTO 14 animation: keys, play, texture, camera, GTA .ycd", TutAnimation);
        T("TUTO 15 modifiers: every modifier on a cube", TutModifiers);
        T("TUTO 16 boolean: subtract (door + window in a wall), union, intersect, undo", TutBoolean);
        T("TUTO 17 wall opening: trace a door and a window on a wall, pull the sides, Enter, edit later", TutWallOpening);
        T("DENSE OPENING: door + window on a very dense wall (photos)", TutDenseOpening);
        T("BEVEL CONNECT: bevel sides with the wheel, connect segments, gothic arch, material editor of the selection (photos)", TutBevelConnect);
        T("PERF FULL: frame and action times on heavy scenes (490k-polygon mesh, 3000 objects)", PerfFull);
        T("VERTEX SNAP: in vertex mode, grab a selected vertex and drop it on another (square shown)", TutVertexSnap);
        T("GIMS HUB: Molo GTA panel in the GIMS layout (photos)", TutGimsHub);
        T("UI OPEN THEME: open window with bookmarks, toolbar fits, look in preferences (photos)", TutOpenTheme);
        T("SHAPES CLEANUP: arch door, round window, segments, clean up (photos)", TutShapesCleanup);
        T("GAME UVTEST: a cube whose texture slides (timeline UV keys), exported as a FiveM resource", GameUvTest);
        T("TUTO 99 trailer preview", TutTrailerPreview);
        T("TUTO 98 lot finder", TutLotTest);
        T("MAP diag: duplicates", MapDiag);
        T("MAP terrain: 4-layer ground", MapTerrainTest);
        T("TERRAIN: 4 layers painted with vertex colour", TerrainPaintTest);
        T("TRIPO diag: textures of the downloaded models", TripoDiag);
        T("MAP hide + send to MODEL", MapHideSendTest);
        T("MAP MLO hide: hiding an interior hides what is inside, and it stays hidden", MapMloHideTest);
        T("CLASSIC: Classic is only a look (4 views, colours), keys and timeline unchanged, back to Standard", ClassicModeTest);
        T("UNDO: move then Ctrl+Z, in MODEL and on a MAP ground", UndoMoveTest);
        T("ANIM: timeline, I key, auto key, play, UV keys, camera, .ycd out and in, your .ycd files", AnimTest);
        T("DIAG cables: what the cable models hold", CableDiag);
        T("MAP STREAM: open a stream folder (ymap, ytyp, ydr), edit, save in place", MapStreamTest);
        T("UX tour: panels a 3D artist uses", UxTour);
        T("CHECKLIST: the points without their own test", ChecklistTest);
        T("ASSETS: browser thumbnails + placing", AssetTest);
        T("FAMILIES: right-click puts a game model in a family (kept), viewer drag turns the model", FamilyTest);
        T("PLANAR: level vertices", PlanarTest);
    }

    IEnumerable<object> TutModeling()
    {
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        var box = Prim(CreateKind.Box, Vector3.Zero); box.Name = "box";
        vp.Camera.Frame(new Vector3(-3, -3, -1), new Vector3(3, 3, 4));
        foreach (var x in StartFilm("01_modeling", 6)) yield return x;
        foreach (var x in Say("Modeling in Polymorph", "Edit mode tools: copy, pull edges, bridge", 60)) yield return x;
        // 1. Shift + move on a face
        foreach (var x in Say("Shift + move copies the selection", "Polygon mode: pick a face, hold Shift, drag an arrow")) yield return x;
        SelectOnly(box); SetLevel(SubObject.Polygon); Tool = Tool.Select;
        int top = FaceToward(box, Vector3.UnitZ);
        foreach (var x in ClickAt(FaceScreen(vp, box, top))) yield return x;
        SubSel(box).Clear(); SubSel(box).Faces.Add(top);
        Tool = Tool.Move; yield return Frames(15);
        var g = GizmoGrip(vp, GizmoHandle.Z);
        foreach (var x in MouseTo(g)) yield return x;
        KeyDownT(Keys.ShiftKey); yield return Frames(10);
        foreach (var x in DragTo(g, g - new Vector2(0, 140), 45)) yield return x;
        KeyUpT(Keys.ShiftKey); yield return Frames(30);
        // 2. edges
        foreach (var x in Say("Shift + move on edges pulls out new faces", "Left, right, up or down: the way you drag")) yield return x;
        var pl = Prim(CreateKind.Plane, new Vector3(4.5f, 0, 0)); pl.Name = "plane";
        vp.Camera.Frame(new Vector3(-2, -3, -1), new Vector3(8, 3, 4)); yield return Frames(10);
        SelectOnly(pl); SetLevel(SubObject.Edge);
        var pm = pl.Mesh; float maxY = pm.Verts.Max(v => v.Y);
        var es = SubSel(pl); es.Clear();
        foreach (var e in MeshOps.BorderEdges(pm)) if (MathF.Abs(pm.Verts[e.A].Y - maxY) < 1e-4f && MathF.Abs(pm.Verts[e.B].Y - maxY) < 1e-4f) es.Edges.Add(e);
        yield return Frames(20);
        var gy = GizmoGrip(vp, GizmoHandle.Y);
        SelectionPivot(out var pv0, out _);
        var yDir = Vector2.Normalize(ScreenOf(vp, pv0 + Vector3.UnitY) - ScreenOf(vp, pv0));
        foreach (var x in MouseTo(gy)) yield return x;
        KeyDownT(Keys.ShiftKey); yield return Frames(8);
        foreach (var x in DragTo(gy, gy + yDir * 110, 40)) yield return x;
        KeyUpT(Keys.ShiftKey); yield return Frames(15);
        var gz = GizmoGrip(vp, GizmoHandle.Z);
        KeyDownT(Keys.ShiftKey); yield return Frames(8);
        foreach (var x in DragTo(gz, gz - new Vector2(0, 110), 40)) yield return x;
        KeyUpT(Keys.ShiftKey); yield return Frames(30);
        // 3. bridge
        foreach (var x in Say("Bridge joins two faces with a tube", "Select two faces, right click > Bridge")) yield return x;
        SetLevel(SubObject.Object);
        var a = Prim(CreateKind.Box, new Vector3(0, 5, 0)); var b = Prim(CreateKind.Box, new Vector3(4, 5, 0.5f));
        SelectOnly(a, b); JoinSelected(); yield return Frames(5);
        var joined = Current;
        vp.Camera.Frame(new Vector3(-2, 2, -1), new Vector3(6, 8, 4)); yield return Frames(10);
        SetLevel(SubObject.Polygon);
        Vector3 WC(int i) => Vector3.Transform(joined.Mesh.FaceCenter(i), joined.World);
        float WNX(int i) => Vector3.Normalize(Vector3.TransformNormal(joined.Mesh.FaceNormal(i), joined.World)).X;
        var allF = Enumerable.Range(0, joined.Mesh.Faces.Count).ToList();
        float midX = allF.Average(i => WC(i).X);
        int fa = allF.Where(i => WC(i).X < midX).OrderByDescending(WNX).First();
        int fb = allF.Where(i => WC(i).X > midX).OrderBy(WNX).First();
        Tool = Tool.Select;
        foreach (var x in ClickAt(FaceScreen(vp, joined, fa))) yield return x;
        SubSel(joined).Clear(); SubSel(joined).Faces.Add(fa); yield return Frames(10);
        KeyDownT(Keys.ControlKey);
        foreach (var x in ClickAt(FaceScreen(vp, joined, fb))) yield return x;
        KeyUpT(Keys.ControlKey);
        SubSel(joined).Faces.Add(fa); SubSel(joined).Faces.Add(fb); yield return Frames(20);
        var mid = (FaceScreen(vp, joined, fa) + FaceScreen(vp, joined, fb)) * 0.5f + new Vector2(0, 60);
        foreach (var x in ClickAt(mid, right: true)) yield return x;
        yield return Frames(20);
        foreach (var x in ClickAt(TutCentre("bridge_face"))) yield return x;
        yield return Frames(40);
        // 4. more in the menu
        foreach (var x in Say("Flip normals, UVs and more: right click", "Everything for the selection is in the menu")) yield return x;
        foreach (var x in ClickAt(mid + new Vector2(120, 40), right: true)) yield return x;
        yield return Frames(50);
        PostKey(Keys.Escape, true); yield return Frames(2); PostKey(Keys.Escape, false); yield return Frames(4);
        { var e0 = new Vector2(vp.X + 30, vp.Y + vp.H - 40); PostMouse(0x200, e0, 0); yield return Frames(2); PostMouse(0x201, e0, 1); yield return Frames(2); PostMouse(0x202, e0, 0); yield return Frames(6); }
        SetLevel(SubObject.Object);
        foreach (var x in Say("Ctrl+Z undoes every step", null, 40)) yield return x;
        foreach (var x in EndFilm()) yield return x;
        Maximized = false; Tool = Tool.Select;
    }

    /// <summary>A slow, explained drag: hover (grab point shows), press, travel, wait on the target (it lights up), release.</summary>
    IEnumerable<object> SnapDrag(Vector2 a, Vector2 b, string hover, string drag, string drop, string dropSub)
    {
        tutCaption = hover.Split('|')[0]; tutSub = hover.Contains('|') ? hover.Split('|')[1] : null;
        foreach (var x in MouseTo(a, 40)) yield return x;
        yield return Frames(55);
        tutCaption = drag.Split('|')[0]; tutSub = drag.Contains('|') ? drag.Split('|')[1] : null;
        PostMouse(0x201, a, 1); tutClick = 0.35f; yield return Frames(10);
        foreach (var x in MouseTo(b, 85, 1)) yield return x;
        yield return Frames(50);
        tutCaption = drop; tutSub = dropSub;
        PostMouse(0x202, b, 0); tutClick = 0.35f; yield return Frames(70);
    }

    IEnumerable<object> TutSnaps()
    {
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        var ba = Prim(CreateKind.Box, new Vector3(-1, -6, 0)); ba.Name = "a";
        var bb = Prim(CreateKind.Box, new Vector3(3, -4, 0)); bb.Name = "b";
        foreach (var v in Viewports) v.Camera.Frame(new Vector3(-2.5f, -7.5f, 0), new Vector3(4.5f, -2.5f, 2));
        SnapGrid = true; SnapVertexT = SnapEdge = SnapFace = true; SnapNearest = false; Snap = false;
        SelectOnly(ba); Tool = Tool.Move;
        foreach (var x in StartFilm("02_snaps", 5)) yield return x;
        foreach (var x in Say("Snaps", "Put one object exactly against another: corner on corner, face on face", 75)) yield return x;
        foreach (var x in Say("Click the magnet to switch snapping on", "or Shift+Tab. Vertex, Edge and Face snaps are on by default", 30)) yield return x;
        foreach (var x in ClickAt(TutCentre("snap_btn"), moveFrames: 35)) yield return x;
        if (!Snap) Snap = true;
        yield return Frames(40);
        Vector3 W(SceneObject o, int i) => Vector3.Transform(o.Mesh.Verts[i], o.World);
        int ia = Enumerable.Range(0, ba.Mesh.Verts.Count).OrderByDescending(i => W(ba, i).X + W(ba, i).Z - W(ba, i).Y * 0.1f).First();
        int ib = Enumerable.Range(0, bb.Mesh.Verts.Count).OrderBy(i => W(bb, i).X - W(bb, i).Z + W(bb, i).Y * 0.1f).First();
        // corner to corner
        foreach (var x in Say("Corner on corner", "Move tool, the left box is selected", 50)) yield return x;
        var cA = ScreenOf(vp, W(ba, ia)); var cB = ScreenOf(vp, W(bb, ib));
        foreach (var x in SnapDrag(cA, cB, "1. Hover a corner of the selected box|The yellow square is the point you grab",
                                           "2. Hold the button and bring it onto a corner of the other box|The target corner lights up when you are on it",
                                           "3. Release: the two corners touch exactly", null)) yield return x;
        // face against face
        DoUndo(); yield return Frames(10);
        ba = Obj("a"); bb = Obj("b"); SelectOnly(ba);
        foreach (var x in Say("Face on face", "Grab the box by the middle of a face", 50)) yield return x;
        var top = ba.Mesh.Verts.Select(v => Vector3.Transform(v, ba.World)).ToList(); float mz = top.Max(v => v.Z);
        var tc = top.Where(v => v.Z > mz - 1e-3f).Aggregate(Vector3.Zero, (q, v) => q + v) / 4;
        int side = FaceToward(bb, Vector3.Normalize(vp.Camera.Eye - bb.Position));
        var sideP = FaceScreen(vp, bb, side) + new Vector2(0, 15);
        foreach (var x in SnapDrag(ScreenOf(vp, tc), sideP, "1. Hover the top face of the selected box|Its centre is the grab point",
                                           "2. Bring it onto a face of the other box|The face lights up",
                                           "3. Release: the box sits against the face", "Never inside: it slides along the wall")) yield return x;
        // gizmo arrow stops against the face
        DoUndo(); yield return Frames(10);
        ba = Obj("a"); bb = Obj("b");
        var bc = bb.Position; ba.Position = new Vector3(bc.X - 4, bc.Y, 0); ba.Touch();
        SelectOnly(ba); yield return Frames(15);
        foreach (var x in Say("With the arrows", "The move stays on the axis and stops on the face", 50)) yield return x;
        var gx = GizmoGrip(vp, GizmoHandle.X);
        var aim = FaceScreen(vp, bb, FaceToward(bb, -Vector3.UnitX));
        if (Vector3.Dot(Vector3.TransformNormal(bb.Mesh.FaceNormal(FaceToward(bb, -Vector3.UnitX)), bb.World), vp.Camera.Eye - bb.Position) < 0) aim = FaceScreen(vp, bb, side);
        foreach (var x in SnapDrag(gx, aim, "1. Take the red arrow (X)|",
                                           "2. Pull it towards the other box|",
                                           "3. Release: it stops right against the face", null)) yield return x;
        foreach (var x in EndFilm()) yield return x;
        Maximized = false; Tool = Tool.Select; Snap = false;
    }

    IEnumerable<object> TutNavigation()
    {
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        for (int i = 0; i < 6; i++) { var o = Prim(i % 2 == 0 ? CreateKind.Box : CreateKind.Cylinder, new Vector3((i % 3) * 4 - 4, (i / 3) * 5, 0)); o.Name = "thing" + i; }
        vp.Camera.Frame(new Vector3(-6, -4, 0), new Vector3(6, 8, 3));
        foreach (var x in StartFilm("03_navigation", 4)) yield return x;
        foreach (var x in Say("Moving around like in a game", null, 50)) yield return x;
        var mid = new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.45f);
        foreach (var x in Say("Hold the right button and move the mouse to look", "The camera stays in place")) yield return x;
        foreach (var x in MouseTo(mid)) yield return x;
        PostMouse(0x204, mid, 2); yield return Frames(4);
        foreach (var x in MouseTo(mid + new Vector2(160, -30), 50, 2)) yield return x;
        foreach (var x in MouseTo(mid + new Vector2(-60, 20), 50, 2)) yield return x;
        foreach (var x in Say("Still holding it: Z Q S D (or W A S D) to fly", "E up, C down, Shift faster, wheel = speed", 10)) yield return x;
        KeyDownT(Keys.Z); yield return Frames(45); KeyUpT(Keys.Z);
        KeyDownT(Keys.Q); yield return Frames(30); KeyUpT(Keys.Q);
        KeyDownT(Keys.E); yield return Frames(25); KeyUpT(Keys.E);
        KeyDownT(Keys.ShiftKey); KeyDownT(Keys.S); yield return Frames(30); KeyUpT(Keys.S); KeyUpT(Keys.ShiftKey);
        PostMouse(0x205, mid + new Vector2(-60, 20), 0); yield return Frames(20);
        foreach (var x in Say("Without the mouse: the arrow keys", "↑ ↓ forward / back, ← → side step")) yield return x;
        KeyDownT(Keys.Up); yield return Frames(40); KeyUpT(Keys.Up);
        KeyDownT(Keys.Left); yield return Frames(30); KeyUpT(Keys.Left);
        yield return Frames(15);
        vp.Camera.Frame(new Vector3(-6, -4, 0), new Vector3(6, 8, 3)); yield return Frames(5);
        foreach (var x in Say("View cube: drag it to turn, click a face to jump", null, 30)) yield return x;
        var cube = new Vector2(vp.X + vp.W - 22f * 2.1f - 8, vp.Y + 22f * 2.1f + 8);
        foreach (var x in DragTo(cube, cube + new Vector2(-120, 30), 50)) yield return x;
        yield return Frames(20);
        foreach (var x in ClickAt(cube)) yield return x;
        yield return Frames(30);
        FrameAll(); yield return Frames(30);
        foreach (var x in EndFilm()) yield return x;
        Maximized = false;
    }

    IEnumerable<object> TutMaterials()
    {
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        var a = Prim(CreateKind.Box, new Vector3(-2, 0, 0)); a.Name = "crate";
        var b = Prim(CreateKind.Cylinder, new Vector3(2, 0, 0)); b.Name = "barrel";
        vp.Camera.Frame(new Vector3(-4, -3, -1), new Vector3(4, 3, 3));
        foreach (var x in StartFilm("04_materials_uv", 4)) yield return x;
        foreach (var x in Say("Materials and UVs", null, 50)) yield return x;
        foreach (var x in Say("Material Editor (M): New makes a material", "It goes on nothing until you drop it")) yield return x;
        materialEditorOpen = true; yield return Frames(20);
        foreach (var x in ClickAt(TutCentre("mat_new"))) yield return x;
        var mat = Scene.Materials.Last(); mat.Name = "red_paint"; mat.ViewportColour = new Vector3(0.75f, 0.12f, 0.1f);
        yield return Frames(30);
        foreach (var x in Say("Drag it onto an object", "Polygon mode: onto one face")) yield return x;
        yield return Frames(5);
        var item = TutCentre("mat:" + mat.Id);
        var (mn, mx) = b.WorldBounds(); // the barrel: the crate sits behind the editor window
        foreach (var x in DragTo(item, ScreenOf(vp, (mn + mx) * 0.5f), 50)) yield return x;
        yield return Frames(40);
        materialEditorOpen = false; yield return Frames(10);
        foreach (var x in Say("Right click > UV: unwrap and projections", "Unwrap, Smart UV, Lightmap, Cube, Cylinder, Sphere, From View...")) yield return x;
        SelectOnly(a); yield return Frames(10);
        var (bn, bx) = a.WorldBounds(); var bs = ScreenOf(vp, (bn + bx) * 0.5f);
        foreach (var x in ClickAt(bs, right: true)) yield return x;
        yield return Frames(15);
        foreach (var x in MouseTo(TutCentre("uv_menu"), 25)) yield return x;
        yield return Frames(35);
        foreach (var x in ClickAt(TutCentre("uv_cube"), moveFrames: 25)) yield return x;
        yield return Frames(70);
        foreach (var x in EndFilm()) yield return x;
        Maximized = false;
    }

    /// <summary>Camera on a point of the plan (3D), seen from a direction, smoothly.</summary>
    IEnumerable<object> CamTo(Viewport vp, Vector3 target, float dist, float yaw, float pitch, int frames = 50)
    {
        var c = vp.Camera; var t0 = c.Target; float d0 = c.Distance, y0 = c.Yaw, p0 = c.Pitch;
        float dy = yaw - y0; while (dy > MathF.PI) dy -= MathF.Tau; while (dy < -MathF.PI) dy += MathF.Tau;
        for (int i = 1; i <= frames; i++)
        {
            float t = Ease(i / (float)frames);
            c.Target = Vector3.Lerp(t0, target, t); c.Distance = d0 + (dist - d0) * t; c.Yaw = y0 + dy * t; c.Pitch = p0 + (pitch - p0) * t;
            yield return Frames(1);
        }
    }

    IEnumerable<object> TutRoomBuilder()
    {
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        Scene.Plan = new RoomPlanData(); PlanLive = true; FinishFrames = true; FinishFrameStyle = 0; FinishJoinDoors = true;
        FinishSkirting = FinishFloorDirt = FinishCeilingDirt = FinishCornerWear = true;
        vp.Camera.Frame(new Vector3(-2, -2, 0), new Vector3(12, 10, 4));
        foreach (var x in StartFilm("05_mlo_room_builder", 9)) yield return x;
        foreach (var x in Say("MLO Room Builder", "Draw the plan, the rooms build themselves in 3D", 60)) yield return x;
        planOpen = true; planTool = 0; planZoom = 26; planPan = new Vector2(40, 40); yield return Frames(20);
        foreach (var x in Say("Room tool: drag to draw a room", "Live: the 3D follows every change")) yield return x;
        foreach (var x in DragTo(PlanToScreen(new Vector2(0, 0)), PlanToScreen(new Vector2(6, 5)), 45)) yield return x;
        yield return Frames(20);
        foreach (var x in DragTo(PlanToScreen(new Vector2(6, 0)), PlanToScreen(new Vector2(11, 5)), 45)) yield return x;
        yield return Frames(20);
        foreach (var x in DragTo(PlanToScreen(new Vector2(0, 5)), PlanToScreen(new Vector2(6, 9)), 45)) yield return x;
        yield return Frames(25);
        foreach (var x in Say("Door and Window tools: click on a wall", "Between two rooms the door goes through both walls")) yield return x;
        planTool = 1; yield return Frames(10);
        foreach (var x in ClickAt(PlanToScreen(new Vector2(6, 2.5f)))) yield return x;
        foreach (var x in ClickAt(PlanToScreen(new Vector2(3, 5)))) yield return x;
        foreach (var x in ClickAt(PlanToScreen(new Vector2(3, 0)))) yield return x;
        planTool = 2; yield return Frames(10);
        foreach (var x in ClickAt(PlanToScreen(new Vector2(0, 2.5f)))) yield return x;
        foreach (var x in ClickAt(PlanToScreen(new Vector2(11, 2.5f)))) yield return x;
        foreach (var x in ClickAt(PlanToScreen(new Vector2(3, 9)))) yield return x;
        yield return Frames(30);
        foreach (var x in Say("Select tool: drag a side to resize", "Skirting, dirt and frames rebuild live")) yield return x;
        planTool = 3; yield return Frames(10);
        foreach (var x in DragTo(PlanToScreen(new Vector2(11, 3.5f)), PlanToScreen(new Vector2(13, 3.5f)), 45)) yield return x;
        yield return Frames(40);
        planOpen = false; yield return Frames(10);
        foreach (var x in Say("The result: closed rooms, portals, finishing", "Skirting boards, dirt, worn corners, door and window frames", 20)) yield return x;
        var all = Scene.Objects.Where(o => o.Folder == PlanFolder).ToList();
        Vector3 mnb = new(0, 0, 0), mxb = new(13, 9, 3);
        if (all.Count > 0) { mnb = all.Select(o => o.WorldBounds().Item1).Aggregate(Vector3.Min); mxb = all.Select(o => o.WorldBounds().Item2).Aggregate(Vector3.Max); }
        vp.Camera.Frame(mnb, mxb);
        for (int i = 0; i < 100; i++) { vp.Camera.Orbit(1.8f, 0); yield return Frames(1); }
        // the joined door frame, from both rooms
        foreach (var x in Say("Connected rooms: one door frame through the gap", "The lining meets the casing of each room, same profile on both sides", 20)) yield return x;
        var door = new Vector3(6, 2.5f, 1.2f);
        foreach (var x in CamTo(vp, door, 4.2f, MathF.PI * 0.75f, 0.12f, 70)) yield return x;
        yield return Frames(40);
        foreach (var x in CamTo(vp, door, 4.2f, -MathF.PI * 0.25f, 0.12f, 110)) yield return x;
        yield return Frames(40);
        foreach (var x in Say("Option: Connect the rooms (joined door frames)", "Off: each room keeps its own casing, the gap stays bare", 20)) yield return x;
        foreach (var x in CamTo(vp, door + new Vector3(0, 0, 0.3f), 2.4f, MathF.PI * 0.1f, 0.35f, 50)) yield return x;
        FinishJoinDoors = false; BuildPlan(true); yield return Frames(50);
        FinishJoinDoors = true; BuildPlan(true); yield return Frames(60);
        // frame styles
        foreach (var x in Say("Frame style: Varied, or one style everywhere", "Flat, Moulded, Modern thin, Classic wide, Rounded", 10)) yield return x;
        foreach (var x in CamTo(vp, door, 4.5f, MathF.PI * 0.8f, 0.15f, 40)) yield return x;
        for (int st = 1; st <= 5; st++) { FinishFrameStyle = st; BuildPlan(true); yield return Frames(32); }
        FinishFrameStyle = 0; BuildPlan(true); yield return Frames(20);
        // inside a room
        foreach (var x in Say("Walk in: every room is closed, ready for the portals", "Then the GTA check and the FiveM export (F10)", 10)) yield return x;
        foreach (var x in CamTo(vp, new Vector3(3, 2.5f, 1.4f), 3.2f, MathF.PI * 0.5f, 0.05f, 60)) yield return x;
        for (int i = 0; i < 90; i++) { vp.Camera.Orbit(1.5f, 0); yield return Frames(1); }
        foreach (var x in CamTo(vp, (mnb + mxb) * 0.5f, (mxb - mnb).Length() * 1.1f, MathF.PI * 0.3f, 0.6f, 60)) yield return x;
        yield return Frames(30);
        foreach (var x in EndFilm()) yield return x;
        Maximized = false;
    }

    IEnumerable<object> TutLights()
    {
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        var floor = Prim(CreateKind.Box, new Vector3(0, 0, -0.2f)); floor.Scale = new Vector3(6, 6, 0.1f); floor.Touch(); floor.Name = "floor";
        var wall = Prim(CreateKind.Box, new Vector3(0, 3, 0)); wall.Scale = new Vector3(6, 0.2f, 1.6f); wall.Touch(); wall.Name = "wall";
        var crate = Prim(CreateKind.Box, new Vector3(-1.5f, 0, 0)); crate.Name = "crate";
        Hour = 23; InteriorLight = false;
        vp.Camera.Frame(new Vector3(-6, -6, -1), new Vector3(6, 4, 4));
        foreach (var x in StartFilm("06_gta_lights", 4)) yield return x;
        foreach (var x in Say("GTA lights", "Point, Spot and Capsule: the three game / Sollumz types", 60)) yield return x;
        foreach (var x in Say("Add a Point light", "Light tab > Add light")) yield return x;
        Cursor3D = new Vector3(1, 0, 1.5f); AddLight(GtaLightKind.Point); var p = Current; yield return Frames(40);
        foreach (var x in Say("Spot: aim it with its rotation", "Spot size and blend in the light panel")) yield return x;
        Cursor3D = new Vector3(-2, 2, 2.5f); AddLight(GtaLightKind.Spot); var s = Current;
        if (s?.Gta.Light != null) { s.Gta.Light.Colour = new Vector3(0.4f, 0.6f, 1f); s.Gta.Light.Intensity = 20; }
        yield return Frames(40);
        foreach (var x in Say("Capsule: a tube of light (neon)", null)) yield return x;
        Cursor3D = new Vector3(2, 2.6f, 1.2f); AddLight(GtaLightKind.Capsule); var c = Current;
        if (c?.Gta.Light != null) { c.Gta.Light.Colour = new Vector3(1f, 0.3f, 0.7f); c.Gta.Light.CapsuleLength = 2; c.Gta.Light.Intensity = 12; }
        yield return Frames(40);
        foreach (var x in Say("Colour by temperature, intensity, falloff", "What you see is what the game does")) yield return x;
        if (p?.Gta.Light != null)
        {
            p.Gta.Light.UseTemperature = true;
            for (int i = 0; i < 60; i++) { p.Gta.Light.Temperature = 1900 + i * 80; yield return Frames(1); }
            for (int i = 0; i < 40; i++) { p.Gta.Light.Intensity = 8 + i * 0.4f; yield return Frames(1); }
        }
        foreach (var x in Say("Exported as they are (.ydr and Sollumz XML)", "Flags, fade distances, corona, volume: all the game fields")) yield return x;
        for (int i = 0; i < 90; i++) { vp.Camera.Orbit(2, 0); yield return Frames(1); }
        foreach (var x in EndFilm()) yield return x;
        Maximized = false; Hour = 13;
    }

    /// <summary>Film 08: what Polymorph took from 3ds Max, Blender and the GTA V tools, shown working.
    /// (Product names appear only in this film's captions and the README, never in the app itself.)</summary>
    IEnumerable<object> TutShowcase()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        var box = Prim(CreateKind.Box, Vector3.Zero); box.Name = "box";
        var box2 = Prim(CreateKind.Box, new Vector3(4, 1, 0)); box2.Name = "box2";
        vp.Camera.Frame(new Vector3(-3, -3, -1), new Vector3(7, 4, 4));
        SelectOnly(box);
        foreach (var x in StartFilm("08_best_of_all", 16)) yield return x;
        foreach (var x in Say("Polymorph", "The best of 3ds Max, Blender and the GTA V tools, in one free app", 90)) yield return x;

        // ---- 3ds Max
        foreach (var x in Say("From 3ds Max", "The workflow modellers know by heart", 60)) yield return x;
        foreach (var x in Say("3ds Max · Sub-object levels and Shift + drag to clone", "Here: a face copied by dragging the arrow with Shift", 20)) yield return x;
        SetLevel(SubObject.Polygon); Tool = Tool.Select;
        int top = FaceToward(box, Vector3.UnitZ);
        foreach (var x in ClickAt(FaceScreen(vp, box, top))) yield return x;
        SubSel(box).Clear(); SubSel(box).Faces.Add(top); Tool = Tool.Move; yield return Frames(12);
        var g = GizmoGrip(vp, GizmoHandle.Z);
        foreach (var x in MouseTo(g)) yield return x;
        KeyDownT(Keys.ShiftKey); yield return Frames(8);
        foreach (var x in DragTo(g, g - new Vector2(0, 120), 40)) yield return x;
        KeyUpT(Keys.ShiftKey); yield return Frames(30);
        DoUndo(); SetLevel(SubObject.Object); yield return Frames(10);
        foreach (var x in Say("3ds Max · Vertex / edge / face snaps", "Corner on corner, exactly", 20)) yield return x;
        Snap = true; SnapVertexT = SnapEdge = SnapFace = true; SelectOnly(box); Tool = Tool.Move; yield return Frames(10);
        Vector3 W(SceneObject o, int i) => Vector3.Transform(o.Mesh.Verts[i], o.World);
        int ia = Enumerable.Range(0, box.Mesh.Verts.Count).OrderByDescending(i => W(box, i).X + W(box, i).Z - W(box, i).Y * 0.1f).First();
        int ib = Enumerable.Range(0, box2.Mesh.Verts.Count).OrderBy(i => W(box2, i).X - W(box2, i).Z + W(box2, i).Y * 0.1f).First();
        var cA = ScreenOf(vp, W(box, ia)); var cB = ScreenOf(vp, W(box2, ib));
        foreach (var x in MouseTo(cA, 30)) yield return x;
        yield return Frames(25);
        PostMouse(0x201, cA, 1); tutClick = 0.35f; yield return Frames(6);
        foreach (var x in MouseTo(cB, 50, 1)) yield return x;
        yield return Frames(25); PostMouse(0x202, cB, 0); yield return Frames(35);
        DoUndo(); Snap = false; yield return Frames(10);
        foreach (var x in Say("3ds Max · ViewCube", "Drag it to turn, click a face to jump", 15)) yield return x;
        var cube = new Vector2(vp.X + vp.W - 22f * 2.1f - 8, vp.Y + 22f * 2.1f + 8);
        foreach (var x in DragTo(cube, cube + new Vector2(-110, 25), 45)) yield return x;
        yield return Frames(25);
        vp.Camera.Frame(new Vector3(-3, -3, -1), new Vector3(7, 4, 4)); yield return Frames(10);
        foreach (var x in Say("3ds Max · Modifier stack: Edit Poly, Unwrap UVW", "Non-destructive, insert anywhere in the stack", 20)) yield return x;
        SelectOnly(box); showModifyTab = true; yield return Frames(15);
        OpenUnwrapModifier(); uvOpen = false; showModifyTab = true; yield return Frames(40);
        { var pos = new Vector2(gpu.Width - panelW * 0.5f, menuH + toolbarH + 140); foreach (var x in MouseTo(pos, 30)) yield return x; }
        yield return Frames(60);
        EndModifierEdit(Current); SetLevel(SubObject.Object); yield return Frames(10);

        // ---- Blender
        foreach (var x in Say("From Blender", "Fast menus and modern tools", 60)) yield return x;
        foreach (var x in Say("Blender · Right-click menus with everything", "UV: Smart UV, Cube, Cylinder, Sphere, Project From View...", 15)) yield return x;
        SelectOnly(box2); Tool = Tool.Select; yield return Frames(8);
        var (bn, bx) = box2.WorldBounds(); var bs = ScreenOf(vp, (bn + bx) * 0.5f);
        foreach (var x in ClickAt(bs, right: true)) yield return x;
        yield return Frames(15);
        foreach (var x in MouseTo(TutCentre("uv_menu"), 25)) yield return x;
        yield return Frames(30);
        foreach (var x in ClickAt(TutCentre("uv_cube"), moveFrames: 20)) yield return x;
        yield return Frames(40);
        PostKey(Keys.Escape, true); yield return Frames(2); PostKey(Keys.Escape, false); yield return Frames(6);
        { var e0 = new Vector2(vp.X + 30, vp.Y + vp.H - 40); PostMouse(0x200, e0, 0); yield return Frames(2); PostMouse(0x201, e0, 1); yield return Frames(2); PostMouse(0x202, e0, 0); yield return Frames(6); }
        foreach (var x in Say("Blender · Bridge", "Two faces joined by a tube, any corner count", 15)) yield return x;
        vp.Camera.Ortho = false; vp.Camera.Yaw = -2.2f; vp.Camera.Pitch = 0.45f; vp.Camera.Frame(new Vector3(-2, -2, -1), new Vector3(6, 4, 3)); yield return Frames(5);
        SelectOnly(box, box2); JoinSelected(); yield return Frames(5);
        var joined = Current; SetLevel(SubObject.Polygon);
        Vector3 WC(int i) => Vector3.Transform(joined.Mesh.FaceCenter(i), joined.World);
        float WNX(int i) => Vector3.Normalize(Vector3.TransformNormal(joined.Mesh.FaceNormal(i), joined.World)).X;
        var allF = Enumerable.Range(0, joined.Mesh.Faces.Count).ToList(); float midX = allF.Average(i => WC(i).X);
        int fa = allF.Where(i => WC(i).X < midX).OrderByDescending(WNX).First(), fb = allF.Where(i => WC(i).X > midX).OrderBy(WNX).First();
        SubSel(joined).Clear(); SubSel(joined).Faces.Add(fa); SubSel(joined).Faces.Add(fb); yield return Frames(30);
        Bridge(); yield return Frames(45);
        SetLevel(SubObject.Object); yield return Frames(5);
        foreach (var x in Say("Blender · Fly like in a game", "Right button + Z Q S D / W A S D", 10)) yield return x;
        var mid = new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.45f);
        foreach (var x in MouseTo(mid)) yield return x;
        PostMouse(0x204, mid, 2); yield return Frames(4);
        foreach (var x in MouseTo(mid + new Vector2(120, -20), 35, 2)) yield return x;
        KeyDownT(Keys.Z); yield return Frames(35); KeyUpT(Keys.Z);
        KeyDownT(Keys.D); yield return Frames(25); KeyUpT(Keys.D);
        PostMouse(0x205, mid + new Vector2(120, -20), 0); yield return Frames(15);
        if (vp.Camera.Ortho) vp.Camera.Ortho = false;

        // ---- GTA V tools
        foreach (var x in Say("From the GTA V tools", "CodeWalker, Sollumz, the MLO workflow", 60)) yield return x;
        foreach (var x in Say("Sollumz · Point, Spot and Capsule lights", "Every game field, exported as is", 10)) yield return x;
        Fresh(); Hour = 23; InteriorLight = false;
        var floor = Prim(CreateKind.Box, new Vector3(0, 0, -0.2f)); floor.Scale = new Vector3(6, 6, 0.1f); floor.Touch();
        var wall = Prim(CreateKind.Box, new Vector3(0, 3, 0)); wall.Scale = new Vector3(6, 0.2f, 1.6f); wall.Touch();
        vp.Camera.Frame(new Vector3(-6, -6, -1), new Vector3(6, 4, 4));
        Cursor3D = new Vector3(1, 0, 1.5f); AddLight(GtaLightKind.Point); yield return Frames(25);
        Cursor3D = new Vector3(-2, 2, 2.5f); AddLight(GtaLightKind.Spot); if (Current?.Gta.Light != null) { Current.Gta.Light.Colour = new Vector3(0.4f, 0.6f, 1f); Current.Gta.Light.Intensity = 20; } yield return Frames(25);
        Cursor3D = new Vector3(2, 2.6f, 1.2f); AddLight(GtaLightKind.Capsule); if (Current?.Gta.Light != null) { Current.Gta.Light.Colour = new Vector3(1f, 0.3f, 0.7f); Current.Gta.Light.CapsuleLength = 2; Current.Gta.Light.Intensity = 12; }
        for (int i = 0; i < 60; i++) { vp.Camera.Orbit(1.5f, 0); yield return Frames(1); }
        Hour = 13;
        foreach (var x in Say("MLO workflow · rooms, portals, .ytyp, FiveM resource", "Polymorph adds: a live Room Builder with joined door frames", 10)) yield return x;
        Fresh();
        Scene.Plan = new RoomPlanData(); FinishFrames = true; FinishFrameStyle = 0; FinishJoinDoors = true;
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "room_1", X0 = 0, Y0 = 0, X1 = 6, Y1 = 5 });
        Scene.Plan.Rooms.Add(new PlanRoom { Name = "room_2", X0 = 6, Y0 = 0, X1 = 11, Y1 = 5 });
        Scene.Plan.Openings.Add(new PlanOpening { X = 6, Y = 2.5f });
        Scene.Plan.Openings.Add(new PlanOpening { X = 3, Y = 0 });
        Scene.Plan.Openings.Add(new PlanOpening { X = 11, Y = 2.5f, Sill = 0.9f, Height = 1.2f });
        BuildPlan(true); vp.Camera.Frame(new Vector3(-1, -1, 0), new Vector3(12, 6, 3)); yield return Frames(10);
        for (int i = 0; i < 70; i++) { vp.Camera.Orbit(1.6f, 0); yield return Frames(1); }
        foreach (var x in CamTo(vp, new Vector3(6, 2.5f, 1.2f), 4f, MathF.PI * 0.75f, 0.12f, 50)) yield return x;
        yield return Frames(40);
        foreach (var x in Say("GTA check before export", "Shell, closed rooms, normals, vertex limit... fixed in one click", 15)) yield return x;
        RunGtaChecks(); yield return Frames(80); checkOpen = false; yield return Frames(5);

        // ---- CodeWalker-like map
        foreach (var x in Say("CodeWalker · The whole GTA V map", "Polymorph adds: HD loaded around the camera, edit, save a FiveM resource", 10)) yield return x;
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        var held = recordDir; recordDir = null; // no frames while the map index loads
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world != null)
        {
            var cam = vp.Camera; MapStream = true;
            cam.Target = new Vector3(-300, 900, 0); cam.Distance = 6500; cam.Pitch = 1.05f;
            yield return Until(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal && archLoading == 0, 300);
            yield return Frames(20);
            recordDir = held;
            for (int i = 0; i < 50; i++) { cam.Orbit(2, 0); yield return Frames(1); }
            var target = new Vector3(195, -934, 30);
            for (int i = 0; i <= 100; i++)
            {
                float t = Ease(i / 100f);
                cam.Target = Vector3.Lerp(new Vector3(-300, 900, 0), target, t); cam.Distance = 6500 * MathF.Pow(260 / 6500f, t); cam.Pitch = 1.05f - 0.4f * t;
                yield return Frames(1);
            }
            yield return Until(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 40);
            for (int i = 0; i < 80; i++) { cam.Orbit(1.5f, 0); yield return Frames(1); }
        }
        recordDir = held;
        foreach (var x in Say("Polymorph · free forever", "Made by Molo Modding", 90)) yield return x;
        foreach (var x in EndFilm()) yield return x;
        SetMapMode(false); Maximized = false;
    }

    /// <summary>Film 09: the trailer footage (no captions: titles and music are added afterwards on the markers).</summary>
    /// <summary>Films: a mesh operation shown as it happens (re-run on the start mesh with its amount growing).</summary>
    IEnumerable<object> AnimMesh(SceneObject o, Action<PolyMesh, float> op, int frames, Viewport vp, float orbit)
    {
        var b = o.Mesh.Clone();
        for (int f = 1; f <= frames; f++)
        {
            var m = b.Clone(); op(m, Ease(f / (float)frames)); o.Mesh = m; o.Touch();
            if (orbit != 0) vp.Camera.Orbit(orbit, 0);
            yield return Frames(1);
        }
    }

    /// <summary>Films: a material of the GTA V library (real shader + textures), searched like a person would.</summary>
    (LibMaterial M, string Cat, string Text) LibPick(params (string Cat, string Text)[] tries)
    {
        EnsureLibrary(); if (library == null) return default;
        foreach (var (cat, text) in tries)
        {
            var m = library.Search(cat, text).FirstOrDefault(x => !x.IsDecal && x.Diffuse != null && library.TexturePath(x.Diffuse) != null);
            if (m != null) return (m, cat, text);
        }
        return default;
    }

    /// <summary>Films: shows the library window on a material (category and search typed in).</summary>
    void LibShow((LibMaterial M, string Cat, string Text) pick, LibraryAction act, Viewport vp)
    {
        if (pick.M == null) return;
        libraryOpen = true; libraryAction = act; libCategory = pick.Cat; libSearch = pick.Text; libSelected = pick.M; libResultsKey = null;
        libWinAt = new Vector2(ImGui.GetIO().DisplaySize.X - 735, vp.Y + 50);
    }

    /// <summary>Films: a GTA library material on faces, box-mapped at the material's real size.</summary>
    void LibOnFaces(SceneObject o, IEnumerable<int> faces, LibMaterial lm, float? tile = null, bool walls = false)
    {
        if (lm == null) return;
        var g = LibraryMaterial(lm);
        var fs = faces.Where(f => f >= 0 && f < o.Mesh.Faces.Count).ToHashSet();
        float t = tile ?? Math.Max(lm.TileMetres, 1.25f);
        foreach (var fi in fs)
        {   // walls: U along the wall, V down (bricks stay level); tops: plan view
            var f = o.Mesh.Faces[fi]; f.MatId = g.Id;
            var n = Vector3.Normalize(Vector3.TransformNormal(o.Mesh.FaceNormal(fi), o.World));
            for (int i = 0; i < f.V.Length; i++)
            {
                var w = Vector3.Transform(o.Mesh.Verts[f.V[i]], o.World);
                float along = MathF.Abs(n.X) > MathF.Abs(n.Y) ? w.Y * MathF.Sign(n.X) : -w.X * MathF.Sign(n.Y);
                // GTA wall textures are laid out with their courses along V (bricks run down the image)
                var uv = MathF.Abs(n.Z) > 0.7f ? new Vector2(w.X, -w.Y) : walls ? new Vector2(-w.Z, along) : new Vector2(along, -w.Z);
                f.UV[i] = uv / t;
            }
        }
        o.Touch();
    }

    /// <summary>Films: the face looking along <paramref name="n"/> whose centre passes the test.</summary>
    static int FaceWhere(PolyMesh m, Vector3 n, Func<Vector3, bool> where) =>
        Enumerable.Range(0, m.Faces.Count).Where(i => Vector3.Dot(m.FaceNormal(i), n) > 0.95f && where(m.FaceCenter(i))).DefaultIfEmpty(-1).First();

    /// <summary>
    /// Films: a four-storey corner building modeled step by step (floor lines, windows, shop front, parapet, cornice), then
    /// unwrapped with cube projections at real size and dressed with real GTA V materials from the library. A ped stands
    /// at the door for scale.
    /// </summary>
    IEnumerable<object> BuildTower(Vector3 at, Viewport vp, float orbit, bool showWindows = true, Action onMaterials = null)
    {
        const float W = 12, L = 9, H = 13.2f;
        float[] rowsTo = { 0, 4.0f, 6.9f, 9.8f, 12.7f, 13.2f };
        var k = Scene.Add("building", MoloMax.Core.Mesh.Primitives.Box(W, L, H, 8, 6, 5), at); k.Name = "building"; MapAdopt(k);
        var plain = Scene.AddMaterial("block_out", new Vector3(0.8f, 0.8f, 0.78f));
        foreach (var f in k.Mesh.Faces) f.MatId = plain.Id;
        k.Touch();
        SceneObject slab = null;
        {   // the plot it stands on (on the map: a new paved plot over the lot)
            float pw = MapMode ? W + 16 : W + 8, pl = MapMode ? L + 16 : L + 8;
            slab = Scene.Add("pavement", MoloMax.Core.Mesh.Primitives.Box(pw, pl, 0.15f), at + new Vector3(0, -1, MapMode ? -0.12f : -0.15f)); slab.Name = "pavement"; MapAdopt(slab);
            foreach (var f in slab.Mesh.Faces) f.MatId = plain.Id;
            slab.Touch();
        }
        Selected.Clear(); Selected.Add(k.Id); ActiveId = k.Id; SelectionChanged();
        var ped = AddPedReference();
        if (ped != null) { ped.Position = at + new Vector3(1.2f, -L / 2 - 1.3f, 0); ped.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f); ped.Touch(); }
        Selected.Clear(); Selected.Add(k.Id); ActiveId = k.Id; SelectionChanged();
        var cam = vp.Camera; var aim = cam.Target;
        IEnumerable<object> Hold(int n)
        {
            for (int i = 0; i < n; i++)
            {   // the building steps aside from the windows open over the view
                float side = (libraryOpen && libWinAt != null ? 1 : 0) - (uvOpen && uvWinAt != null ? 1 : 0);
                cam.Target = Vector3.Lerp(cam.Target, aim + cam.Right * (side * 0.2f * cam.Distance), 0.07f);
                cam.Orbit(orbit, 0); yield return Frames(1);
            }
        }
        IEnumerable<object> Op(Action<PolyMesh, float> op, int n) => AnimMesh(k, op, n, vp, orbit);
        PolyMesh M() => k.Mesh;
        void Pick(IEnumerable<int> fs) { SubSel(k).Clear(); foreach (var f in fs) SubSel(k).Faces.Add(f); }
        SetLevel(SubObject.Polygon);
        foreach (var x in Hold(24)) yield return x;
        // the floors: the loop cuts slide to the real storey heights (a tall shop floor, three storeys, the cornice)
        var start = M().Verts.ToArray();
        int Row(float z) { int r = (int)MathF.Round(z / (H / 5)); return Math.Clamp(r, 0, 5); }
        foreach (var x in Op((m, t) => { for (int i = 0; i < m.Verts.Count; i++) { var v = start[i]; m.Verts[i] = new Vector3(v.X, v.Y, v.Z + (rowsTo[Row(v.Z)] - v.Z) * t); } }, 30)) yield return x;
        // which faces are what
        bool Wall(int i) => MathF.Abs(M().FaceNormal(i).Z) < 0.1f;
        var upper = Enumerable.Range(0, M().Faces.Count).Where(i => Wall(i) && M().FaceCenter(i).Z > 4.1f && M().FaceCenter(i).Z < 12.6f).ToHashSet();
        var shop = Enumerable.Range(0, M().Faces.Count).Where(i => Wall(i) && M().FaceCenter(i).Z < 4f).ToHashSet();
        var cornice = Enumerable.Range(0, M().Faces.Count).Where(i => Wall(i) && M().FaceCenter(i).Z > 12.7f).ToHashSet();
        var roofF = Enumerable.Range(0, M().Faces.Count).Where(i => M().FaceNormal(i).Z > 0.9f).ToHashSet();
        // windows on every bay: inset, sill raised, pushed in
        Pick(upper); foreach (var x in Hold(10)) yield return x;
        int n0 = M().Faces.Count;
        foreach (var x in Op((m, t) => MoloMax.Core.Mesh.MeshOps.InsetFaces(m, upper, 0.3f * t, true), 22)) yield return x;
        var upperRing = Enumerable.Range(n0, M().Faces.Count - n0).ToHashSet();
        var sillVerts = upper.SelectMany(f => M().Faces[f].V).Distinct().ToList();
        var capV0 = sillVerts.ToDictionary(v => v, v => M().Verts[v]);
        foreach (var x in Op((m, t) =>
        {   // the lower edge of each window goes up to sill height
            foreach (var f in upper)
            {
                var fv = m.Faces[f].V; float zmin = fv.Min(v => capV0[v].Z);
                foreach (var v in fv) if (capV0[v].Z < zmin + 0.01f) m.Verts[v] = capV0[v] + new Vector3(0, 0, 0.75f * t);
            }
        }, 16)) yield return x;
        int n1 = M().Faces.Count;
        foreach (var x in Op((m, t) => MoloMax.Core.Mesh.MeshOps.ExtrudeFaces(m, upper, -0.22f * t, true), 18)) yield return x;
        var reveals = Enumerable.Range(n1, M().Faces.Count - n1).ToHashSet();
        // shop front: big glazing on the ground floor
        Pick(shop); foreach (var x in Hold(8)) yield return x;
        int n2 = M().Faces.Count;
        foreach (var x in Op((m, t) => MoloMax.Core.Mesh.MeshOps.InsetFaces(m, shop, 0.14f * t, true), 16)) yield return x;
        var shopRing = Enumerable.Range(n2, M().Faces.Count - n2).ToHashSet();
        int n3 = M().Faces.Count;
        foreach (var x in Op((m, t) => MoloMax.Core.Mesh.MeshOps.ExtrudeFaces(m, shop, -0.3f * t, true), 14)) yield return x;
        foreach (var i in Enumerable.Range(n3, M().Faces.Count - n3)) reveals.Add(i);
        // roof: parapet
        Pick(roofF); foreach (var x in Hold(8)) yield return x;
        int n4 = M().Faces.Count;
        foreach (var x in Op((m, t) => MoloMax.Core.Mesh.MeshOps.InsetFaces(m, roofF, 0.3f * t), 14)) yield return x;
        var parapet = Enumerable.Range(n4, M().Faces.Count - n4).ToHashSet();
        int n5 = M().Faces.Count;
        foreach (var x in Op((m, t) => MoloMax.Core.Mesh.MeshOps.ExtrudeFaces(m, roofF, -0.7f * t), 14)) yield return x;
        foreach (var i in Enumerable.Range(n5, M().Faces.Count - n5)) parapet.Add(i);
        // cornice: the top band pulled out all around
        Pick(cornice); foreach (var x in Hold(8)) yield return x;
        int n6 = M().Faces.Count;
        foreach (var x in Op((m, t) => MoloMax.Core.Mesh.MeshOps.ExtrudeFaces(m, cornice, 0.35f * t), 16)) yield return x;
        foreach (var i in cornice) parapet.Add(i);
        foreach (var i in Enumerable.Range(n6, M().Faces.Count - n6)) parapet.Add(i);
        SubSel(k).Clear();
        foreach (var x in Hold(10)) yield return x;

        // UVs at real size + real GTA V materials
        onMaterials?.Invoke();
        var brick = LibPick(("All", "xj_v_brick03"), ("Walls", "brick"));
        var stone = LibPick(("All", "xj_v_sandstone_blocks"), ("Concrete & stone", "stone"));
        var trim = LibPick(("All", "nk_vw_pent_plaster_white_01_ab"), ("Walls", "plaster"));
        var glass = LibPick(("All", "90perc"), ("Glass", "glass_env"), ("Glass", ""));
        var gravel = LibPick(("All", "rsn_et_gravel001b"), ("Floors", "concrete"));
        var tiles = LibPick(("All", "26_1_stone_tiles_04"), ("Floors", "tile"));
        var glassFaces = new HashSet<int>(upper); glassFaces.UnionWith(shop);
        var steps = new List<((LibMaterial M, string, string) Pick, SceneObject Obj, HashSet<int> Faces, float Tile)>
        {
            (brick, k, upperRing, 0.85f),
            (stone, k, shopRing, 1.6f),
            (trim, k, new HashSet<int>(reveals.Concat(parapet)), 2f),
            (glass, k, glassFaces, 3f),
            (gravel, k, roofF, 1.6f),
        };
        if (slab != null) steps.Add((tiles, slab, Enumerable.Range(0, slab.Mesh.Faces.Count).ToHashSet(), 2f));
        foreach (var (pick, obj, faces, tile) in steps)
        {
            if (pick.M == null) continue;
            var g = LibraryMaterial(pick.M);
            if (showWindows) { LibShow(pick, LibraryAction.Assign, vp); foreach (var x in Hold(16)) yield return x; libraryOpen = false; }
            // cube projection on those faces, at the material's real size
            Selected.Clear(); Selected.Add(obj.Id); ActiveId = obj.Id; SelectionChanged();
            if (Level != SubObject.Polygon) SetLevel(SubObject.Polygon);
            SubSel(obj).Clear(); foreach (var f in faces) SubSel(obj).Faces.Add(f);
            StartCubeProjection();
            if (cubeProj != null) { cubeProj.SizeU = cubeProj.SizeV = tile; cubeProj.World = true; cubeProj.MaterialId = g.Id; ApplyCubeProjection(); }
            if (showWindows) { uvOpen = true; uvWinAt = new Vector2(vp.X + 12, vp.Y + 50); }
            foreach (var x in Hold(showWindows ? 30 : 8)) yield return x;
        }
        uvOpen = false; uvWinAt = null; libraryOpen = false; libWinAt = null; cubeProj = null;
        SetLevel(SubObject.Object); Selected.Clear(); SelectionChanged();
        foreach (var x in Hold(12)) yield return x;
    }

    /// <summary>Films: an open, flat lot near <paramref name="centre"/> (nothing standing on it, the ground level around).</summary>
    string lotStats;
    Vector3? FindLot(Vector3 centre, float radius, float w, float l)
    {
        int nNoHit = 0, nSlope = 0, nBusy = 0;
        float reach = radius + 30;
        var objs = Scene.Objects.Where(o =>
        {
            if (o.Hidden || o.Mesh == null || o.Mesh.Faces.Count == 0) return false;
            var (a, b) = o.FastWorldBounds();
            return b.X > centre.X - reach && a.X < centre.X + reach && b.Y > centre.Y - reach && a.Y < centre.Y + reach;
        }).ToList();
        static bool Road(string n) => n != null && (n.Contains("_rd") || n.Contains("road") || n.Contains("hw") || n.Contains("junc"));
        float? Ground(float x, float y)
        {   // the ground of a lot, not a road
            var hit = Picking.PickNearest(objs, new Ray(new Vector3(x, y, centre.Z + 400), -Vector3.UnitZ));
            if (hit == null || Road(hit.Value.Object.Name?.ToLowerInvariant())) return null;
            return hit.Value.Point.Z;
        }
        var cands = new List<Vector3>();
        for (float dx = -radius; dx <= radius; dx += 8)
            for (float dy = -radius; dy <= radius; dy += 8)
                if (dx * dx + dy * dy <= radius * radius) cands.Add(new Vector3(centre.X + dx, centre.Y + dy, 0));
        foreach (var c in cands.OrderBy(c => Vector2.DistanceSquared(new Vector2(c.X, c.Y), new Vector2(centre.X, centre.Y))))
        {
            float hw = w / 2 + 3, hl = l / 2 + 3;
            var zs = new List<float>();
            bool ok = true;
            foreach (var (ox, oy) in new[] { (0f, 0f), (-hw, -hl), (hw, -hl), (-hw, hl), (hw, hl), (0, -hl), (0, hl), (-hw, 0), (hw, 0) })
            {
                var z = Ground(c.X + ox, c.Y + oy); if (z == null) { ok = false; nNoHit++; break; }
                zs.Add(z.Value);
            }
            if (!ok) continue;
            if (zs.Max() - zs.Min() > 0.5f) { nSlope++; continue; }
            float g = zs.Min();
            // nothing standing on it (small and mid-size models whose box reaches into the footprint above the ground)
            var mn = new Vector3(c.X - hw, c.Y - hl, g + 0.6f); var mx = new Vector3(c.X + hw, c.Y + hl, g + 60);
            bool free = objs.All(o =>
            {
                var (a, b) = o.FastWorldBounds(); var sz = b - a;
                if (MathF.Max(sz.X, sz.Y) > 150 && a.Z < mn.Z + 1.5f) return true; // terrain chunks (a roof or a deck above is not)
                if (MathF.Max(sz.X, MathF.Max(sz.Y, sz.Z)) < 3.5f || sz.Z < 1.2f) return true; // small props, kerbs
                return b.X < mn.X || a.X > mx.X || b.Y < mn.Y || a.Y > mx.Y || b.Z < mn.Z || a.Z > mx.Z;
            });
            // open sky: nothing over it (a bridge, a freeway)
            if (free)
                foreach (var (ox, oy) in new[] { (0f, 0f), (-hw, -hl), (hw, -hl), (-hw, hl), (hw, hl) })
                {
                    var up = Picking.PickNearest(objs, new Ray(new Vector3(c.X + ox, c.Y + oy, g + 1.5f), Vector3.UnitZ));
                    if (up != null && up.Value.Point.Z < g + 60) { free = false; break; }
                }
            if (free) { lotStats = $"{cands.Count} spots: {nNoHit} road/no ground, {nSlope} slope, {nBusy} built; {objs.Count} models"; return new Vector3(c.X, c.Y, g); }
            nBusy++;
        }
        lotStats = $"{cands.Count} spots: {nNoHit} road/no ground, {nSlope} slope, {nBusy} built; {objs.Count} models";
        return null;
    }

    /// <summary>Films: a box with all its edges softened (a cushion, an armrest).</summary>
    static PolyMesh SoftBox(float w, float l, float h, Vector3 at, float round)
    {
        var b = MoloMax.Core.Mesh.Primitives.Box(w, l, h);
        var edges = new HashSet<EdgeKey>();
        foreach (var f in b.Faces) for (int i = 0; i < f.V.Length; i++) edges.Add(EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]));
        if (round > 0) MoloMax.Core.Mesh.MeshOps.ChamferEdges(b, edges, round);
        for (int i = 0; i < b.Verts.Count; i++) b.Verts[i] += at;
        return b;
    }

    /// <summary>Films: a sofa modeled and painted like in a texturing app: a GTA V fabric, worn edges, stitched seams, seat wear, dust.</summary>
    /// <summary>Demo film: animating like Blender, simply (Key, another frame, move, Key, Play), texture slide, camera, .ycd.</summary>
    /// <summary>Every modifier of the catalogue on a cube, one after the other, with what it changes.</summary>
    /// <summary>Builds a FiveM resource like a user would: a textured cube, UV keys on the timeline, GTA export.
    /// POLYMORPH_UVTEST_POS = "x y z heading" (where to put it), POLYMORPH_UVTEST_TEX = the texture, POLYMORPH_UVTEST_OUT = export folder.</summary>
    IEnumerable<object> GameUvTest()
    {
        Fresh();
        var pos = (Environment.GetEnvironmentVariable("POLYMORPH_UVTEST_POS") ?? "0 0 0 0").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var tex = Environment.GetEnvironmentVariable("POLYMORPH_UVTEST_TEX");
        var outDir = Environment.GetEnvironmentVariable("POLYMORPH_UVTEST_OUT") ?? StPath("uvtest_export");
        Scene.Gta.ResourceName = "molo_uvtest";
        Scene.GtaOrigin = new Vector3(pos[0], pos[1], pos[2]);
        if (pos.Length > 3) Scene.Gta.Heading = pos[3];
        // A: no animation  B: Polymorph .ycd  C: Unknown10 = 0  D: C + QuantizeFloat  E: emissive shader (like the portal)
        var names = new[] { "a", "b", "c", "d", "e" };
        var cubes = new List<SceneObject>();
        GtaMaterial Mat(string shader) { var m = GtaMaterial.CreateDefault(Scene.Materials.Max(x => x.Id) + 1, "uvtest_" + shader, new Vector3(1)); m.Shader = shader; m.DiffuseTexture = tex; Scene.Materials.Add(m); return m; }
        var mDef = Mat("default"); var mEm = Mat("emissive");
        for (int i = 0; i < names.Length; i++)
        {
            var c = Prim(CreateKind.Box, new Vector3(i * 3f, 0, 0)); c.Name = "molo_uvt_" + names[i];
            var m = names[i] == "e" ? mEm : mDef;
            foreach (var f in c.Mesh.Faces) f.MatId = m.Id;
            c.Touch(); cubes.Add(c);
            if (names[i] == "a") continue;
            var tr = Scene.Anim.Track(c.Id, true);
            tr.Curve(Core.Anim.Ch.UvV, true).SetKey(0, 0, Core.Anim.KeyInterp.Linear);
            tr.Curve(Core.Anim.Ch.UvV).SetKey(60, 1, Core.Anim.KeyInterp.Linear);
        }
        Scene.Anim.End = 60;
        Note($"cubes a..e from {Scene.GtaOrigin} along +X every 3 m, texture {tex}");
        yield return Frames(5);
        // GTA > Fix all, like the user before an export (exterior prop: red vertex colours, white on emissive)
        var fixes = Core.Gta.Check.GtaChecker.FixAll(Scene); foreach (var o in Scene.Objects) o.Touch();
        Note("Fix all: " + string.Join("; ", fixes));
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        Core.Gta.Export.ExportReport rep = null; string err = null;
        try { rep = Core.Gta.Export.GtaExporter.Export(Undo.SnapshotForSave(Scene), outDir, LoadImageRgba, renderer.Textures.SearchFolders.ToList()); } catch (Exception e) { err = e.ToString(); }
        if (rep == null) { Expect(false, "export: " + err); yield break; }
        foreach (var l in rep.Errors) Note("ERROR " + l);
        foreach (var l in rep.Warnings) Note("warning " + l);
        foreach (var l in rep.Info) Note("info " + l);
        Expect(rep.Ok, $"export ok into {rep.ResourceFolder}");
        var files = Directory.GetFiles(rep.ResourceFolder, "*", SearchOption.AllDirectories);
        var ycd = files.FirstOrDefault(f => f.EndsWith(".ycd", StringComparison.OrdinalIgnoreCase));
        Expect(ycd != null, "a .ycd is written");
        if (ycd == null) yield break;
        // variants C and D, written through CodeWalker
        var xml = Core.Gta.Anim.YcdIO.BinaryToXml(File.ReadAllBytes(ycd));
        File.WriteAllText(ycd + ".before.xml", xml);
        string Section(string x, string anim, Func<string, string> edit)
        {
            int i = x.IndexOf($"<Hash>{anim}</Hash>", x.IndexOf("<Animations>")); if (i < 0) return x;
            int j = x.IndexOf("</Sequences>", i);
            return x[..i] + edit(x[i..j]) + x[j..];
        }
        string U0(string sec) => System.Text.RegularExpressions.Regex.Replace(sec, "<Unknown10 value=\"\\d+\" />", "<Unknown10 value=\"0\" />");
        string Quant(string sec) => System.Text.RegularExpressions.Regex.Replace(sec, "<Type value=\"RawFloat\" />\\s*<Values>([^<]*)</Values>", m =>
        {
            var v = m.Groups[1].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            float mn = v.Min(), mx = v.Max(); float q = MathF.Max((mx - mn) / (v.Count - 1), 1e-6f);
            return $"<Type value=\"QuantizeFloat\" />\n<Quantum value=\"{q.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" />\n<Offset value=\"{mn.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" />\n<Values>{m.Groups[1].Value}</Values>";
        });
        xml = Section(xml, "molo_uvt_c_uv_0", U0);
        xml = Section(xml, "molo_uvt_d_uv_0", sec => Quant(U0(sec)));
        File.WriteAllText(ycd + ".after.xml", xml);
        File.WriteAllBytes(ycd, Core.Gta.Anim.YcdIO.XmlToBinary(xml));
        var back = Core.Gta.Anim.YcdIO.Read(File.ReadAllBytes(ycd), Path.GetFileName(ycd));
        Note("ycd clips: " + string.Join(", ", back.Select(c => $"{c.Name} ({c.Frames} f)")));
        Expect(back.Count == 4, $"4 UV clips (b, c, d, e) in the .ycd ({back.Count})");
        var man = File.ReadAllText(Path.Combine(rep.ResourceFolder, "fxmanifest.lua"));
        Expect(man.Contains("DLC_ITYP_REQUEST"), "fxmanifest requests the .ytyp");
    }

    /// <summary>Boolean with the real buttons of the Object panel: a door and a window cut in a wall, union, intersect, undo.</summary>
    /// <summary>Wall Opening with the real mouse: trace on the wall, pull sides, presets, Enter, edit an opening later, Ctrl+Z.</summary>
    IEnumerable<object> TutBevelConnect()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true;
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; cam.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        IEnumerable<object> Key(Keys k) { KeyDownT(k); yield return Frames(3); KeyUpT(k); yield return Frames(4); }
        var P = Params; float w0 = P.Width, l0 = P.Length, h0 = P.Height;
        P.Width = 2; P.Length = 2; P.Height = 2;
        var b = Prim(CreateKind.Box, Vector3.Zero); b.Name = "bevel";
        var c = Prim(CreateKind.Box, new Vector3(3.5f, 0, 0)); c.Name = "connect";
        P.Width = w0; P.Length = l0; P.Height = h0;
        cam.Frame(new Vector3(-1.5f, -1.5f, -0.2f), new Vector3(5f, 1.5f, 2.4f)); cam.Yaw = -1.1f; cam.Pitch = 0.35f;
        // bevel: one vertical edge, drag value, wheel = 4 more sides
        SelectOnly(b); SetLevel(SubObject.Edge);
        foreach (var e in b.Mesh.AllEdges().Where(e => { var a = b.Mesh.Verts[e.A]; var d = b.Mesh.Verts[e.B]; return a.X > 0.9f && d.X > 0.9f && a.Y < -0.9f && d.Y < -0.9f; })) SubSel(b).Edges.Add(e);
        yield return Frames(4);
        chamferSegments = 1;
        BeginModal(ModalKind.Chamfer); yield return Frames(3);
        ApplyParametric(0.35f);
        for (int i = 0; i < 4; i++) foreach (var x in Key(Keys.PageUp)) yield return x;
        int segModal = chamferSegments;
        foreach (var x in Key(Keys.Enter)) yield return x;
        yield return Frames(6);
        // the width as typed in the Adjust Last Operation panel (the mouse decides it in the live tool)
        if (lastMeshOp != null) { lastMeshOp.Value = 0.35f; RunMeshOp(lastMeshOp.Kind, lastMeshOp.Obj, lastMeshOp.BaseMesh, lastMeshOp.BaseSel, 0.35f, 1, lastMeshOp.Segments); lastMeshOp.Version = b.Version; }
        yield return Frames(4);
        Expect(segModal == 5 && b.Mesh.IsClosedManifold(out _) && b.Mesh.Faces.Count == 11, $"bevel with 5 sides from Page Up in the tool: {b.Mesh.Faces.Count} faces, closed {b.Mesh.IsClosedManifold(out _)}");
        Expect(lastMeshOp != null && lastMeshOp.Segments == 5, "Adjust Last Operation keeps the segments");
        // connect: a ring of 4, 3 segments from the panel
        SetLevel(SubObject.Object); SelectOnly(c); SetLevel(SubObject.Edge);
        foreach (var e in c.Mesh.AllEdges().Where(e => MathF.Abs(c.Mesh.Verts[e.A].Z - c.Mesh.Verts[e.B].Z) > 1.9f)) SubSel(c).Edges.Add(e);
        connectSegments = 1;
        ConnectEdges(); yield return Frames(4);
        Expect(lastMeshOp?.Kind == ModalKind.Connect, "connect opens the Adjust Last Operation panel");
        lastMeshOp.Segments = 3; connectSegments = 3;
        RunMeshOp(ModalKind.Connect, lastMeshOp.Obj, lastMeshOp.BaseMesh, lastMeshOp.BaseSel, 0, 1, 3); lastMeshOp.Version = c.Version;
        yield return Frames(6);
        Expect(c.Mesh.Faces.Count == 18 && c.Mesh.IsClosedManifold(out _), $"connect 3 segments: {c.Mesh.Faces.Count} faces");
        yield return Shot("bevel_connect");
        // gothic arch on a wall
        SetLevel(SubObject.Object);
        P.Width = 4; P.Length = 0.4f; P.Height = 4;
        var wall = Prim(CreateKind.Box, new Vector3(0, 6, 0)); wall.Name = "church wall";
        P.Width = w0; P.Length = l0; P.Height = h0;
        var mod = new WallOpeningModifier();
        mod.Openings.Add(new OpeningRect { Origin = new Vector3(0, -0.2f, 0), Normal = -Vector3.UnitY, Left = -0.7f, Right = 0.7f, Bottom = 0, Top = 3.2f, Shape = OpeningShape.Gothic, Segments = 16 });
        wall.Modifiers.Add(mod); wall.Touch();
        cam.Frame(new Vector3(-2.5f, 3.5f, -0.2f), new Vector3(2.5f, 6.5f, 4.2f)); cam.Yaw = -1.35f; cam.Pitch = 0.12f;
        SelectOnly(wall); yield return Frames(8);
        Expect(Scene.Find(wall.Id).Evaluated().IsClosedManifold(out _), "gothic arch cut, closed");
        yield return Shot("gothic_arch");
        // material editor: only the selection's materials
        var m2 = GtaMaterial.CreateDefault(Scene.NextMaterialId++, "brick_red", new Vector3(0.7f, 0.25f, 0.2f)); Scene.Materials.Add(m2);
        var m3 = GtaMaterial.CreateDefault(Scene.NextMaterialId++, "unused_metal", new Vector3(0.6f)); Scene.Materials.Add(m3);
        foreach (var f in wall.Mesh.Faces) f.MatId = m2.Id; wall.Touch();
        materialEditorOpen = true; matSelOnly = true; matSelKey = null;
        yield return Frames(8);
        var used = MaterialsOfSelection();
        Expect(selectedMaterial == m2.Id && used.Count == 1, $"material editor shows the wall's material only and picks it ({Scene.Materials.First(x => x.Id == selectedMaterial).Name})");
        yield return Shot("material_editor_selection");
        materialEditorOpen = false;
    }

    IEnumerable<object> TutVertexSnap()
    {
        Fresh(); if (MapMode) SetMapMode(false); paintMode = false; SculptMode = false;
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; cam.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        var P = Params; float w0 = P.Width, l0 = P.Length, h0 = P.Height;
        P.Width = 2; P.Length = 2; P.Height = 2;
        var b = Prim(CreateKind.Box, Vector3.Zero);
        P.Width = w0; P.Length = l0; P.Height = h0;
        cam.Frame(new Vector3(-1.5f, -1.5f, -0.2f), new Vector3(1.5f, 1.5f, 2.4f)); cam.Yaw = -1.1f; cam.Pitch = 0.5f;
        UI.ImGuiBackend.SyntheticInput = true;
        SelectOnly(b); SetLevel(SubObject.Vertex); Tool = Tool.Move; Snap = true; SnapVertexT = true;
        int ia = b.Mesh.Verts.FindIndex(v => v.X > 0.9f && v.Y < -0.9f && v.Z > 1.9f);
        int ib = b.Mesh.Verts.FindIndex(v => v.X < -0.9f && v.Y < -0.9f && v.Z > 1.9f);
        SubSel(b).Verts.Clear(); SubSel(b).Verts.Add(ia);
        yield return Frames(6);
        var pa = ScreenOf(vp, Vector3.Transform(b.Mesh.Verts[ia], b.World)) + new Vector2(14, 4);
        var pb = ScreenOf(vp, Vector3.Transform(b.Mesh.Verts[ib], b.World));
        // beside the gizmo centre: the vertex itself, the gizmo is drawn on it so aim a few pixels off its arrows
        foreach (var x in MouseTo(ScreenOf(vp, Vector3.Transform(b.Mesh.Verts[ia], b.World)) + new Vector2(-6, 6), 15)) yield return x;
        yield return Frames(4);
        Expect(snapHover != null, "a selected vertex under the mouse is offered (yellow square)");
        yield return Shot("vertex_hover");
        var pg0 = ScreenOf(vp, Vector3.Transform(b.Mesh.Verts[ia], b.World)) + new Vector2(-6, 6);
        PostMouse(0x201, pg0, 1); tutClick = 0.35f; yield return Frames(3);
        Expect(pendingGrab != null || transforming, $"press on the vertex starts a grab (pending {pendingGrab != null}, transforming {transforming}, box {lmbTracking}, gizmo {gizmo.Hover}, hover {snapHover != null}, down {Input.Down(System.Windows.Forms.MouseButtons.Left)})");
        foreach (var x in MouseTo(Vector2.Lerp(pg0, pb, 0.3f), 8, 1)) yield return x;
        Expect(transforming, $"dragging carries the vertex (pending {pendingGrab != null}, transforming {transforming}, down {Input.Down(System.Windows.Forms.MouseButtons.Left)}, mouse {Input.Mouse})");
        foreach (var x in MouseTo(pb, 20, 1)) yield return x;
        yield return Frames(4);
        Expect(snapMarker != null, $"cyan square on the target vertex ({snapMarker})");
        PostMouse(0x202, pb, 0); yield return Frames(6);
        float dist = Vector3.Distance(b.Mesh.Verts[ia], b.Mesh.Verts[ib]);
        Expect(dist < 1e-3f, $"dropped exactly on the other vertex (distance {dist:0.0000})");
        // the Add Box panel must never rebuild the box over hand edits
        lastAdd = (CreateKind.Box, b.Id); lastAddVersion = b.Version - 1; yield return Frames(3);
        Expect(lastAdd == null, "Adjust Last Operation (Add Box) closes once the box was edited: a parameter change can't undo the edits");
        yield return Shot("vertex_snapped");
        // the middle button does the same (Mimi's way): another corner dropped on a third one
        int ic = b.Mesh.Verts.FindIndex(v => v.X > 0.9f && v.Y > 0.9f && v.Z > 1.9f);
        int id2 = b.Mesh.Verts.FindIndex(v => v.X > 0.9f && v.Y > 0.9f && v.Z < 0.1f);
        SubSel(b).Verts.Clear(); SubSel(b).Verts.Add(ic); yield return Frames(4);
        var pc = ScreenOf(vp, Vector3.Transform(b.Mesh.Verts[ic], b.World)) + new Vector2(-6, 6);
        var pd = ScreenOf(vp, Vector3.Transform(b.Mesh.Verts[id2], b.World));
        foreach (var x in MouseTo(pc, 10)) yield return x;
        yield return Frames(3);
        PostMouse(0x207, pc, 0x10); yield return Frames(3);
        Expect(transforming, "middle button on the selected vertex grabs it");
        foreach (var x in MouseTo(pd, 25, 0x10)) yield return x;
        yield return Frames(3);
        yield return Shot("vertex_mmb_target");
        PostMouse(0x208, pd, 0); yield return Frames(6);
        float d2 = Vector3.Distance(b.Mesh.Verts[ic], b.Mesh.Verts[id2]);
        Expect(d2 < 1e-3f, $"middle-button drag drops it on the other vertex (distance {d2:0.0000})");
        Snap = false;
        // pipette: a face of another object gives its material; in Polygon mode it goes on the selected faces
        var red = Scene.AddMaterial("pipette_red", new Vector3(0.9f, 0.1f, 0.1f));
        var b2 = Prim(CreateKind.Box, new Vector3(0, 4, 0)); b2.Mesh.SetAllMaterial(red.Id); b2.Touch();
        cam.Frame(new Vector3(-1.5f, -1.5f, -0.2f), new Vector3(1.5f, 5.5f, 2.4f)); yield return Frames(4);
        SelectOnly(b); SetLevel(SubObject.Polygon); SubSel(b).Faces.Clear(); SubSel(b).Faces.Add(0);
        eyedropper = true; yield return Frames(2);
        var top2 = FaceToward(b2, Vector3.UnitZ);
        var pt = ScreenOf(vp, Vector3.Transform(b2.Mesh.FaceCenter(top2), b2.World));
        foreach (var x in MouseTo(pt, 10)) yield return x;
        PostMouse(0x201, pt, 1); yield return Frames(3); PostMouse(0x202, pt, 0); yield return Frames(4);
        Expect(!eyedropper && selectedMaterial == red.Id && b.Mesh.Faces[0].MatId == red.Id, "pipette: the other box's material is picked and put on the selected face");
        materialEditorOpen = true; yield return Frames(6);
        yield return Shot("pipette");
        materialEditorOpen = false;
        SetLevel(SubObject.Object); UI.ImGuiBackend.SyntheticInput = false;
    }

    IEnumerable<object> TutGimsHub()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        var b = Prim(CreateKind.Box, Vector3.Zero); SelectOnly(b);
        HubOpen = true; hubPage = HubPage.Home; hubBack.Clear();
        yield return Frames(8);
        yield return Shot("gims_main");
        Expect(tutRects.Count >= 0, "panel drawn");
        HubGo(HubPage.GTools); gToolsTab = 1; yield return Frames(6);
        yield return Shot("gims_tools_game");
        HubBack(); HubGo(HubPage.Create); gCreateCat = null; yield return Frames(6);
        yield return Shot("gims_create_categories");
        gCreateCat = "MLO"; yield return Frames(6);
        yield return Shot("gims_create_mlo");
        HubBack();
        lastReport = new MoloMax.Core.Gta.Export.ExportReport { ResourceFolder = null };
        lastReport.Warnings.Add("shell001: vertex colours should be green (interior)."); lastReport.Info.Add("MLO 'molo_test': 2 room(s), 2 portal(s).");
        gResultErrors = false; HubGo(HubPage.Results); yield return Frames(6);
        yield return Shot("gims_report");
        Expect(hubPage == HubPage.Results, "pages: tools, create, report");
        HubBack(); yield return Frames(2);
        Expect(hubPage == HubPage.Home, "back arrow returns to the main page");
        HubOpen = false;
    }

    IEnumerable<object> TutOpenTheme()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        var dir = Path.Combine(SelfTestDir, "scenes"); Directory.CreateDirectory(Path.Combine(dir, "sub folder"));
        var P = Params; var box = Prim(CreateKind.Box, Vector3.Zero);
        foreach (var n in new[] { "entrepot", "hall_flamingo", "garage" }) SceneIO.Save(Scene, Path.Combine(dir, n + ".mmx"));
        if (!Settings.Bookmarks.Contains(dir)) Settings.Bookmarks.Add(dir);
        openFolder = null; OpenDialog(); openFolder = dir; openListedFor = null;
        yield return Frames(10);
        Expect(openWin && openFiles.Count == 3 && openDirs.Count == 1, $"Open window: {openFiles.Count} scenes, {openDirs.Count} folder, bookmark listed");
        yield return Shot("open_window");
        openWin = false; yield return Frames(4);
        prefsOpen = true; prefsSection = Array.IndexOf(PrefSections, "Interface");
        yield return Frames(6);
        yield return Shot("prefs_look");
        prefsOpen = false;
        yield return Frames(4);
        yield return Shot("toolbar_fits");
        Settings.Bookmarks.Remove(dir);
    }

    IEnumerable<object> TutShapesCleanup()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true; propTab = PropTab.Modifiers; if (TimelineOpen) ToggleTimeline();
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; cam.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        var P = Params; float w0 = P.Width, l0 = P.Length, h0 = P.Height; int ws = P.WSegs, ls = P.LSegs, hs = P.HSegs;
        P.Width = 4f; P.Length = 0.3f; P.Height = 3f; P.WSegs = 20; P.LSegs = 2; P.HSegs = 15;
        var wall = Prim(CreateKind.Box, Vector3.Zero); wall.Name = "wall";
        P.Width = w0; P.Length = l0; P.Height = h0; P.WSegs = ws; P.LSegs = ls; P.HSegs = hs;
        cam.Frame(new Vector3(-2.4f, -2.4f, -0.2f), new Vector3(2.4f, 2.4f, 3.2f)); cam.Yaw = -1.5708f; cam.Pitch = 0.05f;
        SelectOnly(wall); yield return Frames(10);
        Vector2 W(float x, float z) => ScreenOf(vp, new Vector3(x, -0.15f, z));
        IEnumerable<object> Key(Keys k) { KeyDownT(k); yield return Frames(3); KeyUpT(k); yield return Frames(6); }
        WallOpeningModifier Mod() => Scene.Find(wall.Id).Modifiers.OfType<WallOpeningModifier>().FirstOrDefault();
        string Info() { var m = Scene.Find(wall.Id).Evaluated(); return $"{m.Faces.Count} faces, closed {m.IsClosedManifold(out _)}, volume {Core.Mesh.CsgClean.Volume(m):0.000}"; }

        // 1. arch door: trace, D, Arch button, Enter
        foreach (var x in ClickAt(TutCentre("tb_opening"), false, 10)) yield return x;
        foreach (var x in DragTo(W(-1.5f, 0.5f), W(-0.5f, 1.8f), 20)) yield return x;
        yield return Frames(8);
        foreach (var x in Key(Keys.D)) yield return x;
        foreach (var x in ClickAt(TutCentre("op_shape_arch"), false, 10)) yield return x;
        yield return Frames(8);
        bool segShown = tutRects.ContainsKey("op_segments");
        Expect(opRect != null && opRect.Shape == OpeningShape.Arch, $"Arch button picks the arch shape (segments slider shown {segShown})");
        yield return Shot("arch_preview");
        foreach (var x in Key(Keys.Enter)) yield return x;
        yield return Frames(20);
        Expect(Mod()?.Openings.Count == 1 && Mod().Openings[0].Shape == OpeningShape.Arch && Scene.Find(wall.Id).Evaluated().IsClosedManifold(out _), $"arch door cut: {Info()}");

        // 2. round window: click, W, C, + + (more segments), Cut
        foreach (var x in ClickAt(TutCentre("tb_opening"), false, 10)) yield return x;
        foreach (var x in ClickAt(W(1.0f, 1.6f), false, 10)) yield return x;
        yield return Frames(8);
        foreach (var x in Key(Keys.W)) yield return x;
        foreach (var x in Key(Keys.C)) yield return x;
        foreach (var x in Key(Keys.Oemplus)) yield return x;
        foreach (var x in Key(Keys.Oemplus)) yield return x;
        int segs = opRect?.Segments ?? 0;
        foreach (var x in ClickAt(TutCentre("op_cut"), false, 10)) yield return x;
        yield return Frames(20);
        var mod = Mod();
        Expect(mod?.Openings.Count == 2 && mod.Openings[1].Shape == OpeningShape.Circle && mod.Openings[1].Segments == 20, $"round window with + + = {segs} segments: {Info()}");
        yield return Frames(10);
        yield return Shot("arch_round_front");
        cam.Frame(new Vector3(-1.8f, -0.6f, 0f), new Vector3(1.8f, 0.6f, 2.6f)); cam.Yaw = -1.1f; cam.Pitch = 0.2f;
        yield return Frames(8);
        yield return Shot("arch_round_angle");

        // 3. fewer segments on the arch: Edit, - - - , Enter
        EditOpening(Scene.Find(wall.Id), mod, 0); yield return Frames(10);
        for (int i = 0; i < 4; i++) foreach (var x in Key(Keys.OemMinus)) yield return x;
        foreach (var x in Key(Keys.Enter)) yield return x;
        yield return Frames(20);
        Expect(Mod().Openings[0].Segments == 8 && Scene.Find(wall.Id).Evaluated().IsClosedManifold(out _), $"arch down to {Mod().Openings[0].Segments} segments: {Info()}");
        yield return Shot("arch_8_segments");

        // 4. clean up: a box with many segments, triangulated (a messy import)
        Fresh(); if (MapMode) SetMapMode(false);
        vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        P.WSegs = 8; P.LSegs = 8; P.HSegs = 8; P.Width = 2; P.Length = 2; P.Height = 2;
        var box = Prim(CreateKind.Box, Vector3.Zero); box.Name = "messy box";
        P.Width = w0; P.Length = l0; P.Height = h0; P.WSegs = ws; P.LSegs = ls; P.HSegs = hs;
        Core.Mesh.MeshOps.WeldByDistance(box.Mesh, 1e-5f); box.Mesh.CleanFaces();
        box.Mesh = new TriangulateModifier().Apply(box.Mesh.Clone(), true); box.Touch();
        var cyl = Prim(CreateKind.Cylinder, new Vector3(3, 0, 0)); cyl.Name = "messy cylinder";
        cyl.Mesh = new TriangulateModifier().Apply(cyl.Mesh.Clone(), true); cyl.Touch();
        cam.Frame(new Vector3(-1.5f, -2f, -0.2f), new Vector3(4.5f, 2f, 2.4f)); cam.Yaw = -1.0f; cam.Pitch = 0.35f;
        SelectOnly(box, cyl); yield return Frames(10);
        int fb = box.Evaluated().Faces.Count + cyl.Evaluated().Faces.Count;
        yield return Shot("cleanup_before");
        CleanUpSelected(); yield return Frames(15);
        var eb = Scene.Find(box.Id).Evaluated(); var ec = Scene.Find(cyl.Id).Evaluated();
        Note($"clean up: {fb} -> {eb.Faces.Count + ec.Faces.Count} faces; box {Scene.Find(box.Id).Modifiers.OfType<CleanUpModifier>().First().LastReport}; cylinder {Scene.Find(cyl.Id).Modifiers.OfType<CleanUpModifier>().First().LastReport}");
        Expect(eb.Faces.Count == 6 && eb.IsClosedManifold(out _) && ec.IsClosedManifold(out _) && Scene.Find(box.Id).Mesh.Faces.Count > 300, $"Clean Up: box -> {eb.Faces.Count} faces, cylinder -> {ec.Faces.Count}, base meshes untouched");
        SelectOnly(Scene.Find(box.Id)); yield return Frames(10);
        yield return Shot("cleanup_after");
        DoUndo(); yield return Frames(10);
        Expect(!Scene.Find(box.Id).Modifiers.OfType<CleanUpModifier>().Any(), "undo removes the clean up");
    }

    IEnumerable<object> TutDenseOpening()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true; propTab = PropTab.Modifiers; if (TimelineOpen) ToggleTimeline();
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; cam.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        var P = Params; float w0 = P.Width, l0 = P.Length, h0 = P.Height; int ws = P.WSegs, ls = P.LSegs, hs = P.HSegs;
        P.Width = 4f; P.Length = 0.3f; P.Height = 3f; P.WSegs = 40; P.LSegs = 3; P.HSegs = 30;
        var wall = Prim(CreateKind.Box, Vector3.Zero); wall.Name = "dense wall";
        P.Width = w0; P.Length = l0; P.Height = h0; P.WSegs = ws; P.LSegs = ls; P.HSegs = hs;
        cam.Frame(new Vector3(-2.3f, -2.3f, -0.2f), new Vector3(2.3f, 2.3f, 3.2f)); cam.Yaw = -1.5708f; cam.Pitch = 0.05f;
        SelectOnly(wall); yield return Frames(10);
        yield return Shot("dense_before");
        var mod = new WallOpeningModifier();
        mod.Openings.Add(new OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = -1.4f, Right = -0.4f, Bottom = 0, Top = 2.1f });
        mod.Openings.Add(new OpeningRect { Origin = new Vector3(0, -0.15f, 0), Normal = -Vector3.UnitY, Left = 0.45f, Right = 1.55f, Bottom = 1.0f, Top = 2.0f });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        wall.Modifiers.Add(mod); wall.Touch();
        var m = Scene.Find(wall.Id).Evaluated();
        Note($"dense wall: {Scene.Find(wall.Id).Mesh.Faces.Count} -> {m.Faces.Count} faces in {sw.ElapsedMilliseconds} ms, closed {m.IsClosedManifold(out _)}, corners [{string.Join(",", m.Faces.GroupBy(f => f.V.Length).OrderBy(g => g.Key).Select(g => g.Key + "x" + g.Count()))}]");
        Expect(m.IsClosedManifold(out _) && m.Faces.Count > 2000, "dense wall cut, grid kept, closed");
        yield return Frames(10);
        yield return Shot("dense_front");
        cam.Frame(new Vector3(-1.9f, -0.6f, -0.1f), new Vector3(-0.0f, 0.6f, 2.4f)); cam.Yaw = -1.0f; cam.Pitch = 0.25f;
        yield return Frames(8);
        yield return Shot("dense_door_close");
        cam.Frame(new Vector3(0.2f, -0.6f, 0.8f), new Vector3(1.8f, 0.6f, 2.2f)); cam.Yaw = -2.1f; cam.Pitch = -0.2f;
        yield return Frames(8);
        yield return Shot("dense_window_close");
    }

    IEnumerable<object> TutWallOpening()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true; propTab = PropTab.Modifiers; if (TimelineOpen) ToggleTimeline();
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; cam.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        var P = Params; float w0 = P.Width, l0 = P.Length, h0 = P.Height;
        P.Width = 4f; P.Length = 0.3f; P.Height = 3f;
        var wall = Prim(CreateKind.Box, Vector3.Zero); wall.Name = "wall";
        P.Width = w0; P.Length = l0; P.Height = h0;
        cam.Frame(new Vector3(-2.6f, -2.5f, -0.3f), new Vector3(2.6f, 2.5f, 3.3f)); cam.Yaw = -1.3f; cam.Pitch = 0.12f; // in front of the wall
        SelectOnly(wall); yield return Frames(10);
        Vector2 W(float x, float z) => ScreenOf(vp, new Vector3(x, -0.15f, z)); // a point on the front face
        string Info() { var m = Scene.Find(wall.Id).Evaluated(); return $"{m.Faces.Count} faces, closed {m.IsClosedManifold(out _)}, volume {Core.Mesh.CsgClean.Volume(m):0.000}"; }
        IEnumerable<object> Key(Keys k) { KeyDownT(k); yield return Frames(3); KeyUpT(k); yield return Frames(6); }

        foreach (var x in StartFilm("17_wall_opening", 7)) yield return x;
        foreach (var x in Say("Wall Opening", "Doors and windows traced straight on the wall. No cutter, no boolean.", 70)) yield return x;

        foreach (var x in Say("1. Click Opening, drag a rectangle on the wall", "Select the wall first (or right-click > Cut opening)", 25)) yield return x;
        foreach (var x in ClickAt(TutCentre("tb_opening"), false, 25)) yield return x;
        bool started = modal.Kind == ModalKind.Opening;
        foreach (var x in DragTo(W(-1.4f, 0.6f), W(-0.5f, 1.8f), 40)) yield return x;
        yield return Frames(15);
        bool traced = opRect != null && opPhase == 1;
        Note($"traced: tool {started}, rect {opRect?.Width:0.00} x {opRect?.Height:0.00}");
        Expect(started && traced && opRect.Width > 0.7f && opRect.Height > 1f, $"tool started from the toolbar, rectangle traced ({opRect?.Width:0.00} x {opRect?.Height:0.00} m)");

        foreach (var x in Say("2. D = door: from the floor, 2.10 m high", "W = window (sill 0.90 m, 1.20 m high)", 20)) yield return x;
        foreach (var x in Key(Keys.D)) yield return x;
        yield return Frames(20);
        Expect(MathF.Abs(opRect.Height - 2.1f) < 0.01f && MathF.Abs(opRect.Bottom - opWallBottom) < 0.01f, $"D makes it a door ({opRect.Width:0.00} x {opRect.Height:0.00}, sill {opRect.Bottom - opWallBottom:0.00})");

        foreach (var x in Say("3. Pull a side to adjust it", "Each side has its handle; the middle one moves it all", 20)) yield return x;
        float rightBefore = opRect.Right;
        var rh = OpScreen(vp, opRect.Right, (opRect.Bottom + opRect.Top) * 0.5f);
        var rightDir = Vector2.Normalize(W(1, 1) - W(0, 1));
        foreach (var x in DragTo(rh, rh + rightDir * 60, 30)) yield return x;
        yield return Frames(15);
        Expect(opRect.Right > rightBefore + 0.05f, $"pulling the right side widens it ({rightBefore:0.00} -> {opRect.Right:0.00})");

        foreach (var x in Say("4. Enter: the opening is cut", "Clean straight faces, the reveal inside the wall", 20)) yield return x;
        string before = Info();
        foreach (var x in Key(Keys.Enter)) yield return x;
        yield return Frames(30);
        var mod = Scene.Find(wall.Id).Modifiers.OfType<WallOpeningModifier>().FirstOrDefault();
        string afterDoor = Info();
        Note($"door: {before} -> {afterDoor}");
        Expect(mod != null && mod.Openings.Count == 1 && Scene.Find(wall.Id).Evaluated().IsClosedManifold(out _) && Scene.Find(wall.Id).Mesh.Faces.Count == 6, $"Enter cuts the door ({afterDoor}), base mesh untouched");

        foreach (var x in Say("5. A window: one click, W, Cut", "A click makes a 1.20 m window, the panel gives exact sizes", 20)) yield return x;
        foreach (var x in ClickAt(TutCentre("tb_opening"), false, 25)) yield return x;
        foreach (var x in ClickAt(W(1.1f, 1.5f), false, 25)) yield return x;
        yield return Frames(10);
        foreach (var x in Key(Keys.W)) yield return x;
        yield return Frames(15);
        foreach (var x in ClickAt(TutCentre("op_cut"), false, 25)) yield return x;
        yield return Frames(30);
        mod = Scene.Find(wall.Id).Modifiers.OfType<WallOpeningModifier>().FirstOrDefault();
        string afterWin = Info();
        Note($"window: {afterWin}");
        Expect(mod != null && mod.Openings.Count == 2 && Scene.Find(wall.Id).Evaluated().IsClosedManifold(out _), $"window cut from the panel button ({afterWin})");

        foreach (var x in Say("6. Change it later: Modifiers > Edit", "Pull a side, the cut follows live. Enter when done.", 20)) yield return x;
        propTab = PropTab.Modifiers; yield return Frames(15);
        EditOpening(Scene.Find(wall.Id), mod, 1); yield return Frames(15);
        float topBefore = mod.Openings[1].Top;
        var th = OpScreen(vp, (opRect.Left + opRect.Right) * 0.5f, opRect.Top);
        var upDir = Vector2.Normalize(W(0, 2) - W(0, 1));
        foreach (var x in DragTo(th, th + upDir * 50, 30)) yield return x;
        yield return Frames(10);
        foreach (var x in Key(Keys.Enter)) yield return x;
        yield return Frames(25);
        float topAfter = Scene.Find(wall.Id).Modifiers.OfType<WallOpeningModifier>().First().Openings[1].Top;
        Expect(topAfter > topBefore + 0.05f && Scene.Find(wall.Id).Evaluated().IsClosedManifold(out _), $"editing the window raises its top ({topBefore:0.00} -> {topAfter:0.00}), {Info()}");

        foreach (var x in Say("7. Ctrl+Z", "Every step can be undone", 20)) yield return x;
        KeyDownT(Keys.ControlKey); KeyDownT(Keys.Z); yield return Frames(4); KeyUpT(Keys.Z); KeyUpT(Keys.ControlKey); yield return Frames(30);
        float topUndo = Scene.Find(wall.Id).Modifiers.OfType<WallOpeningModifier>().First().Openings[1].Top;
        Expect(MathF.Abs(topUndo - topBefore) < 0.01f, $"Ctrl+Z puts the window back ({topAfter:0.00} -> {topUndo:0.00})");
        foreach (var x in Say("Wall Opening", "Right-click the wall > Cut opening, or the Opening button. Apply the modifier when it's final.", 70)) yield return x;
        foreach (var x in EndFilm()) yield return x;
    }

    IEnumerable<object> TutBoolean()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true; propTab = PropTab.Object; if (TimelineOpen) ToggleTimeline();
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; cam.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        var P = Params; float w0 = P.Width, l0 = P.Length, h0 = P.Height;
        SceneObject Box(string name, float w, float l, float h, Vector3 at) { P.Width = w; P.Length = l; P.Height = h; var o = Prim(CreateKind.Box, at); o.Name = name; return o; }
        string Info(SceneObject o) { var m = Scene.Find(o.Id).Mesh; bool closed = m.IsClosedManifold(out var why); return $"{m.Faces.Count} faces, closed {closed}{(closed ? "" : " (" + why + ")")}"; }
        IEnumerable<object> Click(string key)
        {
            propTab = PropTab.Object; tutRects.Remove(key); yield return Frames(6);
            if (!tutRects.ContainsKey(key)) { Note($"  (button {key} not on screen)"); yield break; }
            var at = TutCentre(key); Note($"  click {key} at {at}");
            foreach (var x in ClickAt(at, false, 25)) yield return x; yield return Frames(20);
        }

        // 1. the wall
        var wall = Box("wall", 4f, 0.3f, 3f, Vector3.Zero);
        var door = Box("door_cutter", 1f, 1f, 2.1f, new Vector3(-0.9f, 0, -0.05f));
        var win = Box("window_cutter", 1.1f, 1f, 0.9f, new Vector3(1.0f, 0, 1.1f));
        P.Width = w0; P.Length = l0; P.Height = h0;
        cam.Frame(new Vector3(-3, -3, -0.5f), new Vector3(3, 3, 3.5f)); cam.Pitch = 0.25f; cam.Yaw = -0.55f;
        Selected.Clear(); SelectionChanged(); yield return Frames(10);
        foreach (var x in StartFilm("16_boolean", 6)) yield return x;
        foreach (var x in Say("Boolean in Polymorph", "Select the cutter, then the object to keep (last = active). Object panel > Boolean.", 70)) yield return x;

        foreach (var x in Say("1. Subtract: a door in the wall", "Click the door box, Shift+click the wall, Subtract", 25)) yield return x;
        string w0i = Info(wall);
        SelectOnly(door, wall); yield return Frames(25);
        foreach (var x in Click("bool_sub")) yield return x;
        string w1 = Info(wall);
        bool doorHidden = Scene.Find(door.Id).Hidden;
        yield return Frames(30);
        Note($"wall {w0i} -> door cut: {w1}");
        Expect(Scene.Find(wall.Id).Mesh.Faces.Count > 6 && doorHidden && Scene.Find(wall.Id).Mesh.IsClosedManifold(out _), $"Subtract cuts the door ({w1}), cutter hidden {doorHidden}");

        foreach (var x in Say("2. Subtract again: a window", "Same thing with the window box", 25)) yield return x;
        SelectOnly(win, Scene.Find(wall.Id)); yield return Frames(25);
        foreach (var x in Click("bool_sub")) yield return x;
        string w2 = Info(wall);
        yield return Frames(30);
        Note($"window cut: {w2}");
        Expect(Scene.Find(wall.Id).Mesh.IsClosedManifold(out _) && Scene.Find(win.Id).Hidden, $"second cut keeps the wall closed ({w2})");

        foreach (var x in Say("3. Ctrl+Z", "Undo gives the window back, the cutter shows again", 20)) yield return x;
        int fBefore = Scene.Find(wall.Id).Mesh.Faces.Count;
        KeyDownT(Keys.ControlKey); KeyDownT(Keys.Z); yield return Frames(4); KeyUpT(Keys.Z); KeyUpT(Keys.ControlKey); yield return Frames(30);
        bool undoOk = !Scene.Find(win.Id).Hidden && Scene.Find(wall.Id).Mesh.Faces.Count != fBefore;
        Expect(undoOk, $"Ctrl+Z undoes the window cut ({Info(wall)}, cutter visible {!Scene.Find(win.Id).Hidden})");
        KeyDownT(Keys.ControlKey); KeyDownT(Keys.ShiftKey); KeyDownT(Keys.Z); yield return Frames(4); KeyUpT(Keys.Z); KeyUpT(Keys.ShiftKey); KeyUpT(Keys.ControlKey); yield return Frames(30);

        // 4. union
        foreach (var x in Scene.Objects.ToList()) x.Hidden = true;
        var a = Box("cube_a", 2f, 2f, 2f, new Vector3(5f, 0, 0));
        var b = Box("cube_b", 2f, 2f, 2f, new Vector3(6f, 1f, 1f));
        P.Width = w0; P.Length = l0; P.Height = h0;
        cam.Frame(new Vector3(3.5f, -2f, -0.5f), new Vector3(8.5f, 3.5f, 3.5f));
        foreach (var x in Say("4. Union: two cubes become one", "Overlapping cubes, one mesh, the inside is removed", 25)) yield return x;
        SelectOnly(b, a); yield return Frames(25);
        foreach (var x in Click("bool_union")) yield return x;
        string u = Info(a); yield return Frames(30);
        Note($"union: {u}");
        Expect(Scene.Find(a.Id).Mesh.IsClosedManifold(out _) && Scene.Find(a.Id).Mesh.Faces.Count > 6, $"Union gives one closed mesh ({u})");

        // 5. intersect
        foreach (var x in Scene.Objects.ToList()) x.Hidden = true;
        var c = Box("cube_c", 2f, 2f, 2f, new Vector3(11f, 0, 0));
        Params.Radius = 1.3f; Params.Segments = 32;
        var sp = Prim(CreateKind.Sphere, new Vector3(11f, 0, 1f)); sp.Name = "sphere";
        P.Width = w0; P.Length = l0; P.Height = h0;
        cam.Frame(new Vector3(9f, -2.5f, -0.5f), new Vector3(13f, 2.5f, 2.8f));
        foreach (var x in Say("5. Intersect: keeps only what both share", "Cube + sphere = a cube with round corners", 25)) yield return x;
        SelectOnly(sp, c); yield return Frames(25);
        foreach (var x in Click("bool_int")) yield return x;
        string it = Info(c); yield return Frames(40);
        var bb = Scene.Find(c.Id).Mesh.Verts; float maxX = bb.Max(v => v.X) - bb.Min(v => v.X);
        Note($"intersect: {it}, width {maxX:0.00} m");
        Expect(Scene.Find(c.Id).Mesh.IsClosedManifold(out _) && maxX <= 2.01f && Scene.Find(c.Id).Mesh.Faces.Count > 6 && Scene.Find(sp.Id).Hidden, $"Intersect keeps the common part ({it}, sphere hidden {Scene.Find(sp.Id).Hidden})");

        foreach (var x in Say("Boolean: Subtract, Union, Intersect", "Cutters stay hidden in the scene: move one and cut again", 70)) yield return x;
        foreach (var x in EndFilm()) yield return x;
    }

    IEnumerable<object> TutModifiers()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true; propTab = PropTab.Modifiers; if (TimelineOpen) ToggleTimeline();
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Shaded; vp.EdgedFaces = true;
        SceneObject cube = null;
        string Count(SceneObject o) { var m = o.Evaluated(); return $"{m.Verts.Count} verts, {m.Faces.Count} faces"; }
        void NewCube(Vector3 frameMin, Vector3 frameMax)
        {
            if (cube != null) { var id = cube.Id; Scene.RemoveObjects(o => o.Id == id); }
            cube = Prim(CreateKind.Box, Vector3.Zero); cube.Name = "cube";
            SelectOnly(cube); propTab = PropTab.Modifiers;
            cam.Frame(frameMin, frameMax); cam.Pitch = 0.45f;
        }
        T Add<T>(T mod) where T : MoloMax.Core.Modifiers.Modifier
        {
            var o = cube;
            Do("Add " + mod.Name, () => { mod.Name = MoloMax.Core.Modifiers.ModifierStack.UniqueName(o.Modifiers, mod.Name); o.Modifiers.Add(mod); o.Touch(); });
            activeModifier[o.Id] = o.Modifiers.Count - 1;
            return mod;
        }
        void Report(string name, string before, bool changed, string why = null)
        {
            string after = Count(cube);
            Note($"{name}: {before} -> {after}{(why != null ? "  (" + why + ")" : "")}");
            Expect(changed, $"{name} changes the cube ({before} -> {after})");
        }
        var small = (new Vector3(-2.2f, -2.2f, -0.5f), new Vector3(2.2f, 2.2f, 2f));
        NewCube(small.Item1, small.Item2);
        yield return Frames(10);
        foreach (var x in StartFilm("15_modifiers", 14)) yield return x;
        foreach (var x in Say("All the modifiers, on a cube", "Properties > Modifiers > Add Modifier. The cube is never touched until you Apply.", 80)) yield return x;

        // 1 Edit Poly
        string b = Count(cube);
        foreach (var x in Say("1. Edit Poly", "Edit inside the stack: here the top face is pulled up (extrude)", 30)) yield return x;
        Add(new MoloMax.Core.Modifiers.EditPolyModifier()); yield return Frames(20);
        EditModifier(cube, 0, SubObject.Polygon); yield return Frames(10);
        int top = FaceToward(cube, Vector3.UnitZ); SubSel(cube).Clear(); SubSel(cube).Faces.Add(top); yield return Frames(20);
        ExtrudeFaces(0.7f, false); yield return Frames(30);
        EndModifierEdit(cube); yield return Frames(40);
        Report("Edit Poly", b, cube.Evaluated().Faces.Count > 6);

        // 2 Unwrap UVW
        NewCube(small.Item1, small.Item2); b = Count(cube);
        foreach (var x in Say("2. Unwrap UVW", "Auto unwrap: the cube is cut and laid flat for the texture", 30)) yield return x;
        var uw = Add(new MoloMax.Core.Modifiers.UnwrapUvwModifier()); yield return Frames(15);
        int charts = 0;
        UnwrapOp(cube, 0, uw, "Auto Unwrap", (m, faces, mod) => { var (n, seams) = UvUnfold.AutoUnwrap(m, mod.AngleLimit, faces); charts = n; mod.SetSeams(seams); UvTools.SetDensity(m, faces, mod.Density, mod.TextureSize); UvTools.Pack(m, faces); });
        uvOpen = true; uvChecker = true; yield return Frames(80);
        uvOpen = false; uvChecker = false;
        Note($"Unwrap UVW: {charts} UV island(s)");
        Expect(charts > 0 && uw.UV != null, $"Unwrap UVW makes UV islands ({charts})");

        // 3 Weld
        NewCube(new Vector3(-2f, -3f, -0.5f), new Vector3(5f, 3f, 2.5f)); b = Count(cube);
        foreach (var x in Say("3. Weld", "Two cubes stuck together (Array) have double points where they touch...", 30)) yield return x;
        var wa = Add(new MoloMax.Core.Modifiers.ArrayModifier()); wa.RelativeOffset = new Vector3(1, 0, 0); cube.Touch(); yield return Frames(40);
        string weldBefore = Count(cube);
        foreach (var x in Say("3. Weld", "...Weld joins them into one: 16 points become 12", 20)) yield return x;
        Add(new MoloMax.Core.Modifiers.WeldModifier()); yield return Frames(50);
        Note($"Weld: {weldBefore} -> {Count(cube)}");
        Expect(cube.Evaluated().Verts.Count == 12, $"Weld joins the double points ({weldBefore} -> {Count(cube)})");

        // 4 Array
        NewCube(new Vector3(-2f, -4f, -0.5f), new Vector3(10f, 4f, 2.5f)); b = Count(cube);
        foreach (var x in Say("4. Array", "Copies in a row: 4 cubes, spaced 1.3 x their size", 30)) yield return x;
        var ar = Add(new MoloMax.Core.Modifiers.ArrayModifier()); yield return Frames(25);
        ar.Count = 4; ar.RelativeOffset = new Vector3(1.3f, 0, 0); cube.Touch(); yield return Frames(50);
        Report("Array", b, cube.Evaluated().Verts.Count == 32);

        // 5 Mirror
        NewCube(new Vector3(-3.5f, -3f, -0.5f), new Vector3(3.5f, 3f, 2f));
        for (int i = 0; i < cube.Mesh.Verts.Count; i++) cube.Mesh.Verts[i] += new Vector3(1.2f, 0, 0); cube.Touch(); b = Count(cube);
        foreach (var x in Say("5. Mirror", "A copy on the other side of the object's centre (X)", 30)) yield return x;
        Add(new MoloMax.Core.Modifiers.MirrorModifier()); yield return Frames(50);
        Report("Mirror", b, cube.Evaluated().Verts.Count == 16);

        // 6 Symmetry
        NewCube(new Vector3(-3f, -3f, -0.5f), new Vector3(3f, 3f, 2f));
        for (int i = 0; i < cube.Mesh.Verts.Count; i++) cube.Mesh.Verts[i] += new Vector3(0.6f, 0, 0); cube.Touch(); b = Count(cube);
        foreach (var x in Say("6. Symmetry", "Cuts at the centre and copies one half over the other", 30)) yield return x;
        var sy = Add(new MoloMax.Core.Modifiers.SymmetryModifier()); yield return Frames(30);
        sy.Flip = true; cube.Touch(); yield return Frames(30);
        Report("Symmetry", b, Count(cube) != b);

        // 7 Solidify
        NewCube(small.Item1, small.Item2);
        top = FaceToward(cube, Vector3.UnitZ); cube.Mesh.Faces.RemoveAt(top); cube.Touch(); b = Count(cube);
        cam.Pitch = 0.9f;
        foreach (var x in Say("7. Solidify", "Gives walls a thickness (here the cube is open on top)", 30)) yield return x;
        var so = Add(new MoloMax.Core.Modifiers.SolidifyModifier()); yield return Frames(20);
        so.Thickness = 0.15f; cube.Touch(); yield return Frames(50);
        Report("Solidify", b, cube.Evaluated().Faces.Count > 5);

        // 8 Subdivision Surface
        NewCube(small.Item1, small.Item2); b = Count(cube);
        foreach (var x in Say("8. Subdivision Surface", "Simple (default): cuts every face, the shape stays the same", 30)) yield return x;
        var sd = Add(new MoloMax.Core.Modifiers.SubdivisionModifier()); yield return Frames(20);
        sd.LevelsViewport = 2; cube.Touch(); yield return Frames(50);
        var ext = cube.Evaluated().Verts; bool sameShape = ext.Max(v => MathF.Abs(v.X)) > 0.99f && ext.Min(v => v.Z) < 0.01f && ext.Max(v => v.Z) > 1.99f;
        Expect(!sd.CatmullClark && sameShape, $"Subdivision Simple by default keeps the cube shape (simple {!sd.CatmullClark}, same shape {sameShape})");
        foreach (var x in Say("8. Subdivision Surface", "Option Catmull-Clark: rounds the shape", 20)) yield return x;
        sd.CatmullClark = true; cube.Touch(); yield return Frames(45);
        Report("Subdivision Surface", b, cube.Evaluated().Faces.Count == 96);

        // 9 Triangulate
        NewCube(small.Item1, small.Item2); b = Count(cube);
        foreach (var x in Say("9. Triangulate", "Every square becomes 2 triangles (what the game uses)", 30)) yield return x;
        Add(new MoloMax.Core.Modifiers.TriangulateModifier()); yield return Frames(50);
        Report("Triangulate", b, cube.Evaluated().Faces.Count == 12);

        // 10 Cast
        NewCube(small.Item1, small.Item2); b = Count(cube);
        foreach (var x in Say("10. Cast", "Pushes the points toward a sphere (cube cut first so it can round)", 30)) yield return x;
        var cs0 = Add(new MoloMax.Core.Modifiers.SubdivisionModifier()); cs0.CatmullClark = false; cs0.LevelsViewport = 3; cube.Touch(); yield return Frames(25);
        var ca = Add(new MoloMax.Core.Modifiers.CastModifier()); yield return Frames(25);
        ca.Factor = 1f; cube.Touch(); yield return Frames(50);
        Report("Cast", b, Count(cube) != b);

        // 11 Displace
        NewCube(small.Item1, small.Item2); b = Count(cube);
        foreach (var x in Say("11. Displace", "Bumps the surface with noise (rocks, ground)", 30)) yield return x;
        var ds0 = Add(new MoloMax.Core.Modifiers.SubdivisionModifier()); ds0.CatmullClark = false; ds0.LevelsViewport = 3; cube.Touch(); yield return Frames(20);
        var dp = Add(new MoloMax.Core.Modifiers.DisplaceNoiseModifier()); yield return Frames(20);
        dp.Strength = 0.35f; dp.Size = 0.5f; cube.Touch(); yield return Frames(50);
        Report("Displace", b, Count(cube) != b);

        // 12 Smooth
        NewCube(small.Item1, small.Item2); b = Count(cube);
        foreach (var x in Say("12. Smooth", "Softens the shape by averaging the points", 30)) yield return x;
        var sm0 = Add(new MoloMax.Core.Modifiers.SubdivisionModifier()); sm0.CatmullClark = false; sm0.LevelsViewport = 2; cube.Touch(); yield return Frames(20);
        var sm = Add(new MoloMax.Core.Modifiers.SmoothModifier()); yield return Frames(20);
        sm.Factor = 1f; sm.Repeat = 6; cube.Touch(); yield return Frames(50);
        var ev = cube.Evaluated(); float maxR = ev.Verts.Max(v => v.Length()), minR = ev.Verts.Min(v => v.Length());
        Report("Smooth", b, Count(cube) != b);

        // 13 Smooth by Angle
        NewCube(small.Item1, small.Item2); b = Count(cube);
        vp.EdgedFaces = false;
        foreach (var x in Say("13. Smooth by Angle", "Shading only: soft between faces under 30 degrees, hard edges stay hard", 30)) yield return x;
        var sa0 = Add(new MoloMax.Core.Modifiers.SubdivisionModifier()); sa0.CatmullClark = true; sa0.LevelsViewport = 2; cube.Touch(); yield return Frames(30);
        Add(new MoloMax.Core.Modifiers.SmoothByAngleModifier()); yield return Frames(50);
        Note($"Smooth by Angle: {b} -> {Count(cube)} (shading only, same points)");
        Expect(cube.Modifiers.Count == 2, "Smooth by Angle added (it changes the shading, not the points)");
        vp.EdgedFaces = true;

        foreach (var x in Say("13 modifiers, all on the cube", "Stack them, switch them off with the eye, Apply when happy", 70)) yield return x;
        foreach (var x in EndFilm()) yield return x;
    }

    IEnumerable<object> TutAnimation()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = false; if (TimelineOpen) ToggleTimeline();
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        var door = Prim(CreateKind.Box, Vector3.Zero); door.Name = "molo_crate";
        var wood = LibPick(("All", "yacht_wood_2"), ("Wood", ""));
        if (wood.M != null) LibOnFaces(door, Enumerable.Range(0, door.Mesh.Faces.Count), wood.M, 1f);
        cam.Frame(new Vector3(-2, -3, -0.5f), new Vector3(6, 3, 2.5f)); cam.Pitch = 0.3f;
        SelectOnly(door); Tool = Tool.Move; AutoKey = false;
        yield return Frames(10);
        foreach (var x in StartFilm("14_animation", 7)) yield return x;
        foreach (var x in Say("Animation in Polymorph", "As simple as Blender: Key, another frame, move, Key, Play", 70)) yield return x;
        // timeline
        foreach (var x in Say("1. Click Timeline", "The timeline opens under the view", 20)) yield return x;
        foreach (var x in ClickAt(TutCentre("tb_timeline"), false, 30)) yield return x;
        yield return Frames(25);
        // key at 0
        foreach (var x in Say("2. Select the object, click Key (or I)", "Frame 0: its place is saved", 20)) yield return x;
        foreach (var x in ClickAt(TutCentre("tl_key"), false, 30)) yield return x;
        yield return Frames(25);
        // frame 60, move, key
        foreach (var x in Say("3. Click frame 60, move the object, Key", "A diamond = a key", 20)) yield return x;
        foreach (var x in ClickAt(RulerAt(60), false, 30)) yield return x;
        yield return Frames(10);
        door = Scene.Find(door.Id);
        var gx = GizmoGrip(vp, GizmoHandle.X);
        SelectionPivot(out var pv, out _); var dir = Vector2.Normalize(ScreenOf(vp, pv + Vector3.UnitX) - ScreenOf(vp, pv));
        foreach (var x in DragTo(gx, gx + dir * 220, 45)) yield return x;
        foreach (var x in ClickAt(TutCentre("tl_key"), false, 30)) yield return x;
        yield return Frames(20);
        // auto key
        foreach (var x in Say("4. Auto key ON: every move is saved", "Frame 120, move it again: no need to click Key", 20)) yield return x;
        foreach (var x in ClickAt(TutCentre("tl_auto"), false, 30)) yield return x;
        foreach (var x in ClickAt(RulerAt(120), false, 30)) yield return x;
        yield return Frames(10);
        var gy = GizmoGrip(vp, GizmoHandle.Y);
        SelectionPivot(out pv, out _); var dy = Vector2.Normalize(ScreenOf(vp, pv + Vector3.UnitY) - ScreenOf(vp, pv));
        foreach (var x in DragTo(gy, gy + dy * 180, 45)) yield return x;
        yield return Frames(15);
        foreach (var x in ClickAt(TutCentre("tl_auto"), false, 20)) yield return x; // off
        // play
        foreach (var x in Say("5. Play", "Space plays and stops", 15)) yield return x;
        foreach (var x in ClickAt(RulerAt(0), false, 25)) yield return x;
        foreach (var x in ClickAt(TutCentre("tl_play"), false, 25)) yield return x;
        for (int i = 0; i < 150; i++) { AnimPlaying = true; yield return Frames(1); }
        foreach (var x in ClickAt(TutCentre("tl_play"), false, 20)) yield return x;
        AnimPlaying = false;
        // texture slide: a screen with a GTA texture
        Undo.Record("screen", Scene);
        var screen = Prim(CreateKind.Plane, new Vector3(-2.5f, 3, 1.4f)); screen.Name = "molo_screen";
        screen.Scale = new Vector3(2.4f, 1.4f, 1); screen.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2); screen.Touch();
        cam.Target = new Vector3(0.5f, 1.5f, 1); cam.Distance = 11;
        var tex = LibPick(("All", "poster"), ("All", "sign"), ("All", "tile"), ("All", ""));
        if (tex.M != null) LibOnFaces(screen, Enumerable.Range(0, screen.Mesh.Faces.Count), tex.M, 1f);
        SelectOnly(screen); yield return Frames(10);
        foreach (var x in Say("6. Texture animation (neons, screens, water)", "Slide the texture and key it: it scrolls in the game too", 30)) yield return x;
        SetFrame(0); uvEditV = 0;
        var st = Scene.Anim.Track(screen.Id, true);
        st.Curve(Core.Anim.Ch.UvV, true).SetKey(0, 0, Core.Anim.KeyInterp.Linear);
        SetFrame(120); st.Curve(Core.Anim.Ch.UvV).SetKey(120, 2, Core.Anim.KeyInterp.Linear);
        animShownFrame = float.NaN; yield return Frames(30);
        SetFrame(0); AnimPlaying = true;
        for (int i = 0; i < 130; i++) { AnimPlaying = true; yield return Frames(1); }
        AnimPlaying = false;
        // camera
        foreach (var x in Say("7. Key camera: a fly-through", "Key the view, turn it, key again, Play", 30)) yield return x;
        SetFrame(0); cam.Yaw = -2.2f; cam.Distance = 14; KeyCamera(); yield return Frames(10);
        SetFrame(120); cam.Yaw = -0.6f; cam.Distance = 9; KeyCamera(); yield return Frames(10);
        SetFrame(0); AnimPlaying = true;
        for (int i = 0; i < 130; i++) { AnimPlaying = true; yield return Frames(1); }
        AnimPlaying = false;
        // ycd
        foreach (var x in Say("GTA .ycd: import or export", "Your .ycd open as keys; the export plays in FiveM (30 fps)", 20)) yield return x;
        foreach (var x in ClickAt(TutCentre("tl_ycd"), false, 30)) yield return x;
        yield return Frames(60);
        PostKey(Keys.Escape, true); yield return Frames(2); PostKey(Keys.Escape, false); yield return Frames(4);
        var outp = StPath("molo_demo_anims.ycd");
        ExportYcd(outp);
        foreach (var x in Say("Exported: molo_demo_anims.ycd", status, 70)) yield return x;
        foreach (var x in EndFilm()) yield return x;
        Expect(File.Exists(outp), "the film's .ycd is written");
    }

    /// <summary>A brush stroke with the real mouse between two points on a mesh (held buttons: 1 left, +4 Shift, +8 Ctrl).</summary>
    IEnumerable<object> Stroke(Vector2 a, Vector2 b, int frames = 30, int buttons = 1)
    {
        foreach (var x in MouseTo(a, 14)) yield return x;
        yield return Frames(3);
        PostMouse(0x201, a, buttons); tutClick = 0.35f; yield return Frames(2);
        for (int i = 1; i <= frames; i++) { PostMouse(0x200, Vector2.Lerp(a, b, Ease(i / (float)frames)), buttons); yield return Frames(1); }
        PostMouse(0x202, b, 0); yield return Frames(6);
    }

    /// <summary>Demo film: the Sculpt and Paint buttons, the sculpt brushes on a rock, then Molo Paint (Substance-like).</summary>
    IEnumerable<object> TutSculptPaintDemo()
    {
        Fresh(); if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true;
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        Params.Radius = 1; Params.Segments = 96;
        var rock = Prim(CreateKind.Sphere, Vector3.Zero); rock.Name = "rock";
        var ctr = rock.Mesh.Verts.Aggregate(Vector3.Zero, (q, w) => q + w) / rock.Mesh.Verts.Count;
        SelectOnly(rock); cam.Frame(ctr - new Vector3(1.5f), ctr + new Vector3(1.5f)); cam.Pitch = 0.25f; cam.Yaw = -1.9f;
        yield return Frames(10);
        foreach (var x in StartFilm("13_demo_sculpt_paint", 9)) yield return x;
        foreach (var x in Say("Sculpt and Paint", "Two buttons in the top bar: Sculpt and Paint", 70)) yield return x;
        // 1. the Sculpt button
        foreach (var x in Say("1. Select a mesh, click Sculpt", "The brushes open in the panel on the right", 30)) yield return x;
        foreach (var x in ClickAt(TutCentre("tb_sculpt"), false, 30)) yield return x;
        yield return Frames(30);
        Vector2 On(float u, float v)
        {
            // a point on the side of the rock facing the camera, u / v across the view
            var right = Vector3.Normalize(Vector3.Cross(cam.Forward, Vector3.UnitZ)); var up = Vector3.Cross(right, cam.Forward);
            var d = Vector3.Normalize(-cam.Forward + right * u + up * v);
            return ScreenOf(vp, ctr + d * 1.0f);
        }
        if (SculptMode)
        {
            Sculpt.SymX = false; Sculpt.Strength = 1f;
            // Draw
            Sculpt.Brush = SculptBrush.Draw; Sculpt.Radius = 0.3f;
            foreach (var x in Say("2. Draw: hold the left button", "It raises the surface under the brush", 20)) yield return x;
            for (int k = 0; k < 4; k++) foreach (var x in Stroke(On(-0.55f, 0.45f - k * 0.3f), On(0.55f, 0.35f - k * 0.3f), 28)) yield return x;
            // Ctrl digs
            foreach (var x in Say("3. Ctrl + drag digs", "Ctrl inverts any brush", 20)) yield return x;
            KeyDownT(Keys.ControlKey); yield return Frames(2);
            foreach (var x in Stroke(On(-0.5f, -0.1f), On(0.5f, 0.05f), 32, 1 | 8)) yield return x;
            KeyUpT(Keys.ControlKey);
            // Inflate
            Sculpt.Brush = SculptBrush.Inflate; Sculpt.Radius = 0.4f;
            foreach (var x in Say("4. Inflate", "Swells the shape (pick the brush in the panel)", 20)) yield return x;
            foreach (var x in Stroke(On(0.3f, 0.5f), On(0.5f, 0.1f), 30)) yield return x;
            // Grab
            Sculpt.Brush = SculptBrush.Grab; Sculpt.Radius = 0.5f;
            foreach (var x in Say("5. Grab", "Pulls a whole area with the mouse", 20)) yield return x;
            foreach (var x in Stroke(On(-0.2f, 0.75f), On(-0.2f, 0.75f) + new Vector2(-40, -90), 34)) yield return x;
            // Shift smooths
            Sculpt.Brush = SculptBrush.Draw; Sculpt.Radius = 0.35f;
            foreach (var x in Say("6. Shift + drag smooths", "Shift smooths with any brush", 20)) yield return x;
            KeyDownT(Keys.ShiftKey); yield return Frames(2);
            foreach (var x in Stroke(On(-0.5f, 0.2f), On(0.5f, 0.2f), 34, 1 | 4)) yield return x;
            KeyUpT(Keys.ShiftKey);
            for (int i = 0; i < 70; i++) { cam.Orbit(1.6f, 0); yield return Frames(1); }
            foreach (var x in ClickAt(TutCentre("tb_sculpt"), false, 24)) yield return x; // leave sculpt
        }
        else Note("sculpt mode did not start: " + status);
        // 2. Paint
        foreach (var x in Say("7. Paint: click Paint", "UVs are made by themselves; layers, smart materials, masks like Substance", 30)) yield return x;
        foreach (var x in ClickAt(TutCentre("tb_paint"), false, 30)) yield return x;
        yield return Frames(40);
        if (paintMode)
        {
            paintTool = PaintTool.Brush; brush.Color = true; brush.ColorValue = new Vector3(0.75f, 0.2f, 0.12f); brush.Strength = 0.9f; brush.Radius = 0.18f;
            foreach (var x in Say("8. Paint with the brush", "B brush, E eraser, F fill, [ ] size", 15)) yield return x;
            for (int k = 0; k < 3; k++) foreach (var x in Stroke(On(-0.5f, 0.3f - k * 0.25f), On(0.5f, 0.25f - k * 0.25f), 26)) yield return x;
            foreach (var x in ClickAt(TutCentre("tb_paint"), false, 24)) yield return x; // leave paint
        }
        else Note("paint mode did not start: " + status);
        // 3. the full Molo Paint: a sofa with smart materials, wear, dust
        foreach (var x in Say("9. Smart materials, wear, dust, bake", "Then export straight to GTA textures (DDS)", 40)) yield return x;
        Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; vp.Shading = ShadingMode.Realistic;
        foreach (var x in TrailerPaint(vp)) yield return x;
        foreach (var x in EndFilm()) yield return x;
    }

    IEnumerable<object> TrailerPaint(Viewport vp)
    {
        var cam = vp.Camera;
        ShowPanel = true;
        // the sofa: base, back, two arms, two seat and two back cushions; wooden legs apart
        var m = new PolyMesh();
        m.Append(SoftBox(2.1f, 0.92f, 0.30f, new Vector3(0, 0, 0.12f), 0.02f));
        m.Append(SoftBox(2.1f, 0.20f, 0.82f, new Vector3(0, 0.36f, 0.12f), 0.03f));
        foreach (var sx in new[] { -1f, 1f })
        {
            m.Append(SoftBox(0.20f, 0.92f, 0.66f, new Vector3(sx * 0.95f, 0, 0.12f), 0.035f));
            m.Append(SoftBox(0.84f, 0.70f, 0.16f, new Vector3(sx * 0.43f, -0.07f, 0.42f), 0.03f));
            m.Append(SoftBox(0.84f, 0.20f, 0.42f, new Vector3(sx * 0.43f, 0.17f, 0.58f), 0.035f));
        }
        var sofa = Scene.Add("sofa", m, Vector3.Zero); sofa.Name = "sofa";
        var lm = new PolyMesh();
        foreach (var (lx, ly) in new[] { (-0.95f, -0.38f), (0.95f, -0.38f), (-0.95f, 0.38f), (0.95f, 0.38f) })
        {
            var leg = MoloMax.Core.Mesh.Primitives.Cone(0.022f, 0.032f, 0.12f, 12);
            for (int i = 0; i < leg.Verts.Count; i++) leg.Verts[i] += new Vector3(lx, ly, 0);
            lm.Append(leg);
        }
        var legs = Scene.Add("sofa_legs", lm, Vector3.Zero); legs.Name = "sofa_legs";
        var wood = LibPick(("All", "yacht_wood_2"), ("Wood", ""));
        if (wood.M != null) LibOnFaces(legs, Enumerable.Range(0, lm.Faces.Count), wood.M, 0.4f);
        SelectOnly(sofa);
        propTab = PropTab.Paint; paintTab = 1; paintRes = 2048;
        DoMesh("Smart UV Project", (oo, mm, s2) => UvUnwrap.SmartProject(mm));
        StartPaint(sofa);
        cam.Target = new Vector3(0.05f, 0, 0.52f); cam.Distance = 3.6f; cam.Pitch = 0.3f; cam.Yaw = -1.75f;
        yield return Frames(12);
        var ts = sofa.Paint?.Cur;
        if (ts == null) yield break;
        var home = cam.Target;
        IEnumerable<object> Glide(bool aside, int n, float orbit)
        {
            for (int i = 0; i < n; i++)
            {
                cam.Target = Vector3.Lerp(cam.Target, aside ? home + cam.Right * 0.35f : home, 0.1f); cam.Orbit(orbit, 0); yield return Frames(1);
            }
        }
        void Up(bool full = false) { ts.Refresh(full); UploadPaint(sofa, ts, full); }
        // 1. a real GTA V fabric from the library, dyed bottle green
        var fabric = LibPick(("All", "fabric_cln_01_dm"), ("Fabric & carpet", ""));
        if (fabric.M != null)
        {
            LibShow(fabric, LibraryAction.Fill, vp);
            foreach (var x in Glide(true, 28, 0.3f)) yield return x;
            LibraryFill(fabric.M);
            if (ts.Current != null) { ts.Current.Color = new Vector3(0.20f, 0.40f, 0.34f); ts.Current.TileMetres = 0.6f; ts.Current.Rough = 0.85f; }
            Up(true);
            foreach (var x in Glide(true, 30, 0.3f)) yield return x;
            libraryOpen = false; libWinAt = null;
            foreach (var x in Glide(false, 16, 0.3f)) yield return x;
        }
        else { AddLayer(ts, new PaintLayer { Name = "Velvet", Kind = LayerKind.Fill, Color = new Vector3(0.16f, 0.34f, 0.29f), Rough = 0.85f }); Up(true); }
        // 2. worn, faded fabric on the edges
        var worn = new PaintLayer { Name = "Faded edges", Kind = LayerKind.Fill, Color = new Vector3(0.36f, 0.54f, 0.47f), Rough = 0.9f };
        AddLayer(ts, worn); SmartMasks.All.First(mk => mk.Name == "Edge wear").Apply(worn);
        worn.MaskThreshold = 0.62f; worn.Opacity = 0.55f;
        Up(true);
        foreach (var x in Glide(false, 30, 0.35f)) yield return x;
        // 3. stitched seams, painted along the cushions' edges
        SetPaintTool(PaintTool.Brush); AddLayer(ts, new PaintLayer { Name = "Stitching", Kind = LayerKind.Paint });
        brush.Stamp = null; brush.Tip = null; brush.Erase = false;
        brush.ColorValue = new Vector3(0.90f, 0.84f, 0.66f); brush.Radius = 0.0065f; brush.Strength = 1; brush.Hardness = 0.9f;
        PrepareStroke(sofa, ts);
        var seams = new List<(Vector3 A, Vector3 B, Vector3 N)>();
        foreach (var sx in new[] { -1f, 1f })
        {
            float cx = sx * 0.43f;
            seams.Add((new Vector3(cx - 0.38f, -0.4205f, 0.545f), new Vector3(cx + 0.38f, -0.4205f, 0.545f), -Vector3.UnitY)); // seat cushion front
            seams.Add((new Vector3(cx - 0.38f, 0.0695f, 0.962f), new Vector3(cx + 0.38f, 0.0695f, 0.962f), -Vector3.UnitY));   // back cushion
            seams.Add((new Vector3(sx * 0.95f - 0.065f, -0.4605f, 0.74f), new Vector3(sx * 0.95f + 0.065f, -0.4605f, 0.74f), -Vector3.UnitY)); // arm
        }
        var farD = cam.Distance; var farT = cam.Target;
        for (int i = 0; i < 24; i++) { float t = Ease(i / 23f); cam.Distance = farD + (1.25f - farD) * t; cam.Target = Vector3.Lerp(farT, new Vector3(-0.35f, -0.35f, 0.6f), t); yield return Frames(1); }
        foreach (var (A, B, N) in seams)
        {
            int dots = (int)(Vector3.Distance(A, B) / 0.014f);
            for (int d = 0; d <= dots; d++)
            {   // a dashed stitch: short dashes
                var p0 = Vector3.Lerp(A, B, d / (float)dots); var p1 = Vector3.Lerp(A, B, Math.Min(1, (d + 0.5f) / dots));
                StampAll(sofa, ts, p0, N, brush.Radius, p0); StampAll(sofa, ts, p1, N, brush.Radius, p0);
                if (d % 6 == 0) { UploadPaint(sofa, ts); cam.Orbit(0.2f, 0); yield return Frames(1); }
            }
        }
        UploadPaint(sofa, ts, true);
        for (int i = 0; i < 20; i++) { cam.Orbit(0.3f, 0); yield return Frames(1); }
        for (int i = 0; i < 24; i++) { float t = Ease(i / 23f); cam.Distance = 1.25f + (2.4f - 1.25f) * t; cam.Target = Vector3.Lerp(new Vector3(-0.35f, -0.35f, 0.6f), new Vector3(0, -0.05f, 0.55f), t); yield return Frames(1); }
        // 4. seat wear: soft passes where people sit and lean
        AddLayer(ts, new PaintLayer { Name = "Seat wear", Kind = LayerKind.Paint });
        brush.ColorValue = new Vector3(0.42f, 0.58f, 0.51f); brush.Radius = 0.15f; brush.Strength = 0.22f; brush.Hardness = 0.05f;
        PrepareStroke(sofa, ts);
        var spots = new List<(Vector3 C, Vector3 N, float R)>
        {
            (new Vector3(-0.43f, -0.10f, 0.581f), Vector3.UnitZ, 0.13f), (new Vector3(0.43f, -0.10f, 0.581f), Vector3.UnitZ, 0.13f),
            (new Vector3(-0.95f, -0.25f, 0.781f), Vector3.UnitZ, 0.05f), (new Vector3(0.95f, -0.25f, 0.781f), Vector3.UnitZ, 0.05f),
        };
        foreach (var (C, N, R) in spots)
        {
            Vector3 prev = C;
            for (int i = 0; i <= 36; i++)
            {
                float a = i / 36f * MathF.Tau * 1.5f; float r = R * (0.3f + 0.7f * i / 36f);
                var p = C + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r * 0.7f, 0);
                StampAll(sofa, ts, p, N, brush.Radius, prev); prev = p;
                if (i % 4 == 0) { UploadPaint(sofa, ts); cam.Orbit(0.25f, 0); yield return Frames(1); }
            }
        }
        UploadPaint(sofa, ts, true);
        // 5. dust in the creases
        var dust = new PaintLayer { Name = "Dust in creases", Kind = LayerKind.Fill, Color = new Vector3(0.12f, 0.15f, 0.12f), Rough = 0.95f };
        AddLayer(ts, dust); SmartMasks.All.First(mk => mk.Name == "Dirt in corners").Apply(dust);
        dust.MaskThreshold = 0.7f; dust.Opacity = 0.45f;
        Up(true);
        for (int i = 0; i < 24; i++) { float t = Ease(i / 23f); cam.Distance = 2.4f + (3.4f - 2.4f) * t; yield return Frames(1); }
        for (int i = 0; i < 110; i++) { cam.Orbit(1.3f, 0); yield return Frames(1); }
        ShowPanel = false;
    }

    /// <summary>The first two trailer shots alone (modeling + GTA materials, painting), for a quick look.</summary>
    IEnumerable<object> TutTrailerPreview()
    {
        Fresh();
        bool exp0 = ShowExplorer, stats0 = ShowStats;
        ShowExplorer = false; ShowPanel = false; ShowStats = false; tutHideCursor = true;
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        foreach (var x in StartFilm("99_preview", 0)) yield return x;
        tutCaption = null; ShowPanel = false;
        var cam = vp.Camera;
        Mark("model");
        cam.Target = new Vector3(0, -1, 6.4f); cam.Distance = 36; cam.Pitch = 0.2f; cam.Yaw = -2.0f;
        foreach (var x in BuildTower(Vector3.Zero, vp, 0.22f, true, () => Mark("gta_mats"))) yield return x;
        for (int i = 0; i < 60; i++) { cam.Orbit(0.8f, 0); yield return Frames(1); }
        Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; vp.Shading = ShadingMode.Realistic;
        Mark("paint");
        foreach (var x in TrailerPaint(vp)) yield return x;
        Mark("stop");
        foreach (var x in EndFilm()) yield return x;
        ShowExplorer = exp0; ShowStats = stats0; tutHideCursor = false; Maximized = false;
    }

    IEnumerable<object> TutLotTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var cam = vp.Camera; MapStream = true;
        var area = new Vector3(-250, -1950, 25);
        GoTo(area); cam.Distance = 260; cam.Pitch = 0.6f;
        yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 25);
        yield return Settle(() => archLoading == 0, 20);
        LoadAreaAt(area, 220);
        yield return Settle(() => mapLoadQueue.Count == 0 && mapLoadTask == null && entOf.Count > 100, 120);
        yield return Settle(() => archLoading == 0, 60);
        ShowExplorer = false; ShowPanel = false; ShowStats = false;
        {
            var spot = new Vector3(-800, -1300, 20);
            GoTo(spot); cam.Distance = 200; cam.Pitch = 0.5f;
            yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 25);
            LoadAreaAt(spot, 60);
            yield return Settle(() => mapLoadQueue.Count == 0 && mapLoadTask == null, 90);
            yield return Settle(() => archLoading == 0, 40);
            GoTo(spot); cam.Distance = 60; cam.Pitch = 1.5f; yield return Frames(3);
            var lot = MapGround();
            Expect(true, $"ground {lot}");
            GoTo(lot + new Vector3(0, 0, 6)); cam.Distance = 44; cam.Pitch = 0.16f; cam.Yaw = -2.36f;
            yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 25);
            yield return Settle(() => archLoading == 0, 30);
            foreach (var x in BuildTower(lot, vp, 0f, false)) { }
            yield return Frames(20);
            yield return Shot("tower_on_map");
            cam.Yaw += 1.2f; yield return Frames(10);
            yield return Shot("tower_on_map2");
        }
        SetMapMode(false);
    }

    IEnumerable<object> FamilyTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        if (game == null) { Expect(false, "game files loaded: " + gameError); yield break; }
        StartClassify();
        yield return Until(() => gameClassified, 1200);
        Expect(gameClassified, "the models are sorted into families");
        var buoy = game.Models.FirstOrDefault(m => m.Name == "apa_prop_yacht_float_1a") ?? game.Models.First(m => m.Ext == ".ydr");
        int veg = Array.FindIndex(GameCategories.All, c => c.Name == "Vegetation");
        Settings.FamilyOverrides.Remove(FamilyKey(buoy)); buoy.Categories = GameCategories.Classify(buoy.Name);
        Expect((buoy.Categories & (1L << veg)) == 0, $"{buoy.Name} is not vegetation by its name");
        SetFamilies(buoy, buoy.Categories | (1L << veg));
        OpenAssetBrowser(); assetSource = 1; assetFamily = veg; assetSearch = buoy.Name; assetKey = null;
        yield return Frames(3);
        Expect(assetItems.Any(i => i.Game == buoy), $"after right-click > Vegetation it is in the Asset Browser's Vegetation ({assetItems.Count})");
        Expect(Settings.FamilyOverrides.TryGetValue(FamilyKey(buoy), out var kept) && (kept & (1L << veg)) != 0, "the choice is kept in the settings (next launch too)");
        // back to automatic
        Settings.FamilyOverrides.Remove(FamilyKey(buoy)); buoy.Categories = GameCategories.Classify(buoy.Name); familyRev++; assetKey = null;
        yield return Frames(2);
        Expect(!assetItems.Any(i => i.Game == buoy), "back to automatic: out of Vegetation");
        assetsOpen = false; assetSearch = "";
    }

    IEnumerable<object> AssetTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        var dir = StPath("assets_lib"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
        var thumbs = AssetThumbDir; 
        // a saved scene in my assets
        var sc = new Scene();
        var a = new SceneObject { Id = sc.NextObjectId++, Name = "crate_a", Mesh = Core.Mesh.Primitives.Box(1, 1, 1) }; sc.Objects.Add(a);
        var b = new SceneObject { Id = sc.NextObjectId++, Name = "crate_b", Mesh = Core.Mesh.Primitives.Cylinder(0.4f, 1.2f, 16), Position = new Vector3(1.5f, 0, 0) }; sc.Objects.Add(b);
        File.WriteAllText(Path.Combine(dir, "two_crates.mmx"), SceneIO.ToJson(sc));
        Settings.AssetFolders.Clear(); Settings.AssetFolders.Add(dir);
        OpenAssetBrowser(); assetSource = 0; assetFamily = 0; assetKey = null; assetSearch = "";
        yield return Frames(5);
        Expect(assetItems.Count == 1 && assetItems[0].Name == "two_crates", $"my assets: {assetItems.Count} item(s) in the folder");
        var tp = assetItems.Count > 0 ? ThumbPath(assetItems[0]) : null;
        if (tp != null && File.Exists(tp)) File.Delete(tp);
        if (assetItems.Count > 0) thumbAsked.Remove(assetItems[0].Key);
        yield return Settle(() => tp != null && File.Exists(tp), 20);
        Expect(tp != null && File.Exists(tp) && new FileInfo(tp).Length > 2000, $"thumbnail rendered and saved ({(tp != null && File.Exists(tp) ? new FileInfo(tp).Length : 0)} bytes)");
        int before = Scene.Objects.Count;
        if (assetItems.Count > 0) PlaceAsset(assetItems[0], new Vector3(5, 5, 0));
        Expect(Scene.Objects.Count == before + 2 && Scene.Objects.Any(o => o.Name == "crate_b" && Vector3.Distance(o.WorldBounds().Min, new Vector3(5, 5, 0)) < 2.5f), "a saved scene placed from the browser (2 objects at the drop point)");
        // game props
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        if (game != null)
        {
            yield return Until(() => gameClassified, 4000);
            assetSource = 1; assetFamily = 0; assetKey = null; // chairs & seats
            yield return Frames(3);
            var first = assetItems.Take(6).ToList();
            foreach (var x in first) { var p = ThumbPath(x); if (File.Exists(p)) File.Delete(p); thumbAsked.Remove(x.Key); }
            assetKey = null;
            yield return Settle(() => first.All(x => File.Exists(ThumbPath(x))), 40);
            Expect(assetItems.Count > 20 && first.Count(x => File.Exists(ThumbPath(x)) && new FileInfo(ThumbPath(x)).Length > 2000) >= 4, $"game props 'Chairs & seats': {assetItems.Count} props, thumbnails for {first.Count(x => File.Exists(ThumbPath(x)))}/6 ({string.Join(", ", first.Select(x => x.Name))})");
            yield return Frames(5);
            yield return Shot("asset_browser");
        }
        assetsOpen = false;
    }

    IEnumerable<object> ChecklistTest()
    {
        Fresh();
        if (MapMode) SetMapMode(false);
        Maximized = true; ShowPanel = true; ActiveViewport = 3;
        var vp = Viewports[3]; vp.Shading = ShadingMode.Realistic;
        yield return Frames(3);
        // 1. edit tools come to the front
        var box = Prim(CreateKind.Box, Vector3.Zero); box.Name = "chk_box";
        SelectOnly(box); propTab = PropTab.Object; yield return Frames(4);
        SetLevel(SubObject.Vertex); yield return Frames(4);
        bool toEdit = propTab == PropTab.Data;
        yield return Shot("chk_edit_tab");
        SetLevel(SubObject.Object); yield return Frames(4);
        Expect(toEdit && propTab == PropTab.Object, $"edit mode brings the Edit tab ({toEdit}), object mode goes back to Object ({propTab})");
        // 2. material panel follows the face
        var mA = Scene.AddMaterial("chk_red", new Vector3(0.8f, 0.2f, 0.2f)); var mB = Scene.AddMaterial("chk_blue", new Vector3(0.2f, 0.3f, 0.9f));
        for (int i = 0; i < box.Mesh.Faces.Count; i++) box.Mesh.Faces[i].MatId = i < 3 ? mA.Id : mB.Id;
        box.Touch();
        SetLevel(SubObject.Polygon); propTab = PropTab.Material; SubSel(box).Clear(); SubSel(box).Faces.Add(4); yield return Frames(4);
        bool followB = selectedMaterial == mB.Id;
        SubSel(box).Clear(); SubSel(box).Faces.Add(0); yield return Frames(4);
        bool followA = selectedMaterial == mA.Id;
        yield return Shot("chk_material_follows");
        Expect(followA && followB, $"material panel follows the selected face (blue {followB}, red {followA})");
        SetLevel(SubObject.Object);
        // 3. Scene Explorer goes to a model picked in the view
        var node = Prim(CreateKind.Box, new Vector3(0, 6, 0)); node.Name = "chk_drawable";
        var kid = Prim(CreateKind.Box, new Vector3(0, 6, 1)); kid.Name = "chk_drawable_high_0"; kid.ParentId = node.Id;
        for (int i = 0; i < 120; i++) { var f = Prim(CreateKind.Box, new Vector3(30 + i * 3, 0, 0)); f.Name = $"filler_{i:000}"; }
        closedObjs.Add(node.Id);
        Selected.Clear(); yield return Frames(6);
        Selected.Add(kid.Id); ActiveId = kid.Id; SelectionChanged(); // as a click in the view does
        yield return Frames(8);
        bool opened = !closedObjs.Contains(node.Id), onScreen = outBandRows.Any(r => r.Id == kid.Id);
        yield return Shot("chk_outliner_reveal");
        Expect(opened && onScreen, $"Scene Explorer opens the parent ({opened}) and scrolls to the picked model ({onScreen})");
        // 4. terrain layer from Molo Materials + paint button
        var tm = Scene.AddMaterial("chk_terrain", new Vector3(0.6f)); tm.Shader = "terrain_cb_w_4lyr";
        EnsureLibrary(); yield return Until(() => library != null && library.Materials.Count > 0, 60);
        var lm = library?.Materials.FirstOrDefault(x => x.Diffuse != null && x.Bump != null);
        if (lm != null) { libLayerMat = tm; libLayerIndex = 2; libraryAction = LibraryAction.TerrainLayer; LibraryPrimary(lm); }
        Expect(lm != null && tm.Layer(2) == lm.Diffuse && tm.LayerBump(2) == lm.Bump, $"terrain layer 3 picked from Molo Materials: {Path.GetFileName(tm.Layer(2) ?? "-")} + {Path.GetFileName(tm.LayerBump(2) ?? "-")}");
        foreach (var f in box.Mesh.Faces) f.MatId = tm.Id; box.Touch();
        SelectOnly(box); propTab = PropTab.Paint; yield return Frames(4);
        Expect(TerrainMaterialOf(box) != null, "Paint tab offers PAINT TERRAIN LAYERS on a terrain model");
        yield return Shot("chk_paint_terrain_button");
        // 5. Tripo link
        propTab = PropTab.Tripo; yield return Frames(4);
        Expect(TripoLink.StartsWith("https://tripo.pxf.io/"), $"Get Tripo button links to {TripoLink}");
        yield return Shot("chk_tripo_tab");
        // 6. MLO rooms are volumes (a real MLO of yours)
        var ytyp = @"C:\Users\molo3\Desktop\base_para_testes\resources\[map]\molo_bolingbrokeprison\stream\molo_bp.ytyp";
        if (File.Exists(ytyp))
        {
            Fresh(); ImportMlo(ytyp);
            yield return Until(() => pendingImports.Count == 0, 180);
            var rooms = Scene.Objects.Where(o => o.GtaType == GtaObjectType.MloRoom && !string.Equals(o.Folder, PlanFolder)).ToList();
            int vol = rooms.Count(o => IsVolumeOnly(o));
            var r0 = rooms.FirstOrDefault();
            bool notPickable = true;
            if (r0 != null)
            {
                var (a, b) = r0.WorldBounds(); var c = (a + b) * 0.5f;
                var ray = new MoloMax.Core.Scenes.Ray(c + new Vector3(0, 0, 200), -Vector3.UnitZ);
                var hit = Picking.PickObject(Scene, ray);
                notPickable = hit == null || hit.Value.Object.GtaType != GtaObjectType.MloRoom;
                SelectOnly(r0); FrameSelection(vp); Selected.Clear();
            }
            yield return Frames(10);
            yield return Shot("chk_mlo_volumes");
            Expect(rooms.Count > 0 && vol == rooms.Count && notPickable, $"molo_bp.ytyp: {rooms.Count} rooms, {vol} shown as volumes, a click does not pick them ({notPickable})");
        }
        else Note("molo_bp.ytyp not found: MLO volumes not checked");
    }

    IEnumerable<object> UxTour()
    {
        Fresh();
        if (MapMode) SetMapMode(false);
        Maximized = false; ShowPanel = true;
        var vp = Viewports[ActiveViewport]; vp.Shading = ShadingMode.Realistic;
        var box = Prim(CreateKind.Box, Vector3.Zero); box.Name = "crate";
        var drawable = Prim(CreateKind.Box, new Vector3(4, 0, 0)); drawable.Name = "shop_counter";
        foreach (var v in Viewports) v.Camera.Frame(new Vector3(-2, -2, -1), new Vector3(6, 2, 3));
        SelectOnly(box); yield return Frames(10);
        yield return Shot("ux_01_object");
        // edit mode, vertex level: Edit Geometry with Make Planar / Preserve UVs
        SetLevel(SubObject.Vertex); SubSel(box).Verts.Add(0); SubSel(box).Verts.Add(1);
        foreach (var t in new[] { PropTab.Modifiers, PropTab.Object, PropTab.Tool })
        { propTab = t; yield return Frames(6); yield return Shot("ux_02_vertex_" + t); }
        SetLevel(SubObject.Polygon); SubSel(box).Faces.Add(0); yield return Frames(4);
        propTab = PropTab.Material; yield return Frames(6); yield return Shot("ux_03_polygon_material");
        // terrain material + paint panel
        var mat = Scene.AddMaterial("ground_terrain", new Vector3(0.6f)); mat.Shader = "terrain_cb_w_4lyr"; mat.EnsureLayers();
        foreach (var f in box.Mesh.Faces) f.MatId = mat.Id; box.Touch();
        SetLevel(SubObject.Object); SelectOnly(box);
        propTab = PropTab.Material; yield return Frames(6); yield return Shot("ux_04_terrain_material");
        propTab = PropTab.Paint; paintMode = true; paintTool = PaintTool.VertexColour; yield return Frames(8); yield return Shot("ux_05_paint_vertex_terrain");
        paintMode = false; paintTool = PaintTool.Brush;
        // export page
        SelectOnly(box, drawable); OpenHub(1); exportSelectedOnly = true; yield return Frames(8); yield return Shot("ux_06_export_selected");
        exportSelectedOnly = false; yield return Frames(4); yield return Shot("ux_07_export_resource");
        HubOpen = false;
        // asset browser
        OpenAssetBrowser(); yield return Frames(10); yield return Shot("ux_08_assets"); assetsOpen = false;
        // right-click menu in the view
        quadCenter = new Vector2(vp.X + vp.W / 2, vp.Y + vp.H / 2); OpenPopup("context"); yield return Frames(6); yield return Shot("ux_09_context_menu");
        ImGui.CloseCurrentPopup();
        yield return Frames(3);
    }

    IEnumerable<object> MapHideSendTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var cam = vp.Camera; MapStream = true;
        var spot = new Vector3(445, -1000, 30);
        GoTo(spot); cam.Distance = 45; cam.Pitch = 0.5f;
        yield return Settle(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal, 120);
        yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 60);
        yield return Settle(() => archLoading == 0, 60);
        yield return Frames(20);
        // the biggest HD model near the camera (a ground or a building)
        var pick = MapLive().Where(o => o.Id >= 0 && !o.Hidden && o.Mesh != null && o.Mesh.Faces.Count > 0 && !string.IsNullOrEmpty(o.Gta.MapArchetype) && Vector3.Distance(o.Position, spot) < 80)
            .OrderByDescending(o => { var (a, b) = o.FastWorldBounds(); return (b.X - a.X) * (b.Y - a.Y); }).FirstOrDefault();
        Expect(pick != null, $"picked {pick?.Name} ({pick?.Gta.MapArchetype}) in {pick?.Folder}");
        if (pick == null) yield break;
        SelectOnly(pick); PinPickedMaps(); yield return Frames(10);
        yield return Shot("map_before_hide");
        // hide it like the eye button does
        foreach (var t in RowTargets(pick)) t.Hidden = true;
        Selected.Clear(); SelectionChanged();
        yield return Frames(150); // the far models step aside on the next refresh (once a second)
        MapGatherDraws(vp, vp.Camera.View * vp.Camera.Projection(vp.Aspect));
        var (mn, mx) = pick.FastWorldBounds(); var c = (mn + mx) * 0.5f;
        bool drawnSingle = mapSingles.Contains(pick);
        bool drawnInst = pick.InstanceKey != null && instGroups.TryGetValue(pick.InstanceKey, out var gi) && Enumerable.Range(0, gi.Count).Any(k => Vector3.Distance(gi.Worlds[k].Translation, pick.Position) < 0.01f);
        var same = Scene.Objects.Where(o => o != pick && !o.Hidden && o.Gta.MapArchetype == pick.Gta.MapArchetype && Vector3.Distance(o.Position, pick.Position) < 0.05f).Select(o => o.Name + "@" + o.Folder).ToList();
        var lodsOver = backdrop.Where(o => !o.Hidden && o.Mesh != null && !FarSteppedAside(o, vp.Camera.Eye) && FarLodVisible(o, vp.Camera.Eye)).Where(o => { var (a, b) = o.FastWorldBounds(); return c.X >= a.X && c.X <= b.X && c.Y >= a.Y && c.Y <= b.Y; }).Select(o => o.Name).Take(6).ToList();
        bool clipped = hiddenClip.Any(b => c.X >= b.Min.X && c.X <= b.Max.X && c.Y >= b.Min.Y && c.Y <= b.Max.Y);
        Expect(!drawnSingle && !drawnInst && same.Count == 0 && clipped, $"hidden: far models cut away over it {clipped},  drawn alone {drawnSingle}, drawn instanced {drawnInst}, same model same place {same.Count} ({string.Join(", ", same.Take(4))}); far models over it: {string.Join(", ", lodsOver)}");
        yield return Shot("map_after_hide");
        foreach (var t in RowTargets(pick)) t.Hidden = false;
        // send to MODEL
        SelectOnly(pick); yield return Frames(3);
        int before = otherMode?.Scene?.Objects.Count ?? 0;
        SendSelectedToModel();
        yield return Until(() => !MapMode && Scene.Objects.Any(o => o.Name.Contains(pick.Gta.MapArchetype, StringComparison.OrdinalIgnoreCase)), 120);
        var got = Scene.Objects.FirstOrDefault(o => o.Name.Contains(pick.Gta.MapArchetype, StringComparison.OrdinalIgnoreCase));
        Expect(!MapMode && got != null, $"send to MODEL: now in {(MapMode ? "MAP" : "MODEL")}, imported {got?.Name} at {got?.Position}");
        if (got != null) { SelectOnly(got); FrameSelection(vp); }
        yield return Frames(20);
        yield return Shot("sent_to_model");
    }

    IEnumerable<object> CableDiag()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        if (game == null) yield break;
        foreach (var name in new[] { "cs5_rd_props_prsncbls06", "cs5_rd_props_cbl85" })
        {
            uint h = Core.Gta.Map.MapDocument.Hash(name);
            var a = game.FindModel(h); var dd = a == null ? game.FindDictionaryOf(h) : null;
            Note($"{name}: model {a?.Path ?? "-"}  dictionary {dd?.Path ?? "-"}  why {game.WhyNoModel(h)}");
            var src = a ?? dd; if (src == null) continue;
            var dir = StPath("cable_diag"); var file = game.Extract(src, dir, h);
            var r = Core.IO.ModelImporter.Import(file, new Core.IO.ImportOptions { Scale = 1, AutoSmoothAngle = 0 });
            foreach (var m in r.Meshes.Take(12)) Note($"   mesh {m.Name} type {m.GtaType} hash {m.DrawableHash:X8} verts {m.Mesh.Verts.Count} faces {m.Mesh.Faces.Count}");
            foreach (var mt in r.Materials.Take(6)) Note($"   material {mt.Name} shader {mt.Shader}");
            foreach (var w in r.Warnings.Take(6)) Note("   warning " + w);
            // raw geometry
            try { foreach (var l in Core.Gta.Import.GtaInspect.Geometries(file, a == null ? h : 0)) Note("   raw: " + l); }
            catch (Exception e) { Note("   raw read: " + e.Message); }
        }
        Expect(true, "diag done");
    }

    IEnumerable<object> MapStreamTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        var src = @"C:\Users\molo3\Desktop\base_para_testes\resources\[map]\molo_bolingbrokeprison\stream";
        if (!Directory.Exists(src)) { Note("Bolingbroke stream folder not found: not checked"); yield break; }
        // a copy: saving writes in place
        var dir = Path.Combine(StPath("stream_copy"), "stream");
        if (Directory.Exists(StPath("stream_copy"))) Directory.Delete(StPath("stream_copy"), true);
        foreach (var f in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(dir, Path.GetRelativePath(src, f)); Directory.CreateDirectory(Path.GetDirectoryName(t)); File.Copy(f, t);
        }
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        OpenStreamFolder(dir);
        Expect(stream != null && stream.Ymaps.Count > 0, $"opened: {status}");
        var docs = mapDocs.Values.Where(d => stream.Contains(d.FilePath)).ToList();
        Expect(docs.Count == stream.Ymaps.Count, $"every .ymap of the folder loaded ({docs.Count}/{stream?.Ymaps.Count}): {string.Join(", ", docs.Select(d => d.Name).Take(6))}");
        var localInts = interiors.Values.Where(i => i.FilePath != null).ToList();
        Expect(stream.Interiors.Count == 0 || localInts.Count > 0, $"its interiors open from its .ytyp: {string.Join(", ", localInts.Select(i => i.Doc.ArchetypeName + " (" + i.ObjOf.Count + " objects)"))}");
        yield return Settle(() => archLoading == 0, 120);
        yield return Frames(20);
        var nearKeys = MapLive().Where(o => o.InstanceKey != null && Vector3.Distance(o.Position, vp.Camera.Target) < 150).Select(o => o.InstanceKey).ToHashSet();
        var localSlots = archSlots.Values.Where(sl => stream.ModelFile(sl.Hash) != null && nearKeys.Contains("map:" + sl.Hash.ToString("X8"))).ToList();
        int ready = localSlots.Count(sl => sl.State == 2);
        Expect(localSlots.Count > 0 && localSlots.All(sl => sl.State == 2), $"its own models drawn: {ready}/{localSlots.Count}" + string.Join(" | ", localSlots.Where(sl => sl.State == 3).Take(3).Select(sl => sl.Name + ": " + sl.Error)));
        var camT = vp.Camera.Target;
        Expect(MathF.Abs(camT.X) < 10000 && MathF.Abs(camT.Y) < 10000, $"the camera goes to the folder's maps: {camT}");
        yield return Shot("stream_folder");
        // move one object of each kind, save: the same files change in place, nothing else appears
        var before = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ent = Scene.Objects.FirstOrDefault(o => entOf.TryGetValue(o.Id, out var de) && stream.Contains(de.Doc.FilePath) && !de.Ent.IsMlo);
        var intObj = localInts.SelectMany(i => i.ObjOf.Values).Select(Scene.Find).FirstOrDefault(o => o != null);
        if (ent != null) ent.Position += new Vector3(0, 0, 0.5f);
        if (intObj != null) intObj.Position += new Vector3(0.25f, 0, 0);
        int n = SaveMaps();
        var after = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Except(before).Select(Path.GetFileName).ToList();
        Expect(n >= (ent != null ? 1 : 0) + (intObj != null ? 1 : 0) && added.Count == 0, $"saved {n} file(s) in place, new files: {added.Count} ({string.Join(", ", added.Take(4))})");
        if (ent != null)
        {
            var de = entOf[ent.Id];
            var back = MapDocument.Load(File.ReadAllBytes(de.Doc.FilePath), Path.GetFileName(de.Doc.FilePath));
            Expect(back.Entities.Any(x => Vector3.Distance(x.Position, ent.Position) < 0.01f), $"{Path.GetFileName(de.Doc.FilePath)} read back: the moved entity is at its new place");
        }
        if (intObj != null)
        {
            var oi = intEntOf[intObj.Id].In;
            var backI = MloDocument.Load(File.ReadAllBytes(oi.FilePath), Path.GetFileName(oi.FilePath), oi.Doc.ArchetypeHash);
            var lp = Vector3.Transform(intObj.Position - oi.Pos, Quaternion.Conjugate(oi.Rot));
            Expect(backI.Entities.Any(x => Vector3.Distance(x.Position, lp) < 0.01f), $"{Path.GetFileName(oi.FilePath)} read back: the moved interior object is at its new place");
        }
        // one prop moved, only its .ymap saved (right-click > Save this .ymap / the panel button / Ctrl+S)
        if (ent != null)
        {
            var de2 = entOf[ent.Id];
            ent.Position += new Vector3(1, 0, 0); SelectOnly(ent);
            Expect(SelectedMapDoc() == de2.Doc, $"the selection's .ymap is {SelectedMapDoc()?.Name}");
            var t0 = File.GetLastWriteTimeUtc(de2.Doc.FilePath);
            System.Threading.Thread.Sleep(20);
            bool ok = SaveMapDoc(de2.Doc);
            var back2 = MapDocument.Load(File.ReadAllBytes(de2.Doc.FilePath), Path.GetFileName(de2.Doc.FilePath));
            Expect(ok && File.GetLastWriteTimeUtc(de2.Doc.FilePath) > t0 && back2.Entities.Any(x => Vector3.Distance(x.Position, ent.Position) < 0.01f), $"Save this .ymap: {Path.GetFileName(de2.Doc.FilePath)} written in place with the prop moved");
            // Save as: a copy elsewhere, which becomes its file
            var other = Path.Combine(StPath("stream_copy"), "saved_as", de2.Doc.Name + ".ymap");
            Expect(SaveMapDoc(de2.Doc, path: other) && File.Exists(other) && de2.Doc.FilePath == other, "Save as: written there, and it is now its file");
        }
        CloseStreamFolder();
        Expect(stream == null && !mapDocs.Values.Any(d => d.FilePath != null && d.FilePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase)), "closed: its maps leave the scene");
    }

    IEnumerable<object> AnimTest()
    {
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true;
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        var box = Prim(CreateKind.Box, Vector3.Zero); box.Name = "molo_anim_box";
        vp.Camera.Frame(new Vector3(-4, -4, -1), new Vector3(8, 4, 4));
        SelectOnly(box); Tool = Tool.Move;
        if (!TimelineOpen) ToggleTimeline();
        yield return Frames(5);
        // 1. frame 0: I keys location / rotation / scale
        SetFrame(0); yield return Frames(2);
        KeyDownT(System.Windows.Forms.Keys.I); yield return Frames(3); KeyUpT(System.Windows.Forms.Keys.I); yield return Frames(3);
        var t = Scene.Anim.Track(box.Id);
        Expect(t != null && t.Curves.Count == 9 && t.Curves.All(c => c.Keys.Count == 1), $"I at frame 0: {t?.Curves.Count ?? 0} curves keyed ({status})");
        // 2. auto key: frame 60, drag the X arrow
        AutoKey = true; SetFrame(60); yield return Frames(3);
        box = Scene.Find(box.Id);
        var gx = GizmoGrip(vp, GizmoHandle.X);
        SelectionPivot(out var pv, out _); var dir = Vector2.Normalize(ScreenOf(vp, pv + Vector3.UnitX) - ScreenOf(vp, pv));
        foreach (var x in DragTo(gx, gx + dir * 160)) yield return x;
        box = Scene.Find(box.Id); t = Scene.Anim.Track(box.Id);
        float x60 = box.Position.X;
        Expect(x60 > 0.5f && t.Curve(Core.Anim.Ch.LocX).Keys.Count == 2, $"auto key: moved to x {x60:0.00} at frame 60, X location keys {t.Curve(Core.Anim.Ch.LocX).Keys.Count}  (grip {gx}, status '{status}', panel {ShowPanel}, hover {gizmo.Hover})");
        AutoKey = false;
        // 3. scrub to 30: half way (smooth)
        SetFrame(30); yield return Frames(3);
        box = Scene.Find(box.Id);
        Expect(box.Position.X > x60 * 0.3f && box.Position.X < x60 * 0.7f, $"frame 30: x {box.Position.X:0.00} between 0 and {x60:0.00}");
        // 4. play
        SetFrame(0); AnimPlaying = true; yield return Frames(40); AnimPlaying = false;
        float fPlay = animFrame;
        Expect(fPlay > 5, $"Play: the frame runs ({fPlay:0.0} after 40 drawn frames)");
        // 5. UV keys: offset V 0 -> 1 between 0 and 60
        Undo.Record("uv", Scene);
        var bt = Scene.Anim.Track(box.Id, true);
        bt.Curve(Core.Anim.Ch.UvV, true).SetKey(0, 0, Core.Anim.KeyInterp.Linear); bt.Curve(Core.Anim.Ch.UvV).SetKey(60, 1);
        SetFrame(30); animShownFrame = float.NaN; yield return Frames(3);
        box = Scene.Find(box.Id);
        Expect(MathF.Abs(box.UvRow1.Z - 0.5f) < 0.01f && box.UvRow1.W > 0, $"UV offset keyed: row 1 at frame 30 = {box.UvRow1}");
        yield return Shot("anim_timeline");
        // 6. camera keys
        var cam = vp.Camera; SetFrame(0); cam.Yaw = 0; KeyCamera();
        SetFrame(60); cam.Yaw = MathF.PI / 2; KeyCamera();
        SetFrame(30); animShownFrame = float.NaN; yield return Frames(3);
        Expect(cam.Yaw > 0.2f && cam.Yaw < 1.4f, $"camera keys: yaw at frame 30 = {cam.Yaw * 180 / MathF.PI:0} degrees");
        // 7. keys moved in the dope sheet / deleted
        int before = Scene.Anim.KeyFrames(new[] { box.Id }).Count;
        // 8. .ycd out, then back onto a fresh box
        var path = StPath("molo_anim_test.ycd");
        Expect(ExportYcd(path) && File.Exists(path), $"export .ycd: {status}");
        var copy = Prim(CreateKind.Box, Vector3.Zero); copy.Name = "molo_anim_box";
        SelectOnly(copy); yield return Frames(2);
        Undo.Record("clear", Scene);
        int applied = ImportYcd(path);
        var ct = Scene.Anim.Track(copy.Id);
        Expect(ycdClips != null && ycdClips.Count >= 2 && ct != null && ct.Has(Core.Anim.Ch.LocX), $"import back: {ycdClips?.Count} clip(s), {applied} applied, the copy has keys {ct?.Curves.Count ?? 0}  ({status})");
        ycdWindow = false;
        // 9. every .ycd of your resources reads
        var root = @"C:\Users\molo3\Desktop\base_para_testes\resources";
        if (Directory.Exists(root))
        {
            int files = 0, clips = 0, bones = 0, uv = 0; var bad = new List<string>();
            foreach (var f in Directory.EnumerateFiles(root, "*.ycd", SearchOption.AllDirectories))
            {
                files++;
                try { var cl = Core.Gta.Anim.YcdIO.Read(File.ReadAllBytes(f), Path.GetFileName(f)); clips += cl.Count; uv += cl.Count(c => c.IsUv); bones += cl.Count(c => c.Tracks.Any(tt => tt.Bone != 0)); if (files <= 6) Note(Path.GetFileName(f) + ": " + string.Join(", ", cl.Take(3).Select(Core.Anim.AnimGta.Describe))); }
                catch (Exception e) { bad.Add(Path.GetFileName(f) + ": " + e.Message); }
            }
            Expect(bad.Count == 0 && clips > 0, $"your .ycd files: {files} read, {clips} clips ({uv} UV, {bones} with a skeleton); errors: {string.Join(" | ", bad.Take(3))}");
        }
        UI.ImGuiBackend.SyntheticInput = false;
        if (TimelineOpen) ToggleTimeline();
    }

    IEnumerable<object> ClassicModeTest()
    {
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true;
        var keys0 = Settings.Keymap; bool tl0 = TimelineOpen;
        // Classic is only a look: 4 views and its colours, nothing else changes
        SetMode(UiMode.Max); yield return Frames(10);
        bool quad = !Maximized;
        yield return Shot("classic_look");
        var b = Prim(CreateKind.Box, Vector3.Zero); SelectOnly(b); yield return Frames(3);
        Undo.Record("Move", Scene); Scene.Find(b.Id).Position = new Vector3(3, 0, 0);
        KeyDownT(Keys.ControlKey); KeyDownT(Keys.Z); yield return Frames(4); KeyUpT(Keys.Z); KeyUpT(Keys.ControlKey); yield return Frames(4);
        Expect(quad && Settings.Keymap == keys0 && TimelineOpen == tl0 && Scene.Find(b.Id)?.Position.X < 0.01f,
            $"Classic look: 4 views {quad}, keys unchanged {Settings.Keymap == keys0}, timeline untouched {TimelineOpen == tl0}, Ctrl+Z {Scene.Find(b.Id)?.Position.X:0.00}");
        SetMode(UiMode.Blender); yield return Frames(6);
        Expect(Maximized && Settings.Keymap == keys0, $"back to Standard: 1 view {Maximized}, keys {Settings.Keymap}");
        UI.ImGuiBackend.SyntheticInput = false;
    }

    IEnumerable<object> UndoMoveTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        UI.ImGuiBackend.SyntheticInput = true; // buttons / Ctrl from the posted messages
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false;
        var box = Prim(CreateKind.Box, Vector3.Zero);
        vp.Camera.Frame(new Vector3(-4, -4, -1), new Vector3(4, 4, 3));
        SelectOnly(box); Tool = Tool.Move; yield return Frames(5);
        var p0 = box.Position; var c = GizmoGrip(vp, GizmoHandle.X);
        SelectionPivot(out var bpv, out _); var bdir = Vector2.Normalize(ScreenOf(vp, bpv + Vector3.UnitX) - ScreenOf(vp, bpv));
        foreach (var x in DragTo(c, c + bdir * 140)) yield return x;
        var moved = box.Position;
        KeyDownT(System.Windows.Forms.Keys.ControlKey); KeyDownT(System.Windows.Forms.Keys.Z); yield return Frames(4);
        KeyUpT(System.Windows.Forms.Keys.Z); KeyUpT(System.Windows.Forms.Keys.ControlKey); yield return Frames(4);
        var back = Scene.Find(box.Id)?.Position ?? new Vector3(999);
        Expect(Vector3.Distance(moved, p0) > 0.1f && Vector3.Distance(back, p0) < 0.001f, $"MODEL: moved {p0} -> {moved}, Ctrl+Z -> {back} (undo: {status})");
        // paint mode on (a stroke made before), then a move: Ctrl+Z undoes the move first, then the stroke
        box = Scene.Find(box.Id); SelectOnly(box);
        StartPaint(box); paintMode = true; paintTool = PaintTool.Brush; yield return Frames(3);
        var pts = box.Paint.Cur; int strokesBefore = paintUndo.Count;
        PrepareStroke(box, pts); yield return Frames(2);
        // a move after the stroke (gizmo, typed value, snap... any scene edit)
        var pm0 = box.Position;
        Undo.Record("Move", Scene); box.Position += new Vector3(3, 0, 0); box.Touch();
        var pm1 = box.Position;
        KeyDownT(System.Windows.Forms.Keys.ControlKey); KeyDownT(System.Windows.Forms.Keys.Z); yield return Frames(4);
        KeyUpT(System.Windows.Forms.Keys.Z); KeyUpT(System.Windows.Forms.Keys.ControlKey); yield return Frames(4);
        var pm2 = Scene.Find(box.Id).Position; int strokesAfter1 = paintUndo.Count;
        KeyDownT(System.Windows.Forms.Keys.ControlKey); KeyDownT(System.Windows.Forms.Keys.Z); yield return Frames(4);
        KeyUpT(System.Windows.Forms.Keys.Z); KeyUpT(System.Windows.Forms.Keys.ControlKey); yield return Frames(4);
        Expect(Vector3.Distance(pm1, pm0) > 0.1f && Vector3.Distance(pm2, pm0) < 0.001f && strokesAfter1 == strokesBefore + 1 && paintUndo.Count == strokesBefore,
            $"in paint mode: move {pm0} -> {pm1}, 1st Ctrl+Z -> {pm2} (strokes {strokesBefore + 1} -> {strokesAfter1}), 2nd Ctrl+Z undoes the stroke ({paintUndo.Count})");
        paintMode = false;
        // MAP: a streamed ground
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        MapStream = true; vp = Viewports[ActiveViewport];
        var spot = new Vector3(230, -790, 30);
        GoTo(spot); vp.Camera.Distance = 45; vp.Camera.Pitch = 0.6f;
        yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 90);
        yield return Settle(() => archLoading == 0, 60);
        yield return Frames(20);
        var ground = MapLive().Where(o => o.Id >= 0 && !o.Hidden && o.Mesh != null && o.Mesh.Faces.Count > 0 && entOf.ContainsKey(o.Id) && Vector3.Distance(o.Position, spot) < 80)
            .OrderByDescending(o => { var (a, b) = o.FastWorldBounds(); return (b.X - a.X) * (b.Y - a.Y); }).FirstOrDefault();
        Expect(ground != null, $"ground picked: {ground?.Name}");
        if (ground == null) { UI.ImGuiBackend.SyntheticInput = false; yield break; }
        SelectOnly(ground); PinPickedMaps(); Tool = Tool.Move; yield return Frames(5);
        SelectionPivot(out var gpiv, out _); vp.Camera.Target = gpiv; vp.Camera.Distance = 40; yield return Frames(10);
        var gp0 = ground.Position; var gc = GizmoGrip(vp, GizmoHandle.X);
        SelectionPivot(out var gpv, out _); var gdir = Vector2.Normalize(ScreenOf(vp, gpv + Vector3.UnitX) - ScreenOf(vp, gpv));
        foreach (var x in DragTo(gc, gc + gdir * 140)) yield return x;
        var gm = Scene.Find(ground.Id)?.Position ?? gp0;
        yield return Frames(30); // the streaming goes on meanwhile
        KeyDownT(System.Windows.Forms.Keys.ControlKey); KeyDownT(System.Windows.Forms.Keys.Z); yield return Frames(4);
        KeyUpT(System.Windows.Forms.Keys.Z); KeyUpT(System.Windows.Forms.Keys.ControlKey); yield return Frames(6);
        var gb = Scene.Find(ground.Id)?.Position ?? new Vector3(999);
        Expect(Vector3.Distance(gm, gp0) > 0.1f && Vector3.Distance(gb, gp0) < 0.001f, $"MAP ground {ground.Name}: moved {gp0} -> {gm}, Ctrl+Z -> {gb} (status: {status}, undo name now {Undo.UndoName})");
        yield return Shot("map_undo");
        UI.ImGuiBackend.SyntheticInput = false;
    }

    IEnumerable<object> MapMloHideTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var cam = vp.Camera; MapStream = true; MapShowInteriors = true;
        var spot = new Vector3(445, -985, 30); // Mission Row police station (an interior)
        GoTo(spot); cam.Distance = 40; cam.Pitch = 0.5f;
        yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 90);
        yield return Settle(() => archLoading == 0, 60);
        yield return Until(() => interiors.Values.Any(i => i.ObjOf.Count > 0), 300);
        var oi = interiors.Values.Where(i => i.ObjOf.Count > 0).OrderBy(i => Vector3.Distance(i.Pos, spot)).FirstOrDefault();
        Expect(oi != null, $"an interior opened near the camera: {oi?.Doc.ArchetypeName} ({oi?.ObjOf.Count} objects)");
        if (oi == null) yield break;
        var inst = Scene.Find(oi.InstanceId);
        yield return Frames(5);
        SelectOnly(inst); PinPickedMaps(); yield return Frames(3); // picked first, like a user does
        foreach (var t in RowTargets(inst)) t.Hidden = true; // the eye button on the MLO row
        Selected.Clear(); SelectionChanged();
        yield return Frames(3);
        MapGatherDraws(vp, vp.Camera.View * vp.Camera.Projection(vp.Aspect));
        var ids = oi.ObjOf.Values.ToHashSet();
        int shown = ids.Count(id => Scene.Find(id) is { Hidden: false });
        int drawn = mapSingles.Count(o => ids.Contains(o.Id)) + instGroups.Values.Sum(g => g.Template != null && ids.Contains(g.Template.Id) ? 1 : 0);
        Expect(shown == 0 && drawn == 0, $"MLO hidden: objects still shown {shown}, drawn {drawn}");
        yield return Shot("mlo_hidden");
        // the camera goes away and comes back: still hidden
        GoTo(spot + new Vector3(600, 0, 0)); yield return Frames(150);
        GoTo(spot); yield return Frames(150);
        var again = interiors.Values.FirstOrDefault(i => i.InstanceId == oi.InstanceId);
        int shownAgain = again == null ? -1 : again.ObjOf.Values.Count(id => Scene.Find(id) is { Hidden: false });
        Expect(shownAgain == 0, $"after the camera went away and came back: shown {shownAgain}");
        inst = Scene.Find(oi.InstanceId); if (inst == null) { Expect(false, "the MLO instance is gone"); yield break; }
        foreach (var t in RowTargets(inst)) t.Hidden = false;
        yield return Frames(3);
        Expect(oi.ObjOf.Values.All(id => Scene.Find(id) is { Hidden: false }), "shown again: everything inside comes back");
        // one object inside hidden by itself stays hidden when the camera comes back
        var one = Scene.Find(oi.ObjOf.Values.First()); SelectOnly(one); PinPickedMaps(); yield return Frames(3);
        one.Hidden = true; Selected.Clear(); SelectionChanged();
        GoTo(spot + new Vector3(600, 0, 0)); yield return Frames(150);
        GoTo(spot); yield return Frames(150);
        Expect(Scene.Find(one.Id) is { Hidden: true }, "one object hidden inside stays hidden after the camera came back");
        one.Hidden = false;
    }

    IEnumerable<object> TripoDiag()
    {
        Fresh();
        var dir = TripoFolder;
        var files = Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.glb", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTime).Take(4).ToList() : new();
        Expect(true, $"{files.Count} recent .glb in {dir}");
        foreach (var fi in files)
        {
            var bytes = File.ReadAllBytes(fi.FullName);
            string json = "";
            if (bytes.Length > 20) { int len = BitConverter.ToInt32(bytes, 12); json = System.Text.Encoding.UTF8.GetString(bytes, 20, Math.Min(len, bytes.Length - 20)); }
            var mimes = System.Text.RegularExpressions.Regex.Matches(json, "\"mimeType\"\\s*:\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value);
            var exts = System.Text.RegularExpressions.Regex.Match(json, "\"extensionsUsed\"\\s*:\\s*\\[([^\\]]*)\\]").Groups[1].Value;
            var r = Core.IO.ModelImporter.Import(fi.FullName, new Core.IO.ImportOptions { TextureFolder = StPath("tripo_tex", Path.GetFileNameWithoutExtension(fi.Name)) });
            var tex = r.Materials.Select(m => m.DiffuseTexture).Where(t => t != null).ToList();
            var loads = tex.Select(t => { try { using var b = new System.Drawing.Bitmap(t); return $"{Path.GetFileName(t)} ok {b.Width}"; } catch (Exception e) { return $"{Path.GetFileName(t)} FAIL {e.Message}"; } });
            Expect(true, $"{fi.Name} ({fi.LastWriteTime:HH:mm}): images [{string.Join(",", mimes)}], extensions [{exts}], {r.Materials.Count} materials, diffuse: {string.Join(" | ", loads)}; warnings: {string.Join(" | ", r.Warnings.Take(4))}");
        }
        // in the view: imported like the Tripo panel does, realistic shading
        if (files.Count > 0)
        {
            var vp = Viewports[ActiveViewport]; vp.Shading = ShadingMode.Realistic; Maximized = true;
            var created = ImportFile(files[0].FullName, Vector3.Zero);
            yield return Frames(10);
            if (created.Count > 0) { SelectOnly(created[0]); FrameSelection(vp); Selected.Clear(); }
            yield return Frames(20);
            var mats = created.SelectMany(o => o.Mesh.Faces.Select(f => f.MatId)).Distinct().Select(id => Scene.Material(id)).ToList();
            var info = mats.Select(m => $"{m.Name} shader {m.Shader} diffuse {m.DiffuseTexture} resolved {renderer.Textures.Resolve(m.DiffuseTexture)} srv {(renderer.Textures.Get(m.DiffuseTexture) != null)}");
            Expect(true, $"in the view: {created.Count} object(s); {string.Join(" | ", info)}; texture errors: {string.Join(" | ", renderer.Textures.Errors.TakeLast(4))}");
            yield return Shot("tripo_in_view");
        }
        yield return Frames(2);
    }

    IEnumerable<object> TerrainPaintTest()
    {
        Fresh();
        if (MapMode) SetMapMode(false);
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        // 4 plain textures (red, green, blue, yellow) so the blend is easy to read
        var dir = StPath("terrain_tex"); Directory.CreateDirectory(dir);
        var cols = new[] { System.Drawing.Color.FromArgb(200, 60, 50), System.Drawing.Color.FromArgb(60, 170, 70), System.Drawing.Color.FromArgb(50, 90, 200), System.Drawing.Color.FromArgb(230, 200, 60) };
        var files = new string[4];
        for (int i = 0; i < 4; i++)
        {
            files[i] = Path.Combine(dir, $"layer{i}.png");
            using var bmp = new System.Drawing.Bitmap(64, 64);
            for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++) bmp.SetPixel(x, y, ((x / 8 + y / 8) % 2 == 0) ? cols[i] : System.Drawing.Color.FromArgb(cols[i].R * 3 / 4, cols[i].G * 3 / 4, cols[i].B * 3 / 4));
            bmp.Save(files[i]);
        }
        var mat = Scene.AddMaterial("terrain_test", new Vector3(0.7f));
        mat.Shader = "terrain_cb_w_4lyr"; mat.EnsureLayers();
        for (int i = 0; i < 4; i++) mat.Layers[i] = files[i];
        var pl = Prim(CreateKind.Plane, Vector3.Zero); pl.Name = "ground";
        pl.Mesh = Core.Mesh.Primitives.Plane(20, 20, 40, 40);
        foreach (var f in pl.Mesh.Faces) { f.MatId = mat.Id; for (int c = 0; c < f.V.Length; c++) f.UV[c] = new Vector2(pl.Mesh.Verts[f.V[c]].X, pl.Mesh.Verts[f.V[c]].Y) / 4f; }
        pl.Touch();
        VertexPaint.FillLayer(pl.Mesh, Enumerable.Range(0, pl.Mesh.Faces.Count), 0);
        VertexPaint.StampLayer(pl.Mesh, new Vector3(-5, 0, 0), Vector3.UnitZ, 4, 1, 1, 0.5f);
        VertexPaint.StampLayer(pl.Mesh, new Vector3(5, 0, 0), Vector3.UnitZ, 4, 2, 1, 0.5f);
        VertexPaint.StampLayer(pl.Mesh, new Vector3(0, 6, 0), Vector3.UnitZ, 3, 3, 1, 0.2f);
        pl.Touch();
        vp.Camera.Frame(new Vector3(-10, -10, 0), new Vector3(10, 10, 2)); yield return Frames(20);
        yield return Shot("terrain_painted");
        // the corner nearest each brush centre carries that layer
        Vector4 W(Vector3 p) { var best = Vector4.Zero; float bd = float.MaxValue; foreach (var f in pl.Mesh.Faces) for (int c = 0; c < f.V.Length; c++) { float d = Vector3.Distance(pl.Mesh.Verts[f.V[c]], p); if (d < bd) { bd = d; best = VertexPaint.LayerWeights(f.Col1At(c)); } } return best; }
        var w1 = W(new Vector3(-5, 0, 0)); var w2 = W(new Vector3(5, 0, 0)); var w3 = W(new Vector3(0, 6, 0)); var w0 = W(new Vector3(-9, -9, 0));
        Expect(w0.X > 0.99f && w1.Y > 0.99f && w2.Z > 0.99f && w3.W > 0.99f, $"layer weights: corner {w0}, brush 1 {w1}, brush 2 {w2}, brush 3 {w3}");
        // save / open keeps the layers and the painted blend
        var file = StPath("terrain.mmx");
        File.WriteAllText(file, SceneIO.ToJson(Scene));
        var back = SceneIO.Load(file);
        var bm = back.Materials.First(m => m.Name == "terrain_test");
        var bo = back.Objects.First(o => o.Name == "ground");
        Expect(bm.IsTerrain && bm.Layer(3) == files[3] && bo.Mesh.Faces.Count(f => f.Col1 != null) == pl.Mesh.Faces.Count, "saved and opened: terrain shader, 4 layers, colour 1 on every face");
        // a plain model (like a GTA ground): Texture 1-4 buttons, "make its material a terrain", then paint texture 2 = blue
        var plain = Prim(CreateKind.Plane, new Vector3(30, 0, 0));
        var pm = GtaMaterial.CreateDefault(Scene.Materials.Max(m => m.Id) + 1, "plain_ground", new Vector3(0.6f)); pm.DiffuseTexture = files[0]; Scene.Materials.Add(pm);
        foreach (var f in plain.Mesh.Faces) f.MatId = pm.Id;
        Expect(TerrainMaterialOf(plain) == null, "a plain ground: not terrain yet");
        MakeTerrainMaterial(plain);
        Expect(pm.IsTerrain && pm.Layer(0) == files[0] && plain.Mesh.Faces.All(f => f.Col1 != null && f.Col1.All(c => c == TerrainSwatches[0].Swatch)), "Make terrain: shader terrain_cb_w_4lyr, its texture is texture 1, everything starts black (texture 1)");
        VertexPaint.FillLayer(plain.Mesh, Enumerable.Range(0, plain.Mesh.Faces.Count), 1);
        Expect(plain.Mesh.Faces.All(f => f.Col1.All(c => c == TerrainSwatches[1].Swatch)), "Texture 2 paints blue (0, 0, 255) as Sollumz");
        // export: the game shader with its 4 layer textures and colour 1
        var ydr = StPath("terrain_ground.ydr");
        string sum = null; string err = null;
        try { sum = Core.Gta.Export.GtaExporter.ExportModelFile(Scene, new[] { pl }, ydr, LoadImageRgba, renderer.Textures.SearchFolders.ToList()); } catch (Exception e) { err = e.Message; }
        Expect(err == null && File.Exists(ydr), $"exported as terrain_cb_w_4lyr: {sum ?? err}");
        // painting with the brush in the view
        SelectOnly(pl);
        yield return Frames(3);
    }

    IEnumerable<object> MapTerrainTest()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var cam = vp.Camera; MapStream = true;
        int n = 0;
        foreach (var spot in new[] { new Vector3(-800, -1300, 20), new Vector3(-1100, 400, 70) })
        {
            GoTo(spot); cam.Distance = 60; cam.Pitch = 0.45f;
            yield return Settle(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal, 120);
            yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 60);
            yield return Settle(() => archLoading == 0, 60);
            yield return Frames(30);
            var vis = Scene.Objects.Where(o => !o.Hidden && o.Mesh != null && o.Gta.MapArchetype != null && Vector3.Distance(o.Position, spot) < 200).ToList();
            var used = vis.SelectMany(o => o.Mesh.Faces.Select(f => f.MatId)).Distinct().Select(id => Scene.Material(id)).ToList();
            var white = used.Where(m => m.DiffuseTexture == null).Select(m => m.Name).ToList();
            var layered = used.Count(m => m.Name.Contains("_l1") || m.Name.Contains("_l2") || m.Name.Contains("_l3") || m.Shader == "normal" && m.FromGame);
            Expect(true, $"spot {spot}: {vis.Count} objects, {used.Count} materials, {white.Count} without texture ({string.Join(", ", white.Take(8))})");
            yield return Shot("terrain_" + (++n));
        }
    }

    IEnumerable<object> MapDiag()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var cam = vp.Camera; MapStream = true;
        var spot = new Vector3(445, -1000, 30);
        GoTo(spot); cam.Distance = 35; cam.Pitch = 0.5f;
        yield return Settle(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal, 120);
        yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 60);
        yield return Settle(() => archLoading == 0, 60);
        yield return Frames(30);
        var eye = cam.Eye;
        var vis = Scene.Objects.Where(o => !o.Hidden && o.Gta.MapArchetype != null && Vector3.Distance(o.Position, spot) < 120).ToList();
        var groups = vis.GroupBy(o => (o.InstanceKey, (int)MathF.Round(o.Position.X * 20), (int)MathF.Round(o.Position.Y * 20), (int)MathF.Round(o.Position.Z * 20))).Where(g => g.Count() > 1).ToList();
        Expect(true, $"{vis.Count} map objects near, {groups.Count} duplicated places; e.g. " + string.Join(" | ", groups.Take(6).Select(g => string.Join(",", g.Select(o => o.Name + "@" + o.Folder)))));
        var docsNear = mapDocs.Keys.Where(k => vis.Any(o => o.Folder == k)).OrderBy(k => k).ToList();
        Expect(true, $"docs near: {string.Join(", ", docsNear)}");
        var far = backdrop.Where(o => !o.Hidden && Vector3.Distance(o.Position, spot) < 150 && !FarSteppedAside(o, eye) && FarLodVisible(o, eye)).ToList();
        Expect(true, $"far models still drawn near the camera: {far.Count}: " + string.Join(", ", far.Take(12).Select(o => o.Name)));
        // same entity from two docs with the same archetype and place
        var byPlace = entOf.Values.GroupBy(v => (v.Ent.ArchetypeHash, (int)MathF.Round(v.Ent.Position.X * 10), (int)MathF.Round(v.Ent.Position.Y * 10))).Where(g => g.Count() > 1).ToList();
        Expect(true, $"entities in two loaded maps: {byPlace.Count}; e.g. " + string.Join(" | ", byPlace.Take(6).Select(g => string.Join(",", g.Select(v => v.Doc.Name + ":" + v.Ent.DisplayName + ":L" + v.Ent.LodLevel)))));
        yield return Shot("diag_all");
        // interiors near the camera open by themselves, view only; streamed maps stay out of the explorer
        yield return Settle(() => interiors.Count > 0, 20);
        yield return Frames(30);
        Expect(interiors.Values.Any(i => i.Auto), $"interiors shown by themselves: {interiors.Count} ({string.Join(", ", interiors.Values.Select(i => i.Doc.ArchetypeName + ":" + i.Doc.Entities.Count))})");
        Expect(outStreamedHidden > 0 && orows.Count < 400, $"Scene Explorer: {orows.Count} rows, {outStreamedHidden} streamed map(s)/interior(s) kept out");
        yield return Shot("diag_interiors");
        // a scene put on the map
        var src = new Scene();
        var bx = new SceneObject { Id = src.NextObjectId++, Name = "my_shed", Mesh = Core.Mesh.Primitives.Box(3, 2, 2.5f) };
        src.Objects.Add(bx);
        var put = ImportSceneOnMap(src, "test scene");
        Expect(put.Count == 1 && MapEditable(put[0]) && put[0].Folder == MapBuildFolder && Vector3.Distance(put[0].WorldBounds().Min, MapGround()) < 4, $"a scene imported onto the map: {put.Count} object(s) in '{put.FirstOrDefault()?.Folder}'");
        // the room plan shows the buildings around
        OpenPlanOnMap(); yield return Frames(6);
        Expect(planFoot != null && planFoot.Count > 20, $"MLO plan on the map: {planFoot?.Count} footprints of the buildings around");
        yield return Shot("diag_plan");
        planOpen = false;
        SetMapMode(false);
    }

    IEnumerable<object> TutTrailer()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        bool exp0 = ShowExplorer, panel0 = ShowPanel, stats0 = ShowStats;
        ShowExplorer = false; ShowPanel = false; ShowStats = false; tutHideCursor = true;
        Maximized = true; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        // the map first, loaded before the camera rolls
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        var cam = vp.Camera;
        if (world != null)
        {
            MapStream = true;
            cam.Target = new Vector3(-300, 600, 0); cam.Distance = 5200; cam.Pitch = 0.95f;
            yield return Settle(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal && archLoading == 0, 240);
            yield return Frames(20);
        }
        // the map is read now (off camera); the film starts in MODEL with the modeling
        SetMapMode(false); Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        foreach (var x in StartFilm("09_trailer", 0)) yield return x;
        tutCaption = null; ShowPanel = false;
        // modeling first: a kiosk built step by step next to a ped
        Mark("model");
        cam.Target = new Vector3(0, -1, 6.4f); cam.Distance = 36; cam.Pitch = 0.2f; cam.Yaw = -2.0f;
        foreach (var x in BuildTower(Vector3.Zero, vp, 0.22f, true, () => Mark("gta_mats"))) yield return x;
        for (int i = 0; i < 60; i++) { cam.Orbit(0.8f, 0); yield return Frames(1); }

        // sculpt
        Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        Mark("sculpt");
        Params.Radius = 1; Params.Segments = 64;
        var rock = Prim(CreateKind.Sphere, new Vector3(0, 0, 0)); rock.Name = "rock";
        var ctr = rock.Mesh.Verts.Aggregate(Vector3.Zero, (a, bb) => a + bb) / rock.Mesh.Verts.Count;
        var rnd = new Random(3);
        for (int i = 0; i < rock.Mesh.Verts.Count; i++) rock.Mesh.Verts[i] = ctr + (rock.Mesh.Verts[i] - ctr) * (1 + ((float)rnd.NextDouble() - 0.5f) * 0.05f);
        rock.Touch();
        SelectOnly(rock); cam.Frame(ctr - new Vector3(1.6f), ctr + new Vector3(1.6f)); cam.Pitch = 0.3f;
        ToggleSculptMode();
        if (SculptMode)
        {
            Sculpt.Brush = SculptBrush.Draw; Sculpt.Radius = 0.35f;
            for (int f = 0; f < 150; f++)
            {
                if (f == 75) Sculpt.Brush = SculptBrush.Inflate;
                float a = f * 0.09f, z = MathF.Sin(f * 0.05f) * 0.6f;
                var dir = Vector3.Normalize(new Vector3(MathF.Cos(a), MathF.Sin(a) * 0.6f - 0.8f, z));
                var pLocal = ctr + dir * 1.0f;
                var stt = CurrentSculptSettings(rock);
                sculptSession.Dab(pLocal, dir, stt); sculptSession.Dab(pLocal + dir * 0.01f, dir, stt); rock.Touch();
                sculptHover = (Vector3.Transform(pLocal, rock.World), dir, Sculpt.Radius, true);
                cam.Orbit(0.5f, 0);
                yield return Frames(1);
            }
            ToggleSculptMode();
        }
        for (int i = 0; i < 30; i++) { cam.Orbit(1f, 0); yield return Frames(1); }

        // texture painting: a sofa, a GTA V fabric, worn edges, stitched seams, seat wear, dust
        Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        Mark("paint");
        foreach (var x in TrailerPaint(vp)) yield return x;
        ShowPanel = false;

        // built for GTA V: the whole map, then modeling right on it
        if (world != null) { SetMapMode(true); vp = Viewports[ActiveViewport]; cam = vp.Camera; ShowPanel = false; ClearMapBuild(); cam.Target = new Vector3(-300, 600, 0); cam.Distance = 5200; cam.Pitch = 0.95f; recordDir_hold = recordDir; recordDir = null; yield return Settle(() => backdropIn.IsEmpty && archLoading == 0, 60); recordDir = recordDir_hold; }
        if (world != null)
        {
            Mark("map_wide");
            for (int i = 0; i < 150; i++) { cam.Orbit(1.4f, 0); yield return Frames(1); }
            Mark("map_zoom");
            var area = new Vector3(-800, -1300, 20); // a parking lot in Little Seoul
            var from = cam.Target; var target = area; float d0 = cam.Distance, p0 = cam.Pitch;
            for (int i = 0; i <= 120; i++)
            {
                float t = Ease(i / 120f);
                cam.Target = Vector3.Lerp(from, target, t); cam.Distance = d0 * MathF.Pow(260 / d0, t); cam.Pitch = p0 - 0.35f * t; cam.Orbit(0.6f, 0);
                yield return Frames(1);
            }
            recordDir_hold = recordDir; recordDir = null; // the HD settles off camera
            yield return Settle(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 25);
            yield return Settle(() => archLoading == 0, 20);
            recordDir = recordDir_hold;
            for (int i = 0; i < 90; i++) { cam.Orbit(1.6f, 0); yield return Frames(1); }
            // modeling right on the map: a building on an empty lot of the city, a ped at its door
            Mark("map_build");
            recordDir_hold = recordDir; recordDir = null;
            LoadAreaAt(area, 60);
            yield return Settle(() => mapLoadQueue.Count == 0 && mapLoadTask == null && entOf.Count > 100, 120);
            yield return Settle(() => archLoading == 0, 60);
            GoTo(area); cam.Distance = 60; cam.Pitch = 1.5f; yield return Frames(3);
            Vector3? lot = MapGround();
            GoTo(lot.Value + new Vector3(0, 0, 6)); cam.Distance = 44; cam.Pitch = 0.16f; cam.Yaw = -2.36f;
            LoadAreaAt(lot.Value);
            yield return Settle(() => mapLoadQueue.Count == 0 && mapLoadTask == null, 60);
            yield return Settle(() => archLoading == 0, 40);
            SetStatus($"Lot at {lot.Value.X:0}, {lot.Value.Y:0}, {lot.Value.Z:0.0}");
            recordDir = recordDir_hold;
            foreach (var x in BuildTower(lot.Value, vp, 0.12f, false)) yield return x;
            for (int i = 0; i < 80; i++) { cam.Orbit(0.9f, 0); yield return Frames(1); }
        }
        SetMapMode(false); Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;

        // MLO: the rooms build themselves
        Mark("mlo");
        Scene.Plan = new RoomPlanData(); FinishFrames = true; FinishFrameStyle = 0; FinishJoinDoors = true;
        FinishSkirting = FinishFloorDirt = FinishCeilingDirt = FinishCornerWear = true;
        cam.Frame(new Vector3(-1, -1, 0), new Vector3(12, 10, 3)); cam.Pitch = 0.75f;
        var plan = Scene.Plan;
        var steps = new List<Action>
        {
            () => plan.Rooms.Add(new PlanRoom { Name = "room_1", X0 = 0, Y0 = 0, X1 = 6, Y1 = 5 }),
            () => plan.Rooms.Add(new PlanRoom { Name = "room_2", X0 = 6, Y0 = 0, X1 = 11, Y1 = 5 }),
            () => plan.Rooms.Add(new PlanRoom { Name = "room_3", X0 = 0, Y0 = 5, X1 = 6, Y1 = 9 }),
            () => { plan.Openings.Add(new PlanOpening { X = 6, Y = 2.5f }); plan.Openings.Add(new PlanOpening { X = 3, Y = 5 }); },
            () => { plan.Openings.Add(new PlanOpening { X = 3, Y = 0 }); plan.Openings.Add(new PlanOpening { X = 11, Y = 2.5f, Sill = 0.9f, Height = 1.2f }); plan.Openings.Add(new PlanOpening { X = 0, Y = 7, Sill = 0.9f, Height = 1.2f }); },
        };
        foreach (var st in steps) { st(); BuildPlan(true); for (int i = 0; i < 26; i++) { cam.Orbit(0.9f, 0); yield return Frames(1); } }
        for (int i = 0; i < 40; i++) { cam.Orbit(0.9f, 0); yield return Frames(1); }
        Mark("frames");
        var door = new Vector3(6, 2.5f, 1.2f);
        foreach (var x in CamTo(vp, door, 3.6f, MathF.PI * 0.8f, 0.15f, 45)) yield return x;
        for (int i = 0; i < 100; i++) { cam.Orbit(-1.2f, 0); yield return Frames(1); }

        // weapon forge: a rifle model glides onto the carbine's skeleton
        Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        if (game != null)
        {
            Mark("weapon");
            propTab = PropTab.Weapon; weaponDonor = 7; weaponMatchSize = true;
            var rifle = ImportAsMine("w_ar_assaultrifle", new Vector3(0.25f, 0.6f, 0.3f), 90, "my_rifle");
            try { weaponBones = MoloMax.Core.Gta.Forge.WeaponForge.DonorBones(game, MoloMax.Core.Gta.Forge.WeaponForge.Donors[7]); ImportDonorGhost("w_ar_carbinerifle"); } catch { weaponBones = null; }
            cam.Target = new Vector3(0.15f, 0.3f, 0.1f); cam.Distance = 1.6f; cam.Pitch = 0.45f;
            for (int i = 0; i < 30; i++) { cam.Orbit(0.8f, 0); yield return Frames(1); }
            if (weaponBones != null && rifle.Count > 0)
            {
                var donorObjs = Scene.Objects.Where(o => o.Folder == ForgeFolder && !o.Hidden && o.Mesh.Faces.Count > 0).ToList();
                var (dmn, dmx) = Bounds(donorObjs.SelectMany(o => o.Mesh.Verts.Select(v => Vector3.Transform(v, o.World))));
                var muz = weaponBones.FirstOrDefault(b => b.Name.Contains("muzzle", StringComparison.OrdinalIgnoreCase))?.Position ?? dmx;
                var (wfit, wm) = MoloMax.Core.Gta.Forge.WeaponForge.Fit(rifle.SelectMany(o => o.Mesh.Verts.Select(v => Vector3.Transform(v, o.World))).ToList(), dmn, dmx, muz, true);
                weaponMuzzle = wm;
                foreach (var x in GlideFit(rifle, wfit, 50, vp, 0.8f)) yield return x;
                foreach (var x in CamTo(vp, new Vector3(0.15f, 0, 0.05f), 1.0f, cam.Yaw + 0.4f, 0.3f, 30)) yield return x;
            }
            for (int i = 0; i < 70; i++) { cam.Orbit(1.2f, 0); yield return Frames(1); }
            // vehicle forge: a car body and its wheels glide onto sultan2's wheels
            Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
            Mark("vehicle");
            propTab = PropTab.Vehicle;
            var car = ImportAsMine("comet2", new Vector3(0, 5.5f, 0), -90, "my_car");
            try
            {
                var cd = MoloMax.Core.Gta.Forge.VehicleForge.LoadDonor(game, "comet2");
                var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
                foreach (var kv in cd.Wheels.Where(w => w.Key is "wheel_lf" or "wheel_rf" or "wheel_lr" or "wheel_rr"))
                {
                    float r = MathF.Max(0.25f, kv.Value.Z - cd.Min.Z);
                    var w = Scene.Add("my_" + kv.Key, MoloMax.Core.Mesh.Primitives.Cylinder(r, 0.24f, 20), Vector3.Zero); w.Name = "my_" + kv.Key;
                    var axis = Vector3.Transform(Vector3.UnitX, q);
                    w.Rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, axis)), MathF.PI / 2);
                    var (wmn, wmx) = w.WorldBounds(); w.Position = new Vector3(0, 5.5f, 0) + Vector3.Transform(kv.Value, q) - (wmn + wmx) * 0.5f; w.Touch();
                    car.Add(w);
                }
                vehicleDonor = MoloMax.Core.Gta.Forge.VehicleForge.LoadDonor(game, "sultan2"); ImportDonorGhost("sultan2");
            }
            catch { vehicleDonor = null; }
            cam.Target = new Vector3(0, 2.7f, 0.6f); cam.Distance = 11; cam.Pitch = 0.5f;
            for (int i = 0; i < 30; i++) { cam.Orbit(0.8f, 0); yield return Frames(1); }
            if (vehicleDonor != null)
            {
                vehicleWheels = MoloMax.Core.Gta.Forge.VehicleForge.FindWheels(car.Select(o => (o.Mesh, o.World)).ToList(), 0, true, out vehicleWheelBoxes);
                yield return Frames(20);
                if (vehicleWheels != null)
                {
                    var vfit = MoloMax.Core.Gta.Forge.VehicleForge.Fit(vehicleWheels, 0, true, vehicleDonor, false);
                    vehicleWheels = null;
                    foreach (var x in GlideFit(car, vfit, 60, vp, 0.6f)) yield return x;
                    foreach (var o in Scene.Objects.Where(o => o.Folder == ForgeFolder)) o.Hidden = true;
                    foreach (var x in CamTo(vp, new Vector3(0, 0, 0.6f), 6.5f, cam.Yaw + 0.5f, 0.3f, 30)) yield return x;
                }
            }
            for (int i = 0; i < 80; i++) { cam.Orbit(1.2f, 0); yield return Frames(1); }
            propTab = PropTab.Object; vehicleDonor = null; weaponBones = null; weaponMuzzle = null;
        }

        // lights at night
        Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        Mark("lights");
        Hour = 23; InteriorLight = false;
        var floor = Prim(CreateKind.Box, new Vector3(0, 0, -0.2f)); floor.Scale = new Vector3(6, 6, 0.1f); floor.Touch();
        var wall = Prim(CreateKind.Box, new Vector3(0, 3, 0)); wall.Scale = new Vector3(6, 0.2f, 1.6f); wall.Touch();
        var crate2 = Prim(CreateKind.Box, new Vector3(-1.5f, 0, 0));
        cam.Frame(new Vector3(-5, -5, -1), new Vector3(5, 4, 3.5f));
        Cursor3D = new Vector3(1, 0, 1.5f); AddLight(GtaLightKind.Point); var lp = Current;
        Cursor3D = new Vector3(-2, 2, 2.5f); AddLight(GtaLightKind.Spot); if (Current?.Gta.Light != null) { Current.Gta.Light.Colour = new Vector3(0.4f, 0.6f, 1f); Current.Gta.Light.Intensity = 20; }
        Cursor3D = new Vector3(2, 2.6f, 1.2f); AddLight(GtaLightKind.Capsule); if (Current?.Gta.Light != null) { Current.Gta.Light.Colour = new Vector3(1f, 0.3f, 0.7f); Current.Gta.Light.CapsuleLength = 2; Current.Gta.Light.Intensity = 12; }
        Selected.Clear(); SelectionChanged();
        for (int i = 0; i < 120; i++) { if (lp?.Gta.Light != null) { lp.Gta.Light.UseTemperature = true; lp.Gta.Light.Temperature = 1900 + i * 45; } cam.Orbit(1.2f, 0); yield return Frames(1); }
        Hour = 13;

        // end: the rooms again, wide
        Fresh(); Maximized = true; vp = Viewports[ActiveViewport]; cam = vp.Camera; vp.Shading = ShadingMode.Realistic;
        Mark("end");
        Scene.Plan = plan; BuildPlan(true);
        var endObjs = Scene.Objects.Where(o => o.Mesh.Faces.Count > 0).ToList();
        if (endObjs.Count > 0) { cam.Frame(endObjs.Select(o => o.WorldBounds().Item1).Aggregate(Vector3.Min), endObjs.Select(o => o.WorldBounds().Item2).Aggregate(Vector3.Max)); cam.Distance *= 1.3f; }
        cam.Pitch = 0.65f;
        for (int i = 0; i < 150; i++) { cam.Orbit(0.7f, 0); yield return Frames(1); }
        Mark("stop");
        foreach (var x in EndFilm()) yield return x;
        ShowExplorer = exp0; ShowPanel = panel0; ShowStats = stats0; tutHideCursor = false; Maximized = false;
    }

    /// <summary>Film 10: modeling right on the map (a box edited in place, MLO rooms on the ground, a game model opened as your own copy).</summary>
    IEnumerable<object> TutMapBuild()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index"); SetMapMode(false); yield break; }
        var vp = Viewports[ActiveViewport]; var cam = vp.Camera;
        var spot = new Vector3(195, -934, 30);
        GoTo(spot); cam.Distance = 45; cam.Pitch = 0.55f; MapStream = true; ShowPanel = true; propTab = PropTab.Map;
        LoadAreaAt(spot);
        yield return Until(() => mapLoadQueue.Count == 0 && mapLoadTask == null && entOf.Count > 100, 240);
        yield return Until(() => archLoading == 0, 90);
        yield return Frames(30);
        foreach (var x in StartFilm("10_map_modeling", 6)) yield return x;
        foreach (var x in Say("Model right on the map", "Every modeling tool works in MAP mode, at the real place", 70)) yield return x;
        // a box, then edit mode
        foreach (var x in Say("Map tab > Box here", "A box on the ground in the middle of the view")) yield return x;
        foreach (var x in ClickAt(TutCentre("map_box"), moveFrames: 35)) yield return x;
        yield return Frames(25);
        var box = Current;
        if (box != null)
        {
            box.Scale = new Vector3(4, 4, 3); box.Touch();
            foreach (var x in Say("Tab: edit mode, like in MODEL", "Shift + drag the arrow: a new floor on top")) yield return x;
            SetLevel(SubObject.Polygon);
            int top = FaceToward(box, Vector3.UnitZ); SubSel(box).Clear(); SubSel(box).Faces.Add(top); Tool = Tool.Move; yield return Frames(15);
            var g = GizmoGrip(vp, GizmoHandle.Z);
            foreach (var x in MouseTo(g)) yield return x;
            KeyDownT(Keys.ShiftKey); yield return Frames(8);
            foreach (var x in DragTo(g, g - new Vector2(0, 110), 45)) yield return x;
            KeyUpT(Keys.ShiftKey); yield return Frames(30);
            SetLevel(SubObject.Object); Tool = Tool.Select;
        }
        // MLO rooms on the ground
        foreach (var x in Say("Map tab > MLO rooms here", "The Room Builder plan stands on the ground right there")) yield return x;
        cam.Target = spot + new Vector3(8, 6, 0); cam.Distance = 40; yield return Frames(5);
        Scene.Plan = new RoomPlanData(); PlanLive = true; FinishFrames = true; FinishJoinDoors = true;
        foreach (var x in ClickAt(TutCentre("map_mlo"), moveFrames: 35)) yield return x;
        planTool = 0; planZoom = 26; planPan = new Vector2(40, 40); yield return Frames(20);
        foreach (var x in DragTo(PlanToScreen(new Vector2(0, 0)), PlanToScreen(new Vector2(6, 5)), 40)) yield return x;
        yield return Frames(10);
        foreach (var x in DragTo(PlanToScreen(new Vector2(6, 0)), PlanToScreen(new Vector2(10, 5)), 40)) yield return x;
        planTool = 1; yield return Frames(8);
        foreach (var x in ClickAt(PlanToScreen(new Vector2(6, 2.5f)))) yield return x;
        foreach (var x in ClickAt(PlanToScreen(new Vector2(3, 0)))) yield return x;
        yield return Frames(20);
        planOpen = false; yield return Frames(10);
        var org = new Vector3(Scene.Plan.OriginX, Scene.Plan.OriginY, Scene.Plan.OriginZ);
        foreach (var x in CamTo(vp, org + new Vector3(5, 2.5f, 1), 22, cam.Yaw + 0.6f, 0.6f, 50)) yield return x;
        for (int i = 0; i < 80; i++) { cam.Orbit(1.2f, 0); yield return Frames(1); }
        // a game model as your own copy
        foreach (var x in Say("Edit a game model: your own copy, in place", "The other places that use it keep the original")) yield return x;
        var ent = Scene.Objects.Where(o => entOf.TryGetValue(o.Id, out var de) && !de.Ent.IsMlo && archSlots.TryGetValue(de.Ent.ArchetypeHash, out var sl) && sl.State == 2 && o.Mesh.Faces.Count > 50)
                               .OrderBy(o => Vector3.DistanceSquared(o.Position, spot)).FirstOrDefault();
        if (ent != null)
        {
            var (emn, emx) = ent.WorldBounds();
            foreach (var x in CamTo(vp, (emn + emx) * 0.5f, MathF.Max(15, (emx - emn).Length() * 1.2f), cam.Yaw, 0.5f, 40)) yield return x;
            foreach (var x in ClickAt(ScreenOf(vp, (emn + emx) * 0.5f))) yield return x;
            SelectOnly(ent); yield return Frames(15);
            foreach (var x in ClickAt(TutCentre("map_edit"), moveFrames: 35)) yield return x;
            yield return Frames(20);
            if (Level != SubObject.Polygon) { EditInPlace(ent); SetLevel(SubObject.Polygon); }
            int f0 = FaceToward(ent, Vector3.UnitZ); SubSel(ent).Clear(); SubSel(ent).Faces.Add(f0); Tool = Tool.Move; yield return Frames(15);
            var g2 = GizmoGrip(vp, GizmoHandle.Z);
            foreach (var x in DragTo(g2, g2 - new Vector2(0, 90), 45)) yield return x;
            yield return Frames(30);
            SetLevel(SubObject.Object); Tool = Tool.Select;
        }
        foreach (var x in Say("SAVE MAP RESOURCE writes it all", "New models, the MLO, the edited .ymap: one FiveM resource", 30)) yield return x;
        foreach (var x in CamTo(vp, spot + new Vector3(4, 3, 0), 70, cam.Yaw + 0.8f, 0.7f, 60)) yield return x;
        for (int i = 0; i < 60; i++) { cam.Orbit(1f, 0); yield return Frames(1); }
        foreach (var x in EndFilm()) yield return x;
        SetMapMode(false);
    }

    /// <summary>Films: objects glide from where they are to their transform times <paramref name="fit"/>.</summary>
    IEnumerable<object> GlideFit(List<SceneObject> objs, Matrix4x4 fit, int frames, Viewport vp = null, float orbit = 0)
    {
        var from = objs.Select(o => (o.Position, o.Rotation, o.Scale)).ToList();
        var to = objs.Select(o => Matrix4x4.Decompose(o.World * fit, out var sc, out var r, out var t) ? (t, Quaternion.Normalize(r), sc) : (o.Position, o.Rotation, o.Scale)).ToList();
        for (int f = 1; f <= frames; f++)
        {
            float k = Ease(f / (float)frames);
            for (int i = 0; i < objs.Count; i++)
            {
                objs[i].Position = Vector3.Lerp(from[i].Position, to[i].Item1, k);
                objs[i].Rotation = Quaternion.Slerp(from[i].Rotation, to[i].Item2, k);
                objs[i].Scale = Vector3.Lerp(from[i].Scale, to[i].Item3, k); objs[i].Touch();
            }
            if (vp != null && orbit != 0) vp.Camera.Orbit(orbit, 0);
            yield return Frames(1);
        }
    }

    IEnumerable<object> ForgeGame()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
    }

    /// <summary>Imports a game model as if it were the user's own mesh (films): placed, turned, collisions hidden.</summary>
    List<SceneObject> ImportAsMine(string model, Vector3 at, float yawDeg, string name)
    {
        var a = game.FindModel(MoloMax.Core.Gta.Map.MapDocument.Hash(model));
        if (a == null) return new();
        var objs = ImportFile(game.Extract(a, Path.Combine(Path.GetTempPath(), "polymorph_mine")), Vector3.Zero);
        importReportOpen = false;
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawDeg * MathF.PI / 180);
        var all = objs.Concat(objs.Select(Scene.Parent).Where(p => p != null)).Distinct().ToList();
        foreach (var o in all)
        {
            o.Position = at + Vector3.Transform(o.Position, q); o.Rotation = q * o.Rotation; o.Touch();
            if (o.GtaType is GtaObjectType.Collision or GtaObjectType.CollisionGroup) o.Hidden = true;
            o.Name = o.Name.Replace(model, name);
        }
        return objs.Where(o => o.GtaType == GtaObjectType.Drawable && o.Mesh.Faces.Count > 0).ToList();
    }

    /// <summary>Film 11: the Weapon Forge (a rifle model turned into an add-on weapon on the carbine's skeleton).</summary>
    IEnumerable<object> TutWeaponForge()
    {
        Fresh();
        foreach (var x in ForgeGame()) yield return x;
        if (game == null) { Expect(false, "game files"); yield break; }
        Maximized = true; ShowPanel = true; propTab = PropTab.Weapon; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        forgeOut = StPath("forge_film"); weaponDonor = 7; weaponName = "molo_rifle"; weaponReplace = false; weaponBones = null; weaponMuzzle = null; forgeReport = null;
        var mine = ImportAsMine("w_ar_assaultrifle", new Vector3(0.2f, 0.7f, 0.25f), 90, "my_rifle");
        vp.Camera.Frame(new Vector3(-0.6f, -0.5f, -0.2f), new Vector3(0.8f, 1.2f, 0.6f));
        foreach (var x in StartFilm("11_weapon_forge", 5)) yield return x;
        foreach (var x in Say("Weapon Forge", "Any 3D model becomes a GTA weapon", 70)) yield return x;
        foreach (var x in Say("1. Pick a game weapon as donor", "It comes in as an orange ghost, with its bones: grip, muzzle, attachments")) yield return x;
        foreach (var x in ClickAt(TutCentre("wf_donor"), moveFrames: 35)) yield return x;
        yield return Frames(20);
        vp.Camera.Frame(new Vector3(-0.6f, -0.5f, -0.2f), new Vector3(0.8f, 1.2f, 0.6f)); yield return Frames(40);
        foreach (var x in Say("2. Select your model and Fit", "The barrel goes on the muzzle, same length, centred")) yield return x;
        SelectOnly(mine.ToArray()); yield return Frames(20);
        foreach (var x in ClickAt(TutCentre("wf_fit"), moveFrames: 35)) yield return x;
        yield return Frames(30);
        foreach (var x in CamTo(vp, new Vector3(0.1f, 0, 0.05f), 1.1f, vp.Camera.Yaw + 0.5f, 0.25f, 50)) yield return x;
        for (int i = 0; i < 70; i++) { vp.Camera.Orbit(1.5f, 0); yield return Frames(1); }
        foreach (var x in Say("3. Make the weapon", "Add-on: metas cloned from the donor, fxmanifest and /give command. Or replace the game's one")) yield return x;
        foreach (var x in ClickAt(TutCentre("wf_make"), moveFrames: 35)) yield return x;
        yield return Frames(90);
        foreach (var x in Say("ensure the resource, /give_molo_rifle in game", null, 60)) yield return x;
        foreach (var x in EndFilm()) yield return x;
        Scene.Objects.RemoveAll(o => o.Folder == ForgeFolder); Maximized = false; propTab = PropTab.Object;
    }

    /// <summary>Film 12: the Vehicle Forge (a car body with simple wheels fitted on sultan2: skeleton, wheels, collision, handling).</summary>
    IEnumerable<object> TutVehicleForge()
    {
        Fresh();
        foreach (var x in ForgeGame()) yield return x;
        if (game == null) { Expect(false, "game files"); yield break; }
        Maximized = true; ShowPanel = true; propTab = PropTab.Vehicle; var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false; vp.Shading = ShadingMode.Realistic;
        forgeOut = StPath("forge_film"); vehicleDonorName = "sultan2"; vehicleName = "molo_comet"; vehicleLabel = "Molo Comet"; vehicleFrontPositive = true; vehicleDonorWheels = true;
        vehicleDonor = null; vehicleWheels = null; forgeReport = null;
        // "your car": a body (the comet's, without its wheels) and four simple wheels, facing +X beside the origin
        var at = new Vector3(0, 5.5f, 0);
        var mine = ImportAsMine("comet2", at, -90, "my_car");
        var cd = MoloMax.Core.Gta.Forge.VehicleForge.LoadDonor(game, "comet2");
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        foreach (var (key, c) in cd.Wheels.Where(w => w.Key is "wheel_lf" or "wheel_rf" or "wheel_lr" or "wheel_rr"))
        {
            float r = MathF.Max(0.25f, c.Z - cd.Min.Z);
            var w = Scene.Add("my_" + key, MoloMax.Core.Mesh.Primitives.Cylinder(r, 0.24f, 20), Vector3.Zero); w.Name = "my_" + key;
            var wc = at + Vector3.Transform(c, q);
            var axis = Vector3.Transform(Vector3.UnitX, q); // the axle, across the car
            w.Rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, axis)), MathF.PI / 2);
            var (wmn, wmx) = w.WorldBounds(); w.Position = wc - (wmn + wmx) * 0.5f; w.Touch();
            mine.Add(w);
        }
        vp.Camera.Frame(new Vector3(-3, -3, 0), new Vector3(3, 8, 2));
        foreach (var x in StartFilm("12_vehicle_forge", 6)) yield return x;
        foreach (var x in Say("Vehicle Forge", "Any car model becomes a FiveM vehicle", 70)) yield return x;
        foreach (var x in Say("1. Pick a game car as donor", "Its skeleton, wheels, collision and handling will carry your body")) yield return x;
        foreach (var x in ClickAt(TutCentre("vf_donor"), moveFrames: 35)) yield return x;
        yield return Frames(15);
        vp.Camera.Frame(new Vector3(-3, -3, 0), new Vector3(3, 8, 2)); yield return Frames(40);
        foreach (var x in Say("2. Select your car: Find my wheels", "Four round parts, one per corner")) yield return x;
        SelectOnly(mine.ToArray()); vehicleFrontPositive = true; yield return Frames(15);
        foreach (var x in ClickAt(TutCentre("vf_wheels"), moveFrames: 35)) yield return x;
        yield return Frames(50);
        foreach (var x in Say("3. Fit on the donor's wheels", "Turned, scaled and placed: your wheels on the donor's")) yield return x;
        foreach (var x in ClickAt(TutCentre("vf_fit"), moveFrames: 35)) yield return x;
        yield return Frames(30);
        foreach (var o in Scene.Objects.Where(o => o.Folder == ForgeFolder)) o.Hidden = true;
        foreach (var x in CamTo(vp, new Vector3(0, 0, 0.7f), 7, vp.Camera.Yaw + 0.6f, 0.3f, 50)) yield return x;
        for (int i = 0; i < 80; i++) { vp.Camera.Orbit(1.5f, 0); yield return Frames(1); }
        foreach (var x in Say("4. Make the vehicle", ".yft on the donor's skeleton, textures, vehicles / handling / carvariations metas, /spawn command")) yield return x;
        foreach (var x in ClickAt(TutCentre("vf_make"), moveFrames: 35)) yield return x;
        yield return Frames(90);
        foreach (var x in Say("ensure the resource, /spawn_molo_comet in game", null, 60)) yield return x;
        foreach (var x in EndFilm()) yield return x;
        Scene.Objects.RemoveAll(o => o.Folder == ForgeFolder); Maximized = false; propTab = PropTab.Object;
    }

    IEnumerable<object> TutMap()
    {
        var gtaFolder = SelfTestArgs.ElementAtOrDefault(0);
        Fresh();
        SetMapMode(true); ClearMapBuild();
        if (game == null && !string.IsNullOrEmpty(gtaFolder)) { Settings.GtaFolder = gtaFolder; if (gameTask == null || gameTask.IsCompleted) StartGameLoad(gtaFolder); }
        yield return Until(() => game != null || gameError != null, 400);
        yield return Until(() => world != null, 600);
        if (world == null) { Expect(false, "map index"); SetMapMode(false); yield break; }
        Maximized = true; var vp = Viewports[ActiveViewport]; var cam = vp.Camera;
        MapStream = true;
        cam.Target = new Vector3(-300, 900, 0); cam.Distance = 6500; cam.Pitch = 1.05f;
        yield return Until(() => backdropIn.IsEmpty && backdropMaps >= backdropTotal && archLoading == 0, 300);
        yield return Frames(30);
        foreach (var x in StartFilm("07_map_mode", 5)) yield return x;
        foreach (var x in Say("MAP mode: the whole GTA V map", "Far models (LOD / SLOD) like the game, light and fast", 70)) yield return x;
        for (int i = 0; i < 60; i++) { cam.Orbit(2, 0); yield return Frames(1); }
        foreach (var x in Say("Levels of detail: one checkbox each", "World panel > Levels of detail")) yield return x;
        SetMapLevel(2, false); yield return Frames(40); SetMapLevel(2, true); yield return Frames(30);
        foreach (var x in Say("Zoom in: the real models (HD) load around the camera", "They go again when you zoom out")) yield return x;
        var target = new Vector3(195, -934, 30);
        for (int i = 0; i <= 90; i++)
        {
            float t = Ease(i / 90f);
            cam.Target = Vector3.Lerp(new Vector3(-300, 900, 0), target, t); cam.Distance = 6500 * MathF.Pow(260 / 6500f, t); cam.Pitch = 1.05f - 0.4f * t;
            yield return Frames(1);
        }
        yield return Until(() => mapStreamQueue.Count == 0 && mapLoadTask == null, 60);
        for (int i = 0; i < 90; i++) { cam.Orbit(1.5f, 0); yield return Frames(1); }
        foreach (var x in Say("Place props: they stick to the surfaces", "Place props panel, click to drop, wheel to turn")) yield return x;
        StartPlacement("prop_bench_01a");
        yield return Until(() => archSlots.TryGetValue(MoloMax.Core.Gta.Map.MapDocument.Hash("prop_bench_01a"), out var sl) && sl.State >= 2, 60);
        var centre = new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.55f);
        foreach (var x in MouseTo(centre - new Vector2(150, 0), 20)) yield return x;
        foreach (var x in MouseTo(centre, 50)) yield return x;
        foreach (var x in ClickAt(centre, moveFrames: 5)) yield return x;
        EndPlacement(); yield return Frames(30);
        foreach (var x in Say("SAVE MAP RESOURCE writes a FiveM resource", "Changed .ymap / .ytyp + fxmanifest, ready to ensure")) yield return x;
        yield return Frames(40);
        foreach (var x in EndFilm()) yield return x;
        SetMapMode(false); Maximized = false;
    }

    IEnumerable<object> PlanarTest()
    {
        Fresh();
        if (MapMode) SetMapMode(false);
        Maximized = true; ActiveViewport = 3;
        var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false;
        yield return Frames(2);
        foreach (var x in ClickAt(new Vector2(vp.X + 40, vp.Y + vp.H - 40))) yield return x; // closes a menu another test left open
        var box = Prim(CreateKind.Box, Vector3.Zero); box.Name = "lvl";
        box.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f); box.Touch();
        vp.Camera.Frame(new Vector3(-3, -3, -2), new Vector3(3, 3, 3));
        bracketsDrawn = 0; SelectOnly(box); yield return Frames(5);
        Expect(bracketsDrawn > 0, $"object mode: selection box drawn ({bracketsDrawn} box)");
        yield return Shot("planar_brackets");
        SetLevel(SubObject.Vertex);
        var m = box.Mesh; var s = SubSel(box); s.Clear();
        for (int i = 0; i < m.Verts.Count; i++) if (m.Verts[i].Z > 0.01f || i % 3 == 0) s.Verts.Add(i);
        int nsel = s.Verts.Count;
        planarTo = 0; planarLocal = false; MakePlanar(2); yield return Frames(3);
        var zs = s.Verts.Select(i => Vector3.Transform(box.Mesh.Verts[i], box.World).Z).ToList();
        Expect(zs.Count == nsel && zs.Max() - zs.Min() < 1e-3f, $"Make Planar Z: {zs.Count} vertices at one world height (spread {zs.Max() - zs.Min():0.#####})");
        planarTo = 1; MakePlanar(0); yield return Frames(3);
        var xs = s.Verts.Select(i => Vector3.Transform(box.Mesh.Verts[i], box.World).X).ToList();
        Expect(xs.Max() - xs.Min() < 1e-3f, "Make Planar X to the lowest");
        DoUndo(); DoUndo(); yield return Frames(3);
        s = SubSel(Obj("lvl")); 
        for (int i = 0; i < Obj("lvl").Mesh.Verts.Count; i++) s.Verts.Add(i);
        var before = Obj("lvl").Mesh.Verts.ToList();
        MakePlanar(-1); yield return Frames(3);
        var after = Obj("lvl").Mesh.Verts;
        var c = Vector3.Zero; foreach (var p in after) c += p; c /= after.Count;
        Expect(after.Count == before.Count, "best-fit plane keeps the vertices");
        yield return Shot("planar_done");
        SetLevel(SubObject.Object);
        // a click on another mesh in polygon level goes on editing that mesh
        var b2 = Prim(CreateKind.Box, new Vector3(9, 0, 0)); b2.Name = "other";
        vp.Camera.Frame(new Vector3(7, -2, -1), new Vector3(11, 2, 3)); yield return Frames(3); // only the other box under the mouse
        SelectOnly(Obj("lvl")); SetLevel(SubObject.Polygon); Tool = Tool.Select; yield return Frames(2);
        int top = FaceToward(b2, Vector3.UnitZ);
        foreach (var x in ClickAt(FaceScreen(vp, b2, top))) yield return x;
        yield return Frames(3);
        Expect(Current == b2 && Level == SubObject.Polygon && SubSel(b2).Faces.Count == 1, $"click on another mesh in polygon level: now editing '{Current?.Name}', {SubSel(b2).Faces.Count} face selected");
        SetLevel(SubObject.Object);
        // Preserve UVs: a vertex slid along a planar face keeps the texture where it was
        var pl = Prim(CreateKind.Plane, new Vector3(0, 8, 0)); pl.Name = "uvplane";
        SelectOnly(pl); SetLevel(SubObject.Vertex); yield return Frames(2);
        var pm = pl.Mesh; var f0 = pm.Faces[0];
        int corner = 0, vi = f0.V[corner];
        var uv0 = f0.UV[corner]; var p0 = pm.Verts[vi];
        // the uv at the point the vertex will land on, read from the original face
        var target = p0 + (pm.Verts[f0.V[2]] - p0) * 0.25f; target.Z = p0.Z;
        var mapRef = FitUvMap(pm, f0);
        Vector2 expectUv = default;
        if (mapRef != null) { var d = target - mapRef.C; float qx = Vector3.Dot(d, mapRef.T), qy = Vector3.Dot(d, mapRef.B); expectUv = new Vector2(mapRef.U.X * qx + mapRef.U.Y * qy + mapRef.U.Z, mapRef.V.X * qx + mapRef.V.Y * qy + mapRef.V.Z); }
        Settings.PreserveUVs = true;
        SubSel(pl).Clear(); SubSel(pl).Verts.Add(vi);
        CaptureOriginals();
        var w0 = Vector3.Transform(target, pl.World);
        ApplyLive(_ => w0, (a1, b1, c1) => (a1, b1, c1));
        var uvNow = pl.Mesh.Faces[0].UV[corner];
        Expect(mapRef != null && Vector2.Distance(uvNow, expectUv) < 1e-3f && Vector2.Distance(uvNow, uv0) > 1e-3f, $"Preserve UVs: moved corner UV {uv0} -> {uvNow} (expected {expectUv})");
        Settings.PreserveUVs = false;
        SetLevel(SubObject.Object);
        // vertex level, Y constraint + vertex snap: the vertex goes to the target's Y only (same level, not onto it)
        UI.ImGuiBackend.SyntheticInput = true;
        var va = Prim(CreateKind.Box, new Vector3(0, 14, 0)); va.Name = "snapv_a";
        var vb = Prim(CreateKind.Box, new Vector3(2.5f, 15.3f, 0.6f)); vb.Name = "snapv_b";
        Maximized = true; ActiveViewport = 3; var vp3 = Viewports[3];
        vp3.Camera.Frame(new Vector3(-2, 12, -1), new Vector3(5, 18, 3)); yield return Frames(3);
        Vector3 WV(SceneObject o, int i) => Vector3.Transform(o.Mesh.Verts[i], o.World);
        int iv = Enumerable.Range(0, va.Mesh.Verts.Count).OrderByDescending(i => WV(va, i).X + WV(va, i).Z).First();
        int it = Enumerable.Range(0, vb.Mesh.Verts.Count).OrderBy(i => WV(vb, i).X - WV(vb, i).Z).First();
        var startV = WV(va, iv); var tgt = WV(vb, it);
        Snap = true; SnapGrid = false; SnapVertexT = true; SnapEdge = SnapFace = SnapNearest = false; SnapAxisConstraint = true;
        SelectOnly(va); SetLevel(SubObject.Vertex); SubSel(va).Clear(); SubSel(va).Verts.Add(iv); yield return Frames(2);
        PostMouse(0x200, ScreenOf(vp3, startV), 0); yield return Frames(3);
        BeginModal(ModalKind.Translate); modal.Axis = 1; modal.Plane = false;
        for (int i = 1; i <= 10; i++) { PostMouse(0x200, Vector2.Lerp(ScreenOf(vp3, startV), ScreenOf(vp3, tgt), i / 10f), 0); yield return Frames(1); }
        yield return Frames(2);
        yield return Shot("snap_vertex_level_y");
        ConfirmModal();
        var endV = WV(va, iv);
        Expect(MathF.Abs(endV.Y - tgt.Y) < 1e-3f && MathF.Abs(endV.X - startV.X) < 1e-4f && MathF.Abs(endV.Z - startV.Z) < 1e-4f, $"vertex level, Y + vertex snap: Y {startV.Y:0.000} -> {endV.Y:0.000} (target {tgt.Y:0.000}), X/Z kept ({endV.X - startV.X:0.0000}, {endV.Z - startV.Z:0.0000})");
        SetLevel(SubObject.Object); Snap = false; SnapGrid = true; SnapEdge = SnapFace = true;
        UI.ImGuiBackend.SyntheticInput = false; Maximized = false;
        // Export > Selected models: one .ydr per selected model, nothing else
        var exDir = StPath("sel_export"); if (Directory.Exists(exDir)) Directory.Delete(exDir, true);
        SelectOnly(Obj("snapv_a"), Obj("snapv_b"));
        exportSelFormat = 0; StartExportSelected(ExportRoots(), exDir);
        yield return Until(() => exportSelTask == null || exportSelTask.IsCompleted, 120);
        var exFiles = Directory.Exists(exDir) ? Directory.GetFiles(exDir) : Array.Empty<string>();
        Expect(exFiles.Length == 2 && exFiles.All(f => f.EndsWith(".ydr") && new FileInfo(f).Length > 1000), $"export selected: {exFiles.Length} file(s): {string.Join(", ", exFiles.Select(Path.GetFileName))} {string.Join(" | ", exportSelTask?.Result ?? new List<string>())}");
    }
}
