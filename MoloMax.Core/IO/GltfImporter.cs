using System.Numerics;
using System.Text;
using System.Text.Json;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.IO;

/// <summary>
/// glTF 2.0 reader (.gltf with external or data-URI buffers, and binary .glb). Reads the node
/// hierarchy, triangle primitives (lists, strips, fans), UV0, COLOR_0 and PBR materials; embedded
/// images are written next to the model so the material editor can use them.
/// </summary>
public static class GltfImporter
{
    const uint GlbMagic = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;

    sealed class Ctx
    {
        public JsonElement Root;
        public byte[][] Buffers;
        public string Dir;
        public string ModelName;
        public ImportOptions Opt;
        public ImportResult Res;
        public readonly Dictionary<int, string> ImageFiles = new();
        public int DefaultMaterial = -1;
        public int Unnamed;
        public bool SkinWarned;
    }

    public static ImportResult Import(string path, ImportOptions opt)
    {
        var bytes = File.ReadAllBytes(path);
        string json; byte[] glbBin = null;
        if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == GlbMagic) (json, glbBin) = ReadGlb(bytes);
        else json = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        return ImportJson(json, glbBin, Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path), opt);
    }

    static (string Json, byte[] Bin) ReadGlb(byte[] b)
    {
        uint version = BitConverter.ToUInt32(b, 4);
        if (version != 2) throw new InvalidDataException($"GLB version {version} is not supported (glTF 2.0 only).");
        int length = (int)Math.Min(BitConverter.ToUInt32(b, 8), (uint)b.Length);
        int pos = 12; string json = null; byte[] bin = null;
        while (pos + 8 <= length)
        {
            int clen = (int)BitConverter.ToUInt32(b, pos); uint ctype = BitConverter.ToUInt32(b, pos + 4);
            pos += 8;
            if (clen < 0 || pos + clen > length) throw new InvalidDataException("GLB chunk runs past the end of the file.");
            if (ctype == ChunkJson && json == null) json = Encoding.UTF8.GetString(b, pos, clen).TrimEnd('\0', ' ');
            else if (ctype == ChunkBin && bin == null) { bin = new byte[clen]; Buffer.BlockCopy(b, pos, bin, 0, clen); }
            pos += (clen + 3) & ~3;
        }
        if (json == null) throw new InvalidDataException("GLB has no JSON chunk.");
        return (json, bin);
    }

    public static ImportResult ImportJson(string json, byte[] glbBin, string dir, string modelName, ImportOptions opt)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var c = new Ctx { Root = root, Dir = dir, ModelName = ModelImporter.SafeName(modelName, "model"), Opt = opt, Res = new ImportResult() };

        foreach (var ext in Strings(root, "extensionsRequired"))
        {
            if (ext is "KHR_draco_mesh_compression" or "EXT_meshopt_compression")
                throw new NotSupportedException($"This file uses {ext} (compressed geometry). Re-export it without mesh compression, or open and re-save it in Blender.");
            c.Res.Warnings.Add($"Extension {ext} is not supported; the file may look different.");
        }
        foreach (var ext in Strings(root, "extensionsUsed"))
            if (ext is "KHR_texture_basisu" or "EXT_texture_webp") c.Res.Warnings.Add($"{ext}: those textures may not load (PNG/JPG work).");

        // buffers
        var buffers = Arr(root, "buffers");
        c.Buffers = new byte[buffers.Count][];
        for (int i = 0; i < buffers.Count; i++)
        {
            var b = buffers[i];
            if (b.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String) c.Buffers[i] = LoadUri(c, uri.GetString());
            else c.Buffers[i] = glbBin ?? throw new InvalidDataException("Buffer without data (a .gltf that lost its .bin?).");
        }

        // materials (indices preserved: glTF material i → result material i)
        var mats = Arr(root, "materials");
        for (int i = 0; i < mats.Count; i++) c.Res.Materials.Add(ReadMaterial(c, mats[i], i));

        // scene graph
        var nodes = Arr(root, "nodes");
        var roots = new List<int>();
        var scenes = Arr(root, "scenes");
        int sceneIndex = root.TryGetProperty("scene", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 0;
        if (scenes.Count > 0 && sceneIndex < scenes.Count) roots.AddRange(Ints(scenes[sceneIndex], "nodes"));
        else
        {
            var isChild = new HashSet<int>();
            foreach (var n in nodes) foreach (var ch in Ints(n, "children")) isChild.Add(ch);
            for (int i = 0; i < nodes.Count; i++) if (!isChild.Contains(i)) roots.Add(i);
        }
        var visiting = new HashSet<int>();
        foreach (var r in roots) Walk(c, nodes, r, Matrix4x4.Identity, visiting);

        // a file with meshes but no nodes: import the meshes as they are
        if (nodes.Count == 0)
        {
            var meshes = Arr(root, "meshes");
            for (int i = 0; i < meshes.Count; i++) AddMesh(c, i, Matrix4x4.Identity, null);
        }
        return c.Res;
    }

    static void Walk(Ctx c, List<JsonElement> nodes, int index, Matrix4x4 parent, HashSet<int> visiting)
    {
        if (index < 0 || index >= nodes.Count || !visiting.Add(index)) return;
        var n = nodes[index];
        var world = LocalMatrix(n) * parent;
        if (n.TryGetProperty("mesh", out var mi) && mi.ValueKind == JsonValueKind.Number)
        {
            var w = world;
            if (n.TryGetProperty("skin", out _))
            {
                // skinned meshes ignore their node transform (the skeleton places them): use the bind pose
                w = Matrix4x4.Identity;
                if (!c.SkinWarned) { c.Res.Warnings.Add("Skinned mesh imported in its bind pose (the rig is not imported)."); c.SkinWarned = true; }
            }
            AddMesh(c, mi.GetInt32(), w, Str(n, "name"));
        }
        foreach (var ch in Ints(n, "children")) Walk(c, nodes, ch, world, visiting);
        visiting.Remove(index);
    }

    static Matrix4x4 LocalMatrix(JsonElement n)
    {
        if (n.TryGetProperty("matrix", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() == 16)
        {
            // glTF stores column-major for column vectors; read in order it is the row-vector matrix System.Numerics uses
            var a = m.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
        }
        var t = Vec(n, "translation", 3, 0f); var r = Vec(n, "rotation", 4, 0f); var s = Vec(n, "scale", 3, 1f);
        var q = n.TryGetProperty("rotation", out _) ? Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3])) : Quaternion.Identity;
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    static void AddMesh(Ctx c, int meshIndex, Matrix4x4 world, string nodeName)
    {
        var meshes = Arr(c.Root, "meshes");
        if (meshIndex < 0 || meshIndex >= meshes.Count) return;
        var mesh = meshes[meshIndex];
        var pm = new PolyMesh();
        bool mirror = world.GetDeterminant() < 0;
        foreach (var prim in Arr(mesh, "primitives"))
        {
            int mode = prim.TryGetProperty("mode", out var md) ? md.GetInt32() : 4;
            if (mode < 4) { c.Res.Warnings.Add("Points and lines were skipped (only triangles are imported)."); continue; }
            if (!prim.TryGetProperty("attributes", out var attrs) || !attrs.TryGetProperty("POSITION", out var posAcc)) continue;
            if (prim.TryGetProperty("extensions", out var pext) && pext.TryGetProperty("KHR_draco_mesh_compression", out _))
                throw new NotSupportedException("This file uses Draco mesh compression. Re-export it without compression, or re-save it in Blender.");

            var pos = ReadAccessor(c, posAcc.GetInt32(), out int pc, out int count);
            float[] uv = null; int uvc = 0; float[] col = null; int colc = 0;
            if (attrs.TryGetProperty("TEXCOORD_0", out var uvAcc)) uv = ReadAccessor(c, uvAcc.GetInt32(), out uvc, out _);
            if (attrs.TryGetProperty("COLOR_0", out var colAcc)) col = ReadAccessor(c, colAcc.GetInt32(), out colc, out _);

            int[] idx;
            if (prim.TryGetProperty("indices", out var ia) && ia.ValueKind == JsonValueKind.Number)
            {
                var f = ReadAccessor(c, ia.GetInt32(), out _, out _);
                idx = new int[f.Length];
                for (int i = 0; i < f.Length; i++) idx[i] = (int)f[i];
            }
            else idx = Enumerable.Range(0, count).ToArray();
            var tris = new List<(int, int, int)>(idx.Length / 3);
            if (mode == 4) for (int i = 0; i + 2 < idx.Length; i += 3) tris.Add((idx[i], idx[i + 1], idx[i + 2]));
            else if (mode == 5) for (int i = 0; i + 2 < idx.Length; i++) tris.Add(i % 2 == 0 ? (idx[i], idx[i + 1], idx[i + 2]) : (idx[i + 1], idx[i], idx[i + 2]));
            else if (mode == 6) for (int i = 1; i + 1 < idx.Length; i++) tris.Add((idx[0], idx[i], idx[i + 1]));

            int matIndex = prim.TryGetProperty("material", out var mt) && mt.ValueKind == JsonValueKind.Number ? mt.GetInt32() : -1;
            if (matIndex < 0 || matIndex >= c.Res.Materials.Count)
            {
                if (c.DefaultMaterial < 0) { c.Res.Materials.Add(GtaMaterial.CreateDefault(0, c.ModelName + "_default", new Vector3(0.75f))); c.DefaultMaterial = c.Res.Materials.Count - 1; }
                matIndex = c.DefaultMaterial;
            }

            int baseV = pm.Verts.Count;
            for (int i = 0; i < count; i++)
                pm.Verts.Add(Vector3.Transform(new Vector3(pos[i * pc], pos[i * pc + 1], pos[i * pc + 2]), world));
            foreach (var (a, b, d) in tris)
            {
                if (a >= count || b >= count || d >= count || a == b || b == d || a == d) continue;
                var tri = mirror ? new[] { a, d, b } : new[] { a, b, d };
                var face = new Face(tri.Select(x => x + baseV).ToArray(), matIndex, 1u);
                for (int k = 0; k < 3; k++)
                {
                    int v = tri[k];
                    if (uv != null && v * uvc + 1 < uv.Length) face.UV[k] = new Vector2(uv[v * uvc], uv[v * uvc + 1]);
                    if (col != null && v * colc + 2 < col.Length) face.Col[k] = new Vector4(col[v * colc], col[v * colc + 1], col[v * colc + 2], colc > 3 ? col[v * colc + 3] : 1f);
                }
                pm.Faces.Add(face);
            }
        }
        if (pm.Faces.Count == 0) return;
        var name = ModelImporter.SafeName(nodeName ?? Str(mesh, "name"), $"{c.ModelName}_{++c.Unnamed:00}");
        if (pm.Verts.Count > 65535) c.Res.Warnings.Add($"{name}: {pm.Verts.Count:N0} vertices before welding; GTA drawables top out at 65,535 per model.");
        c.Res.Meshes.Add(new ImportedMesh { Name = name, Mesh = pm });
    }

    // ---------------------------------------------------------------- materials & images

    static GtaMaterial ReadMaterial(Ctx c, JsonElement m, int i)
    {
        var mat = GtaMaterial.CreateDefault(0, ModelImporter.SafeName(Str(m, "name"), $"{c.ModelName}_mat{i}"), new Vector3(0.8f));
        bool hasNormal = false;
        if (m.TryGetProperty("pbrMetallicRoughness", out var pbr))
        {
            var f = Vec(pbr, "baseColorFactor", 4, 1f);
            mat.ViewportColour = new Vector3(f[0], f[1], f[2]);
            if (pbr.TryGetProperty("baseColorTexture", out var bt)) mat.DiffuseTexture = TextureFile(c, bt);
        }
        if (m.TryGetProperty("normalTexture", out var nt)) { mat.BumpTexture = TextureFile(c, nt); hasNormal = mat.BumpTexture != null; }
        if (mat.DiffuseTexture == null && m.TryGetProperty("emissiveTexture", out var et)) mat.DiffuseTexture = TextureFile(c, et);
        var alpha = Str(m, "alphaMode") ?? "OPAQUE";
        var emissive = Vec(m, "emissiveFactor", 3, 0f);
        bool glows = emissive[0] + emissive[1] + emissive[2] > 1.5f;
        mat.Shader = alpha switch
        {
            "MASK" => "normal_cutout",
            "BLEND" => hasNormal ? "normal_decal" : "decal",
            _ => glows ? "emissive" : hasNormal ? "normal" : "default",
        };
        if (mat.DiffuseTexture != null && mat.ViewportColour.X > 0.99f && mat.ViewportColour.Y > 0.99f && mat.ViewportColour.Z > 0.99f) mat.ViewportColour = new Vector3(0.8f);
        return mat;
    }

    static string TextureFile(Ctx c, JsonElement texInfo)
    {
        if (!texInfo.TryGetProperty("index", out var ti) || ti.ValueKind != JsonValueKind.Number) return null;
        var textures = Arr(c.Root, "textures");
        int t = ti.GetInt32();
        if (t < 0 || t >= textures.Count) return null;
        var tex = textures[t];
        int src = tex.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : -1;
        if (src < 0 && tex.TryGetProperty("extensions", out var te))
            foreach (var p in te.EnumerateObject())
                if (p.Value.TryGetProperty("source", out var s2)) { src = s2.GetInt32(); c.Res.Warnings.Add($"Texture uses {p.Name}; it may not display."); break; }
        if (src < 0) return null;
        if (c.ImageFiles.TryGetValue(src, out var cached)) return cached;
        string file = null;
        try { file = WriteImage(c, src); }
        catch (Exception e) { c.Res.Warnings.Add($"Image {src} could not be extracted: {e.Message}"); }
        c.ImageFiles[src] = file;
        return file;
    }

    static string WriteImage(Ctx c, int index)
    {
        var images = Arr(c.Root, "images");
        if (index >= images.Count) return null;
        var img = images[index];
        var mime = Str(img, "mimeType");
        byte[] data;
        if (img.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String)
        {
            var u = uri.GetString();
            if (!u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var p = Path.Combine(c.Dir, Uri.UnescapeDataString(u));
                return File.Exists(p) ? Path.GetFullPath(p) : null; // external image: use it in place
            }
            if (mime == null)
            {
                int semi = u.IndexOf(';'), comma = u.IndexOf(',');
                int end = semi > 5 ? semi : comma;
                if (end > 5) mime = u[5..end];
            }
            data = LoadUri(c, u);
        }
        else if (img.TryGetProperty("bufferView", out var bv)) data = BufferViewBytes(c, bv.GetInt32());
        else return null;

        string ext = mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/ktx2" => ".ktx2", _ => Sniff(data) };
        var folder = c.Opt.TextureFolder ?? Path.Combine(c.Dir, c.ModelName + "_textures");
        try { Directory.CreateDirectory(folder); }
        catch
        {
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoloMax", "imported", c.ModelName);
            Directory.CreateDirectory(folder);
        }
        var name = ModelImporter.SafeName(Str(img, "name"), $"{c.ModelName}_tex{index}");
        var path = Path.Combine(folder, name + ext);
        for (int n = 2; File.Exists(path) && !SameBytes(path, data); n++) path = Path.Combine(folder, $"{name}_{n}{ext}");
        if (!File.Exists(path)) File.WriteAllBytes(path, data);
        c.Res.TexturesWritten.Add(path);
        return path;
    }

    static bool SameBytes(string path, byte[] data)
    {
        var fi = new FileInfo(path);
        return fi.Length == data.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(data);
    }

    static string Sniff(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50) return ".png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8) return ".jpg";
        if (d.Length > 12 && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return ".webp";
        return ".bin";
    }

    // ---------------------------------------------------------------- buffers & accessors

    static byte[] LoadUri(Ctx c, string uri)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0) throw new InvalidDataException("Malformed data URI.");
            var head = uri[..comma];
            var body = uri[(comma + 1)..];
            return head.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(body) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(body));
        }
        var p = Path.Combine(c.Dir, Uri.UnescapeDataString(uri));
        if (!File.Exists(p)) throw new FileNotFoundException($"Missing buffer file {uri} next to the .gltf.");
        return File.ReadAllBytes(p);
    }

    static byte[] BufferViewBytes(Ctx c, int view)
    {
        var bv = Arr(c.Root, "bufferViews")[view];
        var buf = c.Buffers[bv.GetProperty("buffer").GetInt32()];
        int off = Int(bv, "byteOffset", 0), len = bv.GetProperty("byteLength").GetInt32();
        if (off + len > buf.Length) throw new InvalidDataException("bufferView runs past its buffer.");
        var r = new byte[len];
        Buffer.BlockCopy(buf, off, r, 0, len);
        return r;
    }

    /// <summary>Reads an accessor as floats (integer types are converted, normalised ones mapped to 0..1 / -1..1).</summary>
    static float[] ReadAccessor(Ctx c, int index, out int comps, out int count)
    {
        var acc = Arr(c.Root, "accessors")[index];
        count = acc.GetProperty("count").GetInt32();
        comps = Str(acc, "type") switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16, _ => 1 };
        int ct = acc.GetProperty("componentType").GetInt32();
        bool norm = acc.TryGetProperty("normalized", out var nz) && nz.ValueKind == JsonValueKind.True;
        int csize = ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, _ => 4 };
        var res = new float[count * comps];
        if (acc.TryGetProperty("sparse", out _)) c.Res.Warnings.Add("Sparse accessors are not supported; some vertices may be off.");
        if (!acc.TryGetProperty("bufferView", out var bvp) || bvp.ValueKind != JsonValueKind.Number) return res;
        var bv = Arr(c.Root, "bufferViews")[bvp.GetInt32()];
        var buf = c.Buffers[bv.GetProperty("buffer").GetInt32()];
        int start = Int(bv, "byteOffset", 0) + Int(acc, "byteOffset", 0);
        int stride = Int(bv, "byteStride", 0);
        if (stride == 0) stride = csize * comps;
        for (int i = 0; i < count; i++)
        {
            int p = start + i * stride;
            for (int k = 0; k < comps; k++)
            {
                int q = p + k * csize;
                if (q + csize > buf.Length) throw new InvalidDataException("Accessor runs past its buffer.");
                res[i * comps + k] = ct switch
                {
                    5126 => BitConverter.ToSingle(buf, q),
                    5125 => BitConverter.ToUInt32(buf, q),
                    5123 => norm ? BitConverter.ToUInt16(buf, q) / 65535f : BitConverter.ToUInt16(buf, q),
                    5122 => norm ? MathF.Max(BitConverter.ToInt16(buf, q) / 32767f, -1f) : BitConverter.ToInt16(buf, q),
                    5121 => norm ? buf[q] / 255f : buf[q],
                    5120 => norm ? MathF.Max((sbyte)buf[q] / 127f, -1f) : (sbyte)buf[q],
                    _ => 0,
                };
            }
        }
        return res;
    }

    // ---------------------------------------------------------------- JSON helpers

    static List<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : new List<JsonElement>();
    static IEnumerable<int> Ints(JsonElement e, string name) => Arr(e, name).Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32());
    static IEnumerable<string> Strings(JsonElement e, string name) => Arr(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString());
    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    static int Int(JsonElement e, string name, int def) => e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : def;
    static float[] Vec(JsonElement e, string name, int n, float def)
    {
        var r = Enumerable.Repeat(def, n).ToArray();
        if (e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var x in a.EnumerateArray()) { if (i >= n) break; r[i++] = x.GetSingle(); }
        }
        return r;
    }
}
