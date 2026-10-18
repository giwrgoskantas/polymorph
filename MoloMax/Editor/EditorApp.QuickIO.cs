using System.Numerics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Mesh;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>V: quick import / export menu (GTA ydr / yft / ybn / ydd, GLB, OBJ) and Smart UV Project.</summary>
public sealed partial class EditorApp
{
    void QuickIoMenu()
    {
        Ui.TextDisabled("Import / Export   (V)");
        if (ImGui.MenuItem("Import...", "ydr yft ybn ydd glb obj")) ImportDialog();
        if (ImGui.MenuItem("From the game files (GTA V)...")) OpenGameBrowser();
        if (ImGui.MenuItem("MLO from a .ytyp...")) ImportMloDialog();
        ImGui.Separator();
        bool sel = Selected.Count > 0;
        Ui.TextDisabled(sel ? $"Selection ({Selected.Count})" : "Select objects to export them");
        if (ImGui.MenuItem("Export selected  >  GTA .ydr", null, false, sel)) QuickExport(".ydr");
        if (ImGui.MenuItem("Export selected  >  GTA .yft (physical)", null, false, sel)) QuickExport(".yft");
        if (ImGui.MenuItem("Export selected  >  .glb", null, false, sel)) QuickExport(".glb");
        if (ImGui.MenuItem("Export selected  >  .obj", null, false, sel)) ExportObj(true);
        ImGui.Separator();
        if (ImGui.MenuItem("Export scene  >  .glb")) QuickExport(".glb", all: true);
        if (ImGui.MenuItem("Export FiveM resource...", "F10")) OpenHub(1);
        if (ImGui.MenuItem("Send to my FiveM server")) SendToServer();
    }

    /// <summary>The Unwrap UVW modifier of the current object (added when missing), opened with the UV editor: seams, peel, stitch.</summary>
    public void OpenUnwrapModifier()
    {
        var o = Current;
        if (o == null) { SetStatus("Select a mesh first."); return; }
        int i = o.FullStack.FindIndex(m => m is MoloMax.Core.Modifiers.UnwrapUvwModifier);
        if (i < 0) { Do("Add Unwrap UVW", () => { EndModifierEdit(o, false); o.Modifiers.Add(new MoloMax.Core.Modifiers.UnwrapUvwModifier()); o.Touch(); }); i = o.Modifiers.Count - 1; }
        EditModifier(o, i, SubObject.Polygon); OpenUvEditor();
    }

    /// <summary>A UV operation on what is selected: the selected faces in Polygon mode, the whole object in the other edit
    /// modes, every selected object in object mode (like Blender's U menu).</summary>
    void DoUv(string name, Action<SceneObject, PolyMesh, ISet<int>> op)
    {
        var cur = Current;
        if (Level != SubObject.Object && cur != null)
        {
            bool faces = Level is SubObject.Polygon or SubObject.Element && SubSel(cur).Faces.Count > 0;
            DoMesh(name, (o, m, s) => op(o, m, faces ? new HashSet<int>(s.Faces) : null));
            SetStatus($"{name}: {(faces ? SubSel(cur).Faces.Count + " selected face(s)" : "whole object")}.");
            return;
        }
        var objs = SelObjects().Where(o => o.Mesh != null && o.Mesh.Faces.Count > 0).ToList();
        if (objs.Count == 0) { SetStatus("Select an object (or faces in Polygon mode) first."); return; }
        Do(name, () => { foreach (var o in objs) { op(o, o.Mesh, null); o.Touch(); } });
        SetStatus($"{name}: {objs.Count} object(s).");
    }

    /// <summary>The UV menu (right click, like Blender's U menu): every unwrap / projection the app has, on the selection.</summary>
    void UvMenu()
    {
        bool open = ImGui.BeginMenu("UV"); TutRect("uv_menu");
        if (!open) return;
        UvMenuItems();
        ImGui.EndMenu();
    }

