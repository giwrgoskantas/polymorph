using MoloMax.Core.Gta.Library;
using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
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
        T("TUTO 99 trailer preview", TutTrailerPreview);
        T("TUTO 98 lot finder", TutLotTest);
        T("MAP diag: duplicates", MapDiag);
        T("ASSETS: browser thumbnails + placing", AssetTest);
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
        foreach (var x in Say("Polymorph · free forever", "Open source (MIT), made by Molo Modding", 90)) yield return x;
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
            assetSource = 1; assetFamily = 0; assetKey = null; // chairs & seats
            yield return Until(() => gameClassified, 1200);
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
        var vp = Viewports[ActiveViewport]; vp.Camera.Ortho = false;
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
        var b2 = Prim(CreateKind.Box, new Vector3(4, 0, 0)); b2.Name = "other";
        vp.Camera.Frame(new Vector3(-3, -3, -2), new Vector3(7, 3, 3)); yield return Frames(3);
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
    }
}
