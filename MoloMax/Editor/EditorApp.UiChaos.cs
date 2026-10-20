using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using Keys = System.Windows.Forms.Keys;

namespace MoloMax.Editor;

/// <summary>
/// "UI CHAOS": hundreds of random real inputs (clicks, drags, keys, menu commands, sub-object picks) on a few
/// primitives, through the real window messages. After each action: no frame error, every mesh valid and finite;
/// for each command, undo brings the scene back exactly. Things that open a file dialog or quit are left out
/// (a dialog would wait for a person).
/// </summary>
public sealed partial class EditorApp
{
    public static int FrameErrorCount;
    public static string LastFrameError;

    static readonly string[] ChaosCommands =
    {
        "Undo", "Redo", "Select All", "Select None", "Select Invert", "Select More", "Select Less", "Select Linked",
        "Select Edge Loops", "Select Edge Rings", "Make Planar (best-fit plane)", "Make Planar X", "Make Planar Y",
        "Make Planar Z (level the vertices)", "Duplicate", "Instance (linked duplicate)", "Make Unique", "Select Instances",
        "Delete", "Join", "Clean Up: fewer edges, same shape (flat areas merged, straight points removed)", "Hide Selected",
        "Hide Unselected", "Reveal Hidden", "Local View (isolate)", "Apply Rotation & Scale", "Origin to Geometry",
        "Origin to Base", "Origin to 3D Cursor", "Toggle Edit Mode", "Vertex Select", "Edge Select", "Poly Select",
        "Element Select", "Extrude", "Inset Faces", "Loop Cut", "Fill / Make Face", "Fill Holes", "Subdivide",
        "Flip Normals", "Recalculate Normals Outside", "Recalculate Normals Inside", "Separate Selection", "Connect",
        "Dissolve", "Merge by Distance", "Smooth by Angle", "Shade Flat", "Smart UV Project (unwrap)",
        "Chamfer / Bevel edges", "Knife (cut along a line)", "Bridge (2 faces or 2 edge loops)", "Boolean Union",
        "Boolean Subtract (cut the others out of the active)", "Boolean Intersect", "Snap to vertex (on / off)",
        "Snap to edge (on / off)", "Snap to face (on / off)", "Cube Projection (box map)", "Frame Selected", "Frame All",
        "Top View", "Front View", "Right View", "Perspective / Orthographic", "Toggle Wireframe", "Toggle Quad View",
        "Check Scene for GTA", "Create Drawable (GIMS: game meshes under one model)", "Collision from selection (copy under the Drawable)",
        "Add GTA light (point)", "Add GTA light (spot)", "Sculpt Mode",
    };

    // plain letters and keys; Ctrl only with these (Ctrl+S / O / N would save, open a dialog or ask)
    static readonly Keys[] ChaosKeys =
    {
        Keys.A, Keys.B, Keys.C, Keys.D, Keys.E, Keys.F, Keys.G, Keys.H, Keys.I, Keys.J, Keys.K, Keys.L, Keys.M, Keys.N,
        Keys.P, Keys.Q, Keys.R, Keys.S, Keys.T, Keys.U, Keys.V, Keys.W, Keys.X, Keys.Y, Keys.Z,
        Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.Tab, Keys.Delete, Keys.Escape, Keys.Return, Keys.Space,
        Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.Oemplus, Keys.OemMinus,
    };
    static readonly Keys[] ChaosCtrlKeys = { Keys.Z, Keys.Y, Keys.A, Keys.D, Keys.B, Keys.R, Keys.J, Keys.I, Keys.H };

