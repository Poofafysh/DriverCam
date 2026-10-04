"""Builds CarSkins' BMW E46 model from the downloaded Sketchfab OBJ (CC BY): decimated to about 5k triangles, wheels split
out around their own pivots, written as a .csm (CarSkins model: the Police .pcm text format plus `tex` for textured tags).
blender -b --factory-startup --python build_e46.py -- <obj> <textures dir> <out dir> [preview.png]
"""
import sys, os, math
import bpy, bmesh
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
OBJ, TEX, OUT = argv[0], argv[1], argv[2]
PREVIEW = argv[3] if len(argv) > 3 else None
TARGET_BODY, TARGET_WHEEL, TARGET_GLASS = 3300, 330, 300   # triangles: 3300 + 4 x 330 + 300 = 4920

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=OBJ)
objs = list(bpy.context.scene.objects)
body = [o for o in objs if "Box001" in o.name][0]
glass = [o for o in objs if "Object002" in o.name][0]

def apply_all(o):
    bpy.ops.object.select_all(action='DESELECT'); o.select_set(True); bpy.context.view_layer.objects.active = o
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

for o in (body, glass):
    o.scale = (0.01, 0.01, 0.01)   # centimetres -> metres
    apply_all(o)

# split the body into loose parts: the four 1520-poly parts low and outboard are the wheels
bpy.ops.object.select_all(action='DESELECT'); body.select_set(True); bpy.context.view_layer.objects.active = body
bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.separate(type='LOOSE'); bpy.ops.object.mode_set(mode='OBJECT')
parts = [o for o in bpy.context.scene.objects if o.name.startswith("Box001")]
wheels, rest = {}, []
for o in parts:
    ws = [v.co for v in o.data.vertices]
    mn = Vector((min(v.x for v in ws), min(v.y for v in ws), min(v.z for v in ws)))
    mx = Vector((max(v.x for v in ws), max(v.y for v in ws), max(v.z for v in ws)))
    c = (mn + mx) / 2
    if len(o.data.polygons) > 1000 and mx.z < 0.7 and abs(c.x) > 0.55:
        key = ("F" if c.y > 0 else "R") + ("L" if c.x < 0 else "R")   # Blender +y = front, -x = left
        wheels[key] = (o, c)
    else:
        rest.append(o)
assert len(wheels) == 4, wheels.keys()

# join the rest into one body
bpy.ops.object.select_all(action='DESELECT')
for o in rest: o.select_set(True)
bpy.context.view_layer.objects.active = rest[0]
bpy.ops.object.join()
body = bpy.context.view_layer.objects.active

def tri_count(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)

def decimate(o, target):
    n = tri_count(o)
    if n <= target: return
    m = o.modifiers.new("dec", 'DECIMATE'); m.decimate_type = 'COLLAPSE'; m.ratio = target / n
    m.use_collapse_triangulate = True
    bpy.ops.object.select_all(action='DESELECT'); o.select_set(True); bpy.context.view_layer.objects.active = o
    bpy.ops.object.modifier_apply(modifier=m.name)

decimate(body, TARGET_BODY)

# side windows are body faces made see-through by the Opacity map: move faces whose UV centre is transparent to the glass
op = bpy.data.images.load(os.path.join(TEX, "Standard_00FD5B_Opacity.png"))
ow, oh = op.size; opx = list(op.pixels); och = op.channels
def opacity(u, v):
    x = min(ow - 1, max(0, int((u % 1.0) * ow))); y = min(oh - 1, max(0, int((v % 1.0) * oh)))
    return opx[(y * ow + x) * och]
bm = bmesh.new(); bm.from_mesh(body.data)
uvl = bm.loops.layers.uv.active
sel = []
for f in bm.faces:
    cu = sum(l[uvl].uv.x for l in f.loops) / len(f.loops); cv = sum(l[uvl].uv.y for l in f.loops) / len(f.loops)
    if opacity(cu, cv) < 0.9: sel.append(f)   # the map is 16-bit; glass sits around 0.65
