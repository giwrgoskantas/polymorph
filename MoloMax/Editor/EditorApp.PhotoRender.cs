using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImGuiNET;
using MoloMax.Core.Gta;
using MoloMax.Core.Scenes;
using MoloMax.UI;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace MoloMax.Editor;

/// <summary>
/// Photo render: the scene seen from the active view, path traced by a free engine running in the background (no window),
/// with a few plain settings (quality, size, light mood, brightness...). The picture lands in Pictures\Polymorph.
/// </summary>
public sealed partial class EditorApp
{
    bool photoOpen;
    Process photoProc;
    string photoStatus = "", photoDevice = "", photoOutput, photoError;
    int photoSample, photoSamples;
    string photoRemaining = "";
    readonly Stopwatch photoClock = new();
    readonly List<string> photoLog = new();
    ShaderResourceView photoSrv; Vector2 photoImgSize; string photoSrvPath;
    volatile bool photoDone;

    static readonly string[] PhotoQualityNames = { "Quick look", "Good", "Best" };
    static readonly string[] PhotoQualityHints = { "a few seconds, a bit grainy", "about a minute: the usual choice", "several minutes: final pictures" };
    static readonly string[] PhotoSizeNames = { "Like the view", "HD 1920 x 1080", "4K 3840 x 2160", "Square 2048" };
    static readonly string[] PhotoLightNames = { "Like the view", "Daylight", "Evening", "Night: lamps only", "Studio (props)" };
    static readonly string[] PhotoLightHints =
    {
        "Same time of day, interior switch and lamps as the Realistic view.",
        "Midday sun and sky through the windows, lamps dimmed.",
        "Low warm sun, lamps on.",
        "No sun: only your lights and glowing materials.",
        "Soft studio lights around the selection, grey background: for props.",
    };

    public string PicturesFolder => photoFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Polymorph");
    string photoFolder; // self tests write their pictures in their own folder
    static string PhotoScript => Path.Combine(PythonFolder, "render", "photo_render.py");