    void UvMenuItems()
    {
        if (ImGui.MenuItem("Unwrap (angle based)")) DoUv("Unwrap", (o, m, f) => UvUnfold.AutoUnwrap(m, 66f, f));
        Theme.Tip("Seams on sharp edges (66°), each part flattened and packed in 0..1");
        bool smart = ImGui.MenuItem("Smart UV Project"); TutRect("uv_smart");
        if (smart) DoUv("Smart UV Project", (o, m, f) => UvUnwrap.SmartProject(m, 66f, 0.004f, f));
        if (ImGui.MenuItem("Lightmap Pack")) DoUv("Lightmap Pack", (o, m, f) => UvProject.LightmapPack(m, f));
        Theme.Tip("Each face its own island, all packed in 0..1");
        ImGui.Separator();
        bool cube = ImGui.MenuItem("Cube Projection...", null, false, Selected.Count > 0); TutRect("uv_cube");
        if (cube) StartCubeProjection();
        Theme.Tip("Box map with size, offset, rotation and the material, in a panel");
        if (ImGui.MenuItem("Cylinder Projection")) DoUv("Cylinder Projection", (o, m, f) => UvProject.Cylinder(m, f));
        if (ImGui.MenuItem("Sphere Projection")) DoUv("Sphere Projection", (o, m, f) => UvProject.Sphere(m, f));
        var cam = Viewports[ActiveViewport].Camera;
        (Vector3 R, Vector3 U) Local(SceneObject o)
        {
            Matrix4x4.Invert(o.World, out var inv);
            return (Vector3.TransformNormal(cam.Right, inv), Vector3.TransformNormal(cam.CamUp, inv));
        }
        if (ImGui.MenuItem("Project From View")) DoUv("Project From View", (o, m, f) => { var (r, u) = Local(o); UvProject.FromView(m, f, r, u, false); });
        Theme.Tip("Flat projection along the camera, 1 UV unit = 1 m");
        if (ImGui.MenuItem("Project From View (Bounds)")) DoUv("Project From View (Bounds)", (o, m, f) => { var (r, u) = Local(o); UvProject.FromView(m, f, r, u, true); });
        Theme.Tip("Flat projection along the camera, fitted to 0..1");
        ImGui.Separator();
        if (ImGui.MenuItem("Pack Islands")) DoUv("Pack Islands", (o, m, f) => UvTools.Pack(m, f ?? (IEnumerable<int>)Enumerable.Range(0, m.Faces.Count)));
        if (ImGui.MenuItem("Minimize Stretch")) DoUv("Minimize Stretch", (o, m, f) => UvUnfold.Relax(m, f ?? (IEnumerable<int>)Enumerable.Range(0, m.Faces.Count)));
        if (ImGui.MenuItem("Reset")) DoUv("Reset UVs", (o, m, f) => UvProject.Reset(m, f));
        Theme.Tip("Each face fills the whole 0..1 square");
        ImGui.Separator();
        if (ImGui.MenuItem("Seams, peel, stitch (Unwrap UVW)...", null, false, Current != null)) OpenUnwrapModifier();
        if (ImGui.MenuItem("UV Editor...", null, false, Current != null)) OpenUvEditor();
    }

    public void SmartUv()
    {
        if (Current == null) { SetStatus("Select an object to unwrap."); return; }
        bool faces = Level is SubObject.Polygon or SubObject.Element && SubSel(Current).Faces.Count > 0;
        DoMesh("Smart UV Project", (o, m, s) => UvUnwrap.SmartProject(m, 66f, 0.004f, faces ? s.Faces : null));
        SetStatus(faces ? "Smart UV Project on the selected faces." : "Smart UV Project: whole object unwrapped (islands packed in 0..1).");
    }

    public void QuickExport(string ext, bool all = false)
    {
        var objs = (all ? Scene.Objects.Where(o => !o.Hidden) : SelObjects()).ToList();
        if (objs.Count == 0) { SetStatus("Nothing to export."); return; }
        var baseName = TextureBank.SafeName(objs[0].Name);
        var filter = ext switch { ".ydr" => "GTA drawable (*.ydr)|*.ydr", ".yft" => "GTA fragment (*.yft)|*.yft", _ => "glTF binary (*.glb)|*.glb" };
        using var d = new SaveFileDialog { Filter = filter, FileName = (all ? "scene" : baseName) + ext, Title = "Export " + ext };
        bool ok = d.ShowDialog(form) == DialogResult.OK;
        Input.ReleaseAllKeys();
        if (!ok) return;
        if (ext != ".glb") BakeAllPaint();
        try
        {
            if (ext == ".glb")
            {
                Core.IO.GlbExporter.Export(Scene, objs, d.FileName, TexturePng);
                SetStatus($"Exported {Path.GetFileName(d.FileName)} ({objs.Count} object(s), Y-up, textures embedded)");
            }
            else
            {
                var summary = GtaExporter.ExportModelFile(Scene, objs, d.FileName, LoadImageRgba, renderer.Textures.SearchFolders.ToList());
                SetStatus("Exported " + summary);
            }
        }
        catch (Exception e) { MessageBox.Show(form, "Export failed:\n" + e.Message, "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>Material texture → PNG bytes for the GLB (found next to the scene / in the texture folders).</summary>
    byte[] TexturePng(string path)
    {
        try
        {
            var full = renderer.Textures.Resolve(path);
            if (full == null) return null;
            var img = LoadImageRgba(full);
            if (img == null) return null;
            using var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var bgra = new byte[img.Pixels.Length];
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = img.Pixels[i + 2]; bgra[i + 1] = img.Pixels[i + 1]; bgra[i + 2] = img.Pixels[i]; bgra[i + 3] = img.Pixels[i + 3]; }
            for (int y = 0; y < img.Height; y++) System.Runtime.InteropServices.Marshal.Copy(bgra, y * img.Width * 4, data.Scan0 + y * data.Stride, img.Width * 4);
            bmp.UnlockBits(data);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
