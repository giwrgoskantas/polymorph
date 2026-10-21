"""GTA-style stairs for Polymorph (add-on).

Rules measured on 208 staircases of GTA V (Polymorph stair scan, Oct 2026):
  * riser (step height) 0.20 m  (most between 0.18 and 0.23)
  * tread (step depth)  0.30 m  (most between 0.27 and 0.36)
  * simple interior stairs between walls: only treads + risers, 4 triangles per step, no sides
  * one texture strip laid along the climb: U across the width, V continuous up the profile, about 0.5 UV per metre
  * shader normal_spec (often with detail), collision = a smooth ramp flagged as stairs (the player does not bump)

poly.call / Python:  import gta_stairs; gta_stairs.build(height=3.0, width=1.2)
Menu: Add-ons > GTA: stairs (between walls) / GTA: stairs (solid)
"""
import math
import poly
from System.Numerics import Vector2, Vector4

GREEN = Vector4(0.0, 1.0, 0.0, 1.0)  # Molo rule: non-emissive geometry has green vertex colours


def _quad(m, a, b, c, d, uvs, col=GREEN):
    """Face a-b-c-d counter-clockwise seen from the outside (its normal faces the viewer)."""
    f = m.AddFace([m.AddVertex(a), m.AddVertex(b), m.AddVertex(c), m.AddVertex(d)])
    for i in range(4):
        f.UV[i] = Vector2(float(uvs[i][0]), float(uvs[i][1]))
        f.Col[i] = col
    return f


def _poly(m, pts, uvs, col=GREEN):
    f = m.AddFace([m.AddVertex(p) for p in pts])
    for i in range(len(pts)):
        f.UV[i] = Vector2(float(uvs[i][0]), float(uvs[i][1]))
        f.Col[i] = col
    return f


def stair_mesh(height=3.0, width=1.2, riser=0.20, tread=0.30, landing=0.0, solid=False, texel=0.5):
    """Straight stairs climbing along +Y, pivot at the bottom front centre. Returns (PolyMesh, steps, real riser, tread)."""
    V = poly.vec
    n = max(1, int(round(height / riser)))
    r = height / n
    t = tread
    hw = width / 2.0
    m = poly.PolyMesh()
    s = 0.0  # length travelled along the profile (V of the strip)
    u0, u1 = 0.0, width * texel
    for i in range(n):
        y0, y1 = i * t, (i + 1) * t
        if i == n - 1:
            y1 += landing
        z0, z1 = i * r, (i + 1) * r
        # riser (faces -Y)
        _quad(m, V(-hw, y0, z0), V(hw, y0, z0), V(hw, y0, z1), V(-hw, y0, z1),
              [(u0, -s * texel), (u1, -s * texel), (u1, -(s + r) * texel), (u0, -(s + r) * texel)])
        s += r
        # tread (faces +Z)
        d = y1 - y0
        _quad(m, V(-hw, y0, z1), V(hw, y0, z1), V(hw, y1, z1), V(-hw, y1, z1),
              [(u0, -s * texel), (u1, -s * texel), (u1, -(s + d) * texel), (u0, -(s + d) * texel)])
        s += d
    run = n * t + landing
    if solid:
        # side caps (saw-tooth profile), underside slope and back: a closed volume
        prof = [(0.0, 0.0)]
        for i in range(n):
            y1 = (i + 1) * t + (landing if i == n - 1 else 0.0)
            prof.append((i * t, (i + 1) * r))
            prof.append((y1, (i + 1) * r))
        # the underside follows the slope, leaving a step-thick waist, and ends flat under the landing
        waist = 0.15
        bottom_back = (run, max(0.0, height - r - waist))
        prof.append(bottom_back)
        if n > 1:
            prof.append((t, 0.0))
        # right side (+X), counter-clockwise seen from +X: walk the profile reversed
        right = [V(hw, y, z) for (y, z) in reversed(prof)]
        _poly(m, right, [(y * texel, z * texel) for (y, z) in reversed(prof)])
        left = [V(-hw, y, z) for (y, z) in prof]
        _poly(m, left, [(-y * texel, z * texel) for (y, z) in prof])
        # back (faces +Y), from the landing down to the underside
        zt, zb = height, bottom_back[1]
        _quad(m, V(hw, run, zb), V(-hw, run, zb), V(-hw, run, zt), V(hw, run, zt),
              [(u0, 0), (u1, 0), (u1, (zt - zb) * texel), (u0, (zt - zb) * texel)])
        # underside: slope from the back bottom to the floor at the first step, then the floor strip
        y_s = t if n > 1 else 0.0
        slope_len = math.hypot(run - y_s, zb)
        _quad(m, V(-hw, run, zb), V(hw, run, zb), V(hw, y_s, 0.0), V(-hw, y_s, 0.0),
              [(u0, slope_len * texel), (u1, slope_len * texel), (u1, 0), (u0, 0)])  # faces down
        if n > 1:
            _quad(m, V(-hw, 0.0, 0.0), V(-hw, y_s, 0.0), V(hw, y_s, 0.0), V(hw, 0.0, 0.0),
                  [(u0, 0), (u0, y_s * texel), (u1, y_s * texel), (u1, 0)])
    return m, n, r, t


