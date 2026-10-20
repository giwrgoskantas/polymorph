using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

/// <summary>
/// "Chaos" test: thousands of random modelling operations on random shapes. After each one the mesh must stay valid
/// (no missing point, no NaN, triangulates), a closed solid must stay closed when the operation keeps solids closed,
/// the copy taken before (what undo keeps) must be untouched, and a save / open must give the same mesh back.
/// Every failure is printed with its seed and step so it can be replayed.
/// </summary>
static class Chaos
{
    public sealed record Failure(int Seed, int Step, string Op, string What);

    public static (int Seed, int Step) Replay = (-1, -1);
    public static PolyMesh ReplayBefore, ReplayAfter;
    static bool tracing;
    static void T(string s) { if (tracing) Console.WriteLine("    " + s); }

    public static List<Failure> Run(int seeds, int steps, Action<string> log = null, int firstSeed = 1)
    {
        var fails = new List<Failure>();
        for (int seed = firstSeed; seed < firstSeed + seeds; seed++)
        {
            var rnd = new Random(seed);
            var m = Shape(rnd);
            for (int step = 0; step < steps; step++)
            {
                if (m.Faces.Count == 0 || m.Faces.Count > 60000) { m = Shape(rnd); continue; }
                var before = m.Clone(); var beforeSig = Sig(before);
                bool closed = m.IsClosedManifold(out _);
                string op = "?"; bool keepsClosed = false;
                tracing = Replay == (seed, step);
                if (tracing) { ReplayBefore = before.Clone(); Console.WriteLine($"  replay seed {seed} step {step}: {m.Verts.Count} points, {m.Faces.Count} faces, closed {closed}"); }
                try
                {
                    (op, keepsClosed) = Apply(m, rnd);
                }
                catch (Exception e)
                {
                    fails.Add(new(seed, step, op, "threw " + e.GetType().Name + ": " + e.Message));
                    m = before; continue;
                }
                if (tracing) ReplayAfter = m.Clone();
                string bad = Check(m);
                if (bad == null && closed && keepsClosed && !m.IsClosedManifold(out var why)) bad = "solid opened: " + why;
                if (bad == null && Sig(before) != beforeSig) bad = "the copy taken before (undo) was changed";
                if (bad == null && step % 7 == 0)
                {
                    var sc = new Scene(); sc.Add("x", m, Vector3.Zero);
                    var back = SceneIO.FromJson(SceneIO.ToJson(sc)).Objects[0].Mesh;
                    if (Sig(back) != Sig(m)) bad = "save / open changed the mesh";
                }
                if (bad != null)
                {
                    fails.Add(new(seed, step, op, bad));
                    log?.Invoke($"  seed {seed} step {step} {op}: {bad}");
                    m = before; // keep going from the good state
                }
            }
            // the result of the run goes out to the game format and to .glb, and must come back the same size
            if (ExportChecks && m.Faces.Count > 0 && m.Faces.Count < 20000)
            {
                string bad = RoundTrip(m, seed);
                if (bad != null) { fails.Add(new(seed, steps, "export round trip", bad)); log?.Invoke($"  seed {seed} export: {bad}"); }
            }
        }
        return fails;
    }

    public static bool ExportChecks = true;

    static string RoundTrip(PolyMesh m, int seed)
    {
        var dir = Path.Combine(Path.GetTempPath(), "molo_chaos"); Directory.CreateDirectory(dir);
        var sc = new Scene(); var o = sc.Add("chaos" + seed, m.Clone(), Vector3.Zero);
        var (mn, mx) = m.Bounds();
        int tris = m.Faces.Sum(f => f.V.Length - 2);
        foreach (var ext in new[] { ".ydr", ".glb" })
        {
            var file = Path.Combine(dir, $"chaos{seed}{ext}");
            try
            {
                if (ext == ".ydr") MoloMax.Core.Gta.Export.GtaExporter.ExportModelFile(sc, new[] { o }, file, null);
                else MoloMax.Core.IO.GlbExporter.Export(sc, new[] { o }, file);
                var r = MoloMax.Core.IO.ModelImporter.Import(file);
                var parts = r.Meshes.Where(x => x.GtaType != GtaObjectType.Collision).ToList();
                if (parts.Count == 0) return ext + ": nothing came back";
                var b0 = new Vector3(float.MaxValue); var b1 = new Vector3(float.MinValue); int t = 0;
                foreach (var x in parts) { var (a, b) = x.Mesh.Bounds(); b0 = Vector3.Min(b0, a); b1 = Vector3.Max(b1, b); t += x.Mesh.Faces.Sum(f => f.V.Length - 2); }
                if (ext == ".glb") { b0 = new Vector3(b0.X, b0.Y, b0.Z); }
                float tol = 0.01f + (mx - mn).Length() * 0.002f;
                if (Vector3.Distance(Vector3.Min(b0, b1), Vector3.Min(mn, mx)) > tol && Vector3.Distance(b0, mn) > tol) return $"{ext}: size changed {mn}..{mx} -> {b0}..{b1}";
                if (Vector3.Distance(b1, mx) > tol) return $"{ext}: size changed {mn}..{mx} -> {b0}..{b1}";
                if (t < tris * 0.9) return $"{ext}: {tris} triangles out, {t} back";
            }
            catch (Exception e) { return $"{ext}: threw {e.GetType().Name}: {e.Message}"; }
        }
        return null;
    }

