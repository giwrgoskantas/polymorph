using System.Numerics;
using System.Text.Json;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.IO;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Library;

/// <summary>
/// Builds a Molo texture library from the user's own GTA V install: every interior (MLO) of the game is read, each
/// material used in it (shader, diffuse / normal / spec, settings) is listed once with where it is used (floor, wall,
/// ceiling), its texture size per metre and the interiors it appears in, and its textures are written as .dds.
/// Same format as the library made with molo_material (data/materials.json + textures/*.dds), so the Molo Materials
/// gallery, Paint and the export use it as is. Nothing from the game is shipped: everyone extracts from their own copy.
/// </summary>
public static class LibraryBuilder
{
    public sealed class Result
    {
        public int Interiors, Models, Materials, Textures, Failed;
        public TimeSpan Time;
        public override string ToString() => $"{Materials:N0} materials from {Interiors:N0} interiors ({Models:N0} models, {Textures:N0} textures) in {Time.TotalMinutes:0.0} min" + (Failed > 0 ? $", {Failed} model(s) skipped" : "");
    }

    sealed class Acc
    {
        public string Shader, Diffuse, Bump, Spec, Detail;
        public Dictionary<string, float[]> Params = new();
        public double Floor, Wall, Ceil, Other, TexelSum, TexelW;
        public readonly HashSet<uint> Models = new();
        public readonly Dictionary<string, int> Mlos = new();
    }

