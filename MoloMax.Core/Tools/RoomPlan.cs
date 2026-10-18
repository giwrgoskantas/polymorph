using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Tools;

/// <summary>A room drawn on the floor plan (metres, world X / Y).</summary>
public sealed class PlanRoom
{
    public string Name { get; set; } = "room";
    public float X0 { get; set; }
    public float Y0 { get; set; }
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float Height { get; set; } = 3f;
    public float FloorZ { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public Vector2 Min => new(MathF.Min(X0, X1), MathF.Min(Y0, Y1));
    [System.Text.Json.Serialization.JsonIgnore] public Vector2 Max => new(MathF.Max(X0, X1), MathF.Max(Y0, Y1));
}

/// <summary>A door or window on a wall line of the plan.</summary>
public sealed class PlanOpening
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 1.2f;
    public float Height { get; set; } = 2.3f;
    public float Sill { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Window => Sill > 0.01f;
}

public sealed class RoomPlanData
{
    public List<PlanRoom> Rooms { get; set; } = new();
    public List<PlanOpening> Openings { get; set; } = new();
    /// <summary>Gap between two rooms (Molo rule: rooms never touch, a floor links them).</summary>
    public float Gap { get; set; } = 0.1f;
    /// <summary>World point the plan stands on (MAP mode: the rooms are built right there on the map).</summary>
    public float OriginX { get; set; }
    public float OriginY { get; set; }
    public float OriginZ { get; set; }
    /// <summary>Size references (ped, desk, chair, bed...) placed on the plan to check the dimensions.</summary>
    public List<PlanProp> Props { get; set; } = new();
    /// <summary>Floor plan picture traced under the plan: file, real width in metres, opacity.</summary>
    public string Photo { get; set; }
    public float PhotoWidth { get; set; } = 12f;
    public float PhotoX { get; set; }
    public float PhotoY { get; set; }
    public float PhotoOpacity { get; set; } = 0.6f;
}

/// <summary>A size reference standing on the plan (centre, rotation in degrees).</summary>
public sealed class PlanProp
{
    public string Kind { get; set; } = "ped";
    public float X { get; set; }
    public float Y { get; set; }
    public float Rotation { get; set; }
    public float FloorZ { get; set; }
}

/// <summary>Real sizes of the things a room must fit (GTA scale), width x depth x height in metres.</summary>
public static class PlanReferences
{
    public sealed record Ref(string Kind, string Label, float W, float D, float H, uint Colour);
    public static readonly Ref[] All =
    {
        new("ped", "Ped (1.80 m)", 0.5f, 0.35f, 1.8f, 0xFF40C0FF),
        new("desk", "Desk", 1.6f, 0.8f, 0.75f, 0xFF4090E0),
        new("chair", "Office chair", 0.6f, 0.6f, 1.1f, 0xFF60A0F0),
        new("table", "Table (6 seats)", 1.8f, 0.9f, 0.75f, 0xFF3080D0),
        new("sofa", "Sofa", 2.2f, 0.9f, 0.85f, 0xFFB070E0),
        new("bed", "Double bed", 1.6f, 2.0f, 0.55f, 0xFFA060D0),
        new("counter", "Counter / bar", 2.4f, 0.6f, 1.05f, 0xFF50C090),
        new("locker", "Locker", 0.4f, 0.5f, 1.8f, 0xFF70B070),
        new("door", "Door swing (0.9 m)", 0.9f, 0.9f, 2.1f, 0xFF40FF80),
        new("car", "Car", 4.6f, 1.95f, 1.45f, 0xFF4040E0),
        new("stairs", "Stairs (1 floor)", 1.2f, 4.2f, 3.0f, 0xFF20A0A0),
    };
    public static Ref Of(string kind) => All.FirstOrDefault(r => r.Kind == kind) ?? All[0];
}

/// <summary>
/// Turns a floor plan into Molo-style MLO rooms: every room a closed box with its normals inward, a small gap
/// between rooms, doors and windows cut through both walls, and a short closed passage (floor, sides, lintel)
/// bridging the gap at every door so nothing is seen through.
/// </summary>
public static class RoomPlanBuilder
{
    public sealed class Built
    {
        public List<(string Name, PolyMesh Mesh, Vector3 Position)> Rooms = new();
        public List<(string Name, PolyMesh Mesh, Vector3 Position)> Links = new();
        public List<string> Warnings = new();
    }

