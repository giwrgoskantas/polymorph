using System.Numerics;
using System.Text;
using System.Text.Json;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Core.IO;

/// <summary>
/// glTF binary (.glb) export for Blender, Tripo, Substance...: one mesh per object (modifiers applied, world
/// transform baked), Y-up, UVs, vertex colours, one material per GTA material with its diffuse as PNG.
/// </summary>
public static class GlbExporter
{
    /// <param name="pngForTexture">Returns PNG bytes for a material texture path (null = no texture).</param>
    public static void Export(Scene scene, IEnumerable<SceneObject> objects, string path, Func<string, byte[]> pngForTexture = null)
    {
        var bin = new MemoryStream();
        var bufferViews = new List<object>(); var accessors = new List<object>();
        var meshes = new List<object>(); var nodes = new List<object>();
        var materials = new List<object>(); var images = new List<object>(); var textures = new List<object>();
        var matIndex = new Dictionary<int, int>();

        void Align() { while (bin.Length % 4 != 0) bin.WriteByte(0); }
        int View(byte[] data, int? target)
        {
            Align();
            var d = new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = (int)bin.Length, ["byteLength"] = data.Length };
            if (target.HasValue) d["target"] = target.Value;
            bin.Write(data);
            bufferViews.Add(d);
            return bufferViews.Count - 1;
        }
        int Accessor(float[] data, int comps, string type, bool minmax)
        {
            var bytes = new byte[data.Length * 4]; Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            var a = new Dictionary<string, object> { ["bufferView"] = View(bytes, 34962), ["componentType"] = 5126, ["count"] = data.Length / comps, ["type"] = type };
            if (minmax)
            {
                var mn = new float[comps]; var mx = new float[comps];
                for (int c = 0; c < comps; c++) { mn[c] = float.MaxValue; mx[c] = float.MinValue; }
                for (int i = 0; i < data.Length; i++) { int c = i % comps; mn[c] = MathF.Min(mn[c], data[i]); mx[c] = MathF.Max(mx[c], data[i]); }
                a["min"] = mn; a["max"] = mx;
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }
        int Indices(uint[] idx)
        {
            var bytes = new byte[idx.Length * 4]; Buffer.BlockCopy(idx, 0, bytes, 0, bytes.Length);
            accessors.Add(new Dictionary<string, object> { ["bufferView"] = View(bytes, 34963), ["componentType"] = 5125, ["count"] = idx.Length, ["type"] = "SCALAR" });
            return accessors.Count - 1;
        }
        int Material(int id)
        {
            if (matIndex.TryGetValue(id, out var mi)) return mi;
            var m = scene.Material(id);
            var pbr = new Dictionary<string, object>
            {
                ["baseColorFactor"] = new[] { m.ViewportColour.X, m.ViewportColour.Y, m.ViewportColour.Z, 1f },
                ["metallicFactor"] = 0f, ["roughnessFactor"] = 0.8f,
            };
            var png = m.DiffuseTexture != null ? pngForTexture?.Invoke(m.DiffuseTexture) : null;
            if (png != null)
            {
                images.Add(new Dictionary<string, object> { ["bufferView"] = View(png, null), ["mimeType"] = "image/png", ["name"] = Path.GetFileNameWithoutExtension(m.DiffuseTexture) });
                textures.Add(new Dictionary<string, object> { ["source"] = images.Count - 1 });
                pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = textures.Count - 1 };
                pbr["baseColorFactor"] = new[] { 1f, 1f, 1f, 1f };
            }
            materials.Add(new Dictionary<string, object> { ["name"] = m.Name, ["pbrMetallicRoughness"] = pbr, ["extras"] = new Dictionary<string, object> { ["gta_shader"] = m.Shader } });
            matIndex[id] = materials.Count - 1;
            return matIndex[id];
        }

        foreach (var o in objects)
        {
            var mesh = o.Evaluated(render: true);
            if (mesh.Faces.Count == 0) continue;
            var w = o.World;
            var prims = new List<object>();
            foreach (var group in mesh.Faces.GroupBy(f => f.MatId))
            {
                var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var col = new List<float>(); var idx = new List<uint>();
                foreach (var f in group)
                {
                    var n = Vector3.Normalize(Vector3.TransformNormal(mesh.FaceNormal(f), w));
                    foreach (var (a, b, c) in Triangulator.Triangulate(mesh, f))
                        foreach (var k in new[] { a, b, c })
                        {
                            var p = Vector3.Transform(mesh.Verts[f.V[k]], w);
                            pos.Add(p.X); pos.Add(p.Z); pos.Add(-p.Y);          // Z-up -> Y-up
                            nrm.Add(n.X); nrm.Add(n.Z); nrm.Add(-n.Y);
                            uv.Add(f.UV[k].X); uv.Add(f.UV[k].Y);
                            var cc = f.Col[k]; col.Add(cc.X); col.Add(cc.Y); col.Add(cc.Z); col.Add(cc.W);
                            idx.Add((uint)idx.Count);
                        }
                }
                prims.Add(new Dictionary<string, object>
                {
                    ["attributes"] = new Dictionary<string, object>
                    {
                        ["POSITION"] = Accessor(pos.ToArray(), 3, "VEC3", true), ["NORMAL"] = Accessor(nrm.ToArray(), 3, "VEC3", false),
                        ["TEXCOORD_0"] = Accessor(uv.ToArray(), 2, "VEC2", false), ["COLOR_0"] = Accessor(col.ToArray(), 4, "VEC4", false),
                    },
                    ["indices"] = Indices(idx.ToArray()),
                    ["material"] = Material(group.Key),
                });
            }
            meshes.Add(new Dictionary<string, object> { ["name"] = o.Name, ["primitives"] = prims });
            nodes.Add(new Dictionary<string, object> { ["name"] = o.Name, ["mesh"] = meshes.Count - 1 });
        }
        if (nodes.Count == 0) throw new InvalidOperationException("Nothing to export.");
        Align();
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "Polymorph" },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = Enumerable.Range(0, nodes.Count).ToArray() } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["materials"] = materials, ["accessors"] = accessors, ["bufferViews"] = bufferViews,
            ["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = (int)bin.Length } },
        };
        if (images.Count > 0) { gltf["images"] = images; gltf["textures"] = textures; }
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gltf));
        int jsonPad = (4 - json.Length % 4) % 4;
        var binBytes = bin.ToArray();
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int total = 12 + 8 + json.Length + jsonPad + 8 + binBytes.Length;
        bw.Write(0x46546C67u); bw.Write(2u); bw.Write((uint)total);
        bw.Write((uint)(json.Length + jsonPad)); bw.Write(0x4E4F534Au); bw.Write(json); for (int i = 0; i < jsonPad; i++) bw.Write((byte)' ');
        bw.Write((uint)binBytes.Length); bw.Write(0x004E4942u); bw.Write(binBytes);
    }
}
