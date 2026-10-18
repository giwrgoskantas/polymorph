using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Cube projection (3ds Max UVW Map "Box"): right-click > Cube projection. The values (size U / V, offset, rotation,
/// world or object space) and the material stay editable in a panel at the bottom of the view, like Adjust Last
/// Operation, and every change updates the model at once.
/// </summary>
public sealed partial class EditorApp
{
    sealed class CubeProjState
    {
        public List<(int ObjId, HashSet<int> Faces)> Targets = new();
        public float SizeU = 2f, SizeV = 2f, OffU, OffV, Rotation;
        public bool Lock = true, World = true;
        public int MaterialId = -1;
    }
    CubeProjState cubeProj;

    public void StartCubeProjection()
    {
        var objs = SelObjects().Where(o => o.Mesh.Faces.Count > 0).ToList();
        if (objs.Count == 0) { SetStatus("Select a mesh (or polygons in Edit Mode) first."); return; }
        var st = new CubeProjState { SizeU = cubeProj?.SizeU ?? 2f, SizeV = cubeProj?.SizeV ?? 2f, Lock = cubeProj?.Lock ?? true, World = cubeProj?.World ?? true };
        foreach (var o in objs)
        {
            HashSet<int> faces = null;
            if (Level is SubObject.Polygon or SubObject.Element && o == Current && SubSel(o).Faces.Count > 0) faces = new HashSet<int>(SubSel(o).Faces);
            st.Targets.Add((o.Id, faces ?? Enumerable.Range(0, o.Mesh.Faces.Count).ToHashSet()));
        }
        // material already on the faces (if they all share one)
        var mats = st.Targets.SelectMany(t => t.Faces.Select(f => Scene.Find(t.ObjId).Mesh.Faces[f].MatId)).Distinct().ToList();
        st.MaterialId = mats.Count == 1 ? mats[0] : -1;
        Undo.Record("Cube projection", Scene);
        cubeProj = st; lastAdd = null;
        ApplyCubeProjection();
        SetStatus("Cube projection: adjust size, offset, rotation and material in the panel at the bottom of the view.");
    }

    void ApplyCubeProjection()
    {
        var st = cubeProj; if (st == null) return;
        float a = st.Rotation * MathF.PI / 180f, ca = MathF.Cos(a), sa = MathF.Sin(a);
        foreach (var (id, faces) in st.Targets)
        {
            var o = Scene.Find(id); if (o == null) continue;
            var m = o.Mesh;
            MeshOps.BoxMap(m, faces, 1f, st.World ? o.World : null);
            foreach (var fi in faces)
            {
                if (fi >= m.Faces.Count) continue;
                var f = m.Faces[fi];
                for (int i = 0; i < f.UV.Length; i++)
                {
                    var uv = f.UV[i];
                    uv = new Vector2(uv.X * ca - uv.Y * sa, uv.X * sa + uv.Y * ca);
                    f.UV[i] = new Vector2(uv.X / MathF.Max(st.SizeU, 1e-3f) + st.OffU, uv.Y / MathF.Max(st.SizeV, 1e-3f) + st.OffV);
                }
                if (st.MaterialId >= 0) f.MatId = st.MaterialId;
            }
            o.Touch();
        }
        Dirty = true;
    }

    void DrawCubeProjectionPanel()
    {
        var st = cubeProj; if (st == null) return;
        if (st.Targets.All(t => Scene.Find(t.ObjId) == null)) { cubeProj = null; return; }
        var vp = Viewports[ActiveViewport];
        ImGui.SetNextWindowPos(new Vector2(vp.X + 10, vp.Y + vp.H - 10), ImGuiCond.Always, new Vector2(0, 1));
        ImGui.SetNextWindowSizeConstraints(new Vector2(300, 0), new Vector2(380, 700));
        ImGui.SetNextWindowBgAlpha(0.94f);
        bool keep = true;
        if (!ImGui.Begin("Cube Projection###cubeproj", ref keep, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing)) { ImGui.End(); if (!keep) cubeProj = null; return; }
        int faces = st.Targets.Sum(t => t.Faces.Count);
        Ui.TextDisabled($"{st.Targets.Count} object(s), {faces} polygon(s)");
        bool ch = false;
        float lw = 110;
        void Row(string s) { ImGui.AlignTextToFramePadding(); Ui.Text(s); ImGui.SameLine(lw); ImGui.SetNextItemWidth(-1); }

        // material first: what the modeller sees
        Ui.Label("MATERIAL", Theme.TextDim);
        var names = new List<string> { "(keep the current ones)" }; names.AddRange(Scene.Materials.Select(x => $"{x.Name}  ({x.Shader})"));
        int mi = st.MaterialId < 0 ? 0 : Scene.Materials.FindIndex(x => x.Id == st.MaterialId) + 1;
        ImGui.SetNextItemWidth(-30);
        if (ImGui.Combo("##cpmat", ref mi, names.ToArray(), names.Count)) { st.MaterialId = mi <= 0 ? -1 : Scene.Materials[mi - 1].Id; ch = true; }
        ImGui.SameLine(0, 4);
        if (ImGui.Button("L##cplib", new Vector2(22, 0))) OpenLibrary(LibraryAction.CubeProj);
        Theme.Tip("Pick a GTA material in Molo Materials: its real tile size is used.");
        if (st.MaterialId >= 0 && Scene.Material(st.MaterialId) is { } gm)
        {
            var srv = renderer.Textures.Get(gm.DiffuseTexture);
            if (srv != null) { ImGui.Image(imgui.IdOf(srv), new Vector2(64, 64)); ImGui.SameLine(); }
            ImGui.BeginGroup();
            Ui.Text(gm.Name);
            Ui.TextDisabled(gm.Shader);
            if (ImGui.SmallButton("Open in Material Editor")) { materialEditorOpen = true; selectedMaterial = gm.Id; }
            ImGui.EndGroup();
        }

        Ui.Label("PROJECTION", Theme.TextDim);
        Row("Size U"); if (ImGui.DragFloat("##cpu", ref st.SizeU, 0.01f, 0.01f, 100, "%.2f m")) { if (st.Lock) st.SizeV = st.SizeU; ch = true; }
        Row("Size V"); ImGui.BeginDisabled(st.Lock); if (ImGui.DragFloat("##cpv", ref st.SizeV, 0.01f, 0.01f, 100, "%.2f m")) ch = true; ImGui.EndDisabled();
        if (ImGui.Checkbox("Square (U = V)", ref st.Lock)) { if (st.Lock) st.SizeV = st.SizeU; ch = true; }
        ImGui.SameLine();
        if (ImGui.Checkbox("World space", ref st.World)) ch = true;
        Theme.Tip("On: the texture is fixed in the world, so it lines up across objects and walls. Off: it follows the object.");
        float b4 = (ImGui.GetContentRegionAvail().X - 12) / 4;
        foreach (var s in new[] { 0.5f, 1f, 2f, 4f })
        {
            if (s != 0.5f) ImGui.SameLine(0, 4);
            if (ImGui.Button($"{s:0.#} m##cps{s}", new Vector2(b4, 0))) { st.SizeU = st.SizeV = s; ch = true; }
        }
        Row("Offset U"); if (ImGui.DragFloat("##cpou", ref st.OffU, 0.005f, -10, 10, "%.3f")) ch = true;
        Row("Offset V"); if (ImGui.DragFloat("##cpov", ref st.OffV, 0.005f, -10, 10, "%.3f")) ch = true;
        Row("Rotation"); if (ImGui.DragFloat("##cpr", ref st.Rotation, 1f, -360, 360, "%.0f°")) ch = true;
        float b3 = (ImGui.GetContentRegionAvail().X - 8) / 3;
        if (ImGui.Button("-90°", new Vector2(b3, 0))) { st.Rotation -= 90; ch = true; } ImGui.SameLine(0, 4);
        if (ImGui.Button("+90°", new Vector2(b3, 0))) { st.Rotation += 90; ch = true; } ImGui.SameLine(0, 4);
        if (ImGui.Button("Reset", new Vector2(b3, 0))) { st.OffU = st.OffV = st.Rotation = 0; ch = true; }
        if (ch) ApplyCubeProjection();
        ImGui.Separator();
        if (ImGui.Button("Done", new Vector2(-1, 26))) cubeProj = null;
        Ui.TextDisabled("Ctrl+Z undoes the whole projection.");
        ImGui.End();
        if (!keep) cubeProj = null;
    }
}
