using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Modifiers;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Wall Opening tool: trace a rectangle straight on a wall, pull each side (or the middle) to adjust, Enter cuts it.
/// The openings live in a Wall Opening modifier, so they stay editable (sides, sizes) until the modifier is applied.
/// </summary>
public sealed partial class EditorApp
{
    SceneObject opObj;
    OpeningRect opRect;            // the one being traced / adjusted (object space)
    OpeningRect opBefore;          // copy to put back on Esc when editing an existing opening
    int opEditIndex = -1;          // index in the modifier when editing an existing opening
    int opPhase;                   // 0 = trace, 1 = adjust
    int opHandle = -1, opHover = -1; // 0 left, 1 right, 2 bottom, 3 top, 4 move
    Vector2 opGrab; OpeningRect opGrabRect;
    float opWallBottom, opWallTop;
    Vector2 opPanelPos;

    // ---------------------------------------------------------------- start

    /// <summary>Starts tracing a new opening on the active object.</summary>
    public void StartOpeningTool()
    {
        var o = Current;
        if (o == null || o.Mesh.Faces.Count == 0) { SetStatus("Wall Opening: select the wall first."); return; }
        if (ModalActive) CancelModal();
        if (o.ModEdit != null) EndModifierEdit(o);
        if (Level != SubObject.Object) SetLevel(SubObject.Object);
        opObj = o; opRect = null; opEditIndex = -1; opPhase = 0; opHandle = -1;
        var vp = Hovered() ?? Viewports[ActiveViewport];
        StartModalCommon(ModalKind.Opening, vp, Vector3.Zero);
        modal.Header = "Wall Opening: drag a rectangle on the wall (a click = 1.2 x 1.2 m window)   Esc cancel";
        SetStatus("Wall Opening: drag a rectangle on the wall, then pull its sides. Enter cuts, Esc cancels.");
    }

    /// <summary>Adjusts an opening already in the modifier (live: the cut follows).</summary>
    public void EditOpening(SceneObject o, WallOpeningModifier mod, int index)
    {
        if (o == null || index < 0 || index >= mod.Openings.Count) return;
        if (ModalActive) CancelModal();
        Undo.Record("Edit opening", Scene);
        opObj = o; opRect = mod.Openings[index]; opBefore = opRect.Copy(); opEditIndex = index; opPhase = 1; opHandle = -1;
        WallLimits();
        var vp = Viewports[ActiveViewport];
        StartModalCommon(ModalKind.Opening, vp, Vector3.Zero);
        OpeningHeader();
    }

    void OpeningHeader() => modal.Header = $"Wall Opening {opRect.Width:0.00} x {opRect.Height:0.00} m, sill {opRect.Bottom - opWallBottom:0.00} m: pull the sides or the middle   D door  W window   Enter cut  Esc cancel";

    WallOpeningModifier OpeningModifierOf(SceneObject o) => o.Modifiers.OfType<WallOpeningModifier>().FirstOrDefault();

