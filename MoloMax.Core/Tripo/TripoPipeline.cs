using System.Text.Json.Nodes;

namespace MoloMax.Core.Tripo;

public enum TripoInput { Text, Image, Multiview }

/// <summary>Tripo models (v3 "model" values).</summary>
public static class TripoModels
{
    public static readonly (string Id, string Label, int MaxFaces, bool Quad)[] All =
    {
        ("P2-20260801", "Tripo P2 (low poly, game ready)", 50000, true),
        ("P1-20260311", "Tripo P1 (low poly)", 20000, false),
        ("v3.1-20260211", "Tripo H3.1 (high detail)", 1500000, true),
        ("v3.0-20250812", "Tripo H3.0", 1000000, true),
        ("v2.5-20250123", "Tripo H2.5", 500000, false),
    };
    public static bool IsP(string id) => id != null && id.StartsWith("P");
}

public sealed class TripoOptions
{
    public TripoInput Input = TripoInput.Text;
    public string Prompt = "", NegativePrompt = "";
    public string ImagePath;
    /// <summary>front, left, back, right (front required).</summary>
    public string[] Views = new string[4];
    public string Model = "P2-20260801";
    /// <summary>0 = adaptive.</summary>
    public int FaceLimit = 5000;
    public bool Texture = true, Pbr = true, AutoSize = true;
    /// <summary>standard (2K) / detailed (4K) / extreme (8K).</summary>
    public string TextureQuality = "standard";
    public bool SmartLowPoly;
    /// <summary>Re-unwrap with Tripo Smart UV after generation.</summary>
    public bool SmartUv;
    /// <summary>Generate geometry first, then Smart UV, then texture on the new UVs (Molo pipeline).</summary>
    public bool TextureAfterUv;

    public TripoOptions Clone()
    {
        var o = (TripoOptions)MemberwiseClone();
        o.Views = (string[])Views.Clone();
        return o;
    }
}

/// <summary>A chain of Tripo tasks shown in the Tripo page with its progress.</summary>
public sealed class TripoJob
{
    public string Title;
    public List<string> Steps = new();
    public volatile int Step;
    public volatile int Progress;
    public volatile string Status = "waiting";
    public volatile string Error;
    public volatile bool Done;
    public string ResultFile, PreviewFile, LastTaskId;
    public decimal Credits;
    /// <summary>Set by the editor once the model is in the scene.</summary>
    public bool Imported;
    public CancellationTokenSource Cts = new();
    public DateTime Started = DateTime.Now;
}

/// <summary>
/// Tripo workflows: text / image / multiview to 3D, Smart UV, texture, retopology, chained like the Molo
/// pipeline (P2 at 2500 and 5000 faces, Smart UV, 2K texture).
/// </summary>
public static class TripoPipeline
{
    /// <summary>Generation (+ optional Smart UV + texture), then downloads the final model.</summary>
    public static async Task Generate(TripoClient c, TripoJob job, TripoOptions o, string outDir)
    {
        var ct = job.Cts.Token;
        try
        {
            bool texLater = o.TextureAfterUv && o.Texture;
            job.Steps = new List<string>();
            if (o.Input != TripoInput.Text) job.Steps.Add("Upload");
            job.Steps.Add("Generate");
            if (o.SmartUv || texLater) job.Steps.Add("Smart UV");
            if (texLater) job.Steps.Add("Texture");
            job.Steps.Add("Download");

            // ---- inputs
            string imageToken = null; var viewTokens = new string[4];
            if (o.Input == TripoInput.Image)
            {
                Begin(job, "Upload");
                imageToken = await c.Upload(o.ImagePath, ct);
            }
            else if (o.Input == TripoInput.Multiview)
            {
                Begin(job, "Upload");
                if (string.IsNullOrEmpty(o.Views[0])) throw new TripoException(1004, "Multiview needs at least the front view.");
                for (int i = 0; i < 4; i++) if (!string.IsNullOrEmpty(o.Views[i])) viewTokens[i] = await c.Upload(o.Views[i], ct);
                if (viewTokens.Count(t => t != null) < 2) throw new TripoException(1004, "Multiview needs at least 2 views.");
            }

            // ---- generation
            Begin(job, "Generate");
            var body = new JsonObject { ["model"] = o.Model };
            if (o.FaceLimit > 0) body["face_limit"] = o.FaceLimit;
            bool texNow = o.Texture && !texLater;
            body["texture"] = texNow;
            body["pbr"] = texNow && o.Pbr;
            if (texNow) body["texture_quality"] = o.TextureQuality;
            body["auto_size"] = o.AutoSize;
            body["export_uv"] = true;
            if (o.SmartLowPoly && !TripoModels.IsP(o.Model)) body["smart_low_poly"] = true;
            string route;
            switch (o.Input)
            {
                case TripoInput.Image:
                    route = "/v3/generation/image-to-model";
                    body["input"] = imageToken;
                    break;
                case TripoInput.Multiview:
                    route = "/v3/generation/multiview-to-model";
                    var arr = new JsonArray();
                    string[] keys = { "front", "left", "back", "right" };
                    for (int i = 0; i < 4; i++) if (viewTokens[i] != null) arr.Add(new JsonObject { [keys[i]] = viewTokens[i] });
                    body["inputs"] = arr;
                    break;
                default:
                    route = "/v3/generation/text-to-model";
                    if (string.IsNullOrWhiteSpace(o.Prompt)) throw new TripoException(1004, "Write a prompt first.");
                    body["prompt"] = o.Prompt.Length > 1024 ? o.Prompt[..1024] : o.Prompt;
                    if (!string.IsNullOrWhiteSpace(o.NegativePrompt)) body["negative_prompt"] = o.NegativePrompt.Length > 255 ? o.NegativePrompt[..255] : o.NegativePrompt;
                    break;
            }
            var task = await RunTask(c, job, route, body);

            // ---- Smart UV
            if (o.SmartUv || texLater)
            {
                Begin(job, "Smart UV");
                task = await RunTask(c, job, "/v3/mesh/uv", new JsonObject { ["input"] = task.Id, ["model"] = "default", ["bake"] = texNow });
            }

            // ---- texture on the new UVs
            if (texLater)
            {
                Begin(job, "Texture");
                var tb = new JsonObject { ["input"] = task.Id, ["texture_quality"] = o.TextureQuality, ["pbr"] = o.Pbr };
                tb["texture_prompt"] = o.Input switch
                {
                    TripoInput.Image => new JsonObject { ["image"] = new JsonObject { ["file_token"] = imageToken } },
                    TripoInput.Multiview => new JsonObject { ["image"] = new JsonObject { ["file_token"] = viewTokens[0] } },
                    _ => new JsonObject { ["text"] = o.Prompt },
                };
                task = await RunTask(c, job, "/v3/models/texture", tb);
            }

            await DownloadResult(c, job, task, outDir);
        }
        catch (OperationCanceledException) { job.Error = "Cancelled."; job.Status = "cancelled"; }
        catch (TripoException e) { job.Error = e.Friendly; job.Status = "failed"; }
        catch (Exception e) { job.Error = e.Message; job.Status = "failed"; }
        finally { job.Done = true; }
    }

