using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Gta.Map;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// MAP mode: a game interior (MLO) placed in a loaded map opens for editing. Its room objects come into the scene where
/// they stand in the world; moved / removed / added objects are written back into the interior's .ytyp on save.
/// </summary>
public sealed partial class EditorApp
{
    sealed class OpenInterior
    {
        public MloDocument Doc; public int InstanceId; public Vector3 Pos; public Quaternion Rot = Quaternion.Identity; public string Folder;
        public readonly Dictionary<MloEntity, int> ObjOf = new();
        /// <summary>Opened by itself because the camera came near (view only until edited).</summary>
        public bool Auto;
        /// <summary>The instance's Hidden state last passed on to the room objects.</summary>
        public bool Hidden;
    }

    /// <summary>Hiding an interior (its MLO instance) hides what is inside it too, and showing it brings it back.</summary>
    void SyncInteriorHidden()
    {
        foreach (var oi in interiors.Values)
        {
            var inst = Scene.Find(oi.InstanceId);
            bool h = inst != null && inst.Hidden;
            if (h == oi.Hidden) continue;
            oi.Hidden = h;
            foreach (var id in oi.ObjOf.Values) if (Scene.Find(id) is { } o) o.Hidden = h;
        }
    }
    /// <summary>MAP: show the inside of the interiors (MLO) near the camera.</summary>
    public bool MapShowInteriors = true;
    public float MapInteriorRadius = 90f;
    double interiorAutoTime;
    readonly HashSet<uint> interiorFailed = new();

    /// <summary>Opens the interiors whose instance is near the camera (and closes the far, unchanged ones).</summary>
    void AutoInteriors()
    {
        if (ImGui.GetTime() - interiorAutoTime < 1.0 || game == null) return;
        interiorAutoTime = ImGui.GetTime();
        var p = MapFocus();
        // close: far, instance gone, or switched off, and nothing changed inside
        foreach (var oi in interiors.Values.Where(i => i.Auto).ToList())
        {
            var inst = Scene.Find(oi.InstanceId);
            bool far = inst == null || !MapShowInteriors || Vector2.Distance(new Vector2(oi.Pos.X, oi.Pos.Y), new Vector2(p.X, p.Y)) > MapInteriorRadius * 1.5f;
            // an interior with hidden objects stays open (closing and reopening it would show them again)
            if (far && !oi.Doc.Modified && !oi.Hidden && !oi.ObjOf.Values.Any(id => Scene.Find(id) is { Hidden: true }) && !SyncInterior(oi, apply: false)) CloseInterior(oi);
        }
        if (!MapShowInteriors || !HdStreamingNow) return;
        int opened = 0;
        var mlos = entOf.Where(kv => kv.Value.Ent.IsMlo && Vector2.Distance(new Vector2(kv.Value.Ent.Position.X, kv.Value.Ent.Position.Y), new Vector2(p.X, p.Y)) <= MapInteriorRadius).ToList();
        // the base game and a DLC can place two versions of one interior at the same spot: the game shows the DLC one
        static int Newer(string map) => (map.Contains("dlc", StringComparison.OrdinalIgnoreCase) ? 2 : 0) + (map.Length > 4 && map[3] == '_' || map.Length > 3 && map[2] == '_' ? 1 : 0);
        foreach (var (id, (doc, e)) in mlos)
        {
            if (interiorFailed.Contains(e.ArchetypeHash)) continue;
            if (mlos.Any(o => o.Key != id && Vector3.Distance(o.Value.Ent.Position, e.Position) < 4 && Newer(o.Value.Doc.Name) > Newer(doc.Name))) continue;
            if (interiors.Values.Any(i => i.InstanceId == id)) continue;
            var inst = Scene.Find(id); if (inst == null) continue;
            var keepActive = interiorActive; var keepStatus = status;
            if (EditInterior(inst))
            {
                var oi = interiors.Values.First(i => i.InstanceId == id); oi.Auto = true;
                interiorActive = keepActive;
            }
            else interiorFailed.Add(e.ArchetypeHash);
            status = keepStatus;
            if (++opened >= 2) break; // a couple per second: no hitch
        }
    }

    /// <summary>An interior opened by itself stays out of the Scene Explorer until something in it is picked or changed.</summary>
    bool IsViewOnlyInterior(string folder) =>
        interiors.TryGetValue(folder ?? "", out var oi) && oi.Auto && !oi.Doc.Modified && interiorActive != folder && !Selected.Any(id => intEntOf.TryGetValue(id, out var x) && x.In == oi);
    readonly Dictionary<string, OpenInterior> interiors = new();
    readonly Dictionary<int, (OpenInterior In, MloEntity Ent)> intEntOf = new();
    /// <summary>Interior folder new props go into (else the active .ymap).</summary>
    string interiorActive;
    readonly HashSet<string> interiorChanged = new(); double interiorChangedTime;

