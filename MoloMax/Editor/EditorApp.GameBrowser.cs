using System.Numerics;
using ImGuiNET;
using MoloMax.Core.Gta.Game;
using MoloMax.UI;
using MoloMax.Rendering;

namespace MoloMax.Editor;

/// <summary>
/// Game files browser: reads the GTA V install like CodeWalker (no OpenIV / CodeWalker export needed), searches
/// every model and imports it with its textures, at the 3D cursor or exactly where the game places it.
/// </summary>
public sealed partial class EditorApp
{
    bool gameOpen;
    GameFiles game;
    System.Threading.Tasks.Task gameTask;
    string gameStatus = "", gameError, gameSearch = "";
    float gameProgress;
    bool gYdr = true, gYft = true, gYdd = true, gYbn;
    List<GameAsset> gameResults = new(); string gameKey;
    GameAsset gameSel;
    bool gameSetOrigin = true;

    static string GameExtractDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Polymorph", "GameFiles");

    public void OpenGameBrowser() { gameOpen = true; }

    void StartGameLoad(string folder)
    {
        gameError = null; var f = folder;
        gameTask = System.Threading.Tasks.Task.Run(() =>
        {
            try { var g = GameFiles.Open(f, s => gameStatus = s, Settings.GtaKey); Settings.GtaKey = GameFiles.CurrentKey; Settings.Save(); game = g; gameStatus = $"{g.Models.Count:N0} models"; }
            catch (Exception e) { gameError = e.Message; }
        });
    }

