# Polymorph photo render: run by the path-tracing engine in the background (no window).
#   <engine> -b --factory-startup -P photo_render.py -- job.json
# job.json (written by Polymorph): scene .glb, camera, lights, light mood, quality, size, output .png.
# Prints "POLY:" lines Polymorph reads for its progress bar.
import json
import math
import sys
import time

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
job = json.load(open(argv[0], encoding="utf-8"))


def say(*a):
    print("POLY:", *a, flush=True)


t0 = time.time()
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene

# ---------------------------------------------------------------- the model
say("STEP importing the scene")
bpy.ops.import_scene.gltf(filepath=job["glb"])


def node(nt, kind):
    for n in nt.nodes:
        if n.type == kind:
            return n
    return None


# colours straight from the .glb (the importer may hide them behind vertex colour nodes)
import struct
gltf_colours = {}
try:
    with open(job["glb"], "rb") as f:
        f.read(12)
        ln, kind = struct.unpack("<II", f.read(8))
        doc = json.loads(f.read(ln).decode("utf-8"))
    for m in doc.get("materials", []):
        pbr = m.get("pbrMetallicRoughness", {})
        gltf_colours[m.get("name", "")] = (tuple(pbr.get("baseColorFactor", [0.8, 0.8, 0.8, 1])), "baseColorTexture" in pbr)
except Exception as e:
    say("NOTE colours:", e)

for mat in bpy.data.materials:
    if not mat.use_nodes:
        continue
    nt = mat.node_tree
    bsdf = node(nt, "BSDF_PRINCIPLED")
    if bsdf is None:
        continue
    # the colour texture (not the normal map, which goes through a Normal Map node)
    tex = None
    for n in nt.nodes:
        if n.type == "TEX_IMAGE" and not any(l.to_node.type == "NORMAL_MAP" for o in n.outputs for l in o.links):
            tex = n
            break
    base = bsdf.inputs["Base Color"]
    # GTA vertex colours (green / red bake masks) must not tint the photo: base colour = texture or plain colour
    for l in list(base.links):
        nt.links.remove(l)
    for n in [n for n in nt.nodes if n.type in ("VERTEX_COLOR", "ATTRIBUTE")]:
        nt.nodes.remove(n)
    if tex is not None:
        nt.links.new(tex.outputs["Color"], base)
    else:
        col = gltf_colours.get(mat.name, gltf_colours.get(mat.name.split(".")[0], ((0.8, 0.8, 0.8, 1), False)))[0]
        lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in col[:3]]  # picked colours are sRGB
        base.default_value = (lin[0], lin[1], lin[2], 1.0)
    shader = str(mat.get("gta_shader", "")).lower()
    bsdf.inputs["Roughness"].default_value = 0.55
    if "spec" in shader or "normal_spec" in shader:
        bsdf.inputs["Roughness"].default_value = 0.45
    if "emissive" in shader:
        ec = bsdf.inputs.get("Emission Color") or bsdf.inputs.get("Emission")
        if ec is not None:
            if tex is not None:
                nt.links.new(tex.outputs["Color"], ec)
            else:
                ec.default_value = tuple(mat.diffuse_color)
        es = bsdf.inputs.get("Emission Strength")
        if es is not None:
            es.default_value = 1.5 * job.get("emissive", 1.0)
    if "glass" in shader:
        tw = bsdf.inputs.get("Transmission Weight") or bsdf.inputs.get("Transmission")
        if tw is not None:
            tw.default_value = 1.0
        bsdf.inputs["Roughness"].default_value = 0.03
    if tex is not None and any(k in shader for k in ("decal", "cutout", "alpha")):
        nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
        try:
            mat.blend_method = "HASHED"
        except Exception:
            pass

# backface culling like the view: a room seen from outside (camera above the ceiling, behind a wall) shows its inside.
# Only camera rays go through the back of a face; light and shadows still see every wall as solid.
if job.get("cullBackfaces", True):
    for mat in bpy.data.materials:
        if not mat.use_nodes:
            continue
        nt = mat.node_tree
        out = node(nt, "OUTPUT_MATERIAL")
        if out is None or not out.inputs["Surface"].links:
            continue
        surf = out.inputs["Surface"].links[0].from_socket
        geo = nt.nodes.new("ShaderNodeNewGeometry")
        lp = nt.nodes.new("ShaderNodeLightPath")
        mul = nt.nodes.new("ShaderNodeMath"); mul.operation = "MULTIPLY"
        nt.links.new(geo.outputs["Backfacing"], mul.inputs[0])
        nt.links.new(lp.outputs["Is Camera Ray"], mul.inputs[1])
        tr = nt.nodes.new("ShaderNodeBsdfTransparent")
        mix = nt.nodes.new("ShaderNodeMixShader")
        nt.links.new(mul.outputs[0], mix.inputs["Fac"])
        nt.links.new(surf, mix.inputs[1])
        nt.links.new(tr.outputs[0], mix.inputs[2])
        nt.links.new(mix.outputs[0], out.inputs["Surface"])

