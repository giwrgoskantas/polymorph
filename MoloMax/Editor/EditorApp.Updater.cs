using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ImGuiNET;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Updates in one click. At start (once a day) the website's version.json is read; a newer version shows a green
/// "Update" button in the menu bar. One click downloads the installer, checks it, closes Polymorph, installs over the
/// current copy (settings, autosaves and the library live in %APPDATA%\Polymorph and stay) and starts the new version.
/// </summary>
public sealed partial class EditorApp
{
    public const string UpdateFeedUrl = "https://molossen.github.io/polymorph/version.json";
    /// <summary>The feed (POLYMORPH_UPDATE_FEED points a test copy at a test feed).</summary>
    static string UpdateFeed => Environment.GetEnvironmentVariable("POLYMORPH_UPDATE_FEED") is { Length: > 0 } f ? f : UpdateFeedUrl;

    sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string Setup { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public string Notes { get; set; }
    }

    UpdateInfo updateAvail;
    string updateState;          // null idle, "checking", "downloading", "ready", "error: ..."
    float updateProgress;
    string updateFile;
    bool updateOpen, updateCheckedByHand;
    /// <summary>Polymorph is closing to let the installer replace it: no "save changes?" a second time.</summary>
    public bool ClosingForUpdate;

    /// <summary>"1.2.10" &gt; "1.2.9"; anything unreadable counts as 0.</summary>
    public static int CompareVersions(string a, string b)
    {
        int[] P(string s) => (s ?? "0").Trim().TrimStart('v', 'V').Split('.', '-', '+').Take(4).Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var x = P(a); var y = P(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int u = i < x.Length ? x[i] : 0, v = i < y.Length ? y[i] : 0;
            if (u != v) return u.CompareTo(v);
        }
        return 0;
    }

    /// <summary>At start: once a day at most, in the background (never in the self test).</summary>
    void AutoCheckUpdates()
    {
        if (SelfTestDir != null || !Settings.CheckUpdates) return;
        // the feed is a few hundred bytes: read it at every start, so what is offered is always what the site has now
        CheckUpdates(byHand: false);
    }

    /// <summary>A version found earlier today is still offered after a restart (no new download of the feed).</summary>
    void RestoreKnownUpdate()
    {
        if (string.IsNullOrEmpty(Settings.KnownUpdateJson)) return;
        try
        {
            var u = JsonSerializer.Deserialize<UpdateInfo>(Settings.KnownUpdateJson);
            if (u != null && CompareVersions(u.Version, AppInfo.Version) > 0 && u.Version != Settings.SkippedVersion) updateAvail = u;
        }
        catch { }
    }