    void DrawGameBrowser()
    {
        if (!gameOpen) return;
        ImGui.SetNextWindowSize(new Vector2(1100, 640), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(gpu.Width / 2f - 550, menuH + toolbarH + 40), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Game Files (GTA V)###gamefiles", ref gameOpen, ImGuiWindowFlags.NoSavedSettings)) { ImGui.End(); return; }

        if (game == null)
        {
            string folder = Settings.GtaFolder ?? "";
            bool ok = GameFiles.LooksLikeGta(folder, out bool gen9);
            Ui.TextColored(Theme.Gold, "GTA V install");
            ImGui.SetNextItemWidth(-90);
            if (ImGui.InputTextWithHint("##gfold", @"C:\Program Files\Rockstar Games\Grand Theft Auto V", ref folder, 400)) { Settings.GtaFolder = folder; Settings.Save(); }
            ImGui.SameLine(); if (ImGui.Button("Browse", new Vector2(80, 0)) && PickFolder("GTA V folder (with GTA5.exe)") is { } pf) { Settings.GtaFolder = pf; Settings.Save(); }
            Ui.TextDisabled(ok ? (gen9 ? "GTA V Enhanced found." : "GTA V found.") : "Pick the folder that contains GTA5.exe.");
            bool busy = gameTask is { IsCompleted: false };
            if (!ok || busy) ImGui.BeginDisabled();
            if (ImGui.Button("Load the game files", new Vector2(220, 32)))
            {
                StartGameLoad(folder);
            }
            if (!ok || busy) ImGui.EndDisabled();
            if (busy) { ImGui.SameLine(); ImGui.TextUnformatted(gameStatus); }
            if (gameError != null) Ui.TextColored(new Vector4(1, 0.4f, 0.35f, 1), gameError);
            Ui.TextDisabled("The archives are read in place (nothing is modified). The first load takes a little while.");
            ImGui.End();
            return;
        }

        // classify the models into families once (background), then the type index for the textures
        StartClassify();

        // search bar
        ImGui.SetNextItemWidth(280);
        ImGui.InputTextWithHint("##gsearch", "search a model (e.g. chair, ss1_05, prop_bench)", ref gameSearch, 80);
        ImGui.SameLine(); ImGui.Checkbox("ydr", ref gYdr); ImGui.SameLine(); ImGui.Checkbox("yft", ref gYft); ImGui.SameLine(); ImGui.Checkbox("ydd", ref gYdd); ImGui.SameLine(); ImGui.Checkbox("ybn", ref gYbn);
        ImGui.SameLine(); Ui.TextDisabled(gameStatus);
        bool classified = gameClassified;
        var key = $"{gameSearch}|{gYdr}{gYft}{gYdd}{gYbn}|{gameCat}|{classified}|{familyRev}";
        if (key != gameKey)
        {
            gameKey = key;
            var text = gameSearch.Trim();
            IEnumerable<GameAsset> q;
            if (text.Length >= 2) q = game.Search(text, gYdr, gYft, gYdd, gYbn);
            else if (gameCat >= 0 && classified) q = game.Models.Where(m => (m.Ext == ".ydr" && gYdr) || (m.Ext == ".yft" && gYft) || (m.Ext == ".ydd" && gYdd) || (m.Ext == ".ybn" && gYbn));
            else q = Enumerable.Empty<GameAsset>();
            if (gameCat >= 0 && classified) { long bit = 1L << gameCat; q = q.Where(m => (m.Categories & bit) != 0); }
            gameResults = q.Take(5000).ToList();
        }

        float h = ImGui.GetContentRegionAvail().Y;
        // ---- families
        ImGui.BeginChild("##gcats", new Vector2(170, h), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
        Ui.Label("FAMILIES", Theme.TextDim);
        if (ImGui.Selectable("All models", gameCat < 0)) gameCat = -1;
        if (!classified) Ui.TextDisabled("sorting the models...");
        for (int i = 0; i < GameCategories.All.Length; i++)
            if (ImGui.Selectable(GameCategories.All[i].Name, gameCat == i)) { gameCat = i; gameSel = null; }
        ImGui.EndChild();
        ImGui.SameLine();

        // ---- results
        ImGui.BeginChild("##glist", new Vector2(300, h), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeX);
        if (gameResults.Count == 0) Ui.TextDisabled(gameCat < 0 ? "Type at least 2 letters, or pick a family." : "Nothing in this family with these filters.");
        else Ui.TextDisabled($"{gameResults.Count:N0}{(gameResults.Count == 5000 ? "+" : "")} models");
        unsafe
        {
            var clip = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clip.Begin(gameResults.Count);
            while (clip.Step())
                for (int i = clip.DisplayStart; i < clip.DisplayEnd; i++)
                {
                    var a = gameResults[i];
                    if (ImGui.Selectable($"{a.Name}{a.Ext}##g{i}", gameSel == a, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        gameSel = a;
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) GameImport(a, null);
                    }
                    if (ImGui.BeginPopupContextItem($"##gctx{i}")) { gameSel = a; FamilyMenu(a); ImGui.EndPopup(); }
                }
            clip.End(); clip.Destroy();
        }
        // keyboard: up / down walks the list (the viewer follows)
        if (ImGui.IsWindowFocused() && gameSel != null && gameResults.Count > 0)
        {
            int idx = gameResults.IndexOf(gameSel);
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) && idx < gameResults.Count - 1) gameSel = gameResults[idx + 1];
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow) && idx > 0) gameSel = gameResults[idx - 1];
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // ---- viewer + details
        ImGui.BeginChild("##gdetail", Vector2.Zero, ImGuiChildFlags.Borders);
        if (gameSel == null) Ui.TextDisabled("Pick a model: it shows here in 3D with its textures. Double-click imports it at the 3D cursor.");
        else
        {
            var a = gameSel;
            RequestPreview(a);
            float vw = ImGui.GetContentRegionAvail().X;
            float vh = MathF.Min(vw * 0.75f, MathF.Max(160, ImGui.GetContentRegionAvail().Y - 170));
            GameViewer(new Vector2(vw, vh));
            Ui.TextColored(Theme.Gold, a.Name + a.Ext);
            Ui.TextDisabled(a.Path);
            var chain = game.TypesIndexed ? game.TextureChainOf(a) : new List<GameAsset>();
            Ui.TextDisabled($"{a.Size / 1024:N0} KB   textures: " + (chain.Count > 0 ? string.Join(" > ", chain.Select(c => c.Name + ".ytd")) : "embedded only"));
            if (previewInfo != null) Ui.TextDisabled(previewInfo);
            ImGui.Spacing();
            if (ImGui.Button("Import at the 3D cursor", new Vector2(-1, 30))) GameImport(a, null);
            ImGui.Separator();
            Ui.Text("Where the game places it");
            bool busy = gameTask is { IsCompleted: false };
            if (!game.MapIndexed)
            {
                if (busy) { ImGui.ProgressBar(gameProgress, new Vector2(-1, 0), gameStatus); }
                else if (ImGui.Button("Find it in the map (reads every .ymap once)", new Vector2(-1, 0)))
                    gameTask = System.Threading.Tasks.Task.Run(() => { try { game.BuildMapIndex((s, p) => { gameStatus = s; gameProgress = p; }); gameStatus = "Map index ready"; } catch (Exception e) { gameError = e.Message; } });
            }
            else
            {
                var pl = game.PlacementsOf(a);
                if (pl.Count == 0) Ui.TextDisabled("Not placed in the map (a prop spawned by scripts, an interior part, or a LOD).");
                else
                {
                    ImGui.Checkbox("Set the scene origin there (build around it)", ref gameSetOrigin);
                    Theme.Tip("The scene's GTA origin becomes that point: the building lands at 0,0,0 and your export goes back exactly there.");
                    int n = 0;
                    foreach (var p in pl.Take(40))
                    {
                        ImGui.PushID(n++);
                        if (ImGui.Button("Import here")) GameImport(a, p);
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"{p.Position.X:0.0}, {p.Position.Y:0.0}, {p.Position.Z:0.0}");
                        ImGui.SameLine(); Ui.TextDisabled(p.Ymap);
                        ImGui.PopID();
                    }
                    if (pl.Count > 40) Ui.TextDisabled($"... {pl.Count - 40} more");
                }
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }

