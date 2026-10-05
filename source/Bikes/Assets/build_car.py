"""Builds the Bikes plugin's M2 G87 car model ("2026 Zacoe BMW G87 M2 Widebody Carbon Fiber Kit" by Ddiaz Design,
Sketchfab, CC BY-NC-SA 4.0) from its FBX. Steps:
- drop parts nobody sees (interior, engine, belts, calipers, the base kit);
- copy every mesh with its world transform baked in (the wheels hang off an armature's bones);
- weld and decimate (body about 10,000 triangles: the many loose panels limit the decimation; wheels about 300 each);
- flat colours by part (paint, carbon, glass, lights, tyre, rim, disc) from the source's materials, no texture.
Written as a .csm with `kind car` (Body + WheelFL/FR/RL/RR around their pivots).
blender -b --factory-startup --python build_car.py -- <fbx> <out dir> [preview prefix]

Output frame (Unity, metres): +z forward, +y up, +x right; origin on the ground midway between the axles, on the centre
line. Real size.
"""
import sys, os, math
import bpy, bmesh
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:]
FBX, OUT = argv[0], argv[1]
PREVIEW = argv[2] if len(argv) > 2 else None
NAME = "BMW_M2_G87"
TARGET_BODY, TARGET_WHEEL = 4000, 300
LENGTH = 4.58                                   # the G87 M2's length, metres
DROP = ("Interior_Geo", "Engine_Geo", "SeatBelt_Geo", "Base:", "Calliper")

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=FBX)
arm = next((o for o in bpy.context.scene.objects if o.type == 'ARMATURE'), None)

def under_arm(o):
    while o is not None:
        if o == arm: return True
        o = o.parent
    return False

def sel_only(objs, active=None):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]

def tri_count(o): return sum(len(p.vertices) - 2 for p in o.data.polygons)

def bounds(objs):
    ws = [v.co for o in objs for v in o.data.vertices]
    mn = Vector((min(v.x for v in ws), min(v.y for v in ws), min(v.z for v in ws)))
    mx = Vector((max(v.x for v in ws), max(v.y for v in ws), max(v.z for v in ws)))
    return mn, mx

# baked copies: each mesh with its world transform applied to the vertices, no parent, no modifiers
body_parts, wheel_parts = [], []
for o in list(bpy.context.scene.objects):
    if o.type != 'MESH' or len(o.data.vertices) == 0 or any(d in o.name for d in DROP): continue
    me = o.data.copy(); me.transform(o.matrix_world)
    c = bpy.data.objects.new(o.name + ".baked", me); bpy.context.scene.collection.objects.link(c)
    if under_arm(o):
        if len(me.polygons) > 100: wheel_parts.append(c)   # tyres, rims, discs (calipers and badges are dropped)
    else:
        body_parts.append(c)
for o in list(bpy.context.scene.objects):
    if not o.name.endswith(".baked"): bpy.data.objects.remove(o, do_unlink=True)

# metres, facing +y (the source faces -y), ground at z 0, centred
mn, mx = bounds(body_parts)
scale = LENGTH / (mx.y - mn.y)
T = Matrix.Rotation(math.pi, 4, 'Z') @ Matrix.Scale(scale, 4)
for o in body_parts + wheel_parts: o.data.transform(T)
mn, mx = bounds(body_parts)
mnw, _ = bounds(wheel_parts)
shift = Vector((-(mn.x + mx.x) / 2, -(mn.y + mx.y) / 2, -min(mn.z, mnw.z)))
for o in body_parts + wheel_parts: o.data.transform(Matrix.Translation(shift))

sel_only(body_parts); bpy.ops.object.join(); body = bpy.context.view_layer.objects.active

# wheels: group the parts by corner (front = +y, right = +x), join each corner
groups = {"FL": [], "FR": [], "RL": [], "RR": []}
for o in wheel_parts:
    c = sum((v.co for v in o.data.vertices), Vector()) / len(o.data.vertices)
    groups[("F" if c.y > 0 else "R") + ("R" if c.x > 0 else "L")].append(o)
wheels, pivots = {}, {}
for k, objs in groups.items():
    assert objs, f"no wheel parts for {k}"
    sel_only(objs); bpy.ops.object.join(); w = bpy.context.view_layer.objects.active
    wmn, wmx = bounds([w]); pivots[k] = (wmn + wmx) / 2; wheels[k] = w
mnB, mxB = bounds([body])
print(f"[car] body {mxB.x - mnB.x:.2f} x {mxB.z - mnB.z:.2f} x {mxB.y - mnB.y:.2f} m (w x h x l), {tri_count(body)} tris; "
      f"wheel radius {(bounds([wheels['FL']])[1].z - bounds([wheels['FL']])[0].z) / 2:.3f}")

# flat colours by part: every source material maps to a tag (the kit stacks paint, carbon and base layers on the same
# surface, which a texture bake turns into blotches); the decimation keeps each face's tag
TAGS = {   # tag: (linear rgb, smoothness, metallic, emission)
    "paint": ((0.45, 0.11, 0.99), 0.70, 0.30, 0.0),
    "carbon": ((0.025, 0.025, 0.028), 0.55, 0.20, 0.0),
    "glass": ((0.02, 0.025, 0.03), 0.92, 0.10, 0.0),
    "light": ((0.85, 0.85, 0.80), 0.90, 0.00, 0.6),
    "taillight": ((0.55, 0.02, 0.02), 0.85, 0.00, 0.5),
    "tyre": ((0.02, 0.02, 0.02), 0.15, 0.00, 0.0),
    "rim": ((0.06, 0.06, 0.065), 0.70, 0.80, 0.0),
    "disc": ((0.35, 0.35, 0.36), 0.60, 0.80, 0.0),
}
def tag_of(name):
    n = name.lower()
    if "paint" in n: return "paint"
    if "red_glass" in n: return "taillight"
    if "window" in n or "glass" in n: return "glass"
    if "light" in n: return "light"
    if "tireblur" in n or "tire" in n: return "tyre"
    if "brakedisc" in n: return "disc"
    if "rim" in n: return "rim"
    return "carbon"

