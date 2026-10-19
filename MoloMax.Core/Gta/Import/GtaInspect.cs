using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Import;

/// <summary>Raw look at a model file (diagnostics): shader, vertex and index counts of every geometry.</summary>
public static class GtaInspect
{
    public static List<string> Geometries(string file, uint hash = 0)
    {
        var res = new List<string>();
        var bytes = File.ReadAllBytes(file);
        IEnumerable<Cw.DrawableBase> drawables;
        if (file.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase))
        {
            var y = new Cw.YddFile(); Cw.RpfFile.LoadResourceFile(y, bytes, 165);
            drawables = (y.Drawables ?? Array.Empty<Cw.Drawable>()).Where(d => hash == 0 || y.Dict != null && y.Dict.TryGetValue(hash, out var dd) && dd == d);
        }
        else if (file.EndsWith(".yft", StringComparison.OrdinalIgnoreCase))
        {
            var y = new Cw.YftFile(); Cw.RpfFile.LoadResourceFile(y, bytes, 162);
            drawables = new Cw.DrawableBase[] { y.Fragment?.Drawable };
        }
        else
        {
            var y = new Cw.YdrFile(); Cw.RpfFile.LoadResourceFile(y, bytes, 165);
            drawables = new Cw.DrawableBase[] { y.Drawable };
        }
        foreach (var d in drawables)
        {
            if (d == null) continue;
            foreach (var m in d.AllModels ?? Array.Empty<Cw.DrawableModel>())
                foreach (var g in m.Geometries ?? Array.Empty<Cw.DrawableGeometry>())
                {
                    res.Add($"shader {g.Shader?.Name} verts {g.VerticesCount} indices {g.IndicesCount} vtype {g.VertexData?.VertexType} flags {g.VertexData?.Info?.Flags:X}");
                    var vd = g.VertexData; if (vd == null) continue;
                    uint fl = vd.Info?.Flags ?? 0;
                    for (int i = 0; i < Math.Min(8, vd.VertexCount); i++)
                    {
                        var sb = new System.Text.StringBuilder($"  v{i}: p {vd.GetVector3(i, 0)}");
                        if ((fl & (1u << 3)) != 0) sb.Append($" n {vd.GetVector3(i, 3)}");
                        if ((fl & (1u << 4)) != 0) { var c0 = vd.GetColour(i, 4); sb.Append($" c0 {c0.R},{c0.G},{c0.B},{c0.A}"); }
                        if ((fl & (1u << 6)) != 0) sb.Append($" t0 {vd.GetVector2(i, 6)}");
                        res.Add(sb.ToString());
                    }
                    var idx = g.IndexBuffer?.Indices;
                    if (idx != null) res.Add("  idx " + string.Join(",", idx.Take(18)));
                }
        }
        return res;
    }
}
