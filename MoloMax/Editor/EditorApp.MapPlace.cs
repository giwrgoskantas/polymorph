using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Map;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Putting a scene on the map at the right place: when the scene already has a .ymap (and its .ytyp) - a resource exported
/// before, a Sollumz / CodeWalker map - the dialog asks for it and the models go where that .ymap places them (matched by
/// model name; an MLO by its interior entity). Else the scene's GTA position (origin + heading) or the middle of the view.
/// </summary>
public sealed partial class EditorApp
{
    sealed class PendingPlace
    {
        public Scene Src; public string Label; public string Folder;
        public List<string> Ymaps = new(); public int Pick = -1; public string Ytyp;
        public int Mode; // 0 the .ymap, 1 the GTA position of the scene, 2 the middle of the view
        public string Info = ""; public Matrix4x4? FromYmap; public int Matched, Roots;
    }
    PendingPlace pendingPlace; bool placeCancel;

    /// <summary>Opens the "where on the map?" dialog for a scene (or puts it at the middle of the view when there is nothing to ask).</summary>
    void AskPlaceOnMap(Scene src, string label, string folder)
    {
        var p = new PendingPlace { Src = src, Label = label, Folder = folder };
        foreach (var dir in new[] { folder, src.Gta?.OutputFolder }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.ymap*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }))
                    if ((f.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ymap.xml", StringComparison.OrdinalIgnoreCase)) && !p.Ymaps.Contains(f) && p.Ymaps.Count < 60) p.Ymaps.Add(f);
            }
            catch { }
        // the .ymap that places the most models of the scene comes first
        int best = -1, bestN = 0;
        for (int i = 0; i < p.Ymaps.Count; i++) { var (m, n, _) = PlacementFromYmap(src, p.Ymaps[i]); if (m != null && n > bestN) { best = i; bestN = n; } }
        bool hasOrigin = src.GtaOrigin != Vector3.Zero || src.Gta?.Heading is > 0.01f or < -0.01f;
        if (best >= 0) SetPlaceYmap(p, p.Ymaps[best]);
        p.Mode = p.FromYmap != null ? 0 : hasOrigin ? 1 : 2;
        pendingPlace = p; // the dialog opens on the next frame (DrawMapPlacementDialog)
    }

    void SetPlaceYmap(PendingPlace p, string path)
    {
        if (!p.Ymaps.Contains(path)) p.Ymaps.Insert(0, path);
        p.Pick = p.Ymaps.IndexOf(path);
        var (m, n, info) = PlacementFromYmap(p.Src, path);
        p.FromYmap = m; p.Matched = n; p.Roots = PlaceRoots(p.Src).Count; p.Info = info;
        var dir = Path.GetDirectoryName(path);
        p.Ytyp = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.ytyp*").FirstOrDefault(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ytyp.xml", StringComparison.OrdinalIgnoreCase)) : null;
        if (m != null) p.Mode = 0;
    }

    /// <summary>The top objects of a scene that become models (a Drawable node, or a mesh with nothing above it).</summary>
    static List<SceneObject> PlaceRoots(Scene src) => src.Objects.Where(o => src.Parent(o) == null && (o.GtaType == GtaObjectType.Model || o.Mesh?.Faces.Count > 0)).ToList();

    /// <summary>Names a model of the scene can have as an archetype of a .ymap.</summary>
    static IEnumerable<string> ArchetypeNamesOf(Scene src, SceneObject o)
    {
        yield return ModelFileName(o).ToLowerInvariant();
        yield return o.Name.ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(o.Name, @"^(.+)_(high|med|low|vlow)_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) yield return m.Groups[1].Value.ToLowerInvariant();
        if (!string.IsNullOrEmpty(o.Gta?.MapArchetype)) yield return o.Gta.MapArchetype.ToLowerInvariant();
    }

    /// <summary>
    /// Where a .ymap puts the scene: the transform that takes the scene's own coordinates to the world. Each model found by
    /// name gives it (its entity = its place); with no model name found, an interior entity (MLO) or the only entity of the
    /// .ymap is the scene origin. Null when the .ymap places nothing of the scene.
    /// </summary>
    static (Matrix4x4? Place, int Matched, string Info) PlacementFromYmap(Scene src, string path)
    {
        try
        {
            var roots = PlaceRoots(src);
            var byName = new Dictionary<string, SceneObject>();
            foreach (var r in roots) foreach (var n in ArchetypeNamesOf(src, r)) byName.TryAdd(n, r);
            var hashes = byName.Keys.ToDictionary(n => MapDocument.Hash(n), n => n);
            var ymap = MapDocument.LoadFile(path, h => hashes.TryGetValue(h, out var n) ? n : null);
            if (ymap == null || ymap.Entities.Count == 0) return (null, 0, "empty or unreadable .ymap");
            Matrix4x4? place = null; int matched = 0;
            var done = new HashSet<SceneObject>();
            foreach (var e in ymap.Entities)
            {
                var name = e.Archetype?.ToLowerInvariant();
                if (name == null || !byName.TryGetValue(name, out var o) || !done.Add(o)) continue;
                matched++;
                if (place != null) continue;
                // entity = model's local place in the scene x placement  =>  placement = inverse(local) x entity
                var ent = Matrix4x4.CreateFromQuaternion(e.Rotation) * Matrix4x4.CreateTranslation(e.Position);
                if (Matrix4x4.Invert(RigidOf(o.World), out var inv)) place = inv * ent;
            }
            if (place == null)
            {
                var mlo = ymap.Entities.Where(e => e.IsMlo).ToList();
                var one = mlo.Count == 1 ? mlo[0] : ymap.Entities.Count == 1 ? ymap.Entities[0] : null;
                if (one != null)
                    return (Matrix4x4.CreateFromQuaternion(one.Rotation) * Matrix4x4.CreateTranslation(one.Position), roots.Count,
                            $"{(one.IsMlo ? "interior" : "entity")} {one.DisplayName} at {one.Position.X:0.0}, {one.Position.Y:0.0}, {one.Position.Z:0.0}: the scene origin goes there");
                return (null, 0, $"{ymap.Entities.Count} entities, none is a model of this scene");
            }
            var t = place.Value.Translation;
            return (place, matched, $"{matched} of {roots.Count} model(s) found in it; the scene goes to {t.X:0.0}, {t.Y:0.0}, {t.Z:0.0}");
        }
        catch (Exception ex) { return (null, 0, "could not read it: " + ex.Message); }
    }

    static Matrix4x4 RigidOf(Matrix4x4 m)
    {
        Matrix4x4.Decompose(m, out _, out var r, out var t);
        return Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    void DrawMapPlacementDialog()
    {
        if (pendingPlace == null) return;
        var p = pendingPlace;
        if (!ImGui.IsPopupOpen("Put on the map###mapplace")) ImGui.OpenPopup("Put on the map###mapplace");
        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        bool open = true;
        if (!ImGui.BeginPopupModal("Put on the map###mapplace", ref open, ImGuiWindowFlags.NoSavedSettings)) { if (!open) pendingPlace = null; return; }
        Ui.TextColored(Theme.Gold, p.Label);
        Ui.TextWrapped("Where does it go on the map?");
        ImGui.Spacing();
        // 1. its .ymap / .ytyp
        ImGui.RadioButton("Where its .ymap places it##pl0", ref p.Mode, 0);
        ImGui.Indent(24);
        string cur = p.Pick >= 0 && p.Pick < p.Ymaps.Count ? p.Ymaps[p.Pick] : null;
        ImGui.SetNextItemWidth(-90);
        if (ImGui.BeginCombo("##plymap", cur != null ? Path.GetFileName(cur) : p.Ymaps.Count == 0 ? "no .ymap found next to the scene" : "pick a .ymap"))
        {
            for (int i = 0; i < p.Ymaps.Count; i++)
                if (ImGui.Selectable(MakeRelative(p.Ymaps[i], p.Folder) + "##y" + i, i == p.Pick)) SetPlaceYmap(p, p.Ymaps[i]);
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse...##plb", new Vector2(-1, 0)))
        {
            using var d = new System.Windows.Forms.OpenFileDialog { Filter = "Map (*.ymap, *.ymap.xml)|*.ymap;*.ymap.xml", Title = "The .ymap that places this scene", InitialDirectory = Directory.Exists(p.Folder) ? p.Folder : "" };
            if (ShowFileDialog(d) == System.Windows.Forms.DialogResult.OK) SetPlaceYmap(p, d.FileName);
        }
        if (cur != null)
        {
            Ui.TextColored(p.FromYmap != null ? new Vector4(0.55f, 0.9f, 0.6f, 1) : new Vector4(1f, 0.6f, 0.45f, 1), p.Info);
            Ui.TextDisabled(p.Ytyp != null ? ".ytyp: " + Path.GetFileName(p.Ytyp) : "no .ytyp next to it (the positions come from the .ymap)");
        }
        ImGui.Unindent(24);
        // 2. the GTA position set in the scene
        bool hasOrigin = p.Src.GtaOrigin != Vector3.Zero;
        ImGui.BeginDisabled(!hasOrigin);
        ImGui.RadioButton($"Its GTA position ({p.Src.GtaOrigin.X:0.0}, {p.Src.GtaOrigin.Y:0.0}, {p.Src.GtaOrigin.Z:0.0})##pl1", ref p.Mode, 1);
        ImGui.EndDisabled();
        if (!hasOrigin) { ImGui.SameLine(); Ui.TextDisabled("(not set in this scene)"); }
        // 3. the middle of the view
        ImGui.RadioButton("On the ground at the middle of the view##pl2", ref p.Mode, 2);
        ImGui.Spacing(); ImGui.Separator();
        bool ok = p.Mode != 0 || p.FromYmap != null;
        ImGui.BeginDisabled(!ok);
        if (ImGui.Button("Put it there", new Vector2(140, 0)))
        {
            Matrix4x4? place = p.Mode switch
            {
                0 => p.FromYmap,
                1 => Matrix4x4.CreateRotationZ((p.Src.Gta?.Heading ?? 0) * MathF.PI / 180f) * Matrix4x4.CreateTranslation(p.Src.GtaOrigin),
                _ => null,
            };
            pendingPlace = null; ImGui.CloseCurrentPopup(); ImGui.EndPopup();
            var res = ImportSceneOnMap(p.Src, p.Label, place);
            if (place != null && res.Count > 0)
            {
                // the view goes there (the map streams around it)
                var mn = res.Where(o => o.Mesh?.Faces.Count > 0).Select(o => o.WorldBounds().Min).DefaultIfEmpty(place.Value.Translation).Aggregate(Vector3.Min);
                var mx = res.Where(o => o.Mesh?.Faces.Count > 0).Select(o => o.WorldBounds().Max).DefaultIfEmpty(place.Value.Translation).Aggregate(Vector3.Max);
                var cam = Viewports[ActiveViewport].Camera;
                cam.Target = (mn + mx) * 0.5f; cam.Distance = MathF.Max(20, Vector3.Distance(mn, mx) * 1.2f);
            }
            return;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)) || placeCancel) { pendingPlace = null; placeCancel = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    static string MakeRelative(string path, string folder)
    {
        try { if (!string.IsNullOrEmpty(folder)) { var r = Path.GetRelativePath(folder, path); if (!r.StartsWith("..")) return r; } } catch { }
        return path;
    }
}
