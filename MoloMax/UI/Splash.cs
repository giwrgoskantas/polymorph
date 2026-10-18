using System.Drawing;
using System.Windows.Forms;

namespace MoloMax.UI;

/// <summary>Start-up splash: the Polymorph artwork with an orange loading bar and what is loading.</summary>
public sealed class Splash : Form
{
    static Splash current;
    readonly Image art;
    float progress; string step = "Starting";
    static readonly Color Bg = Color.FromArgb(0x12, 0x14, 0x18), Track = Color.FromArgb(0x1B, 0x1E, 0x24),
        Orange = Color.FromArgb(0xF2, 0x6B, 0x1D), Dim = Color.FromArgb(0x9A, 0xA3, 0xAE);
    Font mono;

    Splash(Image art)
    {
        this.art = art;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true;
        BackColor = Bg; ClientSize = art.Size; Text = "Polymorph"; TopMost = true;
        DoubleBuffered = true;
        try { Icon = AppIcon(); } catch { }
        try
        {
            var pfc = new System.Drawing.Text.PrivateFontCollection();
            pfc.AddFontFile(Asset("IBMPlexMono-Regular.ttf"));
            mono = new Font(pfc.Families[0], 9f);
        }
        catch { mono = new Font("Consolas", 9f); }
    }

    public static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);
    public static Icon AppIcon() => new(Asset("polymorph.ico"));

    public static void Begin()
    {
        try
        {
            var f = Asset("splash.png");
            if (!File.Exists(f)) return;
            current = new Splash(Image.FromFile(f));
            current.Show();
            current.Refresh();
            Application.DoEvents();
        }
        catch { current = null; }
    }

    /// <summary>Moves the bar (0..1) and names the current step.</summary>
    public static void Step(string what, float at)
    {
        if (current == null) return;
        current.step = what; current.progress = Math.Clamp(at, 0, 1);
        current.Refresh();
        Application.DoEvents();
    }

    /// <summary>Self test: the splash as it looks halfway, saved as a PNG.</summary>
    public static void Snapshot(string path)
    {
        var f = Asset("splash.png"); if (!File.Exists(f)) return;
        using var sp = new Splash(Image.FromFile(f)) { progress = 0.6f, step = "Compiling shaders" };
        using var bmp = new Bitmap(sp.ClientSize.Width, sp.ClientSize.Height);
        using (var g = Graphics.FromImage(bmp)) sp.OnPaint(new PaintEventArgs(g, new Rectangle(Point.Empty, bmp.Size)));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public static void End()
    {
        if (current == null) return;
        Step("Ready", 1);
        var s = current; current = null;
        s.Close(); s.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(art, 0, 0, art.Width, art.Height);
        // orange loading bar between the tagline and the footer
        int w = 300, x = (ClientSize.Width - w) / 2, y = ClientSize.Height - 66;
        using (var t = new SolidBrush(Track)) g.FillRectangle(t, x, y, w, 3);
        using (var o = new SolidBrush(Orange)) g.FillRectangle(o, x, y, (int)(w * progress), 3);
        using var d = new SolidBrush(Dim);
        var sz = g.MeasureString(step, mono);
        g.DrawString(step, mono, d, (ClientSize.Width - sz.Width) / 2, y + 8);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { art?.Dispose(); mono?.Dispose(); }
        base.Dispose(disposing);
    }
}
