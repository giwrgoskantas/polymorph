using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Scenes;
using MoloMax.Core.Tripo;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>The Tripo page of the Molo GTA panel: text / image / multiview to 3D, Smart UV, texture, retopo, straight into the scene.</summary>
public sealed partial class EditorApp
{
    readonly TripoOptions tripo = new();
    readonly List<TripoJob> tripoJobs = new();
    string tripoKeyInput = "";
    bool tripoKeyEditing;
    string tripoBalance;
    System.Threading.Tasks.Task<(decimal, decimal)> tripoBalanceTask;
    string tripoRetexPrompt = "";
    int tripoRetopoFaces = 5000;
    bool tripoRetopoQuad;
    readonly Dictionary<TripoJob, Vector3> tripoJobPlace = new();

    static string TripoFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Polymorph", "tripo");

    string TripoKey => SecretStore.Unprotect(Settings.TripoKey);

    TripoClient NewTripoClient() => new(TripoKey, Settings.TripoChina ? TripoClient.ChinaUrl : TripoClient.GlobalUrl);

    void HubTripo()
    {
        // ---- account
        bool hasKey = !string.IsNullOrEmpty(TripoKey);
        if (Ui.Header("Account", hasKey ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.DefaultOpen) || !hasKey || tripoKeyEditing)
        {
            if (!hasKey || tripoKeyEditing)
            {
                Ui.TextWrapped("Paste your Tripo API key (starts with tsk_, from the Tripo developer platform). It is stored encrypted for your Windows account.");
                ImGui.SetNextItemWidth(-70);
                ImGui.InputTextWithHint("##tkey", "tsk_...", ref tripoKeyInput, 200, ImGuiInputTextFlags.Password);
                ImGui.SameLine();
                if (ImGui.Button("Save", new Vector2(-1, 0)))
                {
                    var k = tripoKeyInput.Trim();
                    if (!k.StartsWith("tsk_")) SetStatus("A Tripo API key starts with tsk_.");
                    else { Settings.TripoKey = SecretStore.Protect(k); Settings.Save(); tripoKeyInput = ""; tripoKeyEditing = false; tripoBalance = null; SetStatus("Tripo key saved."); }
                }
                if (hasKey && ImGui.SmallButton("Cancel")) tripoKeyEditing = false;
            }
            else
            {
                Ui.TextDisabled("API key saved (encrypted).");
                ImGui.SameLine();
                if (ImGui.SmallButton("Change")) { tripoKeyEditing = true; tripoKeyInput = ""; }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) { Settings.TripoKey = null; Settings.Save(); tripoBalance = null; }
            }
            bool cn = Settings.TripoChina;
            if (ImGui.Checkbox("China server (tripo3d.com)", ref cn)) { Settings.TripoChina = cn; Settings.Save(); }
        }
        if (!hasKey) return;

        if (tripoBalanceTask != null && tripoBalanceTask.IsCompleted)
        {
            try { var (b, f) = tripoBalanceTask.Result; tripoBalance = $"{b:0.##} credits" + (f > 0 ? $" ({f:0.##} in use)" : ""); }
            catch (Exception e) { tripoBalance = (e.InnerException as TripoException)?.Friendly ?? e.InnerException?.Message ?? e.Message; }
            tripoBalanceTask = null;
        }
        if (ImGui.SmallButton("Balance")) tripoBalanceTask = System.Threading.Tasks.Task.Run(async () => { using var c = NewTripoClient(); return await c.GetBalance(); });
        ImGui.SameLine(); Ui.TextDisabled(tripoBalanceTask != null ? "..." : tripoBalance ?? "");

        // ---- input
        ImGui.Separator();
        int mode = (int)tripo.Input;
        ImGui.RadioButton("Text", ref mode, 0); ImGui.SameLine();
        ImGui.RadioButton("Image", ref mode, 1); ImGui.SameLine();
        ImGui.RadioButton("Multiview", ref mode, 2);
        tripo.Input = (TripoInput)mode;
        switch (tripo.Input)
        {
            case TripoInput.Text:
                ImGui.InputTextMultiline("##prompt", ref tripo.Prompt, 1024, new Vector2(-1, 60));
                if (tripo.Prompt.Length == 0) { var p = ImGui.GetItemRectMin(); ImGui.GetWindowDrawList().AddText(p + new Vector2(6, 4), ImGui.ColorConvertFloat4ToU32(Theme.TextDim), "Describe the object (in English works best)..."); }
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##neg", "Negative prompt (optional)", ref tripo.NegativePrompt, 255);
                break;
            case TripoInput.Image:
                PathField("Image", ref tripo.ImagePath, "Images|*.png;*.jpg;*.jpeg;*.webp");
                break;
            default:
                string[] names = { "Front", "Left", "Back", "Right" };
                for (int i = 0; i < 4; i++) PathField(names[i] + (i == 0 ? " *" : ""), ref tripo.Views[i], "Images|*.png;*.jpg;*.jpeg;*.webp");
                break;
        }

