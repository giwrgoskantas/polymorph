using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;
using MoloMax.Core.Paint;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// The Molo Paint panel, in four tabs so each thing has its place:
/// Tools (what the mouse does), Layers (the stack, masks, filters), Bake (high-poly, mesh maps), Export (textures, preview).
/// </summary>
public sealed partial class EditorApp
{
    int paintTab; // 0 tools, 1 layers, 2 bake, 3 export
    string smartSaveName = "";
    string layerRename; string layerRenameBuf = "";

    static readonly string[] BlendNames = { "Normal", "Multiply", "Overlay", "Screen", "Add" };
    static readonly string[] ViewNames = { "Final", "Roughness", "Metal", "Height", "Ambient occlusion", "Curvature", "Selected layer mask", "Emissive", "Opacity" };
    static readonly string[] ScopeNames = { "Face", "Element", "UV island", "Whole set" };
    static readonly string[] FormatNames = { "GTA DDS only", "+ PNG copies", "+ TGA copies", "+ Sollumz folder (PNG)" };
    /// <summary>"Where does this layer show?" in plain words, mapped to the mask generators.</summary>
    static readonly (string Label, MaskGen Mask, bool Invert)[] WhereChoices =
    {
        ("Everywhere", MaskGen.None, false),
        ("Where I paint", MaskGen.Painted, false),
        ("Worn edges", MaskGen.Edges, false),
        ("Dirt in corners", MaskGen.AmbientOcclusion, false),
        ("In cavities", MaskGen.Cavity, false),
        ("Dust on top", MaskGen.Top, false),
        ("Underneath", MaskGen.Bottom, false),
        ("Dirty bottom", MaskGen.GradientZ, true),
        ("Patches (noise)", MaskGen.Noise, false),
        ("Leaks (streaks)", MaskGen.Streaks, false),
        ("Tile grout", MaskGen.Tiles, false),
    };
    static readonly (string Label, Vector3 Colour)[] Swatches =
    {
        ("White", new(0.9f, 0.9f, 0.88f)), ("Black", new(0.05f, 0.05f, 0.05f)), ("Grey", new(0.45f, 0.45f, 0.45f)), ("Red", new(0.7f, 0.08f, 0.06f)),
        ("Orange", new(0.85f, 0.4f, 0.08f)), ("Yellow", new(0.9f, 0.75f, 0.15f)), ("Green", new(0.2f, 0.45f, 0.15f)), ("Blue", new(0.12f, 0.25f, 0.6f)),
        ("Brown", new(0.3f, 0.18f, 0.09f)), ("Rust", new(0.45f, 0.2f, 0.08f)), ("Dirt", new(0.18f, 0.14f, 0.1f)), ("Concrete", new(0.55f, 0.54f, 0.5f)),
    };
    static readonly (PaintTool Tool, string Label, string Tip)[] ToolButtons =
    {
        (PaintTool.Brush, "Brush", "Paint (B)"), (PaintTool.Eraser, "Eraser", "Erase the layer, or hide in mask mode (E)"),
        (PaintTool.Fill, "Fill", "Fill a face, an element, a UV island or everything (F)"), (PaintTool.Decal, "Decal", "Stamp a GTA decal from the shelf"),
        (PaintTool.Clone, "Clone", "Ctrl+click the source, then paint the copy"), (PaintTool.Smudge, "Smudge", "Drag the colours along the stroke"),
        (PaintTool.VertexColour, "Vertex col.", "Paint the GTA vertex colours (green / white / red)"),
    };

    void PaintProps()
    {
        var o = Current;
        if (o == null) { Ui.TextDisabled("Select the object to paint."); return; }
        var doc = o.Paint;
        if (doc == null)
        {
            Ui.TextWrapped("Paint the model in layers, then Polymorph makes the GTA textures (diffuse, normal, spec).");
            ImGui.Spacing();
            ImGui.SetNextItemWidth(-90);
            int ri = Math.Max(0, Array.IndexOf(PaintSizes, paintRes));
            if (ImGui.Combo("Resolution", ref ri, PaintSizes.Select(s => s + " px").ToArray(), PaintSizes.Length)) paintRes = PaintSizes[ri];
            Theme.Tip("Working resolution. The GTA export is reduced to 1024 by default (Export tab).");
            if (Theme.Toggle("PAINT THIS OBJECT", true, new Vector2(-1, 40)))
            {
                if (!TexelMap.HasUsableUvs(o.Mesh)) DoMesh("Smart UV Project", (oo, m, s) => UvUnwrap.SmartProject(m)); // no UVs: unwrapped automatically
                StartPaint(o);
            }
            Ui.TextDisabled("UVs are made automatically if the model has none. One texture set per material.");
            return;
        }
        var ts = doc.Cur;
        if (paintMeshVersion.TryGetValue(o.Id, out var ver) && ver != o.Version && Level == SubObject.Object && paintTool != PaintTool.VertexColour)
        {
            Ui.TextColored(new Vector4(1, 0.65f, 0.3f, 1), "The model changed.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Update")) { foreach (var s in doc.Sets) { s.Rebuild(o.Mesh, aoRays); UploadPaint(o, s, true); } paintMeshVersion[o.Id] = o.Version; }
        }

