using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Core.Tools;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Automatic finishing of rooms: smart skirting boards (stop at the doors), dirt decals along the floor and under
/// the ceiling, normal-only edge wear on the wall corners, optional cornice. Used by the MLO room builder and by
/// Molo Tools on any selected room.
/// </summary>
public sealed partial class EditorApp
{
    public bool FinishSkirting = true, FinishFloorDirt = true, FinishCeilingDirt = true, FinishCornerWear = true, FinishCornice;
    /// <summary>Casing around every door and window cut in the rooms; style 0 = varied (each opening its own profile).</summary>
    public bool FinishFrames = true;
    public int FinishFrameStyle;
    /// <summary>Connected rooms: at an inner door the two casings are joined by a lining through the gap (one door frame).</summary>
    public bool FinishJoinDoors = true;
    /// <summary>Passages of the plan being finished (inner doors), set by the room builder.</summary>
    List<SceneObject> finishLinks = new();
    static readonly string[] FrameStyleNames = { "Varied", "Flat", "Moulded", "Modern thin", "Classic wide", "Rounded" };

    /// <summary>The casing profile of a style (1..5), as a door or a window frame.</summary>
    static TrimSettings CasingProfile(int style, bool door)
    {
        var k = door ? TrimKind.DoorFrame : TrimKind.WindowFrame;
        return style switch
        {
            1 => new TrimSettings { Kind = k, Height = 0.08f, Depth = 0.015f, Inner = TrimEdge.Square, Outer = TrimEdge.Chamfer, OuterAmount = 0.35f, CapEnds = false },
            2 => TrimSettings.Frame(k),
            3 => new TrimSettings { Kind = k, Height = 0.045f, Depth = 0.022f, Inner = TrimEdge.Square, Outer = TrimEdge.Square, CapEnds = false },
            4 => new TrimSettings { Kind = k, Height = 0.2f, Depth = 0.05f, Inner = TrimEdge.Ogee, Outer = TrimEdge.Round, OuterAmount = 0.45f, Segments = 4, CapEnds = false },
            _ => new TrimSettings { Kind = k, Height = 0.11f, Depth = 0.03f, Inner = TrimEdge.Cove, InnerAmount = 0.4f, Outer = TrimEdge.Round, OuterAmount = 0.9f, Segments = 4, CapEnds = false },
        };
    }
    public float FinishSkirtingHeight = 0.10f, FinishDirtWidth = 0.18f;
    int finishMade;
    /// <summary>Room materials: floor, walls, ceiling, skirting (-1 = keep the default), and their tile size in metres.</summary>
    public readonly int[] RoomMat = { -1, -1, -1, -1 };
    public readonly float[] RoomMatTile = { 2f, 2f, 2f, 1f };
    static readonly string[] RoomSlotNames = { "Floor", "Walls", "Ceiling", "Skirting" };
    int libRoomSlot;

    void RoomMaterialOptions()
    {
        Ui.Text("Materials");
        var names = new List<string> { "(default)" }; names.AddRange(Scene.Materials.Select(m => m.Name));
        for (int i = 0; i < 4; i++)
        {
            ImGui.PushID("rm" + i);
            int idx = RoomMat[i] < 0 ? 0 : Scene.Materials.FindIndex(m => m.Id == RoomMat[i]) + 1;
            ImGui.AlignTextToFramePadding(); Ui.Text(RoomSlotNames[i]); ImGui.SameLine(64);
            ImGui.SetNextItemWidth(-30);
            if (ImGui.Combo("##m", ref idx, names.ToArray(), names.Count)) RoomMat[i] = idx <= 0 ? -1 : Scene.Materials[idx - 1].Id;
            ImGui.SameLine(0, 4);
            if (ImGui.Button("L", new Vector2(22, 0))) { libRoomSlot = i; OpenLibrary(LibraryAction.RoomSlot); }
            Theme.Tip("Pick a GTA material in Molo Materials (texture library)");
            if (RoomMat[i] >= 0 && i < 3) { ImGui.SetNextItemWidth(-1); ImGui.DragFloat("##t", ref RoomMatTile[i], 0.05f, 0.1f, 20, "tile %.2f m"); }
            ImGui.PopID();
        }
    }

