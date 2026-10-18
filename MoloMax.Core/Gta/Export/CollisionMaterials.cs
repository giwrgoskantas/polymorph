namespace MoloMax.Core.Gta.Export;

/// <summary>GTA V collision materials, in materials.dat order (the index goes into the .ybn).</summary>
public static class CollisionMaterials
{
    public static readonly string[] Names =
    {
        "DEFAULT", "CONCRETE", "CONCRETE_POTHOLE", "CONCRETE_DUSTY", "TARMAC", "TARMAC_PAINTED",
        "TARMAC_POTHOLE", "RUMBLE_STRIP", "BREEZE_BLOCK", "ROCK", "ROCK_MOSSY", "STONE",
        "COBBLESTONE", "BRICK", "MARBLE", "PAVING_SLAB", "SANDSTONE_SOLID", "SANDSTONE_BRITTLE",
        "SAND_LOOSE", "SAND_COMPACT", "SAND_WET", "SAND_TRACK", "SAND_UNDERWATER", "SAND_DRY_DEEP",
        "SAND_WET_DEEP", "ICE", "ICE_TARMAC", "SNOW_LOOSE", "SNOW_COMPACT", "SNOW_DEEP",
        "SNOW_TARMAC", "GRAVEL_SMALL", "GRAVEL_LARGE", "GRAVEL_DEEP", "GRAVEL_TRAIN_TRACK", "DIRT_TRACK",
        "MUD_HARD", "MUD_POTHOLE", "MUD_SOFT", "MUD_UNDERWATER", "MUD_DEEP", "MARSH",
        "MARSH_DEEP", "SOIL", "CLAY_HARD", "CLAY_SOFT", "GRASS_LONG", "GRASS",
        "GRASS_SHORT", "HAY", "BUSHES", "TWIGS", "LEAVES", "WOODCHIPS",
        "TREE_BARK", "METAL_SOLID_SMALL", "METAL_SOLID_MEDIUM", "METAL_SOLID_LARGE", "METAL_HOLLOW_SMALL", "METAL_HOLLOW_MEDIUM",
        "METAL_HOLLOW_LARGE", "METAL_CHAINLINK_SMALL", "METAL_CHAINLINK_LARGE", "METAL_CORRUGATED_IRON", "METAL_GRILLE", "METAL_RAILING",
        "METAL_DUCT", "METAL_GARAGE_DOOR", "METAL_MANHOLE", "WOOD_SOLID_SMALL", "WOOD_SOLID_MEDIUM", "WOOD_SOLID_LARGE",
        "WOOD_SOLID_POLISHED", "WOOD_FLOOR_DUSTY", "WOOD_HOLLOW_SMALL", "WOOD_HOLLOW_MEDIUM", "WOOD_HOLLOW_LARGE", "WOOD_CHIPBOARD",
        "WOOD_OLD_CREAKY", "WOOD_HIGH_DENSITY", "WOOD_LATTICE", "CERAMIC", "ROOF_TILE", "ROOF_FELT",
        "FIBREGLASS", "TARPAULIN", "PLASTIC", "PLASTIC_HOLLOW", "PLASTIC_HIGH_DENSITY", "PLASTIC_CLEAR",
        "PLASTIC_HOLLOW_CLEAR", "PLASTIC_HIGH_DENSITY_CLEAR", "FIBREGLASS_HOLLOW", "RUBBER", "RUBBER_HOLLOW", "LINOLEUM",
        "LAMINATE", "CARPET_SOLID", "CARPET_SOLID_DUSTY", "CARPET_FLOORBOARD", "CLOTH", "PLASTER_SOLID",
        "PLASTER_BRITTLE", "CARDBOARD_SHEET", "CARDBOARD_BOX", "PAPER", "FOAM", "FEATHER_PILLOW",
        "POLYSTYRENE", "LEATHER", "TVSCREEN", "SLATTED_BLINDS", "GLASS_SHOOT_THROUGH", "GLASS_BULLETPROOF",
        "GLASS_OPAQUE", "PERSPEX", "CAR_METAL", "CAR_PLASTIC", "CAR_SOFTTOP", "CAR_SOFTTOP_CLEAR",
        "CAR_GLASS_WEAK", "CAR_GLASS_MEDIUM", "CAR_GLASS_STRONG", "CAR_GLASS_BULLETPROOF", "CAR_GLASS_OPAQUE", "WATER",
        "BLOOD", "OIL", "PETROL", "FRESH_MEAT", "DRIED_MEAT", "EMISSIVE_GLASS",
        "EMISSIVE_PLASTIC", "VFX_METAL_ELECTRIFIED", "VFX_METAL_WATER_TOWER", "VFX_METAL_STEAM", "VFX_METAL_FLAME", "PHYS_NO_FRICTION",
        "PHYS_GOLF_BALL", "PHYS_TENNIS_BALL", "PHYS_CASTER", "PHYS_CASTER_RUSTY", "PHYS_CAR_VOID", "PHYS_PED_CAPSULE",
        "PHYS_ELECTRIC_FENCE", "PHYS_ELECTRIC_METAL", "PHYS_BARBED_WIRE", "PHYS_POOLTABLE_SURFACE", "PHYS_POOLTABLE_CUSHION", "PHYS_POOLTABLE_BALL",
        "BUTTOCKS", "THIGH_LEFT", "SHIN_LEFT", "FOOT_LEFT", "THIGH_RIGHT", "SHIN_RIGHT",
        "FOOT_RIGHT", "SPINE0", "SPINE1", "SPINE2", "SPINE3", "CLAVICLE_LEFT",
        "UPPER_ARM_LEFT", "LOWER_ARM_LEFT", "HAND_LEFT", "CLAVICLE_RIGHT", "UPPER_ARM_RIGHT", "LOWER_ARM_RIGHT",
        "HAND_RIGHT", "NECK", "HEAD", "ANIMAL_DEFAULT", "CAR_ENGINE", "PUDDLE",
        "CONCRETE_PAVEMENT", "BRICK_PAVEMENT", "PHYS_DYNAMIC_COVER_BOUND", "VFX_WOOD_BEER_BARREL", "WOOD_HIGH_FRICTION", "ROCK_NOINST",
        "BUSHES_NOINST", "METAL_SOLID_ROAD_SURFACE", "STUNT_RAMP_SURFACE", "TEMP_01", "TEMP_02",
    };

