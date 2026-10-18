using System.Numerics;
using Cw = CodeWalker.GameFiles;
using SDX = SharpDX;

namespace MoloMax.Core.Gta.Map;

/// <summary>One placed archetype of a .ymap (CEntityDef), in world space with its real rotation.</summary>
public sealed class MapEntity
{
    public uint ArchetypeHash;
    /// <summary>Archetype name when known (game index, JenkIndex or set by the editor).</summary>
    public string Archetype;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public Vector3 Scale = Vector3.One;
    public float LodDist = 200;
    public uint Flags = 32;
    public int LodLevel = 5; // LODTYPES_DEPTH_ORPHANHD
    /// <summary>Interior placement (CMloInstanceDef): shown, moved as a whole, its rooms are not edited here.</summary>
    public bool IsMlo;
    public uint Guid;
    /// <summary>LOD tree (read from the file): place in the map's entity list, parent's place in the parent map (-1 none),
    /// distance under which the children replace this entity, number of children.</summary>
    public int Index = -1, ParentIndex = -1, NumChildren;
    public float ChildLodDist;
    internal Cw.YmapEntityDef Def;

    /// <summary>HD (or orphan HD) entity: the one the player sees up close. LOD / SLOD entities are the far versions.</summary>
    public bool IsHd => LodLevel is 0 or 5;
    public string DisplayName => Archetype ?? $"0x{ArchetypeHash:X8}";
}

/// <summary>
/// A .ymap opened for editing: a game map, a file from disk or a new one. Everything CodeWalker reads is kept
/// (LOD links, car generators, occluders, lights...), only the entities are edited, so saving a game map gives back
/// the same map with your changes; FiveM streams it in place of the original when it has the same name.
/// </summary>
public sealed class MapDocument
{
    public string Name;
    public Cw.YmapFile Ymap;
    public readonly List<MapEntity> Entities = new();
    public bool Modified;
    /// <summary>Came from the game archives (else from disk or new).</summary>
    public bool FromGame;
    public string FilePath;
    public Vector3 EntitiesMin, EntitiesMax, StreamingMin, StreamingMax;
    public uint Flags, ContentFlags;
    public string Parent;
    /// <summary>Name hashes of this map and of its parent map (LOD tree links between maps).</summary>
    public uint NameHash, ParentHash;

