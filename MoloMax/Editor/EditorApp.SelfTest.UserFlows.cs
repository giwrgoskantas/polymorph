using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Paint;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using Keys = System.Windows.Forms.Keys;

namespace MoloMax.Editor;

/// <summary>
/// USER FLOWS: the gestures of a real user, with the real mouse and keyboard messages, and the result a user expects
/// checked (not only "it did not crash"). Every bug a user reports gets its gesture here, so it can't come back.
/// Run alone with POLYMORPH_ST_ONLY=USER.
/// </summary>
public sealed partial class EditorApp
{
    void UserFlowTests(Action<string, Func<IEnumerable<object>>> T)
    {
        T("USER: faces: click, quick Ctrl+click a 2nd face (no loop over the whole mesh), Ctrl+Up / Down, arrows still move the camera", UserFaceSelection);
        T("USER: knife: cut starting inside a face, 90° step gives a straight cut, straight on world axes", UserKnife);
        T("USER: loop cut: several loops with +, Tab turns the cut the other way", UserLoopCut);
        T("USER: select 90 heavy objects at once: the views stay fast; Hide on a Drawable hides its meshes", UserBigSelection);
        T("USER: walk with the arrows, then orbit with the mouse: the walk mode leaves (orbit distance back); Ctrl+arrow never walks", UserWalkLeaves);
        T("USER: dense room mesh in vertex mode: every point shown, Drawable row + eye hides it, Separate from points", UserDenseDrawable);
        T("USER: detach faces, make a Drawable of them, click the eye of its mesh row (selected or not, from face mode): it hides", UserDetachEye);
        T("USER: Join (Ctrl+J) with Drawable nodes selected: the meshes join into a mesh, never into the node; hidden ones stay out", UserJoinNodes);
        T("USER: after an undo / redo (the objects are new copies), the eye of a row still hides the real object", UserEyeAfterUndo);
        T("USER: Molo Paint drips: a print near the top of a wall lets drops run down it", UserPaintDrips);
        T("USER: MODEL scene to MAP with its .ymap: the dialog finds the .ymap and the models go where it places them", UserPlaceFromYmap);
        T("USER: holding Ctrl (to add to the selection) never turns the snap on; inside a G move it still flips it", UserCtrlNoSnap);
        T("USER: Shift+click two rows (range of Drawables and meshes), then the eye of one: every selected row hides (Shift held or not, each keymap)", UserShiftEye);
    }

    const int MkShift = 4, MkCtrl = 8;

    /// <summary>A left click with the real window messages (mods: MkShift / MkCtrl, the key is held around the click).</summary>
    IEnumerable<object> UClick(Vector2 p, int mods = 0)
    {
        if ((mods & MkCtrl) != 0) { PostKey(Keys.ControlKey, true); yield return Frames(1); }
        if ((mods & MkShift) != 0) { PostKey(Keys.ShiftKey, true); yield return Frames(1); }
        PostMouse(0x200, p, mods); yield return Frames(2);
        PostMouse(0x201, p, 1 | mods); yield return Frames(2);
        PostMouse(0x202, p, mods); yield return Frames(2);
        if ((mods & MkShift) != 0) { PostKey(Keys.ShiftKey, false); yield return Frames(1); }
        if ((mods & MkCtrl) != 0) { PostKey(Keys.ControlKey, false); yield return Frames(1); }
        yield return Frames(1);
    }

    IEnumerable<object> UKey(Keys k, bool ctrl = false, int hold = 2)
    {
        if (ctrl) { PostKey(Keys.ControlKey, true); yield return Frames(1); }
        PostKey(k, true); yield return Frames(hold); PostKey(k, false); yield return Frames(1);
        if (ctrl) { PostKey(Keys.ControlKey, false); yield return Frames(1); }
        yield return Frames(1);
    }

    Vector2 FaceOnScreen(Viewport vp, SceneObject o, int fi) => ScreenOf(vp, Vector3.Transform(o.Mesh.FaceCenter(fi), o.World));

    static string Faces(IEnumerable<int> f) => "{" + string.Join(",", f.OrderBy(i => i)) + "}";