    /// <summary>The ones a map maker picks most, for quick buttons.</summary>
    public static readonly (string Label, string Name)[] Common =
    {
        ("Concrete", "CONCRETE"), ("Tarmac", "TARMAC"), ("Brick", "BRICK"), ("Marble", "MARBLE"), ("Ceramic tiles", "CERAMIC"),
        ("Wood floor", "WOOD_SOLID_POLISHED"), ("Wood", "WOOD_SOLID_MEDIUM"), ("Carpet", "CARPET_SOLID"), ("Linoleum", "LINOLEUM"),
        ("Metal", "METAL_SOLID_MEDIUM"), ("Metal hollow", "METAL_HOLLOW_MEDIUM"), ("Plaster wall", "PLASTER_SOLID"), ("Glass", "GLASS_OPAQUE"),
        ("Grass", "GRASS"), ("Gravel", "GRAVEL_SMALL"), ("Sand", "SAND_COMPACT"), ("Rock", "ROCK"), ("Plastic", "PLASTIC"), ("Rubber", "RUBBER"),
    };

    public static int IndexOf(string name)
    {
        int i = Array.IndexOf(Names, name);
        return i < 0 ? 0 : i;
    }

    /// <summary>Viewport colour of a collision material, by family (the way GIMS colours collisions).</summary>
    public static System.Numerics.Vector3 Colour(int index)
    {
        string n = NameOf(index);
        (string Key, float R, float G, float B)[] fam =
        {
            ("GLASS", 0.45f, 0.85f, 1f), ("PERSPEX", 0.45f, 0.85f, 1f), ("WATER", 0.15f, 0.45f, 1f), ("PUDDLE", 0.15f, 0.45f, 1f),
            ("TARMAC", 0.32f, 0.32f, 0.36f), ("RUMBLE", 0.32f, 0.32f, 0.36f), ("CONCRETE", 0.66f, 0.66f, 0.64f), ("PAVING", 0.7f, 0.68f, 0.62f), ("BREEZE", 0.66f, 0.66f, 0.64f),
            ("BRICK", 0.78f, 0.36f, 0.24f), ("MARBLE", 0.92f, 0.9f, 0.86f), ("CERAMIC", 0.95f, 0.95f, 0.95f), ("ROOF", 0.6f, 0.25f, 0.2f),
            ("ROCK", 0.5f, 0.47f, 0.42f), ("STONE", 0.5f, 0.47f, 0.42f), ("COBBLE", 0.55f, 0.5f, 0.45f), ("SANDSTONE", 0.85f, 0.72f, 0.5f),
            ("SAND", 0.95f, 0.85f, 0.5f), ("SNOW", 0.95f, 0.97f, 1f), ("ICE", 0.8f, 0.92f, 1f),
            ("GRAVEL", 0.6f, 0.58f, 0.52f), ("DIRT", 0.55f, 0.4f, 0.25f), ("MUD", 0.42f, 0.3f, 0.18f), ("SOIL", 0.5f, 0.35f, 0.2f), ("CLAY", 0.7f, 0.45f, 0.3f), ("MARSH", 0.35f, 0.4f, 0.2f),
            ("GRASS", 0.3f, 0.75f, 0.25f), ("HAY", 0.85f, 0.75f, 0.35f), ("BUSHES", 0.2f, 0.55f, 0.2f), ("LEAVES", 0.4f, 0.6f, 0.15f), ("TWIGS", 0.5f, 0.4f, 0.2f), ("TREE_BARK", 0.45f, 0.3f, 0.15f),
            ("METAL", 0.45f, 0.55f, 0.75f), ("CAR_", 0.5f, 0.5f, 0.85f),
            ("WOOD", 0.72f, 0.48f, 0.22f), ("LAMINATE", 0.8f, 0.6f, 0.35f),
            ("CARPET", 0.75f, 0.25f, 0.45f), ("LINOLEUM", 0.6f, 0.75f, 0.6f), ("CLOTH", 0.8f, 0.4f, 0.6f), ("LEATHER", 0.5f, 0.3f, 0.2f),
            ("PLASTER", 0.9f, 0.88f, 0.8f), ("PLASTIC", 0.95f, 0.55f, 0.85f), ("RUBBER", 0.25f, 0.25f, 0.25f), ("FIBREGLASS", 0.8f, 0.8f, 0.6f), ("TARPAULIN", 0.3f, 0.5f, 0.3f),
            ("CARDBOARD", 0.75f, 0.6f, 0.4f), ("PAPER", 0.95f, 0.95f, 0.9f), ("FOAM", 0.95f, 0.9f, 0.6f), ("POLYSTYRENE", 0.95f, 0.95f, 0.95f),
            ("PHYS_", 0.9f, 0.2f, 0.9f), ("VFX_", 1f, 0.3f, 0.1f), ("EMISSIVE", 1f, 0.95f, 0.4f), ("STUNT", 1f, 0.2f, 0.2f),
        };
        foreach (var (key, r, g, b) in fam) if (n.Contains(key)) return new(r, g, b);
        return new(1f, 0.55f, 0.15f); // DEFAULT and the rest: GTA orange
    }

