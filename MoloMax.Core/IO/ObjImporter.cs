using System.Globalization;
using System.Numerics;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.IO;

/// <summary>
/// Wavefront OBJ reader: v (with optional r g b), vt, f with v/vt/vn and negative indices, o/g split
/// into objects, usemtl + mtllib (Kd, map_Kd, map_Bump / bump / norm). Polygons stay n-gons.
/// </summary>
public static class ObjImporter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ImportResult Import(string path, ImportOptions opt)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        var model = ModelImporter.SafeName(Path.GetFileNameWithoutExtension(path), "model");
        return Parse(File.ReadLines(path), dir, model, opt);
    }

    public static ImportResult Parse(IEnumerable<string> lines, string dir, string model, ImportOptions opt)
    {
        var res = new ImportResult();
        var pos = new List<Vector3>(); var col = new List<Vector4?>(); var uvs = new List<Vector2>();
        var matIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mtlDefs = new Dictionary<string, GtaMaterial>(StringComparer.OrdinalIgnoreCase);
        int currentMat = -1;

        // per object: remap global vertex index → local
        PolyMesh mesh = null; Dictionary<int, int> remap = null; string name = null; int unnamed = 0;
        void StartObject(string n)
        {
            Flush();
            mesh = new PolyMesh(); remap = new Dictionary<int, int>();
            name = ModelImporter.SafeName(n, $"{model}_{++unnamed:00}");
        }
        void Flush()
        {
            if (mesh != null && mesh.Faces.Count > 0) res.Meshes.Add(new ImportedMesh { Name = name, Mesh = mesh });
            mesh = null;
        }
        int Mat()
        {
            if (currentMat >= 0) return currentMat;
            if (!matIndex.TryGetValue("__default", out var d))
            {
                res.Materials.Add(GtaMaterial.CreateDefault(0, model + "_default", new Vector3(0.75f)));
                d = matIndex["__default"] = res.Materials.Count - 1;
            }
            return d;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            var tag = sp < 0 ? line : line[..sp];
            var rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
            var p = rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            switch (tag)
            {
                case "v":
                    if (p.Length < 3) break;
                    pos.Add(new Vector3(F(p[0]), F(p[1]), F(p[2])));
                    col.Add(p.Length >= 6 ? new Vector4(F(p[3]), F(p[4]), F(p[5]), 1) : null);
                    break;
                case "vt":
                    if (p.Length >= 2) uvs.Add(new Vector2(F(p[0]), 1f - F(p[1]))); // OBJ v goes up, ours goes down
                    else if (p.Length == 1) uvs.Add(new Vector2(F(p[0]), 1f));
                    break;
                case "o":
                case "g":
                    if (tag == "g" && mesh != null && mesh.Faces.Count == 0) { name = ModelImporter.SafeName(rest, name); break; }
                    StartObject(rest);
                    break;
                case "usemtl":
                    if (!matIndex.TryGetValue(rest, out var mi))
                    {
                        var m = mtlDefs.TryGetValue(rest, out var def) ? def : GtaMaterial.CreateDefault(0, ModelImporter.SafeName(rest, $"{model}_mat"), new Vector3(0.75f));
                        res.Materials.Add(m);
                        mi = matIndex[rest] = res.Materials.Count - 1;
                    }
                    currentMat = mi;
                    break;
                case "mtllib":
                    foreach (var kv in ReadMtl(Path.Combine(dir, rest), dir, res)) mtlDefs[kv.Key] = kv.Value;
                    break;
                case "f":
                    {
                        if (p.Length < 3) break;
                        if (mesh == null) StartObject(model);
                        var vi = new List<int>(p.Length); var tv = new List<Vector2>(p.Length); var cv = new List<Vector4>(p.Length);
                        bool bad = false;
                        foreach (var tok in p)
                        {
                            var parts = tok.Split('/');
                            if (!int.TryParse(parts[0], NumberStyles.Integer, Inv, out int v)) { bad = true; break; }
                            v = v < 0 ? pos.Count + v : v - 1;
                            if (v < 0 || v >= pos.Count) { bad = true; break; }
                            if (!remap.TryGetValue(v, out int lv)) { lv = mesh.AddVertex(pos[v]); remap[v] = lv; }
                            vi.Add(lv);
                            Vector2 uv = default;
                            if (parts.Length > 1 && parts[1].Length > 0 && int.TryParse(parts[1], NumberStyles.Integer, Inv, out int t))
                            {
                                t = t < 0 ? uvs.Count + t : t - 1;
                                if (t >= 0 && t < uvs.Count) uv = uvs[t];
                            }
                            tv.Add(uv);
                            cv.Add(col[v] ?? PolyMesh.DefaultColour);
                        }
                        if (bad || vi.Distinct().Count() < 3) break;
                        var face = new Face(vi.ToArray(), Mat(), 1u) { UV = tv.ToArray(), Col = cv.ToArray() };
                        // drop repeated corners (some exporters emit them)
                        if (vi.Distinct().Count() != vi.Count) { mesh.Faces.Add(face); mesh.CleanFaces(); }
                        else mesh.Faces.Add(face);
                        break;
                    }
            }
        }
        Flush();
        return res;
    }

    static float F(string s) => float.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0f;

    static Dictionary<string, GtaMaterial> ReadMtl(string path, string dir, ImportResult res)
    {
        var d = new Dictionary<string, GtaMaterial>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) { res.Warnings.Add($"Material library {Path.GetFileName(path)} not found."); return d; }
        GtaMaterial cur = null;
        string Tex(string rest)
        {
            // texture options (-bm 1.0 …) come before the file name: keep the last token(s) that form a file
            var toks = rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < toks.Length; i++)
            {
                var candidate = string.Join(' ', toks.Skip(i));
                var full = Path.IsPathRooted(candidate) ? candidate : Path.Combine(dir, candidate);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
            return toks.Length > 0 ? toks[^1] : null;
        }
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            // MoloMax's own exporter writes "# gta_shader name.sps": honour it on re-import
            if (cur != null && line.StartsWith("# gta_shader ", StringComparison.Ordinal)) { cur.Shader = line[13..].Replace(".sps", "").Trim(); continue; }
            if (line.Length == 0 || line[0] == '#') continue;
            int sp = line.IndexOfAny(new[] { ' ', '\t' });
            var tag = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
            var rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
            switch (tag)
            {
                case "newmtl":
                    cur = GtaMaterial.CreateDefault(0, ModelImporter.SafeName(rest, "mat"), new Vector3(0.75f));
                    cur.Shader = "default";
                    d[rest] = cur;
                    break;
                case "kd" when cur != null:
                    {
                        var p = rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (p.Length >= 3) cur.ViewportColour = new Vector3(F(p[0]), F(p[1]), F(p[2]));
                        break;
                    }
                case "map_kd" when cur != null: cur.DiffuseTexture = Tex(rest); break;
                case "map_bump" or "bump" or "norm" when cur != null: cur.BumpTexture = Tex(rest); cur.Shader = "normal"; break;
                case "map_ks" when cur != null: cur.SpecTexture = Tex(rest); cur.Shader = cur.BumpTexture != null ? "normal_spec" : "spec"; break;
            }
        }
        return d;
    }
}