        // ---- options
        if (Ui.Header("Model options", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int mi = Array.FindIndex(TripoModels.All, m => m.Id == tripo.Model); if (mi < 0) mi = 0;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##tmodel", ref mi, TripoModels.All.Select(m => m.Label).ToArray(), TripoModels.All.Length)) tripo.Model = TripoModels.All[mi].Id;
            var model = TripoModels.All[mi];
            ImGui.SetNextItemWidth(130);
            ImGui.DragInt("Faces", ref tripo.FaceLimit, 50, 0, model.MaxFaces, tripo.FaceLimit <= 0 ? "adaptive" : "%d");
            foreach (var f in new[] { 2500, 5000, 10000 })
            {
                ImGui.SameLine();
                if (Theme.Toggle(f.ToString(), tripo.FaceLimit == f, new Vector2(48, 0))) tripo.FaceLimit = f;
            }
            ImGui.Checkbox("Texture", ref tripo.Texture); ImGui.SameLine();
            ImGui.BeginDisabled(!tripo.Texture);
            ImGui.Checkbox("PBR", ref tripo.Pbr); ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            int q = Array.IndexOf(new[] { "standard", "detailed", "extreme" }, tripo.TextureQuality); if (q < 0) q = 0;
            if (ImGui.Combo("##tq", ref q, new[] { "2K (standard)", "4K (detailed)", "8K (extreme)" }, 3)) tripo.TextureQuality = new[] { "standard", "detailed", "extreme" }[q];
            ImGui.EndDisabled();
            ImGui.Checkbox("Smart UV", ref tripo.SmartUv); Theme.Tip("Clean UV islands from Tripo (models up to 80k triangles).");
            ImGui.SameLine();
            ImGui.BeginDisabled(!tripo.Texture);
            ImGui.Checkbox("Texture after UV", ref tripo.TextureAfterUv); Theme.Tip("Geometry first, then Smart UV, then the texture on the new UVs (your pipeline).");
            ImGui.EndDisabled();
            ImGui.Checkbox("Real size (metres)", ref tripo.AutoSize);
            if (!TripoModels.IsP(tripo.Model)) { ImGui.SameLine(); ImGui.Checkbox("Smart low poly", ref tripo.SmartLowPoly); }
        }

        ImGui.Spacing();
        if (ImGui.Button("Generate", new Vector2(-1, 30))) StartTripo(tripo.Clone(), TripoTitle(tripo, tripo.FaceLimit));
        ImGui.PushStyleColor(ImGuiCol.Button, HubAccent * new Vector4(0.8f, 0.8f, 0.8f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, HubAccent);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.08f, 0.07f, 0.05f, 1));
        if (ImGui.Button("MOLO PIPELINE  P2 2500 + 5000 > Smart UV > 2K", new Vector2(-1, 34)))
        {
            foreach (var faces in new[] { 2500, 5000 })
            {
                var o = tripo.Clone();
                o.Model = "P2-20260801"; o.FaceLimit = faces; o.Texture = true; o.Pbr = true; o.TextureQuality = "standard";
                o.SmartUv = true; o.TextureAfterUv = true; o.AutoSize = true;
                StartTripo(o, TripoTitle(o, faces));
            }
        }
        ImGui.PopStyleColor(3);
        Theme.Tip("Two P2 models (2500 and 5000 faces), Smart UV, then a 2K texture. Both land side by side so you can pick one.");

        // ---- follow-ups on the selected Tripo model
        var cur = Current;
        if (cur?.TripoTaskId != null && Ui.Header($"Selected: {cur.Name}", ImGuiTreeNodeFlags.DefaultOpen))
        {
            float w2 = (ImGui.GetContentRegionAvail().X - 4) / 2;
            if (ImGui.Button("Smart UV", new Vector2(w2, 0))) StartTripoPost(cur, "Smart UV", "/v3/mesh/uv", TripoPipeline.SmartUvBody(cur.TripoTaskId));
            ImGui.SameLine();
            if (ImGui.Button("Retexture", new Vector2(-1, 0))) StartTripoPost(cur, "Retexture", "/v3/models/texture", TripoPipeline.TextureBody(cur.TripoTaskId, tripoRetexPrompt, tripo.TextureQuality, tripo.Pbr));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##retex", "Texture prompt (optional, e.g. rusty painted metal)", ref tripoRetexPrompt, 400);
            ImGui.SetNextItemWidth(110);
            ImGui.DragInt("##rtf", ref tripoRetopoFaces, 50, 500, 20000, "%d faces");
            ImGui.SameLine(); ImGui.Checkbox("Quad", ref tripoRetopoQuad);
            ImGui.SameLine();
            if (ImGui.Button("Retopo", new Vector2(-1, 0))) StartTripoPost(cur, "Retopo", "/v3/mesh/decimate", TripoPipeline.RetopoBody(cur.TripoTaskId, tripoRetopoFaces, tripoRetopoQuad, true));
            Theme.Tip("Smart low poly with Smart UV and the texture baked back.");
        }

