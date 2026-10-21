"""Polymorph Python API.

Drives a running Polymorph (3D modelling for GTA V & FiveM) from Python: scene, objects, instances,
mesh data, the modifier stack (Edit Poly, Unwrap UVW, ...), GTA checks and the FiveM export.

    import polymorph as pm
    pm.connect()                       # finds the running app (%APPDATA%/Polymorph/api.json)
    box = pm.create("box", name="crate", Width=1, Length=1, Height=1)
    pm.add_modifier(box, "Edit Poly")
    pm.edit_poly(box, "extrude", amount=0.3)
    pm.unwrap(box)
    for i in range(5):
        pm.duplicate(box, instance=True, offset=[1.5, 0, 0])
    print(pm.check())
    pm.export("C:/FiveM/resources")

Only the standard library is used. Each call is one undo step in Polymorph.
"""
import json
import os
import urllib.error
import urllib.request

__version__ = "1.0.0"
__all__ = ["Polymorph", "PolymorphError", "connect"]


class PolymorphError(RuntimeError):
    pass


def _info_file():
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    return os.path.join(base, "Polymorph", "api.json")


def _ref(obj):
    """An object reference: id (int), name (str) or a dict returned by the API."""
    if obj is None:
        return {}
    if isinstance(obj, dict):
        return {"id": obj["id"]}
    if isinstance(obj, int):
        return {"id": obj}
    return {"name": str(obj)}


