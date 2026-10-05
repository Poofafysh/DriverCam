"""Builds the Bikes plugin's M2 G87 car model ("2026 Zacoe BMW G87 M2 Widebody Carbon Fiber Kit" by Ddiaz Design,
Sketchfab, CC BY-NC-SA 4.0) from its FBX. Steps:
- drop parts nobody sees (engine, belts, calipers, the base kit); the interior stays (seen through the glass and from
  the driver's seat), decimated on its own to about 14,000 triangles;
- copy every mesh with its world transform baked in (the wheels hang off an armature's bones);
- split every part by material and decimate each piece on its own (same ratio, a floor for small pieces, mirrored
  left / right), so panel and material edges stay where they are: no shredding, no gaps between panels (body about
  20,000 triangles, wheels about 3,000 each plus a rounded tyre of
  2,400); paint lying wholly under a carbon or trim panel is dropped first;
- smooth shading with hard edges past 40 degrees, then face-area weighted normals; the source's normals are kept
  (CAR_RECALC=1 recalculates them outward, which made no visible difference);
- flat colours by part (paint, gloss black trim, carbon, underbody, glass, lights, tyre, rim, disc) from the source's
  materials; the glass is written see-through (`mat glass ... GLASS_ALPHA`); the interior gets one texture (CABIN_TEX:
  the source's label sheet on soft-touch black plus its instrument cluster and centre screen) on its atlas UVs.
Written as a .csm with `kind car` (Body + WheelFL/FR/RL/RR around their pivots).
blender -b --factory-startup --python build_car.py -- <fbx> <out dir> [preview prefix]

Output frame (Unity, metres): +z forward, +y up, +x right; origin on the ground midway between the axles, on the centre
line. Real size.
"""
import sys, os, math
import bpy, bmesh
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index("--") + 1:]
FBX, OUT = argv[0], argv[1]
PREVIEW = argv[2] if len(argv) > 2 else None
NAME = "BMW_M2_G87"
TARGET_BODY, TARGET_WHEEL = 20000, 3000          # wheels: round tyres and rim spokes at close range
MIN_PIECE = 48                                  # small pieces keep at least this many triangles (or all they have)
SMOOTH_ANGLE = math.radians(40)
LENGTH = 4.58                                   # the G87 M2's length, metres
DROP = ("Engine_Geo", "SeatBelt_Geo", "Base:", "Calliper")
CABIN = "Interior_Geo"                          # the interior: seen through the glass and from the driver's seat
TARGET_CABIN = 14000                            # the dash and wheel fill the driver view
CABIN_UV = os.environ.get("CABIN_UV", "uvSet")   # the source's atlas layer (map1 is a tiling detail layer)
CABIN_V = float(os.environ.get("CABIN_V", "1"))   # 1 = as is (checked from the driver's seat: -1 scrambles the cluster)
CABIN_TEX = NAME + "_interior.jpg"              # the source's label sheet on a dark soft-touch base + its lit gauges / screen
GLASS_ALPHA = 0.18                              # see-through windows (the loader's `mat ... alpha`; a windscreen is two layers)

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
body_parts, wheel_parts, cabin_parts = [], [], []
for o in list(bpy.context.scene.objects):
    if o.type != 'MESH' or len(o.data.vertices) == 0 or any(d in o.name for d in DROP): continue
    me = o.data.copy(); me.transform(o.matrix_world)
    c = bpy.data.objects.new(o.name + ".baked", me); bpy.context.scene.collection.objects.link(c)
    if under_arm(o):
        if len(me.polygons) > 100: wheel_parts.append(c)   # tyres, rims, discs (calipers and badges are dropped)
    elif CABIN in o.name:
        cabin_parts.append(c)
    else:
        body_parts.append(c)
for o in list(bpy.context.scene.objects):
    if not o.name.endswith(".baked"): bpy.data.objects.remove(o, do_unlink=True)

