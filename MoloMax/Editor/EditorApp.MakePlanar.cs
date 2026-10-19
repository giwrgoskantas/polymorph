using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

public sealed partial class EditorApp
{
    /// <summary>0 = average, 1 = lowest, 2 = highest.</summary>
    int planarTo;
    bool planarLocal;

    /// <summary>
    /// Puts the selected vertices (or the vertices of the selected edges / polygons) at one level along an axis
    /// (axis 0/1/2 = X/Y/Z, in world space unless planarLocal), or onto their best-fit plane (axis -1).
    /// </summary>
    public void MakePlanar(int axis) => DoMesh(axis < 0 ? "Make Planar" : "Make Planar " + "XYZ"[axis], (o, m, s) =>
    {
        var vs = s.VerticesOf(m, Level);
        if (vs.Count < 2) { SetStatus("Select at least 2 vertices (or edges / polygons) first."); return; }
        var w = o.World;
        bool local = planarLocal || axis < 0;
        Matrix4x4.Invert(w, out var inv);
        var pts = vs.Where(i => i < m.Verts.Count).ToDictionary(i => i, i => local ? m.Verts[i] : Vector3.Transform(m.Verts[i], w));
        if (axis >= 0)
        {
            var vals = pts.Values.Select(p => p[axis]).ToList();
            float level = planarTo switch { 1 => vals.Min(), 2 => vals.Max(), _ => vals.Average() };
            foreach (var (i, p) in pts)
            {
                var q = p; q[axis] = level;
                m.Verts[i] = local ? q : Vector3.Transform(q, inv);
            }
            SetStatus($"{pts.Count} vertices put at {"XYZ"[axis]} = {level:0.###} m");
        }
        else
        {
            // best-fit plane: centroid + normal from the covariance (smallest eigenvector, by power iteration on the inverse spread)
            var c = Vector3.Zero; foreach (var p in pts.Values) c += p; c /= pts.Count;
            var n = BestFitNormal(pts.Values.Select(p => p - c).ToList(), s, m);
            foreach (var (i, p) in pts) m.Verts[i] = p - Vector3.Dot(p - c, n) * n;
            SetStatus($"{pts.Count} vertices flattened onto their plane");
        }
    });

    static Vector3 BestFitNormal(List<Vector3> d, PolySelection s, PolyMesh m)
    {
        // covariance
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in d) { xx += p.X * p.X; xy += p.X * p.Y; xz += p.X * p.Z; yy += p.Y * p.Y; yz += p.Y * p.Z; zz += p.Z * p.Z; }
        // the normal is the axis of least spread: pick the best of the three cross products (robust, no eigen solver)
        float detX = yy * zz - yz * yz, detY = xx * zz - xz * xz, detZ = xx * yy - xy * xy;
        float dmax = MathF.Max(detX, MathF.Max(detY, detZ));
        Vector3 n;
        if (dmax <= 1e-12f) n = Vector3.UnitZ;
        else if (dmax == detX) n = new Vector3(detX, xz * yz - xy * zz, xy * yz - xz * yy);
        else if (dmax == detY) n = new Vector3(xz * yz - xy * zz, detY, xy * xz - yz * xx);
        else n = new Vector3(xy * yz - xz * yy, xy * xz - yz * xx, detZ);
        if (n.LengthSquared() < 1e-20f) return Vector3.UnitZ;
        n = Vector3.Normalize(n);
        // keep the polygons facing the same way (sign does not change the result, only consistency)
        return n.Z < 0 ? -n : n;
    }

    void MakePlanarUi()
    {
        bool pu = Settings.PreserveUVs;
        if (ImGui.Checkbox("Preserve UVs", ref pu)) { Settings.PreserveUVs = pu; Settings.Save(); }
        Theme.Tip("Moving vertices, edges or polygons keeps the texture in place on the surface (the UVs follow).");
        float avail = ImGui.GetContentRegionAvail().X;
        float bw = (avail - 12) / 4;
        if (ImGui.Button("Make Planar", new Vector2(bw, 0))) MakePlanar(-1);
        Theme.Tip("Flatten the selection onto its best-fit plane.");
        ImGui.SameLine(0, 4); if (ImGui.Button("X##mp", new Vector2(bw, 0))) MakePlanar(0);
        Theme.Tip("Put every selected vertex at the same X.");
        ImGui.SameLine(0, 4); if (ImGui.Button("Y##mp", new Vector2(bw, 0))) MakePlanar(1);
        Theme.Tip("Put every selected vertex at the same Y.");
        ImGui.SameLine(0, 4); if (ImGui.Button("Z##mp", new Vector2(bw, 0))) MakePlanar(2);
        Theme.Tip("Put every selected vertex at the same height (Z): level them.");
        ImGui.SetNextItemWidth((avail - 4) / 2);
        ImGui.Combo("##mpto", ref planarTo, "to the average\0to the lowest\0to the highest\0");
        ImGui.SameLine(0, 4); ImGui.Checkbox("Local axes##mp", ref planarLocal);
        Theme.Tip("X / Y / Z use the world axes, or the object's own axes when ticked.");
    }
}