def ramp_mesh(height, width, run, landing):
    """Collision: a smooth closed ramp under the steps (GTA stairs collision), pivot as the stairs."""
    V = poly.vec
    hw = width / 2.0
    m = poly.PolyMesh()
    slope_end = run - landing
    z = [(0.0, 0.0), (slope_end, height), (run, height), (run, 0.0)]          # (y, z) clockwise seen from +X
    uv = [(0, 0)] * 4
    # top slope + landing
    _quad(m, V(-hw, 0, 0), V(hw, 0, 0), V(hw, slope_end, height), V(-hw, slope_end, height), uv)
    if landing > 0.001:
        _quad(m, V(-hw, slope_end, height), V(hw, slope_end, height), V(hw, run, height), V(-hw, run, height), uv)
    _quad(m, V(hw, run, 0), V(-hw, run, 0), V(-hw, run, height), V(hw, run, height), uv)          # back
    _quad(m, V(-hw, 0, 0), V(-hw, run, 0), V(hw, run, 0), V(hw, 0, 0), uv)                          # bottom
    _poly(m, [V(hw, y, zz) for (y, zz) in reversed(z)], [(0, 0)] * 4)                               # right
    _poly(m, [V(-hw, y, zz) for (y, zz) in z], [(0, 0)] * 4)                                        # left
    return m


def build(name="molo_stairs", height=3.0, width=1.2, riser=0.20, tread=0.30, landing=0.0, solid=False,
          texture=None, at=None, collision=True, texel=0.5):
    """Creates the stairs (and their collision ramp) at `at` (default: the 3D cursor). Returns the stairs object."""
    at = poly.context.cursor if at is None else poly.vec(at)
    mesh, n, r, t = stair_mesh(height, width, riser, tread, landing, solid, texel)
    obj = poly.add(name, mesh, at)
    if texture:
        poly.set_material(obj, poly.new_material(name + "_mat", texture, "normal_spec"))
    if collision:
        col = poly.add(name + "_col", ramp_mesh(height, width, n * t + landing, landing), at, poly.GtaObjectType.Collision)
        col.ParentId = obj.Id
        col.Gta.CollisionMaterial = 1  # CONCRETE; the stairs flag is set automatically from the name / shape
    print("stairs '%s': %d steps of %.3f m x %.2f m, %d triangles" % (name, n, r, t, mesh.TriangleCount))
    return obj


def _menu_open():
    build("molo_stairs", solid=False)


def _menu_solid():
    build("molo_stairs_solid", solid=True)


def register():
    poly.register_menu("GTA: stairs (between walls)", _menu_open)
    poly.register_menu("GTA: stairs (solid)", _menu_solid)
