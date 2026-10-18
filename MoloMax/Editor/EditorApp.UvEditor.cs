using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// UV editor: the UVs of the current object in 2D over its texture. Selection is the same polygon selection as the
/// 3D view (faces or whole islands). Drag to move, buttons to rotate / flip / scale / fit / pack, texel density
/// shown and settable in px per metre.
/// </summary>
public sealed partial class EditorApp
{
    bool uvOpen, uvIsland = true, uvChecker;
    Vector2? uvWinAt; // films: where the window stands
    Vector2 uvPan = new(20, 20); float uvZoom = 400;
    bool uvDragging, uvBoxing; Vector2 uvDragStart, uvLastMouse;
    float uvTargetDensity = 256; int uvTexSize = 1024;

    public void OpenUvEditor() { uvOpen = true; if (Current != null && Level != SubObject.Polygon && Level != SubObject.Element) SetLevel(SubObject.Polygon); }

    void DrawUvEditor()
    {
        if (!uvOpen) return;
        if (uvWinAt is Vector2 at) { ImGui.SetNextWindowSize(new Vector2(600, 600), ImGuiCond.Always); ImGui.SetNextWindowPos(at, ImGuiCond.Always); }
        else
        {
            ImGui.SetNextWindowSize(new Vector2(720, 640), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new Vector2(60, menuH + toolbarH + 40), ImGuiCond.FirstUseEver);
        }
        if (!ImGui.Begin("UV Editor###uved", ref uvOpen, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) { ImGui.End(); return; }
        var o = Current;
        if (o == null || o.Mesh.Faces.Count == 0) { Ui.TextDisabled("Select one object with polygons."); ImGui.End(); return; }
        var m = o.Mesh; var sel = SubSel(o).Faces;

        // ---- toolbar
        if (Theme.Toggle("Island", uvIsland, new Vector2(64, 0))) uvIsland = true; ImGui.SameLine(0, 2);
        if (Theme.Toggle("Face", !uvIsland, new Vector2(52, 0))) uvIsland = false; ImGui.SameLine();
        IEnumerable<int> Target() => sel.Count > 0 ? sel.ToList() : Enumerable.Range(0, m.Faces.Count);
        void Op(string name, Action a) { Do(name, () => { a(); o.Touch(); }); }
        var (bmn, bmx) = UvTools.Bounds(m, Target());
        var pivot = (bmn + bmx) * 0.5f;
        if (ImGui.Button("Rot 90")) Op("UV rotate", () => UvTools.Transform(m, Target(), UvTools.Around(pivot, Matrix3x2.CreateRotation(MathF.PI / 2)))); ImGui.SameLine();
        if (ImGui.Button("Flip U")) Op("UV flip", () => UvTools.Transform(m, Target(), UvTools.Around(pivot, Matrix3x2.CreateScale(-1, 1)))); ImGui.SameLine();
        if (ImGui.Button("Flip V")) Op("UV flip", () => UvTools.Transform(m, Target(), UvTools.Around(pivot, Matrix3x2.CreateScale(1, -1)))); ImGui.SameLine();
        if (ImGui.Button("x2")) Op("UV scale", () => UvTools.Transform(m, Target(), UvTools.Around(pivot, Matrix3x2.CreateScale(2)))); ImGui.SameLine();
        if (ImGui.Button("/2")) Op("UV scale", () => UvTools.Transform(m, Target(), UvTools.Around(pivot, Matrix3x2.CreateScale(0.5f)))); ImGui.SameLine();
        if (ImGui.Button("Fit 0-1")) Op("UV fit", () => UvTools.Fit01(m, Target())); ImGui.SameLine();
        if (ImGui.Button("Pack")) Op("UV pack", () => UvTools.Pack(m, Target())); Theme.Tip("Packs the islands into 0..1 (selection, or everything)."); ImGui.SameLine();
        if (ImGui.Button("Unwrap")) SmartUv(); Theme.Tip("Smart UV Project on the selection (or the whole object).");

        float dens = UvTools.TexelDensity(m, Target(), uvTexSize, o.World);
        ImGui.AlignTextToFramePadding(); Ui.Text($"Texel density: {dens:0} px/m");
        ImGui.SameLine(); ImGui.SetNextItemWidth(70);
        int ts = Array.IndexOf(new[] { 256, 512, 1024, 2048 }, uvTexSize); if (ts < 0) ts = 2;
        if (ImGui.Combo("##uvts", ref ts, new[] { "256", "512", "1024", "2048" }, 4)) uvTexSize = new[] { 256, 512, 1024, 2048 }[ts];
        Theme.Tip("Texture size used for the density (GTA: 1024).");
        ImGui.SameLine(); ImGui.SetNextItemWidth(80); ImGui.DragFloat("##uvtd", ref uvTargetDensity, 1, 16, 4096, "%.0f px/m");
        ImGui.SameLine(); if (ImGui.Button("Set density")) Op("UV texel density", () => UvTools.SetDensity(m, Target(), uvTargetDensity, uvTexSize, o.World));
        Theme.Tip("Scales each island so every part of the model gets the same sharpness. 256 px/m is a good GTA interior value.");
        ImGui.SameLine(); ImGui.Checkbox("Checker", ref uvChecker);

        // ---- canvas
        var c0 = ImGui.GetCursorScreenPos(); var size = ImGui.GetContentRegionAvail();
        if (size.X < 50 || size.Y < 50) { ImGui.End(); return; }
        ImGui.InvisibleButton("##uvcanvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonMiddle | ImGuiButtonFlags.MouseButtonRight);
        bool hov = ImGui.IsItemHovered(); bool active = ImGui.IsItemActive();
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(c0, c0 + size, true);
        dl.AddRectFilled(c0, c0 + size, 0xFF2A2A2A);
        Vector2 ToS(Vector2 uv) => c0 + uvPan + uv * uvZoom;
        Vector2 ToUv(Vector2 s) => (s - c0 - uvPan) / uvZoom;
        var io = ImGui.GetIO();
        if (hov && io.MouseWheel != 0)
        {
            var before = ToUv(io.MousePos);
            uvZoom = Math.Clamp(uvZoom * (io.MouseWheel > 0 ? 1.15f : 1 / 1.15f), 20, 20000);
            uvPan += (ToS(before) - io.MousePos) * -1;
        }
        if (active && (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right))) uvPan += io.MouseDelta;
        if (hov && ImGui.IsKeyPressed(ImGuiKey.F, false)) { var (a, b) = UvTools.Bounds(m, Target()); var sz = MathF.Max(b.X - a.X, b.Y - a.Y); if (sz > 1e-5f) { uvZoom = MathF.Min(size.X, size.Y) * 0.85f / sz; uvPan = size * 0.5f - (a + b) * 0.5f * uvZoom; } }

        // texture (diffuse of the first selected face's material, else the object's first)
        int matId = sel.Count > 0 ? m.Faces[sel.First()].MatId : m.Faces[0].MatId;
        var mat = Scene.Material(matId);
        var srv = !uvChecker && mat.DiffuseTexture != null ? renderer.Textures.Get(mat.DiffuseTexture) : null;
        var t0 = ToS(Vector2.Zero); var t1 = ToS(Vector2.One);
        if (srv != null) dl.AddImage(imgui.IdOf(srv), t0, t1);
        else
        {
            int n = 8; float cell = (t1.X - t0.X) / n;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                    dl.AddRectFilled(t0 + new Vector2(x, y) * cell, t0 + new Vector2(x + 1, y + 1) * cell, (x + y) % 2 == 0 ? 0xFF505050u : 0xFF3C3C3Cu);
        }
        dl.AddRect(t0, t1, 0xFFB0B0B0);
        // repeat tiles faintly (GTA textures tile)
        for (int k = -1; k <= 1; k++) for (int j = -1; j <= 1; j++) if (k != 0 || j != 0) dl.AddRect(ToS(new Vector2(k, j)), ToS(new Vector2(k + 1, j + 1)), 0x30FFFFFF);

        // faces
        uint wire = 0xFFE0E0E0, selFill = 0x553030FF, selWire = 0xFF4040FF;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi]; bool s = sel.Contains(fi);
            if (s)
                foreach (var (a, b, c) in Triangulator.Triangulate(m, f))
                    dl.AddTriangleFilled(ToS(f.UV[a]), ToS(f.UV[b]), ToS(f.UV[c]), selFill);
            for (int i = 0; i < f.V.Length; i++) dl.AddLine(ToS(f.UV[i]), ToS(f.UV[(i + 1) % f.V.Length]), s ? selWire : wire, 1f);
        }