    public static string NameOf(int index) => index >= 0 && index < Names.Length ? Names[index] : "DEFAULT";
}

/// <summary>
/// GTA collision material flags (CodeWalker EBoundMaterialFlags). "Auto" works them out from the name, the material
/// and the shape: stairs get FLAG_STAIRS, glass is see-through, water and foliage get their usual flags.
/// </summary>
public static class CollisionFlags
{
    public static readonly (uint Bit, string Name, string Label)[] All =
    {
        (1, "FLAG_STAIRS", "Stairs (smooth walking up)"), (1 << 1, "FLAG_NOT_CLIMBABLE", "Not climbable"),
        (1 << 2, "FLAG_SEE_THROUGH", "See through (AI sees)"), (1 << 3, "FLAG_SHOOT_THROUGH", "Shoot through"),
        (1 << 4, "FLAG_NOT_COVER", "Not cover"), (1 << 5, "FLAG_WALKABLE_PATH", "Walkable path"),
        (1 << 6, "FLAG_NO_CAM_COLLISION", "No camera collision"), (1 << 7, "FLAG_SHOOT_THROUGH_FX", "Shoot through FX"),
        (1 << 8, "FLAG_NO_DECAL", "No decals"), (1 << 9, "FLAG_NO_NAVMESH", "No navmesh"),
        (1 << 10, "FLAG_NO_RAGDOLL", "No ragdoll"), (1 << 11, "FLAG_VEHICLE_WHEEL", "Vehicle wheel"),
        (1 << 12, "FLAG_NO_PTFX", "No particle FX"), (1 << 13, "FLAG_TOO_STEEP_FOR_PLAYER", "Too steep for the player"),
        (1 << 14, "FLAG_NO_NETWORK_SPAWN", "No network spawn"), (1 << 15, "FLAG_NO_CAM_COLLISION_ALLOW_CLIPPING", "No camera collision (clipping)"),
    };

