using System.Numerics;
using CodeWalker.GameFiles;
using MoloMax.Core.Gta.Export;

namespace MoloMax.Core.Gta.Import;

/// <summary>What a .ytyp interior contains, ready to rebuild in the scene.</summary>
public sealed class MloImport
{
    public sealed record Room(string Name, Vector3 Min, Vector3 Max, uint TimecycleHash, uint Flags);
    public sealed record Portal(Vector3[] Corners, uint Flags, int From, int To);
    public sealed record Entity(string Archetype, string File, Vector3 Position, Quaternion Rotation, Vector3 Scale, string EntitySet, uint Flags, uint Hash = 0);
    public string Name;
    public List<Room> Rooms = new();
    public List<Portal> Portals = new();
    public List<Entity> Entities = new();
    public List<string> Collisions = new();
    public List<string> Warnings = new();
}

/// <summary>
/// Reads an interior back from its .ytyp (rooms, portals, entities, entity sets) and finds the .ydr / .yft / .ybn
/// files next to it, so a whole MLO can be re-opened and edited.
/// </summary>
public static class YtypImporter
{
    static bool InsideRooms(string ybn, MloImport m)
    {
        var rooms = m.Rooms.Skip(1).ToList(); // 0 = limbo
        if (rooms.Count == 0) return false;
        try
        {
            if (new FileInfo(ybn).Length > 8_000_000) return false;
            var y = new YbnFile(); y.Load(File.ReadAllBytes(ybn));
            if (y.Bounds == null) return false;
            var bmin = new Vector3(y.Bounds.BoxMin.X, y.Bounds.BoxMin.Y, y.Bounds.BoxMin.Z); var bmax = new Vector3(y.Bounds.BoxMax.X, y.Bounds.BoxMax.Y, y.Bounds.BoxMax.Z);
            var rmin = rooms.Select(r => r.Min).Aggregate(Vector3.Min) - new Vector3(2); var rmax = rooms.Select(r => r.Max).Aggregate(Vector3.Max) + new Vector3(2);
            return bmin.X >= rmin.X && bmin.Y >= rmin.Y && bmin.Z >= rmin.Z && bmax.X <= rmax.X && bmax.Y <= rmax.Y && bmax.Z <= rmax.Z;
        }
        catch { return false; }
    }

    public static List<MloImport> Read(string ytypPath)
    {
        var y = new YtypFile();
        y.Load(File.ReadAllBytes(ytypPath));
        // model files around: the ytyp folder and its siblings (stream/mlo, stream/exterior)
        var dir = Path.GetDirectoryName(Path.GetFullPath(ytypPath))!;
        var roots = new List<string> { dir };
        var parent = Path.GetDirectoryName(dir);
        if (parent != null) roots.AddRange(Directory.GetDirectories(parent));
        var byHash = new Dictionary<uint, string>();
        var cols = new List<string>();
        foreach (var r in roots.Distinct())
            foreach (var f in Directory.EnumerateFiles(r))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                var n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (ext is ".ydr" or ".yft") byHash.TryAdd(JenkHash.GenHash(n), f);
                else if (ext == ".ybn") cols.Add(f);
            }
        var res = new List<MloImport>();
        foreach (var a in y.AllArchetypes ?? Array.Empty<Archetype>())
        {
            if (a is not MloArchetype mlo) continue;
            // game files only keep the hash of the name ("3425927872"): use the file name instead
            var name = a.Name;
            if (string.IsNullOrEmpty(name) || name.All(char.IsDigit) || name.StartsWith("0x"))
                name = Path.GetFileNameWithoutExtension(ytypPath) + (res.Count > 0 ? "_" + (res.Count + 1) : "");
            var m = new MloImport { Name = name };
            foreach (var r in mlo.rooms ?? Array.Empty<MCMloRoomDef>())
                m.Rooms.Add(new MloImport.Room(r.RoomName ?? "room", V(r.BBMin), V(r.BBMax), r.Data.timecycleName.Hash, r.Data.flags));
            foreach (var p in mlo.portals ?? Array.Empty<MCMloPortalDef>())
                m.Portals.Add(new MloImport.Portal((p.Corners ?? Array.Empty<SharpDX.Vector4>()).Select(c => new Vector3(c.X, c.Y, c.Z)).ToArray(), p.Data.flags, (int)p.Data.roomFrom, (int)p.Data.roomTo));
            void Add(MCEntityDef e, string set)
            {
                var d = e.Data;
                uint h = d.archetypeName.Hash;
                byHash.TryGetValue(h, out var file);
                var r = d.rotation;
                // interior entities store the conjugate rotation (like the exporter writes it)
                var q = new Quaternion(r.X, r.Y, r.Z, r.W);
                q = q.IsIdentity ? Quaternion.Identity : Quaternion.Conjugate(Quaternion.Normalize(q));
                m.Entities.Add(new MloImport.Entity(file != null ? Path.GetFileNameWithoutExtension(file) : (e.Name ?? d.archetypeName.Hex), file,
                    V(d.position), q, new Vector3(d.scaleXY, d.scaleXY, d.scaleZ), set, d.flags, h));
                if (file == null) m.Warnings.Add($"Model {e.Name ?? d.archetypeName.Hex} not found next to the .ytyp (placeholder box).");
            }
            foreach (var e in mlo.entities ?? Array.Empty<MCEntityDef>()) Add(e, null);
            foreach (var s in mlo.entitySets ?? Array.Empty<MCMloEntitySet>())
                foreach (var e in s.Entities ?? Array.Empty<MCEntityDef>()) Add(e, s.Name ?? s.Data.name.ToString());
            // collision: .ybn named like the .ytyp, else one lying inside the rooms (not any .ybn found around)
            var named = cols.Where(c => Path.GetFileNameWithoutExtension(c).StartsWith(Path.GetFileNameWithoutExtension(ytypPath), StringComparison.OrdinalIgnoreCase)).ToList();
            m.Collisions.AddRange(named.Count > 0 ? named : cols.Take(30).Where(c => InsideRooms(c, m)));
            res.Add(m);
        }
        return res;
    }

    static Vector3 V(SharpDX.Vector3 v) => new(v.X, v.Y, v.Z);
}
