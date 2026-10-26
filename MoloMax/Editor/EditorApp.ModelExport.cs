using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Export;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// File > Export models: the GIMS way. Page 1 lists the models of the scene with a box each (the selection is ticked),
/// page 2 the options (format, collisions, folder), page 3 the progress and what was written. Each model is one file
/// named after it, with its own pivot; nothing is merged or renamed.
/// </summary>
public sealed partial class EditorApp
{
    bool mxOpen; int mxPage;
    readonly HashSet<int> mxChecked = new();
    readonly HashSet<int> mxExpanded = new();
    string mxFilter = "";
    int mxFormat; // 0 ydr, 1 yft
    bool mxCollisions = true;
    string mxFolder;
    System.Threading.Tasks.Task<List<string>> mxTask;
    List<string> mxResult;
    volatile string mxProgress = "";
    volatile int mxDone;

    /// <summary>Every model of the scene: a Drawable node with its meshes, or a mesh that has no Drawable above it.</summary>
    List<SceneObject> ModelRoots() => Scene.Objects
        .Where(o => Scene.Parent(o) == null && o.GtaType is GtaObjectType.Model or GtaObjectType.Drawable)
        .Where(o => o.GtaType == GtaObjectType.Model ? Scene.Descendants(o).Any(d => d.Mesh?.Faces.Count > 0) : o.Mesh?.Faces.Count > 0)
        .ToList();

