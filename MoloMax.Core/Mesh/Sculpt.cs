using System.Numerics;

namespace MoloMax.Core.Mesh;

public enum SculptBrush { Draw, Smooth, Flatten, Inflate, Grab, Pinch, Relax }

/// <summary>Brush settings (radius in the mesh's own units).</summary>
public sealed class SculptSettings
{
    public SculptBrush Brush = SculptBrush.Smooth;
    public float Radius = 0.2f;
    public float Strength = 0.5f;
    public bool Invert;
    public bool SymX, SymY, SymZ;
    /// <summary>Hard (0) .. soft (1) edge of the brush.</summary>
    public float Falloff = 1f;
}

/// <summary>
/// Sculpting on a PolyMesh: basic brushes to clean generated meshes (Tripo, scans): Smooth, Relax, Flatten, Draw, Inflate, Grab, Pinch.
/// A session belongs to one stroke-series on one mesh; it keeps neighbours and a spatial grid so a dab only touches nearby vertices.
/// </summary>
public sealed class SculptSession
{
    public readonly PolyMesh Mesh;
    readonly int[][] neighbours;
    readonly Dictionary<(int, int, int), List<int>> grid = new();
    float cell;
    Vector3[] normals;
    int dabsSinceGrid;
    // grab: the vertices caught at the start and their weights
    List<(int V, float W)> grabbed;
    Vector3 grabStart;

    public SculptSession(PolyMesh mesh)
    {
        Mesh = mesh;
        var nb = new HashSet<int>[mesh.Verts.Count];
        for (int i = 0; i < nb.Length; i++) nb[i] = new HashSet<int>();
        foreach (var f in mesh.Faces)
            for (int i = 0; i < f.V.Length; i++) { int a = f.V[i], b = f.V[(i + 1) % f.V.Length]; nb[a].Add(b); nb[b].Add(a); }
        neighbours = nb.Select(s => s.ToArray()).ToArray();
        RecalcNormals();
    }

    public void RecalcNormals()
    {
        normals = new Vector3[Mesh.Verts.Count];
        foreach (var f in Mesh.Faces)
        {
            var n = Mesh.FaceNormal(f);
            foreach (var v in f.V) normals[v] += n;
        }
        for (int i = 0; i < normals.Length; i++) normals[i] = normals[i].LengthSquared() > 1e-20f ? Vector3.Normalize(normals[i]) : Vector3.UnitZ;
    }