    void CheckUpdates(bool byHand)
    {
        if (updateState is "checking" or "downloading") return;
        updateState = "checking"; updateCheckedByHand = byHand;
        if (byHand) updateOpen = true;
        Task.Run(async () =>
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Polymorph/" + AppInfo.Version);
                var json = await http.GetStringAsync(UpdateFeed + "?t=" + DateTime.UtcNow.Ticks);
                var u = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                form.BeginInvoke(new Action(() =>
                {
                    Settings.LastUpdateCheck = DateTime.UtcNow;
                    bool newer = u != null && !string.IsNullOrEmpty(u.Setup) && CompareVersions(u.Version, AppInfo.Version) > 0;
                    // skipped by the user: only a manual check offers it again
                    updateAvail = newer && (byHand || u.Version != Settings.SkippedVersion) ? u : null;
                    Settings.KnownUpdateJson = newer ? JsonSerializer.Serialize(u) : null;
                    Settings.Save();
                    updateState = null;
                    if (updateAvail != null && byHand) updateOpen = true;
                }));
            }
            catch (Exception e)
            {
                form.BeginInvoke(new Action(() => updateState = byHand ? "error: the website could not be reached (" + e.Message + ")" : null));
            }
        });
    }

    void DownloadUpdate()
    {
        var u = updateAvail; if (u == null || updateState is "downloading") return;
        updateState = "downloading"; updateProgress = 0;
        Task.Run(async () =>
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Polymorph/" + AppInfo.Version);
                // read the feed again right before downloading: the window may have been open a while, a newer build may be out
                try
                {
                    var fresh = JsonSerializer.Deserialize<UpdateInfo>(await http.GetStringAsync(UpdateFeed + "?t=" + DateTime.UtcNow.Ticks),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (fresh != null && !string.IsNullOrEmpty(fresh.Setup) && CompareVersions(fresh.Version, AppInfo.Version) > 0)
                    { u = fresh; form.BeginInvoke(new Action(() => updateAvail = fresh)); }
                }
                catch { }
                var dir = Path.Combine(Path.GetTempPath(), "PolymorphUpdate"); Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"Polymorph-Setup-{u.Version}.exe");
                using var resp = await http.GetAsync(u.Setup, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? u.Size;
                await using (var src = await resp.Content.ReadAsStreamAsync())
                await using (var dst = File.Create(file))
                {
                    var buf = new byte[1 << 16]; long got = 0; int n;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n)); got += n;
                        if (total > 0) updateProgress = (float)(got / (double)total);
                    }
                }
                if (!string.IsNullOrEmpty(u.Sha256))
                {
                    await using var f = File.OpenRead(file);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(f));
                    if (!hash.Equals(u.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)) throw new Exception("the download is damaged (checksum), try again");
                }
                form.BeginInvoke(new Action(() => { updateFile = file; updateState = "ready"; updateProgress = 1; }));
            }
            catch (Exception e) { form.BeginInvoke(new Action(() => updateState = "error: " + e.Message)); }
        });
    }

    /// <summary>The folder Polymorph is installed in (the installer's uninstaller sits next to it), or null for an unzipped copy.</summary>
    static string InstalledDir()
    {
        var d = AppContext.BaseDirectory.TrimEnd('\\', '/');
        return File.Exists(Path.Combine(d, "Uninstall Polymorph.exe")) && d.EndsWith("Polymorph", StringComparison.OrdinalIgnoreCase) ? d : null;
    }

    void InstallUpdate()
    {
        if (updateFile == null || !File.Exists(updateFile)) { updateState = "error: the installer is missing, download it again"; return; }
        if (!ConfirmDiscard()) return; // the scene is saved (or knowingly dropped) first
        // silent install over this copy, then the installer starts the new version; an unzipped copy installs normally
        var dir = InstalledDir();
        string args = "/S /RELAUNCH" + (dir != null ? " /D=" + dir : "");
        try
        {
            Process.Start(new ProcessStartInfo(updateFile, args) { UseShellExecute = true });
        }
        catch (Exception e) { updateState = "error: " + e.Message; return; }
        Settings.Save();
        ClosingForUpdate = true;
        form.BeginInvoke(new Action(form.Close));
    }

    /// <summary>Menu bar, right side: a green button while an update waits.</summary>
    void UpdateMenuButton()
    {
        if (updateAvail == null) return;
        string label = updateState == "downloading" ? $"Downloading {updateProgress * 100:0}%" : updateState == "ready" ? "Restart to update" : $"Update {updateAvail.Version}";
        float w = ImGui.CalcTextSize(label).X + 24;
        ImGui.SameLine(ImGui.GetWindowWidth() - w - 12);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.13f, 0.55f, 0.32f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.17f, 0.66f, 0.39f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 1, 1, 1));
        if (ImGui.Button(label + "##upd", new Vector2(w, 0))) updateOpen = true;
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Polymorph {updateAvail.Version} is out (you have {AppInfo.Version}). Your settings are kept.");
        TutRect("update_btn");
    }

    void DrawUpdateWindow()
    {
        if (updateOpen) { ImGui.OpenPopup("Update Polymorph"); updateOpen = false; }
        bool open = true;
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Update Polymorph", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;
        var u = updateAvail;
        if (updateState == "checking") Ui.Label("Looking for a new version...", Theme.TextDim);
        else if (updateState?.StartsWith("error") == true && u == null) Ui.TextWrapped("Could not check: " + updateState[7..]);
        else if (u == null) { Ui.Label($"Polymorph {AppInfo.Version} is up to date.", Theme.Gold); }
        else
        {
            ImGui.PushFont(Ui.BrandFont); ImGui.TextUnformatted($"Polymorph {u.Version}"); ImGui.PopFont();
            Ui.Label($"You have {AppInfo.Version}." + (u.Size > 0 ? $"  Download: {u.Size / 1048576.0:0} MB" : ""), Theme.TextDim);
            if (!string.IsNullOrWhiteSpace(u.Notes))
            {
                ImGui.Spacing(); Ui.Label("What's new", Theme.Gold);
                ImGui.PushTextWrapPos(440); ImGui.TextUnformatted(u.Notes); ImGui.PopTextWrapPos();
            }
            ImGui.Spacing();
            Ui.TextDisabled("Your settings, keys, recent files and texture library are kept.");
            Ui.TextDisabled("Polymorph closes, installs the update and opens again by itself.");
            ImGui.Spacing();
            if (updateState == "downloading")
                ImGui.ProgressBar(updateProgress, new Vector2(-1, 0), $"Downloading... {updateProgress * 100:0}%");
            else if (updateState?.StartsWith("error") == true)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.45f, 0.4f, 1)); ImGui.PushTextWrapPos(440);
                ImGui.TextUnformatted("Problem: " + updateState[7..]); ImGui.PopTextWrapPos(); ImGui.PopStyleColor();
            }
            ImGui.Spacing();
            if (updateState == "ready")
            {
                if (ImGui.Button("Install and restart", new Vector2(200, 0))) InstallUpdate();
            }
            else if (updateState != "downloading")
            {
                // one click: download, then install and restart by itself
                if (ImGui.Button("Update now", new Vector2(200, 0))) { updateInstallWhenReady = true; DownloadUpdate(); }
            }
            ImGui.SameLine();
            bool later = ImGui.Button("Later", new Vector2(100, 0)); TutRect("upd_later");
            if (later) ImGui.CloseCurrentPopup();
            ImGui.SameLine();
            if (updateState != "downloading" && ImGui.Button("Skip this version", new Vector2(130, 0)))
            {
                Settings.SkippedVersion = u.Version; Settings.Save(); updateAvail = null; ImGui.CloseCurrentPopup();
            }
        }
        ImGui.Separator();
        bool auto = Settings.CheckUpdates;
        if (ImGui.Checkbox("Check for updates when Polymorph starts", ref auto)) { Settings.CheckUpdates = auto; Settings.Save(); }
        if (u == null && updateState != "checking" && ImGui.Button("Close", new Vector2(120, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    bool updateInstallWhenReady, updateStarted;

    /// <summary>Once a frame: the download finished after "Update now": install right away.</summary>
    void UpdateTick()
    {
        if (updateInstallWhenReady && updateState == "ready") { updateInstallWhenReady = false; InstallUpdate(); }
        if (updateState?.StartsWith("error") == true) updateInstallWhenReady = false;
    }
}
