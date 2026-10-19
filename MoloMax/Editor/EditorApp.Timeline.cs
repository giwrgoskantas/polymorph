using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Anim;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Animation like Blender: a timeline docked under the views (play, frame, range), a dope sheet with the keys of the
/// selected objects (click, drag, Delete, right-click for the interpolation), I / K inserts a key, auto-key records moves.
/// Objects (location, rotation, scale), their UV animation and the camera can be keyed.
/// </summary>
public sealed partial class EditorApp
{
    public bool TimelineOpen;
    const float TimelineH = 190;
    float animFrame;               // current frame (fractional while playing)
    float animShownFrame = float.NaN;
    public bool AnimPlaying;
    public bool AutoKey;
    /// <summary>The camera follows its animation track while playing / scrubbing.</summary>
    public bool AnimCameraFollows = true;
    readonly HashSet<(int Row, float Frame)> keySel = new();
    bool keyDragging; float keyDragStart, keyDragDelta;
    bool scrubbing;
    float uvEditU, uvEditV, uvEditSU = 1, uvEditSV = 1, uvEditRot;

    SceneAnim Anim => Scene.Anim ??= new SceneAnim();
    public int CurrentFrame => (int)MathF.Round(animFrame);

    public void ToggleTimeline() { TimelineOpen = !TimelineOpen; if (!TimelineOpen) AnimPlaying = false; }

    public void SetFrame(float f)
    {
        var a = Anim;
        animFrame = Math.Clamp(f, a.Start, a.End);
    }

    /// <summary>Every frame: playback, then the animated objects / camera go where the curves say when the frame changed.</summary>
    void AnimFrameUpdate(float dt)
    {
        var a = Anim;
        if (AnimPlaying)
        {
            animFrame += dt * a.Fps;
            if (animFrame > a.End) animFrame = a.Start + (animFrame - a.End) % MathF.Max(1, a.End - a.Start);
        }
        if (a.Tracks.Count == 0) { animShownFrame = animFrame; return; }
        if (animShownFrame == animFrame) return;
        animShownFrame = animFrame;
        ApplyAnimNow();
    }

    void ApplyAnimNow()
    {
        var a = Anim;
        float f = AnimPlaying ? animFrame : CurrentFrame;
        // a UV preview without keys goes away on the next frame change
        foreach (var o in Scene.Objects) if (o.UvRow0.W > 0 && !Ch.Uv.Any(c => a.Track(o.Id)?.Has(c) == true)) { o.UvRow0 = default; o.UvRow1 = default; }
        if (a.Apply(Scene, f) > 0) foreach (var t in a.Tracks) Scene.Find(t.ObjectId)?.Touch();
        if (AnimCameraFollows && a.Track(AnimTrack.CameraId) is { } ct)
        {
            var cam = Viewports[ActiveViewport].Camera;
            float V(string ch, float cur) { var c = ct.Curve(ch); return c != null && c.Keys.Count > 0 ? c.Evaluate(f) : cur; }
            cam.Target = new Vector3(V(Ch.CamX, cam.Target.X), V(Ch.CamY, cam.Target.Y), V(Ch.CamZ, cam.Target.Z));
            cam.Yaw = V(Ch.CamYaw, cam.Yaw * 180 / MathF.PI) * MathF.PI / 180;
            cam.Pitch = V(Ch.CamPitch, cam.Pitch * 180 / MathF.PI) * MathF.PI / 180;
            cam.Distance = V(Ch.CamDist, cam.Distance);
        }
    }

    // ---------------------------------------------------------------- keying

    /// <summary>I (K in the classic keymap): location, rotation and scale of the selected objects at the current frame.</summary>
    public void InsertKeyframe(string what = "LocRotScale")
    {
        var objs = SelObjects().Where(o => o.Id >= 0).ToList();
        if (objs.Count == 0) { SetStatus("Select the objects to key (or use Key camera)."); return; }
        Undo.Record("Insert keyframe", Scene);
        var ch = what switch { "Loc" => Ch.Loc, "Rot" => Ch.Rot, "Scale" => Ch.Scale, _ => Ch.Loc.Concat(Ch.Rot).Concat(Ch.Scale).ToArray() };
        foreach (var o in objs) Anim.KeyObject(o, CurrentFrame, ch);
        if (!TimelineOpen) TimelineOpen = true;
        Dirty = true;
        SetStatus($"Keyframe at {CurrentFrame}: {(what == "LocRotScale" ? "location, rotation, scale" : what)} of {objs.Count} object(s)");
    }

