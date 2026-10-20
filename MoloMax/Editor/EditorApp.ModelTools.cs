using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>Bridge, booleans and vertex snapping.</summary>
public sealed partial class EditorApp
{
    /// <summary>Snap moves onto the vertex under the mouse instead of the grid (when snapping is on).</summary>
    public bool SnapToVertex { get => SnapVertexT; set => SnapVertexT = value; }

    public void Bridge()
    {
        var cur = Current;
        if (cur == null || Level == SubObject.Object) { SetStatus("Bridge works in edit mode: two faces (face mode) or two border loops (edge mode)."); return; }
        var s = SubSel(cur);
        if (Level is SubObject.Polygon or SubObject.Element)
        {
            if (s.Faces.Count < 2) { SetStatus("Bridge: select two faces (or two groups of faces) to join."); return; }
            int n = 0;
            DoMesh("Bridge", (o, m, sel) =>
            {
                var f = sel.Faces.ToArray();
                if (f.Length == 2 && m.Faces[f[0]].V.Length == m.Faces[f[1]].V.Length) { int corners = m.Faces[f[0]].V.Length; n = MeshOps.BridgeFaces(m, f[0], f[1]) ? corners : 0; }
                else n = MeshOps.BridgeFaceGroups(m, sel.Faces);
                if (n > 0) sel.Faces.Clear();
            });
            if (n == 0) Undo.DropLast();
            SetStatus(n > 0 ? $"Bridged: {n} new face(s)." : "Bridge: the selection must be two separate faces / groups of faces, each with one outline.");
        }
        else
        {
            int n = 0;
            DoMesh("Bridge", (o, m, sel) =>
            {
                var chains = MeshOps.EdgeChains(sel.Edges);
                if (chains.Count == 2) n = MeshOps.BridgeChains(m, chains[0].Verts, chains[0].Closed, chains[1].Verts, chains[1].Closed);
            });
            if (n == 0) Undo.DropLast();
            SetStatus(n > 0 ? $"Bridged: {n} new face(s)." : "Bridge: select two edges, two edge chains or two edge loops (not touching).");
        }
    }

    public void Boolean(Csg.Op op)
    {
        var a = Current;
        var cutters = SelObjects().Where(o => o != a && o.Mesh.Faces.Count > 0).ToList();
        if (a == null || cutters.Count == 0) { SetStatus("Select the object to keep last (active), with the cutter(s) also selected."); return; }
        PolyMesh result = a.Mesh;
        try { foreach (var c in cutters) result = Csg.Apply(result, a.World, c.Mesh, c.World, op); }
        catch (Exception e) { SetStatus($"Boolean {op} failed: {e.Message} Nothing was changed."); return; }
        if (result.Faces.Count == 0 && op != Csg.Op.Intersect) { SetStatus($"Boolean {op} gave an empty mesh. Nothing was changed."); return; }
        Do("Boolean " + op, () =>
        {
            a.Mesh = result; a.Touch();
            foreach (var c in cutters) c.Hidden = true; // kept (hidden) so you can move them and do it again
        });
        Selected.Clear(); Selected.Add(a.Id); ActiveId = a.Id; SelectionChanged();
        SetStatus($"Boolean {op}: {cutters.Count} cutter(s) used and hidden. Ctrl+Z to undo.");
    }

    /// <summary>World position of the vertex under the mouse (objects not being moved), for vertex snapping.</summary>
    bool VertexUnderMouse(Viewport vp, out Vector3 pos)
    {
        pos = default;
        var ray = RayAt(vp, Input.Mouse);
        var local = vp.Local(Input.Mouse);
        float best = 18f; bool found = false;
        var cur = Current;
        foreach (var o in Scene.Objects)
        {
            if (o.Hidden || o.Mesh.Verts.Count == 0) continue;
            bool editing = Level != SubObject.Object && o == cur;
            if (!editing && xfObjects.ContainsKey(o.Id)) continue;
            // cheap reject: the ray must pass near the object
            var (mn, mx) = o.WorldBounds();
            if (!Picking.RayBox(ray, mn - new Vector3(0.5f), mx + new Vector3(0.5f), out _)) continue;
            var w = o.World;
            for (int i = 0; i < o.Mesh.Verts.Count; i++)
            {
                if (editing && xfVerts.ContainsKey(i)) continue;
                var wp = Vector3.Transform(o.Mesh.Verts[i], w);
                if (!vp.Camera.Project(wp, vp.W, vp.H, out var sp, out _)) continue;
                float d = Vector2.Distance(sp, local);
                if (d < best) { best = d; pos = wp; found = true; }
            }
        }
        return found;
    }
}