class Polymorph:
    def __init__(self, port=None, token=None, host="127.0.0.1", timeout=180):
        if port is None or token is None:
            port = port or os.environ.get("POLYMORPH_PORT")
            token = token or os.environ.get("POLYMORPH_TOKEN")
            if not port or not token:
                try:
                    with open(_info_file(), encoding="utf-8") as f:
                        info = json.load(f)
                    port = port or info["port"]
                    token = token or info["token"]
                except (OSError, ValueError, KeyError):
                    raise PolymorphError("Polymorph is not running (no api.json). Start Polymorph first.")
        self.url = "http://%s:%s" % (host, port)
        self.token = token
        self.timeout = timeout

    # ---- transport
    def _request(self, path, body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(self.url + path, data=data, method="POST" if body is not None else "GET",
                                     headers={"X-Polymorph-Token": self.token, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as r:
                return json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            try:
                msg = json.loads(e.read().decode("utf-8")).get("error")
            except Exception:
                msg = str(e)
            raise PolymorphError(msg)
        except urllib.error.URLError as e:
            raise PolymorphError("Can't reach Polymorph (%s). Is it running?" % e.reason)

    def call(self, method, **params):
        """Calls any API method, e.g. call('scene.list', filter='wall')."""
        params = {k: v for k, v in params.items() if v is not None}
        return self._request("/rpc", {"method": method, "params": params}).get("result")

    def methods(self):
        """The API methods with their parameters (what the MCP server exposes as tools)."""
        return self._request("/api")["methods"]

    # ---- app / scene
    def info(self): return self.call("app.info")
    def objects(self, filter=None, type=None): return self.call("scene.list", filter=filter, type=type)
    def new(self): return self.call("scene.new")
    def open(self, path): return self.call("scene.open", path=path)
    def save(self, path=None): return self.call("scene.save", path=path)
    def undo(self): return self.call("scene.undo")
    def redo(self): return self.call("scene.redo")

    # ---- objects
    def get(self, obj): return self.call("object.get", **_ref(obj))

    def create(self, kind="box", name=None, position=None, rotation=None, **size):
        """Primitive: box, plane, sphere, cylinder, cone, tube, room, stairs, door, portal, collision_box, tufted.
        Size fields as keywords: Width, Length, Height, Radius, Sides, Segments, Steps, RoomWidth, ..."""
        return self.call("object.create", kind=kind, name=name, position=position, rotation=rotation, params=size or None)

    def create_mesh(self, verts, faces, name=None, position=None, type=None):
        return self.call("object.create_mesh", verts=[list(v) for v in verts], faces=[list(f) for f in faces], name=name, position=position, type=type)

    def set(self, obj, **fields):
        """name=... finds the object; use rename= to rename. position / rotation (degrees) / scale / hidden / folder / parent / type / gta={...}"""
        return self.call("object.set", **_ref(obj), **fields)

    def delete(self, *objs):
        ids = [o["id"] if isinstance(o, dict) else o if isinstance(o, int) else self.get(o)["id"] for o in objs]
        return self.call("object.delete", ids=ids)

    def duplicate(self, obj, instance=False, count=1, offset=None):
        """Copies or instances (shared mesh; GTA: same model placed again). Returns the new objects."""
        return self.call("object.duplicate", **_ref(obj), instance=instance, count=count, offset=offset)

    def instance(self, obj, count=1, offset=None): return self.duplicate(obj, True, count, offset)
    def make_unique(self, obj): return self.call("object.make_unique", **_ref(obj))
    def instances(self, obj): return self.call("object.instances", **_ref(obj))
    def selection(self): return self.call("selection.get")
    def select(self, *objs): return self.call("selection.set", ids=[o["id"] if isinstance(o, dict) else o for o in objs])

    def mesh(self, obj, evaluated=False, uv=False):
        """{'verts': [x,y,z,...], 'faces': [[i,j,k],...], 'materials': [...], 'uv': [...]}"""
        return self.call("mesh.get", **_ref(obj), evaluated=evaluated, uv=uv)

    # ---- modifiers
    def modifier_types(self): return self.call("modifier.types")
    def modifiers(self, obj): return self.call("modifier.list", **_ref(obj))
    def add_modifier(self, obj, type, index=None, **props): return self.call("modifier.add", **_ref(obj), type=type, index=index, props=props or None)
    def set_modifier(self, obj, index, **props): return self.call("modifier.set", **_ref(obj), index=index, props=props)
    def remove_modifier(self, obj, index): return self.call("modifier.remove", **_ref(obj), index=index)
    def move_modifier(self, obj, frm, to): return self.call("modifier.move", **_ref(obj), **{"from": frm, "to": to})
    def apply_modifier(self, obj, index): return self.call("modifier.apply", **_ref(obj), index=index)

    def edit_poly(self, obj, op, amount=None, direction=None, outline=None, index=None):
        """Edit Poly step: op = extrude / inset / bevel (faces facing 'direction', default up) or weld."""
        return self.call("modifier.edit_poly", **_ref(obj), op=op, amount=amount, direction=direction, outline=outline, index=index)

    def unwrap(self, obj, angle=None, density=None, texture_size=None):
        """Auto UVs (seams, unfold, texel density, pack) in an Unwrap UVW modifier."""
        return self.call("uv.unwrap", **_ref(obj), angle=angle, density=density, texture_size=texture_size)

    # ---- GTA / view
    def check(self): return self.call("gta.check")
    def fix_all(self): return self.call("gta.fix_all")
    def settings(self, **changes): return self.call("gta.settings", set=changes or None)
    def export(self, folder=None): return self.call("gta.export", folder=folder)
    def frame(self): return self.call("view.frame")
    def screenshot(self, path): return self.call("view.screenshot", path=path)

    # ---- scripting inside Polymorph (like bpy in Blender)
    def run(self, code):
        """Runs Python inside Polymorph ('import poly', like bpy), one undo step.
        Returns {'ok', 'output' (what print() wrote), 'result' (a last bare expression), 'error', 'ms'}.
        Example: run('b = poly.create("box", (0, 0, 1), "crate")\nb.Name')"""
        r = self.call("script.run", code=code)
        if r and not r.get("ok") and r.get("error"):
            raise PolymorphError(r["error"])
        return r


_default = None


def connect(port=None, token=None):
    """Connects the module-level helpers (pm.create, pm.objects, ...) to the running Polymorph."""
    global _default
    _default = Polymorph(port, token)
    return _default


def _client():
    return _default or connect()


def __getattr__(name):
    # pm.create(...), pm.objects(), ... use the default connection
    if hasattr(Polymorph, name) and not name.startswith("_"):
        return getattr(_client(), name)
    raise AttributeError(name)
