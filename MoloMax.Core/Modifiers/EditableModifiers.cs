using System.Numerics;
using System.Text.Json.Serialization;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Modifiers;

/// <summary>
/// A modifier you edit by hand (Edit Poly, Unwrap UVW): the editor swaps the object's mesh for the modifier's working mesh,
/// all the normal edit tools work on it, and <see cref="Store"/> keeps the result when the edit ends.
/// </summary>
public interface IEditableModifier
{
    /// <summary>The mesh to edit: the modifier's current output for this input.</summary>
    PolyMesh BeginEdit(PolyMesh input);
    /// <summary>Keeps the edited mesh, recorded against <paramref name="input"/> (the mesh coming into the modifier).</summary>
    void Store(PolyMesh edited, PolyMesh input);
    /// <summary>Set when the input no longer matches what was edited (shown in the panel).</summary>
    string Warning { get; }
}

/// <summary>A mesh in plain arrays, saved inside a modifier.</summary>
public sealed class MeshData
{
    public float[] V;
    public int[] FV;      // corner vertex indices, all faces one after another
    public int[] FN;      // corners per face
    public float[] UV;    // 2 per corner
    public float[] C;     // 4 per corner
    public int[] M;       // material per face
    public uint[] S;      // smoothing per face

    public static MeshData From(PolyMesh m)
    {
        int corners = m.Faces.Sum(f => f.V.Length);
        var d = new MeshData
        {
            V = new float[m.Verts.Count * 3], FV = new int[corners], FN = new int[m.Faces.Count], UV = new float[corners * 2], C = new float[corners * 4],
            M = new int[m.Faces.Count], S = new uint[m.Faces.Count],
        };
        for (int i = 0; i < m.Verts.Count; i++) { d.V[i * 3] = m.Verts[i].X; d.V[i * 3 + 1] = m.Verts[i].Y; d.V[i * 3 + 2] = m.Verts[i].Z; }
        int k = 0;
        for (int fi = 0; fi < m.Faces.Count; fi++)
        {
            var f = m.Faces[fi]; d.FN[fi] = f.V.Length; d.M[fi] = f.MatId; d.S[fi] = f.Smooth;
            for (int i = 0; i < f.V.Length; i++, k++)
            {
                d.FV[k] = f.V[i];
                d.UV[k * 2] = f.UV[i].X; d.UV[k * 2 + 1] = f.UV[i].Y;
                d.C[k * 4] = f.Col[i].X; d.C[k * 4 + 1] = f.Col[i].Y; d.C[k * 4 + 2] = f.Col[i].Z; d.C[k * 4 + 3] = f.Col[i].W;
            }
        }
        return d;
    }

    public PolyMesh ToMesh()
    {
        var m = new PolyMesh();
        for (int i = 0; i + 2 < V.Length; i += 3) m.Verts.Add(new Vector3(V[i], V[i + 1], V[i + 2]));
        int k = 0;
        for (int fi = 0; fi < FN.Length; fi++)
        {
            int n = FN[fi];
            var vs = new int[n];
            Array.Copy(FV, k, vs, 0, n);
            var f = new Face(vs, M[fi], S[fi]);
            for (int i = 0; i < n; i++, k++)
            {
                f.UV[i] = new Vector2(UV[k * 2], UV[k * 2 + 1]);
                f.Col[i] = new Vector4(C[k * 4], C[k * 4 + 1], C[k * 4 + 2], C[k * 4 + 3]);
            }
            m.Faces.Add(f);
        }
        return m;
    }
}

public static class Topology
{
    /// <summary>Fingerprint of the polygon structure (not the positions): same value = same faces on the same vertex indices.</summary>
    public static long Hash(PolyMesh m)
    {
        unchecked
        {
            long h = 1469598103934665603L ^ m.Verts.Count;
            foreach (var f in m.Faces)
            {
                h = (h ^ f.V.Length) * 1099511628211L;
                foreach (var v in f.V) h = (h ^ v) * 1099511628211L;
            }
            return h;
        }
    }
}

/// <summary>
/// Edit Poly (classic modeller): hand modelling as a step of the stack, anywhere in it. It records the edited mesh against the
/// mesh it received; when the modifiers below only move vertices, the edit follows them (each kept vertex gets the same move).
/// If they change the topology, the last edit is shown and the panel warns.
/// </summary>
public sealed class EditPolyModifier : Modifier, IEditableModifier
{
    public MeshData Result;
    public float[] BasePos;
    public long BaseTopo;
    /// <summary>For each vertex of the result: the input vertex it sits on when the edit was stored (-1 = made by the edit).</summary>
    public int[] Origin;
    [JsonIgnore] public string Warning { get; private set; }
    [JsonIgnore] PolyMesh cache;

    public EditPolyModifier() { Name = "Edit Poly"; }
    public override string TypeName => "Edit Poly";
    public override ModifierCategory Category => ModifierCategory.Edit;