    IEnumerable<object> UserFaceSelection()
    {
        var keymap0 = Settings.Keymap;
        UI.ImGuiBackend.SyntheticInput = true;
        try
        {
            foreach (var km in new[] { Keymap.Max, Keymap.Industry, Keymap.Blender })
            {
                Fresh();
                Settings.Keymap = km;
                // a strip of 8 quads, like a stair: one face loop runs over the whole mesh
                var o = Prim(CreateKind.Box, Vector3.Zero); o.Name = "strip";
                Do("strip", () => { o.Mesh = Primitives.Plane(8, 1, 8, 1); o.Touch(); });
                SelectOnly(o); SetLevel(SubObject.Polygon);
                var vp = Viewports[ActiveViewport];
                vp.Camera.SetView(ViewKind.Perspective); vp.Camera.Yaw = -MathF.PI / 2; vp.Camera.Pitch = 1.25f;
                vp.Camera.Frame(new Vector3(-4.5f, -1, 0), new Vector3(4.5f, 1, 0.2f));
                yield return Frames(3);
                var m = o.Mesh; int n = m.Faces.Count;
                // faces ordered along X
                var order = Enumerable.Range(0, n).OrderBy(i => m.FaceCenter(i).X).ToArray();
                int addMod = km == Keymap.Max ? MkCtrl : MkShift; // Max: Ctrl adds; Standard and Industry: Shift adds
                var s = SubSel(o);
                foreach (var x in UClick(FaceOnScreen(vp, o, order[1]))) yield return x;
                Expect(s.Faces.SetEquals(new[] { order[1] }), $"[{km}] click a face: only that face ({Faces(s.Faces)})");
                // the second click comes fast, inside the double-click time: it must stay one more face
                foreach (var x in UClick(FaceOnScreen(vp, o, order[4]), addMod)) yield return x;
                s = SubSel(o);
                Expect(s.Faces.SetEquals(new[] { order[1], order[4] }), $"[{km}] quick {(addMod == MkCtrl ? "Ctrl" : "Shift")}+click a 2nd face: 2 faces, not the whole loop ({Faces(s.Faces)} of {n})");
                foreach (var x in UClick(FaceOnScreen(vp, o, order[6]))) yield return x;
                s = SubSel(o);
                Expect(s.Faces.SetEquals(new[] { order[6] }), $"[{km}] quick plain click on another face: just that face ({Faces(s.Faces)})");
                foreach (var x in UKey(Keys.Up, ctrl: true)) yield return x;
                s = SubSel(o);
                Expect(s.Faces.SetEquals(new[] { order[5], order[6], order[7] }), $"[{km}] Ctrl+Up grows by one ring ({Faces(s.Faces)})");
                foreach (var x in UKey(Keys.Down, ctrl: true)) yield return x;
                s = SubSel(o);
                Expect(s.Faces.SetEquals(new[] { order[6] }), $"[{km}] Ctrl+Down shrinks back ({Faces(s.Faces)})");
                // the arrows alone keep moving the camera in edit mode, and leave the selection alone
                // the mouse over the view (over a panel the arrows go to its list)
                PostMouse(0x200, new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.5f), 0); yield return Frames(3);
                var eye0 = vp.Camera.Eye;
                foreach (var x in UKey(Keys.Up, hold: 25)) yield return x;
                yield return Frames(15);
                var moved = Vector3.Distance(eye0, vp.Camera.Eye);
                s = SubSel(o);
                Expect(moved > 0.1f && s.Faces.SetEquals(new[] { order[6] }), $"[{km}] Up arrow moves the camera in face mode ({moved:0.00} m), selection kept ({Faces(s.Faces)})");
                yield return Shot("user_faces_" + km.ToString().ToLowerInvariant());
                SetLevel(SubObject.Object);
            }
        }
        finally { Settings.Keymap = keymap0; UI.ImGuiBackend.SyntheticInput = false; }
    }

    IEnumerable<object> UserKnife()
    {
        UI.ImGuiBackend.SyntheticInput = true;
        int snap0 = knifeSnap; bool world0 = knifeWorld, vis0 = knifeVisibleOnly;
        try
        {
            Fresh();
            var o = Prim(CreateKind.Box, Vector3.Zero); o.Name = "knife_box";
            SelectOnly(o); SetLevel(SubObject.Polygon);
            var vp = Viewports[ActiveViewport];
            vp.Camera.SetView(ViewKind.Front);
            var (mn, mx) = o.Mesh.Bounds();
            vp.Camera.Frame(Vector3.Transform(mn, o.World), Vector3.Transform(mx, o.World));
            yield return Frames(3);
            var w = o.World;
            float sx = mx.X - mn.X, sz = mx.Z - mn.Z;
            Vector3 W(float fx, float fz) => Vector3.Transform(new Vector3(mn.X + sx * fx, mn.Y, mn.Z + sz * fz), w);

            // 1. a cut that starts in the middle of the front face and leaves on the right
            knifeSnap = 0; knifeWorld = false; knifeVisibleOnly = false;
            int f0 = o.Mesh.Faces.Count;
            BeginModal(ModalKind.Knife); yield return Frames(2);
            foreach (var x in UClick(ScreenOf(vp, W(0.5f, 0.5f)))) yield return x;
            foreach (var x in UClick(ScreenOf(vp, W(1.4f, 0.5f)))) yield return x;
            Expect(o.Mesh.Faces.Count > f0, $"cut starting inside the front face cuts it ({f0} -> {o.Mesh.Faces.Count} faces)");
            CancelModal(); yield return Frames(2);

            // 2. 90° step: a line drawn a bit crooked comes out plumb
            DoUndo(); yield return Frames(2); o = Scene.Find(o.Id); SelectOnly(o); SetLevel(SubObject.Polygon);
            knifeSnap = 90;
            int v0 = o.Mesh.Verts.Count;
            BeginModal(ModalKind.Knife); yield return Frames(2);
            var top = ScreenOf(vp, W(0.35f, 1.3f)); var bottom = ScreenOf(vp, W(0.35f, -0.3f)) + new Vector2(25, 0);
            foreach (var x in UClick(top)) yield return x;
            PostMouse(0x200, bottom, 0); yield return Frames(2);
            var shown = KnifeEnd();
            Expect(MathF.Abs(shown.X - top.X) < 1f, $"the drawn line snaps to vertical before the click (x {top.X:0} / {shown.X:0})");
            foreach (var x in UClick(bottom)) yield return x;
            var newX = o.Mesh.Verts.Skip(v0).Select(v => Vector3.Transform(v, o.World).X).ToList();
            Expect(newX.Count > 0 && newX.Max() - newX.Min() < 2e-3f, $"90° step: the cut is straight ({newX.Count} new points, x spread {(newX.Count > 0 ? newX.Max() - newX.Min() : 0):0.0000} m)");
            yield return Shot("user_knife_90");
            CancelModal(); yield return Frames(2);

            // 3. straight on world axes in a perspective view: a slanted line still gives one level / plumb cut
            DoUndo(); yield return Frames(2); o = Scene.Find(o.Id); SelectOnly(o); SetLevel(SubObject.Polygon);
            knifeSnap = 0; knifeWorld = true;
            vp.Camera.SetView(ViewKind.Perspective);
            vp.Camera.Frame(Vector3.Transform(mn, o.World), Vector3.Transform(mx, o.World));
            yield return Frames(3);
            v0 = o.Mesh.Verts.Count;
            var c3 = Vector3.Transform((mn + mx) / 2, o.World);
            var a = ScreenOf(vp, c3 + new Vector3(0, 0, sz / 2)) - new Vector2(0, 40); var b = ScreenOf(vp, c3 - new Vector3(0, 0, sz / 2)) + new Vector2(30, 40); // inside the view, a slanted line
            BeginModal(ModalKind.Knife); yield return Frames(2);
            foreach (var x in UClick(a)) yield return x;
            Note($"first click at {a:0}: line started {knifeA.HasValue}, modal {modal.Kind}");
            foreach (var x in UClick(b)) yield return x;
            Note($"second click at {b:0}: line open {knifeA.HasValue}, faces {o.Mesh.Faces.Count}, status '{status}'");
            var pts = o.Mesh.Verts.Skip(v0).Select(v => Vector3.Transform(v, o.World)).ToList();
            float Spread(Func<Vector3, float> f) => pts.Count == 0 ? 99 : pts.Max(f) - pts.Min(f);
            float best = MathF.Min(Spread(p => p.X), MathF.Min(Spread(p => p.Y), Spread(p => p.Z)));
            Expect(pts.Count > 0 && best < 2e-3f, $"straight on axes: all new points on one axis plane ({pts.Count} points, spread {best:0.0000} m)");
            yield return Shot("user_knife_axes");
            CancelModal(); yield return Frames(2);
            SetLevel(SubObject.Object);
        }
        finally { knifeSnap = snap0; knifeWorld = world0; knifeVisibleOnly = vis0; UI.ImGuiBackend.SyntheticInput = false; }
    }

    IEnumerable<object> UserLoopCut()
    {
        UI.ImGuiBackend.SyntheticInput = true;
        int cuts0 = loopCuts; bool cross0 = loopCross;
        try
        {
            Fresh();
            var o = Prim(CreateKind.Box, Vector3.Zero); o.Name = "loop_box";
            SelectOnly(o); SetLevel(SubObject.Edge);
            var vp = Viewports[ActiveViewport];
            vp.Camera.SetView(ViewKind.Perspective);
            var (mn, mx) = o.Mesh.Bounds();
            vp.Camera.Frame(Vector3.Transform(mn, o.World), Vector3.Transform(mx, o.World));
            yield return Frames(3);
            var m = o.Mesh;
            // a vertical edge of the box, its middle on screen
            var e = m.BuildEdgeMap().Keys.Where(k => MathF.Abs(m.Verts[k.A].Z - m.Verts[k.B].Z) > 0.5f)
                .OrderBy(k => Vector3.Distance(Vector3.Transform((m.Verts[k.A] + m.Verts[k.B]) / 2, o.World), vp.Camera.Eye)).First();
            var mid = ScreenOf(vp, Vector3.Transform((m.Verts[e.A] + m.Verts[e.B]) / 2, o.World));

            // 1. two loops, + makes three, click: 3 level loops (12 new edges on a box)
            loopCuts = 2; loopCross = false;
            int f0 = o.Mesh.Faces.Count, v0 = o.Mesh.Verts.Count;
            BeginModal(ModalKind.LoopCut); yield return Frames(2);
            PostMouse(0x200, mid, 0); yield return Frames(3);
            foreach (var x in UKey(Keys.Add)) yield return x;
            Expect(loopCuts == 3 && modal.Preview.Count == 12, $"+ adds a loop: {loopCuts} loops, preview {modal.Preview.Count} segments");
            yield return Shot("user_loopcut_preview");
            foreach (var x in UClick(mid)) yield return x;
            var zs = o.Mesh.Verts.Skip(v0).Select(v => MathF.Round(v.Z, 2)).Distinct().Count();
            Expect(o.Mesh.Faces.Count == f0 + 12 && zs == 3, $"3 loops cut: {f0} -> {o.Mesh.Faces.Count} faces, at {zs} heights");
            if (ModalActive) CancelModal();
            yield return Frames(2);

            // 2. Tab: the same edge, the cut turned across (an upright loop)
            DoUndo(); yield return Frames(2); o = Scene.Find(o.Id); SelectOnly(o); SetLevel(SubObject.Edge);
            loopCuts = 1;
            v0 = o.Mesh.Verts.Count;
            BeginModal(ModalKind.LoopCut); yield return Frames(2);
            foreach (var x in UKey(Keys.Tab)) yield return x;
            PostMouse(0x200, mid + new Vector2(1, 0), 0); yield return Frames(3);
            Expect(loopCross, "Tab turns the cut the other way");
            foreach (var x in UClick(mid + new Vector2(1, 0))) yield return x;
            var nv = o.Mesh.Verts.Skip(v0).ToList();
            bool upright = nv.Count > 0 && nv.Select(v => v.Z).Max() - nv.Select(v => v.Z).Min() > 0.5f;
            Expect(upright, $"after Tab the loop stands up ({nv.Count} new points)");
            yield return Shot("user_loopcut_across");
            if (ModalActive) CancelModal();
            yield return Frames(2);
            SetLevel(SubObject.Object);
        }
        finally { loopCuts = cuts0; loopCross = cross0; UI.ImGuiBackend.SyntheticInput = false; }
    }

    IEnumerable<object> UserDenseDrawable()
    {
        UI.ImGuiBackend.SyntheticInput = true;
        try
        {
            Fresh();
            // a GTA room mesh: ~49 000 points, inside a Drawable, edited in vertex mode
            var m = Prim(CreateKind.Box, Vector3.Zero); m.Name = "room_03_high_0";
            Do("dense", () => { m.Mesh = Primitives.Plane(10, 10, 220, 220); m.Touch(); });
            SelectOnly(m); CreateDrawable(); yield return Frames(4);
            m = Scene.Find(m.Id); var node = Scene.Parent(m);
            Expect(node != null, $"Drawable made ({node?.Name})");
            if (node == null) yield break;
            var vp = Viewports[ActiveViewport];
            vp.Camera.SetView(ViewKind.Perspective); vp.Camera.Pitch = 1.2f; vp.Camera.Frame(new Vector3(-5, -5, 0), new Vector3(5, 5, 0.1f));
            SelectOnly(m); SetLevel(SubObject.Vertex); yield return Frames(4);
            int n = m.Mesh.Verts.Count;
            // mouse far from the mesh (corner of the view): every point must still get its tick
            PostMouse(0x200, new Vector2(vp.X + 30, vp.Y + vp.H - 30), 0); yield return Frames(4);
            Expect(vertexTicksDrawn > n * 0.9f, $"every point of a {n}-point mesh is shown, not only near the mouse ({vertexTicksDrawn} drawn)");
            yield return Shot("user_dense_ticks");

            // Separate in vertex mode: the faces whose points are all selected become a new object
            var grid = Enumerable.Range(0, n).Where(i => MathF.Abs(m.Mesh.Verts[i].X) < 0.06f && MathF.Abs(m.Mesh.Verts[i].Y) < 0.06f).ToList();
            SubSel(m).Clear(); foreach (var i in grid) SubSel(m).Verts.Add(i);
            int faces0 = m.Mesh.Faces.Count, objs0 = Scene.Objects.Count;
            DetachFaces(false); yield return Frames(3);
            m = Scene.Find(m.Id);
            var part = Scene.Objects.LastOrDefault(o => o.Name.StartsWith(m.Name + "_detached"));
            Expect(part != null && part.Mesh.Faces.Count > 0 && m.Mesh.Faces.Count == faces0 - part.Mesh.Faces.Count,
                $"Separate from {grid.Count} selected points: {part?.Mesh.Faces.Count ?? 0} faces to a new object ({faces0} -> {m.Mesh.Faces.Count})");
            DoUndo(); yield return Frames(3); m = Scene.Find(m.Id); node = Scene.Find(node.Id);
            Expect(m.Mesh.Faces.Count == faces0 && Scene.Objects.Count == objs0, "undo puts the faces back");

            // still in vertex mode: click the Drawable row, then its eye: the mesh is hidden
            SelectOnly(m); SetLevel(SubObject.Vertex); yield return Frames(6);
            foreach (var x in ClickAt(TutCentre("row:" + node.Id), moveFrames: 6)) yield return x;
            yield return Frames(3);
            Note($"after the row click: selected {string.Join(",", Selected.Select(i => Scene.Find(i)?.Name))}, level {Level}, current {Current?.Name}");
            foreach (var x in ClickAt(TutCentre("eye:" + node.Id), moveFrames: 6)) yield return x;
            yield return Frames(4);
            m = Scene.Find(m.Id); node = Scene.Find(node.Id);
            Expect(node.Hidden && m.Hidden, $"Drawable row then its eye, from vertex mode: hidden (node {node.Hidden}, mesh {m.Hidden}, level {Level})");
            yield return Shot("user_dense_eye");
            foreach (var x in ClickAt(TutCentre("eye:" + node.Id), moveFrames: 6)) yield return x;
            yield return Frames(3);
            Expect(!Scene.Find(node.Id).Hidden && !Scene.Find(m.Id).Hidden, "the eye again shows it");
            // the eye of the Drawable while its mesh is being edited (row not clicked first)
            SelectOnly(m); SetLevel(SubObject.Vertex); yield return Frames(6);
            foreach (var x in ClickAt(TutCentre("eye:" + node.Id), moveFrames: 6)) yield return x;
            yield return Frames(4);
            Expect(Scene.Find(node.Id).Hidden && Scene.Find(m.Id).Hidden, $"Drawable eye while editing its mesh: hidden (level {Level})");
            foreach (var t in Scene.Objects) t.Hidden = false;
            // a click that comes with no move before it (first click after another window had the mouse): it must land
            // where it was made, on the eye, not on the viewport point where the mouse was last seen
            SelectOnly(m); SetLevel(SubObject.Vertex); SubSel(m).Clear(); yield return Frames(4);
            var inView = new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.5f);
            PostMouse(0x200, inView, 0); yield return Frames(4);
            var eye = TutCentre("eye:" + node.Id);
            PostMouse(0x201, eye, 1); yield return Frames(3);
            PostMouse(0x202, eye, 0); yield return Frames(4);
            Expect(Scene.Find(node.Id).Hidden && SubSel(m).Verts.Count == 0, $"a click with no move before it hits the eye (hidden {Scene.Find(node.Id).Hidden}), not the view ({SubSel(m).Verts.Count} points picked)");
            foreach (var t in Scene.Objects) t.Hidden = false;
            SetLevel(SubObject.Object);
        }
        finally { UI.ImGuiBackend.SyntheticInput = false; }
    }

    IEnumerable<object> UserDetachEye()
    {
        UI.ImGuiBackend.SyntheticInput = true;
        try
        {
            Fresh();
            var m = Prim(CreateKind.Box, Vector3.Zero); m.Name = "ext_58_high_0";
            Do("grid", () => { m.Mesh = Primitives.Plane(10, 10, 30, 30); m.Touch(); });
            SelectOnly(m); CreateDrawable(); yield return Frames(4);
            m = Scene.Find(m.Id);
            var vp = Viewports[ActiveViewport];
            vp.Camera.SetView(ViewKind.Perspective); vp.Camera.Pitch = 1.2f; vp.Camera.Frame(new Vector3(-5, -5, 0), new Vector3(5, 5, 0.1f));
            SelectOnly(m); SetLevel(SubObject.Polygon); yield return Frames(4);
            SubSel(m).Clear();
            for (int i = 0; i < m.Mesh.Faces.Count; i++) { var c = Vector3.Zero; foreach (var v in m.Mesh.Faces[i].V) c += m.Mesh.Verts[v]; c /= m.Mesh.Faces[i].V.Length; if (c.X > 2 && c.Y > 2) SubSel(m).Faces.Add(i); }
            DetachFaces(false); yield return Frames(4);
            var part = Scene.Objects.LastOrDefault(o => o.Name.StartsWith("ext_58_high_0_detached"));
            Expect(part != null, $"detached part made ({part?.Name}, {part?.Mesh.Faces.Count} faces)");
            if (part == null) yield break;
            SetLevel(SubObject.Object); SelectOnly(part); CreateDrawable(); yield return Frames(6);
            part = Scene.Find(part.Id); var node = Scene.Parent(part);
            Note($"part {part.Name} under {node?.Name}");
            string St() => $"mesh hidden {Scene.Find(part.Id).Hidden}, node hidden {Scene.Find(node.Id).Hidden}, drawn {drawList.Any(o => o.Id == part.Id)}, level {Level}, selected [{string.Join(",", Selected.Select(i => Scene.Find(i)?.Name))}]";
            IEnumerable<object> Eye(string what)
            {
                yield return Frames(6);
                var at = TutCentre("eye:" + part.Id);
                Note($"{what}: eye of the mesh row at {at.X:0},{at.Y:0}; before: {St()}");
                foreach (var x in ClickAt(at, moveFrames: 6)) yield return x;
                yield return Frames(6);
                Note($"{what}: after: {St()}");
                Expect(Scene.Find(part.Id).Hidden && !drawList.Any(o => o.Id == part.Id), $"{what}: the eye of the mesh row hides it ({St()})");
                foreach (var t in Scene.Objects) t.Hidden = false;
            }
            SelectNone(); foreach (var x in Eye("nothing selected")) yield return x;
            SelectOnly(part); foreach (var x in Eye("the mesh selected")) yield return x;
            SelectOnly(node); foreach (var x in Eye("the Drawable selected")) yield return x;
            SelectOnly(m); SetLevel(SubObject.Polygon); foreach (var x in Eye("face mode on the original")) yield return x;
            SetLevel(SubObject.Object);
            // the row clicked first (selects it), then its eye, like a user does
            SelectNone(); yield return Frames(4);
            foreach (var x in ClickAt(TutCentre("row:" + part.Id), moveFrames: 6)) yield return x;
            yield return Frames(30);
            foreach (var x in Eye("row clicked, then its eye")) yield return x;
            yield return Shot("user_detach_eye");
        }
        finally { UI.ImGuiBackend.SyntheticInput = false; SetLevel(SubObject.Object); }
    }

    IEnumerable<object> UserJoinNodes()
    {
        Fresh(); paintMode = false; SculptMode = false;
        var meshes = new List<SceneObject>();
        for (int i = 0; i < 4; i++)
        {
            var q = Prim(CreateKind.Box, new Vector3(i * 3, 0, 0)); q.Name = $"room_{i:00}_high_0";
            SelectOnly(q); CreateDrawable(); yield return Frames(2);
            meshes.Add(Scene.Find(q.Id));
        }
        var nodes = meshes.Select(m => Scene.Parent(Scene.Find(m.Id))).ToList();
        int faces = meshes.Take(3).Sum(m => m.Mesh.Faces.Count);
        Scene.Find(meshes[3].Id).Hidden = true; // a hidden one in the range
        // the user selects the Drawable rows (a Shift range), the first one active
        Selected.Clear(); foreach (var n in nodes) Selected.Add(n.Id); foreach (var m in meshes) Selected.Add(m.Id); ActiveId = nodes[0].Id; SelectionChanged();
        yield return Frames(2);
        JoinSelected(); yield return Frames(4);
        var nodeFaces = Scene.Objects.Where(o => o.GtaType == GtaObjectType.Model).Sum(o => o.Mesh?.Faces.Count ?? 0);
        var target = Scene.Find(meshes[0].Id);
        Note($"after the join: {string.Join(", ", Scene.Objects.Select(o => $"{o.Name}({o.Mesh?.Faces.Count}{(o.Hidden ? ",H" : "")})"))}");
        Expect(nodeFaces == 0, $"no Drawable node holds faces ({nodeFaces})");
        Expect(target != null && target.Mesh.Faces.Count == faces, $"the active Drawable's mesh received the visible meshes ({target?.Mesh.Faces.Count} of {faces} faces)");
        Expect(Scene.Find(meshes[3].Id) is { Hidden: true } && Scene.Find(nodes[3].Id) != null, "the hidden mesh and its Drawable are left alone");
        Expect(Scene.Find(nodes[1].Id) == null && Scene.Find(nodes[2].Id) == null, "the emptied Drawables are removed");
        Expect(Selected.Count == 1 && Selected.Contains(meshes[0].Id), "the joined mesh is selected (it can be clicked in the view)");
        DoUndo(); yield return Frames(3);
        Expect(Scene.Find(nodes[1].Id) != null && Scene.Find(meshes[1].Id) != null, "undo brings everything back");
        // only the node selected (no mesh of its own yet): a new mesh under it receives the others
        Fresh();
        var a = Prim(CreateKind.Box, Vector3.Zero); a.Name = "a_high_0"; SelectOnly(a); CreateDrawable(); yield return Frames(2);
        var na = Scene.Parent(Scene.Find(a.Id));
        Scene.RemoveObjects(x => x.Id == a.Id); // an empty Drawable
        var b = Prim(CreateKind.Box, new Vector3(3, 0, 0)); var c = Prim(CreateKind.Box, new Vector3(6, 0, 0));
        Selected.Clear(); Selected.Add(na.Id); Selected.Add(b.Id); Selected.Add(c.Id); ActiveId = na.Id; SelectionChanged();
        JoinSelected(); yield return Frames(3);
        var kid = Scene.Objects.FirstOrDefault(o => Scene.Parent(o) == Scene.Find(na.Id));
        Expect(Scene.Find(na.Id).Mesh.Faces.Count == 0 && kid != null && kid.Mesh.Faces.Count == b.Mesh.Faces.Count * 2, $"empty Drawable active: a new mesh under it gets the faces ({kid?.Name}, {kid?.Mesh.Faces.Count})");
    }

    IEnumerable<object> UserEyeAfterUndo()
    {
        UI.ImGuiBackend.SyntheticInput = true;
        try
        {
            Fresh(); paintMode = false; SculptMode = false; Tool = Tool.Select;
            var meshes = new List<SceneObject>();
            for (int i = 0; i < 3; i++)
            {
                var q = Prim(CreateKind.Box, new Vector3(i * 3, 0, 0)); q.Name = $"room_{i:00}_high_0";
                SelectOnly(q); CreateDrawable(); yield return Frames(2);
                meshes.Add(Scene.Find(q.Id));
            }
            SelectNone(); yield return Frames(6);
            foreach (var (what, act) in new (string, Action)[] { ("undo", () => { Do("move", () => Scene.Find(meshes[2].Id).Position += Vector3.UnitZ); DoUndo(); }), ("redo", () => DoRedo()) })
            {
                act(); yield return Frames(6);
                var live = Scene.Find(meshes[1].Id);
                Note($"after {what}: the object is {(ReferenceEquals(live, meshes[1]) ? "the same" : "a new copy")}");
                SelectOnly(live); yield return Frames(6);
                foreach (var x in ClickAt(TutCentre("eye:" + live.Id), moveFrames: 6)) yield return x;
                yield return Frames(6);
                Expect(Scene.Find(live.Id).Hidden && !drawList.Any(o => o.Id == live.Id), $"after {what}: the eye of the selected mesh row hides it (hidden {Scene.Find(live.Id).Hidden})");
                foreach (var x in ClickAt(TutCentre("eye:" + live.Id), moveFrames: 6)) yield return x;
                yield return Frames(4);
                Expect(!Scene.Find(live.Id).Hidden, $"after {what}: the eye again shows it");
            }
        }
        finally { UI.ImGuiBackend.SyntheticInput = false; }
    }

    IEnumerable<object> UserPaintDrips()
    {
        Fresh();
        var wall = Prim(CreateKind.Box, Vector3.Zero); wall.Name = "drip_wall";
        Do("wall", () => { wall.Mesh = Primitives.Box(3f, 0.3f, 2.4f); wall.Touch(); });
        SelectOnly(wall);
        DoMesh("Smart UV Project", (oo, mm, s2) => UvUnwrap.SmartProject(mm));
        wall = Scene.Find(wall.Id);
        paintRes = 1024; StartPaint(wall); yield return Frames(4);
        var ts = wall.Paint?.Cur;
        Expect(ts != null, "paint started");
        if (ts == null) yield break;
        AddLayer(ts, new PaintLayer { Name = "Rust drips", Kind = LayerKind.Paint });
        var keep = (brush.Drips, brush.DripLength, brush.Radius, brush.ColorValue, brush.Strength);
        paintTool = PaintTool.Brush; brush.Erase = false; brush.Stamp = null; brush.Tip = null;
        brush.Drips = 1; brush.DripLength = 1.2f; brush.Radius = 0.12f; brush.Strength = 1; brush.ColorValue = new Vector3(0.45f, 0.2f, 0.07f);
        // the front face (-Y): prints along a line near the top
        float front = wall.Mesh.Verts.Min(v => v.Y);
        for (int i = 0; i < 12; i++) StampAll(wall, ts, new Vector3(-1.2f + i * 0.2f, front, 2.1f), -Vector3.UnitY, brush.Radius, new Vector3(-1.2f + i * 0.2f, front, 2.1f));
        ts.Refresh(true); UploadPaint(wall, ts, true);
        var l = ts.Current;
        int low = 0;
        for (int t = 0; t < ts.Map.Count; t++) if (l.Cover?[t] > 0.3f && ts.Map.Pos[t].Z < 1.7f && ts.Map.Nrm[t].Y < -0.5f) low++;
        Note($"painted texels well below the prints (z < 1.7 m): {low}");
        Expect(low > 20, $"drops ran down the wall ({low} texels below the prints)");
        var vp = Viewports[ActiveViewport]; vp.Camera.SetView(ViewKind.Perspective); vp.Camera.Target = new Vector3(0, 0, 1.2f); vp.Camera.Distance = 4.5f; vp.Camera.Yaw = -MathF.PI / 2; vp.Camera.Pitch = 0.1f;
        yield return Frames(4);
        yield return Shot("user_paint_drips");
        (brush.Drips, brush.DripLength, brush.Radius, brush.ColorValue, brush.Strength) = keep;
    }

    IEnumerable<object> UserPlaceFromYmap()
    {
        var dir = Path.Combine(SelfTestDir, "place_ymap"); Directory.CreateDirectory(Path.Combine(dir, "stream"));
        var src = new Scene();
        var node = src.Add("pm_a", new MoloMax.Core.Mesh.PolyMesh(), new Vector3(2, 0, 0), GtaObjectType.Model);
        var mesh = src.Add("pm_a_high_0", Primitives.Box(1, 1, 1), Vector3.Zero, GtaObjectType.Drawable); src.SetParent(mesh, node);
        mesh.Position = Vector3.Zero; mesh.Rotation = Quaternion.Identity;
        var b = src.Add("pm_b", Primitives.Box(1, 1, 1), new Vector3(6, 0, 0), GtaObjectType.Drawable);
        // the .ymap of that resource: the scene turned 90° and moved to 100, 200, 30
        var place = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(100, 200, 30);
        var y = MoloMax.Core.Gta.Map.MapDocument.New("pm_test");
        foreach (var o in new[] { node, b }) { Matrix4x4.Decompose(o.World * place, out _, out var r, out var t); y.Add(o.Name, t, r, Vector3.One); }
        var ypath = Path.Combine(dir, "stream", "pm_test.ymap");
        File.WriteAllBytes(ypath, y.Save());
        var (m, n, info) = PlacementFromYmap(src, ypath);
        Note($"placement from the .ymap: {info}");
        Expect(m != null && n == 2, $"both models found in the .ymap ({n})");
        if (m != null)
        {
            var at = Vector3.Transform(Vector3.Zero, b.World * m.Value);
            Expect(Vector3.Distance(at, new Vector3(100, 206, 30)) < 0.01f, $"pm_b goes to 100, 206, 30 ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})");
        }
        AskPlaceOnMap(src, "test scene", dir); yield return Frames(4);
        Expect(pendingPlace is { Mode: 0, FromYmap: not null } && pendingPlace.Ymaps.Count == 1, $"the dialog found the .ymap by itself and picked it ({pendingPlace?.Ymaps.Count} found, mode {pendingPlace?.Mode})");
        yield return Shot("user_place_ymap");
        placeCancel = true; yield return Frames(3);
        Expect(pendingPlace == null && !ImGuiNET.ImGui.IsPopupOpen("Put on the map###mapplace", ImGuiNET.ImGuiPopupFlags.AnyPopupId), "Cancel closes it");
    }

    IEnumerable<object> UserCtrlNoSnap()
    {
        UI.ImGuiBackend.SyntheticInput = true;
        var keymap0 = Settings.Keymap; bool snap0 = Snap;
        try
        {
            foreach (var km in new[] { Keymap.Industry, Keymap.Blender })
            {
                Fresh(); Settings.Keymap = km; Snap = false;
                var b = Prim(CreateKind.Box, Vector3.Zero); SelectOnly(b); yield return Frames(3);
                PostKey(Keys.ControlKey, true); yield return Frames(4);
                Expect(!SnapActive, $"[{km}] Ctrl held, snap off: the snap stays off ({SnapActive})");
                PostKey(Keys.ControlKey, false); yield return Frames(3);
                Snap = true; PostKey(Keys.ControlKey, true); yield return Frames(4);
                Expect(SnapActive, $"[{km}] Ctrl held, snap on: it stays on ({SnapActive})");
                PostKey(Keys.ControlKey, false); yield return Frames(3);
                Snap = false;
            }
        }
        finally { Settings.Keymap = keymap0; Snap = snap0; UI.ImGuiBackend.SyntheticInput = false; }
    }

    IEnumerable<object> UserShiftEye()
    {
        UI.ImGuiBackend.SyntheticInput = true;
        var keymap0 = Settings.Keymap;
        try
        {
            foreach (var km in new[] { Keymap.Max, Keymap.Industry, Keymap.Blender })
            foreach (bool holdShift in new[] { false, true })
            {
                Fresh(); Settings.Keymap = km; paintMode = false; SculptMode = false; Tool = Tool.Select;
                var meshes = new List<SceneObject>();
                for (int i = 0; i < 4; i++)
                {
                    var q = Prim(CreateKind.Box, new Vector3(i * 3, 0, 0)); q.Name = $"ext_{i:00}_high_0";
                    SelectOnly(q); CreateDrawable(); yield return Frames(2);
                    meshes.Add(Scene.Find(q.Id));
                }
                SelectNone(); yield return Frames(10);
                string tag = $"[{km}{(holdShift ? ", Shift held on the eye" : "")}]";
                // click mesh 0, Shift+click mesh 2: a range of rows (meshes and the Drawable rows between)
                foreach (var x in UClick(TutCentre("row:" + meshes[0].Id))) yield return x;
                yield return Frames(20);
                foreach (var x in UClick(TutCentre("row:" + meshes[2].Id), MkShift)) yield return x;
                yield return Frames(20);
                var sel = Selected.ToList();
                Note($"{tag} selected: {string.Join(", ", sel.Select(i => Scene.Find(i)?.Name))}");
                Expect(sel.Contains(meshes[0].Id) && sel.Contains(meshes[2].Id) && sel.Count >= 3, $"{tag} Shift+click selects the range ({sel.Count})");
                // the eye of mesh 1 (inside the range)
                var eye = TutCentre("eye:" + meshes[1].Id);
                if (holdShift) { PostKey(Keys.ShiftKey, true); yield return Frames(2); }
                foreach (var x in UClick(eye, holdShift ? MkShift : 0)) yield return x;
                if (holdShift) { PostKey(Keys.ShiftKey, false); yield return Frames(2); }
                yield return Frames(6);
                string St() => string.Join(" ", meshes.Select(m => Scene.Find(m.Id).Hidden ? "H" : "v"));
                Note($"{tag} after the eye: {St()}, selected {Selected.Count}");
                Expect(sel.All(i => Scene.Find(i)?.Hidden == true) && !Scene.Find(meshes[3].Id).Hidden, $"{tag} the eye hides every selected row, not the 4th ({St()})");
                if (km == Keymap.Max && !holdShift) yield return Shot("user_shift_eye");
                foreach (var x in UClick(eye, holdShift ? MkShift : 0)) yield return x;
                yield return Frames(6);
                Expect(meshes.All(m => !Scene.Find(m.Id).Hidden), $"{tag} the eye again shows them ({St()})");
            }
        }
        finally { Settings.Keymap = keymap0; UI.ImGuiBackend.SyntheticInput = false; }
    }

    IEnumerable<object> UserBigSelection()
    {
        Fresh();
        var objs = new List<SceneObject>();
        for (int i = 0; i < 90; i++)
        {
            var o = Prim(CreateKind.Box, new Vector3((i % 10) * 3, (i / 10) * 3, 0)); o.Name = $"piece_{i:00}";
            o.Mesh = Primitives.Plane(2, 2, 100, 100); o.Touch(); // 10 201 points each, ~920 000 in all
            objs.Add(o);
        }
        Maximized = false; // the four views, like a Classic layout
        foreach (var v in Viewports) v.Camera.Frame(new Vector3(-2, -2, -1), new Vector3(30, 30, 2));
        Selected.Clear(); SelectionChanged(); yield return Frames(10);
        double before = FrameProfile.GetValueOrDefault("viewports");
        foreach (var o in objs) Selected.Add(o.Id); SelectionChanged();
        yield return Frames(40); // past the first frames that build the outlines once
        double after = FrameProfile.GetValueOrDefault("viewports");
        yield return Shot("user_big_selection");
        Expect(after < Math.Max(40, before * 4), $"90 objects selected: views {before:0.0} ms -> {after:0.0} ms per frame (outline meshes kept, not rebuilt every frame)");
        // Hide on a selected Drawable hides its meshes
        SelectOnly(objs[0]); CreateDrawable(); yield return Frames(3);
        var node = Scene.Parent(Scene.Find(objs[0].Id));
        SelectOnly(node); HideSelection(); yield return Frames(3);
        Expect(Scene.Find(node.Id).Hidden && Scene.Find(objs[0].Id).Hidden, "Hide on a selected Drawable hides it and its mesh");
        UnhideAll(); Maximized = true; yield return Frames(2);
    }

    IEnumerable<object> UserWalkLeaves()
    {
        var keymap0 = Settings.Keymap;
        UI.ImGuiBackend.SyntheticInput = true;
        try
        {
            Fresh(); Settings.Keymap = Keymap.Blender;
            Prim(CreateKind.Box, Vector3.Zero);
            Selected.Clear(); SelectionChanged(); // nothing selected: the middle button navigates (on a selection it moves it)
            var vp = Viewports[ActiveViewport]; var cam = vp.Camera;
            cam.SetView(ViewKind.Perspective); cam.Frame(new Vector3(-20, -20, 0), new Vector3(20, 20, 5));
            var mid = new Vector2(vp.X + vp.W * 0.5f, vp.Y + vp.H * 0.5f);
            PostMouse(0x200, mid, 0); yield return Frames(3);
            float d0 = cam.Distance;
            // Ctrl + Up in object mode: no walk
            var e0 = cam.Eye;
            foreach (var x in UKey(Keys.Up, ctrl: true, hold: 20)) yield return x;
            yield return Frames(10);
            Expect(Vector3.Distance(e0, cam.Eye) < 0.01f, $"Ctrl+Up does not walk ({Vector3.Distance(e0, cam.Eye):0.00} m)");
            // walk with Up: the orbit distance gets short
            foreach (var x in UKey(Keys.Up, hold: 20)) yield return x;
            yield return Frames(15);
            float dWalk = cam.Distance;
            Expect(dWalk <= 6.01f && d0 > 6.01f, $"walking: orbit distance {d0:0.0} -> {dWalk:0.0} m");
            // a middle-button orbit: the walk mode leaves, the distance comes back, the eye does not jump
            var eye = cam.Eye;
            Note($"before the orbit: remembered {walkDistance.Count}, fly speed {flyVel.Length():0.00}, nav {nav}, selected {Selected.Count}, tool {Tool}");
            PostMouse(0x207, mid, 0x10); yield return Frames(2);
            Note($"middle pressed: nav {nav}, remembered {walkDistance.Count}, distance {cam.Distance:0.0}, hovered {Hovered() != null}, ui {ImGuiNET.ImGui.GetIO().WantCaptureMouse}");
            PostMouse(0x200, mid + new Vector2(1, 0), 0x10); yield return Frames(2);
            PostMouse(0x208, mid + new Vector2(1, 0), 0); yield return Frames(3);
            Expect(MathF.Abs(cam.Distance - d0) < 0.5f, $"after a middle-button orbit the walk mode is gone: distance {cam.Distance:0.0} m (was {d0:0.0})");
            Expect(Vector3.Distance(eye, cam.Eye) < 1.5f, $"the camera does not jump back ({Vector3.Distance(eye, cam.Eye):0.00} m)");
        }
        finally { Settings.Keymap = keymap0; UI.ImGuiBackend.SyntheticInput = false; }
    }
}