    IEnumerable<object> UiChaos()
    {
        int seeds = 10, actions = 400;
        if (int.TryParse(Environment.GetEnvironmentVariable("POLYMORPH_CHAOS_ACTIONS"), out var na) && na > 0) actions = na;
        if (int.TryParse(Environment.GetEnvironmentVariable("POLYMORPH_CHAOS_SEEDS"), out var ns) && ns > 0) seeds = ns;
        var cmds = Commands().Where(c => ChaosCommands.Contains(c.Name)).ToList();
        Note($"{cmds.Count} commands, {seeds} x {actions} random actions");
        var problems = new List<string>();
        int undoChecks = 0, undoFails = 0, errors0 = FrameErrorCount, total = 0;
        var kinds = new Dictionary<string, int>();
        void Bad(string s) { if (problems.Count < 60) problems.Add(s); }

        int first = int.TryParse(Environment.GetEnvironmentVariable("POLYMORPH_CHAOS_FIRST"), out var fs) && fs > 0 ? fs : 1;
        for (int seed = first; seed < first + seeds; seed++)
        {
            var rnd = new Random(seed * 7919);
            Fresh(); paintMode = false; SculptMode = false;
            UI.ImGuiBackend.SyntheticInput = true;
            Setup(rnd);
            foreach (var v in Viewports) v.Camera.Frame(new Vector3(-5, -5, -1), new Vector3(5, 5, 4));
            yield return Frames(4);

            for (int a = 0; a < actions; a++, total++)
            {
                string what = "";
                int errBefore = FrameErrorCount;
                var vp = Viewports[ActiveViewport];
                Vector2 RandPt() => new(vp.X + 20 + (float)rnd.NextDouble() * (vp.W - 40), vp.Y + 20 + (float)rnd.NextDouble() * (vp.H - 40));
                double r = rnd.NextDouble();

                if (Scene.Objects.Count(o => o.Mesh != null) < 2) Setup(rnd);

                if (r < 0.30)
                {
                    // a menu command; undo must bring the scene back
                    var c = cmds[rnd.Next(cmds.Count)];
                    if (rnd.NextDouble() < 0.5) { RandomPick(rnd); yield return Frames(2); }
                    what = "command " + c.Name;
                    if (c.Enabled != null && !SafeBool(c.Enabled)) { what += " (disabled)"; }
                    else
                    {
                        bool checkUndo = c.Name is not ("Undo" or "Redo") && !c.Name.StartsWith("Select") && !c.Name.Contains("View")
                                         && !c.Name.StartsWith("Toggle") && !c.Name.StartsWith("Frame") && !c.Name.StartsWith("Snap");
                        string lv0 = $"level {Level}, active {Current?.Name}, {Selected.Count} selected, sub {(Current != null ? SubSel(Current).Faces.Count + "f/" + SubSel(Current).Edges.Count + "e/" + SubSel(Current).Verts.Count + "v" : "-")}";
                        string s0 = checkUndo ? SceneSig() : null; long undo0 = Undo.LastStamp;
                        Exception ex = null;
                        try { c.Run(); } catch (Exception e) { ex = e; }
                        if (ex != null) Bad($"seed {seed} #{a} {c.Name}: threw {ex.GetType().Name}: {ex.Message}");
                        yield return Frames(3);
                        checkOpen = false;
                        if (ModalActive) { CancelModal(); yield return Frames(2); if (checkUndo && Undo.LastStamp == undo0 && SceneSig() != s0) Bad($"seed {seed} #{a} {c.Name}: cancelling the tool did not restore the mesh"); }
                        else if (checkUndo && Undo.LastStamp != undo0 && SceneSig() != s0)
                        {
                            undoChecks++;
                            DoUndo(); yield return Frames(2);
                            if (SceneSig() != s0) { undoFails++; Bad($"seed {seed} #{a} {c.Name}: undo did not bring the scene back ({lv0}; undo step '{Undo.UndoName}')"); Note("  before: " + s0); Note("  after:  " + SceneSig()); }
                            if (rnd.NextDouble() < 0.6) { DoRedo(); yield return Frames(2); }
                        }
                    }
                }
                else if (r < 0.50)
                {
                    // a key, maybe with Shift / Alt / Ctrl
                    bool ctrl = rnd.NextDouble() < 0.15, shift = !ctrl && rnd.NextDouble() < 0.2, alt = !ctrl && !shift && rnd.NextDouble() < 0.15;
                    var k = ctrl ? ChaosCtrlKeys[rnd.Next(ChaosCtrlKeys.Length)] : ChaosKeys[rnd.Next(ChaosKeys.Length)];
                    if (alt && k is >= Keys.F1 and <= Keys.F24) alt = false;
                    what = $"key {(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}{k}";
                    var mods = new List<Keys>();
                    if (ctrl) mods.Add(Keys.ControlKey); if (shift) mods.Add(Keys.ShiftKey); if (alt) mods.Add(Keys.Menu);
                    foreach (var m in mods) PostKey(m, true);
                    yield return Frames(1);
                    PostKey(k, true); yield return Frames(2); PostKey(k, false); yield return Frames(1);
                    foreach (var m in mods) PostKey(m, false);
                    yield return Frames(2);
                    // what the key started (a drag tool following the mouse, a modal tool): move a little, then end it
                    if (transforming || ModalActive)
                    {
                        foreach (var x in MouseTo(RandPt(), 6)) yield return x;
                        var end = rnd.NextDouble() < 0.5 ? Keys.Escape : Keys.Return;
                        PostKey(end, true); yield return Frames(1); PostKey(end, false); yield return Frames(3);
                    }
                }
                else if (r < 0.70)
                {
                    // a click in the viewport (not on a panel or window), maybe Ctrl / Shift
                    var p = RandPt();
                    foreach (var x in MouseTo(p, 4)) yield return x;
                    yield return Frames(1);
                    if (ImGuiNET.ImGui.GetIO().WantCaptureMouse) { what = "click skipped (over a window)"; }
                    else
                    {
                        bool right = rnd.NextDouble() < 0.12;
                        int mod = rnd.NextDouble() < 0.2 ? 0x08 : rnd.NextDouble() < 0.2 ? 0x04 : 0;
                        what = $"{(right ? "right " : "")}click {(mod == 8 ? "Ctrl " : mod == 4 ? "Shift " : "")}at {p.X:0},{p.Y:0}";
                        if (mod == 8) PostKey(Keys.ControlKey, true); if (mod == 4) PostKey(Keys.ShiftKey, true);
                        PostMouse(right ? 0x204 : 0x201, p, (right ? 2 : 1) | mod); yield return Frames(2);
                        PostMouse(right ? 0x205 : 0x202, p, mod); yield return Frames(2);
                        if (mod == 8) PostKey(Keys.ControlKey, false); if (mod == 4) PostKey(Keys.ShiftKey, false);
                        yield return Frames(1);
                        if (right) { PostKey(Keys.Escape, true); yield return Frames(1); PostKey(Keys.Escape, false); yield return Frames(2); }
                    }
                }
                else if (r < 0.85)
                {
                    // a drag: left (box select / gizmo / grab), middle (grab or orbit), with Alt (orbit)
                    var p = RandPt(); var q = RandPt();
                    foreach (var x in MouseTo(p, 4)) yield return x;
                    yield return Frames(1);
                    if (ImGuiNET.ImGui.GetIO().WantCaptureMouse) { what = "drag skipped (over a window)"; }
                    else
                    {
                        int b = rnd.Next(3);
                        bool alt = b == 1 && rnd.NextDouble() < 0.5;
                        int down = b == 0 ? 0x201 : 0x207, up = b == 0 ? 0x202 : 0x208, mask = b == 0 ? 1 : 0x10;
                        if (b == 2) { down = 0x201; up = 0x202; mask = 1; }
                        what = $"drag {(b == 0 ? "left" : b == 1 ? "middle" : "left (after gizmo pick)")}{(alt ? " Alt" : "")} {p.X:0},{p.Y:0} -> {q.X:0},{q.Y:0}";
                        if (b == 2 && Selected.Count > 0) { var c = Scene.Find(ActiveId); if (c != null) p = ScreenOf(vp, Vector3.Transform(Vector3.Zero, c.World)) + new Vector2(30, 0); }
                        if (alt) PostKey(Keys.Menu, true);
                        PostMouse(down, p, mask); yield return Frames(2);
                        foreach (var x in MouseTo(q, 8 + rnd.Next(10), mask)) yield return x;
                        PostMouse(up, q, 0); yield return Frames(2);
                        if (alt) PostKey(Keys.Menu, false);
                        yield return Frames(1);
                    }
                }
                else if (r < 0.95)
                {
                    what = RandomPick(rnd);
                    yield return Frames(3);
                }
                else
                {
                    // the wheel (zoom), sometimes with Ctrl (chamfer segments / tools)
                    var p = RandPt();
                    foreach (var x in MouseTo(p, 3)) yield return x;
                    int delta = (rnd.NextDouble() < 0.5 ? 120 : -120) * (1 + rnd.Next(3));
                    what = $"wheel {delta}";
                    PostMessage(form.Handle, 0x20A, (IntPtr)(delta << 16), (IntPtr)(((int)(p.Y + form.Location.Y) << 16) | ((int)(p.X + form.Location.X) & 0xFFFF)));
                    yield return Frames(2);
                }

                // after every action: release everything, end a drag tool still running
                if (transforming) { PostKey(Keys.Escape, true); yield return Frames(1); PostKey(Keys.Escape, false); yield return Frames(2); }
                kinds[what.Split(' ')[0]] = kinds.GetValueOrDefault(what.Split(' ')[0]) + 1;

                if (FrameErrorCount != errBefore) Bad($"seed {seed} #{a} {what}: frame error: {LastFrameError?.Split('\n')[0]}");
                foreach (var o in Scene.Objects)
                {
                    if (o.Mesh == null) continue;
                    var bad = MeshProblem(o.Mesh);
                    if (bad != null) { Bad($"seed {seed} #{a} {what}: {o.Name}: {bad}"); break; }
                }
                if (status != null && status.StartsWith("Error")) { Bad($"seed {seed} #{a} {what}: status \"{status}\""); status = ""; }
                if (a % 200 == 199) yield return Shot($"chaos_s{seed}_{a + 1}");
            }
            if (ModalActive) CancelModal();
            if (Level != SubObject.Object) SetLevel(Level);
        }
        UI.ImGuiBackend.SyntheticInput = false;
        Note($"{total} actions: " + string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}")));
        Note($"undo checked {undoChecks} times, {undoFails} failed; frame errors {FrameErrorCount - errors0}");
        foreach (var p in problems) Note("PROBLEM " + p);
        Expect(problems.Count == 0, $"{total} random actions in the real window: {problems.Count} problem(s)");
    }