    public void KeyCamera()
    {
        Undo.Record("Key camera", Scene);
        var cam = Viewports[ActiveViewport].Camera;
        var t = Anim.Track(AnimTrack.CameraId, true);
        // yaw next to the previous key so a turn past 180° goes on
        float prevYaw = t.Curve(Ch.CamYaw)?.Evaluate(CurrentFrame) ?? cam.Yaw * 180 / MathF.PI, yaw = cam.Yaw * 180 / MathF.PI;
        while (yaw - prevYaw > 180) yaw -= 360; while (yaw - prevYaw < -180) yaw += 360;
        var vals = new[] { cam.Target.X, cam.Target.Y, cam.Target.Z, yaw, cam.Pitch * 180 / MathF.PI, cam.Distance };
        for (int i = 0; i < Ch.Cam.Length; i++) t.Curve(Ch.Cam[i], true).SetKey(CurrentFrame, vals[i]);
        Dirty = true;
        SetStatus($"Camera keyed at frame {CurrentFrame}");
    }

    /// <summary>Auto-key (the red record button): a move / rotate / scale of objects keys them where they end up.</summary>
    void AutoKeyAfterTransform()
    {
        if (!AutoKey || !TimelineOpen || Level != SubObject.Object) return;
        var ch = Tool switch { Tool.Rotate => Ch.Rot, Tool.Scale => Ch.Scale, _ => Ch.Loc };
        foreach (var o in SelObjects().Where(o => o.Id >= 0)) Anim.KeyObject(o, CurrentFrame, ch);
        animShownFrame = animFrame; // the new key is where the object is: no jump
    }

    void KeyUv(SceneObject o, string ch, float v)
    {
        var t = Anim.Track(o.Id, true);
        t.Curve(ch, true).SetKey(CurrentFrame, v);
        Dirty = true;
    }

    // ---------------------------------------------------------------- the panel

    (float X0, float Y0, float W) timelineRect;
    /// <summary>Where the frame ruler is on screen (tutorials click a frame there).</summary>
    (float X0, float X1, float Y) dopeRuler;
    Vector2 RulerAt(float frame) => new(dopeRuler.X0 + (frame - Anim.Start) / MathF.Max(1, Anim.End - Anim.Start) * (dopeRuler.X1 - dopeRuler.X0), dopeRuler.Y);