    /// <summary>Floor / walls / ceiling materials on a room or passage mesh, with real-world box UVs.</summary>
    void ApplyRoomMaterials(SceneObject room)
    {
        if (RoomMat[0] < 0 && RoomMat[1] < 0 && RoomMat[2] < 0) return;
        var m = room.Mesh;
        var sets = new[] { new HashSet<int>(), new HashSet<int>(), new HashSet<int>() };
        for (int f = 0; f < m.Faces.Count; f++)
        {
            var n = m.FaceNormal(m.Faces[f]);
            int slot = n.Z > 0.7f ? 0 : n.Z < -0.7f ? 2 : 1;
            if (RoomMat[slot] < 0) continue;
            m.Faces[f].MatId = RoomMat[slot];
            sets[slot].Add(f);
        }
        for (int i = 0; i < 3; i++) if (sets[i].Count > 0) MeshOps.BoxMap(m, sets[i], MathF.Max(RoomMatTile[i], 0.05f));
        room.Touch();
    }

    void RoomFinishOptions()
    {
        Ui.Text("Finishing (automatic)");
        ImGui.Checkbox("Smart skirting boards", ref FinishSkirting);
        Theme.Tip("Along every floor / wall joint, mitred in the corners, stopping at the doors.");
        if (FinishSkirting) { ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Skirting h.", ref FinishSkirtingHeight, 0.005f, 0.04f, 0.4f, "%.3f m"); }
        ImGui.Checkbox("Dirt along the floor", ref FinishFloorDirt);
        Theme.Tip("decal.sps strip at the bottom of the walls and on the floor next to them.");
        ImGui.Checkbox("Dirt under the ceiling", ref FinishCeilingDirt);
        ImGui.Checkbox("Edge wear on corners", ref FinishCornerWear);
        Theme.Tip("decal_normal_only.sps on the wall corners: chipped, worn edges without changing the colour.");
        ImGui.Checkbox("Cornice", ref FinishCornice);
        ImGui.Checkbox("Door & window frames", ref FinishFrames);
        Theme.Tip("A casing around every door and window, on each room side (no casing across the floor of a doorway).");
        if (FinishFrames) { ImGui.SetNextItemWidth(-90); ImGui.Combo("Frame style", ref FinishFrameStyle, FrameStyleNames, FrameStyleNames.Length); Theme.Tip("Varied: each opening gets its own profile (flat, moulded, modern, classic, rounded).");
            ImGui.Checkbox("Connect the rooms (joined door frames)", ref FinishJoinDoors);
            Theme.Tip("At a door between two rooms, a lining runs through the gap and meets the casing of each side: one real door frame, same profile on both sides."); }
        if (FinishFloorDirt || FinishCeilingDirt || FinishCornerWear) { ImGui.SetNextItemWidth(-90); ImGui.DragFloat("Dirt width", ref FinishDirtWidth, 0.005f, 0.03f, 1f, "%.2f m"); }
    }

    /// <summary>Adds the finishing pieces to the rooms (call inside an undo step). Returns how many objects were made.</summary>
    int FinishRooms(List<SceneObject> rooms, string folder, bool live = false)
    {
        if (rooms.Count == 0) return 0;
        bool any = FinishSkirting || FinishFloorDirt || FinishCeilingDirt || FinishCornerWear || FinishCornice || FinishFrames;
        if (!any) { foreach (var r in rooms) ApplyRoomMaterials(r); return 0; }
        GtaMaterial dirtMat = FinishFloorDirt || FinishCeilingDirt ? StripMaterial("edge_dirt") : null;
        GtaMaterial wearMat = FinishCornerWear ? StripMaterial("edge_wear") : null;
        if (!live) toolSeed++;
        GtaMaterial baseMat = !FinishSkirting ? null : RoomMat[3] >= 0 && Scene.Materials.Any(x => x.Id == RoomMat[3]) ? Scene.Material(RoomMat[3]) : ToolMaterial("molo_baseboard", "normal_spec", new Vector3(0.85f, 0.83f, 0.8f));
        GtaMaterial cornMat = FinishCornice ? ToolMaterial("molo_cornice", "normal_spec", new Vector3(0.85f, 0.83f, 0.8f)) : null;
        GtaMaterial frameMat = FinishFrames ? ToolMaterial("molo_casing", "normal_spec", new Vector3(0.85f, 0.83f, 0.8f)) : null;
        int made = 0;
        void Add(string name, PolyMesh m, SceneObject room, GtaMaterial mat, bool decal)
        {
            if (m == null || m.Faces.Count == 0) return;
            var o = AddToolObject(NextName($"{room.Name}_{name}"), m, room, mat, decal);
            o.Folder = folder ?? room.Folder;
            o.Gta.NoAutoCollision = true;
            made++;
        }
        foreach (var room in rooms)
        {
            ApplyRoomMaterials(room);
            var e = RoomFinish.Classify(room.Mesh);
            if (FinishSkirting && e.FloorWall.Count > 0)
            {
                var ts = new TrimSettings { Kind = TrimKind.Baseboard, Height = FinishSkirtingHeight, Depth = MathF.Max(0.012f, FinishSkirtingHeight * 0.15f), Outer = TrimEdge.Round };
                Add("skirting", TrimSweep.Build(room.Mesh, e.FloorWall, ts, baseMat.Id), room, baseMat, false);
            }
            if (FinishCornice && e.CeilingWall.Count > 0)
            {
                var ts = new TrimSettings { Kind = TrimKind.Cornice, Height = 0.08f, Depth = 0.05f, Outer = TrimEdge.Cove };
                Add("cornice", TrimSweep.Build(room.Mesh, e.CeilingWall, ts, cornMat.Id), room, cornMat, false);
            }
            if (FinishFrames) made += AddFrames(room, frameMat, folder);
            var st = new StripSettings { Width = FinishDirtWidth, Offset = 0.002f };
            if (FinishFloorDirt && e.FloorWall.Count > 0)
            {
                // above the skirting board, the dirt starts on the wall
                Add("floor_dirt", EdgeStrips.Build(room.Mesh, e.FloorWall, st, dirtMat.Id), room, dirtMat, true);
            }
            if (FinishCeilingDirt && e.CeilingWall.Count > 0)
                Add("ceiling_dirt", EdgeStrips.Build(room.Mesh, e.CeilingWall, st, dirtMat.Id), room, dirtMat, true);
            if (FinishCornerWear && e.Corners.Count > 0)
                Add("corner_wear", EdgeStrips.Build(room.Mesh, e.Corners, new StripSettings { Width = MathF.Min(FinishDirtWidth, 0.08f), Offset = 0.002f }, wearMat.Id), room, wearMat, true);
        }
        if (FinishFrames && FinishJoinDoors)
            foreach (var link in finishLinks)
            {
                var lm = DoorLining(link, CasingProfile(FrameStyleAt(link.Position), true), frameMat.Id);
                if (lm == null) continue;
                var o = AddToolObject(NextName($"{link.Name}_lining"), lm, link, frameMat, false);
                o.Folder = folder ?? link.Folder; o.Gta.NoAutoCollision = true; made++;
            }
        return made;
    }

    /// <summary>Frame profile of an opening at this world point (Varied: from the position, so the two sides match).</summary>
    int FrameStyleAt(Vector3 c) => FinishFrameStyle > 0 ? FinishFrameStyle : 1 + (int)((uint)HashCode.Combine(MathF.Round(c.X * 2), MathF.Round(c.Y * 2), MathF.Round(c.Z)) % 5);

    /// <summary>The lining of a door between two rooms: boards on the sides and the head of the passage, running out on
    /// each side as far as the casing stands off the wall, so both casings sit on it (one door frame).</summary>
    static PolyMesh DoorLining(SceneObject link, TrimSettings casing, int matId)
    {
        var lm = link.Mesh; if (lm.Faces.Count == 0) return null;
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var v in lm.Verts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
        var size = mx - mn; var centre = (mn + mx) * 0.5f;
        var across = size.X < size.Y ? Vector3.UnitX : Vector3.UnitY;
        float ext = casing.Depth + 0.002f, t = 0.018f;
        var m = new PolyMesh();
        foreach (var f in lm.Faces)
        {
            var n = lm.FaceNormal(f);
            if (n.Z > 0.7f) continue; // the floor of the doorway stays bare
            var q = f.V.Select(i => lm.Verts[i]).Select(v => v + across * MathF.Sign(Vector3.Dot(v - centre, across)) * ext).ToArray();
            if (q.Length != 4) continue;
            var inner = q.Select(v => v + n * t).ToArray();
            int[] a = q.Select(v => m.AddVertex(v)).ToArray(), b = inner.Select(v => m.AddVertex(v)).ToArray();
            var bc = (q.Aggregate(Vector3.Zero, (x, y) => x + y) + inner.Aggregate(Vector3.Zero, (x, y) => x + y)) / 8;
            void Face(params int[] ids) { var fa = m.AddFace(ids); var fc = ids.Aggregate(Vector3.Zero, (x, i) => x + m.Verts[i]) / ids.Length; if (Vector3.Dot(m.FaceNormal(fa), fc - bc) < 0) fa.Flip(); }
            Face(b[0], b[1], b[2], b[3]); Face(a[3], a[2], a[1], a[0]);
            for (int i = 0; i < 4; i++) { int j = (i + 1) % 4; Face(a[i], a[j], b[j], b[i]); }
        }
        if (m.Faces.Count == 0) return null;
        foreach (var f in m.Faces) f.MatId = matId;
        MeshOps.BoxMap(m, Enumerable.Range(0, m.Faces.Count).ToHashSet(), 1f);
        return m;
    }

    /// <summary>A casing around each opening of a room (its open border loops): doors skip the floor edge, windows are framed
    /// all around. Varied style: the profile depends on where the opening is, so both sides of a door match.</summary>
    int AddFrames(SceneObject room, GtaMaterial mat, string folder)
    {
        var m = room.Mesh; int made = 0;
        if (m.Verts.Count == 0) return 0;
        float floorZ = m.Verts.Min(v => v.Z);
        foreach (var loop in MoloMax.Core.Gta.Export.MloLayout.BorderLoops(m))
        {
            if (loop.Count < 3) continue;
            float lz = loop.Min(v => m.Verts[v].Z);
            bool door = lz <= floorZ + 0.01f;
            var edges = new HashSet<EdgeKey>();
            for (int i = 0; i < loop.Count; i++)
            {
                int a = loop[i], b = loop[(i + 1) % loop.Count];
                if (door && m.Verts[a].Z <= floorZ + 0.01f && m.Verts[b].Z <= floorZ + 0.01f) continue; // no casing across the doorway floor
                edges.Add(EdgeKey.Make(a, b));
            }
            if (edges.Count == 0) continue;
            // world centre of the opening: the two rooms of an inner door land on the same style
            var c = Vector3.Transform(loop.Select(v => m.Verts[v]).Aggregate(Vector3.Zero, (x, y) => x + y) / loop.Count, room.World);
            // an inner door takes the style of its passage: both rooms and the lining get the very same profile
            var link = door ? finishLinks.FirstOrDefault(l => new Vector2(l.Position.X - c.X, l.Position.Y - c.Y).Length() < 0.6f) : null;
            int style = FrameStyleAt(link?.Position ?? c);
            var trim = TrimSweep.Build(m, edges, CasingProfile(style, door), mat.Id);
            if (trim.Faces.Count == 0) continue;
            var o = AddToolObject(NextName($"{room.Name}_{(door ? "doorframe" : "windowframe")}"), trim, room, mat, false);
            o.Folder = folder ?? room.Folder;
            o.Gta.NoAutoCollision = true;
            made++;
        }
        return made;
    }

    /// <summary>Molo Tools: finish the selected rooms (or room-like meshes).</summary>
    void FinishSelectedRooms()
    {
        var rooms = SelObjects().Where(o => o.Mesh.Faces.Count > 0 && o.GtaType is GtaObjectType.MloRoom or GtaObjectType.Drawable).ToList();
        if (rooms.Count == 0) { SetStatus("Select the rooms (or the shell meshes) to finish."); return; }
        int n = 0;
        Do("Finish rooms", () => n = FinishRooms(rooms, null));
        SetStatus(n > 0 ? $"{n} finishing piece(s) added to {rooms.Count} room(s)." : "Nothing to add: no floor / wall or ceiling joint found.");
    }
}
