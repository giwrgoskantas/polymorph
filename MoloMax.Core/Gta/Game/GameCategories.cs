using System.Text.RegularExpressions;

namespace MoloMax.Core.Gta.Game;

/// <summary>Families of game models found from their names (prop_chair_01a -> Chairs & seats), for the game files browser.</summary>
public static class GameCategories
{
    public sealed record Category(string Name, string[] Words, Regex Pattern = null);

    public static readonly Category[] All =
    {
        new("Chairs & seats", new[] { "chair", "seat", "stool", "bench", "sofa", "couch", "armchair", "bean" }),
        new("Tables & desks", new[] { "table", "desk", "counter", "bar" }),
        new("Beds", new[] { "bed", "mattress", "cot", "bunk" }),
        new("Lights & lamps", new[] { "light", "lamp", "lantern", "neon", "spotlight", "chandelier", "bulb", "flood" }),
        new("Doors & gates", new[] { "door", "gate", "shutter", "garagedoor", "doorframe" }),
        new("Fences & barriers", new[] { "fence", "barrier", "rail", "bollard", "cone", "barricade", "wall", "railing", "mesh" }),
        new("Vegetation", new[] { "tree", "bush", "plant", "veg", "flower", "grass", "palm", "hedge", "shrub", "weed", "ivy", "cactus" }),
        new("Rocks & ground", new[] { "rock", "boulder", "stone", "cliff", "dirt", "sand", "rubble" }),
        new("Signs & posters", new[] { "sign", "billboard", "poster", "banner", "flag", "board" }),
        new("Bins & trash", new[] { "bin", "trash", "dumpster", "rubbish", "garbage", "skip", "litter", "junk" }),
        new("Boxes & crates", new[] { "box", "crate", "pallet", "barrel", "container", "cardboard", "case", "drum" }),
        new("Electronics", new[] { "tv", "screen", "monitor", "computer", "laptop", "phone", "speaker", "radio", "cctv", "cam", "keyboard", "console", "tablet", "printer" }),
        new("Kitchen & food", new[] { "kitchen", "fridge", "oven", "cooker", "microwave", "cup", "mug", "plate", "bottle", "food", "burger", "pizza", "drink", "can", "glass", "kettle", "toaster" }),
        new("Bathroom", new[] { "toilet", "shower", "bath", "sink", "basin", "towel", "urinal", "loo" }),
        new("Shelves & storage", new[] { "shelf", "rack", "cabinet", "locker", "drawer", "wardrobe", "cupboard", "bookcase", "safe" }),
        new("Vehicles & parts", new[] { "wheel", "tyre", "tire", "car", "veh", "bike", "engine", "trailer", "boat", "plane", "heli" }),
        new("Industrial", new[] { "pipe", "tank", "generator", "crane", "scaffold", "ladder", "beam", "girder", "vent", "duct", "cable", "pylon", "valve", "drill", "tool" }),
        new("Sports & leisure", new[] { "gym", "weight", "ball", "pool", "arcade", "bowl", "golf", "basket", "dart", "slot", "casino" }),
        new("Decoration", new[] { "vase", "frame", "painting", "rug", "carpet", "curtain", "plant_pot", "statue", "clock", "mirror", "pillow", "cushion", "blind" }),
        new("Weapons & police", new[] { "gun", "rifle", "pistol", "ammo", "weapon", "cop", "police", "taser", "cuff" }),
        new("Map pieces (exterior)", Array.Empty<string>(), new Regex(@"^[a-z]{2,3}\d{0,2}_\d", RegexOptions.Compiled)),
        new("Interiors", Array.Empty<string>(), new Regex(@"^(v_|int_|mp_.*int|.*_int_)", RegexOptions.Compiled)),
        new("LOD / far models", Array.Empty<string>(), new Regex(@"(_lod|lod_|_slod|_hd$)", RegexOptions.Compiled)),
    };

    /// <summary>Bit i set = the model belongs to <see cref="All"/>[i] (a model can be in several families).</summary>
    public static long Classify(string name)
    {
        var n = name.ToLowerInvariant();
        var tokens = n.Split('_', '-', '.');
        long mask = 0;
        for (int i = 0; i < All.Length; i++)
        {
            var c = All[i];
            if (c.Pattern != null) { if (c.Pattern.IsMatch(n)) mask |= 1L << i; continue; }
            foreach (var w in c.Words)
            {
                bool hit = w.Contains('_') ? n.Contains(w) : tokens.Any(t => t == w || (w.Length >= 4 && t.Contains(w)) || (w.Length >= 3 && t.StartsWith(w) && t.Length <= w.Length + 3));
                if (hit) { mask |= 1L << i; break; }
            }
        }
        return mask;
    }
}
