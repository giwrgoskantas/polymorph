using System.Numerics;

namespace MoloMax.Core.Paint;

/// <summary>Procedural pattern of a fill layer (colour A / colour B, or bricks with their mortar).</summary>
public enum FillPattern { None, Voronoi, Cracks, Brick, Checker, Wave, Rings, Magic, Perlin }

/// <summary>
/// Procedural textures, ported from ArmorPaint (armortools, zlib licence: paint/sources/nodes_material/*_texture_node.c):
/// brick, voronoi, checker, wave, magic and the fBm noise with detail / roughness / lacunarity / distortion.
/// Inputs are object-space positions in metres; every function returns 0..1.
/// </summary>
public static class ProcTex
{
    static float Frac(float x) => x - MathF.Floor(x);
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static float Smooth(float e0, float e1, float x) { float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }

    // ---------------------------------------------------------------- noise (tex_noise_f / tex_noise_fbm)
    static float Hash(float n) => Frac(MathF.Sin(n) * 10000f);
    static readonly Vector3 Step = new(110f, 241f, 171f);
    public static float NoiseF(Vector3 x)
    {
        var i = new Vector3(MathF.Floor(x.X), MathF.Floor(x.Y), MathF.Floor(x.Z));
        var f = x - i;
        float n = Vector3.Dot(i, Step);
        var u = f * f * (new Vector3(3) - 2 * f);
        float H(float a, float b, float c) => Hash(n + Vector3.Dot(Step, new Vector3(a, b, c)));
        return Lerp(Lerp(Lerp(H(0, 0, 0), H(1, 0, 0), u.X), Lerp(H(0, 1, 0), H(1, 1, 0), u.X), u.Y),
                    Lerp(Lerp(H(0, 0, 1), H(1, 0, 1), u.X), Lerp(H(0, 1, 1), H(1, 1, 1), u.X), u.Y), u.Z);
    }

    public static float Fbm(Vector3 p, float detail, float roughness, float lacunarity)
    {
        float fscale = 1, amp = 1, maxamp = 0, sum = 0;
        int n = (int)Math.Clamp(detail, 0, 8);
        for (int i = 0; i <= n; i++) { sum += amp * NoiseF(p * fscale); maxamp += amp; amp *= roughness; fscale *= lacunarity; }
        float rmd = detail - MathF.Floor(detail);
        if (rmd > 0) { float t = NoiseF(p * fscale); return Lerp(sum / maxamp, (sum + t * amp) / (maxamp + amp), rmd); }
        return sum / maxamp;
    }

    /// <summary>Noise texture: scale, detail (octaves), roughness, distortion.</summary>
    public static float Perlin(Vector3 p, float scale, float detail = 4, float roughness = 0.5f, float distortion = 0, int seed = 0)
    {
        var pp = p * scale + new Vector3(seed * 17.13f, seed * 3.71f, seed * 9.29f);
        if (distortion != 0)
            pp += distortion * (new Vector3(NoiseF(pp), NoiseF(pp + new Vector3(0.5f, 0, 0)), NoiseF(pp + new Vector3(0, 0.5f, 0))) * 2 - Vector3.One);
        return Fbm(pp, detail, roughness, 2f);
    }

    /// <summary>Domain warp (ArmorPaint warp node): moves the lookup position by a noise vector, amount in metres.</summary>
    public static Vector3 Warp(Vector3 p, float amount, float scale, int seed)
    {
        if (amount <= 0) return p;
        var q = p * scale + new Vector3(seed * 1.7f);
        return p + amount * new Vector3(NoiseF(q) - 0.5f, NoiseF(q + new Vector3(5.2f, 1.3f, 2.8f)) - 0.5f, NoiseF(q + new Vector3(1.7f, 9.2f, 4.1f)) - 0.5f) * 2;
    }

    // ---------------------------------------------------------------- voronoi (3D, F1 / F2)
    static Vector3 Hash3(Vector3 p) => new(
        Frac(MathF.Sin(Vector3.Dot(p, new Vector3(127.1f, 311.7f, 74.7f))) * 43758.5453f),
        Frac(MathF.Sin(Vector3.Dot(p, new Vector3(269.5f, 183.3f, 246.1f))) * 43758.5453f),
        Frac(MathF.Sin(Vector3.Dot(p, new Vector3(113.5f, 271.9f, 124.6f))) * 43758.5453f));

