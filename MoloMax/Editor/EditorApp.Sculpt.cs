using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Sculpt mode: basic brushes to clean generated meshes (Tripo, scans) right in the viewport.
/// LMB paints the brush, Ctrl inverts, Shift smooths, [ and ] change the size.
/// </summary>
public sealed partial class EditorApp
{
    public bool SculptMode;
    public readonly SculptSettings Sculpt = new();
    SculptSession sculptSession;
    int sculptObj = -1, sculptVersion = -1;
    bool sculpting;
    Vector3 lastDab;
    (Vector3 P, Vector3 N, float R, bool Hit) sculptHover;
    Vector3 grabPlanePoint, grabPlaneNormal;

    static readonly (SculptBrush B, string Label, string Tip)[] SculptBrushes =
    {
        (SculptBrush.Smooth, "Smooth", "Averages the surface: removes bumps and noise (Shift while painting with any brush)"),
        (SculptBrush.Relax, "Relax", "Evens out the mesh along the surface without changing its shape (great on generated meshes)"),
        (SculptBrush.Flatten, "Flatten", "Pushes the area onto its average plane (Ctrl: only cuts what sticks out)"),
        (SculptBrush.Draw, "Draw", "Builds the surface up (Ctrl digs)"),
        (SculptBrush.Inflate, "Inflate", "Swells along the normals (Ctrl deflates)"),
        (SculptBrush.Grab, "Grab", "Drags the area with the mouse"),
        (SculptBrush.Pinch, "Pinch", "Pulls toward the brush centre: sharpens creases (Ctrl spreads)"),
    };

    public void ToggleSculptMode()
    {
        var o = Current;
        if (!SculptMode && (o == null || o.Mesh.Faces.Count == 0)) { SetStatus("Select one mesh to sculpt."); return; }
        SculptMode = !SculptMode;
        if (SculptMode)
        {
            paintMode = false;
            if (Level != SubObject.Object) Level = SubObject.Object;
            EnsureSculptSession(o);
            Sculpt.Radius = MathF.Max(0.02f, sculptSession.AverageEdge() * 5 * ObjScale(o));
            SetStatus("Sculpt mode: LMB paints, Ctrl inverts, Shift smooths, [ ] brush size. Esc or the Sculpt button to leave.");
        }
        else { sculptSession = null; sculpting = false; SetStatus("Sculpt mode off."); }
    }

    void EnsureSculptSession(SceneObject o)
    {
        if (sculptSession == null || sculptObj != o.Id || !ReferenceEquals(sculptSession.Mesh, o.Mesh) || sculptVersion != o.Version || sculptSession.Mesh.Verts.Count != o.Mesh.Verts.Count)
        {
            sculptSession = new SculptSession(o.Mesh); sculptObj = o.Id; sculptVersion = o.Version;
        }
    }

