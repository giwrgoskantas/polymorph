using System.Numerics;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;
using MoloMax.Core.Paint;
using MoloMax.Core.Scenes;

/// <summary>
/// `bench &lt;folder&gt;`: the black tufted leather bench (8 x 3 cells on top, 2 rows on the sides, black base),
/// built with the Tufted primitive and textured with Molo Paint layers, saved as a MoloMax scene + GTA textures + .ydr.
/// </summary>
static class LeatherBench
{
    public static int Run(string folder, bool worn = false)
    {
        Directory.CreateDirectory(folder);
        string name = worn ? "molo_bench_leather_worn" : "molo_bench_leather";
        var scene = new Scene();
        var leather = scene.AddMaterial("leather", new Vector3(0.05f));
        var baseMat = scene.AddMaterial("base", new Vector3(0.02f));

        // cushion: 1.40 x 0.48 x 0.40, 8 x 3 cells on top, 2 rows on the sides; base 5 cm, set back 2 cm
        const float W = 1.40f, L = 0.48f, H = 0.40f, B = 0.05f;
        var mesh = Primitives.Tufted(W, L, H, 8, 3, 2, 0.022f, 8, false, leather.Id);
        mesh.Transform(Matrix4x4.CreateTranslation(0, 0, B));
        var plinth = Primitives.Box(W - 0.04f, L - 0.04f, B, 1, 1, 1, baseMat.Id);
        foreach (var f in plinth.Faces) f.MatId = baseMat.Id;
        int first = mesh.Faces.Count;
        mesh.Append(plinth);
        // the cushion UVs come packed from the primitive; the base gets its own atlas (second texture set)
        // GTA rule: green vertex colours on everything that is not emissive
        foreach (var f in mesh.Faces) for (int i = 0; i < f.Col.Length; i++) f.Col[i] = new Vector4(0, 1, 0, 1);
        int charts = UvUnwrap.SmartProject(mesh, 66f, 0.01f, Enumerable.Range(first, mesh.Faces.Count - first).ToHashSet());
        // GIMS layout: the Drawable (one model) with its game mesh and its collision under it
        var node = scene.Add(name, new PolyMesh(), Vector3.Zero, GtaObjectType.Model);
        node.Name = name;
        var o = scene.Add(name + "_high_0", mesh, Vector3.Zero);
        o.Name = name + "_high_0";
        scene.SetParent(o, node);
        var colMesh = Primitives.Box(W - 0.02f, L - 0.02f, B + H - 0.01f); // just inside the cushion (no z-fighting with the seams)
        var col = scene.Add(name + "_col", colMesh, Vector3.Zero, GtaObjectType.Collision);
        col.Name = name + "_col";
        col.Gta.CollisionMaterial = MoloMax.Core.Gta.Export.CollisionMaterials.IndexOf("LEATHER");
        // GIMS "Drawable Col": the collision meshes sit in a collision group under the Drawable
        var grp = scene.Add(name + "_colgroup", new PolyMesh(), Vector3.Zero, GtaObjectType.CollisionGroup);
        grp.Name = name + "_colgroup";
        scene.SetParent(grp, node);
        scene.SetParent(col, grp);
        Console.WriteLine($"mesh: {mesh.Verts.Count} verts, {mesh.Faces.Count} faces, {o.TriangleCount} triangles, {charts} UV charts");

        // Molo Paint: one texture set per material, like the app does
        var scenePath = Path.Combine(folder, name + ".mmx");
        var paintDir = Path.Combine(folder, name + "_paint");
        Directory.CreateDirectory(paintDir);
        var doc = new PaintDoc();
        foreach (var (src, set) in new[] { (leather, "leather"), (baseMat, "base") })
        {
            var baseName = $"{o.Name}_{set}";
            var pm = scene.AddMaterial(baseName + "_paint", new Vector3(0.7f));
            pm.Shader = "normal_spec";
            pm.DiffuseTexture = Path.Combine(paintDir, baseName + ".dds"); pm.BumpTexture = Path.Combine(paintDir, baseName + "_n.dds"); pm.SpecTexture = Path.Combine(paintDir, baseName + "_s.dds");
            pm.SpecMapIntMask = new Vector3(1, 0, 0); pm.Bumpiness = 1f; pm.SpecularIntensity = 1f; pm.SpecularFalloff = 100f;
            foreach (var f in mesh.Faces) if (f.MatId == src.Id) f.MatId = pm.Id;
            var ts = TextureSet.Create(mesh, set == "leather" ? 2048 : 512, 32, pm.Id, set);
            ts.ExportSize = set == "leather" ? 1024 : 512;
            ts.Layers.Clear(); ts.NextId = 1;
            if (set == "leather") Leather(ts); else Base(ts);
            if (worn) { if (set == "leather") WornLeather(ts); else WornBase(ts); }
            ts.Refresh(true);
            doc.Sets.Add(ts);
            var w = PaintExport.Write(ts, paintDir, baseName);
            Console.WriteLine($"{set}: {ts.Map.Count:N0} texels at {ts.Size}, layers: {string.Join(", ", ts.Layers.Select(l => l.Name))}; wrote {string.Join(", ", w.Files.Select(Path.GetFileName))}");
        }
        scene.Materials.RemoveAll(m => m.Id == leather.Id || m.Id == baseMat.Id);
        o.Touch();
        o.PaintFile = Path.Combine(paintDir, o.Name + ".mpaint");
        doc.Save(o.PaintFile);
        SceneIO.Save(scene, scenePath);
        Console.WriteLine("scene: " + scenePath);

        // GTA model with the textures embedded
        var ydr = Path.Combine(folder, name + ".ydr");
        var summary = MoloMax.Core.Gta.Export.GtaExporter.ExportModelFile(scene, new[] { node }, ydr, null, new[] { paintDir });
        Console.WriteLine("ydr: " + summary);
        var back = MoloMax.Core.Gta.Import.GtaImporter.Import(ydr);
        Console.WriteLine($"ydr re-import: {back.Meshes.Sum(m => m.Mesh.Faces.Count)} faces, {back.Materials.Count} materials, {back.TexturesWritten.Count} textures, warnings {back.Warnings.Count}");
        var issues = MoloMax.Core.Gta.Check.GtaChecker.Run(scene);
        foreach (var i in issues) Console.WriteLine($"check: {i.Level}: {i.Message}");
        return 0;
    }