    /// <summary>Opens the interior placed by this map entity (an MLO instance) for editing. Returns false when it cannot.</summary>
    public bool EditInterior(SceneObject inst)
    {
        if (inst == null || !entOf.TryGetValue(inst.Id, out var de) || !de.Ent.IsMlo) { SetStatus("Select an interior (MLO) of a loaded map first."); return false; }
        if (game == null) { SetStatus("Load the game files first (the interior is read from them)."); return false; }
        var open = interiors.Values.FirstOrDefault(i => i.InstanceId == inst.Id);
        if (open != null) { interiorActive = open.Folder; return true; }
        byte[] bytes; string yname;
        try { bytes = game.InteriorTypeFile(de.Ent.ArchetypeHash, out yname); }
        catch (Exception e) { SetStatus("Interior not readable: " + e.Message); return false; }
        if (bytes == null) { SetStatus($"The interior {de.Ent.DisplayName} is not in the game files (.ytyp not found)."); return false; }
        MloDocument doc;
        try { doc = MloDocument.Load(bytes, yname + ".ytyp", de.Ent.ArchetypeHash, h => game.FindModel(h)?.Name); }
        catch (Exception e) { SetStatus("Interior not readable: " + e.Message); return false; }
        if (string.IsNullOrEmpty(doc.ArchetypeName)) doc.ArchetypeName = de.Ent.Archetype ?? yname;
        var folder = "interior " + doc.ArchetypeName;
        var oi = new OpenInterior { Doc = doc, InstanceId = inst.Id, Pos = de.Ent.Position, Rot = Quaternion.Normalize(de.Ent.Rotation), Folder = folder, Hidden = inst.Hidden };
        interiors[folder] = oi;
        if (!Scene.Folders.Contains(folder)) Scene.Folders.Add(folder);
        foreach (var e in doc.Entities) SpawnInteriorEntity(oi, e);
        interiorActive = folder;
        SetStatus($"Interior {doc.ArchetypeName ?? yname}: {doc.Entities.Count} objects in {doc.Rooms.Count - 1} room(s). Save the map resource to write its .ytyp.");
        return true;
    }

    SceneObject SpawnInteriorEntity(OpenInterior oi, MloEntity e)
    {
        var slot = Slot(e.ArchetypeHash, e.Archetype);
        var o = new SceneObject
        {
            Id = Scene.NextObjectId++, Name = e.DisplayName, Mesh = slot.Mesh ?? Placeholder(slot),
            Position = oi.Pos + Vector3.Transform(e.Position, oi.Rot), Rotation = Quaternion.Normalize(oi.Rot * e.Rotation),
            Scale = new Vector3(e.Scale.X, e.Scale.X, e.Scale.Z), Folder = oi.Folder, InstanceKey = "map:" + e.ArchetypeHash.ToString("X8"),
            WireColour = new Vector3(0.55f, 0.85f, 1f),
        };
        o.Gta.MapArchetype = e.Archetype ?? e.ArchetypeHash.ToString("X8");
        o.Gta.Role = GtaRole.Ignore;
        o.Hidden = oi.Hidden;
        Scene.Objects.Add(o);
        intEntOf[o.Id] = (oi, e); oi.ObjOf[e] = o.Id;
        return o;
    }

