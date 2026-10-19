using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Map;

/// <summary>
/// A FiveM resource's stream folder read like a CodeWalker project: its .ymap files, its models (.ydr / .yft / .ydd) and
/// what its .ytyp files say about them (names, drawable dictionaries, interiors).
/// </summary>
public sealed class StreamFolder
{
    public string Folder;
    public readonly List<string> Ymaps = new();
    /// <summary>Archetype hash -> its .ydr / .yft in the folder.</summary>
    public readonly Dictionary<uint, string> Models = new();
    /// <summary>Drawable dictionary hash -> its .ydd in the folder.</summary>
    public readonly Dictionary<uint, string> Dictionaries = new();
    /// <summary>Archetype hash -> the drawable dictionary holding its model (from the .ytyp).</summary>
    public readonly Dictionary<uint, uint> DictionaryOf = new();
    /// <summary>Interior (MLO archetype) hash -> the .ytyp holding it.</summary>
    public readonly Dictionary<uint, string> Interiors = new();
    public readonly Dictionary<uint, string> Names = new();
    public readonly List<string> Ytyps = new();
    public readonly List<string> Errors = new();

    public static StreamFolder Read(string folder)
    {
        var s = new StreamFolder { Folder = folder };
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            uint h = MapDocument.Hash(name);
            switch (ext)
            {
                case ".ymap": s.Ymaps.Add(f); break;
                case ".ydr": case ".yft":
                    if (name.EndsWith("_hi") && ext == ".yft") break; // the high LOD of a fragment: not an archetype
                    s.Models[h] = f; s.Names[h] = name; break;
                case ".ydd": s.Dictionaries[h] = f; break;
                case ".ytyp": s.Ytyps.Add(f); break;
            }
        }
        foreach (var f in s.Ytyps)
        {
            try
            {
                var y = new Cw.YtypFile();
                y.RpfFileEntry = new Cw.RpfResourceFileEntry { Name = Path.GetFileName(f) };
                y.Load(File.ReadAllBytes(f));
                foreach (var a in y.AllArchetypes ?? Array.Empty<Cw.Archetype>())
                {
                    if (a == null) continue;
                    uint h = a.Hash.Hash;
                    if (!string.IsNullOrEmpty(a.Name) && !a.Name.StartsWith("0x")) s.Names.TryAdd(h, a.Name.ToLowerInvariant());
                    if (a is Cw.MloArchetype) s.Interiors[h] = f;
                    else if (a.DrawableDict.Hash != 0 && !s.Models.ContainsKey(h)) s.DictionaryOf[h] = a.DrawableDict.Hash;
                }
            }
            catch (Exception e) { s.Errors.Add(Path.GetFileName(f) + ": " + e.Message); }
        }
        s.Ymaps.Sort(StringComparer.OrdinalIgnoreCase);
        return s;
    }

    /// <summary>The file holding this archetype's model in the folder (.ydr / .yft, else its .ydd), or null.</summary>
    public string ModelFile(uint hash) =>
        Models.TryGetValue(hash, out var f) ? f : DictionaryOf.TryGetValue(hash, out var d) && Dictionaries.TryGetValue(d, out var dd) ? dd : null;

    public bool Contains(string path) =>
        path != null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(Folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
