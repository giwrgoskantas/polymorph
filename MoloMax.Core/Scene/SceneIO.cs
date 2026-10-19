using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoloMax.Core.Gta;
using MoloMax.Core.Mesh;

namespace MoloMax.Core.Scenes;

/// <summary>Native scene format (.mmx, JSON) and OBJ export for Blender / Sollumz.</summary>
public static class SceneIO
{
    // ---- DTOs keep the file format independent of the runtime classes
    sealed class FaceDto { public int[] V { get; set; } public float[] UV { get; set; } public float[] C { get; set; } public float[] C1 { get; set; } public int M { get; set; } public uint S { get; set; } }
    sealed class MeshDto { public float[] V { get; set; } public List<FaceDto> F { get; set; } }
    sealed class ObjDto
    {
        public int Id { get; set; } public string Name { get; set; } public float[] Pos { get; set; } public float[] Rot { get; set; } public float[] Scale { get; set; }
        public float[] Wire { get; set; } public bool Hidden { get; set; } public bool Frozen { get; set; } public string Type { get; set; } public MeshDto Mesh { get; set; }
        public GtaInfo Gta { get; set; }
        public string Tripo { get; set; }
        public string PaintFile { get; set; }
        public string Folder { get; set; }
        public int? Parent { get; set; }
        public List<MoloMax.Core.Modifiers.Modifier> Modifiers { get; set; }
        public string Inst { get; set; }
        public int? MeshOf { get; set; }
    }
    sealed class MatDto
    {
        public string[] Layers { get; set; } public string[] LayerBumps { get; set; }
        public int Id { get; set; } public string Name { get; set; } public string Shader { get; set; }
        public string Diffuse { get; set; } public string Bump { get; set; } public string Spec { get; set; } public string Detail { get; set; }
        public float Bumpiness { get; set; } public float SpecIntensity { get; set; } public float SpecFalloff { get; set; } public float SpecFresnel { get; set; }
        public float[] SpecMask { get; set; } public float Emissive { get; set; } public float[] DetailSettings { get; set; } public float[] Colour { get; set; }
    }
    sealed class SceneDto
    {
        public string Format { get; set; } = "MoloMax"; public int Version { get; set; } = 1;
        public int NextObjectId { get; set; } public int NextMaterialId { get; set; } public float[] GtaOrigin { get; set; } public List<string> Folders { get; set; } public MoloMax.Core.Tools.RoomPlanData Plan { get; set; }
        public List<MatDto> Materials { get; set; } public List<ObjDto> Objects { get; set; }
        public GtaProjectSettings Gta { get; set; }
        public MoloMax.Core.Anim.SceneAnim Anim { get; set; }
    }