    public static Built Build(RoomPlanData plan)
    {
        var res = new Built();
        float g = MathF.Max(0.02f, plan.Gap) * 0.5f;
        var rects = new List<(PlanRoom R, Vector2 Min, Vector2 Max)>();
        foreach (var r in plan.Rooms)
        {
            var mn = r.Min + new Vector2(g); var mx = r.Max - new Vector2(g);
            if (mx.X - mn.X < 0.5f || mx.Y - mn.Y < 0.5f) { res.Warnings.Add($"{r.Name} is too small."); continue; }
            rects.Add((r, mn, mx));
        }
        var meshes = new List<PolyMesh>(); var centres = new List<Vector3>();
        foreach (var (r, mn, mx) in rects)
        {
            var c = new Vector3((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f, r.FloorZ);
            meshes.Add(Primitives.Room(mx.X - mn.X, mx.Y - mn.Y, r.Height));
            centres.Add(c);
        }
        // openings: cut each wall the opening stands on (one room = exterior door / window, two rooms = inner door)
        foreach (var op in plan.Openings)
        {
            var p = new Vector2(op.X, op.Y);
            var hits = new List<int>();
            for (int i = 0; i < rects.Count; i++)
            {
                var (r, mn, mx) = rects[i];
                float tol = g + 0.35f;
                bool nearX = (MathF.Abs(p.X - mn.X) < tol || MathF.Abs(p.X - mx.X) < tol) && p.Y > mn.Y - tol && p.Y < mx.Y + tol;
                bool nearY = (MathF.Abs(p.Y - mn.Y) < tol || MathF.Abs(p.Y - mx.Y) < tol) && p.X > mn.X - tol && p.X < mx.X + tol;
                if (nearX || nearY) hits.Add(i);
            }
            if (hits.Count == 0) { res.Warnings.Add($"Opening at {op.X:0.0}, {op.Y:0.0} is not on a wall."); continue; }
            Vector3 normalAxis = Vector3.Zero;
            foreach (var i in hits)
            {
                var m = meshes[i]; var c = centres[i];
                var local = new Vector3(p.X - c.X, p.Y - c.Y, 0);
                // the wall face whose plane is closest to the point
                int best = -1; float bd = float.MaxValue;
                for (int fi = 0; fi < m.Faces.Count; fi++)
                {
                    var n = m.FaceNormal(fi); if (MathF.Abs(n.Z) > 0.5f) continue;
                    var fc = m.FaceCenter(fi);
                    float d = MathF.Abs(Vector3.Dot(local - fc, n));
                    var along = Vector3.Cross(Vector3.UnitZ, n);
                    if (MathF.Abs(Vector3.Dot(local - fc, along)) > HalfLen(m, fi, along) + 0.01f) continue;
                    if (d < bd) { bd = d; best = fi; }
                }
                if (best < 0 || bd > g + 0.4f) continue;
                var fn = m.FaceNormal(best); var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ - fn * Vector3.Dot(Vector3.UnitZ, fn), fn));
                float offset = Vector3.Dot(local - m.FaceCenter(best), right);
                var cut = Doorway.Cut(m, best, op.Width, op.Height, offset, op.Sill);
                if (!cut.Ok) res.Warnings.Add($"{rects[i].R.Name}: {cut.Message}");
                normalAxis = fn;
            }
            // passage across the gap for inner openings
            if (hits.Count >= 2 && normalAxis != Vector3.Zero)
            {
                var axis = MathF.Abs(normalAxis.X) > MathF.Abs(normalAxis.Y) ? Vector3.UnitX : Vector3.UnitY;
                float z = MathF.Max(rects[hits[0]].R.FloorZ, rects[hits[1]].R.FloorZ);
                // the passage spans the real space between the two walls (rooms drawn apart get a longer one)
                bool ax = axis == Vector3.UnitX; float pa = ax ? p.X : p.Y;
                float WallAt(int i) { var (_, mn, mx) = rects[i]; float a0 = ax ? mn.X : mn.Y, a1 = ax ? mx.X : mx.Y; return MathF.Abs(pa - a0) < MathF.Abs(pa - a1) ? a0 : a1; }
                float w0 = WallAt(hits[0]), w1 = WallAt(hits[1]);
                float depth = MathF.Max(MathF.Abs(w1 - w0), g * 2) + 0.002f, mid = (w0 + w1) * 0.5f;
                var at = ax ? new Vector3(mid, p.Y, z) : new Vector3(p.X, mid, z);
                res.Links.Add(($"link_{res.Links.Count + 1}", Passage(axis, op.Width, op.Height, op.Sill, depth), at));
            }
        }
        for (int i = 0; i < rects.Count; i++) res.Rooms.Add((rects[i].R.Name, meshes[i], centres[i]));
        var origin = new Vector3(plan.OriginX, plan.OriginY, plan.OriginZ);
        if (origin != Vector3.Zero)
        {
            for (int i = 0; i < res.Rooms.Count; i++) res.Rooms[i] = (res.Rooms[i].Name, res.Rooms[i].Mesh, res.Rooms[i].Position + origin);
            for (int i = 0; i < res.Links.Count; i++) res.Links[i] = (res.Links[i].Name, res.Links[i].Mesh, res.Links[i].Position + origin);
        }
        return res;
    }

