using System.Windows.Forms;
using ImGuiNET;

namespace MoloMax.Editor;

/// <summary>
/// Bookmarked folders: pinned on the left of every open / save / import window, with the folders of the recent files,
/// and each window opens again in the folder it was last used in.
/// </summary>
public sealed partial class EditorApp
{
    /// <summary>Shows a file window with the bookmarks; remembers its folder when the user picks a file.</summary>
    DialogResult ShowFileDialog(FileDialog d)
    {
        string kind = string.IsNullOrEmpty(d.Title) ? d.Filter ?? "file" : d.Title;
        try
        {
            d.AutoUpgradeEnabled = true;
            var places = Settings.Bookmarks.Where(Directory.Exists)
                .Concat(Settings.RecentFiles.Select(f => { try { return Path.GetDirectoryName(f); } catch { return null; } }).Where(p => p != null && Directory.Exists(p)).Take(4))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(16);
            foreach (var p in places) d.CustomPlaces.Add(p);
            if (string.IsNullOrEmpty(d.InitialDirectory) && Settings.LastFolders.TryGetValue(kind, out var last) && Directory.Exists(last)) d.InitialDirectory = last;
        }
        catch { }
        var r = d.ShowDialog(form);
        if (r == DialogResult.OK && !string.IsNullOrEmpty(d.FileName))
        {
            try { Settings.LastFolders[kind] = Path.GetDirectoryName(d.FileName); Settings.Save(); } catch { }
        }
        Input.ReleaseAllKeys();
        return r;
    }

    public void AddBookmark(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        if (Settings.Bookmarks.Any(b => string.Equals(b, folder, StringComparison.OrdinalIgnoreCase))) { SetStatus("Already bookmarked: " + folder); return; }
        Settings.Bookmarks.Add(folder); Settings.Save();
        SetStatus("Bookmarked: " + folder + " (shown on the left of the open / save windows)");
    }

    /// <summary>File menu: the bookmarked folders.</summary>
    void BookmarksMenu()
    {
        if (!ImGui.BeginMenu("Bookmarks")) return;
        if (ImGui.MenuItem("Bookmark a folder..."))
        {
            using var d = new FolderBrowserDialog { Description = "Folder to pin in the open / save windows", UseDescriptionForTitle = true };
            if (d.ShowDialog(form) == DialogResult.OK) AddBookmark(d.SelectedPath);
            Input.ReleaseAllKeys();
        }
        if (CurrentFile != null && ImGui.MenuItem("Bookmark this scene's folder")) AddBookmark(Path.GetDirectoryName(CurrentFile));
        if (Settings.Bookmarks.Count > 0) ImGui.Separator();
        foreach (var b in Settings.Bookmarks.ToList())
        {
            if (!ImGui.BeginMenu((Directory.Exists(b) ? "" : "(missing) ") + b + "##bm" + b)) continue;
            if (ImGui.MenuItem("Open a scene from here..."))
            {
                if (ConfirmDiscard())
                {
                    using var d = new OpenFileDialog { Filter = SceneFilter, Title = "Open scene", InitialDirectory = b };
                    if (ShowFileDialog(d) == DialogResult.OK) OpenFile(d.FileName);
                }
            }
            if (ImGui.MenuItem("Import from here..."))
            {
                Settings.LastFolder = b; ImportDialog();
            }
            if (ImGui.MenuItem("Show in Explorer")) { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + b + "\""); } catch { } }
            if (ImGui.MenuItem("Remove bookmark")) { Settings.Bookmarks.Remove(b); Settings.Save(); }
            ImGui.EndMenu();
        }
        if (Settings.Bookmarks.Count == 0) ImGui.TextDisabled("No bookmark yet.");
        ImGui.EndMenu();
    }
}