    static PaintLayer Fill(string name, Vector3 c, float rough) => new() { Name = name, Kind = LayerKind.Fill, Color = c, Rough = rough, UseRough = true };

    /// <summary>Black leather: grain in the relief, darker deep seams, satin worn tops, a little dust in the folds.</summary>
    static void Leather(TextureSet ts)
    {
        ts.NormalStrength = 6f;
        var b = Fill("Black leather", new Vector3(0.042f, 0.042f, 0.046f), 0.24f);
        b.UseHeight = true; b.HeightNoise = 0.22f; b.ColorNoise = 0.12f; b.NoiseScale = 320; // pebble grain, ~3 mm
        ts.AddLayerRaw(b);
        var grain = Fill("Grain creases", new Vector3(0.022f, 0.022f, 0.024f), 0.45f);
        grain.UseHeight = true; grain.Height = 0.38f; grain.Mask = MaskGen.Noise; grain.MaskNoiseScale = 700; grain.MaskThreshold = 0.56f; grain.MaskContrast = 4; grain.Opacity = 0.8f;
        ts.AddLayerRaw(grain);
        var seams = Fill("Stitched seams", new Vector3(0.008f, 0.008f, 0.009f), 0.75f);
        seams.UseHeight = true; seams.Height = 0.3f; seams.Mask = MaskGen.Cavity; seams.MaskThreshold = 0.05f; seams.MaskContrast = 10;
        seams.Filters.Add(new PaintFilter { Kind = FilterKind.Blur, Radius = 1 });
        ts.AddLayerRaw(seams);
        var worn = Fill("Worn satin tops", new Vector3(0.075f, 0.073f, 0.072f), 0.14f);
        worn.Mask = MaskGen.Edges; worn.MaskThreshold = 0.42f; worn.MaskContrast = 3; worn.MaskBreakup = 0.35f; worn.MaskNoiseScale = 18; worn.Opacity = 0.7f;
        ts.AddLayerRaw(worn);
        var folder = new PaintLayer { Name = "Dirt", Kind = LayerKind.Folder, Opacity = 0.6f };
        ts.AddLayerRaw(folder);
        var dust = Fill("Dust in the folds", new Vector3(0.16f, 0.15f, 0.14f), 0.9f);
        dust.Mask = MaskGen.AmbientOcclusion; dust.MaskThreshold = 0.45f; dust.MaskContrast = 4; dust.MaskBreakup = 0.4f; dust.MaskNoiseScale = 25; dust.Opacity = 0.35f; dust.Folder = folder.Id;
        ts.AddLayerRaw(dust);
    }

