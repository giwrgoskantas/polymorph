using System.Numerics;
using System.Xml;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Scenes;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Forge;

/// <summary>
/// Weapon forge: a 3D model becomes a GTA weapon by cloning a game weapon (the donor). The donor's skeleton (grip, muzzle,
/// shell ejector, attachment points) is kept, the muzzle goes to the tip of the new barrel, and either the donor is replaced
/// in game (same file name) or a new add-on weapon is written with its metas cloned from the game files.
/// </summary>
public static class WeaponForge
{
    public sealed record Donor(string Label, string Weapon, string Model);

    public static readonly Donor[] Donors =
    {
        new("Pistol", "WEAPON_PISTOL", "w_pi_pistol"),
        new("Combat pistol", "WEAPON_COMBATPISTOL", "w_pi_combatpistol"),
        new("Heavy pistol", "WEAPON_HEAVYPISTOL", "w_pi_heavypistol"),
        new("Revolver", "WEAPON_REVOLVER", "w_pi_revolver"),
        new("SMG", "WEAPON_SMG", "w_sb_smg"),
        new("Micro SMG", "WEAPON_MICROSMG", "w_sb_microsmg"),
        new("Assault rifle", "WEAPON_ASSAULTRIFLE", "w_ar_assaultrifle"),
        new("Carbine rifle", "WEAPON_CARBINERIFLE", "w_ar_carbinerifle"),
        new("Special carbine", "WEAPON_SPECIALCARBINE", "w_ar_specialcarbine"),
        new("Pump shotgun", "WEAPON_PUMPSHOTGUN", "w_sg_pumpshotgun"),
        new("Sniper rifle", "WEAPON_SNIPERRIFLE", "w_sr_sniperrifle"),
        new("Combat MG", "WEAPON_COMBATMG", "w_mg_combatmg"),
        new("Knife", "WEAPON_KNIFE", "w_me_knife_01"),
        new("Bat", "WEAPON_BAT", "w_me_bat"),
        new("Crowbar", "WEAPON_CROWBAR", "w_me_crowbar"),
        new("Machete", "WEAPON_MACHETE", "w_me_machette_lr"),
    };

    public static bool IsMelee(Donor d) => d.Model.StartsWith("w_me_");

    /// <summary>The donor's bones (positions in the weapon model).</summary>
    public static List<ForgeBone> DonorBones(GameFiles game, Donor d) => LoadDonor(game, d).Bones;

    /// <summary>The donor's drawable (loaded with CodeWalker) and its bones.</summary>
    public static (Cw.YdrFile Ydr, List<ForgeBone> Bones) LoadDonor(GameFiles game, Donor d)
    {
        var a = game.FindModel(Cw.JenkHash.GenHash(d.Model)) ?? throw new FileNotFoundException(d.Model + ".ydr is not in the game files.");
        var ydr = new Cw.YdrFile(); ydr.Load(game.ModelBytes(a));
        return (ydr, ForgeCommon.Bones(ydr.Drawable?.Skeleton));
    }