    static PolyMesh Shape(Random r) => r.Next(8) switch
    {
        0 => Primitives.Box(1 + r.NextSingle() * 3, 1 + r.NextSingle() * 3, 1 + r.NextSingle() * 3, 1 + r.Next(3), 1 + r.Next(3), 1 + r.Next(3)),
        1 => Primitives.Cylinder(0.5f + r.NextSingle(), 1 + r.NextSingle() * 2, 6 + r.Next(12), 1 + r.Next(3)),
        2 => Primitives.Sphere(0.5f + r.NextSingle(), 8 + r.Next(10)),
        3 => Primitives.Plane(2 + r.NextSingle() * 3, 2 + r.NextSingle() * 3, 1 + r.Next(6), 1 + r.Next(6)),
        4 => Primitives.Tube(1 + r.NextSingle(), 0.5f, 1 + r.NextSingle(), 8 + r.Next(8)),
        5 => Primitives.Room(3 + r.NextSingle() * 4, 3 + r.NextSingle() * 4, 2.5f + r.NextSingle()),
        6 => Primitives.Stairs(1 + r.NextSingle(), 3 + r.Next(6), 1 + r.NextSingle() * 2, 1 + r.NextSingle() * 2),
        _ => Primitives.Cone(0.5f + r.NextSingle(), r.NextSingle() * 0.5f, 1 + r.NextSingle(), 6 + r.Next(10)),
    };

    static HashSet<int> SomeFaces(PolyMesh m, Random r, int max = 12)
    {
        var s = new HashSet<int>(); int n = 1 + r.Next(Math.Min(max, m.Faces.Count));
        if (r.Next(3) == 0)
        {
            // a connected patch (what people select by hand)
            int seed = r.Next(m.Faces.Count); s.Add(seed);
            var vs = new HashSet<int>(m.Faces[seed].V);
            for (int i = 0; i < m.Faces.Count && s.Count < n; i++) if (m.Faces[i].V.Any(vs.Contains)) s.Add(i);
            return s;
        }
        for (int i = 0; i < n; i++) s.Add(r.Next(m.Faces.Count));
        return s;
    }

    static HashSet<EdgeKey> SomeEdges(PolyMesh m, Random r, int max = 8)
    {
        var all = new List<EdgeKey>();
        var seen = new HashSet<EdgeKey>();
        foreach (var f in m.Faces) foreach (var e in m.FaceEdges(f)) if (seen.Add(e)) all.Add(e);
        var s = new HashSet<EdgeKey>(); int n = 1 + r.Next(Math.Min(max, all.Count));
        if (r.Next(2) == 0 && all.Count > 0) { foreach (var e in MeshOps.EdgeLoop(m, all[r.Next(all.Count)])) { s.Add(e); if (s.Count >= max * 3) break; } if (s.Count > 0) return s; }
        for (int i = 0; i < n; i++) s.Add(all[r.Next(all.Count)]);
        return s;
    }

    static HashSet<int> SomeVerts(PolyMesh m, Random r, int max = 8)
    {
        var s = new HashSet<int>(); int n = 1 + r.Next(Math.Min(max, m.Verts.Count));
        for (int i = 0; i < n; i++) s.Add(r.Next(m.Verts.Count));
        return s;
    }

    /// <summary>One random operation. Returns its name and whether it keeps a closed solid closed.</summary>
    static HashSet<int> TF(HashSet<int> f) { T("faces " + string.Join(",", f)); return f; }
    static HashSet<EdgeKey> TE(HashSet<EdgeKey> e) { T("edges " + string.Join(" ", e.Select(x => x.A + "-" + x.B))); return e; }