def retag(o):
    """Each material slot is replaced by its tag's material in place, so every face keeps its slot (and so its tag)."""
    me = o.data
    if len(me.materials) == 0:
        me.materials.append(bpy.data.materials.get("tag_carbon") or bpy.data.materials.new("tag_carbon"))
    for i in range(len(me.materials)):
        m = me.materials[i]
        t = tag_of(m.name if m else "")
        me.materials[i] = bpy.data.materials.get("tag_" + t) or bpy.data.materials.new("tag_" + t)

def weld_decimate(o, target):
    sel_only([o]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.remove_doubles(threshold=0.001); bpy.ops.object.mode_set(mode='OBJECT')
    n = tri_count(o)
    if n > target:
        m = o.modifiers.new("dec", 'DECIMATE'); m.decimate_type = 'COLLAPSE'; m.ratio = max(0.001, target / n); m.use_collapse_triangulate = True
        sel_only([o]); bpy.ops.object.modifier_apply(modifier=m.name)

parts = [body] + list(wheels.values())
for o in parts: retag(o)
weld_decimate(body, TARGET_BODY)
for w in wheels.values(): weld_decimate(w, TARGET_WHEEL)
os.makedirs(OUT, exist_ok=True)

def lin2srgb(x): return 12.92 * x if x <= 0.0031308 else 1.055 * x ** (1 / 2.4) - 0.055

# write: Unity frame (x, z, y), winding reversed (loops 0, 2, 1); origin midway between the axles; wheels around pivots
oy = (pivots["FL"].y + pivots["RL"].y) / 2
def fmt(v): return " ".join(f"{c:.5f}" for c in v)
lines = [f"# CarSkins-style model '{NAME}' (2026 Zacoe BMW G87 M2 Widebody Carbon Fiber Kit by Ddiaz Design, Sketchfab, CC BY-NC-SA 4.0; built by build_car.py; Unity axes, metres, real size)",
         "kind car", f"name {NAME}"]
for t, (c, sm, met, e) in TAGS.items():
    lines.append(f"mat {t} {lin2srgb(c[0]):.4f} {lin2srgb(c[1]):.4f} {lin2srgb(c[2]):.4f} {sm:.4f} {met:.4f} {e:.4f}")
def write_part(name, o, pivot=None):
    me = o.data; me.calc_loop_triangles()
    lines.append(f"o {name}")
    if pivot is not None: lines.append(f"p {pivot.x:.5f} {pivot.z:.5f} {pivot.y - oy:.5f}")
    by_tag = {}
    for lt in me.loop_triangles:
        m = me.materials[lt.material_index] if lt.material_index < len(me.materials) else None
        by_tag.setdefault(m.name[4:] if m and m.name.startswith("tag_") else "carbon", []).append(lt)
    n = 0
    for t, lts in by_tag.items():
        lines.append(f"m {t}")
        for lt in lts:
            pts = []
            for li in (lt.loops[0], lt.loops[2], lt.loops[1]):
                v = me.vertices[me.loops[li].vertex_index].co.copy()
                v = v - pivot if pivot is not None else Vector((v.x, v.y - oy, v.z))
                nn = me.corner_normals[li].vector.normalized() if hasattr(me, "corner_normals") else me.vertices[me.loops[li].vertex_index].normal
                pts.append(fmt((v.x, v.z, v.y)) + " " + fmt((nn.x, nn.z, nn.y)))
            lines.append("f " + " ".join(pts)); n += 1
    return n
tris = write_part("Body", body)
for k in ("FL", "FR", "RL", "RR"): tris += write_part("Wheel" + k, wheels[k], pivots[k])
open(os.path.join(OUT, NAME + ".csm"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
print(f"[car] {NAME}: {tris} triangles (body {tri_count(body)}, wheels {', '.join(str(tri_count(w)) for w in wheels.values())}); "
      f"wheelbase {pivots['FL'].y - pivots['RL'].y:.3f} m, track {pivots['FR'].x - pivots['FL'].x:.2f} m")

if PREVIEW:
    sc = bpy.context.scene
    sc.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE_NEXT"
    for t, (c, sm, met, e) in TAGS.items():
        mm = bpy.data.materials.get("tag_" + t)
        if mm is None: continue
        mm.use_nodes = True; mm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (c[0], c[1], c[2], 1)
    w = bpy.data.worlds.new("W"); sc.world = w; w.use_nodes = True; w.node_tree.nodes["Background"].inputs[0].default_value = (0.45, 0.48, 0.52, 1)
    sun = bpy.data.objects.new("S", bpy.data.lights.new("S", "SUN")); sc.collection.objects.link(sun); sun.rotation_euler = (0.8, 0.1, 0.7); sun.data.energy = 3
    sc.render.resolution_x, sc.render.resolution_y = 960, 540
    cd = bpy.data.cameras.new("C"); cam = bpy.data.objects.new("C", cd); sc.collection.objects.link(cam); sc.camera = cam
    for nm, loc in (("q", (5.5, 5.5, 2.2)), ("side", (8.0, 0.0, 0.9))):
        cam.location = loc; cam.rotation_euler = (Vector((0, 0, 0.6)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = f"{PREVIEW}_{nm}.png"; bpy.ops.render.render(write_still=True)