    /// <summary>Voronoi cells: F1 distance, F2 - F1 (0 on the cell borders) and a random value per cell.</summary>
    public static (float F1, float Edge, float Cell) Voronoi(Vector3 p, float scale, float randomness = 1, int seed = 0)
    {
        p = p * scale + new Vector3(seed * 7.31f, seed * 2.17f, seed * 5.53f);
        var c = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        var l = p - c;
        float d1 = 8, d2 = 8; Vector3 best = c;
        for (int k = -1; k <= 1; k++)
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    var o = new Vector3(i, j, k);
                    var pt = o + Hash3(c + o) * randomness;
                    float d = Vector3.Distance(pt, l);
                    if (d < d1) { d2 = d1; d1 = d; best = c + o; }
                    else if (d < d2) d2 = d;
                }
        return (Math.Clamp(d1, 0, 1), Math.Clamp(d2 - d1, 0, 1), Hash3(best + new Vector3(0.37f)).X);
    }

    // ---------------------------------------------------------------- checker
    public static float Checker(Vector3 p, float scale)
    {
        p = (p + new Vector3(0.000001f * 0.999999f)) * scale;
        float xi = MathF.Abs(MathF.Floor(p.X)), yi = MathF.Abs(MathF.Floor(p.Y)), zi = MathF.Abs(MathF.Floor(p.Z));
        bool a = xi % 2 == yi % 2, b = zi % 2 != 0;
        return a == b ? 1 : 0;
    }

    // ---------------------------------------------------------------- brick (2D, on the dominant axis of the normal)
    static float BrickNoise(int n)
    {
        n = (n >> 13) ^ n;
        int nn = unchecked(n * (n * n * 60493 + 19990303) + 1376312589) & 2147483647;
        return nn / 2147483647f;
    }

    /// <summary>Bricks: Mortar is 1 in the joints; Tint is a random value per brick. scale = rows per metre.</summary>
    public static (float Mortar, float Tint) Brick(Vector2 uv, float scale, float mortarSize = 0.02f, float mortarSmooth = 0.1f,
        float brickWidth = 0.5f, float rowHeight = 0.25f, float offset = 0.5f, int offsetFrequency = 2, float squash = 1, int squashFrequency = 2)
    {
        var p = uv * scale * rowHeight; // scale = rows per metre
        float row = MathF.Floor(p.Y / rowHeight);
        float bw = brickWidth, off = 0;
        if (offsetFrequency != 0 && squashFrequency != 0)
        {
            if (Mod(row, squashFrequency) == 0) bw *= squash;
            if (Mod(row, offsetFrequency) == 0) off = bw * offset;
        }
        float col = MathF.Floor((p.X + off) / bw);
        float x = p.X + off - bw * col, y = p.Y - rowHeight * row;
        float tint = BrickNoise(((int)row << 16) + ((int)col & 65535));
        float minDist = MathF.Min(MathF.Min(x, y), MathF.Min(bw - x, rowHeight - y));
        float f = 0;
        if (minDist < mortarSize) f = mortarSmooth <= 0 ? 1 : Smooth(0, mortarSmooth, 1 - minDist / mortarSize);
        return (f, tint);
    }
    static float Mod(float a, float b) => a - b * MathF.Floor(a / b);

    /// <summary>2D coordinates on the plane the normal faces most (simple triplanar pick).</summary>
    public static Vector2 PlaneUv(Vector3 p, Vector3 n)
    {
        var a = new Vector3(MathF.Abs(n.X), MathF.Abs(n.Y), MathF.Abs(n.Z));
        return a.Z >= a.X && a.Z >= a.Y ? new(p.X, p.Y) : a.X >= a.Y ? new(p.Y, p.Z) : new(p.X, p.Z);
    }

    // ---------------------------------------------------------------- wave
    /// <summary>Bands (rings = false) or rings, with a sine profile; distortion breaks the lines with noise.</summary>
    public static float Wave(Vector3 p, float scale, bool rings, float distortion = 0, float detailScale = 1, int direction = 0)
    {
        p *= scale;
        float n;
        if (!rings) n = direction switch { 0 => p.X, 1 => p.Y, 2 => p.Z, _ => (p.X + p.Y + p.Z) * 0.57735027f };
        else
        {
            var c = p;
            if (direction == 0) c.X = 0; else if (direction == 1) c.Y = 0; else if (direction == 2) c.Z = 0;
            n = c.Length();
        }
        n *= 20f;
        if (distortion != 0) n += distortion * (Fbm(p * detailScale, 2, 0.5f, 2) * 2 - 1);
        return 0.5f + 0.5f * MathF.Sin(n - MathF.PI / 2);
    }

    // ---------------------------------------------------------------- magic
    public static float Magic(Vector3 co, float scale, float distortion = 1, int depth = 2)
    {
        var p = co * scale;
        const float tau = 6.28318530718f;
        p = new Vector3(p.X % tau, p.Y % tau, p.Z % tau);
        float x = MathF.Sin((p.X + p.Y + p.Z) * 5), y = MathF.Cos((-p.X + p.Y - p.Z) * 5), z = -MathF.Cos((-p.X - p.Y + p.Z) * 5);
        if (depth > 0)
        {
            x *= distortion; y *= distortion; z *= distortion;
            y = -MathF.Cos(x - y + z); y *= distortion;
            if (depth > 1) { x = MathF.Cos(x - y - z); x *= distortion; }
            if (depth > 2) { z = MathF.Sin(-x - y - z); z *= distortion; }
            if (depth > 3) { x = -MathF.Cos(-x + y - z); x *= distortion; }
            if (depth > 4) { y = -MathF.Sin(-x + y + z); y *= distortion; }
        }
        if (distortion != 0) { float d = distortion * 2; x /= d; y /= d; z /= d; }
        // ArmorPaint returns (0.5 - x, 0.5 - y, 0.5 - z); the grey value is their average
        return Math.Clamp(((0.5f - x) + (0.5f - y) + (0.5f - z)) / 3f, 0, 1);
    }

    /// <summary>One pattern value 0..1 at a texel (position + normal in object space).</summary>
    public static float Pattern(FillPattern kind, Vector3 p, Vector3 n, float scale, int seed, float detail = 4)
    {
        switch (kind)
        {
            case FillPattern.Voronoi: return Voronoi(p, scale, 1, seed).Cell;
            case FillPattern.Cracks: return 1 - Smooth(0, 0.08f, Voronoi(p, scale, 1, seed).Edge);
            case FillPattern.Brick: return Brick(PlaneUv(p, n), scale).Mortar;
            case FillPattern.Checker: return Checker(p, scale);
            case FillPattern.Wave: return Wave(p, scale * 0.05f, false, 2, 1, 3);
            case FillPattern.Rings: return Wave(p, scale * 0.05f, true, 1, 1, 2);
            case FillPattern.Magic: return Magic(p, scale * 0.2f, 1, 2);
            case FillPattern.Perlin: return Perlin(p, scale, detail, 0.5f, 0, seed);
            default: return 0;
        }
    }
}
