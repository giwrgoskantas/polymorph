using System.Numerics;
using System.Xml;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Forge;

/// <summary>
/// Vehicle forge: a 3D car becomes a FiveM vehicle by cloning a game vehicle (the donor). The donor's skeleton, wheels,
/// collisions and physics are kept; its body is replaced by the new model, fitted on the donor's wheels. The metas
/// (vehicles, handling, carvariations) are cloned from the game files and renamed, the donor's textures follow.
/// </summary>
public static class VehicleForge
{
    public static readonly string[] Suggested = { "adder", "comet2", "sultan2", "dominator", "blista", "tailgater2", "baller2", "sandking", "kuruma", "elegy2", "buffalo", "felon", "bati", "sanchez" };

    public sealed class Donor
    {
        public string Name;
        public Cw.YftFile Yft;
        public List<ForgeBone> Bones = new();
        public Vector3 Min, Max;
        /// <summary>Centres of the donor's wheels (by bone name: wheel_lf, wheel_rf, wheel_lr, wheel_rr...).</summary>
        public Dictionary<string, Vector3> Wheels = new();
        public float WheelRadius = 0.35f;
    }

    public static Donor LoadDonor(GameFiles game, string name)
    {
        name = ForgeCommon.SafeName(name);
        var a = game.FindModel(Cw.JenkHash.GenHash(name)) ?? throw new FileNotFoundException(name + ".yft is not in the game files.");
        if (!a.Ext.Equals(".yft", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(name + " is not a vehicle (.yft).");
        var yft = new Cw.YftFile(); yft.Load(game.ModelBytes(a));
        var d = new Donor { Name = name, Yft = yft, Bones = ForgeCommon.Bones(yft.Fragment?.Drawable?.Skeleton) };
        var dr = yft.Fragment.Drawable;
        d.Min = new Vector3(dr.BoundingBoxMin.X, dr.BoundingBoxMin.Y, dr.BoundingBoxMin.Z); d.Max = new Vector3(dr.BoundingBoxMax.X, dr.BoundingBoxMax.Y, dr.BoundingBoxMax.Z);
        foreach (var b in d.Bones) if (b.Name != null && b.Name.StartsWith("wheel_", StringComparison.OrdinalIgnoreCase)) d.Wheels[b.Name.ToLowerInvariant()] = b.Position;
        if (d.Wheels.Count > 0) d.WheelRadius = MathF.Max(0.2f, d.Wheels.Values.Average(w => w.Z) - d.Min.Z);
        return d;
    }

    /// <summary>
    /// Wheels of a car model: the separate parts (connected pieces) low in the model, round seen from the side, one near each
    /// corner. Returns the four centres (lf, rf, lr, rr) in the model's world space, or null when not found.
    /// Assumes the car stands on Z with its length along its longest horizontal axis.
    /// </summary>
    public static Dictionary<string, Vector3> FindWheels(IReadOnlyList<(PolyMesh Mesh, Matrix4x4 World)> parts, int lengthAxis, bool frontIsPositive, out List<(Vector3 Min, Vector3 Max)> boxes)
    {
        boxes = null;
        var islands = new List<(Vector3 Min, Vector3 Max, int Verts)>();
        foreach (var (mesh, world) in parts)
            foreach (var comp in Islands(mesh))
            {
                var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
                foreach (var v in comp) { var p = Vector3.Transform(mesh.Verts[v], world); mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                islands.Add((mn, mx, comp.Count));
            }
        if (islands.Count < 4) return null;
        var all0 = islands.Select(i => i.Min).Aggregate(Vector3.Min); var all1 = islands.Select(i => i.Max).Aggregate(Vector3.Max);
        float height = all1.Z - all0.Z;
        int side = lengthAxis == 0 ? 1 : 0;
        // round from the side: as tall as long (along the car), touching the ground, not too thick
        var cands = islands.Where(i =>
        {
            var s = i.Max - i.Min; float h = s.Z, l = ForgeCommon.Get(s, lengthAxis), w = ForgeCommon.Get(s, side);
            return h > height * 0.15f && h < height * 0.7f && MathF.Abs(h - l) < 0.25f * h && w < h * 0.9f && i.Min.Z < all0.Z + height * 0.12f;
        }).ToList();
        // merge pieces of the same wheel (tyre + rim): overlapping boxes
        var wheels = new List<(Vector3 Min, Vector3 Max)>();
        foreach (var c in cands.OrderByDescending(c => (c.Max - c.Min).LengthSquared()))
        {
            int k = wheels.FindIndex(w => Vector3.Distance((w.Min + w.Max) * 0.5f, (c.Min + c.Max) * 0.5f) < (w.Max - w.Min).Z * 0.5f);
            if (k >= 0) wheels[k] = (Vector3.Min(wheels[k].Min, c.Min), Vector3.Max(wheels[k].Max, c.Max)); else wheels.Add((c.Min, c.Max));
        }
        if (wheels.Count < 4) return null;
        boxes = wheels;
        var centre = (all0 + all1) * 0.5f;
        // one per corner: the farthest from the centre in each quadrant
        var res = new Dictionary<string, Vector3>();
        foreach (var (key, front, right) in new[] { ("wheel_lf", true, false), ("wheel_rf", true, true), ("wheel_lr", false, false), ("wheel_rr", false, true) })
        {
            var q = wheels.Select(w => (w.Min + w.Max) * 0.5f).Where(p =>
            {
                bool f = (ForgeCommon.Get(p, lengthAxis) > ForgeCommon.Get(centre, lengthAxis)) == frontIsPositive;
                // GTA: +Y forward, +X to the right; with the car along Y facing +Y, right = +X (flip when facing -Y)
                float sideCoord = ForgeCommon.Get(p, side) - ForgeCommon.Get(centre, side);
                bool r = lengthAxis == 1 ? (sideCoord > 0) == frontIsPositive : (sideCoord < 0) == frontIsPositive;
                return f == front && r == right;
            }).OrderByDescending(p => Vector3.DistanceSquared(p, centre)).ToList();
            if (q.Count == 0) return null;
            res[key] = q[0];
        }
        return res;
    }

    static List<List<int>> Islands(PolyMesh m)
    {
        var parent = Enumerable.Range(0, m.Verts.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var f in m.Faces) for (int i = 1; i < f.V.Length; i++) { int a = Find(f.V[0]), b = Find(f.V[i]); if (a != b) parent[a] = b; }
        var groups = new Dictionary<int, List<int>>();
        foreach (var f in m.Faces) foreach (var v in f.V) { int r = Find(v); if (!groups.TryGetValue(r, out var l)) groups[r] = l = new List<int>(); l.Add(v); }
        return groups.Values.Select(l => l.Distinct().ToList()).ToList();
    }

    /// <summary>
    /// Fit: the transform that puts the model in the donor's space: car along Y facing +Y, scaled so its wheelbase and track
    /// match the donor's wheels (the donor's skeleton, wheels and collision then fit it), standing on the donor's ground.
    /// </summary>
    public static Matrix4x4 Fit(Dictionary<string, Vector3> mine, int lengthAxis, bool frontIsPositive, Donor d, bool keepProportions)
    {
        // into GTA axes first: length along Y, front +Y
        var rot = Matrix4x4.Identity;
        if (lengthAxis == 0) rot = Matrix4x4.CreateRotationZ(frontIsPositive ? MathF.PI / 2 : -MathF.PI / 2);
        else if (!frontIsPositive) rot = Matrix4x4.CreateRotationZ(MathF.PI);
        var w = mine.ToDictionary(k => k.Key, k => Vector3.Transform(k.Value, rot));
        Vector3 Mid(Dictionary<string, Vector3> x) => x.Values.Aggregate(Vector3.Zero, (a, b) => a + b) / x.Count;
        float Base(Dictionary<string, Vector3> x) => ((x["wheel_lf"] + x["wheel_rf"]) * 0.5f - (x["wheel_lr"] + x["wheel_rr"]) * 0.5f).Y;
        float Track(Dictionary<string, Vector3> x) => ((x["wheel_rf"] - x["wheel_lf"]).X + (x["wheel_rr"] - x["wheel_lr"]).X) * 0.5f;
        var dw = d.Wheels.Where(k => k.Key is "wheel_lf" or "wheel_rf" or "wheel_lr" or "wheel_rr").ToDictionary(k => k.Key, k => k.Value);
        if (dw.Count < 4) throw new InvalidDataException(d.Name + " has no four wheels.");
        float sy = Base(dw) / MathF.Max(1e-3f, Base(w)), sx = Track(dw) / MathF.Max(1e-3f, Track(w));
        if (keepProportions) { sx = sy = (sx + sy) * 0.5f; }
        float sz = (sx + sy) * 0.5f;
        var scale = Matrix4x4.CreateScale(sx, sy, sz);
        var mc = Vector3.Transform(Mid(w), scale);
        var shift = Mid(dw) - mc;
        return rot * scale * Matrix4x4.CreateTranslation(shift);
    }

    public sealed class Options
    {
        public string Name = "molo_car";
        public string Label = "Molo car";
        /// <summary>Keep the donor's body parts that are not covered (false: only the new body).</summary>
        public bool KeepDonorWheels = true;
    }

    /// <summary>Writes the FiveM vehicle resource: name.yft (+ _hi), the donor's textures as name.ytd, metas, fxmanifest and a spawn command.</summary>
    public static ForgeReport Export(GameFiles game, Donor d, Scene scene, IReadOnlyList<(SceneObject Obj, Matrix4x4 ToDonor)> body, Options o, string outputFolder,
                                     Func<string, RgbaImage> imageLoader, IEnumerable<string> textureFolders)
    {
        var rep = new ForgeReport();
        var tmp = Path.Combine(Path.GetTempPath(), "polymorph_vforge_" + Guid.NewGuid().ToString("N"));
        try
        {
            var name = ForgeCommon.SafeName(o.Name);
            var res = Path.Combine(outputFolder, name);
            rep.Folder = res;
            var stream = Path.Combine(res, "stream"); var data = Path.Combine(res, "data");
            Directory.CreateDirectory(stream); Directory.CreateDirectory(data); Directory.CreateDirectory(tmp);
            // ---- the model: the donor's fragment, its main drawable's models replaced by the new body
            var xml = Cw.YftXml.GetXml(d.Yft, tmp);
            var doc = new XmlDocument(); doc.LoadXml(xml);
            var main = doc.SelectSingleNode("/Fragment/Drawable") as XmlElement ?? throw new InvalidDataException("The donor's fragment has no drawable.");
            var model = GtaModel.Build(name, scene, body.Select(b => (b.Obj, b.ToDonor)));
            if (model.IsEmpty) { rep.Errors.Add("The selected objects have no geometry."); return rep; }
            var mine = ForgeCommon.ModelXml(model, textureFolders, imageLoader, out var tex);
            foreach (var t in tex) File.WriteAllBytes(Path.Combine(tmp, t.FileName), t.Dds);
            // shaders: ours appended to the donor's, our geometries renumbered
            var dShaders = main.SelectSingleNode("ShaderGroup/Shaders") as XmlElement;
            int offset = dShaders?.ChildNodes.Count ?? 0;
            foreach (XmlNode s in mine.SelectNodes("/Drawable/ShaderGroup/Shaders/Item")) dShaders.AppendChild(doc.ImportNode(s, true));
            var dTex = main.SelectSingleNode("ShaderGroup/TextureDictionary") as XmlElement;
            var myTex = mine.SelectNodes("/Drawable/ShaderGroup/TextureDictionary/Item");
            if (myTex.Count > 0)
            {
                if (dTex == null) { dTex = doc.CreateElement("TextureDictionary"); main["ShaderGroup"].PrependChild(dTex); }
                foreach (XmlNode t in myTex) dTex.AppendChild(doc.ImportNode(t, true));
            }
            int chassis = Math.Max(0, d.Bones.FindIndex(b => string.Equals(b.Name, "chassis", StringComparison.OrdinalIgnoreCase)));
            foreach (var tag in new[] { "DrawableModelsHigh", "DrawableModelsMedium", "DrawableModelsLow", "DrawableModelsVeryLow" })
                if (main[tag] != null) main.RemoveChild(main[tag]);
            var high = (XmlElement)doc.ImportNode(mine.SelectSingleNode("/Drawable/DrawableModelsHigh"), true);
            foreach (XmlElement si in high.SelectNodes(".//ShaderIndex")) si.SetAttribute("value", (int.Parse(si.GetAttribute("value")) + offset).ToString());
            foreach (XmlElement bi in high.SelectNodes("Item/BoneIndex")) bi.SetAttribute("value", chassis.ToString());
            main.AppendChild(high);
            foreach (var (tag, v) in new[] { ("LodDistHigh", "9998"), ("FlagsMed", "0"), ("FlagsLow", "0"), ("FlagsVlow", "0") })
                if (main[tag] is XmlElement le) le.SetAttribute("value", v);
            var outXml = doc.OuterXml;
            var xdoc = new XmlDocument(); xdoc.LoadXml(outXml);
            var bytes = Cw.XmlYft.GetYft(xdoc, tmp).Save(); // the donor's textures and ours are in tmp
            GtaExporter.Verify(bytes, "yft", name);
            File.WriteAllBytes(Path.Combine(stream, name + ".yft"), bytes);
            File.WriteAllBytes(Path.Combine(stream, name + "_hi.yft"), bytes);
            rep.Files.Add($"stream/{name}.yft"); rep.Files.Add($"stream/{name}_hi.yft");
            rep.Info.Add($"{name}.yft: body {model.VertexCount:N0} vertices on the {d.Name} skeleton ({d.Bones.Count} bones, wheels and collision of {d.Name})");
            // the donor's own textures (interior, badges, wheels) under the new texture dictionary name
            var txd = game.FindModel(Cw.JenkHash.GenHash(d.Name)) is { } da ? game.TextureDictionaryOf(da) : null;
            if (txd != null && game.ModelBytes(txd) is { } tb) { File.WriteAllBytes(Path.Combine(stream, name + ".ytd"), tb); rep.Files.Add($"stream/{name}.ytd"); }
            else rep.Warnings.Add($"{d.Name}.ytd not found: the donor's interior textures will be missing.");
            // ---- metas cloned from the game
            string handlingId = d.Name.ToUpperInvariant();
            var vItem = ForgeCommon.FindItem(game, "vehicles.meta", $">{d.Name}<", $"//InitDatas/Item[translate(modelName,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{d.Name}']", out var vFrom);
            if (vItem == null) rep.Errors.Add($"{d.Name} not found in the game's vehicles.meta.");
            else
            {
                handlingId = vItem["handlingId"]?.InnerText ?? handlingId;
                var vdoc = new XmlDocument();
                vdoc.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CVehicleModelInfo__InitDataList><residentTxd>vehshare</residentTxd><residentAnims /><InitDatas/><txdRelationships/></CVehicleModelInfo__InitDataList>");
                var it = (XmlElement)vdoc.ImportNode(vItem, true);
                ForgeCommon.SetChild(it, "modelName", name); ForgeCommon.SetChild(it, "txdName", name);
                ForgeCommon.SetChild(it, "handlingId", name.ToUpperInvariant()); ForgeCommon.SetChild(it, "gameName", name.ToUpperInvariant());
                vdoc.SelectSingleNode("//InitDatas").AppendChild(it);
                // texture parents of the donor (shared interior / wheels dictionaries) for ours too
                var rel = ForgeCommon.FindItem(game, "vehicles.meta", $">{d.Name}<", $"//txdRelationships/Item[translate(child,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{d.Name}']", out _);
                if (rel != null) { var r = (XmlElement)vdoc.ImportNode(rel, true); ForgeCommon.SetChild(r, "child", name); vdoc.SelectSingleNode("//txdRelationships").AppendChild(r); }
                File.WriteAllText(Path.Combine(data, "vehicles.meta"), ForgeCommon.Pretty(vdoc));
                rep.Files.Add("data/vehicles.meta"); rep.Info.Add($"vehicles.meta: {d.Name} cloned (from {vFrom})");
            }
            var hItem = ForgeCommon.FindItem(game, "handling.meta", handlingId, $"//HandlingData/Item[translate(handlingName,'abcdefghijklmnopqrstuvwxyz','ABCDEFGHIJKLMNOPQRSTUVWXYZ')='{handlingId.ToUpperInvariant()}']", out _);
            if (hItem == null) rep.Warnings.Add($"handling {handlingId} not found: the car uses the default handling.");
            else
            {
                var hdoc = new XmlDocument();
                hdoc.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CHandlingDataMgr><HandlingData/></CHandlingDataMgr>");
                var it = (XmlElement)hdoc.ImportNode(hItem, true); ForgeCommon.SetChild(it, "handlingName", name.ToUpperInvariant());
                hdoc.SelectSingleNode("//HandlingData").AppendChild(it);
                File.WriteAllText(Path.Combine(data, "handling.meta"), ForgeCommon.Pretty(hdoc));
                rep.Files.Add("data/handling.meta");
            }
            var cItem = ForgeCommon.FindItem(game, "carvariations.meta", $">{d.Name}<", $"//variationData/Item[translate(modelName,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='{d.Name}']", out _);
            if (cItem != null)
            {
                var cdoc = new XmlDocument();
                cdoc.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CVehicleModelInfoVariation><variationData/></CVehicleModelInfoVariation>");
                var it = (XmlElement)cdoc.ImportNode(cItem, true); ForgeCommon.SetChild(it, "modelName", name);
                cdoc.SelectSingleNode("//variationData").AppendChild(it);
                File.WriteAllText(Path.Combine(data, "carvariations.meta"), ForgeCommon.Pretty(cdoc));
                rep.Files.Add("data/carvariations.meta");
            }
            var files = rep.Files.Where(f => f.StartsWith("data/")).ToList();
            var man = "fx_version 'cerulean'\ngame 'gta5'\nlua54 'yes'\n\nauthor 'Molo Modding'\ndescription '" + o.Label.Replace("'", " ") + " - made with Polymorph'\n\nfiles {\n" + string.Join("", files.Select(f => $"  '{f}',\n")) + "}\n\n";
            if (files.Contains("data/handling.meta")) man += "data_file 'HANDLING_FILE' 'data/handling.meta'\n";
            if (files.Contains("data/vehicles.meta")) man += "data_file 'VEHICLE_METADATA_FILE' 'data/vehicles.meta'\n";
            if (files.Contains("data/carvariations.meta")) man += "data_file 'VEHICLE_VARIATION_FILE' 'data/carvariations.meta'\n";
            man += "\nclient_script 'client.lua'\n";
            File.WriteAllText(Path.Combine(res, "fxmanifest.lua"), man);
            File.WriteAllText(Path.Combine(res, "client.lua"),
                $"AddTextEntry('{name.ToUpperInvariant()}', '{o.Label.Replace("'", " ")}')\n\n-- /spawn_{name} puts you in the car (test command, remove it on a live server)\nRegisterCommand('spawn_{name}', function()\n    local model = `{name}`\n    RequestModel(model)\n    while not HasModelLoaded(model) do Wait(0) end\n    local ped = PlayerPedId()\n    local p = GetEntityCoords(ped)\n    local veh = CreateVehicle(model, p.x, p.y, p.z, GetEntityHeading(ped), true, false)\n    SetPedIntoVehicle(ped, veh, -1)\n    SetModelAsNoLongerNeeded(model)\nend, false)\n");
            rep.Files.Add("fxmanifest.lua"); rep.Files.Add("client.lua");
            rep.Info.Add($"ensure {name}, then /spawn_{name} in game.");
        }
        catch (Exception e) { rep.Errors.Add(e.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        return rep;
    }
}
