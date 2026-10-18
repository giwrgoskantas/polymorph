using MoloMax.Core.Gta.Export;

namespace MoloMax.Core.Paint;

/// <summary>Writes the painted textures: GTA DDS (DXT1 / DXT5 with mipmaps), PNG (through a callback) or TGA, at the export size.</summary>
public static class PaintExport
{
    public sealed class Written { public List<string> Files = new(); public bool Alpha, Emissive; }

    /// <summary>Halves the image until it fits <paramref name="max"/> (box filter, so 4K work becomes a clean 1024).</summary>
    public static RgbaImage Fit(RgbaImage img, int max)
    {
        while (img.Width > max || img.Height > max) img = img.Half();
        return img;
    }

    /// <summary>
    /// GTA naming like the game's own textures: name.ext (diffuse), name_n (normal), name_s (specular), name_e (emissive, only when painted).
    /// </summary>
    /// <param name="png">PNG encoder (System.Drawing lives in the app).</param>
    public static Written Write(TextureSet ts, string folder, string baseName, Func<RgbaImage, byte[]> png = null)
    {
        if (ts.ExportFormat == PaintExportFormat.Sollumz) folder = Path.Combine(folder, "sollumz", baseName); // one folder per model, PNG, ready for Blender
        Directory.CreateDirectory(folder);
        var (d, n, s, e) = ts.BakeImages();
        var res = new Written { Alpha = d.HasAlpha(), Emissive = ts.HasEmissive };
        int size = Math.Max(4, ts.ExportSize);
        void Out(string suffix, RgbaImage img, DdsFormat fmt)
        {
            string name = suffix.Length == 0 ? baseName : $"{baseName}_{suffix}";
            string path;
            switch (ts.ExportFormat)
            {
                case PaintExportFormat.Png or PaintExportFormat.Sollumz when png != null:
                    path = Path.Combine(folder, name + ".png"); File.WriteAllBytes(path, png(Fit(img, size))); break;
                case PaintExportFormat.Tga:
                case PaintExportFormat.Png:
                case PaintExportFormat.Sollumz:
                    path = Path.Combine(folder, name + ".tga"); File.WriteAllBytes(path, Tga(Fit(img, size))); break;
                default:
                    path = Path.Combine(folder, name + ".dds"); File.WriteAllBytes(path, DdsEncoder.Encode(img, fmt, size, true)); break;
            }
            res.Files.Add(path);
        }
        Out("", d, res.Alpha ? DdsFormat.Dxt5 : DdsFormat.Dxt1);
        Out("n", n, DdsFormat.Dxt1);
        Out("s", s, DdsFormat.Dxt5);
        if (res.Emissive) Out("e", e, DdsFormat.Dxt1);
        return res;
    }

    /// <summary>Uncompressed 32-bit TGA (top-left origin).</summary>
    public static byte[] Tga(RgbaImage img)
    {
        var b = new byte[18 + img.Width * img.Height * 4];
        b[2] = 2; // true colour
        b[12] = (byte)(img.Width & 255); b[13] = (byte)(img.Width >> 8); b[14] = (byte)(img.Height & 255); b[15] = (byte)(img.Height >> 8);
        b[16] = 32; b[17] = 0x28; // 8 alpha bits, top-left
        for (int i = 0; i < img.Width * img.Height; i++)
        {
            int o = 18 + i * 4, p = i * 4;
            b[o] = img.Pixels[p + 2]; b[o + 1] = img.Pixels[p + 1]; b[o + 2] = img.Pixels[p]; b[o + 3] = img.Pixels[p + 3];
        }
        return b;
    }
}
