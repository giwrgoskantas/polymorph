using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Export;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>
/// Send to server: exports the resource straight into the local FiveM server's resources folder and, with the
/// small molomax_bridge resource running there, restarts it in the live server ("ensure"), so the change shows in game.
/// </summary>
public sealed partial class EditorApp
{
    System.Threading.Tasks.Task<ExportReport> sendTask;
    string sendStatus;
    double sendStatusTime;

    public void SendToServer()
    {
        var dest = Settings.ServerResourcesFolder;
        if (string.IsNullOrWhiteSpace(dest) || !Directory.Exists(dest)) { SetStatus("Set your server's resources folder first (Edit > Preferences > GTA & FiveM)."); prefsOpen = true; prefsSection = 7; return; }
        if (sendTask is { IsCompleted: false }) return;
        BakeAllPaint();
        var snapshot = Undo.SnapshotForSave(Scene);
        var folders = renderer.Textures.SearchFolders.ToList();
        var tmp = Path.Combine(Path.GetTempPath(), "Polymorph", "send");
        sendStatus = "Exporting..."; sendStatusTime = ImGui.GetTime();
        sendTask = System.Threading.Tasks.Task.Run(() =>
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            var rep = GtaExporter.Export(snapshot, tmp, LoadImageRgba, folders, s => sendStatus = "Exporting: " + s);
            if (!rep.Ok) return rep;
            sendStatus = "Copying to the server...";
            var name = Path.GetFileName(rep.ResourceFolder.TrimEnd('\\', '/'));
            var target = Path.Combine(dest, name);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyDir(rep.ResourceFolder, target);
            rep.Info.Add("Copied to " + target);
            var url = Settings.ServerRestartUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                sendStatus = "Restarting " + name + " in the server...";
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    var full = $"{url.TrimEnd('/')}?res={Uri.EscapeDataString(name)}&key={Uri.EscapeDataString(Settings.ServerKey ?? "")}";
                    var answer = http.GetStringAsync(full).GetAwaiter().GetResult();
                    rep.Info.Add("Server: " + answer);
                }
                catch (Exception e) { rep.Warnings.Add("Server not reached (" + e.Message + "). Files are copied: type 'ensure " + name + "' in the server console."); }
            }
            else rep.Info.Add($"Type 'ensure {name}' in the server console (or install the bridge to do it automatically).");
            return rep;
        });
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    void PumpSend()
    {
        if (sendTask != null && sendTask.IsCompleted)
        {
            ExportReport rep;
            try { rep = sendTask.Result; } catch (Exception e) { rep = new ExportReport(); rep.Errors.Add(e.InnerException?.Message ?? e.Message); }
            sendTask = null;
            lastReport = rep;
            sendStatus = rep.Ok ? (rep.Warnings.Any(w => w.StartsWith("Server not reached")) ? "Copied to the server (restart it by hand)" : "Sent to the server") : "Send failed: " + string.Join(" ", rep.Errors);
            sendStatusTime = ImGui.GetTime();
            SetStatus(sendStatus);
        }
        if (sendStatus == null || (sendTask == null && ImGui.GetTime() - sendStatusTime > 6)) return;
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f, gpu.Height - statusH - 60), ImGuiCond.Always, new Vector2(0.5f, 1));
        ImGui.SetNextWindowBgAlpha(0.9f);
        ImGui.Begin("##sendstatus", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoMove);
        Ui.TextColored(Theme.Gold, "FiveM  "); ImGui.SameLine(); ImGui.TextUnformatted(sendStatus);
        ImGui.End();
    }

    /// <summary>Writes the molomax_bridge resource into the server and gives the server.cfg lines.</summary>
    public void InstallBridge()
    {
        var dest = Settings.ServerResourcesFolder;
        if (string.IsNullOrWhiteSpace(dest) || !Directory.Exists(dest)) { SetStatus("Set the server's resources folder first."); return; }
        if (string.IsNullOrEmpty(Settings.ServerKey)) Settings.ServerKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var dir = Path.Combine(dest, "molomax_bridge");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "fxmanifest.lua"), "fx_version 'cerulean'\ngame 'gta5'\nauthor 'Molo Modding'\ndescription 'Polymorph: restart a resource from the editor (Send to server)'\nserver_script 'server.lua'\n");
        File.WriteAllText(Path.Combine(dir, "server.lua"), BridgeLua);
        Settings.ServerRestartUrl = "http://127.0.0.1:30120/molomax_bridge/ensure";
        Settings.Save();
        bridgeCfg = $"ensure molomax_bridge\nset molomax_key \"{Settings.ServerKey}\"\nadd_ace resource.molomax_bridge command.ensure allow\nadd_ace resource.molomax_bridge command.refresh allow";
        try { System.Windows.Forms.Clipboard.SetText(bridgeCfg); } catch { }
        bridgeOpen = true;
    }

    string bridgeCfg; bool bridgeOpen;

    void DrawBridgeHelp()
    {
        if (!bridgeOpen) return;
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f, gpu.Height / 2f), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.Begin("FiveM bridge installed###bridge", ref bridgeOpen, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }
        ImGui.Text("molomax_bridge was written into your resources folder.");
        ImGui.Text("Add these lines to server.cfg (already copied to the clipboard), then restart the server once:");
        ImGui.InputTextMultiline("##cfg", ref bridgeCfg, 1000, new Vector2(520, 90), ImGuiInputTextFlags.ReadOnly);
        Ui.TextDisabled("After that, Send to server restarts your resource in the running server by itself.");
        if (ImGui.Button("OK", new Vector2(120, 0))) bridgeOpen = false;
        ImGui.End();
    }

    const string BridgeLua = @"-- MoloMax bridge: lets the MoloMax editor restart a resource after 'Send to server'.
-- Only answers on the server's own HTTP port, with the key set in server.cfg (set molomax_key ""..."").
local key = GetConvar('molomax_key', '')

SetHttpHandler(function(req, res)
    local path, query = req.path:match('^([^%?]*)%??(.*)$')
    if path ~= '/ensure' then res.writeHead(404); res.send('not found'); return end
    local q = {}
    for k, v in query:gmatch('([^&=]+)=([^&]*)') do q[k] = v end
    if key == '' or q.key ~= key then res.writeHead(403); res.send('bad key'); return end
    local name = q.res or ''
    if not name:match('^[%w_%-]+$') then res.writeHead(400); res.send('bad resource name'); return end
    ExecuteCommand('refresh')
    ExecuteCommand('ensure ' .. name)
    print(('[molomax] ensure %s'):format(name))
    res.send('restarted ' .. name)
end)
";
}
