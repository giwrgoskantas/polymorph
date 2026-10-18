namespace MoloMax.Core.Gta.Export;

/// <summary>An RGBA8 image (row-major, top row first).</summary>
public sealed class RgbaImage
{
    public int Width, Height;
    public byte[] Pixels; // 4 bytes per pixel, R G B A

    public RgbaImage(int w, int h) { Width = w; Height = h; Pixels = new byte[w * h * 4]; }

    public static RgbaImage Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var img = new RgbaImage(w, h);
        for (int i = 0; i < w * h; i++) { img.Pixels[i * 4] = r; img.Pixels[i * 4 + 1] = g; img.Pixels[i * 4 + 2] = b; img.Pixels[i * 4 + 3] = a; }
        return img;
    }

    public bool HasAlpha(byte threshold = 250)
    {
        for (int i = 3; i < Pixels.Length; i += 4) if (Pixels[i] < threshold) return true;
        return false;
    }

    /// <summary>Box-filtered half-size copy (for mipmaps).</summary>
    public RgbaImage Half()
    {
        int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2);
        var r = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0, n = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(Width - 1, x * 2 + dx), sy = Math.Min(Height - 1, y * 2 + dy);
                            sum += Pixels[(sy * Width + sx) * 4 + c]; n++;
                        }
                    r.Pixels[(y * w + x) * 4 + c] = (byte)((sum + n / 2) / n);
                }
        return r;
    }

    /// <summary>Bilinear resize (used to bring textures to a power of two, e.g. 1024).</summary>
    public RgbaImage Resize(int w, int h)
    {
        if (w == Width && h == Height) return this;
        var r = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        {
            float fy = (y + 0.5f) * Height / h - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, Height - 1), y1 = Math.Min(y0 + 1, Height - 1);
            float ty = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) * Width / w - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, Width - 1), x1 = Math.Min(x0 + 1, Width - 1);
                float tx = Math.Clamp(fx - x0, 0, 1);
                for (int c = 0; c < 4; c++)
                {
                    float a = Pixels[(y0 * Width + x0) * 4 + c] * (1 - tx) + Pixels[(y0 * Width + x1) * 4 + c] * tx;
                    float b = Pixels[(y1 * Width + x0) * 4 + c] * (1 - tx) + Pixels[(y1 * Width + x1) * 4 + c] * tx;
                    r.Pixels[(y * w + x) * 4 + c] = (byte)Math.Clamp(a * (1 - ty) + b * ty + 0.5f, 0, 255);
                }
            }
        }
        return r;
    }
}

public enum DdsFormat { Dxt1, Dxt5, A8R8G8B8 }

/// <summary>
/// Writes legacy DDS files (D3D9 FourCC headers, no DX10 header) with a full mip chain, the way GTA V wants them:
/// DXT1 for opaque textures, DXT5 when there is transparency.
/// </summary>
public static class DdsEncoder
{
    public static int NextPow2(int v) { int p = 1; while (p < v) p <<= 1; return p; }

    /// <summary>Picks DXT1 or DXT5 from the alpha channel, as the Molo DDS rules say.</summary>
    public static DdsFormat AutoFormat(RgbaImage img) => img.HasAlpha() ? DdsFormat.Dxt5 : DdsFormat.Dxt1;

    /// <param name="maxSize">Largest side after resizing to a power of two (GTA rule: 1024).</param>
    public static byte[] Encode(RgbaImage img, DdsFormat format, int maxSize = 1024, bool mips = true)
    {
        int w = Math.Min(NextPow2(img.Width), maxSize), h = Math.Min(NextPow2(img.Height), maxSize);
        if (format != DdsFormat.A8R8G8B8) { w = Math.Max(4, w); h = Math.Max(4, h); }
        var level = img.Resize(w, h);
        var levels = new List<RgbaImage> { level };
        if (mips)
            while (level.Width > 1 || level.Height > 1)
            {
                level = level.Half();
                levels.Add(level);
                if (format != DdsFormat.A8R8G8B8 && level.Width <= 4 && level.Height <= 4) break; // stop at the 4x4 block
            }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteHeader(bw, w, h, levels.Count, format);
        foreach (var l in levels)
        {
            switch (format)
            {
                case DdsFormat.Dxt1: bw.Write(CompressBlocks(l, false)); break;
                case DdsFormat.Dxt5: bw.Write(CompressBlocks(l, true)); break;
                default:
                    for (int i = 0; i < l.Width * l.Height; i++)
                    {
                        bw.Write(l.Pixels[i * 4 + 2]); bw.Write(l.Pixels[i * 4 + 1]); bw.Write(l.Pixels[i * 4]); bw.Write(l.Pixels[i * 4 + 3]);
                    }
                    break;
            }
        }
        return ms.ToArray();
    }

