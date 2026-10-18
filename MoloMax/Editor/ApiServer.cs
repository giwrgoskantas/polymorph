using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoloMax.Editor;

/// <summary>
/// Polymorph scripting API: a small JSON-RPC server on 127.0.0.1 only. The Python package and the MCP server talk to it.
/// Requests run on the UI thread between two frames (the scene is never touched from another thread).
/// Every request needs the X-Polymorph-Token header (written to %APPDATA%\Polymorph\api.json), so a web page can't drive it.
/// </summary>
public sealed class ApiServer : IDisposable
{
    public sealed record Param(string Name, string Type, string Description, bool Required = false);
    public sealed record Method(string Name, string Description, Param[] Params, Func<JsonObject, object> Run);

    readonly Dictionary<string, Method> methods = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<(Method M, JsonObject P, TaskCompletionSource<object> Done)> queue = new();
    TcpListener listener;
    Thread thread;
    volatile bool running;
    public int Port { get; private set; }
    public string Token { get; private set; }
    public int Calls;
    public string LastMethod;
    public string Error;

    public IEnumerable<Method> Methods => methods.Values.OrderBy(m => m.Name);
    public bool Running => running;

    public void Add(string name, string description, Param[] ps, Func<JsonObject, object> run) => methods[name] = new Method(name, description, ps, run);

    public bool Start(int port, string infoFile)
    {
        Stop();
        try
        {
            Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            running = true; Error = null;
            thread = new Thread(Loop) { IsBackground = true, Name = "Polymorph API" };
            thread.Start();
            if (infoFile != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(infoFile));
                File.WriteAllText(infoFile, JsonSerializer.Serialize(new { port = Port, token = Token, pid = Environment.ProcessId, app = "Polymorph", version = "0.1" }));
            }
            return true;
        }
        catch (Exception e) { Error = e.Message; running = false; return false; }
    }

    public void Stop()
    {
        running = false;
        try { listener?.Stop(); } catch { }
        listener = null;
    }

    public void Dispose() => Stop();

    /// <summary>UI thread, once per frame: runs the queued calls.</summary>
    public void Pump(int maxPerFrame = 64)
    {
        for (int i = 0; i < maxPerFrame && queue.TryDequeue(out var job); i++)
        {
            try { LastMethod = job.M.Name; Calls++; job.Done.TrySetResult(job.M.Run(job.P ?? new JsonObject())); }
            catch (Exception e) { job.Done.TrySetException(e); }
        }
    }

    /// <summary>Runs a call from inside the app (self test), on the calling (UI) thread.</summary>
    public object CallLocal(string method, JsonObject p)
    {
        if (!methods.TryGetValue(method, out var m)) throw new InvalidOperationException("Unknown method " + method);
        return m.Run(p ?? new JsonObject());
    }

    void Loop()
    {
        while (running)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Handle(c));
        }
    }

    static readonly JsonSerializerOptions json = new() { IncludeFields = true, WriteIndented = false, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    void Handle(TcpClient c)
    {
        using var _ = c;
        try
        {
            c.ReceiveTimeout = 30000;
            var s = c.GetStream();
            // request line + headers
            var head = new StringBuilder(); var buf = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n") && head.Length < 16384) { if (s.Read(buf, 0, 1) <= 0) return; head.Append((char)buf[0]); }
            var lines = head.ToString().Split("\r\n");
            var req = lines[0].Split(' ');
            string verb = req[0], path = req.Length > 1 ? req[1] : "/";
            var headers = lines.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
            int len = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
            var body = new byte[len]; int got = 0;
            while (got < len) { int r = s.Read(body, got, len - got); if (r <= 0) break; got += r; }
            if (!headers.TryGetValue("x-polymorph-token", out var tok) || tok != Token) { Reply(s, 401, new { error = "Missing or wrong X-Polymorph-Token (see %APPDATA%\\Polymorph\\api.json)." }); return; }
            if (verb == "GET" && path.StartsWith("/api"))
            {
                Reply(s, 200, new { app = "Polymorph", version = "0.1", methods = Methods.Select(m => new { name = m.Name, description = m.Description, @params = m.Params.Select(p => new { name = p.Name, type = p.Type, description = p.Description, required = p.Required }) }) });
                return;
            }
            if (verb != "POST") { Reply(s, 405, new { error = "POST /rpc" }); return; }
            var msg = JsonNode.Parse(Encoding.UTF8.GetString(body, 0, got))?.AsObject() ?? new JsonObject();
            var name = msg["method"]?.GetValue<string>() ?? "";
            if (!methods.TryGetValue(name, out var m)) { Reply(s, 404, new { error = $"Unknown method '{name}'. GET /api lists them." }); return; }
            var job = (m, msg["params"] as JsonObject, new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously));
            if (job.Item2 != null) msg.Remove("params"); // detach so it can be used alone
            queue.Enqueue(job);
            if (!job.Item3.Task.Wait(TimeSpan.FromSeconds(120))) { Reply(s, 504, new { error = "Polymorph is busy (a dialog is open?)" }); return; }
            if (job.Item3.Task.IsFaulted) { var e = job.Item3.Task.Exception?.GetBaseException(); Reply(s, 400, new { error = e?.Message }); return; }
            Reply(s, 200, new { result = job.Item3.Task.Result });
        }
        catch (Exception e)
        {
            try { Reply(c.GetStream(), 500, new { error = e.Message }); } catch { }
        }
    }

    static void Reply(Stream s, int code, object o)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(o, json);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        s.Write(head); s.Write(bytes); s.Flush();
    }
}
