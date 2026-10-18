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
            var hb = -1; var watch = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(240_000);
                    if (Editor.EditorApp.SelfTestHeartbeat == hb) { File.AppendAllText(Path.Combine(dir, "report.txt"), "\nHUNG: no frame for 4 minutes (a dialog or a deadlock)\n"); Environment.Exit(4); }
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