        // ---- jobs
        if (tripoJobs.Count > 0)
        {
            ImGui.Separator();
            Ui.TextDisabled("Jobs");
            foreach (var j in tripoJobs.ToList())
            {
                ImGui.PushID(j.GetHashCode());
                Ui.Text(j.Title);
                float frac = j.Steps.Count == 0 ? 0 : (j.Step + j.Progress / 100f) / j.Steps.Count;
                if (j.Done && j.Error == null) frac = 1;
                ImGui.ProgressBar(frac, new Vector2(-60, 0), j.Done ? (j.Error == null ? "done" : "failed") : $"{j.Status} {j.Progress}%");
                ImGui.SameLine();
                if (!j.Done) { if (ImGui.SmallButton("Cancel")) j.Cts.Cancel(); }
                else if (ImGui.SmallButton("Clear")) tripoJobs.Remove(j);
                if (j.Error != null) { ImGui.PushTextWrapPos(); Ui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1), j.Error); ImGui.PopTextWrapPos(); }
                else if (j.Credits > 0) Ui.TextDisabled($"{j.Credits:0.##} credits");
                ImGui.PopID();
            }
        }
    }

    static string TripoTitle(TripoOptions o, int faces)
    {
        string name = o.Input switch
        {
            TripoInput.Text => o.Prompt,
            TripoInput.Image => Path.GetFileNameWithoutExtension(o.ImagePath ?? "image"),
            _ => Path.GetFileNameWithoutExtension(o.Views[0] ?? "views"),
        };
        name = TripoPipeline.Safe(string.Join("_", (name ?? "tripo").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4)));
        return faces > 0 ? $"{name}_{faces}" : name;
    }

    void PathField(string label, ref string path, string filter)
    {
        ImGui.AlignTextToFramePadding(); Ui.Text(label); ImGui.SameLine(70);
        string p = path ?? "";
        ImGui.SetNextItemWidth(-70);
        if (ImGui.InputText("##p" + label, ref p, 260)) path = p;
        ImGui.SameLine();
        if (ImGui.Button("Browse##" + label, new Vector2(-1, 0)))
        {
            using var d = new System.Windows.Forms.OpenFileDialog { Filter = filter + "|All files|*.*" };
            if (d.ShowDialog(form) == System.Windows.Forms.DialogResult.OK) path = d.FileName;
        }
    }

    void StartTripo(TripoOptions o, string title)
    {
        if (o.Input == TripoInput.Image && !File.Exists(o.ImagePath ?? "")) { SetStatus("Choose an image first."); return; }
        if (o.Input == TripoInput.Text && string.IsNullOrWhiteSpace(o.Prompt)) { SetStatus("Write a prompt first."); return; }
        var job = new TripoJob { Title = title };
        tripoJobs.Add(job);
        // variants of one run land side by side on the 3D cursor
        int running = tripoJobs.Count(j => !j.Imported && j != job);
        tripoJobPlace[job] = Cursor3D + new Vector3(running * 2.5f, 0, 0);
        System.Threading.Tasks.Task.Run(async () =>
        {
            try { using var c = NewTripoClient(); await TripoPipeline.Generate(c, job, o, TripoFolder); }
            catch (Exception e) { job.Error = (e as TripoException)?.Friendly ?? e.Message; job.Done = true; }
        });
    }

    void StartTripoPost(SceneObject src, string what, string route, System.Text.Json.Nodes.JsonObject body)
    {
        var job = new TripoJob { Title = what };
        tripoJobs.Add(job);
        var (mn, mx) = src.WorldBounds();
        tripoJobPlace[job] = new Vector3(mx.X + 1.5f + (mx.X - mn.X) * 0.5f, (mn.Y + mx.Y) / 2, mn.Z);
        System.Threading.Tasks.Task.Run(async () =>
        {
            try { using var c = NewTripoClient(); await TripoPipeline.PostProcess(c, job, route, body, TripoFolder); }
            catch (Exception e) { job.Error = (e as TripoException)?.Friendly ?? e.Message; job.Done = true; }
        });
        job.Title = $"{what} {src.Name}";
    }

    /// <summary>Called every frame: finished Tripo jobs are imported into the scene (on the UI thread).</summary>
    void PumpTripoJobs()
    {
        foreach (var j in tripoJobs)
        {
            if (!j.Done || j.Imported || j.Error != null || j.ResultFile == null) continue;
            j.Imported = true;
            var ext = Path.GetExtension(j.ResultFile).ToLowerInvariant();
            if (!MoloMax.Core.IO.ModelImporter.CanImport(j.ResultFile))
            {
                j.Error = $"Tripo returned a {ext} file ({j.ResultFile}); Polymorph imports GLB / glTF / OBJ.";
                continue;
            }
            var at = tripoJobPlace.TryGetValue(j, out var p) ? p : Cursor3D;
            var created = ImportFile(j.ResultFile, at);
            foreach (var o in created) { o.TripoTaskId = j.LastTaskId; o.Name = UniqueImportName(j.Title, o); }
            SetStatus($"Tripo: {j.Title} imported ({created.Sum(o => o.Mesh.Faces.Count):N0} polygons).");
        }
    }
}
