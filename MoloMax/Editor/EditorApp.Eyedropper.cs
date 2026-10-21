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
    /// <summary>The material the pipette copied (-1: none yet). Paste puts it on the selection, as many times as wanted.</summary>
    int copiedMaterial = -1;

    /// <summary>Paste button beside the pipette: the copied material goes on the selected faces (or the selected objects).</summary>
    void PasteMaterialButton(string id, float size = 22)
    {
        bool can = copiedMaterial >= 0 && (Level is SubObject.Polygon or SubObject.Element ? Current is { } c && SubSel(c).Faces.Count > 0 : Selected.Count > 0);
        if (!can) ImGui.BeginDisabled();
        bool clicked = ImGui.Button("##" + id, new Vector2(size, size)); TutRect(id);
        var mn = ImGui.GetItemRectMin(); var mx = ImGui.GetItemRectMax(); var dl = ImGui.GetWindowDrawList();
        uint col = ImGui.GetColorU32(ImGuiCol.Text);
        // a clipboard: board, clip on top, a colour swatch of the copied material
        float w = mx.X - mn.X; var a = mn + new Vector2(w * 0.24f, w * 0.2f); var b = mx - new Vector2(w * 0.24f, w * 0.14f);
        if (copiedMaterial >= 0 && Scene.Materials.FirstOrDefault(m => m.Id == copiedMaterial) is { } cm)
            dl.AddRectFilled(a + new Vector2(2, w * 0.2f), b - new Vector2(2, 2), ImGui.ColorConvertFloat4ToU32(new Vector4(cm.ViewportColour, 1)));
        dl.AddRect(a, b, col, 1.5f, ImDrawFlags.None, 1.6f);
        dl.AddRectFilled(new Vector2(mn.X + w * 0.38f, mn.Y + w * 0.13f), new Vector2(mx.X - w * 0.38f, mn.Y + w * 0.27f), col, 1);
        if (!can) ImGui.EndDisabled();
        if (clicked) PasteMaterial();
        string tip = copiedMaterial < 0 ? "Paste: copy a material with the pipette first"
            : $"Paste {Scene.Material(copiedMaterial).Name} on the selected {(Level is SubObject.Polygon or SubObject.Element ? "faces" : "objects")}";
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
    }

    public void PasteMaterial()
    {
        if (copiedMaterial < 0) { SetStatus("Copy a material with the pipette first"); return; }
        var mat = Scene.Material(copiedMaterial);
        int n = Level is SubObject.Polygon or SubObject.Element && Current is { } c ? SubSel(c).Faces.Count : Selected.Count;
        AssignMaterial(mat.Id);
        SetStatus($"Pasted {mat.Name} on {n} {(Level is SubObject.Polygon or SubObject.Element ? "face(s)" : "object(s)")}");
    }

    /// <summary>The pipette button (drawn icon). Returns true on the frame it was clicked.</summary>
    bool EyedropperButton(string id, float size = 22)
    {
        bool clicked = Theme.Toggle("##" + id, eyedropper, new Vector2(size, size)); TutRect(id);
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
                ? "Pipette: click a face to copy its material, then Paste (Esc / right-click: cancel)"
                : "Pipette: click a face to copy its material, then Paste (Esc / right-click: cancel)");
        }
        Theme.Tip("Pipette: click a face in the viewport to copy its material.\nThen select other faces and click Paste (the button beside it).");
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
        selectedMaterial = mat.Id; slotMatId = mat.Id; copiedMaterial = mat.Id;
        SetStatus($"Pipette: {mat.Name} copied. Select faces (or objects) and click Paste");
        return true;
    }
}