    void DrawTimeline()
    {
        if (!TimelineOpen) return;
        var a = Anim;
        float x0 = ShowExplorer ? explorerW : 0, x1 = gpu.Width - (ShowPanel ? panelW : 0), y0 = gpu.Height - statusH - TimelineH;
        timelineRect = (x0, y0, x1 - x0);
        ImGui.SetNextWindowPos(new Vector2(x0, y0));
        ImGui.SetNextWindowSize(new Vector2(x1 - x0, TimelineH));
        ImGui.Begin("##timeline", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus);

        // ---- one simple row: play, frame, Key, Auto key, then a menu for the rest
        if (ImGui.Button("|<")) SetFrame(a.Start); Theme.Tip("First frame");
        ImGui.SameLine(); if (Theme.Toggle(AnimPlaying ? "Pause" : "Play", AnimPlaying, new Vector2(64, 0))) AnimPlaying = !AnimPlaying;
        TutRect("tl_play");
        Theme.Tip(Settings.Keymap == Keymap.Max ? "Play / pause ( / )" : "Play / pause (Space)");
        ImGui.SameLine(); if (ImGui.Button(">|")) SetFrame(a.End); Theme.Tip("Last frame");
        ImGui.SameLine(); Ui.TextDisabled("Frame"); ImGui.SameLine(); ImGui.SetNextItemWidth(64);
        int cf = CurrentFrame;
        if (ImGui.InputInt("##frame", ref cf, 0)) { AnimPlaying = false; SetFrame(cf); }
        Theme.Tip("Current frame (or click in the bar below). Left / Right arrows: one frame");
        ImGui.SameLine(); Ui.TextDisabled("|"); ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.62f, 0.12f, 1)); ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0, 0, 0, 1));
        if (ImGui.Button(Settings.Keymap == Keymap.Max ? "  Key (K)  " : "  Key (I)  ")) InsertKeyframe();
        ImGui.PopStyleColor(2); TutRect("tl_key");
        Theme.Tip("Saves where the selected objects are (position, rotation, size) at this frame");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, AutoKey ? new Vector4(0.8f, 0.15f, 0.15f, 1) : ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
        if (ImGui.Button(AutoKey ? "Auto key ON" : "Auto key")) AutoKey = !AutoKey;
        ImGui.PopStyleColor(); TutRect("tl_auto");
        Theme.Tip("On: every move / rotate / scale is saved as a key by itself");
        ImGui.SameLine(); if (ImGui.Button("Key camera")) KeyCamera();
        Theme.Tip("Saves the view (for a camera fly-through when you play)");
        ImGui.SameLine(); if (ImGui.Button("GTA .ycd")) ImGui.OpenPopup("##ycdmenu");
        TutRect("tl_ycd");
        Theme.Tip("Import or export a GTA animation file");
        if (ImGui.BeginPopup("##ycdmenu"))
        {
            if (ImGui.MenuItem("Import a .ycd...")) ImportYcdDialog();
            if (ImGui.MenuItem("Export the animation as .ycd...")) ExportYcdDialog();
            ImGui.EndPopup();
        }
        ImGui.SameLine(); if (ImGui.Button("Settings")) ImGui.OpenPopup("##tlset");
        if (ImGui.BeginPopup("##tlset"))
        {
            ImGui.SetNextItemWidth(80); int st = a.Start; if (ImGui.InputInt("Start", ref st, 0)) { a.Start = Math.Min(st, a.End - 1); Dirty = true; }
            ImGui.SetNextItemWidth(80); int en = a.End; if (ImGui.InputInt("End", ref en, 0)) { a.End = Math.Max(en, a.Start + 1); Dirty = true; }
            ImGui.SetNextItemWidth(80); int fps = (int)a.Fps; if (ImGui.InputInt("Frames per second", ref fps, 0)) { a.Fps = Math.Clamp(fps, 1, 120); Dirty = true; }
            Ui.TextDisabled("GTA plays animations at 30 fps.");
            ImGui.Checkbox("The view follows the camera keys", ref AnimCameraFollows);
            ImGui.EndPopup();
        }
        ImGui.SameLine(); Ui.TextDisabled($"{a.End - a.Start} frames = {(a.End - a.Start) / MathF.Max(1, a.Fps):0.0} s");

        // ---- left: UV keys of the selected object ; right: dope sheet
        float leftW = 250;
        ImGui.BeginChild("##tlleft", new Vector2(leftW, 0), ImGuiChildFlags.Borders);
        UvKeyPanel();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##dope", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollWithMouse);
        DopeSheet();
        ImGui.EndChild();
        ImGui.End();
    }

    void UvKeyPanel()
    {
        var o = Current;
        if (o == null || o.Id < 0) { Ui.TextDisabled("Select an object to animate it."); return; }
        var t = Anim.Track(o.Id);
        float V(string ch, float d) { var c = t?.Curve(ch); return c != null && c.Keys.Count > 0 ? c.Evaluate(CurrentFrame) : d; }
        if (!ImGui.IsAnyItemActive()) { uvEditU = V(Ch.UvU, 0); uvEditV = V(Ch.UvV, 0); uvEditSU = V(Ch.UvSU, 1); uvEditSV = V(Ch.UvSV, 1); uvEditRot = V(Ch.UvRot, 0); }
        Ui.TextDisabled("Texture animation (neons, screens, water)");
        void Row(string label, string ch, ref float v, float speed)
        {
            ImGui.PushID(ch);
            bool keyed = t?.Curve(ch)?.KeyAt(CurrentFrame) != null, animated = t?.Has(ch) == true;
            // a diamond like Blender's key button: filled yellow = key here, green = animated, hollow = not animated
            var dp = ImGui.GetCursorScreenPos(); float ds = ImGui.GetFrameHeight();
            if (ImGui.InvisibleButton("##key", new Vector2(ds, ds))) { Undo.Record("Key UV", Scene); KeyUv(o, ch, v); }
            var dc = dp + new Vector2(ds / 2, ds / 2); float dr = ds * 0.3f;
            uint col = keyed ? 0xFF30C8FFu : animated ? 0xFF60C060u : 0xFF909090u;
            var dl2 = ImGui.GetWindowDrawList();
            if (keyed || animated) dl2.AddQuadFilled(dc + new Vector2(0, -dr), dc + new Vector2(dr, 0), dc + new Vector2(0, dr), dc + new Vector2(-dr, 0), col);
            dl2.AddQuad(dc + new Vector2(0, -dr), dc + new Vector2(dr, 0), dc + new Vector2(0, dr), dc + new Vector2(-dr, 0), ImGui.IsItemHovered() ? 0xFFFFFFFFu : col, 1.5f);
            Theme.Tip("Key this value at the current frame");
            ImGui.SameLine(); ImGui.SetNextItemWidth(110);
            if (ImGui.DragFloat("##v", ref v, speed, -1000, 1000, "%.3f"))
            {
                if (AutoKey || animated) { if (ImGui.IsItemActivated()) Undo.Record("Key UV", Scene); KeyUv(o, ch, v); }
                var (r0, r1) = SceneAnim.UvRows(uvEditU, uvEditV, uvEditSU, uvEditSV, uvEditRot);
                o.UvRow0 = new Vector4(r0, 1); o.UvRow1 = new Vector4(r1, 1);
            }
            ImGui.SameLine(); Ui.Text(label);
            ImGui.PopID();
        }
        Row("Slide U", Ch.UvU, ref uvEditU, 0.01f);
        Row("Slide V", Ch.UvV, ref uvEditV, 0.01f);
        Row("Scale U", Ch.UvSU, ref uvEditSU, 0.01f);
        Row("Scale V", Ch.UvSV, ref uvEditSV, 0.01f);
        Row("Rotation", Ch.UvRot, ref uvEditRot, 0.5f);
    }

    /// <summary>Rows: the selected objects (and the camera when it is animated); diamonds on every keyed frame.</summary>
    List<(string Name, int Id)> DopeRows()
    {
        var rows = new List<(string, int)>();
        foreach (var o in SelObjects().Where(o => o.Id >= 0)) rows.Add((o.Name, o.Id));
        foreach (var t in Anim.Tracks) if (t.ObjectId >= 0 && !Selected.Contains(t.ObjectId) && Scene.Find(t.ObjectId) is { } o && rows.Count < 12) rows.Add((o.Name + "  (not selected)", o.Id));
        if (Anim.Track(AnimTrack.CameraId) != null) rows.Add(("Camera", AnimTrack.CameraId));
        return rows;
    }

    void DopeSheet()
    {
        var a = Anim;
        var dl = ImGui.GetWindowDrawList();
        var p0 = ImGui.GetCursorScreenPos(); var size = ImGui.GetContentRegionAvail();
        float nameW = 150, rulerH = 20, rowH = 20;
        float fx0 = p0.X + nameW, fx1 = p0.X + size.X - 10;
        float span = MathF.Max(1, a.End - a.Start);
        float X(float f) => fx0 + (f - a.Start) / span * (fx1 - fx0);
        dopeRuler = (fx0, fx1, p0.Y + rulerH / 2);
        float F(float x) => a.Start + (x - fx0) / MathF.Max(1, fx1 - fx0) * span;
        // ruler
        dl.AddRectFilled(new Vector2(fx0, p0.Y), new Vector2(fx1, p0.Y + rulerH), 0xFF2A2A2E);
        int step = span > 400 ? 50 : span > 200 ? 20 : span > 80 ? 10 : 5;
        for (int f = (a.Start / step) * step; f <= a.End; f += step)
        {
            if (f < a.Start) continue;
            float x = X(f);
            dl.AddLine(new Vector2(x, p0.Y + rulerH - 6), new Vector2(x, p0.Y + size.Y), 0x30FFFFFF);
            dl.AddText(new Vector2(x + 2, p0.Y + 2), 0xFFA0A0A0, f.ToString());
        }
        var rows = DopeRows();
        // name column + rows
        for (int r = 0; r < rows.Count; r++)
        {
            float y = p0.Y + rulerH + 4 + r * rowH;
            if (r % 2 == 0) dl.AddRectFilled(new Vector2(p0.X, y), new Vector2(fx1, y + rowH), 0x14FFFFFF);
            var nm = rows[r].Name; if (nm.Length > 22) nm = nm[..21] + "…";
            dl.AddText(new Vector2(p0.X + 4, y + 3), rows[r].Id == AnimTrack.CameraId ? 0xFF80C0FF : 0xFFE0E0E0, nm);
            var t = a.Track(rows[r].Id);
            if (t == null) continue;
            foreach (var kf in t.Curves.SelectMany(c => c.Keys).Select(k => k.Frame).Distinct())
            {
                float shown = keySel.Contains((r, kf)) && keyDragging ? kf + MathF.Round(keyDragDelta) : kf;
                float x = X(shown), cy = y + rowH / 2;
                bool sel = keySel.Contains((r, kf));
                var c = sel ? 0xFF30B0FFu : 0xFFD0D0D0u;
                dl.AddQuadFilled(new Vector2(x, cy - 6), new Vector2(x + 6, cy), new Vector2(x, cy + 6), new Vector2(x - 6, cy), c);
                dl.AddQuad(new Vector2(x, cy - 6), new Vector2(x + 6, cy), new Vector2(x, cy + 6), new Vector2(x - 6, cy), 0xFF000000);
            }
        }
        if (rows.Count == 0) dl.AddText(new Vector2(p0.X + 4, p0.Y + rulerH + 6), 0xFF808080, "1. Select an object   2. Key   3. click another frame   4. move the object   5. Key   6. Play");
        // current frame line
        float cx = X(AnimPlaying ? animFrame : CurrentFrame);
        dl.AddLine(new Vector2(cx, p0.Y), new Vector2(cx, p0.Y + size.Y), 0xFF30A0FF, 2);
        dl.AddRectFilled(new Vector2(cx - 14, p0.Y), new Vector2(cx + 14, p0.Y + rulerH - 2), 0xFF30A0FF, 3);
        var lbl = CurrentFrame.ToString(); var ls = ImGui.CalcTextSize(lbl);
        dl.AddText(new Vector2(cx - ls.X / 2, p0.Y + 2), 0xFFFFFFFF, lbl);

        // ---- mouse
        ImGui.InvisibleButton("##dopearea", new Vector2(MathF.Max(10, size.X), MathF.Max(10, size.Y)));
        bool hov = ImGui.IsItemHovered();
        var m = ImGui.GetMousePos();
        (int Row, float Frame)? KeyUnder()
        {
            for (int r = 0; r < rows.Count; r++)
            {
                float y = p0.Y + rulerH + 4 + r * rowH;
                if (m.Y < y || m.Y > y + rowH) continue;
                var t = a.Track(rows[r].Id); if (t == null) return null;
                float best = 8; float? bf = null;
                foreach (var kf in t.Curves.SelectMany(c => c.Keys).Select(k => k.Frame).Distinct())
                {
                    float d = MathF.Abs(X(kf) - m.X); if (d < best) { best = d; bf = kf; }
                }
                return bf is { } f ? (r, f) : null;
            }
            return null;
        }
        if (hov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var k = KeyUnder();
            if (m.Y < p0.Y + rulerH || k == null)
            {
                if (!ImGui.GetIO().KeyShift) keySel.Clear();
                scrubbing = true; AnimPlaying = false;
            }
            else
            {
                if (!ImGui.GetIO().KeyShift && !keySel.Contains(k.Value)) keySel.Clear();
                keySel.Add(k.Value);
                keyDragging = true; keyDragStart = m.X; keyDragDelta = 0;
            }
        }
        if (scrubbing)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) SetFrame(MathF.Round(F(m.X)));
            else scrubbing = false;
        }
        if (keyDragging)
        {
            keyDragDelta = (m.X - keyDragStart) / MathF.Max(1, fx1 - fx0) * span;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                keyDragging = false;
                int d = (int)MathF.Round(keyDragDelta);
                if (d != 0) MoveKeys(rows, d);
            }
        }
        if (hov && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            var k = KeyUnder();
            if (k != null) { if (!keySel.Contains(k.Value)) { keySel.Clear(); keySel.Add(k.Value); } ImGui.OpenPopup("##keyctx"); }
        }
        if (ImGui.BeginPopup("##keyctx"))
        {
            if (ImGui.MenuItem("Smooth (Bezier)")) SetKeyInterp(rows, KeyInterp.Smooth);
            if (ImGui.MenuItem("Linear")) SetKeyInterp(rows, KeyInterp.Linear);
            if (ImGui.MenuItem("Constant (step)")) SetKeyInterp(rows, KeyInterp.Constant);
            ImGui.Separator();
            if (ImGui.MenuItem("Delete keys", "Del")) DeleteSelectedKeys(rows);
            ImGui.EndPopup();
        }
        if ((ImGui.IsWindowHovered() || ImGui.IsWindowFocused()) && keySel.Count > 0 && (ImGui.IsKeyPressed(ImGuiKey.Delete) || ImGui.IsKeyPressed(ImGuiKey.X))) DeleteSelectedKeys(rows);
        if (hov && ImGui.GetIO().MouseWheel != 0 && ImGui.GetIO().KeyCtrl)
        {
            // Ctrl+wheel: zoom the range around the mouse
            float c = F(m.X), k = ImGui.GetIO().MouseWheel > 0 ? 0.8f : 1.25f;
            a.Start = (int)MathF.Round(c - (c - a.Start) * k); a.End = Math.Max(a.Start + 2, (int)MathF.Round(c + (a.End - c) * k));
        }
    }

    IEnumerable<(AnimTrack T, float Frame)> SelectedKeyRefs(List<(string Name, int Id)> rows)
    {
        foreach (var (r, f) in keySel)
            if (r < rows.Count && Anim.Track(rows[r].Id) is { } t) yield return (t, f);
    }

    void MoveKeys(List<(string Name, int Id)> rows, int d)
    {
        Undo.Record("Move keys", Scene);
        var refs = SelectedKeyRefs(rows).ToList();
        // the selected keys of every curve of the row move together
        foreach (var grp in refs.GroupBy(x => x.T))
            foreach (var c in grp.Key.Curves)
            {
                var frames = grp.Select(x => x.Frame).ToHashSet();
                var moving = c.Keys.Where(k => frames.Any(f => MathF.Abs(k.Frame - f) < 0.01f)).ToList();
                foreach (var k in moving) { c.Keys.Remove(k); }
                foreach (var k in moving) { k.Frame += d; c.Keys.RemoveAll(x => MathF.Abs(x.Frame - k.Frame) < 0.01f); c.Keys.Add(k); }
                c.Sort();
            }
        var moved = keySel.Select(x => (x.Row, x.Frame + d)).ToList(); keySel.Clear(); foreach (var x in moved) keySel.Add(x);
        Dirty = true; animShownFrame = float.NaN;
    }

    void SetKeyInterp(List<(string Name, int Id)> rows, KeyInterp ip)
    {
        Undo.Record("Key interpolation", Scene);
        foreach (var (t, f) in SelectedKeyRefs(rows).ToList())
            foreach (var c in t.Curves) if (c.KeyAt(f) is { } k) k.Interp = ip;
        Dirty = true; animShownFrame = float.NaN;
    }

    void DeleteSelectedKeys(List<(string Name, int Id)> rows)
    {
        var refs = SelectedKeyRefs(rows).ToList();
        if (refs.Count == 0) return;
        Undo.Record("Delete keys", Scene);
        int n = 0;
        foreach (var (t, f) in refs) n += Anim.DeleteKeys(t.ObjectId, f);
        keySel.Clear(); Dirty = true; animShownFrame = float.NaN;
        SetStatus($"{n} key(s) deleted");
    }

    void JumpKey(int dir)
    {
        var frames = Anim.KeyFrames(Selected.Count > 0 ? Selected : null);
        if (frames.Count == 0) return;
        float cur = CurrentFrame;
        var next = dir > 0 ? frames.Where(f => f > cur + 0.01f).DefaultIfEmpty(float.NaN).First() : frames.Where(f => f < cur - 0.01f).DefaultIfEmpty(float.NaN).Last();
        if (!float.IsNaN(next)) { AnimPlaying = false; SetFrame(next); }
    }

    /// <summary>Animation shortcuts (object mode): I / K key, Space or / plays, Shift+Left / Right ends, Up / Down keys.</summary>
    bool AnimKeys(bool ctrl, bool shift, bool alt)
    {
        if (Level != SubObject.Object || ModalActive) return false;
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        bool max = Settings.Keymap == Keymap.Max;
        if (!ctrl && !alt && !shift && P(max ? ImGuiKey.K : ImGuiKey.I) && Selected.Count > 0) { InsertKeyframe(); return true; }
        if (!TimelineOpen) return false;
        if (!ctrl && !alt && !shift && (max ? P(ImGuiKey.Slash) : Settings.Keymap != Keymap.Industry && P(ImGuiKey.Space))) { AnimPlaying = !AnimPlaying; return true; }
        if (shift && !ctrl && P(ImGuiKey.LeftArrow)) { SetFrame(Anim.Start); return true; }
        if (shift && !ctrl && P(ImGuiKey.RightArrow)) { SetFrame(Anim.End); return true; }
        if (!ctrl && !shift && !alt && ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) { AnimPlaying = false; SetFrame(CurrentFrame - 1); return true; }
        if (!ctrl && !shift && !alt && ImGui.IsKeyPressed(ImGuiKey.RightArrow)) { AnimPlaying = false; SetFrame(CurrentFrame + 1); return true; }
        if (!ctrl && !shift && !alt && P(ImGuiKey.UpArrow)) { JumpKey(1); return true; }
        if (!ctrl && !shift && !alt && P(ImGuiKey.DownArrow)) { JumpKey(-1); return true; }
        return false;
    }
}