    /// <summary>
    /// Fit: where to put the model (scale and offset along the barrel) so it lines up with the donor: same length (when
    /// <paramref name="matchSize"/>), its thin end on the donor's muzzle side. Returns the transform to apply to the model's
    /// world points, and the new muzzle point.
    /// </summary>
    public static (Matrix4x4 Fit, Vector3 Muzzle) Fit(IReadOnlyList<Vector3> pts, Vector3 donorMin, Vector3 donorMax, Vector3 donorMuzzle, bool matchSize)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        int ax = ForgeCommon.LongAxis(mx - mn), dax = ForgeCommon.LongAxis(donorMax - donorMin);
        // which end is the barrel: the thinner one (cross-section of the last 15 % of the length)
        float len = ForgeCommon.Get(mx - mn, ax);
        (float Area, Vector3 Centre) End(bool high)
        {
            float lim = high ? ForgeCommon.Get(mx, ax) - len * 0.15f : ForgeCommon.Get(mn, ax) + len * 0.15f;
            var emn = new Vector3(float.MaxValue); var emx = new Vector3(float.MinValue); int n = 0;
            foreach (var p in pts) { float t = ForgeCommon.Get(p, ax); if (high ? t >= lim : t <= lim) { emn = Vector3.Min(emn, p); emx = Vector3.Max(emx, p); n++; } }
            if (n == 0) return (float.MaxValue, Vector3.Zero);
            var e = emx - emn; float area = (e.X * e.Y * e.Z) / MathF.Max(1e-4f, ForgeCommon.Get(e, ax));
            return (area, (emn + emx) * 0.5f);
        }
        var hi = End(true); var lo = End(false);
        bool barrelHigh = hi.Area <= lo.Area;
        // the donor's barrel direction (its muzzle side along its long axis)
        var dc = (donorMin + donorMax) * 0.5f;
        float dSign = MathF.Sign(ForgeCommon.Get(donorMuzzle - dc, dax)); if (dSign == 0) dSign = 1;
        Vector3 Axis(int a) => a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;
        var from = Axis(ax) * (barrelHigh ? 1 : -1); var to = Axis(dax) * dSign;
        var rot = RotationBetween(from, to);
        float dlen = ForgeCommon.Get(donorMax - donorMin, dax);
        float s = matchSize && len > 1e-4f ? dlen / len : 1f;
        var c = (mn + mx) * 0.5f;
        var m = Matrix4x4.CreateTranslation(-c) * Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateScale(s);
        // after rotation and scale: line the barrel tip up with the donor's muzzle along the axis, centres on the other axes
        var tip = Vector3.Transform(barrelHigh ? hi.Centre with { } : lo.Centre, m);
        var tipAxis = Axis(dax) * ForgeCommon.Get(tip, dax);
        float tipAlong = ForgeCommon.Get(tip, dax), muzzleAlong = ForgeCommon.Get(donorMuzzle, dax);
        var shift = dc - Axis(dax) * ForgeCommon.Get(dc, dax) + Axis(dax) * (muzzleAlong - tipAlong);
        m *= Matrix4x4.CreateTranslation(shift);
        var muzzle = Vector3.Transform(barrelHigh ? hi.Centre : lo.Centre, m);
        // the muzzle at the very end of the barrel
        float endAlong = float.MinValue;
        foreach (var p in pts) endAlong = MathF.Max(endAlong, ForgeCommon.Get(Vector3.Transform(p, m), dax) * dSign);
        muzzle = muzzle - Axis(dax) * ForgeCommon.Get(muzzle, dax) + Axis(dax) * endAlong * dSign;
        return (m, muzzle);
    }

    static Quaternion RotationBetween(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.9999f) return Quaternion.Identity;
        if (d < -0.9999f) { var ortho = MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY; return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, ortho)), MathF.PI); }
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, b)), MathF.Acos(d)));
    }

    public sealed class Options
    {
        public Donor Donor;
        public string Name = "molo_weapon";
        /// <summary>True: the file takes the donor's name and replaces it in game. False: a new add-on weapon (metas cloned).</summary>
        public bool Replace;
        public Vector3? Muzzle;
    }

    /// <summary>Writes the weapon resource: the model with the donor's skeleton, and for an add-on the cloned metas, fxmanifest and a give command.</summary>
    public static ForgeReport Export(GameFiles game, Scene scene, IReadOnlyList<SceneObject> objects, Options o, string outputFolder,
                                     Func<string, RgbaImage> imageLoader, IEnumerable<string> textureFolders)
    {
        var rep = new ForgeReport();
        try
        {
            var name = o.Replace ? o.Donor.Model : ForgeCommon.SafeName(o.Name.StartsWith("w_") ? o.Name : "w_" + o.Name);
            var weapon = o.Replace ? o.Donor.Weapon : "WEAPON_" + ForgeCommon.SafeName(o.Name).ToUpperInvariant().Replace("W_", "");
            var res = Path.Combine(outputFolder, ForgeCommon.SafeName(o.Replace ? "molo_" + o.Donor.Model : o.Name));
            rep.Folder = res;
            Directory.CreateDirectory(Path.Combine(res, "stream"));
            var (ydr, bones) = LoadDonor(game, o.Donor);
            // the muzzle bone at the new barrel tip (bones are relative to their parent: only the translation changes)
            var skel = ydr.Drawable.Skeleton;
            if (o.Muzzle is { } mz && skel?.Bones?.Items != null)
                foreach (var b in skel.Bones.Items.Where(b => b.Name != null && b.Name.Contains("muzzle", StringComparison.OrdinalIgnoreCase)))
                {
                    var parent = b.ParentIndex >= 0 ? bones[b.ParentIndex].Position : Vector3.Zero;
                    var t = mz - parent; b.Translation = new SharpDX.Vector3(t.X, t.Y, t.Z);
                }
            var donorXml = Cw.YdrXml.GetXml(ydr, Path.GetTempPath());
            var ddoc = new XmlDocument(); ddoc.LoadXml(donorXml);
            var dskel = ddoc.SelectSingleNode("//Skeleton");
            var model = GtaModel.Build(name, scene, objects.Select(x => (x, x.World)));
            if (model.IsEmpty) { rep.Errors.Add("The selected objects have no geometry."); return rep; }
            model.LodDist = 60;
            var doc = ForgeCommon.ModelXml(model, textureFolders, imageLoader, out var tex);
            var root = doc.DocumentElement;
            if (dskel != null) root.InsertAfter(doc.ImportNode(dskel, true), root["ShaderGroup"]);
            else rep.Warnings.Add("The donor has no skeleton: the weapon will have no grip / muzzle points.");
            var data = ForgeCommon.Compile(doc.OuterXml, "ydr", tex, name);
            File.WriteAllBytes(Path.Combine(res, "stream", name + ".ydr"), data);
            rep.Files.Add("stream/" + name + ".ydr");
            rep.Info.Add($"{name}.ydr: {model.VertexCount:N0} vertices, {tex.Count} texture(s), skeleton of {o.Donor.Model} ({bones.Count} bones)");
            string manifest = "fx_version 'cerulean'\ngame 'gta5'\nlua54 'yes'\n\nauthor 'Molo Modding'\ndescription '" + (o.Replace ? $"{o.Donor.Label} replaced" : weapon) + " - made with Polymorph'\n";
            if (!o.Replace)
            {
                Directory.CreateDirectory(Path.Combine(res, "meta"));
                // CWeaponInfo of the donor, renamed
                var info = ForgeCommon.FindItem(game, "weapons.meta", o.Donor.Weapon, $"//Item[@type='CWeaponInfo'][Name='{o.Donor.Weapon}']", out var from1);
                if (info == null) rep.Errors.Add($"{o.Donor.Weapon} not found in the game's weapons.meta.");
                else
                {
                    var w = new XmlDocument();
                    w.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CWeaponInfoBlob><SlotNavigateOrder><Item><WeaponSlots><Item><OrderNumber value=\"180\"/><Entry>SLOT_" + weapon + "</Entry></Item></WeaponSlots></Item></SlotNavigateOrder><SlotBestOrder><WeaponSlots><Item><OrderNumber value=\"60\"/><Entry>SLOT_" + weapon + "</Entry></Item></WeaponSlots></SlotBestOrder><TintSpecValues/><FiringPatternAliases/><UpperBodyFixupExpressionData/><AimingInfos/><Infos><Item><Infos/></Item></Infos><VehicleWeaponInfos/><Name>" + weapon + "</Name></CWeaponInfoBlob>");
                    var it = (XmlElement)w.ImportNode(info, true);
                    ForgeCommon.SetChild(it, "Name", weapon); ForgeCommon.SetChild(it, "Model", name);
                    w.SelectSingleNode("/CWeaponInfoBlob/Infos/Item/Infos").AppendChild(it);
                    File.WriteAllText(Path.Combine(res, "meta", "weapons.meta"), ForgeCommon.Pretty(w));
                    rep.Files.Add("meta/weapons.meta"); rep.Info.Add($"weapons.meta: {o.Donor.Weapon} cloned as {weapon} (from {from1})");
                }
                // model info (archetype of the weapon model)
                var arch = ForgeCommon.FindItem(game, "weaponarchetypes.meta", o.Donor.Model, $"//Item[modelName='{o.Donor.Model}']", out _);
                var a = new XmlDocument();
                a.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CWeaponModelInfo__InitDataList><InitDatas/></CWeaponModelInfo__InitDataList>");
                XmlElement ai;
                if (arch != null) ai = (XmlElement)a.ImportNode(arch, true);
                else { ai = a.CreateElement("Item"); ai.SetAttribute("type", "CWeaponModelInfo__InitData"); ForgeCommon.SetChild(ai, "lodDist", "500"); }
                ForgeCommon.SetChild(ai, "modelName", name); ForgeCommon.SetChild(ai, "txdName", name); ForgeCommon.SetChild(ai, "ptfxAssetName", ai["ptfxAssetName"]?.InnerText ?? "NULL");
                a.SelectSingleNode("//InitDatas").AppendChild(ai);
                File.WriteAllText(Path.Combine(res, "meta", "weaponarchetypes.meta"), ForgeCommon.Pretty(a));
                rep.Files.Add("meta/weaponarchetypes.meta");
                // animations: every set that animates the donor, for the new weapon too
                var anim = new XmlDocument();
                anim.LoadXml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><CWeaponAnimationsSets><WeaponAnimationsSets/></CWeaponAnimationsSets>");
                var sets = anim.SelectSingleNode("//WeaponAnimationsSets");
                int found = 0;
                foreach (var (path, xml) in game.ReadTextFiles("weaponanimations.meta", o.Donor.Weapon))
                {
                    var d = new XmlDocument(); try { d.LoadXml(xml); } catch { continue; }
                    foreach (XmlElement wa in d.SelectNodes($"//WeaponAnimationsSets/Item/WeaponAnimations/Item[@key='{o.Donor.Weapon}']"))
                    {
                        var set = (XmlElement)wa.ParentNode.ParentNode;
                        var key = set.GetAttribute("key");
                        var mine = sets.SelectSingleNode($"Item[@key='{key}']") as XmlElement;
                        if (mine == null)
                        {
                            mine = anim.CreateElement("Item"); mine.SetAttribute("key", key);
                            if (set["Fallback"] != null) mine.AppendChild(anim.ImportNode(set["Fallback"], true));
                            mine.AppendChild(anim.CreateElement("WeaponAnimations"));
                            sets.AppendChild(mine);
                        }
                        var copy = (XmlElement)anim.ImportNode(wa, true); copy.SetAttribute("key", weapon);
                        if (mine["WeaponAnimations"].SelectSingleNode($"Item[@key='{weapon}']") == null) { mine["WeaponAnimations"].AppendChild(copy); found++; }
                    }
                }
                File.WriteAllText(Path.Combine(res, "meta", "weaponanimations.meta"), ForgeCommon.Pretty(anim));
                rep.Files.Add("meta/weaponanimations.meta"); rep.Info.Add($"weaponanimations.meta: {found} animation set(s) cloned");
                manifest += "\nfiles {\n  'meta/weapons.meta',\n  'meta/weaponarchetypes.meta',\n  'meta/weaponanimations.meta',\n}\n\n"
                          + "data_file 'WEAPONINFO_FILE' 'meta/weapons.meta'\ndata_file 'WEAPON_METADATA_FILE' 'meta/weaponarchetypes.meta'\ndata_file 'WEAPON_ANIMATIONS_FILE' 'meta/weaponanimations.meta'\n\nclient_script 'client.lua'\n";
                File.WriteAllText(Path.Combine(res, "client.lua"),
                    $"-- /give_{name.Replace("w_", "")} gives you the weapon (test command, remove it on a live server)\nRegisterCommand('give_{name.Replace("w_", "")}', function()\n    GiveWeaponToPed(PlayerPedId(), `{weapon}`, 250, false, true)\nend, false)\n");
                rep.Files.Add("client.lua");
            }
            File.WriteAllText(Path.Combine(res, "fxmanifest.lua"), manifest);
            rep.Files.Add("fxmanifest.lua");
            rep.Info.Add(o.Replace ? $"Streams {name}.ydr: every {o.Donor.Label} in game shows your model." : $"Add-on weapon {weapon}: ensure the resource, then /give_{name.Replace("w_", "")} in game.");
        }
        catch (Exception e) { rep.Errors.Add(e.Message); }
        return rep;
    }
}
