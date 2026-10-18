using System.Numerics;
using Cw = CodeWalker.GameFiles;
using SDX = SharpDX;

namespace MoloMax.Core.Gta.Map;

/// <summary>One object of an interior (an entity of a room), in the interior's own space.</summary>
public sealed class MloEntity
{
    public uint ArchetypeHash;
    public string Archetype;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;
    public float LodDist = 100;
    public uint Flags = 32;
    /// <summary>Room it belongs to (index in the interior's room list, 0 = limbo).</summary>
    public int Room = 1;
    internal Cw.MCEntityDef Def;
    public string DisplayName => Archetype ?? $"0x{ArchetypeHash:X8}";
}

/// <summary>
/// An interior (MLO archetype of a .ytyp) opened for editing: the objects of its rooms are moved, removed or added,
/// rooms, portals, entity sets and every other archetype of the file are kept as they are. Saving gives the whole
/// .ytyp back; streamed under the same name, FiveM uses it instead of the game's.
/// </summary>
public sealed class MloDocument
{
    public string Name;              // .ytyp name without extension
    public uint ArchetypeHash;
    public string ArchetypeName;
    public Cw.YtypFile Ytyp;
    public Cw.MloArchetype Mlo;
    public readonly List<MloEntity> Entities = new();
    public readonly List<(string Name, Vector3 Min, Vector3 Max)> Rooms = new();
    public bool Modified;

    static Vector3 V(SDX.Vector3 v) => new(v.X, v.Y, v.Z);
    static SDX.Vector3 S(Vector3 v) => new(v.X, v.Y, v.Z);

    public static MloDocument Load(byte[] ytyp, string name, uint archetypeHash, Func<uint, string> names = null)
    {
        var y = new Cw.YtypFile();
        y.RpfFileEntry = new Cw.RpfResourceFileEntry { Name = Path.GetFileName(name) };
        y.Load(ytyp);
        var mlo = (y.AllArchetypes ?? Array.Empty<Cw.Archetype>()).OfType<Cw.MloArchetype>().FirstOrDefault(a => a.Hash.Hash == archetypeHash)
                  ?? throw new InvalidDataException($"No interior 0x{archetypeHash:X8} in {name}.");
        var d = new MloDocument { Name = Path.GetFileNameWithoutExtension(name).ToLowerInvariant(), ArchetypeHash = archetypeHash, Ytyp = y, Mlo = mlo };
        d.ArchetypeName = names?.Invoke(archetypeHash) ?? Cw.JenkIndex.TryGetString(archetypeHash);
        foreach (var r in mlo.rooms ?? Array.Empty<Cw.MCMloRoomDef>()) d.Rooms.Add((r.RoomName ?? "room", V(r.BBMin), V(r.BBMax)));
        var ents = mlo.entities ?? Array.Empty<Cw.MCEntityDef>();
        var roomOf = new Dictionary<int, int>();
        var rooms = mlo.rooms ?? Array.Empty<Cw.MCMloRoomDef>();
        for (int ri = 0; ri < rooms.Length; ri++)
            foreach (var idx in rooms[ri].AttachedObjects ?? Array.Empty<uint>()) roomOf.TryAdd((int)idx, ri);
        for (int i = 0; i < ents.Length; i++)
        {
            var c = ents[i]._Data;
            uint h = c.archetypeName.Hash;
            string n = names?.Invoke(h);
            if (string.IsNullOrEmpty(n)) { var s = Cw.JenkIndex.TryGetString(h); n = string.IsNullOrEmpty(s) ? null : s; }
            var q = new Quaternion(c.rotation.X, c.rotation.Y, c.rotation.Z, c.rotation.W);
            q = q.IsIdentity || q.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.Conjugate(Quaternion.Normalize(q)); // stored conjugate
            d.Entities.Add(new MloEntity
            {
                ArchetypeHash = h, Archetype = n, Position = V(c.position), Rotation = q, Scale = new Vector3(c.scaleXY, c.scaleXY, c.scaleZ),
                LodDist = c.lodDist, Flags = c.flags, Room = roomOf.TryGetValue(i, out var ro) ? ro : 0, Def = ents[i],
            });
        }
        return d;
    }