    /// <summary>Another object, a level, a random sub-selection (1 to 6 faces / edges / points).</summary>
    string RandomPick(Random rnd)
    {
        var objs = Scene.Objects.Where(o => o.Mesh != null && o.Mesh.Faces.Count > 0).ToList();
        if (objs.Count == 0) return "pick (nothing)";
        var o = objs[rnd.Next(objs.Count)];
        var lvl = (SubObject)rnd.Next(0, 6);
        if (Level != SubObject.Object) SetLevel(Level);
        SelectOnly(o);
        if (lvl != SubObject.Object) SetLevel(lvl);
        if (Level != SubObject.Object && Current?.Mesh is { } m)
        {
            var ss = SubSel(Current); int n = 1 + rnd.Next(6);
            ss.Faces.Clear(); ss.Edges.Clear(); ss.Verts.Clear();
            for (int i = 0; i < n; i++)
            {
                int fi = rnd.Next(m.Faces.Count); var f = m.Faces[fi];
                if (f.Count < 2) continue;
                int c0 = rnd.Next(f.Count);
                ss.Faces.Add(fi); ss.Edges.Add(EdgeKey.Make(f.V[c0], f.V[(c0 + 1) % f.Count])); ss.Verts.Add(f.V[c0]);
            }
            SelectionChanged();
        }
        return $"pick {o.Name} level {Level}";
    }