    /// <summary>
    /// Old, used leather: faded grey-brown patches where people sit, cracks and scuffs on the tops of the cushions,
    /// scratches, brown dirt deep in the seams, dust on top and grime rising from the floor.
    /// </summary>
    static void WornLeather(TextureSet ts)
    {
        // where people sit: the top, a bit down the front edge
        var use = new PaintLayer { Name = "Wear (where people sit)", Kind = LayerKind.Folder, Mask = MaskGen.Top, MaskThreshold = 0.55f, MaskContrast = 3, MaskBreakup = 0.45f, MaskNoiseScale = 6 };
        ts.AddLayerRaw(use);
        var faded = Fill("Faded leather", new Vector3(0.16f, 0.145f, 0.13f), 0.62f);
        faded.Mask = MaskGen.Noise; faded.MaskNoiseScale = 7; faded.MaskThreshold = 0.56f; faded.MaskContrast = 3.5f; faded.MaskBreakup = 0.35f; faded.Folder = use.Id; faded.Opacity = 0.6f;
        faded.Filters.Add(new PaintFilter { Kind = FilterKind.Blur, Radius = 3 });
        ts.AddLayerRaw(faded);
        var scuff = Fill("Scuffed puff tops", new Vector3(0.24f, 0.22f, 0.2f), 0.75f);
        scuff.Mask = MaskGen.Edges; scuff.MaskThreshold = 0.5f; scuff.MaskContrast = 5; scuff.MaskBreakup = 0.6f; scuff.MaskNoiseScale = 30; scuff.Folder = use.Id; scuff.Opacity = 0.6f;
        ts.AddLayerRaw(scuff);
        var cracks = Fill("Cracked leather", new Vector3(0.3f, 0.28f, 0.25f), 0.85f);
        cracks.UseHeight = true; cracks.Height = 0.25f; cracks.Mask = MaskGen.Noise; cracks.MaskNoiseScale = 90; cracks.MaskThreshold = 0.7f; cracks.MaskContrast = 9; cracks.MaskBreakup = 0.25f; cracks.Folder = use.Id; cracks.Opacity = 0.7f;
        ts.AddLayerRaw(cracks);

        // scratches printed with the brush tip, on the top and the front
        var scr = new PaintLayer { Name = "Scratches", Kind = LayerKind.Paint };
        ts.AddLayerRaw(scr); ts.SelectedLayer = ts.Layers.Count - 1;
        var rnd = new Random(7);
        var b = new PaintBrush { Tip = BrushTips.Get("Scratches"), Strength = 0.5f, ColorValue = new Vector3(0.32f, 0.3f, 0.28f), Roughness = true, RoughValue = 0.8f, Height = true, HeightValue = 0.35f };
        var tops = Enumerable.Range(0, ts.Map.Count).Where(t => ts.Map.Nrm[t].Z > 0.6f || ts.Map.Nrm[t].Y < -0.6f).ToArray();
        for (int i = 0; i < 45 && tops.Length > 0; i++)
        {
            int t = tops[rnd.Next(tops.Length)];
            float r = 0.04f + (float)rnd.NextDouble() * 0.08f;
            ts.Stamp(b, ts.Map.Pos[t], ts.Map.Nrm[t], r, (float)rnd.NextDouble() * 360);
        }

        // dirt: brown in the seams, dust on top, grime from the floor
        var dirt = new PaintLayer { Name = "Dirt (heavy)", Kind = LayerKind.Folder };
        ts.AddLayerRaw(dirt);
        var seams = Fill("Brown dirt in the seams", new Vector3(0.11f, 0.08f, 0.05f), 0.9f);
        seams.Mask = MaskGen.Cavity; seams.MaskThreshold = 0.03f; seams.MaskContrast = 8; seams.MaskBreakup = 0.3f; seams.MaskNoiseScale = 20; seams.Folder = dirt.Id; seams.Opacity = 0.9f;
        seams.Filters.Add(new PaintFilter { Kind = FilterKind.Blur, Radius = 2 });
        ts.AddLayerRaw(seams);
        var folds = Fill("Grime in the folds", new Vector3(0.09f, 0.075f, 0.06f), 0.85f);
        folds.Mask = MaskGen.AmbientOcclusion; folds.MaskThreshold = 0.3f; folds.MaskContrast = 3; folds.MaskBreakup = 0.4f; folds.MaskNoiseScale = 15; folds.Folder = dirt.Id; folds.Opacity = 0.4f;
        ts.AddLayerRaw(folds);
        var dust = Fill("Dust on top", new Vector3(0.38f, 0.36f, 0.33f), 0.95f);
        dust.Mask = MaskGen.Top; dust.MaskThreshold = 0.8f; dust.MaskContrast = 4; dust.MaskBreakup = 0.6f; dust.MaskNoiseScale = 12; dust.Folder = dirt.Id; dust.Opacity = 0.22f;
        ts.AddLayerRaw(dust);
        var floor = Fill("Grime from the floor", new Vector3(0.13f, 0.1f, 0.07f), 0.9f);
        floor.Mask = MaskGen.GradientZ; floor.MaskInvert = true; floor.MaskThreshold = 0.12f; floor.MaskContrast = 6; floor.MaskBreakup = 0.5f; floor.MaskNoiseScale = 10; floor.Folder = dirt.Id; floor.Opacity = 0.6f;
        ts.AddLayerRaw(floor);
        var drips = Fill("Old drips", new Vector3(0.1f, 0.08f, 0.06f), 0.5f);
        drips.Mask = MaskGen.Streaks; drips.MaskNoiseScale = 9; drips.MaskThreshold = 0.66f; drips.MaskContrast = 6; drips.Folder = dirt.Id; drips.Opacity = 0.4f;
        ts.AddLayerRaw(drips);
    }