    static float HalfLen(PolyMesh m, int fi, Vector3 along)
    {
        var f = m.Faces[fi]; var c = m.FaceCenter(fi); float mx = 0;
        foreach (var v in f.V) mx = MathF.Max(mx, MathF.Abs(Vector3.Dot(m.Verts[v] - c, along)));
        return mx;
    }

    /// <summary>Open tube across the gap: floor, two sides and lintel, faces looking inward (walked through).</summary>
    static PolyMesh Passage(Vector3 across, float width, float height, float sill, float depth)
    {
        var side = Vector3.Cross(Vector3.UnitZ, across);
        float hw = width * 0.5f, hd = depth * 0.5f, z0 = sill, z1 = sill + height;
        var m = new PolyMesh();
        Vector3 P(float s, float a, float z) => side * s + across * a + Vector3.UnitZ * z;
        int[] Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => new[] { m.AddVertex(a), m.AddVertex(b), m.AddVertex(c), m.AddVertex(d) };
        // floor (up), lintel (down), sides (inward)
        m.AddFace(Quad(P(-hw, -hd, z0), P(hw, -hd, z0), P(hw, hd, z0), P(-hw, hd, z0)));
        m.AddFace(Quad(P(-hw, hd, z1), P(hw, hd, z1), P(hw, -hd, z1), P(-hw, -hd, z1)));
        m.AddFace(Quad(P(-hw, -hd, z0), P(-hw, hd, z0), P(-hw, hd, z1), P(-hw, -hd, z1)));
        m.AddFace(Quad(P(hw, hd, z0), P(hw, -hd, z0), P(hw, -hd, z1), P(hw, hd, z1)));
        // make every face look into the passage
        var centre = Vector3.UnitZ * (z0 + z1) * 0.5f;
        foreach (var f in m.Faces) if (Vector3.Dot(m.FaceNormal(f), centre - m.FaceCenter(f)) < 0) f.Flip();
        MeshOps.WeldByDistance(m, 1e-5f);
        return m;
    }
}
