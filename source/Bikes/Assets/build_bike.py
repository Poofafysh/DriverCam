"""Builds the Bikes plugin's sport bike models from the downloaded glTF (.glb) files: decimated to about 3.6k triangles, the two
wheels split out around their own pivots, written as a .csm with `kind bike` (wheels WheelF / WheelR instead of FL/FR/RL/RR).
blender -b --factory-startup --python build_bike.py -- <rr|lp> <glb> <textures dir> <out dir> [preview prefix]

  rr = BMW S1000RR (~313k tris in loose parts: tiny parts dropped, voxel-remeshed into closed surfaces, decimated)
  lp = low-poly sport bike (one textured ~50k-tri triangle soup: welded, wheels cut out by geometry, decimated)
  Both: a new UV atlas, and the source's colours baked onto one 1024 px base-colour texture (Cycles, diffuse colour).

Output frame (Unity, metres): +z forward, +y up, +x right; origin on the ground midway between the two axles, on the
centre line. Real size (the bike is NOT scaled to the car: see the bike design doc).
"""
import sys, os, math
import bpy, bmesh
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:]
KIND, GLB, TEX, OUT = argv[0], argv[1], argv[2], argv[3]
PREVIEW = argv[4] if len(argv) > 4 else None
TARGET_BODY, TARGET_WHEEL = 3000, 300          # 3000 + 2 x 300 = 3600 triangles
NAME = {"rr": "BMW_S1000RR", "lp": "SportBike"}[KIND]
LENGTH_LP = 2.07                               # the low-poly bike's real length (m): an R1-class sport bike

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=GLB)

def sel_only(objs, active=None):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]

def tri_count(o): return sum(len(p.vertices) - 2 for p in o.data.polygons)

def decimate(o, target):
    n = tri_count(o)
    if n <= target: return
    m = o.modifiers.new("dec", 'DECIMATE'); m.decimate_type = 'COLLAPSE'; m.ratio = max(0.0005, target / n)
    m.use_collapse_triangulate = True
    sel_only([o]); bpy.ops.object.modifier_apply(modifier=m.name)

def join(objs):
    objs = [o for o in objs if o is not None]
    if len(objs) == 1: return objs[0]
    sel_only(objs); bpy.ops.object.join(); return bpy.context.view_layer.objects.active

def bounds(objs):
    ws = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    mn = Vector((min(v.x for v in ws), min(v.y for v in ws), min(v.z for v in ws)))
    mx = Vector((max(v.x for v in ws), max(v.y for v in ws), max(v.z for v in ws)))
    return mn, mx

# flatten: meshes only, parents cleared (world transform kept), transforms applied
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH' and o.name != "Icosphere"]
for o in list(bpy.context.scene.objects):
    if o not in meshes: bpy.data.objects.remove(o, do_unlink=True)
sel_only(meshes); bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

def lin2srgb(x): return 12.92 * x if x <= 0.0031308 else 1.055 * x ** (1 / 2.4) - 0.055

