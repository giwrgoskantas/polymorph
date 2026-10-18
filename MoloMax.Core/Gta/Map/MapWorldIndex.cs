using System.Numerics;
using System.Text.Json;
using MoloMax.Core.Gta.Game;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Map;

/// <summary>One .ymap of the game: where its entities stand (for streaming the world around the camera).</summary>
public sealed class YmapInfo
{
    public string Name { get; set; }
    public string Path { get; set; }
    public float[] EMin { get; set; } = new float[3];
    public float[] EMax { get; set; } = new float[3];
    public uint ContentFlags { get; set; }
    public uint Flags { get; set; }
    public int Entities { get; set; }
    public int HdEntities { get; set; }
    /// <summary>Only far (LOD / SLOD) models: skipped when streaming the editable world.</summary>
    public bool LodOnly { get; set; }
    public bool Interior { get; set; }
    /// <summary>Entities per LOD level (0 HD, 1 LOD, 2 SLOD1, 3 SLOD2, 4 SLOD3, 5 orphan HD).</summary>
    public int[] Levels { get; set; } = new int[6];
    public Vector3 Min => new(EMin[0], EMin[1], EMin[2]);
    public Vector3 Max => new(EMax[0], EMax[1], EMax[2]);
    internal Cw.RpfFileEntry Entry;
    public override string ToString() => Name;
}

/// <summary>
/// Every .ymap of the install with its entity extents. Reading them all takes a minute the first time; the result is
/// kept in a small cache file next to the other Polymorph data, so the next sessions start at once.
/// </summary>
public sealed class MapWorldIndex
{
    public readonly List<YmapInfo> Ymaps = new();
    readonly Dictionary<string, YmapInfo> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly GameFiles game;

    MapWorldIndex(GameFiles g) { game = g; }

    public YmapInfo Find(string name) => byName.TryGetValue(System.IO.Path.GetFileNameWithoutExtension(name ?? ""), out var y) ? y : null;

    public static MapWorldIndex Build(GameFiles g, string cacheFile, Action<string, float> progress = null)
    {
        var idx = new MapWorldIndex(g);
        var entries = new List<Cw.RpfFileEntry>();
        foreach (var rpf in g.Rpf.AllRpfs)
        {
            if (rpf.AllEntries == null) continue;
            foreach (var e in rpf.AllEntries) if (e is Cw.RpfFileEntry fe && (fe.NameLower ?? "").EndsWith(".ymap")) entries.Add(fe);
        }
        // later archives (patches, DLC updates) win over earlier ones with the same name
        var latest = new Dictionary<string, Cw.RpfFileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) latest[System.IO.Path.GetFileNameWithoutExtension(e.NameLower)] = e;

        Dictionary<string, YmapInfo> cached = null;
        string stamp = $"v2|{g.Folder}|{latest.Count}|{(g.Folder != null && File.Exists(System.IO.Path.Combine(g.Folder, g.Gen9 ? "GTA5_Enhanced.exe" : "GTA5.exe")) ? File.GetLastWriteTimeUtc(System.IO.Path.Combine(g.Folder, g.Gen9 ? "GTA5_Enhanced.exe" : "GTA5.exe")).Ticks : 0)}";
        try
        {
            if (cacheFile != null && File.Exists(cacheFile))
            {
                var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cacheFile));
                if (c?.Stamp == stamp && c.Ymaps != null) cached = c.Ymaps.ToDictionary(y => y.Name, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { cached = null; }

        int done = 0;
        foreach (var (name, fe) in latest)
        {
            YmapInfo info;
            if (cached != null && cached.TryGetValue(name, out var ci)) info = ci;
            else
            {
                info = new YmapInfo { Name = name, Path = fe.Path };
                try
                {
                    var y = Cw.RpfFile.GetFile<Cw.YmapFile>(fe);
                    if (y != null)
                    {
                        var md = y._CMapData;
                        info.EMin = new[] { md.entitiesExtentsMin.X, md.entitiesExtentsMin.Y, md.entitiesExtentsMin.Z };
                        info.EMax = new[] { md.entitiesExtentsMax.X, md.entitiesExtentsMax.Y, md.entitiesExtentsMax.Z };
                        info.ContentFlags = md.contentFlags; info.Flags = md.flags;
                        var ents = y.AllEntities ?? Array.Empty<Cw.YmapEntityDef>();
                        info.Entities = ents.Length;
                        info.HdEntities = ents.Count(e => e != null && e.MloParent == null && (e._CEntityDef.lodLevel == Cw.rage__eLodType.LODTYPES_DEPTH_HD || e._CEntityDef.lodLevel == Cw.rage__eLodType.LODTYPES_DEPTH_ORPHANHD));
                        info.LodOnly = info.Entities > 0 && info.HdEntities == 0;
                        info.Interior = (md.contentFlags & 8) != 0 || ents.Any(e => e != null && e.IsMlo);
                        foreach (var e in ents) if (e != null && e.MloParent == null) { int l = (int)e._CEntityDef.lodLevel; if (l >= 0 && l < 6) info.Levels[l]++; }
                    }
                }
                catch { }
            }
            info.Entry = fe;
            idx.Ymaps.Add(info);
            idx.byName[name] = info;
            if (++done % 250 == 0) progress?.Invoke($"Maps {done:N0} / {latest.Count:N0}", done / (float)latest.Count);
        }
        if (cached == null && cacheFile != null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cacheFile));
                File.WriteAllText(cacheFile, JsonSerializer.Serialize(new CacheFile { Stamp = stamp, Ymaps = idx.Ymaps }));
            }
            catch { }
        }
        progress?.Invoke($"{idx.Ymaps.Count:N0} maps indexed", 1);
        return idx;
    }

    sealed class CacheFile { public string Stamp { get; set; } public List<YmapInfo> Ymaps { get; set; } }

    /// <summary>Maps with editable (HD) entities whose box comes within <paramref name="radius"/> of a point.</summary>
    public IEnumerable<YmapInfo> Near(Vector3 p, float radius)
    {
        foreach (var y in Ymaps)
        {
            if (y.LodOnly || y.HdEntities == 0) continue;
            var mn = y.Min; var mx = y.Max;
            var c = Vector3.Clamp(p, mn, mx);
            var d = new Vector2(c.X - p.X, c.Y - p.Y).Length(); // on the ground plane: heights don't matter for streaming
            if (d <= radius) yield return y;
        }
    }

    /// <summary>Opens a game map for editing; archetype names come from the model index.</summary>
    public MapDocument Open(YmapInfo y)
    {
        var f = Cw.RpfFile.GetFile<Cw.YmapFile>(y.Entry) ?? throw new InvalidDataException("Could not read " + y.Name);
        var d = MapDocument.FromYmap(f, y.Name, h => game.FindModel(h)?.Name);
        d.FromGame = true;
        return d;
    }

    /// <summary>Raw bytes of a game map (for "save a copy").</summary>
    public byte[] Bytes(YmapInfo y)
    {
        var raw = y.Entry.File.ExtractFile(y.Entry);
        return y.Entry is Cw.RpfResourceFileEntry rr ? Cw.ResourceBuilder.AddResourceHeader(rr, Cw.ResourceBuilder.Compress(raw)) : raw;
    }
}