    static void WriteHeader(BinaryWriter bw, int w, int h, int mipCount, DdsFormat f)
    {
        const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PITCH = 0x8, DDSD_PIXELFORMAT = 0x1000, DDSD_MIPMAPCOUNT = 0x20000, DDSD_LINEARSIZE = 0x80000;
        bool compressed = f != DdsFormat.A8R8G8B8;
        bw.Write(0x20534444u); // "DDS "
        bw.Write(124u);
        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | (mipCount > 1 ? DDSD_MIPMAPCOUNT : 0) | (compressed ? DDSD_LINEARSIZE : DDSD_PITCH);
        bw.Write(flags);
        bw.Write((uint)h); bw.Write((uint)w);
        uint pitch = compressed ? (uint)(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (f == DdsFormat.Dxt1 ? 8 : 16)) : (uint)(w * 4);
        bw.Write(pitch);
        bw.Write(0u); // depth
        bw.Write((uint)mipCount);
        for (int i = 0; i < 11; i++) bw.Write(0u);
        // pixel format
        bw.Write(32u);
        if (compressed)
        {
            bw.Write(0x4u); // DDPF_FOURCC
            bw.Write(f == DdsFormat.Dxt1 ? 0x31545844u : 0x35545844u); // DXT1 / DXT5
            bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
        }
        else
        {
            bw.Write(0x41u); // DDPF_RGB | DDPF_ALPHAPIXELS
            bw.Write(0u);
            bw.Write(32u);
            bw.Write(0x00ff0000u); bw.Write(0x0000ff00u); bw.Write(0x000000ffu); bw.Write(0xff000000u);
        }
        uint caps = 0x1000 | (mipCount > 1 ? 0x400008u : 0u); // TEXTURE | (MIPMAP | COMPLEX)
        bw.Write(caps); bw.Write(0u); bw.Write(0u); bw.Write(0u); bw.Write(0u);
    }

    // ------------------------------------------------------------------ block compression

