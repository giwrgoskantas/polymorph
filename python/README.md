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

## Scripting inside Polymorph (like bpy)

`script.run` (Python: `pm.run(code)`, MCP tool: `script_run`) runs **C# code inside Polymorph**, with the scene, the mesh tools
and the editor at hand, like Python in Blender. One run is one undo step; it returns what `print()` wrote and the last value.

```python
pm.run('''
var floor = Create("box", new Vector3(0, 0, 0), "room_floor");
var m = Primitives.Box(6, 4, 3);
MeshOps.FlipAll(m);                          // a room: faces inward
var room = Add("room_01", m, new Vector3(10, 0, 0));
SetMaterial(room, NewMaterial("wall_paint", "molo_wall_01"));
AddLight("spot", new Vector3(10, 0, 2.5f));
print(scene.Objects.Count);
''')
```

Globals: `app` (the editor), `scene`, `sel`, `active`, `cursor`, `print(...)`, `Find(name)`, `FindAll(text)`, `Create(kind, at, name)`,
`Add(name, PolyMesh, at)`, `Delete(o)`, `Select(...)`, `Level("edge")`, `Sub(o)` (faces / edges / points selected),
`Import(path)`, `Open(path)`, `SaveAs(path)`, `Material(name)`, `NewMaterial(name, diffuse, shader)`, `SetMaterial(o, m, faces)`,
`AddLight(kind, at)`, `Frame()`, `Call(apiMethod, args)`, and for anything else in the editor (private members too)
`Invoke(method, args...)`, `Get(member)`, `Set(member, value)`, `Members(text)`.
Imported namespaces: `System.Linq`, `System.Numerics`, `MoloMax.Core.Mesh` (PolyMesh, Primitives, MeshOps, UvUnwrap...),
`MoloMax.Core.Scenes`, `MoloMax.Core.Gta`.

## MCP (Claude)

`polymorph_mcp.py` is a standard-library MCP server over stdio. Every API method becomes a tool
(`scene_list`, `object_create`, `modifier_add`, `uv_unwrap`, `gta_check`, `gta_export`...) plus
`view_screenshot`, which returns a picture of the window.

Claude Desktop > Settings > Developer > Edit Config:

```json
{ "mcpServers": { "polymorph": { "command": "python", "args": ["C:/path/to/Polymorph/python/polymorph_mcp.py"] } } }
```

The exact config for your PC is in Polymorph: **Tools > Python & MCP** (Copy config).