    /// <summary>Brings an open interior in line with the scene (world placement back to interior space).</summary>
    bool SyncInterior(OpenInterior oi, bool apply)
    {
        bool changed = false;
        var inv = Quaternion.Conjugate(oi.Rot);
        foreach (var e in oi.Doc.Entities.ToList())
        {
            if (!oi.ObjOf.TryGetValue(e, out var id)) continue;
            var o = Scene.Find(id);
            if (o == null || o.Folder != oi.Folder)
            {
                if (o == null && !mapDeleted.Contains(id)) continue; // gone with an undo of the opening
                changed = true;
                if (apply) { oi.Doc.Remove(e); oi.ObjOf.Remove(e); intEntOf.Remove(id); }
                continue;
            }
            var lp = Vector3.Transform(o.Position - oi.Pos, inv);
            var lr = Quaternion.Normalize(inv * o.Rotation);
            var sc = new Vector3(o.Scale.X, o.Scale.X, o.Scale.Z);
            if (Vector3.DistanceSquared(lp, e.Position) > 1e-8f || MathF.Abs(MathF.Abs(Quaternion.Dot(lr, e.Rotation)) - 1) > 1e-6f || Vector3.DistanceSquared(sc, e.Scale) > 1e-8f)
            {
                changed = true;
                if (apply)
                {
                    e.Position = lp; e.Rotation = lr; e.Scale = sc; oi.Doc.Modified = true;
                    // moved into another room: it belongs there now
                    int r = oi.Doc.RoomAt(lp);
                    var (_, mn, mx) = e.Room > 0 && e.Room < oi.Doc.Rooms.Count ? oi.Doc.Rooms[e.Room] : (null, Vector3.Zero, Vector3.Zero);
                    bool inside = e.Room > 0 && lp.X >= mn.X && lp.X <= mx.X && lp.Y >= mn.Y && lp.Y <= mx.Y && lp.Z >= mn.Z && lp.Z <= mx.Z;
                    if (!inside && r > 0) e.Room = r;
                }
            }
        }
        // objects placed in (or moved into) the interior folder
        foreach (var o in Scene.Objects)
        {
            if (o.Folder != oi.Folder || o.Gta.MapArchetype == null || intEntOf.ContainsKey(o.Id) || o.Id == placeGhost) continue;
            changed = true;
            if (apply)
            {
                if (entOf.TryGetValue(o.Id, out var de)) { de.Doc.Remove(de.Ent); objOf.Remove(de.Ent); entOf.Remove(o.Id); } // came from a .ymap
                var e = oi.Doc.Add(o.Gta.MapArchetype, Vector3.Transform(o.Position - oi.Pos, inv), Quaternion.Normalize(inv * o.Rotation), new Vector3(o.Scale.X, o.Scale.X, o.Scale.Z));
                intEntOf[o.Id] = (oi, e); oi.ObjOf[e] = o.Id;
            }
        }
        if (apply && changed) oi.Doc.Modified = true;
        return changed;
    }

    /// <summary>Writes the changed interiors' .ytyp into the stream folder; returns how many.</summary>
    int SaveInteriors(string stream, List<string> errors)
    {
        int n = 0;
        foreach (var oi in interiors.Values)
        {
            SyncInterior(oi, apply: true);
            if (!oi.Doc.Modified) continue;
            try
            {
                var bytes = oi.Doc.Save();
                Directory.CreateDirectory(stream);
                File.WriteAllBytes(Path.Combine(stream, oi.Doc.Name + ".ytyp"), bytes);
                n++;
            }
            catch (Exception e) { errors.Add($"{oi.Doc.Name}.ytyp: {e.Message}"); }
        }
        return n;
    }

    void CloseInterior(OpenInterior oi)
    {
        var ids = new HashSet<int>(oi.ObjOf.Values);
        Scene.Objects.RemoveAll(o => ids.Contains(o.Id) || o.Folder == oi.Folder && o.Gta.MapArchetype != null);
        foreach (var id in ids) { intEntOf.Remove(id); Selected.Remove(id); }
        interiors.Remove(oi.Folder); Scene.Folders.Remove(oi.Folder);
        if (interiorActive == oi.Folder) interiorActive = null;
        SelectionChanged();
    }

    /// <summary>Map panel part: the open interiors.</summary>
    void InteriorsPanel()
    {
        if (interiors.Count == 0) return;
        Ui.Label("Interiors (MLO) open for editing", Theme.TextDim);
        foreach (var oi in interiors.Values.ToList())
        {
            ImGui.PushID(oi.Folder);
            if (ImGui.RadioButton("##iact", interiorActive == oi.Folder)) interiorActive = oi.Folder;
            Theme.Tip("New props go into this interior");
            ImGui.SameLine();
            if (ImGui.GetTime() - interiorChangedTime > 1) { interiorChangedTime = ImGui.GetTime(); interiorChanged.Clear(); foreach (var x in interiors.Values) if (SyncInterior(x, apply: false)) interiorChanged.Add(x.Folder); }
            bool ch = interiorChanged.Contains(oi.Folder) || oi.Doc.Modified;
            Ui.Text($"{oi.Doc.ArchetypeName ?? oi.Doc.Name}  {oi.Doc.Entities.Count} obj, {oi.Doc.Rooms.Count - 1} rooms" + (ch ? "  *" : ""));
            ImGui.SameLine();
            if (ImGui.SmallButton("Close")) { if (ch) SetStatus("Unsaved interior changes dropped: " + oi.Doc.Name); CloseInterior(oi); }
            ImGui.PopID();
        }
    }
}