    /// <summary>File name of a model: its name; a GIMS mesh name "..._high_0" without that end (the model is the Drawable).</summary>
    static string ModelFileName(SceneObject o)
    {
        var n = o.Name;
        var m = System.Text.RegularExpressions.Regex.Match(n, @"^(.+)_(high|med|low|vlow)_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && o.GtaType == GtaObjectType.Drawable) n = m.Groups[1].Value;
        return TextureBank.SafeName(n);
    }

    public void OpenModelExport()
    {
        mxOpen = true; mxPage = 0; mxResult = null; mxChecked.Clear();
        var roots = ModelRoots();
        // the selection ticked (a mesh picked inside a Drawable ticks its Drawable); nothing selected: everything
        var sel = SelObjects().Select(o => { var r = o; for (var p = Scene.Parent(r); p != null; p = Scene.Parent(p)) r = p; return r.Id; }).ToHashSet();
        foreach (var r in roots) if (sel.Count == 0 || sel.Contains(r.Id)) mxChecked.Add(r.Id);
        mxFolder ??= Scene.Gta.OutputFolder;
        if (string.IsNullOrEmpty(mxFolder)) mxFolder = DefaultExportFolder();
    }

    void DrawModelExport()
    {
        if (!mxOpen) return;
        var vpSize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowSize(new Vector2(460, 560), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(vpSize * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        bool busy = mxTask is { IsCompleted: false };
        if (!ImGui.Begin("Export models###modelexport", ref mxOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }
        if (!mxOpen && busy) mxOpen = true; // an export in progress keeps its window
        var roots = ModelRoots();
        float footer = ImGui.GetFrameHeightWithSpacing() * 1.6f;

        if (mxPage == 0)
        {
            Ui.TextColored(Theme.Gold, "Selection");
            Ui.TextDisabled("Tick the models to export (one file each, named after the model).");
            ImGui.SetNextItemWidth(-130); ImGui.InputTextWithHint("##mxfind", "Find...", ref mxFilter, 64);
            ImGui.SameLine(); if (ImGui.SmallButton("All##mx")) foreach (var r in roots) mxChecked.Add(r.Id);
            ImGui.SameLine(); if (ImGui.SmallButton("None##mx")) mxChecked.Clear();
            ImGui.BeginChild("##mxlist", new Vector2(-1, -footer), ImGuiChildFlags.Borders);
            foreach (var r in roots)
            {
                if (mxFilter.Length > 0 && !r.Name.Contains(mxFilter, StringComparison.OrdinalIgnoreCase)) continue;
                ImGui.PushID(r.Id);
                var kids = Scene.Descendants(r).Where(d => d.Mesh?.Faces.Count > 0 || d.GtaType == GtaObjectType.Collision).ToList();
                bool open = mxExpanded.Contains(r.Id);
                if (kids.Count > 0) { if (ImGui.ArrowButton("##ex", open ? ImGuiDir.Down : ImGuiDir.Right)) { if (open) mxExpanded.Remove(r.Id); else mxExpanded.Add(r.Id); } }
                else ImGui.Dummy(new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine();
                bool on = mxChecked.Contains(r.Id);
                if (ImGui.Checkbox("##on", ref on)) { if (on) mxChecked.Add(r.Id); else mxChecked.Remove(r.Id); }
                ImGui.SameLine();
                int verts = (r.Mesh?.Verts.Count ?? 0) + kids.Sum(k => k.Mesh?.Verts.Count ?? 0);
                Ui.Text($"[Model] {ModelFileName(r)}");
                ImGui.SameLine(); Ui.TextDisabled($"{verts:N0} verts");
                if (open)
                    foreach (var k in kids) { ImGui.Indent(48); Ui.TextDisabled((k.GtaType == GtaObjectType.Collision ? "[Collision] " : "[Mesh] ") + k.Name); ImGui.Unindent(48); }
                ImGui.PopID();
            }
            if (roots.Count == 0) Ui.TextDisabled("The scene has no model with polygons.");
            ImGui.EndChild();
            int n = roots.Count(r => mxChecked.Contains(r.Id));
            Ui.TextDisabled($"{n} of {roots.Count} model(s) ticked");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 110);
            ImGui.BeginDisabled(n == 0);
            if (ImGui.Button("Continue", new Vector2(110, 0))) mxPage = 1;
            ImGui.EndDisabled();
        }
        else if (mxPage == 1)
        {
            if (ImGui.ArrowButton("##back", ImGuiDir.Left)) mxPage = 0;
            ImGui.SameLine(); Ui.TextColored(Theme.Gold, "Options");
            ImGui.Spacing();
            ImGui.AlignTextToFramePadding(); Ui.Text("Format"); ImGui.SameLine(110); ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##mxfmt", ref mxFormat, ".ydr (drawable)\0.yft (physical / fragment)\0");
            ImGui.Checkbox("Collisions", ref mxCollisions);
            Theme.Tip("On: each model takes its collision (its Collision children, or the collision set on the model). Off: no collision at all.");
            ImGui.AlignTextToFramePadding(); Ui.Text("Folder"); ImGui.SameLine(110); ImGui.SetNextItemWidth(-70);
            ImGui.InputText("##mxdir", ref mxFolder, 260);
            ImGui.SameLine();
            if (ImGui.Button("Browse##mx", new Vector2(-1, 0)))
            {
                using var d = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = mxFolder, Description = "Folder for the exported models" };
                if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) mxFolder = d.SelectedPath;
                Input.ReleaseAllKeys();
            }
            ImGui.Spacing();
            Ui.TextDisabled("Textures are embedded in each model. Every model keeps its own pivot.");
            var ticked = roots.Where(r => mxChecked.Contains(r.Id)).ToList();
            string ext = mxFormat == 1 ? ".yft" : ".ydr";
            ImGui.BeginChild("##mxfiles", new Vector2(-1, -footer), ImGuiChildFlags.Borders);
            foreach (var r in ticked) Ui.Text(ModelFileName(r) + ext);
            ImGui.EndChild();
            ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X - 110 + ImGui.GetStyle().WindowPadding.X);
            ImGui.BeginDisabled(ticked.Count == 0 || string.IsNullOrWhiteSpace(mxFolder));
            if (ImGui.Button("Export", new Vector2(110, 0))) { StartModelExport(ticked); mxPage = 2; }
            ImGui.EndDisabled();
        }
        else
        {
            Ui.TextColored(Theme.Gold, busy ? "Exporting..." : "Done");
            Ui.TextDisabled(busy ? mxProgress : mxFolder);
            if (busy) ImGui.ProgressBar(mxDone / (float)Math.Max(1, mxChecked.Count), new Vector2(-1, 0));
            if (mxTask is { IsCompleted: true })
            {
                try { mxResult = mxTask.Result; } catch (Exception e) { mxResult = new() { "ERROR " + (e.InnerException?.Message ?? e.Message) }; }
                mxTask = null;
                SetStatus($"Exported {mxResult.Count(r => !r.StartsWith("ERROR"))} model(s) to {mxFolder}");
            }
            ImGui.BeginChild("##mxres", new Vector2(-1, -footer), ImGuiChildFlags.Borders);
            if (mxResult != null)
                foreach (var line in mxResult)
                    Ui.TextColored(line.StartsWith("ERROR") ? new Vector4(1f, 0.45f, 0.4f, 1) : new Vector4(0.55f, 0.9f, 0.6f, 1), line);
            ImGui.EndChild();
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("Open the folder", new Vector2(140, 0))) OpenUrl(mxFolder);
            ImGui.SameLine();
            if (ImGui.Button("Back", new Vector2(80, 0))) mxPage = 0;
            ImGui.SameLine();
            if (ImGui.Button("Close", new Vector2(80, 0))) mxOpen = false;
            ImGui.EndDisabled();
        }
        ImGui.End();
    }

    void StartModelExport(List<SceneObject> roots)
    {
        BakeAllPaint();
        var snapshot = Undo.SnapshotForSave(Scene);
        var items = roots.Select(r => (r.Id, File: ModelFileName(r))).ToList();
        var texFolders = renderer.Textures.SearchFolders.ToList();
        string ext = mxFormat == 1 ? ".yft" : ".ydr"; string folder = mxFolder; bool col = mxCollisions;
        mxResult = null; mxProgress = ""; mxDone = 0;
        mxTask = System.Threading.Tasks.Task.Run(() =>
        {
            var lines = new List<string>();
            Directory.CreateDirectory(folder);
            foreach (var (id, name) in items)
            {
                var o = snapshot.Find(id); if (o == null) continue;
                mxProgress = $"{mxDone + 1}/{items.Count}  {name}{ext}";
                try { lines.Add(GtaExporter.ExportModelFile(snapshot, new[] { o }, Path.Combine(folder, name + ext), LoadImageRgba, texFolders, col)); }
                catch (Exception e) { lines.Add($"ERROR {name}: {e.Message}"); }
                mxDone++;
            }
            return lines;
        });
    }
}