# metres, facing +y (the source faces -y), ground at z 0, centred
mn, mx = bounds(body_parts)
scale = LENGTH / (mx.y - mn.y)
T = Matrix.Rotation(math.pi, 4, 'Z') @ Matrix.Scale(scale, 4)
for o in body_parts + wheel_parts + cabin_parts: o.data.transform(T)
mn, mx = bounds(body_parts)
mnw, _ = bounds(wheel_parts)
shift = Vector((-(mn.x + mx.x) / 2, -(mn.y + mx.y) / 2, -min(mn.z, mnw.z)))
for o in body_parts + wheel_parts + cabin_parts: o.data.transform(Matrix.Translation(shift))

sel_only(body_parts); bpy.ops.object.join(); body = bpy.context.view_layer.objects.active
sel_only(cabin_parts); bpy.ops.object.join(); cabin = bpy.context.view_layer.objects.active

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
    "trim": ((0.012, 0.012, 0.014), 0.85, 0.10, 0.0),
    "carbon": ((0.025, 0.025, 0.028), 0.40, 0.15, 0.0),
    "under": ((0.015, 0.015, 0.016), 0.20, 0.00, 0.0),
    "glass": ((0.02, 0.025, 0.03), 0.92, 0.10, 0.0),     # see-through: GLASS_ALPHA
    "interior": ((1.0, 1.0, 1.0), 0.30, 0.00, 0.0),       # colour from CABIN_TEX
    "light": ((0.85, 0.85, 0.80), 0.90, 0.00, 0.6),
    "taillight": ((0.55, 0.02, 0.02), 0.85, 0.00, 0.5),
    "tyre": ((0.02, 0.02, 0.02), 0.15, 0.00, 0.0),
    "rim": ((0.06, 0.06, 0.065), 0.70, 0.80, 0.0),
    "disc": ((0.35, 0.35, 0.36), 0.60, 0.80, 0.0),
}
def tag_of(name):
    n = name.lower()
    if "painttnr" in n: return "paint"
    if "coloured" in n: return "trim"                 # window surrounds, mirrors, skirt edges: gloss black
    if "base_material" in n: return "under"           # the floor pan
    if "interior" in n: return "interior"
    if "red_glass" in n: return "taillight"
    if "glass__surr" in n: return "trim"               # the black band round the glass
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

