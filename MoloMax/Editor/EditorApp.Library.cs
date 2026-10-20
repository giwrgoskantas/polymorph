using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Library;
using MoloMax.Core.Mesh;
using MoloMax.Core.Paint;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

public enum LibraryAction { Browse, Fill, Stamp, Assign, Decal, RoomSlot, CubeProj, TerrainLayer }

/// <summary>
/// Molo Materials: the Molo texture library (every GTA V interior material, normal / spec included) inside MoloMax.
/// Assign a material, use it as a Molo Paint fill, stamp a game decal, or place a real GTA decal on a surface.
/// </summary>
public sealed partial class EditorApp
{
    MaterialLibrary library;
    string libraryError;
    bool libraryOpen;
    LibraryAction libraryAction = LibraryAction.Browse;
    string libSearch = "", libCategory = "All";
    LibMaterial libSelected;
    LibMaterial decalPlace;
    float decalSize = 1f;
    List<LibMaterial> libResults = new();
    Vector2? libWinAt; // films: where the window stands
    string libResultsKey;

    GtaMaterial libLayerMat; int libLayerIndex;
    void OpenLibrary(LibraryAction action)
    {
        libraryOpen = true; libraryAction = action;
        if (action == LibraryAction.Stamp) libCategory = "Decals";
        EnsureLibrary();
    }

    void EnsureLibrary()
    {
        if (library != null) return;
        var folder = Settings.LibraryFolder ?? MaterialLibrary.DefaultFolder;
        try { library = MaterialLibrary.Load(folder); libraryError = null; libResultsKey = null; }
        catch (Exception e) { libraryError = e.Message; }
    }

    void DrawLibraryWindow()
    {
        if (!libraryOpen) return;
        if (libWinAt is Vector2 at) { ImGui.SetNextWindowSize(new Vector2(720, 540), ImGuiCond.Always); ImGui.SetNextWindowPos(at, ImGuiCond.Always); }
        else
        {
            ImGui.SetNextWindowSize(new Vector2(760, 560), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new Vector2(explorerW + 60, menuH + toolbarH + 30), ImGuiCond.FirstUseEver);
        }
        string title = libraryAction switch
        {
            LibraryAction.Fill => "Molo Materials - pick a fill for Molo Paint",
            LibraryAction.Stamp => "Molo Materials - pick a decal stamp",
            LibraryAction.RoomSlot => "Molo Materials - pick the " + RoomSlotNames[libRoomSlot].ToLowerInvariant() + " material",
            LibraryAction.TerrainLayer => $"Molo Materials - pick terrain layer {libLayerIndex + 1}",
            _ => "Molo Materials - GTA V texture library",
        };
        if (!ImGui.Begin(title + "###library", ref libraryOpen, ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }

        // folder
        string folder = Settings.LibraryFolder ?? MaterialLibrary.DefaultFolder;
        ImGui.SetNextItemWidth(-160);
        if (ImGui.InputText("##libfolder", ref folder, 400, ImGuiInputTextFlags.EnterReturnsTrue)) { Settings.LibraryFolder = folder; Settings.Save(); library = null; EnsureLibrary(); }
        ImGui.SameLine();
        if (ImGui.Button("Browse...", new Vector2(70, 0)))
        {
            using var d = new System.Windows.Forms.FolderBrowserDialog { Description = "MoloTextureLibrary folder (with data\\materials.json)" };
            if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) { Settings.LibraryFolder = d.SelectedPath; Settings.Save(); library = null; EnsureLibrary(); }
            Input.ReleaseAllKeys();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reload", new Vector2(-1, 0))) { library = null; EnsureLibrary(); }
        if (library == null)
        {
            Ui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), libraryError ?? "Library not loaded.");
            Ui.TextWrapped("Point to a texture library folder (data\\materials.json, textures\\), or make one from your own game:");
            ImGui.Separator();
            LibraryBuildPanel();
            ImGui.End(); return;
        }

