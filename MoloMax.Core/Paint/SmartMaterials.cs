using System.Numerics;
using System.Text.Json;

namespace MoloMax.Core.Paint;

/// <summary>Smart materials: ready-made layer stacks driven by the mesh maps (edges, cavities, AO, top / bottom).</summary>
public static class SmartMaterials
{
    /// <summary>General materials.</summary>
    public static readonly string[] Names =
    {
        "Painted metal, worn", "Rust", "Dirty concrete", "Varnished wood", "Plastic", "GTA wall paint", "Gold / brass", "Dust layer", "Grime layer", "Chrome",
    };

    /// <summary>Materials seen everywhere in Los Santos.</summary>
    public static readonly string[] GtaNames =
    {
        "Asphalt", "Dirty tiles", "Yellow industrial paint", "Wet concrete", "Pallet wood", "Street furniture plastic", "Galvanised metal", "Interior plaster",
        "Red brick wall", "Cracked mud", "Wood grain", "Stone paving",
    };

    public static IEnumerable<string> All => Names.Concat(GtaNames);

    static PaintLayer Fill(string name, Vector3 c, float rough, float metal = 0, MaskGen mask = MaskGen.None, float th = 0.5f, float contrast = 4, float breakup = 0, bool height = false, float h = 0.5f)
        => new() { Name = name, Kind = LayerKind.Fill, Color = c, Rough = rough, Metal = metal, UseMetal = true, UseRough = true, Mask = mask, MaskThreshold = th, MaskContrast = contrast, MaskBreakup = breakup, UseHeight = height, Height = h };

