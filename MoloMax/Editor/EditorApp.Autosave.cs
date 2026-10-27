using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using MoloMax.Core.Scenes;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Autosave every few minutes (in the background, from a cheap undo snapshot), a .bak of the previous save,
/// recent files, and recovery after a crash: a session marker is left while the app runs and removed on a clean exit.
/// </summary>
public sealed partial class EditorApp
{
    static string AutosaveDir => Path.Combine(AppSettings.DataDir, "autosave");
    static string SessionFile => Path.Combine(AutosaveDir, "session.json");
    sealed class SessionInfo { public string Autosave { get; set; } public string Original { get; set; } public DateTime Time { get; set; } }

    double lastAutosave = -1;
    System.Threading.Tasks.Task autosaveTask;
    SessionInfo recovery; bool recoveryChecked, recoveryOpen;

    void AutosaveFrame()
    {
        if (!recoveryChecked) { recoveryChecked = true; CheckRecovery(); WriteSession(null); }
        DrawRecovery();
        double now = ImGui.GetTime();
        if (lastAutosave < 0) lastAutosave = now;
        int min = Settings.AutosaveMinutes;
        if (MapMode) return; // the map is saved as .ymap files, not as a scene
        if (min <= 0 || !Dirty || now - lastAutosave < min * 60 || autosaveTask is { IsCompleted: false } || ModalActive) return;
        lastAutosave = now;
        var snap = Undo.SnapshotForSave(Scene);
        var name = CurrentFile != null ? Path.GetFileNameWithoutExtension(CurrentFile) : "untitled";
        var orig = CurrentFile;
        var path = Path.Combine(AutosaveDir, name + "_autosave.mmx");
        autosaveTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(AutosaveDir);
                var tmp = path + ".tmp";
                SceneIO.Save(snap, tmp);
                File.Move(tmp, path, true);
                WriteSession(new SessionInfo { Autosave = path, Original = orig, Time = DateTime.Now });
            }
            catch { }
        });
        SetStatus($"Autosaved ({DateTime.Now:HH:mm})");
    }

    void WriteSession(SessionInfo info)
    {
        try
        {
            Directory.CreateDirectory(AutosaveDir);
            info ??= new SessionInfo { Time = DateTime.Now };
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(info));
        }
        catch { }
    }

    /// <summary>Called on a clean exit: no recovery prompt next time.</summary>
    public void EndSession()
    {
        try { autosaveTask?.Wait(5000); if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { }
    }

    void CheckRecovery()
    {
        try
        {
            if (!File.Exists(SessionFile)) return;
            var info = JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(SessionFile));
            if (info?.Autosave != null && File.Exists(info.Autosave)) { recovery = info; recoveryOpen = true; }
        }
        catch { }
    }

    void DrawRecovery()
    {
        if (!recoveryOpen) return;
        ImGui.OpenPopup("Recover work?");
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f, gpu.Height / 2f), ImGuiCond.Always, new Vector2(0.5f));
        if (ImGui.BeginPopupModal("Recover work?", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.Text("Polymorph did not close properly last time.");
            Ui.TextDisabled($"Autosave from {recovery.Time:dd/MM HH:mm}" + (recovery.Original != null ? $" ({Path.GetFileName(recovery.Original)})" : ""));
            ImGui.Spacing();
            if (ImGui.Button("Restore the autosave", new Vector2(200, 30)))
            {
                OpenFile(recovery.Autosave);
                CurrentFile = recovery.Original; Dirty = true;   // saving goes back to the original file
                recoveryOpen = false; ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Start fresh", new Vector2(120, 30))) { recoveryOpen = false; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }

    /// <summary>Keeps the previous version of a scene as .mmx.bak before overwriting it.</summary>
    static void BackupBeforeSave(string path)
    {
        try { if (File.Exists(path)) File.Copy(path, path + ".bak", true); } catch { }
    }

    void AddRecent(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Settings.RecentFiles.Insert(0, path);
        if (Settings.RecentFiles.Count > 10) Settings.RecentFiles.RemoveRange(10, Settings.RecentFiles.Count - 10);
        Settings.Save();
    }

    void RecentMenu()
    {
        if (!ImGui.BeginMenu("Open Recent", Settings.RecentFiles.Count > 0)) return;
        var files = Settings.RecentFiles.ToList();
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i]; var name = Path.GetFileName(f);
            // two scenes with the same name in different folders: the folder shows (and each row has its own ID: the same
            // label twice made ImGui stop with a "conflicting ID" error over the menu)
            if (files.Count(x => string.Equals(Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase)) > 1)
                name += "  -  " + Path.GetFileName(Path.GetDirectoryName(f) ?? "");
            ImGui.PushID(i);
            if (ImGui.MenuItem(name, null, false, File.Exists(f))) { if (ConfirmDiscard()) OpenFile(f); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(f);
            ImGui.PopID();
        }
        ImGui.Separator();
        if (ImGui.MenuItem("Open autosave folder")) try { System.Diagnostics.Process.Start("explorer.exe", AutosaveDir); } catch { }
        ImGui.EndMenu();
    }
}