        // filters
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##libsearch", "search: name, shader, interior (v_michael...)", ref libSearch, 100);
        ImGui.SameLine(); ImGui.SetNextItemWidth(170);
        int ci = Math.Max(0, Array.IndexOf(MaterialLibrary.Categories, libCategory));
        if (ImGui.Combo("##libcat", ref ci, MaterialLibrary.Categories, MaterialLibrary.Categories.Length)) libCategory = MaterialLibrary.Categories[ci];
        var key = libCategory + "|" + libSearch;
        if (key != libResultsKey) { libResults = library.Search(libCategory, libSearch).ToList(); libResultsKey = key; }
        ImGui.SameLine(); Ui.TextDisabled($"{libResults.Count:N0} / {library.Materials.Count:N0}");

        // grid + details
        float detailW = 250;
        ImGui.BeginChild("##libgrid", new Vector2(-detailW - 8, 0), ImGuiChildFlags.Borders);
        const float cell = 84;
        int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + 4) / (cell + 4)));
        int rows = (libResults.Count + perRow - 1) / perRow;
        // only the visible rows are drawn (thousands of materials)
        float rowH = cell + 22;
        float scroll = ImGui.GetScrollY(), viewH = ImGui.GetWindowHeight();
        int first = Math.Max(0, (int)(scroll / rowH) - 1), last = Math.Min(rows - 1, (int)((scroll + viewH) / rowH) + 1);
        ImGui.Dummy(new Vector2(0, first * rowH));
        for (int r = first; r <= last; r++)
        {
            for (int c = 0; c < perRow; c++)
            {
                int i = r * perRow + c; if (i >= libResults.Count) break;
                var m = libResults[i];
                if (c > 0) ImGui.SameLine(0, 4);
                ImGui.BeginGroup();
                ImGui.PushID(i);
                var p = ImGui.GetCursorScreenPos();
                bool sel = libSelected == m;
                if (ImGui.InvisibleButton("##t", new Vector2(cell, cell))) libSelected = m;
                bool dbl = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                var dl = ImGui.GetWindowDrawList();
                var srv = renderer.Textures.Thumb(library.PreviewPath(m));
                if (srv != null) dl.AddImage(imgui.IdOf(srv), p, p + new Vector2(cell, cell));
                else dl.AddRectFilled(p, p + new Vector2(cell, cell), 0xFF3A3A3A);
                dl.AddRect(p, p + new Vector2(cell, cell), sel ? 0xFF30B0FF : 0xFF202020, 0, ImDrawFlags.None, sel ? 3 : 1);
                if (m.IsDecal) dl.AddText(p + new Vector2(3, 2), 0xFFFFFFFF, "decal");
                var label = m.Name.Length > 13 ? m.Name[..12] + "…" : m.Name;
                Ui.TextDisabled(label);
                if (ImGui.IsItemHovered() || ImGui.IsItemActive()) ImGui.SetTooltip($"{m.Name}\n{m.Shader}  ({m.Uses} uses)");
                ImGui.PopID();
                ImGui.EndGroup();
                if (dbl) LibraryPrimary(m);
            }
        }
        ImGui.Dummy(new Vector2(0, Math.Max(0, rows - last - 1) * rowH));
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##libdetail", new Vector2(detailW, 0), ImGuiChildFlags.Borders);
        LibraryDetail();
        ImGui.EndChild();
        ImGui.End();
    }

    void LibraryDetail()
    {
        var m = libSelected;
        if (m == null) { Ui.TextWrapped("Pick a material. Double-click does the main action of the window."); return; }
        var srv = renderer.Textures.Thumb(library.PreviewPath(m));
        if (srv != null) ImGui.Image(imgui.IdOf(srv), new Vector2(234, 234));
        Ui.TextWrapped(m.Name);
        Ui.TextDisabled($"{m.Shader}.sps  -  {m.Category}");
        Ui.TextDisabled($"Used {m.Uses}x" + (m.Mlos.Length > 0 ? " in " + string.Join(", ", m.Mlos.Take(4)) + (m.Mlos.Length > 4 ? "…" : "") : ""));
        void Tex(string what, string n) { if (n != null) Ui.TextDisabled($"{what}: {n}"); }
        Tex("Diffuse", m.Diffuse); Tex("Normal", m.Bump); Tex("Spec", m.Spec); Tex("Height", m.Height);
        if (!m.IsDecal) Ui.TextDisabled($"Tile ~{m.TileMetres:0.00} m");
        ImGui.Separator();
        float w = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Assign to selection", new Vector2(w, 0))) LibraryAssign(m);
        Theme.Tip("Selected faces (Edit Mode) or whole selected objects. Shader, textures and parameters as in GTA.");
        bool canPaint = CurSet != null;
        if (!canPaint) ImGui.BeginDisabled();
        if (ImGui.Button("Molo Paint: fill layer", new Vector2(w, 0))) LibraryFill(m);
        if (ImGui.Button("Molo Paint: decal stamp", new Vector2(w, 0))) LibraryStamp(m);
        if (!canPaint) ImGui.EndDisabled();
        if (!canPaint) Ui.TextDisabled("(start Molo Paint on the object first)");
        ImGui.Separator();
        bool placing = decalPlace == m;
        if (Theme.Toggle(placing ? "Placing: click surfaces (Esc)" : "Place GTA decal", placing, new Vector2(w, 0))) { decalPlace = placing ? null : m; if (!placing) { paintMode = false; SetStatus("Click on a surface to place the decal (Esc to stop)."); } }
        Theme.Tip("A small plane stuck on the surface with the decal shader, like Rockstar's decals.");
        ImGui.SetNextItemWidth(-60); ImGui.DragFloat("Size", ref decalSize, 0.01f, 0.05f, 20, "%.2f m");
    }

    void LibraryPrimary(LibMaterial m)
    {
        switch (libraryAction)
        {
            case LibraryAction.Fill: LibraryFill(m); break;
            case LibraryAction.Stamp: LibraryStamp(m); break;
            case LibraryAction.RoomSlot:
                {
                    var g = LibraryMaterial(m);
                    RoomMat[libRoomSlot] = g.Id; RoomMatTile[libRoomSlot] = m.TileMetres;
                    SetStatus($"{RoomSlotNames[libRoomSlot]}: {g.Name} (tile {m.TileMetres:0.00} m). Build again to apply.");
                    libraryOpen = false;
                    break;
                }
            case LibraryAction.TerrainLayer:
                if (libLayerMat != null)
                {
                    Undo.Record("Terrain layer", Scene);
                    libLayerMat.EnsureLayers();
                    libLayerMat.Layers[libLayerIndex] = m.Diffuse; libLayerMat.LayerBumps[libLayerIndex] = m.Bump;
                    Dirty = true;
                    SetStatus($"Terrain layer {libLayerIndex + 1}: {m.Name}");
                }
                libraryOpen = false;
                break;
            case LibraryAction.CubeProj:
                if (cubeProj != null)
                {
                    var g = LibraryMaterial(m);
                    cubeProj.MaterialId = g.Id; cubeProj.SizeU = cubeProj.SizeV = m.TileMetres; cubeProj.Lock = true;
                    ApplyCubeProjection();
                    SetStatus($"{g.Name} projected ({m.TileMetres:0.00} m per tile).");
                }
                libraryOpen = false;
                break;
            default: LibraryAssign(m); break;
        }
    }

    GtaMaterial LibraryMaterial(LibMaterial m)
    {
        var name = Core.Gta.Export.TextureBank.SafeName(m.Name);
        var existing = Scene.Materials.FirstOrDefault(x => x.Name == name && x.Shader == m.Shader);
        if (existing != null) return existing;
        Undo.Record("Library material", Scene);
        var g = library.ToGtaMaterial(m, Scene.NextMaterialId++);
        Scene.Materials.Add(g);
        return g;
    }

    void LibraryAssign(LibMaterial m)
    {
        if (Selected.Count == 0) { SetStatus("Select objects (or faces in Edit Mode) first."); return; }
        var g = LibraryMaterial(m);
        AssignMaterial(g.Id);
        selectedMaterial = g.Id;
        SetStatus($"{g.Name} ({g.Shader}) assigned.");
    }

    void ApplyLibToLayer(PaintLayer l, LibMaterial m)
    {
        l.Name = m.Name; l.Color = Vector3.One; l.UseHeight = true; l.UseRough = m.Spec != null;
        l.TexDiffuse = library.TexturePath(m.Diffuse); l.TexSpec = library.TexturePath(m.Spec); l.TexHeight = library.TexturePath(m.Height);
        l.TileMetres = m.TileMetres; l.Rough = 0.7f;
    }

    void LibraryFill(LibMaterial m)
    {
        var ts = CurSet; if (ts == null) return;
        var l = new PaintLayer { Kind = LayerKind.Fill, Mask = MaskGen.None };
        ApplyLibToLayer(l, m);
        AddLayer(ts, l);
        SetStatus($"Fill layer '{m.Name}' added (tile {m.TileMetres:0.00} m). Set a mask to show it only on edges, cavities, painted areas...");
        if (libraryAction == LibraryAction.Fill) libraryOpen = false;
    }

    void LibraryStamp(LibMaterial m)
    {
        var img = TextureReader.Load(library.TexturePath(m.Diffuse));
        if (img == null) { SetStatus("Could not read " + m.Diffuse); return; }
        paintTool = PaintTool.Decal; brush.Erase = false; brush.Mode = BrushMode.Paint;
        brush.Stamp = img; brush.StampName = m.Name; brush.Color = true;
        if (CurSet is { } ts && ts.Current?.Kind != LayerKind.Paint)
            AddLayer(ts, new PaintLayer { Name = "Decals", Kind = LayerKind.Paint });
        paintMode = true;
        SetStatus($"Stamp '{m.Name}': click on the model (size = brush size, angle in the Brush panel).");
        if (libraryAction == LibraryAction.Stamp) libraryOpen = false;
    }

    /// <summary>Decal placement: left click on any surface adds a decal plane there.</summary>
    bool DecalPlaceInput(Viewport hv, bool uiMouse)
    {
        if (decalPlace == null) return false;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) { decalPlace = null; SetStatus("Decal placement off"); return true; }
        if (hv == null || uiMouse || Input.Alt || !Input.Pressed(System.Windows.Forms.MouseButtons.Left)) return false;
        var hit = Picking.PickObject(Scene, RayAt(hv, Input.Mouse));
        if (!hit.HasValue) return true;
        var h = hit.Value;
        var n = Vector3.Normalize(Vector3.TransformNormal(h.Object.Evaluated().FaceNormal(Math.Min(h.Face, h.Object.Evaluated().Faces.Count - 1)), h.Object.World));
        PlaceDecal(decalPlace, h.Point, n);
        return true;
    }

    void PlaceDecal(LibMaterial m, Vector3 point, Vector3 n)
    {
        var g = LibraryMaterial(m);
        // aspect from the DDS header
        float aspect = 1;
        var path = library.TexturePath(m.Diffuse);
        try { using var fs = File.OpenRead(path); var hdr = new byte[20]; fs.ReadExactly(hdr); int hh = BitConverter.ToInt32(hdr, 12), ww = BitConverter.ToInt32(hdr, 16); if (ww > 0 && hh > 0) aspect = hh / (float)ww; } catch { }
        var mesh = Primitives.Plane(decalSize, decalSize * aspect, 1, 1, g.Id);
        foreach (var f in mesh.Faces)
            for (int i = 0; i < f.Count; i++)
            {
                var v = mesh.Verts[f.V[i]];
                f.UV[i] = new Vector2(v.X / decalSize + 0.5f, 0.5f - v.Y / (decalSize * aspect));
                f.Col[i] = new Vector4(0, 1, 0, 1);
            }
        // plane +Z onto the surface normal, its +Y toward world up (or +Y on floors and ceilings)
        var up = MathF.Abs(n.Z) > 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        var x = Vector3.Normalize(Vector3.Cross(up, n)); var y = Vector3.Cross(n, x);
        var rot = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, n.X, n.Y, n.Z, 0, 0, 0, 0, 1));
        Do("Place decal", () =>
        {
            var o = Scene.Add("decal_" + Core.Gta.Export.TextureBank.SafeName(m.Name), mesh, point + n * 0.004f);
            o.Rotation = rot;
            o.Gta.NoAutoCollision = true;
            o.Touch();
            Selected.Clear(); Selected.Add(o.Id); ActiveId = o.Id;
        });
        SetStatus($"Decal {m.Name} placed ({decalSize:0.00} m). Click again for another, Esc to stop.");
    }
}