    /// <summary>One post-process on an existing Tripo model (by task id): Smart UV, texture, retopology.</summary>
    public static async Task PostProcess(TripoClient c, TripoJob job, string route, JsonObject body, string outDir)
    {
        try
        {
            job.Steps = new List<string> { job.Title, "Download" };
            Begin(job, job.Title);
            var t = await RunTask(c, job, route, body);
            await DownloadResult(c, job, t, outDir);
        }
        catch (OperationCanceledException) { job.Error = "Cancelled."; job.Status = "cancelled"; }
        catch (TripoException e) { job.Error = e.Friendly; job.Status = "failed"; }
        catch (Exception e) { job.Error = e.Message; job.Status = "failed"; }
        finally { job.Done = true; }
    }

    public static JsonObject SmartUvBody(string taskId) => new() { ["input"] = taskId, ["model"] = "default", ["bake"] = true };

    public static JsonObject TextureBody(string taskId, string prompt, string quality, bool pbr)
    {
        var b = new JsonObject { ["input"] = taskId, ["texture_quality"] = quality, ["pbr"] = pbr };
        if (!string.IsNullOrWhiteSpace(prompt)) b["texture_prompt"] = new JsonObject { ["text"] = prompt };
        return b;
    }

    /// <summary>Smart low poly / retopology (v3.0 supports Smart UV in the same pass).</summary>
    public static JsonObject RetopoBody(string taskId, int faces, bool quad, bool smartUv) => new()
    {
        ["input"] = taskId, ["model"] = "v3.0", ["face_limit"] = Math.Clamp(faces, 500, quad ? 10000 : 20000),
        ["quad"] = quad, ["smart_uv"] = smartUv, ["bake"] = true,
    };

    static void Begin(TripoJob job, string step)
    {
        job.Step = Math.Max(0, job.Steps.IndexOf(step));
        job.Status = step;
        job.Progress = 0;
    }

    static async Task<TripoTask> RunTask(TripoClient c, TripoJob job, string route, JsonObject body)
    {
        var id = await c.Create(route, body, job.Cts.Token);
        job.LastTaskId = id;
        var t = await c.Wait(id, p => { job.Progress = p.Progress; }, job.Cts.Token);
        job.Credits += t.CreditsConsumed;
        if (!t.Succeeded) throw new TripoException(0, $"{job.Status} {t.Status}" + (t.Error != null ? ": " + t.Error : "") + " (failed tasks are not charged)");
        return t;
    }

    static async Task DownloadResult(TripoClient c, TripoJob job, TripoTask t, string outDir)
    {
        Begin(job, "Download");
        if (t.ModelUrl == null) throw new TripoException(0, "Tripo returned no model.");
        Directory.CreateDirectory(outDir);
        var baseName = Safe(job.Title) + "_" + t.Id[..Math.Min(8, t.Id.Length)];
        var file = Path.Combine(outDir, baseName + TripoClient.ExtensionOf(t.ModelUrl));
        await c.Download(t.ModelUrl, file, job.Cts.Token);
        if (t.RenderedImageUrl != null)
        {
            try
            {
                var prev = Path.Combine(outDir, baseName + "_preview" + TripoClient.ExtensionOf(t.RenderedImageUrl).Replace(".glb", ".png"));
                await c.Download(t.RenderedImageUrl, prev, job.Cts.Token);
                job.PreviewFile = prev;
            }
            catch { }
        }
        job.ResultFile = file;
        job.LastTaskId = t.Id;
        job.Progress = 100;
        job.Status = "done";
    }

    public static string Safe(string s)
    {
        var chars = (s ?? "tripo").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) && ch < 128 ? ch : '_').ToArray();
        var r = new string(chars).Trim('_');
        while (r.Contains("__")) r = r.Replace("__", "_");
        if (r.Length > 40) r = r[..40].TrimEnd('_');
        return r.Length == 0 ? "tripo" : r;
    }
}