    /// <summary>Viewport mouse in sculpt mode. Returns true when it used the mouse.</summary>
    bool SculptInput(Viewport hv, bool uiMouse)
    {
        sculptHover.Hit = false;
        if (!SculptMode) return false;
        var o = Current;
        if (o == null || Level != SubObject.Object) { sculpting = false; return false; }
        if (hv == null || uiMouse) { if (!Input.Down(System.Windows.Forms.MouseButtons.Left)) EndSculptStroke(o); return sculpting; }
        // [ ] brush size
        if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) Sculpt.Radius = MathF.Max(0.005f, Sculpt.Radius / 1.15f);
        if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) Sculpt.Radius *= 1.15f;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { ToggleSculptMode(); return true; }
        var s = CurrentSculptSettings(o);
        bool hit = HitLocal(o, hv, Input.Mouse, out var pl, out var nl, out _);
        if (hit) sculptHover = (Vector3.Transform(pl, o.World), Vector3.Normalize(Vector3.TransformNormal(nl, o.World)), Sculpt.Radius, true);
        bool pressed = Input.Pressed(System.Windows.Forms.MouseButtons.Left) && !Input.Alt;
        if (pressed)
        {
            if (!hit) return true; // a click next to the model does nothing in sculpt mode
            Undo.Record("Sculpt stroke", Scene); Dirty = true;
            EnsureSculptSession(o);
            if (s.Brush == SculptBrush.Grab)
            {
                sculptSession.BeginGrab(pl, s);
                grabPlanePoint = Vector3.Transform(pl, o.World); grabPlaneNormal = -hv.Camera.Forward;
            }
            else sculptSession.Dab(pl, nl, s);
            lastDab = pl; sculpting = true;
            AfterDab(o);
            return true;
        }
        if (!sculpting) return false;
        if (!Input.Down(System.Windows.Forms.MouseButtons.Left)) { EndSculptStroke(o); return true; }
        if (s.Brush == SculptBrush.Grab)
        {
            // grab follows the mouse on a plane facing the camera through the grabbed point
            if (Picking.RayPlane(RayAt(hv, Input.Mouse), grabPlanePoint, grabPlaneNormal, out var wp))
            {
                Matrix4x4.Invert(o.World, out var inv);
                sculptSession.DragGrab(Vector3.Transform(wp, inv), s);
                AfterDab(o);
            }
            return true;
        }
        if (!hit) return true;
        // dabs spaced along the mouse path (a quarter of the brush)
        float r = s.Radius, step = MathF.Max(r * 0.25f, 1e-4f);
        float d = Vector3.Distance(lastDab, pl);
        if (d >= step)
        {
            int n = Math.Min(32, (int)(d / step));
            for (int i = 1; i <= n; i++) sculptSession.Dab(Vector3.Lerp(lastDab, pl, i / (float)n), nl, s);
            lastDab = pl;
            AfterDab(o);
        }
        return true;
    }

    SculptSettings CurrentSculptSettings(SceneObject o)
    {
        // brush radius is in world metres; the session works in the mesh's own units
        var s = new SculptSettings
        {
            Brush = Input.Shift && Sculpt.Brush != SculptBrush.Grab ? SculptBrush.Smooth : Sculpt.Brush,
            Radius = Sculpt.Radius / ObjScale(o), Strength = Sculpt.Strength, Falloff = Sculpt.Falloff,
            Invert = Sculpt.Invert ^ Input.Ctrl, SymX = Sculpt.SymX, SymY = Sculpt.SymY, SymZ = Sculpt.SymZ,
        };
        return s;
    }

    void AfterDab(SceneObject o)
    {
        o.Touch(); sculptVersion = o.Version;
    }

    void EndSculptStroke(SceneObject o)
    {
        if (!sculpting) return;
        sculpting = false;
        if (Sculpt.Brush == SculptBrush.Grab) sculptSession?.EndGrab(); else sculptSession?.RecalcNormals();
        if (o != null) { o.Mesh.Validate(); AfterDab(o); }
    }

    /// <summary>Brush circle on the surface (and its mirrors).</summary>
    void SculptCursorLines()
    {
        if (!SculptMode || !sculptHover.Hit) return;
        var o = Current; if (o == null) return;
        var col = Sculpt.Invert ^ Input.Ctrl ? new Vector4(0.45f, 0.75f, 1f, 1) : new Vector4(0.95f, 0.42f, 0.11f, 1);
        Circle(sculptHover.P, sculptHover.N, sculptHover.R, col);
        if (Sculpt.SymX || Sculpt.SymY || Sculpt.SymZ)
        {
            Matrix4x4.Invert(o.World, out var inv);
            var pl = Vector3.Transform(sculptHover.P, inv); var nl = Vector3.TransformNormal(sculptHover.N, inv);
            var f = new Vector3(Sculpt.SymX ? -1 : 1, Sculpt.SymY ? -1 : 1, Sculpt.SymZ ? -1 : 1);
            Circle(Vector3.Transform(pl * f, o.World), Vector3.Normalize(Vector3.TransformNormal(nl * f, o.World)), sculptHover.R, col * 0.6f);
        }
    }

    /// <summary>The brush panel at the left of the viewport.</summary>
    void DrawSculptPanel()
    {
        if (!SculptMode) return;
        var o = Current;
        if (o == null) { SculptMode = false; return; }
        var vp = Viewports[ActiveViewport];
        ImGui.SetNextWindowPos(new Vector2(vp.X + 8, vp.Y + 40));
        ImGui.SetNextWindowBgAlpha(0.9f);
        ImGui.Begin("##sculpt", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        Ui.Label("SCULPT", Theme.Gold);
        var sz = new Vector2(74, 26);
        for (int i = 0; i < SculptBrushes.Length; i++)
        {
            var (b, label, tip) = SculptBrushes[i];
            if (i % 2 == 1) ImGui.SameLine(0, 4);
            if (Theme.Toggle(label, Sculpt.Brush == b, sz)) Sculpt.Brush = b;
            Theme.Tip(tip);
        }
        ImGui.Spacing();
        ImGui.SetNextItemWidth(152); ImGui.DragFloat("##srad", ref Sculpt.Radius, Sculpt.Radius * 0.01f + 0.0005f, 0.002f, 50f, "Radius %.3f m");
        Theme.Tip("[ and ] change it while sculpting");
        ImGui.SetNextItemWidth(152); ImGui.SliderFloat("##sstr", ref Sculpt.Strength, 0.01f, 1f, "Strength %.2f");
        ImGui.SetNextItemWidth(152); ImGui.SliderFloat("##sfall", ref Sculpt.Falloff, 0f, 1f, "Softness %.2f");
        ImGui.Checkbox("Invert (Ctrl)", ref Sculpt.Invert);
        Ui.Label("Symmetry", Theme.TextDim);
        var bw = new Vector2(48, 0);
        if (Theme.Toggle("X##ssx", Sculpt.SymX, bw)) Sculpt.SymX ^= true; ImGui.SameLine(0, 4);
        if (Theme.Toggle("Y##ssy", Sculpt.SymY, bw)) Sculpt.SymY ^= true; ImGui.SameLine(0, 4);
        if (Theme.Toggle("Z##ssz", Sculpt.SymZ, bw)) Sculpt.SymZ ^= true;
        Ui.Label("Whole mesh", Theme.TextDim);
        if (ImGui.Button("Smooth all", new Vector2(74, 0))) SculptWhole(o, false); ImGui.SameLine(0, 4);
        if (ImGui.Button("Relax all", new Vector2(74, 0))) SculptWhole(o, true);
        Theme.Tip("Five passes over the whole mesh (open borders stay put). Relax keeps the shape.");
        Ui.TextDisabled($"{o.Mesh.Verts.Count:N0} verts");
        if (ImGui.Button("Exit sculpt", new Vector2(152, 0))) ToggleSculptMode();
        ImGui.End();
    }

    void SculptWhole(SceneObject o, bool relax)
    {
        Do(relax ? "Relax all" : "Smooth all", () =>
        {
            EnsureSculptSession(o);
            sculptSession.SmoothAll(5, 0.5f, relax);
            o.Mesh.Validate(); AfterDab(o);
        });
    }
}
