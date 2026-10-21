"""poly - Python inside Polymorph, like bpy in Blender.

    import poly
    box = poly.create("box", (0, 0, 1), name="crate")
    room = poly.add("room_01", poly.Primitives.Box(6, 4, 3), (10, 0, 0))
    poly.MeshOps.FlipAll(room.Mesh); room.Touch()
    poly.set_material(room, poly.new_material("wall_paint", "molo_wall_01"))
    poly.add_light("spot", (10, 0, 2.5))
    print(len(poly.context.scene.Objects))

Everything of the editor is reachable: poly.context (scene, selected, active, cursor), the mesh tools
(poly.Primitives, poly.MeshOps, poly.PolyMesh, poly.UvUnwrap), and any editor member with poly.invoke / poly.get / poly.set.
Add-ons: a .py file in the add-ons folder with register() (and unregister()); poly.register_menu("My tool", func)
puts func in the Add-ons menu. A run or a menu click is one undo step.
"""
import sys
import clr

clr.AddReference("Polymorph")
clr.AddReference("MoloMax.Core")

from System.Numerics import Vector3  # noqa: E402
from MoloMax.Editor import EditorApp  # noqa: E402
from MoloMax.Core.Mesh import PolyMesh, Primitives, MeshOps, UvUnwrap  # noqa: E402,F401
from MoloMax.Core.Scenes import GtaObjectType, GtaLightKind  # noqa: E402,F401

__all__ = ["context", "vec", "create", "add", "delete", "find", "find_all", "objects", "select", "select_none", "level",
           "sub", "import_file", "open_file", "save_as", "material", "new_material", "set_material", "add_light", "frame",
           "frame_all", "status", "call", "invoke", "get", "set", "members", "register_menu", "Vector3", "PolyMesh",
           "Primitives", "MeshOps", "UvUnwrap", "GtaObjectType", "GtaLightKind"]


def _g():
    g = EditorApp.PyContext
    if g is None:
        raise RuntimeError("Polymorph is not ready")
    return g


def vec(x, y=None, z=None):
    """(x, y, z) / [x, y, z] / Vector3 / three numbers -> Vector3."""
    if y is None:
        if isinstance(x, Vector3):
            return x
        x, y, z = x
    return Vector3(float(x), float(y), float(z))


class _Context:
    """Like bpy.context: what is open and selected right now."""

    @property
    def app(self): return _g().app

    @property
    def scene(self): return _g().scene

    @property
    def selected(self): return list(_g().sel)

    @property
    def active(self): return _g().active

    @property
    def cursor(self): return _g().cursor

    @cursor.setter
    def cursor(self, v): _g().cursor = vec(v)


context = _Context()


# ---- objects
def create(kind="box", at=(0, 0, 0), name=None):
    """box, plane, sphere, cylinder, cone, tube, room, wall, stairs, door, portal, collisionbox, tufted."""
    return _g().Create(kind, vec(at), name)


def add(name, mesh, at=(0, 0, 0), type=None):
    """Adds an object from a PolyMesh you built (Primitives.Box(...), PolyMesh(), MeshOps...)."""
    if type is None:
        return _g().Add(name, mesh, vec(at))
    return _g().Add(name, mesh, vec(at), type)


def delete(obj): _g().Delete(obj)
def find(name_or_id): return _g().Find(name_or_id)
def find_all(text): return list(_g().FindAll(text))
def objects(): return list(_g().Objects)


def select(*objs):
    if not objs:
        _g().SelectNone()
    else:
        from System import Array
        from MoloMax.Core.Scenes import SceneObject
        _g().Select(Array[SceneObject](list(objs)))


def select_none(): _g().SelectNone()
def level(name): _g().Level(name)
def sub(obj): return _g().Sub(obj)


# ---- files
def import_file(path, at=None): return list(_g().Import(path, vec(at) if at is not None else None))
def open_file(path): _g().Open(path)
def save_as(path): _g().SaveAs(path)


# ---- materials, lights
def material(name): return _g().Material(name)
def new_material(name, diffuse=None, shader="normal_spec"): return _g().NewMaterial(name, diffuse, shader)


