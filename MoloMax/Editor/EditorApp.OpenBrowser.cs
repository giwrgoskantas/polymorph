using System.Numerics;
using System.Windows.Forms;
using ImGuiNET;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>File > Open: Polymorph's own window with the bookmarks and recent scenes on the left, the folder on the right.</summary>
public sealed partial class EditorApp
{
    bool openWin;
    string openFolder, openSel, openPathEdit;
    List<string> openDirs = new(), openFiles = new();
    string openListedFor;

    public void OpenDialog()
    {
        openFolder ??= Settings.LastFolders.TryGetValue("Open scene", out var last) && Directory.Exists(last) ? last
            : CurrentFile != null ? Path.GetDirectoryName(CurrentFile)
            : Settings.Bookmarks.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        openSel = null; openListedFor = null; openWin = true;
    }

    /// <summary>The Windows file window (still there from the Open window).</summary>
    void OpenDialogWindows()
    {
        if (!ConfirmDiscard()) return;
        using var d = new OpenFileDialog { Filter = SceneFilter, Title = "Open scene", InitialDirectory = openFolder ?? "" };
        if (ShowFileDialog(d) == DialogResult.OK) OpenFile(d.FileName);
        Input.ReleaseAllKeys();
    }

    void OpenGo(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { SetStatus("Folder not found: " + folder); return; }
        openFolder = folder; openSel = null; openListedFor = null;
    }

    void OpenPick(string file)
    {
        if (!ConfirmDiscard()) return;
        openWin = false;
        try { Settings.LastFolders["Open scene"] = Path.GetDirectoryName(file); Settings.Save(); } catch { }
        OpenFile(file);
    }

    void DrawOpenBrowser()
    {
        if (!openWin) return;
        if (openListedFor != openFolder)
        {
            openListedFor = openFolder; openPathEdit = openFolder;
            try
            {
                openDirs = Directory.GetDirectories(openFolder).Where(d => (new DirectoryInfo(d).Attributes & FileAttributes.Hidden) == 0).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
                openFiles = Directory.GetFiles(openFolder, "*.mmx").OrderByDescending(File.GetLastWriteTime).ToList();
            }
            catch (Exception e) { openDirs = new(); openFiles = new(); SetStatus("Can't read " + openFolder + ": " + e.Message); }
        }
        ImGui.SetNextWindowSize(new Vector2(860, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f - 430, gpu.Height / 2f - 260), ImGuiCond.Appearing);
        if (!ImGui.Begin("Open scene###openscene", ref openWin, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }

        // left: bookmarks, places, recent scenes
        ImGui.BeginChild("##openleft", new Vector2(250, -34), ImGuiChildFlags.Borders);
        Ui.TextColored(Theme.Gold, "Bookmarks");
        string remove = null;
        foreach (var b in Settings.Bookmarks)
        {
            bool ok = Directory.Exists(b);
            if (!ok) ImGui.BeginDisabled();
            if (ImGui.Selectable(Path.GetFileName(b.TrimEnd('\\', '/')) + "##bm" + b, string.Equals(b, openFolder, StringComparison.OrdinalIgnoreCase))) OpenGo(b);
            if (!ok) ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(b + "\nRight-click: remove");
            if (ImGui.BeginPopupContextItem("##bmctx" + b)) { if (ImGui.MenuItem("Remove bookmark")) remove = b; ImGui.EndPopup(); }
        }
        if (remove != null) { Settings.Bookmarks.Remove(remove); Settings.Save(); }
        if (Settings.Bookmarks.Count == 0) Ui.TextDisabled("None yet: open a folder, then\n\"Bookmark this folder\".");
        bool marked = Settings.Bookmarks.Any(b => string.Equals(b, openFolder, StringComparison.OrdinalIgnoreCase));
        if (!marked && ImGui.Button("+ Bookmark this folder", new Vector2(-1, 0))) AddBookmark(openFolder);
        ImGui.Spacing();
        Ui.TextColored(Theme.Gold, "Places");
        foreach (var (name, path) in new[] { ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) })
            if (ImGui.Selectable(name + "##pl", string.Equals(path, openFolder, StringComparison.OrdinalIgnoreCase))) OpenGo(path);
        ImGui.Spacing();
        Ui.TextColored(Theme.Gold, "Recent scenes");
        foreach (var f in Settings.RecentFiles.Where(File.Exists).Take(10))
        {
            if (ImGui.Selectable(Path.GetFileName(f) + "##rc" + f)) OpenPick(f);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(f);
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // right: the folder
        ImGui.BeginGroup();
        if (ImGui.Button("Up", new Vector2(40, 0))) { var up = Path.GetDirectoryName(openFolder?.TrimEnd('\\', '/') ?? ""); if (!string.IsNullOrEmpty(up)) OpenGo(up); }
        ImGui.SameLine(); ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##openpath", ref openPathEdit, 500, ImGuiInputTextFlags.EnterReturnsTrue)) OpenGo(openPathEdit);
        ImGui.BeginChild("##openlist", new Vector2(-1, -34), ImGuiChildFlags.Borders);
        foreach (var d in openDirs)
        {
            if (ImGui.Selectable("[ ] " + Path.GetFileName(d) + "##d" + d, false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { OpenGo(d); break; }
            if (ImGui.IsItemClicked()) { }
        }
        string pick = null;
        foreach (var f in openFiles)
        {
            if (ImGui.Selectable(Path.GetFileNameWithoutExtension(f) + "##f" + f, openSel == f, ImGuiSelectableFlags.AllowDoubleClick))
            {
                openSel = f;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) pick = f;
            }
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 120);
            Ui.TextDisabled(File.GetLastWriteTime(f).ToString("dd/MM/yyyy HH:mm"));
        }
        if (openDirs.Count == 0 && openFiles.Count == 0) Ui.TextDisabled("No scene (.mmx) and no folder here.");
        ImGui.EndChild();
        ImGui.EndGroup();

        // bottom
        if (openSel == null) ImGui.BeginDisabled();
        if (ImGui.Button("Open", new Vector2(120, 26))) pick = openSel;
        if (openSel == null) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Windows window...", new Vector2(150, 26))) { openWin = false; OpenDialogWindows(); }
        Theme.Tip("The usual Windows file window (your bookmarks are on its left too).");
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(90, 26))) openWin = false;
        ImGui.SameLine(); Ui.TextDisabled("Double-click a folder to go in, a scene to open it.");
        ImGui.End();
        if (pick != null) OpenPick(pick);
    }
}