    static byte[] CompressBlocks(RgbaImage img, bool dxt5)
    {
        int bw = Math.Max(1, (img.Width + 3) / 4), bh = Math.Max(1, (img.Height + 3) / 4);
        var output = new byte[bw * bh * (dxt5 ? 16 : 8)];
        Parallel.For(0, bh, by =>
        {
            var blk = new byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(img.Width - 1, bx * 4 + x), sy = Math.Min(img.Height - 1, by * 4 + y);
                        Buffer.BlockCopy(img.Pixels, (sy * img.Width + sx) * 4, blk, (y * 4 + x) * 4, 4);
                    }
                int o = (by * bw + bx) * (dxt5 ? 16 : 8);
                if (dxt5) { EncodeAlpha(blk, output, o); EncodeColour(blk, output, o + 8, false); }
                else EncodeColour(blk, output, o, true);
            }
        });
        return output;
    }

    static ushort To565(int r, int g, int b) => (ushort)(((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255));
    static void From565(ushort c, out int r, out int g, out int b)
    {
        r = (c >> 11) & 31; g = (c >> 5) & 63; b = c & 31;
        r = (r << 3) | (r >> 2); g = (g << 2) | (g >> 4); b = (b << 3) | (b >> 2);
    }

    /// <summary>Colour block: endpoints along the principal axis of the 16 colours, then best-fit indices.</summary>
    static void EncodeColour(byte[] blk, byte[] output, int o, bool allowPunchThrough)
    {
        // punch-through alpha for DXT1 (cutout textures exported as DXT1 keep hard edges)
        bool anyTransparent = false;
        if (allowPunchThrough) for (int i = 0; i < 16; i++) if (blk[i * 4 + 3] < 128) { anyTransparent = true; break; }

        float mr = 0, mg = 0, mb = 0; int n = 0;
        for (int i = 0; i < 16; i++)
        {
            if (anyTransparent && blk[i * 4 + 3] < 128) continue;
            mr += blk[i * 4]; mg += blk[i * 4 + 1]; mb += blk[i * 4 + 2]; n++;
        }
        if (n == 0) { WriteColourBlock(output, o, 0, 0, uint.MaxValue); return; }
        mr /= n; mg /= n; mb /= n;
        float crr = 0, cgg = 0, cbb = 0, crg = 0, crb = 0, cgb = 0;
        for (int i = 0; i < 16; i++)
        {
            if (anyTransparent && blk[i * 4 + 3] < 128) continue;
            float r = blk[i * 4] - mr, g = blk[i * 4 + 1] - mg, b = blk[i * 4 + 2] - mb;
            crr += r * r; cgg += g * g; cbb += b * b; crg += r * g; crb += r * b; cgb += g * b;
        }
        // power iteration for the principal axis
        float ar = 1, ag = 1, ab = 1;
        for (int k = 0; k < 6; k++)
        {
            float nr = crr * ar + crg * ag + crb * ab, ng = crg * ar + cgg * ag + cgb * ab, nb = crb * ar + cgb * ag + cbb * ab;
            float len = MathF.Sqrt(nr * nr + ng * ng + nb * nb);
            if (len < 1e-6f) { ar = 0.577f; ag = 0.577f; ab = 0.577f; break; }
            ar = nr / len; ag = ng / len; ab = nb / len;
        }
        float tmin = float.MaxValue, tmax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            if (anyTransparent && blk[i * 4 + 3] < 128) continue;
            float t = (blk[i * 4] - mr) * ar + (blk[i * 4 + 1] - mg) * ag + (blk[i * 4 + 2] - mb) * ab;
            tmin = MathF.Min(tmin, t); tmax = MathF.Max(tmax, t);
        }
        int C(float v) => Math.Clamp((int)MathF.Round(v), 0, 255);
        ushort c0 = To565(C(mr + ar * tmax), C(mg + ag * tmax), C(mb + ab * tmax));
        ushort c1 = To565(C(mr + ar * tmin), C(mg + ag * tmin), C(mb + ab * tmin));

        bool threeColour = anyTransparent;
        if (threeColour) { if (c0 > c1) (c0, c1) = (c1, c0); }
        else
        {
            if (c0 < c1) (c0, c1) = (c1, c0);
            if (c0 == c1) { if (c1 > 0) c1--; else c0++; }
        }
        From565(c0, out int r0, out int g0, out int b0);
        From565(c1, out int r1, out int g1, out int b1);
        Span<int> pr = stackalloc int[4], pg = stackalloc int[4], pb = stackalloc int[4];
        pr[0] = r0; pg[0] = g0; pb[0] = b0; pr[1] = r1; pg[1] = g1; pb[1] = b1;
        if (!threeColour)
        {
            pr[2] = (2 * r0 + r1) / 3; pg[2] = (2 * g0 + g1) / 3; pb[2] = (2 * b0 + b1) / 3;
            pr[3] = (r0 + 2 * r1) / 3; pg[3] = (g0 + 2 * g1) / 3; pb[3] = (b0 + 2 * b1) / 3;
        }
        else { pr[2] = (r0 + r1) / 2; pg[2] = (g0 + g1) / 2; pb[2] = (b0 + b1) / 2; pr[3] = pg[3] = pb[3] = 0; }
        uint idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            if (threeColour && blk[i * 4 + 3] < 128) best = 3;
            else
            {
                int bd = int.MaxValue, count = threeColour ? 3 : 4;
                for (int k = 0; k < count; k++)
                {
                    int dr = blk[i * 4] - pr[k], dg = blk[i * 4 + 1] - pg[k], db = blk[i * 4 + 2] - pb[k];
                    int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (d < bd) { bd = d; best = k; }
                }
            }
            idx |= (uint)best << (i * 2);
        }
        WriteColourBlock(output, o, c0, c1, idx);
    }

    static void WriteColourBlock(byte[] o, int at, ushort c0, ushort c1, uint idx)
    {
        o[at] = (byte)c0; o[at + 1] = (byte)(c0 >> 8); o[at + 2] = (byte)c1; o[at + 3] = (byte)(c1 >> 8);
        o[at + 4] = (byte)idx; o[at + 5] = (byte)(idx >> 8); o[at + 6] = (byte)(idx >> 16); o[at + 7] = (byte)(idx >> 24);
    }

    static void EncodeAlpha(byte[] blk, byte[] o, int at)
    {
        int a0 = 0, a1 = 255;
        for (int i = 0; i < 16; i++) { int a = blk[i * 4 + 3]; a0 = Math.Max(a0, a); a1 = Math.Min(a1, a); }
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1) for (int k = 1; k <= 6; k++) pal[k + 1] = ((7 - k) * a0 + k * a1) / 7;
        else { for (int k = 1; k <= 4; k++) pal[k + 1] = ((5 - k) * a0 + k * a1) / 5; pal[6] = 0; pal[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = blk[i * 4 + 3], best = 0, bd = int.MaxValue;
            for (int k = 0; k < 8; k++) { int d = Math.Abs(a - pal[k]); if (d < bd) { bd = d; best = k; } }
            bits |= (ulong)best << (i * 3);
        }
        o[at] = (byte)a0; o[at + 1] = (byte)a1;
        for (int k = 0; k < 6; k++) o[at + 2 + k] = (byte)(bits >> (8 * k));
    }
}
