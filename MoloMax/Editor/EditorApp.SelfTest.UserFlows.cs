using System.Numerics;
using MoloMax.Core.Mesh;
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
                int addMod = km == Keymap.Blender ? MkShift : MkCtrl;
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
            var a = ScreenOf(vp, c3 + new Vector3(0, 0, sz)); var b = ScreenOf(vp, c3 - new Vector3(0, 0, sz)) + new Vector2(30, 0);
            BeginModal(ModalKind.Knife); yield return Frames(2);
            foreach (var x in UClick(a)) yield return x;
            foreach (var x in UClick(b)) yield return x;
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
}
