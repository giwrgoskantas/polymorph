using System.Numerics;
using System.Text;
using System.Xml;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Gta.Game;
using MoloMax.Core.Scenes;
using Cw = CodeWalker.GameFiles;

namespace MoloMax.Core.Gta.Forge;

/// <summary>A bone of a game model: name, tag, parent and its position in the model (metres).</summary>
public sealed record ForgeBone(string Name, int Tag, int Index, int Parent, Vector3 Position);

/// <summary>What a forge export produced.</summary>
public sealed class ForgeReport
{
    public string Folder;
    public List<string> Files = new();
    public List<string> Info = new();
    public List<string> Warnings = new();
    public List<string> Errors = new();
    public bool Ok => Errors.Count == 0;
    public override string ToString() => string.Join("\n", Errors.Select(e => "ERROR: " + e).Concat(Warnings.Select(w => "Warning: " + w)).Concat(Info));
}

/// <summary>Shared pieces of the weapon and vehicle forges: donor models from the game, bones, metas cloned from the game files.</summary>
public static class ForgeCommon
{
    public static string SafeName(string s) => new((s ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    /// <summary>Bones of a skeleton with their positions in the model (parent chain applied).</summary>
    public static List<ForgeBone> Bones(Cw.Skeleton skel)
    {
        var res = new List<ForgeBone>();
        var items = skel?.Bones?.Items;
        if (items == null) return res;
        var world = new Matrix4x4[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var b = items[i];
            var local = Matrix4x4.CreateFromQuaternion(new Quaternion(b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W)) * Matrix4x4.CreateTranslation(b.Translation.X, b.Translation.Y, b.Translation.Z);
            int p = b.ParentIndex;
            world[i] = p >= 0 && p < i ? local * world[p] : local;
            res.Add(new ForgeBone(b.Name, b.Tag, i, p, world[i].Translation));
        }
        return res;
    }

    /// <summary>The first &lt;Item&gt; of the meta matching the XPath, from the game files named <paramref name="file"/>.</summary>
    public static XmlElement FindItem(GameFiles game, string file, string hint, string xpath, out string from)
    {
        from = null;
        foreach (var (path, xml) in game.ReadTextFiles(file, hint))
        {
            var doc = new XmlDocument();
            try { doc.LoadXml(xml); } catch { continue; }
            if (doc.SelectSingleNode(xpath) is XmlElement e) { from = path; return e; }
        }
        return null;
    }

    public static void SetChild(XmlElement item, string name, string value)
    {
        var n = item[name];
        if (n == null) { n = item.OwnerDocument.CreateElement(name); item.AppendChild(n); }
        n.InnerText = value;
    }

    /// <summary>Indented XML text of a document.</summary>
    public static string Pretty(XmlDocument doc)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false, Encoding = Encoding.UTF8 })) doc.Save(w);
        return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"");
    }

    /// <summary>Compiles a CodeWalker XML model (textures in <paramref name="textures"/>) and checks it loads back.</summary>
    public static byte[] Compile(string xml, string kind, List<ExportTexture> textures, string name)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "polymorph_forge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var data = GtaExporter.Compile(xml, kind, textures, tmp);
            GtaExporter.Verify(data, kind, name);
            return data;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Our model, as the &lt;Drawable&gt; XML CodeWalker reads (textures embedded).</summary>
    public static XmlDocument ModelXml(GtaModel model, IEnumerable<string> textureFolders, Func<string, RgbaImage> imageLoader, out List<ExportTexture> textures)
    {
        var bank = new TextureBank { ImageLoader = imageLoader };
        if (textureFolders != null) bank.SearchFolders.AddRange(textureFolders);
        var xml = CwXml.Drawable(model, bank, true, out textures);
        var doc = new XmlDocument(); doc.LoadXml(xml);
        return doc;
    }

    /// <summary>Bones of a compiled .ydr (self-tests: the forge output read back).</summary>
    public static List<ForgeBone> YdrBones(byte[] data) { var f = new Cw.YdrFile(); f.Load(data); return Bones(f.Drawable?.Skeleton); }

    /// <summary>Bones and drawn models of a compiled .yft (self-tests).</summary>
    public static (List<ForgeBone> Bones, int Models, int Children) YftInfo(byte[] data)
    {
        var f = new Cw.YftFile(); f.Load(data);
        var d = f.Fragment?.Drawable;
        return (Bones(d?.Skeleton), d?.DrawableModels?.High?.Length ?? 0, f.Fragment?.PhysicsLODGroup?.PhysicsLOD1?.Children?.data_items?.Length ?? 0);
    }

    /// <summary>Longest axis of a box: 0 X, 1 Y, 2 Z.</summary>
    public static int LongAxis(Vector3 size) => size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
    public static float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
}