    /// <summary>Bottom / top of the wall face under the opening (for the door and window presets and the sill).</summary>
    void WallLimits()
    {
        var (_, up, n) = opRect.Axes();
        var m = opObj.Mesh;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in m.Verts)
        {
            if (MathF.Abs(Vector3.Dot(v - opRect.Origin, n)) > 0.01f) continue;
            float y = Vector3.Dot(v - opRect.Origin, up); lo = MathF.Min(lo, y); hi = MathF.Max(hi, y);
        }
        opWallBottom = lo < float.MaxValue ? lo : 0; opWallTop = hi > float.MinValue ? hi : 3;
    }

    // ---------------------------------------------------------------- mouse

    /// <summary>Where the mouse meets the opening's wall plane, in the rectangle's own coordinates.</summary>
    bool OpeningPlanePoint(Viewport vp, out Vector2 xy)
    {
        xy = default;
        var world = opObj.World;
        var (right, up, n) = opRect.Axes();
        var p0 = Vector3.Transform(opRect.Origin, world);
        var nw = Vector3.Normalize(Vector3.TransformNormal(n, world));
        if (!Picking.RayPlane(RayAt(vp, Input.Mouse), p0, nw, out var hit)) return false;
        if (!Matrix4x4.Invert(world, out var inv)) return false;
        var local = Vector3.Transform(hit, inv);
        xy = new Vector2(Vector3.Dot(local - opRect.Origin, right), Vector3.Dot(local - opRect.Origin, up));
        return true;
    }

    float OpSnap(float v) { float step = Input.Ctrl ? 0.1f : 0.01f; return MathF.Round(v / step) * step; }

    Vector2 OpScreen(Viewport vp, float x, float y)
    {
        var w = Vector3.Transform(opRect.At(x, y), opObj.World);
        return vp.Camera.Project(w, vp.W, vp.H, out var s, out _) ? vp.ToScreen(s) : new Vector2(-9999);
    }

    void OpeningMouse(Viewport hv)
    {
        if (opObj == null || Scene.Find(opObj.Id) == null) { CancelModal(); return; }
        opObj = Scene.Find(opObj.Id);
        bool overUi = ImGui.GetIO().WantCaptureMouse;
        if (opPhase == 0)
        {
            if (opRect == null && Input.Pressed(MouseButtons.Left) && hv != null && !overUi)
            {
                var hit = Picking.PickFace(opObj, RayAt(hv, Input.Mouse), evaluated: true);
                if (hit == null) { SetStatus("Wall Opening: click on the selected wall."); return; }
                var ev = opObj.Evaluated();
                if (!Matrix4x4.Invert(opObj.World, out var inv)) return;
                var local = Vector3.Transform(hit.Value.Point, inv);
                opRect = new OpeningRect { Origin = local, Normal = ev.FaceNormal(hit.Value.Face) };
                WallLimits();
                opGrab = Vector2.Zero;
            }
            else if (opRect != null && hv != null)
            {
                if (OpeningPlanePoint(hv, out var xy))
                {
                    float x = OpSnap(xy.X), y = OpSnap(xy.Y);
                    opRect.Left = MathF.Min(0, x); opRect.Right = MathF.Max(0, x); opRect.Bottom = MathF.Min(0, y); opRect.Top = MathF.Max(0, y);
                    modal.Header = $"Wall Opening {opRect.Width:0.00} x {opRect.Height:0.00} m";
                }
                if (!Input.Down(MouseButtons.Left))
                {
                    if (opRect.Width < 0.05f || opRect.Height < 0.05f) { opRect.Left = -0.6f; opRect.Right = 0.6f; opRect.Bottom = -0.6f; opRect.Top = 0.6f; } // a click: a window
                    opPhase = 1; OpeningHeader();
                }
            }
            if (Input.Pressed(MouseButtons.Right)) CancelModal();
            return;
        }

        // adjust: sides, middle
        var vp = hv ?? modal.Vp;
        if (opHandle < 0)
        {
            opHover = -1;
            if (vp != null && !overUi)
            {
                float cx = (opRect.Left + opRect.Right) * 0.5f, cy = (opRect.Bottom + opRect.Top) * 0.5f;
                var hs = new[] { OpScreen(vp, opRect.Left, cy), OpScreen(vp, opRect.Right, cy), OpScreen(vp, cx, opRect.Bottom), OpScreen(vp, cx, opRect.Top), OpScreen(vp, cx, cy) };
                float best = 12;
                for (int i = 0; i < hs.Length; i++) { float d = Vector2.Distance(hs[i], Input.Mouse); if (d < best) { best = d; opHover = i; } }
                if (opHover < 0 && OpeningPlanePoint(vp, out var xy0) && xy0.X > opRect.Left && xy0.X < opRect.Right && xy0.Y > opRect.Bottom && xy0.Y < opRect.Top) opHover = 4;
            }
            if (opHover >= 0 && Input.Pressed(MouseButtons.Left) && OpeningPlanePoint(vp, out var g))
            {
                opHandle = opHover; opGrab = g; opGrabRect = opRect.Copy();
            }
            else if (Input.Pressed(MouseButtons.Right)) CancelModal();
            return;
        }
        if (OpeningPlanePoint(vp, out var p))
        {
            var d = p - opGrab; var r = opGrabRect;
            switch (opHandle)
            {
                case 0: opRect.Left = MathF.Min(OpSnap(r.Left + d.X), opRect.Right - 0.05f); break;
                case 1: opRect.Right = MathF.Max(OpSnap(r.Right + d.X), opRect.Left + 0.05f); break;
                case 2: opRect.Bottom = MathF.Min(OpSnap(r.Bottom + d.Y), opRect.Top - 0.05f); break;
                case 3: opRect.Top = MathF.Max(OpSnap(r.Top + d.Y), opRect.Bottom + 0.05f); break;
                case 4:
                    float dx = OpSnap(d.X), dy = OpSnap(d.Y);
                    opRect.Left = r.Left + dx; opRect.Right = r.Right + dx; opRect.Bottom = r.Bottom + dy; opRect.Top = r.Top + dy; break;
            }
            if (opEditIndex >= 0) opObj.Touch(); // existing opening: the cut follows
            OpeningHeader();
        }
        if (!Input.Down(MouseButtons.Left)) opHandle = -1;
    }

    void OpeningKeys()
    {
        if (opPhase != 1) return;
        bool P(ImGuiKey k) => ImGui.IsKeyPressed(k, false);
        if (ImGui.GetIO().WantTextInput) return;
        if (P(ImGuiKey.D)) OpeningPreset(true);
        if (P(ImGuiKey.W)) OpeningPreset(false);
    }

    void OpeningPreset(bool door)
    {
        float cx = (opRect.Left + opRect.Right) * 0.5f;
        float w = door ? MathF.Max(opRect.Width, 0.9f) : MathF.Max(opRect.Width, 0.6f);
        if (door) { w = opRect.Width >= 0.6f ? opRect.Width : 1.0f; opRect.Bottom = opWallBottom; opRect.Top = opWallBottom + 2.1f; }
        else { opRect.Bottom = opWallBottom + 0.9f; opRect.Top = opRect.Bottom + 1.2f; }
        opRect.Left = cx - w * 0.5f; opRect.Right = cx + w * 0.5f;
        if (opEditIndex >= 0) opObj.Touch();
        OpeningHeader();
    }

    // ---------------------------------------------------------------- confirm / cancel

    void ConfirmOpening()
    {
        modal.Kind = ModalKind.None;
        if (opObj == null || opRect == null || opPhase != 1) return;
        if (opEditIndex >= 0) { opObj.Touch(); Dirty = true; SetStatus($"Opening {opRect.Width:0.00} x {opRect.Height:0.00} m updated."); return; }
        var o = opObj; var rect = opRect.Copy();
        Do("Cut opening", () =>
        {
            var mod = OpeningModifierOf(o);
            if (mod == null) { mod = new WallOpeningModifier(); mod.Name = ModifierStack.UniqueName(o.Modifiers, mod.Name); o.Modifiers.Add(mod); }
            mod.Openings.Add(rect);
            o.Touch();
        });
        var before = o.Evaluated().Faces.Count;
        SetStatus($"Opening {rect.Width:0.00} x {rect.Height:0.00} m cut (Wall Opening modifier: edit or remove it any time in the Modifiers tab).");
        opRect = null;
    }

    void CancelOpening()
    {
        if (opEditIndex >= 0 && opRect != null && opBefore != null)
        {
            opRect.Left = opBefore.Left; opRect.Right = opBefore.Right; opRect.Bottom = opBefore.Bottom; opRect.Top = opBefore.Top;
            opObj?.Touch();
            Undo.DropLast();
        }
        opRect = null; opEditIndex = -1;
    }

    // ---------------------------------------------------------------- drawing

    void OpeningLines()
    {
        if (modal.Kind != ModalKind.Opening || opRect == null || opObj == null) return;
        var w = opObj.World;
        Vector3 P(float x, float y) => Vector3.Transform(opRect.At(x, y), w);
        var c = new Vector4(1f, 0.82f, 0.1f, 1);
        var a = P(opRect.Left, opRect.Bottom); var b = P(opRect.Right, opRect.Bottom); var d = P(opRect.Right, opRect.Top); var e = P(opRect.Left, opRect.Top);
        lines.Line(a, b, c); lines.Line(b, d, c); lines.Line(d, e, c); lines.Line(e, a, c);
    }

    void OpeningOverlay(ImDrawListPtr dl, Viewport vp)
    {
        if (modal.Kind != ModalKind.Opening || opRect == null || opPhase != 1 || vp != Viewports[ActiveViewport]) return;
        float cx = (opRect.Left + opRect.Right) * 0.5f, cy = (opRect.Bottom + opRect.Top) * 0.5f;
        var hs = new[] { OpScreen(vp, opRect.Left, cy), OpScreen(vp, opRect.Right, cy), OpScreen(vp, cx, opRect.Bottom), OpScreen(vp, cx, opRect.Top), OpScreen(vp, cx, cy) };
        for (int i = 0; i < hs.Length; i++)
        {
            bool on = i == opHover || i == opHandle;
            uint col = on ? 0xFF60FFFF : 0xFF20C8F0;
            if (i < 4) dl.AddCircleFilled(hs[i], on ? 8 : 6, col, 16);
            else { dl.AddCircle(hs[i], on ? 10 : 8, col, 16, 2); dl.AddLine(hs[i] - new Vector2(5, 0), hs[i] + new Vector2(5, 0), col, 2); dl.AddLine(hs[i] - new Vector2(0, 5), hs[i] + new Vector2(0, 5), col, 2); }
        }
        // sizes next to the rectangle
        var lblW = $"{opRect.Width:0.00} m"; var lblH = $"{opRect.Height:0.00} m";
        dl.AddText(hs[3] + new Vector2(-ImGui.CalcTextSize(lblW).X * 0.5f, -22), 0xFF60FFFF, lblW);
        dl.AddText(hs[1] + new Vector2(12, -8), 0xFF60FFFF, lblH);
        opPanelPos = new Vector2(MathF.Max(hs[1].X, hs[3].X) + 40, hs[3].Y);
    }

    /// <summary>A small panel next to the rectangle: exact sizes, door / window, cut / cancel.</summary>
    void OpeningPanel()
    {
        if (modal.Kind != ModalKind.Opening || opRect == null || opPhase != 1) return;
        var vp = Viewports[ActiveViewport];
        var pos = new Vector2(vp.X + vp.W - 240, vp.Y + 70); // a corner of the view: never over the wall and its handles
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.Begin("##openingpanel", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        Ui.TextColored(Theme.Gold, "Wall Opening");
        float w = opRect.Width, h = opRect.Height, sill = opRect.Bottom - opWallBottom;
        bool ch = false;
        ImGui.SetNextItemWidth(160); ch |= ImGui.DragFloat("Width", ref w, 0.01f, 0.05f, 50f, "%.2f m");
        ImGui.SetNextItemWidth(160); ch |= ImGui.DragFloat("Height", ref h, 0.01f, 0.05f, 50f, "%.2f m");
        ImGui.SetNextItemWidth(160); ch |= ImGui.DragFloat("Sill", ref sill, 0.01f, -50f, 50f, "%.2f m");
        TutRect("op_width");
        if (ch)
        {
            float cx = (opRect.Left + opRect.Right) * 0.5f;
            opRect.Left = cx - MathF.Max(w, 0.05f) * 0.5f; opRect.Right = cx + MathF.Max(w, 0.05f) * 0.5f;
            opRect.Bottom = opWallBottom + sill; opRect.Top = opRect.Bottom + MathF.Max(h, 0.05f);
            if (opEditIndex >= 0) opObj.Touch();
            OpeningHeader();
        }
        if (ImGui.Button("Door (D)", new Vector2(78, 0))) OpeningPreset(true); ImGui.SameLine();
        if (ImGui.Button("Window (W)", new Vector2(78, 0))) OpeningPreset(false);
        if (ImGui.Button(opEditIndex >= 0 ? "Done (Enter)" : "Cut (Enter)", new Vector2(160, 26))) ConfirmModal();
        TutRect("op_cut");
        ImGui.End();
    }

    // ---------------------------------------------------------------- the modifier's panel

    void WallOpeningFields(SceneObject o, WallOpeningModifier mod)
    {
        if (ImGui.Button("+ Trace a new opening on the wall", new Vector2(-1, 26))) { activeModifier[o.Id] = o.Modifiers.IndexOf(mod); StartOpeningTool(); }
        Theme.Tip("Drag a rectangle on the wall, pull its sides, Enter cuts. Also: right-click the wall > Cut opening.");
        if (mod.Openings.Count == 0) { Ui.TextDisabled("No opening yet."); return; }
        int remove = -1;
        for (int i = 0; i < mod.Openings.Count; i++)
        {
            var op = mod.Openings[i];
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            Ui.Text($"{i + 1}.  {op.Width:0.00} x {op.Height:0.00} m");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 96);
            if (ImGui.Button("Edit", new Vector2(50, 0))) EditOpening(o, mod, i);
            Theme.Tip("Shows the rectangle on the wall: pull its sides, Enter when done.");
            ImGui.SameLine(0, 4);
            if (ImGui.Button("x", new Vector2(24, 0))) remove = i;
            Theme.Tip("Remove this opening (the wall closes again).");
            ImGui.PopID();
        }
        if (remove >= 0) Do("Remove opening", () => { mod.Openings.RemoveAt(remove); o.Touch(); });
    }
}
