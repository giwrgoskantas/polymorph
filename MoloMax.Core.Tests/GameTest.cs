using System.Diagnostics;
using System.Numerics;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Gta.Import;
using MoloMax.Core.Gta.Check;
using MoloMax.Core.IO;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

/// <summary>`game &lt;gta folder&gt; &lt;out folder&gt;`: real-install test (keys, index, search, extract, import, round trip, map index, MLO ytyp).</summary>
static class GameTest
{
    public static int Run(string folder, string outDir)
    {
        int pass = 0, fail = 0;
        var sw = Stopwatch.StartNew();
        void Ok(string n, bool c, string info = "") { if (c) pass++; else fail++; Console.WriteLine($"  {(c ? "ok  " : "FAIL")} {n}  {info}  [{sw.ElapsedMilliseconds} ms]"); sw.Restart(); }
        void Try(string n, Func<string> a) { try { var info = a(); Ok(n, info != null, info ?? ""); } catch (Exception e) { Ok(n, false, e.GetType().Name + ": " + e.Message); } }
        Directory.CreateDirectory(outDir);
        Console.WriteLine("GTA folder: " + folder);

        GameFiles g = null;
        Try("open the install (keys + archives)", () => { g = GameFiles.Open(folder, null); return $"gen9={g.Gen9}, rpfs={g.Rpf.AllRpfs.Count}, models={g.Models.Count:N0}"; });
        if (g == null) { Console.WriteLine($"\n{pass} passed, {fail} failed"); return 1; }
        var key = GameFiles.CurrentKey;
        Try("reopen with the saved key (fast path)", () => { var g2 = GameFiles.Open(folder, null, key); return g2.Models.Count == g.Models.Count ? $"models={g2.Models.Count:N0}" : null; });

        List<GameAsset> hits = null;
        Try("search 'prop_bench'", () => { hits = g.Search("prop_bench").ToList(); return hits.Count > 0 ? $"{hits.Count} hits, first {hits[0]}" : null; });
        Try("search with two words 'prop chair'", () => { var n = g.Search("prop chair").Count(); return n > 0 ? n + " hits" : null; });
        Try("search ybn only", () => { var n = g.Search("", false, false, false, true).Take(5).Count(); return n > 0 ? "ok" : null; });

        // extract + import a handful of real files of each kind
        var samples = new List<GameAsset>();
        samples.AddRange(g.Search("prop_bench_01a").Take(1));
        samples.AddRange(g.Search("prop_rub_carwreck").Where(a => a.Ext == ".ydr").Take(1));
        samples.AddRange(g.Models.Where(a => a.Ext == ".yft" && a.Name.StartsWith("prop_")).Take(2));
        samples.AddRange(g.Models.Where(a => a.Ext == ".ydd").Take(1));
        samples.AddRange(g.Models.Where(a => a.Ext == ".ybn").Take(1));
        samples.AddRange(g.Models.Where(a => a.Ext == ".yft" && a.Path.Contains("vehicles", StringComparison.OrdinalIgnoreCase) && !a.Name.EndsWith("_hi")).Take(1));
        foreach (var a in samples)
        {
            string path = null; ImportResult r = null;
            Try($"extract {a}", () => { path = g.Extract(a, Path.Combine(outDir, "extract")); var hasYtd = File.Exists(Path.ChangeExtension(path, ".ytd")); return $"{new FileInfo(path).Length:N0} bytes, ytd={hasYtd}"; });
            if (path == null) continue;
            Try($"import {a}", () =>
            {
                r = GtaImporter.Import(path);
                int v = r.Meshes.Sum(m => m.Mesh.Verts.Count);
                foreach (var m in r.Meshes) m.Mesh.Validate();
                return r.Meshes.Count > 0 && v > 0 ? $"{r.Meshes.Count} meshes, {v:N0} verts, {r.Materials.Count} mats, {r.TexturesWritten.Count} textures, warnings={r.Warnings.Count}" : null;
            });
            if (r == null || a.Ext == ".ybn" || a.Ext == ".ydd") continue;
            Try($"round trip {a} (re-export + re-import)", () =>
            {
                var sc = new Scene();
                var map = new int[r.Materials.Count];
                for (int i = 0; i < r.Materials.Count; i++) { var m = r.Materials[i].Clone(); m.Id = sc.NextMaterialId++; sc.Materials.Add(m); map[i] = m.Id; }
                var objs = new List<SceneObject>();
                foreach (var im in r.Meshes)
                {
                    foreach (var f in im.Mesh.Faces) f.MatId = f.MatId >= 0 && f.MatId < map.Length ? map[f.MatId] : 0;
                    var o = sc.Add(im.Name, im.Mesh, Vector3.Zero); o.GtaType = im.GtaType; objs.Add(o);
                }
                var outPath = Path.Combine(outDir, "roundtrip", a.Name + "_mm" + (a.Ext == ".yft" ? ".yft" : ".ydr"));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                var sum = GtaExporter.ExportModelFile(sc, objs, outPath, null, new[] { Path.Combine(outDir, "extract") });
                var back = GtaImporter.Import(outPath);
                int v0 = r.Meshes.Where(m => m.GtaType == GtaObjectType.Drawable).Sum(m => m.Mesh.Faces.Count);
                int v1 = back.Meshes.Where(m => m.GtaType == GtaObjectType.Drawable).Sum(m => m.Mesh.Faces.Count);
                bool col0 = r.Meshes.Any(m => m.GtaType == GtaObjectType.Collision), col1 = back.Meshes.Any(m => m.GtaType == GtaObjectType.Collision);
                if (!(v1 > 0 && Math.Abs(v1 - v0) <= v0 / 10 + 2 && col0 == col1)) throw new Exception($"faces {v0}->{v1}, collision {col0}->{col1}");
                return $"faces {v0:N0} -> {v1:N0}, collision {col0}->{col1}; {sum}";
            });
        }

        // the map index (all ymaps / ytyps)
        Try("map index (every .ymap / .ytyp)", () => { g.BuildMapIndex(); var b = g.Search("prop_bench_01a").FirstOrDefault(); return $"bench placements={(b == null ? -1 : g.PlacementsOf(b).Count)}"; });
        Try("placements of a common prop", () => { var n = g.Search("prop_bin_01a").Select(a => g.PlacementsOf(a).Count).FirstOrDefault(); return n > 0 ? n + " placements" : null; });
        Try("texture dictionary from the archetype", () => { var t = samples.Select(a => g.TextureDictionaryOf(a)).FirstOrDefault(x => x != null); return t != null ? t.ToString() : null; });

        // a real interior: find an MLO ytyp, extract it with the files it names, read it
        Try("real MLO .ytyp (rooms / portals / entities)", () =>
        {
            foreach (var rpf in g.Rpf.AllRpfs)
            {
                if (rpf.AllEntries == null) continue;
                foreach (var e in rpf.AllEntries)
                {
                    if (e is not CodeWalker.GameFiles.RpfFileEntry fe || !(fe.NameLower ?? "").EndsWith(".ytyp")) continue;
                    if (!(fe.NameLower.StartsWith("v_int_") || fe.NameLower.Contains("_int"))) continue;
                    CodeWalker.GameFiles.YtypFile y;
                    try { y = CodeWalker.GameFiles.RpfFile.GetFile<CodeWalker.GameFiles.YtypFile>(fe); } catch { continue; }
                    if (y?.AllArchetypes == null || !y.AllArchetypes.Any(x => x is CodeWalker.GameFiles.MloArchetype)) continue;
                    var dir = Path.Combine(outDir, "mlo"); Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, fe.Name);
                    File.WriteAllBytes(p, fe.File.ExtractFile(fe) is { } raw && fe is CodeWalker.GameFiles.RpfResourceFileEntry rr ? CodeWalker.GameFiles.ResourceBuilder.AddResourceHeader(rr, CodeWalker.GameFiles.ResourceBuilder.Compress(raw)) : fe.File.ExtractFile(fe));
                    var mlos = YtypImporter.Read(p);
                    if (mlos.Count == 0) continue;
                    var m = mlos[0];
                    return $"{fe.Name}: {mlos.Count} MLO, first '{m.Name}' rooms={m.Rooms.Count} portals={m.Portals.Count} entities={m.Entities.Count} sets={m.Entities.Select(x => x.EntitySet).Where(x => x != null).Distinct().Count()} collisions={m.Collisions.Count}";
                }
            }
            return null;
        });