if KIND == "rr":
    DROP = {"1", "2", "3", "4", "5", "6", "8", "9", "N", "dials", "takometro", "LBUTTONS", "ign", "interior", "cockpit"}
    for o in list(meshes):
        if o.name in DROP: meshes.remove(o); bpy.data.objects.remove(o, do_unlink=True)
    # the source's tyre and rim materials carry the green paint colour: make tyres black and rims dark grey before baking
    for m in bpy.data.materials:
        n = m.name.lower()
        if not m.use_nodes or not ("tyre" in n or "[wheel]" in n): continue
        b = next((x for x in m.node_tree.nodes if x.type == 'BSDF_PRINCIPLED'), None)
        if b is None: continue
        for l in list(b.inputs["Base Color"].links): m.node_tree.links.remove(l)
        g = 0.025 if "tyre" in n else 0.09
        b.inputs["Base Color"].default_value = (g, g, g, 1)
    byname = {o.name: o for o in meshes}
    WHEEL_PARTS = ("wheel_lf.child", "wheel_lr.child", "bikedisc_f", "bikedisc_r")
    rest = [o for o in meshes if o.name not in WHEEL_PARTS]
    wheelF = join([byname["wheel_lf.child"], byname["bikedisc_f"]])
    wheelR = join([byname["wheel_lr.child"], byname["bikedisc_r"]])
    # forward is +y, up +z already, real metres. The source is hundreds of loose shells (bolts, panels, single-sided
    # skins): decimating them leaves shards and holes. Keep an untouched copy to bake the colours from, then voxel-remesh
    # body and wheels into closed surfaces before decimating.
    body = join(rest)
    srcs = []
    for o in (body, wheelF, wheelR):
        c = o.copy(); c.data = o.data.copy(); bpy.context.scene.collection.objects.link(c); srcs.append(c)
    orig = join(srcs)
    for o, vox in ((body, 0.012), (wheelF, 0.008), (wheelR, 0.008)):
        m = o.modifiers.new("rm", 'REMESH'); m.mode = 'VOXEL'; m.voxel_size = vox; m.adaptivity = 0.0
        sel_only([o]); bpy.ops.object.modifier_apply(modifier=m.name)
    TAGS = {"paint": ((1.0, 1.0, 1.0), 0.5, 0.2, 0.0)}
    decimate(body, TARGET_BODY)
else:
    obj = meshes[0]
    # forward is -x: turn so it is +y, then scale to real length
    obj.data.transform(Matrix.Rotation(math.radians(-90), 4, 'Z'))
    mn, mx = bounds([obj]); scale = LENGTH_LP / (mx.y - mn.y)
    obj.data.transform(Matrix.Scale(scale, 4))
    mn, mx = bounds([obj])
    # the source is a triangle soup with a fragmented UV atlas: decimating it scrambles the UVs. Keep an untouched copy
    # to bake the colours from, weld the working mesh, decimate, unwrap again and bake (below).
    orig = obj.copy(); orig.data = obj.data.copy(); bpy.context.scene.collection.objects.link(orig)
    sel_only([obj]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.mesh.remove_doubles(threshold=0.002); bpy.ops.object.mode_set(mode='OBJECT')
    # wheels: find each axle by fitting the tyre tread (vertices near the centre plane, in the front / rear third)
    vs = [v.co.copy() for v in obj.data.vertices]
    def fit(front):
        best = None
        for r10 in range(240, 360, 4):               # radius 0.24-0.36 m
            r = r10 / 1000.0
            cy = (mx.y - r) if front else (mn.y + r); cz = mn.z + r
            n = 0
            for v in vs:
                if abs(v.x) < 0.05 and abs(math.hypot(v.y - cy, v.z - cz) - r) < 0.012: n += 1
            if best is None or n > best[0]: best = (n, r, cy, cz)
        return best
    fF, fR = fit(True), fit(False)
    print(f"[bike] axle fit front r={fF[1]:.3f} at y={fF[2]:.3f} z={fF[3]:.3f} ({fF[0]} tread verts); rear r={fR[1]:.3f} at y={fR[2]:.3f} z={fR[3]:.3f} ({fR[0]})")
    def cut(fit_, half_width):
        _, r, cy, cz = fit_
        bm = bmesh.new(); bm.from_mesh(obj.data)
        for f in bm.faces:
            f.select = all(abs(v.co.x) < half_width and math.hypot(v.co.y - cy, v.co.z - cz) < r * 1.01 for v in f.verts)
        bm.to_mesh(obj.data); bm.free()
        sel_only([obj]); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.separate(type='SELECTED'); bpy.ops.object.mode_set(mode='OBJECT')
        return [o for o in bpy.context.selected_objects if o != obj][0]
    wheelF = cut(fF, 0.075)                           # front tyre ~120 mm, rim + disc inside
    wheelR = cut(fR, 0.105)                           # rear tyre ~190 mm
    body = obj
    TAGS = {"paint": ((1.0, 1.0, 1.0), 0.5, 0.2, 0.0)}
    decimate(body, TARGET_BODY)
    fwd_rot = Matrix.Identity(4)

decimate(wheelF, TARGET_WHEEL); decimate(wheelR, TARGET_WHEEL)

bake_img = None
if orig is not None:
    # one new UV atlas over body + both wheels, then the source colours baked onto it (Cycles, diffuse colour only)
    parts = [body, wheelF, wheelR]
    for o in parts:
        o.data.materials.clear()
        while o.data.uv_layers: o.data.uv_layers.remove(o.data.uv_layers[0])
        o.data.uv_layers.new(name="bake")
    sel_only(parts); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.004); bpy.ops.object.mode_set(mode='OBJECT')
    bake_img = bpy.data.images.new(NAME + "_bake", 1024, 1024)
    bm_ = bpy.data.materials.new("bake"); bm_.use_nodes = True
    tn = bm_.node_tree.nodes.new("ShaderNodeTexImage"); tn.image = bake_img; bm_.node_tree.nodes.active = tn
    for o in parts: o.data.materials.append(bm_)
    sc = bpy.context.scene; sc.render.engine = 'CYCLES'; sc.cycles.samples = 4; sc.cycles.device = 'CPU'
    bk = sc.render.bake; bk.use_selected_to_active = True; bk.cage_extrusion = 0.02; bk.max_ray_distance = 0.08
    bk.use_pass_direct = False; bk.use_pass_indirect = False; bk.use_pass_color = True
    for i, o in enumerate(parts):
        bk.use_clear = (i == 0)
        sel_only([orig, o], active=o)
        bpy.ops.object.bake(type='DIFFUSE', pass_filter={'COLOR'}, use_clear=(i == 0), margin=4)
    bpy.data.objects.remove(orig, do_unlink=True)

