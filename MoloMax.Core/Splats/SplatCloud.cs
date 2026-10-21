using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace MoloMax.Core.Splats;

/// <summary>
/// A 3D Gaussian splat capture (scan of a real place or object): millions of small coloured blobs.
/// Read from .ply (3DGS / point cloud), .spz (Niantic, v1 to v4) or .splat (antimatter15). Shown in the viewport as a
/// reference to model over; it is never exported to GTA.
/// </summary>
public sealed class SplatCloud
{
    public int Count;
    /// <summary>Centres, 3 floats per splat (file space, see <see cref="SuggestedRotation"/>).</summary>
    public float[] Pos;
    /// <summary>Colour (display, 0..1) and opacity (0..1), 4 floats per splat.</summary>
    public float[] Rgba;
    /// <summary>3D covariance, 6 floats per splat: xx, xy, xz, yy, yz, zz.</summary>
    public float[] Cov;
    /// <summary>Box holding 98% of the splats (stray floaters left out).</summary>
    public Vector3 Min, Max;
    public string Format = "";
    /// <summary>Turns the file's axes into Polymorph's (Z up).</summary>
    public Quaternion SuggestedRotation = Quaternion.Identity;
    public bool PointCloud;

    const float C0 = 0.28209479177387814f;

    public static bool IsSplatFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".spz" or ".splat") return true;
        if (ext != ".ply") return false;
        try
        {
            var h = ReadPlyHeader(path, out _);
            return h.Props.Any(p => p.Name is "scale_0" or "f_dc_0") || h.Elements.Any(e => e.Name == "chunk");
        }
        catch { return false; }
    }

    public static SplatCloud Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var c = ext switch
        {
            ".spz" => LoadSpz(File.ReadAllBytes(path)),
            ".splat" => LoadSplat(File.ReadAllBytes(path)),
            ".ply" => LoadPly(path),
            _ => throw new InvalidDataException("Not a splat file: " + Path.GetFileName(path)),
        };
        if (c.Count == 0) throw new InvalidDataException("No splat in " + Path.GetFileName(path));
        c.ComputeBounds();
        return c;
    }

    void Alloc(int n) { Count = n; Pos = new float[n * 3]; Rgba = new float[n * 4]; Cov = new float[n * 6]; }

    /// <summary>Σ = R S S Rᵀ from a rotation (w, x, y, z, normalised here) and the three axis sizes.</summary>
    void SetGaussian(int i, float sx, float sy, float sz, float qw, float qx, float qy, float qz)
    {
        float ql = MathF.Sqrt(qw * qw + qx * qx + qy * qy + qz * qz);
        if (ql < 1e-12f) { qw = 1; qx = qy = qz = 0; } else { qw /= ql; qx /= ql; qy /= ql; qz /= ql; }
        // rotation matrix (columns are the local axes)
        float r00 = 1 - 2 * (qy * qy + qz * qz), r01 = 2 * (qx * qy - qw * qz), r02 = 2 * (qx * qz + qw * qy);
        float r10 = 2 * (qx * qy + qw * qz), r11 = 1 - 2 * (qx * qx + qz * qz), r12 = 2 * (qy * qz - qw * qx);
        float r20 = 2 * (qx * qz - qw * qy), r21 = 2 * (qy * qz + qw * qx), r22 = 1 - 2 * (qx * qx + qy * qy);
        float a = sx * sx, b = sy * sy, d = sz * sz;
        int o = i * 6;
        Cov[o + 0] = r00 * r00 * a + r01 * r01 * b + r02 * r02 * d;
        Cov[o + 1] = r00 * r10 * a + r01 * r11 * b + r02 * r12 * d;
        Cov[o + 2] = r00 * r20 * a + r01 * r21 * b + r02 * r22 * d;
        Cov[o + 3] = r10 * r10 * a + r11 * r11 * b + r12 * r12 * d;
        Cov[o + 4] = r10 * r20 * a + r11 * r21 * b + r12 * r22 * d;
        Cov[o + 5] = r20 * r20 * a + r21 * r21 * b + r22 * r22 * d;
    }

    void SetColour(int i, float r, float g, float b, float a)
    {
        Rgba[i * 4] = Math.Clamp(r, 0, 1); Rgba[i * 4 + 1] = Math.Clamp(g, 0, 1); Rgba[i * 4 + 2] = Math.Clamp(b, 0, 1); Rgba[i * 4 + 3] = Math.Clamp(a, 0, 1);
    }

    public void ComputeBounds()
    {
        if (Count == 0) { Min = Max = Vector3.Zero; return; }
        // sample up to 20k centres, keep 1%..99% on each axis
        int step = Math.Max(1, Count / 20000);
        var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
        for (int i = 0; i < Count; i += step) { xs.Add(Pos[i * 3]); ys.Add(Pos[i * 3 + 1]); zs.Add(Pos[i * 3 + 2]); }
        xs.Sort(); ys.Sort(); zs.Sort();
        int lo = (int)(xs.Count * 0.01f), hi = Math.Max(lo, (int)(xs.Count * 0.99f) - 1);
        Min = new Vector3(xs[lo], ys[lo], zs[lo]); Max = new Vector3(xs[hi], ys[hi], zs[hi]);
        if (Max.X - Min.X < 1e-3f) Max.X = Min.X + 1e-3f;
        if (Max.Y - Min.Y < 1e-3f) Max.Y = Min.Y + 1e-3f;
        if (Max.Z - Min.Z < 1e-3f) Max.Z = Min.Z + 1e-3f;
    }

    static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    // ------------------------------------------------------------------ PLY

    sealed class PlyProp { public string Name; public string Type; public int Offset; public int Size; }
    sealed class PlyElement { public string Name; public int Count; public List<PlyProp> Props = new(); public int Stride; }
    sealed class PlyHeader { public string Format; public List<PlyElement> Elements = new(); public IEnumerable<PlyProp> Props => Elements.FirstOrDefault(e => e.Name == "vertex")?.Props ?? Enumerable.Empty<PlyProp>(); }

    static int TypeSize(string t) => t switch
    {
        "char" or "uchar" or "int8" or "uint8" => 1,
        "short" or "ushort" or "int16" or "uint16" => 2,
        "int" or "uint" or "int32" or "uint32" or "float" or "float32" => 4,
        "double" or "float64" => 8,
        _ => throw new InvalidDataException("PLY type not supported: " + t),
    };

    static PlyHeader ReadPlyHeader(string path, out long dataStart)
    {
        using var fs = File.OpenRead(path);
        var sb = new StringBuilder(); var h = new PlyHeader(); int b; long pos = 0;
        var line = new StringBuilder();
        while ((b = fs.ReadByte()) >= 0)
        {
            pos++;
            if (b == '\n')
            {
                var l = line.ToString().Trim(); line.Clear();
                if (pos == 4 && l != "ply") throw new InvalidDataException("Not a PLY file.");
                var t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                if (t[0] == "format") h.Format = t[1];
                else if (t[0] == "element") h.Elements.Add(new PlyElement { Name = t[1], Count = int.Parse(t[2]) });
                else if (t[0] == "property" && h.Elements.Count > 0)
                {
                    var e = h.Elements[^1];
                    if (t[1] == "list") { e.Props.Add(new PlyProp { Name = t[^1], Type = "list" }); e.Stride = -1; continue; }
                    int sz = TypeSize(t[1]);
                    e.Props.Add(new PlyProp { Name = t[2], Type = t[1], Offset = e.Stride < 0 ? 0 : e.Stride, Size = sz });
                    if (e.Stride >= 0) e.Stride += sz;
                }
                else if (t[0] == "end_header") { dataStart = pos; return h; }
                if (pos > 1 << 20) break;
            }
            else if (b != '\r') line.Append((char)b);
        }
        throw new InvalidDataException("PLY header not found.");
    }

    static float ReadAs(ReadOnlySpan<byte> row, PlyProp p) => p.Type switch
    {
        "float" or "float32" => BitConverter.ToSingle(row.Slice(p.Offset, 4)),
        "double" or "float64" => (float)BitConverter.ToDouble(row.Slice(p.Offset, 8)),
        "uchar" or "uint8" => row[p.Offset],
        "char" or "int8" => (sbyte)row[p.Offset],
        "ushort" or "uint16" => BitConverter.ToUInt16(row.Slice(p.Offset, 2)),
        "short" or "int16" => BitConverter.ToInt16(row.Slice(p.Offset, 2)),
        "uint" or "uint32" => BitConverter.ToUInt32(row.Slice(p.Offset, 4)),
        _ => BitConverter.ToInt32(row.Slice(p.Offset, 4)),
    };

    static SplatCloud LoadPly(string path)
    {
        var h = ReadPlyHeader(path, out long start);
        if (h.Elements.Any(e => e.Name == "chunk"))
            throw new InvalidDataException("This is a compressed PLY (SuperSplat). Save it as a normal .ply or as .spz, then import it.");
        var ve = h.Elements.FirstOrDefault(e => e.Name == "vertex") ?? throw new InvalidDataException("The PLY has no vertex element.");
        if (h.Elements.TakeWhile(e => e != ve).Any(e => e.Count > 0)) throw new InvalidDataException("PLY layout not supported (vertex must come first).");
        PlyProp P(string n) => ve.Props.FirstOrDefault(p => p.Name == n);
        var px = P("x"); var py = P("y"); var pz = P("z");
        if (px == null || py == null || pz == null) throw new InvalidDataException("The PLY has no x / y / z.");
        bool gauss = P("scale_0") != null && P("rot_0") != null;
        var c = new SplatCloud { Format = gauss ? "PLY (3D Gaussian splats)" : "PLY (points)", PointCloud = !gauss };
        c.Alloc(ve.Count);
        c.SuggestedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2); // COLMAP / 3DGS: Y down, Z forward
        var dc0 = P("f_dc_0"); var dc1 = P("f_dc_1"); var dc2 = P("f_dc_2"); var op = P("opacity");
        var s0 = P("scale_0"); var s1 = P("scale_1"); var s2 = P("scale_2");
        var r0 = P("rot_0"); var r1 = P("rot_1"); var r2 = P("rot_2"); var r3 = P("rot_3");
        var red = P("red") ?? P("r"); var green = P("green") ?? P("g"); var blue = P("blue") ?? P("b");
        float colScale = red != null && red.Type is "float" or "float32" or "double" ? 1f : 1f / 255f;
        bool ascii = h.Format == "ascii";
        if (!ascii && h.Format != "binary_little_endian") throw new InvalidDataException("PLY format not supported: " + h.Format);

        using var fs = File.OpenRead(path);
        fs.Position = start;
        if (ascii)
        {
            using var rd = new StreamReader(fs);
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var row = new byte[ve.Props.Sum(p => p.Size)];
            for (int i = 0; i < ve.Count; i++)
            {
                var t = (rd.ReadLine() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // ascii: write the values as floats into a fake row so one code path reads both
                var vals = new float[ve.Props.Count];
                for (int k = 0; k < vals.Length && k < t.Length; k++) vals[k] = float.Parse(t[k], ci);
                float V(PlyProp p) => p == null ? 0 : vals[ve.Props.IndexOf(p)];
                c.Fill(i, V, px, py, pz, gauss, dc0, dc1, dc2, op, s0, s1, s2, r0, r1, r2, r3, red, green, blue, colScale);
            }
        }
        else
        {
            if (ve.Stride <= 0) throw new InvalidDataException("PLY vertex with a list property is not supported.");
            int stride = ve.Stride, batch = 65536;
            var buf = new byte[stride * batch];
            for (int i0 = 0; i0 < ve.Count; i0 += batch)
            {
                int n = Math.Min(batch, ve.Count - i0);
                fs.ReadExactly(buf, 0, n * stride);
                for (int k = 0; k < n; k++)
                {
                    int rowStart = k * stride;
                    float V(PlyProp p) => p == null ? 0 : ReadAs(new ReadOnlySpan<byte>(buf, rowStart, stride), p);
                    c.Fill(i0 + k, V, px, py, pz, gauss, dc0, dc1, dc2, op, s0, s1, s2, r0, r1, r2, r3, red, green, blue, colScale);
                }
            }
        }
        if (!gauss) c.PointSizes();
        return c;
    }

    void Fill(int i, Func<PlyProp, float> V, PlyProp px, PlyProp py, PlyProp pz, bool gauss, PlyProp dc0, PlyProp dc1, PlyProp dc2, PlyProp op,
        PlyProp s0, PlyProp s1, PlyProp s2, PlyProp r0, PlyProp r1, PlyProp r2, PlyProp r3, PlyProp red, PlyProp green, PlyProp blue, float colScale)
    {
        Pos[i * 3] = V(px); Pos[i * 3 + 1] = V(py); Pos[i * 3 + 2] = V(pz);
        if (gauss)
        {
            float r, g, b;
            if (dc0 != null) { r = 0.5f + C0 * V(dc0); g = 0.5f + C0 * V(dc1); b = 0.5f + C0 * V(dc2); }
            else if (red != null) { r = V(red) * colScale; g = V(green) * colScale; b = V(blue) * colScale; }
            else r = g = b = 0.8f;
            SetColour(i, r, g, b, op != null ? Sigmoid(V(op)) : 1f);
            SetGaussian(i, MathF.Exp(V(s0)), MathF.Exp(V(s1)), MathF.Exp(V(s2)), V(r0), V(r1), V(r2), V(r3));
        }
        else
        {
            if (red != null) SetColour(i, V(red) * colScale, V(green) * colScale, V(blue) * colScale, 1);
            else SetColour(i, 0.8f, 0.8f, 0.8f, 1);
        }
    }

    /// <summary>A plain point cloud gets round splats sized from the point density.</summary>
    void PointSizes()
    {
        ComputeBounds();
        var e = Max - Min;
        float vol = MathF.Max(e.X * e.Y * e.Z, 1e-6f);
        float s = MathF.Max(MathF.Cbrt(vol / Math.Max(Count, 1)) * 0.6f, 1e-4f);
        for (int i = 0; i < Count; i++) SetGaussian(i, s, s, s, 1, 0, 0, 0);
    }

    // ------------------------------------------------------------------ .splat (antimatter15): 32 bytes per splat

    static SplatCloud LoadSplat(byte[] d)
    {
        int n = d.Length / 32;
        var c = new SplatCloud { Format = ".splat" }; c.Alloc(n);
        c.SuggestedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
        for (int i = 0; i < n; i++)
        {
            int o = i * 32;
            c.Pos[i * 3] = BitConverter.ToSingle(d, o); c.Pos[i * 3 + 1] = BitConverter.ToSingle(d, o + 4); c.Pos[i * 3 + 2] = BitConverter.ToSingle(d, o + 8);
            float sx = BitConverter.ToSingle(d, o + 12), sy = BitConverter.ToSingle(d, o + 16), sz = BitConverter.ToSingle(d, o + 20);
            c.SetColour(i, d[o + 24] / 255f, d[o + 25] / 255f, d[o + 26] / 255f, d[o + 27] / 255f);
            c.SetGaussian(i, sx, sy, sz, (d[o + 28] - 128) / 128f, (d[o + 29] - 128) / 128f, (d[o + 30] - 128) / 128f, (d[o + 31] - 128) / 128f);
        }
        return c;
    }

    // ------------------------------------------------------------------ SPZ (Niantic): v1-v3 = one gzip stream, v4 = header + zstd streams

    static SplatCloud LoadSpz(byte[] file)
    {
        if (file.Length >= 2 && file[0] == 0x1f && file[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
            using var ms = new MemoryStream(); gz.CopyTo(ms);
            return SpzLegacy(ms.ToArray());
        }
        if (file.Length < 16 || BitConverter.ToUInt32(file, 0) != 0x5053474e) throw new InvalidDataException("Not an SPZ file.");
        uint version = BitConverter.ToUInt32(file, 4);
        if (version < 4) return SpzLegacy(file);
        return SpzV4(file);
    }

    static SplatCloud SpzLegacy(byte[] d)
    {
        if (d.Length < 16 || BitConverter.ToUInt32(d, 0) != 0x5053474e) throw new InvalidDataException("Not an SPZ file.");
        int version = (int)BitConverter.ToUInt32(d, 4), n = (int)BitConverter.ToUInt32(d, 8);
        int shDeg = d[12], frac = d[13];
        if (version < 1 || version > 3) throw new InvalidDataException("SPZ version " + version + " is not supported.");
        int posSize = version == 1 ? 6 : 9, rotSize = version >= 3 ? 4 : 3;
        int o = 16;
        var pos = new ArraySegment<byte>(d, o, n * posSize); o += n * posSize;
        var alpha = new ArraySegment<byte>(d, o, n); o += n;
        var col = new ArraySegment<byte>(d, o, n * 3); o += n * 3;
        var scl = new ArraySegment<byte>(d, o, n * 3); o += n * 3;
        if (o + n * rotSize > d.Length) throw new InvalidDataException("SPZ file is cut short.");
        var rot = new ArraySegment<byte>(d, o, n * rotSize);
        return SpzDecode(n, version, frac, pos, alpha, col, scl, rot, rotSize, shDeg);
    }

    static SplatCloud SpzV4(byte[] f)
    {
        int n = (int)BitConverter.ToUInt32(f, 8);
        int shDeg = f[12], frac = f[13], streams = f[15];
        long toc = BitConverter.ToUInt32(f, 16);
        if (toc <= 0 || toc + streams * 16L > f.Length) throw new InvalidDataException("SPZ v4 table of contents is damaged.");
        var parts = new List<byte[]>();
        long at = toc + streams * 16L;
        using var dec = new ZstdSharp.Decompressor();
        for (int s = 0; s < streams; s++)
        {
            long csize = BitConverter.ToInt64(f, (int)(toc + s * 16)), usize = BitConverter.ToInt64(f, (int)(toc + s * 16 + 8));
            if (at + csize > f.Length) throw new InvalidDataException("SPZ v4 stream is cut short.");
            var raw = dec.Unwrap(new ReadOnlySpan<byte>(f, (int)at, (int)csize)).ToArray();
            if (raw.Length != usize) throw new InvalidDataException("SPZ v4 stream has the wrong size.");
            parts.Add(raw); at += csize;
        }
        if (parts.Count < 5) throw new InvalidDataException("SPZ v4 file has " + parts.Count + " streams (5 needed).");
        return SpzDecode(n, 4, frac, parts[0], parts[1], parts[2], parts[3], parts[4], 4, shDeg);
    }

    static SplatCloud SpzDecode(int n, int version, int frac, ArraySegment<byte> pos, ArraySegment<byte> alpha, ArraySegment<byte> col,
        ArraySegment<byte> scl, ArraySegment<byte> rot, int rotSize, int shDeg)
    {
        var c = new SplatCloud { Format = "SPZ v" + version }; c.Alloc(n);
        c.SuggestedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2); // SPZ is right / up / back
        float fscale = 1f / (1 << frac);
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 3; k++)
            {
                if (version == 1) c.Pos[i * 3 + k] = (float)BitConverter.ToHalf(pos.Array, pos.Offset + i * 6 + k * 2);
                else
                {
                    int b = pos.Offset + i * 9 + k * 3;
                    int v = pos.Array[b] | pos.Array[b + 1] << 8 | pos.Array[b + 2] << 16;
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xff000000);
                    c.Pos[i * 3 + k] = v * fscale;
                }
            }
            float r = 0.5f + C0 * ((col[i * 3] / 255f - 0.5f) / 0.15f);
            float g = 0.5f + C0 * ((col[i * 3 + 1] / 255f - 0.5f) / 0.15f);
            float bl = 0.5f + C0 * ((col[i * 3 + 2] / 255f - 0.5f) / 0.15f);
            c.SetColour(i, r, g, bl, alpha[i] / 255f);
            float sx = MathF.Exp(scl[i * 3] / 16f - 10f), sy = MathF.Exp(scl[i * 3 + 1] / 16f - 10f), sz = MathF.Exp(scl[i * 3 + 2] / 16f - 10f);
            float qx, qy, qz, qw;
            if (rotSize == 3)
            {
                qx = rot[i * 3] / 127.5f - 1; qy = rot[i * 3 + 1] / 127.5f - 1; qz = rot[i * 3 + 2] / 127.5f - 1;
                qw = MathF.Sqrt(MathF.Max(0, 1 - qx * qx - qy * qy - qz * qz));
            }
            else
            {
                // smallest three: 2 bits = index of the largest, then 3 x (9 bit magnitude + sign)
                uint comp = (uint)(rot[i * 4] | rot[i * 4 + 1] << 8 | rot[i * 4 + 2] << 16 | rot[i * 4 + 3] << 24);
                int largest = (int)(comp >> 30);
                Span<float> q = stackalloc float[4]; float sum = 0;
                for (int k = 3; k >= 0; k--)
                {
                    if (k == largest) continue;
                    uint mag = comp & 511u, neg = (comp >> 9) & 1u; comp >>= 10;
                    float v = 0.70710678f * mag / 511f; if (neg != 0) v = -v;
                    q[k] = v; sum += v * v;
                }
                q[largest] = MathF.Sqrt(MathF.Max(0, 1 - sum));
                qx = q[0]; qy = q[1]; qz = q[2]; qw = q[3];
            }
            c.SetGaussian(i, sx, sy, sz, qw, qx, qy, qz);
        }
        return c;
    }

    // ------------------------------------------------------------------ writing (tests, and saving a cropped capture)

    /// <summary>Writes a standard 3DGS binary PLY (no higher-order colour terms).</summary>
    public static void WritePly(string path, IList<(Vector3 Pos, Vector3 Scale, Quaternion Rot, Vector3 Colour, float Opacity)> splats)
    {
        using var fs = File.Create(path);
        var head = "ply\nformat binary_little_endian 1.0\nelement vertex " + splats.Count + "\n" +
                   string.Concat(new[] { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" }.Select(p => "property float " + p + "\n")) +
                   "end_header\n";
        var hb = Encoding.ASCII.GetBytes(head); fs.Write(hb);
        using var w = new BinaryWriter(fs);
        foreach (var s in splats)
        {
            w.Write(s.Pos.X); w.Write(s.Pos.Y); w.Write(s.Pos.Z);
            w.Write((s.Colour.X - 0.5f) / C0); w.Write((s.Colour.Y - 0.5f) / C0); w.Write((s.Colour.Z - 0.5f) / C0);
            float a = Math.Clamp(s.Opacity, 1e-4f, 1 - 1e-4f); w.Write(MathF.Log(a / (1 - a)));
            w.Write(MathF.Log(s.Scale.X)); w.Write(MathF.Log(s.Scale.Y)); w.Write(MathF.Log(s.Scale.Z));
            w.Write(s.Rot.W); w.Write(s.Rot.X); w.Write(s.Rot.Y); w.Write(s.Rot.Z);
        }
    }

    /// <summary>Writes an SPZ v2 file (gzip, 24-bit positions): used by the self test.</summary>
    public static void WriteSpzV2(string path, IList<(Vector3 Pos, Vector3 Scale, Quaternion Rot, Vector3 Colour, float Opacity)> splats, int fracBits = 12)
    {
        var ms = new MemoryStream(); var w = new BinaryWriter(ms);
        w.Write(0x5053474eu); w.Write(2u); w.Write((uint)splats.Count); w.Write((byte)0); w.Write((byte)fracBits); w.Write((byte)0); w.Write((byte)0);
        foreach (var s in splats)
            foreach (var v in new[] { s.Pos.X, s.Pos.Y, s.Pos.Z })
            { int q = (int)MathF.Round(v * (1 << fracBits)); w.Write((byte)(q & 255)); w.Write((byte)((q >> 8) & 255)); w.Write((byte)((q >> 16) & 255)); }
        foreach (var s in splats) w.Write((byte)Math.Clamp((int)MathF.Round(s.Opacity * 255), 0, 255));
        foreach (var s in splats)
            foreach (var v in new[] { s.Colour.X, s.Colour.Y, s.Colour.Z })
                w.Write((byte)Math.Clamp((int)MathF.Round(((v - 0.5f) / C0 * 0.15f + 0.5f) * 255), 0, 255));
        foreach (var s in splats)
            foreach (var v in new[] { s.Scale.X, s.Scale.Y, s.Scale.Z })
                w.Write((byte)Math.Clamp((int)MathF.Round((MathF.Log(v) + 10) * 16), 0, 255));
        foreach (var s in splats)
        {
            var q = Quaternion.Normalize(s.Rot); if (q.W < 0) q = -q;
            foreach (var v in new[] { q.X, q.Y, q.Z }) w.Write((byte)Math.Clamp((int)MathF.Round((v + 1) * 127.5f), 0, 255));
        }
        w.Flush();
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        gz.Write(ms.ToArray());
    }
}
