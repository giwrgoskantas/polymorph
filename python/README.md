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

## MCP (Claude)

`polymorph_mcp.py` is a standard-library MCP server over stdio. Every API method becomes a tool
(`scene_list`, `object_create`, `modifier_add`, `uv_unwrap`, `gta_check`, `gta_export`...) plus
`view_screenshot`, which returns a picture of the window.

Claude Desktop > Settings > Developer > Edit Config:

```json
{ "mcpServers": { "polymorph": { "command": "python", "args": ["C:/path/to/Polymorph/python/polymorph_mcp.py"] } } }
```

The exact config for your PC is in Polymorph: **Tools > Python & MCP** (Copy config).