    // ---------------------------------------------------------------- 3D viewer

    int gameCat = -1;
    /// <summary>Bumped when a model changes family by hand: the lists rebuild.</summary>
    int familyRev;

    static string FamilyKey(GameAsset a) => (a.Name + a.Ext).ToLowerInvariant();

    /// <summary>Sorts the game models into families once, in the background (names first, then your own choices).</summary>
    void StartClassify()
    {
        if (gameClassify != null || game == null) return;
        var overrides = new Dictionary<string, long>(Settings.FamilyOverrides);
        // its own thread: the map streaming fills the thread pool, a pooled task waited minutes behind it
        gameClassify = System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var m in game.Models)
            {
                try { m.Categories = overrides.TryGetValue(FamilyKey(m), out var o) ? o : GameCategories.Classify(m.Name); }
                catch { m.Categories = 0; }
            }
            gameClassified = true;
            Console.WriteLine($"[families] {game.Models.Count} models sorted in {sw.ElapsedMilliseconds} ms");
            try { game.BuildTypeIndex((st, _) => gameStatus = st); } catch { }
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning, System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>Right-click menu of a game model: tick the families (Game Files and Asset Browser) it belongs to.</summary>
    void FamilyMenu(GameAsset a)
    {
        Ui.TextDisabled(a.Name + a.Ext);
        ImGui.Separator();
        Ui.Label("FAMILIES (ASSET BROWSER)", Theme.TextDim);
        if (!gameClassified) { Ui.TextDisabled("sorting the models..."); return; }
        // the menu stays open: several families can be ticked in a row
        ImGui.PushItemFlag(ImGuiItemFlags.AutoClosePopups, false);
        for (int i = 0; i < GameCategories.All.Length; i++)
        {
            long bit = 1L << i;
            bool on = (a.Categories & bit) != 0;
            if (ImGui.MenuItem(GameCategories.All[i].Name, null, on))
                SetFamilies(a, on ? a.Categories & ~bit : a.Categories | bit);
        }
        ImGui.PopItemFlag();
        ImGui.Separator();
        bool custom = Settings.FamilyOverrides.ContainsKey(FamilyKey(a));
        if (ImGui.MenuItem("Back to automatic", null, false, custom))
        {
            Settings.FamilyOverrides.Remove(FamilyKey(a)); Settings.Save();
            a.Categories = GameCategories.Classify(a.Name); familyRev++; assetKey = null;
        }
        if (custom) Ui.TextDisabled("(families set by hand)");
    }

    void SetFamilies(GameAsset a, long mask)
    {
        a.Categories = mask;
        Settings.FamilyOverrides[FamilyKey(a)] = mask; Settings.Save();
        familyRev++; assetKey = null;
        SetStatus($"{a.Name}: {(mask == 0 ? "no family" : string.Join(", ", Enumerable.Range(0, GameCategories.All.Length).Where(i => (mask & (1L << i)) != 0).Select(i => GameCategories.All[i].Name)))}");
    }
    System.Threading.Tasks.Task gameClassify;
    /// <summary>The models are sorted into families (the type index may still be building).</summary>
    volatile bool gameClassified;
    PreviewTarget previewRt;
    GameAsset previewAsset;
    System.Threading.Tasks.Task<MoloMax.Core.IO.ImportResult> previewTask;
    GameAsset previewPending;
    MoloMax.Core.Scenes.Scene previewScene;
    string previewInfo;
    float previewYaw = 0.7f, previewPitch = 0.35f, previewDist = 3f; Vector3 previewCentre;
    bool previewSpin = true, previewCol;

    void RequestPreview(GameAsset a)
    {
        if (previewAsset == a || previewPending == a) return;
        if (previewTask is { IsCompleted: false }) { previewPending = a; return; } // the next one after the current load
        StartPreview(a);
    }

