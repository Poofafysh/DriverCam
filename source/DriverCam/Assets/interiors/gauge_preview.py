"""Renders a cockpit's working gauges from the driver's eye at a few readings, to check needles against the faces.

blender -b --factory-startup --python gauge_preview.py -- <cockpit_Car.dcm> <out dir> [car .cfg] [kmh|mph]

For each reading (0, half and full scale) it poses every gauge part the way DriverCam does in game, from the part's own
`n` / `dg` / `db` lines and Unity's maths: world = pivot + LookRotation(forward, up) * Euler(0, 0, angle(v) - a0) * vertex,
with Euler(0, 0, +x) turning +x towards +y. The digits and bars get their UVs shifted like Gauges.cs does. Writes
<Car>_<unit>_gauges_<reading>.png (the cluster from the eye, through the wheel), <Car>_<unit>_dials_<reading>.png
(straight on, steering wheel hidden: every needle against its scale) and <Car>_<unit>_eye_<reading>.png (the whole view).
The faces are read from the .dcm's own `tex gauges` / `tex gauges_mph` files next to it.
"""
import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bpy, bmesh
from mathutils import Vector, Matrix, Quaternion
import dcm_io

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, OUT = argv[0], argv[1]
CFG = argv[2] if len(argv) > 2 and argv[2] else None
UNIT = argv[3] if len(argv) > 3 else "kmh"
CAR = os.path.basename(SRC)[len("cockpit_"):-len(".dcm")]
os.makedirs(OUT, exist_ok=True)

model = dcm_io.parse(SRC)
folder = os.path.dirname(os.path.abspath(SRC))
tune = {"Driver.OffsetX": 0.0, "Driver.OffsetY": 0.0, "Driver.OffsetZ": 0.0, "View.Fov": 60.0, "Driver.Pitch": 0.0}
if CFG and os.path.exists(CFG):
    for line in open(CFG, encoding="utf-8"):
        if "=" in line and not line.lstrip().startswith("#"):
            k, v = (t.strip() for t in line.split("=", 1))
            if k in tune:
                try: tune[k] = float(v)
                except ValueError: pass
eye = Vector(model["eye"]) + Vector((tune["Driver.OffsetX"], tune["Driver.OffsetY"], tune["Driver.OffsetZ"]))   # Unity


# ------------------------------------------------------------------ Unity maths (all in Unity coordinates)
def look_rotation(f, u):
    f = Vector(f).normalized(); u = Vector(u)
    r = u.cross(f).normalized()          # Unity: right = Cross(up, forward)
    u = f.cross(r).normalized()
    return Matrix((r, u, f)).transposed()   # columns: local x, y, z


def euler_z(deg):
    a = math.radians(deg)
    return Matrix(((math.cos(a), -math.sin(a), 0), (math.sin(a), math.cos(a), 0), (0, 0, 1)))   # +x turns towards +y


# ------------------------------------------------------------------ materials (unlit-ish, like the game's self-glow)
def tex_material(tag, file):
    m = bpy.data.materials.new("g_" + tag + "_" + file)
    m.use_nodes = True
    nt = m.node_tree; nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    img = nt.nodes.new("ShaderNodeTexImage"); img.image = bpy.data.images.load(os.path.join(folder, file)); img.interpolation = "Closest"
    em = nt.nodes.new("ShaderNodeEmission"); em.inputs["Strength"].default_value = 1.0
    tr = nt.nodes.new("ShaderNodeBsdfTransparent")
    mix = nt.nodes.new("ShaderNodeMixShader")
    gt = nt.nodes.new("ShaderNodeMath"); gt.operation = "GREATER_THAN"; gt.inputs[1].default_value = 0.5   # alpha cutout
    nt.links.new(img.outputs["Color"], em.inputs["Color"])
    nt.links.new(img.outputs["Alpha"], gt.inputs[0])
    nt.links.new(gt.outputs[0], mix.inputs[0])
    nt.links.new(tr.outputs[0], mix.inputs[1]); nt.links.new(em.outputs[0], mix.inputs[2])
    nt.links.new(mix.outputs[0], out.inputs["Surface"])
    try: m.surface_render_method = "DITHERED"
    except AttributeError: m.blend_method = "CLIP"
    return m