    /// <summary>The free path-tracing engine on this PC (newest version found), or null.</summary>
    public string FindRenderEngine()
    {
        if (!string.IsNullOrEmpty(Settings.RenderEngine) && File.Exists(Settings.RenderEngine)) return Settings.RenderEngine;
        var found = new List<(Version V, string Path)>();
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var root = Path.Combine(pf, "Blender Foundation");
            if (Directory.Exists(root))
                foreach (var d in Directory.GetDirectories(root))
                {
                    var exe = Path.Combine(d, "blender.exe");
                    if (!File.Exists(exe)) continue;
                    var m = Regex.Match(Path.GetFileName(d), @"(\d+)\.(\d+)");
                    found.Add((m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(0, 0), exe));
                }
            var steam = Path.Combine(pf, "Steam", "steamapps", "common", "Blender", "blender.exe");
            if (File.Exists(steam)) found.Add((new Version(0, 1), steam));
        }
        return found.OrderByDescending(f => f.V).Select(f => f.Path).FirstOrDefault();
    }

    public bool PhotoRendering => photoProc != null && !photoProc.HasExited;

    /// <summary>Window minimised: no frame is drawn, but Claude / Python calls and a running photo render keep going.</summary>
    public void BackgroundTick()
    {
        inApi = true;
        try { Api.Pump(); } catch { }
        finally { inApi = false; }
    }

    /// <summary>Starts a photo render of the active view. Returns false (and says why) when it cannot.</summary>
    public bool StartPhotoRender()
    {
        photoOpen = true;
        if (PhotoRendering) return false;
        photoError = null;
        var engine = FindRenderEngine();
        if (engine == null) { photoError = "The free render engine is not installed yet (see below)."; return false; }
        if (!File.Exists(PhotoScript)) { photoError = "photo_render.py is missing next to Polymorph: reinstall Polymorph."; return false; }
        var vp = Viewports[ActiveViewport];
        var cam = vp.Camera;
        var objs = Scene.Objects.Where(o => !o.Hidden && o.SplatFile == null && !IsVolumeOnly(o) && o.Mesh != null && o.Mesh.Faces.Count > 0
            && o.GtaType is not (GtaObjectType.Collision or GtaObjectType.Portal or GtaObjectType.Light or GtaObjectType.Model or GtaObjectType.CollisionGroup)).ToList();
        if (objs.Count == 0) { photoError = "Nothing to render: the scene has no visible model."; return false; }

        var jobDir = Path.Combine(Path.GetTempPath(), "polymorph_photo");
        Directory.CreateDirectory(jobDir);
        var glb = Path.Combine(jobDir, "scene.glb");
        try { MoloMax.Core.IO.GlbExporter.Export(Scene, objs, glb, TexturePng); }
        catch (Exception e) { photoError = "Could not prepare the scene: " + e.Message; return false; }

        // size
        int w, h;
        switch (Settings.PhotoSize)
        {
            case 1: w = 1920; h = 1080; break;
            case 2: w = 3840; h = 2160; break;
            case 3: w = h = 2048; break;
            default: w = 1920; h = Math.Max(2, (int)MathF.Round(1920 * vp.H / MathF.Max(vp.W, 1) / 2) * 2); break;
        }
        int q = Math.Clamp(Settings.PhotoQuality, 0, 2);
        var (samples, threshold, bounces) = q switch { 0 => (32, 0.05f, 4), 2 => (512, 0.008f, 12), _ => (128, 0.02f, 8) };

        // light mood
        int mood = Math.Clamp(Settings.PhotoLight, 0, PhotoLightNames.Length - 1);
        float hour = mood switch { 1 => 13f, 2 => 18.6f, _ => Hour };
        bool sunOn = mood switch { 0 => !InteriorLight, 1 or 2 => true, _ => false };
        float lampsMood = mood switch { 1 => 0.4f, 4 => 0f, _ => 1f };
        object sun = null;
        if (sunOn)
        {
            var tod = GtaLighting.TimeOfDay(hour);
            var toward = Vector3.Normalize(tod.LightDir);
            float up = MathF.Max(0, toward.Z);
            var col = new Vector3(tod.LightDirColour.X, tod.LightDirColour.Y, tod.LightDirColour.Z);
            float m = MathF.Max(col.X, MathF.Max(col.Y, col.Z)); col = m > 0 ? col / m : Vector3.One;
            if (up > 0.02f) sun = new { towardSun = A3(toward), colour = A3(col), strength = 4.5f * MathF.Sqrt(up) };
        }
        (Vector3 c, float s) sky = mood switch
        {
            0 => InteriorLight ? (new Vector3(0.62f, 0.66f, 0.75f), 0.12f) : DaySky(hour),
            1 => DaySky(13f),
            2 => (new Vector3(1f, 0.62f, 0.42f), 0.35f),
            3 => (new Vector3(0.05f, 0.07f, 0.13f), 0.25f),
            _ => (new Vector3(0.8f, 0.8f, 0.82f), 0.7f),
        };
        // the scene's lights
        var lamps = new List<object>();
        if (lampsMood > 0)
        {
            int hr = Math.Clamp((int)hour, 0, 23);
            foreach (var o in Scene.Objects)
            {
                if (o.GtaType != GtaObjectType.Light || o.Hidden) continue;
                var L = o.Gta.Light ?? new GtaLight();
                if (mood == 0 && !InteriorLight && (L.TimeFlags & (1u << hr)) == 0) continue;
                var wm = o.World;
                var dir = Vector3.TransformNormal(-Vector3.UnitZ, wm);
                dir = dir.LengthSquared() > 1e-12f ? Vector3.Normalize(dir) : -Vector3.UnitZ;
                lamps.Add(new
                {
                    kind = L.Kind == GtaLightKind.Spot ? "spot" : L.Kind == GtaLightKind.Capsule ? "capsule" : "point",
                    pos = A3(Vector3.Transform(Vector3.Zero, wm)), dir = A3(dir), colour = A3(L.EffectiveColour),
                    watts = L.Intensity * 6f * lampsMood, radius = L.Radius, cone = MathF.Max(L.ConeInner, L.ConeOuter), blend = L.SpotBlend,
                });
            }
        }
        // studio: three soft lights around what is rendered
        var areas = new List<object>();
        if (mood == 4)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var o in (Selected.Count > 0 ? objs.Where(o => Selected.Contains(o.Id)) : objs).DefaultIfEmpty(objs[0]))
            { var (a, b) = o.WorldBounds(); mn = Vector3.Min(mn, a); mx = Vector3.Max(mx, b); }
            var c = (mn + mx) / 2; float r = MathF.Max((mx - mn).Length(), 0.5f);
            var fwd = Vector3.Normalize(cam.Target - cam.Eye); var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
            Vector3 At(float side, float back, float upk) => c - fwd * r * back + right * r * side + Vector3.UnitZ * r * upk;
            areas.Add(new { pos = A3(At(-0.9f, 1.1f, 0.9f)), target = A3(c), watts = 300f * r * r, size = r * 0.8f });
            areas.Add(new { pos = A3(At(1.1f, 0.8f, 0.3f)), target = A3(c), watts = 90f * r * r, size = r });
            areas.Add(new { pos = A3(At(0.2f, -1.2f, 1.0f)), target = A3(c), watts = 200f * r * r, size = r * 0.5f });
        }

        Directory.CreateDirectory(PicturesFolder);
        photoOutput = Path.Combine(PicturesFolder, $"{SafeFile(Path.GetFileNameWithoutExtension(CurrentFile ?? "scene"))}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        var job = new
        {
            glb, output = photoOutput, width = w, height = h, samples, noiseThreshold = threshold, bounces,
            denoise = Settings.PhotoDenoise, soft = Settings.PhotoSoft, transparent = Settings.PhotoTransparent, dof = Settings.PhotoDof,
            exposure = Settings.PhotoExposure, lampPower = Settings.PhotoLamps, emissive = 1f, cullBackfaces = BackfaceCull,
            camera = new { eye = A3(cam.Eye), target = A3(cam.Target), up = A3(cam.CamUp), fovY = cam.Fov, ortho = cam.Ortho, orthoHeight = cam.OrthoHeight, near = cam.Near },
            sun, sky = new { colour = A3(sky.c), strength = sky.s * Settings.PhotoSky }, lights = lamps, areas,
        };
        var jobFile = Path.Combine(jobDir, "job.json");
        File.WriteAllText(jobFile, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }));

        var psi = new ProcessStartInfo(engine, $"-b --factory-startup -P \"{PhotoScript}\" -- \"{jobFile}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = jobDir };
        photoSample = 0; photoSamples = samples; photoRemaining = ""; photoDevice = ""; photoDone = false;
        lock (photoLog) photoLog.Clear();
        photoStatus = "Starting the render engine...";
        try
        {
            photoProc = Process.Start(psi);
            photoProc.OutputDataReceived += (_, e) => PhotoLine(e.Data);
            photoProc.ErrorDataReceived += (_, e) => PhotoLine(e.Data);
            photoProc.BeginOutputReadLine(); photoProc.BeginErrorReadLine();
            photoClock.Restart();
        }
        catch (Exception e) { photoError = "Could not start the render engine: " + e.Message; photoProc = null; return false; }
        return true;
    }

    static float[] A3(Vector3 v) => new[] { v.X, v.Y, v.Z };
    static string SafeFile(string s) => string.Concat((s ?? "scene").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

    /// <summary>Sky colour and strength for an hour (dim at dawn / dusk, dark at night).</summary>
    static (Vector3, float) DaySky(float hour)
    {
        float day = Math.Clamp(MathF.Sin((hour - 6f) / 12f * MathF.PI), 0, 1);
        var c = Vector3.Lerp(new Vector3(1f, 0.65f, 0.45f), new Vector3(0.55f, 0.7f, 1f), MathF.Sqrt(day));
        return (day > 0 ? c : new Vector3(0.05f, 0.07f, 0.13f), day > 0 ? 0.25f + 0.9f * day : 0.15f);
    }

    static readonly Regex SampleRx = new(@"Sample (\d+)\s*/\s*(\d+)|(\d+)\s*/\s*(\d+) [Ss]amples", RegexOptions.Compiled);
    static readonly Regex RemainRx = new(@"Remaining:\s*([\d:.]+)", RegexOptions.Compiled);

    void PhotoLine(string line)
    {
        if (line == null) return;
        lock (photoLog) { photoLog.Add(line); if (photoLog.Count > 400) photoLog.RemoveAt(0); }
        if (line.StartsWith("POLY:"))
        {
            var t = line[5..].Trim();
            if (t.StartsWith("STEP ")) photoStatus = char.ToUpper(t[5]) + t[6..] + "...";
            else if (t.StartsWith("DEVICE ")) photoDevice = t[7..];
            else if (t.StartsWith("DONE ")) photoDone = true;
            if (!t.StartsWith("STATS ")) return;
            line = t[6..]; // the engine's status line: samples and time left are read below
        }
        var m = SampleRx.Match(line);
        if (m.Success)
        {
            int a = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value), b = int.Parse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value);
            if (b > 0) { photoSample = a; photoSamples = b; }
        }
        var r = RemainRx.Match(line);
        if (r.Success) photoRemaining = r.Groups[1].Value;
        if (line.Contains("Denoising")) photoStatus = "Removing the grain...";
    }

    public void StopPhotoRender()
    {
        try { if (PhotoRendering) photoProc.Kill(true); } catch { }
        photoStatus = "Stopped.";
    }

    void PumpPhotoRender()
    {
        if (photoProc == null || !photoProc.HasExited) return;
        var p = photoProc; photoProc = null; photoClock.Stop();
        if (File.Exists(photoOutput) && (photoDone || p.ExitCode == 0))
        {
            photoStatus = $"Done in {photoClock.Elapsed.TotalSeconds:0} s.";
            LoadPhoto(photoOutput);
            SetStatus("Photo saved: " + photoOutput);
        }
        else if (photoStatus != "Stopped.")
        {
            string last; lock (photoLog) last = string.Join("\n", photoLog.Where(l => l.Contains("Error") || l.Contains("error") || l.Contains("Traceback")).TakeLast(4));
            photoError = "The render failed" + (string.IsNullOrWhiteSpace(last) ? $" (code {p.ExitCode})." : ":\n" + last);
            photoStatus = "";
        }
    }

    void LoadPhoto(string path)
    {
        try
        {
            photoSrv?.Dispose(); photoSrv = null;
            using var bmp = new Bitmap(path);
            using var b32 = bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = b32.LockBits(new Rectangle(0, 0, b32.Width, b32.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using var tex = new Texture2D(gpu.Device, new Texture2DDescription
                {
                    Width = b32.Width, Height = b32.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                }, new SharpDX.DataRectangle(data.Scan0, data.Stride));
                photoSrv = new ShaderResourceView(gpu.Device, tex);
            }
            finally { b32.UnlockBits(data); }
            photoImgSize = new Vector2(bmp.Width, bmp.Height); photoSrvPath = path;
        }
        catch (Exception e) { photoError = "Could not show the picture: " + e.Message; }
    }

    void PhotoRenderMenuItems()
    {
        if (ImGui.MenuItem("Photo render...", "F12")) { photoOpen = true; }
    }

    void DrawPhotoRender()
    {
        PumpPhotoRender();
        var io = ImGui.GetIO();
        if (ImGui.IsKeyPressed(ImGuiKey.F12, false) && !io.WantTextInput) { photoOpen = true; StartPhotoRender(); }
        if (!photoOpen) return;
        ImGui.SetNextWindowSize(new Vector2(1180, 700), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Photo render###photo", ref photoOpen)) { ImGui.End(); return; }
        float leftW = 330;
        ImGui.BeginChild("##photoset", new Vector2(leftW, 0));
        // ---- big button + progress
        bool busy = PhotoRendering;
        if (Theme.Toggle(busy ? "STOP" : "RENDER PHOTO  (F12)", true, new Vector2(-1, 40))) { if (busy) StopPhotoRender(); else StartPhotoRender(); }
        TutRect("photo_go");
        if (busy)
        {
            float f = photoSamples > 0 ? photoSample / (float)photoSamples : 0;
            ImGui.ProgressBar(f, new Vector2(-1, 0), photoSample > 0 ? $"{photoSample} / {photoSamples}" : "preparing");
            Ui.TextDisabled($"{photoStatus}  {photoClock.Elapsed.TotalSeconds:0} s" + (photoRemaining != "" ? $", about {photoRemaining} left" : ""));
            if (photoDevice != "") Ui.TextDisabled("On: " + photoDevice);
        }
        else if (photoStatus != "") Ui.TextDisabled(photoStatus);
        if (photoError != null) Ui.TextColored(new Vector4(1f, 0.5f, 0.35f, 1), photoError);
        ImGui.Separator();
        // ---- settings (all plain words)
        Ui.Label("Quality", Theme.TextDim);
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (Theme.Toggle(PhotoQualityNames[i], Settings.PhotoQuality == i, new Vector2((leftW - 16) / 3, 0))) { Settings.PhotoQuality = i; Settings.Save(); }
        }
        Ui.TextDisabled(PhotoQualityHints[Math.Clamp(Settings.PhotoQuality, 0, 2)]);
        ImGui.Spacing();
        Ui.Label("Picture size", Theme.TextDim);
        int size = Settings.PhotoSize; ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##psize", ref size, PhotoSizeNames, PhotoSizeNames.Length)) { Settings.PhotoSize = size; Settings.Save(); }
        ImGui.Spacing();
        Ui.Label("Light", Theme.TextDim);
        int mood = Settings.PhotoLight; ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##plight", ref mood, PhotoLightNames, PhotoLightNames.Length)) { Settings.PhotoLight = mood; Settings.Save(); }
        Ui.TextWrapped(PhotoLightHints[Math.Clamp(Settings.PhotoLight, 0, PhotoLightNames.Length - 1)]);
        ImGui.Spacing();
        float ev = Settings.PhotoExposure, sky = Settings.PhotoSky * 100, lamps = Settings.PhotoLamps * 100;
        ImGui.SetNextItemWidth(-110); if (ImGui.SliderFloat("Brightness", ref ev, -3, 3, ev >= 0 ? "+%.1f" : "%.1f")) Settings.PhotoExposure = ev;
        Theme.Tip("Like a camera exposure: + brighter, - darker. 0 = normal.");
        ImGui.SetNextItemWidth(-110); if (ImGui.SliderFloat("Sky light", ref sky, 0, 300, "%.0f %%")) Settings.PhotoSky = sky / 100;
        Theme.Tip("Light coming from the sky through windows and openings.");
        ImGui.SetNextItemWidth(-110); if (ImGui.SliderFloat("Lamp power", ref lamps, 0, 400, "%.0f %%")) Settings.PhotoLamps = lamps / 100;
        Theme.Tip("All the scene's lights together.");
        if (ImGui.IsItemDeactivatedAfterEdit()) Settings.Save();
        ImGui.Spacing();
        bool dn = Settings.PhotoDenoise, so = Settings.PhotoSoft, tr = Settings.PhotoTransparent, dof = Settings.PhotoDof;
        if (ImGui.Checkbox("Remove grain", ref dn)) { Settings.PhotoDenoise = dn; Settings.Save(); }
        Theme.Tip("Cleans the noise at the end (recommended).");
        if (ImGui.Checkbox("Soft shadows", ref so)) { Settings.PhotoSoft = so; Settings.Save(); }
        if (ImGui.Checkbox("Transparent background", ref tr)) { Settings.PhotoTransparent = tr; Settings.Save(); }
        Theme.Tip("For props: the picture keeps only the model (PNG with alpha).");
        if (ImGui.Checkbox("Blur far and near (focus on the view centre)", ref dof)) { Settings.PhotoDof = dof; Settings.Save(); }
        ImGui.Separator();
        var engine = FindRenderEngine();
        if (engine == null)
        {
            Ui.TextWrapped("The photo render uses a free path-tracing engine. Install it once (free, open source), then click Find again.");
            if (ImGui.Button("Download the free engine", new Vector2(-1, 0))) OpenUrl("https://www.blender.org/download/");
            if (ImGui.Button("Find again", new Vector2((leftW - 12) / 2, 0))) Settings.RenderEngine = "";
            ImGui.SameLine();
        }
        else Ui.TextDisabled("Engine ready.");
        if (ImGui.Button("Choose the engine...", new Vector2(engine == null ? (leftW - 12) / 2 : -1, 0)))
        {
            using var d = new System.Windows.Forms.OpenFileDialog { Filter = "Engine (*.exe)|*.exe", Title = "Choose the render engine (.exe)" };
            if (ShowFileDialog(d) == System.Windows.Forms.DialogResult.OK) { Settings.RenderEngine = d.FileName; Settings.Save(); }
            Input.ReleaseAllKeys();
        }
        ImGui.EndChild();
        ImGui.SameLine();
        // ---- the picture
        ImGui.BeginChild("##photoimg", Vector2.Zero, ImGuiChildFlags.Borders);
        var avail = ImGui.GetContentRegionAvail();
        if (photoSrv != null)
        {
            float k = MathF.Min((avail.X) / photoImgSize.X, (avail.Y - 34) / photoImgSize.Y);
            var sz = photoImgSize * MathF.Max(k, 0.05f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail.X - sz.X) / 2);
            ImGui.Image(imgui.IdOf(photoSrv), sz);
            if (ImGui.Button("Open the picture", new Vector2(160, 0))) OpenUrl(photoSrvPath);
            ImGui.SameLine(); if (ImGui.Button("Open the folder", new Vector2(160, 0))) OpenUrl(PicturesFolder);
            ImGui.SameLine(); Ui.TextDisabled(Path.GetFileName(photoSrvPath));
        }
        else
        {
            ImGui.SetCursorPos(new Vector2(avail.X / 2 - 170, avail.Y / 2 - 20));
            Ui.TextDisabled(busy ? "The picture appears here when it is ready." : "Frame your shot in the view, then RENDER PHOTO.\nThe camera is the active view.");
        }
        ImGui.EndChild();
        ImGui.End();
    }
}