    void StartPreview(GameAsset a)
    {
        previewAsset = a; previewPending = null; previewInfo = "loading...";
        var g = game; var dir = Path.Combine(GameExtractDir, a.Name); var opt = ImportOpts();
        previewTask = System.Threading.Tasks.Task.Run(() => MoloMax.Core.IO.ModelImporter.Import(g.Extract(a, dir), opt));
    }

    /// <summary>Takes a finished preview load and builds the little scene the viewer draws.</summary>
    void PumpPreview()
    {
        if (previewTask == null || !previewTask.IsCompleted) return;
        var t = previewTask; previewTask = null;
        if (previewScene != null) renderer.FreePreview(previewScene.Objects.Select(o => o.Id));
        previewScene = null;
        if (t.IsFaulted) { previewInfo = "Can't show it: " + (t.Exception?.GetBaseException().Message ?? "error"); }
        else
        {
            var r = t.Result;
            var sc = new MoloMax.Core.Scenes.Scene();
            sc.Materials.Clear();
            for (int i = 0; i < r.Materials.Count; i++) { var m = r.Materials[i].Clone(); m.Id = i; sc.Materials.Add(m); }
            if (sc.Materials.Count == 0) sc.Materials.Add(MoloMax.Core.Gta.GtaMaterial.CreateDefault(0, "default", new Vector3(0.7f)));
            int id = -100000;
            foreach (var im in r.Meshes)
            {
                var o = new MoloMax.Core.Scenes.SceneObject { Id = id--, Name = im.Name, Mesh = im.Mesh, GtaType = im.GtaType };
                sc.Objects.Add(o);
            }
            foreach (var tex in r.TexturesWritten)
            {
                var d = Path.GetDirectoryName(tex);
                if (d != null && !renderer.Textures.SearchFolders.Contains(d)) renderer.Textures.SearchFolders.Add(d);
            }
            renderer.Textures.ClearResolveCache();
            var draw = sc.Objects.Where(o => o.GtaType != MoloMax.Core.Scenes.GtaObjectType.Collision).ToList();
            if (draw.Count == 0) draw = sc.Objects;
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var o in draw) foreach (var v in o.Mesh.Verts) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            if (mn.X > mx.X) { mn = Vector3.Zero; mx = Vector3.One; }
            previewCentre = (mn + mx) * 0.5f;
            previewDist = MathF.Max(0.5f, (mx - mn).Length() * 1.15f);
            previewScene = sc;
            int verts = draw.Sum(o => o.Mesh.Verts.Count), polys = draw.Sum(o => o.Mesh.Faces.Count);
            var miss = r.Warnings.Count(w => w.Contains("texture", StringComparison.OrdinalIgnoreCase));
            previewInfo = $"{verts:N0} verts · {polys:N0} polys · {r.TexturesWritten.Count} texture(s){(miss > 0 ? $" · {miss} warning(s)" : "")} · {(mx - mn).X:0.0} x {(mx - mn).Y:0.0} x {(mx - mn).Z:0.0} m";
        }
        if (previewPending != null) StartPreview(previewPending);
    }

    /// <summary>The viewer: drag to turn, wheel to zoom; it turns slowly by itself.</summary>
    void GameViewer(Vector2 size)
    {
        size = new Vector2(MathF.Max(64, MathF.Floor(size.X)), MathF.Max(64, MathF.Floor(size.Y)));
        if (previewRt == null || previewRt.Width != (int)size.X || previewRt.Height != (int)size.Y) { previewRt?.Dispose(); previewRt = new PreviewTarget(gpu.Device, (int)size.X, (int)size.Y); }
        var p0 = ImGui.GetCursorScreenPos();
        // an invisible button over the picture takes the mouse: a drag turns the model, it does not move the window
        ImGui.InvisibleButton("##gview", size);
        bool hover = ImGui.IsItemHovered(), held = ImGui.IsItemActive();
        ImGui.GetWindowDrawList().AddImage(imgui.IdOf(previewRt.Srv), p0, p0 + size);
        if (ImGui.BeginPopupContextItem("##gviewctx") && gameSel != null) { FamilyMenu(gameSel); ImGui.EndPopup(); }
        if (held && ImGui.IsMouseDragging(ImGuiMouseButton.Left)) { var d = ImGui.GetIO().MouseDelta; previewYaw -= d.X * 0.01f; previewPitch = Math.Clamp(previewPitch + d.Y * 0.01f, -1.4f, 1.4f); previewSpin = false; }
        if (hover && ImGui.GetIO().MouseWheel != 0) previewDist *= MathF.Pow(0.88f, ImGui.GetIO().MouseWheel);
        if (hover && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) previewSpin = true;
        var dl = ImGui.GetWindowDrawList();
        if (previewScene == null) dl.AddText(p0 + new Vector2(10, 10), ImGui.GetColorU32(ImGuiCol.TextDisabled), previewInfo ?? "");
        dl.AddText(p0 + new Vector2(8, size.Y - 20), ImGui.GetColorU32(ImGuiCol.TextDisabled), "drag: turn · wheel: zoom · double-click: spin");
        ImGui.SetCursorScreenPos(p0 + new Vector2(size.X - 92, 6));
        ImGui.Checkbox("collision", ref previewCol);
        ImGui.SetCursorScreenPos(p0 + new Vector2(0, size.Y + 4));
        previewWanted = true;
    }
    bool previewWanted;

    /// <summary>Draws the preview scene into its picture (called before the viewports, every frame the viewer is shown).</summary>
    void RenderPreview(float dt)
    {
        if (!previewWanted || previewRt == null) { previewWanted = false; return; }
        previewWanted = false;
        var ctx = gpu.Context;
        ctx.OutputMerger.SetRenderTargets(previewRt.Dsv, previewRt.Rtv);
        ctx.ClearRenderTargetView(previewRt.Rtv, new SharpDX.Mathematics.Interop.RawColor4(0.106f, 0.118f, 0.141f, 1));
        ctx.ClearDepthStencilView(previewRt.Dsv, SharpDX.Direct3D11.DepthStencilClearFlags.Depth, 1f, 0);
        if (previewScene != null)
        {
            if (previewSpin) previewYaw += dt * 0.5f;
            ctx.Rasterizer.SetViewport(0, 0, previewRt.Width, previewRt.Height);
            ctx.Rasterizer.SetScissorRectangle(0, 0, previewRt.Width, previewRt.Height);
            var dir = new Vector3(MathF.Cos(previewPitch) * MathF.Cos(previewYaw), MathF.Cos(previewPitch) * MathF.Sin(previewYaw), MathF.Sin(previewPitch));
            var eye = previewCentre + dir * previewDist;
            var view = Matrix4x4.CreateLookAt(eye, previewCentre, Vector3.UnitZ);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.75f, previewRt.Width / (float)previewRt.Height, previewDist * 0.01f, previewDist * 20f);
            var blend = new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 0);
            renderer.EditObjectId = int.MinValue;
            renderer.SetLights(null, Settings.Filmic ? RenderFlags.Filmic : RenderFlags.None, Matrix4x4.Identity, 0, 0);
            renderer.NoCull = false;
            renderer.BeginScene(view * proj, eye, MoloMax.Core.Gta.GtaLighting.TimeOfDay(13f, 1f), ShadingMode.Realistic, Exposure, MathF.Max(AmbientBoost, 0.2f));
            ctx.OutputMerger.SetDepthStencilState(renderer.DepthDefault);
            ctx.OutputMerger.SetBlendState(renderer.Opaque, blend, -1);
            foreach (var o in previewScene.Objects) if (previewCol || o.GtaType != MoloMax.Core.Scenes.GtaObjectType.Collision) renderer.DrawObject(previewScene, o, false, Vector4.Zero, false);
            ctx.OutputMerger.SetBlendState(renderer.Alpha, blend, -1);
            ctx.OutputMerger.SetDepthStencilState(renderer.DepthRead);
            foreach (var o in previewScene.Objects) if (previewCol || o.GtaType != MoloMax.Core.Scenes.GtaObjectType.Collision) renderer.DrawObject(previewScene, o, true, Vector4.Zero, false);
        }
        gpu.BindBackBuffer();
    }

    void GameImport(GameAsset a, GamePlacement? at)
    {
        if (MapMode) { StartPlacement(a.Name); gameOpen = false; return; } // MAP mode: the prop follows the mouse, click to drop it
        var dir = Path.Combine(GameExtractDir, a.Name);
        var g = game;
        Vector3? pos = null; Quaternion? rot = null; Vector3? sc = null;
        if (at is { } p)
        {
            if (gameSetOrigin) { Do("Set GTA origin", () => Scene.GtaOrigin = p.Position); pos = Vector3.Zero; }
            else pos = p.Position - Scene.GtaOrigin;
            rot = p.Rotation; sc = p.Scale;
        }
        // extraction runs on the import worker, the model then arrives like any .ydr / .yft
        ImportFilesAsync(new[] { Path.Combine(dir, a.Name + a.Ext) }, pos, rot, sc, path => g.Extract(a, dir));
        SetStatus($"Importing {a.Name}{a.Ext} from the game...");
    }
}
