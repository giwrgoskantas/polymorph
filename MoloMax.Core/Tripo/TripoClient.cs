using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoloMax.Core.Tripo;

/// <summary>An error returned by the Tripo API (non-zero code), with its suggestion when there is one.</summary>
public sealed class TripoException : Exception
{
    public int Code { get; }
    public string Suggestion { get; }
    public TripoException(int code, string message, string suggestion = null) : base(message) { Code = code; Suggestion = suggestion; }

    public string Friendly => Code switch
    {
        1000 or 1001 => "Invalid Tripo API key (it starts with tsk_). Check it in the Tripo page.",
        2010 => "Not enough Tripo credits.",
        2008 => "Tripo refused the prompt or image (content policy).",
        2018 => "The model is too complex for this operation.",
        1007 or 2000 => "Tripo is busy (rate limit). Try again in a moment.",
        _ => Message + (string.IsNullOrEmpty(Suggestion) ? "" : " — " + Suggestion),
    };
}

/// <summary>State of a Tripo task (GET /v3/tasks/{id}).</summary>
public sealed class TripoTask
{
    public string Id, Type, Status; // queued running success failed cancelled
    public int Progress;
    public string ModelUrl, RenderedImageUrl, UvRenderUrl;
    public decimal CreditsConsumed;
    public string Error;
    public bool Finished => Status is "success" or "failed" or "cancelled" or "banned" or "expired";
    public bool Succeeded => Status == "success";

    public static TripoTask From(JsonNode d)
    {
        var o = d?["output"];
        string S(JsonNode n, string k) => n?[k]?.GetValueKind() == JsonValueKind.String ? n[k].GetValue<string>() : null;
        var t = new TripoTask
        {
            Id = S(d, "task_id"), Type = S(d, "type"), Status = S(d, "status") ?? "unknown",
            ModelUrl = S(o, "model_url") ?? S(o, "pbr_model") ?? S(o, "model"),
            RenderedImageUrl = S(o, "rendered_image_url") ?? S(o, "rendered_image"),
            UvRenderUrl = S(o, "uv_render_url"),
            Error = S(d, "error_message") ?? S(d, "error_msg"),
        };
        if (d?["progress"] is JsonNode p && p.GetValueKind() == JsonValueKind.Number) t.Progress = (int)p.GetValue<double>();
        if (d?["credits_consumed"] is JsonNode c && c.GetValueKind() == JsonValueKind.Number) t.CreditsConsumed = c.GetValue<decimal>();
        if (t.Error == null && d?["error_code"] is JsonNode ec) t.Error = "error " + ec.ToJsonString();
        return t;
    }
}

/// <summary>
/// Minimal client for the Tripo API v3 (openapi.tripo3d.ai, "Authorization: Bearer tsk_..."): upload, create
/// tasks, poll them, download results. The v2 API stops on 2026-10-31, so only v3 is used.
/// </summary>
public sealed class TripoClient : IDisposable
{
    public const string GlobalUrl = "https://openapi.tripo3d.ai";
    public const string ChinaUrl = "https://openapi.tripo3d.com";

    readonly HttpClient http;      // API calls, with the key
    readonly HttpClient download;  // signed CDN URLs and presigned PUTs: never send the key there
    readonly string baseUrl;

