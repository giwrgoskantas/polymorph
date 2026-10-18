using System.Numerics;
using System.Text;

namespace MoloMax.Core.Gta.Export;

public enum TexKind { Diffuse, Normal, Specular, Detail, Environment }

/// <summary>A texture ready to embed: a GTA name and a legacy DDS file.</summary>
public sealed class ExportTexture
{
    public string Name;
    public byte[] Dds;
    public TexKind Kind;
    public int Width, Height, Mips;
    public string Format; // D3DFMT_DXT1 ...
    public string FileName => Name + ".dds";
}

/// <summary>
/// Turns material texture paths into DDS (DXT1 opaque / DXT5 with alpha, mipmaps, power of two up to 1024)
/// and makes small placeholder textures for samplers a material leaves empty, so every shader parameter is valid.
/// </summary>
public sealed class TextureBank
{
    /// <summary>Decodes PNG / JPG / TGA / BMP to RGBA (supplied by the app; null = only .dds files work).</summary>
    public Func<string, RgbaImage> ImageLoader;
    public int MaxSize = 1024;
    public readonly List<string> Warnings = new();
    /// <summary>Folders searched when a texture path is relative or missing.</summary>
    public List<string> SearchFolders = new();

    readonly Dictionary<string, ExportTexture> byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ExportTexture> byName = new(StringComparer.OrdinalIgnoreCase);

    public static string SafeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "tex";
        var sb = new StringBuilder();
        foreach (var ch in s.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? ch : '_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? "tex" : r;
    }

    string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return path;
        var file = Path.GetFileName(path);
        foreach (var f in SearchFolders)
        {
            if (string.IsNullOrEmpty(f)) continue;
            var p = Path.Combine(f, file);
            if (File.Exists(p)) return p;
            foreach (var ext in new[] { ".dds", ".png", ".jpg", ".jpeg", ".tga" })
            {
                var q = Path.Combine(f, Path.GetFileNameWithoutExtension(file) + ext);
                if (File.Exists(q)) return q;
            }
        }
        return null;
    }

    /// <summary>The texture for a material slot, or null when the path cannot be read (a warning is added).</summary>
    public ExportTexture FromFile(string path, TexKind kind)
    {
        var full = Resolve(path);
        if (full == null)
        {
            if (!string.IsNullOrWhiteSpace(path)) Warnings.Add($"Texture not found: {path}");
            return null;
        }
        if (byPath.TryGetValue(full, out var t)) return t;
        var name = UniqueName(SafeName(Path.GetFileNameWithoutExtension(full)));
        try
        {
            if (full.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = File.ReadAllBytes(full);
                if (!DdsInfo(bytes, out int w, out int h, out int mips, out string fmt))
                {
                    Warnings.Add($"Unsupported DDS (use DXT1/DXT5/A8R8G8B8 without DX10 header): {full}");
                    return null;
                }
                t = new ExportTexture { Name = name, Dds = bytes, Kind = kind, Width = w, Height = h, Mips = mips, Format = fmt };
            }
            else
            {
                if (ImageLoader == null) { Warnings.Add($"Cannot read {full} (no image loader)."); return null; }
                var img = ImageLoader(full);
                if (img == null) { Warnings.Add($"Cannot read image {full}"); return null; }
                t = Encode(name, img, kind);
            }
        }
        catch (Exception e)
        {
            Warnings.Add($"Texture {full}: {e.Message}");
            return null;
        }
        byPath[full] = t; byName[t.Name] = t;
        return t;
    }

    public ExportTexture Encode(string name, RgbaImage img, TexKind kind)
    {
        var fmt = kind == TexKind.Normal ? DdsFormat.Dxt1 : DdsEncoder.AutoFormat(img);
        var dds = DdsEncoder.Encode(img, fmt, MaxSize);
        DdsInfo(dds, out int w, out int h, out int mips, out string f);
        var t = new ExportTexture { Name = name, Dds = dds, Kind = kind, Width = w, Height = h, Mips = mips, Format = f };
        byName[name] = t;
        return t;
    }

    /// <summary>Placeholder for an empty sampler: flat colour diffuse, flat normal, no-spec, neutral detail.</summary>
    public ExportTexture Placeholder(TexKind kind, Vector3 colour)
    {
        string name; RgbaImage img;
        switch (kind)
        {
            case TexKind.Normal: name = "molo_flat_n"; img = RgbaImage.Solid(4, 4, 128, 128, 255); break;
            case TexKind.Specular: name = "molo_flat_s"; img = RgbaImage.Solid(4, 4, 40, 40, 40); break;
            case TexKind.Detail: name = "molo_flat_d"; img = RgbaImage.Solid(4, 4, 128, 128, 128); break;
            case TexKind.Environment: name = "molo_flat_env"; img = RgbaImage.Solid(4, 4, 90, 100, 110); break;
            default:
                {
                    byte r = (byte)Math.Clamp(colour.X * 255, 0, 255), g = (byte)Math.Clamp(colour.Y * 255, 0, 255), b = (byte)Math.Clamp(colour.Z * 255, 0, 255);
                    name = $"molo_col_{r:x2}{g:x2}{b:x2}"; img = RgbaImage.Solid(4, 4, r, g, b);
                    break;
                }
        }
        if (byName.TryGetValue(name, out var t)) return t;
        return Encode(name, img, kind);
    }

    string UniqueName(string n)
    {
        if (!byName.ContainsKey(n)) return n;
        for (int i = 1; ; i++) { var c = $"{n}_{i}"; if (!byName.ContainsKey(c)) return c; }
    }

    /// <summary>Reads the size, mip count and format of a legacy DDS.</summary>
    public static bool DdsInfo(byte[] d, out int w, out int h, out int mips, out string fmt)
    {
        w = h = mips = 0; fmt = null;
        if (d == null || d.Length < 128 || BitConverter.ToUInt32(d, 0) != 0x20534444) return false;
        h = (int)BitConverter.ToUInt32(d, 12); w = (int)BitConverter.ToUInt32(d, 16);
        mips = Math.Max(1, (int)BitConverter.ToUInt32(d, 28));
        uint pfFlags = BitConverter.ToUInt32(d, 80), fourcc = BitConverter.ToUInt32(d, 84), bits = BitConverter.ToUInt32(d, 88);
        if ((pfFlags & 4) != 0)
        {
            fmt = fourcc switch
            {
                0x31545844 => "D3DFMT_DXT1",
                0x33545844 => "D3DFMT_DXT3",
                0x35545844 => "D3DFMT_DXT5",
                0x31495441 => "D3DFMT_ATI1",
                0x32495441 => "D3DFMT_ATI2",
                _ => null,
            };
            return fmt != null;
        }
        if (bits == 32) { fmt = "D3DFMT_A8R8G8B8"; return true; }
        if (bits == 8) { fmt = "D3DFMT_L8"; return true; }
        return false;
    }
}