    /// <summary>Base: lacquer chipped to the raw wood on the edges, kicks and floor grime.</summary>
    static void WornBase(TextureSet ts)
    {
        var chips = Fill("Chipped to the wood", new Vector3(0.28f, 0.17f, 0.09f), 0.8f);
        chips.Mask = MaskGen.Edges; chips.MaskThreshold = 0.35f; chips.MaskContrast = 7; chips.MaskBreakup = 0.7f; chips.MaskNoiseScale = 25;
        ts.AddLayerRaw(chips);
        var kicks = Fill("Shoe scuffs", new Vector3(0.2f, 0.19f, 0.18f), 0.85f);
        kicks.Mask = MaskGen.Noise; kicks.MaskNoiseScale = 18; kicks.MaskThreshold = 0.58f; kicks.MaskContrast = 6; kicks.Opacity = 0.8f;
        ts.AddLayerRaw(kicks);
        var grime = Fill("Floor grime", new Vector3(0.12f, 0.09f, 0.06f), 0.95f);
        grime.Mask = MaskGen.GradientZ; grime.MaskInvert = true; grime.MaskThreshold = 0.5f; grime.MaskContrast = 3; grime.MaskBreakup = 0.5f; grime.Opacity = 0.8f;
        ts.AddLayerRaw(grime);
    }

    /// <summary>Black lacquered wooden base, slightly worn on the edges.</summary>
    static void Base(TextureSet ts)
    {
        var b = Fill("Black lacquer", new Vector3(0.016f, 0.016f, 0.017f), 0.35f);
        b.ColorNoise = 0.06f; b.NoiseScale = 40;
        ts.AddLayerRaw(b);
        var e = Fill("Edge wear", new Vector3(0.06f, 0.055f, 0.05f), 0.5f);
        e.Mask = MaskGen.Edges; e.MaskThreshold = 0.6f; e.MaskContrast = 6; e.MaskBreakup = 0.5f; e.Opacity = 0.5f;
        ts.AddLayerRaw(e);
    }
}