def pieces(o):
    """Splits o by material into separate objects (o is one of them)."""
    sel_only([o]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.remove_doubles(threshold=0.0005)
    if len(o.data.materials) > 1: bpy.ops.mesh.separate(type='MATERIAL')
    bpy.ops.object.mode_set(mode='OBJECT')
    return list(bpy.context.selected_objects)

def tag(q): return q.data.materials[0].name[4:] if len(q.data.materials) and q.data.materials[0] else ""

def decimate(o, ratio):
    """Collapse-decimates o, mirrored left / right. (Tried and dropped: copying the source's normals across, which gave
    wrinkles; and keeping the pieces' open edges fixed, which left fins sticking out of the roof and pillars.)"""
    n = tri_count(o)
    keep = max(n * ratio, min(n, MIN_PIECE))
    if keep < n:
        m = o.modifiers.new("dec", 'DECIMATE'); m.decimate_type = 'COLLAPSE'; m.ratio = max(0.01, keep / n)
        m.use_collapse_triangulate = True; m.use_symmetry = True; m.symmetry_axis = 'X'
        sel_only([o]); bpy.ops.object.modifier_apply(modifier=m.name)

def finish(o):
    """Drops degenerate faces (normals recalculated outward only with CAR_RECALC=1), smooth shading with hard edges past
    SMOOTH_ANGLE."""
    sel_only([o]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.dissolve_degenerate(threshold=0.0002)
    if os.environ.get("CAR_RECALC") == "1": bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.ops.object.shade_smooth_by_angle(angle=SMOOTH_ANGLE)
    # second smoothing pass: face-area weighted normals (big flat faces set the shading, slivers don't), creases kept
    w = o.modifiers.new("wn", 'WEIGHTED_NORMAL'); w.mode = 'FACE_AREA'; w.weight = 50; w.keep_sharp = True
    bpy.ops.object.modifier_apply(modifier=w.name)


COVER = 0.03   # paint with carbon or trim within this many metres outside it is hidden under the widebody kit

def drop_covered(ps):
    """Deletes paint faces lying under a carbon or trim panel (the kit is built over the stock body): decimated, they
    would poke through the panel above them."""
    over = [q for q in ps if tag(q) in ("carbon", "trim")]
    if not over: return
    bm = bmesh.new()
    for q in over: bm.from_mesh(q.data)
    tree = BVHTree.FromBMesh(bm); bm.free()
    dropped = 0
    for q in ps:
        if tag(q) != "paint": continue
        bq = bmesh.new(); bq.from_mesh(q.data)
        # covered = its centre and all its corners are under a panel (a face only partly under one stays: deleting it
        # would leave a jagged hole along the panel's edge)
        def hit(p, n): return tree.ray_cast(p + n * 0.001, n, COVER)[0] is not None
        gone = [f for f in bq.faces if hit(f.calc_center_median(), f.normal) and all(hit(v.co, f.normal) for v in f.verts)]
        bmesh.ops.delete(bq, geom=gone, context='FACES'); dropped += len(gone)
        bq.to_mesh(q.data); bq.free()
    print(f"[car] {dropped} paint faces under the kit dropped")

KEEP_FULL = ("tyre",)   # a tyre's round outline shows its facets first: rounded (ROUND_TYRE), outside the budget
ROUND_TYRE = 2400       # the source tyre is itself coarse: one subdivision step rounds it, then decimated to this

def round_tyre(q):
    """One Catmull-Clark step (rounds the coarse source tyre's outline), then collapse-decimated to ROUND_TYRE."""
    m = q.modifiers.new("sub", 'SUBSURF'); m.levels = 1; m.render_levels = 1
    sel_only([q]); bpy.ops.object.modifier_apply(modifier=m.name)
    n = tri_count(q)
    if n > ROUND_TYRE: decimate(q, ROUND_TYRE / n)

def reduce(o, target, covered=False):
    """o split by material, each piece decimated by the same ratio, joined again."""
    ps = []
    for q in pieces(o): retag(q); ps.append(q)
    if covered: drop_covered(ps)
    full = [q for q in ps if tag(q) in KEEP_FULL]   # kept as they are, outside the budget
    total = sum(tri_count(q) for q in ps if q not in full)
    ratio = min(1.0, max(0, target - sum(tri_count(q) for q in full)) / max(1, total))
    for q in ps:
        if q not in full: decimate(q, ratio)
        elif tag(q) == "tyre": round_tyre(q)
    sel_only(ps); bpy.ops.object.join(); j = bpy.context.view_layer.objects.active
    finish(j)
    return j

body = reduce(body, TARGET_BODY, covered=True)
cabin = reduce(cabin, TARGET_CABIN)
print(f"[car] cabin {tri_count(cabin)} tris")
sel_only([body, cabin], active=body); bpy.ops.object.join(); body = bpy.context.view_layer.objects.active   # one Body mesh
for k in list(wheels): wheels[k] = reduce(wheels[k], TARGET_WHEEL)
os.makedirs(OUT, exist_ok=True)

def lin2srgb(x): return 12.92 * x if x <= 0.0031308 else 1.055 * x ** (1 / 2.4) - 0.055

# write: Unity frame (x, z, y), winding reversed (loops 0, 2, 1); origin midway between the axles; wheels around pivots
oy = (pivots["FL"].y + pivots["RL"].y) / 2
def fmt(v): return " ".join(f"{c:.5f}" for c in v)
lines = [f"# CarSkins-style model '{NAME}' (2026 Zacoe BMW G87 M2 Widebody Carbon Fiber Kit by Ddiaz Design, Sketchfab, CC BY-NC-SA 4.0; built by build_car.py; Unity axes, metres, real size)",
         "kind car", f"name {NAME}", f"tex interior {CABIN_TEX}"]
for t, (c, sm, met, e) in TAGS.items():
    lines.append(f"mat {t} {lin2srgb(c[0]):.4f} {lin2srgb(c[1]):.4f} {lin2srgb(c[2]):.4f} {sm:.4f} {met:.4f} {e:.4f}"
                 + (f" {GLASS_ALPHA:.2f}" if t == "glass" else ""))
def cabin_texture():
    """CABIN_TEX: the source's interior base-colour sheet (white with dark labels and AO) times a dark soft-touch grey,
    plus its emissive sheet (instrument cluster, centre screen, button symbols) added at full strength, so the gauges read
    in the base colour without an emission shader. 1024 px, same UV layout as the source's (map1)."""
    import numpy as np
    src = os.path.dirname(FBX)
    def px(fn):
        im = bpy.data.images.load(os.path.join(src, fn)); im.scale(1024, 1024)
        a = np.empty(1024 * 1024 * 4, dtype=np.float32); im.pixels.foreach_get(a); return a.reshape(-1, 4)
    d = px("InteriorA_DiffuseAOSO.png"); e = px("BMW_M2G87TNR_2023_InteriorA_Emissive.png")
    out = np.ones_like(d)
    out[:, :3] = np.clip(d[:, :3] * 0.20 + e[:, :3] * e[:, 3:4], 0, 1)   # 0.20: dark grey, readable in the game's dim cabin light
    img = bpy.data.images.new("cabin", 1024, 1024); img.pixels.foreach_set(out.ravel())
    img.filepath_raw = os.path.join(OUT, CABIN_TEX); img.file_format = 'JPEG'
    bpy.context.scene.render.image_settings.quality = 90; img.save()

def write_part(name, o, pivot=None):
    me = o.data; me.calc_loop_triangles()
    uv = me.uv_layers[CABIN_UV].data if CABIN_UV in me.uv_layers else None   # the interior's atlas coordinates
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
                p = fmt((v.x, v.z, v.y)) + " " + fmt((nn.x, nn.z, nn.y))
                if t == "interior" and uv is not None: p += f" {uv[li].uv.x:.5f} {CABIN_V * uv[li].uv.y:.5f}"
                pts.append(p)
            lines.append(("u " if t == "interior" and uv is not None else "f ") + " ".join(pts)); n += 1
    return n
cabin_texture()
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
        mm.use_nodes = True; mm.use_backface_culling = True   # as in the game: a flipped face is a hole
        bsdf = mm.node_tree.nodes["Principled BSDF"]; bsdf.inputs["Base Color"].default_value = (c[0], c[1], c[2], 1)
        bsdf.inputs["Roughness"].default_value = 1 - sm; bsdf.inputs["Metallic"].default_value = met
    w = bpy.data.worlds.new("W"); sc.world = w; w.use_nodes = True; w.node_tree.nodes["Background"].inputs[0].default_value = (0.45, 0.48, 0.52, 1)
    sun = bpy.data.objects.new("S", bpy.data.lights.new("S", "SUN")); sc.collection.objects.link(sun); sun.rotation_euler = (0.8, 0.1, 0.7); sun.data.energy = 3
    sc.render.resolution_x, sc.render.resolution_y = 960, 540
    cd = bpy.data.cameras.new("C"); cam = bpy.data.objects.new("C", cd); sc.collection.objects.link(cam); sc.camera = cam
    for nm, loc in (("q", (4.2, 5.0, 1.6)), ("rq", (-4.2, -5.0, 1.9)), ("side", (8.0, 0.0, 0.9))):
        cam.location = loc; cam.rotation_euler = (Vector((0, 0, 0.6)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = f"{PREVIEW}_{nm}.png"; bpy.ops.render.render(write_still=True)