    public static Result Build(GameFiles g, string folder, Action<string, float> progress, CancellationToken cancel = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new Result();
        var texDir = Path.Combine(folder, "textures");
        Directory.CreateDirectory(texDir); Directory.CreateDirectory(Path.Combine(folder, "data"));
        var tmp = Path.Combine(Path.GetTempPath(), "polymorph_library_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            progress?.Invoke("Reading the interiors of the game...", 0);
            var interiors = g.Interiors((s, f) => progress?.Invoke(s, f * 0.05f));
            res.Interiors = interiors.Count;
            // every model placed in an interior, with the interiors it is in
            var mlosOf = new Dictionary<uint, HashSet<string>>();
            foreach (var (name, ytyp, archs) in interiors)
                foreach (var h in archs) { if (!mlosOf.TryGetValue(h, out var set)) mlosOf[h] = set = new(); set.Add(name ?? ytyp); }
            var work = new List<(GameAsset Asset, uint Arch, uint Txd)>();
            var seen = new HashSet<string>();
            foreach (var h in mlosOf.Keys)
            {
                var a = g.FindModel(h) ?? g.FindDictionaryOf(h);
                if (a == null || !seen.Add(a.Path)) continue;
                var chain = g.TextureChainOf(a, h);
                work.Add((a, h, chain.Count > 0 ? chain[0].Hash : 0));
            }
            // models sharing a texture dictionary one after the other: each dictionary is read once
            work.Sort((x, y) => x.Txd != y.Txd ? x.Txd.CompareTo(y.Txd) : string.CompareOrdinal(x.Asset.Path, y.Asset.Path));
            var acc = new Dictionary<string, Acc>(); int missing = 0;
            var logPath = Path.Combine(folder, "data", "build_log.txt");
            void Log(string line) { try { File.AppendAllText(logPath, line + "\r\n"); } catch { } }
            try { File.Delete(logPath); } catch { }
            Log($"{interiors.Count} interiors, {work.Count} models");
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(texDir, "*.dds")) written.Add(Path.GetFileNameWithoutExtension(f));
            uint curTxd = uint.MaxValue;
            var groupTex = new Dictionary<string, Cw.Texture>(StringComparer.OrdinalIgnoreCase);
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < work.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var (a, arch, txd) = work[i];
                if (txd != curTxd)
                {
                    curTxd = txd; groupTex.Clear(); known.Clear();
                    foreach (var ytd in g.TextureChainOf(a, arch))
                    {
                        try
                        {
                            var y = g.LoadYtd(ytd);
                            foreach (var t in y?.TextureDict?.Textures?.data_items ?? Array.Empty<Cw.Texture>())
                                if (t?.Name != null && !known.ContainsKey(t.Name)) { var sn = SafeName(t.Name); groupTex.TryAdd(sn, t); known[t.Name] = Path.Combine(texDir, sn + ".dds"); }
                        }
                        catch { }
                    }
                }
                if (i % 200 == 0) Log($"{DateTime.Now:HH:mm:ss} {i}/{work.Count} models, {acc.Count} materials, {res.Textures} textures, group {groupTex.Count} textures, {missing} refs not found, {res.Failed} failed");
                if (i % 20 == 0) progress?.Invoke($"{a.Name}{a.Ext}  ({i + 1:N0} / {work.Count:N0} models, {acc.Count:N0} materials)", 0.05f + 0.9f * i / Math.Max(1, work.Count));
                ImportResult imp;
                var file = Path.Combine(tmp, "m" + a.Ext);
                try
                {
                    File.WriteAllBytes(file, g.ModelBytes(a));
                    imp = GtaImporter_Import(file, known, texDir);
                    foreach (var tw in imp.TexturesWritten) if (written.Add(Path.GetFileNameWithoutExtension(tw))) res.Textures++; // textures inside the model itself
                }
                catch { res.Failed++; continue; }
                finally { try { File.Delete(file); } catch { } }
                res.Models++;
                var mlos = mlosOf.TryGetValue(arch, out var ms) ? ms : new HashSet<string>();
                foreach (var mesh in imp.Meshes)
                {
                    if (mesh.GtaType != Scenes.GtaObjectType.Drawable) continue;
                    var m = mesh.Mesh;
                    foreach (var face in m.Faces)
                    {
                        if (face.MatId < 0 || face.MatId >= imp.Materials.Count) continue;
                        var mat = imp.Materials[face.MatId];
                        string d = Tex(mat.DiffuseTexture), b = Tex(mat.BumpTexture), s = Tex(mat.SpecTexture);
                        if (d == null && b == null) continue;
                        var key = $"{mat.Shader}|{d}|{b}|{s}";
                        if (!acc.TryGetValue(key, out var e))
                        {
                            acc[key] = e = new Acc { Shader = mat.Shader, Diffuse = d, Bump = b, Spec = s, Detail = Tex(mat.DetailTexture) };
                            e.Params["bumpiness"] = new[] { mat.Bumpiness };
                            e.Params["specularintensitymult"] = new[] { mat.SpecularIntensity };
                            e.Params["specularfalloffmult"] = new[] { mat.SpecularFalloff };
                            e.Params["specularfresnel"] = new[] { mat.SpecularFresnel };
                            e.Params["emissivemultiplier"] = new[] { mat.EmissiveMultiplier };
                            e.Params["specmapintmask"] = new[] { mat.SpecMapIntMask.X, mat.SpecMapIntMask.Y, mat.SpecMapIntMask.Z };
                        }
                        e.Models.Add(arch);
                        foreach (var ml in mlos) e.Mlos[ml] = e.Mlos.GetValueOrDefault(ml) + 1;
                        // where it is used and how big the texture is per metre
                        var (area, n, uvArea) = Measure(m, face);
                        if (!(area > 1e-6) || !double.IsFinite(area)) continue;
                        if (n.Z > 0.7f) e.Floor += area; else if (n.Z < -0.7f) e.Ceil += area; else if (MathF.Abs(n.Z) < 0.3f) e.Wall += area; else e.Other += area;
                        if (uvArea > 1e-9 && double.IsFinite(uvArea)) { e.TexelSum += Math.Sqrt(uvArea / area) * area; e.TexelW += area; }
                    }
                }
                // the textures this model's materials use, written once
                foreach (var mat in imp.Materials)
                    foreach (var t in new[] { mat.DiffuseTexture, mat.BumpTexture, mat.SpecTexture, mat.DetailTexture })
                    {
                        var n = Tex(t);
                        if (n == null || written.Contains(n)) continue;
                        if (!groupTex.TryGetValue(n, out var src)) { missing++; continue; }
                        try { File.WriteAllBytes(Path.Combine(texDir, n + ".dds"), CodeWalker.Utils.DDSIO.GetDDSFile(src)); written.Add(n); res.Textures++; }
                        catch { }
                    }
            }
            progress?.Invoke("Writing the catalogue...", 0.97f);
            var list = acc.Values.Where(e => e.Floor + e.Wall + e.Ceil + e.Other > 0)
                                 .OrderByDescending(e => e.Models.Count).ToList();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var fs = File.Create(Path.Combine(folder, "data", "materials.json")))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = false }))
            {
                w.WriteStartArray();
                foreach (var e in list)
                {
                    var baseId = e.Diffuse ?? e.Bump; var id = baseId;
                    if (!ids.Add(id)) { id = baseId + "__" + e.Shader; for (int k = 2; !ids.Add(id); k++) id = baseId + "__" + e.Shader + k; }
                    w.WriteStartObject();
                    w.WriteString("id", id);
                    w.WriteString("shader", e.Shader);
                    w.WriteStartObject("tex");
                    void T(string sampler, string name) { if (name == null) return; w.WriteStartObject(sampler); w.WriteString("n", name); w.WriteBoolean("ok", written.Contains(name)); w.WriteEndObject(); }
                    T("DiffuseSampler", e.Diffuse); T("BumpSampler", e.Bump); T("SpecSampler", e.Spec); T("DetailSampler", e.Detail);
                    w.WriteEndObject();
                    w.WriteStartObject("par");
                    foreach (var (k, v) in e.Params) { w.WriteStartArray(k); foreach (var x in v) w.WriteNumberValue(Fin(MathF.Round(x, 5))); w.WriteEndArray(); }
                    w.WriteEndObject();
                    w.WriteStartObject("surf");
                    w.WriteNumber("floor", Fin(Math.Round(e.Floor, 2))); w.WriteNumber("wall", Fin(Math.Round(e.Wall, 2))); w.WriteNumber("ceil", Fin(Math.Round(e.Ceil, 2))); w.WriteNumber("other", Fin(Math.Round(e.Other, 2)));
                    w.WriteEndObject();
                    w.WriteNumber("texel", e.TexelW > 0 ? Fin(Math.Round(e.TexelSum / e.TexelW, 4)) : 0);
                    w.WriteNumber("uses", e.Models.Count);
                    w.WriteStartArray("mlos");
                    foreach (var ml in e.Mlos.OrderByDescending(kv => kv.Value).Take(12)) w.WriteStringValue(ml.Key.ToLowerInvariant());
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            File.WriteAllText(Path.Combine(folder, "data", "built_by_polymorph.txt"),
                $"Built from the GTA V install {g.Folder} on {DateTime.Now:yyyy-MM-dd HH:mm}. For your own use: these textures belong to Rockstar Games.\r\n");
            res.Materials = list.Count;
            res.Time = sw.Elapsed;
            progress?.Invoke("Done: " + res, 1);
            return res;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    static ImportResult GtaImporter_Import(string file, Dictionary<string, string> known, string texDir) =>
        Import.GtaImporter.Import(file, new ImportOptions { KnownTextures = known, TextureFolder = texDir, WeldVertices = false, AutoSmoothAngle = 0, SkipExistingTextures = true });

    static double Fin(double v) => double.IsFinite(v) ? v : 0;
    static float Fin(float v) => float.IsFinite(v) ? v : 0;

    /// <summary>A texture reference (file path or bare name) as the library's texture name.</summary>
    static string Tex(string t) => string.IsNullOrEmpty(t) ? null : Path.GetFileNameWithoutExtension(t);

    static string SafeName(string n) => ModelImporter.SafeName(n, "texture");

    static (double Area, Vector3 N, double UvArea) Measure(Mesh.PolyMesh m, Mesh.Face f)
    {
        var a = Vector3.Zero; double uv = 0;
        var p0 = m.Verts[f.V[0]]; var u0 = f.UV[0];
        for (int i = 1; i + 1 < f.V.Length; i++)
        {
            a += Vector3.Cross(m.Verts[f.V[i]] - p0, m.Verts[f.V[i + 1]] - p0);
            var e1 = f.UV[i] - u0; var e2 = f.UV[i + 1] - u0;
            uv += Math.Abs(e1.X * e2.Y - e1.Y * e2.X) * 0.5;
        }
        float len = a.Length();
        return (len * 0.5, len > 1e-9f ? a / len : Vector3.UnitZ, uv);
    }
}
