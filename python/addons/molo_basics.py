"""Example add-on shipped with Polymorph: copy it to your add-ons folder (Add-ons > Open add-ons folder) to make your own.

An add-on is a .py file with register(); poly.register_menu() puts a function in the Add-ons menu.
Each click is one undo step.
"""
import poly


def flip_to_room():
    """MLO rooms: faces turned inward on the selected objects."""
    n = 0
    for o in poly.context.selected:
        if o.Mesh is not None:
            poly.MeshOps.FlipAll(o.Mesh)
            o.Touch()
            n += 1
    print("flipped", n, "object(s)")


def drop_to_ground():
    """Puts the selected objects on the ground (their lowest point at Z = 0)."""
    for o in poly.context.selected:
        lo = o.WorldBounds().Item1
        p = o.Position
        o.Position = poly.Vector3(p.X, p.Y, p.Z - lo.Z)
    print("on the ground:", len(poly.context.selected))


def register():
    poly.register_menu("Molo: flip faces inward (MLO room)", flip_to_room)
    poly.register_menu("Molo: drop selection to the ground", drop_to_ground)