    public const uint Stairs = 1, NotClimbable = 1 << 1, SeeThrough = 1 << 2, ShootThrough = 1 << 3, NotCover = 1 << 4, NoCam = 1 << 6, ShootThroughFx = 1 << 7, NoDecal = 1 << 8;

    /// <summary>One-click setups: material (null = keep) and flags.</summary>
    public static readonly (string Label, string Material, uint Flags, string Tip)[] Presets =
    {
        ("Floor", null, 0, "Normal walkable surface"),
        ("Stairs", null, Stairs, "Players and peds walk up smoothly"),
        ("Water", "WATER", NoDecal | NotClimbable, "Shallow water, fountains, pools (WATER material)"),
        ("Glass", "GLASS_SHOOT_THROUGH", SeeThrough | ShootThroughFx, "Windows: AI sees through, bullets pass with glass FX"),
        ("Foliage", "BUSHES", SeeThrough | ShootThrough | NotCover, "Bushes and hedges: soft, not cover"),
        ("Wall", null, NotClimbable, "Walls players should not climb"),
        ("Fence", "METAL_CHAINLINK_FENCE", SeeThrough | ShootThrough, "Chain-link fences, grilles"),
        ("No camera", null, NoCam, "The camera passes through (thin decor, curtains)"),
    };

    public static string ToXml(uint flags)
    {
        if (flags == 0) return "NONE";
        return string.Join(", ", All.Where(f => (flags & f.Bit) != 0).Select(f => f.Name));
    }

    /// <summary>The flags written for a Collision object (its own when Auto is off).</summary>
    public static uint Resolve(Scenes.SceneObject o) => o.Gta.CollisionFlagsAuto ? Auto(o) : o.Gta.CollisionFlags;

    public static uint Auto(Scenes.SceneObject o)
    {
        var mat = CollisionMaterials.NameOf(o.Gta.CollisionMaterial);
        var name = (o.Name ?? "").ToLowerInvariant();
        uint f = 0;
        if (name.Contains("stair") || name.Contains("step") || name.Contains("escalier") || name.Contains("marche") || LooksLikeStairs(o.Mesh)) f |= Stairs;
        if (mat.Contains("GLASS") || mat == "PERSPEX") f |= SeeThrough | ShootThroughFx;
        if (mat == "WATER" || name.Contains("water") || name.Contains("eau")) f |= NoDecal | NotClimbable;
        if (mat is "BUSHES" or "LEAVES" or "TWIGS" or "GRASS_LONG" or "HAY") f |= SeeThrough | ShootThrough | NotCover;
        if (mat.Contains("CHAINLINK") || mat.Contains("FENCE")) f |= SeeThrough | ShootThrough;
        return f;
    }

    /// <summary>Several flat levels a few centimetres apart, each about one step high: a staircase.</summary>
    public static bool LooksLikeStairs(Mesh.PolyMesh m)
    {
        if (m == null || m.Faces.Count < 6) return false;
        var levels = new List<float>();
        for (int i = 0; i < m.Faces.Count; i++)
        {
            if (m.FaceNormal(i).Z < 0.95f) continue;
            float z = m.FaceCenter(i).Z;
            if (!levels.Any(l => MathF.Abs(l - z) < 0.03f)) levels.Add(z);
        }
        if (levels.Count < 3) return false;
        levels.Sort();
        int steps = 0;
        for (int i = 1; i < levels.Count; i++) { float d = levels[i] - levels[i - 1]; if (d > 0.08f && d < 0.3f) steps++; }
        return steps >= 2 && steps >= (levels.Count - 1) * 0.7f;
    }
}