    void BuildGrid(float radius)
    {
        cell = MathF.Max(radius, 1e-4f);
        grid.Clear();
        for (int i = 0; i < Mesh.Verts.Count; i++)
        {
            var k = Key(Mesh.Verts[i]);
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
            l.Add(i);
        }
        dabsSinceGrid = 0;
    }
    (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

    /// <summary>Vertices within <paramref name="radius"/> of <paramref name="c"/>, with their brush weight.</summary>
    public List<(int V, float W)> Gather(Vector3 c, float radius, float falloff = 1f)
    {
        if (grid.Count == 0 || MathF.Abs(cell - radius) > radius * 0.5f || dabsSinceGrid > 40) BuildGrid(radius);
        dabsSinceGrid++;
        var res = new List<(int, float)>();
        var (kx, ky, kz) = Key(c);
        int span = (int)MathF.Ceiling(radius / cell);
        float r2 = radius * radius;
        for (int x = kx - span; x <= kx + span; x++)
            for (int y = ky - span; y <= ky + span; y++)
                for (int z = kz - span; z <= kz + span; z++)
                    if (grid.TryGetValue((x, y, z), out var l))
                        foreach (var v in l)
                        {
                            float d2 = Vector3.DistanceSquared(Mesh.Verts[v], c);
                            if (d2 > r2) continue;
                            float t = MathF.Sqrt(d2) / radius;
                            // smooth falloff; falloff 0 = flat (hard) brush
                            float soft = (1 - t * t) * (1 - t * t);
                            res.Add((v, falloff * soft + (1 - falloff) * (t < 0.95f ? 1 : soft)));
                        }
        return res;
    }

    static IEnumerable<(Vector3 P, Vector3 N, Vector3 Flip)> Mirrors(Vector3 p, Vector3 n, SculptSettings s)
    {
        yield return (p, n, Vector3.One);
        var axes = new List<Vector3>();
        if (s.SymX) axes.Add(new Vector3(-1, 1, 1));
        if (s.SymY) axes.Add(new Vector3(1, -1, 1));
        if (s.SymZ) axes.Add(new Vector3(1, 1, -1));
        // every combination of the mirror axes
        int count = axes.Count;
        for (int mask = 1; mask < (1 << count); mask++)
        {
            var f = Vector3.One;
            for (int i = 0; i < count; i++) if ((mask & (1 << i)) != 0) f *= axes[i];
            yield return (p * f, n * f, f);
        }
    }

    /// <summary>One brush print at <paramref name="center"/> (mesh space) with the surface normal there. Returns the vertices moved.</summary>
    public int Dab(Vector3 center, Vector3 normal, SculptSettings s, float pressure = 1f)
    {
        int moved = 0;
        foreach (var (c, n, _) in Mirrors(center, normal, s)) moved += DabOne(c, n, s, pressure);
        return moved;
    }

    int DabOne(Vector3 c, Vector3 n, SculptSettings s, float pressure)
    {
        var hits = Gather(c, s.Radius, s.Falloff);
        if (hits.Count == 0) return 0;
        float k = Math.Clamp(s.Strength, 0, 1) * pressure;
        float sign = s.Invert ? -1 : 1;
        var V = Mesh.Verts;
        switch (s.Brush)
        {
            case SculptBrush.Draw:
                {
                    // average normal of the area, so the stroke builds a smooth ridge
                    var an = Vector3.Zero; foreach (var (v, w) in hits) an += normals[v] * w;
                    an = an.LengthSquared() > 1e-12f ? Vector3.Normalize(an) : n;
                    foreach (var (v, w) in hits) V[v] += an * (sign * k * w * s.Radius * 0.15f);
                    break;
                }
            case SculptBrush.Inflate:
                foreach (var (v, w) in hits) V[v] += normals[v] * (sign * k * w * s.Radius * 0.12f);
                break;
            case SculptBrush.Smooth:
            case SculptBrush.Relax:
                {
                    var target = new Vector3[hits.Count];
                    for (int i = 0; i < hits.Count; i++)
                    {
                        int v = hits[i].V; var nb = neighbours[v];
                        if (nb.Length == 0) { target[i] = V[v]; continue; }
                        var avg = Vector3.Zero; foreach (var q in nb) avg += V[q]; avg /= nb.Length;
                        var d = avg - V[v];
                        // Relax keeps the shape: only the part of the move along the surface
                        if (s.Brush == SculptBrush.Relax) d -= normals[v] * Vector3.Dot(d, normals[v]);
                        target[i] = V[v] + d;
                    }
                    for (int i = 0; i < hits.Count; i++) V[hits[i].V] = Vector3.Lerp(V[hits[i].V], target[i], MathF.Min(1, k * hits[i].W * 1.5f));
                    break;
                }
            case SculptBrush.Flatten:
                {
                    // plane through the weighted centre, facing the average normal
                    var pc = Vector3.Zero; var an = Vector3.Zero; float ws = 0;
                    foreach (var (v, w) in hits) { pc += V[v] * w; an += normals[v] * w; ws += w; }
                    if (ws <= 0) break;
                    pc /= ws; an = an.LengthSquared() > 1e-12f ? Vector3.Normalize(an) : n;
                    foreach (var (v, w) in hits)
                    {
                        float dist = Vector3.Dot(V[v] - pc, an);
                        if (s.Invert ? dist > 0 : false) continue;
                        V[v] -= an * (dist * MathF.Min(1, k * w));
                    }
                    break;
                }
            case SculptBrush.Pinch:
                foreach (var (v, w) in hits)
                {
                    var d = c - V[v]; d -= n * Vector3.Dot(d, n);
                    V[v] += d * (sign * k * w * 0.3f);
                }
                break;
            case SculptBrush.Grab:
                break; // see BeginGrab / DragGrab
        }
        // normals of the touched area follow (cheap: just these vertices from their faces is overkill, renormalise in place)
        if (s.Brush is SculptBrush.Draw or SculptBrush.Inflate or SculptBrush.Flatten)
            foreach (var (v, _) in hits) normals[v] = Vector3.Normalize(normals[v]);
        return hits.Count;
    }

    // ------------------------------------------------------------------ grab

    public void BeginGrab(Vector3 c, SculptSettings s) { grabbed = Gather(c, s.Radius, s.Falloff); grabStart = c; grabOrigin = grabbed.Select(h => Mesh.Verts[h.V]).ToArray(); }
    Vector3[] grabOrigin;

    /// <summary>Moves the grabbed vertices so the brush centre sits at <paramref name="c"/> (weights keep the edge soft).</summary>
    public void DragGrab(Vector3 c, SculptSettings s)
    {
        if (grabbed == null) return;
        var d = c - grabStart;
        for (int i = 0; i < grabbed.Count; i++) Mesh.Verts[grabbed[i].V] = grabOrigin[i] + d * grabbed[i].W * Math.Clamp(s.Strength * 2, 0, 1);
    }
    public void EndGrab() { grabbed = null; RecalcNormals(); }

    // ------------------------------------------------------------------ whole mesh

    /// <summary>Smooth / Relax the whole mesh a few times (cleanup of noisy generated meshes). Open borders stay put.</summary>
    public void SmoothAll(int iterations, float factor, bool relaxOnly)
    {
        var border = new bool[Mesh.Verts.Count];
        var edgeCount = new Dictionary<EdgeKey, int>();
        foreach (var f in Mesh.Faces) for (int i = 0; i < f.V.Length; i++) { var e = EdgeKey.Make(f.V[i], f.V[(i + 1) % f.V.Length]); edgeCount[e] = edgeCount.TryGetValue(e, out var c) ? c + 1 : 1; }
        foreach (var (e, c) in edgeCount) if (c == 1) { border[e.A] = true; border[e.B] = true; }
        var V = Mesh.Verts; var next = new Vector3[V.Count];
        for (int it = 0; it < iterations; it++)
        {
            if (relaxOnly) RecalcNormals();
            for (int v = 0; v < V.Count; v++)
            {
                var nb = neighbours[v];
                if (border[v] || nb.Length == 0) { next[v] = V[v]; continue; }
                var avg = Vector3.Zero; foreach (var q in nb) avg += V[q]; avg /= nb.Length;
                var d = avg - V[v];
                if (relaxOnly) d -= normals[v] * Vector3.Dot(d, normals[v]);
                next[v] = V[v] + d * factor;
            }
            for (int v = 0; v < V.Count; v++) V[v] = next[v];
        }
        RecalcNormals();
    }

    /// <summary>Mean distance between neighbours (a good default brush size is a few of these).</summary>
    public float AverageEdge()
    {
        double s = 0; int n = 0;
        for (int v = 0; v < neighbours.Length && n < 20000; v++) foreach (var q in neighbours[v]) { s += Vector3.Distance(Mesh.Verts[v], Mesh.Verts[q]); n++; }
        return n > 0 ? (float)(s / n) : 0.05f;
    }
}