    public static List<PaintLayer> Make(string name, int seed)
    {
        var l = name switch
        {
            "Painted metal, worn" => new List<PaintLayer>
            {
                Fill("Paint", new Vector3(0.28f, 0.36f, 0.42f), 0.5f),
                Fill("Bare metal edges", new Vector3(0.62f, 0.62f, 0.64f), 0.3f, 1, MaskGen.Edges, 0.35f, 6, 0.5f),
                Fill("Dirt", new Vector3(0.16f, 0.13f, 0.1f), 0.9f, 0, MaskGen.AmbientOcclusion, 0.35f, 3, 0.4f),
            },
            "Rust" => new List<PaintLayer>
            {
                new() { Name = "Rust", Kind = LayerKind.Fill, Color = new Vector3(0.42f, 0.2f, 0.08f), Rough = 0.85f, ColorNoise = 0.35f, HeightNoise = 0.15f, UseHeight = true, NoiseScale = 14 },
                Fill("Dark rust", new Vector3(0.2f, 0.09f, 0.04f), 0.95f, 0, MaskGen.Noise, 0.55f, 5, 0.2f),
                Fill("Metal showing", new Vector3(0.5f, 0.48f, 0.46f), 0.4f, 1, MaskGen.Edges, 0.5f, 6, 0.6f),
            },
            "Dirty concrete" => new List<PaintLayer>
            {
                new() { Name = "Concrete", Kind = LayerKind.Fill, Color = new Vector3(0.55f, 0.54f, 0.5f), Rough = 0.92f, ColorNoise = 0.15f, HeightNoise = 0.1f, UseHeight = true, NoiseScale = 20 },
                Fill("Cavity dirt", new Vector3(0.22f, 0.2f, 0.17f), 0.95f, 0, MaskGen.AmbientOcclusion, 0.3f, 3, 0.3f),
                Inv(Fill("Ground stains", new Vector3(0.3f, 0.27f, 0.22f), 0.95f, 0, MaskGen.GradientZ, 0.25f, 4, 0.4f)),
            },
            "Varnished wood" => new List<PaintLayer>
            {
                new() { Name = "Wood", Kind = LayerKind.Fill, Color = new Vector3(0.45f, 0.27f, 0.13f), Rough = 0.35f, ColorNoise = 0.25f, NoiseScale = 6 },
                Fill("Worn varnish", new Vector3(0.55f, 0.38f, 0.22f), 0.8f, 0, MaskGen.Edges, 0.4f, 5, 0.5f),
                Fill("Grime", new Vector3(0.15f, 0.1f, 0.06f), 0.85f, 0, MaskGen.AmbientOcclusion, 0.4f, 3, 0.3f),
            },
            "Plastic" => new List<PaintLayer>
            {
                Fill("Plastic", new Vector3(0.82f, 0.82f, 0.8f), 0.4f),
                Fill("Dirt", new Vector3(0.3f, 0.28f, 0.25f), 0.8f, 0, MaskGen.AmbientOcclusion, 0.4f, 3, 0.3f),
            },
            "GTA wall paint" => new List<PaintLayer>
            {
                new() { Name = "Wall paint", Kind = LayerKind.Fill, Color = new Vector3(0.78f, 0.74f, 0.66f), Rough = 0.85f, ColorNoise = 0.06f, HeightNoise = 0.05f, UseHeight = true, NoiseScale = 30 },
                Fill("Dust on top", new Vector3(0.62f, 0.58f, 0.5f), 0.95f, 0, MaskGen.Top, 0.8f, 6, 0.3f),
                Inv(Fill("Dirt at the bottom", new Vector3(0.35f, 0.31f, 0.26f), 0.95f, 0, MaskGen.GradientZ, 0.15f, 5, 0.4f)),
            },
            "Gold / brass" => new List<PaintLayer>
            {
                Fill("Brass", new Vector3(0.78f, 0.6f, 0.25f), 0.25f, 1),
                Fill("Tarnish", new Vector3(0.25f, 0.22f, 0.12f), 0.7f, 0.5f, MaskGen.AmbientOcclusion, 0.35f, 3, 0.4f),
            },
            "Dust layer" => new List<PaintLayer> { Fill("Dust", new Vector3(0.6f, 0.56f, 0.48f), 0.95f, 0, MaskGen.Top, 0.7f, 5, 0.5f) },
            "Grime layer" => new List<PaintLayer> { Fill("Grime", new Vector3(0.12f, 0.1f, 0.08f), 0.9f, 0, MaskGen.AmbientOcclusion, 0.3f, 4, 0.5f) },
            "Chrome" => new List<PaintLayer> { Fill("Chrome", new Vector3(0.9f, 0.9f, 0.92f), 0.05f, 1) },

            // ---- GTA
            "Asphalt" => new List<PaintLayer>
            {
                new() { Name = "Asphalt", Kind = LayerKind.Fill, Color = new Vector3(0.16f, 0.16f, 0.17f), Rough = 0.93f, ColorNoise = 0.25f, HeightNoise = 0.25f, UseHeight = true, NoiseScale = 60 },
                Fill("Worn patches", new Vector3(0.27f, 0.26f, 0.25f), 0.9f, 0, MaskGen.Noise, 0.58f, 4, 0.3f),
                Fill("Oil stains", new Vector3(0.06f, 0.06f, 0.06f), 0.45f, 0, MaskGen.Noise, 0.68f, 6, 0.2f),
            },
            "Dirty tiles" => new List<PaintLayer>
            {
                new() { Name = "Tiles", Kind = LayerKind.Fill, Color = new Vector3(0.82f, 0.82f, 0.78f), Rough = 0.25f, ColorNoise = 0.04f, NoiseScale = 4 },
                Tiles(Fill("Grout", new Vector3(0.32f, 0.3f, 0.27f), 0.9f, 0, MaskGen.Tiles, 0.5f, 6, 0, true, 0.3f), 4),
                Fill("Grime in corners", new Vector3(0.25f, 0.22f, 0.17f), 0.8f, 0, MaskGen.AmbientOcclusion, 0.35f, 3, 0.4f),
            },
            "Yellow industrial paint" => new List<PaintLayer>
            {
                Fill("Safety yellow", new Vector3(0.85f, 0.65f, 0.08f), 0.55f),
                Fill("Chipped edges", new Vector3(0.35f, 0.34f, 0.33f), 0.45f, 1, MaskGen.Edges, 0.3f, 7, 0.6f),
                Streak(Fill("Rust leaks", new Vector3(0.36f, 0.18f, 0.07f), 0.9f, 0, MaskGen.Streaks, 0.62f, 5, 0.2f)),
            },
            "Wet concrete" => new List<PaintLayer>
            {
                new() { Name = "Concrete", Kind = LayerKind.Fill, Color = new Vector3(0.42f, 0.41f, 0.39f), Rough = 0.88f, ColorNoise = 0.15f, HeightNoise = 0.1f, UseHeight = true, NoiseScale = 20 },
                Fill("Wet areas", new Vector3(0.28f, 0.28f, 0.27f), 0.15f, 0, MaskGen.Noise, 0.5f, 3, 0.3f),
                Streak(Fill("Water streaks", new Vector3(0.3f, 0.3f, 0.29f), 0.3f, 0, MaskGen.Streaks, 0.55f, 4, 0.1f)),
            },
            "Pallet wood" => new List<PaintLayer>
            {
                new() { Name = "Raw wood", Kind = LayerKind.Fill, Color = new Vector3(0.62f, 0.48f, 0.32f), Rough = 0.85f, ColorNoise = 0.3f, HeightNoise = 0.15f, UseHeight = true, NoiseScale = 9 },
                Fill("Grey weathering", new Vector3(0.45f, 0.42f, 0.38f), 0.9f, 0, MaskGen.Top, 0.6f, 4, 0.5f),
                Fill("Dirty corners", new Vector3(0.2f, 0.15f, 0.1f), 0.9f, 0, MaskGen.AmbientOcclusion, 0.35f, 3, 0.3f),
            },
            "Street furniture plastic" => new List<PaintLayer>
            {
                Fill("Green plastic", new Vector3(0.12f, 0.32f, 0.16f), 0.5f),
                Fill("Sun faded top", new Vector3(0.3f, 0.45f, 0.3f), 0.7f, 0, MaskGen.Top, 0.7f, 4, 0.4f),
                Inv(Fill("Dirty base", new Vector3(0.18f, 0.16f, 0.12f), 0.9f, 0, MaskGen.GradientZ, 0.2f, 5, 0.4f)),
            },
            "Galvanised metal" => new List<PaintLayer>
            {
                new() { Name = "Zinc", Kind = LayerKind.Fill, Color = new Vector3(0.66f, 0.68f, 0.7f), Rough = 0.45f, Metal = 1, UseMetal = true, ColorNoise = 0.18f, NoiseScale = 25 },
                Fill("White oxide", new Vector3(0.8f, 0.8f, 0.78f), 0.85f, 0, MaskGen.Noise, 0.62f, 4, 0.3f),
                Fill("Dark cavities", new Vector3(0.22f, 0.22f, 0.22f), 0.8f, 0.3f, MaskGen.AmbientOcclusion, 0.35f, 3, 0.3f),
            },
            "Interior plaster" => new List<PaintLayer>
            {
                new() { Name = "Plaster", Kind = LayerKind.Fill, Color = new Vector3(0.86f, 0.84f, 0.79f), Rough = 0.9f, ColorNoise = 0.04f, HeightNoise = 0.06f, UseHeight = true, NoiseScale = 40 },
                Inv(Fill("Scuffs at the bottom", new Vector3(0.55f, 0.52f, 0.47f), 0.9f, 0, MaskGen.GradientZ, 0.1f, 6, 0.5f)),
                Fill("Corner shadow dirt", new Vector3(0.6f, 0.57f, 0.52f), 0.9f, 0, MaskGen.AmbientOcclusion, 0.4f, 3, 0.3f),
            },
            // ---- procedural (patterns ported from ArmorPaint)
            "Red brick wall" => new List<PaintLayer>
            {
                new() { Name = "Bricks", Kind = LayerKind.Fill, Color = new Vector3(0.48f, 0.18f, 0.11f), Rough = 0.9f, UseHeight = true, Height = 0.6f, ColorNoise = 0.15f, NoiseScale = 20,
                        Pattern = FillPattern.Brick, PatternColor = new Vector3(0.62f, 0.6f, 0.55f), PatternScale = 13, PatternRelief = 0.5f, PatternRough = 0.95f },
                Fill("Soot in the joints", new Vector3(0.15f, 0.13f, 0.11f), 0.95f, 0, MaskGen.AmbientOcclusion, 0.35f, 3, 0.4f),
                Inv(Fill("Damp at the bottom", new Vector3(0.25f, 0.22f, 0.18f), 0.8f, 0, MaskGen.GradientZ, 0.15f, 5, 0.5f)),
            },
            "Cracked mud" => new List<PaintLayer>
            {
                new() { Name = "Dry mud", Kind = LayerKind.Fill, Color = new Vector3(0.5f, 0.4f, 0.28f), Rough = 0.95f, UseHeight = true, Height = 0.6f, ColorNoise = 0.2f, NoiseScale = 12,
                        Pattern = FillPattern.Cracks, PatternColor = new Vector3(0.18f, 0.13f, 0.09f), PatternScale = 6, PatternRelief = 0.7f, Warp = 0.04f, WarpScale = 6 },
                Fill("Lighter crust", new Vector3(0.62f, 0.52f, 0.38f), 0.95f, 0, MaskGen.Perlin, 0.55f, 4, 0, false),
            },
            "Wood grain" => new List<PaintLayer>
            {
                new() { Name = "Wood", Kind = LayerKind.Fill, Color = new Vector3(0.55f, 0.35f, 0.18f), Rough = 0.5f, UseHeight = true, Height = 0.5f,
                        Pattern = FillPattern.Rings, PatternColor = new Vector3(0.32f, 0.18f, 0.08f), PatternScale = 4, PatternRelief = 0.1f, Warp = 0.03f, WarpScale = 3 },
                Fill("Worn edges", new Vector3(0.68f, 0.5f, 0.32f), 0.7f, 0, MaskGen.Edges, 0.4f, 5, 0.5f),
            },
            "Stone paving" => new List<PaintLayer>
            {
                new() { Name = "Stones", Kind = LayerKind.Fill, Color = new Vector3(0.5f, 0.49f, 0.46f), Rough = 0.85f, UseHeight = true, Height = 0.6f, ColorNoise = 0.15f, NoiseScale = 15,
                        Pattern = FillPattern.Cracks, PatternColor = new Vector3(0.2f, 0.19f, 0.17f), PatternScale = 3, PatternRelief = 0.8f, PatternRough = 1f },
                Fill("Moss between", new Vector3(0.18f, 0.25f, 0.1f), 0.95f, 0, MaskGen.Cavity, 0.3f, 4, 0.5f),
            },
            _ => SmartLibrary.Load(name) ?? new List<PaintLayer>(),
        };
        l.RemoveAll(x => x == null);
        foreach (var x in l) { x.Seed = seed++; x.Name = x.Name.StartsWith(name + ":") ? x.Name : $"{name}: {x.Name}"; }
        return l;
    }

