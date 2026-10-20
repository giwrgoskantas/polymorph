using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Anim;
using MoloMax.Core.Gta.Anim;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>GTA clip dictionaries (.ycd) in and out of the timeline.</summary>
public sealed partial class EditorApp
{
    List<YcdClip> ycdClips; string ycdFile; bool ycdWindow;

    public void ImportYcdDialog()
    {
        using var d = new System.Windows.Forms.OpenFileDialog { Filter = "GTA clip dictionary (*.ycd)|*.ycd", Title = "Import a GTA animation (.ycd)" };
        var ok = ShowFileDialog(d) == System.Windows.Forms.DialogResult.OK;
        Input.ReleaseAllKeys();
        if (ok) ImportYcd(d.FileName);
    }

    /// <summary>Reads a .ycd: UV clips go on the models they belong to (by name / clip hash), the others wait in a list to be put on an object.</summary>
    public int ImportYcd(string path)
    {
        try { ycdClips = YcdIO.Read(File.ReadAllBytes(path), Path.GetFileName(path)); }
        catch (Exception e) { SetStatus("Could not read " + Path.GetFileName(path) + ": " + e.Message); return 0; }
        ycdFile = Path.GetFileName(path);
        TimelineOpen = true; ycdWindow = true;
        Undo.Record("Import .ycd", Scene);
        int applied = 0;
        foreach (var c in ycdClips.Where(c => c.IsUv))
        {
            var o = Scene.Objects.FirstOrDefault(o => o.Id >= 0 && o.Mesh != null && MatchesUvClip(o, c));
            if (o != null) { AnimGta.ApplyClip(Anim, o, c, Anim.Start); applied++; }
        }
        // a single object animation and one selected object: straight on it
        var moves = ycdClips.Where(c => !c.IsUv).ToList();
        if (moves.Count == 1 && Current is { } cur && cur.Id >= 0) { AnimGta.ApplyClip(Anim, cur, moves[0], Anim.Start); applied++; }
        else foreach (var c in moves)
        {
            var o = Scene.Objects.FirstOrDefault(o => o.Id >= 0 && c.Name.StartsWith(AnimGta.ModelName(o), StringComparison.OrdinalIgnoreCase));
            if (o != null) { AnimGta.ApplyClip(Anim, o, c, Anim.Start); applied++; }
        }
        animShownFrame = float.NaN; Dirty = true;
        int bones = ycdClips.Count(c => c.Tracks.Any(t => t.Bone != 0));
        SetStatus($"{ycdFile}: {ycdClips.Count} clip(s), {applied} put on objects" + (bones > 0 ? $", {bones} with a skeleton (ped / rig)" : "") + ". The list shows them all.");
        return applied;
    }

    bool MatchesUvClip(SceneObject o, YcdClip c)
    {
        var model = AnimGta.ModelName(o).ToLowerInvariant();
        if (c.Name.StartsWith(model + "_uv", StringComparison.OrdinalIgnoreCase)) return true;
        int shaders = Math.Max(1, UsedMaterials(o).Count);
        for (int i = 0; i < shaders; i++) if (MoloMax.Core.Gta.Export.AnimXml.ClipHash(model, i) == c.Hash) return true;
        return false;
    }

    void DrawYcdWindow()
    {
        if (!ycdWindow || ycdClips == null) return;
        ImGui.SetNextWindowSize(new Vector2(460, 320), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f - 230, gpu.Height / 2f - 200), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"Animations in {ycdFile}###ycdwin", ref ycdWindow, ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }
        Ui.TextDisabled(Current != null ? $"Selected: {Current.Name}" : "Select an object to put a clip on it.");
        ImGui.Separator();
        for (int i = 0; i < ycdClips.Count; i++)
        {
            var c = ycdClips[i];
            ImGui.PushID(i);
            ImGui.TextUnformatted(AnimGta.Describe(c));
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 110 + ImGui.GetCursorPosX() - ImGui.GetCursorPosX());
            bool can = Current != null && Current.Id >= 0;
            if (!can) ImGui.BeginDisabled();
            if (ImGui.SmallButton("Put on selected"))
            {
                Undo.Record("Apply clip", Scene);
                int k = AnimGta.ApplyClip(Anim, Current, c, CurrentFrame);
                animShownFrame = float.NaN; Dirty = true;
                SetStatus($"{c.Name} on {Current.Name}: {k} keys from frame {CurrentFrame}");
            }
            if (!can) ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.End();
    }

    public void ExportYcdDialog()
    {
        var clips = AnimGta.ToClips(Scene, Selected.Any(id => Anim.Track(id) != null) ? Selected.Where(id => Anim.Track(id) != null) : null);
        if (clips.Count == 0) { SetStatus("Nothing animated to export: key an object (I) or its UVs first."); return; }
        var name = (Scene.Gta?.ResourceName ?? "molo") + "_anims.ycd";
        using var d = new System.Windows.Forms.SaveFileDialog { Filter = "GTA clip dictionary (*.ycd)|*.ycd", Title = "Export the animation (.ycd)", FileName = name };
        var ok = ShowFileDialog(d) == System.Windows.Forms.DialogResult.OK;
        Input.ReleaseAllKeys();
        if (ok) ExportYcd(d.FileName, clips);
    }

    public bool ExportYcd(string path, List<YcdClip> clips = null)
    {
        clips ??= AnimGta.ToClips(Scene);
        try
        {
            File.WriteAllBytes(path, YcdIO.Write(clips));
            SetStatus($"{Path.GetFileName(path)}: {clips.Count} clip(s) at {Anim.Fps:0} fps ({string.Join(", ", clips.Select(c => c.Name).Take(4))})");
            return true;
        }
        catch (Exception e) { SetStatus("Export failed: " + e.Message); return false; }
    }
}
