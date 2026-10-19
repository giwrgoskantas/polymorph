using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>Export page, "Selected models": each selected model becomes one .ydr / .yft, with its own pivot. Nothing else is written.</summary>
public sealed partial class EditorApp
{
    bool exportSelectedOnly = true;
    int exportSelFormat; // 0 ydr, 1 yft
    System.Threading.Tasks.Task<List<string>> exportSelTask;
    List<string> exportSelResult;
    string exportSelProgress = "";

    /// <summary>The models to write: a mesh inside a Drawable node exports as that node (its pivot, all its meshes).</summary>
    List<SceneObject> ExportRoots()
    {
        var roots = new List<SceneObject>();
        foreach (var o in SelObjects())
        {
            var r = o;
            for (var p = Scene.Parent(r); p != null; p = Scene.Parent(p))
                if (p.GtaType == GtaObjectType.Model) r = p;
            if (r.GtaType is GtaObjectType.Light or GtaObjectType.Collision && Scene.Parent(r) is { } pp) r = pp;
            if (!roots.Contains(r)) roots.Add(r);
        }
        // a selected parent already carries its children
        return roots.Where(r => !roots.Any(o => o != r && Scene.Descendants(o).Contains(r))).ToList();
    }

    void HubExportSelected()
    {
        var st = Scene.Gta;
        bool busy = exportSelTask is { IsCompleted: false };
        ImGui.BeginDisabled(busy);
        ImGui.AlignTextToFramePadding(); Ui.Text("Output folder"); ImGui.SameLine(120);
        if (string.IsNullOrEmpty(st.OutputFolder)) st.OutputFolder = DefaultExportFolder();
        string folder = st.OutputFolder;
        ImGui.SetNextItemWidth(-70);
        if (ImGui.InputText("##selout", ref folder, 260)) st.OutputFolder = folder;
        ImGui.SameLine();
        if (ImGui.Button("Browse##sel", new Vector2(-1, 0)))
        {
            using var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = st.OutputFolder, Description = "Folder for the exported models" };
            if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) st.OutputFolder = d.SelectedPath;
            Input.ReleaseAllKeys();
        }
        ImGui.AlignTextToFramePadding(); Ui.Text("Format"); ImGui.SameLine(120);
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("##selfmt", ref exportSelFormat, ".ydr (drawable)\0.yft (physical / fragment)\0");
        ImGui.EndDisabled();

        var roots = ExportRoots();
        ImGui.Spacing();
        if (roots.Count == 0) Ui.TextDisabled("Select the models to export in the view or the Scene Explorer.");
        else
        {
            Ui.TextDisabled($"{roots.Count} model(s), one file each, with its own pivot (where it is in the scene):");
            ImGui.BeginChild("##selist", new Vector2(-1, Math.Min(roots.Count, 8) * ImGui.GetTextLineHeightWithSpacing() + 8), ImGuiChildFlags.Borders);
            string ext = exportSelFormat == 1 ? ".yft" : ".ydr";
            foreach (var r in roots) Ui.Text(TextureBank.SafeName(r.Name) + ext);
            ImGui.EndChild();
        }
        ImGui.Spacing();
        if (busy) Theme.Toggle($"Exporting... {exportSelProgress}", true, new Vector2(-1, 38));
        else
        {
            ImGui.BeginDisabled(roots.Count == 0);
            ImGui.PushStyleColor(ImGuiCol.Button, HubAccent * new Vector4(0.8f, 0.8f, 0.8f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, HubAccent);
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.08f, 0.07f, 0.05f, 1));
            if (ImGui.Button("EXPORT", new Vector2(-1, 38))) StartExportSelected(roots, st.OutputFolder);
            ImGui.PopStyleColor(3);
            ImGui.EndDisabled();
        }
        if (exportSelTask is { IsCompleted: true })
        {
            try { exportSelResult = exportSelTask.Result; }
            catch (Exception e) { exportSelResult = new() { "ERROR " + (e.InnerException?.Message ?? e.Message) }; }
            exportSelTask = null;
            SetStatus($"Exported {exportSelResult.Count(r => !r.StartsWith("ERROR"))} model(s) to {st.OutputFolder}");
        }
        if (exportSelResult != null)
            foreach (var line in exportSelResult)
                Ui.TextColored(line.StartsWith("ERROR") ? new Vector4(1f, 0.45f, 0.4f, 1) : new Vector4(0.55f, 0.9f, 0.6f, 1), line);
    }

    void StartExportSelected(List<SceneObject> roots, string folder)
    {
        BakeAllPaint();
        var snapshot = Undo.SnapshotForSave(Scene);
        var ids = roots.Select(r => r.Id).ToList();
        var texFolders = renderer.Textures.SearchFolders.ToList();
        string ext = exportSelFormat == 1 ? ".yft" : ".ydr";
        exportSelResult = null; exportSelProgress = "";
        exportSelTask = System.Threading.Tasks.Task.Run(() =>
        {
            var lines = new List<string>();
            Directory.CreateDirectory(folder);
            for (int i = 0; i < ids.Count; i++)
            {
                var o = snapshot.Find(ids[i]); if (o == null) continue;
                var file = Path.Combine(folder, TextureBank.SafeName(o.Name) + ext);
                exportSelProgress = $"{i + 1}/{ids.Count} {o.Name}";
                try { lines.Add(GtaExporter.ExportModelFile(snapshot, new[] { o }, file, LoadImageRgba, texFolders)); }
                catch (Exception e) { lines.Add($"ERROR {o.Name}: {e.Message}"); }
            }
            return lines;
        });
    }
}
