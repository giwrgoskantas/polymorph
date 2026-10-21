# Polymorph Python API and MCP server

Polymorph runs a small JSON-RPC server on `127.0.0.1` (port 7820 by default, token protected).
The connection details are written to `%APPDATA%\Polymorph\api.json` when the app starts.

## Python

```python
import sys; sys.path.append(r"C:\path\to\Polymorph\python")
import polymorph as pm

pm.connect()
box = pm.create("box", name="molo_crate", Width=1, Length=1, Height=1)
pm.add_modifier(box, "Edit Poly")
pm.edit_poly(box, "extrude", amount=0.3)          # faces facing up
pm.unwrap(box)                                     # Unwrap UVW: seams, unfold, density, pack
pm.instance(box, count=5, offset=[1.5, 0, 0])      # one model, six placements
print(pm.check())                                  # GTA / FiveM checks
pm.export("C:/FiveM/resources")
```

Every call is one undo step. `pm.call("method", ...)` reaches any method; `pm.Polymorph().methods()` lists them.
Scripts can also be run from inside Polymorph: **Tools > Run Python Script** (output in the Python window).

## Python inside Polymorph (like bpy)

Polymorph carries its own Python 3.12 (nothing to install). Scripts and add-ons run **inside** the app, with the scene,
the mesh tools and the editor at hand, like `bpy` in Blender. One run is one undo step.

- **Add-ons > Python Script Editor**: write, then Run (Ctrl+Enter). `print()` and errors show below.
- **Add-ons**: a `.py` file with `register()` in the add-ons folder (Add-ons > Open add-ons folder, `%APPDATA%\Polymorph\addons`).
  `poly.register_menu("My tool", func)` puts `func` in the Add-ons menu. See `python/addons/molo_basics.py`.
- From outside: `pm.run(code)` (this package), the API method `script.run`, or the MCP tool `script_run` (Claude).

```python
import poly

box = poly.create("box", (0, 0, 0), "room_floor")
room = poly.add("room_01", poly.Primitives.Box(6, 4, 3), (10, 0, 0))
poly.MeshOps.FlipAll(room.Mesh); room.Touch()          # a room: faces inward
poly.set_material(room, poly.new_material("wall_paint", "molo_wall_01"))
poly.add_light("spot", (10, 0, 2.5))
print(len(poly.objects()), "objects")
```

`poly`: `context` (`scene`, `selected`, `active`, `cursor`), `create(kind, at, name)`, `add(name, PolyMesh, at)`, `delete(o)`,
`find(name)`, `find_all(text)`, `objects()`, `select(*objs)`, `level("edge")`, `sub(o)` (faces / edges / points selected),
`import_file(path)`, `open_file(path)`, `save_as(path)`, `material(name)`, `new_material(name, diffuse, shader)`,
`set_material(o, m, faces)`, `add_light(kind, at)`, `frame()`, `call(api_method, **args)`, and for anything else in the editor
(private members too) `invoke(method, *args)`, `get(member)`, `set(member, value)`, `members(text)`.
Mesh tools: `poly.Primitives`, `poly.MeshOps`, `poly.PolyMesh`, `poly.UvUnwrap`, `poly.Vector3` (the .NET classes, through pythonnet).

C# also works (`script.run_cs`), same helpers, for heavy loops.

## MCP (Claude)

`polymorph_mcp.py` is a standard-library MCP server over stdio. Every API method becomes a tool
(`scene_list`, `object_create`, `modifier_add`, `uv_unwrap`, `gta_check`, `gta_export`...) plus
`view_screenshot`, which returns a picture of the window.

Claude Desktop > Settings > Developer > Edit Config:

```json
{ "mcpServers": { "polymorph": { "command": "python", "args": ["C:/path/to/Polymorph/python/polymorph_mcp.py"] } } }
```

The exact config for your PC is in Polymorph: **Tools > Python & MCP** (Copy config).