    static PaintLayer Inv(PaintLayer l) { l.MaskInvert = true; return l; }
    static PaintLayer Tiles(PaintLayer l, float perMetre) { l.MaskNoiseScale = perMetre; return l; }
    static PaintLayer Streak(PaintLayer l) { l.MaskNoiseScale = 6; return l; }
}

/// <summary>One-click masks for the selected layer (Substance's smart masks).</summary>
public static class SmartMasks
{
    public sealed record Preset(string Name, string Tip, Action<PaintLayer> Apply);

    public static readonly Preset[] All =
    {
        new("Edge wear", "Worn, chipped edges", l => Set(l, MaskGen.Edges, 0.35f, 7, 0.6f, 14)),
        new("Dirt in corners", "Dirt where light does not reach", l => Set(l, MaskGen.AmbientOcclusion, 0.35f, 3, 0.4f, 12)),
        new("Dust on top", "Dust on the upward faces", l => Set(l, MaskGen.Top, 0.75f, 5, 0.4f, 12)),
        new("Ground dirt", "Dirt rising from the ground", l => { Set(l, MaskGen.GradientZ, 0.2f, 5, 0.5f, 10); l.MaskInvert = true; }),
        new("Leaks", "Vertical streaks (rust, water)", l => Set(l, MaskGen.Streaks, 0.6f, 5, 0.15f, 6)),
        new("Cavity grime", "Grime deep in the cavities", l => Set(l, MaskGen.Cavity, 0.25f, 6, 0.3f, 16)),
        new("Patches", "Random patches", l => Set(l, MaskGen.Noise, 0.55f, 5, 0.2f, 8)),
        new("Tile grout", "Grout lines (4 tiles per metre)", l => Set(l, MaskGen.Tiles, 0.5f, 6, 0, 4)),
        new("Cracks", "Cracked paint or ground (cell borders)", l => Set(l, MaskGen.Cracks, 0.5f, 6, 0.2f, 5)),
        new("Brick joints", "Mortar between bricks (13 rows per metre)", l => Set(l, MaskGen.Brick, 0.5f, 6, 0, 13)),
        new("Clouds", "Soft cloudy noise (Perlin)", l => Set(l, MaskGen.Perlin, 0.5f, 3, 0, 4)),
        new("Spots", "Random cells, half of them shown", l => Set(l, MaskGen.Voronoi, 0.5f, 20, 0, 6)),
    };