for f in bm.faces: f.select = False
for f in sel: f.select = True
bm.to_mesh(body.data); bm.free()
print(f"[E46] {len(sel)} see-through body faces -> glass")
if sel:
    bpy.ops.object.select_all(action='DESELECT'); body.select_set(True); bpy.context.view_layer.objects.active = body
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.separate(type='SELECTED'); bpy.ops.object.mode_set(mode='OBJECT')
    side = [o for o in bpy.context.selected_objects if o != body][0]
    bpy.ops.object.select_all(action='DESELECT'); glass.select_set(True); side.select_set(True); bpy.context.view_layer.objects.active = glass
    bpy.ops.object.join(); glass = bpy.context.view_layer.objects.active
decimate(glass, TARGET_GLASS)
for k, (o, c) in wheels.items(): decimate(o, TARGET_WHEEL)

# textures: base colour down to 1024 (the only map used at runtime)
os.makedirs(OUT, exist_ok=True)
img = bpy.data.images.load(os.path.join(TEX, "Standard_00FD5B_Base_Color.png"))
img.scale(1024, 1024)
img.filepath_raw = os.path.join(OUT, "BMW_E46_basecolor.png"); img.file_format = 'PNG'; img.save()

def fmt(v): return " ".join(f"{c:.5f}" for c in v)

def write_part(lines, name, o, tag, pivot=None):
    """Unity frame: (x, z, y), winding reversed (loops 0, 2, 1); wheel triangles relative to their pivot."""
    me = o.data; me.calc_loop_triangles()
    uv = me.uv_layers.active.data if me.uv_layers else None
    lines.append(f"o {name}")
    if pivot is not None: lines.append(f"p {pivot.x:.5f} {pivot.z:.5f} {pivot.y:.5f}")
    lines.append(f"m {tag}")
    n = 0
    for lt in me.loop_triangles:
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

lines = ["# CarSkins model 'BMW_E46' (BMW E46 1998 low-poly, Sketchfab, CC BY; decimated by build_e46.py; Unity axes, metres)",
         "name BMW_E46",
         "tex paint BMW_E46_basecolor.png",
         "mat paint 1.0000 1.0000 1.0000 0.5500 0.3000 0.0000",
         "mat glass 0.0400 0.0500 0.0600 0.9200 0.2000 0.0000"]
tris = write_part(lines, "Body", body, "paint")
tris += write_part(lines, "Glass", glass, "glass")
for k in ("FL", "FR", "RL", "RR"):
    o, c = wheels[k]
    piv = Vector((c.x, c.y, c.z))
    tris += write_part(lines, "Wheel" + k, o, "paint", piv)
open(os.path.join(OUT, "BMW_E46.csm"), "w", encoding="utf-8").write("\n".join(lines) + "\n")
print(f"[E46] {tris} triangles: body {tri_count(body)}, glass {tri_count(glass)}, wheels " +
      ", ".join(f"{k} {tri_count(o)}" for k, (o, c) in wheels.items()))
print("[E46] wheel pivots (Blender xyz):", {k: tuple(round(a, 3) for a in c) for k, (o, c) in wheels.items()})

if PREVIEW:
    sc = bpy.context.scene
    mat = bpy.data.materials.new("p"); mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]; tn = mat.node_tree.nodes.new("ShaderNodeTexImage"); tn.image = img
    mat.node_tree.links.new(tn.outputs["Color"], bsdf.inputs["Base Color"])
    for o in [body] + [w[0] for w in wheels.values()]:
        o.data.materials.clear(); o.data.materials.append(mat)
    gm = bpy.data.materials.new("g"); gm.use_nodes = True; gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.04, 0.05, 0.06, 1)
    glass.data.materials.clear(); glass.data.materials.append(gm)
    cd = bpy.data.cameras.new("C"); cam = bpy.data.objects.new("C", cd); sc.collection.objects.link(cam); sc.camera = cam
    cam.location = (4.5, 5.0, 1.8); cam.rotation_euler = (Vector((0, 0, 0.6)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    w = bpy.data.worlds.new("W"); sc.world = w; w.use_nodes = True; w.node_tree.nodes["Background"].inputs[0].default_value = (0.6, 0.65, 0.7, 1)
    sun = bpy.data.objects.new("S", bpy.data.lights.new("S", "SUN")); sc.collection.objects.link(sun); sun.rotation_euler = (0.9, 0, 0.6)
    sc.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE_NEXT"
    sc.render.resolution_x, sc.render.resolution_y = 960, 540
    sc.render.filepath = PREVIEW
    bpy.ops.render.render(write_still=True)