    /// <summary>The room holding this point of the interior (its box), else the first room.</summary>
    public int RoomAt(Vector3 p)
    {
        for (int i = 1; i < Rooms.Count; i++)
        {
            var (_, mn, mx) = Rooms[i];
            if (p.X >= mn.X && p.X <= mx.X && p.Y >= mn.Y && p.Y <= mx.Y && p.Z >= mn.Z && p.Z <= mx.Z) return i;
        }
        return Rooms.Count > 1 ? 1 : 0;
    }

    public MloEntity Add(string archetype, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        archetype = archetype.ToLowerInvariant();
        var e = new MloEntity { Archetype = archetype, ArchetypeHash = MapDocument.Hash(archetype), Position = position, Rotation = Quaternion.Normalize(rotation), Scale = scale, Room = RoomAt(position) };
        Entities.Add(e);
        Modified = true;
        return e;
    }

    public void Remove(MloEntity e) { if (Entities.Remove(e)) Modified = true; }

    /// <summary>Writes the objects back into the interior and returns the whole .ytyp.</summary>
    public byte[] Save()
    {
        var old = Mlo.entities ?? Array.Empty<Cw.MCEntityDef>();
        var oldIndex = new Dictionary<Cw.MCEntityDef, int>();
        for (int i = 0; i < old.Length; i++) oldIndex[old[i]] = i;
        var list = new List<Cw.MCEntityDef>();
        var newOfOld = new Dictionary<int, int>();
        foreach (var e in Entities)
        {
            Cw.CEntityDef c;
            if (e.Def != null) c = e.Def._Data;
            else c = new Cw.CEntityDef
            {
                flags = e.Flags, guid = MapDocument.Hash($"{Name}_{e.Archetype}_{e.Position.X:0.00}_{e.Position.Y:0.00}_{Environment.TickCount64}"), parentIndex = -1,
                childLodDist = 0, lodLevel = Cw.rage__eLodType.LODTYPES_DEPTH_ORPHANHD, numChildren = 0, priorityLevel = Cw.rage__ePriorityLevel.PRI_REQUIRED,
                ambientOcclusionMultiplier = 255, artificialAmbientOcclusion = 255, tintValue = 0,
            };
            c.archetypeName = new Cw.MetaHash(e.ArchetypeHash);
            c.position = S(e.Position);
            var q = Quaternion.Conjugate(Quaternion.Normalize(e.Rotation));
            c.rotation = new SDX.Vector4(q.X, q.Y, q.Z, q.W);
            c.scaleXY = e.Scale.X; c.scaleZ = e.Scale.Z;
            c.lodDist = e.LodDist; c.flags = e.Flags;
            var def = new Cw.MCEntityDef(ref c, Mlo);
            if (e.Def != null && oldIndex.TryGetValue(e.Def, out var oi)) { def.Extensions = e.Def.Extensions; newOfOld[oi] = list.Count; }
            e.Def = def;
            list.Add(def);
        }
        Mlo.entities = list.ToArray();
        var rooms = Mlo.rooms ?? Array.Empty<Cw.MCMloRoomDef>();
        for (int ri = 0; ri < rooms.Length; ri++)
        {
            var att = new List<uint>();
            for (int i = 0; i < Entities.Count; i++) if (Entities[i].Room == ri) att.Add((uint)i);
            rooms[ri].AttachedObjects = att.ToArray();
        }
        foreach (var p in Mlo.portals ?? Array.Empty<Cw.MCMloPortalDef>())
            if (p.AttachedObjects != null)
                p.AttachedObjects = p.AttachedObjects.Where(i => newOfOld.ContainsKey((int)i)).Select(i => (uint)newOfOld[(int)i]).ToArray();
        var data = Ytyp.Save();
        Modified = false;
        return data;
    }
}