# pivots: wheel bounding-box centres; frame origin: ground, midway between the axles, on the centre line
def centre(o):
    mn, mx = bounds([o]); return (mn + mx) / 2
pF, pR = centre(wheelF), centre(wheelR)
pF.x = pR.x = 0.0
mnAll, _ = bounds([body, wheelF, wheelR])
origin = Vector((0.0, (pF.y + pR.y) / 2, mnAll.z))
for o in (body, wheelF, wheelR): o.data.transform(Matrix.Translation(-origin))
pF -= origin; pR -= origin

os.makedirs(OUT, exist_ok=True)
texfile = None
if bake_img is not None:
    img = bake_img
    texfile = NAME + "_basecolor.jpg"
    img.filepath_raw = os.path.join(OUT, texfile); img.file_format = 'JPEG'
    bpy.context.scene.render.image_settings.quality = 88
    img.save()

def fmt(v): return " ".join(f"{c:.5f}" for c in v)

def write_part(lines, name, o, tagnames, pivot=None):
    """Unity frame: (x, z, y), winding reversed (loops 0, 2, 1); wheel triangles relative to their pivot."""
    me = o.data; me.calc_loop_triangles()
    uv = me.uv_layers.active.data if (texfile and me.uv_layers) else None
    lines.append(f"o {name}")
    if pivot is not None: lines.append(f"p {pivot.x:.5f} {pivot.z:.5f} {pivot.y:.5f}")
    by_tag = {}
    for lt in me.loop_triangles:
        t = tagnames(me, lt.material_index)
        by_tag.setdefault(t, []).append(lt)
    n = 0
    for t, lts in by_tag.items():
        lines.append(f"m {t}")
        for lt in lts:
            pts = []
            for li in (lt.loops[0], lt.loops[2], lt.loops[1]):
                v = me.vertices[me.loops[li].vertex_index].co.copy()
                if pivot is not None: v = v - pivot
                nn = me.corner_normals[li].vector.normalized() if hasattr(me, "corner_normals") else me.vertices[me.loops[li].vertex_index].normal
                s = fmt((v.x, v.z, v.y)) + " " + fmt((nn.x, nn.z, nn.y))
                if uv is not None: s += f" {uv[li].uv.x:.5f} {uv[li].uv.y:.5f}"
                pts.append(s)
            lines.append(("u " if uv is not None else "f ") + " ".join(pts)); n += 1
    return n

