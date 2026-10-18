"""In-game test helpers (local FiveM test server): install a resource, run console commands through molo_devbridge,
move the camera and take screenshots through molo_cinecam."""
import json, os, shutil, time, urllib.request, base64, glob

SERVER = os.path.join(os.path.expanduser("~"), "Desktop", "base_para_testes")
RES = os.path.join(SERVER, "resources")
TEST_CAT = os.path.join(RES, "[polytest]")
BRIDGE = os.path.join(RES, "[local]", "molo_devbridge")
URL = "http://127.0.0.1:30120/molo_cinecam/cmd"
TOKEN = "mC7vQ2kX9pL4wR8zNt3H"  # the local test server's cinecam_token (server.cfg)


def cc(cmd, timeout=40, **args):
    body = json.dumps({"cmd": cmd, "args": args}).encode()
    req = urllib.request.Request(URL, data=body, method="POST", headers={"X-Cinecam-Token": TOKEN, "Content-Type": "application/json"})
    try:
        return json.loads(urllib.request.urlopen(req, timeout=timeout).read().decode())
    except urllib.error.HTTPError as e:
        try: return json.loads(e.read().decode())
        except Exception: return {"ok": False, "error": str(e)}
    except Exception as e:
        return {"ok": False, "error": str(e)}


def bridge(lines, timeout=40):
    """Runs console commands on the server; returns the new log lines."""
    out = os.path.join(BRIDGE, "out.txt")
    before = open(out, encoding="utf-8", errors="replace").read() if os.path.exists(out) else ""
    rid = "pt%d" % int(time.time() * 1000)
    with open(os.path.join(BRIDGE, "cmd.txt"), "w", encoding="utf-8") as f:
        f.write(rid + "\n" + "\n".join(lines) + "\n")
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(0.5)
        txt = open(out, encoding="utf-8", errors="replace").read() if os.path.exists(out) else ""
        if ("done " + rid) in txt:
            return txt[len(before):] if txt.startswith(before[:200]) else txt[-2000:]
    return "TIMEOUT"


def install(src, name):
    os.makedirs(TEST_CAT, exist_ok=True)
    dst = os.path.join(TEST_CAT, name)
    if os.path.exists(dst): shutil.rmtree(dst)
    shutil.copytree(src, dst)
    return dst


def shot(path, wait=1500, quality=0.85):
    r = cc("screenshot", timeout=45, wait=wait, quality=quality)
    if not r.get("ok"): return r.get("error")
    data = r["data"]["image"]
    if "," in data: data = data.split(",", 1)[1]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, "wb").write(base64.b64decode(data))
    return path


def client_log_tail(n=60, grep=None):
    logs = sorted(glob.glob(os.path.join(os.environ["LOCALAPPDATA"], "FiveM", "FiveM.app", "logs", "CitizenFX_log_*.log")), key=os.path.getmtime)
    if not logs: return []
    lines = open(logs[-1], encoding="utf-8", errors="replace").read().splitlines()
    if grep: lines = [l for l in lines if any(g.lower() in l.lower() for g in grep)]
    return lines[-n:]
