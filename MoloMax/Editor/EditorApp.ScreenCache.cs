using System.Numerics;
using MoloMax.Core.Scenes;

namespace MoloMax.Editor;

/// <summary>
/// Where every vertex of the edited mesh lands on screen, per viewport, computed once per camera / object change
/// (in parallel) and shared by the vertex dots, the hover square, vertex and edge picking. A 500k-vertex room used
/// to be projected point by point several times per frame.
/// </summary>
public sealed partial class EditorApp
{
    sealed class ScreenPts
    {
        public int ObjId = -1; public int Version = -1; public Matrix4x4 Wvp; public float W, H; public PolyMeshRef Mesh;
        public Vector2[] P = Array.Empty<Vector2>(); public bool[] Ok = Array.Empty<bool>();
    }
    /// <summary>Identity of the mesh object (a new mesh after undo with the same version must not reuse the old points).</summary>
    readonly record struct PolyMeshRef(object M);

    readonly Dictionary<Viewport, ScreenPts> screenPts = new();

    /// <summary>Viewport-local screen positions of the mesh vertices of <paramref name="o"/> (Ok[i] false: behind the camera).</summary>
    ScreenPts ScreenVerts(Viewport vp, SceneObject o)
    {
        if (!screenPts.TryGetValue(vp, out var c)) screenPts[vp] = c = new ScreenPts();
        var m = o.Mesh;
        var wvp = o.World * vp.Camera.ViewProj(vp.W / MathF.Max(vp.H, 1));
        if (c.ObjId == o.Id && c.Version == o.Version && c.W == vp.W && c.H == vp.H && c.Wvp == wvp && ReferenceEquals(c.Mesh.M, m) && c.P.Length == m.Verts.Count) return c;
        int n = m.Verts.Count;
        if (c.P.Length != n) { c.P = new Vector2[n]; c.Ok = new bool[n]; }
        var P = c.P; var Ok = c.Ok; float w = vp.W, h = vp.H; var verts = m.Verts;
        if (n < 20000)
            for (int i = 0; i < n; i++) Ok[i] = Camera.Project(wvp, verts[i], w, h, out P[i]);
        else
            Parallel.For(0, (n + 8191) / 8192, b =>
            {
                int e = Math.Min(n, (b + 1) * 8192);
                for (int i = b * 8192; i < e; i++) Ok[i] = Camera.Project(wvp, verts[i], w, h, out P[i]);
            });
        c.ObjId = o.Id; c.Version = o.Version; c.W = w; c.H = h; c.Wvp = wvp; c.Mesh = new PolyMeshRef(m);
        return c;
    }
}