    static Vector3 V(SDX.Vector3 v) => new(v.X, v.Y, v.Z);
    static SDX.Vector3 S(Vector3 v) => new(v.X, v.Y, v.Z);
    static Quaternion Q(SDX.Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    public static MapDocument New(string name)
    {
        name = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        var y = new Cw.YmapFile();
        y.RpfFileEntry = new Cw.RpfResourceFileEntry { Name = name + ".ymap" };
        y.Name = name + ".ymap";
        Cw.JenkIndex.Ensure(name);
        y._CMapData.name = Cw.JenkHash.GenHash(name);
        y._CMapData.parent = new Cw.MetaHash(0);
        y._CMapData.contentFlags = 1;
        y.AllEntities = Array.Empty<Cw.YmapEntityDef>();
        y.RootEntities = Array.Empty<Cw.YmapEntityDef>();
        y.Loaded = true;
        return new MapDocument { Name = name, Ymap = y, Modified = true };
    }

    public static MapDocument Load(byte[] data, string name, Func<uint, string> names = null)
    {
        var y = new Cw.YmapFile();
        y.RpfFileEntry = new Cw.RpfResourceFileEntry { Name = Path.GetFileName(name) };
        y.Load(data);
        return FromYmap(y, name, names);
    }

    public static MapDocument FromYmap(Cw.YmapFile y, string name, Func<uint, string> names = null)
    {
        var d = new MapDocument { Name = Path.GetFileNameWithoutExtension(name).ToLowerInvariant(), Ymap = y };
        var md = y._CMapData;
        d.EntitiesMin = V(md.entitiesExtentsMin); d.EntitiesMax = V(md.entitiesExtentsMax);
        d.StreamingMin = V(md.streamingExtentsMin); d.StreamingMax = V(md.streamingExtentsMax);
        d.Flags = md.flags; d.ContentFlags = md.contentFlags;
        d.NameHash = md.name.Hash; d.ParentHash = md.parent.Hash;
        d.Parent = md.parent.Hash != 0 ? (names?.Invoke(md.parent.Hash) ?? Cw.JenkIndex.TryGetString(md.parent.Hash)) : null;
        if (y.AllEntities != null)
            for (int ei = 0; ei < y.AllEntities.Length; ei++)
            {
                var e = y.AllEntities[ei];
                if (e == null || e.MloParent != null) continue;
                var c = e._CEntityDef;
                uint h = c.archetypeName.Hash;
                string n = names?.Invoke(h);
                if (string.IsNullOrEmpty(n)) { var s = Cw.JenkIndex.TryGetString(h); n = string.IsNullOrEmpty(s) ? null : s; }
                d.Entities.Add(new MapEntity
                {
                    ArchetypeHash = h, Archetype = n,
                    Position = V(e.Position), Rotation = Quaternion.Normalize(Q(e.Orientation)), Scale = V(e.Scale),
                    LodDist = c.lodDist, Flags = c.flags, LodLevel = (int)c.lodLevel, IsMlo = e.IsMlo, Guid = c.guid, Def = e,
                    Index = ei, ParentIndex = c.parentIndex, NumChildren = (int)c.numChildren, ChildLodDist = c.childLodDist,
                });
            }
        return d;
    }

    /// <summary>Adds a new entity (an archetype of the game or of the project).</summary>
    public MapEntity Add(string archetype, Vector3 position, Quaternion rotation, Vector3 scale, float lodDist = 200)
    {
        archetype = archetype.ToLowerInvariant();
        Cw.JenkIndex.Ensure(archetype);
        var e = new MapEntity
        {
            Archetype = archetype, ArchetypeHash = Cw.JenkHash.GenHash(archetype), Position = position, Rotation = Quaternion.Normalize(rotation),
            Scale = scale, LodDist = lodDist, Guid = NewGuid(archetype, position),
        };
        Entities.Add(e);
        Modified = true;
        return e;
    }

    public void Remove(MapEntity e)
    {
        if (!Entities.Remove(e)) return;
        if (e.Def != null && Ymap.AllEntities != null && Ymap.AllEntities.Contains(e.Def)) Ymap.RemoveEntity(e.Def);
        Modified = true;
    }

    /// <summary>Archetype name hash (GTA joaat), remembered so the name shows when read back.</summary>
    public static uint Hash(string name)
    {
        name = (name ?? "").ToLowerInvariant();
        Cw.JenkIndex.Ensure(name);
        return Cw.JenkHash.GenHash(name);
    }

    static uint NewGuid(string name, Vector3 p) => Cw.JenkHash.GenHash($"{name}_{p.X:0.00}_{p.Y:0.00}_{p.Z:0.00}_{Environment.TickCount64}");

    /// <summary>
    /// Writes the entities back into the CodeWalker map and returns the .ymap bytes.
    /// <paramref name="archetypeBounds"/> gives the local box of an archetype (for the extents); unknown ones use a 1 m box.
    /// </summary>
    public byte[] Save(Func<uint, (Vector3 Min, Vector3 Max)?> archetypeBounds = null)
    {
        foreach (var e in Entities)
        {
            if (e.Def == null)
            {
                var c = new Cw.CEntityDef
                {
                    archetypeName = new Cw.MetaHash(e.ArchetypeHash), flags = e.Flags, guid = e.Guid, parentIndex = -1,
                    lodDist = e.LodDist, childLodDist = 0, lodLevel = (Cw.rage__eLodType)e.LodLevel, numChildren = 0,
                    priorityLevel = Cw.rage__ePriorityLevel.PRI_REQUIRED, ambientOcclusionMultiplier = 255, artificialAmbientOcclusion = 255,
                    scaleXY = 1, scaleZ = 1, rotation = new SDX.Vector4(0, 0, 0, 1), position = S(e.Position),
                };
                e.Def = new Cw.YmapEntityDef(Ymap, 0, ref c);
                Ymap.AddEntity(e.Def);
            }
            var d = e.Def;
            var c2 = d._CEntityDef;
            c2.position = S(e.Position);
            var q = Quaternion.Normalize(e.Rotation);
            // CMloInstanceDef rotations are stored as is, CEntityDef ones inverted
            var stored = e.IsMlo ? q : Quaternion.Conjugate(q);
            c2.rotation = new SDX.Vector4(stored.X, stored.Y, stored.Z, stored.W);
            c2.scaleXY = e.Scale.X; c2.scaleZ = e.Scale.Z;
            c2.lodDist = e.LodDist; c2.flags = e.Flags;
            d._CEntityDef = c2;
            d.Position = S(e.Position);
            d.Orientation = new SDX.Quaternion(q.X, q.Y, q.Z, q.W);
            d.Scale = new SDX.Vector3(e.Scale.X, e.Scale.X, e.Scale.Z);
        }
        // extents: entity boxes and their streaming distance
        var emn = new Vector3(float.MaxValue); var emx = new Vector3(float.MinValue);
        var smn = new Vector3(float.MaxValue); var smx = new Vector3(float.MinValue);
        foreach (var e in Entities)
        {
            var b = archetypeBounds?.Invoke(e.ArchetypeHash) ?? (new Vector3(-0.5f), new Vector3(0.5f));
            var (bmn, bmx) = WorldBox(b.Min * new Vector3(e.Scale.X, e.Scale.X, e.Scale.Z), b.Max * new Vector3(e.Scale.X, e.Scale.X, e.Scale.Z), e.Position, e.Rotation);
            emn = Vector3.Min(emn, bmn); emx = Vector3.Max(emx, bmx);
            smn = Vector3.Min(smn, bmn - new Vector3(e.LodDist)); smx = Vector3.Max(smx, bmx + new Vector3(e.LodDist));
        }
        if (Entities.Count == 0) { emn = emx = smn = smx = Vector3.Zero; }
        var md = Ymap._CMapData;
        md.entitiesExtentsMin = S(emn); md.entitiesExtentsMax = S(emx);
        md.streamingExtentsMin = S(smn); md.streamingExtentsMax = S(smx);
        if (md.contentFlags == 0) md.contentFlags = 1;
        Ymap._CMapData = md;
        EntitiesMin = emn; EntitiesMax = emx; StreamingMin = smn; StreamingMax = smx;
        var data = Ymap.Save();
        Modified = false;
        return data;
    }

    public static (Vector3 Min, Vector3 Max) WorldBox(Vector3 mn, Vector3 mx, Vector3 pos, Quaternion rot)
    {
        var a = new Vector3(float.MaxValue); var b = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) != 0 ? mx.X : mn.X, (i & 2) != 0 ? mx.Y : mn.Y, (i & 4) != 0 ? mx.Z : mn.Z);
            var w = Vector3.Transform(c, rot) + pos;
            a = Vector3.Min(a, w); b = Vector3.Max(b, w);
        }
        return (a, b);
    }
}
