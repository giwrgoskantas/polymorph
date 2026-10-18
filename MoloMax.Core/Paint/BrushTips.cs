using System.Numerics;
using MoloMax.Core.Gta.Export;

namespace MoloMax.Core.Paint;

/// <summary>Procedural brush tips (alphas): the shape of each print, painted in the brush colour.</summary>
public static class BrushTips
{
    public static readonly string[] Names = { "Round", "Scratches", "Splatter", "Leak", "Grunge", "Dots", "Square", "Crack" };

    static readonly Dictionary<string, RgbaImage> cache = new();

    /// <summary>The tip image (256 px, white with the shape in alpha); null for the plain round brush.</summary>
    public static RgbaImage Get(string name)
    {
        if (name == null || name == "Round") return null;
        lock (cache)
        {
            if (cache.TryGetValue(name, out var img)) return img;
            img = Make(name, 256);
            cache[name] = img;
            return img;
        }
    }

    static RgbaImage Make(string name, int S)
    {
        var a = new float[S * S];
        var rnd = new Random(name.GetHashCode() & 0x7FFFFFFF);
        float Rf() => (float)rnd.NextDouble();
        void Disc(float cx, float cy, float r, float v)
        {
            int x0 = (int)(cx - r - 1), x1 = (int)(cx + r + 1), y0 = (int)(cy - r - 1), y1 = (int)(cy + r + 1);
            for (int y = Math.Max(0, y0); y <= Math.Min(S - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(S - 1, x1); x++)
                {
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float f = Math.Clamp(r - d + 0.5f, 0, 1);
                    a[x + y * S] = MathF.Max(a[x + y * S], f * v);
                }
        }
        void Line(float x0, float y0, float x1, float y1, float w, float v)
        {
            float len = MathF.Max(MathF.Abs(x1 - x0), MathF.Abs(y1 - y0));
            for (int i = 0; i <= len; i++) { float t = i / MathF.Max(len, 1); Disc(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, w * (0.6f + 0.4f * MathF.Sin(t * 3.14f)), v); }
        }
        float c = S / 2f;
        switch (name)
        {
            case "Scratches":
                for (int i = 0; i < 26; i++)
                {
                    float ang = -0.4f + Rf() * 0.8f, len = S * (0.2f + Rf() * 0.5f);
                    float x = c + (Rf() - 0.5f) * S * 0.7f, y = c + (Rf() - 0.5f) * S * 0.7f;
                    Line(x - MathF.Cos(ang) * len / 2, y - MathF.Sin(ang) * len / 2, x + MathF.Cos(ang) * len / 2, y + MathF.Sin(ang) * len / 2, 0.8f + Rf() * 1.2f, 0.6f + Rf() * 0.4f);
                }
                break;
            case "Splatter":
                Disc(c, c, S * 0.18f, 1);
                for (int i = 0; i < 70; i++)
                {
                    float ang = Rf() * MathF.Tau, dist = S * (0.12f + MathF.Pow(Rf(), 1.6f) * 0.36f);
                    Disc(c + MathF.Cos(ang) * dist, c + MathF.Sin(ang) * dist, S * (0.008f + Rf() * 0.04f) * (1 - dist / S), 1);
                }
                break;
            case "Leak":
                Disc(c, S * 0.18f, S * 0.14f, 1);
                for (int i = 0; i < 7; i++)
                {
                    float x = c + (Rf() - 0.5f) * S * 0.26f, len = S * (0.3f + Rf() * 0.5f), w = S * (0.01f + Rf() * 0.025f);
                    Line(x, S * 0.2f, x + (Rf() - 0.5f) * 6, S * 0.2f + len, w, 1);
                    Disc(x, S * 0.2f + len, w * 1.6f, 1);
                }
                break;
            case "Grunge":
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float n = Noise.Fbm(new Vector3(x * 0.025f, y * 0.025f, 0.5f), 11);
                        float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                        a[x + y * S] = Math.Clamp((n - 0.45f) * 4f, 0, 1) * Math.Clamp((1 - d) * 3, 0, 1);
                    }
                break;
            case "Dots":
                for (int i = 0; i < 120; i++) { float ang = Rf() * MathF.Tau, dist = MathF.Sqrt(Rf()) * c * 0.9f; Disc(c + MathF.Cos(ang) * dist, c + MathF.Sin(ang) * dist, 1.5f + Rf() * 3.5f, 1); }
                break;
            case "Square":
                for (int y = 0; y < S; y++) for (int x = 0; x < S; x++) a[x + y * S] = x > S * 0.08f && x < S * 0.92f && y > S * 0.08f && y < S * 0.92f ? 1 : 0;
                break;
            case "Crack":
                {
                    void Branch(float x, float y, float ang, float len, float w, int depth)
                    {
                        float step = 6;
                        for (float t = 0; t < len; t += step)
                        {
                            float nx = x + MathF.Cos(ang) * step, ny = y + MathF.Sin(ang) * step;
                            Line(x, y, nx, ny, w, 1);
                            x = nx; y = ny; ang += (Rf() - 0.5f) * 0.7f; w = MathF.Max(0.6f, w * 0.97f);
                            if (depth < 3 && Rf() < 0.06f) Branch(x, y, ang + (Rf() < 0.5f ? 0.8f : -0.8f), len * 0.5f, w * 0.7f, depth + 1);
                        }
                    }
                    for (int i = 0; i < 3; i++) Branch(c, c, i * 2.1f + Rf(), S * 0.45f, 2.2f, 0);
                    break;
                }
        }
        var img = new RgbaImage(S, S);
        for (int i = 0; i < S * S; i++) { img.Pixels[i * 4] = img.Pixels[i * 4 + 1] = img.Pixels[i * 4 + 2] = 255; img.Pixels[i * 4 + 3] = (byte)Math.Clamp(a[i] * 255, 0, 255); }
        return img;
    }
}
