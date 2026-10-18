using System.Diagnostics;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MoloMax.UI;

namespace MoloMax.Editor;

/// <summary>Python scripts and the MCP server: status of the API, the Claude config to copy, and a script runner with its output.</summary>
public sealed partial class EditorApp
{
    public static string PythonFolder => Path.Combine(AppContext.BaseDirectory, "python");
    static string McpScript => Path.Combine(PythonFolder, "polymorph_mcp.py");
    readonly StringBuilder scriptLog = new();
    Process scriptProc;
    bool scriptOpen;
    string lastScript;

    /// <summary>A Python 3 on this PC: the "py" launcher or python on the PATH.</summary>
    static string FindPython()
    {
        foreach (var exe in new[] { "py", "python", "python3" })
        {
            try
            {
                var psi = new ProcessStartInfo(exe, exe == "py" ? "-3 --version" : "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(4000) && p.ExitCode == 0) return exe;
            }
            catch { }
        }
        return null;
    }

    string McpConfig()
    {
        var py = (FindPython() ?? "python");
        var args = py == "py" ? new[] { "-3", McpScript.Replace('\\', '/') } : new[] { McpScript.Replace('\\', '/') };
        return System.Text.Json.JsonSerializer.Serialize(new { mcpServers = new { polymorph = new { command = py, args } } }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    void PrefsScripting()
    {
        Ui.TextColored(Theme.Gold, "Python API");
        bool on = Settings.ApiEnabled;
        if (ImGui.Checkbox("Scripting server on (127.0.0.1 only)", ref on))
        {
            Settings.ApiEnabled = on; Settings.Save();
            if (on) { if (!Api.Start(Settings.ApiPort, ApiInfoFile)) Api.Start(0, ApiInfoFile); } else Api.Stop();
        }
        Ui.TextDisabled(Api.Running ? $"Running on port {Api.Port} · {Api.Calls} call(s){(Api.LastMethod != null ? " · last: " + Api.LastMethod : "")}" : "Stopped" + (Api.Error != null ? ": " + Api.Error : ""));
        Ui.TextDisabled("Connection details (port + token) are in " + ApiInfoFile);
        ImGui.Spacing();
        Ui.Label("In Python:", Theme.TextDim);
        Ui.TextWrapped("import sys; sys.path.append(r\"" + PythonFolder + "\")\nimport polymorph as pm\npm.connect()\nbox = pm.create(\"box\", name=\"crate\", Width=1)\npm.unwrap(box); pm.export(\"C:/FiveM/resources\")");
        if (ImGui.Button("Run a Python script...", new Vector2(200, 0))) RunScriptDialog();
        ImGui.SameLine();
        if (ImGui.Button("Open the python folder", new Vector2(200, 0))) { try { Process.Start(new ProcessStartInfo("explorer.exe", PythonFolder) { UseShellExecute = true }); } catch { } }
        ImGui.Separator();
        Ui.TextColored(Theme.Gold, "MCP server (Claude)");
        Ui.TextWrapped("Lets Claude drive Polymorph: every API call becomes a tool (create objects, modifiers, UVs, GTA check, export, screenshots). Add this to claude_desktop_config.json (Claude Desktop > Settings > Developer > Edit Config), then restart Claude:");
        var cfg = McpConfig();
        ImGui.InputTextMultiline("##mcpcfg", ref cfg, 2000, new Vector2(-1, 110), ImGuiInputTextFlags.ReadOnly);
        if (ImGui.Button("Copy config", new Vector2(140, 0))) { ImGui.SetClipboardText(cfg); SetStatus("MCP config copied."); }
        if (FindPythonCached() == null) Ui.TextColored(new Vector4(1f, 0.6f, 0.25f, 1), "No Python 3 found on this PC: install it from python.org (tick \"Add to PATH\").");
    }
    string pyCache; bool pyChecked;
    string FindPythonCached() { if (!pyChecked) { pyCache = FindPython(); pyChecked = true; } return pyCache; }

    void RunScriptDialog()
    {
        using var d = new System.Windows.Forms.OpenFileDialog { Filter = "Python script (*.py)|*.py", Title = "Run a Python script in Polymorph", InitialDirectory = Path.Combine(PythonFolder, "examples") };
        if (d.ShowDialog(form) != System.Windows.Forms.DialogResult.OK) { Input.ReleaseAllKeys(); return; }
        Input.ReleaseAllKeys();
        RunScript(d.FileName);
    }

    public void RunScript(string file)
    {
        var py = FindPythonCached();
        if (py == null) { SetStatus("No Python 3 found (install it from python.org)."); return; }
        if (!Api.Running) { if (!Api.Start(Settings.ApiPort, ApiInfoFile)) Api.Start(0, ApiInfoFile); }
        try { scriptProc?.Kill(); } catch { }
        lastScript = file; scriptOpen = true;
        lock (scriptLog) { scriptLog.Clear(); scriptLog.AppendLine($"> {Path.GetFileName(file)}"); }
        var psi = new ProcessStartInfo(py, (py == "py" ? "-3 " : "") + "-u \"" + file + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(file) };
        psi.Environment["PYTHONPATH"] = PythonFolder;
        psi.Environment["POLYMORPH_PORT"] = Api.Port.ToString();
        psi.Environment["POLYMORPH_TOKEN"] = Api.Token;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        try
        {
            scriptProc = Process.Start(psi);
            scriptProc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (scriptLog) scriptLog.AppendLine(e.Data); };
            scriptProc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (scriptLog) scriptLog.AppendLine(e.Data); };
            scriptProc.EnableRaisingEvents = true;
            scriptProc.Exited += (_, _) => { lock (scriptLog) scriptLog.AppendLine($"(finished, exit code {scriptProc.ExitCode})"); };
            scriptProc.BeginOutputReadLine(); scriptProc.BeginErrorReadLine();
        }
        catch (Exception e) { lock (scriptLog) scriptLog.AppendLine("Could not start Python: " + e.Message); }
    }

    void DrawScriptConsole()
    {
        if (!scriptOpen) return;
        ImGui.SetNextWindowSize(new Vector2(560, 300), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Python###pyconsole", ref scriptOpen, ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }
        bool running = scriptProc != null && !scriptProc.HasExited;
        if (ImGui.Button(running ? "Stop" : "Run again", new Vector2(90, 0))) { if (running) { try { scriptProc.Kill(); } catch { } } else if (lastScript != null) RunScript(lastScript); }
        ImGui.SameLine(); Ui.TextDisabled(lastScript ?? "");
        ImGui.BeginChild("##pylog", Vector2.Zero, ImGuiChildFlags.Borders);
        if (Ui.HasLabelFont) ImGui.PushFont(Ui.LabelFont);
        string text; lock (scriptLog) text = scriptLog.ToString();
        ImGui.TextUnformatted(text);
        if (Ui.HasLabelFont) ImGui.PopFont();
        if (running) ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
        ImGui.End();
    }
}
