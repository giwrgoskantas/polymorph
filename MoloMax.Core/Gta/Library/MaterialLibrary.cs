using System.Numerics;
using System.Text.Json;
using MoloMax.Core.Gta.Export;

namespace MoloMax.Core.Gta.Library;

/// <summary>One material of the Molo texture library (MoloTextureLibrary/data/materials.json): a GTA shader and its textures.</summary>
public sealed class LibMaterial
{
    public string Id, Shader, Diffuse, Bump, Spec, Height, Detail;
    public Dictionary<string, float[]> Params = new();
    public float Floor, Wall, Ceiling, Other, Texel;
    public int Uses;
    public string[] Mlos = Array.Empty<string>();
    public string Category;
    public string Name => Diffuse ?? Id;
    public bool IsDecal => Shader.Contains("decal") || Shader.Contains("cutout");

    /// <summary>Metres covered by one texture repeat (from the library's texel density), clamped to sensible values.</summary>
    public float TileMetres => Texel > 0.01f ? Math.Clamp(1f / Texel, 0.25f, 8f) : 2f;

    public float Param(string name, float fallback) => Params.TryGetValue(name.ToLowerInvariant(), out var v) && v.Length > 0 ? v[0] : fallback;
}

/// <summary>
/// The Molo texture library (all GTA V interior materials extracted with molo_material): loads the catalogue,
/// sorts materials into categories, finds the DDS / preview files, and turns an entry into a MoloMax GtaMaterial.
/// </summary>
public sealed class MaterialLibrary
{
    public string Folder { get; private set; }
    public readonly List<LibMaterial> Materials = new();
    public static readonly string[] Categories = { "All", "Floors", "Walls", "Ceilings", "Decals", "Dirt & stains", "Metal", "Wood", "Concrete & stone", "Tiles", "Fabric & carpet", "Glass", "Emissive & signs", "Other" };

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MoloTextureLibrary");

