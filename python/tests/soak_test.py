"""Long session on the real Polymorph: thousands of random operations through the API (create, edit, modifiers,
undo / redo chains, duplicate, delete, save / reopen, GTA check and export), watching for errors, crashes and
memory growth. Usage: soak_test.py <Polymorph.exe> <out folder> [minutes] [seed]"""
import json, os, random, subprocess, sys, time, traceback
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(here, ".."))
import polymorph as pm

exe, out = sys.argv[1], sys.argv[2]
minutes = float(sys.argv[3]) if len(sys.argv) > 3 else 10
seed = int(sys.argv[4]) if len(sys.argv) > 4 else 1234
os.makedirs(out, exist_ok=True)
rnd = random.Random(seed)
log = open(os.path.join(out, "soak.log"), "w", encoding="utf-8")
def say(s):
    log.write(time.strftime("%H:%M:%S ") + s + "\n"); log.flush()

info_file = os.path.join(os.environ["APPDATA"], "Polymorph", "api.json")
before = os.path.getmtime(info_file) if os.path.exists(info_file) else 0
proc = subprocess.Popen([exe])
t0 = time.time()
while time.time() - t0 < 60:
    if os.path.exists(info_file) and os.path.getmtime(info_file) > before:
        try:
            if json.load(open(info_file)).get("pid") == proc.pid: break
        except ValueError: pass
    time.sleep(0.3)
time.sleep(1.5)
c = pm.connect()
say("started: " + json.dumps(c.info()))

def mem_mb():
    r = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-Process -Id %d).WorkingSet64" % proc.pid], capture_output=True)
    try: return int(r.stdout.decode("mbcs", "replace").strip()) / 1048576
    except Exception: return -1

KINDS = ["box", "sphere", "cylinder", "cone", "tube", "plane", "stairs", "room", "door"]
MODS = None
stats = {"ops": 0, "errors": 0, "undo": 0, "redo": 0, "saves": 0, "exports": 0}
errs = []
scene_file = os.path.join(out, "soak_scene.mmx")
mem0 = None; mem_samples = []
end = time.time() + minutes * 60
last_mem = 0
while time.time() < end:
    if proc.poll() is not None:
        errs.append("CRASH: Polymorph exited with code %s" % proc.returncode); break
    objs = c.objects()
    ids = [o["id"] for o in objs]
    op = rnd.choices(["create", "move", "edit", "modifier", "undo", "redo", "dup", "delete", "unwrap", "save", "check", "export", "new"],
                     [16, 14, 10, 10, 10, 6, 6, 6, 5, 2, 3, 1, 0.5])[0]
    try:
        if op == "create" or not ids:
            k = rnd.choice(KINDS)
            c.create(k, position=[rnd.uniform(-20, 20), rnd.uniform(-20, 20), 0])
        elif op == "move":
            o = rnd.choice(ids)
            c.set(o, position=[rnd.uniform(-30, 30), rnd.uniform(-30, 30), rnd.uniform(0, 5)], rotation=[0, 0, rnd.uniform(0, 360)])
        elif op == "edit":
            o = rnd.choice(ids)
            c.edit_poly(o, rnd.choice(["extrude", "inset", "bevel", "weld"]), amount=rnd.uniform(0.02, 0.4), direction=rnd.choice([[0, 0, 1], [1, 0, 0], [0, -1, 0]]))
        elif op == "modifier":
            MODS = MODS or c.modifier_types()
            o = rnd.choice(ids)
            mods = c.modifiers(o)
            if mods and rnd.random() < 0.4:
                (c.remove_modifier if rnd.random() < 0.6 else c.apply_modifier)(o, rnd.randrange(len(mods)))
            else:
                t = rnd.choice(MODS)
                c.add_modifier(o, t["name"] if isinstance(t, dict) else t)
        elif op == "undo":
            for _ in range(rnd.randint(1, 12)): c.undo(); stats["undo"] += 1
        elif op == "redo":
            for _ in range(rnd.randint(1, 8)): c.redo(); stats["redo"] += 1
        elif op == "dup":
            c.duplicate(rnd.choice(ids), instance=rnd.random() < 0.5, count=rnd.randint(1, 4), offset=[2, 0, 0])
        elif op == "delete":
            c.delete(*rnd.sample(ids, min(len(ids), rnd.randint(1, 3))))
        elif op == "unwrap":
            c.unwrap(rnd.choice(ids))
        elif op == "save":
            c.save(scene_file); stats["saves"] += 1
            n = len(c.objects())
            c.open(scene_file)
            if len(c.objects()) != n: errs.append("save/open: %d objects became %d" % (n, len(c.objects())))
        elif op == "check":
            c.check()
        elif op == "export":
            c.settings(ResourceName="polymorph_soak")
            r = c.export(os.path.join(out, "export")); stats["exports"] += 1
            if r.get("errors"): errs.append("export errors: %s" % r["errors"][:3])
        elif op == "new":
            c.new()
        stats["ops"] += 1
    except pm.PolymorphError as e:
        msg = str(e)
        # refusals of the API for an impossible request are fine (e.g. edit poly on a door); anything else is logged
        expected = any(w in msg.lower() for w in ["not found", "no face", "unknown", "cannot", "nothing", "no modifier", "out of range", "empty", "not an editable", "needs"])
        if not expected:
            stats["errors"] += 1; errs.append("%s: %s" % (op, msg[:200]))
        say("%s -> %s" % (op, msg[:160]))
    except Exception as e:
        stats["errors"] += 1; errs.append("%s: %s" % (op, traceback.format_exc()[-300:]))
        if proc.poll() is not None: errs.append("CRASH during %s (exit %s)" % (op, proc.returncode)); break
    if time.time() - last_mem > 15:
        last_mem = time.time(); m = mem_mb(); mem_samples.append(m)
        if mem0 is None: mem0 = m
        say("ops %d, objects %d, memory %.0f MB" % (stats["ops"], len(ids), m))

alive = proc.poll() is None
if alive:
    try: c.call("app.quit", discard=True); proc.wait(30)
    except Exception: proc.kill()
rep = {"minutes": minutes, "seed": seed, "stats": stats, "alive_until_end": alive, "exit_code": proc.returncode,
       "memory_mb": {"start": mem0, "end": mem_samples[-1] if mem_samples else None, "max": max(mem_samples) if mem_samples else None}, "errors": errs[:40]}
json.dump(rep, open(os.path.join(out, "soak_report.json"), "w"), indent=1)
say("REPORT " + json.dumps(rep))
open(os.path.join(out, "soak.done"), "w").write("done")