# ---------------------------------------------------------------- lights
lamp_power = job.get("lampPower", 1.0)
soft = job.get("soft", False)
for i, L in enumerate(job.get("lights", [])):
    kind = {"spot": "SPOT", "capsule": "POINT"}.get(L["kind"], "POINT")
    data = bpy.data.lights.new("lamp_%d" % i, kind)
    data.color = L["colour"]
    data.energy = L["watts"] * lamp_power
    data.shadow_soft_size = max(L.get("radius", 0.1), 0.25 if soft else 0.04)
    if kind == "SPOT":
        data.spot_size = math.radians(min(179.0, 2 * L["cone"]))
        data.spot_blend = L.get("blend", 0.3)
    ob = bpy.data.objects.new("lamp_%d" % i, data)
    try:
        ob.visible_camera = False  # the bulb itself is not a white disc in the picture (the fitting is the model's job)
    except Exception:
        pass
    ob.location = L["pos"]
    d = Vector(L["dir"])
    ob.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(ob)

if job.get("sun"):
    s = job["sun"]
    data = bpy.data.lights.new("sun", "SUN")
    data.energy = s["strength"]
    data.color = s["colour"]
    data.angle = math.radians(2.5 if soft else 0.8)
    ob = bpy.data.objects.new("sun", data)
    ob.rotation_euler = (-Vector(s["towardSun"])).to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(ob)

for i, a in enumerate(job.get("areas", [])):  # studio lights
    data = bpy.data.lights.new("studio_%d" % i, "AREA")
    data.energy = a["watts"]
    data.size = a["size"]
    data.color = a.get("colour", (1, 1, 1))
    ob = bpy.data.objects.new("studio_%d" % i, data)
    ob.location = a["pos"]
    ob.rotation_euler = (Vector(a["target"]) - Vector(a["pos"])).to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(ob)

world = bpy.data.worlds.new("sky")
world.use_nodes = True
bg = world.node_tree.nodes.get("Background")
bg.inputs["Color"].default_value = tuple(job["sky"]["colour"]) + (1.0,)
bg.inputs["Strength"].default_value = job["sky"]["strength"]
scene.world = world

# ---------------------------------------------------------------- camera
c = job["camera"]
cam = bpy.data.cameras.new("view")
cam.clip_start = max(0.01, c.get("near", 0.05))
cam.clip_end = 5000
if c["ortho"]:
    cam.type = "ORTHO"
    w, h = job["width"], job["height"]
    cam.ortho_scale = c["orthoHeight"] * (w / h if w >= h else 1.0)
else:
    cam.sensor_fit = "VERTICAL"
    cam.angle_y = c["fovY"]
if job.get("dof"):
    cam.dof.use_dof = True
    cam.dof.focus_distance = (Vector(c["target"]) - Vector(c["eye"])).length
    cam.dof.aperture_fstop = 2.8
cob = bpy.data.objects.new("view", cam)
cob.location = c["eye"]
# same framing as the view: forward to the target, the view's own up (works for top views too)
from mathutils import Matrix
fwd = (Vector(c["target"]) - Vector(c["eye"])).normalized()
up = Vector(c["up"])
right = fwd.cross(up).normalized()
true_up = right.cross(fwd).normalized()
cob.rotation_euler = Matrix((right, true_up, -fwd)).transposed().to_euler()
scene.collection.objects.link(cob)
scene.camera = cob

# ---------------------------------------------------------------- engine
scene.render.engine = "CYCLES"
cy = scene.cycles
dev = "CPU"
try:
    prefs = bpy.context.preferences.addons["cycles"].preferences
    for kind in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
        try:
            prefs.compute_device_type = kind
        except Exception:
            continue
        prefs.get_devices()
        gpus = [d for d in prefs.devices if d.type != "CPU"]
        if gpus:
            for d in prefs.devices:
                d.use = d.type != "CPU"
            dev = "GPU (%s: %s)" % (kind, gpus[0].name)
            cy.device = "GPU"
            break
except Exception as e:
    say("NOTE no graphics card for the render:", e)
say("DEVICE", dev)

cy.samples = job["samples"]
cy.use_adaptive_sampling = True
cy.adaptive_threshold = job.get("noiseThreshold", 0.02)
cy.max_bounces = job.get("bounces", 8)
cy.diffuse_bounces = min(4, cy.max_bounces)
cy.glossy_bounces = min(4, cy.max_bounces)
cy.transmission_bounces = cy.max_bounces
cy.use_denoising = job.get("denoise", True)
try:
    cy.denoiser = "OPENIMAGEDENOISE"
except Exception:
    pass
for attr in ("denoising_use_gpu",):
    if hasattr(cy, attr):
        setattr(cy, attr, True)
if job.get("timeLimit"):
    cy.time_limit = job["timeLimit"]

scene.render.resolution_x = job["width"]
scene.render.resolution_y = job["height"]
scene.render.resolution_percentage = 100
scene.render.film_transparent = job.get("transparent", False)
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA" if job.get("transparent") else "RGB"
try:
    scene.view_settings.view_transform = "AgX"
except Exception:
    scene.view_settings.view_transform = "Filmic"
scene.view_settings.exposure = job.get("exposure", 0.0)
scene.render.filepath = job["output"]

_last = [0.0]


def _stats(text, *args):
    # the engine's status line ("... | Sample 12/128 | ..."), at most 4 times a second
    now = time.time()
    if now - _last[0] > 0.25:
        _last[0] = now
        say("STATS", str(text))


try:
    bpy.app.handlers.render_stats.append(_stats)
except Exception:
    pass
say("STEP rendering %dx%d, %d samples" % (job["width"], job["height"], job["samples"]))
bpy.ops.render.render(write_still=True)
say("DONE %s %.1f" % (job["output"], time.time() - t0))