    public bool HasEdit => Result != null;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        if (Result == null) { Warning = null; return m; }
        cache ??= Result.ToMesh();
        var r = cache.Clone();
        if (Topology.Hash(m) == BaseTopo && BasePos != null && BasePos.Length == m.Verts.Count * 3)
        {
            Warning = null;
            // vertices that came from the input follow its moves (the modifiers below were changed)
            if (Origin != null)
                for (int i = 0; i < r.Verts.Count && i < Origin.Length; i++)
                {
                    int o = Origin[i]; if (o < 0) continue;
                    var d = m.Verts[o] - new Vector3(BasePos[o * 3], BasePos[o * 3 + 1], BasePos[o * 3 + 2]);
                    if (d != Vector3.Zero) r.Verts[i] += d;
                }
        }
        else Warning = "The mesh below changed its topology: this Edit Poly shows its last result. Collapse or redo the edit.";
        return r;
    }

    public PolyMesh BeginEdit(PolyMesh input) => Apply(input.Clone(), false);

    public void Store(PolyMesh edited, PolyMesh input)
    {
        Result = MeshData.From(edited); cache = null;
        BaseTopo = Topology.Hash(input);
        BasePos = new float[input.Verts.Count * 3];
        var at = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < input.Verts.Count; i++)
        {
            var p = input.Verts[i]; BasePos[i * 3] = p.X; BasePos[i * 3 + 1] = p.Y; BasePos[i * 3 + 2] = p.Z;
            at.TryAdd(Q(p), i);
        }
        Origin = new int[edited.Verts.Count];
        for (int i = 0; i < edited.Verts.Count; i++) Origin[i] = at.TryGetValue(Q(edited.Verts[i]), out var o) ? o : -1;
        Warning = null;
    }
    static (int, int, int) Q(Vector3 p) => ((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f));

}

/// <summary>
/// Unwrap UVW (classic modeller): UVs as a step of the stack. Keeps one UV per face corner against the input topology;
/// seams are kept too so the islands can be unfolded again (Peel) after a change.
/// </summary>
public sealed class UnwrapUvwModifier : Modifier, IEditableModifier
{
    public float[] UV;
    public long BaseTopo;
    public int BaseFaces;
    /// <summary>Seam edges (vertex index pairs, flattened).</summary>
    public int[] Seams;
    /// <summary>Texel density target (px / m) and texture size used by the density tools.</summary>
    public float Density = 512f;
    public int TextureSize = 1024;
    public float AngleLimit = 66f;
    [JsonIgnore] public string Warning { get; private set; }

    public UnwrapUvwModifier() { Name = "Unwrap UVW"; }
    public override string TypeName => "Unwrap UVW";
    public override ModifierCategory Category => ModifierCategory.Edit;

    public bool HasUv => UV != null;

    public override PolyMesh Apply(PolyMesh m, bool render)
    {
        if (UV == null) { Warning = null; return m; }
        if (Topology.Hash(m) != BaseTopo || m.Faces.Count != BaseFaces)
        {
            Warning = "The mesh below changed its topology: the stored UVs no longer fit. Use Auto Unwrap or Peel again.";
            return m;
        }
        Warning = null;
        int k = 0;
        foreach (var f in m.Faces)
            for (int i = 0; i < f.UV.Length; i++, k++)
                if (k * 2 + 1 < UV.Length) f.UV[i] = new Vector2(UV[k * 2], UV[k * 2 + 1]);
        return m;
    }

    public PolyMesh BeginEdit(PolyMesh input) => Apply(input.Clone(), false);

    public void Store(PolyMesh edited, PolyMesh input)
    {
        // only the UVs are kept: if the edit changed the geometry, that part is dropped (this is not an Edit Poly)
        var src = edited.Faces.Count == input.Faces.Count && Topology.Hash(edited) == Topology.Hash(input) ? edited : null;
        if (src == null) { Warning = "Unwrap UVW keeps only UVs: geometry changes were dropped."; return; }
        StoreUv(src);
    }

    public void StoreUv(PolyMesh m)
    {
        int corners = m.Faces.Sum(f => f.UV.Length);
        UV = new float[corners * 2];
        int k = 0;
        foreach (var f in m.Faces) foreach (var uv in f.UV) { UV[k * 2] = uv.X; UV[k * 2 + 1] = uv.Y; k++; }
        BaseTopo = Topology.Hash(m); BaseFaces = m.Faces.Count;
        Warning = null;
    }

    public HashSet<EdgeKey> SeamSet()
    {
        var s = new HashSet<EdgeKey>();
        if (Seams != null) for (int i = 0; i + 1 < Seams.Length; i += 2) s.Add(EdgeKey.Make(Seams[i], Seams[i + 1]));
        return s;
    }
    public void SetSeams(IEnumerable<EdgeKey> seams) => Seams = seams.SelectMany(e => new[] { e.A, e.B }).ToArray();
}
