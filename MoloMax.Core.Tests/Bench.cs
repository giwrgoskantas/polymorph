using System.Diagnostics;
using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Import;
using MoloMax.Core.IO;

static class Bench
{
    public static void Run()
    {
        var sw = new Stopwatch();
        void T(string n, Action a) { sw.Restart(); a(); Console.WriteLine($"{n,-40} {sw.ElapsedMilliseconds,6} ms"); }
        
        var scene = new Scene();
        // ~1M verts: 20 objects of ~50k verts (sphere 160 segs ~ 25k? use plane grid)
        for (int i = 0; i < 20; i++) scene.Add("o" + i, Primitives.Plane(10, 10, 223, 223), new Vector3(i * 12, 0, 0));
        Console.WriteLine($"verts {scene.Objects.Sum(o => o.Mesh.Verts.Count):N0} faces {scene.Objects.Sum(o => o.Mesh.Faces.Count):N0}");
        T("Scene.Clone (undo snapshot)", () => scene.Clone());
        var us = new UndoStack();
        T("Undo.Record (first, all meshes new)", () => us.Record("a", scene));
        T("Undo.Record (nothing changed)", () => us.Record("b", scene));
        scene.Objects[0].Touch();
        T("Undo.Record (one object changed)", () => us.Record("c", scene));
        T("Undo (one object changed)", () => us.Undo(scene));
        T("Stats()", () => scene.Stats());
        T("RenderMeshBuilder all", () => { foreach (var o in scene.Objects) RenderMeshBuilder.Build(o.Mesh).ToList(); });
        T("RenderMeshBuilder all (warm)", () => { foreach (var o in scene.Objects) RenderMeshBuilder.Build(o.Mesh).ToList(); });
        T("Evaluated all", () => { foreach (var o in scene.Objects) o.Evaluated(); });
        T("Edges all (FaceEdges distinct)", () => { foreach (var o in scene.Objects) o.Mesh.Faces.SelectMany(f => o.Mesh.FaceEdges(f)).Distinct().Count(); });
        var ray = new Ray(new Vector3(5, 0, 50), new Vector3(0, 0, -1));
        T("DrawEdges all", () => { foreach (var o in scene.Objects) o.Mesh.DrawEdges(); });
        T("PickObject", () => Picking.PickObject(scene, ray));
        T("WorldBounds all", () => { foreach (var o in scene.Objects) o.WorldBounds(); });
        
        // import benchmark: a ~200k vert ydr
        var big = new Scene();
        var objs = new List<SceneObject>();
        for (int i = 0; i < 4; i++) objs.Add(big.Add("b" + i, Primitives.Plane(10, 10, 220, 220), new Vector3(i * 11, 0, 0)));
        var ydr = "/tmp/claude-0/bench_big.ydr";
        T("ExportModelFile 4x50k", () => GtaExporter.ExportModelFile(big, objs, ydr, null));
        for (int k = 0; k < 2; k++)
        {
            ImportResult r = null;
            T("GtaImporter.Import (total)", () => r = GtaImporter.Import(ydr));
            Console.WriteLine($"   -> {r.Meshes.Sum(m => m.Mesh.Verts.Count):N0} verts");
        }
        
    }
}

static class BenchImport
{
    public static void Run()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ydr = "/tmp/claude-0/bench_big.ydr";
        var f = new CodeWalker.GameFiles.YdrFile();
        f.Load(File.ReadAllBytes(ydr));
        Console.WriteLine($"CodeWalker load        {sw.ElapsedMilliseconds} ms"); sw.Restart();
        var m = new MoloMax.Core.Mesh.PolyMesh();
        foreach (var model in f.Drawable.DrawableModels.High)
            foreach (var g in model.Geometries)
            {
                var vd = g.VertexData; var idx = g.IndexBuffer.Indices; int b = m.Verts.Count;
                for (int i = 0; i < vd.VertexCount; i++) { var p = vd.GetVector3(i, 0); m.AddVertex(new System.Numerics.Vector3(p.X, p.Y, p.Z)); }
                for (int t = 0; t + 2 < idx.Length; t += 3) m.AddFace(new[] { b + idx[t], b + idx[t + 1], b + idx[t + 2] }, 0);
            }
        Console.WriteLine($"build polymesh         {sw.ElapsedMilliseconds} ms  ({m.Verts.Count} v)"); sw.Restart();
        MoloMax.Core.Mesh.MeshOps.WeldByDistance(m, 1e-5f);
        Console.WriteLine($"weld                   {sw.ElapsedMilliseconds} ms  ({m.Verts.Count} v)"); sw.Restart();
        m.CleanFaces();
        Console.WriteLine($"CleanFaces (tri->quad) {sw.ElapsedMilliseconds} ms  ({m.Faces.Count} f)"); sw.Restart();
        MoloMax.Core.Mesh.MeshOps.AutoSmooth(m, null, 40f);
        Console.WriteLine($"AutoSmooth             {sw.ElapsedMilliseconds} ms"); sw.Restart();
        m.RemoveUnusedVertices();
        Console.WriteLine($"RemoveUnused           {sw.ElapsedMilliseconds} ms"); sw.Restart();
    }
}

static class Reflect
{
    public static void Run(string type)
    {
        var asm = typeof(CodeWalker.GameFiles.YdrFile).Assembly;
        foreach (var t in asm.GetTypes().Where(t => t.Name == type))
        {
            Console.WriteLine(t.FullName);
            foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                Console.WriteLine("  " + m.MemberType + " " + m);
        }
    }
}

static class LightXml
{
    public static void Run()
    {
        var l = new CodeWalker.GameFiles.LightAttributes();
        l.Type = CodeWalker.GameFiles.LightType.Spot;
        var sb = new System.Text.StringBuilder();
        l.WriteXml(sb, 2);
        Console.WriteLine(sb.ToString());
        // how a drawable writes its lights
        var d = new CodeWalker.GameFiles.Drawable();
        Console.WriteLine(string.Join(",", Enum.GetNames(typeof(CodeWalker.GameFiles.LightType))));
    }
}