    public static MaterialLibrary Load(string folder)
    {
        var lib = new MaterialLibrary { Folder = folder };
        var json = Path.Combine(folder, "data", "materials.json");
        if (!File.Exists(json)) throw new FileNotFoundException("materials.json not found (expected in <library>\\data).", json);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var m = new LibMaterial { Id = Str(e, "id"), Shader = Str(e, "shader") ?? "default" };
            if (e.TryGetProperty("tex", out var tex))
                foreach (var t in tex.EnumerateObject())
                {
                    var n = Str(t.Value, "n");
                    if (t.Value.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) continue;
                    switch (t.Name.ToLowerInvariant())
                    {
                        case "diffusesampler": case "texturesampler": m.Diffuse ??= n; break;
                        case "bumpsampler": m.Bump = n; break;
                        case "specsampler": m.Spec = n; break;
                        case "heightsampler": m.Height = n; break;
                        case "detailsampler": m.Detail = n; break;
                    }
                }
            if (e.TryGetProperty("par", out var par))
                foreach (var p in par.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array) m.Params[p.Name.ToLowerInvariant()] = p.Value.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            if (e.TryGetProperty("surf", out var s))
            {
                m.Floor = F(s, "floor"); m.Wall = F(s, "wall"); m.Ceiling = F(s, "ceil"); m.Other = F(s, "other");
            }
            m.Texel = F(e, "texel");
            m.Uses = e.TryGetProperty("uses", out var u) && u.TryGetInt32(out var ui) ? ui : 0;
            if (e.TryGetProperty("mlos", out var ml) && ml.ValueKind == JsonValueKind.Array) m.Mlos = ml.EnumerateArray().Select(x => x.GetString()).ToArray();
            m.Category = Categorise(m);
            lib.Materials.Add(m);
        }
        return lib;
    }

    static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static float F(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0;

    static readonly (string Cat, string[] Keys)[] Keywords =
    {
        ("Glass", new[] { "glass", "window", "mirror" }),
        ("Emissive & signs", new[] { "emissive", "neon", "sign", "light", "lamp", "screen", "poster", "advert" }),
        ("Dirt & stains", new[] { "dirt", "stain", "grime", "burnt", "shadow", "damage", "crack", "leak", "puddle", "rust" }),
        ("Tiles", new[] { "tile", "lino", "mosaic", "checker" }),
        ("Wood", new[] { "wood", "plank", "parquet", "timber", "oak", "pine", "board" }),
        ("Metal", new[] { "metal", "steel", "alum", "chrome", "iron", "grate", "grid", "duct", "pipe", "beam", "girder", "plate" }),
        ("Concrete & stone", new[] { "concrete", "cement", "stone", "brick", "marble", "rock", "plaster", "render", "asphalt" }),
        ("Fabric & carpet", new[] { "carpet", "fabric", "cloth", "rug", "curtain", "leather", "sofa", "velvet" }),
    };

    static string Categorise(LibMaterial m)
    {
        var s = m.Shader;
        if (s.StartsWith("glass") || s.Contains("mirror")) return "Glass";
        if (s.Contains("emissive")) return "Emissive & signs";
        var name = (m.Diffuse ?? m.Id ?? "").ToLowerInvariant();
        if (m.IsDecal)
        {
            foreach (var k in Keywords[2].Keys) if (name.Contains(k)) return "Dirt & stains";
            return "Decals";
        }
        foreach (var (cat, keys) in Keywords) foreach (var k in keys) if (name.Contains(k)) return cat;
        float tot = m.Floor + m.Wall + m.Ceiling;
        if (tot > 0)
        {
            if (m.Floor >= m.Wall && m.Floor >= m.Ceiling) return "Floors";
            if (m.Ceiling > m.Wall) return "Ceilings";
            return "Walls";
        }
        return "Other";
    }

    /// <summary>True when the material fits a category; Floors / Walls / Ceilings also take materials used mostly there.</summary>
    public static bool InCategory(LibMaterial m, string cat)
    {
        if (cat == "All" || m.Category == cat) return true;
        float tot = m.Floor + m.Wall + m.Ceiling;
        if (tot <= 0 || m.IsDecal) return false;
        return cat switch
        {
            "Floors" => m.Floor / tot > 0.6f,
            "Walls" => m.Wall / tot > 0.6f,
            "Ceilings" => m.Ceiling / tot > 0.6f,
            _ => false,
        };
    }

    /// <summary>Shaders used in the library, most used first, as (name, material count).</summary>
    public IReadOnlyList<(string Shader, int Count)> Shaders(bool decals) =>
        Materials.Where(m => m.IsDecal == decals).GroupBy(m => m.Shader).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();

    public IEnumerable<LibMaterial> Search(string category, string text, string shader) =>
        Search(category, text).Where(m => string.IsNullOrEmpty(shader) || m.Shader == shader);

    public IEnumerable<LibMaterial> Search(string category, string text)
    {
        var words = (text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Materials.Where(m => InCategory(m, category) &&
                                    words.All(w => (m.Id ?? "").ToLowerInvariant().Contains(w) || m.Shader.Contains(w) || m.Mlos.Any(x => x.Contains(w))))
                        .OrderByDescending(m => m.Uses);
    }

    public string TexturePath(string name) => name == null ? null : FirstExisting(Path.Combine(Folder, "textures", name + ".dds"));
    public string PreviewPath(LibMaterial m)
    {
        var n = m.Diffuse ?? m.Bump; if (n == null) return null;
        return FirstExisting(Path.Combine(Folder, "previews", n + ".jpg"), Path.Combine(Folder, "previews", n + ".png"), Path.Combine(Folder, "textures", n + ".dds"));
    }
    static string FirstExisting(params string[] p) => p.FirstOrDefault(File.Exists);

    /// <summary>A MoloMax material with the library entry's shader, texture files and parameters (exports as is).</summary>
    public GtaMaterial ToGtaMaterial(LibMaterial m, int id)
    {
        var shader = ShaderDb.FromPreset(m.Shader) != null ? m.Shader : (m.IsDecal ? "decal" : "normal_spec");
        var g = GtaMaterial.CreateDefault(id, Gta.Export.TextureBank.SafeName(m.Name), new Vector3(0.7f));
        g.Shader = shader;
        g.DiffuseTexture = TexturePath(m.Diffuse);
        g.BumpTexture = TexturePath(m.Bump);
        g.SpecTexture = TexturePath(m.Spec);
        g.DetailTexture = TexturePath(m.Detail);
        g.Bumpiness = m.Param("bumpiness", g.Bumpiness);
        g.SpecularIntensity = m.Param("specularintensitymult", g.SpecularIntensity);
        g.SpecularFalloff = m.Param("specularfalloffmult", g.SpecularFalloff);
        g.SpecularFresnel = m.Param("specularfresnel", g.SpecularFresnel);
        g.EmissiveMultiplier = m.Param("emissivemultiplier", g.EmissiveMultiplier);
        if (m.Params.TryGetValue("specmapintmask", out var mk) && mk.Length >= 3) g.SpecMapIntMask = new Vector3(mk[0], mk[1], mk[2]);
        return g;
    }
}

/// <summary>DDS → RGBA (through CodeWalker's decoder), with a small cache. Also reads PNG / JPG via a provided loader.</summary>
public static class TextureReader
{
    static readonly Dictionary<string, RgbaImage> cache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Set by the app (System.Drawing) to read PNG / JPG / BMP.</summary>
    public static Func<string, RgbaImage> BitmapLoader;

    public static RgbaImage Load(string path, int maxSize = 1024)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        lock (cache)
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
        }
        RgbaImage img;
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            var tex = CodeWalker.Utils.DDSIO.GetTexture(File.ReadAllBytes(path));
            int mip = 0, w = tex.Width, h = tex.Height;
            while (Math.Max(w, h) > maxSize && mip + 1 < tex.Levels) { mip++; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
            var px = CodeWalker.Utils.DDSIO.GetPixels(tex, mip);
            img = new RgbaImage(w, h);
            int n = Math.Min(px.Length / 4, w * h);
            for (int i = 0; i < n; i++)
            {
                // CodeWalker returns BGRA
                img.Pixels[i * 4] = px[i * 4 + 2]; img.Pixels[i * 4 + 1] = px[i * 4 + 1]; img.Pixels[i * 4 + 2] = px[i * 4]; img.Pixels[i * 4 + 3] = px[i * 4 + 3];
            }
        }
        else img = BitmapLoader?.Invoke(path);
        if (img == null) return null;
        lock (cache) { if (cache.Count > 64) cache.Clear(); cache[path] = img; }
        return img;
    }

    /// <summary>Bilinear sample with wrap, returns 0..1 RGBA.</summary>
    public static Vector4 Sample(RgbaImage img, float u, float v, bool wrap = true)
    {
        if (img == null) return Vector4.One;
        float x = u * img.Width - 0.5f, y = v * img.Height - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 P(int px, int py)
        {
            if (wrap) { px = ((px % img.Width) + img.Width) % img.Width; py = ((py % img.Height) + img.Height) % img.Height; }
            else { if (px < 0 || py < 0 || px >= img.Width || py >= img.Height) return Vector4.Zero; }
            int o = (px + py * img.Width) * 4;
            return new Vector4(img.Pixels[o], img.Pixels[o + 1], img.Pixels[o + 2], img.Pixels[o + 3]) / 255f;
        }
        return Vector4.Lerp(Vector4.Lerp(P(x0, y0), P(x0 + 1, y0), fx), Vector4.Lerp(P(x0, y0 + 1), P(x0 + 1, y0 + 1), fx), fy);
    }
}
