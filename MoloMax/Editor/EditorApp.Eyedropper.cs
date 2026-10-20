using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Material eyedropper: click the pipette, then a face in the viewport. Its material becomes the picked one (Material
/// Editor and Material tab), and in Polygon mode with faces selected it is put on those faces straight away.
/// </summary>
public sealed partial class EditorApp
{
    bool eyedropper;

    /// <summary>The pipette button (drawn icon). Returns true on the frame it was clicked.</summary>
    bool EyedropperButton(string id, float size = 22)
    {
        bool clicked = Theme.Toggle("##" + id, eyedropper, new Vector2(size, size));
        var mn = ImGui.GetItemRectMin(); var mx = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        uint col = ImGui.GetColorU32(ImGuiCol.Text);
        // the pipette: a bulb top right, a thin glass tube down to the tip bottom left
        var c = (mn + mx) * 0.5f; float s = (mx.X - mn.X) * 0.32f;
        var top = c + new Vector2(s * 0.75f, -s * 0.75f);
        var tip = c + new Vector2(-s, s);
        dl.AddCircleFilled(top, s * 0.38f, col);
        dl.AddLine(top + new Vector2(-s * 0.55f, -s * 0.05f), top + new Vector2(s * 0.05f, s * 0.55f), col, 2f);
        dl.AddLine(top, tip, col, 2.4f);
        dl.AddCircleFilled(tip, 1.4f, col);
        if (clicked)
        {
            eyedropper = !eyedropper;
            if (eyedropper) SetStatus(Level is SubObject.Polygon or SubObject.Element && Current is { } cur && SubSel(cur).Faces.Count > 0
                ? "Pipette: click a face, its material goes on your selected faces (Esc / right-click: cancel)"
                : "Pipette: click a face to pick its material (Esc / right-click: cancel)");
        }
        Theme.Tip("Pipette: click a face in the viewport to take its material.\nIn Polygon mode with faces selected, the material goes on them.");
        return clicked;
    }

    /// <summary>Viewport side of the pipette. True when it took the mouse this frame.</summary>
    bool EyedropperInput(Viewport hv, bool uiMouse)
    {
        if (!eyedropper) return false;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) || (hv != null && !uiMouse && Input.Pressed(System.Windows.Forms.MouseButtons.Right)))
        { eyedropper = false; SetStatus("Pipette cancelled"); return true; }
        if (hv == null || uiMouse) return false;
        ImGui.SetTooltip("Pick a material");
        if (!Input.Pressed(System.Windows.Forms.MouseButtons.Left) || Input.Alt) return false;
        EyedropAt(hv, Input.Mouse);
        return true;
    }

    /// <summary>Takes the material of the face at that screen point (and puts it on the selected faces in Polygon mode).</summary>
    public bool EyedropAt(Viewport vp, Vector2 screen)
    {
        eyedropper = false;
        var ray = RayAt(vp, screen);
        var hit = Picking.PickNearest(Scene.Objects, ray, o => o.Hidden);
        if (hit is not { } h || h.Object?.Mesh == null || h.Face < 0 || h.Face >= h.Object.Mesh.Faces.Count)
        { SetStatus("Pipette: no face under the mouse"); return false; }
        int matId = h.Object.Mesh.Faces[h.Face].MatId;
        var mat = Scene.Material(matId);
        selectedMaterial = mat.Id; slotMatId = mat.Id;
        var cur = Current;
        if (Level is SubObject.Polygon or SubObject.Element && cur != null && SubSel(cur).Faces.Count > 0
            && !(h.Object == cur && SubSel(cur).Faces.Contains(h.Face)))
        {
            int n = SubSel(cur).Faces.Count;
            AssignMaterial(mat.Id);
            SetStatus($"Pipette: {mat.Name} put on {n} selected face(s)");
        }
        else SetStatus($"Pipette: {mat.Name} picked" + (Level == SubObject.Object ? "" : " (select faces and Assign to put it on them)"));
        return true;
    }
}
