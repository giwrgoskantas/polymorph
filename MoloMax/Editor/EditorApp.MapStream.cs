using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Map;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// MAP: a FiveM resource's stream folder opened like a CodeWalker project. Its .ymap files load where they stand, its
/// models (.ydr / .yft / .ydd) replace the game's, its interiors open from its .ytyp; saving writes every changed file
/// back in place (new maps go into the folder).
/// </summary>
public sealed partial class EditorApp
{
    StreamFolder stream;

    /// <summary>The name of an archetype: the stream folder's .ytyp / files first, then the game's.</summary>
    string StreamName(uint h) => stream != null && stream.Names.TryGetValue(h, out var n) ? n : game?.FindModel(h)?.Name;

    /// <summary>The resource holding the stream folder (its parent when the folder is called "stream").</summary>
    string StreamResourceDir()
    {
        var f = stream.Folder.TrimEnd('\\', '/');
        return string.Equals(Path.GetFileName(f), "stream", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(f) : f;
    }

    void OpenStreamFolderDialog()
    {
        var dir = PickFolder("Stream folder of a map resource (its .ymap, .ytyp, .ydr...)");
        Input.ReleaseAllKeys();
        if (dir != null) OpenStreamFolder(dir);
    }

    public void OpenStreamFolder(string dir)
    {
        if (!Directory.Exists(dir)) { SetStatus("Folder not found: " + dir); return; }
        if (!MapMode) SetMapMode(true);
        StreamFolder sf;
        try { sf = StreamFolder.Read(dir); }
        catch (Exception e) { SetStatus("Could not read the folder: " + e.Message); return; }
        if (stream != null) CloseStreamFolder();
        stream = sf;
        // models of the folder: forget what was read for these names (the folder's own files are used now)
        foreach (var h in sf.Models.Keys.Concat(sf.DictionaryOf.Keys))
            if (archSlots.Remove(h)) mapSlotOf.Remove("map:" + h.ToString("X8"));
        foreach (var y in sf.Ymaps) OpenYmapFile(y);
        // its interiors open at once (kept open: they are your work)
        foreach (var (id, (doc, e)) in entOf.ToList())
            if (e.IsMlo && sf.Interiors.ContainsKey(e.ArchetypeHash) && sf.Contains(doc.FilePath) && Scene.Find(id) is { } inst && EditInterior(inst))
                if (interiors.Values.FirstOrDefault(i => i.InstanceId == id) is { } oi) oi.Auto = false;
        var docs = mapDocs.Values.Where(d => sf.Contains(d.FilePath)).ToList();
        // their folders start closed in the Scene Explorer (hundreds of rows each); a pick opens the one it is in
        foreach (var d in docs) closedFolders.Add(d.Name);
        foreach (var oi in interiors.Values.Where(i => i.FilePath != null)) closedFolders.Add(oi.Folder);
        if (docs.Count > 0)
        {
            mapActive = docs[0].Name;
            // the camera goes to the interiors of the folder, else to the middle of its entities (median: a stray far entity
            // or a broken position does not throw the view to the other side of the world)
            var mlo = docs.SelectMany(d => d.Entities).Where(x => x.IsMlo && sf.Interiors.ContainsKey(x.ArchetypeHash)).Select(x => x.Position).ToList();
            var all = mlo.Count > 0 ? mlo : docs.SelectMany(d => d.Entities).Where(x => x.IsHd).Select(x => x.Position).Where(p => MathF.Abs(p.X) < 20000 && MathF.Abs(p.Y) < 20000 && MathF.Abs(p.Z) < 5000).ToList();
            if (all.Count > 0)
            {
                float Med(Func<Vector3, float> f) { var l = all.Select(f).OrderBy(v => v).ToList(); return l[l.Count / 2]; }
                float Spread(Func<Vector3, float> f) { var l = all.Select(f).OrderBy(v => v).ToList(); return l[(int)(l.Count * 0.9f)] - l[(int)(l.Count * 0.1f)]; }
                var cam = Viewports[ActiveViewport].Camera;
                if (cam.Ortho) { cam.Kind = ViewKind.Perspective; cam.Ortho = false; }
                cam.Target = new Vector3(Med(p => p.X), Med(p => p.Y), Med(p => p.Z));
                cam.Distance = Math.Clamp(MathF.Max(Spread(p => p.X), Spread(p => p.Y)) * 1.2f, 120, 600); cam.Pitch = 0.6f;
            }
        }
        SetStatus($"{Path.GetFileName(dir.TrimEnd('\\', '/'))}: {sf.Ymaps.Count} .ymap, {sf.Ytyps.Count} .ytyp ({sf.Interiors.Count} interior(s)), {sf.Models.Count + sf.Dictionaries.Count} model file(s)"
                  + (sf.Errors.Count > 0 ? " | " + string.Join(" | ", sf.Errors.Take(3)) : "") + (game == null ? " | load the game to see its own models around" : ""));
    }

    void CloseStreamFolder()
    {
        if (stream == null) return;
        var sf = stream;
        foreach (var oi in interiors.Values.Where(i => i.FilePath != null).ToList()) CloseInterior(oi);
        foreach (var d in mapDocs.Values.Where(d => sf.Contains(d.FilePath)).ToList()) UnloadMapDoc(d);
        foreach (var h in sf.Models.Keys.Concat(sf.DictionaryOf.Keys))
            if (archSlots.Remove(h)) mapSlotOf.Remove("map:" + h.ToString("X8"));
        stream = null;
        SetStatus("Stream folder closed.");
    }

    /// <summary>Map panel part: open / save / close a stream folder.</summary>
    void StreamFolderPanel(float w2)
    {
        if (stream == null)
        {
            if (ImGui.Button("Open stream folder...", new Vector2(-1, 26))) OpenStreamFolderDialog();
            Theme.Tip("Like a CodeWalker project: every .ymap of the folder loads where it stands, its .ydr / .yft / .ydd are drawn, its interiors open from its .ytyp. SAVE writes the changed files back into the folder.");
            return;
        }
        Ui.TextColored(Theme.Gold, "Stream folder: " + Path.GetFileName(stream.Folder.TrimEnd('\\', '/')));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(stream.Folder);
        Ui.TextDisabled($"{stream.Ymaps.Count} .ymap · {stream.Ytyps.Count} .ytyp · {stream.Interiors.Count} interior(s) · {stream.Models.Count + stream.Dictionaries.Count} models");
        if (ImGui.Button("Reload##sf", new Vector2(w2, 0))) { var f = stream.Folder; CloseStreamFolder(); OpenStreamFolder(f); }
        Theme.Tip("Reads the folder again (unsaved changes in it are lost).");
        ImGui.SameLine(0, 4);
        if (ImGui.Button("Close##sf", new Vector2(w2, 0))) CloseStreamFolder();
    }
}