    public TripoClient(string apiKey, string baseUrl = GlobalUrl, HttpMessageHandler handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.Trim().StartsWith("tsk_"))
            throw new TripoException(1000, "A Tripo API key starts with tsk_.");
        this.baseUrl = (baseUrl ?? GlobalUrl).TrimEnd('/');
        http = handler != null ? new HttpClient(handler, false) : new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        download = handler != null ? new HttpClient(handler, false) : new HttpClient();
        download.Timeout = TimeSpan.FromMinutes(10);
    }

    public void Dispose() { http.Dispose(); download.Dispose(); }

    async Task<JsonNode> Send(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode root = null;
        try { root = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch { }
        int code = root?["code"] is JsonNode c && c.GetValueKind() == JsonValueKind.Number ? c.GetValue<int>() : (resp.IsSuccessStatusCode ? 0 : (int)resp.StatusCode);
        if (code != 0 || !resp.IsSuccessStatusCode)
        {
            string msg = root?["message"]?.ToString() ?? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            throw new TripoException(code == 0 ? (int)resp.StatusCode : code, msg, root?["suggestion"]?.ToString());
        }
        return root?["data"];
    }

    Task<JsonNode> Get(string path, CancellationToken ct) => Send(new HttpRequestMessage(HttpMethod.Get, baseUrl + path), ct);

    Task<JsonNode> Post(string path, JsonObject body, CancellationToken ct) => Send(new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    }, ct);

    public async Task<(decimal Balance, decimal Frozen)> GetBalance(CancellationToken ct = default)
    {
        var d = await Get("/v3/account/balance", ct).ConfigureAwait(false);
        decimal D(string k) => d?[k] is JsonNode n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<decimal>() : 0;
        return (D("balance"), D("frozen"));
    }

    /// <summary>Uploads an image (PNG/JPEG/WebP, 20 MB) or a model (GLB/FBX/OBJ/STL, 150 MB); returns the file token.</summary>
    public async Task<string> Upload(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("File not found", path);
        if (info.Length > 50L * 1024 * 1024)
        {
            var ext = info.Extension.TrimStart('.').ToLowerInvariant();
            var pre = await Post("/v3/files/presign", new JsonObject { ["format"] = ext }, ct).ConfigureAwait(false);
            var url = pre?["presigned_url"]?.ToString();
            var token = pre?["file_token"]?.ToString();
            if (url == null || token == null) throw new TripoException(0, "Unexpected presign response.");
            using var content = new StreamContent(File.OpenRead(path));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = await download.PutAsync(url, content, ct).ConfigureAwait(false);
            if (!put.IsSuccessStatusCode) throw new TripoException((int)put.StatusCode, $"Upload failed (HTTP {(int)put.StatusCode}).");
            return token;
        }
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));
        file.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(path));
        form.Add(file, "file", Path.GetFileName(path));
        var d = await Send(new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v3/files") { Content = form }, ct).ConfigureAwait(false);
        return d?["file_token"]?.ToString() ?? throw new TripoException(0, "Upload returned no file token.");
    }

    static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>Creates a task on a v3 route (e.g. /v3/generation/text-to-model) and returns its id. Never retried (it bills).</summary>
    public async Task<string> Create(string route, JsonObject body, CancellationToken ct = default)
    {
        var d = await Post(route, body, ct).ConfigureAwait(false);
        return d?["task_id"]?.ToString() ?? throw new TripoException(0, $"Unexpected response from {route}.");
    }

    public async Task<TripoTask> GetTask(string id, CancellationToken ct = default) =>
        TripoTask.From(await Get("/v3/tasks/" + Uri.EscapeDataString(id), ct).ConfigureAwait(false));

    /// <summary>Polls until the task finishes (2 s, then slower), reporting progress.</summary>
    public async Task<TripoTask> Wait(string id, Action<TripoTask> onProgress = null, CancellationToken ct = default)
    {
        double delay = 2;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TripoTask t;
            try { t = await GetTask(id, ct).ConfigureAwait(false); }
            catch (TripoException e) when (e.Code is 429 or 1007 or 2000 or >= 500 and < 600)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(32, delay *= 2)), ct).ConfigureAwait(false);
                continue;
            }
            onProgress?.Invoke(t);
            if (t.Finished) return t;
            await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
            delay = Math.Min(8, delay * 1.3);
        }
    }

    /// <summary>Downloads a signed result URL (no API key sent).</summary>
    public async Task Download(string url, string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        using var resp = await download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new TripoException((int)resp.StatusCode, $"Download failed (HTTP {(int)resp.StatusCode}). The link may have expired.");
        await using var fs = File.Create(path);
        await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
    }

    public static string ExtensionOf(string url)
    {
        var name = url.Split('?')[0];
        var ext = Path.GetExtension(name);
        return string.IsNullOrEmpty(ext) || ext.Length > 6 ? ".glb" : ext.ToLowerInvariant();
    }
}