        // header: on / off, texture set
        if (Theme.Toggle(paintMode ? "PAINTING  -  Esc to stop" : "START PAINTING", paintMode, new Vector2(-1, 32))) paintMode = !paintMode;
        if (Level != SubObject.Object) Ui.TextColored(new Vector4(1, 0.65f, 0.3f, 1), "Go back to Object Mode (Tab) to paint.");
        if (doc.Sets.Count > 1)
        {
            ImGui.SetNextItemWidth(-90);
            int a = doc.Active;
            if (ImGui.Combo("Texture set", ref a, doc.Sets.Select(s => s.Name).ToArray(), doc.Sets.Count)) { doc.Active = a; paintUndo.Clear(); }
            Theme.Tip("One texture set per material of the model, each with its own layers and textures.");
        }

        // tabs
        ImGui.Spacing();
        string[] tabs = { "Tools", "Layers", "Bake", "Export" };
        float tw = (ImGui.GetContentRegionAvail().X - 6) / 4;
        for (int i = 0; i < tabs.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 2);
            if (Theme.Toggle(tabs[i], paintTab == i, new Vector2(tw, 26))) paintTab = i;
        }
        ImGui.Separator();
        switch (paintTab)
        {
            case 0: PaintToolsTab(o, ts); break;
            case 1: PaintLayersTab(o, ts); break;
            case 2: PaintBakeTab(o, ts); break;
            default: PaintExportTab(o, ts); break;
        }
    }

    // ------------------------------------------------------------------------------ Tools

    void PaintToolsTab(SceneObject o, TextureSet ts)
    {
        float w4 = (ImGui.GetContentRegionAvail().X - 6) / 4;
        for (int i = 0; i < ToolButtons.Length; i++)
        {
            if (i % 4 != 0) ImGui.SameLine(0, 2);
            var (t, label, tip) = ToolButtons[i];
            if (Theme.Toggle(label, paintTool == t, new Vector2(w4, 28))) SetPaintTool(t);
            Theme.Tip(tip);
        }
        ImGui.Spacing();

        if (paintTool == PaintTool.VertexColour) { VertexColourOptions(o); BrushShape(false); return; }

        // tool-specific
        switch (paintTool)
        {
            case PaintTool.Decal:
                Ui.TextDisabled(brush.Stamp != null ? "Decal: " + brush.StampName : "Pick a decal in the shelf (right).");
                ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Angle", ref brush.StampAngle, -180, 180, "%.0f°");
                ImGui.Checkbox("One print per click", ref brush.StampOnce);
                break;
            case PaintTool.Fill:
                ImGui.SetNextItemWidth(-90); int sc = (int)fillScope;
                if (ImGui.Combo("Fill", ref sc, ScopeNames, ScopeNames.Length)) fillScope = (FillScope)sc;
                Ui.TextDisabled("Click the model: the brush colour / channels fill that part.");
                break;
            case PaintTool.Clone:
                Ui.TextDisabled(cloneSource == null ? "Ctrl+click the source first." : "Source set (green cross). Ctrl+click again to change it.");
                break;
            case PaintTool.Smudge:
                Ui.TextDisabled("Drags the colours under the brush along the stroke.");
                break;
        }

        // what is painted
        if (paintTool is PaintTool.Brush or PaintTool.Fill)
        {
            if (Ui.Header("Colour and channels", Open)) PaintChannels();
        }
        if (paintTool is not PaintTool.Fill)
        {
            if (Ui.Header("Brush", Open)) BrushShape(true);
            if (Ui.Header("Stroke")) StrokeOptions();
        }
        ImGui.Spacing();
        bool mm = brush.MaskMode;
        if (ImGui.Checkbox("Paint the mask of the selected layer", ref mm)) brush.MaskMode = mm;
        Theme.Tip("White shows the layer, the eraser hides it. Works on fills, paint layers and folders.");
        if (paintTool == PaintTool.Eraser && !brush.MaskMode) Ui.TextDisabled("Erases the selected paint layer.");
    }

    void PaintChannels()
    {
        float sw = (ImGui.GetContentRegionAvail().X - 5 * 11) / 12;
        for (int i = 0; i < Swatches.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, 5);
            ImGui.PushID(i);
            var c = Swatches[i].Colour;
            bool sel = Vector3.Distance(c, brush.ColorValue) < 0.01f;
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(c, 1)); ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(c * 1.2f, 1));
            if (ImGui.Button(sel ? "o" : "", new Vector2(sw, sw))) { brush.ColorValue = c; brush.Color = true; }
            ImGui.PopStyleColor(2);
            Theme.Tip(Swatches[i].Label);
            ImGui.PopID();
        }
        ImGui.Checkbox("##pc", ref brush.Color); ImGui.SameLine();
        ImGui.ColorEdit3("Colour", ref brush.ColorValue, ImGuiColorEditFlags.NoInputs);
        ImGui.SameLine(); ImGui.SetNextItemWidth(-1);
        int finish = brush.Metal && brush.MetalValue > 0.5f ? 2 : brush.RoughValue < 0.35f ? 1 : 0;
        if (ImGui.Combo("##finish", ref finish, new[] { "Matte", "Glossy", "Metal" }, 3))
        {
            brush.Roughness = true; brush.Metal = true;
            (brush.RoughValue, brush.MetalValue) = finish switch { 1 => (0.2f, 0f), 2 => (0.3f, 1f), _ => (0.8f, 0f) };
        }
        void Ch(string label, ref bool on, ref float v, string tip)
        {
            ImGui.Checkbox("##" + label, ref on); ImGui.SameLine();
            if (!on) ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat(label, ref v, 0, 1);
            if (!on) ImGui.EndDisabled();
            Theme.Tip(tip);
        }
        Ch("Relief", ref brush.Height, ref brush.HeightValue, "Height: becomes the normal map (0.5 = flat).");
        Ch("Roughness", ref brush.Roughness, ref brush.RoughValue, "0 glossy, 1 matte (GTA specular map).");
        Ch("Metal", ref brush.Metal, ref brush.MetalValue, "Metal shines more in GTA.");
        Ch("Emissive", ref brush.Emissive, ref brush.EmissiveValue, "Glowing parts: screens, signs. Export tab turns them into the GTA emissive shader.");
        Ch("Opacity", ref brush.Alpha, ref brush.AlphaValue, "0 = transparent (holes, glass). The diffuse becomes DXT5 and the material an alpha shader.");
    }

    void BrushShape(bool withTip)
    {
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Size", ref brush.Radius, 0.002f, 0.002f, 20f, "%.2f m");
        Theme.Tip("Also [ and ] while painting.");
        ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Strength", ref brush.Strength, 0.02f, 1f);
        ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Hardness", ref brush.Hardness, 0f, 1f);
        if (!withTip) return;
        if (paintTool != PaintTool.Decal)
        {
            int ti = Math.Max(0, Array.IndexOf(BrushTips.Names, brush.TipName ?? "Round"));
            ImGui.SetNextItemWidth(-90);
            if (ImGui.Combo("Tip", ref ti, BrushTips.Names, BrushTips.Names.Length)) { brush.TipName = BrushTips.Names[ti]; brush.Tip = BrushTips.Get(brush.TipName); }
            Theme.Tip("Shape of the brush (also in the shelf, Alphas tab).");
        }
        if (brush.Tip != null || brush.Stamp != null)
        {
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Size jitter", ref brush.SizeJitter, 0, 1);
            ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Angle jitter", ref brush.AngleJitter, 0, 180, "%.0f°");
            if (paintTool != PaintTool.Decal) { ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Angle", ref brush.StampAngle, -180, 180, "%.0f°"); }
        }
    }

    void StrokeOptions()
    {
        ImGui.SetNextItemWidth(-90); ImGui.SliderFloat("Spacing", ref brush.Spacing, 0.02f, 1f);
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Lazy mouse", ref brush.Lazy, 0.002f, 0, 2, brush.Lazy <= 0 ? "off" : "%.2f m");
        Theme.Tip("The brush trails behind the cursor: clean lines and curves.");
        Ui.TextDisabled("Symmetry"); ImGui.SameLine();
        ImGui.Checkbox("X##sx", ref brush.SymX); ImGui.SameLine(); ImGui.Checkbox("Y##sy", ref brush.SymY); ImGui.SameLine(); ImGui.Checkbox("Z##sz", ref brush.SymZ);
        Theme.Tip("Mirrors the strokes across the object's axes (through its pivot).");
        ImGui.Checkbox("Only surfaces facing the brush", ref brush.BackfaceCull);
        Ui.TextDisabled("Shift+click: straight line from the last print.");
    }

    /// <summary>Terrain layer the vertex colour brush paints (-1: it paints vertex colour 0 as usual).</summary>
    int terrainPaintLayer = -1;
    Core.Gta.GtaMaterial TerrainMaterialOf(SceneObject o)
    {
        if (o?.Mesh == null) return null;
        foreach (var id in o.Mesh.Faces.Select(f => f.MatId).Distinct()) if (Scene.Material(id) is { IsTerrain: true } m) return m;
        return null;
    }

    void TerrainLayerOptions(SceneObject o, Core.Gta.GtaMaterial tm)
    {
        Ui.TextDisabled("Terrain layers (4 textures blended by vertex colour 1)");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        for (int i = 0; i < 4; i++)
        {
            if (i % 2 == 1) ImGui.SameLine(0, 4);
            var tex = tm.Layer(i);
            string label = $"{i + 1}  " + (tex != null ? Path.GetFileNameWithoutExtension(tex) : "(empty)");
            if (Theme.Toggle(label + "##tl" + i, terrainPaintLayer == i, new Vector2(w2, 26))) terrainPaintLayer = terrainPaintLayer == i ? -1 : i;
            Theme.Tip("Paint this layer with the brush (click again to paint vertex colours instead).");
        }
        if (terrainPaintLayer >= 0 && ImGui.Button($"Fill the whole object with layer {terrainPaintLayer + 1}", new Vector2(-1, 0)))
            Do("Terrain layer fill", () => { VertexPaint.FillLayer(o.Mesh, Enumerable.Range(0, o.Mesh.Faces.Count), terrainPaintLayer); o.Touch(); });
        Ui.TextDisabled(terrainPaintLayer >= 0 ? "The view shows the blend live. Ctrl+Z undoes a stroke." : "Pick a layer to paint it.");
        ImGui.Separator();
    }

    void VertexColourOptions(SceneObject o)
    {
        if (TerrainMaterialOf(o) is { } tm) TerrainLayerOptions(o, tm);
        else terrainPaintLayer = -1;
        Ui.TextDisabled("GTA vertex colours (the view shows them)");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        for (int i = 0; i < VertexPaint.Presets.Length; i++)
        {
            var (name, c, tip) = VertexPaint.Presets[i];
            if (i % 2 == 1) ImGui.SameLine(0, 4);
            bool sel = vertexColour == c;
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(c.X * 0.7f, c.Y * 0.7f, c.Z * 0.7f, 1));
            ImGui.PushStyleColor(ImGuiCol.Text, c.X + c.Y + c.Z > 1.5f ? new Vector4(0, 0, 0, 1) : new Vector4(1));
            if (ImGui.Button((sel ? "> " : "") + name, new Vector2(w2, 26))) vertexColour = c;
            ImGui.PopStyleColor(2);
            Theme.Tip(tip);
        }
        ImGui.ColorEdit4("Custom", ref vertexColour, ImGuiColorEditFlags.NoInputs);
        if (ImGui.Button("Fill the whole object", new Vector2(-1, 0)))
        {
            Do("Vertex colour fill", () => { VertexPaint.Fill(o.Mesh, Enumerable.Range(0, o.Mesh.Faces.Count), vertexColour); o.Touch(); });
        }
        Ui.TextDisabled("Paint with the left button. Ctrl+Z undoes a stroke.");
    }

    // ------------------------------------------------------------------------------ Layers

    void PaintLayersTab(SceneObject o, TextureSet ts)
    {
        float w5 = (ImGui.GetContentRegionAvail().X - 8) / 5;
        var cur = ts.Current;
        int folderHere = cur == null ? -1 : cur.IsFolder ? cur.Id : cur.Folder;
        if (ImGui.Button("+ Paint", new Vector2(w5, 0))) AddLayer(ts, new PaintLayer { Name = "Paint " + ts.Layers.Count, Kind = LayerKind.Paint, Folder = folderHere });
        Theme.Tip("New layer to paint on");
        ImGui.SameLine(0, 2);
        if (ImGui.Button("+ Fill", new Vector2(w5, 0))) AddLayer(ts, new PaintLayer { Name = "Fill " + ts.Layers.Count, Kind = LayerKind.Fill, Color = brush.ColorValue, Folder = folderHere, Mask = MaskGen.Painted });
        Theme.Tip("Material layer (colour, roughness...) shown where you paint its mask, or by a smart mask");
        ImGui.SameLine(0, 2);
        if (ImGui.Button("+ Folder", new Vector2(w5, 0))) AddLayer(ts, new PaintLayer { Name = "Folder " + ts.Layers.Count(l => l.IsFolder), Kind = LayerKind.Folder });
        Theme.Tip("Group layers: one switch, one opacity and one mask for all of them");
        ImGui.SameLine(0, 2);
        if (ImGui.Button("Copy", new Vector2(w5, 0)) && cur != null) DuplicateLayer(ts, ts.SelectedLayer);
        Theme.Tip("Duplicate the selected layer");
        ImGui.SameLine(0, 2);
        bool canDel = ts.Layers.Count(l => !l.IsFolder) > 1 || cur?.IsFolder == true;
        if (!canDel) ImGui.BeginDisabled();
        if (ImGui.Button("Delete", new Vector2(w5, 0))) DeleteLayer(ts, ts.SelectedLayer);
        if (!canDel) ImGui.EndDisabled();

        LayerList(ts);
        cur = ts.Current; if (cur == null) return;
        ImGui.Spacing();
        if (Ui.Header(cur.IsFolder ? "Folder" : "Layer", Open)) LayerProperties(ts, cur);
        if (Ui.Header("Mask", Open)) LayerMask(ts, cur);
        if (!cur.IsFolder && Ui.Header("Filters")) LayerFilters(ts, cur);
        if (Ui.Header("My smart materials"))
        {
            Ui.TextDisabled("Save the fill layers of this set to reuse them on other props (shelf, Smart tab).");
            ImGui.SetNextItemWidth(-80); ImGui.InputTextWithHint("##smname", "name", ref smartSaveName, 60);
            ImGui.SameLine();
            if (ImGui.Button("Save", new Vector2(-1, 0)) && smartSaveName.Trim().Length > 0)
            {
                var path = SmartLibrary.Save(smartSaveName.Trim(), ts.Layers);
                SetStatus($"Smart material '{smartSaveName.Trim()}' saved ({Path.GetFileName(path)}).");
                smartSaveName = "";
            }
        }
    }

    void LayerList(TextureSet ts)
    {
        int nRows = Math.Clamp(ts.Layers.Count, 3, 10);
        ImGui.BeginChild("##layers", new Vector2(0, nRows * 24 + 8), ImGuiChildFlags.Borders);
        // top of the stack first; the layers of a folder are listed under it, indented (unless folded)
        var rows = new List<int>();
        for (int i = ts.Layers.Count - 1; i >= 0; i--)
        {
            var li = ts.Layers[i];
            if (!li.IsFolder && ts.ById(li.Folder)?.IsFolder == true) continue;
            rows.Add(i);
            if (li.IsFolder && !li.Collapsed)
                for (int j = ts.Layers.Count - 1; j >= 0; j--) if (ts.Layers[j].Folder == li.Id && !ts.Layers[j].IsFolder) rows.Add(j);
        }
        foreach (var i in rows)
        {
            var l = ts.Layers[i];
            var folder = ts.ById(l.Folder);
            if (folder != null && !folder.IsFolder) folder = null;
            ImGui.PushID(l.Id);
            bool vis = l.Visible;
            if (ImGui.Checkbox("##vis", ref vis)) { l.Visible = vis; ts.Refresh(); }
            Theme.Tip("Show / hide");
            ImGui.SameLine();
            if (folder != null) { ImGui.Dummy(new Vector2(12, 0)); ImGui.SameLine(); }
            var c = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            if (l.IsFolder)
            {
                if (ImGui.ArrowButton("##fold", l.Collapsed ? ImGuiDir.Right : ImGuiDir.Down)) l.Collapsed = !l.Collapsed;
                ImGui.SameLine();
            }
            else
            {
                var col = l.Kind == LayerKind.Fill ? (l.HasTextures ? new Vector3(0.6f, 0.55f, 0.45f) : l.Color) : new Vector3(0.85f);
                dl.AddRectFilled(c + new Vector2(0, 3), c + new Vector2(14, 17), ImGui.ColorConvertFloat4ToU32(new Vector4(col, 1)), 2);
                if (l.Kind == LayerKind.Paint) dl.AddLine(c + new Vector2(3, 14), c + new Vector2(11, 6), 0xFF303030, 2);
                ImGui.Dummy(new Vector2(16, 0)); ImGui.SameLine();
            }
            string tag = (l.Mask != MaskGen.None || l.MaskPaint != null ? "  [mask]" : "") + (l.Filters.Count > 0 ? "  [fx]" : "");
            if (layerRename == l.Id.ToString())
            {
                ImGui.SetNextItemWidth(-1); ImGui.SetKeyboardFocusHere();
                if (ImGui.InputText("##ren", ref layerRenameBuf, 63, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll)) { l.Name = layerRenameBuf; layerRename = null; }
                if (!ImGui.IsItemActive() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) layerRename = null;
            }
            else
            {
                if (ImGui.Selectable(l.Name + tag, ts.SelectedLayer == i, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    ts.SelectedLayer = i;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { layerRename = l.Id.ToString(); layerRenameBuf = l.Name; }
                    if (ts.View == PaintView.LayerMask) ts.SetView(PaintView.LayerMask);
                }
                LayerDragDrop(ts, i);
                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("Rename")) { layerRename = l.Id.ToString(); layerRenameBuf = l.Name; }
                    if (ImGui.MenuItem("Move up", null, false, i < ts.Layers.Count - 1)) MoveLayer(ts, i, i + 1);
                    if (ImGui.MenuItem("Move down", null, false, i > 0)) MoveLayer(ts, i, i - 1);
                    if (ImGui.MenuItem("Duplicate")) DuplicateLayer(ts, i);
                    if (!l.IsFolder && l.Folder >= 0 && ImGui.MenuItem("Take out of the folder")) { l.Folder = -1; ts.Refresh(); }
                    if (!l.IsFolder && ImGui.BeginMenu("Put in folder", ts.Layers.Any(x => x.IsFolder)))
                    {
                        foreach (var f in ts.Layers.Where(x => x.IsFolder)) if (ImGui.MenuItem(f.Name)) { l.Folder = f.Id; ts.Refresh(); }
                        ImGui.EndMenu();
                    }
                    if (ImGui.MenuItem("Delete")) DeleteLayer(ts, i);
                    ImGui.EndPopup();
                }
            }
            ImGui.PopID();
        }
        ImGui.EndChild();
        Ui.TextDisabled("drag to reorder, drop on a folder to put it inside, double-click to rename");
    }

    void MoveLayer(TextureSet ts, int from, int to)
    {
        (ts.Layers[from], ts.Layers[to]) = (ts.Layers[to], ts.Layers[from]);
        ts.SelectedLayer = to; ts.Refresh(); paintUndo.Clear();
    }

    void DuplicateLayer(TextureSet ts, int i)
    {
        var c = ts.Layers[i].CloneLayer(); c.Id = ts.NextId++; c.Name += " copy";
        ts.Layers.Insert(i + 1, c); ts.SelectedLayer = i + 1;
        if (ts.Layers[i].IsFolder) // a folder copies its layers too
        {
            var kids = ts.Layers.Where(x => x.Folder == ts.Layers[i].Id).ToList();
            int at = ts.Layers.IndexOf(c);
            foreach (var k in kids) { var kc = k.CloneLayer(); kc.Id = ts.NextId++; kc.Folder = c.Id; ts.Layers.Insert(at++, kc); }
        }
        ts.Refresh(); paintUndo.Clear();
    }

    void DeleteLayer(TextureSet ts, int i)
    {
        var l = ts.Layers[i];
        if (l.IsFolder) foreach (var k in ts.Layers.Where(x => x.Folder == l.Id)) k.Folder = -1; // the layers stay, out of the folder
        ts.Layers.RemoveAt(i);
        ts.SelectedLayer = Math.Clamp(i - 1, 0, ts.Layers.Count - 1);
        ts.Refresh(); paintUndo.Clear();
    }

    void AddLayer(TextureSet ts, PaintLayer l)
    {
        l.Id = ts.NextId++;
        int at = Math.Clamp(ts.SelectedLayer + 1, 0, ts.Layers.Count);
        ts.Layers.Insert(at, l);
        ts.SelectedLayer = ts.Layers.IndexOf(l);
        ts.Refresh();
    }

    void LayerProperties(TextureSet ts, PaintLayer l)
    {
        bool c = false;
        ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Opacity", ref l.Opacity, 0, 1);
        if (l.IsFolder) { Ui.TextDisabled("Opacity, visibility and mask apply to every layer inside."); if (c) ts.Refresh(); return; }
        int bl = (int)l.Blend; ImGui.SetNextItemWidth(-90); if (ImGui.Combo("Blend", ref bl, BlendNames, BlendNames.Length)) { l.Blend = (PaintBlend)bl; c = true; }
        c |= ImGui.Checkbox("Colour##lc", ref l.UseColor); ImGui.SameLine(); c |= ImGui.Checkbox("Relief##lh", ref l.UseHeight); ImGui.SameLine();
        c |= ImGui.Checkbox("Rough##lr", ref l.UseRough); ImGui.SameLine(); c |= ImGui.Checkbox("Metal##lm", ref l.UseMetal);
        c |= ImGui.Checkbox("Emissive##le", ref l.UseEmissive); ImGui.SameLine(); c |= ImGui.Checkbox("Opacity##la", ref l.UseAlpha);
        if (l.Kind == LayerKind.Fill)
        {
            if (!l.HasTextures) c |= ImGui.ColorEdit3("Colour", ref l.Color, ImGuiColorEditFlags.NoInputs);
            else
            {
                Ui.TextDisabled("Texture: " + Path.GetFileNameWithoutExtension(l.TexDiffuse ?? l.TexHeight ?? ""));
                ImGui.SetNextItemWidth(-90); if (ImGui.DragFloat("Tile size", ref l.TileMetres, 0.01f, 0.05f, 50, "%.2f m")) { l.TexturesChanged(); c = true; }
                c |= ImGui.ColorEdit3("Tint", ref l.Color, ImGuiColorEditFlags.NoInputs);
            }
            ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Roughness", ref l.Rough, 0, 1);
            ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Metal", ref l.Metal, 0, 1);
            if (l.UseHeight) { ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Relief", ref l.Height, 0, 1); }
            if (l.UseEmissive) { ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Emissive", ref l.Emissive, 0, 1); }
            if (l.UseAlpha) { ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Opacity value", ref l.Alpha, 0, 1); }
            ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Variation", ref l.ColorNoise, 0, 1);
            ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Relief noise", ref l.HeightNoise, 0, 1);
        }
        else if (ImGui.SmallButton("Clear this layer")) { l.Cover = null; l.PColor = null; l.PHeight = null; l.PRough = null; l.PMetal = null; l.PEmis = null; l.PAlpha = null; c = true; }
        if (c) ts.Refresh();
    }

    void LayerMask(TextureSet ts, PaintLayer l)
    {
        bool mc = false;
        int where = Array.FindIndex(WhereChoices, w => w.Mask == l.Mask && w.Invert == l.MaskInvert);
        if (where < 0) where = Array.FindIndex(WhereChoices, w => w.Mask == l.Mask);
        if (where < 0) where = 0;
        ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("Where", ref where, WhereChoices.Select(w => w.Label).ToArray(), WhereChoices.Length)) { l.Mask = WhereChoices[where].Mask; l.MaskInvert = WhereChoices[where].Invert; mc = true; }
        if (l.Mask is not (MaskGen.None or MaskGen.Painted))
        {
            float amount = 1 - l.MaskThreshold;
            ImGui.SetNextItemWidth(-90); if (ImGui.SliderFloat("Amount", ref amount, 0, 1)) { l.MaskThreshold = 1 - amount; mc = true; }
            ImGui.SetNextItemWidth(-90); mc |= ImGui.SliderFloat("Sharpness", ref l.MaskContrast, 0.5f, 20);
            ImGui.SetNextItemWidth(-90); mc |= ImGui.SliderFloat("Breakup", ref l.MaskBreakup, 0, 1);
            if (l.Mask is MaskGen.Noise or MaskGen.Streaks or MaskGen.Tiles || l.MaskBreakup > 0) { ImGui.SetNextItemWidth(-90); mc |= ImGui.SliderFloat(l.Mask == MaskGen.Tiles ? "Tiles / m" : "Scale", ref l.MaskNoiseScale, 0.5f, 60); }
            mc |= ImGui.Checkbox("Invert", ref l.MaskInvert);
            ImGui.SameLine(); if (ImGui.SmallButton("New seed")) { l.Seed = Environment.TickCount & 0xFFFF; mc = true; }
        }
        // smart masks
        Ui.TextDisabled("Smart masks");
        float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
        for (int i = 0; i < SmartMasks.All.Length; i++)
        {
            if (i % 2 == 1) ImGui.SameLine(0, 4);
            if (ImGui.Button(SmartMasks.All[i].Name, new Vector2(w2, 0))) { SmartMasks.All[i].Apply(l); mc = true; }
            Theme.Tip(SmartMasks.All[i].Tip);
        }
        // painted mask
        ImGui.Spacing();
        bool mm = brush.MaskMode;
        if (Theme.Toggle(mm ? "Painting the mask (click to stop)" : "Paint on this mask", mm, new Vector2(-1, 0))) { brush.MaskMode = !mm; if (brush.MaskMode && paintTool is not (PaintTool.Brush or PaintTool.Eraser or PaintTool.Fill)) SetPaintTool(PaintTool.Brush); }
        Theme.Tip("Brush shows the layer, eraser hides it, on top of the mask above.");
        if (l.MaskPaint != null || l.PaintedMask != null)
            if (ImGui.SmallButton("Clear the painted mask")) { l.MaskPaint = null; l.PaintedMask = null; mc = true; }
        ImGui.SameLine();
        if (ImGui.SmallButton(ts.View == PaintView.LayerMask ? "Show the result" : "Show the mask")) ts.SetView(ts.View == PaintView.LayerMask ? PaintView.Final : PaintView.LayerMask);
        if (mc) { l.MaskChanged(); ts.Refresh(); }
    }

    void LayerFilters(TextureSet ts, PaintLayer l)
    {
        bool c = false;
        for (int i = 0; i < l.Filters.Count; i++)
        {
            var f = l.Filters[i];
            ImGui.PushID(i);
            c |= ImGui.Checkbox("##fe", ref f.Enabled); ImGui.SameLine();
            Ui.Text(f.Label); ImGui.SameLine(ImGui.GetContentRegionAvail().X - 10);
            if (ImGui.SmallButton("x")) { l.Filters.RemoveAt(i); c = true; ImGui.PopID(); break; }
            switch (f.Kind)
            {
                case FilterKind.Blur: ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Radius", ref f.Radius, 1, 24, "%.0f px"); break;
                case FilterKind.Sharpen: ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Radius", ref f.Radius, 1, 12, "%.0f px"); ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Amount", ref f.Amount, 0, 4); break;
                case FilterKind.Levels:
                    ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Black", ref f.Black, 0, 1);
                    ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("White", ref f.White, 0, 1);
                    ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Gamma", ref f.Gamma, 0.2f, 4);
                    break;
                case FilterKind.HueSaturation:
                    ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Hue", ref f.Hue, -180, 180, "%.0f°");
                    ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Saturation", ref f.Saturation, 0, 2);
                    ImGui.SetNextItemWidth(-90); c |= ImGui.SliderFloat("Brightness", ref f.Brightness, -1, 1);
                    break;
            }
            ImGui.PopID();
        }
        Ui.TextDisabled("Add a filter on the mask:");
        float w3 = (ImGui.GetContentRegionAvail().X - 6) / 4;
        void Add(string label, FilterKind k, bool onMask) { if (ImGui.Button(label, new Vector2(w3, 0))) { l.Filters.Add(new PaintFilter { Kind = k, OnMask = onMask }); c = true; } }
        Add("Blur", FilterKind.Blur, true); ImGui.SameLine(0, 2); Add("Sharpen", FilterKind.Sharpen, true); ImGui.SameLine(0, 2); Add("Levels", FilterKind.Levels, true); ImGui.SameLine(0, 2); Add("Invert", FilterKind.Invert, true);
        Ui.TextDisabled("on the colour:");
        Add("Levels##c", FilterKind.Levels, false); ImGui.SameLine(0, 2); Add("Hue / sat.", FilterKind.HueSaturation, false); ImGui.SameLine(0, 2); Add("Invert##c", FilterKind.Invert, false);
        if (c) { l.MaskChanged(); ts.Refresh(); }
    }

    // ------------------------------------------------------------------------------ Bake

    void PaintBakeTab(SceneObject o, TextureSet ts)
    {
        Ui.TextWrapped("High to low: the details of a high-poly model (sculpt, Tripo model) go into the normal map, AO, curvature and colour of this model.");
        ImGui.Spacing();
        var candidates = Scene.Objects.Where(x => x != o && x.Mesh.Faces.Count > 0 && x.GtaType is GtaObjectType.Drawable).ToList();
        var high = Scene.Find(bakeHighId);
        ImGui.SetNextItemWidth(-90);
        if (ImGui.BeginCombo("High-poly", high?.Name ?? "pick a model"))
        {
            foreach (var c in candidates.OrderByDescending(c => c.TriangleCount))
                if (ImGui.Selectable($"{c.Name}  ({c.TriangleCount:N0} tris)", c.Id == bakeHighId)) bakeHighId = c.Id;
            ImGui.EndCombo();
        }
        if (high == null && Selected.Count == 2) { var other = SelObjects().FirstOrDefault(x => x != o); if (other != null && ImGui.SmallButton("Use the other selected object")) bakeHighId = other.Id; }
        ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Distance", ref bakeSettings.MaxDistance, 0.002f, 0.001f, 5, "%.3f m");
        Theme.Tip("How far the high-poly may be in front of or behind this surface (the cage). Raise it if parts stay flat.");
        ImGui.Checkbox("Normal", ref bakeSettings.Normal); ImGui.SameLine(); ImGui.Checkbox("AO", ref bakeSettings.Ao); ImGui.SameLine();
        ImGui.Checkbox("Curvature", ref bakeSettings.Curvature); ImGui.SameLine(); ImGui.Checkbox("Colour", ref bakeSettings.Colour);
        if (bakeSettings.Ao) { ImGui.SetNextItemWidth(-90); ImGui.SliderInt("AO rays", ref bakeSettings.AoRays, 8, 64); }
        if (bakeSettings.Colour) ImGui.Checkbox("Colour as a new layer", ref bakeColourLayer);
        ImGui.Checkbox("Hide the high-poly after the bake", ref bakeHideHigh);
        bool busy = bakeTask is { IsCompleted: false };
        if (busy || high == null) ImGui.BeginDisabled();
        if (Theme.Toggle(busy ? $"BAKING  {bakeProgress * 100:0}%" : "BAKE", true, new Vector2(-1, 32))) StartBake(o, ts);
        if (busy || high == null) ImGui.EndDisabled();
        if (bakeInfo != null) Ui.TextWrapped(bakeInfo);
        if (ts.HasBakedMaps)
        {
            Ui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1), "This set uses baked maps.");
            if (ImGui.SmallButton("Remove the baked normal")) { ts.BakedNormal = null; ts.Refresh(); }
        }
        ImGui.Separator();
        Ui.TextDisabled("Mesh maps of this model (edges, dirt) without a high-poly");
        ImGui.SetNextItemWidth(-90); ImGui.SliderInt("AO quality", ref aoRays, 8, 128);
        if (ImGui.Button("Recompute edges / dirt maps", new Vector2(-1, 0))) { ts.Rebuild(o.Mesh, aoRays); ts.HasBakedMaps = false; ts.BakedNormal = null; paintMeshVersion[o.Id] = o.Version; UploadPaint(o, ts, true); }
        ImGui.SetNextItemWidth(-90); if (ImGui.SliderFloat("Normal str.", ref ts.NormalStrength, 0, 16)) ts.Refresh();
        Theme.Tip("Strength of the painted relief in the normal map.");
    }

    // ------------------------------------------------------------------------------ Export

    void PaintExportTab(SceneObject o, TextureSet ts)
    {
        // preview
        Ui.TextDisabled("Preview");
        int v = (int)ts.View; ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("Show", ref v, ViewNames, ViewNames.Length)) ts.SetView((PaintView)v);
        float w3 = (ImGui.GetContentRegionAvail().X - 4) / 3;
        if (Theme.Toggle("Day", !InteriorLight && Hour > 7 && Hour < 19, new Vector2(w3, 0))) { InteriorLight = false; Hour = 13; }
        ImGui.SameLine(0, 2);
        if (Theme.Toggle("Night", !InteriorLight && (Hour <= 7 || Hour >= 19), new Vector2(w3, 0))) { InteriorLight = false; Hour = 23; }
        ImGui.SameLine(0, 2);
        if (Theme.Toggle("Interior", InteriorLight, new Vector2(w3, 0))) InteriorLight = true;
        Theme.Tip("GTA lighting in the viewport (time of day / interior)");
        // the four maps side by side
        float tw = (ImGui.GetContentRegionAvail().X - 9) / 4;
        string[] names = { "diffuse", "normal", "spec", "emissive" }; string[] suf = { "d", "n", "s", "e" };
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) ImGui.SameLine(0, 3);
            ImGui.BeginGroup();
            var srv = renderer.Textures.Get(PaintTexPath(o, ts, suf[i]));
            if (srv != null) ImGui.Image(imgui.IdOf(srv), new Vector2(tw, tw)); else ImGui.Dummy(new Vector2(tw, tw));
            if (ImGui.IsItemHovered() && srv != null) { ImGui.BeginTooltip(); ImGui.Image(imgui.IdOf(srv), new Vector2(360, 360)); ImGui.EndTooltip(); }
            Ui.TextDisabled(names[i]);
            ImGui.EndGroup();
        }

        ImGui.Separator();
        Ui.TextDisabled("Textures");
        int ri = Math.Max(0, Array.IndexOf(PaintSizes, ts.Size));
        ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("Working res.", ref ri, PaintSizes.Select(s => s + " px").ToArray(), PaintSizes.Length) && PaintSizes[ri] != ts.Size)
        {
            ts.Resize(o.Mesh, PaintSizes[ri], aoRays); paintUndo.Clear(); UploadPaint(o, ts, true);
            SetStatus($"Painting at {ts.Size} px (the export size stays {ts.ExportSize}).");
        }
        Theme.Tip("Paint finer at 2048 / 4096: the export is reduced cleanly to the export size.");
        int[] exportSizes = { 512, 1024, 2048 };
        int ei = Math.Max(0, Array.IndexOf(exportSizes, ts.ExportSize));
        ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("Export size", ref ei, exportSizes.Select(s => s + (s == 1024 ? " (GTA)" : "") + " px").ToArray(), exportSizes.Length)) ts.ExportSize = exportSizes[ei];
        int fi = (int)ts.ExportFormat; ImGui.SetNextItemWidth(-90);
        if (ImGui.Combo("Files", ref fi, FormatNames, FormatNames.Length)) ts.ExportFormat = (PaintExportFormat)fi;
        Theme.Tip("The DDS files are always written (the scene and the GTA export use them). Sollumz: PNG in one folder per model.");
        if (ts.HasAlpha)
        {
            Ui.TextColored(new Vector4(0.6f, 0.8f, 1, 1), "Transparency painted: DXT5 diffuse.");
            string[] sh = { "normal_spec_decal", "cutout_hard" };
            int si = Math.Max(0, Array.IndexOf(sh, ts.AlphaShader));
            ImGui.SetNextItemWidth(-90);
            if (ImGui.Combo("Alpha shader", ref si, new[] { "Blended (normal_spec_decal)", "Cut out (cutout_hard)" }, 2)) ts.AlphaShader = sh[si];
        }
        if (ts.HasEmissive)
        {
            Ui.TextColored(new Vector4(1, 0.85f, 0.4f, 1), "Emissive painted: _e texture written.");
            if (ImGui.Button("Emissive faces -> GTA emissive shader + white vertex colours", new Vector2(-1, 0))) ApplyEmissiveFaces(o, ts);
        }
        ImGui.Spacing();
        if (Theme.Toggle("SAVE GTA TEXTURES", true, new Vector2(-1, 32))) BakePaint(o);
        Ui.TextDisabled("Also done when you save the scene or export to GTA.");
        Ui.TextDisabled(PaintDir);
        if (ImGui.SmallButton("Open the folder") && Directory.Exists(PaintDir)) try { System.Diagnostics.Process.Start("explorer.exe", PaintDir); } catch { }
        ImGui.Separator();
        if (ImGui.Button("Stop painting this object (keeps the textures)", new Vector2(-1, 0)))
        {
            BakePaint(o, true);
            foreach (var s in o.Paint.Sets) foreach (var x in new[] { "d", "n", "s", "e" }) { renderer.Textures.RemoveDynamic(PaintTexPath(o, s, x)); renderer.Textures.Reload(PaintTexPath(o, s, x)); }
            o.Paint = null; o.PaintFile = null; paintMode = false;
        }
    }
}