    static (string, bool) Apply(PolyMesh m, Random r)
    {
        float amt = 0.02f + r.NextSingle() * 0.4f;
        switch (r.Next(22))
        {
            case 0: { var fs = TF(SomeFaces(m, r)); float h = (r.NextSingle() - 0.3f) * 0.8f; bool bp = r.Next(3) == 0; T($"height {h} byPolygon {bp}"); MeshOps.ExtrudeFaces(m, fs, h, bp); return ("extrude", true); }
            case 1: MeshOps.InsetFaces(m, TF(SomeFaces(m, r)), amt * 0.3f, r.Next(3) == 0); return ("inset", true);
            case 2: MeshOps.BevelFaces(m, TF(SomeFaces(m, r)), amt, amt * 0.2f, r.Next(3) == 0); return ("bevel", true);
            case 3: MeshOps.ChamferEdges(m, TE(SomeEdges(m, r)).ToList(), amt * 0.2f, 1 + r.Next(4)); return ("chamfer", true);
            case 4: MeshOps.ConnectEdges(m, TE(SomeEdges(m, r)), 1 + r.Next(3)); return ("connect", true);
            case 5: MeshOps.Tessellate(m, TF(SomeFaces(m, r))); return ("tessellate", true);
            case 6: { var all = SomeEdges(m, r, 1); MeshOps.LoopCut(m, all.First()); return ("loop cut", true); }
            case 7: MeshOps.FlipFaces(m, TF(SomeFaces(m, r))); return ("flip", false);
            case 8: MeshOps.DeleteFaces(m, TF(SomeFaces(m, r, 3))); return ("delete faces", false);
            case 9: MeshOps.CapBorders(m); return ("cap holes", false);
            case 10: MeshOps.WeldByDistance(m, amt * 0.1f); m.CleanFaces(); return ("weld", false);
            case 11: MeshOps.RemoveEdges(m, SomeEdges(m, r, 3), r.Next(2) == 0); return ("remove edges", false);
            case 12: MeshOps.Collapse(m, SomeVerts(m, r, 3)); m.CleanFaces(); return ("collapse", false);
            case 13: MeshOps.CloneFaces(m, TF(SomeFaces(m, r))); return ("clone faces", false);
            case 14: { var c = MeshOps.Slice(m, new Vector3(r.NextSingle() - 0.5f, r.NextSingle() - 0.5f, r.NextSingle()), Vector3.Normalize(new Vector3(r.NextSingle() - 0.5f, r.NextSingle() - 0.5f, r.NextSingle() - 0.5f) + new Vector3(1e-3f))); return ("slice", true); }
            case 15: MeshOps.AutoSmooth(m, null, 30 + r.Next(40)); return ("auto smooth", true);
            case 16: MeshOps.BoxMap(m, TF(SomeFaces(m, r)), 0.5f + r.NextSingle()); return ("box map", true);
            case 17: UvUnwrap.SmartProject(m); return ("smart uv", true);
            case 18:
                {
                    var c = MeshClean.Run(m, new CleanOptions { Quads = r.Next(2) == 0 }, out _);
                    m.Verts = c.Verts; m.Faces = c.Faces; return ("clean up", true);
                }
            case 19: MeshOps.DissolveStraightVertices(m, SomeVerts(m, r, 20)); return ("dissolve straight", true);
            case 20: { var e = SomeEdges(m, r, 4); MeshOps.ChamferEdges(m, e.ToList(), amt * 0.05f, 1); return ("small chamfer", true); }
            default: MeshOps.RecalcNormals(m); return ("recalc normals", true);
        }
    }

    /// <summary>Null when the mesh is sound, else what is wrong.</summary>
    static string Check(PolyMesh m)
    {
        try { m.Validate(); } catch (Exception e) { return "invalid: " + e.Message; }
        foreach (var v in m.Verts) if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) return "a point is NaN / infinite";
        foreach (var f in m.Faces) foreach (var uv in f.UV) if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) return "a UV is NaN / infinite";
        try
        {
            var g = RenderMeshBuilder.Build(m);
            int tris = g.Sum(x => x.IndexCount) / 3, want = m.Faces.Sum(f => f.V.Length - 2);
            if (tris != want) return $"triangulation gives {tris} triangles instead of {want}";
            foreach (var x in g) foreach (var v in x.VertexArray) if (!float.IsFinite(v.Normal.X) || !float.IsFinite(v.Tangent.X)) return "NaN normal / tangent in the viewport data";
        }
        catch (Exception e) { return "viewport build threw " + e.Message; }
        return null;
    }

    static long Sig(PolyMesh m)
    {
        unchecked
        {
            long h = m.Verts.Count * 31 + m.Faces.Count;
            foreach (var v in m.Verts) h = h * 1000003 ^ BitConverter.SingleToInt32Bits(v.X) ^ (long)BitConverter.SingleToInt32Bits(v.Y) << 11 ^ (long)BitConverter.SingleToInt32Bits(v.Z) << 23;
            foreach (var f in m.Faces)
            {
                h = h * 31 + f.MatId * 7 + f.Smooth;
                for (int i = 0; i < f.V.Length; i++) h = h * 1000003 ^ f.V[i] ^ (long)BitConverter.SingleToInt32Bits(f.UV[i].X) << 7 ^ (long)BitConverter.SingleToInt32Bits(f.Col[i].Y) << 17;
            }
            return h;
        }
    }
}