def set_material(obj, mat, faces=None):
    if faces is None:
        _g().SetMaterial(obj, mat)
    else:
        from System.Collections.Generic import List
        from System import Int32
        lst = List[Int32]()
        for f in faces:
            lst.Add(int(f))
        _g().SetMaterial(obj, mat, lst)


def add_light(kind="point", at=None): return _g().AddLight(kind, vec(at) if at is not None else None)


# ---- view / misc
def frame(): _g().Frame()
def frame_all(): _g().FrameAll()
def status(text): _g().Status(str(text))


def call(method, **args):
    """Any API method: call("gta.export", folder="C:/x")."""
    import json
    from System.Text.Json.Nodes import JsonNode
    node = JsonNode.Parse(json.dumps(args or {}))
    return _g().app.Api.CallLocal(method, node)


def invoke(method, *args): return _g().Invoke(method, *args)
def get(member): return _g().Get(member)
def set(member, value): _g().Set(member, value)  # noqa: A001
def members(text=""): return _g().Members(text)


# ---- add-ons
_menu = []


def register_menu(label, func):
    """Puts func in Polymorph's Add-ons menu (one undo step per click)."""
    _menu[:] = [m for m in _menu if m[0] != label] + [(label, func)]
    EditorApp.PyMenuChanged()


def _menu_labels(): return [m[0] for m in _menu]


def _menu_run(i):
    import io
    import traceback
    buf = io.StringIO()
    old = sys.stdout, sys.stderr
    sys.stdout = sys.stderr = buf
    err = None
    try:
        _menu[int(i)][1]()
    except Exception:
        err = traceback.format_exc(limit=6)
    finally:
        sys.stdout, sys.stderr = old
    return buf.getvalue(), err


_addons = {}


def _load_addons(folder):
    """Imports every add-on of a folder and calls its register()."""
    import importlib.util
    import os
    import traceback
    report = []
    if not os.path.isdir(folder):
        return report
    if folder not in sys.path:
        sys.path.insert(0, folder)
    for fn in sorted(os.listdir(folder)):
        if not fn.endswith(".py") or fn.startswith("_"):
            continue
        path = os.path.join(folder, fn)
        name = "addon_" + fn[:-3]
        try:
            old = _addons.get(path)
            if old is not None and hasattr(old, "unregister"):
                old.unregister()
            spec = importlib.util.spec_from_file_location(name, path)
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)
            if hasattr(mod, "register"):
                mod.register()
            _addons[path] = mod
            report.append((fn, None))
        except Exception:
            report.append((fn, traceback.format_exc(limit=4)))
    return report


# ---- what the editor calls to run a script
def _run(code):
    """Runs code like Blender's text editor; the value of a last bare expression is returned too."""
    import ast
    import io
    import traceback
    buf = io.StringIO()
    old = sys.stdout, sys.stderr
    sys.stdout = sys.stderr = buf
    result = None
    err = None
    g = {"__name__": "__main__", "poly": sys.modules[__name__]}
    exec("from poly import *", g)
    try:
        tree = ast.parse(code, "<script>", "exec")
        last = None
        if tree.body and isinstance(tree.body[-1], ast.Expr):
            last = ast.Expression(tree.body.pop().value)
        exec(compile(tree, "<script>", "exec"), g)
        if last is not None:
            v = eval(compile(last, "<script>", "eval"), g)
            if v is not None:
                result = _fmt(v)
    except SyntaxError:
        err = "Syntax error:\n" + traceback.format_exc(limit=0)
    except Exception:
        err = traceback.format_exc(limit=8)
    finally:
        sys.stdout, sys.stderr = old
    return buf.getvalue(), result, err


def _fmt(v):
    try:
        if hasattr(v, "Mesh") and hasattr(v, "Name") and hasattr(v, "Id"):
            return "%s (id %d, %d faces)" % (v.Name, v.Id, v.Mesh.Faces.Count if v.Mesh is not None else 0)
        if isinstance(v, (list, tuple)):
            return ", ".join(_fmt(x) for x in v[:200])
        return str(v)
    except Exception:
        return repr(v)
