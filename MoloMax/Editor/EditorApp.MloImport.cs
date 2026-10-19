using System.Numerics;
using System.Windows.Forms;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Import;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>Re-opens a whole interior from its .ytyp: rooms, portals (with flags), every entity (entity sets kept), collision.</summary>
public sealed partial class EditorApp
{
    public void ImportMloDialog()
    {
        using var d = new OpenFileDialog { Filter = "MLO archetypes (*.ytyp)|*.ytyp", Title = "Import an MLO (.ytyp with its .ydr / .ybn next to it)" };
        bool ok = ShowFileDialog(d) == DialogResult.OK;
        Input.ReleaseAllKeys();
        if (ok) ImportMlo(d.FileName);
    }

    public void ImportMlo(string path)
    {
        List<MloImport> mlos;
        try { mlos = YtypImporter.Read(path); }
        catch (Exception e) { ImportFailed(path, e); return; }
        if (mlos.Count == 0) { SetStatus("No interior (MLO archetype) in " + Path.GetFileName(path)); return; }
        var m = mlos[0];
        var folder = m.Name;
        var tcByHash = Timecycles.Where(t => t.Length > 0).ToDictionary(t => MetaXml.Jenkins(t), t => t);
        var made = new List<SceneObject>();
        Do("Import MLO " + m.Name, () =>
        {
            if (!Scene.Folders.Contains(folder)) Scene.Folders.Add(folder);
            for (int i = 1; i < m.Rooms.Count; i++) // 0 = limbo
            {
                var r = m.Rooms[i];
                var size = r.Max - r.Min;
                var room = Scene.Add(r.Name, Primitives.Room(size.X, size.Y, size.Z), new Vector3((r.Min.X + r.Max.X) / 2, (r.Min.Y + r.Max.Y) / 2, r.Min.Z), GtaObjectType.MloRoom);
                room.Name = r.Name; room.Folder = folder; room.Gta.RoomFlags = r.Flags; room.Gta.BoundsOnly = true;
                room.WireColour = new Vector3(0.75f, 0.45f, 1f);
                room.Gta.RoomTimecycle = tcByHash.TryGetValue(r.TimecycleHash, out var tc) ? tc : "";
                made.Add(room);
            }
            int pi = 0;
            foreach (var p in m.Portals)
            {
                if (p.Corners.Length < 3) continue;
                var mesh = new PolyMesh();
                foreach (var c in p.Corners) mesh.AddVertex(c);
                mesh.AddFace(Enumerable.Range(0, p.Corners.Length).ToArray(), 0);
                var po = Scene.Add($"portal_{pi++}", mesh, Vector3.Zero, GtaObjectType.Portal);
                po.Folder = folder; po.Gta.PortalFlags = p.Flags;
                made.Add(po);
            }
        });
        // models: one import per entity, placed and rotated like in the interior
        int fromGame = 0; var missing = new List<string>();
        foreach (var e in m.Entities)
        {
            List<SceneObject> objs;
            var file = e.File;
            // not next to the .ytyp: taken from the game files when they are loaded (game interiors)
            if (file == null && game?.FindModel(e.Hash) is { } ga)
            {
                try { file = game.Extract(ga, Path.Combine(GameExtractDir, ga.Name)); fromGame++; } catch { }
            }
            if (file != null) objs = ImportFile(file, e.Position, e.Rotation, e.Scale);
            else
            {
                missing.Add(e.Archetype);
                SceneObject box = null;
                Do("Placeholder", () => { box = Scene.Add(e.Archetype, Primitives.Box(0.5f, 0.5f, 0.5f), e.Position); box.Rotation = e.Rotation; box.Name = e.Archetype; });
                objs = new List<SceneObject> { box };
            }
            var top = objs.FirstOrDefault(o => o.GtaType == GtaObjectType.Model) ?? objs.FirstOrDefault();
            if (top == null) continue;
            bool shell = e.Archetype.Contains("_shell", StringComparison.OrdinalIgnoreCase);
            foreach (var o in objs.Where(o => Scene.Parent(o) == null || o == top))
            {
                o.Folder = folder;
                o.Gta.Role = shell ? GtaRole.Shell : GtaRole.Prop;
                o.Gta.EntitySet = e.EntitySet ?? "";
            }
            foreach (var o in objs) { o.Folder = folder; }
            made.AddRange(objs);
        }
        foreach (var c in m.Collisions) { var objs = ImportFile(c, Vector3.Zero); foreach (var o in objs) o.Folder = folder; made.AddRange(objs); }
        Selected.Clear(); foreach (var o in made.Where(o => Scene.Parent(o) == null)) Selected.Add(o.Id);
        SelectionChanged(); FrameSelection(); Selected.Clear(); SelectionChanged();
        importReportTitle = "Imported MLO " + m.Name;
        importReport.Clear();
        importReport.Add($"{m.Rooms.Count - 1} room(s), {m.Portals.Count} portal(s), {m.Entities.Count} entities ({m.Entities.Count(x => x.EntitySet != null)} in entity sets), {m.Collisions.Count} collision file(s).");
        if (fromGame > 0) importReport.Add($"{fromGame} model(s) taken from the game files.");
        else if (m.Warnings.Count > 0 && game == null) importReport.Add("Tip: load the game files (GTA > Game files) first, missing models are then taken from the game.");
        importReport.AddRange(m.Warnings.Where(w => !w.StartsWith("Model ")).Distinct().Take(10));
        if (missing.Count > 0) importReport.Add($"{missing.Count} model(s) not found (placeholder boxes): " + string.Join(", ", missing.Distinct().Take(15)) + (missing.Distinct().Count() > 15 ? "..." : ""));
        importReportOpen = true;
    }
}