    // GtaInfo / GtaProjectSettings are plain field classes
    static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = false, IncludeFields = true };

    static float[] A(Vector3 v) => new[] { v.X, v.Y, v.Z };
    static float[] A(Vector4 v) => new[] { v.X, v.Y, v.Z, v.W };
    static Vector3 V3(float[] a) => a == null ? Vector3.Zero : new Vector3(a[0], a[1], a[2]);
    static Vector4 V4(float[] a) => a == null ? Vector4.Zero : new Vector4(a[0], a[1], a[2], a[3]);

    public static string ToJson(Scene s)
    {
        var firstOfInst = new Dictionary<string, SceneObject>();
        foreach (var o in s.Objects) if (o.InstanceKey != null) firstOfInst.TryAdd(o.InstanceKey, o);
        var dto = new SceneDto
        {
            NextObjectId = s.NextObjectId, NextMaterialId = s.NextMaterialId, GtaOrigin = A(s.GtaOrigin), Gta = s.Gta, Folders = s.Folders.Count > 0 ? s.Folders : null, Plan = s.Plan?.Rooms.Count > 0 ? s.Plan : null,
            Anim = s.Anim == null || s.Anim.IsEmpty ? null : s.Anim,
            Materials = s.Materials.Select(m => new MatDto
            {
                Id = m.Id, Name = m.Name, Shader = m.Shader, Diffuse = m.DiffuseTexture, Bump = m.BumpTexture, Spec = m.SpecTexture, Detail = m.DetailTexture,
                Bumpiness = m.Bumpiness, SpecIntensity = m.SpecularIntensity, SpecFalloff = m.SpecularFalloff, SpecFresnel = m.SpecularFresnel,
                SpecMask = A(m.SpecMapIntMask), Emissive = m.EmissiveMultiplier, DetailSettings = A(m.DetailSettings), Colour = A(m.ViewportColour),
                Layers = m.Layers?.Any(x => x != null) == true ? m.Layers : null, LayerBumps = m.LayerBumps?.Any(x => x != null) == true ? m.LayerBumps : null,
            }).ToList(),
            Objects = s.Objects.Select(o => ObjOut(o, firstOfInst)).ToList(),
        };
        return JsonSerializer.Serialize(dto, jsonOptions);
    }

    static ObjDto ObjOut(SceneObject o, Dictionary<string, SceneObject> firstOfInst)
    {
        // an object in the middle of an Edit Poly / Unwrap edit is written as if the edit had ended
        if (o.ModEdit != null) { var c = o.Clone(); c.CommitModifierEdit(); o = c; firstOfInst = new(); }
        // an instance stores its mesh once: the other members point to the first one
        if (o.InstanceKey != null && firstOfInst.TryGetValue(o.InstanceKey, out var first) && first != o && ReferenceEquals(first.Mesh, o.Mesh))
            return new ObjDto
            {
                Id = o.Id, Name = o.Name, Pos = A(o.Position), Rot = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W }, Scale = A(o.Scale),
                Wire = A(o.WireColour), Hidden = o.Hidden, Frozen = o.Frozen, Folder = string.IsNullOrEmpty(o.Folder) ? null : o.Folder, Parent = o.ParentId >= 0 ? o.ParentId : null, Type = o.GtaType.ToString(), Gta = o.Gta, Tripo = o.TripoTaskId, PaintFile = o.PaintFile,
                Inst = o.InstanceKey, MeshOf = first.Id,
            };
        return new ObjDto
            {
                Id = o.Id, Name = o.Name, Pos = A(o.Position), Rot = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W }, Scale = A(o.Scale),
                Wire = A(o.WireColour), Hidden = o.Hidden, Frozen = o.Frozen, Folder = string.IsNullOrEmpty(o.Folder) ? null : o.Folder, Parent = o.ParentId >= 0 ? o.ParentId : null, Type = o.GtaType.ToString(), Gta = o.Gta, Tripo = o.TripoTaskId, PaintFile = o.PaintFile, Modifiers = o.Modifiers?.Count > 0 ? o.Modifiers : null,
                Inst = o.InstanceKey,
                Mesh = new MeshDto
                {
                    V = o.Mesh.Verts.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(),
                    F = o.Mesh.Faces.Select(f => new FaceDto
                    {
                        V = f.V, M = f.MatId, S = f.Smooth,
                        UV = f.UV.SelectMany(u => new[] { u.X, u.Y }).ToArray(),
                        C = f.Col.SelectMany(c => new[] { c.X, c.Y, c.Z, c.W }).ToArray(),
                        C1 = f.Col1?.SelectMany(c => new[] { c.X, c.Y, c.Z, c.W }).ToArray(),
                    }).ToList(),
                },
            };
    }

    public static Scene FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<SceneDto>(json, jsonOptions) ?? throw new InvalidDataException("Empty scene file.");
        if (dto.Format != "MoloMax") throw new InvalidDataException("This is not a MoloMax scene.");
        var s = new Scene { NextObjectId = dto.NextObjectId, NextMaterialId = dto.NextMaterialId, GtaOrigin = V3(dto.GtaOrigin), Gta = dto.Gta ?? new GtaProjectSettings(), Folders = dto.Folders ?? new(), Plan = dto.Plan ?? new(), Anim = dto.Anim ?? new() };
        s.Materials.Clear();
        foreach (var m in dto.Materials ?? new())
            s.Materials.Add(new GtaMaterial
            {
                Id = m.Id, Name = m.Name, Shader = m.Shader, DiffuseTexture = m.Diffuse, BumpTexture = m.Bump, SpecTexture = m.Spec, DetailTexture = m.Detail,
                Bumpiness = m.Bumpiness, SpecularIntensity = m.SpecIntensity, SpecularFalloff = m.SpecFalloff, SpecularFresnel = m.SpecFresnel,
                SpecMapIntMask = V3(m.SpecMask), EmissiveMultiplier = m.Emissive, DetailSettings = V4(m.DetailSettings), ViewportColour = V3(m.Colour),
                Layers = m.Layers, LayerBumps = m.LayerBumps,
            });
        if (s.Materials.Count == 0) s.Materials.Add(GtaMaterial.CreateDefault(0, "default_grey", new Vector3(0.72f)));
        var meshOfId = new Dictionary<int, SceneObject>();
        var pending = new List<(SceneObject Obj, int Of)>();
        foreach (var o in dto.Objects ?? new())
        {
            if (o.Mesh == null)
            {
                // instance: takes its mesh from the object it points to (resolved below)
                var inst = new SceneObject
                {
                    Id = o.Id, Name = o.Name, Mesh = new PolyMesh(), Position = V3(o.Pos),
                    Rotation = o.Rot != null ? new Quaternion(o.Rot[0], o.Rot[1], o.Rot[2], o.Rot[3]) : Quaternion.Identity,
                    Scale = o.Scale != null ? V3(o.Scale) : Vector3.One, WireColour = V3(o.Wire), Hidden = o.Hidden, Frozen = o.Frozen, Folder = o.Folder ?? "", ParentId = o.Parent ?? -1,
                    GtaType = Enum.TryParse<GtaObjectType>(o.Type, out var it) ? it : GtaObjectType.Drawable,
                    Gta = o.Gta ?? new GtaInfo(), TripoTaskId = o.Tripo, PaintFile = o.PaintFile, Modifiers = o.Modifiers ?? new(), InstanceKey = o.Inst,
                };
                s.Objects.Add(inst); meshOfId[inst.Id] = inst;
                if (o.MeshOf is int of) pending.Add((inst, of));
                continue;
            }
            var mesh = new PolyMesh();
            for (int i = 0; i + 2 < o.Mesh.V.Length; i += 3) mesh.Verts.Add(new Vector3(o.Mesh.V[i], o.Mesh.V[i + 1], o.Mesh.V[i + 2]));
            foreach (var f in o.Mesh.F)
            {
                var face = new Face(f.V, f.M, f.S);
                for (int i = 0; i < f.V.Length; i++)
                {
                    if (f.UV != null && f.UV.Length >= 2 * f.V.Length) face.UV[i] = new Vector2(f.UV[2 * i], f.UV[2 * i + 1]);
                    if (f.C != null && f.C.Length >= 4 * f.V.Length) face.Col[i] = new Vector4(f.C[4 * i], f.C[4 * i + 1], f.C[4 * i + 2], f.C[4 * i + 3]);
                    if (f.C1 != null && f.C1.Length >= 4 * f.V.Length) { face.Col1 ??= new Vector4[f.V.Length]; face.Col1[i] = new Vector4(f.C1[4 * i], f.C1[4 * i + 1], f.C1[4 * i + 2], f.C1[4 * i + 3]); }
                }
                mesh.Faces.Add(face);
            }
            // repair what older versions could leave (a polygon pinched by a weld, a face with a missing vertex) instead of refusing the file
            mesh.Faces.RemoveAll(f => f.V.Length < 3 || f.V.Any(v => v < 0 || v >= mesh.Verts.Count));
            mesh.CleanFaces();
            mesh.Validate();
            s.Objects.Add(new SceneObject
            {
                Id = o.Id, Name = o.Name, Mesh = mesh, Position = V3(o.Pos),
                Rotation = o.Rot != null ? new Quaternion(o.Rot[0], o.Rot[1], o.Rot[2], o.Rot[3]) : Quaternion.Identity,
                Scale = o.Scale != null ? V3(o.Scale) : Vector3.One, WireColour = V3(o.Wire), Hidden = o.Hidden, Frozen = o.Frozen, Folder = o.Folder ?? "", ParentId = o.Parent ?? -1,
                GtaType = Enum.TryParse<GtaObjectType>(o.Type, out var t) ? t : GtaObjectType.Drawable,
                Gta = o.Gta ?? new GtaInfo(), TripoTaskId = o.Tripo, PaintFile = o.PaintFile, Modifiers = o.Modifiers ?? new(), InstanceKey = o.Inst,
            });
            meshOfId[o.Id] = s.Objects[^1];
        }
        foreach (var (inst, of) in pending)
            if (meshOfId.TryGetValue(of, out var src)) { inst.Mesh = src.Mesh; inst.Modifiers = src.Modifiers; inst.Version = src.Version; }
        // a lone member is a plain object
        foreach (var g in s.Objects.Where(o => o.InstanceKey != null).GroupBy(o => o.InstanceKey).Where(g => g.Count() == 1)) g.First().InstanceKey = null;
        s.SyncInstances();
        return s;
    }

    public static void Save(Scene s, string path) => File.WriteAllText(path, ToJson(s));
    public static Scene Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>
    /// Wavefront OBJ (+ MTL) in Blender's Y-up convention, with vertex colours ("v x y z r g b")
    /// so Sollumz keeps the GTA colour bake. Portals are skipped.
    /// </summary>
    public static void ExportObj(Scene s, string objPath, bool selectedOnly = false, ISet<int> selectedIds = null)
    {
        var ci = CultureInfo.InvariantCulture;
        string mtlName = Path.GetFileNameWithoutExtension(objPath) + ".mtl";
        var obj = new StringBuilder();
        obj.AppendLine("# MoloMax OBJ export (Z-up scene written Y-up)");
        obj.AppendLine($"mtllib {mtlName}");
        int vBase = 1, tBase = 1;
        var usedMats = new HashSet<int>();
        foreach (var o in s.Objects)
        {
            if (o.GtaType == GtaObjectType.Portal) continue;
            if (selectedOnly && (selectedIds == null || !selectedIds.Contains(o.Id))) continue;
            var w = o.World;
            obj.AppendLine($"o {o.Name.ToLowerInvariant()}");
            // per-vertex colour: average of its corners
            var col = new Vector4[o.Mesh.Verts.Count]; var cnt = new int[o.Mesh.Verts.Count];
            foreach (var f in o.Mesh.Faces) for (int i = 0; i < f.V.Length; i++) { col[f.V[i]] += f.Col[i]; cnt[f.V[i]]++; }
            for (int i = 0; i < o.Mesh.Verts.Count; i++)
            {
                var p = Vector3.Transform(o.Mesh.Verts[i], w);
                var c = cnt[i] > 0 ? col[i] / cnt[i] : PolyMesh.DefaultColour;
                obj.AppendLine(string.Format(ci, "v {0:F5} {1:F5} {2:F5} {3:F4} {4:F4} {5:F4}", p.X, p.Z, -p.Y, c.X, c.Y, c.Z));
            }
            int tCount = 0;
            foreach (var f in o.Mesh.Faces) foreach (var uv in f.UV) { obj.AppendLine(string.Format(ci, "vt {0:F5} {1:F5}", uv.X, 1 - uv.Y)); tCount++; }
            int curMat = int.MinValue, t = tBase;
            foreach (var f in o.Mesh.Faces)
            {
                if (f.MatId != curMat) { curMat = f.MatId; usedMats.Add(curMat); obj.AppendLine($"usemtl {s.Material(curMat).Name}"); }
                obj.Append('f');
                for (int i = 0; i < f.V.Length; i++) obj.Append(' ').Append(f.V[i] + vBase).Append('/').Append(t++);
                obj.AppendLine();
            }
            vBase += o.Mesh.Verts.Count; tBase += tCount;
        }
        File.WriteAllText(objPath, obj.ToString());

        var mtl = new StringBuilder("# MoloMax materials (shader name kept in a comment for Sollumz)\n");
        foreach (var id in usedMats)
        {
            var m = s.Material(id);
            mtl.AppendLine($"newmtl {m.Name}");
            mtl.AppendLine($"# gta_shader {m.Shader}.sps");
            mtl.AppendLine(string.Format(ci, "Kd {0:F4} {1:F4} {2:F4}", m.ViewportColour.X, m.ViewportColour.Y, m.ViewportColour.Z));
            if (!string.IsNullOrEmpty(m.DiffuseTexture)) mtl.AppendLine($"map_Kd {m.DiffuseTexture}");
            if (!string.IsNullOrEmpty(m.BumpTexture)) mtl.AppendLine($"map_Bump {m.BumpTexture}");
            if (!string.IsNullOrEmpty(m.SpecTexture)) mtl.AppendLine($"map_Ks {m.SpecTexture}");
            mtl.AppendLine();
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? ".", mtlName), mtl.ToString());
    }
}