def tag_name(me, i):
    if texfile: return "paint"
    m = me.materials[i] if i < len(me.materials) else None
    return m.name[4:] if m and m.name.startswith("tag_") else "c0"

src = {"rr": "BMW S1000 RR by VTX, Sketchfab, CC BY-NC-SA 4.0", "lp": "Low Poly Motorcycle by pyrzegeclb, Sketchfab, CC BY 4.0"}[KIND]
lines = [f"# CarSkins model '{NAME}' ({src}; decimated by build_bike.py; Unity axes, metres, real size)",
         "kind bike", f"name {NAME}"]
if texfile: lines.append(f"tex paint {texfile}")
for t, (c, s, met, e) in TAGS.items():
    lines.append(f"mat {t} {lin2srgb(c[0]):.4f} {lin2srgb(c[1]):.4f} {lin2srgb(c[2]):.4f} {s:.4f} {met:.4f} {e:.4f}")
tris = write_part(lines, "Body", body, tag_name)
tris += write_part(lines, "WheelF", wheelF, tag_name, pF)
tris += write_part(lines, "WheelR", wheelR, tag_name, pR)
open(os.path.join(OUT, NAME + ".csm"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
mnB, mxB = bounds([body, wheelF, wheelR])
print(f"[bike] {NAME}: {tris} triangles (body {tri_count(body)}, wheels {tri_count(wheelF)} + {tri_count(wheelR)}); "
      f"size {mxB.x - mnB.x:.2f} x {mxB.z - mnB.z:.2f} x {mxB.y - mnB.y:.2f} m (w x h x l); wheelbase {pF.y - pR.y:.3f} m; "
      f"wheel radius F {pF.z:.3f} R {pR.z:.3f}")

if PREVIEW:
    sc = bpy.context.scene
    def flat(c, e=0.0):
        m = bpy.data.materials.new("p"); m.use_nodes = True; b = m.node_tree.nodes["Principled BSDF"]
        b.inputs["Base Color"].default_value = (c[0], c[1], c[2], 1); return m
    if texfile:
        sc.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE_NEXT"
        mat = bpy.data.materials.new("t"); mat.use_nodes = True
        bsdf = mat.node_tree.nodes["Principled BSDF"]; tn = mat.node_tree.nodes.new("ShaderNodeTexImage"); tn.image = img
        mat.node_tree.links.new(tn.outputs["Color"], bsdf.inputs["Base Color"])
        for o in (body, wheelF, wheelR): o.data.materials.clear(); o.data.materials.append(mat)
    else:
        for t, (c, s, met, e) in TAGS.items():
            m = bpy.data.materials.get("tag_" + t)
            if m is None: continue
            m.use_nodes = True; m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (c[0], c[1], c[2], 1)
    for o in (wheelF, wheelR): o.location = o.location   # pivots are only metadata here
    w = bpy.data.worlds.new("W"); sc.world = w; w.use_nodes = True; w.node_tree.nodes["Background"].inputs[0].default_value = (0.45, 0.48, 0.52, 1)
    sun = bpy.data.objects.new("S", bpy.data.lights.new("S", "SUN")); sc.collection.objects.link(sun); sun.rotation_euler = (0.8, 0.1, 0.7); sun.data.energy = 3
    sc.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE_NEXT"
    sc.render.resolution_x, sc.render.resolution_y = 960, 540
    cd = bpy.data.cameras.new("C"); cam = bpy.data.objects.new("C", cd); sc.collection.objects.link(cam); sc.camera = cam
    for nm, loc in (("q", (3.2, 2.6, 1.3)), ("side", (4.2, 0.0, 0.6))):
        cam.location = loc; cam.rotation_euler = (Vector((0, 0, 0.55)) - cam.location).to_track_quat("-Z", "Y").to_euler()
        sc.render.filepath = f"{PREVIEW}_{nm}.png"; bpy.ops.render.render(write_still=True)