def col_material(tag):
    c = model["mats"].get(tag, dcm_io.PALETTE.get(tag, (0.4, 0.4, 0.4)) + (0.3, 0.0, 0.35))
    m = bpy.data.materials.new("c_" + tag)
    m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = (c[0], c[1], c[2], 1)
    b.inputs["Roughness"].default_value = 1.0 - c[3]
    b.inputs["Metallic"].default_value = c[4]
    try:
        b.inputs["Emission Color"].default_value = (c[0], c[1], c[2], 1); b.inputs["Emission Strength"].default_value = 0.35 * c[5] * 3
    except KeyError: pass
    return m


MATS = {}
def mat_for(tag, face_unit):
    file = model["tex"].get(tag)
    if tag == "gauges" and face_unit == "mph" and "gauges_mph" in model["tex"]: file = model["tex"]["gauges_mph"]
    key = (tag, file)
    if key not in MATS: MATS[key] = tex_material(tag, file) if file else col_material(tag)
    return MATS[key]


# ------------------------------------------------------------------ build
def make_obj(name, tris_by_tag, xf, uv_shift=None):
    """tris in Unity coordinates (or pivot space with xf), -> one Blender object; uv_shift(tag, tri_index) -> (du, dv)."""
    me = bpy.data.meshes.new(name); bm = bmesh.new(); uvl = bm.loops.layers.uv.new("UVMap")
    slots = []
    for tag, tris in tris_by_tag.items():
        slots.append(tag); si = len(slots) - 1
        for ti, tri in enumerate(tris):
            vs = [bm.verts.new(dcm_io.u2b(*xf(Vector(v)))) for (v, n, uv) in tri]
            try: f = bm.faces.new(vs)
            except ValueError: continue
            f.material_index = si
            sh = uv_shift(tag, ti) if uv_shift else (0.0, 0.0)
            for loop, (v, n, uv) in zip(f.loops, tri):
                if uv is not None: loop[uvl].uv = (uv[0] + sh[0], uv[1] + sh[1])
    bm.to_mesh(me); bm.free()
    for tag in slots: me.materials.append(mat_for(tag, UNIT))
    ob = bpy.data.objects.new(name, me); bpy.context.scene.collection.objects.link(ob)
    return ob


def floats(tok, i, default=float("nan")):
    try: return float(tok[i])
    except (IndexError, ValueError): return default


def pose_gauges(frac):
    """Gauge parts at `frac` of each scale (needle angle = a0 + (a1 - a0) * frac; digits show frac of the speed dial)."""
    objs = []
    speed_max = 0.0
    for part in model["parts"]:
        for l in part.get("gauge", []):
            t = l.split()
            if t[0] == "n" and t[1] == "speed" and t[2] == UNIT: speed_max = max(speed_max, floats(t, 4, 0.0))
    if speed_max <= 0: speed_max = 320.0 if UNIT == "kmh" else 200.0
    for part in model["parts"]:
        g = part.get("gauge")
        if not g: continue
        P, F, U = part["pivot"]
        M = look_rotation(F, U); Pv = Vector(P)
        theta = 0.0; shifts = {}
        needles = [l.split() for l in g if l.startswith("n ")]
        pick = [t for t in needles if t[2] in (UNIT, "-")] or needles
        if pick:
            t = pick[0]; a0, a1 = floats(t, 5), floats(t, 6)
            theta = (a0 + (a1 - a0) * frac) - a0
        quad = 0
        for l in g:
            t = l.split()
            if t[0] == "dg":
                count, du = int(t[3]), floats(t, 6)
                val = int(math.floor(speed_max * frac)); val = max(0, min(val, 10 ** count - 1))
                digits = str(val).rjust(count)
                for i, ch in enumerate(digits):
                    k = 10 if ch == " " else int(ch)
                    for tri in (2 * (quad + i), 2 * (quad + i) + 1): shifts[tri] = (k * du, 0.0)
                quad += count
            elif t[0] == "db":
                count, lo, hi, red = int(t[2]), floats(t, 3), floats(t, 4), floats(t, 5)
                lit, red_lit = (floats(t, 6), floats(t, 7)), (floats(t, 8), floats(t, 9))
                v = lo + (hi - lo) * frac
                for i in range(count):
                    thr = lo + (i + 1) / count * (hi - lo)
                    if v >= thr - 1e-3:
                        s = red_lit if thr >= red else lit
                        for tri in (2 * (quad + i), 2 * (quad + i) + 1): shifts[tri] = s
                quad += count
        R = euler_z(theta)
        xf = lambda v, M=M, R=R, Pv=Pv: Pv + M @ (R @ v)
        tags = part["tags"]
        first = next(iter(tags), None)
        objs.append(make_obj(part["name"] + f"_{frac:g}", tags, xf, lambda tag, ti: shifts.get(ti, (0.0, 0.0)) if tag == first else (0.0, 0.0)))
    return objs


