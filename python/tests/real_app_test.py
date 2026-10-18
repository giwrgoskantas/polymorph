"""Starts the real Polymorph (not the self test), drives it from Python and through the MCP server, exports, then closes it cleanly."""
import json, os, subprocess, sys, time, tempfile, shutil
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(here, ".."))
import polymorph as pm

exe = sys.argv[1]
out = sys.argv[2]
os.makedirs(out, exist_ok=True)
info_file = os.path.join(os.environ["APPDATA"], "Polymorph", "api.json")
lines = []
def check(name, ok):
    lines.append(("  ok   " if ok else "  FAIL ") + name); print(lines[-1], flush=True)

t0 = time.time()
before = os.path.getmtime(info_file) if os.path.exists(info_file) else 0
proc = subprocess.Popen([exe])
while time.time() - t0 < 60:
    if os.path.exists(info_file) and os.path.getmtime(info_file) > before:
        try:
            if json.load(open(info_file)).get("pid") == proc.pid: break
        except ValueError: pass
    time.sleep(0.3)
check("Polymorph started and opened its API in %.1f s" % (time.time() - t0), os.path.exists(info_file) and json.load(open(info_file)).get("pid") == proc.pid)
time.sleep(1.5)
c = pm.connect()
info = c.info()
check("python: talks to the running app (%s %s, %d objects)" % (info["app"], info["version"], info["objects"]), info["app"] == "Polymorph")
old = os.path.join(os.environ["APPDATA"], "MoloMax"); new = os.path.join(os.environ["APPDATA"], "Polymorph")
olds = sorted(os.listdir(old)) if os.path.isdir(old) else []
check("MoloMax settings copied to Polymorph (%s)" % ", ".join(olds), all(os.path.exists(os.path.join(new, f)) for f in olds) and os.path.exists(os.path.join(new, ".from_molomax")))

# the shipped example
env = dict(os.environ, PYTHONPATH=os.path.join(here, ".."))
ex = subprocess.run([sys.executable, os.path.join(here, "..", "examples", "street_props.py")], capture_output=True, text=True, env=env, timeout=120)
print(ex.stdout.strip()); print(ex.stderr.strip())
check("example street_props.py runs (%s)" % (ex.stdout.strip().splitlines()[-1] if ex.stdout.strip() else ex.stderr.strip()[-80:]), ex.returncode == 0 and "10 bollards" in ex.stdout)

# GTA export through the API
res = c.settings(ResourceName="polymorph_realtest")
rep = c.export(out)
ydr = [f for f in rep["files"] if f.endswith(".ydr")]
check("export: %d ydr, %d entities, errors %s" % (len(ydr), rep["entities"], rep["errors"]), not rep["errors"] and len(ydr) == 1 and rep["entities"] >= 10)

# MCP server against the real app
mcp = subprocess.Popen([sys.executable, os.path.join(here, "..", "polymorph_mcp.py")], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
def rpc(i, m, p=None):
    mcp.stdin.write(json.dumps({"jsonrpc": "2.0", "id": i, "method": m, "params": p or {}}) + "\n"); mcp.stdin.flush()
    return json.loads(mcp.stdout.readline())
rpc(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "realtest", "version": "1"}})
tools = rpc(2, "tools/list")["result"]["tools"]
st = rpc(3, "tools/call", {"name": "polymorph_status", "arguments": {}})["result"]
lst = rpc(4, "tools/call", {"name": "scene_list", "arguments": {"filter": "bollard"}})["result"]
shot = rpc(5, "tools/call", {"name": "view_screenshot", "arguments": {}})["result"]
if not shot["isError"]:
    import base64
    open(os.path.join(out, "mcp_screenshot.png"), "wb").write(base64.b64decode(shot["content"][0]["data"]))
check("mcp: %d tools, status ok, scene_list sees the bollards, screenshot %d KB" % (len(tools), len(shot["content"][0].get("data", "")) * 3 // 4 // 1024),
      not st["isError"] and "bollard" in lst["content"][0]["text"] and not shot["isError"])
mcp.stdin.close(); mcp.wait(5)

c.call("app.quit", discard=True)
try: proc.wait(30); closed = True
except subprocess.TimeoutExpired: closed = False; proc.kill()
check("app.quit closes Polymorph cleanly (exit code %s)" % proc.returncode, closed and proc.returncode == 0)
sess = os.path.join(os.environ["APPDATA"], "Polymorph", "autosave", "session.json")
check("no crash-recovery marker left behind", not os.path.exists(sess))
open(os.path.join(out, "report.txt"), "w").write("\n".join(lines))
sys.exit(1 if any("FAIL" in l for l in lines) else 0)
