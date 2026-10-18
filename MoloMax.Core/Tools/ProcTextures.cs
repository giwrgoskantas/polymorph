using System.Numerics;
using MoloMax.Core.Gta.Export;

namespace MoloMax.Core.Tools;

/// <summary>
/// Procedural textures for the Molo Tools (all generated, no game textures shipped): AO and dirt gradients,
/// chipped-edge and rounded-edge normal maps, blob shadows, glow halos, and maps derived from a diffuse.
/// Rows are top-down (row 0 = V 0), the DirectX convention GTA uses.
/// </summary>
public static class ProcTextures
{
    static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>Strip gradient across V (edge at V 0.5): black, alpha from centre to outer. Tiles along U.</summary>
    public static RgbaImage StripGradient(float alphaCenter, float alphaOuter, Vector3 colour, int w = 16, int h = 256)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h, c = MathF.Abs(2 * v - 1);
            float a = alphaCenter + (alphaOuter - alphaCenter) * Smooth(c);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                img.Pixels[i] = B(colour.X); img.Pixels[i + 1] = B(colour.Y); img.Pixels[i + 2] = B(colour.Z); img.Pixels[i + 3] = B(a);
            }
        }
        return img;
    }

    /// <summary>Dirt strip: dark grime with noise breaking up the alpha, denser at the edge (V 0.5).</summary>
    public static RgbaImage DirtStrip(float alphaCenter, float alphaOuter, Vector3 colour, int seed, int w = 256, int h = 256)
    {
        var img = new RgbaImage(w, h);
        var n1 = new ValueNoise(seed, 8); var n2 = new ValueNoise(seed + 1, 32);
        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) / h, c = MathF.Abs(2 * v - 1);
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;
                float nz = 0.6f * n1.Sample(u, v) + 0.4f * n2.Sample(u, v);
                float a = (alphaCenter + (alphaOuter - alphaCenter) * Smooth(c)) * Math.Clamp(0.45f + nz * 0.9f - c * 0.3f, 0, 1);
                float shade = 0.75f + 0.5f * nz;
                int i = (y * w + x) * 4;
                img.Pixels[i] = B(colour.X * shade); img.Pixels[i + 1] = B(colour.Y * shade); img.Pixels[i + 2] = B(colour.Z * shade); img.Pixels[i + 3] = B(a);
            }
        }
        return img;
    }

    /// <summary>
    /// Edge normal map across V (edge at V 0.5): a rounded bevel, plus chips when damage &gt; 0.
    /// Alpha fades from centre to outer so the decal blends. Normals are encoded 0.5 + 0.5 n (DirectX, green down).
    /// </summary>
    public static RgbaImage EdgeNormal(float strength, float damage, int grain, int seed, float alphaCenter = 0.85f, float alphaOuter = 0f, int size = 256)
    {
        int w = size, h = size;
        var height = new float[w * h];
        var alpha = new float[w * h];
        var n1 = new ValueNoise(seed, grain); var n2 = new ValueNoise(seed + 1, grain * 4);
        float depth = 0.5f * MathF.Abs(strength);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w, v = (y + 0.5f) / h, c = MathF.Abs(2 * v - 1);
                float mask = MathF.Pow(1 - c, 1.5f);
                float nz = 0.65f * n1.Sample(u, v) + 0.35f * n2.Sample(u, v);
                float chip = MathF.Max(0, nz - 0.45f) / 0.55f;
                height[y * w + x] = -(1 - Smooth(c)) * depth - damage * mask * chip * depth * 1.4f;
                alpha[y * w + x] = (alphaCenter + (alphaOuter - alphaCenter) * Smooth(c)) * (1 - damage * 0.45f * (1 - MathF.Min(1, nz * 1.6f)));
            }
        var rnd = new Random(seed + 2);
        float sign = strength < 0 ? -1 : 1;
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int xr = (x + 1) % w, yd = Math.Min(h - 1, y + 1);
                float dx = (height[y * w + xr] - height[y * w + x]) * w;
                float dy = (height[yd * w + x] - height[y * w + x]) * h;
                float c = MathF.Abs(2 * ((y + 0.5f) / h) - 1), mask = MathF.Pow(1 - c, 1.5f);
                float gx = (float)(rnd.NextDouble() - 0.5) * 0.6f * damage * mask, gy = (float)(rnd.NextDouble() - 0.5) * 0.6f * damage * mask;
                var n = Vector3.Normalize(new Vector3((-dx * 1.2f + gx) * sign, (-dy * 1.2f + gy) * sign, 1));
                int i = (y * w + x) * 4;
                img.Pixels[i] = B(0.5f + 0.5f * n.X); img.Pixels[i + 1] = B(0.5f + 0.5f * n.Y); img.Pixels[i + 2] = B(0.5f + 0.5f * n.Z); img.Pixels[i + 3] = B(alpha[y * w + x]);
            }
        return img;
    }

    /// <summary>Soft blob shadow: black, alpha 1 inside a superellipse, fading to 0 at its border.</summary>
    public static RgbaImage BlobShadow(float opacity, float softness, float roundness, int size = 128)
    {
        var img = new RgbaImage(size, size);
        float n = MathF.Max(roundness, 0.5f), inner = 1 - Math.Clamp(softness, 0.01f, 1f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2 - 1, v = (y + 0.5f) / size * 2 - 1;
                float d = MathF.Pow(MathF.Pow(MathF.Abs(u), n) + MathF.Pow(MathF.Abs(v), n), 1 / n);
                float a = d <= inner ? 1 : d >= 1 ? 0 : 1 - Smooth((d - inner) / MathF.Max(1e-4f, 1 - inner));
                img.Pixels[(y * size + x) * 4 + 3] = B(opacity * a);
            }
        return img;
    }

    /// <summary>Three passes of a separable box blur (close to a Gaussian), clamped at the borders.</summary>
    public static float[] Blur(float[] src, int w, int h, int r)
    {
        if (r <= 0) return (float[])src.Clone();
        var a = (float[])src.Clone(); var tmp = new float[a.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int y = 0; y < h; y++)
            {
                float sum = 0; int win = 2 * r + 1;
                for (int k = -r; k <= r; k++) sum += a[y * w + Math.Clamp(k, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    tmp[y * w + x] = sum / win;
                    sum += a[y * w + Math.Clamp(x + r + 1, 0, w - 1)] - a[y * w + Math.Clamp(x - r, 0, w - 1)];
                }
            }
            for (int x = 0; x < w; x++)
            {
                float sum = 0; int win = 2 * r + 1;
                for (int k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    a[y * w + x] = sum / win;
                    sum += tmp[Math.Clamp(y + r + 1, 0, h - 1) * w + x] - tmp[Math.Clamp(y - r, 0, h - 1) * w + x];
                }
            }
        }
        return a;
    }

    /// <summary>Glow halo from a silhouette mask: a tight core plus a wide halo, colour premultiplied.</summary>
    public static RgbaImage Halo(float[] mask, int w, int h, Vector3 colour, float coreSize, float coreIntensity, float haloSize, float haloIntensity, float opacity)
    {
        int b = Math.Max(w, h);
        var core = Blur(mask, w, h, Math.Max(1, (int)(b * coreSize)));
        var halo = Blur(mask, w, h, Math.Max(2, (int)(b * haloSize)));
        var img = new RgbaImage(w, h);
        for (int i = 0; i < w * h; i++)
        {
            float v = Math.Clamp(core[i] * coreIntensity + halo[i] * haloIntensity, 0, 1) * opacity;
            img.Pixels[i * 4] = B(v * colour.X); img.Pixels[i * 4 + 1] = B(v * colour.Y); img.Pixels[i * 4 + 2] = B(v * colour.Z); img.Pixels[i * 4 + 3] = B(v);
        }
        return img;
    }

    // ------------------------------------------------------------------ maps from a diffuse (Texturizer)

    static float[] Gray(RgbaImage img)
    {
        var g = new float[img.Width * img.Height];
        for (int i = 0; i < g.Length; i++) g[i] = (img.Pixels[i * 4] * 0.299f + img.Pixels[i * 4 + 1] * 0.587f + img.Pixels[i * 4 + 2] * 0.114f) / 255f;
        return g;
    }

    /// <summary>Height from a diffuse: multi-scale detail bands (fine detail weighs less than big shapes), normalised.</summary>
    public static float[] HeightFromDiffuse(RgbaImage img, float contrast = 1.5f, bool invert = false)
    {
        int w = img.Width, h = img.Height;
        var g = Gray(img);
        float res = MathF.Min(w, h) / 512f;
        float[] radii = { 1, 1.4f, 2.6f, 4.5f, 7.9f, 15, 28 };
        float[] weights = { 0.15f, 0.19f, 0.30f, 0.50f, 0.70f, 0.90f, 1f };
        var prev = g; var height = new float[g.Length];
        for (int b = 0; b < radii.Length; b++)
        {
            var bl = Blur(g, w, h, Math.Max(1, (int)MathF.Round(radii[b] * res)));
            for (int i = 0; i < g.Length; i++) height[i] += (prev[i] - bl[i]) * weights[b];
            prev = bl;
        }
        // normalise to the 1st..99th percentile
        var sorted = (float[])height.Clone(); Array.Sort(sorted);
        float lo = sorted[(int)(sorted.Length * 0.01f)], hi = sorted[(int)(sorted.Length * 0.99f)];
        float span = MathF.Max(1e-5f, hi - lo);
        for (int i = 0; i < height.Length; i++)
        {
            float v = (height[i] - lo) / span;
            v = Math.Clamp((v - 0.5f) * contrast + 0.5f, 0, 1);
            height[i] = invert ? 1 - v : v;
        }
        return height;
    }

    /// <summary>Normal map (DirectX, green down) from a height field.</summary>
    public static RgbaImage NormalFromHeight(float[] height, int w, int h, float strength = 4f)
    {
        var hb = Blur(height, w, h, 1);
        var img = new RgbaImage(w, h);
        float s = strength * MathF.Max(w, h) / 512f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float l = hb[y * w + (x - 1 + w) % w], r = hb[y * w + (x + 1) % w];
                float u = hb[((y - 1 + h) % h) * w + x], d = hb[((y + 1) % h) * w + x];
                var n = Vector3.Normalize(new Vector3(-(r - l) * s, -(d - u) * s, 1));
                int i = (y * w + x) * 4;
                img.Pixels[i] = B(0.5f + 0.5f * n.X); img.Pixels[i + 1] = B(0.5f + 0.5f * n.Y); img.Pixels[i + 2] = B(0.5f + 0.5f * n.Z); img.Pixels[i + 3] = 255;
            }
        return img;
    }

    /// <summary>Specular map from a diffuse: bright where local detail is lighter than its surroundings.</summary>
    public static RgbaImage SpecFromDiffuse(RgbaImage img, float contrast = 1.2f)
    {
        int w = img.Width, h = img.Height;
        var g = Gray(img);
        var bl = Blur(g, w, h, Math.Max(1, (int)(4 * MathF.Min(w, h) / 512f)));
        var o = new RgbaImage(w, h);
        for (int i = 0; i < g.Length; i++)
        {
            float v = Math.Clamp(0.35f + (g[i] - bl[i]) * 2f, 0, 1);
            v = Math.Clamp((v - 0.5f) * contrast + 0.5f, 0, 1);
            o.Pixels[i * 4] = o.Pixels[i * 4 + 1] = o.Pixels[i * 4 + 2] = B(v); o.Pixels[i * 4 + 3] = 255;
        }
        return o;
    }
}

/// <summary>Tileable value noise on a grid (wraps in both directions).</summary>
public sealed class ValueNoise
{
    readonly float[] grid; readonly int size;
    public ValueNoise(int seed, int size)
    {
        this.size = Math.Max(2, size);
        var r = new Random(seed);
        grid = new float[this.size * this.size];
        for (int i = 0; i < grid.Length; i++) grid[i] = (float)r.NextDouble();
    }
    public float Sample(float u, float v)
    {
        float x = u * size, y = v * size;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float tx = x - x0, ty = y - y0;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        float G(int i, int j) => grid[((j % size + size) % size) * size + ((i % size + size) % size)];
        float a = G(x0, y0) + (G(x0 + 1, y0) - G(x0, y0)) * tx;
        float b = G(x0, y0 + 1) + (G(x0 + 1, y0 + 1) - G(x0, y0 + 1)) * tx;
        return a + (b - a) * ty;
    }
}