        // export a real prop as a full FiveM resource + run the checker on it
        Try("checker + full resource export of a real prop", () =>
        {
            var a = samples.FirstOrDefault(x => x.Ext is ".ydr" or ".yft");
            if (a == null) return null;
            var r = GtaImporter.Import(Path.Combine(outDir, "extract", a.Name + a.Ext));
            var sc = new Scene();
            var map = new int[r.Materials.Count];
            for (int i = 0; i < r.Materials.Count; i++) { var m = r.Materials[i].Clone(); m.Id = sc.NextMaterialId++; sc.Materials.Add(m); map[i] = m.Id; }
            foreach (var im in r.Meshes) { foreach (var f in im.Mesh.Faces) f.MatId = f.MatId >= 0 && f.MatId < map.Length ? map[f.MatId] : 0; var o = sc.Add(im.Name, im.Mesh, Vector3.Zero); o.GtaType = im.GtaType; }
            var issues = GtaChecker.Run(sc);
            GtaChecker.FixAll(sc);
            var after = GtaChecker.Run(sc);
            var rep = GtaExporter.Export(sc, Path.Combine(outDir, "resource"), null, new[] { Path.Combine(outDir, "extract") });
            int files = Directory.GetFiles(Path.Combine(outDir, "resource"), "*", SearchOption.AllDirectories).Length;
            return files > 0 ? $"issues {issues.Count} -> {after.Count} after Fix all, {files} files written" : null;
        });

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
