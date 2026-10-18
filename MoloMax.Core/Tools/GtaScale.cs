using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Tools;

/// <summary>
/// Human scale for GTA: a ped of the game's size to model next to, and real heights of the things people use, so a prop
/// can be brought to the right size from its name (chair, stool, door, counter...).
/// </summary>
public static class GtaScale
{
    /// <summary>Height of a GTA ped standing (metres).</summary>
    public const float PedHeight = 1.818f;

    public sealed record Kind(string Id, string Label, float Height, string[] Words);

    /// <summary>Overall heights (floor to top) of common props at GTA scale. The first words that match the name win.</summary>
    public static readonly Kind[] Kinds =
    {
        new("ped", "Ped / person", PedHeight, new[] { "ped", "human", "person", "character", "perso", "npc" }),
        new("door", "Door", 2.30f, new[] { "door", "porte" }),
        new("handle", "Door handle (height)", 1.127f, new[] { "handle", "poignee", "poignée" }),
        new("bar_counter", "Bar counter", 1.067f, new[] { "bar_counter", "barcounter", "comptoir_bar", "comptoirbar", "bar counter", "bar" }),
        new("counter", "Counter / sink", 0.90f, new[] { "counter", "comptoir", "sink", "evier", "évier", "kitchen", "cuisine", "worktop" }),
        new("coffee_table", "Coffee table", 0.429f, new[] { "coffee", "table_basse", "tablebasse", "basse", "low_table" }),
        new("desk", "Desk / table", 0.80f, new[] { "desk", "bureau", "table" }),
        new("stool", "Stool", 0.75f, new[] { "stool", "tabouret" }),
        new("chair", "Chair", 0.90f, new[] { "chair", "chaise", "armchair", "fauteuil" }),
        new("seat", "Seat / bench (seat height)", 0.455f, new[] { "seat", "bench", "banc", "assise", "pouf" }),
        new("sofa", "Sofa", 0.85f, new[] { "sofa", "couch", "canape", "canapé" }),
        new("bed", "Bed", 0.60f, new[] { "bed", "lit_", "matelas", "mattress" }),
        new("fridge", "Fridge", 1.80f, new[] { "fridge", "frigo", "refrigerator" }),
        new("wardrobe", "Wardrobe", 2.00f, new[] { "wardrobe", "armoire", "closet", "locker", "casier" }),
        new("toilet", "Toilet", 0.78f, new[] { "toilet", "wc" }),
        new("shelf", "Shelf unit", 1.80f, new[] { "shelf", "shelves", "etagere", "étagère", "rack" }),
        new("lamp", "Floor lamp", 1.60f, new[] { "floorlamp", "floor_lamp", "lampadaire" }),
        new("tv", "TV (screen)", 0.65f, new[] { "tv", "television", "télé", "tele" }),
        new("car", "Car", 1.45f, new[] { "car", "voiture", "vehicle" }),
    };

    /// <summary>The kind of prop a name describes (null when nothing matches).</summary>
    public static Kind Guess(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.ToLowerInvariant();
        // the longest word found wins ("tabouret_bar" is a stool, not a bar counter)
        Kind best = null; int bestLen = 0;
        foreach (var k in Kinds)
            foreach (var w in k.Words)
            {
                int i = n.IndexOf(w, StringComparison.Ordinal);
                if (i < 0 || w.Length <= bestLen) continue;
                // whole word-ish: not inside another word ("table" in "stable", "bar" in "barrel")
                bool before = i == 0 || !char.IsLetter(n[i - 1]);
                bool after = i + w.Length >= n.Length || !char.IsLetter(n[i + w.Length]);
                if (before && after || w.Length >= 6) { best = k; bestLen = w.Length; }
            }
        if (best != null) return best;
        return null;
    }

    /// <summary>
    /// A low-poly ped mannequin, <paramref name="height"/> tall, standing on Z = 0 and looking along -Y (like a GTA ped
    /// facing the default camera): legs, hips, torso, shoulders, arms along the body, neck and head.
    /// </summary>
    public static PolyMesh Ped(float height = PedHeight)
    {
        float k = height / PedHeight;
        var m = new PolyMesh();
        void Part(PolyMesh p, Vector3 at) { foreach (var i in Enumerable.Range(0, p.Verts.Count)) p.Verts[i] = p.Verts[i] * k + at * k; m.Append(p); }
        // legs (base at the feet)
        Part(Primitives.Box(0.15f, 0.20f, 0.86f), new Vector3(-0.10f, 0, 0));
        Part(Primitives.Box(0.15f, 0.20f, 0.86f), new Vector3(0.10f, 0, 0));
        Part(Primitives.Box(0.13f, 0.27f, 0.07f), new Vector3(-0.10f, -0.04f, 0)); // feet
        Part(Primitives.Box(0.13f, 0.27f, 0.07f), new Vector3(0.10f, -0.04f, 0));
        // hips and torso
        Part(Primitives.Box(0.36f, 0.22f, 0.16f), new Vector3(0, 0, 0.86f));
        Part(Primitives.Box(0.40f, 0.24f, 0.46f), new Vector3(0, 0, 1.02f));
        Part(Primitives.Box(0.48f, 0.22f, 0.10f), new Vector3(0, 0, 1.40f)); // shoulders
        // arms along the body
        Part(Primitives.Box(0.10f, 0.12f, 0.62f), new Vector3(-0.29f, 0, 0.84f));
        Part(Primitives.Box(0.10f, 0.12f, 0.62f), new Vector3(0.29f, 0, 0.84f));
        // neck and head
        Part(Primitives.Cylinder(0.055f, 0.08f, 8), new Vector3(0, 0, 1.50f));
        Part(Primitives.Sphere(0.115f, 12), new Vector3(0, -0.01f, PedHeight - 0.115f)); // centred sphere
        return m;
    }

    /// <summary>Scale factor that brings something <paramref name="currentHeight"/> tall to the kind's height.</summary>
    public static float FactorFor(Kind k, float currentHeight) => currentHeight > 1e-5f ? k.Height / currentHeight : 1f;
}
