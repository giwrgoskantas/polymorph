using System.Windows.Forms;

namespace MoloMax;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // French Windows groups thousands with a narrow no-break space the UI font doesn't have ("1?295?411"):
        // keep the user's culture but group digits with a plain space
        var ci = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
        ci.NumberFormat.NumberGroupSeparator = " "; ci.NumberFormat.CurrencyGroupSeparator = " "; ci.NumberFormat.PercentGroupSeparator = " ";
        System.Globalization.CultureInfo.CurrentCulture = ci; System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ci;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        string start = args.Length > 0 ? args[0] : null;
        if (args.Length > 1 && args[0] == "--selftest")
        {
            // scripted test of the real editor: settings / autosave in the test folder, screenshots + report, then quit
            var dir = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(dir);
            Editor.EditorApp.SelfTestDir = dir;
            Editor.EditorApp.SelfTestArgs = args.Skip(2).ToArray();
            Editor.AppSettings.DataDir = Path.Combine(dir, "appdata");
            try { UI.Splash.Snapshot(Path.Combine(dir, "shots", "00_splash.png")); } catch { }
            start = null;
            // a fake "did not close properly" session with an autosave, so the recovery prompt is tested too
            var autoDir = Path.Combine(Editor.AppSettings.DataDir, "autosave");
            Directory.CreateDirectory(autoDir);
            var sc = new Core.Scenes.Scene();
            sc.Add("recovered_box", Core.Mesh.Primitives.Box(1, 1, 1), System.Numerics.Vector3.Zero);
            var auto = Path.Combine(autoDir, "untitled_autosave.mmx");
            Core.Scenes.SceneIO.Save(sc, auto);
            File.WriteAllText(Path.Combine(autoDir, "session.json"), System.Text.Json.JsonSerializer.Serialize(new { Autosave = auto, Original = (string)null, Time = DateTime.Now }));
            uint mainThreadId = GetCurrentThreadId();
            var hb = -1; var watch = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(240_000);
                    if (Editor.EditorApp.SelfTestHeartbeat == hb) { File.AppendAllText(Path.Combine(dir, "report.txt"), "\nHUNG: no frame for 4 minutes (a dialog or a deadlock)\n" + HangInfo(mainThreadId) + "\n"); Environment.Exit(4); }
                    hb = Editor.EditorApp.SelfTestHeartbeat;
                }
            }) { IsBackground = true };
            watch.Start();
        }
        if (Editor.EditorApp.SelfTestDir == null) { MigrateOldData(); UI.Splash.Begin(); }
        try
        {
            using var form = new MainForm(start);
            form.Run();
        }
        catch (Exception e) { Crash(e); }
    }

    // ---- what the UI thread is stuck in, when a self test hangs (a menu or move loop, a dialog window...)
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct GuiThreadInfo { public int cbSize; public uint flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public int l, t, r, b; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, EnumProc f, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    static string HangInfo(uint thread)
    {
        try
        {
            var gi = new GuiThreadInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GuiThreadInfo>() };
            var sb = new System.Text.StringBuilder();
            if (GetGUIThreadInfo(thread, ref gi))
            {
                var f = new List<string>();
                if ((gi.flags & 0x1) != 0) f.Add("caret blinking"); if ((gi.flags & 0x2) != 0) f.Add("IN MOVE/SIZE LOOP");
                if ((gi.flags & 0x4) != 0) f.Add("IN MENU MODE"); if ((gi.flags & 0x8) != 0) f.Add("IN SYSTEM MENU"); if ((gi.flags & 0x10) != 0) f.Add("IN POPUP MENU");
                sb.AppendLine("UI thread: " + (f.Count > 0 ? string.Join(", ", f) : "no menu / move loop") + $", capture {gi.hwndCapture}, active {gi.hwndActive}");
            }
            EnumThreadWindows(thread, (h, _) =>
            {
                var t = new System.Text.StringBuilder(256); var c = new System.Text.StringBuilder(256);
                GetWindowText(h, t, 256); GetClassName(h, c, 256);
                if (t.Length > 0 || IsWindowVisible(h)) sb.AppendLine($"window: {c} '{t}' visible {IsWindowVisible(h)} minimised {IsIconic(h)}");
                return true;
            }, IntPtr.Zero);
            sb.AppendLine("last frame stage: " + Editor.EditorApp.FrameStage);
            return sb.ToString();
        }
        catch (Exception e) { return "hang info failed: " + e.Message; }
    }

    /// <summary>Polymorph was called MoloMax: settings, autosave, add-ons and extracted game files are copied to the new folders once.</summary>
    static void MigrateOldData()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.MyDocuments })
        {
            try
            {
                var b = Environment.GetFolderPath(root);
                string old = Path.Combine(b, "MoloMax"), now = Path.Combine(b, "Polymorph");
                if (!Directory.Exists(old) || File.Exists(Path.Combine(now, ".from_molomax"))) continue;
                // copy (the old version may still be open, and stays usable)
                foreach (var f in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var dst = Path.Combine(now, Path.GetRelativePath(old, f));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(f, dst, false);
                    }
                    catch { }
                }
                // a crash marker of the old version is not ours
                try { File.Delete(Path.Combine(now, "autosave", "session.json")); } catch { }
                try { File.WriteAllText(Path.Combine(now, ".from_molomax"), DateTime.Now.ToString("s")); } catch { }
            }
            catch { }
        }
    }

    static void Crash(Exception e)
    {
        UI.Splash.End();
        if (Editor.EditorApp.SelfTestDir != null)
        {
            try { File.AppendAllText(Path.Combine(Editor.EditorApp.SelfTestDir, "report.txt"), "\nCRASH: " + e); } catch { }
            Environment.Exit(3);
        }
        try
        {
            var log = Path.Combine(AppContext.BaseDirectory, "molomax_crash.txt");
            File.WriteAllText(log, e?.ToString() ?? "unknown error");
            MessageBox.Show($"Polymorph stopped because of an error:\n\n{e?.Message}\n\nDetails were written to:\n{log}", "Polymorph", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