dcm_io.clear_scene()
static = [p for p in model["parts"] if "gauge" not in p]
wheel_objs = []
for part in static:
    if part["pivot"] is None:
        make_obj(part["name"], part["tags"], lambda v: v)
    else:
        P, F, U = part["pivot"]; M = look_rotation(F, U); Pv = Vector(P)
        o = make_obj(part["name"], part["tags"], lambda v, M=M, Pv=Pv: Pv + M @ v)
        if part["group"] == "SteeringWheel": wheel_objs.append(o)

sc = bpy.context.scene
for eng in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
    try: sc.render.engine = eng; break
    except TypeError: pass
sc.world = sc.world or bpy.data.worlds.new("W")
sc.world.use_nodes = True
bg = sc.world.node_tree.nodes.get("Background")
if bg: bg.inputs[0].default_value = (0.55, 0.6, 0.68, 1); bg.inputs[1].default_value = 0.8
sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN")); sun.data.energy = 2.5
sun.rotation_euler = (math.radians(50), 0, math.radians(20)); sc.collection.objects.link(sun)
sc.view_settings.view_transform = "Standard"

centres = [Vector(p["pivot"][0]) for p in model["parts"] if p.get("gauge")]
cl = sum(centres, Vector()) / len(centres) if centres else eye + Vector((0, -0.3, 0.8))

cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam")); sc.collection.objects.link(cam); sc.camera = cam
cam.data.lens_unit = "FOV"; cam.data.sensor_fit = "VERTICAL"; cam.data.clip_start = 0.02


def aim(target_u, fov, frm=None):
    cam.location = dcm_io.u2b(*(frm if frm is not None else eye))
    d = dcm_io.u2b(*target_u) - cam.location
    cam.rotation_euler = d.to_track_quat("-Z", "Z").to_euler()
    cam.data.angle = math.radians(fov)


for frac, label in ((0.0, "0"), (0.5, "mid"), (1.0, "max")):
    objs = pose_gauges(frac)
    aim(cl, 14 if len(centres) > 1 else 12)
    sc.render.resolution_x, sc.render.resolution_y = 1280, 720
    sc.render.filepath = os.path.join(OUT, f"{CAR}_{UNIT}_gauges_{label}.png")
    bpy.ops.render.render(write_still=True)
    if centres:   # straight on to the dials, wheel hidden
        gp = next(p for p in model["parts"] if p.get("gauge"))
        for o in wheel_objs: o.hide_render = True
        aim(cl, 20, frm=cl - Vector(gp["pivot"][1]).normalized() * 0.75)
        sc.render.filepath = os.path.join(OUT, f"{CAR}_{UNIT}_dials_{label}.png")
        bpy.ops.render.render(write_still=True)
        for o in wheel_objs: o.hide_render = False
    aim(eye + Vector((0, -math.sin(math.radians(tune['Driver.Pitch'])), 1)), tune["View.Fov"])
    sc.render.resolution_x, sc.render.resolution_y = 1280, 720
    sc.render.filepath = os.path.join(OUT, f"{CAR}_{UNIT}_eye_{label}.png")
    bpy.ops.render.render(write_still=True)
    for o in objs: bpy.data.objects.remove(o, do_unlink=True)
print(f"[{CAR}] gauge previews in {OUT}")