        // ---- interaction
        if (hov && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var uv = ToUv(io.MousePos);
            int hit = FaceAtUv(m, uv);
            uvLastMouse = io.MousePos; uvDragStart = io.MousePos;
            if (hit >= 0 && sel.Contains(hit) && !io.KeyShift && !io.KeyCtrl) { uvDragging = true; Undo.Record("UV move", Scene); }
            else if (hit >= 0)
            {
                var pick = uvIsland ? UvTools.Islands(m).First(i => i.Contains(hit)) : new List<int> { hit };
                if (!io.KeyShift && !io.KeyCtrl) sel.Clear();
                if (io.KeyCtrl) foreach (var f in pick) sel.Remove(f); else foreach (var f in pick) sel.Add(f);
                uvDragging = !io.KeyShift && !io.KeyCtrl; if (uvDragging) Undo.Record("UV move", Scene);
            }
            else uvBoxing = true;
        }
        if (uvDragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var d = (io.MousePos - uvLastMouse) / uvZoom;
                if (io.KeyCtrl) d = Vector2.Zero; // hold Ctrl to stop
                if (d != Vector2.Zero) { UvTools.Transform(m, sel, Matrix3x2.CreateTranslation(d)); o.Touch(); Dirty = true; }
                uvLastMouse = io.MousePos;
            }
            else uvDragging = false;
        }
        if (uvBoxing)
        {
            dl.AddRect(Vector2.Min(uvDragStart, io.MousePos), Vector2.Max(uvDragStart, io.MousePos), 0xFFFFFFFF);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                uvBoxing = false;
                var a = ToUv(Vector2.Min(uvDragStart, io.MousePos)); var b = ToUv(Vector2.Max(uvDragStart, io.MousePos));
                if (!io.KeyShift && !io.KeyCtrl) sel.Clear();
                if ((b - a).Length() * uvZoom > 3)
                    for (int fi = 0; fi < m.Faces.Count; fi++)
                    {
                        var c = Vector2.Zero; foreach (var uv in m.Faces[fi].UV) c += uv; c /= m.Faces[fi].UV.Length;
                        if (c.X >= a.X && c.X <= b.X && c.Y >= a.Y && c.Y <= b.Y) { if (io.KeyCtrl) sel.Remove(fi); else sel.Add(fi); }
                    }
            }
        }
        dl.PopClipRect();
        var mu = ToUv(io.MousePos);
        dl.AddText(c0 + new Vector2(8, size.Y - 20), 0xFFB0B0B0, hov ? $"u {mu.X:0.000}  v {mu.Y:0.000}    wheel zoom, right / middle drag pan, F frame, drag selected to move" : $"{sel.Count} face(s) selected");
        ImGui.End();
    }

    static int FaceAtUv(PolyMesh m, Vector2 p)
    {
        for (int fi = m.Faces.Count - 1; fi >= 0; fi--)
        {
            var uv = m.Faces[fi].UV; bool inside = false;
            for (int i = 0, j = uv.Length - 1; i < uv.Length; j = i++)
                if ((uv[i].Y > p.Y) != (uv[j].Y > p.Y) && p.X < (uv[j].X - uv[i].X) * (p.Y - uv[i].Y) / (uv[j].Y - uv[i].Y + 1e-12f) + uv[i].X) inside = !inside;
            if (inside) return fi;
        }
        return -1;
    }
}
