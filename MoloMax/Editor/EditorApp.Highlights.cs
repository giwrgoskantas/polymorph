using System.Numerics;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.Rendering;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace MoloMax.Editor;

/// <summary>
/// Selected faces / edges drawn from buffers kept on the graphics card, rebuilt only when the mesh or the selection
/// changes (50k selected faces used to be triangulated and re-sent every frame). Faces turned away get the dark tint
/// through a second pass with the other side culled.
/// </summary>
public sealed partial class EditorApp
{
    sealed class HiCache : IDisposable
    {
        public int ObjId = -1, Version = -1; public long Sig; public SubObject Level;
        public Buffer Tris, Lines; public int TriCount, LineCount;
        public void Dispose() { Tris?.Dispose(); Lines?.Dispose(); Tris = Lines = null; TriCount = LineCount = 0; }
    }
    readonly HiCache hiCache = new();

    /// <summary>Cheap fingerprint of a set (count + mixed sum): changes when the selection changes.</summary>
    static long SetSig(HashSet<int> s)
    {
        long h = s.Count * unchecked((long)0x9E3779B97F4A7C15UL); ulong acc = 0;
        foreach (var x in s) acc += (ulong)x * 0x9E3779B1UL ^ (ulong)x * 0x85EBCA77UL << 17;
        return h ^ (long)acc;
    }
    static long SetSig(HashSet<EdgeKey> s)
    {
        long h = s.Count * unchecked((long)0x9E3779B97F4A7C15UL); ulong acc = 0;
        foreach (var e in s) acc += (ulong)e.A * 0x9E3779B1UL ^ (ulong)e.B * 0x85EBCA77UL << 13;
        return h ^ (long)acc;
    }

    void SubObjectHighlightsGpu(SceneObject o, Matrix4x4 viewProj)
    {
        var s = SubSel(o); var m = o.Mesh;
        bool faces = Level is SubObject.Polygon or SubObject.Element;
        long sig = faces ? SetSig(s.Faces) : SetSig(s.Edges);
        var c = hiCache;
        if (c.ObjId != o.Id || c.Version != o.Version || c.Sig != sig || c.Level != Level)
        {
            c.Dispose();
            c.ObjId = o.Id; c.Version = o.Version; c.Sig = sig; c.Level = Level;
            var white = Vector4.One;
            if (faces)
            {
                var sel = s.Faces.Where(fi => fi < m.Faces.Count && m.Faces[fi].V.Length >= 3).ToArray();
                var tOff = new int[sel.Length + 1]; var eOff = new int[sel.Length + 1];
                for (int i = 0; i < sel.Length; i++) { int n = m.Faces[sel[i]].V.Length; tOff[i + 1] = tOff[i] + (n - 2) * 3; eOff[i + 1] = eOff[i] + n * 2; }
                var tv = new LineVertex[tOff[^1]]; var ev = new LineVertex[eOff[^1]];
                Parallel.For(0, (sel.Length + 2047) / 2048, b =>
                {
                    for (int i = b * 2048; i < Math.Min(sel.Length, (b + 1) * 2048); i++)
                    {
                        var f = m.Faces[sel[i]]; int n = f.V.Length; int t = tOff[i], e = eOff[i];
                        if (n <= 4)
                            for (int k = 1; k + 1 < n; k++) { tv[t++] = new(m.Verts[f.V[0]], white); tv[t++] = new(m.Verts[f.V[k]], white); tv[t++] = new(m.Verts[f.V[k + 1]], white); }
                        else
                        {
                            var tr = Triangulator.Triangulate(m, f);
                            for (int k = 0; k < n - 2; k++)
                            {
                                var (a, bb, cc) = k < tr.Count ? tr[k] : (0, 0, 0);
                                tv[t++] = new(m.Verts[f.V[a]], white); tv[t++] = new(m.Verts[f.V[bb]], white); tv[t++] = new(m.Verts[f.V[cc]], white);
                            }
                        }
                        for (int k = 0; k < n; k++) { ev[e++] = new(m.Verts[f.V[k]], white); ev[e++] = new(m.Verts[f.V[(k + 1) % n]], white); }
                    }
                });
                if (tv.Length > 0) { c.Tris = Buffer.Create(lines.Device, SharpDX.Direct3D11.BindFlags.VertexBuffer, tv); c.TriCount = tv.Length; }
                if (ev.Length > 0) { c.Lines = Buffer.Create(lines.Device, SharpDX.Direct3D11.BindFlags.VertexBuffer, ev); c.LineCount = ev.Length; }
            }
            else
            {
                var ev = new List<LineVertex>(s.Edges.Count * 2);
                foreach (var e in s.Edges) if (e.A < m.Verts.Count && e.B < m.Verts.Count) { ev.Add(new(m.Verts[e.A], white)); ev.Add(new(m.Verts[e.B], white)); }
                if (ev.Count > 0) { c.Lines = Buffer.Create(lines.Device, SharpDX.Direct3D11.BindFlags.VertexBuffer, ev.ToArray()); c.LineCount = ev.Count; }
            }
        }
        var wvp = o.World * viewProj;
        var red = new Vector4(1f, 0.1f, 0.1f, 1);
        if (c.TriCount > 0)
        {
            var front = new Vector4(SettingColour(Settings.SelFrontColour, new(1f, 0.05f, 0.05f)), 0.45f);
            var back = new Vector4(SettingColour(Settings.SelBackColour, new(0.32f, 0.02f, 0.02f)), 0.6f);
            if (o.World.GetDeterminant() < 0) (front, back) = (back, front); // mirrored object: its triangles turn the other way on screen
            lines.Flush(viewProj, 0.0004f); // whatever was queued before, in order
            gpu.Context.Rasterizer.State = renderer.CullBack;
            lines.DrawStaticTris(c.Tris, c.TriCount, wvp, front, 0.0004f);
            gpu.Context.Rasterizer.State = renderer.CullFront;
            lines.DrawStaticTris(c.Tris, c.TriCount, wvp, back, 0.0004f);
            gpu.Context.Rasterizer.State = renderer.LinesState;
        }
        if (c.LineCount > 0) lines.DrawStatic(c.Lines, c.LineCount, wvp, red, 0.0004f);
    }
}
