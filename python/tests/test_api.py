"""End-to-end test of the Python package and the MCP server against a running Polymorph (run by the self test)."""
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import polymorph as pm  # noqa: E402

fails = 0


def check(name, ok):
    global fails
    print(("  ok   " if ok else "  FAIL ") + name, flush=True)
    if not ok:
        fails += 1


pm.connect()
info = pm.info()
check("python: connected to %s %s" % (info["app"], info["version"]), info["app"] == "Polymorph")
b = pm.create("cylinder", name="py_bollard", Radius=0.12, Height=0.9, Sides=12)
check("python: create a cylinder", b["name"] == "py_bollard" and b["faces"] > 12)
pm.add_modifier(b, "Edit Poly")
pm.edit_poly(b, "inset", amount=0.03)
r = pm.edit_poly(b, "extrude", amount=0.05)
ev = pm.mesh(b, evaluated=True)
check("python: Edit Poly inset + extrude in the stack (%d faces after)" % len(ev["faces"]), len(ev["faces"]) > b["faces"] and r["type"] == "Edit Poly")
u = pm.unwrap(b)
check("python: Unwrap UVW (%d charts)" % u["charts"], u["charts"] >= 3)
mods = pm.modifiers(b)
check("python: stack = %s" % [m["type"] for m in mods], [m["type"] for m in mods] == ["Edit Poly", "Unwrap UVW"])
pm.set(b, gta={"Role": "Prop"}, position=[0, 5, 0])
made = pm.instance(b, count=4, offset=[1.5, 0, 0])
check("python: 4 instances (5 share the mesh)", len(made) == 4 and made[-1]["instances"] == 5 and len(pm.instances(b)) == 5)
check("python: objects() finds 5 bollards", len(pm.objects(filter="py_bollard")) == 5)
check("python: GTA check runs", isinstance(pm.check(), list))
try:
    pm.call("no.such.method")
    check("python: unknown method raises", False)
except pm.PolymorphError:
    check("python: unknown method raises", True)

# MCP server over stdio
mcp = subprocess.Popen([sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "polymorph_mcp.py")],
                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=os.environ.copy())


def rpc(i, method, params=None):
    mcp.stdin.write(json.dumps({"jsonrpc": "2.0", "id": i, "method": method, "params": params or {}}) + "\n")
    mcp.stdin.flush()
    return json.loads(mcp.stdout.readline())


init = rpc(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}})
check("mcp: initialize -> %s" % init["result"]["serverInfo"]["name"], init["result"]["serverInfo"]["name"] == "polymorph")
mcp.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
tools = rpc(2, "tools/list")["result"]["tools"]
names = [t["name"] for t in tools]
check("mcp: %d tools (object_create, modifier_add, uv_unwrap, gta_export, view_screenshot...)" % len(tools),
      all(n in names for n in ["object_create", "modifier_add", "uv_unwrap", "gta_export", "view_screenshot", "polymorph_status"]))
res = rpc(3, "tools/call", {"name": "object_create", "arguments": {"kind": "box", "name": "mcp_box", "position": [0, -3, 0]}})["result"]
check("mcp: object_create made a box", not res["isError"] and "mcp_box" in res["content"][0]["text"])
shot = rpc(4, "tools/call", {"name": "view_screenshot", "arguments": {}})["result"]
check("mcp: view_screenshot returns a PNG image", not shot["isError"] and shot["content"][0]["type"] == "image" and len(shot["content"][0]["data"]) > 1000)
mcp.stdin.close()
mcp.wait(5)
print("python api test: %d failed" % fails, flush=True)
sys.exit(1 if fails else 0)
