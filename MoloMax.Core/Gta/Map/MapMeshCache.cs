using System.Numerics;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Gta.Map;

/// <summary>
/// Map models already read once, kept as a small binary file (mesh + materials): the next time the map opens they load
/// in a few milliseconds instead of being extracted and converted again.
/// </summary>
public static class MapMeshCache
{
    const int Magic = 0x434D4D50; // "PMMC"
    const int Version = 2; // 2: terrain layers + vertex colour 1

    public static void Save(string path, PolyMesh m, IReadOnlyList<GtaMaterial> mats)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic); w.Write(Version);
            w.Write(mats.Count);
            foreach (var x in mats)
            {
                Str(w, x.Name); Str(w, x.Shader); Str(w, x.DiffuseTexture); Str(w, x.BumpTexture); Str(w, x.SpecTexture); Str(w, x.DetailTexture);
                w.Write(x.Bumpiness); w.Write(x.SpecularIntensity); w.Write(x.SpecularFalloff); w.Write(x.SpecularFresnel);
                V3(w, x.SpecMapIntMask); w.Write(x.EmissiveMultiplier); V4(w, x.DetailSettings); V3(w, x.ViewportColour);
                for (int k = 0; k < 4; k++) { Str(w, x.Layer(k)); Str(w, x.LayerBump(k)); }
            }
            w.Write(m.Verts.Count);
            foreach (var v in m.Verts) V3(w, v);
            w.Write(m.Faces.Count);
            foreach (var f in m.Faces)
            {
                w.Write(f.V.Length); w.Write(f.MatId); w.Write(f.Smooth); w.Write(f.Col1 != null);
                for (int i = 0; i < f.V.Length; i++)
                {
                    w.Write(f.V[i]);
                    var uv = f.UV[i]; w.Write(uv.X); w.Write(uv.Y);
                    V4(w, f.Col[i]);
                    if (f.Col1 != null) V4(w, f.Col1[i]);
                }
            }
        }
        File.Move(tmp, path, true);
    }

    public static (PolyMesh Mesh, List<GtaMaterial> Materials)? Load(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version) return null;
            int nm = r.ReadInt32();
            var mats = new List<GtaMaterial>(nm);
            for (int i = 0; i < nm; i++)
            {
                var x = new GtaMaterial
                {
                    Name = Str(r) ?? "Material", Shader = Str(r) ?? "normal_spec", DiffuseTexture = Str(r), BumpTexture = Str(r), SpecTexture = Str(r), DetailTexture = Str(r),
                    Bumpiness = r.ReadSingle(), SpecularIntensity = r.ReadSingle(), SpecularFalloff = r.ReadSingle(), SpecularFresnel = r.ReadSingle(),
                };
                x.SpecMapIntMask = V3(r); x.EmissiveMultiplier = r.ReadSingle(); x.DetailSettings = V4(r); x.ViewportColour = V3(r);
                for (int k = 0; k < 4; k++)
                {
                    var l = Str(r); var lb = Str(r);
                    if (l != null || lb != null) { x.EnsureLayers(); x.Layers[k] = l; x.LayerBumps[k] = lb; }
                }
                mats.Add(x);
            }
            var m = new PolyMesh();
            int nv = r.ReadInt32();
            m.Verts = new List<Vector3>(nv);
            for (int i = 0; i < nv; i++) m.Verts.Add(V3(r));
            int nf = r.ReadInt32();
            m.Faces = new List<Face>(nf);
            for (int i = 0; i < nf; i++)
            {
                int n = r.ReadInt32(); int mat = r.ReadInt32(); uint sm = r.ReadUInt32(); bool c1 = r.ReadBoolean();
                var vi = new int[n];
                var f = new Face(vi, mat, sm);
                if (c1) f.Col1 = new Vector4[n];
                for (int c = 0; c < n; c++)
                {
                    vi[c] = r.ReadInt32();
                    f.UV[c] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    f.Col[c] = V4(r);
                    if (c1) f.Col1[c] = V4(r);
                }
                m.Faces.Add(f);
            }
            return (m, mats);
        }
        catch { return null; }
    }

    static void Str(BinaryWriter w, string s) { w.Write(s != null); if (s != null) w.Write(s); }
    static string Str(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
    static void V3(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static void V4(BinaryWriter w, Vector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
    static Vector3 V3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    static Vector4 V4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