    static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

    void Setup(Random rnd)
    {
        var kinds = new[] { CreateKind.Box, CreateKind.Sphere, CreateKind.Cylinder, CreateKind.Box, CreateKind.Plane };
        for (int i = 0; i < 4; i++)
        {
            var o = Prim(kinds[rnd.Next(kinds.Length)], new Vector3(-4 + i * 2.7f, (float)rnd.NextDouble() * 2 - 1, 0));
            o.Name = "chaos_" + o.Id;
        }
    }

    string SceneSig()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in Scene.Objects.OrderBy(o => o.Id))
        {
            sb.Append(o.Id).Append(':').Append(o.Name).Append(o.Hidden ? 'h' : 'v');
            var w = o.World; sb.Append($"{w.M41:0.###},{w.M42:0.###},{w.M43:0.###},{w.M11:0.###},{w.M22:0.###};");
            if (o.Mesh is { } m)
            {
                double s = 0; foreach (var v in m.Verts) s += v.X * 1.1 + v.Y * 1.7 + v.Z * 2.3;
                sb.Append($"{m.Verts.Count}/{m.Faces.Count}/{s:0.###}");
            }
            sb.Append('|');
        }
        return sb.ToString();
    }

    static string MeshProblem(PolyMesh m)
    {
        try { m.Validate(); } catch (Exception e) { return "invalid mesh: " + e.Message; }
        foreach (var v in m.Verts) if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) return "point not a number";
        return null;
    }
}
