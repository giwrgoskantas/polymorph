using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace MoloMax.Core.Gta.Export;

/// <summary>One parameter of a GTA shader as listed in Shaders.xml.</summary>
public sealed record ShaderParamDef(string Name, string Type, Vector4 Default, int Uv)
{
    public bool IsTexture => Type == "Texture";
    /// <summary>Number of float4 slots (arrays use several).</summary>
    public int Count { get; init; } = 1;
}

/// <summary>A GTA shader: its .sps presets (with render buckets), vertex layout and parameters.</summary>
public sealed class ShaderDef
{
    public string Name;
    public List<(string File, int Bucket)> Files = new();
    /// <summary>Vertex layout of the static (non-skinned) variant, e.g. Position Normal Colour0 TexCoord0 Tangent.</summary>
    public List<string> Layout = new();
    public List<ShaderParamDef> Params = new();
}

/// <summary>
/// GTA V shader definitions (szio Shaders.xml, MIT, Sollumz contributors). Exported materials list every
/// parameter of their shader in order, with the vertex layout the shader expects — like Sollumz does.
/// </summary>
public static class ShaderDb
{
    static Dictionary<string, ShaderDef> byName;
    static Dictionary<string, (ShaderDef Def, int Bucket)> byFile;

    static void Load()
    {
        if (byName != null) return;
        byName = new(StringComparer.OrdinalIgnoreCase);
        byFile = new(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(ShaderDb).Assembly.GetManifestResourceStream("Shaders.xml")
            ?? throw new InvalidOperationException("Shaders.xml is missing from MoloMax.Core.");
        var doc = XDocument.Load(s);
        foreach (var it in doc.Root.Elements("Item"))
        {
            var d = new ShaderDef { Name = (string)it.Element("Name") };
            foreach (var f in it.Element("FileName")?.Elements("Item") ?? Enumerable.Empty<XElement>())
                d.Files.Add((f.Value.Trim(), int.Parse((string)f.Attribute("bucket") ?? "0", CultureInfo.InvariantCulture)));
            var layouts = it.Element("Layout")?.Elements("Item").ToList() ?? new();
            var lay = layouts.FirstOrDefault(l => !l.Elements().Any(e => e.Name.LocalName.StartsWith("Blend"))) ?? layouts.FirstOrDefault();
            if (lay != null) d.Layout = lay.Elements().Select(e => e.Name.LocalName).ToList();
            foreach (var p in it.Element("Parameters")?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                string type = (string)p.Attribute("type");
                float F(string a) => float.TryParse((string)p.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
                int count = int.TryParse((string)p.Attribute("count"), out var c) ? c : 1;
                if (type == "float4x4") count = 4;
                var def = new Vector4(F("x"), F("y"), F("z"), F("w"));
                d.Params.Add(new ShaderParamDef((string)p.Attribute("name"), type, def, int.TryParse((string)p.Attribute("uv"), out var uv) ? uv : 0) { Count = count });
            }
            byName[d.Name] = d;
            foreach (var (file, bucket) in d.Files)
            {
                var key = Path.GetFileNameWithoutExtension(file);
                byFile.TryAdd(key, (d, bucket));
            }
        }
    }

    public static ShaderDef Get(string shaderName) { Load(); return byName.TryGetValue(shaderName, out var d) ? d : null; }

    /// <summary>Resolves a preset (.sps name without extension, e.g. "normal_spec_cutout") to its shader and render bucket.</summary>
    public static (ShaderDef Def, int Bucket)? FromPreset(string preset)
    {
        Load();
        if (preset == null) return null;
        preset = Path.GetFileNameWithoutExtension(preset);
        if (byFile.TryGetValue(preset, out var r)) return r;
        if (byName.TryGetValue(preset, out var d)) return (d, d.Files.Count > 0 ? d.Files[0].Bucket : 0);
        return null;
    }

    public static IEnumerable<string> AllPresets() { Load(); return byFile.Keys.OrderBy(k => k); }
}