    static void Set(PaintLayer l, MaskGen g, float th, float contrast, float breakup, float scale)
    {
        l.Mask = g; l.MaskThreshold = th; l.MaskContrast = contrast; l.MaskBreakup = breakup; l.MaskNoiseScale = scale; l.MaskInvert = false;
        l.MaskChanged();
    }
}

/// <summary>Your own smart materials: a layer stack saved as a small JSON file (settings, not the painted pixels).</summary>
public static class SmartLibrary
{
    public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MoloMax", "smart_materials");

    sealed class File1 { public string Name { get; set; } public List<string> Layers { get; set; } = new(); }

    public static IEnumerable<string> Names()
    {
        if (!Directory.Exists(Folder)) yield break;
        foreach (var f in Directory.GetFiles(Folder, "*.json").OrderBy(f => f))
        {
            string n = null;
            try { n = JsonSerializer.Deserialize<File1>(File.ReadAllText(f))?.Name; } catch { }
            if (!string.IsNullOrWhiteSpace(n)) yield return n;
        }
    }

    static string PathOf(string name) => Path.Combine(Folder, string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')) + ".json");

    /// <summary>Saves the layers (top-level ones and those in folders) of a texture set under a name.</summary>
    public static string Save(string name, IEnumerable<PaintLayer> layers)
    {
        Directory.CreateDirectory(Folder);
        var f = new File1 { Name = name };
        foreach (var l in layers.Where(l => !l.IsFolder))
        {
            var c = l.CloneLayer();
            c.Folder = -1; c.Name = c.Name.Contains(": ") ? c.Name[(c.Name.IndexOf(": ") + 2)..] : c.Name;
            if (c.Kind == LayerKind.Paint) continue; // painted strokes belong to one model
            f.Layers.Add(c.SettingsJson());
        }
        var path = PathOf(name);
        File.WriteAllText(path, JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static List<PaintLayer> Load(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<File1>(File.ReadAllText(path))?.Layers.Select(PaintLayer.FromSettingsJson).ToList(); }
        catch { return null; }
    }

    public static void Delete(string name) { var p = PathOf(name); if (File.Exists(p)) File.Delete(p); }
}
